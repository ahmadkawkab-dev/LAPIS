# Production release: planning, board chat, and notifications

Publishing and VM operations are performed by the owner. This is a preparation
runbook, not a record of commands already executed. The separate release command
journal records confirmed operations and will be exported to PDF outside the
repository when the workflow is complete.

Local release checks on 2026-10-04: backend Release build passed; integration
suite passed with 201 successes, no failures, and one skipped opt-in live ClamAV
test; all 31 frontend tests and the production build passed. EF reported no pending
model changes. The reviewed SQL upgraded an isolated PostgreSQL 17 database from
the five-migration baseline to all 15 migrations, and replay preserved its schema
and migration history. The existing backend suite also covers legacy notification
data preservation. These checks do not certify production push delivery.

Reviewed release SQL SHA256:

```text
d3f6af5d09bc1da94bbbef5e780963ba61dddefa2ac6568288ee08f9be08820a
```

## Confirmed starting point

On 2026-10-04, the owner confirmed that the VM restarted successfully, the frontend
and PostgreSQL containers were healthy, and the running API was:

```text
ghcr.io/ahmadkawkab-dev/wukna-api:sha-a95c368f6030a17c366284fc7afbb6ee06fb5033
```

The database had five applied migrations, ending at
`20260928153353_TrackConnectionVersions`. The API's existing volumes are
`wukna-prod_data_protection_keys` and `wukna-prod_profile_images`; the database
volume is `wukna-prod_postgres_data`. Retain all three.

## Release artifacts and deployment gate

`ops/migrations/2026-10-04-production-release.sql` covers the ten migrations from
that baseline through `20261004004046_AddNotificationDelivery`. It was generated
with:

```bash
dotnet ef migrations script \
  20260928153353_TrackConnectionVersions \
  20261004004046_AddNotificationDelivery \
  --idempotent --configuration Release --no-build \
  --output ops/migrations/2026-10-04-production-release.sql
```

The reviewed script adds the SQL statement terminator after the legacy
`INSERT ... SELECT` copy. EF's generated idempotent wrapper omitted that semicolon
because the original migration's raw SQL has no terminator. Direct EF migration
tests pass, but PostgreSQL rejects the uncorrected `DO` block. Preserve this
reviewed correction when regenerating the script; the migration files remain
unchanged.

Review the migration history before applying it. This script expects the existing
baseline tables and history; it is not an empty-database installer. Each migration
has its own transaction and history guard. With `ON_ERROR_STOP=1`, an error stops
execution; earlier committed migrations remain applied. Investigate the error
and history before any retry. Do not manually insert migration history rows.

The notification migration copies legacy reminder IDs and read/dismissed state
before dropping `task_notifications`. The reminder migration initializes schedule
generation IDs. Changing the task-list relationship to cascade deletion changes
future deletion behavior. These are intentional existing migrations; no migration
was rewritten for this release.

CI tests and publishes ARM64 images after a successful push to `main`. Automatic
production deployment requires `PROD_AUTO_DEPLOY=true` in the GitHub production
environment. Set it to `false` while preparing this release. The deployment
workflow rejects changed migration files by comparing the running image's commit
with the target commit. Applying SQL does not remove that comparison. Use the
reviewed manual VM procedure below; leave the gate intact.

## Before maintenance

1. Finish local verification and review the release diff. Include the CI
   `dotnet test --project` correction, this runbook, and the SQL in the release.
2. The owner merges the reviewed changes into `main`, waits for CI and both image
   publishes to pass, and records the full resulting 40-character commit SHA.
   Use that SHA for both images and every transferred release artifact.
3. Transfer the committed `compose.production.yaml`,
   `compose.attachments.production.yaml`, `ops/production-deploy`,
   `ops/verify-production-env.py`, and release SQL to a VM staging directory.
   Compare SHA256 checksums with the local copies from that same commit. Do not
   transfer local development secret files.
4. Record the current image tags, volume names, and migration history. Back up
   `/etc/wukna/production.env`, the installed Compose files/helper, and any
   attachment marker into a new root-owned mode-0700 backup directory before
   changing them. Preserve their original ownership and permissions.
5. Pull both new image tags before starting the maintenance window. Confirm the
   scanner image is available and that the VM can reserve its configured 4 GiB
   limit if attachments will be enabled.

The current installed helper predates the opt-in attachment override. Install the
reviewed helper as root:root mode 0755 at `/usr/local/sbin/wukna-deploy` and both
reviewed Compose files as root:root mode 0644 under
`/home/ubuntu/wukna-deploy/`. Keep `/etc/wukna` root-owned mode 0700 and its
`production.env` root-owned mode 0600. Verify the base protected environment using
`ops/verify-production-env.py` before changing notification settings; it compares
the live credentials without displaying them.

## Production notification configuration

In-app notifications and open-page sounds do not require an email service.
Browser push needs a separate stable production VAPID pair and a contact subject.
Do not reuse the development pair. After pulling the new API image, generate the
pair once with the approved release SHA:

```bash
sudo docker run --rm --user 0 --network none \
  -v /etc/wukna:/keys \
  ghcr.io/ahmadkawkab-dev/wukna-api:sha-RELEASE_SHA \
  --generate-vapid-keys /keys/web-push.env
```

