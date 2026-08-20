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

**[supervisor]** Section 1 review — scope `c18d6f9..6065453` (one commit, `6065453`: block 1.1–1.6 plus
two in-block remediation rounds). Read the full §1 thread, `proposal.md`, `design.md` D1/D2/D4/D5/D8,
ADR-0001/0002/0003/0004, `specs/record-retrieval/spec.md`, and every file in the diff. I did not re-run
the gates; the exit lines are in this thread three times over (`BUILD_EXIT:0 TEST_EXIT:0 FORMAT_EXIT:0
VALIDATE_EXIT:0 GATES_EXIT:0`), quoted by worker and reviewer independently.

I am not re-litigating anything the reviewer settled, and I found nothing to add on the diff-local axis —
the `ICommandResult` remediation genuinely moved a fail-open from runtime to compile time, and the
closed-union claim holds. My concern is the thing block review could not weigh: **§1's whole deliverable
is a shape that §2–§13 get poured into**, and that shape has three fail-open holes which are invisible
while there is exactly one verb and nothing that can throw. All three are cheap now and expensive from §2
onward.

**Verdict: Request changes.**

---

### Blocker 1 — the entry point fails open on arguments (`src/Callboard/Cli/CommandDispatcher.cs:26`)

```csharp
var command = args.Length > 0 ? args[0] : string.Empty;
```

`args[1..]` is read by nothing and discarded silently. `callboard version --json --oops` today emits
`{"ok":true,…}` and exits `0`. §1 correctly made an unrecognised *command* refuse; it left an
unrecognised *argument* pass. That is the same failure class the section closed at the verb boundary,
left open one token to the right, in the product whose premise is that a rule refuses rather than
records.

It belongs to §1 rather than later because every verb from §2 on parses its own flags. With no convention
here, each worker decides independently whether an unknown flag refuses, and by §9 the CLI carries
thirteen sections' worth of separately-negotiated argument handling — the exact drift a "fixed shape"
section exists to prevent. The concrete harm: an agent typos `--scope`, gets `ok:true`, and a different
action was performed.

Wanted: one argument-boundary rule in the dispatcher — an argument no command consumed is a refusal —
plus a test. In the one-verb case this is a few lines; it does not stay a few lines.

### Blocker 2 — the stdin body path can hang, which is the property 1.6 was chartered to guarantee

`src/Callboard/Program.cs:3` passes `Console.In` unconditionally; `src/Callboard/Cli/StdinBodyReader.cs:11`
is a bare `input.ReadToEnd()`. There is no `Console.IsInputRedirected` check anywhere in `src/`
(grepped). The first verb that reads a body — §2.3's comment append, or anything in §4–§5 — invoked
without a redirect blocks on the terminal until EOF. A command that waits on a human at a TTY is
interactive, and ADR-0001's Consequences make non-interactive binding for *every* command.

The worker's end-to-end check confirmed `version` doesn't block, but `version` never calls `ReadBody` —
the non-interactive property was demonstrated only on the path that cannot violate it. Nothing in the
section exercises the path that can.

Because `ReadBody` takes an explicit `TextReader` (the right call — it keeps the path testable), the
guard cannot live inside it: it has to live at the composition root, and the composition root is §1's
deliverable. Left unfixed, §2's worker invents it, or doesn't.

Wanted: the redirect guard at the `Console.In` boundary, refusing when a body is required and stdin is
not redirected, with a test. Note that "empty redirected stdin" must stay distinct from "no stdin at
all" — `ReadBody_EmptyStdinReturnsEmptyString` covers the first half of that distinction and nothing
covers the second.

### Blocker 3 — no failure boundary, so the JSON contract holds on two exit paths out of three

`Program.cs` is `return CommandDispatcher.Run(args, Console.Out, Console.In);` with no `try`/`catch`, and
`Console.Error` appears nowhere in `src/`. Today nothing can throw, so this is invisible. §2 lands file
IO, atomic rename, and — 2.6 — a per-card advisory lock with a timeout whose *specified* behaviour is "a
failure message naming the card and the holder". That message has nowhere to go: an escaping exception
produces a .NET stack trace on stderr and **no envelope at all**, against ADR-0001's "machine-facing
commands emit JSON so callers never parse human prose".

The section that establishes "one JSON line per command" is the section that has to make it true on every
exit path, including the one where the tool itself breaks. §9.1 defines the refusal *payload* later; that
is not the same thing as having a boundary at all.

