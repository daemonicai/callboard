using System.Linq;
using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §7 block B — <see cref="CardIdentityResolver"/>: "which card carries this id?", answered by
/// reading the record across every directory <see cref="CardLayout.ResolveRecordDirectories"/>
/// names. <see cref="CommandDispatcherFindingRecordTests"/> and
/// <see cref="CommandDispatcherFindingStatusTests"/> cover this at the CLI boundary (validated
/// <c>--section</c>, the rewired finding→section link); this file proves the resolver itself,
/// directly, including the one directory neither of those exercises: <c>changes/archive/</c>.
/// </summary>
public sealed class CardIdentityResolverTests : IDisposable
{
    private static readonly DateTimeOffset Recorded = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-card-identity-resolver-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void NoCardsAnywhere_NotFound()
    {
        var resolution = CardIdentityResolver.Resolve(_root, "S-0001");

        resolution.Match<object?>(
            onFound: (filePath, card) => throw Fail($"expected NotFound, got Found('{filePath}')"),
            onNotFound: id =>
            {
                Assert.Equal("S-0001", id);
                return null;
            },
            onDuplicate: (id, filePaths) => throw Fail($"expected NotFound, got Duplicate('{id}')"),
            onCorrupt: (id, files) => throw Fail($"expected NotFound, got Corrupt('{id}')"),
            onUnreadable: (id, files) => throw Fail($"expected NotFound, got Unreadable('{id}')"));
    }

    [Fact]
    public void CardInRegister_Found()
    {
        var path = WriteCard(CardLayout.RegisterDirectory, "r-0001.md", "R-0001", CardKind.Rule, CardScope.Repository);

        AssertFound(CardIdentityResolver.Resolve(_root, "R-0001"), path);
    }

    [Fact]
    public void CardInDecisions_Found()
    {
        var path = WriteCard(CardLayout.DecisionsDirectory, "d-0001.md", "D-0001", CardKind.Decision, CardScope.Capability);

        AssertFound(CardIdentityResolver.Resolve(_root, "D-0001"), path);
    }

    [Fact]
    public void CardInALiveChange_Found()
    {
        var path = WriteCard(CardLayout.ChangesDirectory("establish-callboard"), "s-0001.md", "S-0001", CardKind.Section, CardScope.Change);

        AssertFound(CardIdentityResolver.Resolve(_root, "S-0001"), path);
    }

    // Archive is a directory move and nothing else (Product Owner ruling): a card that survived
    // archive must resolve exactly as it did while its change was live. Writing directly under
    // changes/archive/<name>/ rather than calling an archive verb — none exists yet (§7 block D) —
    // is deliberate: this proves the resolver's own search of that directory, independent of how a
    // card gets there.
    [Fact]
    public void CardInAnArchivedChange_StillResolves()
    {
        var path = WriteCard(CardLayout.ArchivedChangeDirectory("establish-callboard"), "s-0002.md", "S-0002", CardKind.Section, CardScope.Change);

        AssertFound(CardIdentityResolver.Resolve(_root, "S-0002"), path);
    }

    // The defect §6 fail-closed on twice, now at the resolver's own layer: two files claiming the
    // same id refuse, never "whichever sorted first". Only reachable by hand-editing — no verb
    // through CardIdentityAllocator can produce a repeat.
    [Fact]
    public void TwoFilesClaimTheSameId_Duplicate_NeverPicksOne()
    {
        var pathA = WriteCard(CardLayout.RegisterDirectory, "r-0001.md", "R-0001", CardKind.Rule, CardScope.Repository);
        var pathB = WriteCard(CardLayout.ChangesDirectory("establish-callboard"), "r-0001-dup.md", "R-0001", CardKind.Rule, CardScope.Change);

        var resolution = CardIdentityResolver.Resolve(_root, "R-0001");

        resolution.Match<object?>(
            onFound: (filePath, card) => throw Fail($"expected Duplicate, got Found('{filePath}')"),
            onNotFound: id => throw Fail($"expected Duplicate, got NotFound('{id}')"),
            onDuplicate: (id, filePaths) =>
            {
                Assert.Equal("R-0001", id);
                Assert.Contains(pathA, filePaths);
                Assert.Contains(pathB, filePaths);
                Assert.Equal(2, filePaths.Count);
                return null;
            },
            onCorrupt: (id, files) => throw Fail($"expected Duplicate, got Corrupt('{id}')"),
            onUnreadable: (id, files) => throw Fail($"expected Duplicate, got Unreadable('{id}')"));
    }

