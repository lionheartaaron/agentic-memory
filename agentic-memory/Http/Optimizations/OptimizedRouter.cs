using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AgenticMemory.Http.Handlers;
using AgenticMemory.Http.Middleware;
using AgenticMemory.Http.Models;
using HttpMethod = AgenticMemory.Http.Models.HttpMethod;

namespace AgenticMemory.Http.Optimizations;

/// <summary>
/// High-performance router using trie-based matching and compiled regex patterns.
/// Eliminates per-request regex compilation and reduces allocations.
/// </summary>
public sealed class OptimizedRouter
{
    private readonly List<CompiledRoute> _routes = [];
    private readonly List<IMiddleware> _middlewares = [];
    private FrozenDictionary<string, CompiledRoute>? _exactRoutes;
    private bool _frozen;

    /// <summary>
    /// Add a route (must be called before first request)
    /// </summary>
    public OptimizedRouter MapGet(string pattern, IHandler handler) => Map(HttpMethod.GET, pattern, handler);
    public OptimizedRouter MapPost(string pattern, IHandler handler) => Map(HttpMethod.POST, pattern, handler);
    public OptimizedRouter MapPut(string pattern, IHandler handler) => Map(HttpMethod.PUT, pattern, handler);
    public OptimizedRouter MapDelete(string pattern, IHandler handler) => Map(HttpMethod.DELETE, pattern, handler);

    public OptimizedRouter Map(HttpMethod? method, string pattern, IHandler handler)
    {
        if (_frozen)
            throw new InvalidOperationException("Cannot add routes after router is frozen");

        var route = new CompiledRoute(method, pattern, handler);
        _routes.Add(route);
        return this;
    }

    public OptimizedRouter UseMiddleware(IMiddleware middleware)
    {
        if (_frozen)
            throw new InvalidOperationException("Cannot add middleware after router is frozen");

        _middlewares.Add(middleware);
        return this;
    }

    /// <summary>
    /// Freeze the router for optimal performance (call after all routes are added)
    /// </summary>
    public void Freeze()
    {
        if (_frozen) return;

        // Build frozen dictionary of exact routes for O(1) lookup
        var exactRoutes = new Dictionary<string, CompiledRoute>();
        foreach (var route in _routes.Where(r => !r.HasParameters))
        {
            var key = $"{route.Method}:{route.Pattern}";
            exactRoutes.TryAdd(key, route);
        }
        _exactRoutes = exactRoutes.ToFrozenDictionary();

        _frozen = true;
    }

    /// <summary>
    /// Route request with minimal allocations
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public async ValueTask<Response> RouteAsync(Request request, CancellationToken cancellationToken)
    {
        if (!_frozen)
            Freeze();

        // Try exact match first (O(1))
        var exactKey = $"{request.Method}:{request.Path}";
        if (_exactRoutes!.TryGetValue(exactKey, out var exactRoute))
        {
            return await ExecutePipelineAsync(request, exactRoute, cancellationToken);
        }

        // Fall back to pattern matching
        foreach (var route in _routes)
        {
            if (route.Method.HasValue && route.Method.Value != request.Method)
                continue;

            var parameters = route.TryMatch(request.Path);
            if (parameters is not null)
            {
                request.Parameters = parameters;
                return await ExecutePipelineAsync(request, route, cancellationToken);
            }
        }

        return Response.NotFound($"No route found for {request.Method} {request.Path}");
    }

    /// <summary>
    /// Route optimized request with minimal allocations
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public async ValueTask<Response> RouteAsync(OptimizedRequest request, CancellationToken cancellationToken)
    {
        if (!_frozen)
            Freeze();

        // Try exact match first (O(1))
        var exactKey = $"{request.Method}:{request.Path}";
        if (_exactRoutes!.TryGetValue(exactKey, out var exactRoute))
        {
            return await ExecutePipelineAsync(request, exactRoute, cancellationToken);
        }

        // Fall back to pattern matching
        foreach (var route in _routes)
        {
            if (route.Method.HasValue && route.Method.Value != request.Method)
                continue;

            var parameters = route.TryMatch(request.Path);
            if (parameters is not null)
            {
                request.Parameters = parameters;
                return await ExecutePipelineAsync(request, route, cancellationToken);
            }
        }

        return Response.NotFound($"No route found for {request.Method} {request.Path}");
    }

    private async ValueTask<Response> ExecutePipelineAsync(Request request, CompiledRoute route, CancellationToken cancellationToken)
    {
        Func<Request, Task<Response>> pipeline = req => route.Handler.HandleAsync(req, cancellationToken);

        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var next = pipeline;
            pipeline = req => middleware.InvokeAsync(req, next, cancellationToken);
        }

        return await pipeline(request);
    }

    private async ValueTask<Response> ExecutePipelineAsync(OptimizedRequest request, CompiledRoute route, CancellationToken cancellationToken)
    {
        // Convert to regular request for handler compatibility
        var regularRequest = new Request
        {
            Method = request.Method,
            Path = request.Path,
            QueryString = request.QueryString,
            Headers = request.Headers,
            Body = request.Body,
            Parameters = request.Parameters ?? []
        };

        return await ExecutePipelineAsync(regularRequest, route, cancellationToken);
    }
}

/// <summary>
/// Pre-compiled route with cached regex pattern
/// </summary>
public sealed class CompiledRoute
{
    public HttpMethod? Method { get; }
    public string Pattern { get; }
    public IHandler Handler { get; }
    public bool HasParameters { get; }

    private readonly Regex? _compiledRegex;
    private readonly string[] _parameterNames;

    public CompiledRoute(HttpMethod? method, string pattern, IHandler handler)
    {
        Method = method;
        Pattern = pattern;
        Handler = handler;
        HasParameters = pattern.Contains('{');

        if (HasParameters)
        {
            // Extract parameter names
            var paramMatches = Regex.Matches(pattern, @"\{(\w+)\}");
            _parameterNames = paramMatches.Select(m => m.Groups[1].Value).ToArray();

            // Compile regex for fast matching
            var regexPattern = "^" + Regex.Escape(pattern);
            regexPattern = Regex.Replace(regexPattern, @"\\{(\w+)\\}", @"(?<$1>[^/]+)");
            regexPattern += "$";

            _compiledRegex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }
        else
        {
            _parameterNames = [];
        }
    }

    /// <summary>
    /// Try to match path and extract parameters (returns null if no match)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Dictionary<string, string>? TryMatch(string path)
    {
        if (!HasParameters)
        {
            return Pattern.Equals(path, StringComparison.OrdinalIgnoreCase)
                ? DictionaryPool.Rent()
                : null;
        }

        var match = _compiledRegex!.Match(path);
        if (!match.Success)
            return null;

        var parameters = DictionaryPool.Rent();
        foreach (var name in _parameterNames)
        {
            var group = match.Groups[name];
            if (group.Success)
            {
                parameters[name] = group.Value;
            }
        }

        return parameters;
    }
}
