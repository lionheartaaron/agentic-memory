using AgenticMemory.Configuration;

namespace AgenticMemory.Helpers;

/// <summary>
/// Helper methods for console output formatting.
/// </summary>
public static class ConsoleHelpers
{
    private const int BoxWidth = 96;

    /// <summary>
    /// Prints the startup banner to the console.
    /// </summary>
    public static void PrintStartupBanner(AppSettings settings, string listeningOn, bool embeddingsActive)
    {
        Console.WriteLine();
        Console.WriteLine(TopBorder());
        Console.WriteLine(Header("Agentic Memory Server (MCP SDK)"));
        Console.WriteLine(Separator());
        Console.WriteLine(Line($"Listening on: {listeningOn}"));
        Console.WriteLine(Line($"Semantic Search: {Status(settings.Embeddings.Enabled, embeddingsActive)}"));
        Console.WriteLine(Line($"Conflict Resolution: Enabled"));
        Console.WriteLine(Line($"Background Maintenance: {Status(settings.Maintenance.Enabled, true)}"));
        Console.WriteLine(Line($"MCP Protocol: Enabled (Official SDK)"));
        Console.WriteLine(Line("Press Ctrl+C to stop"));
        Console.WriteLine(Separator());
        Console.WriteLine(Line("MCP Endpoints:"));
        Console.WriteLine(Line(Endpoint("POST", "/mcp", "MCP JSON-RPC (Streamable HTTP)")));
        Console.WriteLine(Line(Endpoint("GET", "/mcp/sse", "MCP SSE transport")));
        Console.WriteLine(Separator());
        Console.WriteLine(Line("REST API (backward compatible):"));
        Console.WriteLine(Line(Endpoint("GET", "/api/admin/health", "Health check")));
        Console.WriteLine(Line(Endpoint("GET", "/api/admin/stats", "Server statistics")));
        Console.WriteLine(Line(Endpoint("POST", "/api/memory", "Create memory")));
        Console.WriteLine(Line(Endpoint("GET", "/api/memory/{id}", "Get memory")));
        Console.WriteLine(Line(Endpoint("PUT", "/api/memory/{id}", "Update memory")));
        Console.WriteLine(Line(Endpoint("DELETE", "/api/memory/{id}", "Delete memory")));
        Console.WriteLine(Line(Endpoint("POST", "/api/memory/search", "Search memories")));
        Console.WriteLine(BottomBorder());
        Console.WriteLine();
    }

    private static string TopBorder() => $"+{new string('-', BoxWidth - 2)}+";
    private static string BottomBorder() => $"+{new string('-', BoxWidth - 2)}+";
    private static string Separator() => $"+{new string('-', BoxWidth - 2)}+";
    private static string Line(string text) => $"|  {text.PadRight(BoxWidth - 4)}|";
    private static string Header(string text) => $"|{text.PadLeft((BoxWidth + text.Length) / 2).PadRight(BoxWidth - 2)}|";
    private static string Status(bool enabled, bool active) => enabled ? (active ? "Enabled" : "Enabled (inactive)") : "Disabled";
    private static string Endpoint(string method, string path, string description) => $"  {method,-6} {path,-34} {description}";
}
