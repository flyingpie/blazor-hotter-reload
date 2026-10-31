namespace BlazorHMR.Server.Compilation;

/// <summary>
/// A compiler error in a form that can be sent to the client and printed by the server (line/column are 1-based).
/// </summary>
public sealed record CompileDiagnostic(string File, int Line, int Column, string Id, string Message);
