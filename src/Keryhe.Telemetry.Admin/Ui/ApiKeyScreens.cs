using Keryhe.Telemetry.Admin.Data;
using Keryhe.Telemetry.Admin.Security;
using Spectre.Console;

namespace Keryhe.Telemetry.Admin.Ui;

/// <summary>
/// Tenant detail (the API key list) and every key operation. See plans/admin-tui.md, sections
/// 6.4–6.8.
/// </summary>
public static class ApiKeyScreens
{
    private const string CreateKey = "Create API key";
    private const string RevokeKey = "Revoke API key";
    private const string ReactivateKey = "Reactivate API key";
    private const string DeleteKey = "Delete API key";
    private const string Back = "Back";

    // Default from Keryhe.Telemetry.Core.Data.TenantResolutionOptions — this tool cannot read the
    // collector host's actual configured value, so these are named as defaults, not asserted as
    // fact. See plans/admin-tui.md, section 6.6.
    private const int DefaultPositiveCacheTtlSeconds = 30;
    private const int DefaultNegativeCacheTtlSeconds = 5;

    public static async Task RunAsync(string providerName, IAdminRepository repository, TenantRow tenant, CancellationToken ct)
    {
        while (true)
        {
            AnsiConsole.Clear();
            Banner.Print(providerName, repository);
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(tenant.Name)}[/] (id {tenant.Id})");
            AnsiConsole.WriteLine();

            var keys = await repository.GetApiKeysAsync(tenant.Id, ct);
            PrintKeyTable(keys, repository.CreatedAtColumnHeader);

            var choices = new List<string> { CreateKey };
            var activeKeys = keys.Where(k => k.IsActive).ToList();
            var inactiveKeys = keys.Where(k => !k.IsActive).ToList();
            if (activeKeys.Count > 0) choices.Add(RevokeKey);
            if (inactiveKeys.Count > 0) choices.Add(ReactivateKey);
            if (keys.Count > 0) choices.Add(DeleteKey);
            choices.Add(Back);

            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().AddChoices(choices));

