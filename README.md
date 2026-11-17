# PostgreSQL Lock & Subtrans Monitor

A small local Windows tool that watches a shared PostgreSQL cluster. It detects **sessions blocked on locks**: who is
waiting, who blocks them and the root blocker. It keeps the evidence in a local SQLite file and e-mails an alert, so the
data survives after the DB team terminates sessions. Subtransaction (`SubtransSLRU` / `SubtransBuffer`) monitoring is the
second phase.

- **Read-only:** fixed `SELECT`/`SHOW` statements on a `default_transaction_read_only=on` connection.
- **No database actions:** it never terminates sessions, cancels queries or changes settings.
- **Credentials:** passwords live only in Windows Credential Manager.

> **User guide (Turkish): [docs/USER_GUIDE.md](docs/USER_GUIDE.md)**
>
> Status: lock monitoring is complete and tested on simulated data, but not yet verified against the real cluster.
> Subtrans monitoring is the next phase.

## Build and run

Requires the .NET 10 SDK.

```
dotnet build PgMonitor.slnx
dotnet test --solution PgMonitor.slnx
dotnet run --project src/PgMonitor.App
```

The app starts in **demo mode**, using fictional data with no network connection. Switch to live monitoring in
*Ayarlar*.

Data, logs and settings live in `%LOCALAPPDATA%\PgMonitor`. Command-line options:

| Option | Effect |
|---|---|
| `--data <folder>` | Use another data folder |
| `--snapshot <folder> --seconds N` | Run the demo off-screen and save one PNG per tab |

## Documents

- [Architecture](design/ARCHITECTURE.md)
- [Dependencies](design/DEPENDENCIES.md)
- [Test plan](design/TEST_PLAN.md)
- [Technical decisions](design/TECHNICAL_DECISIONS.md)

License: MIT
