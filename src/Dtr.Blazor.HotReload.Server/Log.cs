namespace Dtr.Blazor.HotReload.Server;

/// <summary>
/// Plain console output, this is a dev tool.
/// </summary>
internal static class Log
{
	// The worker, the WebSocket clients, and the dev server all log from different threads.
	private static readonly Lock ConsoleLock = new();

	public static void Info(string message) => Write(ConsoleColor.Gray, message);

	public static void Success(string message) => Write(ConsoleColor.Green, message);

	public static void Warning(string message) => Write(ConsoleColor.Yellow, message);

	public static void Error(string message) => Write(ConsoleColor.Red, message);

	private static void Write(ConsoleColor color, string message)
	{
		lock (ConsoleLock)
		{
			Console.ForegroundColor = color;
			Console.WriteLine($"[hotreload {DateTime.Now:HH:mm:ss}] {message}");
			Console.ResetColor();
		}
	}
}
