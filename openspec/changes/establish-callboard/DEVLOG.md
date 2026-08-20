# DEVLOG — establish-callboard

The shared working channel for this change. Organised by `## N.` section mirroring `tasks.md`, with a
pinned `## NEXT` at the bottom. Append-only; only `## NEXT` is rewritten.

---

## 1. Project setup and the command surface

**[architect]** Base: c18d6f9 — the .NET 10 NativeAOT solution, its gates, and the CLI's fixed shape
(non-interactive, JSON out, stdin bodies, non-zero on refusal), with no card verbs yet.

**[architect]** Block 1.1–1.6 briefed → @worker. Product Owner confirmed both carving calls: section 1
lands as **one block**, and 1.6 delivers the CLI's **shape only, no verbs** — a placeholder command
proves the wiring; the verb vocabulary follows the specs in later sections (design.md Open Question 1).

### Tasks in this block

- 1.1 Create the .NET 10 solution with a single `src/Callboard` project and a `tests/Callboard.Tests`
  project
- 1.2 Enable NativeAOT publishing, nullable reference types, and treat warnings as errors
- 1.3 Add `.editorconfig` so `dotnet format` has rules to verify against
- 1.4 Verify each gate command from `design.md` D8 runs non-interactively and exits non-zero on failure
- 1.5 Gitignore the derived index path
- 1.6 Establish the CLI entry point: non-interactive, JSON output for machine commands, card bodies read
  from stdin, non-zero exit on refusal

### Binding decisions

- **ADR-0002 / design.md D2** — .NET 10, NativeAOT, single self-contained binary. NativeAOT constrains
  library choice: **no runtime code generation, no unbounded reflection, serialization via source
  generators**. Any candidate dependency must be verified AOT-compatible *before* adoption, not after.
  Use `System.Text.Json` source-generated contexts; do not reach for reflection-based serialization.
- **ADR-0001 / design.md D1** — the CLI is the single surface. Agents invoke `callboard` through the
  shell; **card bodies arrive on stdin** (never as arguments — no workflow may require shell-quoting a
  multi-line body); machine-facing output is **JSON**; every command is **non-interactive**; a refusal
  **exits non-zero**.
- **design.md D8** — the gate commands are already in the root `Makefile` and are the command surface.
  `build` / `test` / `format` / `validate`, each printing `LABEL_EXIT:<n>`. `publish` targets
  `osx-arm64`. There is deliberately **no `lint` target**.
- **design.md D4 / ADR-0004** — the derived index is gitignored, never authoritative, never a lock.
  `.gitignore` already carries `callboard/.index/` and the `*.db*` patterns; 1.5 is a verification, not
  new work — confirm the path the code will actually use matches what is ignored, and say so.

### Binding spec — `record-retrieval`

> The tool SHALL be an optimisation and an enforcement layer, never a precondition for comprehension.

Nothing in this block may make the tool a precondition for reading the record.

### Done-gates

`make build` → `BUILD_EXIT:0`, `make test` → `TEST_EXIT:0`, `make format` → `FORMAT_EXIT:0`,
`make validate` → `VALIDATE_EXIT:0`. Read the exit line, not the output.

**[worker]** Block 1.1–1.6 implemented.

