# TrainingHub

[![CI](https://github.com/maxime-poulain/TrainingHub/actions/workflows/ci.yml/badge.svg)](https://github.com/maxime-poulain/TrainingHub/actions/workflows/ci.yml)
[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=maxime-poulain_BLRefactoring&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=maxime-poulain_BLRefactoring)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=maxime-poulain_BLRefactoring&metric=coverage)](https://sonarcloud.io/component_measures?id=maxime-poulain_BLRefactoring&metric=coverage)

A .NET 10 reference implementation that runs **two application styles over one shared domain
model**: a classic layered DDD stack and a CQRS stack. Both expose the same trainer/training
business capabilities, persist through the same EF Core model against SQL Server, and react to
the same domain events — so the two styles can be compared on identical ground rather than on
two different problems.

---

## Table of contents

- [What this project is](#what-this-project-is)
- [Strategic design](#strategic-design)
- [Architecture](#architecture)
- [Domain model](#domain-model)
- [How it works](#how-it-works)
- [Persistence](#persistence)
- [Security](#security)
- [API reference](#api-reference)
- [Tech stack](#tech-stack)
- [Getting started](#getting-started)
- [Testing](#testing)
- [Continuous integration](#continuous-integration)
- [Repository conventions](#repository-conventions)
- [Licence](#licence)

---

## What this project is

The domain is deliberately small — trainers publish trainings — so that the architecture stays
the subject. What the repository actually demonstrates:

- **One domain, two application styles.** `TrainingHub.Shared.Domain` is consumed unchanged by
  an application-service stack (`src/DDD`) and a command/query stack (`src/DDDWithCqrs`). Every
  use case exists in both, which makes the trade-offs of each style directly observable.
- **A domain that speaks only in business concepts.** Aggregates accept value objects and typed
  identifiers — never a `string`, a `Guid` or a parameter object shaped like an HTTP request.
  Turning raw input into those concepts is the application layer's job.
- **Invariants that cannot be bypassed.** Constructors are private, collections are exposed
  read-only, and every state transition goes through a behaviour method that either succeeds
  entirely or changes nothing.
- **Failure as a value, not an exception.** A railway-oriented `Result` carries accumulated
  business errors from the domain up to the HTTP status code.
- **Domain events dispatched inside the unit of work**, before persistence, so a handler's own
  writes join the same transaction.
- **End-to-end optimistic concurrency**, from a SQL Server `rowversion` up to HTTP `ETag` /
  `If-Match`, so two users cannot silently overwrite each other.

---

## Strategic design

Everything below this line is the tactical half of Domain-Driven Design — what the model is made of.
The strategic half is what decides where the lines are, and it lives in
**[`docs/strategic-design/`](docs/strategic-design/)**: the bounded contexts and their ubiquitous
language, a context map that names where each seam is visible in the code, and an event storming of
the two main flows.

Start there if you want the business before the architecture. Six architecture rules keep those
documents answerable to the model — an aggregate nobody placed in a context fails the build, and so
does a term the document and the code spell differently. See
[ADR 0023](docs/adr/0023-document-the-strategic-design-and-hold-it-to-the-model.md); what those
documents *list* is derived from the code as well, by
[ADR 0041](docs/adr/0041-derive-every-named-list-from-the-code.md).

---

## Architecture

### The dependency rule

Dependencies point inward. The domain knows nothing of persistence, HTTP or the shape of the
messages the API receives; infrastructure depends on the domain to implement its ports.

```mermaid
flowchart TB
    A["API hosts — controllers, authentication, HTTP concerns"]
    B["Application — use cases, DTOs, value-object factories"]
    C["Domain — aggregates, value objects, domain events, ports"]
    D["Shared kernel — Entity, Result, Specification, cross-cutting ports"]
    E["Infrastructure — EF Core, Identity, repositories, adapters"]

    A --> B
    B --> C
    C --> D
    E --> C
    E --> D
    A -. composition root only .-> E
```

`TrainingHub.Shared.Domain` references exactly one project — the shared kernel — and nothing
else.

The two API hosts are thin. What they have in common — controller bases, the `TrainingOwner`
policy, CORS, Identity and JWT wiring, the logging pipeline, the HTTP side of optimistic
concurrency — lives in `TrainingHub.Shared.Api`, so a rule can only be written once. Duplicating it across two
`Program.cs` is how the CQRS host ended up with no CORS policy at all while the layered one had
one, and how it kept relying on an `IHttpContextAccessor` it never registered. Persistence stayed
in `TrainingHub.Shared.Infrastructure`, which carries no ASP.NET Core framework reference.

**HTTP is a boundary, not a window.** The contracts the API publishes — `*HttpRequest` and
`*HttpResponse`, under `Shared.Api/Contracts/` — belong to the API and to nothing else. Commands,
queries and application DTOs stop at that line: no controller names one, and each host maps the
shared contracts onto its own vocabulary — the layered one to its application services, the CQRS
one to its commands and queries. Before that, the CQRS controllers bound an `EditTrainingCommand`
straight from the request body and then assigned its route identifier and expected version onto
it, which is why those commands carried `[JsonIgnore]`: a serialisation concern lodged inside an
application message. The published API and the internals can now change without each other's
permission, and the two hosts cannot drift on it, since the contract they serve is one object.

**A list leaves as one page, on either host.** `PageRequest` and `PagedResult<T>` are kernel
vocabulary beside `Result`, and both hosts answer their list endpoint with the same envelope and
its metadata. It was not always so: for a while only the CQRS host paged, deliberately — until the
asymmetry falsified two of this repository's own promises, that the stacks are compared on
identical ground and that the client generated from either host fits both. What the parity shows
instead is the honest difference: the layered service asks the repository a named question
(`GetPageByTrainerIdAsync`) and gets a page of whole aggregates to map, while the CQRS handler
projects only the columns its DTO names before the page is fetched. Same contract on the wire,
different bill underneath.

Pages also need an order no two rows can tie on, or `OFFSET`/`FETCH` lets the server put a row on
two pages and another on none. `ToPagedResultAsync` therefore takes an `IOrderedQueryable`, which
only `NewestFirst` produces, so nobody can page without ordering first — the mistake is
unwritable rather than discouraged. The order, the bounds and the envelope are recorded in
[ADR 0001](docs/adr/0001-paginate-on-the-query-side-over-a-total-order.md); the parity, and what
it displaced, in [ADR 0029](docs/adr/0029-answer-a-list-the-same-way-on-both-hosts.md).

The request contracts declare their constraints as **data annotations**, so `[ApiController]`
rejects a malformed body at model binding with a `ValidationProblemDetails` keyed by field name,
before any command or application service sees it. The shape matters as much as the check: a form
on the other end can mark each offending input rather than show one message for the whole
submission, and the annotations reach the OpenAPI document, so generated clients inherit the same
constraints. They mirror the bounds the value objects enforce — the domain stays the judge and
rejects on its own terms anything that reaches it another way. What they deliberately do not
check is the shape of an email address: .NET's `[EmailAddress]` and the domain's validator
disagree, and an API refusing what the domain accepts would be worse than one asking later.

**Every failure leaves in the same shape**, an RFC 7807 `ProblemDetails`, whether it came from a
data annotation, a FluentValidation rule, a business rule or an unhandled exception — with the
domain error codes carried in the `domainErrors` extension, so a client can still branch on
`Training.DuplicateTitle`. The API used to answer four different bodies depending on how far a request got,
one of them a bare JSON string. The handlers live in `Shared.Api`, so the two hosts cannot differ on
them — the layered one had none at all. See
[ADR 0004](docs/adr/0004-publish-every-error-as-rfc-7807-problem-details.md).

### Solution layout

Twenty-seven projects: sixteen under `src/`, eleven under `tests/`. The backend and all tests target
**net10.0**; the Blazor pair and the generated clients target **net9.0**.

| Project | Responsibility |
|---|---|
| `TrainingHub.Shared` | Shared kernel: `Entity`, `AggregateRoot`, `ValueObject`, `EntityId`, `Result`/`ErrorCollection`, `Specification`, `PageRequest`/`PagedResult`, and the cross-cutting ports `IUnitOfWork`, `ICurrentUserService`, `ITrainingSearchIndexer`, plus the CQS marker interfaces |
| `TrainingHub.Shared.Domain` | The domain model: `Trainer` and `Training` aggregates, value objects, domain events, specifications, repository interfaces, and the fact ports `IUniquenessTitleChecker` and `ITrainingCounter` |
| `TrainingHub.Shared.Application` | Value-object factories, DTOs, the aggregate-to-DTO projections, the seventeen domain event handlers, the integration events with their stable-name registry and both ports (publisher and consumer), and the fourteen post-commit consumers — all shared by both stacks |
| `TrainingHub.Shared.Infrastructure` | Persistence only: EF Core `TrainingContext`, mappings, migrations, interceptors, `UnitOfWork`, repositories, the paged-read extensions (`NewestFirst`, `ToPagedResultAsync`), the identity store, and the transactional outbox — publisher, delivery worker, dispatcher |
| `TrainingHub.Shared.Api` | The HTTP boundary: the `*HttpRequest` and `*HttpResponse` contracts both hosts publish, their mappings to the application layer, the controller bases, the `TrainingOwner` policy, CORS, Identity, JWT wiring, token issuance, concurrency helpers |
| `DDD.Application` | Application services: `TrainerApplicationService`, `TrainingApplicationService` |
| `DDD.Api` | REST host for the layered stack — controllers, composition root |
| `DDDWithCqrs.Application` | Commands, command handlers, FluentValidation validators |
| `DDDWithCqrs.Infrastructure` | **Query handlers**, Mediator dispatchers, pipeline behaviours |
| `DDDWithCqrs.Api` | REST host for the CQRS stack — controllers, composition root |
| `DDD.Domain`, `DDD.Infrastructure`, `DDDWithCqrs.Domain` | Routing projects with no source files; the domain and infrastructure they stand for live in the `TrainingHub.Shared.*` projects |
| `TrainingHub.GeneratedClients` | NSwag-generated typed HTTP clients, checked in as source |
| `TrainingHub.Blazor` / `.Client` | Blazor WebAssembly front end built with MudBlazor, and the **backend for frontend** that serves it: cookie authentication, and a YARP proxy that attaches the API's access token server-side |
| `tests/*` | Eleven projects — ten test suites and the shared kit they draw from — see [Testing](#testing) |

### Project dependency graph

```mermaid
flowchart LR
    Kernel["TrainingHub.Shared"]
    Domain["Shared.Domain"]
    SharedApp["Shared.Application"]
    SharedInfra["Shared.Infrastructure"]
    SharedApi["Shared.Api"]

    DddDomain["DDD.Domain"]
    DddApp["DDD.Application"]
    DddInfra["DDD.Infrastructure"]
    DddApi["DDD.Api"]

    CqrsDomain["DDDWithCqrs.Domain"]
    CqrsApp["DDDWithCqrs.Application"]
    CqrsInfra["DDDWithCqrs.Infrastructure"]
    CqrsApi["DDDWithCqrs.Api"]

    Clients["GeneratedClients"]
    BlazorClient["Blazor.Client"]
    BlazorHost["Blazor"]

    Domain --> Kernel
    SharedApp --> Domain
    SharedInfra --> Domain
    SharedInfra --> SharedApp
    SharedApi --> SharedApp
    SharedApi --> SharedInfra

    DddDomain --> Kernel
    DddApp --> SharedApp
    DddApp --> DddDomain
    DddInfra --> SharedInfra
    DddInfra --> DddApp
    DddApi --> DddInfra
    DddApi --> SharedApi

    CqrsDomain --> Kernel
    CqrsApp --> SharedApp
    CqrsApp --> CqrsDomain
    CqrsInfra --> SharedInfra
    CqrsInfra --> Kernel
    CqrsInfra --> CqrsApp
    CqrsApi --> CqrsInfra
    CqrsApi --> SharedApi

    BlazorClient --> Clients
    BlazorHost --> BlazorClient
```

The Blazor and generated-client projects form a separate net9.0 island; the backend graph is
rooted at the shared kernel.

---

## Domain model

### Trainer

```csharp
public static Trainer Create(TrainerId id, UserId userId, Name name, Email contactEmail, Bio? bio);
public void Edit(Name name, Email contactEmail, Bio? bio);
public void AttachPhoto(TrainerPhoto photo);
public void RemovePhoto();
public void MarkForDeletion();
```

`Create` returns a `Trainer`, **not** a `Result<Trainer>`: every argument is an already-valid
value object and the aggregate carries no cross-field rule, so assembling valid parts cannot
produce an invalid whole. `Edit` returns nothing for the same reason — a half-edited trainer is
impossible by construction rather than by discipline.

The profile is edited as a whole, from a single form carrying every field, so there is one entry
point rather than one mutator per attribute. Events are computed **before** mutation and raised
**only for attributes that actually changed**, using the value objects' structural equality. A
`null` bio clears it.

`ContactEmail` is the address a trainer publishes — deliberately distinct from the credential of
their Identity account, which the aggregate only ever references through `UserId`. Two trainers
of the same organisation may share one, so no uniqueness rule applies to it.

`Photo` holds an identity, a media type and a size — never a bucket, a key or a URL. The aggregate
says *which* photo a trainer has; where the bytes live is the infrastructure's business, which is
why moving them to a different store touches no file in the domain. `TrainerPhoto.Create` mints a
fresh identity every time, and that is load-bearing rather than incidental: a replacement therefore
writes to a key nothing names yet, so the row can be committed before anything is deleted. Neither
`AttachPhoto` nor `RemovePhoto` hands the displaced photo back — an aggregate answers whether a
change was allowed and is not a way of reading through it — so a caller that has cleanup to do
reads `Photo` before the call. See ADR 0021.

### Training

```csharp
public static Task<Result<Training>> CreateAsync(
    TrainingId trainingId, TrainerId trainerId,
    TrainingTitle title, TrainingDescription description,
    TrainingPrerequisites prerequisites, AcquiredSkills acquiredSkills,
    IReadOnlyCollection<Topic> topics,
    IUniquenessTitleChecker titleChecker, ITrainingCounter trainingCounter,
    CancellationToken cancellationToken = default);

public Task<Result> EditAsync(/* the same, without the identifiers and the counter */);
```

Here the result **is** a `Result`, for the rules the aggregate cannot settle on its own — each
one a fact in rows it cannot see, brought to the factory through a port named after its question
(ADR 0030). A title must be unique among the trainings of the same trainer, asked through
`IUniquenessTitleChecker`; and a trainer publishes at most `Training.MaximumPerTrainer` (ten)
trainings, asked through `ITrainingCounter` at creation only — editing changes a training, never
how many there are — answering `Training.CatalogueFull` when the catalogue is full. Creation and
edition share a private `ApplyEditionAsync` that checks the title rule first and **mutates
nothing when it fails** — so a rejected edition never leaves the aggregate half-changed. The
uniqueness lookup only runs when the title actually changed. Topics are de-duplicated and fully
replaced on each edition.

Neither creation nor edition raises its event from that shared path: each public entry point
raises the event matching its own intent, and only on success.

`IsOwnedBy` is the one question the aggregate answers, and the exception that proves the
no-reading-through-aggregates rule rather than eroding it: it wears
`TrainingOwnedBySpecification` — a named domain rule, not a state read — and the rule that bans
data-returning methods pins it by name, so the next question has to arrive with a record of its
own. See ADR 0028.

Both aggregates carry a lifecycle
([ADR 0050](docs/adr/0050-retire-a-training-rather-than-delete-it.md)). A training is `Published` or
`Unpublished`, a trainer `Active` or `Suspended`, and public visibility is composed from the two
rather than stored — so suspending a trainer writes one column and touches none of their trainings,
which is what makes the sanction liftable. Both pairs are reachable in both directions and every
move announces itself, which is what separates this from a soft delete wearing an enum; a rule
holds that last part. Deleting survives and changes role: withdrawing is the everyday act, and
`DELETE` answers the training created by mistake and a trainer's right to have their data removed.

### Value objects

Every value object has a private constructor and a static `Create` returning `Result<T>`, so an
invalid instance cannot exist. The three closed enumerations are the exception the rule states as a
shape: their only instances are their own static fields, so there is no factory because there is
nothing to refuse.

| Value object | Rule | Error code |
|---|---|---|
| `Name` | Firstname and lastname 2–50 characters once trimmed; **both errors accumulate** | `InvalidFirstname`, `InvalidLastname` |
| `Email` | Non-empty, valid format via `EmailValidation` | `InvalidEmail` |
| `Bio` | Non-empty, at most 500 characters once trimmed | `BioEmpty`, `BioExceeds500Characters` |
| `TrainingTitle` | Non-empty, 5–100 characters once trimmed | `InvalidTitle` |
| `TrainingDescription` | Non-empty, at most 500 characters | `InvalidDescription` |
| `TrainingPrerequisites` | Non-empty, at most 500 characters | `InvalidPrerequisites` |
| `AcquiredSkills` | Non-empty, at most 500 characters | `InvalidAcquiredSkills` |
| `Topic` | Closed set of six values, resolved by name | `InvalidTopic` |
| `TrainingStatus` | Closed set of two: `Published`, `Unpublished` | — |
| `TrainerStatus` | Closed set of two: `Active`, `Suspended` | — |
| `TrainerPhoto` | Non-empty, at most 5 MiB, PNG/JPEG/WebP — and the bytes must be what the content type declares | `PhotoEmpty`, `PhotoTooLarge`, `PhotoFormatNotSupported`, `PhotoContentMismatch` |

Three behaviours are worth knowing:

- **`TrainingTitle` compares case-insensitively.** `"Intro to C#"` and `"INTRO TO C#"` are the
  same title, which is what makes the uniqueness rule meaningful.
- **`Topic` is a closed enumeration**, not free text: Programming, Design, Marketing, Business,
  Personal Development, Leadership. `Topic.TryFromName` resolves a name without throwing — an
  unrecognised name is a validation error produced by the application layer, never an exception.
- **The two statuses resolve by name and throw**, unlike `Topic`, and the asymmetry is the point:
  a topic name arrives from a client and is reported back with everything else that was wrong,
  while a status name arrives from the column the domain wrote it to. A word the domain does not
  know there means a corrupt row, which no caller can be asked to handle and none should silently
  read as `Published`.

### Typed identifiers

`TrainerId`, `TrainingId` and `UserId` derive from `EntityId<T>`. Their constructors are private,
`Guid.Empty` is rejected at construction, and instances are built through `Create`, `Generate` or
an explicit cast — `TrainerId id = (TrainerId)someGuid` — all three backed by a compiled expression
cached per type. The cast is explicit, never implicit: turning a loose `Guid` into an identifier can
fail, and an implicit conversion would hide both the intent and the failure. Identifiers are generated by the caller before
the write, so the primary key is known without a database round-trip. A `TrainerId` is never
equal to a `TrainingId`, even for the same underlying `Guid`.

### Domain events

Events carry value objects and typed identifiers, not primitives.

| Event | Payload | Raised when |
|---|---|---|
| `TrainerCreatedDomainEvent` | `TrainerId`, `Name`, `Email` | A trainer is created |
| `TrainerNameChangedDomainEvent` | `TrainerId`, old `Name`, new `Name` | Only if the name actually changed |
| `TrainerContactEmailChangedDomainEvent` | `TrainerId`, old `Email`, new `Email` | Only if the contact email actually changed |
| `TrainerDeletedDomainEvent` | `TrainerId` | A trainer is marked for deletion — no use case does so yet, see below |
| `TrainingCreatedDomainEvent` | `TrainingId`, `TrainerId` | A training is successfully created |
| `TrainingEditedDomainEvent` | `TrainingId`, `TrainerId` | A training is successfully edited |
| `TrainingTransferredDomainEvent` | `TrainingId`, former `TrainerId`, new `TrainerId` | A training changes hands — decided by the one recorded domain service (ADR 0036) |

Events carry the facts their consumers need rather than just an identifier, because they are
dispatched **before** persistence: a handler cannot reload an aggregate that is not saved yet.

Their handlers live in `TrainingHub.Shared.Application/EventHandlers/` and are shared by both
stacks:

| Handler | Reacts to | Effect |
|---|---|---|
| `PublishIntegrationEventWhenTrainerCreatedEventHandler` | `TrainerCreatedDomainEvent` | Commits `TrainerCreatedIntegrationEvent` to the outbox — the welcome email becomes the delivery worker's reaction |
| `PublishIntegrationEventWhenTrainerContactEmailChangedEventHandler` | `TrainerContactEmailChangedDomainEvent` | Commits both addresses as `TrainerContactEmailChangedIntegrationEvent` — warning the **previous** address becomes the worker's reaction |
| `AuditWhenTrainerNameChangedEventHandler` | `TrainerNameChangedDomainEvent` | Structured audit trail |
| `DeleteTrainingWhenTrainerDeletedEventHandler` | `TrainerDeletedDomainEvent` | Deletes the trainer's trainings — cross-aggregate consistency without a database cascade |
| `PublishIntegrationEventWhenTrainingCreatedEventHandler` | `TrainingCreatedDomainEvent` | Commits `TrainingCreatedIntegrationEvent` to the outbox — indexing becomes the worker's reaction |
| `PublishIntegrationEventWhenTrainingEditedEventHandler` | `TrainingEditedDomainEvent` | Commits `TrainingEditedIntegrationEvent`, kept apart from the created fact so consumers can tell them apart |
| `PublishIntegrationEventWhenTrainingTransferredEventHandler` | `TrainingTransferredDomainEvent` | Commits `TrainingTransferredIntegrationEvent`, both owners on it — re-indexing under the new owner becomes the worker's reaction |

`Trainer.MarkForDeletion` and the trainer-deletion handler above have no caller in production,
deliberately: the API exposes no way to delete a trainer (see [Security](#security)). What the
aggregate states is the rule — a trainer does not disappear without their trainings — and the rule
holds whoever ends up triggering it. The behaviour is covered by `DomainEventPipelineTests`, which
drives it through the host's own services.

Six of the seventeen handlers act inside the transaction — ADR 0002's *domain reactions* — and eleven
translate the domain event into an integration event and commit it to the transactional outbox
(see [ADR 0024](docs/adr/0024-publish-facts-not-intents-and-version-them-in-the-envelope.md) and
[the outbox section](#domain-events-and-the-unit-of-work)). After the commit, the outbox delivery
worker hands each fact to its consumers — `IIntegrationEventHandler<TEvent>` implementations in
the same application layer — which is where the welcome email, the address warning, the three
sanction notices and the index updates now happen (ADR 0025, ADR 0056). The messages leave over
real SMTP: `IEmailSender` is declared beside its consumers in
`Shared.Application/Notifications/` and implemented by a MailKit adapter
pointed at whatever relay the `Smtp` section names — a Mailpit container locally (see
[ADR 0031](docs/adr/0031-send-email-over-smtp-and-prove-it-against-a-real-server.md)).
`ITrainingSearchIndexer` remains a kernel port with a fake implementation that only writes to the
log, so the project still depends on no search engine.

### Use cases

Every use case exists in both stacks. Note where the handler lives: in CQRS, **query handlers sit
in the infrastructure layer**, next to the persistence they project from.

The names say the layer, by convention and by rule: a layered service carries the
`ApplicationService` suffix in full — the mirror of the domain's `*DomainService`
([ADR 0036](docs/adr/0036-model-the-decision-that-has-no-home-as-a-domain-service.md)) — so a
reader can tell an application service from a domain, infrastructure or any other kind of service
at the name alone (`TheLayeredApplication_NamesItsServicesInFull`).

| Use case | `src/DDD` | `src/DDDWithCqrs` | Handler project |
|---|---|---|---|
| Create trainer | `TrainerApplicationService.CreateAsync` | `CreateTrainerCommand` | Application |
| Edit own profile | `TrainerApplicationService.EditAsync` | `EditTrainerCommand` | Application |
| Read own profile | `TrainerApplicationService.GetByIdAsync` | `GetTrainerByIdQuery` | Infrastructure |
| Publish or replace a portrait | `TrainerApplicationService.SetPhotoAsync` | `SetTrainerPhotoCommand` | Application |
| Remove a portrait | `TrainerApplicationService.RemovePhotoAsync` | `RemoveTrainerPhotoCommand` | Application |
| View a trainer's portrait | `TrainerApplicationService.GetPhotoAsync` | `GetTrainerPhotoQuery` | Infrastructure |
| Create training | `TrainingApplicationService.CreateAsync` | `CreateTrainingCommand` | Application |
| Edit training | `TrainingApplicationService.EditAsync` | `EditTrainingCommand` | Application |
| Delete training | `TrainingApplicationService.DeleteAsync` | `DeleteTrainingCommand` | Application |
| Transfer training | `TrainingApplicationService.TransferAsync` | `TransferTrainingCommand` | Application |
| Read one own training | `TrainingApplicationService.GetByIdAsync` | `GetTrainingByIdQuery` | Infrastructure |
| List own trainings | `TrainingApplicationService.GetMineAsync` | `GetMyTrainingsQuery` | Infrastructure |

The read paths differ by design: the layered stack loads aggregates through repositories and maps
them, while the CQRS stack projects straight from `TrainingContext` into DTOs with
`IQueryable` expressions, under a pipeline behaviour that switches change tracking off for
queries and restores it afterwards.

---

## How it works

### Results instead of exceptions

`Result` and `Result<T>` expose no `IsSuccess` and no `Value`: the only way to read one is to
`Match` or `Switch` on it, so an unchecked failure cannot slip through. Errors accumulate in an
`ErrorCollection`, which is how a single request can report every invalid field at once rather
than the first one.

Each error carries a code, and a code belongs to whoever raises it. The kernel declares only the
four that belong to nobody; everything else is declared beside the aggregate whose invariant was
broken, and carries that aggregate's name.

| Holder | Codes |
|---|---|
| `ErrorCodes` (kernel) | `Unspecified`, `NotFound`, `ConcurrencyConflict`, `Validation` |
| `TrainingErrorCodes` | `Training.InvalidTitle`, `Training.DuplicateTitle`, `Training.InvalidDescription`, `Training.InvalidPrerequisites`, `Training.InvalidAcquiredSkills`, `Training.InvalidTopic`, `Training.CatalogueFull`, `Training.TransferToSelf`, `Training.RecipientCatalogueFull`, `Training.UnknownRecipient`, `Training.AlreadyPublished`, `Training.AlreadyUnpublished`, `Training.TrainerSuspended`, `Training.RecipientSuspended`, `Training.AlreadyWithheld`, `Training.NotWithheld`, `Training.Withheld`, `Training.WithholdingReasonEmpty`, `Training.WithholdingReasonTooLong` |
| `TrainerErrorCodes` | `Trainer.InvalidEmail`, `Trainer.InvalidFirstname`, `Trainer.InvalidLastname`, `Trainer.BioEmpty`, `Trainer.BioExceeds500Characters`, `Trainer.PhotoEmpty`, `Trainer.PhotoTooLarge`, `Trainer.PhotoFormatNotSupported`, `Trainer.PhotoContentMismatch`, `Trainer.AlreadySuspended`, `Trainer.NotSuspended`, `Trainer.SuspensionReasonEmpty`, `Trainer.SuspensionReasonTooLong` |

`Validation` is the one the kernel declares for somebody else: the FluentValidation pipeline of the
CQRS stack answers with it, and nothing in the domain ever does (ADR 0016).

`ErrorCode` is a value object over a string. The set is open — any holder can declare one — so
`ErrorVocabularyRules` keeps it honest: nothing constructs a code at a call site, every code is
declared on a `*ErrorCodes` holder, and no two share a value. The table above is compared with the
holders on every build, in both directions, since a published vocabulary missing a code is a client
unable to branch on it (ADR 0015, ADR 0038).

### Turning input into domain concepts

Because aggregates only accept value objects, something has to build them. That is the
application layer's job, done once for both stacks by `TrainerProfileFactory` and
`TrainingDetailsFactory` in `TrainingHub.Shared.Application/Factories/`. They validate every
field, accumulate all errors in a single pass, resolve topic names against the closed set, and
either return the value objects or the complete list of what was wrong.

### Turning domain concepts back into output

The reverse direction is written once too, in `TrainingHub.Shared.Application/Projections/`.
Each aggregate has a single `Expression<Func<TAggregate, TDto>>`, consumed two ways: the CQRS
query handlers hand it to EF Core, which folds it into the `SELECT` list so no aggregate is ever
materialised, while the layered application services call the same expression compiled once into
a delegate.

The expression is the source and the delegate the derivative, never the reverse — an expression
can always be compiled, a compiled delegate can never be translated to SQL. The two stacks used
to hold their own copy of the mapping, so a field added to a DTO could reach one of them and stay
silently `null` on the other. The price of the arrangement is that the mapping must remain
EF-translatable, which is stricter than C#: null-conditional access is the usual casualty, and
the trainer's bio is read through a ternary for that reason.

### Domain events and the unit of work

Events are raised inside aggregates and dispatched by an EF Core interceptor **during**
`SaveChanges`, before anything is written. Handlers that stage further changes — deleting a
trainer's trainings, for instance — therefore take part in the same implicit transaction, and a
single commit persists the whole outcome.

```mermaid
sequenceDiagram
    participant App as Application layer
    participant Agg as Aggregate
    participant UoW as UnitOfWork
    participant Int as DomainEventInterceptor
    participant Med as Mediator
    participant H as Domain event handlers
    participant DB as SQL Server

    App->>Agg: behaviour method
    Agg->>Agg: raise domain event
    App->>UoW: SaveChangesAsync
    UoW->>Int: SavingChangesAsync
    loop until no aggregate holds an event
        Int->>Int: collect and clear events from tracked aggregates
        Int->>Med: publish each event
        Med->>H: handle
        H-->>Int: may stage further changes,<br/>including outbox rows
    end
    Int-->>UoW: continue
    UoW->>DB: single transaction, one commit:<br/>state change + outbox rows
```

The loop matters: a handler may itself change an aggregate that raises new events, and draining
continues until none is left.

That transaction is exactly right for a handler that only stages further changes, and wrong for one
that leaves the process — which is why no handler leaves the process anymore. The split is recorded
in [ADR 0002](docs/adr/0002-keep-domain-reactions-in-the-transaction-and-deliver-integration-events-through-an-outbox.md):
**domain reactions** (the cascade delete, the audit line) stay in the transaction, and
**integration events** go through a transactional outbox, whose message design is recorded in
[ADR 0024](docs/adr/0024-publish-facts-not-intents-and-version-them-in-the-envelope.md).

A handler crossing the boundary now translates the domain event into a primitives-only fact —
`TrainerCreatedIntegrationEvent`, not `WelcomeEmailRequested` — and hands it to
`IIntegrationEventPublisher`. The outbox implementation serializes it into an `OutboxMessage` row
staged in the **same `TrainingContext`** the save is flowing through, so the diagram above already
tells the whole story: the fact commits with the state change that justified it, or dies with it.
The write side is proven from the change tracker up to a lost optimistic-concurrency race
(`OutboxTests`), and each envelope carries a stable name and version resolved through an explicit
registry, a version-7 GUID the delivery ledger dedups on, and the `Attempts`/`Error` columns
the retry policy will use.

**Delivery** is the other half (ADR 0025). Each host runs `OutboxDeliveryWorker`, a hosted
service that polls the table, claims the oldest unprocessed rows in a single
`UPDATE … OUTPUT` under `READPAST` and a database lease — two hosts over one table are competing
consumers, safely — and hands each fact to each of its consumers independently, through an
explicit dispatcher that isolates every consumer from its neighbours' failures. Each success lands
in a per-consumer delivery ledger (`OutboxMessageConsumer`), so a retry re-runs only the consumers
still owed — a failing neighbour cannot replay a delivered welcome email (ADR 0034). The message
is stamped `ProcessedOnUtc` when every consumer has settled; a failed pass records its reasons on
the envelope, counts one attempt, and books the next try one doubling further out — 30 s, then 60,
then 120 — so a downstream outage is ridden out rather than burned through (ADR 0033). A message
whose budget in `OutboxOptions.MaxAttempts` is spent is poison: kept, no longer claimed, its last
error beside it, announced once at Error in the log — and its ledger shows the operator exactly
which consumers are owed. Delivered rows older than `OutboxOptions.RetentionPeriod` are swept
after each drain, their ledger rows cascading with them — poison never is. Delivery is
at-least-once and eventual by a few seconds; the ledger dedups by the envelope's id, and the
residual lapsed-lease window is narrowed to the consumers not yet settled.

### A write, end to end

```mermaid
sequenceDiagram
    participant C as Client
    participant Ctrl as Controller
    participant V as ValidationPipelineBehavior
    participant Hdl as Command handler
    participant F as Value-object factory
    participant Agg as Aggregate
    participant Repo as Repository
    participant UoW as UnitOfWork

    C->>Ctrl: PUT with JSON body
    Ctrl->>V: dispatch command
    V->>V: run FluentValidation rules
    V->>Hdl: handle
    Hdl->>Repo: load aggregate
    Hdl->>F: build value objects from primitives
    F-->>Hdl: Result — value objects or accumulated errors
    Hdl->>Agg: behaviour method
    Hdl->>Repo: Update
    Hdl->>UoW: SaveChangesAsync
    UoW-->>Ctrl: Result
    Ctrl-->>C: 200, or a status derived from the error code
```

The CQRS pipeline enforces one rule beyond validation itself: **every command must declare a
validator**, even an empty one, or dispatch fails loudly rather than silently skipping checks.
The layered stack has no request-validation layer — its only guards are the value objects and the
factories above.

**A rejected command answers like every other failure.** `ValidationPipelineBehavior` returns a
failed `Result` carrying `ErrorCodes.Validation` rather than throwing, so the rejection leaves
through the single place a business failure becomes a body and is published under `domainErrors`.
It used to throw, and the same endpoint then published two error shapes depending on which rule the
caller broke — a malformed email as a field map, a bio too long as domain codes. Queries still throw:
they answer with the data they read and have no failed state to return, so their three identifier
guards remain the one path into `ValidationExceptionHandler`. See
[ADR 0016](docs/adr/0016-let-a-rejected-command-fail-like-every-other-command.md).

### Optimistic concurrency

Every aggregate root carries a `RowVersion`, declared once for all of them in
`AggregateRootTypeConfiguration` and mapped to a SQL Server `rowversion` that the server bumps on
every update. EF Core adds it to the `WHERE` clause of `UPDATE` and `DELETE`, so a statement that
matches no row means somebody else got there first.

A store-side token alone would not prevent the case that matters — two users editing from forms
loaded at different times — because each request reloads the aggregate and would compare an
already-current token. So the version travels to the client: reads publish it as an `ETag`, edits
must send it back as `If-Match`, and the application compares it against the aggregate it just
loaded.

```mermaid
sequenceDiagram
    participant A as User A
    participant B as User B
    participant Api as API
    participant DB as SQL Server

    A->>Api: GET /Training/{id}
    Api-->>A: 200 + ETag "v1"
    B->>Api: GET /Training/{id}
    Api-->>B: 200 + ETag "v1"

    A->>Api: PUT If-Match "v1"
    Api->>DB: UPDATE ... WHERE RowVersion = v1
    DB-->>Api: 1 row
    Api-->>A: 200 + ETag "v2"

    B->>Api: PUT If-Match "v1"
    Api->>Api: loaded version is v2, expected v1
    Api-->>B: 412 Precondition Failed
```

Both guards are kept and layered: the comparison catches the cross-request case the store cannot
see, and the database token settles the race two concurrent requests can still lose between that
check and the update. `DbUpdateConcurrencyException` is translated in `UnitOfWork` into a
storage-agnostic `ConcurrencyConflictException`, so the application layer never learns that EF
Core or SQL Server are involved, and both paths surface the same `ConcurrencyConflict` error.

At the HTTP edge, a missing `If-Match` answers **428 Precondition Required** — an unconditional
write would let a caller overwrite changes they never saw — and a stale one answers **412
Precondition Failed**. Weak validators and `*` are rejected: both would let a caller through
without stating which version they read.

Both ends of that exchange are **declared in the OpenAPI document** — `If-Match` as a bound
`[FromHeader]` parameter, `ETag` as a response header written in by a transformer — and asserted by
`OpenApiDocumentTest` on both hosts. That is not documentation hygiene: while the header was merely
read off `Request.Headers`, it never reached the document, the generated client had no parameter for
it, and every edit from the front end came back 428 with nothing failing anywhere else. See
[ADR 0010](docs/adr/0010-declare-the-conditional-request-contract-in-the-document.md).

### Repositories, unit of work and specifications

Repositories return aggregates and only stage changes; nothing is written until
`IUnitOfWork.SaveChangesAsync` runs, which is the single commit point. `UnitOfWork` also
translates SQL Server's duplicate-key errors into `UniqueConstraintViolationException`, letting
the application turn a lost uniqueness race into an ordinary business failure without depending
on the provider.

**A specification names a business rule, or it does not exist** (ADR 0028). Each one lives in the
domain, beside the aggregate it speaks about, and answers two ways from a single statement:
`IsSatisfiedBy` in memory for a decision, `Criteria` as an expression for the repository
implementation that has to ask the database. `TrainingOwnedBySpecification` says who a training
answers to — worn by the aggregate as `Training.IsOwnedBy` — and
`TrainingTitleExistsForTrainerSpecification` is the data half of the uniqueness invariant the
`Training` aggregate enforces. What specifications are **not** here is a query DSL: they carry no
ordering and no paging (ADR 0001), repository contracts expose named questions rather than
specification-taking members, and the CQRS readers never touch one — their queries are SQL shaped
for a screen, and four architecture rules hold each of those lines.

A named question may now carry **named criteria** — the administrative listings filter by a state
and by a term ([ADR 0055](docs/adr/0055-let-the-administration-read-what-the-catalogue-may-not.md))
— and that is exactly where the fourth rule sits. A status is a value the adapter interprets; an
`Expression<Func<T, bool>>` is a query the caller wrote, and refusing the bare shape is what keeps
the line at *named criteria* from being the line at *anything*.

---

## Persistence

EF Core maps the model without letting persistence concerns leak into it:

- **A value object is persisted by its shape** (ADR 0032). One scalar converts on its column
  (`TrainingTitle`, `TrainingDescription`, `TrainingPrerequisites`, `AcquiredSkills`); several
  scalars — or an optional value — flatten as a complex property in the owner's table (`Name`,
  `Email`, `Bio` and `TrainerPhoto` on `Trainer`); a collection owns a relational side table
  (`Topic` in `TrainingTopic`), never a JSON column.
- **Typed identifiers convert to `Guid`** through a converter declared once in
  `AggregateRootTypeConfiguration`, alongside the key, the audit columns and the concurrency
  token — so a new aggregate inherits all of it.
- **Title uniqueness is a unique index** on `(TrainerId, Title)`. An application-level pre-check
  gives a clean error message; only the index makes the rule hold under concurrency.
- **The `DomainEvents` collection is ignored**, so a domain concern never reaches a column.

| Migration | What it does |
|---|---|
| `InitialCreate` | `Trainer`, `Training`, `TrainingTopic` |
| `AddUniqueTrainingTitlePerTrainer` | Unique index on `(TrainerId, Title)` |
| `MakeTrainerBioOptional` | `Bio` becomes nullable, with a data fix for existing rows |
| `RenameTrainerEmailToContactEmail` | Renames the column, preserving data |
| `AddAggregateRowVersion` | Adds the `rowversion` column to both aggregates |
| `UseFullTimestampPrecision` | `CreatedOn` / `ModifiedOn` go from `datetime2(2)` to `datetime2(7)` |
| `AddTrainerPhoto` | `PhotoId`, `PhotoContentType`, `PhotoByteSize` on `Trainer` |
| `AddOutbox` | The `OutboxMessage` table the integration events travel through |
| `AddOutboxLease` | Lease columns on `OutboxMessage`, so one worker delivers at a time |
| `AddOutboxBackoffAndRetention` | `NextAttemptOnUtc` and the delivered-rows index: the retry schedule, and the sweep's seek |
| `AddOutboxConsumerLedger` | The per-consumer delivery ledger: which consumers a message has reached, riding the envelope's lifecycle by cascade |

ASP.NET Identity lives in its own `DbContext` with its own migration.

Two interceptors run on every save: `DomainEventInterceptor` dispatches domain events before
persistence, and `AuditableEntitiesInterceptor` stamps `CreatedOn` and `ModifiedOn`.

---

## Security

Registration creates the Identity account and its trainer **atomically**, inside a
`TransactionScope` that is completed only when both succeed — so a failed trainer creation leaves
no orphan account behind.

Sign-in goes through `SignInManager.CheckPasswordSignInAsync` with lockout enabled, and answers
the same generic message whether the password is wrong or the account is locked, so the response
reveals nothing about account state.

Registration is deliberately not held to that standard: it answers `409` when a username or email
is already taken, and names which in the body. It always did — the `400` this replaced carried the
same `DuplicateEmail` code — so an account-enumeration oracle exists here by design and not by
accident. Closing it means changing what registration *says*, which is a decision of its own and
has not been taken.

The issued JWT carries the user's name, identifier and email, the roles the account holds, and —
when the account is somebody's trainer — the trainer's first and last name and a **`trainer_id`**
claim that lets the API resolve the caller's trainer without a lookup. `ICurrentUserService` reads
it.

**An administrator is an account, not a trainer** (ADR 0051), so those three claims are absent from
their token rather than empty. Three authorization policies follow, declared once in
`TrainingHub.Shared.Api` and registered by both hosts through a single `AddApiAuthorization`, so
neither can end up holding two of the three:

| Policy | Demands | Carried by |
|---|---|---|
| `TrainingOwner` | the caller owns the training the route names | five write actions |
| `Trainer` | the caller is somebody's trainer | `ApiControllerBase`, so every trainer action |
| `Administrator` | the `Administrator` role | `AdministrationControllerBase`, so the six administrative actions |

`TrainingOwner` checks ownership only: a training that does not exist lets the policy succeed so the
action can answer `404` rather than `403` — what the caller learns is that no training of theirs
carries that identifier, which is what they asked. It is also the only one of the three with a
handler of its own, ownership being a question only the database can answer; a role and a claim are
already in the token.

**The role is granted, never claimed.** There is deliberately no endpoint that grants one. In
Development a start-up seeder creates the configured account if it is missing and grants it the
role — `admin` / `admin` out of the box, and nobody's trainer, so its token carries no `trainer_id`.
Everywhere else the grant is a database operation, the same shape ADR 0003 chose for applying
migrations and for the same reason. See [the default administrator](#the-default-administrator) for
why a known credential is a fixture rather than a hole.

**The browser never holds that token.** The Blazor host is a backend for frontend: it signs the
user in by calling the API itself, keeps the JWT inside an encrypted `HttpOnly` cookie, and forwards
everything under `/api` to the API with the token attached server-side. The WebAssembly application
talks only to its own origin, asks `/bff/user` who the caller is, and has no credential to leak —
where it previously kept the JWT in `localStorage`, readable by any script on the page. The cookie
brings request forgery back, which `SameSite=Strict` and a required `X-Requested-With` header
answer. The reasoning, the alternatives — in-memory tokens, refresh tokens, Duende.BFF — and what
this does *not* protect against are in
[ADR 0009](docs/adr/0009-hold-the-access-token-in-the-bff-instead-of-the-browser.md).

The trainer endpoints need no policy of their own beyond `Trainer`, because none of them takes an
identifier and none of them destroys anything: reading and editing one's own profile are addressed
as `/Trainer/me` and resolve the trainer from the `trainer_id` claim. There is nothing to tamper
with. Deletion is absent by design — a trainer never deletes themselves, and the operation waits for
a use case the administrator can reach rather than being exposed under a weaker guard in the
meantime.

---

## API reference

Both hosts expose the same routes. Authentication is required everywhere except registration and
login.

Every authenticated route answers `401` without a body when no valid token is presented, and `403`
the same way — to a caller who is nobody's trainer, and, on the owner-only writes, to a trainer who
is not the owner. Both are declared in the document; neither carries a problem document, because
neither carries anything. See ADR 0011.

Every error that *does* carry a body carries the same one — an RFC 7807 problem document, served as
`application/problem+json`. A failure that broke a field names it under `errors`; a failure that
broke a business rule carries this API's own codes under `domainErrors`. See ADR 0012.

| Verb | Route | Notes |
|---|---|---|
| `POST` | `/Auth/register` | `201` with the new trainer's identifier and `Location: /Trainer/me`; `409` when the username or email is taken, `400` otherwise, both keyed by field |
| `POST` | `/Auth/login` | `200` with a JWT, or `401` — the same answer for an unknown username, a wrong password and a locked-out account |
| `GET` | `/Trainer/me` | The caller's own profile, with an `ETag`. `200`, `404` |
| `PUT` | `/Trainer/me` | Requires `If-Match`. `200`, `400`, `404`, `412`, `428` |
| `GET` | `/Trainer/{id}/photo` | The trainer's portrait, with a strong `ETag` and a year-long immutable cache. `200`, `304`, `404` |
| `PUT` | `/Trainer/me/photo` | `multipart/form-data`. Publishes **and** replaces. `200` with the updated profile, `400`, `404`, `409` |
| `DELETE` | `/Trainer/me/photo` | `204`, `404`, `409` |
| `POST` | `/Training` | `201` with the new identifier, `409` on a duplicate title, `400` when the catalogue is full (`Training.CatalogueFull`, at ten **published** trainings) or the content is invalid |
| `GET` | `/Training/my-trainings` | The caller's own trainings, newest first. Takes no identifier. One page on either host: `?page=` and `?pageSize=` (default 20, maximum 100), answered as `{ items, page, pageSize, totalCount, totalPages, hasNextPage, hasPreviousPage }` |
| `GET` | `/Training/{id}` | Owner only. `200` with an `ETag`, `400` on a malformed identifier, or `404` — including when the training exists but belongs to somebody else |
| `PUT` | `/Training/{trainingId}` | Owner only. Requires `If-Match`. `200` with the updated training and its new `ETag`, `400`, `403`, `404`, `409`, `412`, `428` |
| `DELETE` | `/Training/{trainingId}` | Owner only. `204`, `400`, `403`, `404` |
| `POST` | `/Training/{trainingId}/transfer` | Owner only. Hands the training to the recipient the body names when their catalogue allows it (ADR 0036). `204`, `400` (self, unknown, full or suspended recipient), `403`, `404`, `409` on the recipient's duplicate title |
| `POST` | `/Training/{trainingId}/unpublish` | Owner only. Withdraws the training from public view; it stays in the owner's own listing (ADR 0050). No body, no `If-Match`. `204`, `400`, `403`, `404`, `409` when it was already withdrawn |
| `POST` | `/Training/{trainingId}/publish` | Owner only. Offers a withdrawn training again. `204`, `400` when the owner is suspended or their catalogue is full, `403`, `404`, `409` when it was already published |
| `POST` | `/Administration/trainers/{trainerId}/suspend` | Administrator only. The body carries the reason. `204`, `400` when the reason is empty or over 500 characters, `404`, `409` when the trainer was already suspended |
| `POST` | `/Administration/trainers/{trainerId}/reinstate` | Administrator only. No body. `204`, `400`, `404`, `409` when the trainer was not under sanction |
| `POST` | `/Administration/trainings/{trainingId}/withhold` | Administrator only. The body carries the reason. Takes the training out of public view where its owner cannot put it back (ADR 0052). `204`, `400`, `404`, `409` when it was already withheld |
| `POST` | `/Administration/trainings/{trainingId}/release` | Administrator only. No body. Lifts the interdiction; the training lands on *unpublished*, and publishing is the owner's call again. `204`, `400`, `404`, `409` when it was not withheld |
| `GET` | `/Administration/trainers` | Administrator only. One page of trainers, newest first. `?status=` (`Active`, `Suspended`), `?search=` on the name or the contact address, `?page=`, `?pageSize=`. `200`, `400` when the status names nothing or the page is out of range |
| `GET` | `/Administration/trainings` | Administrator only. One page of trainings across every trainer, newest first. `?status=` (`Published`, `Unpublished`, `Withheld`), `?page=`, `?pageSize=`. No `?search=`: the title is a value-converted column EF Core cannot match a substring against, which [ADR 0055](docs/adr/0055-let-the-administration-read-what-the-catalogue-may-not.md) records. `200`, `400` |

Twenty-one endpoints, and not one of them lets a trainer reach what another trainer owns. The six
under `/Administration` act on somebody else's aggregate by design and are the only six that do —
behind a role that is granted by hand and by no endpoint at all (ADR 0051). They are grouped by the
authority they exercise rather than by the resource they act on, which is what that record says an
administrator is: a permission, not a context. There used to be
five more — `/Trainer/all`, `/Trainer/{id}`, `/Training/all`, `/Training/by-trainer/{id}` and
`/Training/by-topic/{topic}` — and between them they handed out every trainer's name, contact email
and bio to any authenticated caller, enumerable. Nothing in the application asked for them: the
front end reads the signed-in trainer's profile and that trainer's own trainings — and, on the two
administrative screens, every trainer and every training, which it asks for at `/Administration` and
is answered `403` anywhere else. They were removed rather than restricted, because a catalogue read
scoped to one caller is not a catalogue read.

**Two of them have come back, and the difference is the audience rather than the shape**
([ADR 0055](docs/adr/0055-let-the-administration-read-what-the-catalogue-may-not.md)). The two
`/Administration` listings serve the same columns `/Trainer/all` served — a name, a contact address
— to the one role that can act on them, and to nobody else: a trainer's token is answered `403` on
both, which is the whole of what makes them a different read. They are paged under the same cap as
every other list, filtered by a state the domain declares, and they exist because the four
administrative decisions take an identifier that nothing else hands out.

What could not be removed was locked instead. `GET /Training/{id}` is what the edit form loads and
what a creation's `Location` points at, so it stays — with the owner written into the query rather
than checked after it. A training belonging to somebody else answers `404`, not `403`: a `403`
would confirm that the identifier names something real, which is itself what is being withheld.

The photo is the one read addressed by identifier rather than by `me`, and deliberately so:
publishing a portrait is self-service, but looking at one is what a catalogue of trainers does. It
is already shaped for that day — making it public is `[AllowAnonymous]` and nothing else. Its cache
is aggressive because the address changes whenever the picture does: a replacement mints a new photo
identity, so the bytes under any one `ETag` genuinely never change, and a CDN can sit in front of
the route without a line moving. Writing is one verb, `PUT`, because there is no third thing to do
to a photo and because its idempotence makes a retried five-megabyte upload safe.

A body past the request size limit answers `400`, not `413`, and that is a finding rather than an
oversight. The limit does stop the server reading an arbitrary payload — which is the property
worth having — but a body-read failure inside model binding never reaches an exception handler:
MVC folds it into model state and answers with an unbound file. A handler was written to publish
`413` in this API's problem shape, the integration suite established that nothing ever calls it,
and it was deleted rather than left to suggest a status the API cannot produce. No `If-Match`:
nothing is being edited against a version the caller read, so a lost race answers `409` rather than
a `412` no precondition was asked for.

Trainers are created only through registration — there is no `POST /Trainer` — and no endpoint
deletes one. Removing a trainer is an administrative decision, and the role entitled to it now
exists (ADR 0051) while the endpoint still does not: what the administration got is suspension,
which is reversible, and a deletion is not. Nothing is exposed rather than something irreversible
exposed early. The two endpoints acting
on a trainer's own profile are addressed as `me` rather than by identifier, which is also where
registration's `Location` now points: the address of what was created, from its creator's side.

**The word changes where the thing changes.** `me` names an identity, and in this domain the
identity *is* a trainer — `/Trainer/me` reads as the trainer who is calling. Under `/Training` it
named nothing, a caller not being a training, and the route said `me` regardless until a second one
appeared: two endpoints ending in the same word rewarded a careless reading with the wrong one.
Hence `GET /Training/my-trainings`. The asymmetry is the result and not an oversight — one addresses
a resource by who it is, the other selects a collection by whose it is, and one word for both is
what made the first of them ambiguous.

### Health

Beside the thirteen operations, every host answers for its own health at two anonymous endpoints —
anonymous because their consumers are orchestrators and probes that hold no token, and because the
body carries nothing worth one:

- `GET /health/live` runs no checks: a `200 Healthy` means the process is up and routing, which is
  all a container restart decision should ever read.
- `GET /health/ready` runs five probes — the database, a signed single-key read of the object
  store, an SMTP connect-and-quit, the outbox's poison gauge, and the pending migrations of both
  contexts — and answers `{ "status": …, "checks": [{ "name": …, "status": … }] }`. Names and
  statuses, nothing else: no description, no exception, no duration ever leaves on this route, and
  a unit test holds the writer to that. `Degraded` means poison messages are waiting for an
  operator while the host still serves; the failure of any other probe is `Unhealthy`.

  The schema probe is what makes ADR 0003 enforceable rather than merely logged: outside
  Development a host applies no migration, and one whose database is behind now stops receiving
  traffic instead of announcing it to a log nobody is reading. It re-reads on every poll and caches
  nothing, so readiness goes green on its own once the schema is applied out of band — no restart.
  See [ADR 0045](docs/adr/0045-fail-readiness-while-a-migration-is-pending.md).

The pair is wired once in `Shared.Api` (`AddApiHealth`/`MapApiHealth`), so neither API host can
answer less than the other; the BFF, whose world is the layered API, answers `/health/live` only.
The serving surface ships entirely in the ASP.NET Core shared framework and costs no package.

In **Development only** — the same bargain as Scalar's reference UI — each API host also serves a
dashboard at `/healthchecks-ui` (the Xabaril `AspNetCore.HealthChecks.UI` packages, the one place
they may be named): a page polling the same five probes every ten seconds through its own
detailed endpoint, `/health/ui`, whose richer body — descriptions, durations, exceptions — is
exactly what stays off the anonymous production surface. Its history is in-memory and forgets on
restart, deliberately. See
[ADR 0037](docs/adr/0037-answer-for-the-hosts-health-at-two-endpoints.md).

---

## Tech stack

| Package | Role |
|---|---|
| `Microsoft.EntityFrameworkCore.SqlServer` | Persistence, complex properties, owned collections, value conversions, `rowversion` concurrency token |
| `Mediator` (`Mediator.Abstractions` + source generator) | Source-generated dispatch for domain events, commands and queries — no reflection at runtime |
| `FluentValidation` | Request validation in the CQRS stack, wired as a pipeline behaviour |
| `EmailValidation` | Email format checking inside the `Email` value object |
| `Microsoft.AspNetCore.Identity.EntityFrameworkCore` | User accounts, password hashing, lockout |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | Bearer token authentication |
| `Microsoft.AspNetCore.OpenApi`, `Scalar.AspNetCore` | The OpenAPI document and its reference UI |
| `MudBlazor` | Component library of the Blazor WebAssembly front end |
| `Serilog.AspNetCore` | The API hosts' logging — console and rolling text files, tuned by the typed `ApiLogging` options ([ADR 0026](docs/adr/0026-log-with-serilog-to-console-and-files-through-typed-options.md)) |
| `AspNetCore.HealthChecks.UI`, `.UI.Client`, `.UI.InMemory.Storage` | The health dashboard at `/healthchecks-ui`, Development only — the probes it watches stay hand-rolled ([ADR 0037](docs/adr/0037-answer-for-the-hosts-health-at-two-endpoints.md)) |
| `Yarp.ReverseProxy` | The BFF's proxy — forwards `/api` to the REST API and attaches the access token from the session cookie |
| `bunit` | Renders a Blazor component in-process, so the profile page's client-side decisions are tested rather than only clicked |
| `xunit`, `AwesomeAssertions`, `Moq` | Testing — `AwesomeAssertions` is the Apache 2.0 community fork of FluentAssertions, whose 8.x line moved to a commercial licence |
| `NetArchTest.eNhancedEdition` | The engine of the dependency half of the architecture rules — the maintained fork of NetArchTest, which is how the records become the executable rules in `TrainingHub.Architecture.Tests` |
| `Microsoft.EntityFrameworkCore.InMemory` | A `DbContext` without a server, for the unit-side tests that need EF's change tracker but not SQL Server — and, pinned to the EF 10 build, the provider the health dashboard's store runs on |
| `AWSSDK.S3` | The object store photos live in — pointed at a SeaweedFS container locally, and at any S3-compatible provider by configuration |
| `MailKit` | The SMTP client the emails leave through — pointed at a Mailpit container locally, and at any relay by configuration ([ADR 0031](docs/adr/0031-send-email-over-smtp-and-prove-it-against-a-real-server.md)) |
| `Testcontainers`, `Testcontainers.MsSql` | A real SQL Server, a real object store and a real mail server per integration test run |
| `Respawn` | Database reset between integration tests |

Versions are managed centrally in `Directory.Packages.props` — projects reference packages
without a version attribute, every version is exact, and transitive pinning is enabled.

---

## Getting started

### Prerequisites

- **.NET SDK 10**
- **Docker** — for SQL Server, the object store and the mail server, and required by the integration tests

### Run the dependencies

```bash
docker compose up -d sqlserver seaweedfs mailpit
```

This starts SQL Server 2022 on port `1433`, SeaweedFS on `8333` (its S3 endpoint) and `9333`
(the master's own UI), each with a named volume, and Mailpit on `1025` (SMTP) and `8025` — every
email the hosts send is readable at <http://localhost:8025>. SeaweedFS rather than MinIO, whose community
repository was archived in April 2026 and publishes no binaries; both speak S3, and the API talks to
whichever through `AWSSDK.S3`, so the provider is four configuration values rather than a rewrite.
The bucket is created at startup in `Development`, in the same spirit as the migrations below. See
[ADR 0021](docs/adr/0021-store-a-photo-beside-the-row-that-names-it.md). `docker compose up` also builds
and runs the layered API on <http://localhost:5085> — but nothing in CI builds that image, so
treat a `docker build` as the check rather than the guarantee. It went unbuildable once already:
the restore stage stopped copying two files it needs, and the README said this sentence throughout.
Since ADR 0037 that container answers for itself the way its three dependencies always have: the
compose file polls its `/health/live`, so `docker compose ps` shows the API as `healthy` rather
than merely running.

### Run an API

```bash
dotnet run --project src/DDD/Api            # https://localhost:7249
dotnet run --project src/DDDWithCqrs/Api    # https://localhost:7048
```

**In `Development`**, both hosts apply their EF Core migrations at startup, for the business and the
Identity databases alike, so no manual `dotnet ef database update` is needed — and each host serves
its OpenAPI document at `/openapi/v1.json`, a Scalar reference UI alongside it, and the health
dashboard at `/healthchecks-ui`.

Everywhere else they apply nothing: the schema is brought up to date out of band, as a step of the
release, and startup only reports whether any migration is pending. Migrating from the process that
serves requests means concurrent instances racing on DDL, standing DDL rights for the application,
and a schema change that stopping the process cannot undo. See
[ADR 0003](docs/adr/0003-apply-migrations-on-startup-in-development-only.md).

The Blazor front end runs with:

```bash
dotnet run --project src/Web/TrainingHub.Blazor/TrainingHub.Blazor   # https://localhost:7067
```

It needs the layered API above to be running, since it forwards to it. **HTTPS is not optional
here**: the session cookie is `Secure` and uses the `__Host-` prefix, so it is simply not set over
plain HTTP and sign-in would appear to do nothing. There is only an `https` launch profile for that
reason.

### Configuration

Each API expects:

| Key | Purpose |
|---|---|
| `ConnectionStrings:TrainingContext` | SQL Server connection, used by both the business and Identity contexts |
| `Jwt:Key` | Signing key. The host **fails fast at startup** with an explicit message when it is missing |
| `Jwt:Issuer`, `Jwt:Audience` | Token validation parameters |
| `Jwt:ExpireMinutes` | Token lifetime |
| `Cors:AllowedOrigins` | Origins allowed to call the API from a browser. Absent or empty means no cross-origin caller is accepted, and the host logs a warning at startup |
| `ObjectStorage:ServiceUrl` | The S3 endpoint photos are stored at. The host **fails fast at startup** when it is missing |
| `ObjectStorage:BucketName` | The bucket they go in. Also required at startup |
| `ObjectStorage:AccessKey`, `ObjectStorage:SecretKey` | Credentials. They must match an identity in `docker/seaweedfs-s3.json`: started without that file SeaweedFS accepts anonymous requests and **refuses signed ones**, so an SDK — which signs everything — gets a 500 per upload from a container that reports itself healthy |
| `ObjectStorage:CreateBucketOnStartup` | Creates the bucket when absent. On for the local container, which comes up empty; off elsewhere, where a bucket is provisioned once by whoever owns the account |
| `Smtp:Host`, `Smtp:Port`, `Smtp:SenderAddress` | The mail server the outbox consumers deliver through, and the identity messages are sent as. All three **fail fast at startup** when missing ([ADR 0031](docs/adr/0031-send-email-over-smtp-and-prove-it-against-a-real-server.md)) |
| `Smtp:SenderName`, `Smtp:Username`, `Smtp:Password`, `Smtp:UseStartTls` | Optional: a display name, credentials for a relay that wants them (they travel as a pair or not at all), and STARTTLS for one reached across a real network. The local Mailpit container needs none of them |
| `ApiLogging:*` | The Serilog pipeline both hosts share: `Path`, `RollingInterval`, `RetainedFileCountLimit`, `MinimumLevel`, `LevelOverrides`, `WriteToFile`. Every key has a working default — a host with no section logs to the console and to daily files under `logs/` ([ADR 0026](docs/adr/0026-log-with-serilog-to-console-and-files-through-typed-options.md)) |
| `Outbox:*` | How eagerly each host's delivery worker drains the outbox, and how patiently it retries: `PollInterval`, `BatchSize`, `MaxAttempts`, `LeaseDuration`, `RetryDelay` — the base of the doubling schedule a failed attempt books its next try on — and `RetentionPeriod`, past which delivered rows are swept while poison stays for the operator. Every knob has a working default (5 s, 20, 5 attempts, 30 s, 30 s, 14 days) and all fail fast at startup when non-positive ([ADR 0025](docs/adr/0025-deliver-the-outbox-with-a-hosted-service-in-each-host.md), [ADR 0033](docs/adr/0033-back-off-between-retries-log-the-poison-and-sweep-the-delivered-history.md)) |

#### Local overrides

Every host — both APIs and the Blazor BFF — loads an optional `appsettings.Local.json` from its
project directory **after every other source**, environment variables included, so whatever it
says wins ([ADR 0035](docs/adr/0035-give-every-developer-a-git-ignored-local-overrides-file.md)).
The file is git-ignored and excluded from the Docker build context: it is the preferred place for
anything per-developer — a local connection string, real SMTP credentials, different CORS
origins — and the only local channel that reaches `dotnet ef` at design time, which runs with no
environment set and therefore never reads `appsettings.Development.json`. The integration suites
deliberately ignore it, so a local override never changes what the tests prove.

Supply keys through `appsettings.Local.json` (preferred), user secrets, or environment
variables — the `docker compose` service passes them as `ConnectionStrings__TrainingContext`,
`Jwt__Key` and so on.

#### The default administrator

There is one already, and it needs no setup:

| | |
|---|---|
| Username | `admin` |
| Password | `admin` |

A start-up seeder creates the account if it is missing and grants it the `Administrator` role,
reading `Administrator:Username` and `Administrator:Password` from the committed
`appsettings.Development.json`. Name another username in `appsettings.Local.json` to override it; an
account that already exists **keeps its password**, because this is a seeder and not a reset.

**It is a fixture, not a secret, and the environment gate is the whole of its safety.** The seeder
runs in **Development only** — the shape [ADR 0003](docs/adr/0003-apply-migrations-on-startup-in-development-only.md)
chose for applying migrations, and for the same reason — and two independent things must both hold
before it creates anything: the host runs as Development, and a configuration section that exists in
no other committed file names a password. Everywhere else the grant is a documented database
operation, and there is deliberately no endpoint that grants a role
([ADR 0051](docs/adr/0051-give-the-administrator-authority-not-a-context.md)).

It refuses nothing either: a password Identity rejects, or a username with no password to create it
from, is reported in the log and the host starts anyway.

**`admin` is nobody's trainer** — that is the point of it, not an omission. Its token carries no
`trainer_id`, so the trainer endpoints answer it `403`. What it can reach is its own: signing it into
the Blazor front end shows two navigation entries nobody else sees, `/administration/trainers` and
`/administration/trainings`. The same authority is reachable through the API, `/scalar/v1` in
Development being the quickest way in.

#### Sending real email

Locally every message ends in **Mailpit**, and Mailpit is a sink: it accepts anything and relays
nothing, which is what makes registering trainers safe on a development machine. Delivery to a
real inbox is a relay choice, not a code change — that is the claim
[ADR 0031](docs/adr/0031-send-email-over-smtp-and-prove-it-against-a-real-server.md) makes — so
point the `Smtp` section at a transactional relay instead. With [Brevo](https://www.brevo.com)'s
free tier as the worked example (sign up, verify a sender address, generate an SMTP key — no
domain, no DNS):

```jsonc
// src/DDD/Api/appsettings.Local.json — git-ignored, so the key never leaves the machine
{
  "Smtp": {
    "Host": "smtp-relay.brevo.com",
    "Port": 587,
    "UseStartTls": true,
    "SenderAddress": "the-address-you-verified",
    "Username": "your-brevo-login",
    "Password": "<the SMTP key>"
  }
}
```

Or, for secrets kept outside the working tree, the user-secrets alternative:

```bash
cd src/DDD/Api
dotnet user-secrets init
dotnet user-secrets set "Smtp:Host" "smtp-relay.brevo.com"
dotnet user-secrets set "Smtp:Password" "<the SMTP key>"
# …and the remaining Smtp keys the same way.
```

Either way, never `appsettings.Development.json`: this repository is public and an SMTP key
committed to a public repository is harvested within minutes. Run the host, register a trainer,
and the welcome email arrives for real — check the junk folder, where an unknown sender's first
message often lands. Any relay speaking SMTP works the same way; Brevo is only the example
because its free tier asks for nothing beyond a verified sender address.

The Blazor **host** expects one key of its own:

| Key | Purpose |
|---|---|
| `Api:BaseAddress` | Address of the REST API the BFF forwards to |

It lives in the host's `appsettings.Development.json` and **never reaches the browser**. The
WebAssembly application has no API address at all: it calls the origin that served it, and the host
decides what sits behind `/api`. That is the visible half of
[ADR 0009](docs/adr/0009-hold-the-access-token-in-the-bff-instead-of-the-browser.md) — the front end
cannot be pointed at the wrong backend, because it is not pointed at one. Like the API settings
above, the value sits in the environment-specific file — or in the host's own
`appsettings.Local.json` when a developer's backend lives elsewhere: a `localhost` address is a
development fact, and the host fails fast with an explicit message when the key is missing rather
than falling back to a default that would be wrong in production.

---

## Testing

```bash
# Unit tests — no infrastructure required
dotnet test --filter "FullyQualifiedName!~IntegrationTests"

# Integration tests — requires Docker
dotnet test --filter "FullyQualifiedName~IntegrationTests"
```

The two filters are exact inverses, so between them every test runs exactly once.

| Project | Scope |
|---|---|
| `TrainingHub.Shared.Domain.Tests` | Aggregates, value objects, typed identifiers, `Result`, specifications, the page vocabulary's bounds and arithmetic |
| `TrainingHub.DDD.Application.Tests` | Application services, factories, mappers, domain event handlers — including the eleven that translate a domain event into an integration event — and the fourteen post-commit consumers |
| `TrainingHub.DDDWithCqrs.Tests` | Command handlers, validators, pipeline behaviours |
| `TrainingHub.Shared.Api.Tests` | Entity-tag encoding and parsing, the guard that keeps client generation away from a database, what the unhandled-exception handler is allowed to tell a caller, and the transformer that describes an uploaded file inline so a client generator recognises it as one |
| `TrainingHub.Shared.Infrastructure.Tests` | The auditable-entities interceptor — that it stamps, and reads the clock once per entity —, the outbox publisher observed through the change tracker, the serializer's round trip for every registered event, the dispatcher held to its routing table, the envelope's state transitions, the bucket bootstrapper, mostly for when it does nothing, and — over SQLite rather than a substitute — the names a page of trainings asks for by identifier |
| `TrainingHub.Blazor.Bff.Tests` | The backend for frontend over HTTP: the cookie's flags, the forgery guard, the token attached to a forwarded call, and what signing out revokes |
| `TrainingHub.Blazor.Client.Tests` | The front end, rendered in-process with bUnit: the sign-in page's refusal to redirect anywhere but a path of its own origin, the deep link a redirect to sign-in preserves, the header that makes a cookie-authenticated call unusable as a forgery, an unreachable BFF read as anonymous rather than as an exception, the per-field messages read out of a problem document, the training form's bounds tied to the ones the generated contract publishes, and — on the profile page — the size ceiling that refuses a file before it is uploaded, the image address that defeats a year-long cache, and the server's refusal shown in its own words. The administrative pages are here too: the coordinates each listing owns and the criteria it forwards unchanged, the reason a dialog collected reaching the call that carries it, the lifting that asks for no reason at all, and the training row that names its owner rather than showing an identifier |
| `TrainingHub.DDD.Api.IntegrationTests` | The layered host, HTTP end to end against a real SQL Server and a real object store |
| `TrainingHub.DDDWithCqrs.Api.IntegrationTests` | The CQRS host, same treatment |
| `TrainingHub.Architecture.Tests` | The decisions themselves: the dependency rule, the CQRS shape, the modelling conventions, and a rule that fails when a record is defended by nothing — see [ADR 0013](docs/adr/0013-make-every-record-answer-to-a-test.md) |
| `TrainingHub.Api.TestKit` | Not a test project: the fixtures both integration suites share |

No test count is quoted here on purpose: a `[Theory]` expands to as many cases as it has rows, so
the only honest figure is the one the two commands above print, and a figure written down goes
stale on the next commit that adds a test.

The integration tests start SQL Server — and beside it a real object store and a real mail
server — through **Testcontainers**: no manual setup, no shared environment. **Respawn** empties
the database before each test, so every one of them starts
from a known state. The test host wires the same EF Core interceptors as production, so domain
events really are dispatched: the trainer-deletion cascade is asserted on both hosts by
`DomainEventPipelineTests`.

**Both stacks are covered, and almost entirely over HTTP** — nearly every assertion crosses
routing, model binding, JWT authentication, the `TrainingOwner` policy and the shared exception
handlers. Three go further down: `DomainEventPipelineTests` resolves the repositories and the unit of
work from the host's container, because the cascade it proves lost its endpoint when trainer
deletion left the API and a pipeline nothing exercises is a pipeline nobody notices breaking;
`TimestampPrecisionTests` reads the stored rows directly, because what it is about is what the
column kept, which no response can show; and `OutboxTests` drives a lost optimistic-concurrency
race through two service scopes, because the atomicity it proves — a failed save taking its staged
outbox row down with it — cannot be shown over HTTP: the stale-`If-Match` path is refused before
any event is raised. The same suite watches the delivery worker do its actual job against the real
database: a committed fact gets processed and stamped, and a message nobody can read spends its
attempt budget, keeps its error, and stops the line for no one. `EmailTests` follows two of those
facts one hop further: the welcome message and the address-change warning are read back out of a
real Mailpit container through its HTTP API, subject, recipient and wording intact — the proof
that the SMTP adapter ADR 0031 introduced actually delivers. Validation is where the two suites still differ, though far
less than they did: an invalid field on the layered host is caught by the value objects, while on the
CQRS host a FluentValidation validator inside `ValidationPipelineBehavior` catches it first. Both now
answer the same shape — a `domainErrors` document — since that behaviour returns a failed `Result`
rather than throwing; what differs is the code inside it, because two different layers judged.

`TrainingHub.Api.TestKit` holds the shared fixtures — the Testcontainers host, the Respawn
checkpoint, the registration and conditional-request helpers — generic over the entry point.
Only the `Program` type differs between the two suites.

**It also holds the facts themselves, and that is the point of it.** A fact about the API is
written once as an abstract `*Test<TFactory>` and run twice, so the two hosts cannot answer it
differently without one of them going red; each suite carries a one-line derivation and nothing
else. `TrainerProfileTest` and `TrainingLifecycleTest` were the last two that were not shared —
twenty-one facts each suite spelled out for itself, already drifting in what they checked rather
than in what they expected. What a suite still declares on its own is a fact about *that host*: on
the CQRS side, that a message its validator refuses never reaches the aggregate.
`NoFact_IsAssertedByBothSuitesInTheirOwnCode` is what keeps a copy from reappearing — the three
older rules all read outward from the kit and could not see a fact that lived in both suites and in
neither.

**The BFF suite needs no infrastructure at all**, which is why it sits with the unit tests. It hosts
the real `Program.cs` — pipeline order included — and replaces only the far side of the proxy, with
a handler that records what the API was sent. Cookie authentication, the forgery guard, the
authorization on the proxied route and the token transform are the production ones. That is what
lets it assert the things [ADR 0009](docs/adr/0009-hold-the-access-token-in-the-bff-instead-of-the-browser.md)
claims and the code cannot show: the response to a sign-in carries a cookie and never the token, a
call arriving without the application's marker header is refused even when it carries a valid
session, and signing out revokes access rather than merely forgetting it.

---

## Continuous integration

| Workflow | Trigger | What it runs |
|---|---|---|
| `ci.yml` | Push on `master` and on `claude/**`, pull request on `master` | Regenerate and commit the HTTP client, build in Release, unit tests |
| `integration-tests.yml` | Push on `claude/**`, manual dispatch, nightly at 03:17 UTC | The integration tests, naming every failed test as an annotation and publishing the TRX report as an artifact |
| `sonar.yml` | Push on `master`, pull request on `master` | Static analysis and coverage, reported to SonarQube Cloud |

The whole solution is built by both — including the integration test project, so a project that
no longer compiles fails the pipeline even when its tests are not run. `integration-tests.yml`
declares `permissions: contents: read`; `ci.yml` needs `contents: write` for one step, and one
only — the client commit described below.

**One run per commit.** A branch of this repository fires both `push` and `pull_request` for the
same commit once a pull request is open, and the two land in different concurrency groups, so
neither cancels the other: the same build was being paid for twice to answer the same question.
`ci.yml` therefore skips its pull-request run when the head branch belongs to this repository —
the push run has already built that commit and posted the check. What the `pull_request` trigger is
kept for is the one case `push` cannot reach: **a fork**, whose pushes fire nothing here. The other
two workflows never doubled — `sonar.yml` pushes only on `master`, and `integration-tests.yml` has
no pull-request trigger at all.

**CI writes the HTTP client.** On a push it regenerates the client from the API's own OpenAPI
document and commits the result, so a controller change carries its client with it. The API is the
source of truth and consumers are expected to follow — a regenerated client can rename a type and
break whatever calls it, which is accepted rather than prevented. See
[ADR 0008](docs/adr/0008-generate-the-http-client-from-a-script-and-verify-it-in-ci.md).

The integration suite stays off the pull-request path — it starts a real SQL Server through
Testcontainers — with one exception: pushes to an agent branch run it. The GitHub App an agent acts
through has no `actions: write` and cannot dispatch a workflow, so without that trigger its work
reached review having never met a database.

### Static analysis

`sonar.yml` reports to [SonarQube Cloud](https://sonarcloud.io): coverage, security hotspots,
duplication and cognitive complexity — the things a rule cannot state as yes or no, and which the
architecture suite therefore cannot check. It runs in a workflow of its own so that `ci.yml` stays
fast and keeps its `contents: write` to itself. It runs the whole test suite, integration tests
included, because a coverage figure produced without them would be false: this repository's
assertions mostly cross the HTTP boundary. See
[ADR 0017](docs/adr/0017-measure-what-the-rules-cannot-with-sonarqube-cloud.md).

**Until the repository is connected, the analysis is skipped rather than failed.** A guard job reads
`SONAR_TOKEN`; when it is absent the analysis job never starts, so a repository without the secret
shows a neutral skip instead of a red check about a missing secret.

**What is measured, and what is not.** Two families are excluded, and both because nobody writes
them: the generated HTTP client, which NSwag rewrites on every API change, and the EF Core
migrations — together about a fifth of everything that would otherwise be counted as production
code. Their snapshots are
near-identical by construction, so they would decide the duplication figure; nothing hand-written
covers them, so they would decide the coverage figure; and an issue raised in either is fixed by
regenerating, never by editing. Test projects need no exclusion — the .NET scanner recognises them
and keeps them out of the coverage denominator on its own, architecture tests included.

**One measure is narrowed rather than one family excluded.** The two host projects — `src/DDD/Api`
and `src/DDDWithCqrs/Api` — are named in `sonar.cpd.exclusions`, which removes them from the
*duplication* detector and from nothing else: their bugs, hotspots and coverage are read like any
other code, and they are the files every request passes through. What is exempt is the repetition,
because this repository publishes one API from two implementations and two rules require the two
copies to declare the same operations and the same shapes — so an endpoint's routing attribute,
`[ProducesResponseType]` run and action signature exist twice, identically, by decision. A rule
derives that list from the hosts and fails if it grows past them, so the setting cannot become a
place to file an inconvenient figure. See
[ADR 0049](docs/adr/0049-measure-duplication-where-repetition-is-a-defect.md).

Test *results* are published alongside coverage, from the same run: `--logger trx` feeds
`sonar.cs.vstest.reportsPaths`, so the dashboard reports how many tests ran and how long they took,
not only which lines they reached.

#### One-time setup

Everything below is done by hand, once, outside the repository.

1. Sign in to [sonarcloud.io](https://sonarcloud.io) with GitHub and install the **SonarQube Cloud**
   GitHub App on this repository. The app is what decorates a pull request — one summary comment,
   edited in place rather than repeated per run, and a `SonarCloud Code Analysis` check. The
   workflow alone cannot post either.
2. Import the repository as a project. Note the two keys it shows — they must match what
   `sonar.yml` passes on `begin`:

   | Setting in `sonar.yml` | Value | Where to read it |
   |---|---|---|
   | Organization (`/o:`) | `9c1eb57d24115cbbd103219f` | the organization URL: `sonarcloud.io/organizations/<this>/projects` |
   | Project key (`/k:`) | `maxime-poulain_BLRefactoring` | the project URL: `sonarcloud.io/project/overview?id=<this>` |

   Neither is a secret: a key names something, it does not authorise anything. Both therefore sit in
   the workflow, in the open, where a reader can see what is analysed and where it is reported. Only
   the token is a secret. Note that the organization key is a generated string while the project key
   is the `owner_repo` form — the second is what a GitHub import produces, so the project is bound
   to this repository and pull-request decoration works.

   The project key still spells the name this repository carried before
   [ADR 0022](docs/adr/0022-name-the-repository-after-the-domain-it-serves.md), and that is
   deliberate rather than missed. A SonarCloud project key is immutable: the only way to change it
   is to delete the project and import it again, which discards every measurement taken against the
   old one — the coverage history and the baseline the new-code condition is compared with. The
   binding to the repository is by key, not by name, so it survives a repository rename untouched.

3. In the project's **Administration → Analysis Method**, turn **Automatic Analysis off**. It and
   the CI-based analysis are mutually exclusive, and leaving it on makes the workflow fail with a
   message that does not say so.
   A project created by hand rather than imported carries no binding to the repository, and without
   one the analysis still runs and reports — it simply decorates no pull request, which reads as the
   app being broken rather than as an unset field. **Administration → General Settings → DevOps
   Platform Integration** is where to check.
4. Generate a token under **My Account → Security** — a *Project Analysis Token*, scoped to this
   project, rather than a user token whose leak would carry a whole account. Add it under
   **Settings → Secrets and variables → Actions → New repository secret**, named `SONAR_TOKEN`.

   That is the only thing to configure. `GITHUB_TOKEN` is provided by Actions, and the two keys are
   in the workflow. Until the secret exists the analysis job is skipped rather than failed.

#### Making the gate block a merge

`sonar.yml` waits on the gate **for a pull request only**, so the job goes red where going red stops
something — a change about to enter `master`. On a push to `master` the analysis still runs and
still publishes its verdict, but does not fail the build: the code is already in, so a red cross
there prevents nothing and only makes the default branch look broken. The verdict is not lost, it
lives on the branch's dashboard and badge. See
[ADR 0018](docs/adr/0018-fail-on-the-gate-where-failing-stops-something.md).

To stop a failing pull request being merged, add a check to branch protection:
**Settings → Branches → Add rule** on `master`, tick **Require status checks to pass before
merging**, and select **`Analyze`**. The app's own `SonarCloud Code Analysis` check carries the same
verdict and would do as well; `Analyze` is named here because it fails for a broken *analysis* too —
a bad token, an unreachable server, an unparseable coverage report — not only for a failed gate.

Do that *after* the first analysis of `master` comes back green. The default gate requires 80%
coverage on new code, and switching the rule on before a baseline exists blocks every pull request
on a number nobody has seen yet. Note that a pull request touching no product code passes that
condition at 0.0% — there is nothing to cover, so it does not apply. The figure that matters is the
one the analysis of `master` produces.

---

## Repository conventions

- **Central package management.** Every NuGet version lives in `Directory.Packages.props`; no
  project carries a `Version` attribute and no version is a wildcard. One project carries a
  `VersionOverride`, which is the exception that has to be stated rather than discovered:
  `TrainingHub.Blazor.Client.Tests` raises the ASP.NET Core Components family to 10.x for itself,
  because bUnit's net10.0 assets require it while the Blazor projects target net9.0 and the central
  pin follows them. Moving those projects to net10.0 removes the override and this sentence with it.
- **Shared MSBuild properties.** `Nullable` and `ImplicitUsings` are enabled solution-wide from
  the root `Directory.Build.props`; target frameworks stay per-project.
- **Code style** is described in `.editorconfig`: file-scoped namespaces, `var`, Allman braces,
  naming conventions, and a hundred and sixty-one analyzer severities — all of them enforced at build
  time, including the formatting ones.
- **Line endings** are normalised to LF by `.gitattributes`, in the repository and the working
  tree, whatever the contributor's platform.
- **Commits** are imperative one-liners, squash-merged from a pull request.
- **Assertions are AwesomeAssertions**, in every test project including the shared test kit.
  `subject.Should().Be(…)` rather than `Assert.Equal(…)`: a failure names the subject and the
  expectation, where xUnit's message names neither. The licence question behind the choice — and
  why not Shouldly — is in [ADR 0007](docs/adr/0007-assert-with-awesomeassertions.md).
- **Architecture decision records** live in [`docs/adr/`](docs/adr/), one numbered file per
  decision, each recording the alternatives and why they lost. A decision that changes gets a new
  record superseding the old one; merged records are not rewritten. What that protects is the
  reasoning — the options that were open and why the loser lost — and not the identifiers the
  reasoning happens to mention: when the repository was renamed, the project names inside every
  record were renamed with it, because a record pointing at a project that no longer exists cannot
  be read at all. See [ADR 0022](docs/adr/0022-name-the-repository-after-the-domain-it-serves.md).
- **The name this repository used to carry is gone, and a rule keeps it gone.**
  `NothingInTheRepository_StillCarriesTheFormerName` scans every file — source, workflows, compose,
  the records, this one — and permits the former name in one position only: immediately after
  `maxime-poulain_`, which is the SonarCloud project key and cannot be renamed from here. The name
  itself is written down in exactly two places, and
  [ADR 0022](docs/adr/0022-name-the-repository-after-the-domain-it-serves.md) is one of them.
- **The build fails on a warning.** `.editorconfig` sets a hundred and sixty-one analyzer rules on
  purpose, and `Directory.Build.props` turns a warning into an error, so the severities written
  there are rules rather than preferences — an architecture rule checks that they stay that way.
  Every rule is either enforced or demoted with the argument for lowering it written beside it;
  there is no third category, and that too is a rule rather than a promise. EF Core migrations are
  exempt, and the generated HTTP client with them, because nobody writes either.
  See [ADR 0019](docs/adr/0019-enforce-the-ruleset-this-repository-already-declared.md), which
  records the census that made enforcing them safe — including that the ruleset had been declaring
  a rule twice and silently demoting it — and
  [ADR 0020](docs/adr/0020-declare-every-rule-this-codebase-already-satisfies.md), which measured
  the four hundred rules nobody had asked about and declared the sixty this codebase already
  satisfied for nothing.

---

## Licence

[MIT](LICENSE).

