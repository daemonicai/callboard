namespace Callboard.Cards;

/// <summary>One live, open <c>section</c> card (working-context, §10 block C: "the open
/// sections").</summary>
internal sealed record DerivedStateOpenSection(string FilePath, CardFile Card, string ChangeName);

/// <summary>One live (undischarged) <c>obligation</c> card, with the section it is owed to
/// (working-context: "the live obligations with the section that owes each"). <see
/// cref="OwedBySectionId"/> is <see cref="RegisterCardFields.OwedBy"/> verbatim — a section card
/// id, never a free-text label.</summary>
internal sealed record DerivedStateObligation(string FilePath, CardFile Card, string OwedBySectionId);

/// <summary>One live (not <see cref="QuestionStatus.Answered"/>) <c>question</c> card, with who
/// currently owes its answer (working-context: "the open questions with who owes each answer").
/// <see cref="OwesAnswer"/> is the card's own <see cref="CardFrontmatter.Owner"/> — card-model's
/// "Ownership names whose turn it is" applies to a question exactly as it does to any other card,
/// and a deferred question's owner is who register's "the question remains open ... and continues
/// to surface to the role that owes its answer" means by that role.</summary>
internal sealed record DerivedStateQuestion(string FilePath, CardFile Card, CardOwner OwesAnswer);

/// <summary>One live <c>block</c> card carrying at least one <c>blocked_by</c> entry (working-
/// context: "every blocked card with what blocks it"). <see cref="Halted"/> is derived, never
/// stored (escalation-severity: "escalation severity is derived from a question's owner"): true
/// exactly when at least one blocking id resolves to a live, Product-Owner-owned, open question —
/// <see cref="HaltedByQuestionId"/>/<see cref="HaltedByQuestionTitle"/> name it. A card blocked
/// only by non-Product-Owner questions (or by cards that are not questions at all) is blocked but
/// not halted — <see cref="Halted"/> is <see langword="false"/>, keeping the two facts legible
/// separately rather than collapsing "blocked" and "halted" into one signal.</summary>
internal sealed record DerivedStateBlockedCard(
    string FilePath,
    CardFile Card,
    IReadOnlyList<string> BlockedByIds,
    bool Halted,
    string? HaltedByQuestionId,
    string? HaltedByQuestionTitle);

/// <summary>The derived state summary (working-context, §10 block C: "a summary of overall process
/// state comprising the open sections, task completion counted from the task list itself, the live
/// obligations with the section that owes each, the open questions with who owes each answer, and
/// every blocked card with what blocks it"). Every field here is computed fresh by <see
/// cref="DerivedStateAssembler.Build"/> on each call — nothing on this type, or on any type it is
/// built from, is ever persisted.</summary>
internal sealed record DerivedState(
    IReadOnlyList<DerivedStateOpenSection> OpenSections,
    IReadOnlyList<TasksMdCompletion> TaskCompletion,
    IReadOnlyList<DerivedStateObligation> LiveObligations,
    IReadOnlyList<DerivedStateQuestion> OpenQuestions,
    IReadOnlyList<DerivedStateBlockedCard> BlockedCards);

/// <summary>
/// Assembles <see cref="DerivedState"/> for <c>callboard state</c> (§10 block C). Not role-scoped —
/// unlike <see cref="WorkingContextAssembler"/>, this is the same read for every caller (working-
/// context's own scenario: "any role requests the state summary"). Reads the primary record
/// directly via <see cref="CardLayout.ResolveLiveRecordDirectories"/> — never the derived index,
/// never the archive (the same §10-opening Product Owner ruling <see cref="WorkingContextAssembler"/>
/// follows) — and <see cref="RuleCitations.CountCitations"/>'s O(rules × cards) walk is never
/// called from this path.
/// </summary>
internal static class DerivedStateAssembler
{
    internal static DerivedState Build(string cardsRoot)
    {
        var openSections = new List<DerivedStateOpenSection>();
        var obligations = new List<DerivedStateObligation>();
        var questions = new List<DerivedStateQuestion>();
        var blockedCards = new List<DerivedStateBlockedCard>();
        var changeNames = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var directory in CardLayout.ResolveLiveRecordDirectories(cardsRoot))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var changeName = WorkingContextAssembler.ChangeNameForDirectory(cardsRoot, directory);
            if (changeName is not null)
            {
                changeNames.Add(changeName);
            }

            foreach (var (filePath, result) in CardStore.ReadAllCards(directory))
            {
                var card = result.Match<CardFile?>(
                    onSuccess: static success => success.Card,
                    onFailure: static _ => null);

                if (card is null || CardLifecycle.IsClosed(card))
                {
                    continue;
                }

                if (CardStore.IsSectionCard(card))
                {
                    if (changeName is not null)
                    {
                        openSections.Add(new DerivedStateOpenSection(filePath, card, changeName));
                    }

                    continue;
                }

                if (CardStore.IsObligationCard(card) && card.RegisterFields.OwedBy is { } owedBy)
                {
                    obligations.Add(new DerivedStateObligation(filePath, card, owedBy));
                    continue;
                }

                if (CardStore.IsQuestionCard(card))
                {
                    questions.Add(new DerivedStateQuestion(filePath, card, card.Frontmatter.Owner));
                    continue;
                }

                if (CardStore.IsBlockCard(card) && card.BlockFields.BlockedBy.Length > 0)
                {
                    var haltingQuestion = CardStore.FindBlockingOpenProductOwnerQuestion(cardsRoot, card);
                    blockedCards.Add(new DerivedStateBlockedCard(
                        filePath,
                        card,
                        [.. card.BlockFields.BlockedBy],
                        Halted: haltingQuestion is not null,
                        HaltedByQuestionId: haltingQuestion?.QuestionId,
                        HaltedByQuestionTitle: haltingQuestion?.Title));
                }
            }
        }

        openSections.Sort(static (a, b) => string.CompareOrdinal(a.Card.Frontmatter.Id, b.Card.Frontmatter.Id));
        obligations.Sort(static (a, b) => string.CompareOrdinal(a.Card.Frontmatter.Id, b.Card.Frontmatter.Id));
        questions.Sort(static (a, b) => string.CompareOrdinal(a.Card.Frontmatter.Id, b.Card.Frontmatter.Id));
        blockedCards.Sort(static (a, b) => string.CompareOrdinal(a.Card.Frontmatter.Id, b.Card.Frontmatter.Id));

        var taskCompletion = changeNames.Select(name => TasksMdParser.CountCompletion(cardsRoot, name)).ToList();

        return new DerivedState(openSections, taskCompletion, obligations, questions, blockedCards);
    }
}
