namespace Wukna.Shared.Errors;

using Microsoft.AspNetCore.Diagnostics;

/// <summary>Dispatches unexpected exceptions while preserving the host's other failure responses.</summary>
public sealed class UnexpectedExceptionMiddleware(RequestDelegate next, IEnumerable<IExceptionHandler> handlers)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (exception is not BadHttpRequestException &&
            !context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            // UseExceptionHandler also adds cache headers to thrown bad requests.
            // Leave those exceptions, disconnects and started responses with Kestrel.
            foreach (var handler in handlers)
                if (await handler.TryHandleAsync(context, exception, context.RequestAborted))
                    return;
            throw;
        }
    }
}
