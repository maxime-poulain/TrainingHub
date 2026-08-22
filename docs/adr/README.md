# Architecture decision records

One file per decision that would be expensive to reverse, or that a reader would otherwise
reasonably assume was an accident.

The code says *what*, and the comments say *why this line*. What neither can hold is the shape of a
decision: the options that were open, what each would have cost, and why the one that lost was
rejected. Without that, the second reader either takes the design on trust or rediscovers the
argument — and sometimes reverses it, since the rejected option is usually the one that looks
simpler from the outside.

## Conventions

- One record per file, numbered in order: `NNNN-a-sentence-in-the-imperative.md`.
- Numbers are never reused, and a record's **body** is never rewritten once merged. A decision that
  changes gets a new record that supersedes or amends the old one, and the old one is marked as such
  and left in place — the reasoning that was true at the time is what makes the change legible.
- The **status line is the exception**, and the only one: it carries the record's standing, not its
  argument, so a later decision annotates it. A record that amends another declares it in an
  `- **Amends:** NNNN` field, and the amended record's status names it back (ADR 0039).
- Status is one of `Proposed`, `Accepted`, `Superseded by NNNN`, optionally followed by ` — ` and
  what later records did to the decision. It is written in the record; the table below repeats it,
  and a rule holds the two to each other.
- Record the alternatives and why they lost. A record without them documents an outcome, not a
  decision, and cannot be revisited.

## Index

