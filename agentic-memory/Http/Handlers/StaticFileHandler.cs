using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Http.Models;

namespace AgenticMemory.Http.Handlers;

/// <summary>
/// Serve static HTML memory nodes and root interface
/// </summary>
public class StaticFileHandler : IHandler
{
    private readonly IMemoryRepository? _repository;

    public StaticFileHandler(IMemoryRepository? repository = null)
    {
        _repository = repository;
    }

    public Task<Response> HandleAsync(Request request, CancellationToken cancellationToken)
    {
        if (request.Method != Models.HttpMethod.GET)
        {
            return Task.FromResult(Response.MethodNotAllowed("Use GET for static files"));
        }

        var path = request.Path;

        // Root path - return search interface
        if (path == "/" || path == "/index.html")
        {
            return Task.FromResult(Response.Html(GetSearchInterfaceHtml()));
        }

        // Memory node HTML - /memory/{id}.html
        if (path.StartsWith("/memory/") && path.EndsWith(".html"))
        {
            // Extract just the path before query string if present
            var pathWithoutQuery = path.Contains('?') ? path[..path.IndexOf('?')] : path;
            var idStr = pathWithoutQuery["/memory/".Length..^".html".Length];
            if (Guid.TryParse(idStr, out var id))
            {
                var searchQuery = request.GetQueryParameter("q");
                return GetMemoryNodeHtmlAsync(id, searchQuery, cancellationToken);
            }
        }

        return Task.FromResult(Response.NotFound("Static file not found"));
    }

    private static string GetSearchInterfaceHtml()
    {
        return """
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>Agentic Memory - AI Memory Search</title>
                <link rel="stylesheet" href="/css/main.css">
            </head>
            <body>
                <div class="container">
                    <h1>&#129504; Agentic Memory</h1>
                    <p class="subtitle">Intelligent memory retrieval for AI agents</p>
                    
                    <div class="search-form">
                        <form action="/search" method="GET">
                            <div class="search-box">
                                <textarea name="q" placeholder="Search memories, concepts, or ideas..." rows="1" autofocus></textarea>
                                <button type="submit">
                                    <span>Search</span>
                                </button>
                            </div>
                            <div class="options">
                                <label>
                                    <span>Results:</span>
                                    <input type="number" name="top_n" value="10" min="1" max="100">
                                </label>
                            </div>
                        </form>
                    </div>
                    
                    <div id="results"></div>
                    
                    <script>
                    const textarea = document.querySelector('.search-box textarea');
                    const form = textarea.closest('form');
                    
                    textarea.addEventListener('input', function() {
                        this.style.height = 'auto';
                        this.style.height = Math.min(this.scrollHeight, 200) + 'px';
                    });
                    textarea.addEventListener('keydown', function(e) {
                        if (e.key === 'Enter' && !e.shiftKey) {
                            e.preventDefault();
                            submitSearch();
                        }
                    });
                    
                    form.addEventListener('submit', function(e) {
                        e.preventDefault();
                        submitSearch();
                    });
                    
                    function submitSearch() {
                        const q = encodeURIComponent(textarea.value);
                        const topN = form.querySelector('input[name="top_n"]').value;
                        window.location.href = '/search?q=' + q + '&top_n=' + topN;
                    }
                    </script>
                    
                    <div class="stats">
                        <p>Ultra-fast semantic search powered by raw TCP &#8226; <a href="/api/admin/stats">Server Stats</a> &#8226; <a href="/api/admin/health">Health</a></p>
                    </div>
                </div>
            </body>
            </html>
            """;
    }

