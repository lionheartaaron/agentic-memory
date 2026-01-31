using System.Diagnostics;
using AgenticMemory.Http.Models;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Http.Middleware;

/// <summary>
/// Log all requests and responses
/// </summary>
public class LoggingMiddleware : IMiddleware
{
    private readonly ILogger<LoggingMiddleware> _logger;

    public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task<Response> InvokeAsync(Request request, Func<Request, Task<Response>> next, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("{Method} {Path}", request.Method, request.Path);

        try
        {
            var response = await next(request);
            stopwatch.Stop();

            _logger.LogInformation("{Method} {Path} -> {StatusCode} in {Duration}ms",
                request.Method, request.Path, response.StatusCode, stopwatch.ElapsedMilliseconds);

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "{Method} {Path} -> Error in {Duration}ms",
                request.Method, request.Path, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
