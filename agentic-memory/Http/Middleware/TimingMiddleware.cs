using System.Diagnostics;
using AgenticMemory.Http.Models;

namespace AgenticMemory.Http.Middleware;

/// <summary>
/// Track request duration and add X-Response-Time header
/// </summary>
public class TimingMiddleware : IMiddleware
{
    public async Task<Response> InvokeAsync(Request request, Func<Request, Task<Response>> next, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var response = await next(request);

        stopwatch.Stop();
        response.Headers["X-Response-Time"] = $"{stopwatch.ElapsedMilliseconds}ms";

        return response;
    }
}
