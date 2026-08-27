using System.Linq;
using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §10 block E at the CLI boundary: <c>rule review [--ceiling &lt;n&gt;]</c> (carried item B from
/// §7's close, register: "Register size triggers review, never eviction"). Both spec scenarios —
/// passing the ceiling raises a review and retires nothing; an uncited rule is queued and remains
/// live — plus the stated-ceiling requirement (which value applied, and why), the queue still
/// reporting when the ceiling is not passed, and the CLI-layer refusal for a malformed
/// <c>--ceiling</c>. Every test asserts the on-disk cards are byte-unchanged after the call, not
/// merely that the call did not error — "retires nothing" is a claim about the record, not about
/// the exit code.
/// </summary>
public sealed class CommandDispatcherRuleReviewTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    // register: "the ceiling SHALL NOT act as a hard cap" — passing it raises a review, and every
    // rule card that was open before the call is still open, byte-identical, afterwards.
    [Fact]
    public void CeilingPassed_RaisesTheReview_AndRetiresNothing()
    {
        using var repo = new TempGitRepo();
        var path1 = WriteRule(repo, "r-0001", "R-0001", "open", "Body.");
        var path2 = WriteRule(repo, "r-0002", "R-0002", "open", "Body.");
        var path3 = WriteRule(repo, "r-0003", "R-0003", "open", "Body.");
        var before1 = File.ReadAllBytes(path1);
        var before2 = File.ReadAllBytes(path2);
        var before3 = File.ReadAllBytes(path3);

        var result = RuleReview(repo, ceiling: 2);

        Assert.Equal(2, result.GetProperty("ceiling").GetInt32());
        Assert.Equal("flag", result.GetProperty("ceilingSource").GetString());
        Assert.Equal(3, result.GetProperty("liveRuleCount").GetInt32());
        Assert.True(result.GetProperty("ceilingPassed").GetBoolean());

        Assert.Equal(before1, File.ReadAllBytes(path1));
        Assert.Equal(before2, File.ReadAllBytes(path2));
        Assert.Equal(before3, File.ReadAllBytes(path3));
        Assert.Equal("open", AssertParseSuccess(CardStore.ReadCard(path1)).Frontmatter.Status);
        Assert.Equal("open", AssertParseSuccess(CardStore.ReadCard(path2)).Frontmatter.Status);
        Assert.Equal("open", AssertParseSuccess(CardStore.ReadCard(path3)).Frontmatter.Status);
    }

    // register: "a rule that is never cited SHALL be placed in a review queue for a human and
    // SHALL NOT be retired automatically" — queued, and still open and unchanged afterwards.
    [Fact]
    public void UncitedOpenRule_IsQueued_AndRemainsLive()
    {
        using var repo = new TempGitRepo();
        var uncitedPath = WriteRule(repo, "r-0004", "R-0004", "open", "Never mentioned anywhere.");
        var citedPath = WriteRule(repo, "r-0005", "R-0005", "open", "The cited one.");
        WriteRule(repo, "r-0006", "R-0006", "open", "This leans on R-0005.");
        var before = File.ReadAllBytes(uncitedPath);

        var result = RuleReview(repo, ceiling: 50);

        var uncited = result.GetProperty("uncitedOpenRules").EnumerateArray().ToList();
        var uncitedIds = uncited.Select(static entry => entry.GetProperty("id").GetString()).ToList();
        Assert.Contains("R-0004", uncitedIds);
        Assert.DoesNotContain("R-0005", uncitedIds);

        var queued = Assert.Single(uncited, entry => entry.GetProperty("id").GetString() == "R-0004");
        Assert.Equal(uncitedPath, queued.GetProperty("filePath").GetString());
        Assert.Equal("A rule", queued.GetProperty("title").GetString());

        Assert.Equal(before, File.ReadAllBytes(uncitedPath));
        Assert.Equal("open", AssertParseSuccess(CardStore.ReadCard(uncitedPath)).Frontmatter.Status);
        Assert.Equal("open", AssertParseSuccess(CardStore.ReadCard(citedPath)).Frontmatter.Status);
    }

    [Fact]
    public void CeilingAbsent_AppliesTheDefault_AndStatesWhichApplied()
    {
        using var repo = new TempGitRepo();
        WriteRule(repo, "r-0007", "R-0007", "open", "Body.");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["rule", "review"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");

        Assert.Equal(CommandDispatcher.DefaultRuleReviewCeiling, result.GetProperty("ceiling").GetInt32());
        Assert.Equal("default", result.GetProperty("ceilingSource").GetString());
        Assert.False(result.GetProperty("ceilingPassed").GetBoolean());
    }

    [Fact]
    public void CeilingNotPassed_StillReportsTheQueue()
    {
        using var repo = new TempGitRepo();
        WriteRule(repo, "r-0008", "R-0008", "open", "Never mentioned anywhere.");

        var result = RuleReview(repo, ceiling: 50);

        Assert.False(result.GetProperty("ceilingPassed").GetBoolean());
        var uncitedIds = result.GetProperty("uncitedOpenRules").EnumerateArray()
            .Select(static entry => entry.GetProperty("id").GetString()).ToList();
        Assert.Contains("R-0008", uncitedIds);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("")]
    public void MalformedCeiling_Refuses_AtTheCliLayer(string ceilingText)
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["rule", "review", "--ceiling", ceilingText],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-ceiling", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void OutsideAnyGitRepository_Refuses_WithRepoRootNotFoundCode()
    {
        using var directory = new TempDirectory();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["rule", "review"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: directory.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("repo-root-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    private static string WriteRule(TempGitRepo repo, string fileStem, string id, string status, string body)
    {
        var path = System.IO.Path.Combine(repo.RegisterDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Rule, "A rule", status, CardOwner.Architect, CardScope.Repository, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, body, [], [], RegisterFields: RegisterCardFields.Empty);
        WriteCard(path, card);
        return path;
    }

    private static void WriteCard(string path, CardFile card) =>
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match(
            onSuccess: static success => success.Card,
            onFailure: static failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private static JsonElement RuleReview(TempGitRepo repo, int ceiling)
    {
        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["rule", "review", "--ceiling", ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"callboard-rule-review-cli-nongit-{Guid.NewGuid():N}");

        internal TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string RegisterDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-rule-review-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            RegisterDirectory = System.IO.Path.Combine(Path, CardLayout.RegisterDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(RegisterDirectory);
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
