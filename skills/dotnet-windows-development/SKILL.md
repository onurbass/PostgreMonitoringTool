---
name: dotnet-windows-development
description: Build the local Windows .NET desktop monitor - solution layout, Generic Host, Npgsql data source with read-only session options, async/cancellation, WPF dashboard threading, power/sleep handling, single-instance, packaging and Turkish-culture pitfalls. Use when creating or changing project structure, hosting, the PostgreSQL connection setup, UI code or build/publish settings.
compatibility: .NET 10 LTS (net10.0-windows), WPF, Windows 10/11 x64. .NET 8 and 9 passed end of support in Nov 2026 - confirm on Microsoft's support policy page. Verify current NuGet versions before pinning.
---

# dotnet-windows-development

Owns: **how the application is structured, hosted and built.** Query content -> postgres-diagnostics. Buffering/SQLite design -> lightweight-monitoring-architecture. Secrets/redaction -> secure-local-development. Detection logic -> incident-analysis-and-alerting.

## Stack decisions

| Area | Choice | Why |
|---|---|---|
| Runtime | `net10.0-windows`, C# latest, `Nullable` on, `TreatWarningsAsErrors` | Current LTS; analyzers catch async/null bugs early |
| UI | WPF + CommunityToolkit.Mvvm | Mature DataGrid, Windows-only is fine, light. WinUI 3 is heavier to package; avoid web UIs/Blazor Hybrid/Electron (adds a web stack and ports) |
| Tray | WinForms `NotifyIcon` (`UseWindowsForms`) or H.NotifyIcon | WPF has no built-in tray icon |
| Host | `Microsoft.Extensions.Hosting` Generic Host, `BackgroundService` | DI, config, lifetime, graceful shutdown |
| PostgreSQL | Npgsql (`NpgsqlDataSource`) | Official-quality driver; read-only only |
| Local store | Microsoft.Data.Sqlite, plain ADO.NET (no EF Core) | Smaller, predictable, no migrations framework |
| Mail | MailKit (library only, no service) | `System.Net.Mail.SmtpClient` is discouraged for new code |
| Logging | Microsoft.Extensions.Logging + Serilog file sink with size-capped rolling | Bounded disk; see redaction in secure-local-development |

Do not add Docker, installers requiring services, web servers, background Windows Services, or telemetry packages (see Run model).

## Solution layout (dependencies point inward)

```
src/
  PgMonitor.Core            domain types, incident engine, interfaces; NO IO, NO Npgsql/Sqlite refs
  PgMonitor.Postgres        collectors (Npgsql), capability probing, SQL constants
  PgMonitor.Storage         Sqlite repositories, retention, migrations
  PgMonitor.Security        credential store, redaction
  PgMonitor.Notifications   SMTP sender, outbox worker
  PgMonitor.App             WPF, DI composition root, hosted services
tests/  one test project per src project + PgMonitor.Scenarios
Directory.Build.props       TFM, nullable, analyzers, lock files
Directory.Packages.props    central package versions (CPM)
```
Enable `RestorePackagesWithLockFile` and Central Package Management. `Core` receives a `TimeProvider` by injection and never calls `DateTime.UtcNow` / `DateTimeOffset.UtcNow` directly.

## Run model

