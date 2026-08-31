## Purpose

The core read operation: given a role, return everything that role needs to act and nothing else, within
a stated budget that does not grow as a change lengthens — plus the derived state summary that replaces
the hand-maintained pin.

## Requirements

### Requirement: Role-scoped working context

Given a role, the system SHALL return that role's complete working context, composed of exactly:

1. the live standing rules and hazards, first and unconditionally;
2. the role's queue — cards it owns, plus cards carrying unresolved comments addressed to it — in a
   stated order;
3. the top queue item in full: its body, `base`, referenced tasks, constraints, unresolved threads
   addressed to the caller, and the previous round's verdict where one exists;
4. nothing else.

The response SHALL NOT contain closed cards, another role's queue, or narrative from prior sections.

#### Scenario: Context contains only the caller's work

- **WHEN** the `worker` role requests its working context
- **THEN** the response contains the register, the worker's queue and its top item in full, and contains
  no card owned solely by another role

#### Scenario: Addressed thread pulls a card into the queue

- **WHEN** a card owned by the architect carries an unresolved comment addressed to the reviewer
- **THEN** that card appears in the reviewer's queue

#### Scenario: Prior verdict accompanies a remediation

- **WHEN** a worker requests context and its top item is a block at round 2
- **THEN** the response includes the verdict recorded at round 1

### Requirement: Working context fits a stated budget

The working-context response SHALL fit a stated budget, targeting under 3,000 tokens. The budget SHALL
be a requirement of the response and not a target it may exceed.

Where content would exceed the budget, the system SHALL shorten narrative only. It SHALL NOT shorten the
register or the brief, and SHALL state explicitly that it has truncated and what it truncated.

Where the register and the brief **alone** — with all narrative already dropped — still exceed the
budget, the system SHALL deliver both whole rather than shorten either, and SHALL state that the budget
was exceeded and which of the two drove it. This is the one case the budget may be exceeded; every other
case is governed by the paragraph above.

#### Scenario: Oversized content is truncated in the narrative

- **WHEN** a card's accumulated thread would push the response past its budget
- **THEN** the narrative is shortened, the register and brief are delivered whole, and the response says
  what was truncated

#### Scenario: Truncation is never silent

- **WHEN** any part of a response is omitted for budget
- **THEN** the response states that omission

#### Scenario: The register and brief alone exceed the budget

- **WHEN** the register and the brief, with all narrative already dropped, still exceed the budget
- **THEN** the response delivers both whole, states that the budget was exceeded, and names which of the
  two drove the overage

### Requirement: Working-context cost does not grow with the change

The size of the working-context response SHALL be governed by the size of the live working set rather
than by the accumulated length of the change. Its size late in a change of comparable size to the
measured one SHALL remain within 20% of its size at the start.

#### Scenario: Cost is flat across a long change

- **WHEN** working context is requested at the twelfth section of a change and at its first
- **THEN** the two responses are within 20% of each other in size

### Requirement: Derived state summary

The system SHALL produce a summary of overall process state comprising the open sections, task
completion counted from the task list itself, the live obligations with the section that owes each, the
open questions with who owes each answer, and every blocked card with what blocks it.

Every figure in this summary SHALL be derived at the time of the request. No figure SHALL be hand-entered
anywhere in the system, and the system SHALL NOT maintain a hand-written pin.

#### Scenario: Summary is derived on request

- **WHEN** any role requests the state summary
- **THEN** every count in the response is computed from the current record

#### Scenario: Hand-entered state is not accepted

- **WHEN** a role attempts to record a count or a next-step pin as stored text
- **THEN** the system refuses and states that this state is derived

### Requirement: Escalation severity is derived from ownership

The system SHALL derive escalation severity from a question's owner rather than storing it. A question
owned by the Product Owner SHALL constitute a stop-and-ask and SHALL halt the cards it blocks. A question
owned by any other role SHALL leave the cards it blocks free to proceed on other fronts.

#### Scenario: Product Owner question halts its dependents

- **WHEN** an open question owned by the Product Owner blocks a card
- **THEN** that card is reported as halted pending the answer

#### Scenario: Agent-owned question does not halt

- **WHEN** an open question owned by the reviewer blocks a card
- **THEN** that card remains available for work on fronts the question does not block

#### Scenario: A deferred Product Owner question still halts

- **WHEN** a question owned by the Product Owner is deferred rather than answered, and it blocks a card
- **THEN** that card is still reported as halted pending the answer — deferring does not lift the halt
