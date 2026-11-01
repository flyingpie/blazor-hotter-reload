using System.Text.Json.Serialization;

namespace BlazorHMR.Protocol;

/// <summary>
/// A message from the hot reload server. The <c>type</c> property selects the concrete message, so a client can deserialize
/// straight into the right shape instead of one record with a field for every message type.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), MessageTypes.Hello)]
[JsonDerivedType(typeof(CompilingMessage), MessageTypes.Compiling)]
[JsonDerivedType(typeof(NoChangesMessage), MessageTypes.NoChanges)]
[JsonDerivedType(typeof(GenerationMessage), MessageTypes.Generation)]
[JsonDerivedType(typeof(PatchMessage), MessageTypes.Patch)]
[JsonDerivedType(typeof(ErrorsMessage), MessageTypes.Errors)]
[JsonDerivedType(typeof(CssMessage), MessageTypes.Css)]
[JsonDerivedType(typeof(RebuiltMessage), MessageTypes.Rebuilt)]
[JsonDerivedType(typeof(ReloadMessage), MessageTypes.Reload)]
public abstract record ServerMessage;

/// <summary>
/// The server's state when a client (re)connects: the current generation, the in-place patches on top of it, and the
/// stylesheets that changed. A client that missed messages catches up from this.
/// </summary>
/// <param name="ProtocolVersion">The server's <see cref="Protocol.ProtocolVersion.Current"/>.</param>
/// <param name="Session">The server's session id; a new session means generation numbers started over.</param>
/// <param name="Generation">The current generation.</param>
/// <param name="Assemblies">The assembly names of the current generation.</param>
/// <param name="Patches">The in-place patches applied on top of the current generation, in order.</param>
/// <param name="Css">The stylesheets that changed in this session.</param>
public sealed record HelloMessage(
	int ProtocolVersion,
	string Session,
	int Generation,
	IReadOnlyList<string> Assemblies,
	IReadOnlyList<ServerMessage> Patches,
	IReadOnlyList<string> Css
) : ServerMessage;

/// <summary>
/// A compile is running.
/// </summary>
public sealed record CompilingMessage : ServerMessage;

/// <summary>
/// The changes didn't affect the compiled code, so there is nothing to apply.
/// </summary>
public sealed record NoChangesMessage : ServerMessage;

/// <summary>
/// A complete new generation of the app, for a full swap.
/// </summary>
/// <param name="Generation">The generation number.</param>
/// <param name="Assemblies">The assembly names to download for this generation.</param>
public sealed record GenerationMessage(int Generation, IReadOnlyList<string> Assemblies) : ServerMessage;

/// <summary>
/// An in-place patch for the current generation.
/// </summary>
/// <param name="Session">The server's session id.</param>
/// <param name="Generation">The generation this patch applies to.</param>
/// <param name="Sequence">The patch's sequence number within the generation.</param>
/// <param name="Changes">The per-assembly changes.</param>
public sealed record PatchMessage(
	string Session,
	int Generation,
	int Sequence,
	IReadOnlyList<PatchChange> Changes
) : ServerMessage;

/// <summary>
/// The code doesn't compile; the previous version keeps running.
/// </summary>
public sealed record ErrorsMessage(IReadOnlyList<CompileDiagnostic> Diagnostics) : ServerMessage;

/// <summary>
/// A stylesheet changed and has to be reloaded.
/// </summary>
public sealed record CssMessage(string Path) : ServerMessage;

/// <summary>
/// The app was rebuilt outside the hot reload server; the page has to start over.
/// </summary>
public sealed record RebuiltMessage : ServerMessage;

/// <summary>
/// The page has to reload.
/// </summary>
public sealed record ReloadMessage : ServerMessage;
