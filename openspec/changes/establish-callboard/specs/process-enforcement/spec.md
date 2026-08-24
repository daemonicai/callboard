## Purpose

The refusals. Each rule here closes a failure observed in the incumbent's own record — a file can only
note that a rule was broken, and recording a break is strictly worse than preventing it.

## ADDED Requirements

### Requirement: Refusals are explained and attributable

Every refused transition SHALL state which rule refused it and what would satisfy that rule. A refusal
SHALL be recorded against the card with the acting role and the time, so that a pattern of refusals is
itself visible.

#### Scenario: Refusal names its rule

- **WHEN** any transition is refused
- **THEN** the response names the refusing rule and states what would satisfy it

### Requirement: Landing requires a current certification

The system SHALL refuse to land a card whose `reviewed_state` does not match the current state of the
repository.

#### Scenario: Fingerprint has moved since approval

- **WHEN** a card is landed after its approved state has changed
- **THEN** the system refuses and names the certified state and the current one

#### Scenario: Certification is current

- **WHEN** a card is landed while `reviewed_state` matches the current state
- **THEN** the transition proceeds

### Requirement: Landing requires recorded passing gates

The system SHALL refuse to land a card carrying any gate whose recorded exit code is non-zero, or any
gate that is expected but absent from the record.

#### Scenario: Gate absent from the record

- **WHEN** a card is landed with an expected gate having no recorded exit code
- **THEN** the system refuses and names the missing gate

#### Scenario: Gate recorded as failing

- **WHEN** a card is landed with a gate recorded at a non-zero exit code
- **THEN** the system refuses and names the failing gate

### Requirement: Approval is refused from the wrong role

The system SHALL refuse an approval attempted by a role other than `reviewer` or
`supervisor`.

#### Scenario: Architect approves its own work

- **WHEN** the architect attempts to approve a block
- **THEN** the system refuses and names the roles permitted to approve

### Requirement: A verdict cannot leave threads unanswered

The system SHALL refuse to move a card out of `in-review` while any comment addressed to the acting role
on that card remains unresolved.

#### Scenario: Unresolved question in a verdict

- **WHEN** a reviewer records a verdict while a comment addressed to it on that card is unresolved
- **THEN** the system refuses and lists the unresolved threads

### Requirement: Section close settles its obligations

The system SHALL refuse to close a section while any `obligation` card owed by that section remains
open. Each SHALL be discharged, promoted to a wider scope, or declined with a recorded reason.

#### Scenario: Obligation still owed

- **WHEN** a section is closed with an open obligation owed by it
- **THEN** the system refuses and lists the obligations and the dispositions available

### Requirement: Section close settles its questions

The system SHALL refuse to close a section while any `question` raised in it remains open, unless that
question is deferred to a named target.

#### Scenario: Open question at section close

- **WHEN** a section is closed with an open, undeferred question
- **THEN** the system refuses and names the question

#### Scenario: Deferred question permits close

- **WHEN** a section is closed with a question deferred to a named later section or change
- **THEN** the close proceeds and the question remains open against its target

### Requirement: Section close settles its addressed threads

The system SHALL refuse to close a section while any comment addressed to a role within it remains
unresolved. Each SHALL be resolved, promoted to a `question`, promoted to a `decision`, or declined with
a recorded reason.

To keep this gate from becoming a formality discharged in bulk at the moment of closing, the system SHALL
surface addressed comments left unresolved for longer than one round, as a prompt rather than a
constraint.

#### Scenario: Unresolved thread blocks section close

- **WHEN** a section is closed with an unresolved addressed comment
- **THEN** the system refuses and lists the dispositions available for it

#### Scenario: Ageing thread is surfaced early

- **WHEN** an addressed comment has been unresolved for longer than one round
- **THEN** the system surfaces it to the addressed role without refusing anything

### Requirement: An answer must be written down

The system SHALL refuse to mark a question answered unless it names the `decision` card recording the
answer, or records the answer inline where it is trivial.

#### Scenario: Question closed with no recorded answer

- **WHEN** a question is marked answered with neither a decision reference nor an inline answer
- **THEN** the system refuses and states what is required

### Requirement: Work cannot proceed past a stop-and-ask

The system SHALL refuse to advance a card blocked by an open question owned by the Product Owner.

#### Scenario: Advancing past an escalation

- **WHEN** a card blocked by an open Product Owner question is advanced
- **THEN** the system refuses and names the question awaiting an answer

### Requirement: Archive settles orphaned obligations

The system SHALL refuse to archive a change while any change-scoped obligation owed by no remaining
section is open. Each SHALL be discharged, promoted to a wider scope, or declined with a recorded reason.

This gate exists because obligations owed by no section that remains have been observed to surface at
archive time or not at all.

#### Scenario: Orphaned obligation at archive

- **WHEN** a change is archived carrying an open obligation whose owing section has closed
- **THEN** the system refuses and lists the obligations and the dispositions available

#### Scenario: Promotion satisfies the archive gate

- **WHEN** an orphaned obligation is promoted to a wider scope
- **THEN** the archive proceeds and the obligation remains live at its new scope
