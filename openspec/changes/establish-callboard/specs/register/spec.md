## Purpose

Governs the standing material every agent must have in front of it — the rules, hazards, obligations and
decisions that outlive the work that raised them, how they reach a wider scope, and how the rule set is
kept small enough to keep injecting without losing what any member of it actually said.

## ADDED Requirements

### Requirement: Rules and hazards are injected unconditionally

The system SHALL supply the complete current set of live `rule` and `hazard` cards with every brief
issued, without the receiving role requesting them and without condition.

The register SHALL be delivered rather than made available for search, so that no agent can fail to look
for it. Where a response must be shortened, the register SHALL NOT be what is shortened.

#### Scenario: Every brief carries the register

- **WHEN** a brief is issued to any role
- **THEN** the response contains every live rule and every live hazard

#### Scenario: Register survives truncation

- **WHEN** a response would exceed its budget
- **THEN** the register is delivered in full and other content is shortened instead

### Requirement: Register kinds have a two-state lifecycle

`rule`, `hazard`, `obligation` and `decision` cards SHALL be `open` or `discharged` and SHALL NOT occupy
flow states. For `rule` and `decision`, `discharged` SHALL mean superseded rather than completed.

An `obligation` SHALL name the section expected to discharge it. A `decision` MAY name the decision it
supersedes and the decision that supersedes it.

#### Scenario: Obligation names what owes it

- **WHEN** an obligation is raised
- **THEN** the card names the section expected to discharge it

#### Scenario: Superseded decision remains readable

- **WHEN** a decision is superseded by a later one
- **THEN** the earlier decision is marked discharged, names its successor, and remains retrievable

### Requirement: The register lives above the change

Repository-scoped cards SHALL belong to the repository and SHALL NOT be owned by any change. Archiving a
change SHALL act as a filter that relocates its change-scoped cards into the archive, exactly as
written, and leaves cards of wider scope untouched — settling nothing.

The system SHALL NOT require a carry-forward step at archive, because a handoff is a transit in which
material can be dropped, and the only cross-change carry on record survived on human memory rather than
on a handoff.

#### Scenario: Archive leaves the register standing

- **WHEN** a change is archived
- **THEN** every card in the change directory, obligations included, moves into the archive exactly as
  written, with no obligation settled by the act of archiving, and every repository-scoped rule, hazard
  and open question remains live and unmoved

#### Scenario: Question outlives its change

- **WHEN** a question raised in one change is still open when that change archives
- **THEN** the question remains open and continues to surface to the role that owes its answer

### Requirement: Promotion is retrospective and preserves the link

The system SHALL support promotion at the checkpoint where the outcome is known, rather than requiring a
classification when material is first raised. Durability is a property of an answer and not of the
question that prompted it, so the system SHALL NOT ask a role to predict it.

Promoting a change-scoped rule to repository scope SHALL move the same card, retaining its identity, text
and thread.

Promotion SHALL NOT be limited to rules. An `obligation` that outlives the change it was raised in SHALL
be promotable to a wider scope on the same terms — the same card, retaining its identity, text and
thread — because an obligation whose owing section has closed must have somewhere to go other than a
discharge that says it was met. `process-enforcement` refuses an archive that would strand one, and a
refusal whose only route out is to declare the work done is a refusal that manufactures false
settlements.

Authoring a rule from findings SHALL create a new card and SHALL record which findings it was earned
from, because a rule backed by several independent findings across several sections is a different
proposition from one backed by a single incident.

#### Scenario: Obligation promoted across scope

- **WHEN** an open obligation whose owing section has closed is promoted to a wider scope
- **THEN** the same card moves to that scope, retaining its identity, text and thread, and remains open

#### Scenario: Rule promoted across scope

- **WHEN** a change-scoped rule is promoted to repository scope
- **THEN** the same card persists with its identity, text and citation history intact

#### Scenario: Rule authored from findings keeps its backing

- **WHEN** a rule is authored generalising several findings
- **THEN** the rule is a new card recording the findings it was earned from, and those findings are
  unchanged

