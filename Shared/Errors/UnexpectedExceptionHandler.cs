namespace Wukna.Shared.Errors;

using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

/// <summary>Writes a safe, traceable response for unexpected request failures only.</summary>
public sealed class UnexpectedExceptionHandler(ILogger<UnexpectedExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is BadHttpRequestException || context.Response.HasStarted ||
            context.RequestAborted.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            return false;

        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        logger.LogError(new EventId(1, "UnhandledRequestException"), exception,
            "Unexpected request exception. TraceId: {TraceId}; RequestId: {RequestId}.",
            traceId, context.TraceIdentifier);

        context.Response.Clear();
        context.Response.Headers.CacheControl = "no-store";
        // Keep this writer local to unexpected errors. AddProblemDetails would also
        // augment existing Results.Problem responses with a traceId extension.
        await Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "An unexpected error occurred.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "server_error",
                ["traceId"] = traceId
            }).ExecuteAsync(context);
        return true;
    }
}
