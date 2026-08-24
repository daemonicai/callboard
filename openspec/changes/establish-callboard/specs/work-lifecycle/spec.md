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
                ├──── recertification-refused ◀────────────┤
                └──── amendment-requested ◀────────────────┘
                            (round += 1 on all four)
```

`changes-requested` and `fix-before-land` both leave `in-review`; `recertification-refused` and
`amendment-requested` both leave `approved`. They are distinct named transitions because the name is
recorded in the card's history — see `review-certification` for what raises each.

`amendment-requested` is the architect deliberately reopening an approved block for a further
amendment. It is the transition that delivers `review-certification`'s "a further amendment after a
recertification SHALL require a new round": once a block's single recertification is spent, this is
the only way back to `briefed`, and it is invoked on purpose rather than falling out of a refusal.

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
a whole section, SHALL create a new card — see "Section remediation is a new card".

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

Closing a section SHALL refuse where any block in it is not `approved`, where any block's
`reviewed_state` does not match that block's current state, or where any block carries an expected gate
whose recorded exit code is non-zero or absent.

#### Scenario: An approved block is not yet landed

- **WHEN** a block is approved by its reviewer and its section has not closed
- **THEN** the block's status is `approved`, and no transition available to a caller moves it to `landed`

#### Scenario: Section close lands its blocks together

- **WHEN** a supervisor closes a section whose blocks are all `approved` with matching `reviewed_state`
  and green expected gates
- **THEN** every block in that section moves to `landed`, recording the acting role and the time

#### Scenario: One unlandable block refuses the whole close

- **WHEN** a section is closed while one of its blocks carries a gate recorded non-zero
- **THEN** the system refuses, names that block and that gate, and no block in the section moves to
  `landed`

### Requirement: Section remediation is a new card

A supervisor verdict of `request-changes` against a section SHALL be discharged by a new `block` card in
that section, carrying the findings as its brief. It SHALL tick no task, and it SHALL NOT reopen any
block the reviewer already approved — a supervisor's findings are raised against the section, including
findings about the relationship between blocks that belong to no single block.

Each further `request-changes` verdict against the same section SHALL create a further card. Every
verdict SHALL be retained against the section entity; a later verdict SHALL NOT overwrite an earlier one.

#### Scenario: Supervisor findings become a new block

- **WHEN** a supervisor records `request-changes` against a section
- **THEN** the system creates a new `block` card in that section carrying the findings, ticks no task,
  and reopens no existing block

#### Scenario: A second pushback creates a second card

- **WHEN** a supervisor records `request-changes` against a section that already has one remediation card
- **THEN** the system creates a second remediation card, and both verdicts remain recorded against the
  section

### Requirement: Remediation beyond the second round requires recorded authorisation

A section SHALL admit two remediation cards without ceremony. A third or subsequent remediation card
SHALL be refused unless the refusal is discharged by a recorded Product Owner authorisation naming the
section and the reason.

The authorisation SHALL be part of the record, not a permission granted out of band. A section that will
not converge is a signal about the section breakdown or the spec, and the reason it was pushed further
SHALL be legible later.

#### Scenario: Unauthorised third remediation is refused

- **WHEN** a third remediation card is created for a section with no recorded authorisation
- **THEN** the system refuses and states that a recorded Product Owner authorisation would satisfy it

#### Scenario: Authorised third remediation proceeds

- **WHEN** a Product Owner authorisation for that section is recorded and a third remediation card is
  created
- **THEN** the system permits it, and the authorisation and its reason are readable from the section

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
