using System.Text.Json;
using System.Text.Json.Serialization;
using Callboard.Cards;

namespace Callboard.Cli;

/// <summary>
/// <c>callboard state</c>'s success result (§10 block C, working-context: "a summary of overall
/// process state comprising the open sections, task completion counted from the task list itself,
/// the live obligations with the section that owes each, the open questions with who owes each
/// answer, and every blocked card with what blocks it"). Not role-scoped — <see cref="Cards.
/// DerivedStateAssembler.Build"/> takes no <see cref="CardOwner"/> — and, unlike <see
/// cref="ContextResult"/>, carries no character budget: the working-context budget (D6) is stated
/// for the working-context response specifically, not for this one (§10 block C brief — see the
/// DEVLOG for why this is left explicit rather than assumed either way).
/// </summary>
internal sealed class StateResult : ICommandResult
{
    [JsonPropertyName("openSections")]
    public required IReadOnlyList<StateOpenSectionResult> OpenSections { get; init; }

    [JsonPropertyName("taskCompletion")]
    public required IReadOnlyList<StateTaskCompletionResult> TaskCompletion { get; init; }

    [JsonPropertyName("liveObligations")]
    public required IReadOnlyList<StateObligationResult> LiveObligations { get; init; }

    [JsonPropertyName("openQuestions")]
    public required IReadOnlyList<StateQuestionResult> OpenQuestions { get; init; }

    [JsonPropertyName("blockedCards")]
    public required IReadOnlyList<StateBlockedCardResult> BlockedCards { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.StateResult);
}

/// <summary>One open <c>section</c> card.</summary>
internal sealed class StateOpenSectionResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("changeName")]
    public required string ChangeName { get; init; }
}

/// <summary>One live change's task completion, counted from its own <c>tasks.md</c> at request
/// time — see <see cref="Cards.TasksMdCompletion"/>. <see cref="TasksFileFound"/> is
/// <see langword="false"/> when the change has no <c>tasks.md</c>; <see cref="Ticked"/>/<see
/// cref="Total"/> are then both <c>0</c>, but that is reported alongside the flag rather than
/// standing alone, so "no file" is never mistaken for "no tasks".</summary>
internal sealed class StateTaskCompletionResult
{
    [JsonPropertyName("changeName")]
    public required string ChangeName { get; init; }

    [JsonPropertyName("tasksFileFound")]
    public required bool TasksFileFound { get; init; }

    [JsonPropertyName("ticked")]
    public required int Ticked { get; init; }

    [JsonPropertyName("total")]
    public required int Total { get; init; }
}

/// <summary>One live (undischarged) <c>obligation</c> card and the section it is owed to.</summary>
internal sealed class StateObligationResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("owedBySectionId")]
    public required string OwedBySectionId { get; init; }
}

/// <summary>One live <c>question</c> card and who currently owes its answer.</summary>
internal sealed class StateQuestionResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("owesAnswer")]
    public required string OwesAnswer { get; init; }
}

/// <summary>One live <c>block</c> card carrying at least one <c>blocked_by</c> entry (escalation-
/// severity: severity is derived from a blocking question's owner, never stored). <see
/// cref="Halted"/> is <see langword="true"/> exactly when at least one blocker resolves to a live,
/// Product-Owner-owned, open question — <see cref="HaltedByQuestionId"/>/<see
/// cref="HaltedByQuestionTitle"/> name it. A card blocked only by a non-Product-Owner question (or
/// by a non-question card) is still listed here with <see cref="Halted"/> <see langword="false"/> —
/// blocked and halted are kept legible as two different facts, not collapsed into one.</summary>
internal sealed class StateBlockedCardResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("blockedByIds")]
    public required IReadOnlyList<string> BlockedByIds { get; init; }

    [JsonPropertyName("halted")]
    public required bool Halted { get; init; }

    [JsonPropertyName("haltedByQuestionId")]
    public string? HaltedByQuestionId { get; init; }

    [JsonPropertyName("haltedByQuestionTitle")]
    public string? HaltedByQuestionTitle { get; init; }
}
