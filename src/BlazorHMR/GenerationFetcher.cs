using System.Net.Http;
using BlazorHMR.FullSwap;

namespace BlazorHMR;

/// <summary>
/// Downloads the assemblies and symbols of a generation from the server. The server keeps a few recent generations, so a
/// client that reconnects can still fetch the one it is missing.
/// </summary>
internal sealed class GenerationFetcher : IDisposable
{
	private readonly HttpClient _http;

	public GenerationFetcher(Uri serverUrl) => _http = new HttpClient { BaseAddress = serverUrl };

	public async Task<IReadOnlyList<GenerationAssembly>> FetchAsync(
		int generation,
		IReadOnlyList<string> names,
		CancellationToken token
	)
	{
		return await Task.WhenAll(names.Select(name => FetchAsync(generation, name, token))).ConfigureAwait(true);
	}

	private async Task<GenerationAssembly> FetchAsync(int generation, string name, CancellationToken token)
	{
		var assembly = await _http
			.GetByteArrayAsync(new Uri($"hotreload/{generation}/{name}.dll", UriKind.Relative), token)
			.ConfigureAwait(true);
		var symbols = await _http
			.GetByteArrayAsync(new Uri($"hotreload/{generation}/{name}.pdb", UriKind.Relative), token)
			.ConfigureAwait(true);

		return new GenerationAssembly(name, assembly, symbols);
	}

	public void Dispose() => _http.Dispose();
}
