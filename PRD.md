# PRD — `callboard`

**Status:** Draft for `discovery`
**Author:** Emmz
**Date:** 2026-08-19
**Phase:** Pre-discovery. No technology decisions are made in this document; the few that appear are
flagged explicitly as deferred to `design`.

---

## 1. Summary

`callboard` replaces the append-only `DEVLOG.md` currently used as the shared working channel between
the main Claude Code session (Analyst / Architect / Orchestrator) and its `worker`, `reviewer` and
`supervisor` subagents.

The name is taken from the backstage noticeboard: the place everyone consults *before* they go on,
rather than a log written *after* the fact. That distinction is the product thesis.

`callboard` is a work-tracking and process-enforcement tool for a multi-agent development loop. It
holds cards for units of work, questions, obligations, decisions, standing rules and hazards; it
routes work between agents by ownership; it returns a minimal, role-scoped working context on demand;
and it refuses state transitions that violate the process.

---

## 2. Problem

### 2.1 The incumbent

`DEVLOG.md` is a single append-only Markdown file, organised by `## N.` section mirroring `tasks.md`,
with a hand-maintained `## NEXT` pin at the bottom. Every agent message is prefixed `**[role]**`.
Agents address each other in prose (`@worker`, `→ @reviewer`).

It works well up to roughly two sections. It degrades badly after that.

### 2.2 Measured state of a single change

Figures from the `git-backed-content-core` change, taken at §12 (twelve of thirteen sections closed):

| Metric | Value |
|---|---|
| File size | 2.07 MB |
| Lines | 26,769 |
| Agent messages | 433 |
| Median message | 3,896 chars |
| Mean message | 4,735 chars |
| Largest single message | 20,112 chars |
| `architect` | 216 msgs / 588k chars |
| `worker` | 94 msgs / 470k chars |
| `reviewer` | 91 msgs / 710k chars |
| `supervisor` | 25 msgs / 281k chars |
| `product-owner` | 7 msgs / 1k chars |
| `@role` mentions (prose only, non-routing) | 434 |

`reviewer` and `supervisor` together account for roughly half the file. Both are audit output: high
value at the moment of writing, near-zero value on re-read, and never again addressed to anyone.

### 2.3 Failure modes observed

These are drawn from the change's own record, not hypothesised.

1. **Unbounded read cost.** An agent needing its next instruction must either ingest the whole file or
   guess at a range. The live working set at any moment is a few thousand characters inside two
   million.
2. **Hand-maintained state drifts.** The `## NEXT` pin is rewritten by hand. Its own text records that
   *"a hand count here was wrong once and rode along through two blocks."* A separate pin entry
   asserted a Product Owner decision was still owed after that decision had shipped.
3. **No addressing.** `@reviewer` is decoration. No agent can ask "what is assigned to me and
   unanswered" without reading prose.
4. **No status.** Whether an obligation raised in §4 is still live cannot be determined without
   reading §4. This is what forced the manual extraction of `LEDGER.md` on 2026-08-19 — a distillation
   pass that is itself a correctness risk.
5. **Namespace collision.** Reviewer verdicts are pasted with `## Verdict:` headings at the same level
   as `## 4.` section headings. The document outline is destroyed; heading-based navigation no longer
   works.
6. **Recorded rules are not applied rules.** Standing rules and live hazards live at the bottom of a
   2 MB file. The `Reviewed-state:` fingerprint check (a rule in `CLAUDE.md`) was skipped twice in one
   change, both times over correct code.
7. **Questions do not survive their context.** A question parked by the `invite-only-authentication`
   supervisor ("which git email do we stamp, and what for an account with none?") resurfaced only
   because a human remembered it, in a *subsequent, separate change*, where it became decision D10.

### 2.4 Root cause

`DEVLOG.md` fuses three documents with incompatible lifecycles:

| | Churn | Live set | Read by |
|---|---|---|---|
| **Routing** — briefs, reports, verdicts, whose turn it is | Very high | Tiny | Only the agent whose turn it is |
| **Register** — forward obligations, standing rules, live hazards, decisions | Low | ~40 items, long-lived | Everyone, every turn |
| **Narrative** — reasoning, instrument failures, what was checked clean | Append-only | Zero | Nobody, until archive |