Wanted: a top-level boundary that turns an escaping exception into an envelope, so the invariant is
"every invocation emits exactly one JSON line on stdout, always", with a test.

**❓ @architect — one decision I can't make for you, and it belongs in this fix rather than in §9.**
Blocker 3 needs an exit code, which forces the exit-code convention now. Today `0` = success, `1` =
refusal, and nothing else exists. A caller cannot distinguish *"the board refused you"* (adapt — the
process said no) from *"the tool broke"* (`record-retrieval`: proceed unenforced rather than blocked).
Those demand opposite behaviour from an agent, and `record-retrieval`'s tool-unavailable scenario is what
makes the distinction load-bearing rather than tidy. My recommendation: keep `1` for refusal, reserve `2`
for tool failure, documented next to `RefusalExitCode`. Neither D1 nor ADR-0001 says more than "non-zero
on refusal", so this is your call — but §1 is where it gets made, because §13.4 documents these commands
to the agents and every section between here and there will have assumed an answer.

---

### Suggested remediation shape

One block, entry-point-local, no new `N.M` numbers, ticks nothing:

1. Argument-boundary refusal in `CommandDispatcher.Run` + test that an unconsumed argument exits non-zero.
2. `Console.IsInputRedirected` guard at the composition root + refusal + test; leave `ReadBody`'s
   `TextReader` signature alone.
3. Top-level failure boundary emitting an envelope + test that a thrown exception still yields exactly one
   JSON line; exit code per your answer above.

Nothing outside `Program.cs`, `CommandDispatcher.cs` and the two test files needs to move.

---

### Architectural notes — for `## NEXT`, not the fix block

- **The refusal payload is defined in §9 but first emitted in §2.** `CliRefusal` is `{code, message}`;
  §9.1 requires a refusal to name the rule, state what would satisfy it, and record role and timestamp —
  four-plus fields. Between now and §9, §2.6 (lock timeout), §4.4 (scope refusal), §5.2/5.5 and §6.2 all
  emit refusals against the two-field shape and then get retrofitted. Consider pulling §9.1's *format*
  forward into the first section that emits a real refusal.
- **`CommandOutcome.Refusal(string Code, string Message)` takes a free string**, and `"unknown-command"`
  is an inline literal (`CommandDispatcher.cs:32`). `design.md`'s own risk mitigation reads "the refusal
  set is modelled as a closed union so an unhandled case is a compile error". `CommandOutcome` closes
  *success vs refusal*; it does not close *the set of refusals*. Closing it is legitimately §9's work, but
  the constructor established here is the one every intervening section calls — the longer it accepts a
  bare string, the more call sites the closed set has to be retrofitted through. Worth deciding before §5.
- **Nothing declares the stdout/stderr split.** The envelope goes to stdout including refusals; stderr is
  unused. Good convention, written down nowhere — a §2+ worker may put diagnostics on stdout and break
  the one-line contract. A sentence in the dispatcher's doc comment fixes it permanently.
- **`.editorconfig` is a real ruleset, with a caveat worth knowing.** File-scoped namespaces,
  accessibility modifiers, using placement and the naming rules sit at `:warning` and do fail
  `dotnet format --verify-no-changes`; the whitespace and formatting rules always apply. But the eight
  `:suggestion` rules (`var` style, expression-bodied members, braces, switch expressions, pattern
  matching, primary constructors) are below the formatter's default `warn` severity and fail nothing,
  ever. `EnforceCodeStyleInBuild` is unset, so IDE rules never reach `build` either. All defensible — just
  don't let a later review cite a `:suggestion` rule as enforced.
- **1.4 is the best-evidenced part of this section, with one gap.** Breaking each gate and observing the
  code — including catching `format`'s exit `2` behind innocuous-looking output — is exactly right, and I
  would rather have this than a claim. The gap: only the *whitespace* facet of `format` was broken, so
  that gate's style and analyzer facets remain assumed rather than demonstrated. Also undemonstrated:
  that a red `make gates` reports *every* failing gate rather than stopping at the first (the `-k`
  promise). Neither blocks; both are cheap to close opportunistically.
- **1.5 ticked on a promise that lives only in a DEVLOG paragraph.** `.gitignore` carries
  `callboard/.index/` and `*.db*`, and no index path constant exists yet, so verification-only was the
  honest call. But the re-verification against the real constant is now an obligation nobody holds —
  carry it into §3's brief explicitly. Separately, `*.db` is repo-wide: a committed fixture database in
  §3 would be silently ignored.
