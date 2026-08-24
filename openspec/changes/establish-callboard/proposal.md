## Why

`DEVLOG.md` — the append-only Markdown channel shared by the main session and its `worker`, `reviewer`
and `supervisor` subagents — works to about two sections and then degrades. Measured at §12 of the
`git-backed-content-core` change: 2.07 MB, 26,769 lines, 433 messages, mean message 4,735 characters.
The live working set at any moment is a few thousand characters inside two million.

The root cause is that one file fuses three documents with incompatible lifecycles — high-churn
**routing** (whose turn it is), low-churn **register** (standing rules, live hazards, forward
obligations, decisions) read by everyone every turn, and write-only **narrative** nobody reads until
archive. An append-only file serves the third well and the first two badly.

The consequences are not hypothesised; each is drawn from the change's own record. A hand-maintained
`## NEXT` pin whose own text admits "a hand count here was wrong once and rode along through two
blocks". `@reviewer` as decoration, with no agent able to ask what is assigned to it. Obligation status
undeterminable without re-reading the section that raised it, which forced a manual `LEDGER.md`
extraction that is itself a correctness risk. A `Reviewed-state:` fingerprint rule skipped twice in one
change. A question parked in one change resurfacing in a later one only because a human remembered it.

The decisive argument for a tool over a file: **a file can only record; a tool can refuse.** Every
failure above is mechanically checkable. This repository has already made the same move once — agents
cannot write git, `tasks.md`, the `Makefile`, `CLAUDE.md` or `.claude/`, enforced by hooks rather than
requested in prose — and it worked.

## What Changes

`callboard` replaces `DEVLOG.md` as the working channel. It holds cards for units of work, questions,
findings, obligations, decisions, standing rules and hazards; routes work by ownership; returns a
minimal role-scoped working context on demand; and refuses transitions that violate the process.

- **Separate the three lifecycles.** Routing becomes queried and mutated state; the register is
  *injected* into every brief rather than stored somewhere an agent could fail to look; narrative
  attaches to cards and is retrieved by identifier instead of by scrolling.
- **Bound the read cost of "what do I do next"** to a small stated budget that does not grow with the
  length of the change.
- **Derive process state instead of maintaining it by hand.** No hand counts; the `## NEXT` pin ceases
  to exist.
- **Turn process rules into refusals.** Landing against a stale fingerprint, landing on an unrecorded
  gate, approving from the wrong role, closing a section over open obligations or questions — all
  refused rather than noted afterwards.
- **Give questions, obligations and decisions identity that outlives the work that raised them**,
  including across change boundaries and into archived changes.
- **A store above the change.** Rules, hazards and questions belong to the repository; obligations
  belong to a change and are promotable. Archive is a *filter*, not a handoff — nothing is in transit,
  so nothing can be dropped there.
- **BREAKING — `Approve with nits` is removed as a verdict.** Approve becomes binary and certifies one
  SHA. Each nit takes an architect-chosen disposition instead: fix-before-land, defer, or decline.
- **A certification covers one state and is spent when that state changes.** There is deliberately no
  operation for re-asserting an approval's claims over an amended state: a fix confined to the sites a
  reviewer named is exactly where an unnoticed break hides, so an amended block is reviewed afresh
  rather than re-certified over the difference.
- **Lose no narrative.** Everything currently written to `DEVLOG.md` stays recorded and retrievable; it
  simply leaves the default read path. The record stays legible without the tool.

## Capabilities

### New Capabilities

- `card-model`: the single card entity with its `kind` discriminator, stable quotable identity, scope
  attribute, ownership, and append-only addressed comment threads.
- `work-lifecycle`: `block` cards through drafting → briefed → building → in-review → approved → landed
  → closed, with remediation as the same card at a higher round, gates as recorded exit codes, blocked
  derived from `blocked_by`, and sections as entities carrying status, base commit and verdict.
- `review-certification`: binary approve certifying one SHA, certification text written to be legible
  to a reviewer who did not author it, an approval spent by any change to the state it certifies, and
  nits as comments carrying a disposition.
- `findings`: clean findings with instrument, declared extent and blind spot; staleness computed when
  covered code moves; a separate disposition for findings that argue rather than measure and so cannot
  be re-verified.
- `register`: `rule`, `hazard`, `obligation` and `decision` cards — their scopes, unconditional
  injection into every brief, retrospective promotion, and compaction of rules into families by
  supersession rather than deletion.
- `working-context`: the budget-bounded role-scoped context response, and the derived state summary that
  replaces the hand-maintained pin.
