namespace Wukna.Features.Auth;

using System.IdentityModel.Tokens.Jwt;
using Wukna.Features.Auth.DTOs;
using Wukna.Features.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        group.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { token = tokens.RequestToken });
        });

        group.MapGet("/account", async (HttpContext context, UserManager<User> userManager) =>
        {
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(subject, out var userId)) return Results.Unauthorized();

            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null) return Results.Unauthorized();

            var logins = await userManager.GetLoginsAsync(user);
            return Results.Ok(new
            {
                user.Id,
                user.Email,
                HasPassword = await userManager.HasPasswordAsync(user),
                ExternalLogins = logins.Select(login => login.LoginProvider).Distinct().ToArray()
            });
        }).RequireAuthorization();

        group.MapGet("/external/google", (SignInManager<User> signInManager) =>
        {
            // Identity records the provider and protects the redirect through OAuth state and
            // correlation cookies. The destination is fixed rather than supplied by the browser.
            var properties = signInManager.ConfigureExternalAuthenticationProperties(
                GoogleOAuthSettings.AuthenticationScheme,
                GoogleOAuthSettings.ApplicationCallbackPath);
            return Results.Challenge(
                properties,
                [GoogleOAuthSettings.AuthenticationScheme]);
        });

        group.MapGet("/external/google/callback", async (
            HttpContext context,
            SignInManager<User> signInManager,
            GoogleLoginService googleLoginService,
            ExternalLoginGrantService grantService,
            GoogleOAuthSettings googleSettings,
            ILogger<GoogleLoginService> logger,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var loginInfo = await signInManager.GetExternalLoginInfoAsync();
                if (loginInfo is null)
                {
                    logger.LogWarning("Google callback did not contain an Identity external login ticket.");
                    return Results.Redirect(
                        googleSettings.BuildFrontendErrorRedirect(
                            GoogleLoginService.ExternalLoginFailed));
                }

                var resolution = await googleLoginService.ResolveUserAsync(
                    loginInfo,
                    cancellationToken);
                if (!resolution.Succeeded)
                {
                    logger.LogWarning(
                        "Google identity resolution failed with code {ErrorCode}.",
                        resolution.ErrorCode);
                    return Results.Redirect(
                        googleSettings.BuildFrontendErrorRedirect(
                            resolution.ErrorCode ?? GoogleLoginService.ExternalLoginFailed));
                }

                var code = await grantService.CreateAsync(
                    resolution.User!,
                    context.Response,
                    cancellationToken);
                return Results.Redirect(googleSettings.BuildFrontendSuccessRedirect(code));
            }
            finally
            {
                // The external principal has served its only purpose. It is not a WUKNA session.
                await context.SignOutAsync(IdentityConstants.ExternalScheme);
            }
        });

        group.MapPost("/external/google/link-intent", async (
            HttpContext context,
            GoogleLinkIntentService linkIntentService) =>
        {
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(subject, out var userId) ||
                !await linkIntentService.CreateAsync(userId, context.Response))
                return Results.Unauthorized();

            return Results.NoContent();
        }).RequireAuthorization();

        group.MapGet("/external/google/link", async (
            HttpContext context,
            GoogleLinkIntentService linkIntentService,
            SignInManager<User> signInManager,
            GoogleOAuthSettings googleSettings) =>
        {
            var user = await linkIntentService.ValidateAsync(context.Request);
            if (user is null)
            {
                linkIntentService.Delete(context.Response);
                return Results.Redirect(googleSettings.BuildFrontendErrorRedirect(
                    GoogleLoginService.ExternalLoginFailed));
            }

            // Identity binds the temporary external ticket to the user in addition to our
            // protected link-intent cookie. Both must name the same user at callback time.
            var properties = signInManager.ConfigureExternalAuthenticationProperties(
                GoogleOAuthSettings.AuthenticationScheme,
                GoogleOAuthSettings.LinkCallbackPath,
                user.Id.ToString());
            return Results.Challenge(properties, [GoogleOAuthSettings.AuthenticationScheme]);
        });

        group.MapGet("/external/google/link/callback", async (
            HttpContext context,
            GoogleLinkIntentService linkIntentService,
            GoogleAccountLinkService accountLinkService,
            SignInManager<User> signInManager,
            GoogleOAuthSettings googleSettings) =>
        {
            try
            {
                var user = await linkIntentService.ValidateAsync(context.Request);
                if (user is null)
                    return Results.Redirect(googleSettings.BuildFrontendErrorRedirect(
                        GoogleLoginService.ExternalLoginFailed));

                var loginInfo = await signInManager.GetExternalLoginInfoAsync(
                    expectedXsrf: user.Id.ToString());
                if (loginInfo is null)
                    return Results.Redirect(googleSettings.BuildFrontendErrorRedirect(
                        GoogleLoginService.ExternalLoginFailed));

                var result = await accountLinkService.LinkAsync(user, loginInfo);
                return Results.Redirect(result.Succeeded
                    ? googleSettings.BuildFrontendLinkedRedirect()
                    : googleSettings.BuildFrontendErrorRedirect(
                        result.ErrorCode ?? GoogleLoginService.ExternalLoginFailed));
            }
            finally
            {
                linkIntentService.Delete(context.Response);
                await context.SignOutAsync(IdentityConstants.ExternalScheme);
            }
        });

        group.MapDelete("/external/google/link", async (
            HttpContext context,
            GoogleAccountLinkService accountLinkService,
            CancellationToken cancellationToken) =>
        {
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(subject, out var userId))
                return Results.Unauthorized();

            var result = await accountLinkService.UnlinkAsync(userId, cancellationToken);
            if (result.Succeeded)
                return Results.NoContent();

            var statusCode = result.ErrorCode == GoogleAccountLinkService.CannotRemoveOnlyLogin
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return Results.Json(new { error = result.ErrorCode }, statusCode: statusCode);
        }).RequireAuthorization();

        group.MapPost("/external/exchange", async (
            ExternalExchangeRequestDto request,
            HttpContext context,
            ExternalLoginGrantService grantService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await grantService.ExchangeAsync(
                    request.Code,
                    context.Request.Cookies[
                        ExternalLoginGrantService.BrowserBindingCookieName],
                    context.Response,
                    cancellationToken);

                if (result.Response is not null)
                {
                    return Results.Ok(result.Response);
                }

                var statusCode = result.ErrorCode == ExternalLoginGrantService.CodeExpired
                    ? StatusCodes.Status410Gone
                    : StatusCodes.Status400BadRequest;
                return Results.Json(
                    new { error = result.ErrorCode },
                    statusCode: statusCode);
            }
            finally
            {
                // A failed or completed exchange must restart from Google with a fresh binding.
                grantService.DeleteBrowserBindingCookie(context.Response);
            }
        });

        group.MapPost("/register", async (
            RegisterRequestDto request,
            HttpContext context,
            IAntiforgery antiforgery,
            AuthService service,
            CancellationToken cancellationToken) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest(new { code = "invalid_csrf", error = "Invalid CSRF token." });
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest(new { error = "Email and password are required." });

            var result = await service.RegisterAsync(
                request, context.Response, cancellationToken);
            if (result.ErrorCode == AuthService.EmailAlreadyRegistered)
                return Results.Conflict(new { error = result.ErrorCode });
            if (result.Response is null)
                return Results.BadRequest(new { errors = result.ValidationErrors });

            return Results.Ok(result.Response);
        });

        group.MapPost("/login", async (
            LoginRequestDto request,
            HttpContext context,
            IAntiforgery antiforgery,
            AuthService service,
            CancellationToken cancellationToken) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest(new { code = "invalid_csrf", error = "Invalid CSRF token." });
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                return Results.Unauthorized();

            var response = await service.LoginAsync(request, context.Response, cancellationToken);
            if (response is null)
                return Results.Unauthorized();

            return Results.Ok(response);
        });

        group.MapPost("/refresh", async (
            HttpContext context,
            IAntiforgery antiforgery,
            AuthService service,
            SessionIssuer sessionIssuer,
            CancellationToken cancellationToken) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest(new { code = "invalid_csrf", error = "Invalid CSRF token." });

            var response = await service.RefreshAsync(
                SessionIssuer.ReadRefreshCookie(context.Request),
                context.Response,
                cancellationToken);
            if (response is null)
            {
                sessionIssuer.DeleteRefreshCookie(context.Response);
                return Results.Unauthorized();
            }

            return Results.Ok(response);
        });

        group.MapPost("/logout", async (
            HttpContext context,
            IAntiforgery antiforgery,
            AuthService service,
            SessionIssuer sessionIssuer,
            CancellationToken cancellationToken) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest(new { code = "invalid_csrf", error = "Invalid CSRF token." });

            await service.LogoutAsync(
                SessionIssuer.ReadRefreshCookie(context.Request), cancellationToken);
            sessionIssuer.DeleteRefreshCookie(context.Response);
            return Results.NoContent();
        });

        group.MapPost("/logout-everywhere", async (
            HttpContext context,
            AuthService service,
            SessionIssuer sessionIssuer,
            CancellationToken cancellationToken) =>
        {
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(subject, out var userId))
                return Results.Unauthorized();

            await service.LogoutEverywhereAsync(userId, cancellationToken);
            sessionIssuer.DeleteRefreshCookie(context.Response);
            return Results.NoContent();
        }).RequireAuthorization();

        return endpoints;
    }
}
