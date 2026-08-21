using Callboard.Cards;

namespace Callboard.Tests;

public sealed class CardStoreWriteTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-store-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardStoreWriteTests()
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
    public void WriteCard_CreatesAReadableFile()
    {
        var path = Path.Combine(_directory, "b-0001.md");
        var card = SampleCard("B-0001");

        var result = CardStore.WriteCard(path, card, TimeSpan.FromSeconds(5), ChangeName);

        AssertSuccess(result);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(card.Frontmatter, read.Frontmatter);
    }

    [Fact]
    public void WriteCard_LeavesNoTempFileBehindOnSuccess()
    {
        var path = Path.Combine(_directory, "b-0002.md");
        AssertSuccess(CardStore.WriteCard(path, SampleCard("B-0002"), TimeSpan.FromSeconds(5), ChangeName));

        var entries = Directory.GetFiles(_directory);
        Assert.Equal([path], entries);
    }

    [Fact]
    public void WriteCard_CreatesTheContainingDirectory_WhenItDoesNotYetExist()
    {
        // A fresh change name, not the one the constructor already created — its scope directory
        // must not exist on disk yet for this to actually exercise directory creation.
        const string freshChangeName = "not-yet-created-change";
        var directory = Path.Combine(_root, CardLayout.ChangesDirectory(freshChangeName).Replace('/', Path.DirectorySeparatorChar));
        var path = Path.Combine(directory, "b-0003.md");
        Assert.False(Directory.Exists(directory));

        AssertSuccess(CardStore.WriteCard(path, SampleCard("B-0003"), TimeSpan.FromSeconds(5), freshChangeName));

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task WriteCard_OverwritingRepeatedly_NeverExposesAPartiallyWrittenFileToAConcurrentReader()
    {
        var path = Path.Combine(_directory, "b-0004.md");
        AssertSuccess(CardStore.WriteCard(path, SampleCard("B-0004", body: new string('x', 20_000)), TimeSpan.FromSeconds(5), ChangeName));

        var readerFailures = new List<string>();
        var stop = false;

        var reader = Task.Run(
            () =>
            {
                while (!Volatile.Read(ref stop))
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var result = CardStore.ReadCard(path);
                    result.Match<object?>(
                        onSuccess: static _ => null,
                        onFailure: failure =>
                        {
                            lock (readerFailures)
                            {
                                readerFailures.Add(failure.Reason);
                            }

                            return null;
                        });
                }
            },
            TestContext.Current.CancellationToken);

        for (var i = 0; i < 50; i++)
        {
            AssertSuccess(CardStore.WriteCard(path, SampleCard("B-0004", body: new string((char)('a' + (i % 26)), 20_000)), TimeSpan.FromSeconds(5), ChangeName));
        }

        Volatile.Write(ref stop, true);
        await reader;

        Assert.Empty(readerFailures);
    }

    [Fact]
    public void AppendComment_AddsToAnExistingCard()
    {
        var path = Path.Combine(_directory, "b-0005.md");
        AssertSuccess(CardStore.WriteCard(path, SampleCard("B-0005"), TimeSpan.FromSeconds(5), ChangeName));

        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, false, []);
        AssertSuccess(CardStore.AppendComment(path, comment, TimeSpan.FromSeconds(5), ChangeName));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(comment, Assert.Single(read.Comments));
    }

    [Fact]
    public void AppendComment_PreservesAnUnrecognisedFrontmatterField_ThatWasAlreadyOnDisk()
    {
        var path = Path.Combine(_directory, "b-0006.md");

        // Written directly, bypassing CardFile/CardStore, standing in for a §5 field a newer
        // build (or a human) already added to this card — this build's own schema does not model
        // "base" (CardFrontmatter.cs's own doc comment names it as a future field), which is
        // exactly the case this test asserts AppendComment must not silently destroy.
        var raw =
            "---\n" +
            "id: B-0006\nkind: block\ntitle: Title\nstatus: open\nowner: worker\nscope: change\nsection: 2\n" +
            "created: 2026-08-20T09:00:00+00:00\nupdated: 2026-08-20T09:00:00+00:00\n" +
            "base: B-0001\n" +
            "---\n" +
            "Body.\n";
        File.WriteAllText(path, raw, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, false, []);
        AssertSuccess(CardStore.AppendComment(path, comment, TimeSpan.FromSeconds(5), ChangeName));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(("base", "B-0001"), Assert.Single(read.UnknownFrontmatterFields));
        Assert.Equal(comment, Assert.Single(read.Comments));
    }

    [Fact]
    public void WriteCard_RefusesAChangeScopedCard_WhenNoChangeNameIsSupplied()
    {
        // ValidateAgainstLayout resolves the expected directory via CardLayout.DirectoryFor,
        // which throws ArgumentException for a Change-scoped card with no change name; the guard
        // catches that and turns it into a CardWriteResult.Failure rather than letting it escape
        // as an unhandled exception. Exercised at the CardStore boundary, not just on CardLayout
        // in isolation, since that catch-and-convert is exactly what this block wired up.
        var path = Path.Combine(_directory, "b-0099.md");

        var result = CardStore.WriteCard(path, SampleCard("B-0099"), TimeSpan.FromSeconds(5), changeName: null);

        AssertFailure(result);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WriteCard_RefusesWhenTheFilePathDoesNotLiveInTheScopesLayoutDirectory()
    {
        // A Repository-scoped card belongs in callboard/register/; writing it under the
        // change-scoped directory the constructor already created is a genuine layout mismatch,
        // not a lookalike — this proves the guard refuses the ordinary wrong-directory case
        // before the more adversarial suffix-collision case below.
        var path = Path.Combine(_directory, "r-0001.md");
        var card = RepositoryScopedCard("R-0001");

        var result = CardStore.WriteCard(path, card, TimeSpan.FromSeconds(5));

        var failure = AssertFailure(result);
        Assert.Contains("does not live in the directory", failure, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WriteCard_RefusesADirectoryThatMerelyEndsWithTheExpectedDirectorysCharacters()
    {
        // "evilcallboard/register/" ends with the same characters as the expected
        // "callboard/register/" — a raw string-suffix match would wrongly accept this. The guard
        // has to compare whole path segments, not trailing characters, to refuse it.
        var evilDirectory = Path.Combine(_root, "evilcallboard", "register");
        Directory.CreateDirectory(evilDirectory);
        var path = Path.Combine(evilDirectory, "r-0002.md");
        var card = RepositoryScopedCard("R-0002");

        var result = CardStore.WriteCard(path, card, TimeSpan.FromSeconds(5));

        var failure = AssertFailure(result);
        Assert.Contains("does not live in the directory", failure, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AppendComment_RefusesWhenTheFilePathDoesNotLiveInTheDirectoryTheCardsOwnScopeRequires()
    {
        // Write a Repository-scoped card legitimately (in its real directory), then try to append
        // to it from a path outside that directory. AppendComment resolves the expected directory
        // from the card's own on-disk scope, so this must refuse just as WriteCard does.
        var registerDirectory = Path.Combine(_root, "callboard", "register");
        Directory.CreateDirectory(registerDirectory);
        var realPath = Path.Combine(registerDirectory, "r-0003.md");
        AssertSuccess(CardStore.WriteCard(realPath, RepositoryScopedCard("R-0003"), TimeSpan.FromSeconds(5)));

        var wrongPath = Path.Combine(_directory, "r-0003.md");
        File.Copy(realPath, wrongPath);

        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, false, []);
        var result = CardStore.AppendComment(wrongPath, comment, TimeSpan.FromSeconds(5));

        var failure = AssertFailure(result);
        Assert.Contains("does not live in the directory", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendComment_WhenNoCardExistsAtThatPath_Fails()
    {
        var path = Path.Combine(_directory, "missing.md");
        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, false, []);

        var result = CardStore.AppendComment(path, comment, TimeSpan.FromSeconds(5), ChangeName);

        var failure = AssertFailure(result);
        Assert.Contains(path, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendComment_WhenTheCardFileIsCorrupt_FailsWithoutTouchingTheFile()
    {
        var path = Path.Combine(_directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");

        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, false, []);
        var result = CardStore.AppendComment(path, comment, TimeSpan.FromSeconds(5), ChangeName);

        AssertFailure(result);
        Assert.Equal("not a card file at all", File.ReadAllText(path));
    }

    private static CardFile SampleCard(string id, string body = "Body.") =>
        new(
            new CardFrontmatter(id, CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Change, "2", Created, Created),
            body,
            [],
            []);

    private static CardFile RepositoryScopedCard(string id) =>
        new(
            new CardFrontmatter(id, CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Repository, string.Empty, Created, Created),
            "Body.",
            [],
            []);

    private static void AssertSuccess(CardWriteResult result) =>
        result.Match<object?>(
            onSuccess: static _ => null,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected write success, got failure: {failure.Reason}"));

    private static string AssertFailure(CardWriteResult result) =>
        result.Match(
            onSuccess: static _ => throw new Xunit.Sdk.XunitException("expected write failure, got success."),
            onFailure: failure => failure.Reason);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
