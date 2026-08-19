# dmons-scaffold: 0.5.1
# callboard — gate targets.
#
# Every gate prints its own exit code as `LABEL_EXIT:<n>` and exits with it, so a
# report can quote the code rather than an agent's reading of the output. This is
# not cosmetic: a tool can exit non-zero while printing output that reads exactly
# like a clean run. `dotnet format --verify-no-changes` is exactly this: it exits 2
# while printing lines that scan as ordinary `warning WHITESPACE` / `warning IDExxxx`
# advisories.
#
# `gates` runs every gate in its set WITHOUT stopping at the first failure, so one
# invocation reports the whole picture instead of hiding the rest behind gate one.
#
# The active OpenSpec changes are discovered, not hardcoded, so the command string
# stays stable as changes come and go.

SHELL := /bin/bash

CHANGES := $(notdir $(patsubst %/,%,$(filter-out %/archive/,$(wildcard openspec/changes/*/))))

.PHONY: build test format validate changes gates publish clean

# --- dotnet --------------------------------------------------------------------

build:
	@dotnet build -c Release --nologo; code=$$?; echo "BUILD_EXIT:$$code"; exit $$code

test:
	@dotnet test -c Release --nologo; code=$$?; echo "TEST_EXIT:$$code"; exit $$code

format:
	@dotnet format --verify-no-changes; code=$$?; echo "FORMAT_EXIT:$$code"; exit $$code

# --- spec ----------------------------------------------------------------------

# Validates every active change (archive excluded). No active change is a failure,
# not a silent pass — an empty run would otherwise report VALIDATE_EXIT:0 while
# having validated nothing.
validate:
	@fail=0; \
	if [ -z "$(CHANGES)" ]; then \
		echo "no active change found under openspec/changes/"; fail=1; \
	else \
		for c in $(CHANGES); do openspec validate $$c --strict || fail=1; done; \
	fi; \
	echo "VALIDATE_EXIT:$$fail"; exit $$fail

changes:
	@echo "$(CHANGES)"

# --- gate sets -----------------------------------------------------------------

gates:
	@$(MAKE) --no-print-directory -k build test format validate; code=$$?; \
	echo "GATES_EXIT:$$code"; exit $$code

# --- release & housekeeping ----------------------------------------------------

# Not gates. `publish` is the Product Owner's to run — no agent invokes it as part of a block.
publish:
	@dotnet publish src/Callboard -c Release -r osx-arm64; code=$$?; echo "PUBLISH_EXIT:$$code"; exit $$code

clean:
	@dotnet clean; code=$$?; echo "CLEAN_EXIT:$$code"; exit $$code
