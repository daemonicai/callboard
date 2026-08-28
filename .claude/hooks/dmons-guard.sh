#!/usr/bin/env bash
# dmons-scaffold: 0.5.1
#
# dmons boundary guard — a PreToolUse hook for the OpenSpec Apply Workflow's agents.
#
#   usage: dmons-guard.sh <role>        role = worker | auditor
#
# Wired from the `hooks:` frontmatter of .claude/agents/worker*.md (role `worker`) and of
# reviewer.md / supervisor.md (role `auditor`). Frontmatter hooks fire only for that agent's
# own tool calls, so this never sees the Architect's — which is the whole point: the
# Architect must commit and tick boxes, and the agents it spawns must not.
#
# Exit 2 blocks the call; the agent reads stderr as the reason it was blocked. The agents'
# prompts already explain these boundaries. That prose is the explanation; this file is the
# enforcement, and the two must keep saying the same thing.
#
# Fails CLOSED: if it cannot parse the tool call, it blocks it.

set -uo pipefail

ROLE="${1:-worker}"

# The card store, as a path tail. Deliberately NOT `callboard/` on its own: this repository is
# itself named `callboard`, so every source file under it has `callboard/` in its absolute path
# and a naive match would deny the whole tree. The store is the five directories `CardLayout.cs`
# and `IndexPaths.cs` name, matched as a tail the way `AnchoredCardPath` matches one — which is
# also what makes this correct for a store rooted somewhere other than this repo.
#
# The directory name ends at a non-path character, NOT at a `/`: `rm -rf callboard/register`
# destroys the store just as thoroughly as `rm callboard/register/rule-0001.md`, and a pattern
# that insisted on the trailing slash waved the more destructive of the two through.
STORE_PATH_RE='(^|[^[:alnum:]_.-])(\.{1,2}/|[^[:space:]]*/)?callboard/(register|decisions|changes|identities|\.index)(/|[^[:alnum:]_.-]|$)'

# The store *root* — `rm -rf callboard`, which the pattern above cannot cover, because it names
# no subdirectory. The difficulty is that a bare `callboard` token is far more often the tool
# being invoked than the directory being destroyed.
#
# The first attempt matched the root only as the ARGUMENT of a destructive verb, which made the
# rule ORDER-DEPENDENT and so fail open on every form where the verb does not sit beside the
# word: `ls -d callboard | xargs rm -rf`, `find callboard -delete`, `tar -xzf b.tgz -C callboard`.
# So the root is matched as a plain token here, and the distinction between the tool and the
# store is drawn separately, by position: see the stripping step in the loop below.
STORE_ROOT_RE='(^|[^[:alnum:]_./-])[^[:alnum:][:space:]/._-]?(\.{1,2}/|[^[:space:]]*/)?callboard/?([^[:alnum:]_./-]|$)'

deny() {
  printf 'BLOCKED by the OpenSpec Apply Workflow (%s boundary).\n\n%s\n\nThis is not a permission prompt and not a transient failure — retrying it, or reaching the same result by another tool, is itself a breach. Post the reason to the change DEVLOG and hand back to the Architect; that hand-back is the expected outcome here, not a failure.\n' \
    "$ROLE" "$1" >&2
  exit 2
}

# One refusal, stated once. It is reached from two rules — the file-writing tools and the
# shell-running ones — and two copies of a message drift apart the moment either is edited.
deny_store() {
  deny "The card store is \`callboard\`'s to write. Editing a card file directly bypasses every refusal the tool exists to enforce, and leaves the derived index describing a record that no longer says that. Record it with the verb for what it is — \`callboard comment add\`, \`callboard block create\`, \`callboard block base\`, \`callboard block transition\` — and if no verb records what you have, that is a gap to report to the Architect, not to route around. Reading card files (\`cat\`, \`grep\`, \`ls\`, \`sed -n\`) is fine and is not blocked."
}

command -v jq >/dev/null 2>&1 || deny "jq is not on PATH, so this guard cannot inspect the tool call. It fails closed rather than waving calls through unchecked. Install jq."

INPUT=$(cat)
TOOL=$(printf '%s' "$INPUT" | jq -r '.tool_name // empty')

# ---------------------------------------------------------------------------
# 1. No agent invokes another agent.
# ---------------------------------------------------------------------------
case "$TOOL" in
  Agent | Task)
    deny "Only the Analyst/Architect (the main thread) invokes agents. A handoff such as \`-> @reviewer\` is a DEVLOG post and a line in your report, not an agent call. If this block needs someone else's help, that is a signal to stop and report, not to delegate."
    ;;
