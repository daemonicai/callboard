using System.Linq;
using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 7.10 — <see cref="RuleCitations"/> (register: "Register size triggers review, never eviction").
/// Product Owner ruling: "a citation is a reference from another card. Counting walks the record
/// for cards naming a rule's id ... No new verb, no new state ... it counts distinct referencing
/// cards, not occasions of use." Covers the counting walk itself (body mention, comment mention,
/// several mentions in one card still counting once, a mention in an archived change, a rule never
/// mentioned counting zero, an id-prefix near-miss not counting), the ceiling predicate (a stated
/// trigger, not a cap), and the uncited-open-rule queue (never a discharged rule, never touching
/// anything it names).
/// </summary>
public sealed class RuleCitationsTests : IDisposable
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset Created = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-rule-citations-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _registerDirectory;
    private readonly string _changeDirectory;

    public RuleCitationsTests()
    {
        _registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        _changeDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_registerDirectory);
        Directory.CreateDirectory(_changeDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CountCitations_NoOtherCardMentionsTheId_IsZero()
    {
        WriteRepositoryRule("r-0001", "R-0001", "A standalone rule.");

        var count = RuleCitations.CountCitations(_root, "R-0001", Path.Combine(_registerDirectory, "r-0001.md"));

        Assert.Equal(0, count);
    }

    [Fact]
    public void CountCitations_AnotherCardsBodyNamesTheId_CountsOne()
    {
        var targetPath = WriteRepositoryRule("r-0002", "R-0002", "The target rule.");
        WriteRepositoryRule("r-0003", "R-0003", "This leans on R-0002 directly.");

        var count = RuleCitations.CountCitations(_root, "R-0002", targetPath);

        Assert.Equal(1, count);
    }

    [Fact]
    public void CountCitations_MentionOnlyInAComment_StillCounts()
    {
        var targetPath = WriteRepositoryRule("r-0004", "R-0004", "The target rule.");
        var citingPath = Path.Combine(_registerDirectory, "r-0005.md");
        var frontmatter = new CardFrontmatter(
            "R-0005", CardKind.Rule, "A citing rule", RegisterLifecycleState.Open.ToWireString(),
            CardOwner.Architect, CardScope.Repository, string.Empty, Created, Created);
        var comment = new CardComment(
            "c-0001", CardOwner.Reviewer, Created, "Per R-0004, this holds.", null, null, null, []);
        var card = new CardFile(frontmatter, "No mention in the body.", [comment], [], RegisterFields: RegisterCardFields.Empty);
        File.WriteAllText(citingPath, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var count = RuleCitations.CountCitations(_root, "R-0004", targetPath);

        Assert.Equal(1, count);
    }

    // Product Owner's accepted consequence, proven directly: "a rule leaned on repeatedly within
    // one card's thread counts once", not once per mention.
    [Fact]
    public void CountCitations_SameCardMentionsTheIdSeveralTimes_StillCountsOnce()
    {
        var targetPath = WriteRepositoryRule("r-0006", "R-0006", "The target rule.");
        WriteRepositoryRule("r-0007", "R-0007", "R-0006 said it, R-0006 repeated it, and R-0006 said it a third time.");

        var count = RuleCitations.CountCitations(_root, "R-0006", targetPath);

        Assert.Equal(1, count);
    }

    // Every card mentioning the id counts once each — several distinct referencing cards, not one
    // running tally.
    [Fact]
    public void CountCitations_SeveralDifferentCardsMentionTheId_CountsEachOnce()
    {
        var targetPath = WriteRepositoryRule("r-0008", "R-0008", "The target rule.");
        WriteRepositoryRule("r-0009", "R-0009", "First reference to R-0008.");
        WriteRepositoryRule("r-0010", "R-0010", "Second reference to R-0008.");

        var count = RuleCitations.CountCitations(_root, "R-0008", targetPath);

        Assert.Equal(2, count);
    }

    // The token-boundary check: R-0008 is a proper prefix of R-00080's own trailing token text, but
    // the two are not the same identity and must not be confused.
    [Fact]
    public void CountCitations_LongerIdContainingTheTargetAsAPrefix_DoesNotCount()
    {
        var targetPath = WriteRepositoryRule("r-0011", "R-0011", "The target rule.");
        WriteRepositoryRule("r-0012", "R-0012", "This mentions only the longer, different id R-00110.");

        var count = RuleCitations.CountCitations(_root, "R-0011", targetPath);

        Assert.Equal(0, count);
    }

    // A citation reaches into an archived change, the same reach CardIdentityResolver already gives
    // every other reference — register's "identity SHALL remain valid and resolvable after archive".
    [Fact]
    public void CountCitations_MentionInAnArchivedChange_StillCounts()
    {
        var targetPath = WriteRepositoryRule("r-0013", "R-0013", "The target rule.");
        var archivedDirectory = Path.Combine(
            _root, CardLayout.ArchiveDirectory.Replace('/', Path.DirectorySeparatorChar), "2026-01-01-old-change");
        Directory.CreateDirectory(archivedDirectory);
        var archivedFrontmatter = new CardFrontmatter(
            "R-0099", CardKind.Rule, "An archived rule", RegisterLifecycleState.Discharged.ToWireString(),
            CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var archivedCard = new CardFile(archivedFrontmatter, "It cited R-0013 back then.", [], [], RegisterFields: RegisterCardFields.Empty);
        File.WriteAllText(
            Path.Combine(archivedDirectory, "r-0099.md"),
            CardFileWriter.Serialize(archivedCard),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var count = RuleCitations.CountCitations(_root, "R-0013", targetPath);

        Assert.Equal(1, count);
    }

    [Fact]
    public void CeilingPassed_LiveCountAboveCeiling_IsTrue()
    {
        Assert.True(RuleCitations.CeilingPassed(liveRuleCount: 51, ceiling: 50));
    }

    [Fact]
    public void CeilingPassed_LiveCountAtOrBelowCeiling_IsFalse()
    {
        Assert.False(RuleCitations.CeilingPassed(liveRuleCount: 50, ceiling: 50));
        Assert.False(RuleCitations.CeilingPassed(liveRuleCount: 10, ceiling: 50));
    }

    // §10 block E's caller: the figure `rule review` states the ceiling against — every live
    // (open) rule counts, cited or not, and a discharged one never does.
    [Fact]
    public void CountLiveOpenRules_CountsOpenRepositoryRules_NotDischargedOnes()
    {
        WriteRepositoryRule("r-0021", "R-0021", "Body.");
        WriteRepositoryRule("r-0022", "R-0022", "Body.");
        var dischargedPath = Path.Combine(_registerDirectory, "r-0023.md");
        WriteRuleCardAt(dischargedPath, "R-0023", CardScope.Repository, RegisterLifecycleState.Discharged, "Body.");

        var count = RuleCitations.CountLiveOpenRules(_root);

        Assert.Equal(2, count);
    }

    // Same exclusion UncitedOpenRules already gets right, proven independently for the count: a
    // never-promoted change-scoped rule left open in an archived change is not part of the live
    // register and must not inflate this figure.
    [Fact]
    public void CountLiveOpenRules_ChangeScopedOpenRuleInAnArchivedChange_IsNotCounted()
    {
        WriteRepositoryRule("r-0024", "R-0024", "Body.");
        var archivedDirectory = Path.Combine(
            _root, CardLayout.ArchiveDirectory.Replace('/', Path.DirectorySeparatorChar), "2026-01-01-old-change");
        Directory.CreateDirectory(archivedDirectory);
        WriteRuleCardAt(Path.Combine(archivedDirectory, "r-0025.md"), "R-0025", CardScope.Change, RegisterLifecycleState.Open, "Body.");

        var count = RuleCitations.CountLiveOpenRules(_root);

        Assert.Equal(1, count);
    }

    [Fact]
    public void UncitedOpenRules_ContainsOnlyOpenRulesWithZeroCitations()
    {
        var uncitedPath = WriteRepositoryRule("r-0014", "R-0014", "Never mentioned anywhere.");
        var citedPath = WriteRepositoryRule("r-0015", "R-0015", "The cited rule.");
        WriteRepositoryRule("r-0016", "R-0016", "This leans on R-0015.");
        var dischargedPath = Path.Combine(_registerDirectory, "r-0017.md");
        WriteRuleCardAt(dischargedPath, "R-0017", CardScope.Repository, RegisterLifecycleState.Discharged, "Never mentioned, but discharged.");

        var uncited = RuleCitations.UncitedOpenRules(_root);

        var uncitedPaths = uncited.Select(static entry => entry.FilePath).ToList();
        Assert.Contains(uncitedPath, uncitedPaths);
        Assert.DoesNotContain(citedPath, uncitedPaths);
        Assert.DoesNotContain(dischargedPath, uncitedPaths);
    }

    // §7 remediation, blocker 2: resolvable is not the same question as live. A change-scoped rule
    // left `open` when its change archives is still resolvable (CountCitations reaches it — proven
    // above) but is not part of the live register, so it must not enter this queue — the queue
    // would otherwise grow, permanently, with every archived change that never promoted its rules.
    [Fact]
    public void UncitedOpenRules_ChangeScopedOpenRuleInAnArchivedChange_IsNotQueued()
    {
        var archivedDirectory = Path.Combine(
            _root, CardLayout.ArchiveDirectory.Replace('/', Path.DirectorySeparatorChar), "2026-01-01-old-change");
        Directory.CreateDirectory(archivedDirectory);
        var archivedRulePath = Path.Combine(archivedDirectory, "r-0019.md");
        WriteRuleCardAt(archivedRulePath, "R-0019", CardScope.Change, RegisterLifecycleState.Open, "Never promoted, archived open.");

        var uncited = RuleCitations.UncitedOpenRules(_root);

        Assert.DoesNotContain(archivedRulePath, uncited.Select(static entry => entry.FilePath));
    }

    // The other half of the same distinction: a rule promoted to repository scope before its
    // change archived is still live and still belongs in the queue — archiving a change must not
    // sweep away a rule that has already left it.
    [Fact]
    public void UncitedOpenRules_RepositoryScopedRulePromotedBeforeArchive_IsStillQueued()
    {
        var promotedPath = WriteRepositoryRule("r-0020", "R-0020", "Promoted out before its change archived.");
        var archivedDirectory = Path.Combine(
            _root, CardLayout.ArchiveDirectory.Replace('/', Path.DirectorySeparatorChar), "2026-01-01-old-change");
        Directory.CreateDirectory(archivedDirectory);

        var uncited = RuleCitations.UncitedOpenRules(_root);

        Assert.Contains(promotedPath, uncited.Select(static entry => entry.FilePath));
    }

    // "SHALL NOT be retired automatically" — proven by execution: the queue computation itself
    // never discharges, never writes, never touches the file it names.
    [Fact]
    public void UncitedOpenRules_DoesNotMutateAnyCardItNames()
    {
        var path = WriteRepositoryRule("r-0018", "R-0018", "Never mentioned anywhere.");
        var before = File.ReadAllBytes(path);

        RuleCitations.UncitedOpenRules(_root);

        var after = File.ReadAllBytes(path);
        Assert.Equal(before, after);
        var onDisk = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("open", onDisk.Frontmatter.Status);
    }

    private string WriteRepositoryRule(string fileStem, string id, string body)
    {
        var path = Path.Combine(_registerDirectory, fileStem + ".md");
        WriteRuleCardAt(path, id, CardScope.Repository, RegisterLifecycleState.Open, body);
        return path;
    }

    private static void WriteRuleCardAt(string path, string id, CardScope scope, RegisterLifecycleState state, string body)
    {
        var frontmatter = new CardFrontmatter(
            id, CardKind.Rule, "A rule", state.ToWireString(), CardOwner.Architect, scope, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, body, [], [], RegisterFields: RegisterCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
