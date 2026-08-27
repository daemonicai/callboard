using System.Text;

namespace Callboard.Cards;

/// <summary>
/// Writes an export document the way a card is written — temp file, then atomic rename (D7,
/// ADR-0003) — so an interrupted export can never leave a half-written archive artefact at
/// <paramref name="outputPath"/>. Not <see cref="CardStore"/>'s own <c>AtomicWrite</c>: that method
/// is anchored to a resolved card path (<see cref="AnchoredCardPath"/>) and returns
/// <see cref="CardWriteResult"/>, a card-shaped outcome; this writes an arbitrary caller-named
/// path that is not a card at all, so it gets its own small outcome type
/// (<see cref="RecordExportWriteOutcome"/>) rather than stretching a card-addressed one to cover a
/// target that is never resolved through <see cref="CardIdentityResolver"/> or locked under
/// ADR-0004 (there is nothing card-shaped to lock).
/// </summary>
internal static class RecordExportWriter
{
    internal static RecordExportWriteOutcome WriteAtomically(string outputPath, string content, bool force)
    {
        if (!force && File.Exists(outputPath))
        {
            return RecordExportWriteOutcome.TargetExists;
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(directory))
        {
            return RecordExportWriteOutcome.ToolFailure($"'{outputPath}' has no containing directory to write into.");
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RecordExportWriteOutcome.ToolFailure($"could not create '{directory}': {ex.Message}");
        }

        // Beside the target, on the same filesystem, never the system temp directory — a rename
        // across filesystems degrades to a copy and stops being atomic, the same reason CardStore's
        // own AtomicWrite places its temp file this way (ADR-0003 / D7).
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(outputPath)}.tmp-{Guid.NewGuid():N}");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // overwrite: true is safe here — the TargetExists check above already refused an
            // existing target unless --force asked for it, so by the time this rename runs the
            // caller has either confirmed there is nothing to lose or explicitly said to overwrite
            // it. A concurrent writer racing this same path between the check and this rename is
            // not guarded against: unlike a card, an export target carries no per-card lock
            // (ADR-0004 locks cards, not arbitrary files), and a single-maintainer, single-machine
            // deployment with no concurrent export callers has no such race to guard against today.
            File.Move(tempPath, outputPath, overwrite: true);
            return RecordExportWriteOutcome.Written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RecordExportWriteOutcome.ToolFailure($"could not write '{outputPath}': {ex.Message}");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