- `process-enforcement`: the refusal rules, the section-close gates, and the archive gate.
- `record-retrieval`: the primary record's legibility and diffability without the tool, narrative
  retrieval by identifier, rebuildable derived state, export in the incumbent's shape, and a read-only
  human view.

### Modified Capabilities

None — this is the project's first change and `openspec/specs/` is empty.

## Impact

- Replaces `DEVLOG.md` as the working channel for the OpenSpec apply loop. The existing file is **not**
  parsed or auto-imported; §0–§11 are closed and move wholesale to an archive path. Live state is
  migrated by hand (~50–60 cards). An auto-import would produce 433 cards of unverified status.
- Migration is *enabled* by this change but performing it is a later act; `callboard` must be able to
  receive the migration, not carry it out.
- `tasks.md` is read, never written. It stays with the architect behind its existing hook boundary.
- `CLAUDE.md` stays agent-unwritable. `callboard` holds repository-scoped rules; promoting one into
  `CLAUDE.md` remains a Product Owner act, because two writable constitutions leave no way to tell which
  binds.
- Does not replace git, `tasks.md`, `design.md`, `proposal.md` or any OpenSpec artefact. Card task
  references point into `tasks.md`; they do not own it.
- The `worker` / `reviewer` / `supervisor` agent definitions and the hook layer will need to change to
  address `callboard` instead of `DEVLOG.md`. Out of scope here; flagged for the Architect.

## Divergences from `PRD.md`

`PRD.md` (2026-08-19, Emmz) was drafted for discovery. The discovery interview amended it. Where the two
disagree, **this proposal and its specs are authoritative**; the PRD stands as the evidence base for §2's
measured failure modes.

| PRD as drafted | Amended | Why |
|---|---|---|
| Six card kinds (§6.1) | Seven — `finding` added | Clean findings carry an instrument, extent, blind spot, `verified_at` and a staleness computation, and degrade at section close; rules have none of those and must survive it. Mechanically distinct kinds. |
| `rule` fixed at repository scope | `rule` carries `scope: change \| repository` | The PRD fixed the scope, then described a three-rung promotion ladder that needs it to vary. Finding → rule is *authoring* (the promoted text is never the finding's text); change rule → repository rule is promotion of one card. No section scope: a rule applying to one section is a constraint in a brief. |
| §4 "one change at a time" vs §8.3 register portability (OQ-6) | One store above the change; archive filters | The only cross-change carry on record survived on human memory, not on a handoff. A handoff is transit and transit drops things. |
| OQ-7 "approve with nits" as a verdict variant | Deleted; approve is binary | Fusing certification with a finding set produced two of §5 block A's three review rounds — nits applied after an Approve mean the committed state is not the certified state, which R1 already refuses. |
| Nits undifferentiated | Nit disposition drives promotion; `decline` → `decision`, not `obligation` | A nit accepted as-is is reasoning about why the code is right — the same species as D8–D11. Filing it as a discharged obligation buries a decision in the debt register. |
| Register cost "negligible, ~40 entries" (§6.3) | Rules compact into families; hazards self-limit | Refuted by measurement: this one change earned ~15–20 standing rules across 66 rule-earning passages. Forty entries is one change's output, not steady state. Rules and hazards need different treatments. |
| §10 S5 gated completion on real-world use | Dogfooding validates, does not gate | S5 becomes: R1–R8 each have a test. |
| OQ-3 detect-and-prompt on inline drift | Refuse at section close, with the nudge as a batch-limiter | The classification is not knowable at raise time — durability is a property of the answer, not the question. Promote retrospectively at the checkpoint where the answer is known. |
| OQ-4 sections possibly a grouping attribute | Sections are entities | Required by finding degradation, decisions targeting a future section, the section-close gates, and supervisor verdicts against a commit range. |
| OQ-8 `blocked` possibly a status | Derived from non-empty `blocked_by` | Removes a state from the machine, as the PRD itself suspected. |
| OQ-5 open | `tasks.md` is read-only | Two writers to one file is the ambiguity the `CLAUDE.md` boundary already rejected. |
| §8.2 specifies SQLite | Deferred to the Architect | Discovery makes no technology decisions. The requirement is a rebuildable, non-authoritative derived index; the store is the Architect's call. |
| OQ-1, OQ-2 | Still deferred to design | Interface shape (CLI / MCP / both) and the budget-enforcement mechanism. |

**Recorded gap:** no observed case of a durable question lost to inline drift. That failure is inferred
from the incentive structure and from the one cross-change question surviving on human memory — it is
not measured, and should not later be cited as though it were.
