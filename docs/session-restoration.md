# Session restoration and Data Protection

Wukna keeps the short-lived access token and CSRF request token in memory. The persistent,
HttpOnly `wukna.refresh` cookie preserves login across browser restarts. Its raw value is
hashed in PostgreSQL and rotates after each successful refresh.

At startup the shared auth client obtains uncached `/api/auth/csrf` credentials, then
refreshes when it has no valid access token. Protected components and realtime start only
after bootstrap succeeds. A known antiforgery rejection is retried once with fresh CSRF;
network failures and ordinary validation/permission errors are not retried as mutations.
Temporary restoration failures retain the route and show a retry action. A refresh 401
means the refresh credential is absent, expired, revoked, or invalid and requires login.

## Persistent key ring

Both Docker configurations mount `/app/App_Data/data-protection-keys` outside the disposable
API container and set `DataProtection__KeysPath` to that directory. Production retains the
existing `wukna-prod_data_protection_keys` volume name. The application runs as `app` and
restricts directory access to its owner (0700). This uses filesystem access control; it
does not add certificate encryption at rest. Protect the Docker host and volume backups.

Keep the Data Protection application discriminator **Lapis**, which predates the product
rename. Changing it, deleting old keys, or deleting the volume prevents decryption of
existing antiforgery and OAuth-related protected payloads. Refresh tokens themselves use
database hashes rather than Data Protection. Keep the configured JWT signing key stable
across releases as well.

The deployment helper verifies the running API's key-volume mount before replacing it and
checks the replacement's mount and access. Container replacement uses Compose `up` and
preserves named volumes. Do not remove the key volume during routine deployment.
Install the updated production Compose file and `ops/production-deploy` helper through the
existing reviewed host configuration procedure; publishing images does not update those
host files automatically.

## Regression checks

- `cd frontend && npm test`: startup ordering, stale CSRF recovery, concurrency and retry budgets.
- `dotnet test --project tests/Wukna.IntegrationTests/Wukna.IntegrationTests.csproj`:
  real antiforgery validation, refresh rotation/revocation, profile access and host restart
  with a persisted key ring. Restart tests use isolated PostgreSQL and temporary key directories.

Microsoft documents external key persistence for Docker in its [Data Protection guidance](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/default-settings?view=aspnetcore-10.0), and the dependency of antiforgery credentials on Data Protection in its [antiforgery guidance](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0).
