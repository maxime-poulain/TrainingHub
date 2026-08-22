# 0096 — Name the operation once, and bound every tag

- **Status:** Accepted
- **Date:** 2026-08-22

## Context

ADR 0095 opens the wire; this record decides what travels on it. The dangers are known and dull:
traces that record everything say nothing, a metric with an unbounded tag is a memory leak in the
aggregator, and telemetry is the easiest place in a system for personal data to leak — an address
in a span attribute outlives every retention policy the database ever heard of. Each needed a
decision, not a habit.

The repository also already owns most of the vocabulary telemetry needs. The naming records
(ADR 0081, 0086, 0087) spent real effort making a message's type name say everything; each
aggregate owns stable error codes (ADR 0015) that the localization architecture deliberately keeps
apart from human sentences (ADR 0089); the outbox registry (ADR 0024) closes the set of fact
names; and ADR 0027 already decided where a caller's identity lives — on the log line.

## Decision

**Three sources, named for the mechanisms they trace, each beside its instrumentation site.**
`TrainingHub.Application` for command and query spans, owned by the CQRS pipeline behavior;
`TrainingHub.Outbox` for delivery and consumer spans, beside the processor;
`TrainingHub.Email` for the SMTP send, beside the adapter. Each meter carries its source's name.
The bounded contexts were considered and declined: four of the six are ports rather than code
units, so context-named sources would map to nothing an operator can switch on or off.

**One pipeline behavior is the whole of the application-level tracing.** Registered first, ahead
of validation, so a rejected command counts as a failed command — the telemetry restatement of
ADR 0016. The span is named by the message type, `CreateTrainerCommand` and not a trimmed
`CreateTrainer`: the naming records made that name self-describing, and telemetry does not coin a
second vocabulary it would then have to keep synchronized. No handler carries a line of telemetry.
The layered stack deliberately gets none of this: it has no pipeline to hang one behavior on, its
HTTP route already names each of its use cases, and everything below the route — domain, EF,
outbox, email — is shared and instrumented identically. The difference is not a gap; it is the
two styles' honest prices, on display.

**Domain events get no spans.** They dispatch inside `SaveChangesAsync`, inside the very span
their command opened, before the transaction commits (ADR 0002); their effects — the SQL spans,
the outbox rows — are already in the trace. Eighteen extra spans per save would say nothing new.
The `AuditWhen*` handlers remain the repository's answer to "should events be logged": by explicit
decision, per handler, never automatically.

**A histogram per operation kind, and no counter triple beside it.** `traininghub.commands.duration`
and `traininghub.queries.duration` carry count, failure rate and latency at once — an executed
counter and a failed counter would restate two of the histogram's own dimensions.
`traininghub.outbox.delivery.duration` and `traininghub.email.send.duration` follow the same
shape; `traininghub.outbox.poisoned` counts the one transition that is an alarm rather than a
rate; and `traininghub.facts.delivered`, tagged by wire name, is the entire business surface —
trainings created, trainers suspended, verifications requested — read where every business fact
already passes, one counter, zero new classes, zero domain coupling.

**Every tag comes from a set the code closes.** Message type names, the seventeen registered fact
names, the twenty consumer names, three outcomes, and `error.code` from the arch-ruled error
vocabulary — bounded by construction, every one. Never a user, trainer or training identifier,
never an address, never a URL, never a request id. Failed operations carry `error.code` — the
stable code, `Trainer.PhotoTooLarge`, never the localized sentence: codes are telemetry's language
exactly as they are the API's (ADR 0012, 0089).

**Identity stays in the logs, and personal data stays out of everything.** ADR 0027 already
placed the caller on the log line; logs now travel with trace identifiers (ADR 0095), so "who" is
one correlated query away from any span — which is why spans and metrics carry no identity at all.
No signal ever carries a password, a token, a verification or reset link, an address, a message
body, or a name; the send span records the SMTP conversation's duration and outcome and nothing
about the letter. The instrumentation's own redaction of query strings stays at its default, which
covers the token-bearing links the notices carry.

## Consequences

- The names are a published contract: dashboards and alerts will be written against them, so
  `TheInstrumentationNames_AreTheRecordedOnes` pins every source, meter and instrument name to
  this record — renaming one is a red build naming what it breaks, not a quiet dashboard outage.
- The inner circle carries no telemetry dependency, and now provably: the compiled-metadata rules
  could never see an unused package reference, so
  `TheInnerCircle_CarriesNoTelemetryPackage` reads the project files themselves — the kernel, the
  domains and the applications reference nothing named OpenTelemetry or Serilog.
  `Microsoft.Extensions.Logging.Abstractions` in the shared application layer remains the one
  recorded exception, carried since the audit handlers exist.
- A new command, query, fact or consumer is observable the moment it exists — the behavior, the
  processor and the registry are the instrumentation, so there is nothing per-feature to add, and
  nothing per-feature to forget.
- The cardinality budget is enforceable by review with one question: which closed set does this
  tag come from? A tag with no answer has no place on a metric.

## Alternatives considered

**Per-handler instrumentation.** The same code in eighty handlers, drifting apart from the first
week — the exact shape the validation pipeline already exists to prevent. Rejected for telemetry
for the same reason it was rejected for validation.

**Decorators over the layered stack's application services.** Four hand-written decorators,
thirty-six forwarding methods, registered by hand — a framework-sized abstraction bought so that
one stack can restate what its HTTP route already says. The asymmetry is the more honest exhibit.

**A business-metrics handler per integration event.** Seventeen classes, each incrementing one
counter, each a naming-convention citizen (ADR 0087) — against one counter at the one point every
fact already passes. The handlers would also count deliveries per consumer rather than facts;
the processor counts each fact once, at the moment its last consumer settles.

**Entity identifiers as span attributes.** Useful in development, personal data in production —
a trainer's identifier in a span is pseudonymous at best and retained wherever traces go. With
identity on the correlated log line, the attribute would buy convenience and cost a GDPR answer.
The minimum for diagnosis rides the span; the rest is one trace-id query away.

## Verification

`TheInstrumentationNames_AreTheRecordedOnes` was seen red against a renamed source before it was
trusted, and `TheInnerCircle_CarriesNoTelemetryPackage` against a telemetry package planted in an
application project. The behavior's spans and tags — success, refusal with its code, escaped
exception, and the pass-through for messages that are neither commands nor queries — are proven by
`TelemetryPipelineBehaviorTests` through the BCL's own listeners, which is exactly what any
subscriber sees. That the send span carries no recipient, subject or body is asserted the same
way, listener in hand, beside the adapter — not promised in prose.
