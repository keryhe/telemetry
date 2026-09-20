using Keryhe.Telemetry.Admin;
using Keryhe.Telemetry.Admin.Data;
using Keryhe.Telemetry.Admin.Ui;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

// Configuration order: appsettings.json (Database:Provider) -> User Secrets
// (ConnectionStrings:Admin) -> environment variables (ConnectionStrings__Admin). See
// plans/admin-tui.md, section 3.1. Deliberately no ConnectionStrings section ships in
// appsettings.json, so there is no template value to accidentally commit.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets(typeof(AdminOptions).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();

var options = new AdminOptions
{
    Provider = configuration["Database:Provider"],
    ConnectionString = configuration["ConnectionStrings:Admin"]
};

// Fail before the first menu, not on the first operation. See plans/admin-tui.md, section 3.2.
if (string.IsNullOrWhiteSpace(options.Provider))
{
    AnsiConsole.MarkupLine("[red]Missing configuration: Database:Provider (appsettings.json).[/]");
    AnsiConsole.MarkupLine("[grey]Supported values: PostgreSQL, Timescale, SqlServer.[/]");
    return 1;
}

if (string.IsNullOrWhiteSpace(options.ConnectionString))
{
    AnsiConsole.MarkupLine("[red]Missing configuration: ConnectionStrings:Admin.[/]");
    AnsiConsole.MarkupLine("[grey]Set it in this project's User Secrets:[/]");
    AnsiConsole.MarkupLine(
        "[grey]  dotnet user-secrets set \"ConnectionStrings:Admin\" \"<connection string>\" " +
        "--project src/Keryhe.Telemetry.Admin[/]");
    return 1;
}

// MySql and ClickHouse are valid Database:Provider values for the telemetry hosts, but this tool
// does not support them — an explicit, honest message rather than falling into a generic "unknown
// provider" arm. See plans/admin-tui.md, section 3.3.
IAdminRepository repository;
string providerDisplayName;
switch (options.Provider.Trim())
{
    case "PostgreSQL":
        repository = new NpgsqlAdminRepository(options.ConnectionString);
        providerDisplayName = "PostgreSQL";
        break;
    case "Timescale":
        repository = new NpgsqlAdminRepository(options.ConnectionString);
        providerDisplayName = "Timescale";
        break;
    case "SqlServer":
        repository = new SqlServerAdminRepository(options.ConnectionString);
        providerDisplayName = "SqlServer";
        break;
    case "MySql":
    case "ClickHouse":
        AnsiConsole.MarkupLine(
            $"[red]Provider '{Markup.Escape(options.Provider)}' is valid for the telemetry hosts but is " +
            "not supported by this admin tool. Supported: PostgreSQL, Timescale, SqlServer.[/]");
        return 1;
    default:
        AnsiConsole.MarkupLine($"[red]Unknown Database:Provider '{Markup.Escape(options.Provider)}'.[/]");
        AnsiConsole.MarkupLine("[grey]Supported values: PostgreSQL, Timescale, SqlServer.[/]");
        return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await repository.CheckConnectivityAsync(cts.Token);
}
catch (Exception ex)
{
    var (server, database) = repository.DescribeTarget();
    AnsiConsole.MarkupLine($"[red]Could not connect to {Markup.Escape(server)} / {Markup.Escape(database)}:[/]");
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
    return 1;
}

try
{
    await AdminApp.RunAsync(providerDisplayName, repository, cts.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C — exit quietly.
}

return 0;
