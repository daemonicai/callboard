using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 4.3 — a card identity stays resolvable, with its status and full thread, after the change that
/// raised it is archived. Archive-as-a-verb is not built in this section (block A's brief); this
/// simulates it as what it is per the Product Owner's binding decision: a directory move of
/// <c>callboard/changes/&lt;name&gt;/</c> to <c>callboard/changes/archive/&lt;name&gt;/</c>.
/// </summary>
public sealed class CardIdentityArchiveSurvivalTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "sample-change";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-archive-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void IdentityRaisedInAChange_ResolvesWithStatusAndFullThread_AfterTheChangeIsArchived()
    {
        var liveDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(liveDirectory);
        var cardPath = Path.Combine(liveDirectory, "b-0001.md");

        var frontmatter = new CardFrontmatter(
            "B-0001", CardKind.Block, "Archived block", "closed", CardOwner.Architect, CardScope.Change, "4", Created, Created);
        var card = new CardFile(frontmatter, "Original body.", [], []);
        AssertWriteSuccess(CardStore.WriteCard(_root, cardPath, card, TimeSpan.FromSeconds(5), ChangeName));

        var firstComment = new CardComment("C-0001", CardOwner.Worker, Created, "First reply.", null, null, null, []);
        var secondComment = new CardComment("C-0002", CardOwner.Reviewer, Created, "Second reply.", "C-0001", CardOwner.Worker, "C-0001", []);
        AssertWriteSuccess(CardStore.AppendComment(_root, cardPath, firstComment, TimeSpan.FromSeconds(5), ChangeName));
        AssertWriteSuccess(CardStore.AppendComment(_root, cardPath, secondComment, TimeSpan.FromSeconds(5), ChangeName));

        // Archive itself is a directory move on callboard/changes/<name>/ and nothing else (the
        // Product Owner's binding decision) — simulated directly rather than through a verb this
        // block does not build.
        var archiveRoot = Path.Combine(_root, "callboard", "changes", "archive");
        Directory.CreateDirectory(archiveRoot);
        var archivedDirectory = Path.Combine(archiveRoot, ChangeName);
        Directory.Move(liveDirectory, archivedDirectory);

        Assert.False(Directory.Exists(liveDirectory));
        var archivedCardPath = Path.Combine(archivedDirectory, "b-0001.md");

        var resolved = AssertParseSuccess(CardStore.ReadCard(archivedCardPath));

        Assert.Equal("B-0001", resolved.Frontmatter.Id);
        Assert.Equal("closed", resolved.Frontmatter.Status);
        Assert.Equal("Original body.", resolved.Body);
        Assert.Equal(2, resolved.Comments.Count);
        Assert.Equal("First reply.", resolved.Comments[0].Body);
        Assert.Equal("Second reply.", resolved.Comments[1].Body);
        Assert.Equal("C-0001", resolved.Comments[1].ReplyTo);
        Assert.Equal("C-0001", resolved.Comments[1].Resolves);
        Assert.True(CardCommentRouting.IsResolved(resolved.Comments, 0));
    }

    private static void AssertWriteSuccess(CardWriteResult result) =>
        result.Match<object?>(
            onSuccess: static _ => null,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected write success, got failure: {failure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
