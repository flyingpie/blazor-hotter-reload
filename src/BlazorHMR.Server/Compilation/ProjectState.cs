using BlazorHMR.Server.Project;

namespace BlazorHMR.Server.Compilation;

/// <summary>
/// What the worker knows about one hot project: whether its last compile failed, and whether the client runs a different
/// version than the build output (through an in-place patch or a full swap).
/// </summary>
internal sealed class ProjectState(HotProject project)
{
	public HotProject Project { get; } = project;

	public bool HasErrors { get; set; }

	/// <summary>
	/// Whether the client runs a different version of this project than the build output.
	/// </summary>
	public bool ChangedSinceBuild { get; set; }
}
