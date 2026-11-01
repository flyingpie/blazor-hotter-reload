namespace BlazorHMR.Protocol;

/// <summary>
/// The version of the wire protocol. The client and the server ship together, but a mismatch is logged instead of failing
/// with a confusing deserialization error.
/// </summary>
public static class ProtocolVersion
{
	/// <summary>
	/// Bump this whenever a message shape changes in a way that older peers can't handle.
	/// </summary>
	public const int Current = 1;
}
