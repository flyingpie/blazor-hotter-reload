using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Dtr.Blazor.HotReload.Server.Compiling;

/// <summary>
/// A Roslyn workspace with the hot projects (the app and the libraries it ships), kept in sync with the file system.
/// <para>
/// Roslyn compiles incrementally: only changed documents are re-parsed, and the source generators (Razor!) run incrementally too.
/// </para>
/// </summary>
internal sealed class HotSolution
{
	private const string TargetPathKey = "build_metadata.AdditionalFiles.TargetPath";
	private const string SynthesizedConfigName = "dtr-hotreload.generated.globalconfig";
	private const int MaxReportedErrors = 50;

	private readonly Dictionary<string, Entry> _entries;

	private HotSolution(Solution solution, Dictionary<string, Entry> entries)
	{
		Solution = solution;
		_entries = entries;
	}

	public Solution Solution { get; private set; }

	public static HotSolution Create(IReadOnlyList<HotProject> projects)
	{
		var workspace = new AdhocWorkspace();

		var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
		var infos = new List<ProjectInfo>();
		foreach (var project in projects)
		{
			var inputs = ProjectInputs.Load(project.ResponseFile, project.ProjectDirectory);
			var args = File.ReadAllLines(project.ResponseFile);
			var info = CommandLineProject.CreateProjectInfo(project.Name, LanguageNames.CSharp, args, project.ProjectDirectory, workspace);

			infos.Add(info);
			entries[project.ProjectFile] = new Entry(project, inputs, info.Id);
		}

		// References to the other hot projects' (reference) assemblies become project references.
		var idsByAssembly = entries.Values.ToDictionary(e => e.Project.AssemblyName, e => e.Id, StringComparer.OrdinalIgnoreCase);
		for (var i = 0; i < infos.Count; i++)
		{
			var info = infos[i];
			var projectReferences = new List<ProjectReference>();
			var metadataReferences = new List<MetadataReference>();
			foreach (var reference in info.MetadataReferences)
			{
				if (
					reference is PortableExecutableReference { FilePath: { } path }
					&& idsByAssembly.TryGetValue(Path.GetFileNameWithoutExtension(path), out var id)
					&& id != info.Id
				)
				{
					projectReferences.Add(new ProjectReference(id));
				}
				else
				{
					metadataReferences.Add(reference);
				}
			}

			infos[i] = info.WithMetadataReferences(metadataReferences).WithProjectReferences(projectReferences);
		}

		var solution = infos.Aggregate(workspace.CurrentSolution, (s, info) => s.AddProject(info));
		return new HotSolution(solution, entries);
	}

	/// <summary>
	/// Errors only, with paths relative to the project.
	/// </summary>
	public static List<CompileDiagnostic> ToCompileDiagnostics(HotProject project, IEnumerable<Diagnostic> diagnostics) =>
		[
			.. diagnostics
				.Where(d => d.Severity == DiagnosticSeverity.Error)
				.Distinct()
				.Take(MaxReportedErrors)
				.Select(d => ToCompileDiagnostic(project, d)),
		];

	/// <summary>
	/// Compiles all projects once, which also loads all documents and runs the generators (slow the first time).
	/// </summary>
	public async Task WarmUpAsync()
	{
		foreach (var entry in _entries.Values)
		{
			var stopwatch = Stopwatch.StartNew();
			var compilation = await Solution.GetProject(entry.Id)!.GetCompilationAsync().ConfigureAwait(false);
			var errors = compilation!.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
			Log.Info(
				$"Compiled {entry.Project.Name} in {stopwatch.ElapsedMilliseconds} ms{(errors > 0 ? $" ({errors} errors)" : string.Empty)}"
			);
		}
	}

	/// <summary>
	/// Brings the documents of the given projects in line with the file system (changed, added and removed .cs and .razor files).
	/// </summary>
	public async Task SyncAsync(IEnumerable<HotProject> projects)
	{
		foreach (var project in projects)
		{
			var entry = _entries[project.ProjectFile];
			await SyncDocumentsAsync(entry, additional: false).ConfigureAwait(false);
			await SyncDocumentsAsync(entry, additional: true).ConfigureAwait(false);
			UpdateConfigForNewRazorFiles(entry);
		}
	}

	public async Task<CompileResult> EmitAsync(HotProject project)
	{
		var stopwatch = Stopwatch.StartNew();
		var entry = _entries[project.ProjectFile];
		var compilation = (await Solution.GetProject(entry.Id)!.GetCompilationAsync().ConfigureAwait(false))!;

		using var assemblyStream = new MemoryStream();
		using var symbolStream = new MemoryStream();
		var emitOptions = entry.Inputs.Arguments.EmitOptions.WithDebugInformationFormat(DebugInformationFormat.PortablePdb);
		var result = compilation.Emit(
			assemblyStream,
			symbolStream,
			manifestResources: entry.Inputs.Arguments.ManifestResources,
			options: emitOptions
		);

		stopwatch.Stop();
		var errors = ToCompileDiagnostics(project, result.Diagnostics);
		return result.Success && errors.Count == 0
			? new CompileResult(assemblyStream.ToArray(), symbolStream.ToArray(), [], stopwatch.Elapsed)
			: new CompileResult(null, null, errors, stopwatch.Elapsed);
	}

