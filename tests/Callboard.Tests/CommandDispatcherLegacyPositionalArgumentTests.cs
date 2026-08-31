using System.Linq;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 14.5, reviewer nit on the block's own Approve (§13's ruling: a nit inside an Approve has to say
/// what it obliges, and this one obliged two things — a test, and a look at the diagnostic).
///
/// <para>
/// The safety property card-model's own scenario states — "its file is named for the identity the
/// system issued, and the caller was never able to name it something else" — held from the moment
/// 14.5 landed: nothing in <c>CardStore.CreateCard</c>'s new signature has a <c>filePath</c>
/// parameter for a caller to fill in. But nothing in the block's own diff <em>asserted</em> that the
/// historical positional-path invocation (every creation verb's shape before 14.5) actually produces
/// no card at the caller's chosen name — only that the new, positional-free shape works. This file
/// is that assertion, pinned against three verbs chosen to cover the three distinct parser shapes
/// 14.5 touched: <see cref="RunRuleCreate"/> et al.'s standalone hand-rolled parse function, the
/// shared <c>ParseCardCreate</c> helper (<c>section create</c>/<c>decision create</c>), and a verb
/// with its own extra required repeatable flag beyond the shared shape (<c>block create</c>'s
/// <c>--task</c>). Not exhaustive over all nine creation verbs — the mechanism is identical for the
/// rest (<c>hazard create</c>, <c>obligation create</c>, <c>decision create</c>, <c>question
/// create</c>, <c>rule author</c> all route through the same <c>RefuseLeadingPositionalArgument</c>
/// guard <see cref="CommandParser"/> now runs first) — a representative sample proportionate to what
/// changed, not a sixth or seventh near-identical copy.
/// </para>
///
/// <para>
/// <b>14.5-remediation (§14 supervisor finding): a tenth and eleventh door, added by name.</b>
/// <c>finding record</c> was not one of the nine creation verbs 14.5 itself touched — it mints a
/// card too, but through <see cref="Cards.CardStore.RecordFinding"/>, a different call graph — and
/// the supervisor found it still took a caller-supplied path for both the finding's own file and,
/// via <c>--blind-spot-file</c>, the raised obligation/hazard's. This is not a representative
/// sample any more for those two doors specifically: both are asserted below by name, alongside the
/// three original doors.
/// </para>
///
/// <para>
/// <b>The diagnostic half.</b> The reviewer's finding: feeding the shipped binary the old positional
/// invocation produced <c>missing-argument</c> — technically true (a downstream flag never got read)
/// but misleading (the caller did supply it; a stray leading token, not a missing one, is what
/// stopped it being read). <c>CommandParser.RefuseLeadingPositionalArgument</c> now intercepts this
/// one shape — a leading token that does not look like a flag, before any flag has been consumed —
/// naming it <c>unexpected-positional-argument</c> and stating the remedy that actually applies:
/// drop the argument, read <c>filePath</c> from the response. <b>Judged and left alone by design:</b>
/// <c>rule propose-compact</c>'s own pre-14.5 shape used a flag (<c>--proposal-file &lt;path&gt;</c>),
/// not a positional — removing that flag leaves the funnel's own generic <c>unrecognised-argument</c>
/// refusal to catch it cleanly on its own (proven in
/// <c>RuleProposeCompact_LegacyProposalFileFlag_Refuses_WithTheExistingUnrecognisedArgumentCode</c>
/// below), so no bespoke handling was added there — a worse parser is not worth a better message, and
/// this one shape did not need it.
/// </para>
/// </summary>
public sealed class CommandDispatcherLegacyPositionalArgumentTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [MemberData(nameof(LegacyPositionalInvocations))]
    public void CreationVerb_LegacyPositionalPathArgument_Refuses_WithTheRemovedArgumentNamed_AndWritesNoCard(
        string kindLabel, string[] args)
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(args, output, repo.Path, "Body.");

        Assert.True(
            exitCode == CommandDispatcher.RefusalExitCode,
            $"expected the legacy '{kindLabel} create' invocation to refuse; got exit code {exitCode}: {output}");
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("unexpected-positional-argument", refusal.GetProperty("code").GetString());
        var message = refusal.GetProperty("message").GetString()!;
        Assert.Contains("14.5", message, StringComparison.Ordinal);
        Assert.Contains("legacy-path.md", message, StringComparison.Ordinal);

        // card-model, "The file is named for the card": "the caller was never able to name it
        // something else" — not one card exists anywhere in the repository, named or otherwise.
        Assert.False(
            Directory.Exists(repo.Path) &&
            Directory.EnumerateFiles(repo.Path, "*.md", SearchOption.AllDirectories).Any());
    }

    public static IEnumerable<object[]> LegacyPositionalInvocations()
    {
        // Standalone hand-rolled parse function (ParseRuleCreate).
        yield return new object[]
        {
            "rule",
            new[]
            {
                "rule", "create", "legacy-path.md", "--title", "Never trust a path string",
                "--role", "architect", "--scope", "repository",
            },
        };

        // The shared ParseCardCreate helper (section create/decision create).
        yield return new object[]
        {
            "section",
            new[]
            {
                "section", "create", "legacy-path.md", "--title", "7. Register",
                "--role", "architect", "--change", ChangeName,
            },
        };

        // A verb with its own extra required repeatable flag beyond the shared shape.
        yield return new object[]
        {
            "block",
            new[]
            {
                "block", "create", "legacy-path.md", "--title", "Flow",
                "--role", "architect", "--change", ChangeName, "--task", "14.5",
            },
        };

        // 14.5-remediation (§14 supervisor finding): the tenth door onto this exact guard —
        // `finding record` never had it, and is the verb the supervisor's finding is about.
        yield return new object[]
        {
            "finding",
            new[]
            {
                "finding", "record", "legacy-path.md", "--title", "Checked",
                "--role", "worker", "--section", "S-0001", "--change", ChangeName, "--blind-spot", "none",
            },
        };
    }

    // 14.5-remediation (§14 supervisor finding): the eleventh door — `finding record
    // --blind-spot-file <path>`, which named the *raised* obligation/hazard's own file. Unlike the
    // positional path above, this flag is not routed onto RefuseLeadingPositionalArgument (it was
    // never a leading positional): it is simply gone from ConsumeKnownFlags' known-flag set, so a
    // caller still spelling it gets the funnel's own generic unrecognised-argument refusal — the
    // same "judged and left alone" disposition RuleProposeCompact_LegacyProposalFileFlag... proves
    // below for --proposal-file, and for the identical reason: a worse parser is not worth a better
    // message for a flag this verb never routes specially.
    [Fact]
    public void FindingRecord_LegacyBlindSpotFileFlag_Refuses_WithTheExistingUnrecognisedArgumentCode_AndWritesNoCard()
    {
        using var repo = new TempGitRepo();
        var sectionOutput = new StringWriter();
        RunInRepo(["section", "create", "--title", "Section", "--role", "architect", "--change", ChangeName], sectionOutput, repo.Path, "Body.");
        var sectionId = JsonDocument.Parse(sectionOutput.ToString()).RootElement.GetProperty("result").GetProperty("id").GetString();

        var bodyFilePath = Path.Combine(repo.Path, "blind-spot-body.md");
        File.WriteAllText(bodyFilePath, "Content.");

        // --blind-spot-file trails every other flag ConsumeKnownFlags would otherwise still
        // recognise — the same "leftover token, not a missing one" shape the sibling
        // RuleProposeCompact_LegacyProposalFileFlag... test below relies on: placed earlier, the
        // unconsumed remainder (its own value, plus every flag behind it) would strand a
        // still-required flag and surface as a misleading missing-argument instead, the exact
        // asymmetry 14.5's positional guard was written to avoid on the positional's own leading
        // token.
        var output = new StringWriter();
        var exitCode = RunInRepo(
            [
                "finding", "record", "--role", "worker", "--title", "Checked", "--section", sectionId!, "--change", ChangeName,
                "--blind-spot", "hazard", "--blind-spot-title", "T", "--blind-spot-body-file", bodyFilePath,
                "--blind-spot-file", "legacy-raised.md",
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("unrecognised-argument", refusal.GetProperty("code").GetString());
        Assert.Contains("--blind-spot-file", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);

        // Only the section card exists under the record proper — the finding itself, and any
        // raised card, were never written. (The body file above also matches "*.md" but lives
        // outside callboard/, which is why the check is scoped to the record's own root.)
        Assert.Single(Directory.EnumerateFiles(Path.Combine(repo.Path, "callboard"), "*.md", SearchOption.AllDirectories));
    }

    // The judged-and-left-alone half: 'rule propose-compact' never had a positional path (it used
    // '--proposal-file <path>'), so removing that flag was never this shape's regression to begin
    // with — the funnel's own EnforceNoUnconsumedArguments already names the stray flag cleanly,
    // with no bespoke RefuseLeadingPositionalArgument-style guard needed.
    [Fact]
    public void RuleProposeCompact_LegacyProposalFileFlag_Refuses_WithTheExistingUnrecognisedArgumentCode()
    {
        using var repo = new TempGitRepo();
        var ruleOutput = new StringWriter();
        RunInRepo(["rule", "create", "--title", "A rule", "--role", "architect", "--scope", "repository"], ruleOutput, repo.Path, "Body.");
        var ruleId = JsonDocument.Parse(ruleOutput.ToString()).RootElement.GetProperty("result").GetProperty("id").GetString();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["rule", "propose-compact", "--absorbs", ruleId!, "--role", "worker", "--proposal-file", "legacy-proposal.md"],
            output, repo.Path, "Candidate text.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("unrecognised-argument", refusal.GetProperty("code").GetString());
        Assert.Contains("--proposal-file", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-legacy-positional-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
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
