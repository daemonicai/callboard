## Purpose

Governs how a unit of work moves from a carved brief to a closed block — its states, its remediation
rounds, the gate evidence it must carry, what it is blocked on, and the sections that group and close
it.

## ADDED Requirements

### Requirement: Block cards move through a defined flow

A `block` card SHALL occupy exactly one of `drafting`, `briefed`, `building`, `in-review`, `approved`,
`landed` or `closed`, and SHALL move between them only along the defined transitions:

```
drafting ──▶ briefed ──▶ building ──▶ in-review ──┬──▶ approved ──▶ landed ──▶ closed
                ▲                                 │        │
                ├──── changes-requested ◀─────────┤        │
                ├──── fix-before-land ◀───────────┘        │
                └──── finding-recurred ◀───────────────────┘
                           (round += 1 on all three)
```

`changes-requested` and `fix-before-land` both leave `in-review`; `finding-recurred` leaves
`approved`. They are distinct named transitions because the name is recorded in the card's history —
see `review-certification` for what raises each.

`finding-recurred` is a supervisor returning a remediation card whose finding it reports still
unresolved; it is the only transition a supervisor drives directly, and it never targets a
task-implementing block.

`approved` is terminal for a block that implements tasks. Once a reviewer has certified such a block it
does not go back to work: it waits, and it lands when its section closes. No transition reopens it —
not the architect's, not the supervisor's. Work found wanting in an approved block becomes a new
remediation block in that section, because a supervisor reviews the section as a whole and its findings
routinely span more than one block, belonging to no single block's card. The one card that can return
from `approved` is a remediation card, through `finding-recurred`, because that card *is* the finding
and its thread is that finding's whole history.

Every transition SHALL record the acting role and the time it occurred.

#### Scenario: Legal transition is recorded

- **WHEN** a `briefed` block is claimed by a worker
- **THEN** the block moves to `building` and the system records the acting role and timestamp

#### Scenario: Illegal transition is refused

- **WHEN** a role attempts to move a `drafting` block directly to `approved`
- **THEN** the system refuses and states the transitions available from `drafting`

### Requirement: Reviewer remediation is the same card at a higher round

A block returned for changes **by its reviewer** SHALL return to `briefed` with its `round` incremented,
on the same card. The system SHALL NOT create a new card for a reviewer remediation, and a remediation
SHALL NOT tick any task.

This governs the block-level review loop only. Section-level remediation, raised by a supervisor against
a whole section, is routed by whether a card already owns the finding — see "Section remediation
follows the finding, not the verdict".

One card's thread SHALL therefore constitute the complete audit trail of one unit of work across all
its rounds.

#### Scenario: Changes requested increments the round

- **WHEN** a reviewer requests changes on a block at round 1
- **THEN** the same card returns to `briefed` at round 2, retaining its identity, tasks and thread

#### Scenario: Remediation ticks nothing

- **WHEN** a block re-enters `briefed` for remediation
- **THEN** no task referenced by the card is marked complete as a result

### Requirement: Blocks carry their brief context

A `block` card SHALL carry the task references it implements, the `base` commit its brief was carved
against, the `reviewed_state` commit a reviewer actually reviewed, its recorded gate results, its
current `round`, and the cards it is blocked by.

`base` SHALL be recorded before the block is briefed, and SHALL NOT change across remediation rounds.

#### Scenario: Brief without a base is refused

- **WHEN** a block is moved to `briefed` with no `base` recorded
- **THEN** the system refuses and states that a brief must name the commit it was carved against

### Requirement: Gate results are recorded as exit codes

A gate result SHALL be recorded on the card as a label paired with the exit code the gate returned. A
recorded exit code SHALL be the only accepted evidence that a gate passed; gate output prose SHALL NOT
be accepted as evidence.

#### Scenario: Gate recorded from its exit code

- **WHEN** a gate labelled `build` completes with exit code 0 and that result is recorded
- **THEN** the card shows `build` as passed

#### Scenario: Narrative claim of a pass is not evidence

