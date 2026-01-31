using System.Text.RegularExpressions;
using AgenticMemory.Http.Handlers;
using AgenticMemory.Http.Middleware;
using AgenticMemory.Http.Models;

namespace AgenticMemory.Http;

/// <summary>
/// Route requests to appropriate handlers
/// </summary>
public class Router
{
    private readonly List<Route> _routes = [];
    private readonly List<IMiddleware> _middlewares = [];

    /// <summary>
    /// Add a route to the router
    /// </summary>
    public Router MapGet(string pattern, IHandler handler)
    {
        _routes.Add(new Route(Models.HttpMethod.GET, pattern, handler));
        return this;
    }

    /// <summary>
    /// Add a POST route to the router
    /// </summary>
    public Router MapPost(string pattern, IHandler handler)
    {
        _routes.Add(new Route(Models.HttpMethod.POST, pattern, handler));
        return this;
    }

    /// <summary>
    /// Add a PUT route to the router
    /// </summary>
    public Router MapPut(string pattern, IHandler handler)
    {
        _routes.Add(new Route(Models.HttpMethod.PUT, pattern, handler));
        return this;
    }

    /// <summary>
    /// Add a DELETE route to the router
    /// </summary>
    public Router MapDelete(string pattern, IHandler handler)
    {
        _routes.Add(new Route(Models.HttpMethod.DELETE, pattern, handler));
        return this;
    }

    /// <summary>
    /// Add a route that handles all methods
    /// </summary>
    public Router MapAll(string pattern, IHandler handler)
    {
        _routes.Add(new Route(null, pattern, handler));
        return this;
    }

    /// <summary>
    /// Add middleware to the pipeline
    /// </summary>
    public Router UseMiddleware(IMiddleware middleware)
    {
        _middlewares.Add(middleware);
        return this;
    }

    /// <summary>
    /// Route request and execute handler
    /// </summary>
    public async Task<Response> RouteAsync(Request request, CancellationToken cancellationToken)
    {
        // Find matching route
        var (route, parameters) = MatchRoute(request.Method, request.Path);

        if (route is null)
        {
            return Response.NotFound($"No route found for {request.Method} {request.Path}");
        }

        // Set path parameters on request
        request.Parameters = parameters;

        // Build and execute middleware pipeline
        Func<Request, Task<Response>> pipeline = req => route.Handler.HandleAsync(req, cancellationToken);

        // Wrap in middleware (reverse order so first middleware is outermost)
        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var next = pipeline;
            pipeline = req => middleware.InvokeAsync(req, next, cancellationToken);
        }

        return await pipeline(request);
    }

    private (Route? route, Dictionary<string, string> parameters) MatchRoute(Models.HttpMethod method, string path)
    {
        foreach (var route in _routes)
        {
            // Check method (null means any method)
            if (route.Method.HasValue && route.Method.Value != method)
                continue;

            // Try to match path
            var parameters = ExtractParameters(route.Pattern, path);
            if (parameters is not null)
            {
                return (route, parameters);
            }
        }

        return (null, []);
    }

    private static Dictionary<string, string>? ExtractParameters(string pattern, string path)
    {
        var parameters = new Dictionary<string, string>();

        // Exact match
        if (!pattern.Contains('{'))
        {
            return pattern.Equals(path, StringComparison.OrdinalIgnoreCase) ? parameters : null;
        }

        // Convert pattern to regex
        // /api/memory/{id} -> ^/api/memory/(?<id>[^/]+)$
        var regexPattern = "^" + Regex.Replace(pattern, @"\{(\w+)\}", m => $"(?<{m.Groups[1].Value}>[^/]+)") + "$";

        var match = Regex.Match(path, regexPattern, RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        // Extract named groups
        foreach (var groupName in match.Groups.Keys)
        {
            if (groupName != "0" && match.Groups[groupName].Success)
            {
                parameters[groupName] = match.Groups[groupName].Value;
            }
        }

        return parameters;
    }
}

/// <summary>
/// Represents a registered route
/// </summary>
public record Route(Models.HttpMethod? Method, string Pattern, IHandler Handler);
