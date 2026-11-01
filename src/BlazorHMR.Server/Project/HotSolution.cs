using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using BlazorHMR.Protocol;
using BlazorHMR.Server.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace BlazorHMR.Server.Project;

/// <summary>
/// A Roslyn workspace with the hot projects (the app and the libraries it ships), kept in sync with the file system.
/// <para>
/// Used both for full compiles (new generations) and for Edit-and-Continue patches, so the PDBs of a generation match exactly what
/// the delta engine expects as its baseline. Roslyn compiles incrementally: only changed documents are re-parsed, and the source
/// generators (Razor!) run incrementally too.
/// </para>
/// </summary>
internal sealed class HotSolution
{
	private const string TargetPathKey = "build_metadata.AdditionalFiles.TargetPath";
	private const string SynthesizedConfigName = "blazor-hmr.generated.globalconfig";
	private const int MaxReportedErrors = 50;

	/// <summary>
	/// The Edit-and-Continue services are MEF parts in these.
	/// </summary>
	private static readonly string[] FeatureAssemblies =
	[
		"Microsoft.CodeAnalysis.Features",
		"Microsoft.CodeAnalysis.CSharp.Features",
		"Microsoft.CodeAnalysis.ExternalAccess.HotReload",
	];

	private readonly Dictionary<string, Entry> _entries;

	private HotSolution(AdhocWorkspace workspace, Solution solution, Dictionary<string, Entry> entries)
	{
		Workspace = workspace;
		Solution = solution;
		_entries = entries;
	}

	public AdhocWorkspace Workspace { get; }

	public Solution Solution { get; private set; }

	public IEnumerable<ProjectId> ProjectIds => _entries.Values.Select(e => e.Id);

	/// <summary>
	/// Builds a workspace from the compiler command line of every hot project. The command lines come from the last real
	/// build (or from a design-time compile for libraries), so they match what the app is running.
	/// </summary>
	public static HotSolution Create(IReadOnlyList<HotProject> projects, string appBaselineAssembly)
	{
		// The Edit-and-Continue services are MEF parts; load them into the workspace's host.
		var features = FeatureAssemblies.Select(Assembly.Load);
		var workspace = new AdhocWorkspace(MefHostServices.Create(MefHostServices.DefaultAssemblies.Concat(features)));

		var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
		var infos = new List<ProjectInfo>();
		foreach (var project in projects)
		{
			var inputs = ProjectInputs.Load(project.ResponseFile, project.ProjectDirectory);
			var args = File.ReadAllLines(project.ResponseFile);
			var info = CommandLineProject.CreateProjectInfo(project.Name, LanguageNames.CSharp, args, project.ProjectDirectory, workspace);

			// Edit-and-Continue skips projects without a file path, and needs the baseline (what the client is running).
			// The app's baseline is the build output; a library's baseline is its reference assembly (or its own output).
			var baseline = project.IsApp ? appBaselineAssembly : info.CompilationOutputInfo.AssemblyPath ?? inputs.OutputAssembly;
			info = info.WithFilePath(project.ProjectFile)
				.WithCompilationOutputInfo(info.CompilationOutputInfo.WithAssemblyPath(baseline))
				.WithDocuments(info.Documents.Select(Snapshot))
				.WithAdditionalDocuments(info.AdditionalDocuments.Select(Snapshot));

			infos.Add(info);
			entries[project.ProjectFile] = new Entry(project, inputs, info.Id);
		}

		// References to the other hot projects' (reference) assemblies become project references, so Roslyn compiles them
		// together and a change in a library flows into the app without an intermediate binary.
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
		return new HotSolution(workspace, solution, entries);
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

	public ProjectId GetProjectId(HotProject project) => _entries[project.ProjectFile].Id;

	public HotProject GetProject(ProjectId id) => _entries.Values.Single(e => e.Id == id).Project;

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

	/// <summary>
	/// Points the delta engine to the assembly the client is now running for this project.
	/// </summary>
	public void SetBaseline(HotProject project, string assemblyPath)
	{
		var id = _entries[project.ProjectFile].Id;
		var output = Solution.GetProject(id)!.CompilationOutputInfo.WithAssemblyPath(assemblyPath);
		Solution = Solution.WithProjectCompilationOutputInfo(id, output);
	}

	/// <summary>
	/// Compiles one project to an in-memory assembly + portable PDB. The caller publishes them as a generation.
	/// </summary>
	public async Task<CompileResult> EmitAsync(HotProject project)
	{
		var stopwatch = Stopwatch.StartNew();
		var entry = _entries[project.ProjectFile];
		// A clone: the workspace's compilation may already have been emitted by the delta engine, and emitting the same compilation
		// again can fail (anonymous types and delegates get their names during emit).
		var compilation = (await Solution.GetProject(entry.Id)!.GetCompilationAsync().ConfigureAwait(false))!.Clone();

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

	/// <summary>
	/// Turns a Roslyn diagnostic into a line/column in a project-relative file (Razor diagnostics point at generated
	/// documents, so the mapped span is used).
	/// </summary>
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

	/// <summary>
	/// Reads the document now, so the text can't change behind our back. The workspace would otherwise (re)load it from disk lazily,
	/// and then the delta engine wouldn't see the difference between the old and the new version.
	/// </summary>
	private static DocumentInfo Snapshot(DocumentInfo document)
	{
		if (document.FilePath is null || ReadText(document.FilePath) is not { } text)
		{
			return document;
		}

		return document.WithTextLoader(TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), document.FilePath)));
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

				// Same checksum algorithm as the compiler uses by default, so the delta engine can match the documents with the PDB.
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

	/// <summary>
	/// Makes the workspace documents match the files on disk: update changed text, add new files, remove files that were
	/// deleted or moved out of the project. Runs separately for source and additional (Razor) documents.
	/// </summary>
	private async Task SyncDocumentsAsync(Entry entry, bool additional)
	{
		var project = Solution.GetProject(entry.Id)!;
		var desired = (additional ? entry.Inputs.GetAdditionalFiles() : entry.Inputs.GetSourceFiles()).ToHashSet(StringComparer.Ordinal);

		// File paths can be duplicated (e.g. the same file linked into the project twice); keep one document per path.
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

		// Razor files that exist in the workspace but not in the build's generated editorconfig: the Razor source generator
		// would derive the wrong namespace (or scoped CSS scope) for them.
		var missing = project
			.AdditionalDocuments.Select(d => d.FilePath)
			.OfType<string>()
			.Where(path => Path.GetExtension(path) is ".razor" or ".cshtml")
			.Where(path => !entry.BaseConfigSet.GetOptionsForSourcePath(path).AnalyzerOptions.ContainsKey(TargetPathKey))
			.Order(StringComparer.Ordinal)
			.ToList();

		// Rebuilding the config document is cheap, but avoid touching the solution when the set hasn't changed.
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

		// Same shape as the SDK's generated editorconfig. TargetPath is base64 because it can contain characters that
		// editorconfig treats specially; CssScope is empty for files that don't have a scoped CSS file.
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