- **WHEN** a comment states a gate passed but no exit code is recorded for that gate
- **THEN** the card shows that gate as absent, and transitions requiring it treat it as not passed

### Requirement: Blocked is derived, not stored

The system SHALL derive whether a card is blocked from whether its `blocked_by` set is non-empty, and
SHALL NOT hold `blocked` as a status. A card SHALL retain its flow state throughout, so that clearing
what blocked it requires no state restoration.

#### Scenario: Blocking and unblocking preserve state

- **WHEN** a `building` block is blocked on an open question and that question is later answered
- **THEN** the block reports as blocked while the question is open, reports as unblocked afterwards, and
  is in state `building` throughout

### Requirement: Approval is provisional until the section closes

A block reaching `approved` SHALL NOT be treated as landed. `approved` records that the block's reviewer
certified it; it does not record that the process accepted it, which only a supervisor's section verdict
establishes.

`land` SHALL NOT be individually invocable. A block SHALL reach `landed` only as a consequence of its
section closing, and closing a section SHALL land every block in that section as one operation or refuse
and land none.

Closing a section SHALL refuse where any block in it is not `approved`, or where any block carries an
expected gate whose recorded exit code is non-zero or absent.

Closing a section SHALL NOT compare any block's `reviewed_state` against the state of the repository at
close. A block can sit `approved` for a long time while its siblings land, and a sibling touching its
files leaves its certification describing a state that no longer exists — but the remedy is not to
reopen that block. The supervisor's review of the section as a whole, at the state the section actually
closes in, is what covers the difference: it is the review that sees a fix confined to one block break
something in another, which no block-local review can see. `reviewed_state` stays recorded as evidence
of what each reviewer certified. It is not a gate on landing.

#### Scenario: An approved block is not yet landed

- **WHEN** a block is approved by its reviewer and its section has not closed
- **THEN** the block's status is `approved`, and no transition available to a caller moves it to `landed`

#### Scenario: Section close lands its blocks together

- **WHEN** a supervisor closes a section whose blocks are all `approved` with green expected gates
- **THEN** every block in that section moves to `landed`, recording the acting role and the time

#### Scenario: One unlandable block refuses the whole close

- **WHEN** a section is closed while one of its blocks carries a gate recorded non-zero
- **THEN** the system refuses, names that block and that gate, and no block in the section moves to
  `landed`

### Requirement: Section remediation follows the finding, not the verdict

A supervisor verdict of `request-changes` against a section SHALL be discharged per finding, and each
finding SHALL be routed by whether a card already owns it.

A finding raised for the first time has no card to own it and SHALL create a new `block` card in that
section, carrying the finding as its brief. It SHALL tick no task, and it SHALL NOT reopen a block that
implements tasks — a supervisor's findings are raised against the section, including findings about the
relationship between blocks, which belong to no single block.

A finding the supervisor reports as still unresolved SHALL return the card that owns it to `briefed` with
`round` incremented, by the `finding-recurred` transition, on that same card. A recurrence SHALL NOT
create a second card for the same finding, so that one card's thread is the complete history of one
finding across every round it took to close.

A single verdict MAY do both: return one card for a recurrence and create another for a new finding.

Every verdict SHALL be retained against the section entity; a later verdict SHALL NOT overwrite an
earlier one.

#### Scenario: A first-time finding becomes a new block

- **WHEN** a supervisor records `request-changes` naming a finding no card owns
- **THEN** the system creates a new `block` card in that section carrying the finding, ticks no task, and
  reopens no task-implementing block

#### Scenario: An unresolved finding returns its own card

- **WHEN** a supervisor records `request-changes` reporting that a finding on an existing remediation
  card is still unresolved
- **THEN** that card returns to `briefed` with `round` incremented, and no second card is created for it

#### Scenario: One verdict both returns and creates

- **WHEN** a supervisor's verdict reports one finding still unresolved and identifies one new finding
- **THEN** the owning card returns to `briefed` at a higher round and a new card is created for the new
  finding

### Requirement: Remediation beyond the second round requires recorded authorisation

