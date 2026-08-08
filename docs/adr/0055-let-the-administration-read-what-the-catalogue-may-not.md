# 0055 — Let the administration read what the catalogue may not

- **Status:** Accepted
- **Amends:** [0028](0028-a-specification-names-a-business-rule-or-it-does-not-exist.md)
- **Date:** 2026-08-08

**This record was `Proposed` until the commit that built it**, the treatment 0050, 0051 and 0052 all
had, and for the same reason: writing the decision down before the code is the point, and claiming
the code already obeys it would be the lie this repository refuses everywhere else.

## Context

ADR 0051 gave the administration an authority, ADR 0052 gave it two decisions per aggregate, and
ADR 0054 gave those four decisions endpoints. Every one of them takes an identifier. **Nothing gives
one.** An administrator can withhold a training and then has no way of finding it again; the four
endpoints are a set of doors with no corridor.

The obvious read is one this repository deleted on purpose, and the README still says why:

> There used to be five more — `/Trainer/all`, `/Trainer/{id}`, `/Training/all`,
> `/Training/by-trainer/{id}` and `/Training/by-topic/{topic}` — and between them they handed out
> every trainer's name, contact email and bio to any authenticated caller, enumerable. […] They were
> removed rather than restricted, because a catalogue read scoped to one caller is not a catalogue
> read.

A reader who meets `/Administration/trainers` after that paragraph is entitled to think somebody
relapsed. They did not, and the difference is worth a record rather than a comment.

There is a second thing this forces, smaller and sharper. ADR 0028 requalified a specification
machinery that had drifted into a query DSL, and the remark it left on the first paged repository
question is categorical: *"no criteria travels through here"*. A list filtered by standing and by a
term is criteria travelling through there. Either the line moves and says where it now is, or it is
quietly crossed.

**Both filters were designed before either was tried against the model, and one of the two did not
survive it.** What that cost and what it changed is under Decision; it is recorded rather than
smoothed over, because the asymmetry it leaves is the first thing a reader will ask about.

## Decision

**The withdrawn read comes back, narrowed by audience rather than by shape.**

```
GET /Administration/trainers?status=&search=&page=&pageSize=
GET /Administration/trainings?status=&page=&pageSize=
```

- **What changed is who may ask, not what is returned.** `/Trainer/all` served a trainer's name and
  contact address to any authenticated caller. These serve the same columns to the one role that can
  suspend them, behind `AdministratorPolicy` on `AdministrationControllerBase` (ADR 0054), granted by
  hand and by no endpoint at all (ADR 0051). Both are paged, and both inherit ADR 0029's published
  cap rather than declaring page coordinates of their own — a listing with bounds of its own is how
  a cap becomes a suggestion.
- **Their own contracts and their own DTOs**, `Administration*`, rather than fields added to
  `TrainerHttpResponse` and `TrainingHttpResponse`. Two audiences, two shapes: a column added for the
  administration cannot reach a trainer's own profile by accident, and the reverse. The cost is
  named under Consequences rather than hidden.
- **A repository question may take named criteria. It may never take a predicate.** That is where
  ADR 0028's line moves to. A status and a term are values the adapter is free to interpret, index or
  ignore; an `Expression<Func<T, bool>>` is a fragment of a query written by somebody who cannot see
  the schema, and an `IOrderedQueryable` is the total order ADR 0001 requires handed to whoever asks
  last. **The order is not a criterion**: `NewestFirst` stays the only one, which is what stops the
  two hosts paging differently.
- **A status name the domain does not spell is refused at the boundary**, by a `[KnownStatus]`
  annotation that reads the allowed values off the domain type rather than listing them. A fourth
  state becomes filterable the day it is declared. An unknown name is a `400` naming the parameter,
  never an empty page — a caller who mistyped a filter and got nothing back would read it as
  "nothing matches".
- **The trainers' term is a `LIKE '%term%'` against the write model, deliberately and
  provisionally.** It is matched against a trainer's two names and contact address, and nothing else:
  a bio is prose, and searching it turns a bounded scan into an unbounded one for matches nobody
  asked for.
- **The trainings' listing has no term, and that is a persistence fact rather than a choice.**
  `Training.Title` is mapped through a value converter, and EF Core compares a converted property for
  equality without being able to look inside it — `Title.Value.Contains(term)` does not translate,
  and `EF.Property<string>` fails as an invalid cast. Both were tried against the real model rather
  than assumed. Making the title searchable means remapping it as a complex property, and that column
  carries the unique index enforcing title uniqueness under concurrency: a migration and a decision
  of its own, not a line in a listings change. **The status filter is what a moderator actually needs
  here** — nothing else in this API can find a withheld training.