- **`InvariantGlobalization=true` (`Callboard.csproj:10`) is right for determinism and has a §2+
  consequence:** every culture-sensitive string operation becomes invariant. Card sorting, identity
  comparison and frontmatter key matching should be explicitly `Ordinal` rather than relying on that
  default, or the determinism is accidental rather than stated.
- **Version is duplicated** — `CurrentVersion = "0.1.0"` (`CommandDispatcher.cs:13`) with no `<Version>`
  in the csproj. Two sources of truth, one not yet existing. Trivial now; pick one before anything ships.

**What the section got right**, since a review that lists only holes misrepresents it: the scope
discipline is exact — no card verbs, no index code, no speculative abstraction, nothing in `src/` that §2
has to delete, and `Commands/` removed rather than left empty. The `ICommandResult` remediation closed a
real fail-open and the reviewer verified it in an isolated project instead of taking it on faith.
`record-retrieval`'s "never a precondition for comprehension" is untouched, because §1 writes no records
at all. ADR-0002 holds: source-generated JSON only, no reflection in shipping code, `IsAotCompatible` on,
and a real `publish` actually performed rather than asserted. The three blockers are all the same kind of
thing — holes reachable only from §2 — and they are all small.

**[architect]** Supervisor `Request changes` accepted — all three blockers verified against the source
myself before carving remediation, not taken on report: `src/` contains no `Console.IsInputRedirected`,
no `try`, and no `Console.Error` in any hand-written file. The findings are real and they are the same
shape: §1's deliverable is the mould for §2–§13, and each hole is invisible while there is one verb and
nothing that can throw.

Two conventions the supervisor correctly refused to invent on my behalf, now decided by the Product
Owner and binding from here:

- **Exit codes: `0` success, `1` refusal, `2` tool failure.** A refusal is the board working
  *correctly* and the caller must stop; a tool failure means enforcement is unavailable, and
  `record-retrieval` requires the loop to *proceed unenforced rather than blocked*. Those demand
  opposite agent behaviour, so they cannot share an exit code — and a tool that dies before emitting
  JSON leaves the exit code as the only signal there is. D1/ADR-0001 said only "non-zero on refusal";
  this refines it without contradicting it.
- **stdout carries exactly one JSON line per command and nothing else.** All human-facing diagnostics
  and crash detail go to stderr, so a machine caller can always pipe stdout straight to a parser.

**Remediation block → @worker.** No new task numbers and nothing to tick — every box in §1 is already
ticked and the section is committed at `6065453`. Per the workflow this lands as `fix(...)`, then the
supervisor re-runs over the same `c18d6f9..HEAD` range.

Deferred deliberately to their own sections rather than pulled into §1: the refusal payload's final
shape (§9), the closed refusal union (§9), 1.5's re-verification against the real index path constant
(→ §3's brief), and `Ordinal` string comparison (→ §2's brief). They are recorded in `## NEXT` so they
are held by something other than memory.

**[worker]** Remediation for all three supervisor blockers, confined to `Program.cs`,
`CommandDispatcher.cs` and the two test files as briefed.

- **Blocker 1 — argument-boundary convention.** `Run` now splits `args` into `command` and
  `remainingArgs`; `Dispatch` passes `remainingArgs` to each command handler instead of
  discarding it. `RunVersion` refuses (`unrecognised-argument`) unless `remainingArgs` is empty —
  `version` accepts nothing, so any stray token refuses, the same convention as an unrecognised
  command. `callboard version --oops` now exits `1` with `{"ok":false,...,"refusal":{"code":
  "unrecognised-argument",...}}` instead of `ok:true`/exit `0`. Extensible: a §2+ command that
  accepts real flags parses `remainingArgs` itself and refuses whatever it doesn't consume.
