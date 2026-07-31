using System.Text;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Search;
using AgenticMemory.CodeIndex;
using AgenticMemory.Configuration;
using AgenticMemory.Extensions;
using AgenticMemory.Helpers;
using AgenticMemory.Logging;
using AgenticMemory.Middleware;
using AgenticMemory.Persistence;
using AgenticMemory.Persistence.Migrations;
using AgenticMemory.Tools;

namespace AgenticMemory;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var appBasePath = AppContext.BaseDirectory;

        // The content root is handed to the builder rather than assigned to builder.Environment
        // afterwards. Assigning it after construction updates the path but leaves the web-root
        // file provider pointing wherever the host first resolved it, so wwwroot silently stops
        // being served: the REST API and MCP keep working and the dashboard 404s. It reproduces
        // by starting the published binary through a relative path — which is exactly how a
        // parent process tends to launch a sidecar.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = appBasePath,
        });

        // A published build has a real wwwroot beside the binary. A development build does not: the
        // Web SDK leaves the dashboard where Vite wrote it and emits a manifest in the output folder
        // that maps it back. The slim builder is the one host builder that does not read that
        // manifest, so without this the dashboard 404s under `dotnet run` while the API answers
        // normally. In a published build there is no manifest and this does nothing.
        builder.WebHost.UseStaticWebAssets();

        builder.Configuration
            .SetBasePath(appBasePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

        var (settings, paths) = LoadAndResolveSettings(builder.Configuration, appBasePath, args);

        ConfigureKestrel(builder, settings);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new SpectreConsoleLoggerProvider());

        // Reported here rather than with the rest of the banner: a model download can sit between
        // the two for minutes, and "we moved your database" is the first thing a returning user
        // should see, not something scrolled off the top by progress bars.
        ConsoleHelpers.PrintMigrationReport(LegacyDataMigration.Run(paths, settings));

        // Two migrations, in the only order that works: the file is put in its final location first,
        // then its contents are brought up to the current schema. Opening the database is done here,
        // explicitly, rather than left to whichever service the container happened to resolve first —
        // a schema migration is not something to discover halfway through building a container.
        var database = OpenDatabase(settings);
        ConsoleHelpers.PrintSchemaMigrationReport(database.Migration);

        builder.Services.AddSingleton(paths);
        builder.Services.AddAgenticMemoryServices(settings, database);
        ConfigureMcpServer(builder);

        var app = builder.Build();

        // Before anything is routed, so no endpoint can be reached without passing it. A no-op when
        // no key is configured.
        app.UseApiKeyAuthentication(settings.Server);

        app.UseStaticFiles();
        app.MapMcp("/mcp");
        app.MapRestApiEndpoints();
        app.MapFallbackToFile("index.html");

        PrintStartupInfo(app, settings, paths);
        app.ReRegisterSavedWorkspaces();

        // Restore the persisted active project so the watcher can resume on startup
        var activeProject = app.Services.GetService<AgenticMemory.CodeIndex.ActiveProjectService>();
        activeProject?.Load();

        await app.RunAsync();
    }

    /// <summary>
    /// Opens the database, which is also what runs any pending schema migration.
    ///
    /// A migration that cannot be completed ends the process instead of degrading: continuing would
    /// mean this build writing to data it has already established it cannot read correctly. Exiting
    /// non-zero with the reason on the console is also the only thing a host process can act on — an
    /// Electron parent sees a sidecar that failed to start and why, rather than one that came up and
    /// quietly served the wrong answers.
    /// </summary>
    private static SharedLiteDatabase OpenDatabase(AppSettings settings)
    {
        var logger = new SpectreConsoleLoggerProvider().CreateLogger("Database");

        try
        {
            return new SharedLiteDatabase(
                settings.Storage.DatabasePath, settings.Maintenance.BackupPath, logger);
        }
        catch (Exception ex) when (
            ex is DatabaseSchemaTooNewException or DatabaseMigrationFailedException)
        {
            ConsoleHelpers.PrintFatalDatabaseError(ex.Message);
            Environment.Exit(1);
            throw; // unreachable; keeps the compiler happy about the return path
        }
    }

    /// <summary>
    /// Binds configuration and makes every path absolute: per-user state under the data directory,
    /// model weights beside the program. After this runs, no path in <see cref="AppSettings"/> is
    /// relative — see <see cref="AppPaths"/> for where the line between the two falls and why it
    /// matters when the server is shipped as a sidecar.
    /// </summary>
    private static (AppSettings Settings, AppPaths Paths) LoadAndResolveSettings(
        ConfigurationManager configuration, string appBasePath, string[] args)
    {
        // First pass: the bundled file only, read purely to learn where the data directory is.
        var settings = new AppSettings();
        configuration.Bind(settings);

        var paths = AppPaths.Resolve(
            appBasePath, args, settings.Storage.DataDirectory, settings.Storage.ModelsDirectory);

        paths.EnsureUsable();

        // Second pass: an optional overlay in the data directory, which is where a packaged app can
        // actually be edited — the bundled file is inside a read-only, update-replaced bundle. It
        // cannot move the data directory, since resolving that is what found this file.
        configuration.AddJsonFile(
            Path.Combine(paths.DataDirectory, "appsettings.json"), optional: true, reloadOnChange: false);

        settings = new AppSettings();
        configuration.Bind(settings);

        settings.Storage.DatabasePath =
            paths.InData(settings.Storage.DatabasePath, StorageSettings.DefaultDatabaseFileName);
        settings.Maintenance.BackupPath =
            paths.InData(settings.Maintenance.BackupPath, MaintenanceSettings.DefaultRelativeBackupPath);

        settings.Embeddings.ModelsPath =
            paths.InModels(settings.Embeddings.ModelsPath, EmbeddingsSettings.DefaultRelativeModelsPath);
        settings.Generation.ModelsPath =
            paths.InModels(settings.Generation.ModelsPath, GenerationSettings.DefaultRelativeModelsPath);
        settings.CodeIndex.TypeScriptModelsPath =
            paths.InModels(settings.CodeIndex.TypeScriptModelsPath, CodeIndexSettings.DefaultRelativeTypeScriptPath);

        ApplyCommandLineOverrides(settings, args);

        // Last word on the API key, so a host that generates one per install can pass it without
        // writing to a file. Deliberately not a command-line flag — see ServerSettings.ApiKey.
        if (Environment.GetEnvironmentVariable(ServerSettings.ApiKeyVariable) is { } key
            && !string.IsNullOrWhiteSpace(key))
        {
            settings.Server.ApiKey = key.Trim();
        }

        return (settings, paths);
    }

    private static void ApplyCommandLineOverrides(AppSettings settings, string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var port))
                    settings.Server.Port = port;
            }
            else if ((args[i] == "--bind" || args[i] == "-b") && i + 1 < args.Length)
            {
                settings.Server.BindAddress = args[i + 1];
            }
        }
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder, AppSettings settings)
    {
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.Listen(
                System.Net.IPAddress.Parse(settings.Server.BindAddress),
                settings.Server.Port);
        });
    }

    private static void ConfigureMcpServer(WebApplicationBuilder builder)
    {
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "agentic-memory",

                    // The real build version, not a literal. A client that logs which server it
                    // spoke to is only useful if the answer changes when the server does.
                    Version = AppVersion.Current,
                };
                options.ServerInstructions = McpInstructions;
            })
            .WithHttpTransport()
            .WithTools<MemoryTools>()
            .WithTools<AgenticMemory.Tools.CodeIndexTools>();
    }

    private const string McpInstructions =
        """
        You have compiler-level code intelligence via the agentic-memory MCP server. It is precise and
        scoped — prefer it over grep and cold file reads, which cost far more tokens for less accuracy.

        Tool order — follow this every session:

        1. get_subproject_context — call once at the start to learn the workspace layout (sub-projects,
           entry points, manifests). The 'subproject' names it returns scope every other tool.
        2. get_file_context(path, subproject) — before read_file on any file. Returns its symbols,
           signatures, imports/exports and dependencies without the bodies. Usually enough on its own.
        3. get_symbol_context(symbol, subproject) — instead of grep for any named symbol. Returns the
           definition, implementations, callers and reference count.
        4. get_symbol_sourcecode(symbol, subproject) — instead of read_file when you know the symbol.
           Returns just that symbol's source.
        5. search_code(query, subproject) — when you don't yet know the file or symbol; semantic/keyword
           file search. Then narrow with the tools above.

        Always pass the 'subproject' argument when you know it — it scopes the lookup and improves
        accuracy on multi-project repositories. Fall back to grep or read_file only when these return
        nothing. All tools act on the active workspace (or the only one registered).
        """;

    private static void PrintStartupInfo(WebApplication app, AppSettings settings, AppPaths paths)
    {
        var listeningOn = $"http://{settings.Server.BindAddress}:{settings.Server.Port}";

        var embeddingService = app.Services.GetRequiredService<IEmbeddingService>();
        var searchEngine = app.Services.GetRequiredService<ISearchService>() as MemorySearchEngine;
        var embeddingsActive = embeddingService.IsAvailable && searchEngine?.SemanticSearchAvailable == true;

        // Eagerly resolve generative model service — triggers auto-download if needed
        var generativeService = app.Services.GetRequiredService<IGenerativeModelService>();

        var codeIndex = app.Services.GetService<CodeIndexService>();
        ConsoleHelpers.PrintStartupBanner(
            settings, paths, listeningOn, embeddingsActive, generativeService.IsAvailable, codeIndex);
    }
}

