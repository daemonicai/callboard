using Callboard.Cards;

namespace Callboard.Tests;

public sealed class CardLayoutTests
{
    [Fact]
    public void RepositoryScope_ResolvesToRegister() =>
        Assert.Equal("callboard/register/", CardLayout.DirectoryFor(CardScope.Repository, changeName: null));

    [Fact]
    public void CapabilityScope_ResolvesToDecisions() =>
        Assert.Equal("callboard/decisions/", CardLayout.DirectoryFor(CardScope.Capability, changeName: null));

    [Fact]
    public void ChangeScope_ResolvesToTheNamedChangeDirectory() =>
        Assert.Equal("callboard/changes/establish-callboard/", CardLayout.DirectoryFor(CardScope.Change, "establish-callboard"));

    [Fact]
    public void SectionScope_ResolvesToItsChangesDirectory_BecauseASectionLivesInsideItsChange() =>
        Assert.Equal("callboard/changes/establish-callboard/", CardLayout.DirectoryFor(CardScope.Section, "establish-callboard"));

    [Fact]
    public void ChangeScope_WithoutAChangeName_Throws() =>
        Assert.Throws<ArgumentException>(() => CardLayout.DirectoryFor(CardScope.Change, changeName: null));

    [Theory]
    [InlineData("../../etc")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("foo/../../etc")]
    [InlineData("")]
    public void ChangeScope_WithATraversalOrSeparatorInTheChangeName_Throws(string changeName) =>
        Assert.Throws<ArgumentException>(() => CardLayout.DirectoryFor(CardScope.Change, changeName));

    [Fact]
    public void SectionScope_WithATraversalInTheChangeName_Throws() =>
        Assert.Throws<ArgumentException>(() => CardLayout.DirectoryFor(CardScope.Section, "../../etc"));

    [Fact]
    public void ChangesDirectory_WithTheReservedArchiveName_Refuses() =>
        Assert.Throws<ArgumentException>(() => CardLayout.ChangesDirectory("archive"));

    [Fact]
    public void ChangeScope_WithTheReservedArchiveName_Refuses() =>
        Assert.Throws<ArgumentException>(() => CardLayout.DirectoryFor(CardScope.Change, "archive"));

    [Fact]
    public void ChangesDirectory_WithAnOrdinaryName_IsNotRefused() =>
        Assert.Equal("callboard/changes/establish-callboard/", CardLayout.ChangesDirectory("establish-callboard"));

    [Fact]
    public void RequireSafePathSegment_AcceptsAnOrdinaryName() =>
        Assert.Equal("establish-callboard", CardLayout.RequireSafePathSegment("establish-callboard", "changeName"));

    [Theory]
    [InlineData("../x")]
    [InlineData("x/..")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void RequireSafePathSegment_RejectsTraversalSeparatorsAndEmpty(string value) =>
        Assert.Throws<ArgumentException>(() => CardLayout.RequireSafePathSegment(value, "id"));

    // §7 remediation, blocker 2: resolvable (ResolveRecordDirectories, which reaches the archive)
    // is not the same question as live (ResolveLiveRecordDirectories, which does not) — pinned
    // directly at the layout level, the single place both RuleCitations.UncitedOpenRules and any
    // later caller must go through rather than each drawing this line for itself.
    [Fact]
    public void ResolveLiveRecordDirectories_ExcludesArchivedChanges_ButResolveRecordDirectories_IncludesThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "callboard-card-layout-tests-" + Guid.NewGuid().ToString("N"));
        var liveChangeDirectory = Path.Combine(root, CardLayout.ChangesDirectory("live-change").Replace('/', Path.DirectorySeparatorChar));
        var archivedChangeDirectory = Path.Combine(root, CardLayout.ArchivedChangeDirectory("old-change").Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(liveChangeDirectory);
        Directory.CreateDirectory(archivedChangeDirectory);

        try
        {
            var live = CardLayout.ResolveLiveRecordDirectories(root);
            Assert.Contains(Path.TrimEndingDirectorySeparator(liveChangeDirectory), live.Select(Path.TrimEndingDirectorySeparator));
            Assert.DoesNotContain(Path.TrimEndingDirectorySeparator(archivedChangeDirectory), live.Select(Path.TrimEndingDirectorySeparator));

            var all = CardLayout.ResolveRecordDirectories(root);
            Assert.Contains(Path.TrimEndingDirectorySeparator(liveChangeDirectory), all.Select(Path.TrimEndingDirectorySeparator));
            Assert.Contains(Path.TrimEndingDirectorySeparator(archivedChangeDirectory), all.Select(Path.TrimEndingDirectorySeparator));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
