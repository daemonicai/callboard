# ADR-0004 — SQLite holds derived queryable state; narrative stays in files

- **Status:** Accepted
- **Date:** 2026-08-19
- **Deciders:** Emmz (Product Owner), Architect

## Context

`working-context` must answer within a budget of under 3,000 tokens, and its cost must stay flat as a
change lengthens. `record-retrieval` requires derived state to be fully rebuildable from the primary
record, never authoritative, and never committed.

The Architect initially recommended holding no persistent index at all and deriving everything in memory
per invocation, sizing the corpus at 80–120 cards per change.

**That recommendation was wrong and the Product Owner rejected it.** The sizing counted cards and
ignored that the narrative does not disappear under `callboard` — it moves onto the cards. The measured
incumbent was 2.07 MB and 26,769 lines for a single change. Further, `register` places repository-scoped
material above the change and requires card identities to stay resolvable after archive, so the corpus
accumulates monotonically and has no ceiling by design.

## Decision

**SQLite is the derived index**, gitignored, rebuildable from the primary record, never authoritative.

The index holds **derived queryable state only** — status, owner, kind, scope, blocked-on edges, thread
routing state, citation counts, staleness inputs and section rollups. **Comment bodies remain in the
card files.** Narrative retrieval reads the file addressed by card identity.

## Rationale

- **The corpus is unbounded, so scanning it per invocation is not viable.** The Product Owner's
  objection stands on measured evidence from the incumbent.
- **The requirement admits no interesting alternative once volume is granted.** A local, embedded,
  zero-administration, transactional query store over structured data is what SQLite is.
- **Splitting metadata from narrative keeps the index small regardless of narrative growth.** Queries
  that must meet a token budget touch only structured fields; a rebuild scans frontmatter rather than
  rehydrating megabytes of prose. Narrative volume therefore stops affecting query cost at all — which
  is what makes the flat-cost requirement achievable rather than merely hoped for.
- **Keeping the index non-authoritative stays cheap** because correctness of writes rests on the file
  lock and atomic rename (ADR-0003), not on the database.

## Alternatives considered

**No persistent index; derive in memory per invocation.** Would have removed a schema, migrations,
rebuild logic and the whole class of index-versus-record divergence bugs. Rejected on volume, as above.
Recorded because the reasoning that killed it is the reasoning that justifies the index.

**Index the narrative too, with full-text search.** Rejected for v1: the specs require narrative
retrieval *by identifier*, not search. Adding FTS would grow the index in exactly the dimension this
decision exists to keep out of it. Revisit if a search requirement is ever raised.

## Consequences

- A schema and a migration path are needed, and a rebuild command (`record-retrieval`'s rebuild
  requirement) must be implemented and tested, not assumed.
- The index must be gitignored. Committing it would make a non-authoritative artefact look
  authoritative in review.
- Where index and record disagree, the record governs; the index is rebuilt. This must be a tested
  behaviour rather than a documented intention.
- The index is never taken as a lock. Write correctness stays with the file lock, so the index can be
  deleted at any moment without risking data.
