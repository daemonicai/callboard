## Purpose

Defines the single card entity that everything in `callboard` is made of — its kinds, its stable
quotable identity, the scope that decides how long it lives, who owns it, and the append-only addressed
threads that turn agent-to-agent prose into actual routing.

## ADDED Requirements

### Requirement: Single card entity with a kind discriminator

The system SHALL represent every unit of work, question, finding, obligation, rule, hazard, decision
and section as a card of one entity type, distinguished by a `kind` field taking exactly one of:
`block`, `question`, `finding`, `obligation`, `rule`, `hazard`, `decision`, `section`.

Every card SHALL carry `id`, `kind`, `title`, `status`, `owner`, `created`, `updated` and `body`. Cards
raised within a section SHALL carry that `section`; cards not tied to one SHALL leave it empty.

#### Scenario: Card created with a recognised kind

- **WHEN** a card is created with `kind` set to one of the eight recognised values
- **THEN** the system assigns it an identity and records `created` and `updated`

#### Scenario: Card created with an unrecognised kind

- **WHEN** a card is created with a `kind` outside the eight recognised values
- **THEN** the system refuses the creation and names the recognised kinds

### Requirement: Stable, human-quotable, kind-prefixed identity

Each card SHALL receive an identity that is stable for the card's whole life, prefixed by its kind so
the identity alone tells a reader what it refers to (for example `B-0042`, `Q-0007`, `F-0031`,
`D-0019`). An identity SHALL NOT be reused after its card is closed, discharged or withdrawn.

A card's identity SHALL remain valid and resolvable after the change that raised it is archived.

#### Scenario: Identity survives archive

- **WHEN** a reader resolves a card identity raised in a change that has since been archived
- **THEN** the system returns that card, its status and its full thread

#### Scenario: Identity is not recycled

- **WHEN** a card is closed and a new card of the same kind is created afterwards
- **THEN** the new card receives an identity distinct from every identity previously issued

### Requirement: Ownership names whose turn it is

Every card SHALL carry an `owner` naming the single role whose turn it is to act — `architect`,
`worker`, `reviewer`, `supervisor` or `product-owner`. Ownership SHALL be queryable, so that any role
can be told what is assigned to it without reading prose.

Every ownership change SHALL record the acting role and the time it occurred.

#### Scenario: Role queries its own assignments

- **WHEN** a role asks what is assigned to it
- **THEN** the system returns every card whose `owner` is that role, and nothing owned by another role

#### Scenario: Ownership handover is attributed

- **WHEN** a role transfers a card's ownership to another role
- **THEN** the system records the acting role and the timestamp against that card

### Requirement: Scope determines lifetime

Every card SHALL carry a scope of `section`, `change`, `capability` or `repository`, determining what
event, if any, ends its life. `rule` cards SHALL take `change` or `repository` and no other value.
`hazard` and `question` cards SHALL be repository-scoped. `obligation` cards SHALL be change-scoped.
`decision` cards SHALL be capability-scoped, following the specification they bind. `finding` cards
SHALL be section-scoped. `section` cards SHALL be change-scoped.

Scope SHALL be an attribute of the card and not implied by its kind alone, so that a card may be
promoted to a wider scope without losing its identity or thread.

#### Scenario: Rule promoted from change to repository scope

- **WHEN** a change-scoped rule is promoted to repository scope
- **THEN** the same card retains its identity, body and thread, and its scope becomes `repository`

#### Scenario: Rule given an unsupported scope

- **WHEN** a `rule` card is created or promoted with a scope of `section`
- **THEN** the system refuses and states that a rule applying to one section is a constraint in a brief

### Requirement: Append-only addressed comment threads

Cards SHALL carry an append-only sequence of comments. Each comment SHALL record its own identity, the
role that wrote it, a timestamp and a body, and MAY record the comment it replies to, the role it is
addressed `to`, and whether it is resolved.

A comment SHALL NOT be edited or deleted once appended; a correction is a further comment.

A comment addressed to a role and not yet resolved SHALL constitute a live thread and SHALL appear in
that role's queue. Addressing SHALL be a structural property of the comment, not prose within it — a
role mention in body text SHALL NOT route anything.

#### Scenario: Addressed comment routes to its target

- **WHEN** a comment is addressed to `reviewer` and left unresolved
- **THEN** that card appears in the `reviewer` queue even though the card's `owner` is another role

#### Scenario: Role mention in prose does not route

- **WHEN** a comment body mentions a role without addressing the comment to it
- **THEN** the card does not appear in that role's queue on account of the mention

#### Scenario: Resolved thread leaves the queue

- **WHEN** an addressed comment is resolved
- **THEN** the card ceases to appear in that role's queue on account of that comment, and the comment
  remains readable in the thread

#### Scenario: Appended comment cannot be rewritten

- **WHEN** any role attempts to alter or remove an existing comment
- **THEN** the system refuses and states that corrections are appended
