## 1. Project setup and the command surface

- [x] 1.1 Create the .NET 10 solution with a single `src/Callboard` project and a `tests/Callboard.Tests` project
- [x] 1.2 Enable NativeAOT publishing, nullable reference types, and treat warnings as errors
- [x] 1.3 Add `.editorconfig` so `dotnet format` has rules to verify against
- [x] 1.4 Verify each gate command from `design.md` D8 runs non-interactively and exits non-zero on failure
- [x] 1.5 Gitignore the derived index path
- [x] 1.6 Establish the CLI entry point: non-interactive, JSON output for machine commands, card bodies read from stdin, non-zero exit on refusal

## 2. Primary record — card files

- [x] 2.1 Define the frontmatter schema covering every common field from `card-model`
- [x] 2.2 Implement frontmatter parsing and serialization, verifying AOT compatibility before adopting any library
- [x] 2.3 Implement the delimited appended-comment block format, readable unaided and diff-friendly
- [x] 2.4 Implement the scope-shaped directory layout from `design.md` D3
- [x] 2.5 Implement atomic write via temporary file and rename
- [x] 2.6 Implement the per-card advisory lock with a timeout and a failure message naming the card and holder
- [x] 2.7 Test that two concurrent comment appends to one card both survive in a determinate order
- [x] 2.8 Test that a corrupted card file leaves every other card readable

## 3. Derived index

- [x] 3.1 Define the SQLite schema for derived queryable state only — no comment bodies
- [x] 3.2 Implement index population from the primary record
- [x] 3.3 Implement the full rebuild command
- [x] 3.4 Test that destroying the index and rebuilding produces identical answers
- [x] 3.5 Test that where index and record disagree, the record governs and the index is rebuilt
- [x] 3.6 Verify the index is never taken as a lock — deleting it mid-session loses no data

## 4. Card model

- [x] 4.1 Model the seven kinds as a closed union so an unhandled kind is a compile error
- [x] 4.2 Implement kind-prefixed identity allocation that never recycles an identity
- [x] 4.3 Test that a card identity stays resolvable after its change is archived
- [x] 4.4 Implement the scope attribute, refusing `section` scope on a `rule`
- [x] 4.5 Implement ownership with attributed, timestamped handover
- [x] 4.6 Implement append-only comments with structural addressing, replies and resolution
- [x] 4.7 Test that a role mention in prose routes nothing and that an addressed comment does
- [x] 4.8 Test that an appended comment cannot be edited or deleted

## 5. Work lifecycle and sections

- [x] 5.1 Model the block flow states as a closed union with an exhaustive transition table
- [x] 5.2 Implement transitions recording acting role and timestamp; refuse undefined transitions naming what is available
- [x] 5.3 Implement remediation as the same card at an incremented round, ticking no task
- [x] 5.4 Implement `base`, `reviewed_state`, `tasks`, `round` and `blocked_by` on block cards
- [x] 5.5 Refuse briefing a block with no `base` recorded
- [x] 5.6 Implement gate results as label-to-exit-code, with narrative claims carrying no weight
- [x] 5.7 Derive blocked from a non-empty `blocked_by`, preserving flow state throughout
- [x] 5.8 Implement sections as entities carrying status, base commit and supervisor verdict

## 6. Findings

- [x] 6.1 Implement the `finding` card with instrument, extent, `verified_at` and blind spot
- [x] 6.2 Refuse a clean finding lacking either a declared blind spot or an explicit assertion of none
- [x] 6.3 Raise a declared blind spot as an obligation or hazard that does not degrade at section close
- [x] 6.4 Implement the three extent declaration forms, defaulting to block scope and requiring narrowing to be explicit
- [x] 6.5 Implement staleness computation against declared extent, presented as needing re-verification rather than as refutation
- [x] 6.6 Implement the clean-as-argued disposition, excluded from staleness computation and surfaced honestly
- [x] 6.7 Implement finding degradation at section close

## 7. Register

- [x] 7.1 Implement the two-state lifecycle for `rule`, `hazard`, `obligation` and `decision`
- [x] 7.2 Implement `owed_by` on obligations and supersession links on decisions
- [x] 7.3 Implement repository-scoped storage so archive is a directory-level filter with nothing in transit
- [x] 7.4 Test that archiving a change leaves rules, hazards and open questions live and unmoved
- [x] 7.5 Implement rule promotion across scope, preserving identity, text and thread
- [x] 7.6 Implement rule authoring from findings, recording `earned_from` and leaving the findings unchanged
- [x] 7.7 Implement compaction into families by supersession, recording `absorbs` and keeping members retrievable
- [x] 7.8 Implement architect-performed change-scoped compaction at archive
- [x] 7.9 Implement agent-proposed, Product-Owner-decided repository-scoped compaction that applies nothing on its own
- [x] 7.10 Implement citation counting, the soft ceiling as a review trigger, and the uncited-rule human queue
- [x] 7.11 Refuse a hazard raised without a verification condition; discharge a hazard whose condition lapses
- [x] 7.12 Refuse any agent write to the project's agent instruction file, recording the promotion as awaiting a decision

## 8. Review and certification