    // §6 remediation B3, re-applied at the resolver's own layer: zero matches is not the same as
    // zero candidates. A file elsewhere in the record could not be read, so the requested id might
    // live in it — the resolver must not report NotFound.
    [Fact]
    public void NoMatch_ButAFileElsewhereCouldNotBeRead_Unreadable_NeverNotFound()
    {
        Directory.CreateDirectory(Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar)));
        var garbagePath = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar), "r-broken.md");
        File.WriteAllText(garbagePath, "not a card at all, no frontmatter fence", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var resolution = CardIdentityResolver.Resolve(_root, "R-9999");

        resolution.Match<object?>(
            onFound: (filePath, card) => throw Fail($"expected Unreadable, got Found('{filePath}')"),
            onNotFound: id => throw Fail($"expected Unreadable, got NotFound('{id}')"),
            onDuplicate: (id, filePaths) => throw Fail($"expected Unreadable, got Duplicate('{id}')"),
            onCorrupt: (id, files) => throw Fail($"expected Unreadable, got Corrupt('{id}')"),
            onUnreadable: (id, files) =>
            {
                Assert.Equal("R-9999", id);
                Assert.Contains(garbagePath, files.Select(static file => file.FilePath));
                return null;
            });
    }

    // A confirmed match takes precedence over an unrelated read failure elsewhere — the resolver's
    // "zero matches" gate on unreadable candidates is never reached once a match is confirmed.
    [Fact]
    public void MatchFound_UnrelatedUnreadableFileElsewhere_StillFound()
    {
        var path = WriteCard(CardLayout.RegisterDirectory, "r-0001.md", "R-0001", CardKind.Rule, CardScope.Repository);
        var garbagePath = Path.Combine(_root, CardLayout.DecisionsDirectory.Replace('/', Path.DirectorySeparatorChar), "d-broken.md");
        Directory.CreateDirectory(Path.GetDirectoryName(garbagePath)!);
        File.WriteAllText(garbagePath, "garbage", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        AssertFound(CardIdentityResolver.Resolve(_root, "R-0001"), path);
    }

    // §13.6 — a file that declares the requested id in its own leading frontmatter fence, but
    // fails to parse for some other reason, answers Corrupt rather than Unreadable: the file
    // claiming the id is sitting right there, so the honest remedy is "open it", not "hunt for a
    // typo elsewhere".
    [Fact]
    public void NoParsedMatch_ButACorruptFileDeclaresTheId_Corrupt_NeverUnreadable()
    {
        var corruptPath = WriteCorruptCard(CardLayout.RegisterDirectory, "r-corrupt.md", "R-0007");

        var resolution = CardIdentityResolver.Resolve(_root, "R-0007");

        resolution.Match<object?>(
            onFound: (filePath, card) => throw Fail($"expected Corrupt, got Found('{filePath}')"),
            onNotFound: id => throw Fail($"expected Corrupt, got NotFound('{id}')"),
            onDuplicate: (id, filePaths) => throw Fail($"expected Corrupt, got Duplicate('{id}')"),
            onCorrupt: (id, claimants) =>
            {
                Assert.Equal("R-0007", id);
                var claimant = Assert.Single(claimants);
                Assert.Equal(corruptPath, claimant.FilePath);
                Assert.Contains("unrecognised status", claimant.Reason, StringComparison.Ordinal);
                return null;
            },
            onUnreadable: (id, files) => throw Fail($"expected Corrupt, got Unreadable('{id}')"));
    }

    // A corrupt file elsewhere that does not declare the requested id must not be attributed to
    // it — this stays the honest Unreadable answer, not a manufactured Corrupt.
    [Fact]
    public void NoParsedMatch_CorruptFileDeclaresADifferentId_Unreadable_NeverCorrupt()
    {
        WriteCorruptCard(CardLayout.RegisterDirectory, "r-corrupt.md", "R-9999");

        var resolution = CardIdentityResolver.Resolve(_root, "R-0007");

        resolution.Match<object?>(
            onFound: (filePath, card) => throw Fail($"expected Unreadable, got Found('{filePath}')"),
            onNotFound: id => throw Fail($"expected Unreadable, got NotFound('{id}')"),
            onDuplicate: (id, filePaths) => throw Fail($"expected Unreadable, got Duplicate('{id}')"),
            onCorrupt: (id, claimants) => throw Fail($"expected Unreadable, got Corrupt('{id}')"),
            onUnreadable: (id, files) =>
            {
                Assert.Equal("R-0007", id);
                Assert.Single(files);
                return null;
            });
    }

    // A parsed match wins over a corrupt file also claiming the id — recovery is evidence, never a
    // second record (§13.6's precedence rule).
    [Fact]
    public void ParsedMatch_TakesPrecedenceOverACorruptFileAlsoClaimingTheId()
    {
        var path = WriteCard(CardLayout.RegisterDirectory, "r-0001.md", "R-0001", CardKind.Rule, CardScope.Repository);
        WriteCorruptCard(CardLayout.DecisionsDirectory, "r-corrupt.md", "R-0001");

        AssertFound(CardIdentityResolver.Resolve(_root, "R-0001"), path);
    }

    // A file with no intact frontmatter fence has nothing to recover — it must never be attributed
    // to an id it merely mentions in unstructured text, and stays Unreadable.
    [Fact]
    public void NoFrontmatterFenceAtAll_NeverAttributed_StaysUnreadable()
    {
        Directory.CreateDirectory(Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar)));
        var path = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar), "r-no-fence.md");
        File.WriteAllText(path, "id: R-0001\nnot a real card file", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var resolution = CardIdentityResolver.Resolve(_root, "R-0001");

        resolution.Match<object?>(
            onFound: (filePath, card) => throw Fail($"expected Unreadable, got Found('{filePath}')"),
            onNotFound: id => throw Fail($"expected Unreadable, got NotFound('{id}')"),
            onDuplicate: (id, filePaths) => throw Fail($"expected Unreadable, got Duplicate('{id}')"),
            onCorrupt: (id, claimants) => throw Fail($"expected Unreadable, got Corrupt('{id}')"),
            onUnreadable: (id, files) =>
            {
                Assert.Equal("R-0001", id);
                return null;
            });
    }

    // The record resolves, never the index (ADR-0004) — proven by execution: no SQLite database
    // exists anywhere under _root, and the resolver still answers correctly by reading files.
    [Fact]
    public void ResolvesWithoutAnyIndexDatabaseOnDisk()
    {
        var path = WriteCard(CardLayout.RegisterDirectory, "r-0001.md", "R-0001", CardKind.Rule, CardScope.Repository);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.db", SearchOption.AllDirectories));

        AssertFound(CardIdentityResolver.Resolve(_root, "R-0001"), path);
    }

    private string WriteCard(string relativeDirectory, string fileStem, string id, CardKind kind, CardScope scope)
    {
        var directory = Path.Combine(_root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem);
        var frontmatter = new CardFrontmatter(id, kind, "Title", "open", CardOwner.Architect, scope, string.Empty, Recorded, Recorded);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    // A file whose leading frontmatter fence is intact — so §13.6 recovery can read its declared
    // 'id' — but whose status fails the parser's own vocabulary check (§12 block A), so the file as
    // a whole still fails to parse. Register-kind here regardless of directory: only the fence and
    // the id matter to the resolver, and using one kind everywhere keeps every corrupt fixture in
    // this file identical bar the id and the directory.
    private string WriteCorruptCard(string relativeDirectory, string fileStem, string id)
    {
        var directory = Path.Combine(_root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem);
        var frontmatter = new CardFrontmatter(
            id, CardKind.Rule, "Title", "not-a-real-status", CardOwner.Architect, CardScope.Repository, string.Empty, Recorded, Recorded);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static void AssertFound(CardIdentityResolution resolution, string expectedPath) =>
        resolution.Match<object?>(
            onFound: (filePath, card) =>
            {
                Assert.Equal(expectedPath, filePath);
                Assert.NotNull(card);
                return null;
            },
            onNotFound: id => throw Fail($"expected Found, got NotFound('{id}')"),
            onDuplicate: (id, filePaths) => throw Fail($"expected Found, got Duplicate('{id}')"),
            onCorrupt: (id, files) => throw Fail($"expected Found, got Corrupt('{id}')"),
            onUnreadable: (id, files) => throw Fail($"expected Found, got Unreadable('{id}')"));

    private static Xunit.Sdk.XunitException Fail(string message) => new(message);
}
