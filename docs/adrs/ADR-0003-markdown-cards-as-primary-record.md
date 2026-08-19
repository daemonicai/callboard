# ADR-0003 — Git-committed Markdown files are the primary record, one file per card

- **Status:** Accepted
- **Date:** 2026-08-19
- **Deciders:** Emmz (Product Owner), Architect

## Context

`record-retrieval` requires a record that is legible unaided, diffable per card, damage-contained per
card, and tolerant of concurrent access by several agents. `register` requires that archiving a change
act as a filter which closes change-scoped cards and leaves wider-scoped cards untouched — with nothing
in transit, because transit is where material gets dropped.

## Decision

The primary record is **plain-text files committed to the repository, one file per card**: YAML
frontmatter carrying the structured fields, a Markdown body, and comments appended as delimited blocks.

The layout reflects card scope directly, so that archive is a directory-level operation:

```
callboard/
  register/          repository-scoped: rule, hazard, question
  decisions/         capability-scoped, mirroring the spec paths they bind
  changes/<name>/    change-scoped: block, obligation, finding, section
```

Writes take a per-card advisory lock with a timeout, and are made to a temporary file renamed into
place, so an interrupted write cannot leave a partially-written card.

## Rationale

- **One file per card gives all three properties at once.** A card's movement is one file's diff.
  Corruption is bounded to one card. A reader with no tool opens one file and sees status, owner, scope,
  body and full thread.
- **Appending a comment stays a clean diff** even on a card carrying a long thread, because the append
  is at the end of the file.
- **Scope-shaped directories make archive structural.** Archive touches `changes/<name>/` and nothing
  else, so repository-scoped material is not moved, rewritten or handed over — it is simply not in the
  directory being archived. This is the mechanical realisation of "archive is a filter, not a handoff".
- **Atomic rename plus advisory locking** is what the requirement anticipated, and keeps correctness
  independent of the derived index (see ADR-0004).

## Alternatives considered

**A directory per card with one file per comment.** Concurrent appends would never collide and damage
would be contained to a single comment. Rejected: the measured change carried 433 messages, which would
become 433 files per change, and preserving thread order still requires atomic sequence allocation — so
it trades a lock for a different concurrency problem while multiplying file count.

**Card file plus a separate thread file.** Keeps metadata small and stable while the thread grows.
Rejected because a card's state and the history that produced it stop being a single diff, which is
precisely what makes the record reviewable.

**A single file per section or per change.** Rejected outright — it reproduces the incumbent's failure.

## Consequences

- A card with a long thread becomes a large file. This is acceptable: the working-context path never
  reads full threads, and narrative retrieval is by identity.
- Frontmatter must be parsed by an AOT-compatible library, or hand-rolled against a deliberately narrow
  schema. Adopting a YAML library requires checking it under NativeAOT (ADR-0002).
- Lock acquisition needs a timeout and a clear failure, or a crashed agent leaves a card unwritable.
- Card files are committed, so the repository grows with narrative. This is intended — the record is a
  first-class repository artefact.
