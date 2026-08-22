# 0026 — Log with Serilog to console and files, through typed options

- **Status:** Accepted — amended by [0095](0095-observe-every-host-with-opentelemetry-through-one-seam.md): the aggregator this record reserved a day for exists, and the two text sinks gain an OTLP sibling inside the same extension, on the telemetry endpoint's switch
- **Date:** 2026-08-04

## Context

Neither API host configured logging at all: three `Program.cs` files leaned on the framework's
defaults, and every `appsettings*.json` carried the same minimal `Logging` block. That is console
output and nothing else — a host that dies at three in the morning leaves no trace of why, because
the console it wrote to died with it. The ask was concrete: logs on disk as well as on screen, and
the knobs — where the files go, how they roll, what level is worth keeping — in one typed place
rather than scattered strings.

Two constraints shaped the choice more than any sink. First, the integration suite attaches an
`ILoggerProvider` that records what a host logged while answering a 500 (`RecordedServerErrors`) —
any logging library that replaces the framework's pipeline can silently starve it, and no test
would go red. Second, this repository's configuration style is typed options validated at start-up
(`ObjectStorageOptions`, `OutboxOptions`); a logging system configured through a free-form
configuration DSL would be the one section of the file nothing validates.

## Decision

**Serilog, in the two API hosts only.** The Blazor BFF keeps the framework default: it owns no
domain behaviour and no persistence, and its diagnostics have not yet earned a dependency. One
NuGet entry (`Serilog.AspNetCore`) covers the logger, both sinks and the hosting integration.

**Console and rolling text files, same readable template.** Both sinks share one output template —
timestamp, level, source context, message, exception — so a line found in a file matches what the
console showed. Text rather than compact JSON: the reader today is a person with `tail` and
`grep`, and a machine format earns its place the day an aggregator exists to consume it.

**One typed options class, `ApiLoggingOptions`, bound and validated at start-up.** Path, rolling
interval, retention, minimum level, per-namespace overrides, and a switch for the file sink —
bound from the `ApiLogging` section, `ValidateOnStart`, defaults that work with no section at all.
The section is deliberately neither `Logging` (the framework's own, whose filter rules stop
applying once Serilog replaces the logger factory — the hosts' old blocks are removed rather than
left lying) nor `Serilog` (the section `ReadFrom.Configuration` would read; see the alternatives).

**`writeToProviders: true`, guarded by a test.** Serilog's hosting integration stops feeding
other registered `ILoggerProvider`s by default, which would kill the suite's error recording
without failing anything. The flag stays on, and `LoggingTest` in the test kit writes one error
through each host's logger factory and demands it back from the recording — the silent flip is a
red build now.

**Wired once, in `TrainingHub.Shared.Api`, called by both hosts.** `AddApiLogging` /
`UseApiLogging` follow the `CorsExtensions` shape: a pipeline configured in one `Program.cs` only
observes that host. `UseApiLogging` adds Serilog's request logging — one line per request — which
is why the default level overrides silence the framework's own per-request narration.

## Consequences

- A host failure finally survives the process: `logs/traininghub-<date>.log`, rolled daily,
  31 files kept. Retention is a privacy decision as much as a disk one — in Development EF Core
  logs command parameters (`EnableSensitiveDataLogging`), so files must expire, and the
  integration suite turns the file sink off entirely rather than persist test data.
- The API image runs as a non-root user against a root-owned `/app`, and Serilog reports an
  unwritable sink only through `SelfLog`, which nobody reads. The Dockerfile prepares a writable
  `logs/` before dropping privileges, so the failure cannot happen instead of being handled.
- `scripts/generate-clients.sh` loads the layered host during the build-time OpenAPI emission;
  whatever it logs lands under `logs/`, which `.gitignore` already covers — no new entry.
- Both hosts must keep calling the pair, and nothing about host symmetry was ever guarded before;
  `BothApiHosts_ConfigureTheSameLogging` reads the two `Program.cs` files and says so. Serilog
  itself stays a detail of the shared API layer — its `Logging` namespace and the extension that
  wires it: `OnlyTheLoggingExtension_TouchesSerilog` keeps the library out of every inner layer,
  out of the hosts' own code, and out of everything else in `Shared.Api`.

## Alternatives considered

**Stay on `Microsoft.Extensions.Logging`.** The gap is precisely the requirement: the framework
ships no file sink, so files mean a third-party provider anyway — the choice was never "no
dependency", only which one.

**NLog.** A capable equal, and the older configuration culture shows it: XML files, layout
renderers, and an ecosystem organised around configuration-as-document. Serilog's
configuration-as-code is the shape this repository already speaks — a lambda receiving validated
options — and message templates with structured properties are first-class rather than bolted on.

**`ReadFrom.Configuration` over a `Serilog` section.** The idiomatic Serilog answer, rejected
deliberately: the section becomes a stringly-typed DSL — sink names, argument names, level
switches — that binds against nothing, so a typo means a sink that silently never attaches, and
`ValidateOnStart` protects none of it. The whole point of the options pattern here is that a wrong
configuration refuses to start.

**Compact JSON files (`CompactJsonFormatter`).** The right format the day logs are shipped to an
aggregator, and a worse one every day before that: nobody greps JSON for pleasure. The formatter
is one constructor argument in one extension; adopting it later is not a migration.

**Request logging off (skip `UseSerilogRequestLogging`).** Fewer moving parts, but with
`Microsoft.AspNetCore` overridden to Warning the hosts would log no trace of ordinary traffic at
all — the file that exists to explain an incident would be missing the request that caused it.