    private async Task<Response> GetMemoryNodeHtmlAsync(Guid id, string? searchQuery, CancellationToken cancellationToken)
    {
        MemoryNode node;

        // Fetch actual memory from repository if available
        if (_repository is not null)
        {
            var entity = await _repository.GetAsync(id, cancellationToken);
            if (entity is null)
            {
                return Response.NotFound($"Memory node with ID {id} not found");
            }

            // Reinforce the memory on access
            await _repository.ReinforceAsync(id, cancellationToken);

            node = entity.ToHandlerModel();
        }
        else
        {
            // Fallback to sample data when no repository available
            node = new MemoryNode
            {
                Id = id,
                Title = "Sample Memory Node",
                Summary = "This is a sample memory node.",
                Content = "Full content of the memory node would go here with detailed information.",
                Tags = ["sample", "demo"],
                CreatedAt = DateTime.UtcNow.AddDays(-7),
                LastAccessedAt = DateTime.UtcNow,
                ReinforcementScore = 1.5,
                LinkedNodeIds = [Guid.NewGuid(), Guid.NewGuid()]
            };
        }

        var linkedNodesHtml = node.LinkedNodeIds.Count > 0
            ? "<ul>" + string.Join("", node.LinkedNodeIds.Select(lid => $"<li><a href=\"/memory/{lid}.html\">{lid}</a></li>")) + "</ul>"
            : "<p class=\"text-muted\">No linked nodes</p>";

        var tagsHtml = string.Join("", node.Tags.Select(t => $"<span class=\"tag\">{System.Web.HttpUtility.HtmlEncode(t)}</span>"));

        // Build back link - return to search results if we came from a search, otherwise home
        var backLink = string.IsNullOrEmpty(searchQuery)
            ? "<a href=\"/\">&#8592; Back to Search</a>"
            : $"<a href=\"/search?q={System.Web.HttpUtility.UrlEncode(searchQuery)}\">&#8592; Back to Search Results</a>";
        
        var deleteRedirectUrl = string.IsNullOrEmpty(searchQuery)
            ? "/"
            : $"/search?q={System.Web.HttpUtility.UrlEncode(searchQuery)}";

        var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>{{System.Web.HttpUtility.HtmlEncode(node.Title)}} - Agentic Memory</title>
                <link rel="stylesheet" href="/css/main.css">
                <link rel="stylesheet" href="/css/memory.css">
            </head>
            <body>
                <div class="container">
                    <div class="breadcrumb">
                        {{backLink}}
                    </div>
                    
                    <div class="memory-header">
                        <h1>{{System.Web.HttpUtility.HtmlEncode(node.Title)}}</h1>
                        <div class="tags">{{tagsHtml}}</div>
                    </div>
                    
                    <div class="summary">{{System.Web.HttpUtility.HtmlEncode(node.Summary)}}</div>
                    
                    <div class="content">{{System.Web.HttpUtility.HtmlEncode(node.Content)}}</div>
                    
                    <div class="meta">
                        <div class="meta-item">
                            <dt>Created</dt>
                            <dd>{{node.CreatedAt:MMM dd, yyyy}} at {{node.CreatedAt:HH:mm}} UTC</dd>
                        </div>
                        <div class="meta-item">
                            <dt>Last Accessed</dt>
                            <dd>{{node.LastAccessedAt:MMM dd, yyyy}} at {{node.LastAccessedAt:HH:mm}} UTC</dd>
                        </div>
                        <div class="meta-item">
                            <dt>Reinforcement</dt>
                            <dd><span class="score-badge">{{node.ReinforcementScore:F2}}</span></dd>
                        </div>
                        <div class="meta-item">
                            <dt>Node ID</dt>
                            <dd><code>{{node.Id}}</code></dd>
                        </div>
                    </div>
                    
                    <div class="linked-nodes">
                        <h3>Linked Nodes</h3>
                        {{linkedNodesHtml}}
                    </div>
                    
                    <div class="management-actions">
                        <h3>Actions</h3>
                        <div class="action-buttons">
                            <button class="btn btn-danger" onclick="deleteMemory('{{node.Id}}')">
                                Delete Memory
                            </button>
                            <button class="btn btn-secondary" onclick="reinforceMemory('{{node.Id}}')">
                                Reinforce
                            </button>
                            <button class="btn btn-secondary" onclick="copyId('{{node.Id}}')">
                                Copy ID
                            </button>
                        </div>
                    </div>
                </div>
                
                <script>
                const deleteRedirectUrl = '{{deleteRedirectUrl}}';
                
                async function deleteMemory(id) {
                    if (!confirm('Are you sure you want to delete this memory? This action cannot be undone.')) {
                        return;
                    }
                    
                    try {
                        const response = await fetch('/api/memory/' + id, {
                            method: 'DELETE'
                        });
                        
                        if (response.ok || response.status === 204) {
                            alert('Memory deleted successfully');
                            window.location.href = deleteRedirectUrl;
                        } else {
                            const error = await response.text();
                            alert('Failed to delete memory: ' + error);
                        }
                    } catch (e) {
                        alert('Error deleting memory: ' + e.message);
                    }
                }
                
                async function reinforceMemory(id) {
                    try {
                        const response = await fetch('/api/memory/' + id + '/reinforce', {
                            method: 'POST'
                        });
                        
                        if (response.ok) {
                            alert('Memory reinforced successfully');
                            location.reload();
                        } else {
                            const error = await response.text();
                            alert('Failed to reinforce memory: ' + error);
                        }
                    } catch (e) {
                        alert('Error reinforcing memory: ' + e.message);
                    }
                }
                
                function copyId(id) {
                    navigator.clipboard.writeText(id).then(() => {
                        alert('Memory ID copied to clipboard');
                    }).catch(e => {
                        alert('Failed to copy: ' + e.message);
                    });
                }
                </script>
            </body>
            </html>
            """;

        return Response.Html(html);
    }
}
