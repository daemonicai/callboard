## Purpose

Governs what a review verdict certifies — approval as a binary certification of one exact state, the
claims and limits it must enumerate, and the disposition every nit must receive so none dies by
neglect. A certification covers one state and one state only: once that state changes, the approval is
spent, and the block is reviewed afresh rather than having its claims re-asserted over the difference.

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
SHALL be written to be actionable by a reviewer who did not author it, because the reviewer who reads a
block's certification is frequently not the one who wrote it.

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
| `fix-before-land` | Stays inline; the block returns to `briefed`, `round` increments, and the amended state requires a **fresh review** |
| `defer` | Promoted to an `obligation` card naming what discharges it |
| `decline` | Promoted to a `decision` card recording the reason the code is right as it stands |

A nit MAY name the sites it concerns, so that a worker picking the fix up knows where to start. Those
sites SHALL be guidance to whoever does the work and SHALL NOT be treated as a bound on what the fix may
touch: a nit's stated sites are where the reviewer noticed the problem, not a claim about where the
problem ends.

A nit SHALL be raised only against a block that is under review. Raising one against a block in any
other state SHALL be refused, naming the state the block is in and the obligation route below.

An observation made outside a review is not thereby lost: where the architect or the Product Owner
judges that it needs fixing, it SHALL be recorded as an `obligation` naming the section expected to
discharge it. That judgement SHALL NOT be automated — the system SHALL NOT promote a refused nit to an
obligation on its own, because whether an observation needs fixing is exactly the decision the system
cannot make and MUST NOT record as though it had been made.

This bound is what makes the rule below enforceable rather than aspirational. A nit ceases to be live
only by disposition, and a disposition is refused while the block cannot move; a nit raised against a
terminal block could therefore never be dispositioned and never block anything, lapsing by exactly the
neglect this requirement forbids.

A nit SHALL cease to be live only through one of these three dispositions. It SHALL NOT lapse by
neglect.

#### Scenario: Declined nit becomes a decision

- **WHEN** the architect declines a nit
- **THEN** the system creates a `decision` card recording the reason, and does not create an obligation

#### Scenario: Deferred nit becomes an obligation

- **WHEN** the architect defers a nit
- **THEN** the system creates an `obligation` card naming what will discharge it

#### Scenario: Nit raised outside review is refused

- **WHEN** a nit is raised against a block that is not under review
- **THEN** the system refuses, names the block's current state, and names recording an `obligation` as
  the route for an observation the architect or Product Owner judges needs fixing

#### Scenario: Undispositioned nits block the verdict

- **WHEN** a block is moved out of `in-review` while a nit raised against it has no disposition
- **THEN** the system refuses and names the undispositioned nits

### Requirement: Approval is role-bounded

Only the `reviewer` and `supervisor` roles SHALL record an `approve` verdict.

#### Scenario: Non-reviewing role attempts approval

- **WHEN** the `architect` or `worker` role attempts to approve a block
- **THEN** the system refuses and names the roles permitted to approve
