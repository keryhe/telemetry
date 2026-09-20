using Keryhe.Telemetry.Admin.Data;
using Spectre.Console;

namespace Keryhe.Telemetry.Admin.Ui;

/// <summary>
/// The home screen (tenant list) and tenant creation. See plans/admin-tui.md, sections 6.2 and
/// 6.3. The tenant list IS the home screen — there is no menu behind a menu.
/// </summary>
public static class TenantScreens
{
    private const string SelectTenant = "Select a tenant";
    private const string CreateTenantChoice = "Create tenant";
    private const string Refresh = "Refresh";
    private const string Quit = "Quit";

    public static async Task RunAsync(string providerName, IAdminRepository repository, CancellationToken ct)
    {
        while (true)
        {
            AnsiConsole.Clear();
            Banner.Print(providerName, repository);

            var tenants = await repository.GetTenantsAsync(ct);
            PrintTenantTable(tenants, repository.CreatedAtColumnHeader);

            var choices = new List<string>();
            if (tenants.Count > 0) choices.Add(SelectTenant);
            choices.Add(CreateTenantChoice);
            choices.Add(Refresh);
            choices.Add(Quit);

            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().AddChoices(choices));

            switch (choice)
            {
                case SelectTenant:
                    var picked = PickTenant(tenants);
                    if (picked is not null)
                    {
                        await ApiKeyScreens.RunAsync(providerName, repository, picked, ct);
                    }
                    break;

                case CreateTenantChoice:
                    await CreateTenantAsync(providerName, repository, ct);
                    break;

                case Refresh:
                    break;

                case Quit:
                    return;
            }
        }
    }

    private static void PrintTenantTable(IReadOnlyList<TenantRow> tenants, string createdAtHeader)
    {
        if (tenants.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No tenants yet.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Id");
        table.AddColumn("Name");
        table.AddColumn(createdAtHeader);
        table.AddColumn("Keys");

        foreach (var t in tenants)
        {
            table.AddRow(
                t.Id.ToString(),
                Markup.Escape(t.Name),
                t.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                $"{t.KeyCount} ({t.ActiveKeyCount} active)");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static TenantRow? PickTenant(IReadOnlyList<TenantRow> tenants)
    {
        const string back = "« Back";
        var names = tenants.Select(t => t.Name).Append(back).ToList();
        var pickedName = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a tenant")
                .AddChoices(names));

        return pickedName == back ? null : tenants.First(t => t.Name == pickedName);
    }

    private static async Task CreateTenantAsync(string providerName, IAdminRepository repository, CancellationToken ct)
    {
        var name = AnsiConsole.Prompt(
            new TextPrompt<string>("Tenant name:")
                .Validate(Validation.ValidateName));

        long tenantId;
        try
        {
            tenantId = await repository.CreateTenantAsync(name.Trim(), ct);
        }
        catch (UniqueConstraintViolationException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Press Enter to continue.");
            Console.ReadLine();
            return;
        }

        AnsiConsole.MarkupLine($"[green]Created tenant '{Markup.Escape(name.Trim())}' (id {tenantId}).[/]");

        // A tenant with no API key cannot ingest anything, so creating one is never on its own a
        // finished task. See plans/admin-tui.md, section 6.3.
        if (AnsiConsole.Confirm("Create an API key for this tenant now?", defaultValue: false))
        {
            var tenant = new TenantRow(tenantId, name.Trim(), DateTime.UtcNow, 0, 0);
            await ApiKeyScreens.CreateApiKeyAsync(providerName, repository, tenant, ct);
        }
    }
}
