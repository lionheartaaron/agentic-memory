using System.Security.Cryptography;
using System.Text;
using AgenticMemory.Http.Models;
using HttpMethod = AgenticMemory.Http.Models.HttpMethod;

namespace AgenticMemory.Http.Handlers;

/// <summary>
/// Serve CSS stylesheets with browser caching support
/// </summary>
public class CssHandler : IHandler
{
    private const string ContentTypeCss = "text/css; charset=utf-8";

    // Cache duration: 1 year (immutable assets), or 1 day for development
    private const int CacheMaxAgeSeconds = 86400; // 1 day - increase to 31536000 (1 year) for production

    // Pre-computed CSS and ETag for performance
    private static readonly Lazy<(string Css, string ETag)> MainStylesheet = new(ComputeMainStylesheet);
    private static readonly Lazy<(string Css, string ETag)> MemoryStylesheet = new(ComputeMemoryStylesheet);
    private static readonly Lazy<(string Css, string ETag)> SearchResultsStylesheet = new(ComputeSearchResultsStylesheet);

    public Task<Response> HandleAsync(Request request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.GET)
        {
            return Task.FromResult(Response.MethodNotAllowed("Use GET for CSS"));
        }

        var path = request.Path;

        // Check If-None-Match header for conditional request
        var ifNoneMatch = request.GetHeader("If-None-Match");

