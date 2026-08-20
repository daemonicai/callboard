namespace Callboard.Cli;

/// <summary>
/// The stdin body-reading path (ADR-0001 / design.md D1): card bodies arrive on stdin, never
/// as a shell argument, so a multi-line Markdown body never needs quoting. Verbs that accept a
/// body call this; it takes an explicit <see cref="TextReader"/> rather than reading
/// <see cref="Console.In"/> directly so it is testable without process redirection.
/// </summary>
internal static class StdinBodyReader
{
    internal static string ReadBody(TextReader input) => input.ReadToEnd();
}
