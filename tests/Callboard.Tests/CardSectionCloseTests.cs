using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 5.8 — closing a section under lock (§5 block E, work-lifecycle: "closing it SHALL record the
/// acting role and the time"). This type never checks §9's closing conditions (open obligations,
/// undeferred questions, unresolved threads) — see <see cref="CardSectionCloseOutcome"/>'s own doc
/// comment; these tests only cover what this block owns: the entity's own state.
/// </summary>
public sealed class CardSectionCloseTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-section-close-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardSectionCloseTests()
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
    public void CloseSection_OnAnOpenSection_RecordsActingRoleAndTime_AndFlipsStatus()
    {
        var path = WriteInitialSectionCard("s-0001", "S-0001");

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created.AddDays(3), TimeSpan.FromSeconds(5), ChangeName);

        var closed = AssertClosed(outcome);
        Assert.Equal("closed", closed.Card.Frontmatter.Status);
        Assert.Equal(CardOwner.Architect, closed.Card.SectionFields.ClosedBy);
        Assert.Equal(Created.AddDays(3), closed.Card.SectionFields.ClosedAt);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("closed", read.Frontmatter.Status);
        Assert.Equal(CardOwner.Architect, read.SectionFields.ClosedBy);
        Assert.Equal(Created.AddDays(3), read.SectionFields.ClosedAt);
    }

    // Owed evidence — closing does not re-record a new acting role/time over the first: what would
    // have to break for this to go red is CloseSectionUnderExistingLock skipping the
    // already-closed check and silently overwriting ClosedBy/ClosedAt on a second call.
    [Fact]
    public void CloseSection_AlreadyClosed_Refuses_AndDoesNotOverwriteTheFirstClosure()
    {
        var path = WriteInitialSectionCard("s-0002", "S-0002");
        AssertClosed(CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Supervisor, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName);

        var already = Assert.IsType<CardSectionCloseOutcome.AlreadyClosed>(outcome);
        Assert.Equal(path, already.FilePath);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(CardOwner.Architect, read.SectionFields.ClosedBy);
        Assert.Equal(Created, read.SectionFields.ClosedAt);
    }

    [Fact]
    public void CloseSection_TargetIsNotASectionCard_Refuses()
    {
        var path = Path.Combine(_directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "5", Created, Created);
        AssertWriteSuccess(CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notASection = Assert.IsType<CardSectionCloseOutcome.NotASectionCard>(outcome);
        Assert.Equal(CardKind.Question, notASection.Kind);
    }

    [Fact]
    public void CloseSection_WhenNoCardExistsAtThatPath_Fails()
    {
        var path = Path.Combine(_directory, "missing.md");

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notFound = Assert.IsType<CardSectionCloseOutcome.CardNotFound>(outcome);
        Assert.Equal(path, notFound.FilePath);
    }

    [Fact]
    public void CloseSection_LayoutMismatch_ReturnsLayoutMismatch_NotCardNotFound()
    {
        var path = WriteInitialSectionCard("s-0003", "S-0003");

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), "a-different-change");

        Assert.IsType<CardSectionCloseOutcome.LayoutMismatch>(outcome);
    }

    [Fact]
    public void CloseSection_WhenTheCardFileIsCorrupt_ReturnsCardCorrupt_NotARefusalShapedOutcome()
    {
        var path = Path.Combine(_directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var corrupt = Assert.IsType<CardSectionCloseOutcome.CardCorrupt>(outcome);
        Assert.Equal(path, corrupt.FilePath);
    }

    [Fact]
    public void CloseSection_WhenTheLockIsHeldByAnotherCaller_ReturnsToolFailure_NotARefusalShapedOutcome()
    {
        var path = WriteInitialSectionCard("s-0004", "S-0004");
        var holder = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));

        try
        {
            var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromMilliseconds(200), ChangeName);

            Assert.IsType<CardSectionCloseOutcome.ToolFailure>(outcome);
        }
        finally
        {
            holder.Dispose();
        }
    }

    private string WriteInitialSectionCard(string fileStem, string id)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, "5", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardSectionCloseOutcome.Closed AssertClosed(CardSectionCloseOutcome outcome) =>
        outcome.Match(
            onClosed: static closed => closed,
            onAlreadyClosed: static already => throw new Xunit.Sdk.XunitException($"expected Closed, got AlreadyClosed: '{already.FilePath}'"),
            onNotASectionCard: static n => throw new Xunit.Sdk.XunitException($"expected Closed, got NotASectionCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Closed, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Closed, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Closed, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Closed, got ToolFailure: {toolFailure.Reason}"));

    private static CardLock AssertAcquired(CardLockResult result) =>
        result.Match(
            onAcquired: static acquired => acquired.Lock,
            onTimedOut: static timedOut => throw new Xunit.Sdk.XunitException($"expected to acquire the lock, timed out: {timedOut.Message}"));

    private static void AssertWriteSuccess(CardWriteResult result) =>
        result.Match<object?>(
            onSuccess: static _ => null,
            onNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected write success, got NotFound: '{notFound.FilePath}'"),
            onAlreadyExists: alreadyExists => throw new Xunit.Sdk.XunitException($"expected write success, got AlreadyExists: '{alreadyExists.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected write success, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected write success, got Corrupt: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected write success, got ToolFailure: {toolFailure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