esac

# ---------------------------------------------------------------------------
# 2. Anything that can run a shell.
#    Bash is the obvious surface. context-mode's ctx_* tools run commands too, and the
#    workflow deliberately routes every `make` gate through them — so a guard matching only
#    Bash would leave the agents' busiest path unguarded.
# ---------------------------------------------------------------------------
case "$TOOL" in
  Bash | PowerShell | *ctx_execute | *ctx_execute_file | *ctx_batch_execute)
    # Every string anywhere in the tool input. This covers Bash's `.command`, ctx_execute's
    # `.code`, and ctx_batch_execute's `.commands[].command` without needing to know which
    # tool put the command where — and it keeps working when a tool's input shape changes.
    CMD=$(printf '%s' "$INPUT" | jq -r '[.tool_input | .. | strings] | join(" ; ")')

    # Rejoin backslash line continuations FIRST, before any rule reads $CMD. The shell treats
    # `rm -rf \<newline>callboard/register` as ONE command; every rule below that reasons about
    # a line or a segment would otherwise see two, with the verb in one and its target in the
    # other, and neither half matching. That is true of the store rules and equally true of the
    # git rule above them — `git \<newline>commit` is the same trick — so it is fixed here, at
    # the point the text is assembled, rather than at each rule that would have to remember.
    CMD=${CMD//\\$'\n'/ }

    # Collapse git's global options before matching the subcommand, so `git -C sub push` and
    # `git -c user.name=x commit` read as `git push` / `git commit`. Without this, any option
    # that takes a separate value walks straight past the subcommand list below.
    CMD=$(printf '%s' "$CMD" | sed -E 's/([^[:alnum:]_.-]|^)git([[:space:]]+(-[cC][[:space:]]+[^[:space:]]+|--(git-dir|work-tree|namespace|exec-path)[= ][^[:space:]]+|-[^[:space:]]+))*[[:space:]]+/\1git /g')

    if printf '%s' "$CMD" | grep -Eq '(^|[^[:alnum:]_.-])git[[:space:]]+(commit|add|push|tag|merge|rebase|reset|revert|cherry-pick|stash|am|apply|restore|switch|checkout|clean|rm|mv|worktree|update-ref)([^[:alnum:]_-]|$)'; then
      deny "The Architect owns the git history — it commits once per block, after the reviewer approves and every gate has printed EXIT:0. Leave the work uncommitted in the tree and report which \`N.M\` tasks you completed. Reading history (\`git diff\`, \`git log\`, \`git status\`, \`git show\`) is fine and is not blocked."
    fi

    if printf '%s' "$CMD" | grep -Eq '(^|[^[:alnum:]_.-])gh[[:space:]]+(pr|release|repo|api|workflow)([^[:alnum:]_-]|$)'; then
      deny "Pull requests, releases, and anything else that leaves this machine are the Product Owner's call, routed through the Architect."
    fi

    if printf '%s' "$CMD" | grep -Eq '\.claude/(hooks|agents|settings)'; then
      deny "\`.claude/\` holds the workflow's own definitions — the agent files, this guard, and the permission config. No block touches them; a change there is a change to the rules you are working under."
    fi

    # The card store is written by the `callboard` tool and by nothing else. Reads are not
    # touched — `cat`, `grep`, `ls` and `sed -n` over a card file are how the record stays
    # legible when the tool cannot run, and the tool's own writes go through the binary rather
    # than through any verb below.
    #
    # A code-execution tool carrying NON-shell code is judged before any of that. There is no
    # verb vocabulary to read there — `open(p,'w').write(x)` is a write, `open(p).read()` is a
    # read, and they differ by one character — so any mention of a store path in Python or
    # JavaScript fails shut. Nothing is lost: reading a card is `cat`.
    LANGUAGE=$(printf '%s' "$INPUT" | jq -r '.tool_input.language // empty')
    case "$LANGUAGE" in
      '' | shell | bash | sh | zsh | fish) ;;
      *)
        printf '%s' "$CMD" | grep -Eq "$STORE_PATH_RE|$STORE_ROOT_RE" && deny_store
        ;;
    esac

    # Split on the shell's SEQUENCING operators, so a legitimate read in one command is not
    # condemned by an unrelated `rm` in the next. Not on `|`: a pipeline is one command, and
    # splitting it hid `ls callboard/register/*.md | xargs rm` in a segment whose only verb
    # was `ls`.
    # shellcheck disable=SC2020  # character-for-character is exactly what is wanted below: each
    # sequencing operator becomes a newline, and the repeat in set2 is the point, not a word swap.
    while IFS= read -r SEG; do
      # Draw the line between the tool and the store by POSITION, not by proximity to a verb.
      # A `callboard` token in command position — at the head of a segment, of a pipeline stage,
      # or of a substitution — is the binary being invoked, which is the one thing entitled to
      # write the store. Blank those out; whatever `callboard` remains is a path.
      SEG=$(printf '%s' "$SEG" | sed -E 's#(^|[|(`])([[:space:]]*(sudo|env|time|nice)[[:space:]]+)*[[:space:]]*(\./)?callboard([[:space:]]|$)#\1 #g')

      printf '%s' "$SEG" | grep -Eq "$STORE_PATH_RE|$STORE_ROOT_RE" || continue
      if # a redirect onto the store — `>`, `>>`, or the clobbering `>|`
        printf '%s' "$SEG" | grep -Eq '>[>|]?[[:space:]]*[^[:space:]]*callboard' ||
        # a verb whose whole purpose is to write, move, unpack or destroy
        printf '%s' "$SEG" | grep -Eq '(^|[^[:alnum:]_.-])(rm|rmdir|unlink|mv|cp|tee|touch|truncate|mkdir|install|dd|ln|chmod|chown|shred|rsync|tar|unzip|gunzip|zip)([[:space:]]|$)' ||
        # a general-purpose interpreter, on any flags at all — see the non-shell case above.
        # `ed`, `ex` and `patch` belong here and not in the in-place rule below: `patch` edits in
        # place by DEFAULT and `ed` has no `-i` at all, so requiring the flag exempted the two
        # tools that need no flag. `sed`, `awk` and `jq` stay out deliberately: each needs `-i`
        # or a redirect to write, both already caught, and that is what keeps `sed -n` a read.
        printf '%s' "$SEG" | grep -Eq '(^|[^[:alnum:]_.-])(python3?|node|nodejs|ruby|perl|php|deno|bun|osascript|ed|ex|patch)([[:space:]]|$)' ||
        printf '%s' "$SEG" | grep -Eq '(^|[^[:alnum:]_.-])dotnet[[:space:]]+(fsi|script)([[:space:]]|$)' ||
        # in-place editing by the one tool that does need a flag to do it
        printf '%s' "$SEG" | grep -Eq '(^|[^[:alnum:]_.-])sed([[:space:]][^;]*)?[[:space:]](-[^[:space:]]*i([[:space:]]|$)|--in-place)' ||
        # `find` deleting the store itself, or handing it to something that will
        printf '%s' "$SEG" | grep -Eq '(^|[^[:alnum:]_.-])find([[:space:]]|$)[^;]*[[:space:]]-(delete|exec|execdir)([[:space:]]|$)'; then
        deny_store
      fi
    done <<<"$(printf '%s' "$CMD" | tr ';&\n' '\n\n\n')"
    ;;