### Requirement: Declining is distinguishable from discharging

An obligation that will not be met SHALL be closable by declining it, and the record SHALL carry the
reason and SHALL distinguish a declined obligation from one that was met. The two are different facts
about the work and a record that conflates them cannot be read back honestly — "we decided not to" is
the outcome most worth finding later, and it is the one an unqualified discharge would hide.

Declining SHALL NOT introduce a third lifecycle state. A declined obligation is `discharged` in the
two-state sense this capability already defines — no longer live — and the manner of its closure is
recorded alongside, exactly as `discharged` already means *superseded* rather than *completed* for a
`rule` or a `decision`. The distinction the record owes its reader is why the card closed, not which
status word it carries.

#### Scenario: Obligation declined rather than met

- **WHEN** an open obligation is declined with a recorded reason
- **THEN** it stops being open, the reason is part of the record, and the record distinguishes it from
  an obligation that was met

#### Scenario: Declining requires a reason

- **WHEN** an obligation is declined with no reason recorded
- **THEN** the system refuses and states that a reason is required

### Requirement: Rules compact into families by supersession

The system SHALL support compacting several rules into a family rule stating what they share. A family
rule SHALL record the rules it absorbs, and every absorbed rule SHALL remain retrievable so that an
over-abstract family can be unpicked.

Compaction SHALL supersede and SHALL NOT delete, because generalising loses operative content and the
loss is silent — a blunted rule is discovered only when it fails to fire.

Compaction of change-scoped rules SHALL be performed by the architect at archive. Compaction of
repository-scoped rules SHALL be proposed by an agent and decided by the Product Owner, since families
form across several changes and archive is the wrong cadence for them.

#### Scenario: Family records what it absorbs

- **WHEN** several rules are compacted into a family
- **THEN** the family records the absorbed rules, and each remains retrievable by identity

#### Scenario: Repository compaction is proposed, not applied

- **WHEN** an agent proposes a repository-scoped family
- **THEN** the system records the proposal with its candidate text, backing set and citation counts, and
  applies nothing until the Product Owner decides

### Requirement: Register size triggers review, never eviction

The system SHALL count how often each rule is cited and SHALL surface a stated size ceiling as a trigger
for a compaction review. The ceiling SHALL NOT act as a hard cap, because a hard cap forces retiring a
good rule in order to admit a good rule.

Citation counts SHALL surface candidates only. A rule that is never cited SHALL be placed in a review
queue for a human and SHALL NOT be retired automatically, because a rule that never fires may be one
that is working and counting cannot distinguish that from a dead one.

#### Scenario: Ceiling triggers a review

- **WHEN** the live rule set passes the stated ceiling
- **THEN** the system raises a compaction review and retires nothing

#### Scenario: Uncited rule is queued, not retired

- **WHEN** a rule has no recorded citations
- **THEN** it is placed in the human review queue and remains live until a human rules on it

### Requirement: Hazards carry a verification condition

Each `hazard` card SHALL carry a condition under which it can be verified still to hold and a cadence at
which that condition is re-checked. A hazard whose condition no longer holds SHALL be discharged.

Hazards SHALL be treated separately from rules, because hazards are environment facts that go stale
silently while rules are durable.

#### Scenario: Hazard without a verification condition is refused

- **WHEN** a hazard is raised without a condition under which it can be re-checked
- **THEN** the system refuses and states the condition it requires

#### Scenario: Hazard discharged when its condition lapses

- **WHEN** a hazard's re-check finds its condition no longer holds
- **THEN** the hazard is discharged and ceases to be injected into briefs

### Requirement: The project constitution stays outside agent control

The system SHALL hold repository-scoped rules and SHALL NOT write to the project's agent instruction
file. Promoting a repository-scoped rule into that file SHALL remain a Product Owner act.

#### Scenario: Agent attempts to write the constitution

- **WHEN** any agent attempts to promote a rule into the project's agent instruction file
- **THEN** the system refuses and records the promotion as awaiting a Product Owner decision
