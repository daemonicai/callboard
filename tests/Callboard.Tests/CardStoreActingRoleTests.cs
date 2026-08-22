using System.Reflection;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §5 remediation (DEVLOG §5 finding B1): six CLI write verbs (<c>block transition</c>, <c>block
/// gate</c>, <c>block add-blocker</c>, <c>block remove-blocker</c>, <c>section verdict</c>,
/// <c>section close</c>) each require and validate <c>--role</c> during parse, but three of the six
/// <see cref="CardStore"/> methods behind them were built with no acting-role parameter at all, so
/// the role reached neither the card nor the JSON envelope. The fix threaded <see cref="CardOwner"/>
/// through all six.
///
/// <para>
/// <b>This is a regression lock on those six, stated as a convention, not a structural
/// guarantee (§3's rule about honest claims).</b> Reflection can prove these six named methods
/// still take a <see cref="CardOwner"/> today; it cannot prove a future seventh write verb will —
/// nothing stops a new <c>CardStore</c> method from being added, and this codebase's own list of
/// "expected members" tests (<see cref="CardCommentImmutabilityTests.
/// CardStore_EntireStaticMethodSurface_IsExplicitlyAccountedFor"/>) is what catches a new member
/// existing at all; it does not itself check what parameters that member takes. What this test
/// actually closes is narrower and real: none of these six can silently lose its acting-role
/// parameter again without this test going red first.
/// </para>
/// </summary>
public sealed class CardStoreActingRoleTests
{
    [Fact]
    public void TheSixWriteVerbMethods_EachTakeACardOwnerActingRoleParameter()
    {
        var methods = new[]
        {
            nameof(CardStore.ApplyBlockTransition),
            nameof(CardStore.RecordGateResult),
            nameof(CardStore.AddBlockedBy),
            nameof(CardStore.RemoveBlockedBy),
            nameof(CardStore.RecordSectionVerdict),
            nameof(CardStore.CloseSection),
        };

        foreach (var methodName in methods)
        {
            var method = typeof(CardStore).GetMethod(
                methodName, BindingFlags.NonPublic | BindingFlags.Static, [
                    typeof(string), typeof(string), .. ExtraParameterTypesFor(methodName),
                ]);

            Assert.True(
                method is not null,
                $"expected CardStore.{methodName}(string cardsRoot, string filePath, ...) — the well-known signature shape moved.");

            Assert.Contains(
                method!.GetParameters(),
                parameter => parameter.ParameterType == typeof(CardOwner));
        }
    }

    /// <summary>The domain-specific parameters between <c>(cardsRoot, filePath, ...)</c> and the
    /// shared <c>(..., DateTimeOffset timestamp, TimeSpan lockTimeout, string? changeName)</c>
    /// tail every one of these six methods shares — named explicitly so the overload lookup above
    /// resolves the one real method per name rather than guessing an arity.</summary>
    private static Type[] ExtraParameterTypesFor(string methodName) => methodName switch
    {
        nameof(CardStore.ApplyBlockTransition) =>
            [typeof(string), typeof(CardOwner), typeof(DateTimeOffset), typeof(string), typeof(TimeSpan), typeof(string)],
        nameof(CardStore.RecordGateResult) =>
            [typeof(string), typeof(int), typeof(CardOwner), typeof(DateTimeOffset), typeof(TimeSpan), typeof(string)],
        nameof(CardStore.AddBlockedBy) or nameof(CardStore.RemoveBlockedBy) =>
            [typeof(string), typeof(CardOwner), typeof(DateTimeOffset), typeof(TimeSpan), typeof(string)],
        nameof(CardStore.RecordSectionVerdict) =>
            [typeof(SectionVerdict), typeof(string), typeof(string), typeof(CardOwner), typeof(DateTimeOffset), typeof(TimeSpan), typeof(string)],
        nameof(CardStore.CloseSection) =>
            [typeof(CardOwner), typeof(DateTimeOffset), typeof(TimeSpan), typeof(string)],
        _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, "unaccounted-for method name — add its parameter shape above."),
    };
}
