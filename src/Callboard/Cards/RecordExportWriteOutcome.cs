namespace Callboard.Cards;

/// <summary>
/// Closed union over how writing an export document (§11 block C) can end. Not card-addressed —
/// nothing on this path touches a card, so this is not <see cref="ICardRefusalReason"/>-shaped and
/// nothing here is ever <c>RefuseAndRecord</c>ed against a card: <see cref="TargetExists"/> is a
/// plain caller-correctable fact about the output path, while <see cref="ToolFailure"/> is
/// enforcement itself being unavailable rather than the board saying no (ADR-0001) — <see
/// cref="Cli.CommandDispatcher"/>'s mapping of this case follows the same "throw, do not hand-build
/// a refusal" idiom every other <c>ToolFailure</c> case in this codebase already uses.
/// </summary>
internal abstract record RecordExportWriteOutcome
{
    private RecordExportWriteOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onWritten,
        Func<TResult> onTargetExists,
        Func<string, TResult> onToolFailure);

    internal static readonly RecordExportWriteOutcome Written = new WrittenCase();

    internal static readonly RecordExportWriteOutcome TargetExists = new TargetExistsCase();

    internal static RecordExportWriteOutcome ToolFailure(string reason) => new ToolFailureCase(reason);

    private sealed record WrittenCase : RecordExportWriteOutcome
    {
        internal override TResult Match<TResult>(Func<TResult> onWritten, Func<TResult> onTargetExists, Func<string, TResult> onToolFailure) =>
            onWritten();
    }

    private sealed record TargetExistsCase : RecordExportWriteOutcome
    {
        internal override TResult Match<TResult>(Func<TResult> onWritten, Func<TResult> onTargetExists, Func<string, TResult> onToolFailure) =>
            onTargetExists();
    }

    private sealed record ToolFailureCase(string Reason) : RecordExportWriteOutcome
    {
        internal override TResult Match<TResult>(Func<TResult> onWritten, Func<TResult> onTargetExists, Func<string, TResult> onToolFailure) =>
            onToolFailure(Reason);
    }
}
