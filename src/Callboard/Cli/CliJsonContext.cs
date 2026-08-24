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
[JsonSerializable(typeof(IndexRebuildIdentityCounterViolation))]
[JsonSerializable(typeof(BlockTransitionResult))]
[JsonSerializable(typeof(BlockGateResult))]
[JsonSerializable(typeof(BlockedByResult))]
[JsonSerializable(typeof(BlockApproveResult))]
[JsonSerializable(typeof(BlockApprovalClaimResult))]
[JsonSerializable(typeof(SectionVerdictResult))]
[JsonSerializable(typeof(SectionCloseResult))]
[JsonSerializable(typeof(SectionStatusResult))]
[JsonSerializable(typeof(FindingRecordResult))]
[JsonSerializable(typeof(FindingStatusResult))]
[JsonSerializable(typeof(CardCreateResult))]
[JsonSerializable(typeof(CardRegisterDischargeResult))]
[JsonSerializable(typeof(DecisionSupersedeResult))]
[JsonSerializable(typeof(ChangeArchiveResult))]
[JsonSerializable(typeof(RulePromoteResult))]
[JsonSerializable(typeof(RuleAuthorResult))]
[JsonSerializable(typeof(RuleCompactResult))]
[JsonSerializable(typeof(RuleProposeCompactResult))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
