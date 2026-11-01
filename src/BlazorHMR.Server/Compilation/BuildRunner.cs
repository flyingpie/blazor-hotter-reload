using BlazorHMR.Protocol;
using BlazorHMR.Server.Project;
using BlazorHMR.Server.Server;

namespace BlazorHMR.Server.Compilation;

/// <summary>
/// Runs the real builds that the in-memory compilers can't do: scoped CSS (generated at build time), and starting over from
/// the output of a regular build.
/// </summary>
internal sealed class BuildRunner(
	ProjectLocation location,
	HotReloadHub hub,
	ChangeClassifier changes,
	DevServerProcess? devServer
)
{
	/// <summary>
	/// A regular build of the app happened (which also builds the libraries): start over from its output.
	/// </summary>
	public async Task RestartFromBuildAsync(Func<Task> reloadProjects, CancellationToken token)
	{
		Log.Info("Build output changed, starting over from the new build");
		await reloadProjects().ConfigureAwait(false);
		hub.Reset();

		// The dev server only picks up the new (fingerprinted) build output after a restart.
		if (devServer is null)
		{
			hub.Broadcast(new RebuiltMessage());
		}
		else
		{
			await devServer.RestartAsync(token).ConfigureAwait(false);
			hub.Broadcast(new ReloadMessage());
		}
	}

	/// <summary>
	/// Scoped CSS is generated at build time (the <c>b-...</c> attributes and the bundled stylesheet), so an in-memory compile
	/// can't apply it. Build the app, then start over from the new output. The build's own output events are ignored: this
	/// method handles that output itself.
	/// </summary>
	public async Task RebuildForScopedCssAsync(Func<Task> reloadProjects, CancellationToken token)
	{
		Log.Info("Scoped CSS changed, building the app...");
		hub.Broadcast(new CompilingMessage());

		changes.BeginScopedCssBuild();
		try
		{
			// A desktop app keeps its output files open (on Windows), so stop it before the build replaces them.
			devServer?.Stop();

			var (exitCode, _) = await DotnetCli
				.RunAsync(["build", location.ProjectFile, "-c", "Debug", "--nologo", "--no-restore"], echo: true)
				.ConfigureAwait(false);
			if (exitCode != 0)
			{
				Log.Error("Build failed, the app keeps running the previous version.");

				hub.Broadcast(
					new ErrorsMessage(
						[
							new CompileDiagnostic(
								Path.GetFileName(location.ProjectFile),
								1,
								1,
								"BUILD",
								"Scoped CSS build failed, see the server console."
							),
						]
					)
				);

				// We stopped it above, so bring the previous version back.
				if (devServer is not null)
				{
					await devServer.StartAsync(token).ConfigureAwait(false);
				}

				return;
			}
		}
		finally
		{
			changes.EndScopedCssBuild();
		}

		await RestartFromBuildAsync(reloadProjects, token).ConfigureAwait(false);
	}
}
