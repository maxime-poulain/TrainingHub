# 0095 — Observe every host with OpenTelemetry, through one seam

- **Status:** Accepted
- **Amends:** [0026](0026-log-with-serilog-to-console-and-files-through-typed-options.md)
- **Date:** 2026-08-22

## Context

The application is architecturally clean and operationally blind. A request that slows down cannot
say where the time went; a command that fails is a log line with no surroundings; the outbox
delivers a committed fact minutes after the request that produced it, and nothing connects the
two. The health endpoints (ADR 0037) answer whether the hosts are up, and the Serilog files
(ADR 0026) answer what a host said — but "which database query made `POST /Training` slow
yesterday" is a question this repository could not answer at all.

ADR 0026 saw this day coming and left the door on the latch: text sinks were chosen because "the
reader today is a person with `tail` and `grep`, and a machine format earns its place the day an
aggregator exists to consume it." This record is that day.

Two constraints shape everything below. The platform already speaks a telemetry language:
`ActivitySource` and `Meter` are BCL types in the shared framework, ASP.NET Core opens an activity
for every request whether anyone listens or not, and `HttpClient` propagates the W3C trace context
on its own. And this repository confines every adopted library to a named seam — Serilog to its
`Logging` corner, the health dashboard to its extension — with a rule holding each line.

## Decision

**OpenTelemetry, as the subscriber and the wire — never as the vocabulary.** Custom
instrumentation anywhere in the solution speaks the BCL's `ActivitySource` and `Meter`, which need
no package. Only one seam references OpenTelemetry itself: `AddApiTelemetry` in
`TrainingHub.Shared.Api`, following the `AddApiLogging` shape, called by both API hosts so neither
can quietly observe less than the other. The packages are the stable 1.18.0 train throughout; the
EF Core bridge, which has never shipped a stable version, stays out — database spans come from the
stable SqlClient bridge, database metrics from EF Core's own built-in meter.

**One typed options class, and the endpoint is the switch.** `TelemetryOptions`, section
`Telemetry`, bound and validated at startup like every options class here (ADR 0033). A blank or
absent `OtlpEndpoint` registers nothing at all — not a disabled pipeline, no pipeline — which is
what CI, the test factories and a developer who never started the dashboard run under. The
property is a `string` rather than a `Uri` on purpose: the test factories neutralize the committed
Development value with an empty setting, and an empty string bound to a `Uri` becomes an empty
relative address instead of staying recognizably blank.

**The platform's own instrumentation first, with the noise filtered where it is subscribed.**
Request spans, outgoing-call spans, SQL command spans, runtime and HTTP meters — all from the
standard bridges, none hand-rolled. Three filters keep the signal worth reading: health probes and
the Development dashboard's ten-second polls are not recorded, on the way in or out; and a SQL
command with no enclosing operation — the outbox's five-second poll, the startup migrations — is
not a trace. Exceptions are recorded on the span where they escape.

**One resource identity, the one the deployment already uses.** `service.name` is the host's
application name — the same identity the health dashboard registers — with the assembly's
informational version and the environment name beside it. Telemetry invents no second naming
scheme.

**Parent-based ratio sampling, as configuration.** `TracesSampleRatio` feeds the standard
`ParentBased(TraceIdRatioBased)` sampler: one in Development, so every trace is whole and
findable; a dial for anything production-shaped. No custom sampler.

**Logs join the same wire through the Serilog seam.** `AddApiLogging` gains the official
OpenTelemetry sink beside its console and file sinks — inside the one extension allowed to touch
Serilog, on the same endpoint switch, stamped with the same service name. Log lines reach the
aggregator with their structured properties, the caller ADR 0027 stamps, and the trace and span
identifiers that make them findable from the trace. The console and the files stay exactly as
ADR 0026 shaped them; this amends that record's format clause, nothing else.

**The BFF gets its own small counterpart, not a reference to the seam.** The proxy propagates the
trace context to the API whether or not anything records, so an uninstrumented BFF would leave
every API trace naming a parent span no backend ever received. Its `Telemetry` corner wires the
same section shape with the platform bridges and the framework's logging provider — this host has
no Serilog, deliberately — and never references `TrainingHub.Shared.Api`, which would drag the
backend into a host that owns no domain and no persistence.

