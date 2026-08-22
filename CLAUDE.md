# CLAUDE.md

This repository is a showcase project: it exists to demonstrate professional engineering practice,
not to maximize feature delivery. Architectural consistency, readability and long-term evolvability
outrank shipping speed. Understand the existing design before changing it.

## Read first, in this order

1. `README.md` — the architecture, the domain model, the conventions.
2. `docs/adr/README.md` — the index of 97 architecture decision records.
3. The records relevant to what you are touching.
4. `tests/TrainingHub.Architecture.Tests/` — the same decisions as 242 executable rules. Often
   faster than reading prose: each rule names the record it defends and quotes it.
5. The existing implementation.

ADRs are the source of truth. If the implementation contradicts an accepted record, that is a defect
— unless a later record explicitly supersedes it.

## Commands

```bash
dotnet build TrainingHub.slnx --configuration Release          # zero warnings, or it is a failure
dotnet test  TrainingHub.slnx --filter "FullyQualifiedName!~IntegrationTests"   # no Docker needed
dotnet test  TrainingHub.slnx                                  # everything; needs Docker
./scripts/generate-clients.sh                                  # after any change to the API surface
./scripts/start-dependencies.sh                                # the 4 dependencies alone; hosts run from the IDE
docker compose --profile full up -d --build                    # the whole stack: 4 dependencies, 3 hosts
```

The bare `docker compose up` is the developer's command: the three host services sit behind the
`full` profile, so it starts SQL Server, SeaweedFS, Mailpit and the Aspire Dashboard alone and
builds nothing (ADR 0075). The full profile builds an image per host and starts all seven
containers (ADR 0065). The BFF
container needs the developer's TLS certificate at `docker/https/traininghub.pfx` — one
`dotnet dev-certs` command, in the README — because its session cookie is `__Host-` prefixed and a
browser stores none over plain HTTP. Named here because the container fails to start without it;
the dependencies-only workflow needs no certificate at all.

## Traps that cost a CI round-trip

- **An incremental build skips analyzers.** A local "0 warnings" on a project MSBuild considers up
  to date proves nothing. Before trusting it: delete every `bin/` and `obj/`, then rebuild in
  Release. An unused `using` (IDE0005) is an *error* here, and that is how one reaches CI.
