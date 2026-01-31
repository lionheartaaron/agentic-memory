using AgenticMemory.Http.Models;

namespace AgenticMemory.Http.Handlers;

/// <summary>
/// Base interface for request handlers
/// </summary>
public interface IHandler
{
    /// <summary>
    /// Handle the request and return a response
    /// </summary>
    Task<Response> HandleAsync(Request request, CancellationToken cancellationToken);
}