A section SHALL admit two `request-changes` verdicts without ceremony. A third or subsequent
`request-changes` verdict against the same section SHALL be refused unless the refusal is discharged by a
recorded Product Owner authorisation naming the section and the reason.

The count SHALL be of the section's own retained verdicts, derived from the record at the time it is
asked and never stored as a figure. Counting verdicts rather than remediation cards is what makes the
bound total: a section may fail to converge by accumulating new findings, by the same finding recurring
round after round, or by both at once, and only the verdict count sees all three.

An authorisation SHALL discharge exactly one verdict. It is spent by the verdict it permits, so a fourth
`request-changes` verdict requires a fourth authorisation, and each carries its own reason. A single
standing permission would let one reason stand in for every round that followed it, and the bound exists
precisely to force the conversation again each time the section fails to converge.

Recording an authorisation SHALL be refused unless the section is already at the bound with none
unspent — that is, unless a `request-changes` verdict is being refused for want of one. An authorisation
is therefore always contemporaneous with the refusal it discharges. Authorisations recorded ahead of
need would satisfy the one-for-one rule while defeating it: their reasons would be written before the
findings they are supposed to justify pushing past, and a reason given for a round that has not happened
yet cannot be a reason at all.

#### Scenario: Authorisation ahead of need is refused

- **WHEN** a Product Owner records an authorisation for a section that is not currently at the bound
- **THEN** the system refuses and states that an authorisation is recorded against a refused verdict, not
  in advance of one

The authorisation SHALL be part of the record, not a permission granted out of band. A section that will
not converge is a signal about the section breakdown or the spec, and the reason it was pushed further
SHALL be legible later.

#### Scenario: Unauthorised third verdict is refused

- **WHEN** a supervisor records a third `request-changes` verdict against a section with no recorded
  authorisation
- **THEN** the system refuses and states that a recorded Product Owner authorisation would satisfy it

#### Scenario: A recurring finding counts toward the bound

- **WHEN** a section's three `request-changes` verdicts all report the same finding still unresolved,
  creating no new card
- **THEN** the third is refused on the same bound, because the count is of verdicts and not of cards

#### Scenario: Authorised third verdict proceeds

- **WHEN** a Product Owner authorisation for that section is recorded and a third `request-changes`
  verdict is made
- **THEN** the system permits it, and the authorisation and its reason are readable from the section

### Requirement: Stored round agrees with the transition history

A block card's `round` SHALL be stored, because gate evidence is pinned to the round it was recorded in
and that pin must survive on the wire. It SHALL nonetheless equal one plus the number of
round-incrementing transitions in that card's own history, and every transition that increments it SHALL
advance the field and append to the history as one write.

Where a card's stored `round` disagrees with its transition history, the system SHALL refuse to act on
that card and SHALL NOT reconcile the two. Neither is privileged: a stored count ahead of the history
and a history ahead of the count are different failures, and guessing which one is right would silently
destroy the evidence of whichever was correct.

#### Scenario: Round and history advance together

- **WHEN** any round-incrementing transition is applied to a block
- **THEN** the card's `round` and its transition history both advance in the same write

#### Scenario: A disagreeing card is refused, not repaired

- **WHEN** a block card's stored `round` does not equal one plus its round-incrementing transitions
- **THEN** the system refuses to act on that card, names both figures, and alters neither

### Requirement: Sections are entities

A section SHALL be a first-class entity carrying its own status, its `base` commit, and the supervisor
verdict recorded against its commit range. Cards SHALL reference the section that raised them.

A section SHALL be closable only when the conditions imposed by process enforcement are met, and closing
it SHALL record the acting role and the time.

#### Scenario: Section carries a supervisor verdict

- **WHEN** a supervisor records a verdict for a section against a commit range
- **THEN** the verdict, the range and the acting role are recorded against that section entity

#### Scenario: Section state is queryable without reading its cards

- **WHEN** a role asks for a section's status
- **THEN** the system answers from the section entity without requiring its cards to be read
