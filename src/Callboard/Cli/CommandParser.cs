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
        _ => new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
            "unknown-command",
            $"no such command: '{command}'. Known commands: version, index, block.")),
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

        while (context.Arguments.Peek() is { } flag)
        {
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

            if (flag == "--base")
            {
                context.Arguments.TryTake();
                baseCommit = context.Arguments.TryTake();
                if (baseCommit is null)
                {
                    return new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
                        "missing-flag-value", "'--base' requires a value."));
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

        var flags = ParseRoleAndChangeFlags(context, "this command");
        if (flags.Refusal is not null)
        {
            return new CommandDispatcher.ParseResult.Refused(flags.Refusal);
        }

        return new CommandDispatcher.ParseResult.Ready(
            build(filePath, blockingCardId, flags.Role!, flags.ChangeName, context.WorkingDirectory, context.Clock()));
    }

    /// <summary>
    /// The <c>--role</c> (required)/<c>--change</c> (optional) flag pair every §5 block D verb
    /// takes, factored out once both <see cref="ParseBlockGate"/> and
    /// <see cref="ParseBlockedByMutation"/> needed it — <see cref="ParseBlockTransition"/> is left
    /// with its own inline copy (it also has <c>--base</c> interleaved in the same loop, so
    /// factoring it out there would split one flag loop across two methods for no gain).
    /// </summary>
    private static (CardOwner? Role, string? ChangeName, CommandOutcome.Refusal? Refusal) ParseRoleAndChangeFlags(
        CommandDispatcher.CommandContext context, string commandLabel)
    {
        string? roleText = null;
        string? changeName = null;

        while (context.Arguments.Peek() is { } flag)
        {
            if (flag == "--role")
            {
                context.Arguments.TryTake();
                roleText = context.Arguments.TryTake();
                if (roleText is null)
                {
                    return (null, null, new CommandOutcome.Refusal("missing-flag-value", "'--role' requires a value."));
                }

                continue;
            }

            if (flag == "--change")
            {
                context.Arguments.TryTake();
                changeName = context.Arguments.TryTake();
                if (changeName is null)
                {
                    return (null, null, new CommandOutcome.Refusal("missing-flag-value", "'--change' requires a value."));
                }

                continue;
            }

            break;
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
}