- **Run as a normal per-user desktop app**, auto-start via Startup folder shortcut or a per-user "At log on" scheduled task. **Not a Windows Service**: Credential Manager entries are per-user and a service account cannot read them.
- Single instance: named `Mutex` (`Local\PgMonitor.<user>`); second launch focuses the first.
- Data/config under `%LOCALAPPDATA%\PgMonitor\` (`settings.json`, `monitor.db`, `logs\`), never next to the executable. `settings.json` holds only non-secret values (host, port, db, thresholds, mapping).
- Minimize-to-tray; closing the window must not stop monitoring. On real exit: if an incident is open, flush buffers and write a `monitor_stopped` event before exit.

## Npgsql setup (read-only, low-impact)

Build the data source once at startup from validated options plus a runtime-retrieved secret:

```csharp
var csb = new NpgsqlConnectionStringBuilder {
    Host = o.Host, Port = o.Port, Database = o.Database, Username = o.Username,
    ApplicationName = "PgMonitor",                 // lets the tool exclude itself
    SslMode = o.SslMode,                           // prefer VerifyFull; Require minimum
    Pooling = true, MinPoolSize = 0, MaxPoolSize = 2,
    Timeout = 10, CommandTimeout = 10, KeepAlive = 30,
    Options = "-c default_transaction_read_only=on -c statement_timeout=5000 " +
              "-c lock_timeout=1000 -c idle_in_transaction_session_timeout=15000",
    IncludeErrorDetail = false                     // never leak data in exceptions
};
// password supplied separately (see secure-local-development), never placed in options/log
var ds = new NpgsqlDataSourceBuilder(csb.ConnectionString) { /* password set via builder */ }.Build();
```
- Max **2** connections total (sampler + occasional blocker lookup). The monitor must never contribute to connection pressure.
- After connect, verify `SHOW default_transaction_read_only` = `on`; refuse to run otherwise.
- Prepare hot statements (`cmd.Prepare()` / `AutoPrepareMinUsages`) - the same 2-3 statements run every tick.
- Read with `CommandBehavior.SequentialAccess`, ordinal-based `GetFieldValue<T>`; map to immutable `readonly record struct`/records. Handle `DBNull` explicitly.
- Reconnect: catch `NpgsqlException`/`TimeoutException`, exponential backoff with jitter (1s -> 60s cap), publish `MonitorState.Disconnected`. Never tight-loop.
- Do not set `Trust Server Certificate=true` silently; see secure-local-development.

## Async, threading, lifetime

- Everything IO is `async` with a `CancellationToken` tied to host shutdown; no `.Result`/`.Wait()`; no `async void` except UI event handlers.
- Sampler runs on `PeriodicTimer` (see architecture skill for tick rules); do not use `Task.Delay` loops.
- Hand data to the UI via an immutable snapshot reference (`Volatile.Write`) plus a UI-side `DispatcherTimer` (about 1 s). Do not marshal per-row updates from background threads.
- DataGrid: `EnableRowVirtualization`, fixed row cap (e.g. 500), no per-tick `ItemsSource` rebuild when nothing changed, truncate SQL cell text and show full text in a detail pane.
- Unhandled exceptions: log (redacted), show non-modal notice, keep sampling. Wrap each hosted service loop in a try/catch-and-continue with backoff.

## Power and clock events

- Subscribe to `SystemEvents.PowerModeChanged`: on suspend stop timers; on resume record a **gap** (`monitor_gap` event with start/end), re-baseline cumulative counters, reconnect. A gap is *unknown*, never *healthy*.
- Use `TimeProvider` everywhere; store UTC (`DateTimeOffset` -> unix ms); convert to local only in the view layer. Server time comes from the sample (`now()`), client time only for local scheduling.

## Turkish-locale safety (user machine is `tr-TR`)

- Culture-sensitive casing breaks identifiers (`"INFO".ToLower()` -> dotless i). Use `ToLowerInvariant()`/`ToUpperInvariant()` or `StringComparison.OrdinalIgnoreCase` for SQL keywords, wait-event names, db names, config keys.
- Format/parse numbers and dates for storage with `CultureInfo.InvariantCulture`; set `CultureInfo.DefaultThreadCurrentCulture` only for display. Enable CA1304/CA1305/CA1309/CA1311 analyzers.
- Do not set `InvariantGlobalization=true` if localized UI formatting is wanted; just be explicit in code.

## Build and publish

- `dotnet publish -c Release -r win-x64 --self-contained false` (small, needs .NET 10 Desktop Runtime) or `--self-contained true` (no prerequisites, larger). **No trimming or NativeAOT** (WPF unsupported).
- Workstation, non-concurrent GC is adequate for this footprint: `<ServerGarbageCollector>false</ServerGarbageCollector>`, optionally `<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>`; set `TieredPGO` default. Measure before tuning further.
- `dotnet list package --vulnerable --include-transitive` before each release; keep NuGet source to nuget.org only.
