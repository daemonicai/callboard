using Callboard.Cards;

namespace Callboard.Cli;

/// <summary>
/// The parse phase (O-3, DEVLOG §5): consumes argv tokens from a <see cref="CommandDispatcher.
/// CommandContext"/> and builds a <see cref="CommandDispatcher.ParseResult"/> — either a refusal,
/// or a <see cref="CommandDispatcher.ParsedCommand"/> describing what was asked for. Deliberately
/// a separate top-level class from <see cref="CommandDispatcher"/>, not a nested one: the handler
/// functions (<c>CommandDispatcher.RunVersion</c>, <c>CommandDispatcher.RunIndexRebuild</c>) are
/// <see langword="private"/> members of <see cref="CommandDispatcher"/>, and a nested type can see
/// its enclosing type's private members regardless of the nested type's own accessibility — so
/// nesting this here would not have stopped a parse arm from naming a handler. As a sibling
/// top-level class, it cannot: see the <see cref="CommandDispatcher"/> class doc comment for
/// exactly what that does and does not rule out. Every method here is <see langword="private"/> to
/// this class except <see cref="Parse"/>, which <see cref="CommandDispatcher.Run"/> calls — the
/// only caller, and the only place a resulting <see cref="CommandDispatcher.ParsedCommand"/> is
/// ever matched to a handler.
/// </summary>
internal static class CommandParser
{
    /// <summary>
    /// Routes on <paramref name="command"/>, consuming from <paramref name="context"/>'s cursor
    /// whatever each arm recognises, and either refuses outright or hands back a
    /// <see cref="CommandDispatcher.ParseResult.Ready"/> wrapping an inert
    /// <see cref="CommandDispatcher.ParsedCommand"/>. No arm here calls a handler — it cannot, see
    /// the class doc comment — which is exactly what makes deferring dispatch past
    /// <see cref="CommandDispatcher.EnforceNoUnconsumedArguments"/> meaningful.
    /// <see cref="CommandDispatcher.Run"/> is the only caller.
    /// </summary>
    internal static CommandDispatcher.ParseResult Parse(string command, CommandDispatcher.CommandContext context) => command switch
    {
        "version" => new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.Version()),
        "index" => ParseIndex(context),
        "block" => ParseBlock(context),
        "section" => ParseSection(context),
        "finding" => ParseFinding(context),
        "rule" => ParseRule(context),
        "hazard" => ParseHazard(context),
        "obligation" => ParseObligation(context),
        "decision" => ParseDecision(context),
        _ => new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
            "unknown-command",
            $"no such command: '{command}'. Known commands: version, index, block, section, finding, rule, hazard, obligation, decision.")),
    };

    /// <summary>
    /// <c>index</c>'s only job is routing to a subcommand — currently just <c>rebuild</c>. No
    /// subcommand, or one this dispatcher does not recognise, refuses and names what does exist.
    /// Peeks rather than taking: a token this method does not recognise is left in
    /// <see cref="CommandDispatcher.CommandContext.Arguments"/> unconsumed, both so
    /// <see cref="CommandDispatcher.EnforceNoUnconsumedArguments"/> still sees it (not
    /// load-bearing here — the refusal already stands on its own) and, the reason it matters, so
    /// <c>CommandDispatcher.RecognisedCommand</c> never reports a subcommand that was rejected as
    /// part of the recognised command name.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseIndex(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'index' requires a subcommand. Known subcommands: rebuild."));
            case "rebuild":
                context.Arguments.TryTake();
                return ParseIndexRebuild(context.WorkingDirectory);
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'index' subcommand: '{subcommand}'. Known subcommands: rebuild."));
        }
    }

    /// <summary>
    /// Builds <c>index rebuild</c>'s <see cref="CommandDispatcher.ParsedCommand.IndexRebuild"/> —
    /// data only. Takes only the <see langword="string"/> working directory
    /// <see cref="ParseIndex"/> already had — not the <see cref="CommandDispatcher.CommandContext"/>
    /// and not the <see cref="ArgumentCursor"/> — so nothing in this method's body can name the
    /// cursor at all: pulling another token here is <c>CS0103</c> (unknown identifier), not a
    /// mistake this design leaves to discipline. This method also cannot name
    /// <c>CommandDispatcher.RunIndexRebuild</c> at all — that call is <c>CS0122</c> — which is the
    /// class doc comment's structural half of O-3's evidence for this verb.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseIndexRebuild(string workingDirectory) =>
        new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.IndexRebuild(workingDirectory));

    /// <summary>
    /// <c>block</c>'s only job is routing to a subcommand: <c>transition</c>, <c>gate</c> (§5
    /// block D), <c>add-blocker</c> and <c>remove-blocker</c> (§5 block D). Same peek-don't-take
    /// shape as <see cref="ParseIndex"/>, same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseBlock(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'block' requires a subcommand. Known subcommands: transition, gate, add-blocker, remove-blocker."));
            case "transition":
                context.Arguments.TryTake();
                return ParseBlockTransition(context);
            case "gate":
                context.Arguments.TryTake();
                return ParseBlockGate(context);
            case "add-blocker":
                context.Arguments.TryTake();
                return ParseBlockedByMutation(context, static (filePath, blockingCardId, role, changeName, workingDirectory, timestamp) =>
                    new CommandDispatcher.ParsedCommand.BlockAddBlocker(filePath, blockingCardId, role, changeName, workingDirectory, timestamp));
            case "remove-blocker":
                context.Arguments.TryTake();
                return ParseBlockedByMutation(context, static (filePath, blockingCardId, role, changeName, workingDirectory, timestamp) =>
                    new CommandDispatcher.ParsedCommand.BlockRemoveBlocker(filePath, blockingCardId, role, changeName, workingDirectory, timestamp));
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'block' subcommand: '{subcommand}'. Known subcommands: transition, gate, add-blocker, remove-blocker."));
        }
    }

    /// <summary>
    /// Builds <c>block transition</c>'s <see cref="CommandDispatcher.ParsedCommand.BlockTransition"/>:
    /// two positional tokens (card file path, transition name) followed by <c>--role</c> (required),
    /// and the optional <c>--base</c>/<c>--change</c> flags. Everything decidable from argv alone is
    /// decided here — including <c>--role</c>'s validity, a <see cref="CardOwner"/> wire-format check
    /// that needs no file access — so only what genuinely depends on the card's on-disk state
    /// (whether the named transition is legal from its current status, whether <c>base</c> is
    /// already recorded) is left to the execute phase (O-3). A flag this method does not recognise
    /// is left unconsumed, the same "peek, don't take" discipline <see cref="ParseIndex"/> already
    /// uses, so the funnel's own <c>unrecognised-argument</c> refusal covers it without a second,
    /// verb-specific code for the same fact.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseBlockTransition(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'block transition' requires a card file path and a transition name."));
        }

        var transitionName = context.Arguments.TryTake();
        if (transitionName is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'block transition' requires a transition name."));
        }

        string? roleText = null;
        string? baseCommit = null;
        string? changeName = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--role"] = value => roleText = value,
            ["--base"] = value => baseCommit = value,
            ["--change"] = value => changeName = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'block transition' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.BlockTransition(
            filePath, transitionName, role, baseCommit, changeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>block gate</c>'s <see cref="CommandDispatcher.ParsedCommand.BlockGate"/>: three
    /// positional tokens (card file path, gate label, exit code) followed by <c>--role</c>
    /// (required) and the optional <c>--change</c> flag. The label
    /// (<see cref="GateResult.IsValidLabel"/>) and the exit code (a valid integer) are both
    /// argv-decidable, so both are validated here rather than left to the execute phase (O-3), the
    /// same discipline <see cref="ParseBlockTransition"/> already applies to <c>--role</c>.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseBlockGate(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'block gate' requires a card file path, a gate label and an exit code."));
        }

        var label = context.Arguments.TryTake();
        if (label is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'block gate' requires a gate label and an exit code."));
        }

        if (!GateResult.IsValidLabel(label))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "invalid-gate-label",
                $"'{label}' is not a valid gate label — a label cannot be empty, whitespace-only, or contain '=' or ','."));
        }

        var exitCodeText = context.Arguments.TryTake();
        if (exitCodeText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'block gate' requires an exit code."));
        }

        if (!int.TryParse(exitCodeText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var exitCode))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "invalid-exit-code",
                $"'{exitCodeText}' is not a valid exit code — it must be an integer."));
        }

        var flags = ParseRoleAndChangeFlags(context, "'block gate'");
        if (flags.Refusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flags.Refusal);
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.BlockGate(
            filePath, label, exitCode, flags.Role!, flags.ChangeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds either <c>block add-blocker</c>'s <see cref="CommandDispatcher.ParsedCommand.BlockAddBlocker"/>
    /// or <c>block remove-blocker</c>'s <see cref="CommandDispatcher.ParsedCommand.BlockRemoveBlocker"/>
    /// — the two verbs take identical argv shape (a card file path, a blocking card id,
    /// <c>--role</c>, an optional <c>--change</c>) and differ only in which case they construct,
    /// so <paramref name="build"/> is the one place that difference lives.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseBlockedByMutation(
        CommandDispatcher.CommandContext context,
        Func<string, string, CardOwner, string?, string, DateTimeOffset, CommandDispatcher.ParsedCommand> build)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "this command requires a card file path and a blocking card id."));
        }

        var blockingCardId = context.Arguments.TryTake();
        if (blockingCardId is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "this command requires a blocking card id."));
        }

        if (!BlockCardFields.IsValidListItem(blockingCardId))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "invalid-blocking-card-id",
                "a blocking card id cannot be empty or whitespace-only — card identities are never empty."));
        }

        var flags = ParseRoleAndChangeFlags(context, "this command");
        if (flags.Refusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flags.Refusal);
        }

        return new CommandDispatcher.ParseResult.Ready(
            build(filePath, blockingCardId, flags.Role!, flags.ChangeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// The one flag-loop every §5 verb's flag parsing now goes through (§5 remediation, DEVLOG §5
    /// finding B1/N residue) — <paramref name="setters"/> names every flag this call recognises and
    /// what to do with its value; a token that matches none of them is left unconsumed, the same
    /// "peek, don't take" discipline <see cref="ParseIndex"/> uses, so the funnel's own
    /// <c>unrecognised-argument</c> refusal covers it. <see cref="ParseBlockTransition"/> used to
    /// keep its own hand-copied loop alongside this one — same shape, drawn from the same cursor,
    /// with nothing tying the two together — because it also parses <c>--base</c> in the same pass;
    /// that duplication is exactly where the acting-role fix (finding B1) landing in only two of
    /// three verbs became invisible to a block reviewer looking at one call site at a time. Both
    /// now build a <paramref name="setters"/> map over this one loop instead.
    /// </summary>
    private static CommandOutcome.Refusal? ConsumeKnownFlags(
        CommandDispatcher.CommandContext context, IReadOnlyDictionary<string, Action<string>> setters)
    {
        while (context.Arguments.Peek() is { } flag)
        {
            if (!setters.TryGetValue(flag, out var setter))
            {
                break;
            }

            context.Arguments.TryTake();
            var value = context.Arguments.TryTake();
            if (value is null)
            {
                return new CommandOutcome.Refusal("missing-flag-value", $"'{flag}' requires a value.");
            }

            setter(value);
        }

        return null;
    }

    /// <summary>
    /// The <c>--role</c> (required)/<c>--change</c> (optional) flag pair every §5 block D verb
    /// takes, factored out once both <see cref="ParseBlockGate"/> and
    /// <see cref="ParseBlockedByMutation"/> needed it — built over the same <see cref="
    /// ConsumeKnownFlags"/> loop <see cref="ParseBlockTransition"/> now uses for its own,
    /// <c>--base</c>-inclusive flag set, so the two can no longer drift on how a flag is consumed.
    /// </summary>
    private static (CardOwner? Role, string? ChangeName, CommandOutcome.Refusal? Refusal) ParseRoleAndChangeFlags(
        CommandDispatcher.CommandContext context, string commandLabel)
    {
        string? roleText = null;
        string? changeName = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--role"] = value => roleText = value,
            ["--change"] = value => changeName = value,
        });
        if (flagRefusal is not null)
        {
            return (null, null, flagRefusal);
        }

        if (roleText is null)
        {
            return (null, null, new CommandOutcome.Refusal("missing-argument", $"{commandLabel} requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return (null, null, new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        return (role, changeName, null);
    }

    /// <summary>
    /// The shape every §7 block A creation verb shares once its kind-specific fields (if any) are
    /// out of the way: one positional token (card file path), <c>--role</c> and <c>--title</c>
    /// (both required), <c>--change</c> (required exactly when <paramref name="requireChange"/> is
    /// <see langword="true"/>), and a body read from stdin — the same read-only-extraction
    /// discipline <see cref="ParseFindingRecord"/> already applies. <paramref name="build"/> is the
    /// one place the resulting <see cref="CommandDispatcher.ParsedCommand"/> case differs; used
    /// directly by <see cref="ParseSectionCreate"/> and <see cref="ParseObligationCreate"/>, whose
    /// argv shape is otherwise identical. <see cref="ParseRuleCreate"/> and
    /// <see cref="ParseHazardCreate"/> do not use this — they each have an extra required flag
    /// (<c>--scope</c>, <c>--condition</c>/<c>--cadence</c>) this shape has no room for.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseCardCreate(
        CommandDispatcher.CommandContext context,
        string commandLabel,
        bool requireChange,
        Func<string, string, CardOwner, string, string?, string, DateTimeOffset, CommandDispatcher.ParsedCommand> build)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", $"{commandLabel} requires a card file path."));
        }

        string? title = null;
        string? roleText = null;
        string? changeName = null;

        // --change is registered as a known flag only when this verb's scope actually needs one
        // (requireChange) — a decision (Capability scope, no changeName) is never offered a flag it
        // has nowhere to use; the funnel's own unrecognised-argument refusal catches a caller who
        // supplies one anyway, rather than this method silently accepting and discarding it.
        var setters = new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--title"] = value => title = value,
            ["--role"] = value => roleText = value,
        };
        if (requireChange)
        {
            setters["--change"] = value => changeName = value;
        }

        var flagRefusal = ConsumeKnownFlags(context, setters);
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (title is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", $"{commandLabel} requires '--title <text>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", $"{commandLabel} requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (requireChange && changeName is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", $"{commandLabel} requires '--change <name>'."));
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var body = StdinBodyReader.ReadBody(stdin!);

        return new CommandDispatcher.ParseResult.Ready(build(filePath, title, role, body, changeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// The shape every §7 block A discharge verb shares — one positional token (card file path),
    /// <c>--role</c> (required) and the optional <c>--change</c> flag, the same
    /// <see cref="ParseRoleAndChangeFlags"/> pair <see cref="ParseSectionClose"/> already uses.
    /// <paramref name="kind"/> is fixed per caller (<see cref="ParseRule"/>/<see cref="ParseHazard"/>/
    /// <see cref="ParseObligation"/>/<see cref="ParseDecision"/>'s own <c>discharge</c> arm), never
    /// read from argv — there is no <c>--kind</c> flag, because which of the four kinds is being
    /// discharged is exactly what the top-level command word (<c>rule</c>/<c>hazard</c>/…) already
    /// said.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseRegisterDischarge(
        CommandDispatcher.CommandContext context, CardKind kind, string commandLabel)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", $"{commandLabel} requires a card file path."));
        }

        var flags = ParseRoleAndChangeFlags(context, commandLabel);
        if (flags.Refusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flags.Refusal);
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.RegisterDischarge(
            kind, filePath, flags.Role!, flags.ChangeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// <c>section</c>'s only job is routing to a subcommand: <c>verdict</c>, <c>close</c> and
    /// <c>status</c> (§5 block E). Same peek-don't-take shape as <see cref="ParseBlock"/>, same
    /// reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseSection(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'section' requires a subcommand. Known subcommands: create, verdict, close, status."));
            case "create":
                context.Arguments.TryTake();
                return ParseSectionCreate(context);
            case "verdict":
                context.Arguments.TryTake();
                return ParseSectionVerdict(context);
            case "close":
                context.Arguments.TryTake();
                return ParseSectionClose(context);
            case "status":
                context.Arguments.TryTake();
                return ParseSectionStatus(context);
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'section' subcommand: '{subcommand}'. Known subcommands: create, verdict, close, status."));
        }
    }

    /// <summary>
    /// Builds <c>section create</c>'s <see cref="CommandDispatcher.ParsedCommand.SectionCreate"/>
    /// (§7 block A, Product Owner ruling: "<c>section create</c> is in §7's scope"). One positional
    /// token (card file path); <c>--role</c>, <c>--title</c> and <c>--change</c> are required. The
    /// body is read from stdin during this parse, the same read-only-extraction discipline
    /// <see cref="ParseFindingRecord"/> already applies.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseSectionCreate(CommandDispatcher.CommandContext context) =>
        ParseCardCreate(context, "'section create'", requireChange: true, build:
            (filePath, title, role, body, changeName, workingDirectory, timestamp) =>
                new CommandDispatcher.ParsedCommand.SectionCreate(filePath, title, role, body, changeName!, workingDirectory, timestamp));

    /// <summary>
    /// Builds <c>section verdict</c>'s <see cref="CommandDispatcher.ParsedCommand.SectionVerdict"/>:
    /// one positional token (card file path) followed by <c>--verdict</c>, <c>--range-from</c> and
    /// <c>--range-to</c> (all required), <c>--role</c> (required) and the optional <c>--change</c>
    /// flag. <c>--verdict</c>'s wire-format validity is argv-decidable, the same O-3 discipline
    /// <see cref="ParseBlockTransition"/> already applies to <c>--role</c>.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseSectionVerdict(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'section verdict' requires a card file path."));
        }

        string? verdictText = null;
        string? rangeFrom = null;
        string? rangeTo = null;
        string? roleText = null;
        string? changeName = null;

        while (context.Arguments.Peek() is { } flag)
        {
            if (flag == "--verdict")
            {
                context.Arguments.TryTake();
                verdictText = context.Arguments.TryTake();
                if (verdictText is null)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "missing-flag-value", "'--verdict' requires a value."));
                }

                continue;
            }

            if (flag == "--range-from")
            {
                context.Arguments.TryTake();
                rangeFrom = context.Arguments.TryTake();
                if (rangeFrom is null)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "missing-flag-value", "'--range-from' requires a value."));
                }

                continue;
            }

            if (flag == "--range-to")
            {
                context.Arguments.TryTake();
                rangeTo = context.Arguments.TryTake();
                if (rangeTo is null)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "missing-flag-value", "'--range-to' requires a value."));
                }

                continue;
            }

            if (flag == "--role")
            {
                context.Arguments.TryTake();
                roleText = context.Arguments.TryTake();
                if (roleText is null)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "missing-flag-value", "'--role' requires a value."));
                }

                continue;
            }

            if (flag == "--change")
            {
                context.Arguments.TryTake();
                changeName = context.Arguments.TryTake();
                if (changeName is null)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "missing-flag-value", "'--change' requires a value."));
                }

                continue;
            }

            break;
        }

        if (verdictText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'section verdict' requires '--verdict <verdict>'."));
        }

        if (!SectionVerdictWireFormat.TryParse(verdictText, out var verdict))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-verdict", $"unrecognised verdict: '{verdictText}'. Recognised verdicts: {SectionVerdictWireFormat.RecognisedValues}."));
        }

        if (rangeFrom is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'section verdict' requires '--range-from <commit>'."));
        }

        if (!Callboard.Cards.SectionVerdictEntry.IsValidRangeValue(rangeFrom))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "invalid-range", "'--range-from' cannot be empty or whitespace-only — a range endpoint that cannot be read back is not a range."));
        }

        if (rangeTo is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'section verdict' requires '--range-to <commit>'."));
        }

        if (!Callboard.Cards.SectionVerdictEntry.IsValidRangeValue(rangeTo))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "invalid-range", "'--range-to' cannot be empty or whitespace-only — a range endpoint that cannot be read back is not a range."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'section verdict' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.SectionVerdict(
            filePath, verdict, rangeFrom, rangeTo, role, changeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>section close</c>'s <see cref="CommandDispatcher.ParsedCommand.SectionClose"/>:
    /// one positional token (card file path), <c>--role</c> (required) and the optional
    /// <c>--change</c> flag — the same <c>--role</c>/<c>--change</c> pair
    /// <see cref="ParseRoleAndChangeFlags"/> already factors out.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseSectionClose(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'section close' requires a card file path."));
        }

        var flags = ParseRoleAndChangeFlags(context, "'section close'");
        if (flags.Refusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flags.Refusal);
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.SectionClose(
            filePath, flags.Role!, flags.ChangeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// Builds <c>section status</c>'s <see cref="CommandDispatcher.ParsedCommand.SectionStatus"/>:
    /// one positional token (card file path), nothing else — read-only, so no role, no
    /// <c>--change</c>, no timestamp (work-lifecycle: "the system answers from the section entity
    /// without requiring its cards to be read").
    /// </summary>
    private static CommandDispatcher.ParseResult ParseSectionStatus(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'section status' requires a card file path."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.SectionStatus(filePath, context.WorkingDirectory));
    }

    /// <summary>
    /// <c>finding</c>'s only job is routing to a subcommand: <c>record</c> and <c>status</c> (§6
    /// block C). Same peek-don't-take shape as <see cref="ParseIndex"/>, same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseFinding(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'finding' requires a subcommand. Known subcommands: record, status."));
            case "record":
                context.Arguments.TryTake();
                return ParseFindingRecord(context);
            case "status":
                context.Arguments.TryTake();
                return ParseFindingStatus(context);
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'finding' subcommand: '{subcommand}'. Known subcommands: record, status."));
        }
    }

    /// <summary>
    /// Builds <c>finding status</c>'s <see cref="CommandDispatcher.ParsedCommand.FindingStatus"/>:
    /// one positional token (card file path), nothing else — read-only, the same
    /// <see cref="ParseSectionStatus"/> shape. Unlike <c>section status</c>, this also carries
    /// <see cref="CommandDispatcher.CommandContext.WorkingDirectory"/>: staleness computation needs
    /// the repository root to resolve an <see cref="Callboard.Cards.FindingExtent.Explicit"/>
    /// extent's paths against (§6 block C ruling — the fingerprint is content, resolved relative to
    /// the repo, never git).
    /// </summary>
    private static CommandDispatcher.ParseResult ParseFindingStatus(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument",
                "'finding status' requires a card file path."));
        }

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.FindingStatus(filePath, context.WorkingDirectory));
    }

    /// <summary>
    /// Builds <c>finding record</c>'s <see cref="CommandDispatcher.ParsedCommand.FindingRecord"/>
    /// (§6 block B). One positional token (card file path); <c>--role</c>, <c>--title</c>,
    /// <c>--section</c>, <c>--change</c> and <c>--blind-spot</c> are required, the rest optional.
    /// The body is read from stdin during this parse (a read-only extraction, not the card-writing
    /// side effect O-3 guards — see <see cref="CommandDispatcher.ParsedCommand.FindingRecord"/>'s
    /// own doc comment), so a missing or non-redirected stdin refuses here rather than at execute
    /// time.
    ///
    /// <para>
    /// <b>The blind-spot declaration is checked as input, here (findings: "A clean finding requires
    /// a blind-spot declaration", §6 block B Architect ruling).</b> Block A already made "not
    /// declared" unrepresentable on a constructed <see cref="Callboard.Cards.FindingCardFields"/> —
    /// there is no nullable field left to discover empty later — so the refusal belongs at this
    /// boundary: the caller either supplied <c>--blind-spot none</c> (an explicit assertion there is
    /// no blind spot) or <c>--blind-spot obligation</c>/<c>--blind-spot hazard</c> (a declaration
    /// that is raised as that kind of card). Anything else — the flag missing entirely, or any other
    /// value — refuses with <c>unrecognised-blind-spot</c>, naming both declarations the system will
    /// accept, exactly as the spec's "the system refuses and names the declaration it requires"
    /// scenario asks for.
    /// </para>
    /// </summary>
    private static CommandDispatcher.ParseResult ParseFindingRecord(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'finding record' requires a card file path."));
        }

        string? roleText = null;
        string? title = null;
        string? section = null;
        string? changeName = null;
        string? blindSpotText = null;
        string? instrument = null;
        string? verifiedAt = null;
        string? extentInstrument = null;
        string? extentExplicitRaw = null;
        string? blindSpotFile = null;
        string? blindSpotTitle = null;
        string? blindSpotBodyFile = null;
        string? dispositionText = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--role"] = value => roleText = value,
            ["--title"] = value => title = value,
            ["--section"] = value => section = value,
            ["--change"] = value => changeName = value,
            ["--blind-spot"] = value => blindSpotText = value,
            ["--instrument"] = value => instrument = value,
            ["--verified-at"] = value => verifiedAt = value,
            ["--extent-instrument"] = value => extentInstrument = value,
            ["--extent-explicit"] = value => extentExplicitRaw = value,
            ["--blind-spot-file"] = value => blindSpotFile = value,
            ["--blind-spot-title"] = value => blindSpotTitle = value,
            ["--blind-spot-body-file"] = value => blindSpotBodyFile = value,
            ["--disposition"] = value => dispositionText = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'finding record' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (title is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'finding record' requires '--title <text>'."));
        }

        if (section is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'finding record' requires '--section <name>'."));
        }

        if (changeName is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'finding record' requires '--change <name>'."));
        }

        FindingBlindSpotRaiseRequest? raiseRequest;
        switch (blindSpotText)
        {
            case "none":
                raiseRequest = null;
                break;
            case "obligation":
            case "hazard":
                if (blindSpotFile is null)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "missing-argument",
                        $"'finding record' requires '--blind-spot-file <path>' when --blind-spot is '{blindSpotText}'."));
                }

                if (blindSpotTitle is null)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "missing-argument",
                        $"'finding record' requires '--blind-spot-title <text>' when --blind-spot is '{blindSpotText}'."));
                }

                if (blindSpotBodyFile is null)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "missing-argument",
                        $"'finding record' requires '--blind-spot-body-file <path>' when --blind-spot is '{blindSpotText}'."));
                }

                // §6 remediation (reviewer nit) — split along the same "File.Exists first" line
                // RunFindingStatus/RunSectionStatus already draw for card-not-found: a path the
                // caller named that resolves to no readable file (missing, or naming a directory —
                // File.Exists is false for both) is the caller's own mistake to fix, argv-decidable
                // here at parse time; a path that does exist as a file but cannot be read for
                // environmental reasons (permission denied, a disk error) is not something the
                // caller typo'd, and gets a different code even though both still refuse at parse
                // time — there is no tool-failure outcome available before ParseResult.Ready.
                if (!File.Exists(blindSpotBodyFile))
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "blind-spot-body-file-not-found",
                        $"'--blind-spot-body-file' names a path with no readable file: '{blindSpotBodyFile}'."));
                }

                string blindSpotBody;
                try
                {
                    blindSpotBody = File.ReadAllText(blindSpotBodyFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "blind-spot-body-file-unreadable",
                        $"'--blind-spot-body-file' names a file that exists but could not be read: '{blindSpotBodyFile}' ({ex.Message})"));
                }

                var raisedKind = blindSpotText == "obligation" ? CardKind.Obligation : CardKind.Hazard;
                raiseRequest = new FindingBlindSpotRaiseRequest(raisedKind, blindSpotFile, blindSpotTitle, blindSpotBody);
                break;
            default:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unrecognised-blind-spot",
                    "'finding record' requires '--blind-spot <none|obligation|hazard>' — 'none' explicitly asserts " +
                    "there is no blind spot; 'obligation' or 'hazard' declares one and raises it as that kind of " +
                    (blindSpotText is null ? "card." : $"card. Unrecognised value: '{blindSpotText}'.")));
        }

        if (extentInstrument is not null && extentExplicitRaw is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "invalid-extent",
                "'--extent-instrument' and '--extent-explicit' are mutually exclusive; declare at most one."));
        }

        var extent = FindingExtent.BlockScope;
        if (extentInstrument is not null)
        {
            try
            {
                extent = FindingExtent.Instrument(extentInstrument);
            }
            catch (ArgumentException ex)
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal("invalid-extent", ex.Message));
            }
        }
        else if (extentExplicitRaw is not null)
        {
            try
            {
                extent = FindingExtent.Explicit(extentExplicitRaw.Split(','));
            }
            catch (ArgumentException ex)
            {
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal("invalid-extent", ex.Message));
            }
        }

        // §6 block C: absent-or-"measured" declares the default (FindingDisposition.Measured,
        // itself never re-verified by staleness) — same "argv-decidable, checked here" O-3
        // discipline --blind-spot's own switch above already applies.
        FindingDisposition disposition;
        switch (dispositionText)
        {
            case null:
            case "measured":
                disposition = FindingDisposition.Measured;
                break;
            case "argued-clean":
                disposition = FindingDisposition.ArguedClean;
                break;
            default:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unrecognised-disposition",
                    $"'finding record' requires '--disposition <measured|argued-clean>' when supplied. Unrecognised value: '{dispositionText}'."));
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var body = StdinBodyReader.ReadBody(stdin!);

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.FindingRecord(
            filePath, title, section, changeName, role, body, instrument, extent, verifiedAt, raiseRequest, disposition,
            context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// <c>rule</c>'s only job is routing to a subcommand: <c>create</c> and <c>discharge</c> (§7
    /// block A). Same peek-don't-take shape as <see cref="ParseIndex"/>, same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseRule(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'rule' requires a subcommand. Known subcommands: create, discharge."));
            case "create":
                context.Arguments.TryTake();
                return ParseRuleCreate(context);
            case "discharge":
                context.Arguments.TryTake();
                return ParseRegisterDischarge(context, CardKind.Rule, "'rule discharge'");
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'rule' subcommand: '{subcommand}'. Known subcommands: create, discharge."));
        }
    }

    /// <summary>
    /// Builds <c>rule create</c>'s <see cref="CommandDispatcher.ParsedCommand.RuleCreate"/> (§7
    /// block A). <c>--scope</c> is the one flag no other creation verb here needs — <c>rule</c> is
    /// the only kind <see cref="CardScopeRules"/> gives more than one legal scope, and its wire-
    /// format validity is checked here (argv-decidable, O-3); whether the specific pairing is
    /// actually legal is still <see cref="CardScopeRules.Validate"/>'s call at execute time, not
    /// this method's.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseRuleCreate(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule create' requires a card file path."));
        }

        string? title = null;
        string? roleText = null;
        string? scopeText = null;
        string? changeName = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--title"] = value => title = value,
            ["--role"] = value => roleText = value,
            ["--scope"] = value => scopeText = value,
            ["--change"] = value => changeName = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (title is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule create' requires '--title <text>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule create' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (scopeText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'rule create' requires '--scope <change|repository>'."));
        }

        if (!CardScopeWireFormat.TryParse(scopeText, out var scope))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-scope", $"unrecognised scope: '{scopeText}'. Recognised scopes: {CardScopeWireFormat.RecognisedValues}."));
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var body = StdinBodyReader.ReadBody(stdin!);

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.RuleCreate(
            filePath, title, role, scope, body, changeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// <c>hazard</c>'s only job is routing to a subcommand: <c>create</c> and <c>discharge</c> (§7
    /// block A). Same peek-don't-take shape as <see cref="ParseIndex"/>, same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseHazard(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'hazard' requires a subcommand. Known subcommands: create, discharge."));
            case "create":
                context.Arguments.TryTake();
                return ParseHazardCreate(context);
            case "discharge":
                context.Arguments.TryTake();
                return ParseRegisterDischarge(context, CardKind.Hazard, "'hazard discharge'");
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'hazard' subcommand: '{subcommand}'. Known subcommands: create, discharge."));
        }
    }

    /// <summary>
    /// Builds <c>hazard create</c>'s <see cref="CommandDispatcher.ParsedCommand.HazardCreate"/> (§7
    /// block A, register: "Hazards carry a verification condition"). <b>The load-bearing refusal
    /// site for register's "the system refuses and states the condition it requires" scenario</b> —
    /// <c>--condition</c> and <c>--cadence</c> are both required, and a missing one is refused here,
    /// naming exactly what is missing, rather than deferred to a later layer that would have to
    /// reconstruct the same check. <b>Two distinct codes, not one (reviewer finding, block A
    /// review round 1):</b> a missing <c>--condition</c> is <c>hazard-missing-condition</c>, a
    /// missing <c>--cadence</c> is <c>hazard-missing-cadence</c> — an earlier version minted
    /// <c>hazard-missing-condition</c> for both, which let a machine caller correct the wrong flag
    /// on the first refusal and then be surprised by a second one. Two independently-triggerable
    /// conditions get two codes; this is not the near-synonymous-code collapse <see cref="
    /// CommandDispatcher.WrongCardKind"/>'s own doc comment describes, because that shape is one
    /// code covering the same fact stated two ways, not two different facts sharing one code.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseHazardCreate(CommandDispatcher.CommandContext context)
    {
        var filePath = context.Arguments.TryTake();
        if (filePath is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'hazard create' requires a card file path."));
        }

        string? title = null;
        string? roleText = null;
        string? condition = null;
        string? cadence = null;

        var flagRefusal = ConsumeKnownFlags(context, new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["--title"] = value => title = value,
            ["--role"] = value => roleText = value,
            ["--condition"] = value => condition = value,
            ["--cadence"] = value => cadence = value,
        });
        if (flagRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flagRefusal);
        }

        if (title is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'hazard create' requires '--title <text>'."));
        }

        if (roleText is null)
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "missing-argument", "'hazard create' requires '--role <role>'."));
        }

        if (!CardOwnerWireFormat.TryParse(roleText, out var role))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "unrecognised-role", $"unrecognised role: '{roleText}'. Recognised roles: {CardOwnerWireFormat.RecognisedValues}."));
        }

        if (string.IsNullOrWhiteSpace(condition))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "hazard-missing-condition",
                "'hazard create' requires '--condition <text>' — a hazard cannot be raised without a condition " +
                "under which it can be verified still to hold."));
        }

        if (string.IsNullOrWhiteSpace(cadence))
        {
            return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                "hazard-missing-cadence",
                "'hazard create' requires '--cadence <text>' — a hazard cannot be raised without a cadence at " +
                "which its condition is re-checked."));
        }

        var stdinRefusal = StdinBodyReader.RedirectedStdin.TryCreate(context.Input, context.IsInputRedirected, out var stdin);
        if (stdinRefusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(stdinRefusal);
        }

        var body = StdinBodyReader.ReadBody(stdin!);

        return new CommandDispatcher.ParseResult.Ready(new CommandDispatcher.ParsedCommand.HazardCreate(
            filePath, title, role, body, condition, cadence, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// <c>obligation</c>'s only job is routing to a subcommand: <c>create</c> and <c>discharge</c>
    /// (§7 block A). Same peek-don't-take shape as <see cref="ParseIndex"/>, same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseObligation(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'obligation' requires a subcommand. Known subcommands: create, discharge."));
            case "create":
                context.Arguments.TryTake();
                return ParseObligationCreate(context);
            case "discharge":
                context.Arguments.TryTake();
                return ParseRegisterDischarge(context, CardKind.Obligation, "'obligation discharge'");
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'obligation' subcommand: '{subcommand}'. Known subcommands: create, discharge."));
        }
    }

    /// <summary>
    /// Builds <c>obligation create</c>'s <see cref="CommandDispatcher.ParsedCommand.ObligationCreate"/>
    /// (§7 block A). Scope is always <see cref="CardScope.Change"/>, so <c>--change</c> is required
    /// — the same shape <see cref="ParseSectionCreate"/> already shares via <see cref="ParseCardCreate"/>.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseObligationCreate(CommandDispatcher.CommandContext context) =>
        ParseCardCreate(context, "'obligation create'", requireChange: true, build:
            (filePath, title, role, body, changeName, workingDirectory, timestamp) =>
                new CommandDispatcher.ParsedCommand.ObligationCreate(filePath, title, role, body, changeName!, workingDirectory, timestamp));

    /// <summary>
    /// <c>decision</c>'s only job is routing to a subcommand: <c>create</c> and <c>discharge</c>
    /// (§7 block A). Same peek-don't-take shape as <see cref="ParseIndex"/>, same reason.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseDecision(CommandDispatcher.CommandContext context)
    {
        switch (context.Arguments.Peek())
        {
            case null:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "missing-subcommand",
                    "'decision' requires a subcommand. Known subcommands: create, discharge."));
            case "create":
                context.Arguments.TryTake();
                return ParseDecisionCreate(context);
            case "discharge":
                context.Arguments.TryTake();
                return ParseRegisterDischarge(context, CardKind.Decision, "'decision discharge'");
            case var subcommand:
                return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                    "unknown-subcommand",
                    $"no such 'decision' subcommand: '{subcommand}'. Known subcommands: create, discharge."));
        }
    }

    /// <summary>
    /// Builds <c>decision create</c>'s <see cref="CommandDispatcher.ParsedCommand.DecisionCreate"/>
    /// (§7 block A). Scope is always <see cref="CardScope.Capability"/>, which
    /// <see cref="Cards.CardLayout.DirectoryFor"/> resolves without a change name — so, unlike
    /// <see cref="ParseObligationCreate"/>/<see cref="ParseSectionCreate"/>, this does not accept
    /// <c>--change</c> at all.
    /// </summary>
    private static CommandDispatcher.ParseResult ParseDecisionCreate(CommandDispatcher.CommandContext context) =>
        ParseCardCreate(context, "'decision create'", requireChange: false, build:
            (filePath, title, role, body, _, workingDirectory, timestamp) =>
                new CommandDispatcher.ParsedCommand.DecisionCreate(filePath, title, role, body, workingDirectory, timestamp));
}
