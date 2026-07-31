using AgenticMemory.CodeIndex;
using AgenticMemory.Configuration;
using AgenticMemory.Persistence.Migrations;
using Spectre.Console;

namespace AgenticMemory.Helpers;

public static class ConsoleHelpers
{
    /// <summary>
    /// Reports anything relocated out of the program directory on this start.
    ///
    /// Printed before the banner and unconditionally, not at debug level: a database moving is a
    /// thing the user should be told about once, in plain sight, rather than discover later.
    /// </summary>
    public static void PrintMigrationReport(LegacyDataMigration.Result migration)
    {
        if (!migration.DidAnything && migration.Conflicts.Count == 0 && migration.Failed.Count == 0)
            return;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule(":package: [bold blue]Moved to your user data folder[/]").RuleStyle("blue dim").LeftJustified());

        foreach (var moved in migration.Moved)
            AnsiConsole.MarkupLine($"  :check_mark_button: [grey]{Markup.Escape(moved)}[/]");

        foreach (var conflict in migration.Conflicts)
            AnsiConsole.MarkupLine($"  :warning:  [yellow]{Markup.Escape(conflict)}[/]");

        foreach (var failed in migration.Failed)
            AnsiConsole.MarkupLine($"  :warning:  [yellow]{Markup.Escape(failed)}[/]");

        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Reports a schema upgrade, on the same principle as the file move above: it happened to the
    /// user's data, so it is said out loud once rather than left in a log nobody reads. Silent when
    /// nothing changed, which is every start after the one that upgrades.
    /// </summary>
    public static void PrintSchemaMigrationReport(DatabaseMigrationReport migration)
    {
        if (!migration.Ran) return;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Rule($":card_index_dividers: [bold blue]Database upgraded to schema v{migration.ToVersion}[/]")
                .RuleStyle("blue dim")
                .LeftJustified());

        foreach (var step in migration.Applied)
            AnsiConsole.MarkupLine(
                $"  :check_mark_button: [grey]v{step.FromVersion} → v{step.ToVersion}  " +
                $"{Markup.Escape(step.Name)}[/] [grey dim]· {step.DocumentsTouched} document(s)[/]");

