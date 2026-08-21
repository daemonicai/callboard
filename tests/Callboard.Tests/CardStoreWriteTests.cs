using Callboard.Cards;

namespace Callboard.Tests;

public sealed class CardStoreWriteTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "callboard-store-tests-" + Guid.NewGuid().ToString("N"));

    public CardStoreWriteTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void WriteCard_CreatesAReadableFile()
    {
        var path = Path.Combine(_directory, "b-0001.md");
        var card = SampleCard("B-0001");

        var result = CardStore.WriteCard(path, card, TimeSpan.FromSeconds(5));

        AssertSuccess(result);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(card.Frontmatter, read.Frontmatter);
    }

    [Fact]
    public void WriteCard_LeavesNoTempFileBehindOnSuccess()
    {
        var path = Path.Combine(_directory, "b-0002.md");
        AssertSuccess(CardStore.WriteCard(path, SampleCard("B-0002"), TimeSpan.FromSeconds(5)));

        var entries = Directory.GetFiles(_directory);
        Assert.Equal([path], entries);
    }

    [Fact]
    public void WriteCard_CreatesTheContainingDirectory_WhenItDoesNotYetExist()
    {
        var nested = Path.Combine(_directory, "nested", "sub");
        var path = Path.Combine(nested, "b-0003.md");

        AssertSuccess(CardStore.WriteCard(path, SampleCard("B-0003"), TimeSpan.FromSeconds(5)));

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task WriteCard_OverwritingRepeatedly_NeverExposesAPartiallyWrittenFileToAConcurrentReader()
    {
        var path = Path.Combine(_directory, "b-0004.md");
        AssertSuccess(CardStore.WriteCard(path, SampleCard("B-0004", body: new string('x', 20_000)), TimeSpan.FromSeconds(5)));

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
            AssertSuccess(CardStore.WriteCard(path, SampleCard("B-0004", body: new string((char)('a' + (i % 26)), 20_000)), TimeSpan.FromSeconds(5)));
        }

        Volatile.Write(ref stop, true);
        await reader;

        Assert.Empty(readerFailures);
    }

    [Fact]
    public void AppendComment_AddsToAnExistingCard()
    {
        var path = Path.Combine(_directory, "b-0005.md");
        AssertSuccess(CardStore.WriteCard(path, SampleCard("B-0005"), TimeSpan.FromSeconds(5)));

        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, false);
        AssertSuccess(CardStore.AppendComment(path, comment, TimeSpan.FromSeconds(5)));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(comment, Assert.Single(read.Comments));
    }

    [Fact]
    public void AppendComment_WhenNoCardExistsAtThatPath_Fails()
    {
        var path = Path.Combine(_directory, "missing.md");
        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, false);

        var result = CardStore.AppendComment(path, comment, TimeSpan.FromSeconds(5));

        var failure = AssertFailure(result);
        Assert.Contains(path, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendComment_WhenTheCardFileIsCorrupt_FailsWithoutTouchingTheFile()
    {
        var path = Path.Combine(_directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");

        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, false);
        var result = CardStore.AppendComment(path, comment, TimeSpan.FromSeconds(5));

        AssertFailure(result);
        Assert.Equal("not a card file at all", File.ReadAllText(path));
    }

    private static CardFile SampleCard(string id, string body = "Body.") =>
        new(
            new CardFrontmatter(id, CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Change, "2", Created, Created),
            body,
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
