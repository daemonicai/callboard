using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// <c>change export &lt;change-name&gt; --out &lt;path&gt;</c>'s success result (§11 block C) —
/// <see cref="SectionExportResult"/>'s whole-change sibling. Same "report the path, not the
/// document" shape and the same reason.
/// </summary>
internal sealed class ChangeExportResult : ICommandResult
{
    [JsonPropertyName("changeName")]
    public required string ChangeName { get; init; }

    [JsonPropertyName("outputPath")]
    public required string OutputPath { get; init; }

    [JsonPropertyName("cardCount")]
    public required int CardCount { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.ChangeExportResult);
}
