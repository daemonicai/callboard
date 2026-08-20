namespace Callboard.Cards;

/// <summary>
/// The scope-shaped directory layout from design.md D3 / ADR-0003: where a card's file lives is
/// a function of its <see cref="CardScope"/>, not its <see cref="CardKind"/> — a rule promoted
/// from <see cref="CardScope.Change"/> to <see cref="CardScope.Repository"/> moves from
/// <c>changes/&lt;name&gt;/</c> into <see cref="RegisterDirectory"/> alongside every other
/// repository-scoped card, which is what makes that promotion a directory move rather than a
/// rewrite. This is the layout only — identity/filename allocation is 4.2, and archive itself
/// (a directory operation on <c>changes/&lt;name&gt;/</c> and nothing else) is later work; this
/// type exists so both can resolve the same directory the same way.
/// </summary>
internal static class CardLayout
{
    internal const string RegisterDirectory = "callboard/register/";
    internal const string DecisionsDirectory = "callboard/decisions/";

    internal static string ChangesDirectory(string changeName) =>
        $"callboard/changes/{changeName}/";

    /// <summary>
    /// Resolves the directory a card of the given <paramref name="scope"/> lives in.
    /// <paramref name="changeName"/> is required for <see cref="CardScope.Change"/> and
    /// <see cref="CardScope.Section"/> — a section lives inside the change that raised it, so
    /// both resolve into that change's directory — and is ignored otherwise. A missing change
    /// name for a scope that needs one is a caller error, not a runtime card-model refusal (that
    /// refusal table is 4.4's), so it throws rather than returning a result.
    /// </summary>
    internal static string DirectoryFor(CardScope scope, string? changeName) => scope.Match(
        onSection: () => ChangesDirectory(RequireChangeName(changeName)),
        onChange: () => ChangesDirectory(RequireChangeName(changeName)),
        onCapability: () => DecisionsDirectory,
        onRepository: () => RegisterDirectory);

    private static string RequireChangeName(string? changeName) =>
        string.IsNullOrEmpty(changeName)
            ? throw new ArgumentException("a change name is required to resolve a change- or section-scoped card's directory.", nameof(changeName))
            : changeName;
}
