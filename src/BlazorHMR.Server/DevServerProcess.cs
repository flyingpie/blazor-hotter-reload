using System.Diagnostics;
using System.Net.Http;

namespace BlazorHMR.Server;

/// <summary>
/// Runs the app itself ("dotnet run") as a child process, and restarts it after every regular build.
/// <para>
/// For a Blazor WebAssembly app this is the DevServer, which only reads the static asset manifest on startup, so it needs a restart
/// after every build. For a desktop app there is no URL to wait for: the process itself is the app.
/// </para>
/// </summary>
internal sealed class DevServerProcess(ProjectLocation location, string? launchProfile, Uri? url) : IDisposable
{
	private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan DesktopStartGracePeriod = TimeSpan.FromSeconds(1);

	private Process? _process;

	public Uri? Url { get; } = url;

	/// <summary>
	/// Starts "dotnet run --no-build" for the app. The hot reload server already compiles, so there is no need to build again.
	/// </summary>
	public async Task StartAsync(CancellationToken token)
	{
		var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false, WorkingDirectory = location.ProjectDirectory };
		string[] args = launchProfile is null
			? ["run", "--no-build", "--project", location.ProjectFile]
			: ["run", "--no-build", "--project", location.ProjectFile, "--launch-profile", launchProfile];
		foreach (var arg in args)
		{
			startInfo.ArgumentList.Add(arg);
		}

		Log.Info(Url is null ? "Starting the app..." : $"Starting dev server ({Url})...");
		_process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the dev server.");

		// No URL (desktop): give it a moment to fail on startup, then assume it is fine. There is nothing to poll.
		if (Url is null)
		{
			await Task.Delay(DesktopStartGracePeriod, token).ConfigureAwait(false);
			if (_process.HasExited)
			{
				throw new InvalidOperationException($"The app exited with code {_process.ExitCode}.");
			}

			Log.Success("App started");
			return;
		}

		// Web app: poll until it answers. A browser refresh before the server is ready would show a connection error.
		using var http = new HttpClient();
		var stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < StartTimeout)
		{
			token.ThrowIfCancellationRequested();
			if (_process.HasExited)
			{
				throw new InvalidOperationException($"The dev server exited with code {_process.ExitCode}.");
			}

			try
			{
				using var response = await http.GetAsync(Url, token).ConfigureAwait(false);
				if (response.IsSuccessStatusCode)
				{
					Log.Success($"Dev server is up: {Url}");
					return;
				}
			}
			catch (HttpRequestException)
			{
				// Not yet
			}

			await Task.Delay(250, token).ConfigureAwait(false);
		}

		throw new TimeoutException($"The dev server did not come up at {Url}.");
	}

	/// <summary>
	/// Kills the app (and its child processes) and starts it again from the new build output.
	/// </summary>
	public async Task RestartAsync(CancellationToken token)
	{
		Stop();
		await StartAsync(token).ConfigureAwait(false);
	}

	public void Dispose() => Stop();

	/// <summary>
	/// Stops the app (and its child processes), e.g. before a build that has to replace its output files.
	/// </summary>
	public void Stop()
	{
		if (_process is null)
		{
			return;
		}

		if (!_process.HasExited)
		{
			_process.Kill(entireProcessTree: true);
			_process.WaitForExit();
		}

		_process.Dispose();
		_process = null;
	}
}
