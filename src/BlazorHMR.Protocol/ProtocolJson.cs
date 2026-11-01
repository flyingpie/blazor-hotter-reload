using System.Text.Json;

namespace BlazorHMR.Protocol;

/// <summary>
/// The JSON settings both sides use: web defaults (camelCase property names), and the polymorphic <c>type</c> discriminator
/// configured on <see cref="ServerMessage"/> and <see cref="ClientMessage"/>.
/// <para>
/// A single instance, so the client and server serialize exactly the same way. Don't mutate it after the first use:
/// <see cref="JsonSerializerOptions"/> is not thread-safe to change once it has been used.
/// </para>
/// <para>
/// Always serialize as <see cref="ServerMessage"/> or <see cref="ClientMessage"/>, not as the concrete record: the
/// <c>type</c> discriminator is only written when the declared type is the polymorphic base.
/// </para>
/// </summary>
public static class ProtocolJson
{
	public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
