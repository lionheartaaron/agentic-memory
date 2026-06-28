using System.Text;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Search;
using AgenticMemory.CodeIndex;
using AgenticMemory.Configuration;
using AgenticMemory.Extensions;
using AgenticMemory.Helpers;
using AgenticMemory.Logging;
using AgenticMemory.Tools;

namespace AgenticMemory;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var appBasePath = AppContext.BaseDirectory;
        var builder = WebApplication.CreateSlimBuilder(args);

        builder.Environment.ContentRootPath = appBasePath;
        builder.Configuration
            .SetBasePath(appBasePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

        var settings = LoadAndResolveSettings(builder.Configuration, appBasePath, args);

        ConfigureKestrel(builder, settings);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new SpectreConsoleLoggerProvider());

        EnsureDataDirectoryExists(settings);

        builder.Services.AddAgenticMemoryServices(settings);
        ConfigureMcpServer(builder);

        var app = builder.Build();

        app.UseStaticFiles();
        app.MapMcp("/mcp");
        app.MapRestApiEndpoints();
        app.MapFallbackToFile("index.html");

        PrintStartupInfo(app, settings);
        app.MigrateProjectsToWorkspaces();
        app.ReRegisterSavedWorkspaces();

        // Restore the persisted active project so the watcher can resume on startup
        var activeProject = app.Services.GetService<AgenticMemory.CodeIndex.ActiveProjectService>();
        activeProject?.Load();

        await app.RunAsync();
    }

    private static AppSettings LoadAndResolveSettings(ConfigurationManager configuration, string appBasePath, string[] args)
    {
        var settings = new AppSettings();
        configuration.Bind(settings);

        settings.Storage.DatabasePath = ResolvePath(settings.Storage.DatabasePath, appBasePath);
        settings.Embeddings.ModelsPath = ResolvePath(settings.Embeddings.ModelsPath, appBasePath);
        settings.Generation.ModelsPath = ResolvePath(settings.Generation.ModelsPath, appBasePath);

        ApplyCommandLineOverrides(settings, args);

        return settings;
    }

    private static string ResolvePath(string path, string basePath) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(basePath, path));

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

    private static void EnsureDataDirectoryExists(AppSettings settings)
    {
        var dataDir = Path.GetDirectoryName(settings.Storage.DatabasePath);
        if (!string.IsNullOrEmpty(dataDir) && !Directory.Exists(dataDir))
        {
            Directory.CreateDirectory(dataDir);
        }
    }

    private static void ConfigureMcpServer(WebApplicationBuilder builder)
    {
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "agentic-memory",
                    Version = "1.0.0"
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

    private static void PrintStartupInfo(WebApplication app, AppSettings settings)
    {
        var listeningOn = $"http://{settings.Server.BindAddress}:{settings.Server.Port}";

        var embeddingService = app.Services.GetRequiredService<IEmbeddingService>();
        var searchEngine = app.Services.GetRequiredService<ISearchService>() as MemorySearchEngine;
        var embeddingsActive = embeddingService.IsAvailable && searchEngine?.SemanticSearchAvailable == true;

        // Eagerly resolve generative model service — triggers auto-download if needed
        var generativeService = app.Services.GetRequiredService<IGenerativeModelService>();

        var codeIndex = app.Services.GetService<CodeIndexService>();
        ConsoleHelpers.PrintStartupBanner(settings, listeningOn, embeddingsActive, generativeService.IsAvailable, codeIndex);
    }
}