- `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are on. XML documentation is required on
  every public member (CS1591).
- The integration suites need Docker. Without it, run the filtered command above and say plainly
  which suites did not run — never report a suite green that never started.
- Both API hosts must publish the same operations. Every endpoint is written twice, in
  `src/DDD/Api/` and `src/DDDWithCqrs/Api/` (`BothHosts_PublishTheSameOperations`).
- The README's mermaid graph is compared edge by edge with the project references. Changing a
  `ProjectReference` means updating the diagram in the same commit.
- Every NuGet version lives in `Directory.Packages.props`; a `PackageReference` never carries
  `Version`. A new entry carries the comment that file's convention requires.
- Never set `RootNamespace` or `AssemblyName`. A namespace is the csproj file name followed by the
  folders (`EveryNamespace_AgreesWithItsFolder`).
- `src/TrainingHub.GeneratedClients/Clients.Generated.cs` is generated. Regenerate it; never edit it.

## One domain, two application styles

`src/TrainingHub.Shared.*` holds the domain, the kernel, persistence and the HTTP boundary. Two
stacks consume it and every use case exists in both:

- `src/DDD` — application services. Controllers inject `ITrainerApplicationService` and friends.
- `src/DDDWithCqrs` — commands and queries, dispatched through `ICommandDispatcher` /
  `IQueryDispatcher`. Controllers name no command or query; `HttpToApplicationMappings` builds them.

Both hosts page their lists with the kernel's `PageRequest`/`PagedResult` over the same total
order (ADR 0001, ADR 0029): the CQRS handler projects columns, the layered service asks the
repository a named question and maps the aggregates.

## Domain

- Business rules live in the domain. Aggregates accept value objects and typed identifiers, never a
  `string`, a `Guid`, or an object shaped like an HTTP request.
- Constructors are private; a value object is built through a factory that can refuse, returning
  `Result<T>`. Classes are sealed unless inheritance is a decision (ADR 0014).
- `Result` exposes no `IsSuccess` and no `Value`. Use `Match`, `MatchAsync`, `Bind`, `Switch`.
- An aggregate answers whether a change was allowed; it is not a way of reading state
  (`NoAggregate_ReturnsData`). The one pinned exception is a boolean question wearing a domain
  specification (`Training.IsOwnedBy`, ADR 0028).
- A specification names a business rule, or it does not exist: declared in the domain beside its
  aggregate, one expression answering both in memory and as a query criteria, never a query DSL —
  repositories expose named questions, and the CQRS readers never touch one (ADR 0028).
- A rule the aggregate cannot settle alone comes to it through a port declared beside it
  (`IUniquenessTitleChecker`, `ITrainingCounter`): the port answers the fact, the factory makes
  the decision, and the domain names no service to decide in an aggregate's place (ADR 0030).
  A decision with **no** home at all is a *recorded* domain service — named `*DomainService` in
  full, never a bare `*Service`, static, stateless, ports as parameters, pinned by rule
  (`TrainingTransferDomainService`, ADR 0036).
- Each aggregate owns the error codes it raises, prefixed with its own name — `Trainer.PhotoTooLarge`
  (ADR 0015). `ErrorCodes.Validation` belongs to the FluentValidation pipeline alone (ADR 0016).

## CQRS

- A command answers a **bare `Result`** — never `Result<T>`. What changed is read back with a query:
  a write that hands back what it wrote is a read in disguise (`EveryCommand_AnswersWithABareResult`).
- A query never changes state, and answers a `*Dto` — never an aggregate, entity or value object.
- **A query is named for what it retrieves and what scopes it**, the way a command is named for what
  it does: a retrieval verb (`Get`, `Search`, `Retrieve`, `List`, `Find`), then what is retrieved,
  then the criterion as `ByX` whenever there is one — `GetTrainerProfileByTrainerIdQuery`,
  `GetTrainingsByStatusQuery`. **The measure is that a reader need not open the file**, and it is
  what settles the rest: `ById` only where the `Id` is that of the thing just named
  (`GetTrainingByIdQuery`, never `GetTrainerProfileByIdQuery` — a profile has no identifier, the
  value is a `TrainerId`); the criterion named even when the message does not carry it
  (`GetTrainingsByCurrentTrainerQuery` declares only its paging, the trainer coming from
  `ICurrentUserService`, which is why the name is the *only* place its scoping is written); paging
  is not a criterion; a query that fetches has a criterion while a query that *searches* is one, so
  `SearchCatalogQuery` needs no `ByX`; and where two identifiers travel together `ByX` names the one
  that identifies the answer, not the one that authorizes the read (ADR 0081,
  `EveryQuery_IsNamedForWhatItRetrieves`).
  **A read port is not a message and keeps its name** —
  `ICatalogDetailQuery`, `ITrainerAccountQuery`, `ITrainingSearchQuery` are named questions an outer
  layer asks an adapter (ADR 0028, ADR 0055), and renaming one renames a vocabulary this record
  does not own.
- **A message whose criterion is its caller says `Current`** — its handler resolves the trainer
  through `ICurrentUserService` and it carries no identifier of its own, so nothing but the name
  can say whom it acts on: `EraseCurrentTrainerCommand`, `EditCurrentTrainerCommand`,
  `GetTrainingsByCurrentTrainerQuery`. A message carrying an explicit identifier never says it —
  `SuspendTrainerCommand` acts on whoever it names, `GetTrainerByIdQuery` on whoever it is given —
  because the name must describe the message, not its most frequent caller (ADR 0086,
  `EveryMessageActingForItsCaller_SaysCurrent`).
- **An event handler is named `{Reaction}When{Event}Handler`, the event's full type name embedded**:
  `DeleteTrainingWhenTrainerDeletedDomainEventHandler` for `TrainerDeletedDomainEvent`,
  `SendErasureNoticeWhenAccountErasedIntegrationEventHandler` for `AccountErasedIntegrationEvent`.
  The `DomainEventHandler` / `IntegrationEventHandler` suffix falls out of the embedding and says
  which kind of event — the moment it runs and the rules it runs under — while the reaction phrase
  is what lets one event keep two handlers (ADR 0087, `EveryHandler_IsNamedForTheEventItHandles`).
- Command handlers live in the application layer, query handlers in infrastructure.
- One validator per command, beside it. One folder per use case.
- **Every identifier a command or query carries is refused empty by its own validator**, even
  where the HTTP contract already refuses it (ADR 0046). The two answer different callers: the
  contract answers a request, the validator answers anything that reaches a dispatcher. The
  application layer never assumes the boundary checked first
  (`EveryIdentifierAMessageCarries_IsRefusedEmptyByItsValidator`).

## HTTP boundary

- The published contracts are `*HttpRequest` and `*HttpResponse`, under `Shared.Api/Contracts/`. No
  controller names a command, a query or an application DTO, and no inner layer names a contract.
- **The qualifier says which boundary a type belongs to, and the two must never be confused.** The
  layered stack's application services take a `*Request` and answer a `*Dto`; the API's published
  contracts are `*HttpRequest` and `*HttpResponse`. `EditTrainerHttpRequest` is what a client sends;
  `TrainerEditionRequest` is what the application layer accepts, after the mapping.
- **Both end in `Request`, so the suffix no longer places a type and no rule may ask it to**
  (ADR 0048). What an action binds or answers is named for the transport and lives under
  `Contracts/`; what an inner layer declares never is. That is checked per assembly rather than per
  string, in two halves that can fail separately — a layered signature that takes an `*HttpRequest`
  (`EveryLayeredServiceSignature_SaysWhichBoundaryItIsOn`), and an inner layer that declares one
  (`NoInnerLayer_DeclaresATypeNamedForTheTransport`).
- The CQRS stack names its inputs differently on purpose — a `*Command` or a `*Query`, one folder
  per use case — and answers the same `*Dto`. The `*Request` half is the layered stack's; the `Dto`
  half is shared by both.
- Every failure leaves as RFC 7807 Problem Details, with domain codes under `domainErrors`
  (ADR 0004, ADR 0012).
- Every action declares the statuses it can answer; every route identifier is constrained
  (`{id:guid}`). A creation answers 201 with the address of what was created (ADR 0011).

## Localization

- The words live in `src/TrainingHub.Translations` — marker types plus `.resx` families, neutral
  English with `fr` and `ru` beside it — and that project references **nothing**, so every adapter
  may load it and no inner circle may reference it (ADR 0088,
  `NoInnerCircle_ReferencesTheTranslations`, `TheTranslations_DependOnNothing`). The domain keeps
  authoring codes; sentences are the boundary's.
- Composing an email is presentation too: the application asks `INotificationComposer` for finished
  words — one method per notice, ten of them — and the adapter lives in
  `Shared.Infrastructure/Email/` beside `SmtpEmailSender`, the one corner of the infrastructure
  allowed to read Translations (`OnlyTheEmailCorner_OfTheInfrastructureReadsTheTranslations`,
  ADR 0090). A repository, an interceptor, the outbox processor never compose prose.
- **An email is written in its recipient's language, and that language is read where their address
  is** (ADR 0091). The account states it at registration and changes it from the language selector;
  each consumer takes it from whatever resolved the recipient — the fact, the invitation, or the
  read port — and never from the culture of whoever caused the notice, which for a suspension is
  the administrator's. Two rules hold it: `EveryNotice_ReadsItsLanguageWhereItReadsItsAddress` and
  `NoNotice_ComposesItsOwnProse`.
- **A word between the tags of a `.razor` is a red build** (`NoScreen_ShowsAWordItDidNotAskFor`),
  with the brand as the one named exemption. The topics are a family of their own, keyed by name
  and censused both ways against the domain's closed set — and the *value* that travels stays the
  English name, because a filter key is a value and not a word.
- One list, one default: `SupportedLanguages` (`en`, `fr`, `ru`, default `en`). Adding a language
  means growing that list, adding the culture's resx beside every neutral file with **exactly**
  the same keys (`EveryCultureResource_CarriesExactlyTheDefaultsKeys`), and declaring the new
  compound extension unread in `AmericanSpellingRules` — the census fails otherwise.
- Resolution is the BFF's: culture cookie, then `Accept-Language`, then English. The API hosts
  read the header alone through `AddApiLocalization`/`UseApiLocalization`
  (`BothApiHosts_ResolveTheSameCulture`), and the BFF restates its resolution in that header on
  both channels to the API. `<html lang>` carries the answer; the WebAssembly boot reads it back
  — never resolve a culture a second time somewhere else.
- Consumption is `IStringLocalizer<CommonResources>` and nothing custom. Neutral resx files are
  read by the spelling rule (English we write); `.fr.resx`/`.ru.resx` are declared unread — their
  keys are governed by the key-set rule instead.
- **Every surface reads its words from its own family** — `CatalogResources`,
  `TrainingResources`, `TrainerResources`, `AuthenticationResources`, `AdministrationResources`,
  the shell and the shared sentences in `CommonResources` — in whole sentences, never fragments:
  `{0}` templates where data joins, two entries where a plural changes shape, whole localized
  sentences passed where grammar used to be composed (ADR 0089). A new screen ships its words in
  the three languages from the first commit.
- **The problem funnel translates per entry.** `ProblemResultExtensions.Problem` looks each
  `domainErrors[].errorCode` up in `DomainErrorResources`; a hit answers the culture's sentence
  with the code untouched, a miss passes the domain's authored sentence through. The catalog
  holds only codes the domain raises with one non-interpolated sentence
  (`EveryDomainErrorKey_IsACodeTheDomainRaises`); the neutral entry restates the domain verbatim,
  so English never moves. The annotation templates resolve the same way — the English template is
  the key (`AddApiValidationLocalization`, `BothApiHosts_LocalizeTheirAnnotations`).
- **A key is a string, so the typo is a rule, not a review comment**: every literal key a source
  file asks of a localizer must exist in its family's neutral file
  (`EveryKeyAScreenAsks_ExistsInItsFamily`) — mistype `L["FirstName"]` and the build goes red
  naming the file, the family and the key.

## Observability

- **Only the telemetry seam touches OpenTelemetry** (`OnlyTheTelemetrySeam_TouchesOpenTelemetry`,
  ADR 0095): `AddApiTelemetry` in `Shared.Api`, called by both API hosts
  (`BothApiHosts_ConfigureTheSameTelemetry`), plus the BFF's own small `Telemetry/` corner.
  Custom instrumentation everywhere else speaks the BCL's `ActivitySource` and `Meter` — no
  package, ever, in an inner layer (`TheInnerCircle_CarriesNoTelemetryPackage`).
- The `Telemetry:OtlpEndpoint` setting is the switch: blank means nothing is registered at all,
  which is what CI and the test factories run under. It is a `string`, not a `Uri`, so a blank
  override stays recognizably blank.
- **The names are a contract** (`TheInstrumentationNames_AreTheRecordedOnes`, ADR 0096): sources
  `TrainingHub.Application` / `TrainingHub.Outbox` / `TrainingHub.Email`; span names are the
  message type names; metrics are `traininghub.*` histograms plus the poison counter and
  `traininghub.facts.delivered` — dashboards query these strings, so renaming one is a decision,
  not a refactoring.
- **Every metric tag comes from a set the code closes** — message names, the registered fact
  names, consumer names, three outcomes, the error-code vocabulary. Never an entity identifier,
  an address, a URL or a request id; failed operations carry `error.code`, never the localized
  sentence. Identity stays on the log line (ADR 0027), which now carries the trace id to join on.
- Command and query telemetry is the CQRS host's `TelemetryPipelineBehavior`, first in the
  pipeline; handlers carry none. Domain event handlers get no spans — they run inside the
  command's own span. The layered stack is observable through its HTTP spans, deliberately.
- The outbox envelope stores its producer's `traceparent`
  (`TheOutboxEnvelope_CarriesItsProducersTraceContext`, ADR 0097); every delivery attempt is a new
  root span linked back to it — never a child.

## C# style

The build enforces the style, so match what is there rather than normalizing it:

- **Both member forms are deliberate**: a block where there is a guard clause, an arrow where the
  member is one expression. `.editorconfig` declines to pick a side and says why — do not convert
  one into the other. Properties, indexers and accessors *must* use expression bodies (IDE0025-0027
  are errors).
- **Primary constructors for injected dependencies** — controllers, handlers, adapters. Ordinary
  constructors elsewhere; IDE0290 is a suggestion on purpose, so do not convert the four that remain.
- File-scoped namespaces, `var`, Allman braces, and a hundred and sixty-two analyzer severities, all
  enforced at build time.
- **Where SonarQube and this repository's ruleset disagree, the ruleset wins.** A Sonar finding is
  never on its own a reason to rewrite code `.editorconfig` deliberately allows: the quality profile
  is somebody else's list, while every severity here was chosen for this codebase and every demotion
  carries the argument for it (`EveryDemotedRule_SaysWhyItWasDemoted`). Act on a finding when it
  names a real defect; never to make a style rule stop reporting. Examples written in this file
  follow the same rule — a complete member rather than a fragment.

## Tests

- Behavior changed → unit tests.
- API behavior changed → an integration test in `tests/TrainingHub.Api.TestKit/`, so both suites run
  it rather than one.
- An architectural rule changed → the rule, carrying
  `[ArchitectureRule("<adr>", "the decision in the record's own words")]`.
- Assertions are AwesomeAssertions; `Assert.*` is refused by a rule (ADR 0007).
- Before trusting a new rule, break the thing it defends and watch it fail. A rule that has never
  failed has never been shown to check anything.

## Documentation

- A new architectural decision → a new record in `docs/adr/`, a row in `docs/adr/README.md`, and a
  rule that defends it. ADR 0013 makes that last part mandatory.
- A merged record is never rewritten. A decision that changes gets a new record superseding it.
- Update the README when a convention, a workflow or the project graph changes.
- **A living document answers to three ledgers in `DocumentationRules`, and a sentence belongs to
  exactly one.** A sentence that *counts* is a `CountedClaims` row (ADR 0038); one that
  *enumerates* is a `NamedLists` row (ADR 0041); one that *denies* is a `ClaimsOfAbsence` row
  (ADR 0092). The third exists because a denial ages worst: an assertion going stale reads as
  incomplete, while *it does not serve a catalog* reads as a decision long after it stopped being
  true — which is exactly how it survived eleven merges. Each row names the sentence it anchors on,
  so **rewording a guarded sentence fails the build**: a guarantee quietly rephrased is a guarantee
  quietly withdrawn, and no rule can tell those apart.

## Before calling it done

- Clean Release build, zero warnings.
- Every suite you could run passes; name the ones you could not run and why.
- No dead code, no comment describing something that is no longer true.
- Documentation and implementation agree.

A commit message is a short, descriptive imperative title in the Linux-kernel style — the title
says the change directly, never through a Conventional Commits prefix (`feat:`, `fix:`, `chore:`…
are banned) — a blank line, then a body: the main changes and their motivation, whenever that
adds value. Less detailed than the pull request's description, but enough that someone reading
only the git history understands what was done and why. Squash-merged from a pull request. An
AI-assisted commit keeps its `Co-Authored-By` trailer — always — but never carries a Claude
session reference: no `Claude-Session` trailer, no session URL, not in the message and not in
anything committed. Check the message and the staged diff for one before every commit. If you see
a better design that no accepted record forbids, propose it before implementing it.

**A commit carries exactly one co-author, and it is Claude.** Never a second `Co-Authored-By`
trailer, whatever the reason offered for adding one. A co-authorship is a claim about who wrote the
code, and the only honest one here names the assistant that helped write it.

The trailer is not the only place a second name appears, and the other one is easy to introduce by
accident. **The author and the committer must be the same identity** — the repository's own, the one
the rest of the history uses. When the two differ GitHub prints both beside the co-author, which
reads as a co-authorship nobody agreed to. `git commit --amend` is where this happens: it inherits
the original author but takes the committer from `git config`, so a squashing amend has to set both
on purpose rather than let one of them drift:

```bash
GIT_COMMITTER_NAME="<the author's name>" GIT_COMMITTER_EMAIL="<the author's address>" \
  git commit --amend --no-edit --author="<the author's name> <the author's address>"
