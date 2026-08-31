namespace Callboard.Cards;

/// <summary>
/// One first-time finding from a <c>section verdict</c> invocation (§8a block B, work-lifecycle: "A
/// finding raised for the first time has no card to own it and SHALL create a new <c>block</c> card
/// in that section, carrying the finding as its brief"). Built by parsing one <c>--finding-new
/// &lt;manifest-file&gt;</c> occurrence — see <see cref="NewFindingCardManifest"/> for the fenced,
/// self-contained file format a manifest is read from, and the DEVLOG (§8a block B, "architect:
/// accept the design, reject the one-new-finding cap" and the worker's reply) for why one
/// self-describing file per finding replaced an earlier four-flag, positionally-zipped design: a
/// caller can compose any number of <c>--finding-new</c> occurrences with nothing to mis-zip,
/// because there is no second flag whose <em>n</em>-th occurrence has to correspond to this one's.
/// Carries everything <see cref="CardStore.RecordSectionVerdictUnderExistingLock"/> needs to create
/// the card — except a remediation card's identity is not tool-allocated at all here: <see
/// cref="Key"/>, not a minted <c>CardFrontmatter.Id</c>, is what a later verdict looks up to decide
/// whether this finding already has an owner (work-lifecycle: "each finding SHALL be routed by
/// whether a card already owns it"). The card's own <c>id</c> is still allocated the ordinary way,
/// through <see cref="CardIdentityAllocator"/>, inside the locked write, and its file is named for
/// that id via <see cref="CardLayout.FileNameFor"/> the same "container, then allocate, then
/// FileNameFor" way every other card-minting door follows (14.5-remediation, §14 supervisor
/// finding, second round: this type no longer carries the caller-supplied <c>FilePath</c> its own
/// manifest's <c>new-card-file</c> header used to name — see <see cref="NewFindingCardManifest"/>'s
/// own doc comment). <see cref="Key"/> and <c>id</c> are deliberately different things: the key is
/// the supervisor's own stable name for the defect, the id is this codebase's own card identity,
/// and nothing requires the two to look alike.
/// </summary>
/// <param name="Key">The supervisor's own stable identifier for this finding — never empty or
/// whitespace-only (checked while parsing the manifest, file-decidable, the same discipline every
/// other caller-supplied wire value in this codebase applies). Recorded on the created card as
/// <see cref="BlockCardFields.FindingKey"/>, so a later verdict reporting the same finding still
/// unresolved can find the card that owns it by this same text.</param>
/// <param name="Title">The new card's title.</param>
/// <param name="Body">The new card's body — the finding text itself, which work-lifecycle calls
/// "the finding as its brief". Everything in the manifest file after its closing fence, verbatim,
/// never as a quoted argument (ADR-0001: card bodies come from a file or stdin, never inline).</param>
internal sealed record NewFindingCardRequest(string Key, string Title, string Body)
{
    /// <summary>The one predicate <see cref="Callboard.Cli.CommandParser"/>'s <c>section verdict</c>
    /// parse arm checks before this type is ever constructed, and the one <see cref="CardStore.
    /// RecordSectionVerdictUnderExistingLock"/> would otherwise have to trust blindly — the same
    /// "argv-decidable, checked at the one door" discipline <see cref="GateResult.IsValidLabel"/>
    /// and <see cref="BlockCardFields.IsValidListItem"/> already apply to their own wire
    /// values.</summary>
    internal static bool IsValidKey(string key) => !string.IsNullOrWhiteSpace(key);
}
