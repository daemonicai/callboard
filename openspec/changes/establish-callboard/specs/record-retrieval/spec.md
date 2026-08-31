## Purpose

What the record is made of and how it is read back — legible and diffable without the tool, retrievable
by identity rather than by scrolling, rebuildable in its derived parts, exportable in the incumbent's
shape, and viewable by a human at a glance.

## ADDED Requirements

### Requirement: The record is legible without the tool

The primary record SHALL be plain text, human-readable, and committed to the repository. A reader with no
access to the tool SHALL be able to determine a card's status, owner and history from the record alone.

The tool SHALL be an optimisation and an enforcement layer, never a precondition for comprehension.

Where the record carries text written to be read as sentences — a refusal's rule and remedy, a
certification's claims and limits, an authorisation's reason — the record SHALL present it as sentences.
A reader SHALL NOT have to decode an escape convention to read prose the system wrote for them.

The record's structured metadata SHALL share one delimited block syntax rather than a syntax per kind of
entry, so that a reader who has learnt to read one entry can read them all.

#### Scenario: Card read without the tool

- **WHEN** a reader inspects the record directly with no tool available
- **THEN** the card's status, owner, scope and full thread are determinable from what they read

#### Scenario: Tool unavailable

- **WHEN** the tool cannot run
- **THEN** the record remains readable and the loop can proceed unenforced rather than blocked

#### Scenario: Recorded prose reads as prose

- **WHEN** a reader opens a card carrying a recorded refusal, certification or authorisation
- **THEN** its sentences read as ordinary text, with no escape marker standing in for an ordinary space

#### Scenario: One syntax across the record

- **WHEN** a reader encounters any of the record's structured metadata blocks
- **THEN** every such block is delimited and fielded the same way, whatever it records

### Requirement: The record is diffable per card

The record SHALL be organised so that a review of repository history shows exactly which cards moved and
how. A change to one card SHALL NOT appear as a change to another.

#### Scenario: Review shows card-level movement

- **WHEN** several cards change and the repository difference is reviewed
- **THEN** each card's movement is separately identifiable

### Requirement: Concurrent work does not corrupt the record

The record SHALL tolerate several roles acting at once. Acting on distinct cards SHALL be
contention-free. Where two roles act on one card, the system SHALL serialise their writes such that
neither is lost and the thread's order is preserved.

Damage to any single card SHALL NOT compromise any other card.

#### Scenario: Two roles comment on one card

- **WHEN** two roles append comments to the same card at the same moment
- **THEN** both comments are recorded, in a determinate order, with neither overwritten

#### Scenario: Damage is contained

- **WHEN** one card's record is corrupted
- **THEN** every other card remains readable and usable

### Requirement: Narrative is retrieved by identity

The system SHALL return a card's full content, including every comment on it, given the card's identity.
This material SHALL be retrievable and quotable, and SHALL NOT appear on any default read path.

#### Scenario: Full card fetched by identity

- **WHEN** a role requests a card by its identity
- **THEN** the system returns its full body and complete thread

#### Scenario: Narrative stays off the default path

- **WHEN** a role requests its working context
- **THEN** no narrative from cards outside its queue appears in the response

### Requirement: Derived state is rebuildable and never authoritative

The system SHALL be able to reconstruct all derived state from the primary record alone. Derived state
SHALL NOT be authoritative for anything, and SHALL NOT be committed to the repository.

#### Scenario: Derived state discarded and rebuilt

- **WHEN** all derived state is destroyed and a rebuild is run
- **THEN** the system reconstructs it from the primary record and answers identically to before

#### Scenario: Disagreement resolves to the record

- **WHEN** derived state disagrees with the primary record
- **THEN** the primary record governs

### Requirement: Export in the incumbent's shape

The system SHALL render a section, or a whole change, as a single readable document approximating the
shape of the log it replaces, for archival alongside the other change artefacts.

Every class of content previously written to that log SHALL have a home in the model and SHALL be
reconstitutable by this export.

#### Scenario: Section exported as one document

- **WHEN** a closed section is exported
- **THEN** the system produces a single document containing its cards, threads, verdicts and findings in
  reading order

#### Scenario: Closed cards leave the working set without leaving the repository

- **WHEN** cards are closed
- **THEN** they no longer appear in default queries and remain present in the record and in exports

### Requirement: Human view of the board

The system SHALL provide a local, read-only, human-readable view of the board showing cards by column
and owner, what is blocked and on what, and the open questions with who owes each answer.

This view SHALL require no server, no authentication and no hosting.

#### Scenario: Product Owner reads overall state

- **WHEN** the Product Owner opens the view
- **THEN** they see cards by column and owner, blocked relationships, and open questions with their
  owners

#### Scenario: View is read-only

- **WHEN** the Product Owner attempts to alter state from the view
- **THEN** no state changes