```

Verify it the way a reader will see it, before pushing: `git log -1 --format='%an <%ae>%n%cn <%ce>'`
prints the same line twice, or the commit is not ready.

**Everything written for Git or for GitHub is in English — the whole artifact, not its title.**
There is no part of one where another language is acceptable, and *the title was in English* does
not satisfy this rule. It covers, exhaustively:

- a commit's **subject line** and its **body**, trailers included;
- a pull request's **title**, the **headings** of its description, and **every word of the
  description itself** — prose, tables, bullet lists, quoted output, the comments inside a code
  block, the technical explanation, the motivation, the verification section, the *what is next*;
- anything else published beside one: an issue or pull-request **comment**, a **review** and its
  inline remarks, a **reply to a reviewer**, a branch name, a release note.

**The conversation is the exception, and the only one.** Answer the user in the language they wrote
to you in — that costs nothing and belongs to them. What separates the two is permanence and
audience: a chat is ephemeral and has one reader, while a description stays attached to the diff for
as long as the diff exists and is read by whoever arrives next. A repository whose prose changes
language according to who happened to ask for the change is one that has to be read twice. Translate
at the boundary rather than writing across it: think in whichever language the discussion is in, and
write the artifact in English.

**And that English is American English — everywhere, without exception (ADR 0064).** This is a
spelling rule, not a language rule: the paragraph above says *which language*, this one says *which
variant*. Where two forms exist, write the American one — `color`, `behavior`, `organization`,
`authorization`, `center`, `catalog`, `analyze`.

It covers, exhaustively:

- **every identifier** — type, interface, method, property, field, variable, parameter, namespace,
  folder and file name;
- **every comment and every XML documentation block**, `<summary>` and `<remarks>` alike;
- **every error message, log message and technical string**, including the ones a caller reads;
- **every document** — `README.md`, `CLAUDE.md`, `docs/`, the strategic design, and every new
  architecture decision record;
- **every commit message and pull-request title, description, comment and review**;
- **every test name and every assertion's `because` clause**.

The endings that decide almost all of it, written the way this repository writes them:

- **`-ize`, `-ization`, `-izer`** — `organize`, `authorize`, `normalize`, `sanitize`, `initialize`,
  `serialize`, `optimize`, `tokenize`, `materialize`, and the nouns and agents built on them.
- **`-yze`** — `analyze`, `analyzer`, `paralyze`. **`analysis` does not move**: only the verb does,
  which is why `AnalysisRules` is already correct.
- **`-or`** — `color`, `behavior`, `favor`, `honor`, `labor`, `neighbor`, `rigor`.
- **`-er`** — `center`, `meter`, `fiber`, `theater`, `caliber`.
- **`-se`** — `license`, `defense`, `offense`, `pretense`, and `practice` for both the noun and the
  verb.
- **`-og`** — `catalog`, `dialog`, `analog`.
- **one `l` before a suffix** — `canceled`, `modeling`, `traveling`, `labeled`, `signaled`.
  `cancellation` keeps both, on either side of the Atlantic, and is already correct.
- **two `l`s where American doubles instead** — `enrollment`, `fulfill`, `installment`.
- **and the rest, one by one** — `artifact`, `judgment`, `gray`, `mold`, `program`, `check`,
  `draft`, `aging`, `skeptic`, and `while` and `among` in place of their `-st` forms.

**The complete list lives in one place**, and it is the dictionary inside
`tests/TrainingHub.Architecture.Tests/Rules/AmericanSpellingRules.cs` — every refused spelling with
what replaces it. Restating it here would be a second copy to forget, so this section teaches the
shape and that file holds the letter.

**What is exempt is exempt by name, and nothing is exempt by being forgotten (ADR 0066).** A word
coming from outside this repository keeps the spelling its author gave it — `Color`,
`IPipelineBehavior`, `AuthorizationPolicy`, `Serializer` are what .NET calls them, and renaming them
is not an option. The sixty-three records merged before ADR 0064 keep the words they were written
with, because a merged record is never rewritten. `AmericanSpellingRules.cs` is invisible to its own
rule, because it is the file that lists what the rule refuses. And six kinds of file are read by
nobody, each with its reason written beside it in that same file: the MIT license, a developer's
`appsettings.Local.json` and their `.pfx`, the rolling `.log` a running host leaves in the tree,
and the two files a vendored Claude skill carries — its `SKILL.md` and its `LICENSE.txt`, words
written outside this repository and kept verbatim.

**A file that is neither read nor declared unread fails the build**, which is the half worth
remembering when adding a kind of file this repository has not held before.
`EveryFileThisRepositoryHolds_IsEitherReadOrDeclaredUnread` closes the two sets against each other,
because the previous shape — a list of extensions naming what to check — went quiet about three
Dockerfiles, a `.gitattributes` and a Python script while reporting itself kept. Adding a `.toml`
now costs one line saying which side it is on; it used to cost nothing and check nothing.

**`EveryWordThisRepositoryWrites_UsesAmericanSpelling` fails the build** when any of this is broken,
so a spelling is a red test rather than a review comment. A word the dictionary does not yet know is
a word to add to it, in the same commit that introduces the need.

**Pushing a `claude/*` branch means opening its pull request, without being asked.** This paragraph
is that request, made once and standing: work that is finished is work a reviewer can see, and a
branch sitting on the remote with no pull request is finished work nobody has been told about. So
the last step of the push is `gh pr create` — or the equivalent GitHub tool — against the default
branch, every time, with no confirmation sought.

Three things bound it, and they are what make a standing authorization safe:

- **One pull request per branch.** If the branch already has an open one, the push updates it —
  including after a force-push — and the description is rewritten to describe what the branch now
  contains. Opening a second is how a review ends up split across two threads.
- **A merged pull request is finished.** Follow-up work restarts the branch from the default branch
  and gets a *new* pull request; it is never stacked onto merged history.
- **Opening is not following.** Do not subscribe to the pull request's activity, poll its checks or
  schedule a check-in unless asked to. Opening it hands the work over; watching it is a separate
  request.

Everything else about the artifact still applies: English throughout, the title and description
written to the conventions below, and a repository template filled in when there is one.

**A pull request Claude Code owns carries one commit, and stays that way.** After every push to a
`claude/*` branch, squash the branch back to a single commit against the base, force-push it, and
then *check* — `git log --oneline origin/master..HEAD` prints one line, or the job is not finished.
The message is rewritten to describe the whole change, never appended to: a squashed history whose
message still narrates the first attempt is worse than the commits it replaced. It obeys the same
conventions as any other — imperative title, no Conventional Commits prefix, blank line, body.

Three conditions, and none of them is optional:

- **Fetch first, and squash everything since the base — not "your" commits.** `ci.yml` commits the
  regenerated client to the pull request's branch with `GITHUB_TOKEN`, and that push triggers no
  workflow, so a squash computed from a stale local view deletes work nothing will redo. Fetch, then
  `git reset --soft $(git merge-base HEAD origin/master)`, so the bot's tree is *inside* the squash
  rather than under it.
- **`--force-with-lease`, never a bare `--force`** — and know what the bare form compares against.
  It checks the remote-tracking ref, which the fetch above has just moved, so on its own it stops
  protecting you at exactly the moment it matters. Name what you verified:
  `--force-with-lease=<branch>:<sha>`.
- **Only a branch Claude Code owns, and only before review.** Never rewrite a branch anyone else
  pushes to. Once a reviewer has commented, stop squashing: a force-push orphans inline comments
  anchored to commits that no longer exist and destroys the *changes since your last review* diff.
  From then on, add commits and squash once, at the end — GitHub's squash-merge would deliver the
  same single commit to `master` either way.
