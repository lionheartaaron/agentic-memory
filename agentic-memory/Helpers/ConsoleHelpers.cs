using AgenticMemory.Configuration;
using Spectre.Console;

namespace AgenticMemory.Helpers;

public static class ConsoleHelpers
{
    public static void PrintStartupBanner(
        AppSettings settings,
        string listeningOn,
        bool embeddingsActive,
        bool generativeAvailable)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Rule(":brain: [bold blue]Agentic Memory Server[/] [grey dim](MCP SDK)[/]")
                .RuleStyle("blue dim")
                .LeftJustified());
        AnsiConsole.WriteLine();

        // Core status
        AnsiConsole.MarkupLine($"  :globe_with_meridians:  [grey]Listening on[/]     [bold white]{Markup.Escape(listeningOn)}[/]");
        AnsiConsole.MarkupLine($"  :floppy_disk:  [grey]Database[/]         [dim]{Markup.Escape(settings.Storage.DatabasePath)}[/]");
        AnsiConsole.MarkupLine($"  :magnifying_glass_tilted_left:  [grey]Semantic search[/]  {ServiceStatus(settings.Embeddings.Enabled, embeddingsActive)}");
        AnsiConsole.MarkupLine($"  :robot:  [grey]Generative model[/] {ServiceStatus(settings.Generation.Enabled, generativeAvailable)}");
        AnsiConsole.MarkupLine($"  :hammer_and_wrench:  [grey]Maintenance[/]      {ServiceStatus(settings.Maintenance.Enabled, settings.Maintenance.Enabled)}");
        AnsiConsole.MarkupLine("  :electric_plug:  [grey]MCP protocol[/]     :check_mark_button: [green]Enabled[/]");
        AnsiConsole.MarkupLine("  :keyboard:  [grey]Stop server[/]      [dim]Ctrl+C[/]");
        AnsiConsole.WriteLine();

        // MCP endpoints
        AnsiConsole.Write(new Rule("[grey]MCP Endpoints[/]").RuleStyle("grey dim").LeftJustified());
        var mcpTable = new Table()
            .NoBorder()
            .HideHeaders()
            .AddColumn(new TableColumn("").Width(8))
            .AddColumn(new TableColumn("").Width(14))
            .AddColumn(new TableColumn(""));
        mcpTable.AddRow("[bold cyan]POST[/]", "[blue]/mcp[/]",     "[grey]MCP JSON-RPC (Streamable HTTP)[/]");
        mcpTable.AddRow("[bold cyan]GET[/]",  "[blue]/mcp/sse[/]", "[grey]MCP SSE transport[/]");
        AnsiConsole.Write(mcpTable);
        AnsiConsole.WriteLine();

        // REST endpoints
        AnsiConsole.Write(new Rule("[grey]REST API[/]").RuleStyle("grey dim").LeftJustified());
        var restTable = new Table()
            .NoBorder()
            .HideHeaders()
            .AddColumn(new TableColumn("").Width(8))
            .AddColumn(new TableColumn("").Width(28))
            .AddColumn(new TableColumn(""));
        restTable.AddRow("[bold green]GET[/]",    "[blue]/api/admin/health[/]",    "[grey]Health check[/]");
        restTable.AddRow("[bold green]GET[/]",    "[blue]/api/admin/stats[/]",     "[grey]Statistics[/]");
        restTable.AddRow("[bold yellow]POST[/]",  "[blue]/api/memory[/]",          "[grey]Create memory[/]");
        restTable.AddRow("[bold green]GET[/]",    "[blue]/api/memory/{id}[/]",     "[grey]Get memory[/]");
        restTable.AddRow("[bold cyan]PUT[/]",     "[blue]/api/memory/{id}[/]",     "[grey]Update memory[/]");
        restTable.AddRow("[bold red]DELETE[/]",   "[blue]/api/memory/{id}[/]",     "[grey]Delete memory[/]");
        restTable.AddRow("[bold yellow]POST[/]",  "[blue]/api/memory/search[/]",   "[grey]Search memories[/]");
        restTable.AddRow("[bold green]GET[/]",    "[blue]/api/generate/status[/]", "[grey]Generative model status[/]");
        restTable.AddRow("[bold yellow]POST[/]",  "[blue]/api/generate[/]",        "[grey]Blocking inference[/]");
        restTable.AddRow("[bold yellow]POST[/]",  "[blue]/api/generate/stream[/]", "[grey]Streaming SSE inference[/]");
        AnsiConsole.Write(restTable);
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Rule(":fire: [green]Ready[/]").RuleStyle("green dim"));
        AnsiConsole.WriteLine();
    }

    private static string ServiceStatus(bool enabled, bool active) => (enabled, active) switch
    {
        (false, _) => ":prohibited: [grey]Disabled[/]",
        (true, true) => ":check_mark_button: [green]Enabled[/]",
        (true, false) => ":warning: [yellow]Enabled (inactive)[/]",
    };
}
