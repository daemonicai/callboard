using System.Collections.Immutable;

namespace Callboard.Cards;

/// <summary>
/// The three extent-declaration forms findings' "Extent is declared, widest by default" names, in
/// the order the spec states them as a preference: <see cref="Instrument"/> — a re-runnable
/// command whose extent is what re-running it covers, the preferred form for asserting an absence
/// across a subtree; <see cref="Explicit"/> — an explicitly declared set of paths, line ranges or
/// symbols; and <see cref="BlockScope"/> — the scope of the block that raised the finding, which
/// is also what an <em>undeclared</em> extent resolves to (<see cref="FindingCardFields.Extent"/>'s
/// own doc comment). Modelled as a closed union for the same reason as <see cref="CardKind"/> — see
/// that type's doc comment — with <see cref="Instrument"/> and <see cref="Explicit"/> carrying a
/// payload the same way <see cref="GateStatus.Recorded"/> does.
///
/// <para>
/// <b>Narrowing is explicit by construction.</b> There is no path from an undeclared extent to
/// <see cref="Explicit"/> other than a caller stating it: the only way to obtain an
/// <see cref="Explicit"/> value is <see cref="Explicit"/> itself, which requires at least one item,
/// and none of the other two cases ever produce one. §6 block A carries this vocabulary and the
/// default; enforcing that a narrowed extent was actually intended (rather than merely
/// constructible) is a later block's job, the same "block A carries the vocabulary, not the
/// enforcement" convention <see cref="BlockCardFields"/>'s own doc comment states.
/// </para>
/// </summary>
internal abstract record FindingExtent
{
    private FindingExtent()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<string, TResult> onInstrument,
        Func<ImmutableArray<string>, TResult> onExplicit,
        Func<TResult> onBlockScope);

    /// <summary>A re-runnable <paramref name="command"/> whose extent is what re-running it covers.
    /// Never empty or whitespace-only — see this type's own validating accessor.</summary>
    internal static FindingExtent Instrument(string command) => new InstrumentCase(command);

    /// <summary>An explicitly declared set of paths, line ranges or symbols. Never empty, and never
    /// contains an empty or whitespace-only item — see this type's own validating accessor, the same
    /// <see cref="BlockCardFields.IsValidListItem"/> discipline <see cref="BlockCardFields.Tasks"/>
    /// already applies.</summary>
    internal static FindingExtent Explicit(IReadOnlyList<string> items) => new ExplicitCase(items);

    /// <summary>The scope of the block that raised the finding — both an explicit declaration and
    /// what an undeclared extent defaults to.</summary>
    internal static readonly FindingExtent BlockScope = new BlockScopeCase();

    private sealed record InstrumentCase : FindingExtent
    {
        // Initialized to a placeholder here only to satisfy definite-assignment nullability
        // analysis across the constructor/init-accessor boundary — the constructor below always
        // overwrites it through the validating Command accessor before the value escapes.
        private readonly string _command = string.Empty;

        internal string Command
        {
            get => _command;
            init => _command = RequireNonEmpty(value, nameof(Command));
        }

        internal InstrumentCase(string command)
        {
            Command = command;
        }

        internal override TResult Match<TResult>(Func<string, TResult> onInstrument, Func<ImmutableArray<string>, TResult> onExplicit, Func<TResult> onBlockScope) =>
            onInstrument(Command);
    }

    private sealed record ExplicitCase : FindingExtent
    {
        private readonly ImmutableArray<string> _items;

        internal ImmutableArray<string> Items
        {
            get => _items;
            init => _items = RequireNonEmptyWithNoBlankItems(value);
        }

        internal ExplicitCase(IReadOnlyList<string> items)
        {
            // .ToImmutableArray() copies items's current contents now, at construction time — the
            // same bypass BlockCardFields.Tasks's own doc comment explains: a caller's later
            // mutation of a retained List<T> source cannot reach the value built here.
            Items = items.ToImmutableArray();
        }

        internal override TResult Match<TResult>(Func<string, TResult> onInstrument, Func<ImmutableArray<string>, TResult> onExplicit, Func<TResult> onBlockScope) =>
            onExplicit(Items);

        // ImmutableArray<T>'s own Equals compares the underlying array by reference, not
        // element-wise — same reason BlockCardFields overrides Equals for Tasks/BlockedBy.
        public bool Equals(ExplicitCase? other) => other is not null && Items.SequenceEqual(other.Items);

        public override int GetHashCode() => Items.Length;

        private static ImmutableArray<string> RequireNonEmptyWithNoBlankItems(ImmutableArray<string> items)
        {
            if (items.Length == 0)
            {
                throw new ArgumentException(
                    "an explicit extent must declare at least one path, line range or symbol — an empty " +
                    "declaration is indistinguishable from no declaration at all, which defaults to block scope.",
                    nameof(items));
            }

            foreach (var item in items)
            {
                if (!BlockCardFields.IsValidListItem(item))
                {
                    throw new ArgumentException(
                        "an explicit extent item cannot be empty or whitespace-only.", nameof(items));
                }
            }

            return items;
        }
    }

    private sealed record BlockScopeCase : FindingExtent
    {
        internal override TResult Match<TResult>(Func<string, TResult> onInstrument, Func<ImmutableArray<string>, TResult> onExplicit, Func<TResult> onBlockScope) =>
            onBlockScope();
    }

    private static string RequireNonEmpty(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("an instrument extent's command cannot be empty or whitespace-only.", paramName);
        }

        return value;
    }
}