            switch (choice)
            {
                case CreateKey:
                    await CreateApiKeyAsync(providerName, repository, tenant, ct);
                    break;
                case RevokeKey:
                    await RevokeAsync(repository, activeKeys, ct);
                    break;
                case ReactivateKey:
                    await ReactivateAsync(repository, inactiveKeys, ct);
                    break;
                case DeleteKey:
                    await DeleteAsync(repository, keys, ct);
                    break;
                case Back:
                    return;
            }
        }
    }

    private static void PrintKeyTable(IReadOnlyList<ApiKeyRow> keys, string createdAtHeader)
    {
        if (keys.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No API keys for this tenant yet.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Id");
        table.AddColumn("Name");
        table.AddColumn("Active");
        table.AddColumn("Hash");
        table.AddColumn(createdAtHeader);
        table.AddColumn("Last used");

        foreach (var k in keys)
        {
            table.AddRow(
                k.Id.ToString(),
                Markup.Escape(k.Name),
                k.IsActive ? "[green]yes[/]" : "[red]no[/]",
                k.HashPrefix,
                k.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                k.LastUsedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// The screen that matters: generate, hash, insert, and display the plaintext exactly once.
    /// Called both from the tenant-detail menu and immediately after tenant creation. See
    /// plans/admin-tui.md, section 6.5.
    /// </summary>
    public static async Task CreateApiKeyAsync(string providerName, IAdminRepository repository, TenantRow tenant, CancellationToken ct)
    {
        var name = AnsiConsole.Prompt(
            new TextPrompt<string>("API key name:")
                .Validate(Validation.ValidateName));

        var plaintextKey = ApiKeyHashing.GenerateKey();
        var hash = ApiKeyHashing.ComputeHash(plaintextKey);

        await repository.CreateApiKeyAsync(tenant.Id, name.Trim(), hash, ct);

        AnsiConsole.WriteLine();
        var panel = new Panel(
                new Rows(
                    Text.Empty,
                    new Text(plaintextKey),
                    Text.Empty,
                    new Markup("[grey]Shown once. Only its SHA-256 hash is stored — this value[/]"),
                    new Markup("[grey]cannot be recovered. Copy it now.[/]"),
                    Text.Empty,
                    new Markup("[grey]Send as:[/]  Authorization: Bearer <key>")))
            .Header($" API key — {Markup.Escape(tenant.Name)} / {Markup.Escape(name.Trim())} ")
            .Expand();
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        // Explicit acknowledgement, not an auto-return — see plans/admin-tui.md, section 6.5.
        AnsiConsole.MarkupLine("Press Enter once you have saved it.");
        Console.ReadLine();
    }

    private static async Task RevokeAsync(IAdminRepository repository, IReadOnlyList<ApiKeyRow> activeKeys, CancellationToken ct)
    {
        const string cancel = "« Cancel";
        var names = activeKeys.Select(k => k.Name).Append(cancel).ToList();
        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Revoke which key?").AddChoices(names));
        if (picked == cancel) return;

        var key = activeKeys.First(k => k.Name == picked);
        if (!AnsiConsole.Confirm($"Revoke '{Markup.Escape(key.Name)}'? It will stop authenticating.", defaultValue: false))
        {
            return;
        }

        await repository.SetApiKeyActiveAsync(key.Id, active: false, ct);

        AnsiConsole.MarkupLine("[green]Revoked.[/]");
        AnsiConsole.MarkupLine(
            $"[grey]Ingestion may keep accepting this key for up to ~{DefaultPositiveCacheTtlSeconds}s " +
            "(Telemetry:TenantResolution:PositiveCacheTtlSeconds, default shown) until the collector's " +
            "resolver cache expires.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Press Enter to continue.");
        Console.ReadLine();
    }

    private static async Task ReactivateAsync(IAdminRepository repository, IReadOnlyList<ApiKeyRow> inactiveKeys, CancellationToken ct)
    {
        const string cancel = "« Cancel";
        var names = inactiveKeys.Select(k => k.Name).Append(cancel).ToList();
        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Reactivate which key?").AddChoices(names));
        if (picked == cancel) return;

        var key = inactiveKeys.First(k => k.Name == picked);
        if (!AnsiConsole.Confirm($"Reactivate '{Markup.Escape(key.Name)}'?", defaultValue: false))
        {
            return;
        }

        await repository.SetApiKeyActiveAsync(key.Id, active: true, ct);

        AnsiConsole.MarkupLine("[green]Reactivated.[/]");
        AnsiConsole.MarkupLine(
            $"[grey]Negative lookups are cached for up to ~{DefaultNegativeCacheTtlSeconds}s by default, " +
            "so this key should become usable quickly.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Press Enter to continue.");
        Console.ReadLine();
    }

    private static async Task DeleteAsync(IAdminRepository repository, IReadOnlyList<ApiKeyRow> keys, CancellationToken ct)
    {
        const string cancel = "« Cancel";
        var names = keys.Select(k => k.Name).Append(cancel).ToList();
        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("[red]Delete which key?[/]").AddChoices(names));
        if (picked == cancel) return;

        var key = keys.First(k => k.Name == picked);

        AnsiConsole.MarkupLine(
            $"[red]Permanently delete '{Markup.Escape(key.Name)}'? This removes the row and its last_used_at history.[/]");
        var typed = AnsiConsole.Ask<string>("Type the key name to confirm:");
        if (typed != key.Name)
        {
            AnsiConsole.MarkupLine("[grey]Name did not match — cancelled.[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Press Enter to continue.");
            Console.ReadLine();
            return;
        }

        await repository.DeleteApiKeyAsync(key.Id, ct);
        AnsiConsole.MarkupLine("[green]Deleted.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Press Enter to continue.");
        Console.ReadLine();
    }
}
