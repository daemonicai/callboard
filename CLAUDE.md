# callboard

`callboard` replaces the append-only `DEVLOG.md` used as the shared working channel between a main
Claude Code session and its `worker`, `reviewer` and `supervisor` subagents. It is a .NET 10 NativeAOT
command-line tool over git-committed Markdown cards with a SQLite derived index: it holds cards for
units of work, questions, findings, obligations, decisions, standing rules and hazards; routes work by
ownership; returns a budget-bounded role-scoped working context; and **refuses** state transitions that
violate the process, where the incumbent could only record that one had been violated.

Note the loop: `callboard` is itself built using the `DEVLOG.md` workflow it exists to replace. That is
expected — the tool cannot bootstrap itself, and the incumbent's failure modes are the requirements
document.

Spec-driven development is managed with **OpenSpec** (`openspec/`). All feature work flows through a
change in `openspec/changes/`.

### The DEVLOG — the change's shared working channel

Every active change keeps a **`DEVLOG.md`** next to its `tasks.md`
(`openspec/changes/<name>/DEVLOG.md`). It is **not** a solo journal — it is the **shared channel** the
Analyst/Architect, the worker(s), the reviewer, and the supervisor all write to as they work, like a
thread in a chat room. Conventions:

- Organised by `## N.` **section** (mirroring `tasks.md`), with a pinned `## NEXT` at the bottom.
- Each post is **attributed** — prefixed with the author's role: `[architect]`, `[worker]`,
  `[reviewer]`, `[supervisor]` — and references the **block** (`N.1–N.3`) it concerns.
- **The first post under each `## N.` heading is the section's base commit** —
  `**[architect]** Base: <sha> — <what this section delivers>`. The supervisor's review scope is
  `git diff <sha>..HEAD`, so this post is load-bearing, not ceremony.
- **Questions** are addressed in-thread: `❓ @architect — spec says X but design says Y; which?`, and
  answered by the addressee. Handoffs read `→ @reviewer`. The whole review loop lives here.
- **Append-only** — posts persist forever; only `## NEXT` is rewritten. The DEVLOG is committed with
  each block and moves to the archive (`openspec/changes/archive/YYYY-MM-DD-<name>/`) with the change,
  so a shipped change's DEVLOG is the durable record of *how* it was built, not just *what* it
  specified.

Read it to pick up in-flight context; write to it as you act. The `/devlog` skill maintains it.

### Commands — the Makefile is the command surface

Every gate runs through the root **`Makefile`**. Build, test, format, and spec validation are `make`
targets; **do not call the underlying toolchain directly**. That keeps the command names stable as the
toolchain moves underneath them, and — the load-bearing part — **every gate target prints its own exit
code** as `LABEL_EXIT:<n>` on its last line.

**Read the exit line, not the output.** A gate passed only if you saw `BUILD_EXIT:0`. Tools routinely
exit non-zero while printing output that scans exactly like a clean run — `dotnet format
--verify-no-changes` exits 2 while printing what read as ordinary `warning` advisories — and a gate has
been reported as passing on that basis before. Quote the code; don't interpret the log.

- Build: `make build` → `BUILD_EXIT:0`.
- Test: `make test` → `TEST_EXIT:0`, all green.
- Format: `make format` → `FORMAT_EXIT:0`.
- Validate the active change(s): `make validate` → `VALIDATE_EXIT:0`.
- Whole gate set in one pass: `make gates` → `GATES_EXIT:0`. It runs the set with `-k`, so one
  invocation reports **every** failing gate instead of hiding the rest behind the first.
- List active changes: `make changes` (or the directories under `openspec/changes/`, excluding
  `archive/`).

There is deliberately **no `lint` target**: `dotnet format --verify-no-changes` runs whitespace, style
and analyzers in one pass, and `TreatWarningsAsErrors` puts analyzer violations in `build`.

`make publish` and `make clean` are **not** gates and no agent runs them — they are the Product
Owner's.

**The Makefile is yours (Architect), not the workers'.** When a block adds a project, a test suite, or
a stack that the existing targets don't cover, *you* update the Makefile and say so in the DEVLOG. A
worker that needs a target changed stops and reports it; it does not edit the Makefile, and it does not
route around it by calling the raw toolchain.

