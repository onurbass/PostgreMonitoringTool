# DEPENDENCIES.md - What we use and why

Status: **v2 (2026-11-17).** Versions were checked on nuget.org on this date and are pinned in `Directory.Packages.props`.

## 1. Tools

| Tool | State on this machine | Needed for |
|---|---|---|
| .NET SDK 10.0.301 (runtime 10.0.9, WindowsDesktop 10.0.9) | **installed** | build, test, run |
| Git 2.55 | installed | local version history (no remote) |
| PostgreSQL, Docker, pgAdmin | not needed | - |

`global.json` pins SDK `10.0.301` with `rollForward: latestFeature`.

## 2. Target framework

.NET 10 (LTS, supported until November 2028). .NET 11 shipped in November 2026 but is STS, so we stay on the LTS line and
choose the `10.0.x` versions of the Microsoft packages.

## 3. NuGet packages

| Package | Version | Project | Why |
|---|---|---|---|
| Npgsql | 10.0.3 | Infrastructure | PostgreSQL driver (supports PG 13+) |
| Microsoft.Data.Sqlite | 10.0.15 | Infrastructure | embedded SQLite, native library bundled |
| MailKit | 5.0.0 | Infrastructure | SMTP with TLS (Microsoft discourages `System.Net.Mail.SmtpClient`) |
| Microsoft.Extensions.Hosting | 10.0.15 | Infrastructure, App | DI, options, logging, background service |
| Serilog.Extensions.Hosting | 10.0.0 | App | rolling log file |
| Serilog.Sinks.File | 7.0.0 | App | size-limited log files |
| CommunityToolkit.Mvvm | 8.4.2 | App | less view-model boilerplate |
| xunit.v3 | 4.0.3 | Tests | test framework; runs on Microsoft.Testing.Platform (set in `global.json`), so no VSTest packages are needed |

Not used: Entity Framework/Dapper (we have a few fixed queries), Moq (the simulator and small fakes are enough),
FluentAssertions (license change), Shouldly (xUnit asserts are enough), Docker/Testcontainers, any telemetry SDK.
Windows Credential Manager is called through 3 Win32 functions (`CredWriteW`, `CredReadW`, `CredDeleteW`), so no package
is needed. The tray icon uses the built-in WinForms `NotifyIcon`.

Licenses: Npgsql (PostgreSQL License), Microsoft.* (MIT), MailKit (MIT), Serilog (Apache-2.0), CommunityToolkit (MIT),
xUnit (Apache-2.0). Check them against company rules before installing on the managed laptop.

## 4. Moving to the company laptop

| Risk | Plan |
|---|---|
| nuget.org blocked | run `dotnet restore` at home, copy `%USERPROFILE%\.nuget\packages` or use `dotnet publish` output |
| No SDK there | `dotnet publish -c Release -r win-x64 --self-contained` produces a folder that needs no installed runtime |
| Antivirus/app control blocks an unsigned exe | ask IT, or run with `dotnet PgMonitor.App.dll` |
| Network | needs a route to PostgreSQL host:port and the SMTP host (VPN when remote) |
