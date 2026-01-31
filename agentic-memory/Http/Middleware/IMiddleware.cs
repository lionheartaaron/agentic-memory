using AgenticMemory.Http.Models;

namespace AgenticMemory.Http.Middleware;

/// <summary>
/// Middleware pipeline pattern interface
/// </summary>
public interface IMiddleware
{
    /// <summary>
    /// Process request and optionally call next middleware
    /// </summary>
    Task<Response> InvokeAsync(Request request, Func<Request, Task<Response>> next, CancellationToken cancellationToken);
}
