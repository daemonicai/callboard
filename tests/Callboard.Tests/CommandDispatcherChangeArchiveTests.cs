using System.Linq;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 7.3/7.4 at the CLI boundary: <c>change archive</c>. Domain-level correctness (bytes unmoved,
/// resolver end to end, index rebuild) is <see cref="CardChangeArchiveTests"/>'s job — this proves
/// the verb is actually wired: parsed, dispatched, and every <see cref="ChangeArchiveOutcome"/>
/// case reaches its own refusal code.
/// </summary>
public sealed class CommandDispatcherChangeArchiveTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 23, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ChangeArchive_LiveChangeWithAnOpenObligation_Succeeds_AndSettlesIt()
    {
        using var repo = new TempGitRepo();

        var sectionOutput = new StringWriter();
        RunInRepo(
            ["section", "create", Path.Combine(repo.CardsDirectory, "s-0001.md"), "--title", "7. Register", "--role", "architect", "--change", ChangeName],
            sectionOutput, repo.Path, "Body.");
        var sectionId = ExtractResultId(sectionOutput);

        var obligationOutput = new StringWriter();
        RunInRepo(
            [
                "obligation", "create", Path.Combine(repo.CardsDirectory, "o-0001.md"), "--title", "Settle the migration",
                "--role", "architect", "--change", ChangeName, "--owed-by", sectionId,
            ],
            obligationOutput, repo.Path, "Body.");
        var obligationId = ExtractResultId(obligationOutput);

        var archiveOutput = new StringWriter();
        var exitCode = RunInRepo(["change", "archive", ChangeName, "--role", "architect"], archiveOutput, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(archiveOutput.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(ChangeName, result.GetProperty("changeName").GetString());
        Assert.Equal("architect", result.GetProperty("archivedBy").GetString());
        var settled = result.GetProperty("settledObligationIds").EnumerateArray().Select(static e => e.GetString()).ToList();
        Assert.Equal([obligationId], settled);
        Assert.False(Directory.Exists(repo.CardsDirectory));
    }

    [Fact]
    public void ChangeArchive_NoSuchLiveChange_Refuses_WithChangeNotFound()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(["change", "archive", "no-such-change", "--role", "architect"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("change-not-found", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void ChangeArchive_ReservedNameArchive_Refuses_WithInvalidChangeName()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(["change", "archive", "archive", "--role", "architect"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("invalid-change-name", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void ChangeArchive_TwiceOnTheSameChange_Refuses_WithAlreadyArchived()
    {
        using var repo = new TempGitRepo();
        RunInRepo(
            ["section", "create", Path.Combine(repo.CardsDirectory, "s-0002.md"), "--title", "7. Register", "--role", "architect", "--change", ChangeName],
            new StringWriter(), repo.Path, "Body.");

        var first = new StringWriter();
        var firstExitCode = RunInRepo(["change", "archive", ChangeName, "--role", "architect"], first, repo.Path, string.Empty);
        Assert.Equal(CommandDispatcher.SuccessExitCode, firstExitCode);

        // Recreate a live change under the same name so the second call has something to try to
        // archive — proving the refusal, not merely "no live change" again.
        Directory.CreateDirectory(repo.CardsDirectory);
        var second = new StringWriter();
        var exitCode = RunInRepo(["change", "archive", ChangeName, "--role", "architect"], second, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(second.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("already-archived", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void ChangeArchive_MissingRole_Refuses()
    {
        using var repo = new TempGitRepo();
        Directory.CreateDirectory(repo.CardsDirectory);

        var output = new StringWriter();
        var exitCode = RunInRepo(["change", "archive", ChangeName], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void ChangeArchive_MissingChangeName_Refuses()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(["change", "archive"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
    }

    // §7 block F's hook: --compact-family/--absorbs, architect-performed, change-scoped only.
    // Everything else about the archive itself (obligations settled, directory moved) is unchanged —
    // this proves the hook actually runs before the move and its result reaches the response.
    [Fact]
    public void ChangeArchive_WithCompactFamilyAndAbsorbs_CompactsFirst_ThenArchives()
    {
        using var repo = new TempGitRepo();
        var familyId = CreateChangeScopedRule(repo, "r-0001", "The family statement");
        var memberId = CreateChangeScopedRule(repo, "r-0002", "A member rule");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["change", "archive", ChangeName, "--role", "architect", "--compact-family", familyId, "--absorbs", memberId],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(familyId, result.GetProperty("compactedFamilyId").GetString());
        var compactedRuleIds = result.GetProperty("compactedRuleIds").EnumerateArray().Select(static e => e.GetString()).ToList();
        Assert.Equal([memberId], compactedRuleIds);
        Assert.False(Directory.Exists(repo.CardsDirectory), "archive must still run once compaction succeeds.");

        var archivedDirectory = result.GetProperty("archivedDirectory").GetString()!;
        var familyPath = Path.Combine(archivedDirectory, "r-0001.md");
        var familyOnDisk = AssertParseSuccess(CardStore.ReadCard(familyPath));
        Assert.True(familyOnDisk.RegisterFields.Absorbs.SequenceEqual([memberId], StringComparer.Ordinal));
    }

    // Ordinary archive (no compaction requested) still reports null for both compaction fields —
    // block D's own "a change with nothing to compact" case, still required to work (brief item 7).
    [Fact]
    public void ChangeArchive_WithoutCompactionFlags_ReportsNullCompactionFields()
    {
        using var repo = new TempGitRepo();
        Directory.CreateDirectory(repo.CardsDirectory);

        var output = new StringWriter();
        var exitCode = RunInRepo(["change", "archive", ChangeName, "--role", "architect"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        // CliJsonContext omits a null property entirely (DefaultIgnoreCondition.WhenWritingNull) —
        // absence, not a JSON null, is what "nothing to compact" looks like on the wire.
        Assert.False(result.TryGetProperty("compactedFamilyId", out _));
        Assert.False(result.TryGetProperty("compactedRuleIds", out _));
    }

    // Register: "Compaction of change-scoped rules SHALL be performed by the architect at archive".
    // §7 block F remediation (reviewer, second round): enforcement lives in one place —
    // CardStore.CompactRules itself — inherited by both rule compact and this hook, and every path
    // through it refuses with no write, no lock and no partial state. That is the guarantee; it is
    // NOT a guarantee that the role check is the *first* thing either handler evaluates — argv-shape
    // checks in RunRuleCompact and id-resolution in RunChangeArchive both run ahead of CompactRules
    // and can refuse for their own reasons first, on their own codes, before a non-architect ever
    // reaches the role check. Both of those earlier-refusal shapes are covered below, by name, so
    // this comment cannot silently drift from what the tests actually assert (the same "a claim only
    // the untested branch relies on" defect §6's WrongCardKind count and IndexSchema's field note
    // both already taught this codebase to name explicitly rather than repeat).
    [Fact]
    public void ChangeArchive_CompactionRequestedByNonArchitect_WithResolvableIds_Refuses_WithRoleNotPermitted_WithoutArchivingOrCompacting()
    {
        using var repo = new TempGitRepo();
        var familyId = CreateChangeScopedRule(repo, "r-0003", "Family");
        var memberId = CreateChangeScopedRule(repo, "r-0004", "Member");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["change", "archive", ChangeName, "--role", "worker", "--compact-family", familyId, "--absorbs", memberId],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("role-not-permitted", refusal.GetProperty("code").GetString());
        Assert.True(Directory.Exists(repo.CardsDirectory), "a refused compaction request must leave the change entirely unarchived.");

        var memberPath = Path.Combine(repo.CardsDirectory, "r-0004.md");
        var memberOnDisk = AssertParseSuccess(CardStore.ReadCard(memberPath));
        Assert.Equal("open", memberOnDisk.Frontmatter.Status);
    }

    // The branch the reviewer's real-binary run actually exposed: RunChangeArchive resolves
    // --compact-family/--absorbs before CompactRules ever runs, so a non-architect naming an id
    // that does not resolve sees card-id-not-found, not role-not-permitted — a pure resolution
    // refusal, no write, no lock, nothing archived. This is not a gap in enforcement (CompactRules
    // is still the only place a role is ever authorised to write); it is what "one enforcement
    // point, inherited by both callers" actually looks like once argv/resolution checks ahead of it
    // are allowed to fire first, which the Architect ruled is fine (learning whether an id resolves
    // is not a threat this tool defends against).
    [Fact]
    public void ChangeArchive_CompactionRequestedByNonArchitect_WithAnUnresolvableAbsorbedId_Refuses_WithCardIdNotFound_NotRoleNotPermitted()
    {
        using var repo = new TempGitRepo();
        var familyId = CreateChangeScopedRule(repo, "r-0006", "Family");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["change", "archive", ChangeName, "--role", "worker", "--compact-family", familyId, "--absorbs", "R-9999"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-not-found", refusal.GetProperty("code").GetString());
        Assert.True(Directory.Exists(repo.CardsDirectory), "a refused compaction request must leave the change entirely unarchived.");
    }

    [Fact]
    public void ChangeArchive_CompactFamilyWithoutAbsorbs_Refuses_AtParseTime()
    {
        using var repo = new TempGitRepo();
        Directory.CreateDirectory(repo.CardsDirectory);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["change", "archive", ChangeName, "--role", "architect", "--compact-family", "R-0001"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
    }

    // A compaction refusal (an unresolvable member id) leaves the change entirely live — the
    // archive move never runs once compaction has failed.
    [Fact]
    public void ChangeArchive_CompactionAbsorbsNamesAnIdThatDoesNotExist_Refuses_WithoutArchiving()
    {
        using var repo = new TempGitRepo();
        var familyId = CreateChangeScopedRule(repo, "r-0005", "Family");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["change", "archive", ChangeName, "--role", "architect", "--compact-family", familyId, "--absorbs", "R-9999"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-not-found", refusal.GetProperty("code").GetString());
        Assert.True(Directory.Exists(repo.CardsDirectory));
    }

    private static string CreateChangeScopedRule(TempGitRepo repo, string fileStem, string title)
    {
        var path = Path.Combine(repo.CardsDirectory, fileStem + ".md");
        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "create", path, "--title", title, "--role", "architect", "--scope", "change", "--change", ChangeName],
            output, repo.Path, "Body.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        return ExtractResultId(output);
    }

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private static string ExtractResultId(StringWriter output)
    {
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").GetProperty("id").GetString()!;
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string CardsDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-change-archive-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            CardsDirectory = System.IO.Path.Combine(Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
