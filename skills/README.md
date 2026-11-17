# Agent Skills – PostgreSQL Subtrans Monitoring Tool

Six independent, composable skills for a **personal, local, Windows-only, read-only** PostgreSQL cluster monitor (.NET + SQLite + Windows Credential Manager).

## Which skill for which task

| Task | Load |
|---|---|
| Write / review a monitoring query, interpret a wait event, handle PG version or permission differences | `postgres-diagnostics` |
| Create projects, host, Npgsql data source, UI, packaging, power events | `dotnet-windows-development` |
| Sampling loop, ring buffer, memory budget, SQLite schema, retention, size cap | `lightweight-monitoring-architecture` |
| Credentials, redaction, read-only enforcement, SQL-text handling, dependencies, agent working rules | `secure-local-development` |
| Trigger rules, incident lifecycle, severity, suspects, emails | `incident-analysis-and-alerting` |
| Tests, fixtures, analyzers, code review | `dotnet-testing-and-code-review` |

Typical combinations: new collector = diagnostics + architecture + dotnet + security. Alert change = incident + testing. Any PR review = testing (review checklist) + security.

## Ownership (no overlap)

| Concern | Single owner |
|---|---|
| SQL text, wait-event meaning, version/privilege matrix | postgres-diagnostics |
| Polling cadence, tiers, buffer, SQLite, retention | lightweight-monitoring-architecture |
| Detection thresholds, state machine, evidence labels, email policy | incident-analysis-and-alerting |
| Secrets, redaction, read-only guarantees, egress | secure-local-development |
| Project layout, hosting, Npgsql/WPF specifics | dotnet-windows-development |
| Test and review method | dotnet-testing-and-code-review |

Skills reference each other by name only; none copies another's rules.

## Fixed project constraints (every skill reinforces, none overrides)

Local Windows app only · existing authorized PostgreSQL account, no new user or privileges · strictly read-only monitoring · SQLite via Microsoft.Data.Sqlite · Windows Credential Manager for secrets · bounded memory, incident-triggered detail persistence · minimal DB load, memory, disk · no credential exposure, no data to external services · no Docker, Kubernetes, cloud or extra services.

## Installing

Copy the `skills/` subfolders into `.claude/skills/` of the repository (Claude Code project skills), or into your personal skills folder. Each folder is self-contained: `SKILL.md` plus optional `references/`.

## Layout

```
skills/
  README.md
  postgres-diagnostics/            SKILL.md, references/queries.md
  dotnet-windows-development/      SKILL.md
  lightweight-monitoring-architecture/  SKILL.md, references/sqlite-schema.md
  secure-local-development/        SKILL.md, references/credential-manager.md
  incident-analysis-and-alerting/  SKILL.md, references/rules-and-defaults.md
  dotnet-testing-and-code-review/  SKILL.md, references/review-checklist.md
```
