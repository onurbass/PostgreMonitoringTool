# PostgreSQL Lock & Subtrans Monitor

A small local Windows tool that watches a shared PostgreSQL cluster. It detects **sessions blocked on locks**: who is
waiting, who blocks them and the root blocker. It keeps the evidence in a local SQLite file and e-mails an alert, so the
data survives after the DB team terminates sessions. Subtransaction (`SubtransSLRU` / `SubtransBuffer`) monitoring is the
second phase.

- **Read-only:** fixed `SELECT`/`SHOW` statements on a `default_transaction_read_only=on` connection.
- **No database actions:** it never terminates sessions, cancels queries or changes settings.
- **Credentials:** passwords live only in Windows Credential Manager.

> Status: under development. A user guide (Turkish) will be added in `docs/USER_GUIDE.md`.

## Build

Requires the .NET 10 SDK.

```
dotnet build PgMonitor.slnx
dotnet test --solution PgMonitor.slnx
```

## Documents

- [Architecture](design/ARCHITECTURE.md)
- [Dependencies](design/DEPENDENCIES.md)
- [Test plan](design/TEST_PLAN.md)
- [Technical decisions](design/TECHNICAL_DECISIONS.md)

License: MIT
