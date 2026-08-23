using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §6 block C, domain layer: <see cref="FindingExtentFingerprint"/> — the content fingerprint the
/// Product Owner ruling calls for ("`callboard` never invokes git"), and <see cref="FindingExtentFingerprint.
/// FilePathFor"/>, the file-granularity resolution that makes the "over-report, never under-report"
/// ruling concrete.
/// </summary>
public sealed class FindingExtentFingerprintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "callboard-extent-fingerprint-tests-" + Guid.NewGuid().ToString("N"));

    public FindingExtentFingerprintTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("src/Foo.cs", "src/Foo.cs")]
    [InlineData("src/Foo.cs:10-20", "src/Foo.cs")]
    [InlineData("src/Foo.cs#Bar", "src/Foo.cs")]
    [InlineData("src/Foo.cs#Bar:10-20", "src/Foo.cs")]
    public void FilePathFor_ResolvesARangeOrSymbolQualifierDownToItsFile(string item, string expectedPath)
    {
        Assert.Equal(expectedPath, FindingExtentFingerprint.FilePathFor(item));
    }

    [Fact]
    public void Compute_ForInstrumentExtent_ReturnsNull()
    {
        Assert.Null(FindingExtentFingerprint.Compute(FindingExtent.Instrument("make gates"), _root));
    }

    [Fact]
    public void Compute_ForBlockScopeExtent_ReturnsNull()
    {
        Assert.Null(FindingExtentFingerprint.Compute(FindingExtent.BlockScope, _root));
    }

    [Fact]
    public void Compute_ForExplicitExtent_HashesEachFile()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "content a");
        var fingerprint = FindingExtentFingerprint.Compute(FindingExtent.Explicit(["a.cs"]), _root);

        Assert.NotNull(fingerprint);
        var file = Assert.Single(fingerprint!.Files);
        Assert.Equal("a.cs", file.RelativePath);
        Assert.NotNull(file.ContentHash);
        Assert.Equal(64, file.ContentHash!.Length); // SHA-256 hex
    }

    [Fact]
    public void Compute_TwoItemsResolvingToTheSameFile_DeduplicateToOneEntry()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "content a");
        var fingerprint = FindingExtentFingerprint.Compute(FindingExtent.Explicit(["a.cs:1-5", "a.cs:9-20"]), _root)!;

        Assert.Single(fingerprint.Files);
    }

    [Fact]
    public void Compute_SameContentDifferentQualifiers_ProducesTheSameFingerprint()
    {
        // The ruling's own example: a line range whose file changed elsewhere is reported stale
        // even though the range itself did not move — because fingerprinting resolves to the whole
        // file, not the range. Demonstrated here structurally: two different items naming the same
        // file with different qualifiers fingerprint identically.
        File.WriteAllText(Path.Combine(_root, "a.cs"), "content a");
        var byRange = FindingExtentFingerprint.Compute(FindingExtent.Explicit(["a.cs:1-5"]), _root)!;
        var bySymbol = FindingExtentFingerprint.Compute(FindingExtent.Explicit(["a.cs#SomeMethod"]), _root)!;

        Assert.Equal(byRange, bySymbol);
    }

    [Fact]
    public void Compute_MissingFile_HasANullContentHash_NotAThrow()
    {
        var fingerprint = FindingExtentFingerprint.Compute(FindingExtent.Explicit(["does-not-exist.cs"]), _root)!;

        var file = Assert.Single(fingerprint.Files);
        Assert.Null(file.ContentHash);
    }

    [Fact]
    public void Compute_FilesAreSortedOrdinally_RegardlessOfDeclarationOrder()
    {
        File.WriteAllText(Path.Combine(_root, "b.cs"), "b");
        File.WriteAllText(Path.Combine(_root, "a.cs"), "a");

        var declaredBThenA = FindingExtentFingerprint.Compute(FindingExtent.Explicit(["b.cs", "a.cs"]), _root)!;
        var declaredAThenB = FindingExtentFingerprint.Compute(FindingExtent.Explicit(["a.cs", "b.cs"]), _root)!;

        Assert.Equal(declaredBThenA, declaredAThenB);
        Assert.Equal(["a.cs", "b.cs"], declaredBThenA.Files.Select(static f => f.RelativePath));
    }
}