A single append-only file serves the third well and the first two badly. `callboard` separates them
and gives each the access pattern it needs: routing is queried and mutated, the register is *injected*
rather than searched, and narrative is attached to cards so it is retrievable by identifier instead of
by scrolling.

---

## 3. Goals

**G1 — Bound the read cost of "what do I do next" to a small, predictable budget** that does not grow
with the length of the change.

**G2 — Make the register unmissable.** Standing rules and live hazards are supplied with every brief
rather than stored somewhere an agent could fail to look.

**G3 — Derive process state rather than maintain it by hand.** No hand counts. No hand-written pin.

**G4 — Enforce the process mechanically.** Where a rule in `CLAUDE.md` is currently a request, make it
a refusal. Recording that a rule was broken is strictly worse than preventing the break.

**G5 — Give questions and obligations an identity that outlives the work that raised them**, including
across change boundaries and into archived changes.

**G6 — Lose no narrative.** Everything currently written to `DEVLOG.md` remains recorded and
retrievable; it simply stops being on the default read path.

**G7 — Remain legible without the tool.** The record is a first-class repository artefact: readable by
a human, diffable in review, and readable by an agent using ordinary file reads if the tool is
unavailable or the model wants raw context.

---

## 4. Non-goals

- **Not a project management tool.** No estimates, no velocity, no burndown, no sprints, no WIP
  limits. Agents do not have a work-in-progress problem.
- **Not multi-user.** One human, one repository, one change at a time.
- **Not a replacement for `tasks.md`, `design.md`, `proposal.md` or the OpenSpec artefacts.**
  `callboard` references task numbers; it does not own them.
- **Not a replacement for git.** Commits, branches and history remain authoritative for code.
- **No web service.** A local, static rendering for human consumption is in scope (see F9); a server,
  authentication or hosting is not.
- **Not a general-purpose issue tracker.** Scope is one change's working loop.

---

## 5. Users

| User | Nature | Primary need |
|---|---|---|
| `architect` (main session) | Agent | Carve briefs, land work, close sections, derive overall state |
| `worker` | Subagent | Receive one brief with everything needed to execute it, and nothing else |
| `reviewer` | Subagent | Receive a card plus the exact reviewable state; record a verdict |
| `supervisor` | Subagent | Review a whole section; record a verdict against a commit range |
| `product-owner` | Human (Emmz) | Answer escalations; see overall state at a glance; audit the loop |

Agents interact through a machine surface. The Product Owner interacts through the same surface plus a
human-readable view.

---

## 6. Domain model

### 6.1 Card

One entity type with a `kind` discriminator, and two lifecycle families.

Fields common to all kinds:

| Field | Notes |
|---|---|
| `id` | Stable, human-quotable, kind-prefixed (e.g. `B-0042`, `Q-0007`, `D-0019`) |
| `kind` | `block` \| `question` \| `obligation` \| `rule` \| `hazard` \| `decision` |
| `title` | One line |
| `section` | The `## N.` section that raised it. Nullable for change-spanning register entries |
| `status` | Kind-dependent; see 6.2 and 6.3 |
| `owner` | The role whose turn it is. **The single field `DEVLOG.md` cannot express** |
| `created`, `updated` | Timestamps |
| `body` | Markdown. The brief, the question, the rule text |
| `comments[]` | Append-only thread; see 6.4 |

### 6.2 Flow kinds

`block` and `question` move through states.

**`block`** — a unit of work, corresponding to today's "block A / block C1 / block D2".

Additional fields: `tasks[]` (`N.M` references into `tasks.md`), `round` (remediation round),
`base` (commit the brief was carved against), `reviewed_state` (commit the reviewer actually
reviewed), `gates{}` (label → exit code), `blocked_by[]`.

```
drafting ──▶ briefed ──▶ building ──▶ in-review ──┬──▶ approved ──▶ landed ──▶ closed
                  ▲                               │
                  └──── changes-requested ◀───────┘
                            (round += 1)

any flow state ──▶ blocked ──▶ (returns to prior state on unblock)
```