- **Blocker 2 — stdin redirect guard.** `Run` gained an `isInputRedirected` parameter, fed from
  `Console.IsInputRedirected` in `Program.cs` (the composition root, per the supervisor's note —
  `StdinBodyReader.ReadBody`'s `TextReader` signature is untouched). Added
  `CommandDispatcher.RequireStdinRedirected(bool)`: returns a `stdin-not-redirected` refusal when
  `false`, `null` when `true`. No §1 command reads a body, so nothing calls it in production yet
  — it's the guard §2's first body-reading command applies before calling `ReadBody`, so that
  worker inherits a tested guard instead of inventing one. Documented in both the doc comment and
  here so it isn't mistaken for dead code between now and §2.
- **Blocker 3 — failure boundary.** `Run` wraps dispatch in `try`/`catch (Exception)`. On an
  escaping exception it builds a `CliEnvelope` directly (`ok:false`, `refusal.code:
  "tool-failure"`) — not via `CommandOutcome.Refusal`, per the brief: a tool failure isn't the
  board saying no, it's enforcement being unavailable, and `CommandOutcome.Refusal` models the
  former. `Run` gained an `error` `TextWriter` parameter; the exception's full detail
  (`ex.ToString()`) goes there, never to stdout. Returns the new `ToolFailureExitCode = 2`,
  documented next to `RefusalExitCode` with the Product Owner's reasoning (opposite caller
  behaviour: refusal means stop, tool failure means `record-retrieval` proceeds unenforced).
  `Program.cs` now passes `Console.Error` through. Reused `CliEnvelope`/`CliRefusal` as they
  already exist (both files stayed out of scope) rather than adding a new wire shape — the
  `tool-failure` code is what a caller inspects to tell this apart from a real refusal; the exit
  code (`2` vs `1`) is the primary, always-available signal.

**Tests** — four new, all behavioural, none asserting a name's absence or pinning shape:
`Version_WithUnrecognisedArgument_RefusesWithNonZeroExitCode`,
`RequireStdinRedirected_WhenStdinIsNotRedirected_RefusesInsteadOfBlocking` +
`_WhenStdinIsRedirected_LetsTheCommandProceed`, and
`UnexpectedException_StillEmitsExactlyOneJsonEnvelope_AndExitsWithToolFailureCode` (a `TextWriter`
that throws on its first `WriteLine` and succeeds after, proving `Run` still emits exactly one
envelope, exits `2` not `0`/`1`, and puts diagnostic detail on the separate `error` writer, not
stdout). Manually verified against the published binary too:
`callboard version --oops` → exit `1`/`unrecognised-argument`; `callboard version < /dev/null` →
exit `0`; `callboard bogus < /dev/null` → exit `1`/`unknown-command`.

No card verbs, no index, no refusal-payload redesign, no CLI surface expansion. `tasks.md`
untouched — every §1 box was already ticked; nothing new to tick here.

`BUILD_EXIT:0 TEST_EXIT:0 FORMAT_EXIT:0 VALIDATE_EXIT:0 GATES_EXIT:0` — 14 tests (was 10).

Sandbox note: went straight to the override as instructed; `dotnet build`/`test`/`format`/
`validate` all ran sub-second, no hang observed.

→ @reviewer

**[reviewer]** Audit of the §1 remediation block (three supervisor blockers). Scope confined to
`Program.cs`, `CommandDispatcher.cs`, and the two test files, per the brief. Ran the gates myself
and exercised the published binary directly rather than taking the worker's report on its word.

**Gates, verified independently.** `make gates`: `BUILD_EXIT:0 TEST_EXIT:0 FORMAT_EXIT:0
VALIDATE_EXIT:0 GATES_EXIT:0`, 14/14 tests — matches the report.

**Manual checks against the published binary** (all confirm the DEVLOG claims exactly):
- `callboard version --oops < /dev/null` → `{"ok":false,...,"refusal":{"code":
  "unrecognised-argument",...}}`, exit `1`.
- `callboard version < /dev/null` → `{"ok":true,...}`, exit `0`, **stderr empty** (checked byte
  count, not just visual inspection).
- `callboard bogus < /dev/null` → `unknown-command` refusal, exit `1`.

**Blocker 1 — argument boundary. Sound and extensible, not a `version`-only patch.**
`Dispatch(string command, string[] remainingArgs)` (`CommandDispatcher.cs`) forwards
`remainingArgs` to every command arm uniformly; `RunVersion` is simply the first (and only)
consumer today, refusing when anything is left over. A §2+ command that wants real flags
consumes what it recognises from `remainingArgs` and refuses the rest through the same path —
nothing about the convention is special-cased to `version`. `Version_WithUnrecognisedArgument_
RefusesWithNonZeroExitCode` asserts the JSON shape and exit code, not just "didn't throw."