- [x] 8.1 Implement the binary verdict, refusing any approval carrying unresolved blocking findings
- [x] 8.2 Record `reviewed_state` as the exact state certified, including uncommitted content
- [x] 8.3 Refuse an approval that enumerates no claims and no limits
- [x] 8.4 Implement nits as addressed comments carrying a disposition
- [x] 8.5 Implement `fix-before-land` returning the block to `briefed` with an incremented round
- [x] 8.6 Implement `defer` promoting to an obligation and `decline` promoting to a decision
- [x] 8.7 Refuse leaving `in-review` with any undispositioned nit
- [x] 8.8 Implement `recertify` with individually assertable and refusable claims
- [x] 8.9 Implement per-claim refusal returning the block to `briefed` without re-stamping `reviewed_state`
- [x] 8.10 Enforce at most one recertification per approval
- [x] 8.11 Implement the mechanical preconditions as refuse-only: gates re-run green, difference confined to nit sites
- [x] 8.12 Test that green preconditions confer no claim until a reviewer asserts it
- [x] 8.13 Restrict approval and recertification to `reviewer` and `supervisor`

## 8a. Provisional approval and section-driven landing

- [ ] 8a.1 Scope reviewer remediation to the same card, distinct from section remediation
- [ ] 8a.2 Refuse `land` as an individually invocable transition, naming section close as the only door
- [ ] 8a.3 Implement section close landing every block in the section as one write, or landing none
- [ ] 8a.4 Refuse section close where any block is not `approved`
- [ ] 8a.5 Refuse section close where any block's `reviewed_state` does not match its current state
- [ ] 8a.6 Refuse section close on any block carrying a non-zero or absent expected gate
- [ ] 8a.7 Implement a first-time supervisor finding creating a new remediation block card, ticking nothing
- [ ] 8a.8 Implement `finding-recurred` returning the owning card to `briefed` at a higher round
- [ ] 8a.9 Refuse a recurrence that would create a second card for a finding a card already owns
- [ ] 8a.10 Implement one verdict both returning a card for a recurrence and creating one for a new finding
- [ ] 8a.11 Refuse `finding-recurred` targeting a task-implementing block
- [ ] 8a.12 Retain every supervisor verdict against the section, never overwriting an earlier one
- [ ] 8a.13 Refuse a third or subsequent `request-changes` verdict absent a recorded Product Owner authorisation
- [ ] 8a.14 Derive the verdict count from the record at request time, never storing it
- [ ] 8a.15 Implement recording that authorisation, naming the section and the reason
- [ ] 8a.16 Test that `landed` is unreachable except through the block's section closing
- [ ] 8a.17 Refuse acting on a block whose stored `round` disagrees with its transition history, reconciling neither
- [ ] 8a.18 Test that every round-incrementing transition advances the field and the history in one write

## 9. Process enforcement

- [ ] 9.1 Implement the refusal reporting format: name the rule, state what would satisfy it, record role and timestamp
- [ ] 9.2 Refuse approval from a role other than reviewer or supervisor
- [ ] 9.3 Refuse leaving `in-review` with threads addressed to the acting role unresolved
- [ ] 9.4 Refuse section close over open obligations owed by that section
- [ ] 9.5 Refuse section close over open undeferred questions
- [ ] 9.6 Refuse section close over unresolved addressed threads, and surface threads older than one round as a prompt
- [ ] 9.7 Refuse marking a question answered without a decision reference or an inline answer
- [ ] 9.8 Refuse advancing a card blocked by an open Product Owner question
- [ ] 9.9 Refuse archive over open change-scoped obligations owed by no remaining section
- [ ] 9.10 Add a test per refusal rule demonstrating it fires, per the amended S5

## 10. Working context and derived state

- [ ] 10.1 Implement the four-part working-context response in the specified order
- [ ] 10.2 Implement queue composition from ownership plus unresolved addressed threads
- [ ] 10.3 Include the previous round's verdict on a remediation
- [ ] 10.4 Implement priority assembly with cumulative character-based measurement and margin
- [ ] 10.5 Truncate narrative only, never the register or brief, and state every omission
- [ ] 10.6 Test that the response fits the budget at a corpus comparable to the measured change
- [ ] 10.7 Test that response size late in a change stays within 20% of its size at the start
- [ ] 10.8 Implement the derived state summary with every figure computed at request time
- [ ] 10.9 Refuse any attempt to store a hand-entered count or next-step pin
- [ ] 10.10 Derive escalation severity from question ownership, halting dependents only for Product Owner questions

## 11. Narrative retrieval and export

- [ ] 11.1 Implement full card retrieval by identity, including the complete thread
- [ ] 11.2 Verify no narrative outside the caller's queue reaches the working-context response
- [ ] 11.3 Implement section and whole-change export approximating the incumbent's shape
- [ ] 11.4 Verify every class of content previously written to `DEVLOG.md` has a home and is reconstitutable
- [ ] 11.5 Implement closed cards leaving default queries while remaining in the record and exports

## 12. Human view

- [ ] 12.1 Generate a single self-contained HTML file with inline CSS, no server and no build step
- [ ] 12.2 Render cards by column and owner, blocked-on relationships, and open questions with their owners
- [ ] 12.3 Verify the view is read-only and alters no state

## 13. Integration with the Apply Workflow

- [ ] 13.1 Extend the hook boundary to deny agent writes to the card store
- [ ] 13.2 Verify the record stays readable and the loop proceeds unenforced when the tool cannot run
- [ ] 13.3 Verify a card's status, owner, scope and history are determinable from the file alone
- [ ] 13.4 Document the commands the worker, reviewer and supervisor agents use in place of `DEVLOG.md`