A remediation is **the same card at a higher round**, not a new card. This matches existing practice
("still the same remediation block; no new task numbers, ticks nothing") and means one card's thread
is the complete audit trail of one unit of work.

**`question`** — an open matter requiring an answer before dependent work can proceed.

Additional fields: `owner` (who must answer), `blocking[]` (cards that cannot advance),
`answer_ref` (the `decision` card recording the answer), `answer_inline` (boolean escape hatch for
trivia), `defer_to` (target change or section, valid only with `status: deferred`).

```
open ──┬──▶ answered      (requires answer_ref OR answer_inline)
       ├──▶ deferred      (requires defer_to)
       └──▶ withdrawn     (requires a reason in the thread)
```

Questions are cards rather than comment threads specifically so they can outlive their originating
work — including across archived changes, which the D10 case demonstrates is a real requirement.
Escalation severity is *derived*, not stored: `owner == product-owner` means stop-and-ask and halts
the blocked cards; any other owner leaves blocked cards free to continue on other fronts.

### 6.3 Register kinds

`obligation`, `rule`, `hazard`, `decision` have no columns. They are `open` or `discharged`
(`rule` and `decision` are effectively permanent; `discharged` means superseded).

Additional fields: `owed_by` (the section expected to discharge an obligation), `supersedes` /
`superseded_by` (for decisions).

The defining behaviour is not their lifecycle but their **delivery**: `rule` and `hazard` cards are
injected into every brief unconditionally. They currently total roughly forty short entries. The cost
of always supplying them is negligible; the cost of an agent missing one is measured in hours, and has
been paid repeatedly.

### 6.4 Comments

Append-only entries on a card: `{id, role, timestamp, body, re (parent comment id), to (role),
resolved (boolean)}`.

A comment with `to` set and `resolved: false` is a live thread and appears in that role's queue. This
is what turns the current 434 decorative `@role` mentions into actual routing.

Inline threads and `question` cards coexist deliberately: threads are frictionless and belong to the
card, questions are durable and belong to the change. See Open Question OQ-3 on managing drift between
them.

---

## 7. Functional requirements

### F1 — Role-scoped working context (the core operation)

Given a role, return that role's complete working context and nothing else:

1. Standing rules and live hazards — always, unconditionally, first.
2. The role's queue, ordered: cards where `owner` matches, plus cards with unresolved threads
   addressed to that role.
3. The top queue item in full: body/brief, `base`, referenced tasks, constraints, unresolved threads
   addressed to the caller, and the previous round's verdict where applicable.
4. Nothing else. No closed cards. No other roles' queues. No narrative from prior sections.

**This response must fit a small, stated token budget — target under 3,000 tokens.** The budget is a
hard product requirement, not an aspiration; it is the primary measure of whether `callboard`
succeeds. Where a card's own content would exceed the budget, the response truncates the *narrative*
and never the register or the brief, and says so explicitly.

### F2 — Derived state summary

Reconstruct, by derivation, what the hand-maintained `## NEXT` pin currently holds: open sections,
task completion counted from `tasks.md` itself, live obligations with the section that owes them, open
questions with who owes an answer, and every card blocked and on what.

No figure in this output may be hand-entered anywhere.

### F3 — Card lifecycle operations

Create, brief, claim, report, review, request changes, approve, land, close, block, unblock. Each
transition records the acting role and timestamp, and each is subject to F5.

### F4 — Threads

Add a comment; address it to a role; reply to a comment; resolve a thread. Unresolved addressed
threads surface in the target role's queue (F1).

### F5 — Refusal rules

Transitions that violate the process are refused with a clear reason. Minimum set, each traceable to
an observed failure:

| Rule | Refuses | Failure it closes |
|---|---|---|
| R1 | `land` where the card's `reviewed_state` ≠ current `HEAD` | Fingerprint gap; opened twice in one change |
| R2 | `land` where any gate on the card is non-zero or absent | "A gate passed only when you saw `LABEL_EXIT:0`" |
| R3 | `approve` by a role other than `reviewer` or `supervisor` | Role boundary |
| R4 | Leaving `in-review` with unresolved threads addressed to the caller | Questions lost in a verdict |
| R5 | Closing a section with `obligation` cards `owed_by` it still open | "Several are owed by no section that remains" |
| R6 | Closing a section with `question` cards open in it, unless `deferred` with a `defer_to` | Questions dying with their context |
| R7 | `answered` on a question without `answer_ref` or `answer_inline` | Answers agreed in conversation and never written down |
| R8 | Advancing a card whose `blocked_by` includes an open product-owner question | Working past a stop-and-ask |

Rationale, and the strongest argument for a tool over a file: **a file can only record; a tool can
refuse.** Every failure in §2.3 is mechanically checkable. This mirrors the existing move from
requested boundaries to hook-enforced ones (agents cannot write git, `tasks.md`, the `Makefile`,
`CLAUDE.md` or `.claude/`), which demonstrably worked.

### F6 — Gate recording

Gate results are recorded as label → exit code on the card, and are the only accepted evidence a gate
passed. Concluding a gate passed from reading its output ceases to be possible because the transition
consults the recorded exit code, not prose.

### F7 — Narrative retrieval

Full card content including all comments, by identifier. This is where the material currently
occupying 2 MB lives. It is retrievable, quotable and never on the default read path.

### F8 — Export and archive

Render a section, or a whole change, as a single Markdown document in approximately the current
`DEVLOG.md` shape, for archival alongside the OpenSpec artefacts. Closed cards move out of the default
query set without leaving the repository.

### F9 — Human view

A local, static, human-readable rendering of the board for the Product Owner: columns, owners,
blocked-on relationships, open questions. Read-only is acceptable for v1.

### F10 — Index rebuild

Rebuild all derived state from the primary record. Derived state is disposable by definition (see
§8.2).

---

## 8. Data and durability requirements

### 8.1 Primary record

The primary record is a set of plain-text, human-readable, git-committed files inside the repository —
one file per card, structured metadata plus Markdown body plus appended thread.

Requirements this shape must satisfy:

- **Diffable.** A pull request shows exactly which cards moved and how.
- **Readable without the tool.** An agent can read a card with an ordinary file read; the tool is an
  optimisation and an enforcement layer, not a gatekeeper on comprehension.
- **Collision-tolerant.** Multiple subagents may act concurrently. Card-level isolation means the
  common case is contention-free; the realistic collision is two roles commenting on one card, which
  requires advisory locking or an equivalent.
- **Recoverable.** Corruption of any single card must not compromise the rest.

Expected volume, extrapolating from the measured change: 80–120 cards per change, forty-odd of which
are register entries carried between changes.

### 8.2 Derived index

A **SQLite** database holds derived state: queue ordering, blocked-on graph, section rollups, and
whatever else F1 and F2 need to answer within budget. It is:

- gitignored,
- fully rebuildable from the primary record (F10),
- never authoritative for anything.

SQLite is specified here rather than deferred because the requirement — a local, embedded, zero-
administration, transactional query store over a small dataset — admits no interesting alternative,
and pretending otherwise would waste a design round.

### 8.3 Register portability

Register cards (`rule`, `hazard`, and unresolved `obligation` / `question`) must be carryable from one
change to the next. This is a first-class requirement, not an export convenience: the D10 case is a
question crossing a change boundary, and the standing rules earned in this change are precisely the
asset worth keeping.

---

## 9. Migration

The existing `DEVLOG.md` will **not** be parsed or auto-imported.

§0–§11 are closed with a supervisor `Approve`. They move wholesale to an archive path and are never
read by the tool. Live state is migrated by hand, in a single session:

- `LEDGER.md`'s 32 forward obligations → `obligation` cards, each with `owed_by`
- Standing rules earned in §0–§11 → `rule` cards
- Live hazards → `hazard` cards
- D8–D18 → `decision` cards
- §12's live state → three or four `block` cards and its open questions

Estimated 50–60 cards, hand-checked. An auto-import would produce 433 cards of unverified status,
which is worse than nothing.

---

## 10. Success criteria

