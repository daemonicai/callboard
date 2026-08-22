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
        _ => new CommandDispatcher.ParseResult.Refused(new CommandOutcome.Refusal(
            "unknown-command",
            $"no such command: '{command}'. Known commands: version, index.")),
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
}