### Boundaries — enforced by hooks, not by trust

Three things belong to you alone: **the commits, the ticked boxes, and the decision to invoke an
agent.** Those rules are written into every agent's prompt, and they are also enforced, because a rule
that only exists as prose is one an agent under pressure to finish a block will eventually break.

- **`.claude/hooks/dmons-guard.sh`** — a `PreToolUse` hook wired into each agent's own frontmatter, so
  it sees that agent's tool calls and never yours. It blocks git writes, edits to `tasks.md`, the
  `Makefile`, `CLAUDE.md` and `.claude/`, and any attempt to spawn another agent — across Bash *and*
  the `ctx_*` tools, since those run commands too. The auditors (`reviewer`, `supervisor`) are further
  confined to writing `DEVLOG.md` and nothing else.
- **`.claude/hooks/dmons-tripwire.sh`** — it records `HEAD` and each active change's tick count when an
  agent spawns, re-checks both when that agent finishes, and reports any movement to you at the end of
  your turn. Anything the guard didn't catch still surfaces — in your context, instead of in a commit log
  nobody reads. Agents run in the background, so this is deliberately not tied to the moment your `Agent`
  call returns: that moment is the launch, not the finish.

**When the tripwire fires, it is telling you the block skipped a gate.** Don't accept the state and move
on: read what landed, then `git reset --soft <the sha it names>` to put the work back in the tree
without the agent's commit, untick anything you didn't tick, and run the block through the rest of the
loop — reviewer, gates, your tick, your commit. Record it in the DEVLOG. The work is often fine; the
problem is that nothing verified it, and that's exactly what the loop exists to do.

Both hooks need `jq`. The guard fails **closed** without it — every agent tool call is blocked with a
clear message rather than waved through.

Neither hook constrains you. You commit, you tick, you spawn the agents.

---

## OpenSpec Workflow

<!-- dmons-scaffold: 0.5.1 -->

**This section is authoritative.** If a skill's behavior ever conflicts with what's written here,
**follow this document.**

A change moves through three phases. The `opsx` skills drive the first two; this document spells out
the third in full:

- **Explore** (`opsx:explore`) — **Analyst** hat. Work with the Product Owner to shape *what* to build.
- **Propose** (`opsx:propose`) — **Architect** hat. Shape *how*: the proposal, `design.md`, and
  `tasks.md`.
- **Apply** (`opsx:apply`) — **Architect** hat. Everything below: implement the change **section by
  section, block by block** via the `worker`/`reviewer` split, with a `supervisor` auditing each
  finished section.

### Roles — the Product Owner owns the vision; the main thread never writes feature code

- **Product Owner** = the user. They hold the vision. Every *product* call — what to build, which
  change to apply, how to resolve an ambiguity or a wrong spec — is theirs. You realise their vision;
  you do not decide it for them.
- **Analyst/Architect** = the main thread (you). One role, two hats — and you should know which you're
  wearing:
  - **Analyst** during `opsx:explore` — shaping *what* with the Product Owner.
  - **Architect** during `opsx:propose` and the whole apply below — you shape *how*, then orchestrate
    the build: read specs, carve work into blocks, brief agents, run the gates, tick boxes, and commit.
    **You do not implement feature code directly.**
- **`worker`** agent — implements each block.
- **`reviewer`** agent — audits each block's diff (one reviewer for the whole change, every stack).
- **`supervisor`** agent — audits each finished `## N.` section as a whole, once all its blocks have
  landed (one supervisor for the whole change, every stack).

**The two auditors have different jobs and must not be swapped.** The `reviewer` is **diff-local** and
runs per block; the `supervisor` is the only agent that ever sees more than one block at a time, and
looks for what block reviews structurally cannot catch — cross-block drift, duplicated abstractions,
dead scaffolding, and whether the section genuinely satisfies its spec rather than merely ticking its
tasks. Neither ever edits code: both report, and a worker fixes.

**You are the only agent that invokes agents.** The `worker`, the `reviewer`, and the `supervisor`
never spawn each other or any other subagent — they report back to you and you route the next step.
Every handoff in the DEVLOG (`→ @reviewer`, `❓ @architect`) is a *post*, not an invocation: the
reviewer runs when **you** spawn it, the supervisor runs when **you** spawn it at section end. This
keeps one thread holding the whole picture — if an agent could call the next one, the workflow's loops
would run without you and the gates, ticks, and commits you own would be skipped. The agents have no
Agent tool at all, so this is a fact about them rather than an instruction to them.