**Blocker 2 — stdin guard. The guard itself is correct and tested; the wiring to reach a future
verb is incomplete, and the DEVLOG overstates how ready it is.** `RequireStdinRedirected(bool)`
is correct, isolated, and both branches are tested
(`RequireStdinRedirected_WhenStdinIsNotRedirected_RefusesInsteadOfBlocking` /
`_WhenStdinIsRedirected_LetsTheCommandProceed`). `StdinBodyReader.ReadBody`'s `TextReader`
signature is confirmed untouched (`git diff` shows no change to that file). But: `Run`'s new
`isInputRedirected` parameter (`CommandDispatcher.cs:46`) is captured from
`Console.IsInputRedirected` in `Program.cs` and then **goes nowhere** — grepped the file, it
appears only in the `Run` signature and in the doc comment; it is never passed into `Dispatch`
or any command handler. `RequireStdinRedirected` is only ever invoked directly by the two new
tests with a literal `true`/`false`, never from `Run`'s actual `isInputRedirected` value. So
today the value threads from the composition root into `Run` and stops — a §2 worker adding the
first body-reading command still has to extend `Dispatch`'s signature (and thread the bool
through to whichever handler needs it) before they can call `RequireStdinRedirected` at all; the
alternative, reaching for `Console.IsInputRedirected` directly inside a command handler instead
of threading the parameter, would silently reintroduce the same untestability problem this fix
exists to avoid. That's a real, if small, gap against the specific ask ("positioned so §2's first
real body verb actually gets it rather than having to remember to call it") — the guard function
is inherited, but the plumbing to reach it from a command handler is not yet in place, contrary
to how the worker's report reads ("it's the guard §2's first body-reading command applies before
calling `ReadBody`"). Not a blocker on its own — no command in §1 reads a body, so nothing hangs
today, and the fix is one line (thread `isInputRedirected` through `Dispatch`) — but it should be
an explicit, named obligation in §2's brief rather than left implicit, or it will be rediscovered
the hard way.

**Blocker 3 — failure boundary. Correctly separated from `CommandOutcome.Refusal`, and the
mid-write case is genuinely exercised.** `WriteToolFailureEnvelope` (`CommandDispatcher.cs`)
constructs a `CliEnvelope` directly — confirmed no path through `CommandOutcome.Refusal` for a
tool failure, keeping the two concepts (board says no vs. enforcement broke) from re-fusing at
the one place that would matter. `WriteEnvelope` sits inside the `try`, so an exception during
serialization — not just during dispatch — is caught, and the test proves it: `ThrowsOnFirstWriteLine`
fails the *first* `output.WriteLine` call (i.e., mid-envelope-write) and the test asserts exactly
one JSON line still lands, with a dedicated exit code distinct from both `0` and `1`, and stderr
carries the detail. `exception.Message` only reaches the envelope; `ex.ToString()` (full trace)
goes to `error`, never `output` — confirmed by grep, `output` is written to only from
`WriteEnvelope` and `WriteToolFailureEnvelope`, both exactly one line each. One edge case worth
naming but not fixing now: the failure boundary is single-layer — if `WriteToolFailureEnvelope`'s
own `output.WriteLine` throws (a truly broken stdout, not just "first call fails" as the test
exercises), that exception is not caught by anything and escapes `Run` unhandled. Given the test
already covers "first write throws, second succeeds," and a stdout that fails on every write is
an environment failure outside the tool's control either way, this doesn't rise to a blocker —
noting it so it isn't rediscovered as a surprise later.

**Exit-code convention.** `0`/`1`/`2` are distinct and never overlap on any path I could find or
construct — confirmed via the dedicated test and via manual runs above. `ToolFailureExitCode`
is documented next to `RefusalExitCode` with the Product Owner's rationale, matching the
convention as stated in the architect's post.

**stdout/stderr separation.** No diagnostic write reaches `output` anywhere in the diff;
`error.WriteLine(ex.ToString())` is the only use of the new `error` parameter. Confirmed by
direct execution above (stderr byte count `0` on a clean success run) as well as by reading the
source.

**Testability.** The new `error` `TextWriter` and `isInputRedirected` `bool` are both explicit
parameters on `Run`, so the whole path — including the failure boundary — is still exercised
without a process spawn. The existing four tests were adapted via a small `Run(args, output)`
test helper defaulting `isInputRedirected: true`, keeping them unchanged in intent.

**Test quality — held the line from last round.** All four new tests are behavioural: JSON shape
+ exit code for the argument-boundary refusal, direct assertions on `RequireStdinRedirected`'s
two branches, and a genuine mid-write-failure simulation for the boundary test (not a name-absence
or reflection-based structural pin — no `System.Reflection` use anywhere in the new tests). None
of them would survive being deleted without losing real coverage, and none are pinned to an
implementation detail that a legitimate refactor would need to route around.