| # | Criterion | Measure |
|---|---|---|
| S1 | Working-context response stays within budget | F1 output < 3,000 tokens at any point in a change of comparable size to the measured one |
| S2 | Working-context cost is flat, not growing | F1 output size at §12 within 20% of its size at §1 |
| S3 | Register is never missed | Every brief issued contains the full current rule and hazard set — verifiable by inspection of the output, not by policy |
| S4 | No hand-maintained state | Zero figures in F2 output are hand-entered; the `## NEXT` pin ceases to exist |
| S5 | Refusals fire | Each of R1–R8 has a test that demonstrates the refusal, and at least R1 and R2 fire at least once in real use during the first change run under `callboard` |
| S6 | Nothing lost | Every class of content currently written to `DEVLOG.md` has a home, and F8 can reconstitute a section in the incumbent's shape |
| S7 | Legible unaided | A reader with no tool access can determine a card's status, owner and history from the file alone |

---

## 11. Open questions for `discovery`

**OQ-1 — Interface shape.** Whether `callboard` is invoked as a command-line tool, exposed as an MCP
server, or both. Relevant considerations, offered as input rather than conclusion: MCP tool
definitions occupy context in every agent's prompt whether used or not, which is in tension with G1;
the existing hook layer already polices command invocation and would need extending to cover a second
surface; a command-line surface has an obvious degraded mode (agents read card files directly)
whereas an MCP-only store does not; MCP resources are a better fit than shelling out for attaching
card content. **Deferred to `design`.**

**OQ-2 — Token budget mechanism.** Whether F1's budget is enforced by measurement and truncation, by
construction (bounding what may be included), or by a card-authoring constraint upstream. Affects
whether card bodies need a length policy.

**OQ-3 — Inline threads versus question cards.** Raising a `question` card has friction; commenting
with `to:` does not. Agents will drift toward the frictionless option and durable questions will be
lost inline — the exact failure `question` cards exist to prevent. Candidate mitigation: detect an
unresolved addressed comment older than one round and prompt for promotion to a card, rather than
attempting to prohibit inline questions. Needs a decision.

**OQ-4 — Section ownership.** Whether sections are entities in their own right (with status, base
commit and a supervisor verdict) or purely a grouping attribute on cards. R5 and R6 imply the former.

**OQ-5 — Relationship to `tasks.md`.** Cards reference `N.M` task numbers, and F2 counts completion
from `tasks.md` directly. Whether `callboard` should ever *write* tick marks, or whether that stays
with the architect and its hook boundary, needs settling.

**OQ-6 — Multiple concurrent changes.** Scope is currently one change at a time. Whether register
portability (§8.3) implies a store above the change, or is satisfied by an explicit carry-forward step
at archive time.

**OQ-7 — Verdict vocabulary.** The incumbent uses `Approve`, `Approve with nits`, `Request changes`.
Whether "approve with nits" is a distinct status, an approval carrying unresolved non-blocking
threads, or an approval plus generated `obligation` cards.

**OQ-8 — Blocked semantics.** Whether `blocked` is a status (as drawn in 6.2) or a derived condition
of having a non-empty `blocked_by`. The latter is likely cleaner and removes a state from the machine.

---

## 12. Appendix — incumbent conventions worth preserving

Conventions from `DEVLOG.md` that earned their place and should survive into `callboard`'s model:

- **`Base:`** — the commit a brief was carved against, posted before the first block of a section.
- **`Reviewed-state:`** — the commit fingerprint the reviewer actually reviewed. Becomes R1.
- **`Gates:` with `LABEL_EXIT:0`** — exit code as the only evidence of a pass. Becomes F6 and R2.
- **`Claim:` / `Instrument:` / `Blind spot:`** — the structure worker and reviewer findings already
  use. A strong candidate for a structured comment template rather than free prose.
- **Remediation keeps the block number and ticks nothing** — becomes `round`.
- **Stop-and-ask escalation to the Product Owner** — becomes a `question` card owned by
  `product-owner`, with R8.
- **"Checked clean — recorded so it is not re-litigated"** — a recurring section in reviews with no
  home in the model as drafted. Possibly an `obligation` in reverse, possibly a comment category.
  Worth raising in discovery.