        if (migration.BackupPath is not null)
            AnsiConsole.MarkupLine(
                $"  :floppy_disk: [grey dim]Snapshot before upgrade: {Markup.Escape(migration.BackupPath)}[/]");
        else
            AnsiConsole.MarkupLine(
                "  :warning:  [yellow]No snapshot could be taken before the upgrade.[/]");

        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// The last thing printed before the process gives up. Deliberately loud and deliberately
    /// wordy — this is read by someone whose data did not open, and the message carries what to do.
    /// </summary>
    public static void PrintFatalDatabaseError(string message)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Panel(new Markup($"[white]{Markup.Escape(message)}[/]"))
                .Header(":cross_mark: [bold red]Cannot open the memory database[/]")
                .BorderColor(Color.Red)
                .Expand());
        AnsiConsole.WriteLine();
    }

    public static void PrintStartupBanner(
        AppSettings settings,
        AppPaths paths,
        string listeningOn,
        bool embeddingsActive,
        bool generativeAvailable,
        CodeIndexService? codeIndex = null)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Rule($":brain: [bold blue]Agentic Memory Server[/] [grey dim]v{AppVersion.Current} (MCP SDK)[/]")
                .RuleStyle("blue dim")
                .LeftJustified());
        AnsiConsole.WriteLine();

        // ── Core services ──────────────────────────────────────────────────────
        AnsiConsole.MarkupLine($"  :globe_with_meridians:  [grey]Listening on[/]     [bold white]{Markup.Escape(listeningOn)}[/]");
        AnsiConsole.MarkupLine($"  :locked:  [grey]Auth[/]             {AuthStatus(settings.Server)}");
        AnsiConsole.MarkupLine($"  :floppy_disk:  [grey]Database[/]         [dim]{Markup.Escape(settings.Storage.DatabasePath)}[/] [grey dim]· schema v{DatabaseSchema.Current}[/]");
        AnsiConsole.MarkupLine($"  :file_folder:  [grey]Data[/]             [dim]{Markup.Escape(paths.DataDirectory)}[/] [grey dim]· {OriginLabel(paths.Origin)}[/]");
        AnsiConsole.MarkupLine($"  :card_file_box:  [grey]Models[/]           [dim]{Markup.Escape(paths.ModelsDirectory)}[/]");

        // Embeddings — show model name and vector dimensions when active
        var embeddingDetail = embeddingsActive
            ? $":check_mark_button: [green]Active[/] [grey dim]· {Markup.Escape(settings.Embeddings.ModelFileName)} · {settings.Embeddings.ModelDimensions}-dim[/]"
            : ServiceStatus(settings.Embeddings.Enabled, embeddingsActive);
        AnsiConsole.MarkupLine($"  :magnifying_glass_tilted_left:  [grey]Embeddings[/]       {embeddingDetail}");

        // Generative model — show model folder name when active
        var genModelName = Path.GetFileName(settings.Generation.ModelsPath.TrimEnd('/', '\\'));
        var generativeDetail = generativeAvailable
            ? $":check_mark_button: [green]Active[/] [grey dim]· {Markup.Escape(genModelName)}[/]"
            : ServiceStatus(settings.Generation.Enabled, generativeAvailable);
        AnsiConsole.MarkupLine($"  :robot:  [grey]Generative[/]       {generativeDetail}");

        AnsiConsole.MarkupLine($"  :hammer_and_wrench:  [grey]Maintenance[/]      {ServiceStatus(settings.Maintenance.Enabled, settings.Maintenance.Enabled)}");

        // Code index — one line per registered provider
        if (!settings.CodeIndex.Enabled)
        {
            AnsiConsole.MarkupLine("  :books:  [grey]Code index[/]       :prohibited: [grey]Disabled[/]");
        }
        else if (codeIndex is null || codeIndex.Providers.Count == 0)
        {
            AnsiConsole.MarkupLine("  :books:  [grey]Code index[/]       :warning: [yellow]Enabled (no providers)[/]");
        }
        else
        {
            var providerLabels = codeIndex.Providers.Select(p => ProviderLabel(p, settings.CodeIndex)).ToList();
            AnsiConsole.MarkupLine($"  :books:  [grey]Code index[/]       {string.Join("  [grey]·[/]  ", providerLabels)}");
        }

        AnsiConsole.MarkupLine("  :electric_plug:  [grey]MCP protocol[/]     :check_mark_button: [green]Enabled[/]");
        AnsiConsole.MarkupLine("  :keyboard:  [grey]Stop server[/]      [dim]Ctrl+C[/]");
        AnsiConsole.WriteLine();

        // ── MCP endpoints ──────────────────────────────────────────────────────
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

        // ── REST endpoints ─────────────────────────────────────────────────────
        AnsiConsole.Write(new Rule("[grey]REST API[/]").RuleStyle("grey dim").LeftJustified());
        var restTable = new Table()
            .NoBorder()
            .HideHeaders()
            .AddColumn(new TableColumn("").Width(8))
            .AddColumn(new TableColumn("").Width(30))
            .AddColumn(new TableColumn(""));

        // Memory
        restTable.AddRow("[bold yellow]POST[/]",  "[blue]/api/memory[/]",          "[grey]Create memory[/]");
        restTable.AddRow("[bold green]GET[/]",    "[blue]/api/memory[/]",           "[grey]List memories[/]");
        restTable.AddRow("[bold green]GET[/]",    "[blue]/api/memory/{id}[/]",      "[grey]Get memory[/]");
        restTable.AddRow("[bold cyan]PUT[/]",     "[blue]/api/memory/{id}[/]",      "[grey]Update memory[/]");
        restTable.AddRow("[bold red]DELETE[/]",   "[blue]/api/memory/{id}[/]",      "[grey]Delete memory[/]");
        restTable.AddRow("[bold yellow]POST[/]",  "[blue]/api/memory/search[/]",    "[grey]Search memories[/]");
        // Generation
        restTable.AddRow("[bold green]GET[/]",    "[blue]/api/generate/status[/]",  "[grey]Generative model status[/]");
        restTable.AddRow("[bold yellow]POST[/]",  "[blue]/api/generate[/]",         "[grey]Blocking inference[/]");
        restTable.AddRow("[bold yellow]POST[/]",  "[blue]/api/generate/stream[/]",  "[grey]Streaming SSE inference[/]");
        // Code index
        restTable.AddRow("[bold green]GET[/]",    "[blue]/api/file/context[/]",     "[grey]Compiler-backed file context[/]");
        restTable.AddRow("[bold yellow]POST[/]",  "[blue]/api/file/summary[/]",     "[grey]LLM file summary[/]");
        // File browser
        restTable.AddRow("[bold green]GET[/]",    "[blue]/api/files/browse[/]",     "[grey]Browse filesystem[/]");
        // Key-value store
        restTable.AddRow("[bold green]GET[/]",    "[blue]/api/kv/{key}[/]",         "[grey]Get key-value entry[/]");
        restTable.AddRow("[bold cyan]PUT[/]",     "[blue]/api/kv/{key}[/]",         "[grey]Set key-value entry[/]");
        restTable.AddRow("[bold red]DELETE[/]",   "[blue]/api/kv/{key}[/]",         "[grey]Delete key-value entry[/]");
        // Admin
        restTable.AddRow("[bold green]GET[/]",    "[blue]/api/admin/health[/]",     "[grey]Health check[/]");
        restTable.AddRow("[bold green]GET[/]",    "[blue]/api/admin/stats[/]",      "[grey]Statistics[/]");

        AnsiConsole.Write(restTable);
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Rule(":fire: [green]Ready[/]").RuleStyle("green dim"));
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Says plainly when the store is unauthenticated, and louder when it is also reachable off this
    /// machine. Someone running a companion app should not have to read the configuration to find
    /// out that their memories are on the network.
    /// </summary>
    private static string AuthStatus(ServerSettings server)
    {
        if (server.RequiresAuthentication)
            return $":check_mark_button: [green]API key required[/] [grey dim]· {Markup.Escape(server.ApiKeyHeader)}[/]";

        var loopbackOnly = server.BindAddress is "127.0.0.1" or "::1" or "localhost";

        return loopbackOnly
            ? ":unlocked: [grey]None[/] [grey dim]· this machine only[/]"
            : ":warning:  [yellow]None: reachable from the whole network[/] [grey dim]· set Server:ApiKey[/]";
    }

    private static string OriginLabel(PathOrigin origin) => origin switch
    {
        PathOrigin.CommandLine     => "--data-dir",
        PathOrigin.Environment     => AppPaths.DataDirectoryVariable,
        PathOrigin.Configuration   => "Storage:DataDirectory",
        PathOrigin.Portable        => $"portable ({AppPaths.PortableMarkerFile})",
        _                          => "per-user default",
    };

    private static string ProviderLabel(ICodeIntelligenceProvider provider, CodeIndexSettings settings)
    {
        var name = provider.ProviderType switch
        {
            "dotnet-csharp"               => "C# Roslyn",
            "typescript-react-native-expo" => "TypeScript V8",
            _                             => provider.ProviderType
        };

        // TypeScript provider is only truly active if typescript.js was found
        var tsActive = provider.ProviderType != "typescript-react-native-expo" ||
                       (!string.IsNullOrEmpty(settings.TypeScriptCompilerPath) && File.Exists(settings.TypeScriptCompilerPath)) ||
                       File.Exists(Path.Combine(settings.TypeScriptModelsPath, "typescript.js"));

        return tsActive
            ? $":check_mark_button: [green]{name}[/]"
            : $":warning: [yellow]{name} (typescript.js not found)[/]";
    }

    private static string ServiceStatus(bool enabled, bool active) => (enabled, active) switch
    {
        (false, _)    => ":prohibited: [grey]Disabled[/]",
        (true, true)  => ":check_mark_button: [green]Active[/]",
        (true, false) => ":warning: [yellow]Enabled (inactive)[/]",
    };
}
