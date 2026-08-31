## Purpose

Governs the record of what was checked and found clean — the instrument used, the extent covered, the
blind spot declared alongside it, and the staleness that stops a clean result being offered as settled
once the ground beneath it has moved.

## Requirements

### Requirement: Clean findings are cards, distinct from rules

A result checked and found clean SHALL be recorded as a `finding` card carrying the instrument used, the
extent covered, the `verified_at` state it was verified against, and a declared blind spot.

A `finding` SHALL be section-scoped and SHALL degrade at section close. A `rule` SHALL carry none of
these fields and SHALL survive section close. The system SHALL NOT treat a finding as a rule or convert
one into the other.

#### Scenario: Finding recorded with its instrument and state

- **WHEN** a role records a clean finding
- **THEN** the card carries the instrument, the extent, `verified_at`, and the declared blind spot

#### Scenario: Finding degrades at section close

- **WHEN** the section that raised a finding closes
- **THEN** the finding is no longer offered as live and remains retrievable by identity

### Requirement: A clean finding requires a blind-spot declaration

The system SHALL refuse to record a clean finding unless the recording role declares a blind spot or
explicitly asserts that there is none. The declaration SHALL be made by the role holding the instrument,
at the time of writing.

A declared blind spot SHALL NOT be recorded as part of the clean result. It SHALL be raised as an
`obligation` or a `hazard`, and SHALL NOT degrade at section close, because a blind spot filed under a
clean heading has been observed to ship.

#### Scenario: Clean finding without a declaration is refused

- **WHEN** a role records a clean finding declaring neither a blind spot nor its absence
- **THEN** the system refuses and names the declaration it requires

#### Scenario: Declared blind spot outlives the section

- **WHEN** a clean finding is recorded with a declared blind spot and its section later closes
- **THEN** the finding degrades and the blind spot remains live as an obligation or hazard

### Requirement: Extent is declared, widest by default

A finding SHALL declare its extent in one of the following forms, in order of preference:

1. a re-runnable instrument, whose extent is what re-running it covers;
2. explicit paths, line ranges or symbols;
3. the scope of the block that raised it.

Where no extent is declared, the system SHALL default to the scope of the block that raised the finding.
Narrowing the extent below that default SHALL require an explicit declaration.

A re-runnable instrument SHALL be the preferred form for a finding asserting the absence of something
across a subtree, because an enumerated path set either over-states or under-states such an extent.

#### Scenario: Undeclared extent defaults to block scope

- **WHEN** a finding is recorded with no declared extent
- **THEN** the system records its extent as the scope of the block that raised it

#### Scenario: Narrowed extent must be declared

- **WHEN** a finding covers less than its block's scope
- **THEN** the system requires the narrower extent to be stated explicitly before recording it

### Requirement: Findings stale when their extent moves

The system SHALL mark a clean finding as stale when the state covered by its extent differs from its
`verified_at` state. A stale finding SHALL remain readable and SHALL NOT be offered as a settled result.

Staleness SHALL be presented as calling for re-verification, distinctly from a finding being
re-litigated. A finding that is stale SHALL NOT thereby be treated as wrong.

#### Scenario: Covered code moves

- **WHEN** code within a finding's declared extent changes after `verified_at`
- **THEN** the finding is reported as stale and is not offered as settled

#### Scenario: Unrelated code moves

- **WHEN** code outside a finding's declared extent changes
- **THEN** the finding remains current

#### Scenario: Stale finding is not a refuted finding

- **WHEN** a stale finding is surfaced
- **THEN** the system states that it requires re-verification and does not present it as incorrect

### Requirement: Findings that argue rather than measure are dispositioned separately

A finding that reasons over a claim, and so has no instrument to replay, SHALL be recorded with a
distinct disposition marking it as clean as argued at a named state and not re-verifiable. The system
SHALL NOT apply staleness computation to such a finding.

#### Scenario: Non-verifiable finding recorded

- **WHEN** a role records a finding that argues over a claim rather than measuring it
- **THEN** the system records it as clean-as-argued at a named state and never reports it as stale

#### Scenario: Non-verifiable finding is surfaced honestly

- **WHEN** a clean-as-argued finding is surfaced
- **THEN** the system states that it was argued rather than measured
