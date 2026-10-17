namespace Dtr.Blazor.HotReload.Server.Compiling;

public sealed record CompileDiagnostic(string File, int Line, int Column, string Id, string Message);
