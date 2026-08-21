namespace Callboard.Cli;

/// <summary>
/// The stdin body-reading path (ADR-0001 / design.md D1): card bodies arrive on stdin, never
/// as a shell argument, so a multi-line Markdown body never needs quoting. <see cref="ReadBody"/>
/// only accepts a <see cref="RedirectedStdin"/> — there is no overload taking a raw
/// <see cref="TextReader"/> — and the only way to obtain one is
/// <see cref="RedirectedStdin.TryCreate"/>, which fails when stdin is not actually redirected.
/// That makes the guard a precondition of <em>obtaining the reader</em> rather than a call a
/// body-reading handler is merely supposed to remember to make first (§3 obligation 4, carried
/// from §1): "forgot to check <c>IsInputRedirected</c>" stops compiling, because there is nothing
/// to pass to <see cref="ReadBody"/> without having passed the check.
/// </summary>
internal static class StdinBodyReader
{
    internal static string ReadBody(RedirectedStdin stdin) => stdin.Reader.ReadToEnd();

    /// <summary>
    /// A stdin <see cref="TextReader"/> that is only ever constructed once the caller has proven
    /// stdin is redirected. The constructor is private, so the only way any code in this assembly
    /// can produce an instance is <see cref="TryCreate"/> — there is no back door that skips the
    /// check. This is a <see langword="class"/>, not a <see langword="struct"/>, deliberately: a
    /// <see langword="struct"/> keeps an implicit parameterless constructor even with every other
    /// constructor made private (<c>default(RedirectedStdin)</c> would still compile and produce a
    /// half-formed instance with a <see langword="null"/> <see cref="Reader"/>), which would leave
    /// exactly the back door this type exists to remove. A reference type has no such default.
    /// </summary>
    internal sealed class RedirectedStdin
    {
        private RedirectedStdin(TextReader reader) => Reader = reader;

        internal TextReader Reader { get; }

        /// <summary>
        /// Succeeds — producing a <see cref="RedirectedStdin"/> via <paramref name="stdin"/> — only
        /// when <paramref name="isInputRedirected"/> is <see langword="true"/>; otherwise returns
        /// the refusal a body-reading command must surface rather than blocking on
        /// <c>ReadToEnd</c> waiting for an EOF that interactive use will never send (a command that
        /// waits on a human at a TTY is interactive, which ADR-0001 forbids for every command).
        /// </summary>
        internal static CommandOutcome.Refusal? TryCreate(TextReader input, bool isInputRedirected, out RedirectedStdin? stdin)
        {
            if (isInputRedirected)
            {
                stdin = new RedirectedStdin(input);
                return null;
            }

            stdin = null;
            return new CommandOutcome.Refusal(
                "stdin-not-redirected",
                "this command reads its body from stdin; redirect it (a pipe or `< file`) rather than running interactively.");
        }
    }
}
