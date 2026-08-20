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
}
