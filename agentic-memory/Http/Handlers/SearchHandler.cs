using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Http.Models;

namespace AgenticMemory.Http.Handlers;

/// <summary>
/// Handle memory search requests
/// </summary>
public class SearchHandler : IHandler
{
    private readonly ISearchService? _searchService;

    public SearchHandler(ISearchService? searchService = null)
    {
        _searchService = searchService;
    }

    public Task<Response> HandleAsync(Request request, CancellationToken cancellationToken)
    {
        // Check Accept header for content negotiation
        var accept = request.Accept ?? "application/json";

        if (request.Method == Models.HttpMethod.POST)
        {
            return HandlePostSearchAsync(request, accept, cancellationToken);
        }
        else if (request.Method == Models.HttpMethod.GET)
        {
            return HandleGetSearchAsync(request, accept, cancellationToken);
        }

        return Task.FromResult(Response.MethodNotAllowed("Use GET or POST for search"));
    }

    private async Task<Response> HandlePostSearchAsync(Request request, string accept, CancellationToken cancellationToken)
    {
        // Parse SearchRequest from JSON body
        var searchRequest = request.GetBodyAs<SearchRequest>();

        if (searchRequest is null || string.IsNullOrWhiteSpace(searchRequest.Query))
        {
            return Response.BadRequest("Query is required");
        }

        if (searchRequest.TopN <= 0)
        {
            searchRequest = searchRequest with { TopN = 5 };
        }

        SearchResponse results;

        // Use real search service if available
        if (_searchService is not null)
        {
            var scored = await _searchService.SearchAsync(
                searchRequest.Query,
                searchRequest.TopN,
                searchRequest.Tags,
                cancellationToken);

            results = new SearchResponse
            {
                Query = searchRequest.Query,
                Results = scored.Select(s => new SearchResult
                {
                    Id = s.Memory.Id,
                    Title = s.Memory.Title,
                    Summary = s.Memory.Summary,
                    Score = s.Score,
                    Tags = s.Memory.Tags
                }).ToList(),
                TotalResults = scored.Count
            };
        }
        else
        {
            // Fallback to mock data when no service available
            results = new SearchResponse
            {
                Query = searchRequest.Query,
                Results =
                [
                    new SearchResult
                    {
                        Id = Guid.NewGuid(),
                        Title = "Sample Memory Node",
                        Summary = $"This is a sample result for query: {searchRequest.Query}",
                        Score = 0.95,
                        Tags = ["sample", "demo"]
                    }
                ],
                TotalResults = 1
            };
        }

        if (accept.Contains("text/html"))
        {
            return Response.Html(RenderSearchResultsHtml(results));
        }

        return Response.Ok(results);
    }

    private async Task<Response> HandleGetSearchAsync(Request request, string accept, CancellationToken cancellationToken)
    {
        var query = request.GetQueryParameter("q") ?? request.GetQueryParameter("query");
        var topNStr = request.GetQueryParameter("top_n") ?? request.GetQueryParameter("limit") ?? "5";

        if (string.IsNullOrWhiteSpace(query))
        {
            return Response.BadRequest("Query parameter 'q' is required");
        }

        if (!int.TryParse(topNStr, out var topN) || topN <= 0)
        {
            topN = 5;
        }

        SearchResponse results;

        // Use real search service if available
        if (_searchService is not null)
        {
            var scored = await _searchService.SearchAsync(query, topN, null, cancellationToken);

            results = new SearchResponse
            {
                Query = query,
                Results = scored.Select(s => new SearchResult
                {
                    Id = s.Memory.Id,
                    Title = s.Memory.Title,
                    Summary = s.Memory.Summary,
                    Score = s.Score,
                    Tags = s.Memory.Tags
                }).ToList(),
                TotalResults = scored.Count
            };
        }
        else
        {
            // Fallback to mock data when no service available
            results = new SearchResponse
            {
                Query = query,
                Results =
                [
                    new SearchResult
                    {
                        Id = Guid.NewGuid(),
                        Title = "Sample Memory Node",
                        Summary = $"This is a sample result for query: {query}",
                        Score = 0.95,
                        Tags = ["sample", "demo"]
                    }
                ],
                TotalResults = 1
            };
        }

        if (accept.Contains("text/html"))
        {
            return Response.Html(RenderSearchResultsHtml(results));
        }

        return Response.Ok(results);
    }

    private static string RenderSearchResultsHtml(SearchResponse response)
    {
        var tagsHtml = (List<string> tags) => tags.Count > 0 
            ? string.Join("", tags.Select(t => $"<span>{System.Web.HttpUtility.HtmlEncode(t)}</span>"))
            : "";

        var resultsHtml = response.Results.Count > 0
            ? string.Join("\n", response.Results.Select(r => $"""
                <article class="result">
                    <h2><a href="/memory/{r.Id}.html">{System.Web.HttpUtility.HtmlEncode(r.Title)}</a></h2>
                    <p>{System.Web.HttpUtility.HtmlEncode(r.Summary)}</p>
                    <div class="meta">
                        <span class="score">{r.Score:F2}</span>
                        <span class="tags">{tagsHtml(r.Tags)}</span>
                    </div>
                </article>
                """))
            : """<div class="no-results"><h3>No results found</h3><p>Try different search terms</p></div>""";

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>Search: {{System.Web.HttpUtility.HtmlEncode(response.Query)}} - Agentic Memory</title>
                <link rel="stylesheet" href="/css/main.css">
                <link rel="stylesheet" href="/css/search.css">
            </head>
            <body>
                <div class="container">
                    <div class="results-header">
                        <h1>&#128269; "{{System.Web.HttpUtility.HtmlEncode(response.Query)}}"</h1>
                        <p class="results-count">Found <strong>{{response.TotalResults}}</strong> memories</p>
                    </div>
                    
                    {{resultsHtml}}
                    
                    <div class="back-link">
                        <a href="/">&#8592; New Search</a>
                    </div>
                </div>
            </body>
            </html>
            """;
    }
}

public record SearchRequest
{
    public string Query { get; init; } = string.Empty;
    public int TopN { get; init; } = 5;
    public List<string>? Tags { get; init; }
    public bool IncludeLinked { get; init; } = true;
}

public record SearchResponse
{
    public string Query { get; init; } = string.Empty;
    public List<SearchResult> Results { get; init; } = [];
    public int TotalResults { get; init; }
}

public record SearchResult
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public double Score { get; init; }
    public List<string> Tags { get; init; } = [];
}
