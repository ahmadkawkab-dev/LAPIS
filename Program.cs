using Microsoft.EntityFrameworkCore;
using Wukna.Features.Auth;
using Wukna.Features.Board;
using Wukna.Features.Notes;
using Wukna.Features.NoteConnection;
using Wukna.Features.Realtime;
using Wukna.Features.Users;
using Wukna.Features.Profile;
using Wukna.Features.Tasks;
using Wukna.Features.Calendar;
using Wukna.Features.Notifications;
using Wukna.Shared.Data.AppDbContext;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Wukna.Shared.Health;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadinessHealthCheck>("database");
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = 5 * 1024 * 1024 + 64 * 1024);

// Keep runtime mapping aligned with the design-time factory before creating migrations.
builder.Services.AddDbContext<WuknaDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
           .UseSnakeCaseNamingConvention());

var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
// WebApplicationBuilder loads User Secrets automatically in Development.
// Validate each setting separately so configuration errors identify the exact key to fix.
ValidateJwtOptions(jwtOptions);
var googleSettings = builder.Configuration
    .GetSection(GoogleOAuthSettings.SectionName)
    .Get<GoogleOAuthSettings>() ?? new GoogleOAuthSettings();
ValidateGoogleOAuthSettings(googleSettings, builder.Environment);

builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton(googleSettings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BoardActivity>();
builder.Services.AddScoped<BoardSummaryReader>();
builder.Services.AddSingleton<IProfileImageStore, FileProfileImageStore>();
builder.Services.AddHostedService<ProfileImageCleanupService>();
builder.Services.AddScoped<BoardAccess>();
builder.Services.AddScoped<BoardRealtimeDispatcher>();
builder.Services.AddSingleton<IBoardConnectionRegistry, BoardConnectionRegistry>();
builder.Services.AddSingleton<INoteGeometryPreviewRegistry, NoteGeometryPreviewRegistry>();
builder.Services.AddSingleton<INoteEditingRegistry, NoteEditingRegistry>();
builder.Services.AddSingleton<IBoardCursorRegistry, BoardCursorRegistry>();
builder.Services.AddSingleton<IBoardRealtimePublisher, BoardRealtimePublisher>();
builder.Services.AddHostedService<NoteEditingCleanupService>();
builder.Services.AddHostedService<BoardCursorCleanupService>();
builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
builder.Services.AddSignalR(options =>
    options.EnableDetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddSingleton<JwtTokenGenerator>();
builder.Services.AddScoped<SessionIssuer>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<GoogleLoginService>();
builder.Services.AddScoped<ExternalLoginGrantService>();
builder.Services.AddScoped<GoogleLinkIntentService>();
builder.Services.AddScoped<GoogleAccountLinkService>();
builder.Services.AddHostedService<ExternalLoginGrantCleanupService>();
builder.Services.AddHostedService<TaskReminderWorker>();
// Keep the pre-rename cryptographic application name so existing protected auth payloads
// remain readable across deployment of the Wukna identifiers.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Lapis");
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (builder.Environment.IsProduction() || !string.IsNullOrWhiteSpace(keysPath))
{
    keysPath = string.IsNullOrWhiteSpace(keysPath) ? "/app/App_Data/data-protection-keys" : keysPath;
    if (!Path.IsPathFullyQualified(keysPath))
        throw new InvalidOperationException("DataProtection:KeysPath must be an absolute directory path.");
    var keysDirectory = Directory.CreateDirectory(keysPath);
    // Only the application account can traverse the key ring. Docker mounts this directory
    // from a persistent volume; changing its application discriminator would invalidate old payloads.
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(keysDirectory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    dataProtection.PersistKeysToFileSystem(keysDirectory);
}

builder.Services.AddIdentityCore<User>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<WuknaDbContext>()
    .AddSignInManager();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // Browser WebSocket/EventSource APIs cannot attach the Authorization header.
                // Accept SignalR's query-token fallback on the one hub path only.
                if (context.HttpContext.Request.Path.StartsWithSegments(BoardHub.Path))
                {
                    var accessToken = context.Request.Query["access_token"].ToString();
                    if (!string.IsNullOrWhiteSpace(accessToken)) context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    })
    .AddCookie(IdentityConstants.ExternalScheme, options =>
    {
        // Keep the established cookie name so an in-flight Google callback survives rollout.
        // This cookie carries Google's validated principal to Wukna's internal callback.
        options.Cookie.Name = "lapis.external";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.None
            : CookieSecurePolicy.Always;
        options.Cookie.Path = "/api/auth";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        options.SlidingExpiration = false;
    })
    .AddGoogle(GoogleOAuthSettings.AuthenticationScheme, options =>
    {
        options.ClientId = googleSettings.ClientId;
        options.ClientSecret = googleSettings.ClientSecret;
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.CallbackPath = GoogleOAuthSettings.ProviderCallbackPath;
        options.UsePkce = true;
        options.SaveTokens = false;

        // Google's user-info formats have used both names. Mapping both lets the callback
        // require verified email without relying on an unverified address.
        options.ClaimActions.MapJsonKey(
            GoogleOAuthSettings.EmailVerifiedClaim,
            "verified_email",
            ClaimValueTypes.Boolean);
        options.ClaimActions.MapJsonKey(
            GoogleOAuthSettings.EmailVerifiedClaim,
            "email_verified",
            ClaimValueTypes.Boolean);

        // Provider cancellation and malformed/failed remote callbacks return only stable public
        // error codes to the fixed frontend callback. Internal exception details are not exposed.
        options.Events.OnAccessDenied = context =>
        {
            context.HandleResponse();
            context.Response.Redirect(
                googleSettings.BuildFrontendErrorRedirect("oauth_cancelled"));
            return Task.CompletedTask;
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.Redirect(
                googleSettings.BuildFrontendErrorRedirect(
                    GoogleLoginService.ExternalLoginFailed));
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "wukna.csrf";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.None
        : CookieSecurePolicy.Always;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;

    if (builder.Environment.IsProduction())
    {
        if (!IPAddress.TryParse(builder.Configuration["ReverseProxy:KnownProxyIp"], out var proxyIp))
            throw new InvalidOperationException(
                "ReverseProxy:KnownProxyIp must be a valid IP address in Production.");

        options.KnownProxies.Add(proxyIp);
        options.KnownProxies.Add(proxyIp.MapToIPv6());
    }
    else
    {
        options.KnownProxies.Add(IPAddress.Parse("172.20.0.4"));
    }
});

var app = builder.Build();

   
app.UseForwardedHeaders();
app.UseSwagger();
app.UseSwaggerUI();


app.UseStaticFiles();
app.UseStaticFiles(ProfileImageStaticFiles.Options(app.Services.GetRequiredService<IProfileImageStore>()));
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapAuthEndpoints();
app.MapBoardEndpoints();
app.MapNoteEndpoints();
app.MapNoteConnectionEndpoints();
app.MapProfileEndpoints();
app.MapPersonalTaskEndpoints();
app.MapTaskPlanningEndpoints();
app.MapTaskTemplateEndpoints();
app.MapCalendarEventEndpoints();
app.MapCalendarRangeEndpoints();
app.MapTaskReminderEndpoints();
app.MapHub<BoardHub>(BoardHub.Path, options =>
    options.CloseOnAuthenticationExpiration = true).RequireAuthorization();

app.Run();

static void ValidateJwtOptions(JwtOptions options)
{
    if (string.IsNullOrWhiteSpace(options.Issuer))
        throw new InvalidOperationException("Jwt:Issuer is required.");
    if (string.IsNullOrWhiteSpace(options.Audience))
        throw new InvalidOperationException("Jwt:Audience is required.");
    if (string.IsNullOrWhiteSpace(options.SigningKey))
        throw new InvalidOperationException("Jwt:SigningKey is required.");
    if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
        throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 bytes.");
    if (options.AccessTokenMinutes is < 1 or > 60)
        throw new InvalidOperationException("Jwt:AccessTokenMinutes must be between 1 and 60.");
    if (options.RefreshTokenDays is < 1 or > 365)
        throw new InvalidOperationException("Jwt:RefreshTokenDays must be between 1 and 365.");
}

static void ValidateGoogleOAuthSettings(
    GoogleOAuthSettings settings,
    IHostEnvironment environment)
{
    if (string.IsNullOrWhiteSpace(settings.ClientId))
        throw new InvalidOperationException("Authentication:Google:ClientId is required.");
    if (string.IsNullOrWhiteSpace(settings.ClientSecret))
        throw new InvalidOperationException("Authentication:Google:ClientSecret is required.");
    if (!Uri.TryCreate(settings.FrontendBaseUrl, UriKind.Absolute, out var frontendUri) ||
        frontendUri.UserInfo.Length > 0 ||
        frontendUri.AbsolutePath != "/" ||
        frontendUri.Query.Length > 0 ||
        frontendUri.Fragment.Length > 0 ||
        (frontendUri.Scheme != Uri.UriSchemeHttps &&
         !(environment.IsDevelopment() &&
           frontendUri.Scheme == Uri.UriSchemeHttp &&
           frontendUri.IsLoopback)))
    {
        throw new InvalidOperationException(
            "Authentication:Google:FrontendBaseUrl must be an HTTPS origin, " +
            "or an HTTP loopback origin in Development.");
    }
}

public partial class Program;