| # | Decision | Status |
|---|----------|--------|
| [0001](0001-paginate-on-the-query-side-over-a-total-order.md) | Paginate on the query side, over a total order | Accepted — amended in part by 0029; amended in part by 0071: the search index's total order becomes a pair a caller chooses from, each total by the same tie-break |
| [0002](0002-keep-domain-reactions-in-the-transaction-and-deliver-integration-events-through-an-outbox.md) | Keep domain reactions in the transaction, deliver integration events through an outbox | Accepted — implemented; the message design is recorded in 0024, the delivery worker in 0025; its registration consequence is corrected by 0040 |
| [0003](0003-apply-migrations-on-startup-in-development-only.md) | Apply migrations on startup in Development only | Accepted — amended by [0045](0045-fail-readiness-while-a-migration-is-pending.md): the readiness probe this record said to revisit it for now exists, and a pending migration fails it |
| [0004](0004-publish-every-error-as-rfc-7807-problem-details.md) | Publish every error as RFC 7807 Problem Details | Accepted — amended in part by 0012 |
| [0005](0005-store-audit-timestamps-at-full-precision.md) | Store audit timestamps at full precision | Accepted |
| [0006](0006-describe-the-api-with-the-frameworks-openapi-generator.md) | Describe the API with the framework's OpenAPI generator | Accepted — one paragraph superseded by 0008 |
| [0007](0007-assert-with-awesomeassertions.md) | Assert with AwesomeAssertions | Accepted |
| [0008](0008-generate-the-http-client-from-a-script-and-verify-it-in-ci.md) | Regenerate the HTTP client from the API, and commit it automatically | Accepted — the list-shape argument for the source host is dated by 0029; the hosts now answer alike, and the layered one remains the source |
| [0009](0009-hold-the-access-token-in-the-bff-instead-of-the-browser.md) | Hold the access token in the BFF instead of the browser | Accepted — amended by [0063](0063-strip-the-metadata-before-the-bytes-are-stored.md): the forgery guard also admits a safe same-origin read the browser attests, because a custom header is one this application's own images cannot set either |
| [0010](0010-declare-the-conditional-request-contract-in-the-document.md) | Declare the conditional-request contract in the document | Accepted |
| [0011](0011-answer-a-creation-with-201-and-the-address-of-what-was-created.md) | Answer a creation with 201 and the address of what was created | Accepted, amended — see the Amendment section below |
| [0012](0012-finish-the-one-error-shape-and-name-its-members-apart.md) | Finish the one error shape, and name its members apart | Accepted — amended by 0016 |
| [0013](0013-make-every-record-answer-to-a-test.md) | Make every record answer to a test | Accepted — amended by 0039: the ledger of exemptions is what says how many there are |
| [0014](0014-seal-by-default-and-let-inheritance-be-a-decision.md) | Seal by default, and let inheritance be a decision | Accepted |
| [0015](0015-let-each-aggregate-own-the-errors-it-raises.md) | Let each aggregate own the errors it raises | Accepted |
| [0016](0016-let-a-rejected-command-fail-like-every-other-command.md) | Let a rejected command fail like every other command | Accepted — the validation cost it recorded and deferred is paid off by 0043 |
| [0017](0017-measure-what-the-rules-cannot-with-sonarqube-cloud.md) | Measure what the rules cannot, with SonarQube Cloud | Accepted — amended by 0018, and by [0049](0049-measure-duplication-where-repetition-is-a-defect.md): the duplication measure exempts the two hosts, whose published declarations two rules require to be identical |
| [0018](0018-fail-on-the-gate-where-failing-stops-something.md) | Fail on the gate where failing stops something | Accepted |
| [0019](0019-enforce-the-ruleset-this-repository-already-declared.md) | Enforce the ruleset this repository already declared | Accepted — amended by 0020; amended by [0076](0076-target-one-framework-across-the-solution.md): the `AnalysisLevel` pin is removed — one framework across the solution makes it a no-op |
| [0020](0020-declare-every-rule-this-codebase-already-satisfies.md) | Declare every rule this codebase already satisfies | Accepted |
| [0021](0021-store-a-photo-beside-the-row-that-names-it.md) | Store a photo beside the row that names it, and never overwrite in place | Accepted — amended by [0063](0063-strip-the-metadata-before-the-bytes-are-stored.md): the metadata this record deferred stripping is stripped when the bytes arrive, and the domain records that it was |
| [0022](0022-name-the-repository-after-the-domain-it-serves.md) | Name the repository after the domain it serves | Accepted |
| [0023](0023-document-the-strategic-design-and-hold-it-to-the-model.md) | Document the strategic design, and hold it to the model | Accepted |
| [0024](0024-publish-facts-not-intents-and-version-them-in-the-envelope.md) | Publish facts, not intents, and version them in the envelope | Accepted — the email half of "the ports remain fakes" is dated by 0031; the search half stays true; the retry contract gains its schedule in 0033; the per-consumer half of its at-least-once promise is made true by 0034 |
| [0025](0025-deliver-the-outbox-with-a-hosted-service-in-each-host.md) | Deliver the outbox with a hosted service in each host | Accepted — the email half of "they remain fakes" is dated by 0031; the search half stays true; the retry cadence, the poison's silence and the table's growth are hardened by 0033; delivery is settled per consumer by 0034; the dead-letter surface it deferred is built by 0061 |
| [0026](0026-log-with-serilog-to-console-and-files-through-typed-options.md) | Log with Serilog to console and files, through typed options | Accepted — amended by [0095](0095-observe-every-host-with-opentelemetry-through-one-seam.md): the aggregator this record reserved a day for exists, and the two text sinks gain an OTLP sibling inside the same extension, on the telemetry endpoint's switch |
| [0027](0027-stamp-the-callers-identity-on-every-log-line.md) | Stamp the caller's identity on every log line | Accepted |
| [0028](0028-a-specification-names-a-business-rule-or-it-does-not-exist.md) | A specification names a business rule, or it does not exist | Accepted — amended by [0055](0055-let-the-administration-read-what-the-catalogue-may-not.md): a named question may carry named criteria, and the line this record drew at none moves to *never a predicate* |
| [0029](0029-answer-a-list-the-same-way-on-both-hosts.md) | Answer a list the same way on both hosts | Accepted — amended by 0071: the shared list contract gains a sort parameter — the same closed set of orders on both hosts |
| [0030](0030-bring-the-fact-to-the-aggregate-not-the-decision-to-a-service.md) | Bring the fact to the aggregate, not the decision to a service | Accepted — narrowed by 0036: a decision with no home is a recorded domain service |
| [0031](0031-send-email-over-smtp-and-prove-it-against-a-real-server.md) | Send email over SMTP, and prove it against a real server | Accepted |
| [0032](0032-flatten-a-value-object-as-a-complex-property-not-an-owned-entity.md) | Flatten a value object as a complex property, not an owned entity | Accepted — amended by [0060](0060-look-inside-the-column-a-search-has-to-read.md): one scalar converts, unless the column has to be looked inside — a converted `TrainingTitle` is one no substring match can translate against |
| [0033](0033-back-off-between-retries-log-the-poison-and-sweep-the-delivered-history.md) | Back off between retries, log the poison, and sweep the delivered history | Accepted — the per-consumer isolation it left out arrives in 0034; the poison gains a pollable gauge in 0037; the dead-letter surface it deferred is built by 0061 |
| [0034](0034-deliver-once-per-consumer-not-once-per-message.md) | Deliver once per consumer, not once per message | Accepted |
| [0035](0035-give-every-developer-a-git-ignored-local-overrides-file.md) | Give every developer a git-ignored local overrides file | Accepted |
| [0036](0036-model-the-decision-that-has-no-home-as-a-domain-service.md) | Model the decision that has no home as a domain service | Accepted |
| [0037](0037-answer-for-the-hosts-health-at-two-endpoints.md) | Answer for the host's health at two endpoints | Accepted — amended by [0045](0045-fail-readiness-while-a-migration-is-pending.md): a fifth probe answers for the schema, so every "four probes" below now reads five; the dead-letter surface it deferred is built by 0061; amended by [0076](0076-target-one-framework-across-the-solution.md): the BFF's inline liveness pair is no longer a consequence of the target framework, and stands on the reason it always had — this host owns no dependency to report readiness on |
| [0038](0038-derive-every-counted-claim-from-the-code.md) | Derive every counted claim from the code | Accepted — amended by [0065](0065-ship-every-host-as-an-image-and-build-them-in-the-pipeline.md): the compose file and the Dockerfiles it recorded as read by nothing are read by two rules; amended by [0092](0092-hold-a-documents-claims-of-absence-to-the-code.md): a third ledger joins the two, for the claims that deny rather than count or enumerate |
| [0066](0066-close-the-spelling-rules-selection-against-its-exemptions.md) | Close the spelling rule's selection against its exemptions | Accepted |
| [0067](0067-cache-the-image-layers-without-taking-a-dependency.md) | Cache the image layers without taking a dependency | Superseded by [0068](0068-remove-the-image-layer-cache.md): measured warm, the cache saved nothing and doubled the job |
| [0068](0068-remove-the-image-layer-cache.md) | Remove the image layer cache | Accepted |
| [0039](0039-hold-the-record-and-its-index-to-the-same-status.md) | Hold the record and its index to the same status | Accepted |
| [0040](0040-register-the-trainer-and-the-account-in-one-transaction.md) | Register the trainer and the account in one transaction | Accepted |
| [0041](0041-derive-every-named-list-from-the-code.md) | Derive every named list from the code | Accepted |
| [0042](0042-close-the-boundarys-vocabulary.md) | Close the boundary's vocabulary | Accepted — amended by [0048](0048-qualify-a-contract-before-naming-what-it-is.md): the qualifier moves to the front of the contract's name, and the rules read the assembly rather than the suffix |
| [0043](0043-validate-once-where-the-rule-lives.md) | Validate once, where the rule lives | Accepted — amended by [0046](0046-refuse-the-empty-identifier-at-every-entry-point.md): the one shape rule this record kept in the pipeline gains a second half at the HTTP boundary, and the pipeline keeps its own; the sentence emptying the creation validators is corrected there |
| [0044](0044-let-the-domain-speak-entirely-in-its-own-terms.md) | Let the domain speak entirely in its own terms | Accepted |
| [0045](0045-fail-readiness-while-a-migration-is-pending.md) | Fail readiness while a migration is pending | Accepted |
| [0046](0046-refuse-the-empty-identifier-at-every-entry-point.md) | Refuse the empty identifier at every entry point | Accepted |
| [0047](0047-verify-the-build-a-pull-request-delegates.md) | Verify the build a pull request delegates | Accepted |
| [0048](0048-qualify-a-contract-before-naming-what-it-is.md) | Qualify a contract before naming what it is | Accepted — amended by [0081](0081-name-a-query-for-what-it-retrieves-and-what-scopes-it.md): the query half of the CQRS vocabulary gains a convention — a retrieval verb, what is retrieved, and the criterion as ByX |
| [0049](0049-measure-duplication-where-repetition-is-a-defect.md) | Measure duplication where repetition is a defect | Accepted — amended by [0079](0079-build-the-development-catalog-with-the-domain.md): a written corpus joins the two hosts under the duplication exemption, named in a registry that carries its argument |
| [0050](0050-retire-a-training-rather-than-delete-it.md) | Retire a training rather than delete it | Accepted — amended by [0056](0056-announce-the-sanction-and-let-the-index-compose-visibility.md): the suspension has a surface and consumers now, so it leaves the context as a fact, and the index composes a trainer's standing rather than forgetting their catalogue; amended by [0053](0053-a-suspended-trainer-reads-and-does-not-write.md): a suspended trainer loses every write, editing and unpublishing included |
| [0051](0051-give-the-administrator-authority-not-a-context.md) | Give the administrator authority, not a context | Accepted |
| [0052](0052-make-an-administrative-removal-a-state-of-its-own.md) | Make an administrative removal a state of its own | Accepted |
| [0053](0053-a-suspended-trainer-reads-and-does-not-write.md) | A suspended trainer reads, and does not write | Accepted — amended by [0057](0057-the-trainers-own-surface-says-where-they-stand.md): the write controls a suspension forbids are shown disabled rather than removed; amended by [0085](0085-let-the-account-erase-itself-trainings-and-all.md): erasing the account is the one write a suspension does not take away |
| [0054](0054-give-the-administration-a-surface-of-its-own.md) | Give the administration a surface of its own | Accepted |
| [0055](0055-let-the-administration-read-what-the-catalogue-may-not.md) | Let the administration read what the catalogue may not | Accepted — amended by [0059](0059-give-the-search-index-a-body-and-a-query-surface.md): the search index settles the public training search and not the administration's, which needs the states the index refuses to hold |
| [0056](0056-announce-the-sanction-and-let-the-index-compose-visibility.md) | Announce the sanction, and let the index compose visibility | Accepted |
| [0057](0057-the-trainers-own-surface-says-where-they-stand.md) | The trainer's own surface says where they stand | Accepted |
| [0058](0058-a-translation-to-a-published-contract-is-total.md) | A translation to a published contract is total | Accepted |
| [0059](0059-give-the-search-index-a-body-and-a-query-surface.md) | Give the search index a body, and a query surface | Accepted — amended by [0069](0069-give-the-catalog-its-first-facet.md): the index gains its first non-title dimension — the topics a training declares, served as the catalog's facets; amended by [0071](0071-give-the-catalog-a-second-published-order.md): the index gains the training's own age, and a second published order over it |
| [0060](0060-look-inside-the-column-a-search-has-to-read.md) | Look inside the column a search has to read | Accepted |
| [0061](0061-give-the-poison-a-url-and-an-operator-a-way-back-in.md) | Give the poison a URL, and an operator a way back in | Accepted |
| [0062](0062-let-the-proxy-forward-one-family-of-paths-without-a-token.md) | Let the proxy forward one family of paths without a token | Accepted — amended by [0063](0063-strip-the-metadata-before-the-bytes-are-stored.md): the precondition it named is met, and the portrait is published at an address carrying the photo's identity; amended by [0070](0070-open-a-trainers-public-page.md): the detail port gains the profile's reads — an offering trainer's page and portrait, visibility from the index as ever |
| [0063](0063-strip-the-metadata-before-the-bytes-are-stored.md) | Strip the metadata before the bytes are stored, and publish only what was stripped | Accepted — amended by [0070](0070-open-a-trainers-public-page.md): the identifier this record would not hand out is handed out on purpose, now that a person has a page to be — the directory ADR 0055 withdrew stays withdrawn |
| [0069](0069-give-the-catalog-its-first-facet.md) | Give the catalog its first facet | Accepted — amended by [0080](0080-let-a-visitor-browse-several-shelves-at-once.md): the topic filter becomes a selection joined by *or*, and the facet counts stop being the whole catalog's to answer the term the visitor typed |
| [0070](0070-open-a-trainers-public-page.md) | Open a trainer's public page | Accepted — amended by [0082](0082-let-a-visitor-reach-a-trainer-without-learning-their-address.md): the platform becomes the channel it was called, and the rule withholding an address is narrowed to what the catalog answers |
| [0071](0071-give-the-catalog-a-second-published-order.md) | Give the catalog a second published order | Accepted — amended by [0074](0074-make-the-catalog-the-front-door.md): the default order flips to newest first — the front door shows what recently went on offer, and the alphabet becomes the order a caller asks for |
| [0072](0072-prerender-the-catalogs-routes.md) | Prerender the catalog's routes | Accepted — amended by [0074](0074-make-the-catalog-the-front-door.md): the closed set of prerendered routes gains the root, which serves the catalog itself |
| [0073](0073-describe-the-catalog-to-the-machines-that-read-it.md) | Describe the catalog to the machines that read it | Accepted — amended by [0080](0080-let-a-visitor-browse-several-shelves-at-once.md): the canonical keeps the whole selection rather than one topic, sorted ordinally so one question has one spelling |
| [0074](0074-make-the-catalog-the-front-door.md) | Make the catalog the front door | Accepted — amended by [0078](0078-land-the-administrator-in-the-administration.md): the browser asks the API's own question about its caller, and the menu's trainer doors are qualified the way its administration doors already were |
| [0064](0064-write-this-repository-in-american-english.md) | Write this repository in American English | Accepted — amended by [0066](0066-close-the-spelling-rules-selection-against-its-exemptions.md): the selection is closed against its exemptions, so a file governed by neither fails the build |
| [0065](0065-ship-every-host-as-an-image-and-build-them-in-the-pipeline.md) | Ship every host as an image, and build them in the pipeline | Accepted — amended by [0067](0067-cache-the-image-layers-without-taking-a-dependency.md): the layer cache it turned down is taken, the objection having been the dependency rather than the caching; amended by [0068](0068-remove-the-image-layer-cache.md): the cache is removed on measurement, and building without one stands again; amended by [0075](0075-give-the-bare-compose-up-to-the-developer.md): the bare compose up starts the dependencies alone, and the stack whole moves behind the full profile; amended by [0079](0079-build-the-development-catalog-with-the-domain.md): an image compiles under this repository's ruleset rather than the analyzers' defaults, the root's build configuration being derived rather than listed |
| [0075](0075-give-the-bare-compose-up-to-the-developer.md) | Give the bare compose up to the developer | Accepted |
| [0076](0076-target-one-framework-across-the-solution.md) | Target one framework across the solution | Accepted |
| [0077](0077-resolve-the-theme-before-the-first-paint.md) | Resolve the theme before the first paint | Accepted |
| [0078](0078-land-the-administrator-in-the-administration.md) | Land the administrator in the administration | Accepted |
| [0079](0079-build-the-development-catalog-with-the-domain.md) | Build the development catalog with the domain | Accepted |
| [0080](0080-let-a-visitor-browse-several-shelves-at-once.md) | Let a visitor browse several shelves at once | Accepted |
| [0081](0081-name-a-query-for-what-it-retrieves-and-what-scopes-it.md) | Name a query for what it retrieves and what scopes it | Accepted — amended by [0086](0086-say-current-when-the-caller-is-the-criterion.md): the command half gains the clause the caller-scoped commands were hiding — a message whose criterion is its caller says Current |
| [0082](0082-let-a-visitor-reach-a-trainer-without-learning-their-address.md) | Let a visitor reach a trainer without learning their address | Accepted — amended by [0083](0083-ask-the-visitor-for-proof-where-their-address-is-real.md): a Turnstile challenge stands in front of the contact endpoint, judged at the BFF where the visitor's connection ends |
| [0083](0083-ask-the-visitor-for-proof-where-their-address-is-real.md) | Ask the visitor for proof where their address is real | Accepted |
| [0084](0084-reset-a-forgotten-password-with-a-credential-the-database-cannot-leak.md) | Reset a forgotten password with a credential the database cannot leak | Accepted |
| [0085](0085-let-the-account-erase-itself-trainings-and-all.md) | Let the account erase itself, trainings and all | Accepted |
| [0086](0086-say-current-when-the-caller-is-the-criterion.md) | Say Current when the caller is the criterion | Accepted |
| [0087](0087-name-a-handler-for-the-event-it-handles.md) | Name a handler for the event it handles | Accepted |
| [0088](0088-answer-in-the-visitors-language-and-resolve-it-at-the-door.md) | Answer in the visitor's language, and resolve it at the door | Accepted — amended by [0089](0089-localize-every-surface-and-translate-the-refusal-at-its-funnel.md): the problem funnel now presents each cataloged refusal in the resolved culture, so `domainErrors[].errorMessage` is the domain's sentence only where the catalog has no entry |
| [0089](0089-localize-every-surface-and-translate-the-refusal-at-its-funnel.md) | Localize every surface, and translate the refusal at its funnel | Accepted |
| [0090](0090-prove-the-address-before-the-catalog-grows.md) | Prove the address before the catalog grows | Accepted — amended by [0091](0091-write-to-everyone-in-the-language-they-read.md): the deferral it recorded is lifted, `IVerificationEmailComposer` is folded into `INotificationComposer`, and the verification fact no longer carries a culture |
| [0091](0091-write-to-everyone-in-the-language-they-read.md) | Write to everyone in the language they read | Accepted |
| [0092](0092-hold-a-documents-claims-of-absence-to-the-code.md) | Hold a document's claims of absence to the code | Accepted |
| [0093](0093-let-the-keyboard-finish-what-the-form-starts.md) | Let the keyboard finish what the form starts | Accepted |
| [0094](0094-read-the-catalog-from-a-snapshot.md) | Read the catalog from a snapshot | Accepted |
| [0095](0095-observe-every-host-with-opentelemetry-through-one-seam.md) | Observe every host with OpenTelemetry, through one seam | Accepted |
| [0096](0096-name-the-operation-once-and-bound-every-tag.md) | Name the operation once, and bound every tag | Accepted |
| [0097](0097-link-the-delivery-to-the-trace-that-committed-the-fact.md) | Link the delivery to the trace that committed the fact | Accepted |
