using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 4.5 — ownership names whose turn it is, and every handover records the acting role and the
/// time it occurred (card-model: "Ownership names whose turn it is" — "**Every** ownership change
/// SHALL record the acting role and the time it occurred"). Recorded as an append-only
/// <see cref="CardHandover"/> sequence (<see cref="CardFile.Handovers"/>), not overwritable
/// frontmatter scalars — reviewer round 1, finding 3: two scalars overwritten on every transfer
/// cannot satisfy "every" for a card handed over more than once, which is the ordinary lifecycle
/// (architect → worker → reviewer → supervisor), not an edge case. <see cref="CardFrontmatter.Owner"/>
/// stays the queryable current state; every assertion here is against the parsed record, not just
/// the write's own success/failure outcome.
/// </summary>
public sealed class CardOwnershipTransferTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-ownership-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardOwnershipTransferTests()
    {
        _directory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TransferOwnership_ChangesOwner_AndAppendsAHandoverRecordingTheActingRoleAndTimestamp()
    {
        var path = WriteInitialCard("b-0001", "B-0001", CardOwner.Worker);
        var handoverTime = Created.AddHours(3);

        // The acting role is neither the outgoing nor the incoming owner — the ordinary case
        // (an architect reassigning worker's card to reviewer) — proving the attribution is not
        // collapsed into "the previous owner did it".
        var result = CardStore.TransferOwnership(_root, path, CardOwner.Reviewer, CardOwner.Architect, handoverTime, TimeSpan.FromSeconds(5), ChangeName);

        AssertSuccess(result);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(CardOwner.Reviewer, read.Frontmatter.Owner);
        var handover = Assert.Single(read.Handovers);
        Assert.Equal(CardOwner.Architect, handover.By);
        Assert.Equal(CardOwner.Reviewer, handover.To);
        Assert.Equal(handoverTime, handover.Timestamp);
        Assert.Equal(handoverTime, read.Frontmatter.Updated);
    }

    [Fact]
    public void TransferOwnership_TwiceInARow_RetainsBothHandoversInOrder()
    {
        // This is the test the reviewer's finding 3 named directly: the frontmatter-scalar shape
        // left only the most recent handover recoverable. The append-only sequence keeps both.
        var path = WriteInitialCard("b-0002", "B-0002", CardOwner.Worker);
        var firstHandover = Created.AddHours(1);
        var secondHandover = Created.AddHours(2);

        AssertSuccess(CardStore.TransferOwnership(_root, path, CardOwner.Reviewer, CardOwner.Architect, firstHandover, TimeSpan.FromSeconds(5), ChangeName));
        AssertSuccess(CardStore.TransferOwnership(_root, path, CardOwner.Supervisor, CardOwner.Reviewer, secondHandover, TimeSpan.FromSeconds(5), ChangeName));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(CardOwner.Supervisor, read.Frontmatter.Owner);
        Assert.Equal(2, read.Handovers.Count);

        Assert.Equal(CardOwner.Architect, read.Handovers[0].By);
        Assert.Equal(CardOwner.Reviewer, read.Handovers[0].To);
        Assert.Equal(firstHandover, read.Handovers[0].Timestamp);

        Assert.Equal(CardOwner.Reviewer, read.Handovers[1].By);
        Assert.Equal(CardOwner.Supervisor, read.Handovers[1].To);
        Assert.Equal(secondHandover, read.Handovers[1].Timestamp);
    }

    [Fact]
    public void TransferOwnership_OwnerAlwaysMatchesTheMostRecentHandoversTo_ByConstruction()
    {
        // How Owner (the state) and Handovers (the history) are kept from disagreeing: the same
        // write sets Owner to exactly the To of the entry it appends — there is no second code
        // path that could set one without the other, so a derived-value drift (the index problem
        // moved into the record) cannot occur here even after several transfers.
        var path = WriteInitialCard("b-0003", "B-0003", CardOwner.Worker);
        var owners = new[] { CardOwner.Reviewer, CardOwner.Supervisor, CardOwner.ProductOwner, CardOwner.Worker };

        var timestamp = Created;
        foreach (var owner in owners)
        {
            timestamp = timestamp.AddHours(1);
            AssertSuccess(CardStore.TransferOwnership(_root, path, owner, CardOwner.Architect, timestamp, TimeSpan.FromSeconds(5), ChangeName));
        }

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(owners.Length, read.Handovers.Count);
        Assert.Equal(read.Handovers[^1].To, read.Frontmatter.Owner);
        Assert.Equal(CardOwner.Worker, read.Frontmatter.Owner);
    }

    [Fact]
    public void TransferOwnership_OnACardNeverHandedOver_HasAnEmptyHandoverSequenceOnDisk()
    {
        var path = WriteInitialCard("b-0004", "B-0004", CardOwner.Worker);

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("callboard:handover", raw, StringComparison.Ordinal);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.Handovers);
    }

    [Fact]
    public void TransferOwnership_WritesTheHandoverAsItsOwnBlock_NeverAsAnAppendedComment()
    {
        // O-2's fix does not become a building block for 4.5 (the architect's brief): the
        // handover must not show up as a new entry in the card's comment thread — it has no
        // author writing prose and no addressee, and routing must never read it as a comment
        // addressed to a role's queue.
        var path = WriteInitialCard("b-0005", "B-0005", CardOwner.Worker);

        AssertSuccess(CardStore.TransferOwnership(_root, path, CardOwner.Reviewer, CardOwner.Architect, Created.AddHours(1), TimeSpan.FromSeconds(5), ChangeName));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.Comments);
        Assert.Single(read.Handovers);
    }

    [Fact]
    public void TransferOwnership_AndAppendComment_EachPreserveTheOtherSequenceUntouched()
    {
        // Two independent append-only sequences sharing one card: a comment append must not
        // disturb the handover history, and a handover must not disturb the comment thread.
        var path = WriteInitialCard("b-0006", "B-0006", CardOwner.Worker);
        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Started.", null, null, null, []);

        AssertSuccess(CardStore.AppendComment(_root, path, comment, TimeSpan.FromSeconds(5), ChangeName));
        AssertSuccess(CardStore.TransferOwnership(_root, path, CardOwner.Reviewer, CardOwner.Architect, Created.AddHours(1), TimeSpan.FromSeconds(5), ChangeName));
        AssertSuccess(CardStore.AppendComment(
            _root, path, new CardComment("C-0002", CardOwner.Reviewer, Created.AddHours(2), "Reviewing.", null, null, null, []),
            TimeSpan.FromSeconds(5), ChangeName));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(2, read.Comments.Count);
        Assert.Single(read.Handovers);
        Assert.Equal(CardOwner.Reviewer, read.Frontmatter.Owner);
    }

    [Fact]
    public void TransferOwnership_RefusesACorrectlyShapedTail_UnderTheWrongRepositoryRoot()
    {
        var path = WriteInitialCard("b-0007", "B-0007", CardOwner.Worker);

        var result = CardStore.TransferOwnership(
            Path.Combine(Path.GetTempPath(), "callboard-ownership-wrong-root-" + Guid.NewGuid().ToString("N")),
            path, CardOwner.Reviewer, CardOwner.Architect, Created.AddHours(1), TimeSpan.FromSeconds(5), ChangeName);

        var failure = AssertFailure(result);
        Assert.Contains("does not live in the directory", failure, StringComparison.Ordinal);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(CardOwner.Worker, read.Frontmatter.Owner);
        Assert.Empty(read.Handovers);
    }

    [Fact]
    public void TransferOwnership_WhenNoCardExistsAtThatPath_Fails()
    {
        var path = Path.Combine(_directory, "missing.md");

        var result = CardStore.TransferOwnership(_root, path, CardOwner.Reviewer, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var failure = AssertFailure(result);
        Assert.Contains(path, failure, StringComparison.Ordinal);
    }

    private string WriteInitialCard(string fileStem, string id, CardOwner owner)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "Title", "open", owner, CardScope.Change, "4", Created, Created);
        AssertSuccess(CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), ChangeName));
        return path;
    }

    private static void AssertSuccess(CardWriteResult result) =>
        result.Match<object?>(
            onSuccess: static _ => null,
            onNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected write success, got NotFound: '{notFound.FilePath}'"),
            onAlreadyExists: alreadyExists => throw new Xunit.Sdk.XunitException($"expected write success, got AlreadyExists: '{alreadyExists.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected write success, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected write success, got Corrupt: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected write success, got ToolFailure: {toolFailure.Reason}"));

    private static string AssertFailure(CardWriteResult result) =>
        result.Match(
            onSuccess: static _ => throw new Xunit.Sdk.XunitException("expected write failure, got success."),
            onNotFound: notFound => $"no card file exists at '{notFound.FilePath}'.",
            onAlreadyExists: alreadyExists => $"a card already exists at '{alreadyExists.FilePath}'.",
            onLayoutMismatch: layoutMismatch => layoutMismatch.Reason,
            onCorrupt: corrupt => $"the card file is corrupt: {corrupt.Reason}",
            onToolFailure: toolFailure => toolFailure.Reason);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
