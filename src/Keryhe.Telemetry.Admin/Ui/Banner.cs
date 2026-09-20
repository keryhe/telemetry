using Keryhe.Telemetry.Admin.Data;
using Spectre.Console;

namespace Keryhe.Telemetry.Admin.Ui;

/// <summary>
/// The always-visible connection-target header. Printed at startup and re-printed at the top of
/// every screen redraw so the operator never loses track of which database they are pointed at.
/// See plans/admin-tui.md, section 6.1.
/// </summary>
public static class Banner
{
    private static readonly string[] LocalHostNames = ["localhost", "127.0.0.1", ".", "(local)"];

    public static void Print(string providerName, IAdminRepository repository)
    {
        var (server, database) = repository.DescribeTarget();
        var isLocal = LocalHostNames.Contains(server, StringComparer.OrdinalIgnoreCase);

        AnsiConsole.Write(new Rule("[bold]Keryhe Telemetry Admin[/]").LeftJustified());

        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().PadRight(2));
        grid.AddColumn();
        grid.AddRow("[grey]Provider[/]", Markup.Escape(providerName));
        grid.AddRow("[grey]Server[/]", isLocal ? Markup.Escape(server) : $"[red]{Markup.Escape(server)}[/]");
        grid.AddRow("[grey]Database[/]", Markup.Escape(database));
        AnsiConsole.Write(grid);

        AnsiConsole.Write(new Rule());
        AnsiConsole.WriteLine();
    }
}
