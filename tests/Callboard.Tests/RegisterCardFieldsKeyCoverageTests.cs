using System.Reflection;
using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §7 block C remediation: the reviewer reproduced a real corruption bug against production
/// code — <c>owed_by</c>/<c>supersedes</c>/<c>superseded_by</c> were spelled in
/// <c>CardFileWriter</c>'s emission but absent from <c>CardFileParser</c>'s known-key set, so the
/// parser filed them as <see cref="CardFile.UnknownFrontmatterFields"/>, which the writer then
/// re-emitted <em>alongside</em> the known-field line it wrote from <see cref="RegisterCardFields"/>
/// itself — every parse-then-write cycle duplicated the line, without bound.
///
/// <para>
/// The structural fix is <see cref="RegisterCardFieldKeys.All"/> — the one declaration both
/// <c>CardFileWriter</c> and <c>CardFileParser</c> now read from, so the two can no longer name the
/// same field two different ways. What that alone cannot catch: a <em>new</em> property added to
/// <see cref="RegisterCardFields"/> without ever being added to <see cref="RegisterCardFieldKeys.All"/>
/// at all. This file closes that gap the way <c>CardCommentImmutabilityTests</c>'s own complete
/// method inventory does — by reflecting over the type's actual surface, enumerated from the code,
/// not hand-listed, so a forgotten field fails this test rather than silently rotting the record.
/// </para>
/// </summary>
public sealed class RegisterCardFieldsKeyCoverageTests
{
    [Fact]
    public void EveryRegisterCardFieldsProperty_HasAMatchingRegisterCardFieldKeysConstant()
    {
        // EqualityContract is the compiler-synthesised property every record type carries
        // (used by its own generated Equals) — not a frontmatter field, excluded by name rather
        // than by a broader filter that could as easily hide a real field again.
        var properties = typeof(RegisterCardFields)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static property => property.Name)
            .Where(static name => !string.Equals(name, "EqualityContract", StringComparison.Ordinal))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        var expectedWireKeys = properties.Select(ToSnakeCase).OrderBy(static key => key, StringComparer.Ordinal).ToList();
        var actualWireKeys = RegisterCardFieldKeys.All.OrderBy(static key => key, StringComparer.Ordinal).ToList();

        Assert.Equal(expectedWireKeys, actualWireKeys);
    }

    [Fact]
    public void RegisterCardFieldKeysAll_HasNoDuplicates()
    {
        Assert.Equal(RegisterCardFieldKeys.All.Count, RegisterCardFieldKeys.All.Distinct(StringComparer.Ordinal).Count());
    }

    private static string ToSnakeCase(string pascalCaseName)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < pascalCaseName.Length; i++)
        {
            var c = pascalCaseName[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