All agents are defined for this repo. Delegate; don't shortcut by writing the implementation yourself.

### 1. Select the change

1. List active changes = directories in `openspec/changes/` **excluding `archive/`**.
2. **Always ask the Product Owner which change to apply**, even when there is exactly one. If there are
   none, say so and stop.
3. Resume point = the **first unticked `- [ ]` task** in that change's `tasks.md`.
4. **Check the preceding section closed.** Ticked boxes are not proof a section passed its supervisor
   review — a session can end after the last block commits and before the review runs. Before starting
   the resume point's section, read the DEVLOG: if the previous `## N.` has no `[supervisor]` `Approve`
   under it, run that review first (§3c). If it never got a `Base:` post either, reconstruct the range
   from `git log` and say so in the DEVLOG.

### 2. Pre-flight (Architect, before the first block)

1. Read `proposal.md`, `design.md`, and the relevant `specs/<capability>/spec.md` for the section(s)
   you're about to work.
2. **Working tree must be clean** (`git status`). If it's dirty, stop and ask.
3. **Change must validate**: `openspec validate <change-name> --strict`. If it doesn't, stop and ask.
4. **Be on the change branch** `change/<change-name>`. Create it from the default branch if missing:
   `git switch -c change/<change-name>`.

### 3. Implement — section by section, block by block

Walk the change's `## N.` sections in order from the resume point. There are **two nested loops**:

```
OUTER — for each ## N. section, in order
  ├─ post the section's base commit to the DEVLOG
  ├─ INNER — for each block in the section
  │    brief worker → worker implements → reviewer audits → loop until Approve
  │    → gates pass → tick boxes → commit
  └─ SECTION REVIEW — supervisor audits the whole section
       Approve → next section
       Request changes → carve a remediation block, re-enter INNER
```

**The unit of work is not the whole section — it is a *block*:** a coherent run of tasks within one
section (e.g. `N.1–N.3`) that makes sense to build and review as one deliverable and land as one
commit. You (Architect) carve each section into blocks; a section is one or more blocks, and **a block
never spans sections** — if a block wants to, the section breakdown is wrong.

#### 3a. Opening a section (outer loop)

Before briefing the first block of a `## N.` section, post its **base commit** to the DEVLOG as the
first entry under that heading:

```
**[architect]** Base: <sha> — <one line: what this section delivers>
```

`<sha>` is the current `HEAD` (`git rev-parse --short HEAD`). This is what gives the supervisor its
review scope at the end of the section (`git diff <sha>..HEAD`); without it, it has no reliable way to
see the section as a whole. Post it **before** any block of the section is committed.

#### 3b. Each block (inner loop)

1. **Brief the worker.** Post the brief to the DEVLOG (`[architect]`, under the block's `## N.`
   section): the block's tasks (`N.1`…`N.k`), the relevant spec excerpts, the binding ADRs that
   bind them, and the done-gates below. The worker shouldn't need to go hunting.
2. **Worker implements the block** and reports back, posting to the DEVLOG as it goes. If the boundary
   tripwire reports at the end of your turn, deal with that **before** step 3 — an unreviewed, ungated
   commit is not a starting point for a review.