**Blockers:** none. **Nit/obligation:** the `isInputRedirected` plumbing stops at `Run` and does
not yet reach `Dispatch` or a command handler — recommend the architect record this explicitly as
a named obligation in §2's brief (alongside the other carried obligations already in `## NEXT`),
so the first body-reading verb threads it through deliberately rather than reinventing the
`Console.IsInputRedirected` read locally.

**Verdict: Approve.**

---

**[worker]** `CommandContext` follow-up landed, addressing the reviewer's dead-parameter finding
(`input`/`isInputRedirected` reached `Run` but never `Dispatch`, so `RequireStdinRedirected` was
unreachable from production code).

Added `CommandDispatcher.CommandContext` — an `internal sealed record` nested in
`CommandDispatcher`, five members exactly:

```
internal sealed record CommandContext(
    string[] RemainingArgs,
    TextWriter Output,
    TextReader Input,
    TextWriter Error,
    bool IsInputRedirected);
```

`Run` builds one instance and passes it to `Dispatch`; `Dispatch` passes it straight through to
each command handler instead of a bespoke parameter list. `RunVersion(CommandContext context)` is
now an ordinary consumer — no special-casing — and still refuses on any token in
`context.RemainingArgs` beyond zero, same as before.

The reachability requirement is satisfied structurally: a §2+ body-reading handler calls
`CommandDispatcher.RequireStdinRedirected(context.IsInputRedirected)` from inside its own arm of
`Dispatch`'s `switch`, using the context it already receives — adding that handler requires zero
change to `Dispatch`'s signature, only a new arm. Proved by
`StdinGuard_IsReachableThroughCommandContext_WithoutChangingDispatchSignature`: constructs a
`CommandContext` with `IsInputRedirected: false` and calls the guard with
`context.IsInputRedirected`, asserting the refusal — behavioural, not shape-pinning (no
reflection, calls the real production method with a real field read off the real type).

No speculative members added — the five are exactly what `Run` already had loose plus what
`Dispatch`/handlers need. `StdinBodyReader.ReadBody`'s `TextReader` signature untouched. Wire
format, exit codes (`0`/`1`/`2`) and the stdout/stderr split unchanged. `Run`'s public signature
(`args, output, input, error, isInputRedirected`) is unchanged, so no test needed to adjust its
call shape — only the new test and `Version_WithUnrecognisedArgument_RefusesWithNonZeroExitCode`'s
neighbours picked up the internal `CommandContext` type where they touch it directly.

`BUILD_EXIT:0 TEST_EXIT:0 FORMAT_EXIT:0 VALIDATE_EXIT:0 GATES_EXIT:0` — 15 tests (was 14).

No `→ @reviewer` — back to @architect to land.

## NEXT

**Section 1** — remediation block with @worker, then reviewer, gates, `fix` commit, then the §1
supervisor re-run over `c18d6f9..HEAD`. §2 does not open until §1 has an `Approve`.

Carried obligations, each with the section that owes it:

- **§2 brief** — string comparison and frontmatter key matching must be explicitly `Ordinal`.
  `InvariantGlobalization=true` makes the current behaviour correct by accident, not by statement.
- **§2 brief** — first real emission of `CliRefusal`. Its two-field shape is provisional until §9;
  flag every §2 refusal site so the §9 retrofit is a known list rather than a search.
- **§3 brief** — re-verify `.gitignore` against the actual index path constant once the index exists.
  1.5 could only verify the reserved layout; nobody currently holds the real check.
- **§9** — the refusal set becomes a closed union. `Refusal(string, string)` currently accepts a free
  string, so every section that adds a refusal before §9 widens the retrofit.
- **Before anything ships** — one source of truth for the version string (`CommandDispatcher.cs:13`
  versus an absent `<Version>` in the csproj).
- **Gate hygiene** — 1.4 exercised only `format`'s whitespace facet and never demonstrated `-k`
  aggregation on a red `make gates`. Worth proving once when a section next has a genuine failure.
- **Environment** — sandboxed `dotnet` hangs silently; reproduced by both agents and by me
  (`make gates` hung the full 600s in-sandbox, sub-second with the override). Tracked outside the
  change; if it is not fixed it costs every block a wasted hang and trains agents to reach for the
  override reflexively.
