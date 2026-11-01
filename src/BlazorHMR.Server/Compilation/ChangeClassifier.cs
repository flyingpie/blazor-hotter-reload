using BlazorHMR.Server.Project;

namespace BlazorHMR.Server.Compilation;

/// <summary>
/// Classifies file changes and accumulates them into a <see cref="ChangeSet"/> for the worker loop. The watchers call it from
/// their own threads; the worker takes one consistent snapshot per iteration.
/// <para>
/// CSS and other wwwroot assets are handled separately from source changes; project files and scoped CSS need a regular build
/// (which cannot be done by the in-memory compiler).
/// </para>
/// </summary>
internal sealed class ChangeClassifier
{
	internal static readonly TimeSpan SourceDebounce = TimeSpan.FromMilliseconds(100);
	internal static readonly TimeSpan ScopedCssDebounce = TimeSpan.FromMilliseconds(300);

	// The build writes the intermediate assembly before copying everything to the output directory, give it some time.
	internal static readonly TimeSpan BuildDebounce = TimeSpan.FromSeconds(2);

	// Our own build's file notifications can arrive after the process exits; ignore them for a moment too.
	private static readonly TimeSpan OwnBuildGracePeriod = BuildDebounce + TimeSpan.FromSeconds(1);

	private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

	private readonly Lock _lock = new();
	private readonly HashSet<string> _changedCss = new(StringComparer.Ordinal);
	private readonly HashSet<string> _changedProjects = new(StringComparer.Ordinal);
	private readonly HashSet<string> _changedFiles = new(StringComparer.Ordinal);

	private bool _assetsChanged;
	private bool _buildChanged;
	private bool _scopedCssChanged;
	private bool _fullSwapRequested;
	private bool _buildingScopedCss;
	private DateTime _ignoreBuildOutputUntil;

	/// <summary>
	/// Classifies a file change in <paramref name="project"/>. Returns the debounce delay when the change matters, or
	/// <see langword="null"/> when it can be ignored.
	/// </summary>
	public TimeSpan? SourceChanged(HotProject project, string path)
	{
		if (IsIgnored(project.ProjectDirectory, path))
		{
			return null;
		}

		var scopedCss = IsScopedCss(path);

		lock (_lock)
		{
			var extension = Path.GetExtension(path);
			var wwwroot = Path.Combine(project.ProjectDirectory, "wwwroot");
			if (scopedCss)
			{
				_scopedCssChanged = true;
			}
			else if (path.StartsWith(wwwroot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
			{
				if (extension == ".css")
				{
					_changedCss.Add(Path.GetRelativePath(wwwroot, path).Replace('\\', '/'));
				}
				else if (!Directory.Exists(path))
				{
					_assetsChanged = true;
				}
			}
			else if (ProjectInputs.IsLiveFile(project.ProjectDirectory, path) || Directory.Exists(path) || extension.Length == 0)
			{
				// Also directories: a deleted/renamed directory takes its files with it.
				_changedProjects.Add(project.ProjectFile);
				_changedFiles.Add(path);
			}
			else if (extension is ".csproj" or ".props" or ".targets")
			{
				Log.Warning($"{Path.GetFileName(path)} changed, this needs a regular build (which hot reload will pick up)");
				return null;
			}
			else
			{
				return null;
			}
		}

		return scopedCss ? ScopedCssDebounce : SourceDebounce;
	}

	/// <summary>
	/// A regular build wrote a new assembly/boot config. Returns whether the worker should handle it; our own scoped CSS build
	/// is handled by <see cref="BuildRunner"/> instead.
	/// </summary>
	public bool BuildOutputChanged()
	{
		lock (_lock)
		{
			if (_buildingScopedCss || DateTime.UtcNow < _ignoreBuildOutputUntil)
			{
				return false;
			}

			_buildChanged = true;
			return true;
		}
	}

	/// <summary>
	/// The client asked for a full swap (a restart, or a patch it couldn't apply).
	/// </summary>
	public void RequestFullSwap()
	{
		lock (_lock)
		{
			_fullSwapRequested = true;
		}
	}

	/// <summary>
	/// Marks the start of a scoped CSS build, so its own output events are ignored.
	/// </summary>
	public void BeginScopedCssBuild()
	{
		lock (_lock)
		{
			_buildingScopedCss = true;
		}
	}

	/// <summary>
	/// Marks the end of a scoped CSS build: clear the build flag and ignore the build's own output events for a moment.
	/// </summary>
	public void EndScopedCssBuild()
	{
		lock (_lock)
		{
			_buildingScopedCss = false;
			_buildChanged = false;
			_ignoreBuildOutputUntil = DateTime.UtcNow + OwnBuildGracePeriod;
		}
	}

	/// <summary>
	/// Takes everything that changed since the previous snapshot, and resets the sets, so changes that arrive while the worker
	/// compiles are handled in the next iteration.
	/// </summary>
	public ChangeSet Take()
	{
		lock (_lock)
		{
			var change = new ChangeSet(
				_assetsChanged,
				_buildChanged,
				_scopedCssChanged,
				_fullSwapRequested,
				[.. _changedCss],
				[.. _changedProjects],
				[.. _changedFiles]
			);

			_assetsChanged = _buildChanged = _scopedCssChanged = _fullSwapRequested = false;
			_changedCss.Clear();
			_changedProjects.Clear();
			_changedFiles.Clear();

			return change;
		}
	}

	/// <summary>
	/// Changes under bin/obj, node_modules, or hidden directories are not source changes, and neither are the temporary files
	/// that editors create while saving.
	/// </summary>
	private static bool IsIgnored(string projectDirectory, string path)
	{
		var relative = Path.GetRelativePath(projectDirectory, path);
		var parts = relative.Split(PathSeparators);
		if (parts[0] is "bin" or "obj" or "node_modules" || parts.Any(p => p.StartsWith('.')))
		{
			return true;
		}

		// Temporary files of editors (vim, JetBrains, VS, emacs, ...).
		var name = parts[^1];
		return name.EndsWith('~')
			|| name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".swp", StringComparison.Ordinal)
			|| name.Contains("___jb_", StringComparison.Ordinal)
			|| name.StartsWith('~');
	}

	/// <summary>
	/// A scoped CSS file (<c>Component.razor.css</c> or <c>Page.cshtml.css</c>). The SDK generates the scoped attributes and
	/// bundled stylesheet at build time, so the watcher treats it as a build request, not as a source file.
	/// </summary>
	private static bool IsScopedCss(string path)
	{
		var name = Path.GetFileName(path);
		return name.EndsWith(".razor.css", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".cshtml.css", StringComparison.OrdinalIgnoreCase);
	}
}

/// <summary>
/// One snapshot of everything the watchers and client messages reported since the previous one.
/// </summary>
internal readonly record struct ChangeSet(
	bool Assets,
	bool Build,
	bool ScopedCss,
	bool FullSwap,
	string[] Css,
	string[] Projects,
	string[] Files
);
