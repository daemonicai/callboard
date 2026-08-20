using System.Text.Json;

namespace Callboard.Cli;

/// <summary>
/// A command's success payload. Every type carried by <see cref="CommandOutcome.Success"/> must
/// implement this so it can serialise itself against its own source-generated
/// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/> in
/// <see cref="CliJsonContext"/>. This is what turns "a new result type forgot its JSON mapping"
/// from a runtime <see cref="NotSupportedException"/> at first invocation into a compile error:
/// <see cref="CommandOutcome.Success"/> only accepts an <see cref="ICommandResult"/>, so a type
/// that does not implement <see cref="ToJsonElement"/> cannot be constructed into a result at
/// all, and there is no <c>object</c>-typed fallback anywhere on the path from result to
/// envelope.
/// </summary>
internal interface ICommandResult
{
    JsonElement ToJsonElement();
}
