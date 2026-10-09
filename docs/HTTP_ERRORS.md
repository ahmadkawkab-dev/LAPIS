# HTTP error responses

Unexpected exceptions before a response starts return HTTP 500 with
`Content-Type: application/problem+json` and `Cache-Control: no-store`:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
  "title": "An unexpected error occurred.",
  "status": 500,
  "code": "server_error",
  "traceId": "0123456789abcdef0123456789abcdef"
}
```

`traceId` is the current activity's trace ID, or the ASP.NET Core request identifier
when no activity exists. The `Wukna.Shared.Errors.UnexpectedExceptionHandler` logger
records the original exception, trace ID and request identifier with event ID 1
(`UnhandledRequestException`). Exception messages, stack traces and request data
are not included in the response. The error response is JSON even when the request
has an `Accept: text/html` header.

Existing expected errors keep their own contracts: `{ error }`, `{ code }`, mixed
objects, Identity validation arrays, existing profile ProblemDetails, and empty
authorization or malformed-request responses. The `server_error` code matches the
frontend's existing fallback for HTTP 500; no frontend change is required.

The handler is registered as `IExceptionHandler` and invoked by
`UnexpectedExceptionMiddleware` before the existing middleware pipeline. The adapter
only catches exceptions before a response starts and while the request is active.
`BadHttpRequestException`, client cancellation and exceptions after the response
starts pass through to the host, preserving Kestrel's existing behavior. A dependency
cancellation while the HTTP request remains active is an unexpected failure and
returns the same safe HTTP 500 response.

This deliberately avoids global `AddProblemDetails` registration and
`UseExceptionHandler`: the default ProblemDetails writer adds trace fields to existing
`Results.Problem` responses, and exception middleware applies cache headers to thrown
bad requests. See the ASP.NET Core 10.0.12 sources for
[the default writer](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Http/Http.Extensions/src/DefaultProblemDetailsWriter.cs)
and [exception middleware](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Middleware/Diagnostics/src/ExceptionHandler/ExceptionHandlerMiddlewareImpl.cs).

`ProductionHttpContractTests` exercises these boundaries against the real application
in Production mode over Kestrel HTTP/1.1. Failure injection is confined to the test
assembly and uses the existing profile endpoint; no failure endpoints are exposed.