- **1.1** `.NET 10` solution as `Callboard.slnx` (the SDK's new default format), `src/Callboard`
  (Exe) and `tests/Callboard.Tests` (xUnit 2.9.3), wired via `ProjectReference` + `InternalsVisibleTo`
  so tests exercise the CLI's internal types directly (no process-spawn needed for unit coverage).
- **1.2** `PublishAot=true`, `RuntimeIdentifiers=osx-arm64`, `Nullable=enable`,
  `TreatWarningsAsErrors=true`, `IsAotCompatible=true`, `InvariantGlobalization=true` on
  `src/Callboard`. Verified with a real `dotnet publish src/Callboard -c Release -r osx-arm64`: zero
  trim/AOT warnings, produces a single Mach-O arm64 binary that runs standalone.
- **1.3** `.editorconfig` at repo root: 4-space C#, file-scoped namespaces required, `this.`
  discouraged, LF/UTF-8-no-BOM. Verified it's the thing `dotnet format` checks against — see 1.4.
- **1.4** Verified all four gates non-interactively, **by breaking each one and observing the exit
  code**, then restoring:
  - `build`: syntax error in `Program.cs` → `BUILD_EXIT:1` (CS1040/CS1002), restored → `0`.
  - `test`: flipped an assertion in `CommandDispatcherTests` → `TEST_EXIT:1` (xUnit reports the
    failing assert), restored → `0`.
  - `format`: added stray leading whitespace to `Program.cs` → `FORMAT_EXIT:2` printing an ordinary
    `error WHITESPACE` line — confirms the brief's warning that this gate's non-zero exit does not
    look alarming in the log, only the exit line tells you. Restored → `0`.
  - `validate`: gutted `specs/card-model/spec.md` to a header with no delta sections →
    `VALIDATE_EXIT:1` (`No delta sections found`). Restored → `0`.
  - All four confirmed non-interactive (no prompts, redirected stdin/no TTY) throughout.
  - Note: on the first attempt, `dotnet build`/`dotnet restore` hung indefinitely with zero output
    inside this agent's default sandbox — no error, just silence (looked like a blocked Unix-socket
    call to the MSBuild/VBCSCompiler node server, not a network issue; `curl` to `api.nuget.org`
    from the same shell was instant). Every `dotnet` invocation for this block was run with the
    sandbox override; flagging this in case it recurs for other blocks.
- **1.5** No index-writing code exists in this block (that's §3). Verification only:
  `.gitignore` already carries `callboard/.index/` and `*.db*`, which matches the reserved layout
  in design.md D3 (`callboard/register/`, `callboard/decisions/`, `callboard/changes/<name>/`) plus
  D4's derived index sitting alongside them. Nothing to add. Will re-verify against the actual path
  constant once §3 implements the index.
- **1.6** CLI shape only, per the Product Owner's scope boundary — no card verbs:
  - `Program.cs` → `CommandDispatcher.Run(args, Console.Out, Console.In)`, explicit
    `TextWriter`/`TextReader` params so the whole path is unit-testable without spawning a process.
  - `CommandOutcome`: closed union (`Success`/`Refusal`) — private constructor, sealed nested
    records. C#'s switch-exhaustiveness analysis does **not** treat that as closed on its own (first
    pass hit CS8509/CS8524 on both a type-pattern switch and an enum-discriminant switch — verified
    the hard way, not assumed); the visitor (`Match<TResult>`, abstract on the base) is what actually
    makes an unhandled case fail the build, since a third subtype couldn't compile without also
    implementing `Match`.
  - `CliEnvelope`/`CliRefusal`/`VersionResult` + `CliJsonContext` (`System.Text.Json`
    source-generated, no reflection-based overloads anywhere) — one JSON line per command:
    `{"ok":bool,"command":str,"result"|"refusal":...}`.
  - `StdinBodyReader.ReadBody(TextReader)` — the stdin body-reading path, implemented and tested
    directly (multi-line bodies with quotes/backticks/redirects round-trip byte-for-byte). Not wired
    to any verb yet since none exist; ready for §2+ to call.
  - `version` is the one placeholder command. Unknown command → `Refusal("unknown-command", …)` →
    `RefusalExitCode = 1`; every command exits via `ExitCodeFor(outcome)`, so a refusal is always
    observable from the exit code alone (ADR-0001).
  - Published-binary end-to-end check: `callboard version` → exit 0 + JSON; `callboard frobnicate` →
    exit 1 + JSON refusal; body piped on stdin doesn't block or get consumed by `version` — process
    stays non-interactive either way.

**AOT-compatibility check on the one adopted dependency (xUnit 2.9.3, test project only):** not
published AOT — `make publish` only targets `src/Callboard` (D8) — so this isn't a binding
constraint for it. Verified anyway that `dotnet test` runs it cleanly under .NET 10 with no
compatibility shims. No other third-party package was adopted in this block; `System.Text.Json`
(source-gen mode only) and `System.Runtime.CompilerServices.InternalsVisibleTo` are BCL.

