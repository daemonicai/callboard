# Agent command reference

This document is for an agent — `worker`, `reviewer` or `supervisor` — mid-block, that needs to
record something and would otherwise append a post to `DEVLOG.md`. `callboard` is the tool that
replaces that file. For each thing a role currently *writes as a post*, this names the verb that
records it instead, with a command you can run, run against the binary as it stands.

It does **not** cover the `architect` role's own posts (base commits, briefs, `## NEXT`) except
where a gap under one of the three roles above turns out to be architect-only — those are named as
gaps, not mapped.

## How the command surface was derived, and why that matters

No command list was handed down for this document — it is re-derived from the source, the same
discipline §13 of this change's own DEVLOG established after two prior counts (a "seven families"
and a "seven doors") both turned out wrong by re-derivation. What follows was read from
`src/Callboard/Cli/CommandParser.cs` (the argv grammar) and `src/Callboard/Cli/CommandDispatcher.cs`
(what each parsed command does), not inferred from doc comments or test fixtures.

`CommandParser.Parse`'s own top-level `switch` dispatches on the first argument to **17 families**:
`version`, `index`, `block`, `section`, `finding`, `rule`, `hazard`, `obligation`, `decision`,
`question`, `change`, `nit`, `comment`, `context`, `state`, `card`, `view`. Every family but
`version`, `context`, `state`, and `view` has its own subcommand `switch` inside a `Parse<Family>`
method, naming its subcommands in the `missing-subcommand`/`unknown-subcommand` refusal text it
returns — that text is itself the enumeration, read directly rather than paraphrased:

| Family | Subcommands |
|---|---|
| `index` | `rebuild` |
| `block` | `create`, `base`, `transition`, `gate`, `add-blocker`, `remove-blocker`, `approve` |
| `section` | `create`, `verdict`, `authorise`, `close`, `status`, `export` |
| `finding` | `record`, `status` |
| `rule` | `create`, `discharge`, `promote`, `author`, `compact`, `propose-compact`, `promote-constitution`, `review` |
| `hazard` | `create`, `discharge` |
| `obligation` | `create`, `discharge`, `promote`, `decline` |
| `decision` | `create`, `discharge`, `supersede` |
| `question` | `create`, `answer`, `defer` |
| `change` | `archive`, `export` |
| `nit` | `raise`, `disposition` |
| `comment` | `add`, `resolve`, `promote`, `decline` |
| `card` | `show` |

Summed with the four leaf families (`version`, `context --role`, `state`, `view --out`), that is
**49 leaf commands**. This document maps a fraction of them — the ones a `worker`, `reviewer` or
`supervisor` reaches for in place of a DEVLOG post — and says explicitly, per role, which of the
rest were judged out of scope and why, rather than omitting them silently.

## The one distinction to hold onto: creation vs. addressing

