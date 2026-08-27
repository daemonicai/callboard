namespace Callboard.Cards;

/// <summary>
/// The frontmatter key names reserved for the derived state summary (working-context, §10 block
/// C: "No figure SHALL be hand-entered anywhere in the system, and the system SHALL NOT maintain a
/// hand-written pin"). These are never keys this build's own parser assigns a typed home to — a
/// card carrying one arrives on <see cref="CardFile.UnknownFrontmatterFields"/>, which means the
/// only way one gets onto a card at all is a hand edit made outside the tool. Named after the
/// concepts <c>callboard state</c> itself reports (open sections, task completion, live
/// obligations, open questions, blocked cards) plus the "next-step pin" the spec's own scenario
/// names, so that reserving them is legible against the response they would otherwise shadow.
/// </summary>
internal static class DerivedStateFieldKeys
{
    internal const string OpenSections = "open_sections";
    internal const string TaskCompletion = "task_completion";
    internal const string LiveObligations = "live_obligations";
    internal const string OpenQuestions = "open_questions";
    internal const string BlockedCards = "blocked_cards";
    internal const string NextStep = "next_step";

    /// <summary>Every reserved key this build recognises. <see cref="CardStore.
    /// ReservedDerivedStateFieldKeyIn"/> is the only reader.</summary>
    internal static readonly IReadOnlyList<string> All =
        [OpenSections, TaskCompletion, LiveObligations, OpenQuestions, BlockedCards, NextStep];
}
