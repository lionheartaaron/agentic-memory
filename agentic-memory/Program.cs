using System.Text;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Search;
using AgenticMemory.Configuration;
using AgenticMemory.Extensions;
using AgenticMemory.Helpers;
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
        builder.Logging.AddConsole();

        EnsureDataDirectoryExists(settings);

        builder.Services.AddAgenticMemoryServices(settings);
        ConfigureMcpServer(builder);

        var app = builder.Build();

        app.MapMcp("/mcp");
        app.MapRestApiEndpoints();

        PrintStartupInfo(app, settings);

        await app.RunAsync();
    }

    private static AppSettings LoadAndResolveSettings(ConfigurationManager configuration, string appBasePath, string[] args)
    {
        var settings = new AppSettings();
        configuration.Bind(settings);

        settings.Storage.DatabasePath = ResolvePath(settings.Storage.DatabasePath, appBasePath);
        settings.Embeddings.ModelsPath = ResolvePath(settings.Embeddings.ModelsPath, appBasePath);

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
            })
            .WithHttpTransport()
            .WithTools<MemoryTools>();
    }

    private static void PrintStartupInfo(WebApplication app, AppSettings settings)
    {
        var listeningOn = $"http://{settings.Server.BindAddress}:{settings.Server.Port}";
        var embeddingService = app.Services.GetRequiredService<IEmbeddingService>();
        var searchEngine = app.Services.GetRequiredService<ISearchService>() as MemorySearchEngine;
        var embeddingsActive = embeddingService.IsAvailable && searchEngine?.SemanticSearchAvailable == true;

        ConsoleHelpers.PrintStartupBanner(settings, listeningOn, embeddingsActive);
    }
}

