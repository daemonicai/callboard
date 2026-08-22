using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 5.8's hardest requirement (work-lifecycle: "the system answers from the section entity without
/// requiring its cards to be read"), proven the way §5 block D proved
/// <see cref="BlockCardFields.GateStatusOf"/> stays out of reach of <see cref="CardFile.Comments"/>:
/// by constructing exactly the scenario a wrong implementation would answer differently on, then
/// asserting the right answer came out.
///
/// <para>
/// <b>The mutation this test exists to catch, named before it is written:</b> a later change routes
/// <c>section status</c> (or any answer to "what is this section's status") through walking the
/// cards raised within the section — e.g. deriving "closed" from "every block card carrying this
/// section's id in its own <c>section</c> field has itself reached <c>closed</c>/<c>landed</c>",
/// instead of reading the section card's own <c>status</c> field. That derivation is a plausible
/// thing to write (it is, after all, roughly how a human reads a section's progress), which is
/// exactly why it needs a test that fails if written, not just a passing test that happens not to
/// exercise it.
/// </para>
///
/// <para>
/// The two tests below plant raised cards whose own state disagrees with the section entity's own
/// recorded status in <em>both</em> directions — section says <c>open</c> while every raised card
/// looks done, and section says <c>closed</c> while a raised card looks still in flight — so an
/// aggregate-over-children implementation would get at least one of the two wrong, however it
/// aggregated. Both assert the section entity's own field wins.
/// </para>
/// </summary>
public sealed class SectionStatusStructuralTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 22, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void SectionStatus_ReportsOpen_EvenWhenEveryRaisedBlockCardLooksClosed()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteSectionCard(repo.Path, "s-0001", "S-0001", status: "open");
        WriteRaisedBlockCard(repo.Path, "b-0001", "B-0001", sectionId: "S-0001", status: "closed");
        WriteRaisedBlockCard(repo.Path, "b-0002", "B-0002", sectionId: "S-0001", status: "landed");

        var output = new StringWriter();
        var exitCode = RunInRepo(["section", "status", sectionPath], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("open", doc.RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    [Fact]
    public void SectionStatus_ReportsClosed_EvenWhenARaisedBlockCardLooksStillInFlight()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteSectionCard(repo.Path, "s-0002", "S-0002", status: "closed");
        WriteRaisedBlockCard(repo.Path, "b-0003", "B-0003", sectionId: "S-0002", status: "building");

        var output = new StringWriter();
        var exitCode = RunInRepo(["section", "status", sectionPath], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("closed", doc.RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    // The same proposition proven a second, structurally different way: the raised block cards'
    // own files are deleted outright before the status query runs, and the answer is unchanged.
    // If any code path on this route ever enumerated or opened the directory's other cards, this
    // would either throw (file gone) or answer differently — it does neither.
    [Fact]
    public void SectionStatus_IsUnaffectedByDeletingEveryOtherCardInTheSameDirectory()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteSectionCard(repo.Path, "s-0003", "S-0003", status: "open");
        var raisedPath = WriteRaisedBlockCard(repo.Path, "b-0004", "B-0004", sectionId: "S-0003", status: "closed");
        File.Delete(raisedPath);

        var output = new StringWriter();
        var exitCode = RunInRepo(["section", "status", sectionPath], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("open", doc.RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    private static string WriteSectionCard(string repoRoot, string fileStem, string id, string status)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", status, CardOwner.Architect, CardScope.Change, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteRaisedBlockCard(string repoRoot, string fileStem, string id, string sectionId, string status)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", status, CardOwner.Worker, CardScope.Change, sectionId, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory) =>
        CommandDispatcher.Run(args, output, TextReader.Null, TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-section-status-structural-tests-" + Guid.NewGuid().ToString("N"));
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
