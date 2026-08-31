#!/bin/sh
# 14.6 verification recipe — 13.9's recipe, ported onto the surface after 14.5.
#
# 14.5 removed the positional card file path from every creation verb ("block create",
# "question create" included): the system now names the file, the caller only names the
# container. So step 1 and step 5 below no longer pass a path — the card's real filename
# is read back from the tool's own JSON result (`.result.filePath`) and used from then on.
# Nothing else about what this run exercises has changed: same refusals, same comment
# thread shape, same edge-whitespace title.
#
# One consequence, stated rather than hidden: 13.9's script named the target file
# "B-0099.md" and the tool wrote identity "B-0001" inside it — a deliberate mismatch,
# because that is what made the file worth reading. 14.5 makes that mismatch unreachable
# through the CLI: the card now lands at "B-0001.md", named for its own identity, not for
# whatever the caller typed. That is 14.5's own spec scenario ("The file is named for the
# card") working as designed, not a hole in this recipe.
#
# Run from the repository root after `make build`. Requires: dotnet, git, sed. Writes into
# a throwaway directory under /private/tmp; nothing here touches the repository itself.
set -e

BOARD_DLL="$(pwd)/src/Callboard/bin/Release/net10.0/callboard.dll"
if [ ! -f "$BOARD_DLL" ]; then
  echo "Build first: make build" >&2
  exit 1
fi
cb() { dotnet "$BOARD_DLL" "$@"; }

SCRATCH="/private/tmp/cb-verify-14-6-$$"
rm -rf "$SCRATCH"
mkdir -p "$SCRATCH"
cd "$SCRATCH"
git init -q

echo "== Step 1: create a block card (no path argument — the tool names the file) =="
CREATE_JSON="$(printf '%s\n' "Implements the read-half determinability check." | \
  cb block create --title "Wire a retry budget" --role architect --change establish-callboard --task 13.9)"
echo "$CREATE_JSON"
BLOCK_PATH="$(printf '%s' "$CREATE_JSON" | sed -n 's/.*"filePath":"\([^"]*\)".*/\1/p')"
BLOCK_ID="$(printf '%s' "$CREATE_JSON" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')"
echo "(card landed at: $BLOCK_PATH, identity: $BLOCK_ID)"

echo
echo "== Step 2: a refusal (undefined transition) =="
cb block transition "$BLOCK_PATH" build --role worker --change establish-callboard || true

echo
echo "== Step 3: brief it (records base), then a role tries an out-of-turn transition (second refusal) =="
cb block transition "$BLOCK_PATH" brief --role architect --base f100b77 --change establish-callboard
cb block transition "$BLOCK_PATH" claim --role worker --change establish-callboard

echo
echo "== Step 4: a comment thread — raised, replied, then resolved by a different role (a third refusal along the way) =="
FIRST_COMMENT_JSON="$(printf '%s\n' "Four readers still discard the parse failure with onFailure: static _ => null." | \
  cb comment add --id "$BLOCK_ID" --role reviewer --to architect --change establish-callboard)"
echo "$FIRST_COMMENT_JSON"
FIRST_COMMENT_ID="$(printf '%s' "$FIRST_COMMENT_JSON" | sed -n 's/.*"commentId":"\([^"]*\)".*/\1/p')"
printf '%s\n' "Fixed in the same commit; four readers now report a count and a path." | \
  cb comment add --id "$BLOCK_ID" --role architect --to reviewer --reply-to "$FIRST_COMMENT_ID" --change establish-callboard
printf '%s\n' "Confirmed against the diff; closing the thread." | \
  cb comment resolve --id "$BLOCK_ID" --comment-id "$FIRST_COMMENT_ID" --role reviewer --change establish-callboard || true
printf '%s\n' "Confirmed against the diff; closing the thread." | \
  cb comment resolve --id "$BLOCK_ID" --comment-id "$FIRST_COMMENT_ID" --role architect --change establish-callboard

echo
echo "== Step 5: a question card with a title that has a trailing space (no path argument here either) =="
QUESTION_JSON="$(printf '%s\n' "Fixed backoff, three attempts." | \
  cb question create --title "Which retry policy applies? " --role architect --owed-by product-owner)"
echo "$QUESTION_JSON"
QUESTION_PATH="$(printf '%s' "$QUESTION_JSON" | sed -n 's/.*"filePath":"\([^"]*\)".*/\1/p')"
QUESTION_ID="$(printf '%s' "$QUESTION_JSON" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')"
echo "(card landed at: $QUESTION_PATH, identity: $QUESTION_ID)"

echo
echo "=================================================================="
echo "RAW FILE — $BLOCK_PATH"
echo "=================================================================="
cat "$BLOCK_PATH"

echo
echo "=================================================================="
echo "RAW FILE — $QUESTION_PATH"
echo "=================================================================="
cat "$QUESTION_PATH"

echo
echo "=================================================================="
echo "TOOL'S OWN READING — for cross-check only, read this AFTER you have"
echo "written down your own answers from the raw files above"
echo "=================================================================="
echo "--- card show $BLOCK_ID ---"
cb card show "$BLOCK_ID"
echo
echo "--- state ---"
cb state
