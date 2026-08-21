using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// Source-generated JSON contract for every type the CLI serialises. NativeAOT forbids
/// reflection-based serialization (ADR-0002 / design.md D2); registering every emitted type
/// here, rather than calling the reflection-based <c>JsonSerializer</c> overloads, is what
/// keeps that true as verbs are added in later sections.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliEnvelope))]
[JsonSerializable(typeof(CliRefusal))]
[JsonSerializable(typeof(VersionResult))]
[JsonSerializable(typeof(IndexRebuildResult))]
[JsonSerializable(typeof(IndexRebuildFailure))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
