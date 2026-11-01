using BlazorHMR.Server.Project;

namespace BlazorHMR.Server.Compilation;

/// <summary>
/// Watches two things and reports them to <see cref="ChangeClassifier"/>: source files (for hot reload), and the build output
/// (for a regular <c>dotnet build</c> that the user ran themselves, which requires starting over from the new output).
/// Coalesces a burst of events into one signal.
/// </summary>
internal sealed class SourceWatcher(ChangeClassifier changes, Action onChanges) : IDisposable
{
	private readonly Lock _timerLock = new();
	private readonly List<FileSystemWatcher> _watchers = [];

	private Timer? _debounce;

	/// <summary>
	/// Starts watching the hot projects and their build output.
	/// </summary>
	public void Start(IReadOnlyList<ProjectState> projects)
	{
		foreach (var state in projects)
		{
			var project = state.Project;
			var sources = new FileSystemWatcher(project.ProjectDirectory)
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
			};
			sources.Changed += (_, e) => OnSourceChanged(project, e.FullPath);
			sources.Created += (_, e) => OnSourceChanged(project, e.FullPath);
			sources.Deleted += (_, e) => OnSourceChanged(project, e.FullPath);
			sources.Renamed += (_, e) =>
			{
				OnSourceChanged(project, e.OldFullPath);
				OnSourceChanged(project, e.FullPath);
			};
			sources.Error += (_, e) => Log.Warning($"File watcher error: {e.GetException().Message}");
			sources.EnableRaisingEvents = true;
			_watchers.Add(sources);
			Log.Success($"Watching {project.ProjectDirectory}");
		}

		// Every hot project's assembly: a regular build of the app also rebuilds the libraries, and the app assembly itself might not
		// change (e.g. when only a library changed). Plus the WebAssembly boot config, which also changes when only static assets changed.
		var outputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		void AddOutput(string directory, string fileName)
		{
			if (!outputs.TryGetValue(directory, out var files))
			{
				outputs[directory] = files = new HashSet<string>(StringComparer.Ordinal);
			}

			files.Add(fileName);
		}

		foreach (var state in projects)
		{
			AddOutput(Path.GetDirectoryName(state.Project.ResponseFile)!, state.Project.AssemblyName + ".dll");
		}

		foreach (var (directory, files) in outputs)
		{
			var buildOutput = new FileSystemWatcher(directory)
			{
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
			};
			FileSystemEventHandler onChanged = (_, e) =>
			{
				if (e.Name is not null && files.Contains(e.Name))
				{
					OnBuildOutputChanged();
				}
			};
			buildOutput.Changed += onChanged;
			buildOutput.Created += onChanged;
			buildOutput.Renamed += (sender, e) => onChanged(sender, e);
			buildOutput.EnableRaisingEvents = true;
			_watchers.Add(buildOutput);
		}
	}

	public void Dispose()
	{
		_watchers.ForEach(w => w.Dispose());
		lock (_timerLock)
		{
			_debounce?.Dispose();
		}
	}

	private void OnSourceChanged(HotProject project, string path)
	{
		if (changes.SourceChanged(project, path) is { } delay)
		{
			Debounce(delay);
		}
	}

	private void OnBuildOutputChanged()
	{
		if (changes.BuildOutputChanged())
		{
			Debounce(ChangeClassifier.BuildDebounce);
		}
	}

	/// <summary>
	/// Coalesces a burst of file events into one signal. The timer is reused; changing its due time replaces the pending one.
	/// </summary>
	private void Debounce(TimeSpan delay)
	{
		lock (_timerLock)
		{
			_debounce ??= new Timer(_ => onChanges());
			_debounce.Change(delay, Timeout.InfiniteTimeSpan);
		}
	}
}
