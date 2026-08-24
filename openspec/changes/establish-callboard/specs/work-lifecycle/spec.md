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

### Requirement: Remediation is the same card at a higher round

A block returned for changes SHALL return to `briefed` with its `round` incremented, on the same card.
The system SHALL NOT create a new card for a remediation, and a remediation SHALL NOT tick any task.

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
