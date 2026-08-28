using Callboard.Cards;
using Callboard.Index;
using Microsoft.Data.Sqlite;

namespace Callboard.Tests;

/// <summary>
/// 13.8, Part 1 — record-retrieval's "Tool unavailable" scenario, the automatable half: "the record
/// remains readable and the loop can proceed unenforced rather than blocked" (13.8 does not cover
/// the requirement's other scenario, "Card read without the tool" — that is 13.9). Each test proves
/// one of the brief's five properties directly against the read paths (<see
/// cref="DerivedStateAssembler.Build"/>, <see cref="WorkingContextAssembler.Build"/>) rather than
/// asserting them in a comment. The recipe covering what these tests structurally cannot — a world
/// with no binary at all — is in <c>DEVLOG.md</c> under §13, addressed to the Product Owner.
/// </summary>
public sealed class ToolAbsentInvariantTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-tool-absent-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>The index is not a precondition (D4/ADR-0004): the read paths answer identically
    /// whether <c>callboard/.index</c> was never built, was built and deleted, or is present
    /// but stale — because neither read path ever opens it.</summary>
    [Fact]
    public void ReadPaths_AnswerWithNoIndexPresent()
    {
        SeedRealisticRecord();

        var stateWithNoIndex = DerivedStateAssembler.Build(_root);
        var contextWithNoIndex = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        Assert.False(Directory.Exists(Path.GetDirectoryName(IndexPaths.DatabasePath(_root))));
        AssertRealisticRecordVisible(stateWithNoIndex, contextWithNoIndex);

        // Build the index, then delete it outright, and confirm nothing changes.
        IndexPopulator.Populate(_root, IndexPaths.DatabasePath(_root));
        Assert.True(File.Exists(IndexPaths.DatabasePath(_root)));
        Directory.Delete(Path.Combine(_root, "callboard", ".index"), recursive: true);

        var stateAfterDelete = DerivedStateAssembler.Build(_root);
        var contextAfterDelete = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        Assert.Equal(FingerprintState(stateWithNoIndex, _root), FingerprintState(stateAfterDelete, _root));
        Assert.Equal(FingerprintContext(contextWithNoIndex, _root), FingerprintContext(contextAfterDelete, _root));
    }

    /// <summary>A rebuild is a full replace, never an incremental merge (<see
    /// cref="IndexPopulator"/>'s own doc comment): populating into a database that already holds a
    /// stale card's rows produces exactly the rows a scratch database would, for the same record.
    /// If the two could differ, whatever was left over from the stale population would be exerting
    /// authority the record itself no longer grants it.</summary>
    [Fact]
    public void Rebuild_FromScratchOrOverAnExistingDatabase_ProducesIdenticalRows()
    {
        var stalePath = CardPath(CardScope.Change, "b-9999");
        WriteCard(CardScope.Change, "b-9999", GoodCard("B-9999"));
        var scratchDatabasePath = Path.Combine(_root, "scratch.db");
        IndexPopulator.Populate(_root, scratchDatabasePath);

        var stalePopulatedPath = Path.Combine(_root, "stale.db");
        File.Copy(scratchDatabasePath, stalePopulatedPath);

        File.Delete(stalePath);
        WriteCard(CardScope.Change, "b-0001", GoodCard("B-0001"));

        var freshPath = Path.Combine(_root, "fresh.db");
        IndexPopulator.Populate(_root, freshPath);
        IndexPopulator.Populate(_root, stalePopulatedPath);

        Assert.Equal(DumpCardRows(freshPath), DumpCardRows(stalePopulatedPath));
    }

    /// <summary>Nothing the tool needs to be <em>correct</em> lives outside what a fresh clone would
    /// bring. Simulates a clone by copying every file except the ones <c>.gitignore</c> excludes
    /// (<c>callboard/.index/</c>, <c>*.lock</c>, <c>*.tmp-*</c>) and confirms both read paths — and
    /// identity allocation, which the index/lock/tmp exclusions do <em>not</em> cover because the
    /// counter file is committed — produce the same result as the original tree.</summary>
    [Fact]
    public void FreshClone_OmittingOnlyGitignoredPaths_ReadsIdentically()
    {
        SeedRealisticRecord();
        var allocated = CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5));
        var allocatedId = Assert.IsType<CardIdentityAllocationResult.Allocated>(allocated).Id;
        IndexPopulator.Populate(_root, IndexPaths.DatabasePath(_root));
        File.WriteAllText(Path.Combine(_root, "stray.lock"), "not a real pid");

        var originalState = DerivedStateAssembler.Build(_root);
        var originalContext = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        var clonePath = Path.Combine(Path.GetTempPath(), "callboard-clone-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyExceptGitignored(_root, clonePath);

            Assert.False(Directory.Exists(Path.Combine(clonePath, "callboard", ".index")));
            Assert.True(File.Exists(Path.Combine(clonePath, "callboard", "identities", "block.count")));

            var cloneState = DerivedStateAssembler.Build(clonePath);
            var cloneContext = WorkingContextAssembler.Build(clonePath, CardOwner.Worker);

            Assert.Equal(FingerprintState(originalState, _root), FingerprintState(cloneState, clonePath));
            Assert.Equal(FingerprintContext(originalContext, _root), FingerprintContext(cloneContext, clonePath));

            var cloneAllocated = CardIdentityAllocator.Allocate(clonePath, CardKind.Block, TimeSpan.FromSeconds(5));
            var cloneAllocatedId = Assert.IsType<CardIdentityAllocationResult.Allocated>(cloneAllocated).Id;
            Assert.NotEqual(allocatedId, cloneAllocatedId);
        }
        finally
        {
            if (Directory.Exists(clonePath))
            {
                Directory.Delete(clonePath, recursive: true);
            }
        }
    }

    /// <summary>Reads take no lock (ADR-0004): a lock file left behind by a process that no longer
    /// exists — the exact shape <see cref="CardLock"/>'s stale-holder handling names as the expected
    /// case, not an exotic one — must not make the card it guards unreadable.</summary>
    [Fact]
    public void StaleLock_DoesNotBlockAReadOfTheCardItGuards()
    {
        WriteCard(CardScope.Change, "b-0001", GoodCard("B-0001"));
        var cardPath = CardPath(CardScope.Change, "b-0001");
        File.WriteAllText(cardPath + ".lock", "999999999");

        var result = CardStore.ReadCard(cardPath);

        var success = Assert.IsType<CardFileParseResult.Success>(result);
        Assert.Equal("B-0001", success.Card.Frontmatter.Id);

        var state = DerivedStateAssembler.Build(_root);
        Assert.Empty(state.Unreadable);
    }

    /// <summary>The automatable half of "the loop proceeds unenforced": a card composed by hand, in
    /// a text editor, with no call into <see cref="CardStore"/> and no lock ever taken, parses and
    /// is indistinguishable from a tool-written twin to every read path this build has.</summary>
    [Fact]
    public void HandWrittenCard_IsIndistinguishableFromAToolWrittenTwin()
    {
        var toolWrittenFrontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "Which retry policy?", "open", CardOwner.Architect, CardScope.Change, "13", Created, Created);
        WriteCard(CardScope.Change, "q-0001", new NewCardFile(toolWrittenFrontmatter, "Body of the question."));

        var handWrittenFrontmatter = toolWrittenFrontmatter with { Id = "Q-0002" };
        var handWrittenCard = new CardFile(handWrittenFrontmatter, "Body of the question.", [], []);
        var handTypedText = CardFileWriter.Serialize(handWrittenCard);
        var handWrittenPath = CardPath(CardScope.Change, "q-0002");
        File.WriteAllText(handWrittenPath, handTypedText); // no CardStore, no CardLock — exactly what an editor save does

        var toolWrittenParsed = CardStore.ReadCard(CardPath(CardScope.Change, "q-0001"));
        var handWrittenParsed = CardStore.ReadCard(handWrittenPath);

        var toolWrittenSuccess = Assert.IsType<CardFileParseResult.Success>(toolWrittenParsed);
        var handWrittenSuccess = Assert.IsType<CardFileParseResult.Success>(handWrittenParsed);

        // Field by field, not a whole-record Assert.Equal: CardFile's list-typed fields (Comments,
        // UnknownFrontmatterFields, ...) use List<T>'s reference equality under record-generated
        // Equals, so two independently-parsed empty lists compare unequal even with identical
        // content — the same reason every other test in this suite compares fields, not records.
        Assert.Equal(toolWrittenFrontmatter with { Id = "irrelevant" }, handWrittenSuccess.Card.Frontmatter with { Id = "irrelevant" });
        Assert.Equal(toolWrittenSuccess.Card.Body, handWrittenSuccess.Card.Body);
        Assert.Empty(handWrittenSuccess.Card.Comments);
        Assert.Empty(handWrittenSuccess.Card.UnknownFrontmatterFields);

        var state = DerivedStateAssembler.Build(_root);
        Assert.Empty(state.Unreadable);
        Assert.Contains(state.OpenQuestions, q => q.Card.Frontmatter.Id == "Q-0001");
        Assert.Contains(state.OpenQuestions, q => q.Card.Frontmatter.Id == "Q-0002");
    }

    private void SeedRealisticRecord()
    {
        var sectionFrontmatter = new CardFrontmatter(
            "S-0001", CardKind.Section, "Section 13", "open", CardOwner.Architect, CardScope.Change, "13", Created, Created);
        WriteCard(CardScope.Change, "s-0001", new NewCardFile(sectionFrontmatter, "Section body."));

        var obligationFrontmatter = new CardFrontmatter(
            "O-0001", CardKind.Obligation, "Write the recipe", "open", CardOwner.Worker, CardScope.Change, "13", Created, Created);
        WriteCard(
            CardScope.Change,
            "o-0001",
            new NewCardFile(obligationFrontmatter, "Owed body.", RegisterFields: new RegisterCardFields(null, null, null, null, OwedBy: "S-0001")));

        var questionFrontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "Which lock timeout?", "open", CardOwner.ProductOwner, CardScope.Change, "13", Created, Created);
        WriteCard(CardScope.Change, "q-0001", new NewCardFile(questionFrontmatter, "Question body."));

        WriteCard(CardScope.Change, "b-9000", GoodCard("B-9000"));
    }

    private static void AssertRealisticRecordVisible(DerivedState state, WorkingContext context)
    {
        Assert.Empty(state.Unreadable);
        Assert.Single(state.OpenSections);
        Assert.Single(state.LiveObligations);
        Assert.Single(state.OpenQuestions);
        Assert.Empty(context.Unreadable);
    }

    /// <summary>A deep, content-based digest of a <see cref="DerivedState"/> — not a whole-record
    /// <c>Assert.Equal</c>, for the same List-reference-equality reason <see
    /// cref="HandWrittenCard_IsIndistinguishableFromAToolWrittenTwin"/> avoids one.</summary>
    private static string FingerprintState(DerivedState state, string root) => string.Join(
        '\n',
        "sections:" + string.Join(',', state.OpenSections.Select(s => Relative(s.FilePath, root) + "=" + s.Card.Frontmatter.Id).OrderBy(s => s, StringComparer.Ordinal)),
        "tasks:" + string.Join(',', state.TaskCompletion.Select(t => $"{t.ChangeName}:{t.TasksFileFound}:{t.Ticked}/{t.Total}").OrderBy(t => t, StringComparer.Ordinal)),
        "obligations:" + string.Join(',', state.LiveObligations.Select(o => Relative(o.FilePath, root) + "=" + o.Card.Frontmatter.Id + "->" + o.OwedBySectionId).OrderBy(o => o, StringComparer.Ordinal)),
        "questions:" + string.Join(',', state.OpenQuestions.Select(q => Relative(q.FilePath, root) + "=" + q.Card.Frontmatter.Id + "->" + q.OwesAnswer.ToWireString()).OrderBy(q => q, StringComparer.Ordinal)),
        "blocked:" + string.Join(',', state.BlockedCards.Select(b => Relative(b.FilePath, root) + "=" + b.Card.Frontmatter.Id + ":" + b.Halted).OrderBy(b => b, StringComparer.Ordinal)),
        "unreadable:" + string.Join(',', state.Unreadable.Select(u => Relative(u.FilePath, root)).OrderBy(u => u, StringComparer.Ordinal)));

    /// <summary>Same purpose as <see cref="FingerprintState"/>, for <see cref="WorkingContext"/>.</summary>
    private static string FingerprintContext(WorkingContext context, string root) => string.Join(
        '\n',
        "rulesAndHazards:" + string.Join(',', context.LiveRulesAndHazards.Select(r => Relative(r.FilePath, root) + "=" + r.Card.Frontmatter.Id).OrderBy(r => r, StringComparer.Ordinal)),
        "queue:" + string.Join(',', context.Queue.Select(q => Relative(q.FilePath, root) + "=" + q.Card.Frontmatter.Id).OrderBy(q => q, StringComparer.Ordinal)),
        "top:" + (context.TopItem is { } top ? top.Card.Frontmatter.Id : "(none)"),
        "unreadable:" + string.Join(',', context.Unreadable.Select(u => Relative(u.FilePath, root)).OrderBy(u => u, StringComparer.Ordinal)));

    private static string Relative(string filePath, string root) => Path.GetRelativePath(root, filePath);

    private static IReadOnlyList<string> DumpCardRows(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, kind, title, status, owner, scope, section, created, updated, file_path FROM cards ORDER BY id;";
        using var reader = command.ExecuteReader();

        var rows = new List<string>();
        while (reader.Read())
        {
            var fields = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                fields[i] = reader.GetValue(i)?.ToString() ?? string.Empty;
            }

            rows.Add(string.Join('|', fields));
        }

        return rows;
    }

    /// <summary>Mirrors what <c>git clone</c> actually brings: every committed file, minus what
    /// <c>.gitignore</c> excludes for callboard (<c>callboard/.index/</c>, <c>*.db*</c>,
    /// <c>callboard/**/*.lock</c>, <c>callboard/**/*.tmp-*</c>).</summary>
    private static void CopyExceptGitignored(string sourceRoot, string destinationRoot)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, sourceFile);
            var relativeSlash = relative.Replace(Path.DirectorySeparatorChar, '/');

            if (relativeSlash.StartsWith("callboard/.index/", StringComparison.Ordinal)
                || relativeSlash.EndsWith(".lock", StringComparison.Ordinal)
                || relativeSlash.Contains(".tmp-", StringComparison.Ordinal)
                || relativeSlash.EndsWith(".db", StringComparison.Ordinal)
                || relativeSlash.EndsWith(".db-shm", StringComparison.Ordinal)
                || relativeSlash.EndsWith(".db-wal", StringComparison.Ordinal))
            {
                continue;
            }

            var destinationFile = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile);
        }
    }

    private static NewCardFile GoodCard(string id) =>
        new(
            new CardFrontmatter(id, CardKind.Block, "Title " + id, "drafting", CardOwner.Worker, CardScope.Change, "13", Created, Created),
            "Body.");

    private string CardPath(CardScope scope, string fileStem)
    {
        var directory = scope.Match(
            onSection: () => CardLayout.ChangesDirectory(ChangeName),
            onChange: () => CardLayout.ChangesDirectory(ChangeName),
            onCapability: () => CardLayout.DecisionsDirectory,
            onRepository: () => CardLayout.RegisterDirectory);

        return Path.Combine(_root, directory.Replace('/', Path.DirectorySeparatorChar), fileStem + ".md");
    }

    private void WriteCard(CardScope scope, string fileStem, NewCardFile card)
    {
        var path = CardPath(scope, fileStem);
        var changeName = scope.Match(
            onSection: () => (string?)ChangeName,
            onChange: () => (string?)ChangeName,
            onCapability: () => null,
            onRepository: () => null);

        var result = CardStore.WriteCard(_root, path, card, TimeSpan.FromSeconds(5), changeName);
        result.Match<object?>(
            onSuccess: static _ => null,
            onNotFound: notFound => throw new Xunit.Sdk.XunitException($"setup write failed: no card at '{notFound.FilePath}'"),
            onAlreadyExists: alreadyExists => throw new Xunit.Sdk.XunitException($"setup write failed: already exists at '{alreadyExists.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"setup write failed: {layoutMismatch.Reason}"),
            onCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"setup write failed: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"setup write failed: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: disagreement => throw new Xunit.Sdk.XunitException($"setup write failed: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"setup write failed: hand-entered derived-state field '{handEntered.Key}'"));
    }
}
