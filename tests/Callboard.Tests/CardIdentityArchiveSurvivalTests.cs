using Callboard.Cards;
using Callboard.Index;
using Microsoft.Data.Sqlite;

namespace Callboard.Tests;

/// <summary>
/// 4.3 — a card identity stays resolvable, with its status and full thread, after the change that
/// raised it is archived. Archive-as-a-verb is not built in this section (block A's brief); this
/// simulates it as what it is per the Product Owner's binding decision: a directory move of
/// <c>callboard/changes/&lt;name&gt;/</c> to <see cref="CardLayout.ArchivedChangeDirectory"/>.
///
/// <para>
/// <b>§4 remediation, R1 — rewritten to resolve through the production path, not a hand-built
/// string.</b> The original version of this file moved the directory to a path it built itself
/// (<c>Path.Combine(_root, "callboard", "changes", "archive")</c>) and then read the card straight
/// back with <see cref="CardStore.ReadCard"/> — a call that takes any path at all and does not
/// consult <see cref="CardLayout"/> or enumerate anything. That proved only "a Markdown file
/// survives a directory move", a proposition no §4 code could break, because the test was both the
/// only caller that moved the directory <em>and</em> the only statement anywhere of what path it
/// moved it to. Neither <see cref="IndexPopulator"/> nor <see cref="CardIdentityAllocator"/> — the
/// two derived paths that actually need to find an archived card — were exercised at all. This
/// version moves the directory to <see cref="CardLayout.ArchivedChangeDirectory"/> (the single
/// statement of that path now that it exists) and resolves the archived card only through
/// <see cref="IndexPopulator.Populate"/>, the same rebuild <c>index rebuild</c> calls in production.
/// </para>
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
        var cardPath = WriteAndArchiveOneCard(status: "closed");

        var databasePath = IndexPaths.DatabasePath(_root);
        var result = IndexPopulator.Populate(_root, databasePath);

        // The card is genuinely gone from the live directory this build knows about — otherwise
        // "found by the populator" would prove nothing about archive specifically.
        var liveDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Assert.False(Directory.Exists(liveDirectory));

        Assert.Equal(1, result.IndexedCardCount);
        Assert.Equal(2, result.IndexedCommentCount);
        Assert.Empty(result.Failures);

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=false");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT status, file_path FROM cards WHERE id = 'B-0001';";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("closed", reader.GetString(0));
            Assert.Equal(cardPath, reader.GetString(1));
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT comment_id, reply_to, resolved FROM comments WHERE card_id = 'B-0001' ORDER BY ordinal;";
            using var reader = command.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal("C-0001", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(1, reader.GetInt32(2));

            Assert.True(reader.Read());
            Assert.Equal("C-0002", reader.GetString(0));
            Assert.Equal("C-0001", reader.GetString(1));
            Assert.Equal(0, reader.GetInt32(2));

            Assert.False(reader.Read());
        }
    }

    /// <summary>
    /// Supervisor reproduction (a): a counter left behind an identity that exists only in the
    /// archive is exactly as much a recycling risk as one left behind a live card, and must be
    /// caught the same way. Before R1, this scenario produced <c>indexedCardCount: 1</c> (the
    /// unrelated live card only) and an empty <c>identityCounterViolations</c> — the archived
    /// B-0001 was invisible to the check built specifically to prevent recycling it.
    /// </summary>
    [Fact]
    public void VerifyCounters_ReportsAViolation_WhenTheHighestIdentityExistsOnlyInTheArchive()
    {
        WriteAndArchiveOneCard(status: "closed");

        // A block counter left at 0 — as if it had never recorded B-0001's allocation, or had been
        // reset — while a 'block' card numbered 1 exists, archived. Written directly: this test
        // means to construct the discrepancy, not allocate through the normal path.
        var counterPath = Path.Combine(_root, CardLayout.IdentityCounterPath(CardKind.Block).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(counterPath)!);
        File.WriteAllText(counterPath, "0");

        var databasePath = IndexPaths.DatabasePath(_root);
        var result = IndexPopulator.Populate(_root, databasePath);

        Assert.Equal(1, result.IndexedCardCount);

        var violation = Assert.Single(result.IdentityCounterViolations);
        Assert.Equal(CardKind.Block, violation.Kind);
        Assert.Equal(0, violation.CounterValue);
        Assert.Equal(1, violation.ObservedMaxId);
    }

    private string WriteAndArchiveOneCard(string status)
    {
        var liveDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(liveDirectory);
        var cardPath = Path.Combine(liveDirectory, "b-0001.md");

        var frontmatter = new CardFrontmatter(
            "B-0001", CardKind.Block, "Archived block", status, CardOwner.Architect, CardScope.Change, "4", Created, Created);
        AssertWriteSuccess(CardStore.WriteCard(_root, cardPath, new NewCardFile(frontmatter, "Original body."), TimeSpan.FromSeconds(5), ChangeName));

        var firstComment = new CardComment("C-0001", CardOwner.Worker, Created, "First reply.", null, null, null, []);
        var secondComment = new CardComment("C-0002", CardOwner.Reviewer, Created, "Second reply.", "C-0001", CardOwner.Worker, "C-0001", []);
        AssertWriteSuccess(CardStore.AppendComment(_root, cardPath, firstComment, TimeSpan.FromSeconds(5), ChangeName));
        AssertWriteSuccess(CardStore.AppendComment(_root, cardPath, secondComment, TimeSpan.FromSeconds(5), ChangeName));

        // Archive itself is a directory move on callboard/changes/<name>/ and nothing else (the
        // Product Owner's binding decision) — simulated directly rather than through a verb this
        // block does not build, but the *destination* is CardLayout's own — the single statement
        // of that path now that R1 has given it one, not a string this test built by hand.
        var archivedDirectory = Path.TrimEndingDirectorySeparator(
            Path.Combine(_root, CardLayout.ArchivedChangeDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.Combine(_root, CardLayout.ArchiveDirectory.Replace('/', Path.DirectorySeparatorChar)));
        Directory.Move(liveDirectory, archivedDirectory);

        return Path.Combine(archivedDirectory, "b-0001.md");
    }

    private static void AssertWriteSuccess(CardWriteResult result) =>
        result.Match<object?>(
            onSuccess: static _ => null,
            onNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected write success, got NotFound: '{notFound.FilePath}'"),
            onAlreadyExists: alreadyExists => throw new Xunit.Sdk.XunitException($"expected write success, got AlreadyExists: '{alreadyExists.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected write success, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected write success, got Corrupt: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected write success, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: disagreement => throw new Xunit.Sdk.XunitException($"expected write success, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected write success, got HandEnteredDerivedState: '{handEntered.Key}'"));
}
