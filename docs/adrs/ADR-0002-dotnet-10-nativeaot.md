# ADR-0002 — .NET 10 with NativeAOT is the implementation platform

- **Status:** Accepted
- **Date:** 2026-08-19
- **Deciders:** Emmz (Product Owner), Architect

## Context

`callboard` is a local command-line tool (ADR-0001) implementing a state machine with seven card kinds,
seven flow states, four scopes and roughly fifteen refusal rules whose entire value is that they never
fail open. It is maintained by one person. It is invoked from `PATH` by agents that cannot be expected
to manage a runtime.

.NET 10, Node 24, Go 1.26 and Rust 1.89 are all present on the development machine, so availability
discriminates nothing.

## Decision

**.NET 10, published with NativeAOT as a single self-contained binary**, one project, no runtime
dependency at the point of use.

## Rationale

- **The type system can exhaust the state space.** The refusal rules are the product. Closed unions with
  exhaustive matching turn "a transition nobody handled" from a runtime hole into a compile error, which
  is the correct place for it in a tool whose selling point is that it refuses reliably.
- **Single-binary distribution.** Agents invoke it from `PATH`. NativeAOT ships one file with no runtime
  install, no version drift, and nothing to break when an unrelated toolchain updates.
- **The gates are clean and real.** `dotnet format --verify-no-changes` is a true check mode that exits
  non-zero and rewrites nothing — the Apply Workflow requires a formatter that cannot silently edit. The
  `whitespace` / `style` / `analyzers` subcommands split naturally into a distinct format gate and lint
  gate. `dotnet build` and `dotnet test` are non-interactive and exit correctly.
- **One maintainer.** For a solo tool the cost of an unfamiliar stack is paid on every future change.
  This is an engineering criterion, not a preference — but it was applied last, after the arguments
  above, and would not have carried the decision alone.

**Explicitly not a reason:** startup latency. A change of the measured size involves a few hundred
invocations; native start versus a managed runtime is roughly thirty seconds across an entire change.
That difference does not justify any choice and was discarded as an argument.

## Alternatives considered

**Rust.** The honest runner-up, with stronger type-level guarantees than .NET for a machine that must
never fail open, and the tightest binary. Rejected on learning time and compile latency for a tool that
will be iterated on heavily by one person. Would be reconsidered if refusal correctness proved harder to
hold than expected.

**TypeScript on Node.** Would be the strongest candidate had MCP become a real surface, since its SDK is
the most mature. ADR-0001 made that moot. Discriminated unions are workable but exhaustiveness checking
is opt-in and easily lost, and it needs the runtime present at the point of use.

**Go.** Simplest build and distribution and fast to write. Rejected because it has no sum types, so
completeness of the refusal set stays a test concern rather than a compile-time one — the weakest fit
for this specific problem.

## Consequences

- Everything NativeAOT constrains applies: no runtime code generation, no unbounded reflection.
  Serialization must use source generators, and any dependency must be AOT-compatible. This is a real
  constraint on library selection and should be checked before adopting one.
- The published artefact targets `osx-arm64`. Adding further runtime identifiers is trivial and should
  be done when CI or a second machine needs one, not before.
- Gate commands are fixed by this decision and recorded verbatim in `design.md`.