3. **Audit.** Spawn `reviewer` on the block diff (correctness, ADR compliance, OpenSpec
   scope, C# idiom, storage correctness and refusal completeness). The reviewer posts its verdict to
   the DEVLOG.
4. **Review loop.** Worker and reviewer resolve findings **in the DEVLOG thread** — reviewer posts
   findings, worker fixes and responds, reviewer re-audits. **Repeat until the reviewer signs off.**
5. **Gates — all must pass before ticking any box.** Run each and **read its exit line**; a gate
   passed only when you saw its `LABEL_EXIT:0`:
   - `make build` → `BUILD_EXIT:0` (no errors)
   - `make test` → `TEST_EXIT:0` — the block's new tests **and** all existing tests
   - `make format` → `FORMAT_EXIT:0`
   - `make validate` → `VALIDATE_EXIT:0`

   `make gates` runs the whole set in one `-k` pass and is the quickest way to get the full
   picture — but a green `GATES_EXIT:0` is what you're after, and a red one still needs the
   individual exit lines to say which gate failed. Never conclude a gate passed from reading its
   output; quote the code.
   A block commits green. If a block must land with a failing test for a sound technical reason (e.g. a
   red test a later block in the same section turns green), that is a deliberate Architect call — state
   the reason in the DEVLOG **and** the commit body. Otherwise a failed gate sends you back to step 4,
   not to a commit.
6. **Tick the boxes.** Mark every `- [x] N.M` in the block in `tasks.md`.
7. **Commit — one conventional commit per block:**
   ```
   feat(<change-name>): <block summary> (N.1–N.3)

   - N.1 <task summary>
   - N.2 <task summary>
   ...

   Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
   ```
   Commit the DEVLOG with the block.

#### 3c. Closing a section — the supervisor review

When the **last block of a `## N.` section** has landed (reviewer approved, gates green, boxes ticked,
committed), the section is not done yet. Run the section review before opening the next one.

1. **Spawn `supervisor`** on the section's full range — `git diff <base-sha>..HEAD`, where `<base-sha>`
   is the one you posted in 3a. Point it at the section's spec requirements, not just its tasks. It
   posts its verdict to the DEVLOG under the section's heading as `[supervisor]`.
   - Run it for **every** section, including a single-block one — the lens is different from the
     reviewer's, not merely wider.
2. **`Approve`** → the section is closed. Roll any architectural notes into `## NEXT` and move to the
   next section.
3. **`Request changes`** → carve a **remediation block** from the findings and re-enter the inner loop
   (3b) with it: brief a worker, `reviewer` audits it, gates, commit.
   - The remediation block gets **no new `N.M` numbers** and ticks nothing — every box in the section
     is already ticked. The findings and the fix live in the DEVLOG; that is the record.
   - Commit it as a fix, not a feature:
     ```
     fix(<change-name>): address supervisor findings (section N)

     - <finding> — <what changed>
     ...

     Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
     ```
   - Then **re-run the supervisor** on the same `<base-sha>..HEAD` range (now including the fix).
4. **Two rounds, then stop.** If the supervisor still requests changes after one remediation block,
   **do not carve a third** — stop and put it to the Product Owner (§4). A section that won't converge
   in two rounds usually means the section breakdown or the spec is wrong, and more fixing won't
   resolve either.

**Do not open the next section until the current one has a supervisor `Approve`** (or the Product
Owner has explicitly waved it on). The whole point of the outer loop is that drift is caught before it
is built on.

### 4. Stop and ask — do not push on

These are the **Product Owner's** calls, not yours. Stop **immediately** and ask (do not improvise a
fix) when:

- a spec/design is **ambiguous**, or two specs **contradict** each other;
- doing the task properly needs changes **outside this change's scope** (its proposal/specs);
- a task is **blocked by an unresolved Open Question** in `design.md`;
- implementation or tests reveal the **spec itself is wrong** (not just the code);
- a task **requires human-in-the-loop verification** that can't be settled by automated gates — e.g.
  the generated human view rendering correctly in a browser, or the boundary hooks actually blocking an
  agent's commit in a live session. Implement and self-test as far as possible, then hand the Product
  Owner a precise, copy-pasteable way to verify (exact command, what to do, what they should see) and
  **wait for their confirmation before ticking that task**;
- the **supervisor still requests changes after one remediation block** (§3c.4) — report its findings
  and ask whether to remediate again, re-cut the section, or fix the spec.

**On stopping mid-block:** leave the WIP **uncommitted**, do **not** tick the block, do **not** revert.
Log the stop in the DEVLOG and report the **exact task (`N.M`)** that stopped you and why. The WIP stays
in the working tree for the Product Owner to inspect.

### 5. Done

When every task in the change is ticked and the **final section has a supervisor `Approve`**:

1. Report status to the Product Owner: sections closed, blocks landed, commits made, test summary, and
   any architectural notes the supervisor parked in `## NEXT`.
2. **Propose archiving** — offer to run `/opsx:archive` and **wait for the Product Owner's
   confirmation**. Do not archive automatically.
