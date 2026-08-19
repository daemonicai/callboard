# ADR-0001 — A command-line tool is `callboard`'s single surface

- **Status:** Accepted
- **Date:** 2026-08-19
- **Deciders:** Emmz (Product Owner), Architect
- **Resolves:** `PRD.md` OQ-1

## Context

`callboard` is consulted by agents on every turn and mutated by them throughout a change. It must
enforce refusals that the incumbent `DEVLOG.md` could only record. The candidate surfaces were a
command-line tool, an MCP server, or both.

Two requirements bear directly:

- `record-retrieval` — *"the tool SHALL be an optimisation and an enforcement layer, never a
  precondition for comprehension"*, and with the tool unavailable *"the loop can proceed unenforced
  rather than blocked"*.
- `process-enforcement` — the refusals only bind if the card store cannot be written around.

`PRD.md` OQ-1 raised a further consideration: MCP tool definitions occupy context in every agent's
prompt whether used or not, in tension with G1's bounded read cost.

## Decision

`callboard` presents a **command-line tool as its only surface**. Agents invoke it through the shell.
Card bodies are supplied on stdin; machine-facing output is JSON.

Direct agent writes to the card store are denied by the repository's existing hook layer, alongside the
paths it already protects (git, `tasks.md`, the `Makefile`, `CLAUDE.md`, `.claude/`). All mutation flows
through the tool, which is what makes a refusal a refusal.

## Rationale

- **Degraded mode exists and is required.** Plain files under a CLI remain readable when the tool cannot
  run. An MCP-only store has no degraded mode at all — an unavailable server means an unreadable record,
  which the specs forbid.
- **Enforcement extends a proven boundary.** The hook layer already denies agent writes to five
  protected paths and demonstrably worked. Adding a sixth is far cheaper and lower-risk than standing up
  a second policing surface for MCP calls.
- **The context tax is smaller than it was, but not zero.** Claude Code now supports deferred MCP tools
  loaded on demand, which substantially weakens the PRD's original objection. It does not eliminate it:
  the working-context call is made every turn, so it is precisely the tool that could not be deferred,
  and its schema would sit in every agent's prompt permanently — against a response budget of under
  3,000 tokens.

## Alternatives considered

**MCP server only.** Best ergonomics for structured calls, and MCP resources are a genuinely better fit
than shelling out for attaching card content. Rejected: no degraded mode, and enforcement would need a
new policing surface built from nothing.

**CLI for mutation plus MCP for reads.** Keeps enforcement on the proven boundary while giving reads the
better shape. Rejected for v1 as two surfaces to keep consistent for a benefit that is ergonomic rather
than functional. This remains the most likely future revision — see Consequences.

## Consequences

- Multi-line Markdown card bodies must be passed on stdin rather than as arguments. Shell quoting of
  card content is a known friction point and the CLI must be designed so no workflow requires quoting a
  body inline.
- Machine-facing commands emit JSON so callers never parse human prose.
- Every command must be non-interactive and must exit non-zero on refusal, so a refusal is observable
  from an exit code rather than by reading output.
- **Revisit trigger:** if the working-context call proves awkward in practice through the shell, adding
  MCP *reads* over the same store is the intended escape hatch. Mutation and enforcement stay on the
  CLI regardless.
