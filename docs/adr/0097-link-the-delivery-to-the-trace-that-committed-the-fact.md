# 0097 — Link the delivery to the trace that committed the fact

- **Status:** Accepted
- **Date:** 2026-08-22

## Context

The outbox is where a trace used to end. A request commits a fact and answers; seconds later — or
minutes and four retries later — a worker in either host claims the row, runs its consumers, sends
its mail. With ADR 0095 both halves are traced, and nothing connects them: the delivery starts
from a background poll that belongs to no request, so the most interesting causal chain in the
system — "this registration caused this welcome email" — existed everywhere except in the
telemetry.

The W3C context that could connect them is in hand at exactly the right moment.
`OutboxIntegrationEventPublisher` stages the envelope while the request's own activity is current,
inside the very save that commits the fact (ADR 0002); ASP.NET Core opens that activity for every
request whether or not anything exports. The question was never whether the context is available
but what the envelope should carry, and what the delivery should do with it.

## Decision

**The envelope carries one `traceparent`, and nothing else of telemetry.** A nullable
fifty-five-character column, stamped from the current activity's identifier in the same
transaction everything else on the row commits in. Not `tracestate`, not baggage, not span data:
those would make the outbox a telemetry store, and the outbox is a delivery mechanism that now
carries a pointer. An envelope with no context — nothing was being traced — is delivered exactly
as before; the column is never load-bearing.

**Each delivery attempt is its own trace, linked back — never a child.** The processor starts
`Deliver {Name}` as a new root carrying a span link to the stored context, the shape OpenTelemetry
names for asynchronous work. Continuing the original trace was the tempting alternative and the
wrong one twice over: a message retried on a doubling schedule would stretch one request's trace
across minutes of unrelated waiting, and under parent-based sampling an unsampled request would
silently swallow the delivery spans of everything it caused — the retry that matters most would be
the one nobody recorded. Linked roots keep every delivery findable on its own and its origin one
click away, whatever the sampler decided about either.

**One span per consumer, named by the ledger identity.** Below the delivery root, each consumer
runs in a span carrying its `ConsumerName` — the same name the delivery ledger settles under
(ADR 0034) — with its outcome, and its exception when it failed. The trace of a partial delivery
then shows the retry story exactly as the ledger records it: which consumers settled, which one
failed, what the next attempt still owes. A payload nobody can deserialize has no consumer span to
blame, so the delivery root records that exception itself.

## Consequences

- The showcase chain is now walkable in the dashboard: the request trace holds the command and its
  SQL; the linked `Deliver TrainerCreated` trace holds the consumer spans and the SMTP send; the
  Mailpit inbox holds the words. Two traces, one link apart, each honest about its own duration.
- One migration adds the column; the raw claim SQL reads whole rows and needed nothing. The
  publisher stamps unconditionally — the stamp is two fields read from an object already in hand,
  and gating it on "is anything exporting" would couple a write path to an observability switch.
- A requeued poison message re-delivers under a fresh trace that still links to the original
  request, however old: the pointer ages with the row, not with the telemetry backend's retention.
- `TheOutboxEnvelope_CarriesItsProducersTraceContext` holds the shape: the column exists, the
  configuration maps it, the publisher stamps it.

## Alternatives considered

**No propagation.** The delivery is traced but orphaned; correlating it back means grepping logs
for the message identifier. That is the status quo this record exists to end, kept only if the
column were expensive — and it is fifty-five nullable characters.

**Continue the trace: the delivery as a child of the stored context.** One seamless trace in the
demo, and the two failure modes above in production — minutes-long traces that render as mostly
silence, and parent-based sampling deciding retrospectively that a delivery was not worth
recording because its request was not. The demo is one click worse; everything else is better.

**A general correlation-identifier column.** A `CorrelationId` answers logs but not traces; the
`traceparent` answers both — it is the correlation identifier, standardized, and the log lines
already carry the same trace identifier through the sink (ADR 0095). Inventing a second
correlation currency would mean maintaining an exchange rate between them.

**Stamp only when the activity is recorded.** Saves fifty-five characters per row while sampling
is off, at the price of an envelope whose column means "was sampled" instead of "was caused by" —
and of a write path that changes behavior with an observability dial. The column stores the fact's
provenance; whether anyone recorded the producing trace is the backend's business.

## Verification

`TheOutboxEnvelope_CarriesItsProducersTraceContext` was seen red with the publisher's stamp
commented out before it was trusted. `OutboxTelemetryTests` proves the link mechanics through the
BCL's own listener: a stored context becomes exactly one link on a root span, a null or garbled
one becomes a plain root, and an ambient activity around the worker loop cannot adopt the delivery
as its child. The integration kit proves the stamp end to end on both hosts: a registration's
committed envelope carries a `TraceParent` that parses as a W3C context — with the suites' export
switched off, which is the point: the stamp is a fact about the envelope, not about telemetry
being on.
