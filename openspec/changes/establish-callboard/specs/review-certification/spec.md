## Purpose

Governs what a review verdict certifies and how it can be amended — approval as a binary certification
of one exact state, recertification of an already-certified claim set over a small amendment, and the
disposition every nit must receive so none dies by neglect.

## ADDED Requirements

### Requirement: Approve is binary and certifies one state

A review verdict SHALL be exactly one of `approve` or `request-changes`. The system SHALL NOT offer a
verdict combining approval with outstanding findings.

An `approve` SHALL certify one exact state, recorded as the card's `reviewed_state`. An approval SHALL
name that state explicitly, including any uncommitted working-tree content it covers.

#### Scenario: Approval records the state it certifies

- **WHEN** a reviewer approves a block
- **THEN** the system records `reviewed_state` as the exact state reviewed, and the approval names it

#### Scenario: Approve-with-nits is unavailable

- **WHEN** a reviewer attempts to record an approval carrying unresolved blocking findings
- **THEN** the system refuses and directs the reviewer to `approve` with dispositioned nits or to
  `request-changes`

### Requirement: Certification enumerates its claims

An approval SHALL enumerate the claims it makes and state what it does not establish. Certification text
SHALL be written to be actionable by a reviewer who did not author it, because recertification may be
performed by a different reviewer reading it cold.

#### Scenario: Approval without enumerated claims is refused

- **WHEN** a reviewer records an approval that states no claims and no limits
- **THEN** the system refuses and states that certification text is read by a later reviewer who did not
  write it

### Requirement: Nits carry a disposition

A nit SHALL be raised as an addressed comment, not as a card, so that raising one is no more costly than
commenting. Every nit SHALL receive a disposition chosen by the architect, of `fix-before-land`, `defer`
or `decline`. A reviewer MAY mark a nit as required; that marking SHALL NOT bind the architect's
disposition.

The disposition SHALL determine what becomes of the nit:

| Disposition | Outcome |
|---|---|
| `fix-before-land` | Stays inline; the block returns to `briefed`, `round` increments, and the amended state requires re-certification |
| `defer` | Promoted to an `obligation` card naming what discharges it |
| `decline` | Promoted to a `decision` card recording the reason the code is right as it stands |

A nit SHALL cease to be live only through one of these three dispositions. It SHALL NOT lapse by
neglect.

#### Scenario: Declined nit becomes a decision

- **WHEN** the architect declines a nit
- **THEN** the system creates a `decision` card recording the reason, and does not create an obligation

#### Scenario: Deferred nit becomes an obligation

- **WHEN** the architect defers a nit
- **THEN** the system creates an `obligation` card naming what will discharge it

#### Scenario: Undispositioned nits block the verdict

- **WHEN** a block is moved out of `in-review` while a nit raised against it has no disposition
- **THEN** the system refuses and names the undispositioned nits

### Requirement: Recertification re-asserts an existing claim set

The system SHALL provide a `recertify` operation by which a reviewer re-asserts an existing approval's
enumerated claims over an amended state, claim by claim, without performing a full re-audit.

Each claim SHALL be individually assertable or refusable. The reviewer SHALL re-derive each claim
against the code; reading the difference between the certified and amended states SHALL NOT be
sufficient, because a difference confined to the expected sites has been observed to be green over a
real defect.

A successful recertification SHALL re-stamp `reviewed_state` to the amended state. A refusal of any
claim SHALL be a first-class outcome that returns the block to `briefed` and increments `round`.

#### Scenario: Per-claim refusal returns the block

- **WHEN** a reviewer recertifies three claims and refuses the second
- **THEN** the system records all three outcomes, does not re-stamp `reviewed_state`, and returns the
  block to `briefed` with `round` incremented

#### Scenario: All claims re-asserted

- **WHEN** a reviewer re-asserts every enumerated claim over the amended state
- **THEN** the system re-stamps `reviewed_state` to the amended state without incrementing `round`

### Requirement: Recertification is bounded

The system SHALL permit at most one recertification per approval. A further amendment after a
recertification SHALL require a new round.

Mechanical preconditions SHALL gate recertification and SHALL be able only to refuse it, never to
satisfy it: every gate on the card SHALL have been re-run to a passing exit code, and the difference
between certified and amended states SHALL be confined to the sites of the dispositioned nits. A
difference extending beyond those sites SHALL send the block to full re-review.

#### Scenario: Second recertification is refused

- **WHEN** a block that has already been recertified once is amended again
- **THEN** the system refuses recertification and states that further iteration requires a new round

#### Scenario: Out-of-scope difference forces full re-review

- **WHEN** an amendment touches code outside the sites of the dispositioned nits
- **THEN** the system refuses recertification and routes the block to full re-review

#### Scenario: Green preconditions do not confer approval

- **WHEN** gates re-run green and the difference is confined to the nit sites
- **THEN** the system permits recertification to proceed but records no claim as re-asserted until the
  reviewer asserts it

### Requirement: Approval is role-bounded

Only the `reviewer` and `supervisor` roles SHALL record an `approve` verdict or perform a
recertification.

#### Scenario: Non-reviewing role attempts approval

- **WHEN** the `architect` or `worker` role attempts to approve a block
- **THEN** the system refuses and names the roles permitted to approve
