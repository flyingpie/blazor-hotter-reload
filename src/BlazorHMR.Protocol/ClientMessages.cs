using System.Text.Json.Serialization;

namespace BlazorHMR.Protocol;

/// <summary>
/// A message from the app to the hot reload server. The <c>type</c> property selects the concrete message.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CapabilitiesMessage), MessageTypes.Capabilities)]
[JsonDerivedType(typeof(RestartMessage), MessageTypes.Restart)]
[JsonDerivedType(typeof(PatchFailedMessage), MessageTypes.PatchFailed)]
public abstract record ClientMessage;

/// <summary>
/// What kind of in-place patches this runtime can apply, sent right after every (re)connect.
/// </summary>
/// <param name="ProtocolVersion">The client's <see cref="Protocol.ProtocolVersion.Current"/>.</param>
/// <param name="Supported">Whether the runtime supports in-place patches at all.</param>
/// <param name="Capabilities">The capability list the runtime reports (space-separated).</param>
public sealed record CapabilitiesMessage(int ProtocolVersion, bool Supported, string Capabilities) : ClientMessage;

/// <summary>
/// The client asks for a full swap (a new generation, compiled from scratch).
/// </summary>
public sealed record RestartMessage : ClientMessage;

/// <summary>
/// The client couldn't apply an in-place patch; the server should do a full swap instead.
/// </summary>
public sealed record PatchFailedMessage : ClientMessage;