esac

# ---------------------------------------------------------------------------
# 3. Anything that writes a file.
# ---------------------------------------------------------------------------
case "$TOOL" in
  Edit | Write | MultiEdit | NotebookEdit)
    FILE=$(printf '%s' "$INPUT" | jq -r '.tool_input.file_path // .tool_input.notebook_path // empty')
    BASE=${FILE##*/}

    case "$BASE" in
      tasks.md)
        deny "\`tasks.md\` is the Architect's ledger, and a ticked box is a claim that the gates passed. It flips \`[ ]\` to \`[x]\` itself, after it has run them. Report the \`N.M\` numbers you completed and let it tick."
        ;;
      Makefile | GNUmakefile | *.mk)
        deny "The Makefile is the Architect's. If this block needs a gate target that does not exist, or an existing target no longer covers what it names, stop and report that — a gate written by the agent it gates is not a gate."
        ;;
    esac

    case "$FILE" in
      CLAUDE.md | */CLAUDE.md | *.claude/* | */.claude/*)
        deny "\`CLAUDE.md\` and \`.claude/\` define the workflow you are running inside. Editing them from within a block is out of scope by construction."
        ;;
    esac

    printf '%s' "$FILE" | grep -Eq "$STORE_PATH_RE" && deny_store

    if [ "$ROLE" = auditor ] && [ "$BASE" != DEVLOG.md ]; then
      deny "You report; you do not edit. \`DEVLOG.md\` is the one file you write — findings go there, and a worker applies them under the Architect's direction. Fixing it yourself removes the review the workflow is built on."
    fi
    ;;
esac

exit 0