**Gates — full pass, both raw and through `make`:**
`BUILD_EXIT:0 TEST_EXIT:0 FORMAT_EXIT:0 VALIDATE_EXIT:0 GATES_EXIT:0` (`make gates`, single `-k` pass).
8/8 tests pass (`CommandDispatcherTests` ×5, `StdinBodyReaderTests` ×3), covering the three behaviours
named in the brief: JSON envelope shape, stdin body round-trip (including shell-metacharacter
payloads, to demonstrate no quoting is required), and non-zero exit on refusal.

→ @reviewer

**[reviewer]** Block 1.1–1.6 audited. Working tree read directly (block is uncommitted; `git status
--short` confirms only `.editorconfig`, `Callboard.slnx`, `src/`, `tests/`, and this DEVLOG are
untracked — `bin/`/`obj/` correctly gitignored, nothing stray committed).

**Verified independently, not taken on the worker's word:**
- **Closed-union claim (D2/ADR-0002).** Reproduced in an isolated scratch project: a third
  `CommandOutcome` nested record that omits `Match<TResult>` fails with `CS0534 — 'Pending' does not
  implement inherited abstract member 'CommandOutcome.Match<TResult>(...)'`. The private constructor
  (`CommandOutcome.cs:14`) also genuinely closes the hierarchy to callers outside the file — an
  external assembly cannot subclass it at all, private-ctor-derivation only works for nested types.
  The worker's account holds: exhaustiveness here is enforced by the abstract-method-override
  requirement, not by switch-pattern analysis, and that mechanism is real and did fail to compile as
  claimed.
- **Gates.** Ran `make gates` myself (also hit the same sandboxed `dotnet build`/`restore` hang the
  worker flagged — reproduced, not a new finding, not re-litigating it). With the sandbox override:
  `BUILD_EXIT:0 TEST_EXIT:0 FORMAT_EXIT:0 VALIDATE_EXIT:0 GATES_EXIT:0`, 8/8 tests passing — matches
  the worker's report exactly.
