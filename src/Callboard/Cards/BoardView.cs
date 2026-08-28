namespace Callboard.Cards;

/// <summary>One card placed in one <see cref="BoardViewColumn"/>.</summary>
internal sealed record BoardViewCard(string FilePath, CardFile Card);

/// <summary>One owner's cards within one <see cref="BoardViewColumn"/>, in card-id order. Never
/// present for an owner with zero cards in that column — the brief's "a column with no cards"
/// render case still needs a valid, empty column, but an owner sub-heading with nothing under it
/// is not information.</summary>
internal sealed record BoardViewOwnerGroup(CardOwner Owner, IReadOnlyList<BoardViewCard> Cards);

/// <summary>One state within a <see cref="BoardViewLane"/>'s own flow (or, for the register area,
/// one of <see cref="RegisterLifecycleState"/>'s two states) — "column" (record-retrieval: "cards
/// by column and owner"). Product Owner ruling, §12 block B rework: a column is a flow state, not
/// a card kind — the worker's first reading (§12 block B's original post) grouped by <see
/// cref="CardKind"/> instead and was overturned; that reasoning is not repeated here. Always
/// present for every state its lane declares, even when <see cref="OwnerGroups"/> is empty — an
/// empty column still renders, it does not disappear (an empty <c>in-review</c> column is itself
/// information).</summary>
internal sealed record BoardViewColumn(string Name, IReadOnlyList<BoardViewOwnerGroup> OwnerGroups);

/// <summary>One flow vocabulary's own row of columns (Product Owner ruling: "lanes by flow
/// vocabulary, columns by that vocabulary's states"). Four lanes carry the four kinds that occupy
/// a flow state — block (<see cref="BlockFlowState"/>), section (<see cref="SectionFlowState"/>),
/// question (<see cref="QuestionStatus"/>, deferred folded into the open column per §10 ruling 2 —
/// "not answering is itself a halting state"), finding (a single column, since a finding never
/// closes) — and the same shape does double duty for the register area below them: one lane per
/// register kind (obligation, rule, hazard, decision), each with exactly two columns, open and
/// discharged, because register cards SHALL NOT occupy flow states and so never get a flow lane of
/// their own.</summary>
internal sealed record BoardViewLane(string Name, IReadOnlyList<BoardViewColumn> Columns);

/// <summary>One referenced card's display facts — <see cref="CardFrontmatter.Title"/> and
/// whether it is closed — keyed by <see cref="CardFrontmatter.Id"/>. What lets the renderer show
/// a blocked-on id's title, and whether it is blocked on a card that has since closed, without a
/// second walk of the record: built from the same read <see cref="BoardViewAssembler.Build"/>
/// already does for <see cref="BoardView.Lanes"/>.</summary>
internal sealed record BoardViewCardSummary(string Title, bool Closed);

/// <summary>The whole board (§12 block B, record-retrieval: "cards by column and owner, what is
/// blocked and on what, and the open questions with who owes each answer"). <see
/// cref="BlockedById"/> and <see cref="OpenQuestionOwesById"/> are keyed views over <see
/// cref="DerivedState.BlockedCards"/>/<see cref="DerivedState.OpenQuestions"/> verbatim — reused,
/// never re-derived — so the renderer can annotate a blocked block card, or an open question card,
/// inline where the lane already shows it (Product Owner ruling: "must not be exiled to a footer
/// the eye never reaches while it is looking at the lane"), rather than in a separate summary
/// section. <see cref="Unreadable"/> carries every card file the read found and could not parse
/// (§12 remediation, generalised onto <see cref="UnreadableCard"/> in §13.5) — never silently
/// dropped from the board. It is <see cref="DerivedState.Unreadable"/> verbatim, not a second
/// walk's own set: <c>view</c> already reuses <see cref="DerivedStateAssembler.Build"/>'s blocked
/// and open-question facts rather than re-deriving them, and what the record could not read is
/// exactly the same kind of fact — two independently collected sets over the same directories
/// could disagree, and the board and <c>state</c> would then each be describing a different
/// record.</summary>
internal sealed record BoardView(
    IReadOnlyList<BoardViewLane> Lanes,
    IReadOnlyList<BoardViewLane> RegisterLanes,
    IReadOnlyDictionary<string, BoardViewCardSummary> SummaryById,
    IReadOnlyDictionary<string, DerivedStateBlockedCard> BlockedById,
    IReadOnlyDictionary<string, CardOwner> OpenQuestionOwesById,
    IReadOnlyList<UnreadableCard> Unreadable);