**No creation verb takes a positional card file path any more.** `14.5` (`cfff4b6`) moved every
verb that mints a new card — `rule create`, `hazard create`, `obligation create`, `decision create`,
`block create`, `section create`, `question create`, `rule author`, `rule propose-compact` — off a
caller-supplied path entirely. The caller names the **container** (`--change <name>` where the
card's scope needs one); the system names the **file**, from the identity it mints, and reports it
back in the result's `filePath`. A caller cannot express a wrong filename for a newly created card
through the CLI any more — do not write `block create <path>` into anything; that argument does not
exist.

**The property is about every door that mints a card, not only the nine `14.5` touched at the CLI's
own primary-verb layer — a lesson this section itself needed twice.** A card is also minted as the
*second* write of a two-card verb, or a *manifest field* rather than an argv flag, and both shapes
turned out to have caller-named doors of their own, found and closed across two supervisor
remediation rounds:

- **`finding record`** mints the finding itself, and (with `--blind-spot obligation|hazard`) the
  raised card alongside it. Both used to take a caller-supplied path — the finding via a leading
  positional, the raised card via `--blind-spot-file` — and neither does now; read where each landed
  from the response's `filePath`/`raisedCardFilePath`.
- **`nit disposition --disposition defer|decline --raise <path>`** and **`comment promote --to
  question|decision --raise <path>`** each raised a second card (an obligation/decision; a
  question/decision) at a caller-named `--raise <path>`. `--raise` is gone from both; read where the
  raised card landed from the response's own `raisedCardFilePath`.
- **`section verdict --finding-new <manifest-file>`** mints a brand-new `block` card per first-time
  finding. The manifest format itself used to carry a `new-card-file:` header naming that card's
  path — the one door in this whole list where a caller named a file from *inside a file*, not an
  argv flag. That header is gone; a manifest still spelling it refuses loudly
  (`finding-new-manifest-malformed`, naming the header and why), the same "an unknown or
  now-forbidden field refuses rather than silently drops" discipline `14.1`'s unterminated-block
  check established for the card format proper. `section verdict`'s own response reports only the
  new card's `id` (under `newCardIds`), not its path — read the id back and resolve it if the path is
  needed, the same as any other identity-addressed reference.

Do not write `finding record <path>`, `--blind-spot-file`, `nit disposition --raise`, `comment
promote --raise`, or a manifest `new-card-file:` header into anything; none of them exist any more.

**Addressing an existing card is a different door and was not touched.** Verbs that act on a card
that already exists still take a path as their first positional argument — `block transition`,
`block base`, `block gate`, `section verdict`, `section authorise`, `section close`, `section
status`, `section export`, `finding status`. Where a verb needs no file-adjacent context of its
own — `block approve`, `nit raise`, `nit disposition`, `comment add`, `comment resolve`, `comment
promote`, `comment decline` — it addresses by `--id <card-id>` instead, resolved through the same
identity resolver at execute time. `card show <card-id>` addresses by a bare positional id (not a
path, and not a `--id` flag). **Do not flatten this into "everything takes `--id`"** — a
path-addressed verb and an `--id`-addressed verb are not interchangeable, and the asymmetry between
the two is real: which one a given verb uses is listed against each command below.

## Verified against a real board

Every command below was run, in order, against a throwaway `git init`-only repository, with the
release binary built from this change's own tree (`b9039ca`). Nothing here is adapted from a test
fixture or inferred from a doc comment. The full transcript this reference was written from is
reproducible with:

```sh
make build
sh docs/../openspec/changes/establish-callboard/verify-14-6.sh   # the file-legibility recipe, for context
```

— but the specific run this document quotes is its own script, not committed (throwaway, matching
`verify-14-6.sh`'s own "nothing here touches the repository itself" discipline): create a section
and a block, walk the block through `brief`/`claim`/`submit-for-review`/`changes-requested`/`nit
raise`/`fix-before-land`/`approve`, record a supervisor verdict, then read the card back. The one
refusal shown outside that transcript — a worker attempting `claim` on a block still in `drafting`,
under `block transition` below — came from a second, throwaway run built the same way, against a
freshly created block with no `brief` applied yet; everything else quotes the one continuous run.
JSON below is trimmed to what's load-bearing for the point being made — every value shown is real
output, none edited for content.

---

## `worker`

The worker's DEVLOG posts are: what was implemented (with any notable decision), a question when
blocked, and the handoff to `reviewer`.

### The handoff and the work itself: `block transition`

Path-addressed. Not every refusal on this surface is about *who* may act — some are about *when*.
A worker that claims a block the moment `block create` returns, before the architect has briefed it,
meets the other kind — the flow itself, not the caller's role, is what refuses:

```sh
$ cb block transition callboard/changes/establish-callboard/B-0001.md claim --role worker --change establish-callboard
{"ok":false,"refusal":{
  "code":"undefined-transition",
  "message":"no transition 'claim' from 'drafting'. Available: brief.",
  "rule":"work-lifecycle: block cards move through a defined flow",
  "remedy":"call one of the transitions available from 'drafting': brief."
}}
```

The role was never checked — `--role worker` was perfectly legitimate here — and the remedy is not
"ask someone else," it is **what edge exists from where the card actually stands right now**
(`Available: brief`), which is what lets a caller recover without guessing rather than retrying blind.
Once the architect has briefed it (`block transition ... brief --role architect`, not a worker's own
door — see "Judged out of scope" below), the two edges a worker drives itself are legal:

```sh
$ cb block transition callboard/changes/establish-callboard/B-0001.md claim --role worker --change establish-callboard
{"ok":true, ... "result":{"transition":"claim","from":"briefed","to":"building", ...}}

$ cb block transition callboard/changes/establish-callboard/B-0001.md submit-for-review --role worker --change establish-callboard
{"ok":true, ... "result":{"transition":"submit-for-review","from":"building","to":"in-review", ...}}
```

`submit-for-review` **is** the `→ @reviewer` handoff — it is the only edge a worker can drive from
`building`, and it is what makes the block visible to a reviewer's queue. There is no separate
"post a handoff" verb; the transition itself is the record.

**What was implemented, and any notable decision, has no dedicated verb.** `block transition` carries
no body — only `--role`/`--base`/`--change`. The nearest fit for narrative is `comment add` (below),
which is addressed prose against the card, not a transition. Nothing forces a worker to leave one;
if the brief asked for a decision to be recorded and none of the verbs below fit it, that is a real
gap, not a mapping this document should paper over — see "What has no verb," at the end.

### A question, addressed to whoever can answer: `comment add`

`--id <card-id>` addressed, not a path. `--to <role>` is optional but is what turns a comment into
one addressed the way the DEVLOG's `❓ @architect` convention addresses one; `--reply-to <comment-id>`
threads a reply. Body is read from stdin, never as a quoted argument, and cannot be empty.

```sh
$ printf '%s\n' "Spec says X but design says Y — which?" | \
  cb comment add --id B-0001 --role worker --to architect --change establish-callboard
{"ok":true, ... "result":{"commentId":"comment-a4fad...","to":"architect", ...}}
```

### Closing a thread you raised: `comment resolve`, and the refusal that matters here

`comment resolve` requires a body too (a correction is an appended comment, never an edit — see
`ADR-0003`) and is **role-bounded to the thread's addressee or the card's owner** (Product Owner
ruling, §10). A worker who raised a question **cannot resolve its own thread** — only the party it
was addressed to, or the card's owner, can:

```sh
$ printf '%s\n' "Thanks, done." | \
  cb comment resolve --id B-0001 --comment-id comment-a4fad... --role worker --change establish-callboard
{"ok":false,"refusal":{
  "code":"role-not-permitted",
  "message":"'…B-0001.md' thread disposition denied for role 'worker'.",
  "rule":"process-enforcement: a thread is disposed of only by its addressee or the card's owner",
  "remedy":"only 'architect' (the thread's addressee) or 'architect' (the card's owner) may dispose of this thread; 'worker' attempted it."
}}
```

Exit code non-zero (verified: the process exits `1`). The remedy names *who* may act, not a
different verb — `comment resolve` is still the right door, just not for this role on this thread.
The party the comment was addressed to closes it the same way, and that call succeeds.

### Reading your own queue: `card show`, `context`, `state`

All three are read-only and take no `--role`-scoped write. `card show <card-id>` (a bare id, not a
path, not `--id`) returns the whole card, including its transition history, comments and any
recorded refusals in one JSON document — useful to confirm exactly what landed after a write.
`context --role <role>` returns the role-scoped, budget-bounded working context (its own queue
ordering and the register); `state` returns the whole-board summary with no `--role` at all.

```sh
$ cb card show B-0001
{"ok":true, ... "result":{"id":"B-0001", "status":"approved", "transitions":[...], "comments":[...], "refusals":[...]}}

$ cb context --role reviewer
{"ok":true, ... "result":{"role":"reviewer","queue":[],"budget":{"tokenBudget":3000, ..., "truncated":false}, ...}}

$ cb state
{"ok":true, ... "result":{"openSections":[],"taskCompletion":[...],"liveObligations":[],"openQuestions":[],"blockedCards":[]}}
```

### Judged out of scope for `worker`, and why

- **`block create`** — mints the block card; that is the architect's brief, not the worker's act.
- **`block base`, `block gate`, `block add-blocker`, `block remove-blocker`** — recording the brief's
  base commit, a CI gate result, and blocker relationships are architect/tooling acts against a card
  a worker did not create and does not certify.
- **`comment promote`, `comment decline`** — a thread can be promoted to an obligation/hazard card or
  declined outright, the same addressee/owner role-bound door as `comment resolve`. Real, but this
  pass did not exercise either against a live board, and this document only documents what was run.
- **`question create`** — a standalone question card (`--owed-by <role>`), distinct from an in-thread
  `comment add --to`. The DEVLOG's questions are in-thread ("❓ @architect — spec says X…"), which
  `comment add` already covers; a standalone question card is a heavier-weight object this document
  does not claim worker needs.

---

## `reviewer`

The reviewer's DEVLOG posts are: the verdict on a block's diff, findings raised in-thread, and the
repeated audit loop until sign-off.

### Sending it back: `block transition ... changes-requested`

Path-addressed, same shape as the worker's transitions. No nits need to exist for this edge — it is
the plain "not yet" verdict:

```sh
$ cb block transition callboard/changes/establish-callboard/B-0001.md changes-requested --role reviewer --change establish-callboard
{"ok":true, ... "result":{"transition":"changes-requested","from":"in-review","to":"briefed", ..., "round":2}}
```

`round` incremented — recorded automatically, not something the reviewer states.

### A finding, in-thread: `nit raise`

`--id <block-card-id>` addressed. `--site <ref>` is repeatable (one flag occurrence per site, not
comma-joined — a comma-joined value would silently split one site's own text into two). `--required`
marks it required rather than advisory. Body is the nit's own text, read from stdin, addressed to the
architect implicitly (a nit's disposition is the architect's call — see below).

```sh
$ printf '%s\n' "The retry budget is hard-coded; should read from config." | \
  cb nit raise --id B-0001 --role reviewer --site "src/Retry.cs:42" --change establish-callboard
{"ok":true, ... "result":{"nitId":"nit-085741...","sites":["src/Retry.cs:42"],"required":false, ...}}
```

**An undispositioned nit blocks the certification below** — the refusal that proves it:

```sh
$ cb block approve --id B-0001 --role reviewer --state "retry budget reads from config" --claims "config-driven retry budget" --change establish-callboard
{"ok":false,"refusal":{
  "code":"undispositioned-nits",
  "message":"'…B-0001.md' cannot leave 'in-review' — the following nit(s) have no disposition: nit-085741....",
  "rule":"review-certification: undispositioned nits block the verdict",
  "remedy":"disposition the following nit(s) before this transition: nit-085741...."
}}
```

The remedy names the verb by concept ("disposition"), and the verb that satisfies it —
`nit disposition`, below — is architect's, not the reviewer's own. A reviewer that raises a nit is
handing it to the architect, not deciding its outcome itself.

### The certification: `block approve`

`--id`-addressed, role-bounded to `reviewer` or `supervisor` (`review-certification: "Approval is
role-bounded"`). Requires `--state <text>` naming exactly what was certified, and at least one of
`--claims <text>` (repeatable) or `--limits <text>` (repeatable) — claims-only and limits-only both
pass; neither present refuses.

```sh
$ cb block approve --id B-0001 --role reviewer \
    --state "retry budget reads from config, tests green" \
    --claims "config-driven retry budget" \
    --limits "no change to the backoff curve itself" \
    --change establish-callboard
{"ok":true, ... "result":{"reviewedState":"retry budget reads from config, tests green",
  "claims":[{"id":"634ae646...","text":"config-driven retry budget"}],
  "limits":["no change to the backoff curve itself"], "round":3}}
```

The role bound is real, not advisory — a worker attempting the same call is refused:

```sh
$ cb block approve --id B-0001 --role worker --state "..." --claims "..." --change establish-callboard
{"ok":false,"refusal":{
  "code":"role-not-permitted",
  "message":"recording an approval is restricted to the 'reviewer', 'supervisor' roles; 'worker' attempted it.",
  "rule":"review-certification: approval is role-bounded",
  "remedy":"only 'reviewer' or 'supervisor' may record an approval; 'worker' attempted it."
}}
```

There is no separate "post the verdict" step — `block approve`'s own write **is** the review sign-off
the DEVLOG loop currently records as a post.

### Reading, same as `worker`: `card show`, `context`, `state`

Identical verbs, same shapes — see the `worker` section above.

### Judged out of scope for `reviewer`, and why

- **`nit disposition`** — role-bounded to `architect` (checked in code, not merely by convention:
  `CardStore.DispositionNit` refuses any other role before it reads anything else). A reviewer raises
  a nit; it does not decide `fix-before-land`/`defer`/`decline` for its own finding.
  `fix-before-land` was exercised in this pass' transcript, by the architect, purely to demonstrate
  what unblocks `block approve` above — not as a reviewer verb.
- **`comment promote`, `comment decline`** — same reasoning as under `worker`: real, addressee/owner
  role-bound, not exercised in this pass.

---

## `supervisor`

The supervisor's one DEVLOG post per section is its verdict — `Approve` or `Request changes` —
against the range `git diff <base-sha>..HEAD`, where `<base-sha>` is the section's own base-commit
post.

### The verdict: `section verdict`

Path-addressed (the section card), not `--id`. `--verdict approve|request-changes` matches the exact
two-word vocabulary the DEVLOG convention already uses — this is not a coincidence; `SectionVerdict`'s
own doc comment says it was modelled to match `CLAUDE.md §3c` directly. `--range-from`/`--range-to`
are required and recorded as given (no git validation — the same "recorded, not verified against git"
shape `block transition --base` already has). `--finding-new <manifest-file>` (repeatable, one
self-contained manifest per new finding — never comma-joined flags, for the same reason `--claims`/
`--site` are repeatable rather than joined) raises new findings **in the same write** as the verdict;
`--finding-recurred <card-id>` (repeatable) reopens a remediation card the supervisor already owns.

```sh
$ cb section verdict callboard/changes/establish-callboard/S-0001.md \
    --verdict approve --range-from f100b77 --range-to HEAD \
    --role supervisor --change establish-callboard
{"ok":true, ... "result":{"verdict":"approve","rangeFrom":"f100b77","rangeTo":"HEAD",
  "recurredCardIds":[],"newCardIds":[]}}
```

A `request-changes` verdict is the identical call with `--verdict request-changes`, carrying whatever
`--finding-new`/`--finding-recurred` the findings require — not run again here since the wire format
and the write path are exactly the same call, differing only in that one flag's value (verified above
that `SectionVerdict`'s two cases are `approve` and `request-changes`, nothing else — the `TryParse`
dictionary in `SectionVerdict.cs` holds exactly those two keys).

**This is the one verb that fully replaces a supervisor DEVLOG post's own content** — verdict, range,
and (when the verdict carries findings) the findings themselves, in one write.

### Findings that recur across rounds: `nit`/`block approve` also apply

A supervisor is one of the two roles `block approve` accepts (`IsApprovingRole` returns `true` for
both `reviewer` and `supervisor`) — the same door documented under `reviewer` above works identically
here, for the case where a supervisor is certifying a remediation block directly rather than a whole
section. Not re-run in this pass; the mechanics are identical to the `reviewer` example.

### Reading, same as the other two roles: `card show`, `context`, `state`

Identical verbs, same shapes — see the `worker` section above.

### Judged out of scope for `supervisor`, and why

- **`finding record`** — mints a standalone finding card (a `CardStore.CreateCard`-style door since
  the 14.5-remediation — see "The one distinction to hold onto" above), with its own
  `--blind-spot`/`--extent-instrument`/`--extent-explicit` surface. A section verdict's own
  `--finding-new <manifest>` is the mechanism this document verified for attaching new findings to a
  verdict in one write; `finding record`'s standalone door (recording a finding *outside* a verdict,
  or amending one after the fact) was not exercised here and is its own surface, not a small addendum
  to this one.
- **`section create`, `section authorise`, `section close`, `section status`, `section export`** —
  `section create` is the architect's act (mirrors `block create`); `section authorise` is role-bound
  to `product-owner` alone (`CardStore.IsAuthorisingRole`, checked in code, not merely a convention);
  `section close`/`section status`/`section export` are the architect's own read/land verbs for
  running the outer loop, not a supervisor's. All were run once in this pass purely to produce the
  board state the transcript above needed, not as claimed supervisor verbs.

---

## What has no verb — stated plainly, not mapped around

Not every DEVLOG mechanism has a callboard equivalent yet. Three specific gaps, checked directly
against the command surface above rather than assumed:

1. **The section's base-commit post** (`**[architect]** Base: <sha> — ...`, `CLAUDE.md` §3a) has no
   verb. It is `git rev-parse --short HEAD`, stated in prose, and nothing in the 49-command surface
   above records a "this is where a section's range starts" fact against a section card. `section
   verdict --range-from`/`--range-to` **record** a range once a verdict is being written, but nothing
   captures the base at the moment a section opens, before any block exists to hang it on.
2. **The supervisor's review scope**, `git diff <base-sha>..HEAD`, is computed by the supervisor from
   git directly — no callboard verb reads or states a commit range as "what this review covers"
   independent of `section verdict`'s own `--range-from`/`--range-to`, which record the range only as
   part of recording the verdict, after the fact.
3. **The pinned `## NEXT` block** — the architect's own running summary, rewritten each time rather
   than appended — has no equivalent. Every card write this surface supports is append-only (`ADR-0003`
   / process-enforcement's "Comments are append-only"); nothing in the 49-command surface rewrites a
   single persistent summary in place, and nothing should, on the same grounds `DEVLOG.md`'s own
   append-only convention already rests on. `## NEXT`'s job — a live, mutable pointer to the resume
   point — has no card-shaped analogue here.

These are gaps in the *record*, not gaps in this document's coverage of it: an honest "no verb exists"
is worth more here than inventing one that doesn't hold up against the code.
