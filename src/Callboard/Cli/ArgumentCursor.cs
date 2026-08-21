namespace Callboard.Cli;

/// <summary>
/// The only way a command handler sees argv tokens (§3 obligation 3, carried from §1): a handler
/// takes tokens off the front one at a time via <see cref="TryTake"/> and never sees the raw
/// array, so "a handler ignored a token the caller passed" stops being possible to write by
/// accident — there is no <c>string[]</c> to index into. What is left unconsumed after routing
/// reaches a leaf command is what <see cref="CommandDispatcher"/> checks — in
/// <c>CommandDispatcher.EnforceNoUnconsumedArguments</c>, called once from <c>Run</c> on whatever
/// <see cref="CommandDispatcher.Dispatch(string, CommandDispatcher.CommandContext)"/> returned —
/// per ADR-0001's "any token it does not consume is a refusal".
/// <para>
/// That check runs <em>after</em> <c>Dispatch</c> returns, not before: it overrides a
/// <see cref="CommandOutcome.Success"/> into a refusal, but a <see cref="CommandOutcome.Refusal"/>
/// the handler already returned passes through untouched. Enforcement is therefore post-hoc — by
/// the time an unrecognised trailing token turns a handler's outcome into a refusal, that handler
/// has already run and any side effect it had has already happened. §3 accepted this for
/// <c>index rebuild</c> because its side effect (the derived index) is disposable and rebuildable;
/// it is <b>not</b> acceptable for a verb whose side effect writes the primary record (obligation
/// O-3, DEVLOG §3) — see
/// <c>CommandDispatcherTests.IndexRebuild_WithTrailingToken_RefusesButHasAlreadyWrittenTheIndex</c>
/// for the pinned characterisation of today's behaviour.
/// </para>
/// </summary>
internal sealed class ArgumentCursor
{
    private readonly IReadOnlyList<string> _tokens;
    private int _index;

    internal ArgumentCursor(IReadOnlyList<string> tokens) => _tokens = tokens;

    /// <summary>Takes the next token, or <see langword="null"/> if none remain.</summary>
    internal string? TryTake() => _index < _tokens.Count ? _tokens[_index++] : null;

    /// <summary>
    /// Looks at the next token without consuming it, so a router can decide whether a token
    /// belongs to it before committing to take it — the envelope's <c>command</c> field (built
    /// from <see cref="ConsumedTokens"/>) only ever names what was actually recognised, never a
    /// token a handler merely glanced at and rejected.
    /// </summary>
    internal string? Peek() => _index < _tokens.Count ? _tokens[_index] : null;

    internal bool HasUnconsumedTokens => _index < _tokens.Count;

    /// <summary>The next unconsumed token. Only valid when <see cref="HasUnconsumedTokens"/>.</summary>
    internal string FirstUnconsumed => _tokens[_index];

    /// <summary>
    /// Every token taken so far, in order — the single source of truth
    /// <see cref="CommandDispatcher.Run"/> uses to build the envelope's <c>command</c> field, so a
    /// two-token verb like <c>index rebuild</c> reports both tokens without any handler having to
    /// report its own name back out.
    /// </summary>
    internal IReadOnlyList<string> ConsumedTokens => _tokens.Take(_index).ToArray();
}