        return path switch
        {
            "/css/main.css" => ServeCss(MainStylesheet.Value, ifNoneMatch),
            "/css/memory.css" => ServeCss(MemoryStylesheet.Value, ifNoneMatch),
            "/css/search.css" => ServeCss(SearchResultsStylesheet.Value, ifNoneMatch),
            _ => Task.FromResult(Response.NotFound("CSS file not found"))
        };
    }

    private static Task<Response> ServeCss((string Css, string ETag) stylesheet, string? ifNoneMatch)
    {
        // Return 304 Not Modified if ETag matches
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == stylesheet.ETag)
        {
            return Task.FromResult(new Response
            {
                StatusCode = 304,
                ReasonPhrase = "Not Modified",
                Headers =
                {
                    ["ETag"] = stylesheet.ETag,
                    ["Cache-Control"] = $"public, max-age={CacheMaxAgeSeconds}"
                }
            });
        }

        return Task.FromResult(new Response
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Body = stylesheet.Css,
            ContentType = ContentTypeCss,
            Headers =
            {
                ["ETag"] = stylesheet.ETag,
                ["Cache-Control"] = $"public, max-age={CacheMaxAgeSeconds}",
                ["Vary"] = "Accept-Encoding"
            }
        });
    }

    private static string ComputeETag(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return $"\"{Convert.ToHexString(hash)[..16]}\""; // First 16 chars of hash
    }

    private static (string Css, string ETag) ComputeMainStylesheet()
    {
        var css = """
            /* Agentic Memory - Modern AI Assistant Stylesheet */
            
            :root {
                --font-sans: system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
                --font-mono: ui-monospace, SFMono-Regular, 'SF Mono', Menlo, Consolas, monospace;
                --primary: #6366f1;
                --primary-hover: #4f46e5;
                --primary-light: #e0e7ff;
                --accent: #8b5cf6;
                --success: #10b981;
                --warning: #f59e0b;
                --bg: #0f0f23;
                --bg-secondary: #1a1a2e;
                --bg-card: #16213e;
                --bg-input: #1a1a2e;
                --text: #e2e8f0;
                --text-muted: #94a3b8;
                --text-dim: #64748b;
                --border: #334155;
                --glow: 0 0 20px rgba(99, 102, 241, 0.3);
                --shadow: 0 4px 24px rgba(0, 0, 0, 0.4);
                --radius: 12px;
                --radius-lg: 16px;
                --radius-full: 9999px;
                --transition: all 0.2s cubic-bezier(0.4, 0, 0.2, 1);
            }

            * { box-sizing: border-box; margin: 0; padding: 0; }

            body {
                font-family: var(--font-sans);
                background: var(--bg);
                background-image: 
                    radial-gradient(ellipse at top, rgba(99, 102, 241, 0.1) 0%, transparent 50%),
                    radial-gradient(ellipse at bottom, rgba(139, 92, 246, 0.05) 0%, transparent 50%);
                min-height: 100vh;
                color: var(--text);
                line-height: 1.6;
            }

            .container {
                max-width: 720px;
                margin: 0 auto;
                padding: 40px 20px;
            }

            /* Header */
            h1 {
                font-size: 2.5rem;
                font-weight: 700;
                text-align: center;
                margin-bottom: 8px;
                background: linear-gradient(135deg, var(--primary) 0%, var(--accent) 100%);
                -webkit-background-clip: text;
                -webkit-text-fill-color: transparent;
                background-clip: text;
            }

            .subtitle {
                text-align: center;
                color: var(--text-muted);
                margin-bottom: 40px;
                font-size: 1.1rem;
            }

            /* Search Form */
            .search-form {
                background: var(--bg-card);
                border: 1px solid var(--border);
                border-radius: var(--radius-lg);
                padding: 24px;
                box-shadow: var(--shadow);
                position: relative;
                overflow: hidden;
            }

            .search-form::before {
                content: '';
                position: absolute;
                top: 0;
                left: 0;
                right: 0;
                height: 2px;
                background: linear-gradient(90deg, var(--primary), var(--accent), var(--primary));
                background-size: 200% 100%;
                animation: shimmer 3s linear infinite;
            }

            @keyframes shimmer {
                0% { background-position: 200% 0; }
                100% { background-position: -200% 0; }
            }

            .search-box {
                display: flex;
                align-items: flex-end;
                gap: 12px;
                margin-bottom: 16px;
            }

            .search-box textarea {
                flex: 1;
                padding: 14px 18px;
                font-size: 1rem;
                font-family: var(--font-sans);
                background: var(--bg-input);
                border: 1px solid var(--border);
                border-radius: var(--radius);
                color: var(--text);
                outline: none;
                transition: var(--transition);
                resize: none;
                overflow-y: hidden;
                min-height: 52px;
                max-height: 200px;
                line-height: 1.5;
            }

            .search-box textarea::placeholder {
                color: var(--text-dim);
            }

            .search-box textarea:focus {
                border-color: var(--primary);
                box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.2);
                overflow-y: auto;
            }

            input[type="number"] {
                width: 70px;
                padding: 8px 12px;
                background: var(--bg-input);
                border: 1px solid var(--border);
                border-radius: var(--radius);
                color: var(--text);
                font-size: 0.9rem;
            }

            button {
                padding: 14px 28px;
                font-size: 1rem;
                font-weight: 600;
                background: linear-gradient(135deg, var(--primary) 0%, var(--accent) 100%);
                color: white;
                border: none;
                border-radius: var(--radius);
                cursor: pointer;
                transition: var(--transition);
                white-space: nowrap;
            }

            button:hover {
                transform: translateY(-2px);
                box-shadow: var(--glow);
            }

            button:active {
                transform: translateY(0);
            }

            .options {
                display: flex;
                align-items: center;
                gap: 16px;
                color: var(--text-muted);
                font-size: 0.9rem;
            }

            .options label {
                display: flex;
                align-items: center;
                gap: 8px;
            }

            /* Results */
            #results { margin-top: 32px; }

            .result {
                background: var(--bg-card);
                border: 1px solid var(--border);
                border-radius: var(--radius);
                padding: 20px;
                margin-bottom: 16px;
                transition: var(--transition);
            }

            .result:hover {
                border-color: var(--primary);
                transform: translateX(4px);
            }

            .result h3 {
                font-size: 1.1rem;
                margin-bottom: 8px;
            }

            .result h3 a {
                color: var(--text);
                text-decoration: none;
                transition: var(--transition);
            }

            .result h3 a:hover {
                color: var(--primary);
            }

            .result p {
                color: var(--text-muted);
                font-size: 0.95rem;
                margin-bottom: 12px;
            }

            .result .meta {
                display: flex;
                gap: 16px;
                font-size: 0.85rem;
                color: var(--text-dim);
            }

            /* Footer */
            .stats {
                text-align: center;
                color: var(--text-dim);
                margin-top: 40px;
                padding-top: 20px;
                border-top: 1px solid var(--border);
                font-size: 0.9rem;
            }

            .stats a {
                color: var(--primary);
                text-decoration: none;
            }

            .stats a:hover {
                text-decoration: underline;
            }

            /* Links */
            a { color: var(--primary); text-decoration: none; transition: var(--transition); }
            a:hover { color: var(--accent); }

            /* Utilities */
            .text-muted { color: var(--text-muted); }
            .text-center { text-align: center; }
            .mt-1 { margin-top: 8px; }
            .mt-2 { margin-top: 16px; }
            .mt-3 { margin-top: 24px; }

            /* Responsive */
            @media (max-width: 640px) {
                h1 { font-size: 1.75rem; }
                .search-box { flex-direction: column; }
                button { width: 100%; }
            }
            """;
        return (css, ComputeETag(css));
    }

    private static (string Css, string ETag) ComputeMemoryStylesheet()
    {
        var css = """
            /* Agentic Memory - Memory Node Stylesheet */

            .breadcrumb {
                display: inline-flex;
                align-items: center;
                gap: 8px;
                padding: 8px 16px;
                background: var(--bg-secondary);
                border-radius: var(--radius-full);
                color: var(--text-muted);
                font-size: 0.9rem;
                margin-bottom: 24px;
            }

            .breadcrumb a {
                color: var(--primary);
                display: flex;
                align-items: center;
                gap: 6px;
            }

            .memory-header {
                margin-bottom: 32px;
            }

            .memory-header h1 {
                font-size: 2rem;
                margin-bottom: 16px;
                text-align: left;
                background: none;
                -webkit-text-fill-color: var(--text);
            }

            .summary {
                font-size: 1.1rem;
                color: var(--text-muted);
                padding: 16px 20px;
                background: linear-gradient(135deg, rgba(99, 102, 241, 0.1) 0%, rgba(139, 92, 246, 0.1) 100%);
                border-left: 3px solid var(--primary);
                border-radius: 0 var(--radius) var(--radius) 0;
                margin: 24px 0;
            }

            .content {
                background: var(--bg-card);
                border: 1px solid var(--border);
                border-radius: var(--radius);
                padding: 24px;
                margin: 24px 0;
                white-space: pre-wrap;
                font-size: 0.95rem;
                line-height: 1.8;
            }

            .meta {
                display: grid;
                grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
                gap: 16px;
                background: var(--bg-card);
                border: 1px solid var(--border);
                border-radius: var(--radius);
                padding: 20px;
                margin: 24px 0;
            }

            .meta-item {
                display: flex;
                flex-direction: column;
                gap: 4px;
            }

            .meta dt {
                font-size: 0.75rem;
                text-transform: uppercase;
                letter-spacing: 0.05em;
                color: var(--text-dim);
                font-weight: 600;
            }

            .meta dd {
                color: var(--text);
                font-size: 0.95rem;
            }

            .tags {
                display: flex;
                gap: 8px;
                flex-wrap: wrap;
            }

            .tag {
                display: inline-flex;
                align-items: center;
                padding: 4px 12px;
                background: linear-gradient(135deg, var(--primary) 0%, var(--accent) 100%);
                color: white;
                border-radius: var(--radius-full);
                font-size: 0.8rem;
                font-weight: 500;
            }

            .linked-nodes {
                margin-top: 32px;
            }

            .linked-nodes h3 {
                font-size: 1.1rem;
                color: var(--text);
                margin-bottom: 16px;
                display: flex;
                align-items: center;
                gap: 8px;
            }

            .linked-nodes h3::before {
                content: '';
                width: 4px;
                height: 20px;
                background: var(--primary);
                border-radius: 2px;
            }

            .linked-nodes ul {
                list-style: none;
                display: grid;
                gap: 8px;
            }

            .linked-nodes li {
                background: var(--bg-card);
                border: 1px solid var(--border);
                border-radius: var(--radius);
                transition: var(--transition);
            }

            .linked-nodes li:hover {
                border-color: var(--primary);
                transform: translateX(4px);
            }

            .linked-nodes a {
                display: block;
                padding: 12px 16px;
                color: var(--text);
                font-family: var(--font-mono);
                font-size: 0.85rem;
            }

            code {
                background: var(--bg-secondary);
                padding: 2px 8px;
                border-radius: 4px;
                font-family: var(--font-mono);
                font-size: 0.85em;
                color: var(--accent);
            }

            .score-badge {
                display: inline-flex;
                align-items: center;
                gap: 4px;
                padding: 4px 10px;
                background: rgba(16, 185, 129, 0.2);
                color: var(--success);
                border-radius: var(--radius-full);
                font-size: 0.85rem;
                font-weight: 600;
            }
            """;
        return (css, ComputeETag(css));
    }

    private static (string Css, string ETag) ComputeSearchResultsStylesheet()
    {
        var css = """
            /* Agentic Memory - Search Results Stylesheet */

            .results-header {
                margin-bottom: 24px;
            }

            .results-header h1 {
                font-size: 1.5rem;
                text-align: left;
                margin-bottom: 8px;
                background: none;
                -webkit-text-fill-color: var(--text);
            }

            .results-count {
                color: var(--text-muted);
                font-size: 0.95rem;
            }

            .results-count strong {
                color: var(--primary);
            }

            .result {
                background: var(--bg-card);
                border: 1px solid var(--border);
                border-radius: var(--radius);
                padding: 20px;
                margin-bottom: 12px;
                transition: var(--transition);
                position: relative;
                overflow: hidden;
            }

            .result::before {
                content: '';
                position: absolute;
                left: 0;
                top: 0;
                bottom: 0;
                width: 3px;
                background: var(--primary);
                opacity: 0;
                transition: var(--transition);
            }

            .result:hover {
                border-color: var(--primary);
                transform: translateY(-2px);
                box-shadow: 0 8px 24px rgba(0, 0, 0, 0.3);
            }

            .result:hover::before {
                opacity: 1;
            }

            .result h2 {
                font-size: 1.15rem;
                margin-bottom: 8px;
                font-weight: 600;
            }

            .result h2 a {
                color: var(--text);
                text-decoration: none;
            }

            .result h2 a:hover {
                color: var(--primary);
            }

            .result p {
                color: var(--text-muted);
                font-size: 0.9rem;
                margin-bottom: 12px;
                display: -webkit-box;
                -webkit-line-clamp: 2;
                -webkit-box-orient: vertical;
                overflow: hidden;
            }

            .meta {
                display: flex;
                align-items: center;
                gap: 16px;
                font-size: 0.85rem;
            }

            .score {
                display: inline-flex;
                align-items: center;
                gap: 4px;
                padding: 3px 10px;
                background: rgba(16, 185, 129, 0.15);
                color: var(--success);
                border-radius: var(--radius-full);
                font-weight: 600;
            }

            .score::before {
                content: '';
                width: 6px;
                height: 6px;
                background: var(--success);
                border-radius: 50%;
            }

            .tags {
                color: var(--text-dim);
            }

            .tags span {
                padding: 2px 8px;
                background: var(--bg-secondary);
                border-radius: 4px;
                margin-right: 6px;
                font-size: 0.8rem;
            }

            .back-link {
                margin-top: 32px;
                padding-top: 24px;
                border-top: 1px solid var(--border);
            }

            .back-link a {
                display: inline-flex;
                align-items: center;
                gap: 8px;
                color: var(--text-muted);
                font-size: 0.9rem;
                transition: var(--transition);
            }

            .back-link a:hover {
                color: var(--primary);
            }

            /* Empty state */
            .no-results {
                text-align: center;
                padding: 60px 20px;
                color: var(--text-muted);
            }

            .no-results h3 {
                font-size: 1.25rem;
                color: var(--text);
                margin-bottom: 8px;
            }
            """;
        return (css, ComputeETag(css));
    }
}