/// <summary>
/// Assembles <see cref="BoardView"/> for <c>view --out &lt;path&gt;</c> (§12 block B). Reads the
/// primary record directly, via <see cref="CardLayout.ResolveLiveRecordDirectories"/> — never the
/// derived index (§10's binding ruling 1: "the read paths read card files, not the index"), and
/// never the archive, the same scope every other read path in this file group already honours.
/// </summary>
internal static class BoardViewAssembler
{
    private const string OpenColumnName = "Open";
    private const string DischargedColumnName = "Discharged";

    internal static BoardView Build(string cardsRoot)
    {
        var allCards = new List<(string FilePath, CardFile Card)>();
        foreach (var directory in CardLayout.ResolveLiveRecordDirectories(cardsRoot))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var (filePath, result) in CardStore.ReadAllCards(directory))
            {
                // Deliberately not collected here (§13.5): DerivedStateAssembler.Build below walks
                // exactly these directories and reports the same parse failures, and BoardView.
                // Unreadable is that set verbatim. Collecting a second one at this site is the
                // parallel set this assembler exists not to grow.
                var card = result.Match<CardFile?>(
                    onSuccess: static success => success.Card,
                    onFailure: static _ => null);

                if (card is not null)
                {
                    allCards.Add((filePath, card));
                }
            }
        }

        var lanes = new List<BoardViewLane>
        {
            BuildBlockLane(allCards),
            BuildSectionLane(allCards),
            BuildQuestionLane(allCards),
            BuildFindingLane(allCards),
        };

        var registerLanes = CardKindWireFormat.RegisterKinds
            .Select(kind => BuildRegisterLane(kind, allCards))
            .ToList();

        var summaryById = new Dictionary<string, BoardViewCardSummary>(StringComparer.Ordinal);
        foreach (var (_, card) in allCards)
        {
            summaryById[card.Frontmatter.Id] = new BoardViewCardSummary(card.Frontmatter.Title, CardLifecycle.IsClosed(card));
        }

        var state = DerivedStateAssembler.Build(cardsRoot);

        var blockedById = new Dictionary<string, DerivedStateBlockedCard>(StringComparer.Ordinal);
        foreach (var blocked in state.BlockedCards)
        {
            blockedById[blocked.Card.Frontmatter.Id] = blocked;
        }

        var openQuestionOwesById = new Dictionary<string, CardOwner>(StringComparer.Ordinal);
        foreach (var question in state.OpenQuestions)
        {
            openQuestionOwesById[question.Card.Frontmatter.Id] = question.OwesAnswer;
        }

        return new BoardView(lanes, registerLanes, summaryById, blockedById, openQuestionOwesById, state.Unreadable);
    }

    /// <summary>Blocks lane: one column per <see cref="BlockFlowState"/>, in <see cref="
    /// BlockFlowStateWireFormat"/>'s own flow order.</summary>
    private static BoardViewLane BuildBlockLane(IReadOnlyList<(string FilePath, CardFile Card)> allCards)
    {
        var blockCards = allCards.Where(static entry => CardStore.IsBlockCard(entry.Card)).ToList();

        BoardViewColumn BuildFor(BlockFlowState state, string name) => BuildColumn(
            name,
            blockCards.Where(entry => BlockFlowStateWireFormat.TryParse(entry.Card.Frontmatter.Status, out var parsed) && ReferenceEquals(parsed, state)));

        return new BoardViewLane("Block", [
            BuildFor(BlockFlowState.Drafting, "Drafting"),
            BuildFor(BlockFlowState.Briefed, "Briefed"),
            BuildFor(BlockFlowState.Building, "Building"),
            BuildFor(BlockFlowState.InReview, "In review"),
            BuildFor(BlockFlowState.Approved, "Approved"),
            BuildFor(BlockFlowState.Landed, "Landed"),
            BuildFor(BlockFlowState.Closed, "Closed"),
        ]);
    }

    /// <summary>Sections lane: one column per <see cref="SectionFlowState"/> (open,
    /// closed).</summary>
    private static BoardViewLane BuildSectionLane(IReadOnlyList<(string FilePath, CardFile Card)> allCards)
    {
        var sectionCards = allCards.Where(static entry => CardStore.IsSectionCard(entry.Card)).ToList();

        BoardViewColumn BuildFor(SectionFlowState state, string name) => BuildColumn(
            name,
            sectionCards.Where(entry => SectionFlowStateWireFormat.TryParse(entry.Card.Frontmatter.Status, out var parsed) && ReferenceEquals(parsed, state)));

        return new BoardViewLane("Section", [
            BuildFor(SectionFlowState.Open, "Open"),
            BuildFor(SectionFlowState.Closed, "Closed"),
        ]);
    }

    /// <summary>Questions lane: <see cref="QuestionStatus.Deferred"/> folds into the open column
    /// (§10 ruling 2: "not answering is itself a halting state" — a deferred question is not a
    /// softer third state), so this lane has exactly two columns, not three.</summary>
    private static BoardViewLane BuildQuestionLane(IReadOnlyList<(string FilePath, CardFile Card)> allCards)
    {
        var questionCards = allCards.Where(static entry => CardStore.IsQuestionCard(entry.Card)).ToList();

        var open = questionCards.Where(entry => QuestionStatusWireFormat.TryParse(entry.Card.Frontmatter.Status, out var parsed)
            && (ReferenceEquals(parsed, QuestionStatus.Open) || ReferenceEquals(parsed, QuestionStatus.Deferred)));
        var answered = questionCards.Where(entry => QuestionStatusWireFormat.TryParse(entry.Card.Frontmatter.Status, out var parsed)
            && ReferenceEquals(parsed, QuestionStatus.Answered));

        return new BoardViewLane("Question", [
            BuildColumn("Open", open),
            BuildColumn("Answered", answered),
        ]);
    }

    /// <summary>Findings lane: a single column — a finding never closes (<see cref="
    /// CardLifecycle.IsClosed"/>'s <c>onFinding</c> arm is always <see langword="false"/>), so
    /// inventing a second state here would be symmetry for its own sake, not information.</summary>
    private static BoardViewLane BuildFindingLane(IReadOnlyList<(string FilePath, CardFile Card)> allCards)
    {
        var findingCards = allCards.Where(static entry => CardStore.IsFindingCard(entry.Card));
        return new BoardViewLane("Finding", [BuildColumn("Open", findingCards)]);
    }

    /// <summary>One register kind's own lane in the register area: exactly two columns, open and
    /// discharged (<see cref="RegisterLifecycleState"/>) — register cards SHALL NOT occupy flow
    /// states, so this is never a flow lane, only shaped like one for rendering reuse.</summary>
    private static BoardViewLane BuildRegisterLane(CardKind kind, IReadOnlyList<(string FilePath, CardFile Card)> allCards)
    {
        var kindCards = allCards.Where(entry => entry.Card.Frontmatter.Kind == kind).ToList();

        BoardViewColumn BuildFor(RegisterLifecycleState state, string columnName) => BuildColumn(
            columnName,
            kindCards.Where(entry => RegisterLifecycleStateWireFormat.TryParse(entry.Card.Frontmatter.Status, out var parsed) && ReferenceEquals(parsed, state)));

        return new BoardViewLane(kind.DisplayName(), [
            BuildFor(RegisterLifecycleState.Open, OpenColumnName),
            BuildFor(RegisterLifecycleState.Discharged, DischargedColumnName),
        ]);
    }

    private static BoardViewColumn BuildColumn(string name, IEnumerable<(string FilePath, CardFile Card)> cards)
    {
        var cardList = cards.ToList();
        var ownerGroups = new List<BoardViewOwnerGroup>();
        foreach (var owner in CardOwnerWireFormat.AllOwners)
        {
            var ownerCards = cardList
                .Where(entry => entry.Card.Frontmatter.Owner == owner)
                .OrderBy(static entry => entry.Card.Frontmatter.Id, StringComparer.Ordinal)
                .Select(static entry => new BoardViewCard(entry.FilePath, entry.Card))
                .ToList();

            if (ownerCards.Count > 0)
            {
                ownerGroups.Add(new BoardViewOwnerGroup(owner, ownerCards));
            }
        }

        return new BoardViewColumn(name, ownerGroups);
    }
}