**The Aspire Dashboard is the fourth dependency, and nothing depends on it.** One container
speaking OTLP natively, showing traces, metrics and structured logs, joining the bare
`docker compose up` beside SQL Server, SeaweedFS and Mailpit — and like Mailpit it keeps no
volume, because telemetry history belongs to a production backend. No host declares `depends_on`
toward it and no health check watches it: the exporter is batched and background, so a dashboard
that is down costs dropped batches and nothing else. No OpenTelemetry Collector: three processes
exporting to one backend need no fan-out, no collector-side processing, and no second
configuration surface; the day multiple backends or tail sampling arrive is the day a collector
earns its container.

## Consequences

- A slow request now names its spans: the HTTP span, the command below it (ADR 0096), the SQL
  commands below that, and — one link away — the outbox delivery it caused (ADR 0097).
- Both API hosts call `AddApiTelemetry` and neither may drift:
  `BothApiHosts_ConfigureTheSameTelemetry` reads the two `Program.cs` files. OpenTelemetry itself
  stays a detail of the seam and the BFF's telemetry corner: `OnlyTheTelemetrySeam_TouchesOpenTelemetry`
  keeps the library out of every other assembly and every other file.
- The generated-client emission boots the layered host; the seam returns early under
  `OpenApiDocumentGeneration.IsInProgress()`, the same gate the migrations and seeders use, so
  that boot opens no exporter.
- The integration factories blank the endpoint, so no suite exports anywhere — with the endpoint
  set and the backend away, the SDK's exporter retries in the background and drops, which is the
  failure behavior a business application owes its telemetry: none.
- Four dependencies now, not three: the compose file, `scripts/start-dependencies.sh`, the README
  and CLAUDE.md all say so, and the dashboard's two ports — 18888 for the UI, 4317 for OTLP — join
  the documented set.

## Alternatives considered

**Grafana with Tempo, Prometheus and Loki — or the `otel-lgtm` all-in-one.** The production-shaped
answer, and the right one for a deployment with operators, alerting and dashboards-as-code. As the
fourth container of a development machine it is a heavier image, more moving parts, and
provisioning files this repository would have to own and census, in exchange for query languages
no local workflow here needs. The hosts speak OTLP either way: pointing them at a Grafana stack
later is configuration, not a migration — the same posture ADR 0026 took toward the aggregator.

**Jaeger.** Traces only. Two more containers arrive the moment metrics and logs matter, and the
smallest useful stack stops being small.

**An OpenTelemetry Collector between the hosts and the backend.** The recommended production
topology, adopted here the day it answers a need: several backends, redaction outside the
process, tail sampling. Today it would be one more container and one more configuration dialect
between three processes and one dashboard.

**The `OTEL_*` environment variables instead of typed options.** The SDK reads them natively, and
they are exactly the stringly-typed surface ADR 0026 rejected when it declined
`ReadFrom.Configuration`: nothing binds them, nothing validates them, and a typo exports nowhere
without failing anything. One typed class, validated at startup, is the house answer.

**Forwarding Serilog's events through `writeToProviders` to an OpenTelemetry logging provider.**
No new package, one pipeline — and the forwarding path re-renders each event for the providers,
where the official sink ships the Serilog event itself: template, properties, enrichers, trace
correlation. The sink is one `WriteTo` call inside the seam that already owns Serilog.

## Verification

The two rules were seen red before they were trusted: `BothApiHosts_ConfigureTheSameTelemetry`
against a host whose call was commented out, and `OnlyTheTelemetrySeam_TouchesOpenTelemetry`
against a controller reaching for an OpenTelemetry type.

The failure behavior is proven by hand, the way the compose healthchecks are (ADR 0065): stop
`aspire-dashboard`, create a training, and the request answers exactly as before — the only trace
of the outage is the exporter's own event-source counter of dropped batches. The suites prove the
off switch instead: they run with the endpoint blanked, and their green includes every request
having worked with no pipeline registered.
