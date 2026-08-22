using Callboard.Cards;
using Callboard.Index;
using Microsoft.Data.Sqlite;

namespace Callboard.Tests;

/// <summary>
/// 3.4–3.6 — the three index invariants design.md names explicitly: rebuild is deterministic,
/// the record governs over the index, and the index is never a lock and never load-bearing for
/// the record. Each test in this file was, during development, run once against a deliberately
/// broken implementation and confirmed to fail before being run against the real one — see the
/// worker's DEVLOG post for what was broken and what happened. A test that has never failed is a
/// claim, not evidence.
///
/// <para>
/// 3.5's ruling (architect, DEVLOG): §3 has no query path, so "the record governs" is demonstrated
/// here only as far as the index's own construction proves it — the index has exactly one input,
/// so nothing but the record can change what a rebuild produces. Whether a mixed index/record
/// disagreement is resolved correctly by whatever eventually reads the index is §10's property,
/// not this section's.
/// </para>
/// </summary>
public sealed class IndexInvariantTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 8, 21, 10, 30, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-index-invariant-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // 3.4 — destroying the index and rebuilding produces identical answers.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Rebuild_ProducesIdenticalAnswers_AcrossThreeConsecutiveDestroyAndRebuildCycles()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        BuildCorpus();

        IndexPopulator.Populate(_root, databasePath);
        var firstDump = DumpDatabase(databasePath);
        Assert.NotEmpty(firstDump.Cards);
        Assert.NotEmpty(firstDump.Comments);

        File.Delete(databasePath);
        IndexPopulator.Populate(_root, databasePath);
        var secondDump = DumpDatabase(databasePath);

        File.Delete(databasePath);
        IndexPopulator.Populate(_root, databasePath);
        var thirdDump = DumpDatabase(databasePath);

        Assert.Equal(firstDump.Cards, secondDump.Cards);
        Assert.Equal(firstDump.Comments, secondDump.Comments);
        Assert.Equal(firstDump.Cards, thirdDump.Cards);
        Assert.Equal(firstDump.Comments, thirdDump.Comments);
    }

    // ---------------------------------------------------------------------------------------
    // 3.5 — the record governs, demonstrated the way the architect's ruling scoped it: the
    // index has exactly one input, so nothing but the record can change what a rebuild produces.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Rebuild_DiscardsAnyHandMutationMadeDirectlyToTheDatabase_TheRecordIsUntouched()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        WriteCard("b-0001", GoodCard("B-0001", "Original title"));
        IndexPopulator.Populate(_root, databasePath);
        var recordTruth = DumpDatabase(databasePath);

        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=false"))
        {
            connection.Open();

            Execute(connection, "UPDATE cards SET title = 'MUTATED', status = 'mutated', owner = 'reviewer' WHERE id = 'B-0001';");
            Execute(connection, "DELETE FROM comments;");
            Execute(
                connection,
                """
                INSERT INTO cards (id, kind, title, status, owner, scope, section, created, updated, file_path)
                VALUES ('B-9999', 'block', 'Fabricated', 'open', 'worker', 'change', '3', '2026-08-20T09:00:00+00:00', '2026-08-20T09:00:00+00:00', 'nowhere.md');
                """);
        }

        // The mutated database, not yet rebuilt, must actually disagree with the record — or this
        // test would not be testing anything.
        using (var mutatedConnection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=false"))
        {
            mutatedConnection.Open();
            using var command = mutatedConnection.CreateCommand();
            command.CommandText = "SELECT title FROM cards WHERE id = 'B-0001';";
            Assert.Equal("MUTATED", (string)command.ExecuteScalar()!);
        }

        IndexPopulator.Populate(_root, databasePath);
        var rebuilt = DumpDatabase(databasePath);

        Assert.Equal(recordTruth.Cards, rebuilt.Cards);
        Assert.Equal(recordTruth.Comments, rebuilt.Comments);
        Assert.DoesNotContain(rebuilt.Cards, card => card.Contains("B-9999", StringComparison.Ordinal));
    }

    [Fact]
    public void Rebuild_ReflectsAFileMutation_EvenWhenTheIndexWasStale()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        var path = WriteCard("b-0002", GoodCard("B-0002", "Before edit"));
        IndexPopulator.Populate(_root, databasePath);

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=false"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT title FROM cards WHERE id = 'B-0002';";
            Assert.Equal("Before edit", (string)command.ExecuteScalar()!);
        }

        // Mutate the card file directly (File.WriteAllText, not CardStore.WriteCard — WriteCard
        // is create-only as of DEVLOG §4 block C review round 1), leaving the index stale — no
        // Populate call in between. This is genuinely the scenario the test name describes: an
        // edit made outside the tool (ADR-0003, "legible without the tool" — the record is a file
        // humans are expected to hand-edit), not a second call through the production API.
        var mutated = GoodCard("B-0002", "After edit");
        File.WriteAllText(path, CardFileWriter.Serialize(new CardFile(mutated.Frontmatter, mutated.Body, [], [])));

        IndexPopulator.Populate(_root, databasePath);

        using var readConnection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=false");
        readConnection.Open();
        using var readCommand = readConnection.CreateCommand();
        readCommand.CommandText = "SELECT title FROM cards WHERE id = 'B-0002';";
        Assert.Equal("After edit", (string)readCommand.ExecuteScalar()!);
    }

    [Fact]
    public void Rebuild_IsAFullReplace_ACardDeletedFromTheRecordDisappearsFromTheIndex()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        var survivorPath = WriteCard("b-0003", GoodCard("B-0003", "Survivor"));
        var deletedPath = WriteCard("b-0004", GoodCard("B-0004", "About to be deleted"));
        IndexPopulator.Populate(_root, databasePath);

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=false"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM cards WHERE id = 'B-0004';";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }

        File.Delete(deletedPath);
        Assert.True(File.Exists(survivorPath));

        IndexPopulator.Populate(_root, databasePath);

        using var readConnection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=false");
        readConnection.Open();
        using var command2 = readConnection.CreateCommand();
        command2.CommandText = "SELECT id FROM cards ORDER BY id;";
        using var reader = command2.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        Assert.Equal(["B-0003"], ids);
    }

    // ---------------------------------------------------------------------------------------
    // 3.6 — the index is never a lock, and deleting it mid-session loses no data.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void DeletingTheIndex_BetweenTwoCardWrites_AndWhileACardLockIsHeld_LosesNoDataAndRebuildRecovers()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        var firstPath = WriteCard("b-0005", GoodCard("B-0005", "First"));
        IndexPopulator.Populate(_root, databasePath);
        Assert.True(File.Exists(databasePath));

        // Hold a CardLock on an unrelated card while the index is deleted, so a real write is
        // genuinely in flight (mid-session) when the index vanishes underneath it.
        var lockTarget = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar), "held.md");
        Directory.CreateDirectory(Path.GetDirectoryName(lockTarget)!);
        var acquireResult = CardLock.Acquire(lockTarget, TimeSpan.FromSeconds(5));
        var held = acquireResult.Match(
            onAcquired: acquired => acquired.Lock,
            onTimedOut: timedOut => throw new Xunit.Sdk.XunitException($"expected to acquire the lock, timed out: {timedOut.Message}"));

        try
        {
            File.Delete(databasePath);
            Assert.False(File.Exists(databasePath));

            var secondPath = WriteCard("b-0006", GoodCard("B-0006", "Second, written with the index gone"));
            Assert.True(File.Exists(secondPath));
        }
        finally
        {
            held.Dispose();
        }

        Assert.True(File.Exists(firstPath));
        var firstRead = CardStore.ReadCard(firstPath);
        AssertParseSuccess(firstRead);

        var result = IndexPopulator.Populate(_root, databasePath);

        Assert.Equal(2, result.IndexedCardCount);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void CardWrites_SucceedWithTheIndexAbsentEntirely_AndNeverCreateOne()
    {
        var databasePath = IndexPaths.DatabasePath(_root);
        Assert.False(File.Exists(databasePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(databasePath)));

        var path = WriteCard("b-0007", GoodCard("B-0007", "Written with no index anywhere"));

        // A second write path (TransferOwnership, not a second WriteCard — WriteCard is
        // create-only as of DEVLOG §4 block C review round 1) exercising the same "no index
        // anywhere" claim for a read-modify-write, not just a fresh create.
        AssertWriteSuccess(CardStore.TransferOwnership(_root, path, CardOwner.Reviewer, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName));

        var appendResult = CardStore.AppendComment(
            _root,
            path,
            new CardComment("C-0001", CardOwner.Worker, Created, "A comment.", null, null, null, []),
            TimeSpan.FromSeconds(5),
            ChangeName);
        AssertWriteSuccess(appendResult);

        var read = CardStore.ReadCard(path);
        var card = AssertParseSuccess(read);
        Assert.Single(card.Comments);

        Assert.False(File.Exists(databasePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(databasePath)));
    }

    [Fact]
    public void ConcurrentCardWrites_BehaveIdentically_WhetherTheIndexExistsIsAbsentOrIsDeletedUnderneath()
    {
        AssertConcurrentAppendsSurvive(indexState: "absent");
        AssertConcurrentAppendsSurvive(indexState: "present");
        AssertConcurrentAppendsSurvive(indexState: "deleted-mid-run");
    }

    private void AssertConcurrentAppendsSurvive(string indexState)
    {
        var scenarioRoot = Path.Combine(_root, "scenario-" + indexState);
        var directory = Path.Combine(scenarioRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "stress.md");
        var frontmatter = new CardFrontmatter(
            "B-0800", CardKind.Block, "Concurrent", "open", CardOwner.Worker, CardScope.Change, "3", Created, Created);
        AssertWriteSuccess(CardStore.WriteCard(scenarioRoot, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), ChangeName));

        var databasePath = IndexPaths.DatabasePath(scenarioRoot);
        if (indexState is "present" or "deleted-mid-run")
        {
            IndexPopulator.Populate(scenarioRoot, databasePath);
            Assert.True(File.Exists(databasePath));
        }

        const int appendCount = 20;
        var comments = Enumerable.Range(0, appendCount)
            .Select(i => new CardComment($"C-{i:D3}", CardOwner.Worker, Created, $"Comment {i}.", null, null, null, []))
            .ToList();

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var threads = comments
            .Select(comment => new Thread(() =>
            {
                try
                {
                    AssertWriteSuccess(CardStore.AppendComment(scenarioRoot, path, comment, TimeSpan.FromSeconds(30), ChangeName));
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }))
            .ToList();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        if (indexState is "deleted-mid-run")
        {
            File.Delete(databasePath);
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        if (!exceptions.IsEmpty)
        {
            throw new AggregateException($"scenario '{indexState}'", exceptions);
        }

        var final = AssertParseSuccess(CardStore.ReadCard(path));

        Assert.Equal(appendCount, final.Comments.Count);
        Assert.Equal(
            comments.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal),
            final.Comments.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(final.Comments.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count(), final.Comments.Count);
    }

    // ---------------------------------------------------------------------------------------
    // Corpus and dump helpers.
    // ---------------------------------------------------------------------------------------

    private const string ChangeName = "establish-callboard";
    private const string OtherChangeName = "another-change";

    private void BuildCorpus()
    {
        WriteRegisterCard("r-0001", "R-0001", CardKind.Rule, CardOwner.Architect);
        WriteRegisterCard("r-0002", "R-0002", CardKind.Obligation, CardOwner.ProductOwner);
        WriteDecisionCard("d-0001", "D-0001", CardKind.Decision, CardOwner.Architect);

        var comment1 = new CardComment("C-0001", CardOwner.Worker, Created, "First.", null, CardOwner.Architect, null, []);
        var reply = new CardComment("C-0002", CardOwner.Architect, Updated, "Reply.", "C-0001", null, "C-0001", []);
        WriteCardInChange(ChangeName, "b-0010", "B-0010", CardKind.Block, CardOwner.Worker, CardScope.Change, [comment1, reply]);

        var findingComment = new CardComment("C-0003", CardOwner.Reviewer, Created, "Finding narrative.", null, CardOwner.Worker, null, []);
        WriteCardInChange(ChangeName, "b-0011", "B-0011", CardKind.Finding, CardOwner.Reviewer, CardScope.Section, [findingComment]);

        WriteCardInChange(OtherChangeName, "h-0001", "H-0001", CardKind.Hazard, CardOwner.Supervisor, CardScope.Change, []);
        WriteCardInChange(OtherChangeName, "q-0001", "Q-0001", CardKind.Question, CardOwner.Worker, CardScope.Change, []);
    }

    private void WriteRegisterCard(string fileStem, string id, CardKind kind, CardOwner owner)
    {
        var path = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar), fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, kind, "Title " + id, "open", owner, CardScope.Repository, string.Empty, Created, Updated);
        AssertWriteSuccess(CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body for " + id + "."), TimeSpan.FromSeconds(5)));
    }

    private void WriteDecisionCard(string fileStem, string id, CardKind kind, CardOwner owner)
    {
        var path = Path.Combine(_root, CardLayout.DecisionsDirectory.Replace('/', Path.DirectorySeparatorChar), fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, kind, "Title " + id, "open", owner, CardScope.Capability, string.Empty, Created, Updated);
        AssertWriteSuccess(CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body for " + id + "."), TimeSpan.FromSeconds(5)));
    }

    private void WriteCardInChange(string changeName, string fileStem, string id, CardKind kind, CardOwner owner, CardScope scope, IReadOnlyList<CardComment> comments)
    {
        var path = Path.Combine(_root, CardLayout.ChangesDirectory(changeName).Replace('/', Path.DirectorySeparatorChar), fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, kind, "Title " + id, "open", owner, scope, "3", Created, Updated);
        AssertWriteSuccess(CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body for " + id + "."), TimeSpan.FromSeconds(5), changeName));

        foreach (var comment in comments)
        {
            AssertWriteSuccess(CardStore.AppendComment(_root, path, comment, TimeSpan.FromSeconds(5), changeName));
        }
    }

    private string WriteCard(string fileStem, NewCardFile card)
    {
        var path = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar), fileStem + ".md");
        AssertWriteSuccess(CardStore.WriteCard(_root, path, card, TimeSpan.FromSeconds(5), ChangeName));
        return path;
    }

    private static NewCardFile GoodCard(string id, string title) =>
        new(
            new CardFrontmatter(id, CardKind.Block, title, "open", CardOwner.Worker, CardScope.Change, "3", Created, Created),
            "Body.");

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A deterministic, canonical dump of every row of every table — every column, ordered by
    /// primary key — so two databases can be compared as answers rather than as bytes (3.4: "not
    /// bytes... compare the derived state").
    /// </summary>
    private static (IReadOnlyList<string> Cards, IReadOnlyList<string> Comments) DumpDatabase(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=false");
        connection.Open();

        var cards = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT id, kind, title, status, owner, scope, section, created, updated, file_path FROM cards ORDER BY id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var fields = Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue);
                cards.Add(string.Join("|", fields));
            }
        }

        var comments = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT card_id, comment_id, ordinal, author, timestamp, reply_to, addressed_to, resolved FROM comments ORDER BY card_id, ordinal;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var fields = Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "<null>" : reader.GetValue(i));
                comments.Add(string.Join("|", fields));
            }
        }

        return (cards, comments);
    }

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
