using AgenticMemory.Http.Models;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Http.Middleware;

/// <summary>
/// Global exception handling middleware
/// </summary>
public class ErrorHandlingMiddleware : IMiddleware
{
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(ILogger<ErrorHandlingMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task<Response> InvokeAsync(Request request, Func<Request, Task<Response>> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next(request);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Request cancelled: {Method} {Path}", request.Method, request.Path);
            return Response.InternalServerError("Request cancelled");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Bad request: {Method} {Path}", request.Method, request.Path);
            return Response.BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception: {Method} {Path}", request.Method, request.Path);
            return Response.InternalServerError("An internal error occurred");
        }
    }
}