`RELEASE_SHA` is a placeholder to replace, not a runnable image tag. The command
only writes the pair to a mode-0600 file; it does not start the API or print keys.
It refuses to overwrite an existing key file. If production keys already exist,
retain and validate them rather than generating replacements.

Configure these entries once in the protected `production.env`:

```text
WEB_PUSH_ENABLED=true
WEB_PUSH_PUBLIC_KEY=<from the protected production key file>
WEB_PUSH_PRIVATE_KEY=<from the protected production key file>
WEB_PUSH_SUBJECT=mailto:<owner-approved contact email>
```

Transfer keys directly between protected files or a private editor. Never paste
them into chat, command arguments, the PDF, or Git. Preserve the existing database,
JWT, Google OAuth, and public-origin settings. Invalid enabled VAPID configuration
fails API startup. Outbound HTTPS to supported browser push providers is needed.

If enabling the already verified OCI attachment integration, configure
`OCI_CHAT_REGION=me-riyadh-1`, `OCI_CHAT_NAMESPACE=axjttjuno68o`,
`OCI_CHAT_BUCKET=wukna-chat-attachments`, and `OCI_CHAT_PREFIX=wukna-chat` in the
same protected environment. Enable the override using a root-owned mode-0600
`/etc/wukna/attachments.enabled` file containing exactly `enabled`. The helper
then starts the private scanner and waits for it to become healthy. These are
attachment settings; notifications require no new Oracle bucket permission.

Validate the installed configuration without printing its rendered secrets:

```bash
sudo /usr/local/sbin/wukna-deploy check RELEASE_SHA
```

## Maintenance backup and restore rehearsal

Plan a short maintenance window. Stop the frontend and API so no application
writes occur between the final backup and migration; leave PostgreSQL running:

```bash
sudo docker stop wukna-prod-frontend-1 wukna-prod-api-1
```

Use a new timestamped root-owned backup directory and mode-0600 files. Run
`pg_dump -Fc` inside `wukna-prod-db-1` as `wukna` for the `wukna` database, writing
the binary output to a temporary file with a root shell's redirection. Require
success and nonempty output before renaming it to `database.dump`. Record its
SHA256 checksum. Do not use a TTY for a binary dump.

Also archive the existing Data Protection keys and profile images from the stopped
API's read-only volumes. Keep configuration backups alongside the dump. A database
backup alone does not preserve the key ring used to decrypt credentials.

Restore the dump into a disposable PostgreSQL 17 container with a distinct name,
no published ports, no production volumes, and `--network none`. Give it a
temporary data directory, wait until SQL queries succeed, and restore with
`pg_restore --exit-on-error --no-owner --no-privileges`. Compare the restored
migration history and selected row counts with the stopped production baseline.
Remove only that disposable container after successful verification. Copy the
backup to a protected off-VM destination before migration.

If backup or restore validation fails, keep the old release and resolve the
failure before applying SQL. Restarting the existing stopped containers is the
pre-migration recovery path.

## Apply SQL and start the release

Only after the owner confirms the backup, restore rehearsal, release checksums,
and production configuration, run the staged reviewed script:

```bash
sudo docker exec -i wukna-prod-db-1 \
  psql -X -U wukna -d wukna -v ON_ERROR_STOP=1 \
  < /home/ubuntu/wukna-release/2026-10-04-production-release.sql
```

The path assumes the agreed staging directory. Verify the complete ordered
history contains all 15 expected migrations and ends at `AddNotificationDelivery`:

```bash
sudo docker exec wukna-prod-db-1 psql -X -U wukna -d wukna -At \
  -c 'SELECT migration_id FROM "__EFMigrationsHistory" ORDER BY migration_id;'
```

Then deploy the exact published SHA with the installed reviewed helper:

```bash
sudo /usr/local/sbin/wukna-deploy deploy RELEASE_SHA
```

The helper does not migrate or back up the database. It starts only the release
services and optional scanner, retains the existing volumes, verifies image tags,
and checks API reachability from the frontend. A successful helper is a basic
health check; it does not prove all user flows or browser delivery.

## Release validation and recovery

Confirm both image tags, container health, external HTTPS access, login, existing
boards, profile images, and saved tasks. Verify scheduled chat task import remains
idempotent, ICS downloads work, and persisted notifications can be read/dismissed.

Enroll a production browser and enable its delivery preference. Test a background
chat message, mention/reply, reminder, invitation, completion, and sound muting.
Use two accounts where needed. A visible active chat suppresses its notification;
test with the receiving chat in the background. Custom clips require an open,
audio-enabled page. Closed/suspended-page sound follows browser/OS settings.

Keep automatic deployment disabled until validation and the owner’s release
decision are complete. Record every confirmed command and result in the external
journal; export the final PDF after the workflow is finished.

After SQL has run, reverting images alone is not a complete rollback. If recovery
requires restoring the old database, stop writers, retain the failed database and
logs for diagnosis, and agree on the restore procedure before replacing production
data. Restore the verified pre-release database, matching configuration/images,
and preserved key ring. Changes made after the backup would be lost in a restore.
Do not run automatic downgrade migrations or delete production volumes.