	private static CompileDiagnostic ToCompileDiagnostic(HotProject project, Diagnostic diagnostic)
	{
		var span = diagnostic.Location.GetMappedLineSpan();
		var file = span.IsValid ? span.Path : string.Empty;
		if (Path.IsPathRooted(file))
		{
			file = Path.GetRelativePath(project.ProjectDirectory, file);
		}

		return new CompileDiagnostic(
			file,
			span.StartLinePosition.Line + 1,
			span.StartLinePosition.Character + 1,
			diagnostic.Id,
			diagnostic.GetMessage(CultureInfo.InvariantCulture)
		);
	}

	private static SourceText? ReadText(string path)
	{
		// Editors sometimes have the file locked for a moment while saving, so retry a few times.
		const int MaxAttempts = 5;
		var attempt = 0;
		while (true)
		{
			try
			{
				using var stream = File.OpenRead(path);

				// Same checksum algorithm as the compiler uses by default.
				return SourceText.From(stream, encoding: null, SourceHashAlgorithm.Sha256, canBeEmbedded: true);
			}
			catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
			{
				return null;
			}
			catch (IOException) when (++attempt < MaxAttempts)
			{
				Thread.Sleep(20);
			}
		}
	}

	private async Task SyncDocumentsAsync(Entry entry, bool additional)
	{
		var project = Solution.GetProject(entry.Id)!;
		var desired = (additional ? entry.Inputs.GetAdditionalFiles() : entry.Inputs.GetSourceFiles()).ToHashSet(StringComparer.Ordinal);
		var documents = (additional ? project.AdditionalDocuments : project.Documents)
			.Where(d => d.FilePath is not null)
			.GroupBy(d => d.FilePath!, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

		foreach (var path in desired)
		{
			if (ReadText(path) is not { } text)
			{
				continue;
			}

			if (documents.TryGetValue(path, out var document))
			{
				if (!(await document.GetTextAsync().ConfigureAwait(false)).ContentEquals(text))
				{
					Solution = additional
						? Solution.WithAdditionalDocumentText(document.Id, text, PreservationMode.PreserveIdentity)
						: Solution.WithDocumentText(document.Id, text, PreservationMode.PreserveIdentity);
				}
			}
			else
			{
				var info = DocumentInfo.Create(
					DocumentId.CreateNewId(entry.Id),
					Path.GetFileName(path),
					loader: TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), path)),
					filePath: path
				);
				Solution = additional ? Solution.AddAdditionalDocument(info) : Solution.AddDocument(info);
			}
		}

		// Only live files are removed (not e.g. the generated GlobalUsings under obj/).
		foreach (var (path, document) in documents)
		{
			if (!desired.Contains(path) && ProjectInputs.IsLiveFile(entry.Project.ProjectDirectory, path))
			{
				Solution = additional ? Solution.RemoveAdditionalDocument(document.Id) : Solution.RemoveDocument(document.Id);
			}
		}
	}

	/// <summary>
	/// The SDK passes per-file metadata of Razor files (the relative path, which determines the namespace) through a generated editorconfig.
	/// That one only knows about the files that existed during the last build, so for new files, we generate those entries ourselves.
	/// </summary>
	private void UpdateConfigForNewRazorFiles(Entry entry)
	{
		var project = Solution.GetProject(entry.Id)!;
		var missing = project
			.AdditionalDocuments.Select(d => d.FilePath)
			.OfType<string>()
			.Where(path => Path.GetExtension(path) is ".razor" or ".cshtml")
			.Where(path => !entry.BaseConfigSet.GetOptionsForSourcePath(path).AnalyzerOptions.ContainsKey(TargetPathKey))
			.Order(StringComparer.Ordinal)
			.ToList();

		var key = string.Join('|', missing);
		if (key == entry.SynthesizedConfigKey)
		{
			return;
		}

		entry.SynthesizedConfigKey = key;
		if (entry.SynthesizedConfigId is not null)
		{
			Solution = Solution.RemoveAnalyzerConfigDocument(entry.SynthesizedConfigId);
			entry.SynthesizedConfigId = null;
		}

		if (missing.Count == 0)
		{
			return;
		}

		var config = new StringBuilder("is_global = true\n");
		foreach (var path in missing)
		{
			var relative = Path.GetRelativePath(entry.Project.ProjectDirectory, path);
			config.Append('[').Append(path.Replace('\\', '/')).Append("]\n");
			config.Append(TargetPathKey).Append(" = ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(relative))).Append('\n');
			config.Append("build_metadata.AdditionalFiles.CssScope = \n");
		}

		var configPath = Path.Combine(entry.Project.ProjectDirectory, "obj", SynthesizedConfigName);
		entry.SynthesizedConfigId = DocumentId.CreateNewId(entry.Id);
		Solution = Solution.AddAnalyzerConfigDocument(
			entry.SynthesizedConfigId,
			SynthesizedConfigName,
			SourceText.From(config.ToString(), Encoding.UTF8),
			filePath: configPath
		);
	}

	private sealed class Entry(HotProject project, ProjectInputs inputs, ProjectId id)
	{
		public HotProject Project { get; } = project;

		public ProjectInputs Inputs { get; } = inputs;

		public ProjectId Id { get; } = id;

		public AnalyzerConfigSet BaseConfigSet { get; } =
			AnalyzerConfigSet.Create(
				inputs
					.Arguments.AnalyzerConfigPaths.Select(path => AnalyzerConfig.Parse(SourceText.From(File.ReadAllText(path)), path))
					.ToList()
			);

		public string SynthesizedConfigKey { get; set; } = string.Empty;

		public DocumentId? SynthesizedConfigId { get; set; }
	}
}
