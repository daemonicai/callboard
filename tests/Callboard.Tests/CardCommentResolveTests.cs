using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §9 remediation, round two (S4) — <see cref="CardStore.ResolveComment"/>, the store method behind
/// both <c>comment resolve</c> and <c>comment decline --reason</c>. Card-model: resolution is an
/// appended comment naming what it <see cref="CardComment.Resolves"/>, never a mutation of the
/// resolved comment.
/// </summary>
public sealed class CardCommentResolveTests : IDisposable
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset Created = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ResolvedAt = Created.AddHours(3);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-comment-resolve-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _changeDirectory;

    public CardCommentResolveTests()
    {
        _changeDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_changeDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ResolveComment_LiveAddressedComment_Resolves_AppendsAResolvingComment_NeverMutatesTheOriginal()
    {
        var path = WriteCardWithComment("b-0001", "B-0001", "thread-1", CardOwner.Reviewer);

        var outcome = CardStore.ResolveComment(
            _root, path, "thread-1", CardOwner.Reviewer, "Fixed in the last commit.", requireReason: false, ResolvedAt, TimeSpan.FromSeconds(5), ChangeName);

        var resolved = AssertResolved(outcome);
        Assert.Equal("thread-1", resolved.ResolvingComment.Resolves);
        Assert.Equal("Fixed in the last commit.", resolved.ResolvingComment.Body);
        Assert.Equal(CardOwner.Reviewer, resolved.ResolvingComment.Author);
        Assert.Equal(ResolvedAt, resolved.ResolvingComment.Timestamp);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(2, read.Comments.Count);
        Assert.Equal("thread-1", read.Comments[0].Id);
        Assert.Equal("Original comment.", read.Comments[0].Body);
        Assert.Null(read.Comments[0].Resolves);
        Assert.True(CardCommentRouting.IsResolved(read.Comments, 0));
        Assert.Empty(read.Refusals);
    }

    [Fact]
    public void ResolveComment_WithAReason_RequireReasonTrue_Resolves_RecordingTheReasonAsTheCommentBody()
    {
        var path = WriteCardWithComment("b-0002", "B-0002", "thread-1", CardOwner.Architect);

        var outcome = CardStore.ResolveComment(
            _root, path, "thread-1", CardOwner.Architect, "won't fix — out of scope.", requireReason: true, ResolvedAt, TimeSpan.FromSeconds(5), ChangeName);

        var resolved = AssertResolved(outcome);
        Assert.Equal("won't fix — out of scope.", resolved.ResolvingComment.Body);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.True(CardCommentRouting.IsResolved(read.Comments, 0));
    }

    [Fact]
    public void ResolveComment_CommentDoesNotExist_Refuses_AndRecordsTheRefusal_AndAppendsNothing()
    {
        var path = WriteCardWithComment("b-0003", "B-0003", "thread-1", CardOwner.Reviewer);

        var outcome = CardStore.ResolveComment(
            _root, path, "no-such-thread", CardOwner.Reviewer, "irrelevant.", requireReason: false, ResolvedAt, TimeSpan.FromSeconds(5), ChangeName);

        var refusal = Assert.IsType<CardCommentResolveOutcome.CommentNotFound>(outcome);
        Assert.Equal("no-such-thread", refusal.CommentId);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Single(read.Comments);
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Reviewer, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void ResolveComment_AlreadyResolved_Refuses_AndRecordsTheRefusal_AndDoesNotDoubleResolve()
    {
        var path = WriteCardWithComment("b-0004", "B-0004", "thread-1", CardOwner.Reviewer);
        var first = CardStore.ResolveComment(
            _root, path, "thread-1", CardOwner.Reviewer, "First resolution.", requireReason: false, ResolvedAt, TimeSpan.FromSeconds(5), ChangeName);
        AssertResolved(first);

        var outcome = CardStore.ResolveComment(
            _root, path, "thread-1", CardOwner.Reviewer, "Second attempt.", requireReason: false, ResolvedAt.AddMinutes(5), TimeSpan.FromSeconds(5), ChangeName);

        var refusal = Assert.IsType<CardCommentResolveOutcome.AlreadyResolved>(outcome);
        Assert.Equal("thread-1", refusal.CommentId);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(2, read.Comments.Count);
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Reviewer, recorded.By);
    }

    [Fact]
    public void ResolveComment_RequireReasonTrue_NoReason_Refuses_AndRecordsTheRefusal_AndDoesNotResolve()
    {
        var path = WriteCardWithComment("b-0005", "B-0005", "thread-1", CardOwner.Architect);

        var outcome = CardStore.ResolveComment(
            _root, path, "thread-1", CardOwner.Architect, "   ", requireReason: true, ResolvedAt, TimeSpan.FromSeconds(5), ChangeName);

        Assert.IsType<CardCommentResolveOutcome.ReasonRequired>(outcome);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Single(read.Comments);
        Assert.False(CardCommentRouting.IsResolved(read.Comments, 0));
        var recorded = Assert.Single(read.Refusals);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    private string WriteCardWithComment(string fileStem, string id, string commentId, CardOwner addressedTo)
    {
        var path = Path.Combine(_changeDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "A block card", "in-review", CardOwner.Worker, CardScope.Change, "S-0001", Created, Created);
        var comment = new CardComment(
            Id: commentId, Author: CardOwner.Architect, Timestamp: Created.AddHours(1), Body: "Original comment.",
            ReplyTo: null, To: addressedTo, Resolves: null, UnknownHeaderFields: []);
        var card = new CardFile(frontmatter, "Body.", [comment], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardCommentResolveOutcome.Resolved AssertResolved(CardCommentResolveOutcome outcome) =>
        outcome.Match(
            onResolved: static resolved => resolved,
            onCommentNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Resolved, got CommentNotFound: '{notFound.CommentId}'"),
            onAlreadyResolved: static already => throw new Xunit.Sdk.XunitException($"expected Resolved, got AlreadyResolved: '{already.CommentId}'"),
            onReasonRequired: static reasonRequired => throw new Xunit.Sdk.XunitException($"expected Resolved, got ReasonRequired: '{reasonRequired.FilePath}'"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Resolved, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Resolved, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Resolved, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Resolved, got ToolFailure: {toolFailure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