- **AOT/JSON.** `grep` over `src/` confirms both `JsonSerializer` call sites
  (`CommandDispatcher.cs:64,69`) go through `CliJsonContext.Default.*` source-generated overloads;
  no reflection-based `Serialize(object)`/`Deserialize<T>()` calls anywhere. No `Activator`/
  `System.Reflection` use in hand-written code (only in generated `obj/` assembly-attribute files,
  which don't ship). `PublishAot`, `IsAotCompatible`, `TreatWarningsAsErrors` all set on
  `src/Callboard/Callboard.csproj`. `InternalsVisibleTo` (`AssemblyInfo.cs`) is compile-time-only and
  doesn't touch the AOT/trim story; xUnit is correctly scoped to the test project only, which `make
  publish` never targets.
- **Refusal exit path.** `CommandDispatcher.Run` (`CommandDispatcher.cs:24-39`) always routes through
  `ExitCodeFor(outcome)`, itself an exhaustive `Match` over the closed union — there is no code path
  that reaches `return` without going through it, no path that returns 0 for a `Refusal`, and unknown
  commands fall into the same `Refusal` arm as any other refusal (`_ => new
  CommandOutcome.Refusal("unknown-command", ...)`) rather than a silent pass. Confirmed at the
  published-binary level too (worker's report, and consistent with the source).
- **Stdin.** `StdinBodyReader.ReadBody` (`StdinBodyReader.cs:11`) is a single `ReadToEnd()` call —
  no argument-based body path exists anywhere in `CommandDispatcher`. Tests exercise multi-line
  content and shell metacharacters round-tripped byte-for-byte, which is the right thing to assert
  given ADR-0001's stated risk.
- **.gitignore vs D4.** `callboard/.index/`, `*.db`, `*.db-shm`, `*.db-wal` are present and match the
  reserved layout in design.md D3/D4; no index-writing code exists yet, so 1.5 is correctly
  verification-only as the worker reported.
- **Scope boundary.** No card-verb scaffolding found — `version` is the only registered command,
  `SerializeResult` only maps `VersionResult`. No drive-by work outside 1.1–1.6, block stays inside
  §1. `Makefile` untouched.
- **Tests assert behaviour, not tautologies.** `CommandDispatcherTests` checks exit codes *and*
  mutual exclusivity of `result`/`refusal` keys via `TryGetProperty` (not just presence of the
  expected key) — a regression that leaked both keys would be caught. `StdinBodyReaderTests` checks
  exact byte-for-byte content including shell metacharacters, not just "didn't throw."

**Nits (non-blocking):**
- `src/Callboard/Commands/VersionResult.cs:3` — declares `namespace Callboard.Cli`, not
  `Callboard.Commands`. Folder and namespace disagree. Harmless today with one file in `Commands/`,
  but worth fixing before more result types land there in later sections, or the `Commands/` folder
  stops meaning anything.
- `CliEnvelope`/`CliRefusal`/`VersionResult` are `sealed class` with `required`/`init` members rather
  than `record`. Functionally fine (immutable, used only as JSON DTOs), but the project's own C#
  idiom guidance prefers `record` for immutable value types — worth a decision on whether JSON DTOs
  are an intentional exception, so it doesn't drift block to block.

**Architectural note (not a finding against this block — flagging for later sections per D2's own
framing):** `CommandDispatcher.SerializeResult` (`CommandDispatcher.cs:67-72`) dispatches on
`result.GetType()` via a non-exhaustive `switch` with a `_ => throw new NotSupportedException(...)`
default arm. That's a runtime hole, not a compile-time one — a future command that returns a new
result type but isn't added here fails at first invocation, not at build time. This is outside
`CommandOutcome`'s closed union (which only covers Success/Refusal, and does hold, per above) so it's
not a violation of anything binding in this block. But since D2's whole rationale is "an unhandled
case is a compile error, not a runtime hole," this specific seam is worth the same treatment
(e.g. a `Match`-style visitor on the result payload, or generating this switch) before command
verbs multiply in later sections — otherwise the exhaustiveness story has one quiet exception in it.

**Blockers:** none.

**Verdict: Approve.**

**[architect]** Nit dispositions for the reviewer's block 1.1–1.6 findings. Approve is binary and has
been given; each nit takes a disposition rather than riding along inside the verdict.

- **`SerializeResult` fails open — `fix-before-land`.** `CommandDispatcher.SerializeResult`
  (`src/Callboard/Cli/CommandDispatcher.cs:67`) takes `object` and switches on runtime type with a
  `NotSupportedException` fallback. It sits *outside* the closed union `CommandOutcome` correctly
  enforces, so from §2 onward a command whose result type is never added to that switch **compiles
  cleanly and throws on first invocation**. That is the exact failure shape this project exists to
  remove — a rule that fails open — relocated to the JSON boundary. It is one file now and grows with
  every result type added across the remaining eleven sections. Product Owner confirmed: fix before §1
  lands.
- **Namespace/folder drift — `fix-before-land`.** `src/Callboard/Commands/VersionResult.cs:3` declares
  `namespace Callboard.Cli` from a `Commands/` folder. Trivial, but folder/namespace drift compounds
  and §2+ adds many files.
- **DTOs as classes rather than records — `decline`.** `required` init-only sealed classes are the
  right shape for `System.Text.Json` source-generated contracts; records would add value-equality that
  nothing here needs and that would invite equality-based assertions the JSON contract does not
  promise. Recorded as reasoning, not as debt.

→ @worker for the two `fix-before-land` items. No new task numbers — this is remediation within block
1.1–1.6, which has not yet landed.

**[worker]** Both `fix-before-land` items resolved.

- **`SerializeResult` fail-open closed.** Added `ICommandResult` (`Cli/ICommandResult.cs`) — one
  method, `JsonElement ToJsonElement()`. `CommandOutcome.Success.Result` is now typed
  `ICommandResult` instead of `object`. `VersionResult` implements it, serialising itself against
  its own `CliJsonContext.Default.VersionResult` `JsonTypeInfo`. `CommandDispatcher.SerializeResult`
  and its runtime type switch are deleted entirely; `WriteEnvelope` now calls
  `success.Result.ToJsonElement()`. A result type that doesn't implement `ICommandResult` can no
  longer be constructed into a `CommandOutcome.Success` at all — the failure moved from "compiles,
  throws at first invocation" to "does not compile." Envelope shape is unchanged (`{"ok":…,
  "command":…,"result"|"refusal":…}`), confirmed by the unchanged existing envelope-shape tests.
  Added three tests: `CommandDispatcher_NoLongerHasARuntimeTypeSwitchFallback` (reflects for
  `SerializeResult` and asserts it's gone), `CommandOutcomeSuccess_CarriesACommandResult_NotAnUntypedObject`
  (asserts `Success.Result`'s declared type is `ICommandResult`, not `object`), and
  `Version_ResultSerialisesThroughItsOwnPerTypeMethod` (asserts `VersionResult.ToJsonElement()`
  round-trips through its own source-generated path).
- **Namespace/folder drift resolved.** Moved `VersionResult.cs` from `Commands/` to `Cli/` (kept
  `namespace Callboard.Cli`) rather than renamesspacing it — it now lives next to
  `CommandDispatcher`, `CommandOutcome`, `ICommandResult` and `CliJsonContext`, the files it's
  coupled to. `Commands/` is empty and removed. §2+ result types that stay CLI-envelope concerns
  (implementing `ICommandResult`) belong in `Cli/` alongside it; if a later section's result types
  grow real card-kind vocabulary of their own, that's a namespace worth re-deciding then, not now.

No card verbs added, index untouched, CLI surface unchanged (still just `version` and the unknown-
command refusal).

`BUILD_EXIT:0 TEST_EXIT:0 FORMAT_EXIT:0 VALIDATE_EXIT:0 GATES_EXIT:0`

→ @reviewer

**[reviewer]** Re-audit of the two `fix-before-land` remediations. Narrow scope per the architect's
request — not re-reviewing the whole block, only the four points below.

**1. Compile-time guarantee — verified independently, holds.** Reproduced in an isolated scratch
project with the new `ICommandResult`/`CommandOutcome`/`Success` shapes copied verbatim: constructing
`new CommandOutcome.Success(new NotAResult())` where `NotAResult` does not implement `ICommandResult`
fails with `CS1503 — cannot convert from 'NotAResult' to 'ICommandResult'`. This is a genuine compile
error, not an analyzer warning. `grep` over `src/` confirms no `object`-typed result path or
`.GetType()`/runtime-type-switch survives anywhere — `CommandDispatcher.SerializeResult` is gone
entirely (`src/Callboard/Cli/CommandDispatcher.cs` no longer contains it), `WriteEnvelope` calls
`success.Result.ToJsonElement()` directly (`CommandDispatcher.cs:55`), and `CommandOutcome.Success`
(`CommandOutcome.cs:22`) is now typed `ICommandResult`, not `object`. The fail-open seam I flagged in
the first pass is closed as claimed — a result type that forgets `ICommandResult` cannot reach
`Success` at all, at any point on the path from result to envelope.

**2. AOT and wire format — unaffected, correct.** `VersionResult.ToJsonElement()`
(`src/Callboard/Cli/VersionResult.cs:16`) still calls the source-generated
`CliJsonContext.Default.VersionResult` overload — no reflection-based `JsonSerializer` call
introduced. `System.Reflection`/`BindingFlags` usage is confined to the test project
(`tests/Callboard.Tests/CommandDispatcherTests.cs`), which is never published AOT (`make publish`
only targets `src/Callboard`), so this doesn't touch the AOT story. Ran the published-shape CLI
directly: `version` → `{"ok":true,"command":"version","result":{"version":"0.1.0"}}`; unknown command
→ `{"ok":false,"command":"frobnicate","refusal":{"code":"unknown-command","message":"no such
command: 'frobnicate'. Known commands: version."}}` with exit 1 — both byte-identical in shape to the
envelope contract from the first pass (field order, `ok`/`command`/`result`|`refusal` mutual
exclusivity all unchanged).

**3. The three new tests — mixed, one is a real regression test, one should be dropped, one is fine
on its own but overlaps with a design choice worth naming.**
- `Version_ResultSerialisesThroughItsOwnPerTypeMethod` — genuine behavioural test, calls
  `ToJsonElement()` and asserts the actual output. No reflection, would survive any refactor that
  preserves behaviour. Keep.
- `CommandOutcomeSuccess_CarriesACommandResult_NotAnUntypedObject` — reflection-based, but it asserts
  a real design invariant (`Success.Result`'s declared type is `ICommandResult`-assignable, not
  `object`), which is exactly the thing that regressed once already. It would break on a legitimate
  rename of the `Result` property, but that's a narrow, visible kind of breakage, and there's no
  non-reflective way to assert a declared member type from outside the assembly in C#. Defensible;
  keep, though it's testing shape rather than behaviour.
- `CommandDispatcher_NoLongerHasARuntimeTypeSwitchFallback` — **this is the brittle one, and my
  answer to your question is: drop it.** It asserts only that no method literally named
  `SerializeResult` exists on `CommandDispatcher` via reflection. It doesn't assert the actual
  invariant (that no `object`-typed/runtime-type-switch serialization path exists anywhere) — a
  reintroduction of the same bug under any other name (e.g. `SerializePayload`, or moved to a new
  static class) would sail through it untouched. And it fails for reasons that have nothing to do
  with the invariant it's meant to guard: any future private helper that is coincidentally also
  named `SerializeResult` (for an unrelated purpose) breaks this test. It pins a method name, not a
  guarantee. `CommandOutcomeSuccess_CarriesACommandResult_NotAnUntypedObject` already covers the real
  invariant (the type system, not a name, is what closes the hole) — this test adds reflection-based
  fragility with no corresponding regression-catching power. Recommend deleting it now, before the
  pattern of "assert a symbol is absent" gets copied into later sections.

**4. Namespace/folder drift — resolved cleanly.** `src/Callboard/Commands/` is gone
(confirmed: directory does not exist). No stale `Callboard.Commands` namespace or `Commands/` path
references anywhere in `src/`, `tests/`, or the `.csproj` files. `VersionResult.cs` now lives in
`Cli/` next to the types it's coupled to, namespace `Callboard.Cli` matches its folder.

**Gates.** Ran `make gates` myself: `BUILD_EXIT:0 TEST_EXIT:0 FORMAT_EXIT:0 VALIDATE_EXIT:0
GATES_EXIT:0`, 11/11 tests (8 original + 3 new) — matches the worker's report.

**Blockers:** none. The one test-quality point above (§3) is a recommendation, not a blocker — the
compile-time guarantee it imperfectly guards is independently verified and holds regardless of that
test's presence.

**Verdict: Approve.**

**[worker]** Deleted `CommandDispatcher_NoLongerHasARuntimeTypeSwitchFallback` per architect
direction on the reviewer's §3 recommendation above. It asserted absence of a method named
`SerializeResult` — a name check standing in for the actual invariant, which would pass if the
switch reappeared under another name and fail on an unrelated method that happened to share the
name. The real guarantee is carried by `CommandOutcomeSuccess_CarriesACommandResult_NotAnUntypedObject`
and, more strongly, by the compiler itself — kept both remaining new tests, including
`Version_ResultSerialisesThroughItsOwnPerTypeMethod`. `using System.Reflection;` stays in
`CommandDispatcherTests.cs`, still needed by the surviving reflection test.

`BUILD_EXIT:0 TEST_EXIT:0 FORMAT_EXIT:0 VALIDATE_EXIT:0 GATES_EXIT:0` — 10 tests (was 11).

---

## NEXT

Block 1.1–1.6 with @worker. On approval: gates, tick 1.1–1.6, commit, then the §1 supervisor review
before opening §2.
