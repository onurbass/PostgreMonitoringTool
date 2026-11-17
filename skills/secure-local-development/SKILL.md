---
name: secure-local-development
description: Security rules for the local Windows monitor - Windows Credential Manager for PostgreSQL and SMTP secrets, session-only credentials, log and exception redaction, TLS, enforced read-only database access, SQL-text sensitivity, egress allowlist, supply-chain hygiene and working rules for AI agents. Use when handling any credential, connection setting, logging, exception message, outbound connection, dependency or when reviewing security-sensitive changes.
compatibility: Windows 10/11 per-user Credential Manager (advapi32 CredRead/CredWrite), .NET 10, Npgsql, MailKit, Microsoft.Data.Sqlite. Credential blob limit is 2560 bytes (about 1280 UTF-16 characters).
---

# secure-local-development

Owns: **secrets, redaction, read-only enforcement, data egress and dependency hygiene.** It constrains the other skills and never relaxes a project constraint. Credential Manager code: `references/credential-manager.md`.

## Non-negotiables

1. The tool uses the user's **existing** PostgreSQL account. Never suggest creating a monitoring role, granting `pg_monitor`/`pg_read_all_stats`, or changing permissions. If data is not visible, report limited visibility (postgres-diagnostics).
2. Credentials never appear in: source, `settings.json`, YAML/.env, SQLite, logs, exception text, UI labels, crash dumps, test fixtures, commit history, or prompts to any AI tool.
3. **No database information leaves the machine** except the configured SMTP incident emails to configured recipients. No telemetry, analytics, crash-reporting, update checks, or web calls.
4. Monitoring is read-only, enforced in depth (below), not by convention.

## Secrets: Windows Credential Manager

- Generic credentials, per-user, `CRED_PERSIST_LOCAL_MACHINE` (survives reboot, does not roam). Targets: `PgMonitor/Postgres/{host}:{port}/{user}` and `PgMonitor/Smtp/{host}:{port}/{user}`.
- Store **only the password** (and username in the credential's `UserName` field). Non-secret host/port/db stay in `settings.json`.
- UI actions required: Save, Use for this session only, Remove saved credential, Test connection. Password boxes use `PasswordBox` (no binding to string properties long-term; read at the moment of use).
- **Session-only mode**: hold the secret in a small `SessionSecret` object in memory, never persisted, cleared on disconnect/exit. Be honest in docs: .NET strings cannot be reliably zeroed; minimize lifetime and copies, avoid putting the password in long-lived strings, collections, view-models or exceptions. Prefer `char[]`/`byte[]` that you clear, and convert to string only inside the data-source builder.
- Retrieve at use (connect/send), not at startup into a global. Never cache in static fields.
- Do **not** use `dotnet user-secrets`, environment variables, or `appsettings.*.json` for DB/SMTP passwords, even in development (they are plaintext). Dev uses the same Credential Manager flow.
- If the OS prompts or read fails, surface "credential unavailable" without detail; never fall back to plaintext.

## Redaction

- Wrap logging with a `Redactor` applied to every message template argument that may carry connection data. Never log `NpgsqlConnectionStringBuilder`, `ConnectionString`, `NpgsqlDataSource` or `MailKit` client objects with `ToString()` (builders print the password).
- Keep `Persist Security Info` off (default) and `Include Error Detail=false`.
- Exceptions: log type, `SqlState`, and a sanitized message; do not forward `PostgresException.Detail/Where/Hint/InternalQuery`, and never show raw exceptions in the UI or emails. Show correlation id + "see local log".
- Add a unit test that runs failure paths with a canary password (`"CANARY-pw-123"`) and asserts it appears in no log sink, exception message, outbox row or SQLite file (see dotnet-testing-and-code-review).
- Serilog file sink: size-capped, rolling, retained-count limited; logs under `%LOCALAPPDATA%\PgMonitor\logs` with user-only ACL.

## Enforced read-only (defense in depth)

1. Session options: `default_transaction_read_only=on` (see dotnet-windows-development) and verify with `SHOW` at connect.
2. All SQL lives in one `SqlCatalog` of `const string` statements; each must begin with `SELECT`/`SHOW`/`WITH ... SELECT`. A unit test scans the catalog and rejects `INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|TRUNCATE|GRANT|pg_terminate_backend|pg_cancel_backend|pg_reload_conf|set_config|COPY|DO` tokens.
3. No dynamic SQL composed from user input or from observed data (SQL text seen in `pg_stat_activity` is data, never executed).
4. A banned-API analyzer rule forbids `NpgsqlCommand` creation outside the Postgres project.
5. No feature that terminates, cancels or modifies sessions. Recommendations go to the human in text only.

## Connection security

- Default `SslMode=VerifyFull` with optional custom root cert path; fall back to `Require` only by explicit user choice; `Trust Server Certificate` only via an explicit setting with a visible warning and a log note. Same for SMTP (MailKit: validate certificates, STARTTLS or implicit TLS; never accept-all validation callbacks).
- The app opens **no listening ports** and hosts no web server. Outbound allowlist = configured PostgreSQL host:port and configured SMTP host:port, validated at startup; any other `HttpClient`/socket use is a review failure.
- Validate settings (host length/charset, port range, recipient format). Recipient list is explicit; no wildcards; optional allowed-domain list.

## Data on disk

- `monitor.db` and logs live in `%LOCALAPPDATA%` (user-only ACL; verify and fix inheritance on first run). Mention BitLocker/EFS as the at-rest control; do not add custom crypto.
- Observed SQL text may contain business data. Mendix/JDBC statements usually use bind placeholders (`$1`), but not always. Provide `QueryTextMode`: `Full` (local DB, default), `LiteralMasked` (default for emails), `HashOnly`. Masking is best-effort (strip quoted strings and numeric literals); state this limitation in the UI. Truncate SQL in emails (about 300 chars).
- Screenshots/fixtures from real incidents must be anonymized (db names, users, IPs, SQL literals) before entering the repo or an AI conversation.

## Supply chain

- Central package management + lock files; `NuGetAudit` enabled; `dotnet list package --vulnerable --include-transitive` in the release checklist; nuget.org only; prefer first-party/Microsoft and widely used libraries (Npgsql, MailKit, Serilog). No telemetry/APM SDKs.
- Disable CLI telemetry in the dev environment (`DOTNET_CLI_TELEMETRY_OPTOUT=1`).
- Do not execute downloaded scripts or add tools that phone home.

## Working rules for AI agents on this repo

- Never request, display, echo or store real credentials; never read Credential Manager entries into the conversation.
- Never run the application against the real cluster or run ad-hoc SQL there; tests use recorded/synthetic data. Integration checks against the real DB are performed by the user only.
- Never add network calls, telemetry, new services, containers, or elevated-permission requirements.
- When a task seems to need a secret or a DB privilege, stop and ask the user for a design that avoids it.
- Report any suspected secret in files or logs immediately and propose removal and rotation by the user.
