using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dtr.Blazor.HotReload.Server;

/// <summary>
/// The server process: a minimal web host with the WebSocket endpoint, the generation download endpoint, and the compile
/// worker. It only listens on localhost; the app connects to it.
/// </summary>
internal static class HotReloadServer
{
	/// <summary>
	/// Starts the web host, then (optionally) the app's dev server, then the compile worker. Runs until the process is stopped.
	/// </summary>
	public static async Task RunAsync(ProjectLocation location, int port, DevServerProcess? devServer)
	{
		var hub = new HotReloadHub();
		using var worker = new CompileWorker(location, hub, devServer);

		var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
		builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddCors();

		await using var app = builder.Build();

		// Only listening on localhost, and only serving the app's own assembly, so any origin is fine.
		app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
		app.UseWebSockets();

		// The client's WebSocket. Keeping the HTTP host tiny means the server starts fast and has no app dependencies.
		app.Map(
			"/hotreload",
			async context =>
			{
				if (!context.WebSockets.IsWebSocketRequest)
				{
					context.Response.StatusCode = StatusCodes.Status400BadRequest;
					return;
				}

				// Also stop when the server stops, otherwise shutting down waits for the clients to disconnect.
				var stopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
				using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
				using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
				await hub.HandleAsync(socket, cancellation.Token).ConfigureAwait(false);
			}
		);
		app.MapGet("/hotreload/{generation:int}/{file}", (int generation, string file) => Serve(hub, generation, file));

		await app.StartAsync().ConfigureAwait(false);
		Log.Success($"Listening on http://127.0.0.1:{port}");

		var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
		try
		{
			if (devServer is not null)
			{
				await devServer.StartAsync(lifetime.ApplicationStopping).ConfigureAwait(false);
			}

			await worker.RunAsync(lifetime.ApplicationStopping).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Shutting down
		}

		await app.StopAsync().ConfigureAwait(false);
	}

	/// <summary>
	/// Serves "{assemblyName}.dll" or "{assemblyName}.pdb" of a generation.
	/// </summary>
	private static IResult Serve(HotReloadHub hub, int generation, string file)
	{
		var extension = Path.GetExtension(file);
		if (extension is not (".dll" or ".pdb") || hub.GetAssembly(generation, Path.GetFileNameWithoutExtension(file)) is not { } value)
		{
			return Results.NotFound();
		}

		return Results.Bytes(extension == ".pdb" ? value.Symbols : value.Assembly, "application/octet-stream");
	}
}
