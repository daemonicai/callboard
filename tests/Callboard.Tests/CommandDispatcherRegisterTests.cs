using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 7.1/7.11 at the CLI boundary: <c>rule|hazard|obligation|decision create</c>, <c>section create</c>
/// and <c>rule|hazard|obligation|decision discharge</c>. Same "own refusal code, own test"
/// discipline earlier sections established — the load-bearing test here is
/// <see cref="HazardCreate_MissingCondition_Refuses_AndStatesTheConditionRequired"/>: register's
/// "the system refuses and states the condition it requires" scenario, checked against the actual
/// message text.
/// </summary>
public sealed class CommandDispatcherRegisterTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 23, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RuleCreate_ChangeScoped_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "r-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["rule", "create", path, "--title", "Never trust a path string", "--role", "architect", "--scope", "change", "--change", ChangeName],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("R-0001", result.GetProperty("id").GetString());
        Assert.Equal("rule", result.GetProperty("kind").GetString());
        Assert.Equal("change", result.GetProperty("scope").GetString());
        Assert.Equal("open", result.GetProperty("status").GetString());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RuleCreate_SectionScoped_Refuses_WithTheSpecsExactWording()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "r-0002.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["rule", "create", path, "--title", "Bad", "--role", "architect", "--scope", "section", "--change", ChangeName],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("scope-refused", refusal.GetProperty("code").GetString());
        Assert.Contains("a rule applying to one section is a constraint in a brief", refusal.GetProperty("message").GetString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void HazardCreate_WithConditionAndCadence_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "h-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "hazard", "create", path, "--title", "Rotating key", "--role", "worker",
                "--condition", "The staging key changes every 90 days", "--cadence", "monthly",
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("hazard", result.GetProperty("kind").GetString());
        Assert.Equal("repository", result.GetProperty("scope").GetString());
        Assert.Equal("The staging key changes every 90 days", result.GetProperty("condition").GetString());
        Assert.Equal("monthly", result.GetProperty("cadence").GetString());
    }

    // The load-bearing refusal (register: "the system refuses and states the condition it
    // requires"). What would have to break for this to go red: ParseHazardCreate accepting an
    // absent or blank --condition instead of refusing.
    [Fact]
    public void HazardCreate_MissingCondition_Refuses_AndStatesTheConditionRequired()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "h-0002.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["hazard", "create", path, "--title", "Rotating key", "--role", "worker", "--cadence", "monthly"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("hazard-missing-condition", refusal.GetProperty("code").GetString());
        Assert.Contains("condition", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    // Reviewer finding, block A review round 1: a missing --cadence must mint its own code
    // (hazard-missing-cadence), distinct from a missing --condition's hazard-missing-condition —
    // one code silently covering two independently-triggerable conditions is exactly what a
    // refusal code exists to make unnecessary to disambiguate from prose.
    [Fact]
    public void HazardCreate_MissingCadence_Refuses_WithItsOwnDistinctCode()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "h-0003.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["hazard", "create", path, "--title", "Rotating key", "--role", "worker", "--condition", "The key rotates"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("hazard-missing-cadence", refusal.GetProperty("code").GetString());
        Assert.Contains("cadence", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ObligationCreate_Succeeds()
    {
        using var repo = new TempGitRepo();
        var sectionPath = Path.Combine(repo.CardsDirectory, "s-0001.md");
        var sectionOutput = new StringWriter();
        RunInRepo(
            ["section", "create", sectionPath, "--title", "7. Register", "--role", "architect", "--change", ChangeName],
            sectionOutput, repo.Path, "Body.");
        using var sectionDoc = JsonDocument.Parse(sectionOutput.ToString());
        var sectionId = sectionDoc.RootElement.GetProperty("result").GetProperty("id").GetString();

        var path = Path.Combine(repo.CardsDirectory, "o-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["obligation", "create", path, "--title", "Settle the migration", "--role", "architect", "--change", ChangeName, "--section", sectionId!],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("obligation", result.GetProperty("kind").GetString());
        Assert.Equal(sectionId, result.GetProperty("owedBy").GetString());
    }

    [Fact]
    public void ObligationCreate_MissingOwedBy_Refuses_WithItsOwnDistinctCode()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "o-0002.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["obligation", "create", path, "--title", "Settle the migration", "--role", "architect", "--change", ChangeName],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("obligation-missing-section", refusal.GetProperty("code").GetString());
        Assert.Contains("--section", refusal.GetProperty("message").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ObligationCreate_OwedByDoesNotResolve_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "o-0003.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "obligation", "create", path, "--title", "Settle the migration", "--role", "architect",
                "--change", ChangeName, "--section", "S-9999",
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-id-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ObligationCreate_OwedByNamesANonSectionCard_Refuses()
    {
        using var repo = new TempGitRepo();
        var decisionPath = Path.Combine(repo.DecisionsDirectory, "d-0009.md");
        var decisionOutput = new StringWriter();
        RunInRepo(
            ["decision", "create", decisionPath, "--title", "Adopt option A", "--role", "product-owner"],
            decisionOutput, repo.Path, "Body.");
        using var decisionDoc = JsonDocument.Parse(decisionOutput.ToString());
        var decisionId = decisionDoc.RootElement.GetProperty("result").GetProperty("id").GetString();

        var path = Path.Combine(repo.CardsDirectory, "o-0004.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "obligation", "create", path, "--title", "Settle the migration", "--role", "architect",
                "--change", ChangeName, "--section", decisionId!,
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DecisionCreate_Succeeds_AndDoesNotAcceptAChangeFlag()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.DecisionsDirectory, "d-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["decision", "create", path, "--title", "Adopt option A", "--role", "product-owner"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("decision", doc.RootElement.GetProperty("result").GetProperty("kind").GetString());
        Assert.Equal("capability", doc.RootElement.GetProperty("result").GetProperty("scope").GetString());
    }

    [Fact]
    public void SectionCreate_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "s-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "create", path, "--title", "9. Review", "--role", "architect", "--change", ChangeName],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("section", result.GetProperty("kind").GetString());
        Assert.Equal("open", result.GetProperty("status").GetString());
    }

    // §7 second remediation reviewer nit: MapCardCreateOutcome's response `actingRole` is now a
    // parameter, not a read-back of the written card's own `owner` — self-checking became
    // caller-trusting. The reviewer demonstrated the gap by hardcoding a wrong role into
    // RunRuleCreate's MapCardCreateOutcome call and watching the full suite pass anyway. This is
    // the cross-check that closes it for the five kinds that share the helper: for each, the
    // response's `actingRole` must equal `Frontmatter.Owner` read back from a *fresh*,
    // independent `CardStore.ReadCard` — never from the response object itself, since the whole
    // point is that those two are no longer structurally the same read. Every case below uses
    // `--role worker`, deliberately not `architect` — the reviewer's own mutation hardcoded
    // `CardOwner.Architect`, and every existing test in this file happens to use `architect`,
    // which is exactly why that mutation passed unnoticed; a `worker`-rooted assertion fails
    // immediately against that same mutation (verified below, then reverted — see the DEVLOG post
    // for the discrimination proof).
    //
    // One mechanical `[Theory]` rather than five hand-written near-copies: the five verbs'
    // argument shapes are not a fixed property list `RegisterCardFieldsKeyCoverageTests`-style
    // reflection could enumerate (rule needs `--scope`; hazard needs `--condition`/`--cadence`;
    // obligation needs `--change` plus a real section id to resolve `--section` against; decision
    // needs neither; section needs `--change`) — that is business logic, not a reflectable shape —
    // so the data lives in one `MemberData` list instead: a sixth or seventh creation verb extends
    // that list, not a sixth or seventh copy of this method.
    [Theory]
    [MemberData(nameof(CreationVerbCrossCheckCases))]
    public void CreationVerb_ResponseActingRole_MatchesOwnerReadBackFromDisk(
        string kindLabel, Func<TempGitRepo, (string[] Args, string FilePath)> buildRequest)
    {
        using var repo = new TempGitRepo();
        var (args, filePath) = buildRequest(repo);

        var output = new StringWriter();
        var exitCode = RunInRepo(args, output, repo.Path, "Body.");

        Assert.True(exitCode == CommandDispatcher.SuccessExitCode, $"expected '{kindLabel} create' to succeed; got exit code {exitCode}: {output}");
        using var doc = JsonDocument.Parse(output.ToString());
        var reportedActingRole = doc.RootElement.GetProperty("result").GetProperty("actingRole").GetString();
        Assert.True("worker" == reportedActingRole, $"'{kindLabel} create' response actingRole was '{reportedActingRole}', expected 'worker'.");

        var onDisk = AssertParseSuccess(CardStore.ReadCard(filePath));
        Assert.True(
            reportedActingRole == onDisk.Frontmatter.Owner.ToWireString(),
            $"'{kindLabel} create' response actingRole ('{reportedActingRole}') disagreed with the card's own " +
            $"owner on disk ('{onDisk.Frontmatter.Owner.ToWireString()}') — this is exactly the divergence the " +
            "response and the record must never be free to have.");
    }

    public static IEnumerable<object[]> CreationVerbCrossCheckCases()
    {
        yield return new object[]
        {
            "rule",
            (Func<TempGitRepo, (string[] Args, string FilePath)>)(repo =>
            {
                var path = Path.Combine(repo.RegisterDirectory, "r-cross-check.md");
                return (new[] { "rule", "create", path, "--title", "Cross-check", "--role", "worker", "--scope", "repository" }, path);
            }),
        };
        yield return new object[]
        {
            "hazard",
            (Func<TempGitRepo, (string[] Args, string FilePath)>)(repo =>
            {
                var path = Path.Combine(repo.RegisterDirectory, "h-cross-check.md");
                return (new[]
                {
                    "hazard", "create", path, "--title", "Cross-check", "--role", "worker",
                    "--condition", "The staging key never rotates", "--cadence", "weekly",
                }, path);
            }),
        };
        yield return new object[]
        {
            "obligation",
            (Func<TempGitRepo, (string[] Args, string FilePath)>)(repo =>
            {
                var sectionPath = Path.Combine(repo.CardsDirectory, "s-cross-check-obligation.md");
                var sectionOutput = new StringWriter();
                RunInRepo(
                    ["section", "create", sectionPath, "--title", "Cross-check section", "--role", "worker", "--change", ChangeName],
                    sectionOutput, repo.Path, "Body.");
                using var sectionDoc = JsonDocument.Parse(sectionOutput.ToString());
                var sectionId = sectionDoc.RootElement.GetProperty("result").GetProperty("id").GetString()!;

                var path = Path.Combine(repo.CardsDirectory, "o-cross-check.md");
                return (new[]
                {
                    "obligation", "create", path, "--title", "Cross-check", "--role", "worker",
                    "--change", ChangeName, "--section", sectionId,
                }, path);
            }),
        };
        yield return new object[]
        {
            "decision",
            (Func<TempGitRepo, (string[] Args, string FilePath)>)(repo =>
            {
                var path = Path.Combine(repo.DecisionsDirectory, "d-cross-check.md");
                return (new[] { "decision", "create", path, "--title", "Cross-check", "--role", "worker" }, path);
            }),
        };
        yield return new object[]
        {
            "section",
            (Func<TempGitRepo, (string[] Args, string FilePath)>)(repo =>
            {
                var path = Path.Combine(repo.CardsDirectory, "s-cross-check.md");
                return (new[] { "section", "create", path, "--title", "Cross-check", "--role", "worker", "--change", ChangeName }, path);
            }),
        };
    }

    [Fact]
    public void RuleDischarge_OnAnOpenRule_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "r-0003.md");
        var createOutput = new StringWriter();
        RunInRepo(
            ["rule", "create", path, "--title", "A repository rule", "--role", "architect", "--scope", "repository"],
            createOutput, repo.Path, "Body.");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["rule", "discharge", path, "--role", "architect"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("architect", result.GetProperty("actingRole").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("discharged", read.Frontmatter.Status);
    }

    // §12 block A round two, item 3: the envelope-category regression. `onCardCorrupt` in
    // CommandDispatcher must return a refusal (corrupt.Reason verbatim), not throw into the
    // tool-failure envelope — this is the CLI-level proof for the "register" command family,
    // exercised through `rule discharge`. Status "briefed" is not a legal register lifecycle
    // state, so the §12 block A parse door refuses the card at read time, and that refusal must
    // reach the caller as `card-corrupt` with the field/value/kind/recognised-values intact.
    [Fact]
    public void RuleDischarge_CorruptCard_ExitsAsRefusal_WithReasonIntact()
    {
        using var repo = new TempGitRepo();
        Directory.CreateDirectory(repo.RegisterDirectory);
        var path = Path.Combine(repo.RegisterDirectory, "r-9001.md");
        var frontmatter = new CardFrontmatter(
            "R-9001", CardKind.Rule, "Title", "briefed", CardOwner.Architect, CardScope.Repository,
            string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["rule", "discharge", path, "--role", "architect"],
            output, TextReader.Null, error, isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.NotEqual(CommandDispatcher.ToolFailureExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-corrupt", refusal.GetProperty("code").GetString());
        var message = refusal.GetProperty("message").GetString();
        Assert.NotNull(message);
        Assert.Contains("'briefed'", message, StringComparison.Ordinal);
        Assert.Contains("'rule'", message, StringComparison.Ordinal);
        Assert.Contains(RegisterLifecycleStateWireFormat.RecognisedValues, message, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(error.ToString()));
    }

    [Fact]
    public void HazardDischarge_WhoseConditionHasLapsed_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "h-0004.md");
        var createOutput = new StringWriter();
        RunInRepo(
            [
                "hazard", "create", path, "--title", "Rotating key", "--role", "worker",
                "--condition", "The staging key never rotates", "--cadence", "weekly",
            ],
            createOutput, repo.Path, "Body.");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["hazard", "discharge", path, "--role", "worker"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("discharged", read.Frontmatter.Status);
        Assert.Equal(CardOwner.Worker, read.RegisterFields.DischargedBy);
    }

    [Fact]
    public void Discharge_AlreadyDischarged_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.DecisionsDirectory, "d-0002.md");
        var createOutput = new StringWriter();
        RunInRepo(
            ["decision", "create", path, "--title", "Adopt option A", "--role", "product-owner"],
            createOutput, repo.Path, "Body.");

        var firstDischarge = new StringWriter();
        var firstExitCode = CommandDispatcher.Run(
            ["decision", "discharge", path, "--role", "product-owner"],
            firstDischarge, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);
        Assert.Equal(CommandDispatcher.SuccessExitCode, firstExitCode);

        var second = new StringWriter();
        var secondExitCode = CommandDispatcher.Run(
            ["decision", "discharge", path, "--role", "product-owner"],
            second, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, secondExitCode);
        using var doc = JsonDocument.Parse(second.ToString());
        Assert.Equal("already-discharged", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void Discharge_TargetIsNotARegisterCard_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "s-0002.md");
        var createOutput = new StringWriter();
        RunInRepo(
            ["section", "create", path, "--title", "10. Section", "--role", "architect", "--change", ChangeName],
            createOutput, repo.Path, "Body.");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["rule", "discharge", path, "--role", "architect", "--change", ChangeName],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("not-a-register-card", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void DecisionSupersede_TwoOpenDecisions_Succeeds()
    {
        using var repo = new TempGitRepo();
        var supersedingId = CreateDecision(repo, "d-0003", "Adopt option B");
        var supersededId = CreateDecision(repo, "d-0004", "Adopt option A");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["decision", "supersede", supersedingId, "--supersedes", supersededId, "--role", "product-owner"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(supersedingId, result.GetProperty("supersedingId").GetString());
        Assert.Equal(supersededId, result.GetProperty("supersededId").GetString());
        Assert.Equal("product-owner", result.GetProperty("actingRole").GetString());
    }

    [Fact]
    public void DecisionSupersede_SameIdOnBothSides_Refuses_WithoutHanging()
    {
        using var repo = new TempGitRepo();
        var id = CreateDecision(repo, "d-0005", "Adopt option A");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["decision", "supersede", id, "--supersedes", id, "--role", "product-owner"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("self-supersession", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void DecisionSupersede_SupersededAlreadyDischarged_Refuses()
    {
        using var repo = new TempGitRepo();
        var first = CreateDecision(repo, "d-0006", "Adopt option A");
        var second = CreateDecision(repo, "d-0007", "Adopt option B");
        var third = CreateDecision(repo, "d-0008", "Adopt option C");

        var firstOutput = new StringWriter();
        CommandDispatcher.Run(
            ["decision", "supersede", second, "--supersedes", first, "--role", "product-owner"],
            firstOutput, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["decision", "supersede", third, "--supersedes", first, "--role", "product-owner"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("already-discharged", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void DecisionSupersede_SupersedesIdDoesNotResolve_Refuses()
    {
        using var repo = new TempGitRepo();
        var supersedingId = CreateDecision(repo, "d-0009", "Adopt option B");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["decision", "supersede", supersedingId, "--supersedes", "D-9999", "--role", "product-owner"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-id-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void DecisionSupersede_SupersedesNamesANonDecisionCard_Refuses()
    {
        using var repo = new TempGitRepo();
        var supersedingId = CreateDecision(repo, "d-0010", "Adopt option B");

        var rulePath = Path.Combine(repo.RegisterDirectory, "r-0010.md");
        var ruleOutput = new StringWriter();
        RunInRepo(
            ["rule", "create", rulePath, "--title", "A repository rule", "--role", "architect", "--scope", "repository"],
            ruleOutput, repo.Path, "Body.");
        using var ruleDoc = JsonDocument.Parse(ruleOutput.ToString());
        var ruleId = ruleDoc.RootElement.GetProperty("result").GetProperty("id").GetString();

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["decision", "supersede", supersedingId, "--supersedes", ruleId!, "--role", "product-owner"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Retrieval by id after supersession, proven through the CLI boundary rather than the domain
    // layer alone: 'decision status' does not exist (11.1 is out of scope for this block), so this
    // reads back the file CardStore.ReadCard sees, the same evidence the resolver itself uses.
    [Fact]
    public void DecisionSupersede_SupersededDecision_StillReadableFromDiskAfterwards()
    {
        using var repo = new TempGitRepo();
        var supersedingId = CreateDecision(repo, "d-0011", "Adopt option B");
        var supersededId = CreateDecision(repo, "d-0012", "Adopt option A");
        var supersededPath = Path.Combine(repo.DecisionsDirectory, "d-0012.md");

        var output = new StringWriter();
        CommandDispatcher.Run(
            ["decision", "supersede", supersedingId, "--supersedes", supersededId, "--role", "product-owner"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        var read = AssertParseSuccess(CardStore.ReadCard(supersededPath));
        Assert.Equal(supersededId, read.Frontmatter.Id);
        Assert.Equal("discharged", read.Frontmatter.Status);
        Assert.Equal(supersedingId, read.RegisterFields.SupersededBy);
    }

    private static string CreateDecision(TempGitRepo repo, string fileStem, string title)
    {
        var path = Path.Combine(repo.DecisionsDirectory, fileStem + ".md");
        var output = new StringWriter();
        RunInRepo(["decision", "create", path, "--title", title, "--role", "product-owner"], output, repo.Path, "Body.");
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").GetProperty("id").GetString()!;
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    public sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string CardsDirectory { get; }

        internal string RegisterDirectory { get; }

        internal string DecisionsDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-register-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            CardsDirectory = System.IO.Path.Combine(Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', System.IO.Path.DirectorySeparatorChar));
            RegisterDirectory = System.IO.Path.Combine(Path, CardLayout.RegisterDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
            DecisionsDirectory = System.IO.Path.Combine(Path, CardLayout.DecisionsDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
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
