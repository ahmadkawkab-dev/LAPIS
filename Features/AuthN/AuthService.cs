namespace Wukna.Features.Auth;

using Wukna.Features.Auth.DTOs;
using Wukna.Features.Users;
using Wukna.Shared.Data.AppDbContext;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public sealed record RegistrationResult(
    AuthResponseDto? Response,
    string? ErrorCode,
    string[] ValidationErrors);

public sealed class AuthService(
    WuknaDbContext db,
    UserManager<User> userManager,
    SignInManager<User> signInManager,
    SessionIssuer sessionIssuer,
    TimeProvider timeProvider)
{
    public const string EmailAlreadyRegistered = "email_already_registered";

    public async Task<RegistrationResult> RegisterAsync(
        RegisterRequestDto request,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();

        // This gives the normal duplicate-registration path a stable response before attempting
        // an insert. The unique normalized-email index remains the final concurrency safeguard.
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return new RegistrationResult(null, EmailAlreadyRegistered, []);
        }

        var user = new User { UserName = email, Email = email };
        user.Username = UsernamePolicy.FromEmail(email, user.Id);
        user.NormalizedUsername = UsernamePolicy.Normalize(user.Username);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        IdentityResult result;
        try
        {
            result = await userManager.CreateAsync(user, request.Password);
        }
        catch (DbUpdateException exception) when (IsEmailUniquenessViolation(exception))
        {
            // A concurrent request inserted the same normalized email after our initial check.
            return new RegistrationResult(null, EmailAlreadyRegistered, []);
        }

        if (!result.Succeeded)
        {
            if (result.Errors.Any(error =>
                    error.Code is "DuplicateEmail" or "DuplicateUserName"))
            {
                return new RegistrationResult(null, EmailAlreadyRegistered, []);
            }

            return new RegistrationResult(
                null,
                null,
                result.Errors.Select(error => error.Description).ToArray());
        }

        var session = await sessionIssuer.IssueAsync(user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        sessionIssuer.SetRefreshCookie(response, session);
        return new RegistrationResult(session.Response, null, []);
    }

    public async Task<AuthResponseDto?> LoginAsync(
        LoginRequestDto request,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            return null;
        }

        var result = await signInManager.CheckPasswordSignInAsync(
            user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedUser = await LockUserAsync(user.Id, cancellationToken);
        if (lockedUser is null)
        {
            return null;
        }

        var session = await sessionIssuer.IssueAsync(lockedUser, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        sessionIssuer.SetRefreshCookie(response, session);
        return session.Response;
    }

    public async Task<AuthResponseDto?> RefreshAsync(
        string? rawToken,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var hash = sessionIssuer.HashRefreshToken(rawToken);
        var existing = await db.RefreshTokens.AsNoTracking()
            .SingleOrDefaultAsync(token => token.TokenHash == hash, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(existing.UserId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var changed = await db.RefreshTokens
            .Where(token => token.Id == existing.Id && !token.IsRevoked && token.ExpiredAt > now)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(token => token.IsRevoked, true),
                cancellationToken);
        if (changed != 1)
        {
            return null;
        }

        var session = await sessionIssuer.IssueAsync(user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        sessionIssuer.SetRefreshCookie(response, session);
        return session.Response;
    }

    public async Task LogoutAsync(string? rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return;
        }

        var hash = sessionIssuer.HashRefreshToken(rawToken);
        await db.RefreshTokens
            .Where(token => token.TokenHash == hash && !token.IsRevoked)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(token => token.IsRevoked, true),
                cancellationToken);
    }

    public async Task<int> LogoutEverywhereAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(userId, cancellationToken);
        if (user is null)
        {
            return 0;
        }

        var changed = await db.RefreshTokens
            .Where(token => token.UserId == userId && !token.IsRevoked)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(token => token.IsRevoked, true),
                cancellationToken);
        await db.BrowserPushSubscriptions.Where(item => item.UserId == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.DisabledAt, timeProvider.GetUtcNow()), cancellationToken);
        await db.NotificationClientPresence.Where(item => item.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private Task<User?> LockUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users
            // The user-row lock serializes refresh rotation with logout-everywhere.
            // Handwritten SQL must use the mapped snake_case table and column names.
            .FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE id = {userId} FOR UPDATE")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

    private static bool IsEmailUniquenessViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "EmailIndex" or "UserNameIndex"
        };
}