## Consequences

- **Search does not seek.** `LIKE '%term%'` cannot use an index, so every search scans. The page
  bounds what is *returned*, not what is *read*, and the count reads everything by definition. At
  this scale that is free; it stops being free somewhere in the tens of thousands of rows, and the
  answer then is the Search Indexing context, which already exists on the context map and already
  consumes the training facts. **This record is where that is written down**, so the day it becomes
  slow, it is a known consequence being paid rather than a mystery being profiled.
- **The two listings are asymmetric, and a reader will notice before they read this.** Trainers are
  searchable by name and trainings are not. The reason is above; what matters here is that the same
  destination settles both — a training search belongs to the Search Indexing context, which is
  where it would have had to go anyway once a `LIKE` on a hundred thousand titles stopped being
  free. The asymmetry is stated on the filter contract, on the query, on the repository question and
  here, so nobody has to guess whether it was an oversight.
- **ADR 0052's consequence stays open.** That record says the trainer's own listing must show the
  withholding reason. It does not, because the reason lives on `AdministrationTrainingHttpResponse`
  and not on `TrainingHttpResponse` — the price of separate contracts, paid here and settled when the
  trainer's own surface learns to show it.
- **A second projection to keep in step.** `ToAdministrationDtoExpression` sits beside
  `ToDtoExpression` for each aggregate, on purpose: a reader comparing them sees in one screen what
  the administration is shown and what a trainer is. The trainings' half stopped being an expression
  once the row learned to name its owner — a column no aggregate carries, so the layered reader
  passes it in and the CQRS reader joins for it. The pairing this bullet asks a reader to compare is
  unchanged; only one of the two is now a method.
- **Rule 165**, `NoRepositoryQuestion_TakesAPredicateOrAnOrdering`. Its sibling refuses
  `ISpecification`; this refuses the bare shape somebody writes once they have understood that a
  specification would be refused. A line at "named criteria" is only a line while nothing anonymous
  crosses it.
- **Two operations on each host**, so the generated client grows two — `Administration_GetTrainers`
  and `Administration_GetTrainings` — which `BothHosts_PublishTheSameOperations` requires of both.

## Alternatives considered

**Leave the four endpoints without a listing.** Defensible for exactly as long as an administrator
has somebody handing them identifiers. There is no such person, and an authority that can only act on
what it already knows the identifier of is not an authority.

**Extend `TrainerHttpResponse` and `TrainingHttpResponse` instead.** Fewer types, one projection,
and it would have closed ADR 0052's open consequence in the same commit — the trainer would read
their own withholding reason for free. Rejected in favour of separation: the two responses have
different audiences, and one type serving both is how a field added for an administrator ends up on
a public profile. The cost is real and is recorded above rather than argued away.

**Filter in memory, after the page.** A page of twenty filtered down to nine, with a total counting
the whole table. It is the version that looks correct in a demo and is wrong in every number it
prints, which is why the end-to-end fact for this is about the *count* and not about the rows.

**Take an `ISpecification` or an expression, and let each caller compose.** The design ADR 0028
removed, arriving by a new door. It would have made these two listings trivial and the next five
inevitable.

**Delegate search to the Search Indexing context now.** The right destination, and premature. That
context is a fake with one consumer; giving it a query surface before it has an index is building
the second system to avoid a `LIKE`. What this record does instead is name the trigger.

**Let an unknown status match nothing.** One less annotation, and it turns a typo into a silence.
An empty page is a legitimate answer to a good question, so it must not also be the answer to a bad
one.

## Verification

- **`NoRepositoryQuestion_TakesAPredicateOrAnOrdering`**, watched failing first: a repository
  question given an `Expression<Func<Trainer, bool>>` named that method and that parameter, and the
  parameter was removed.
- **The filter reaching SQL rather than a materialised page**, in `AdministrationListTest` so both
  hosts answer it: six trainings, two withheld, a page size of two — the count says two. A filter
  applied after paging answers six, or serves a page holding a training nobody withheld.
- **The withdrawn read staying withdrawn for everybody else**: a trainer's token is `403` on both
  listings and an anonymous caller `401`. That refusal is the entire difference between this read and
  the one that was deleted, so it is asserted on both.
- **The term reaching all three trainer columns**, name and contact address alike, over HTTP against
  the real mapping — which is also what proves the two criteria compose rather than replace one
  another.
- **`[KnownStatus]` read off the domain**, including the fact that `Withheld` — declared after the
  annotation's shape existed elsewhere — is accepted without any test or validator naming it.
- **The envelope crossing the mapping intact**, in the layered suite: a page of aggregates becomes a
  page of DTOs and the counts still describe the same set.
