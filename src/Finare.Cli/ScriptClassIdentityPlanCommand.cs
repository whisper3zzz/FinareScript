using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Finare.Core;
using Finare.CSharp;

namespace Finare.Cli;

internal static class ScriptClassIdentityPlanCommand
{
    private const string RequestSchema = "unifiedscript.csharp-source-identity-request/v0.1";
    private const string OutputSchema = "unifiedscript.csharp-source-identity-plan/v0.1";

    public static int Run(IReadOnlyList<string> args)
    {
        if (!TryParseArguments(args, out Options? options) || options is null)
        {
            PrintUsage();
            return 64;
        }

        string projectPath = NormalizeFullPath(options.ProjectPath);
        string requestPath = NormalizeFullPath(options.RequestPath);
        string outputPath = NormalizeFullPath(options.OutputPath);
        if (!File.Exists(requestPath))
        {
            throw new FileNotFoundException("Identity transition request does not exist.", requestPath);
        }

        JsonSerializerOptions jsonOptions = CreateCommandJsonOptions();
        string requestJson = File.ReadAllText(requestPath);
        using (JsonDocument requestDocument = JsonDocument.Parse(requestJson))
        {
            ValidateRequestJsonShape(requestDocument.RootElement);
        }
        IdentityPlanRequest request = JsonSerializer.Deserialize<IdentityPlanRequest>(
            requestJson, jsonOptions)
            ?? throw new JsonException("The identity transition request was empty.");
        ValidateRequestEnvelope(request);

        CSharpProjectLoadResult loaded;
        try
        {
            loaded = new CSharpProjectLoader().Load(
                projectPath,
                options: new CSharpProjectLoadOptions(
                    options.Configuration,
                    options.TargetFramework,
                    Restore: false,
                    Build: false,
                    DotNetPath: options.DotNetPath));
        }
        catch (CSharpProjectLoadException exception)
        {
            throw new ArgumentException(exception.Message, exception);
        }

        if (!string.Equals(
                NormalizeFullPath(loaded.ProjectPath),
                projectPath,
                PathComparison))
        {
            throw new ArgumentException("The loaded project path did not match the requested project.");
        }

        ProjectSnapshot snapshot = ReadProjectSnapshot(loaded);
        IReadOnlyList<CSharpSourceIdentityTransition> transitions =
            ValidateAndBuildTransitions(request, snapshot);
        ValidateOutputPath(outputPath, projectPath, requestPath, snapshot, transitions);

        CSharpCompilationRequest plannerRequest = new(
            loaded.Request.AssemblyName,
            snapshot.Sources,
            loaded.Request.ReferencePaths,
            loaded.Request.EffectCatalog,
            loaded.Request.Settings,
            loaded.Request.RuntimeReferencePaths,
            loaded.Request.IncludeDefaultReferences);
        CSharpSourceIdentityTransitionPlanResult planned =
            new CSharpSourceIdentityTransitionPlanner().Plan(plannerRequest, transitions);
        if (!planned.Succeeded || planned.Analysis?.Semantics is null)
        {
            string diagnostics = string.Join(
                Environment.NewLine,
                planned.Diagnostics
                    .Where(static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error)
                    .Select(FormatDiagnostic));
            throw new ArgumentException(
                string.IsNullOrWhiteSpace(diagnostics)
                    ? "The identity transition planner rejected the request."
                    : diagnostics);
        }

        var outcomes = planned.Transitions.ToDictionary(
            static outcome => outcome.Transition.NormalizedOriginalLogicalPath,
            StringComparer.Ordinal);
        var outputDocuments = request.Documents.Select(document =>
        {
            string sourcePath = NormalizePath(
                snapshot.SourcesByPath[NormalizeFullPath(document.SourcePath)].Path);
            CSharpSourceIdentityTransitionOutcome outcome = outcomes[sourcePath];
            string targetPath = outcome.Transition.NormalizedTargetLogicalPath;
            IdentitySidecarDocument? sidecar = outcome.IdentitySidecar;
            return new IdentityPlanOutputDocument(
                sourcePath,
                targetPath,
                document.SourceDigest,
                document.SourceSidecarDigest,
                document.PreserveIdentity,
                targetPath + ".usids",
                sidecar is null
                    ? null
                    : IdentitySidecarJson.Serialize(sidecar, writeIndented: false) + "\n");
        }).ToArray();
        var output = new IdentityPlanOutput(
            OutputSchema,
            NormalizePath(projectPath),
            planned.Analysis.Semantics.ConfigurationId.Value,
            outputDocuments);

        // This is the only filesystem mutation performed by the command. The
        // request, source files, and source-adjacent sidecars are never moved
        // or rewritten.
        Program.WriteAtomically(
            outputPath,
            JsonSerializer.Serialize(output, jsonOptions) + "\n");
        Console.WriteLine(outputPath);
        return 0;
    }

    private static ProjectSnapshot ReadProjectSnapshot(CSharpProjectLoadResult loaded)
    {
        var sourceByPath = new Dictionary<string, SourceSnapshot>(PathComparer);
        foreach (CSharpProjectSource projectSource in loaded.Sources)
        {
            string path = NormalizeFullPath(projectSource.Path);
            if (!sourceByPath.TryAdd(path, ReadSourceSnapshot(path)))
            {
                throw new ArgumentException($"The project contains duplicate source path '{path}'.");
            }
        }

        var documents = new List<CSharpSourceDocument>(sourceByPath.Count);
        foreach (SourceSnapshot source in sourceByPath.Values.OrderBy(static source => source.Path, PathComparer))
        {
            IdentitySidecarDocument? sidecar = null;
            if (source.SidecarBytes is not null)
            {
                sidecar = IdentitySidecarJson.Deserialize(DecodeText(source.SidecarBytes));
                sidecar = sidecar with { SourcePath = NormalizePath(source.Path) };
            }
            documents.Add(new CSharpSourceDocument(
                NormalizePath(source.Path),
                source.Text,
                sidecar));
        }

        return new ProjectSnapshot(sourceByPath, documents);
    }

    private static SourceSnapshot ReadSourceSnapshot(string path)
    {
        byte[] sourceBytes = File.ReadAllBytes(path);
        string sourceText = DecodeText(sourceBytes);
        string sidecarPath = path + ".usids";
        byte[]? sidecarBytes = File.Exists(sidecarPath)
            ? File.ReadAllBytes(sidecarPath)
            : null;
        return new SourceSnapshot(
            path,
            sourceBytes,
            sourceText,
            sidecarPath,
            sidecarBytes);
    }

    private static IReadOnlyList<CSharpSourceIdentityTransition> ValidateAndBuildTransitions(
        IdentityPlanRequest request,
        ProjectSnapshot snapshot)
    {
        var transitions = new List<CSharpSourceIdentityTransition>(request.Documents.Count);
        var originals = new HashSet<string>(PathComparer);
        var targets = new HashSet<string>(PathComparer);
        var participatingSourcePaths = request.Documents
            .Select(document => NormalizeFullPath(document.SourcePath))
            .ToHashSet(PathComparer);

        foreach (IdentityPlanRequestDocument document in request.Documents)
        {
            if (string.IsNullOrWhiteSpace(document.SourcePath) ||
                !Path.IsPathRooted(document.SourcePath) ||
                string.IsNullOrWhiteSpace(document.TargetPath) ||
                !Path.IsPathRooted(document.TargetPath))
            {
                throw new ArgumentException(
                    "Identity transition sourcePath and targetPath must be absolute paths.");
            }
            string sourcePath = NormalizeFullPath(document.SourcePath);
            string targetPath = NormalizeFullPath(document.TargetPath);
            if (!snapshot.SourcesByPath.TryGetValue(sourcePath, out SourceSnapshot? source))
            {
                throw new ArgumentException($"Request source '{sourcePath}' is not an evaluated project source.");
            }
            sourcePath = source.Path;
            if (string.Equals(sourcePath, targetPath, PathComparison))
            {
                targetPath = sourcePath;
            }
            if (!originals.Add(sourcePath))
            {
                throw new ArgumentException($"Request contains duplicate source path '{sourcePath}'.");
            }
            if (!targets.Add(targetPath))
            {
                throw new ArgumentException($"Request contains duplicate target path '{targetPath}'.");
            }
            if (!IsSha256Digest(document.SourceDigest) ||
                !string.Equals(document.SourceDigest, Digest(source.SourceBytes), StringComparison.Ordinal))
            {
                throw new ArgumentException($"Source digest precondition failed for '{sourcePath}'.");
            }

            string? actualSidecarDigest = source.SidecarBytes is null
                ? null
                : Digest(source.SidecarBytes);
            if (!string.Equals(
                    document.SourceSidecarDigest,
                    actualSidecarDigest,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException($"Source sidecar digest precondition failed for '{sourcePath}'.");
            }

            if (document.PreserveIdentity && sourcePath.Length == 0)
            {
                throw new ArgumentException("A preserving transition requires a source path.");
            }
            if (!document.PreserveIdentity && string.Equals(sourcePath, targetPath, PathComparison))
            {
                throw new ArgumentException("A duplicate transition must use a different target path.");
            }
            if (File.Exists(targetPath) &&
                !string.Equals(sourcePath, targetPath, PathComparison) &&
                !participatingSourcePaths.Contains(targetPath))
            {
                throw new ArgumentException($"Transition target already exists: '{targetPath}'.");
            }
            string targetSidecarPath = targetPath + ".usids";
            if (File.Exists(targetSidecarPath) &&
                !string.Equals(source.SidecarPath, targetSidecarPath, PathComparison) &&
                !participatingSourcePaths.Contains(targetPath))
            {
                throw new ArgumentException($"Transition target sidecar already exists: '{targetSidecarPath}'.");
            }

            transitions.Add(new CSharpSourceIdentityTransition(
                NormalizePath(sourcePath),
                NormalizePath(targetPath),
                document.TargetSource ?? throw new ArgumentException(
                    $"Transition target source is required for '{sourcePath}'."),
                document.PreserveIdentity));
        }

        return transitions;
    }

    private static void ValidateRequestEnvelope(IdentityPlanRequest request)
    {
        if (!string.Equals(request.SchemaVersion, RequestSchema, StringComparison.Ordinal) ||
            request.Documents is null ||
            request.Documents.Count == 0)
        {
            throw new ArgumentException($"Expected request schema '{RequestSchema}' with at least one document.");
        }
    }

    private static void ValidateRequestJsonShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Identity transition request must be a JSON object.");
        }

        JsonElement schema = RequiredProperty(root, "schemaVersion");
        if (schema.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Identity transition request schemaVersion must be a string.");
        }

        JsonElement documents = RequiredProperty(root, "documents");
        if (documents.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Identity transition request documents must be an array.");
        }

        foreach (JsonElement document in documents.EnumerateArray())
        {
            if (document.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Identity transition request documents cannot contain null or scalar values.");
            }

            RequireString(document, "sourcePath");
            RequireString(document, "targetPath");
            RequireString(document, "sourceDigest");
            RequireString(document, "targetSource");
            JsonElement sourceSidecarDigest = RequiredProperty(document, "sourceSidecarDigest");
            if (sourceSidecarDigest.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new JsonException("sourceSidecarDigest must be a string or explicit null.");
            }

            JsonElement preserveIdentity = RequiredProperty(document, "preserveIdentity");
            if (preserveIdentity.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new JsonException("preserveIdentity must be a boolean.");
            }
        }
    }

    private static JsonElement RequiredProperty(JsonElement objectElement, string name)
    {
        if (!objectElement.TryGetProperty(name, out JsonElement value))
        {
            throw new JsonException($"Identity transition request is missing required field '{name}'.");
        }

        return value;
    }

    private static void RequireString(JsonElement objectElement, string name)
    {
        if (RequiredProperty(objectElement, name).ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Identity transition request field '{name}' must be a string.");
        }
    }

    private static void ValidateOutputPath(
        string outputPath,
        string projectPath,
        string requestPath,
        ProjectSnapshot snapshot,
        IReadOnlyList<CSharpSourceIdentityTransition> transitions)
    {
        if (string.Equals(outputPath, projectPath, PathComparison) ||
            string.Equals(outputPath, requestPath, PathComparison) ||
            snapshot.SourcesByPath.ContainsKey(outputPath) ||
            snapshot.SourcesByPath.Values.Any(source =>
                string.Equals(outputPath, source.SidecarPath, PathComparison)) ||
            transitions.Any(transition =>
                string.Equals(outputPath, NormalizeFullPath(transition.NormalizedTargetLogicalPath), PathComparison) ||
                string.Equals(
                    outputPath,
                    NormalizeFullPath(transition.NormalizedTargetLogicalPath) + ".usids",
                    PathComparison)))
        {
            throw new ArgumentException(
                $"Identity plan output cannot overwrite the project, request, source, sidecar, or transition target '{outputPath}'.");
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            Encoding encoding;
            int offset;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = new UTF8Encoding(false, true);
                offset = 3;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
                offset = 2;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
                offset = 2;
            }
            else
            {
                encoding = new UTF8Encoding(false, true);
                offset = 0;
            }

            return encoding.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (Exception exception) when (exception is DecoderFallbackException or ArgumentException)
        {
            throw new InvalidDataException(
                "A project source or identity sidecar is not valid UTF-8 or UTF-16 text.",
                exception);
        }
    }

    private static string Digest(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256Digest(string? value) =>
        value is not null && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.Length == "sha256:".Length + 64 &&
        value.Skip("sha256:".Length).All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string NormalizeFullPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizePath(string path) =>
        NormalizeFullPath(path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FormatDiagnostic(UnifiedDiagnostic diagnostic) =>
        $"{diagnostic.Span.FilePath}({diagnostic.Span.Start.Line},{diagnostic.Span.Start.Column}): " +
        $"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Code}: {diagnostic.Message}";

    private static JsonSerializerOptions CreateCommandJsonOptions()
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented: true);
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        return options;
    }

    private static bool TryParseArguments(
        IReadOnlyList<string> args,
        out Options? options)
    {
        options = null;
        if (args.Count < 2 || args[0] != "script-class-identity-plan")
        {
            return false;
        }

        string projectPath = args[1];
        string? requestPath = null;
        string? outputPath = null;
        string configuration = "Debug";
        string? targetFramework = null;
        string? dotnetPath = null;
        for (int index = 2; index < args.Count; index++)
        {
            string option = args[index];
            if (++index >= args.Count)
            {
                return false;
            }
            string value = args[index];
            switch (option)
            {
                case "--request" when requestPath is null:
                    requestPath = value;
                    break;
                case "--out" when outputPath is null:
                    outputPath = value;
                    break;
                case "--configuration" when configuration == "Debug":
                    configuration = value;
                    break;
                case "--framework" when targetFramework is null:
                    targetFramework = value;
                    break;
                case "--dotnet" when dotnetPath is null:
                    dotnetPath = value;
                    break;
                default:
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(projectPath) ||
            string.IsNullOrWhiteSpace(requestPath) ||
            string.IsNullOrWhiteSpace(outputPath) ||
            string.IsNullOrWhiteSpace(configuration))
        {
            return false;
        }

        options = new Options(projectPath, requestPath, outputPath, configuration, targetFramework, dotnetPath);
        return true;
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        "Usage: finare script-class-identity-plan <project.csproj> " +
        "--request <request.json> --out <plan.json> [--configuration <name>] " +
        "[--framework <tfm>] [--dotnet <path>]");

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record Options(
        string ProjectPath,
        string RequestPath,
        string OutputPath,
        string Configuration,
        string? TargetFramework,
        string? DotNetPath);

    private sealed record ProjectSnapshot(
        IReadOnlyDictionary<string, SourceSnapshot> SourcesByPath,
        IReadOnlyList<CSharpSourceDocument> Sources);

    private sealed record SourceSnapshot(
        string Path,
        byte[] SourceBytes,
        string Text,
        string SidecarPath,
        byte[]? SidecarBytes);

    private sealed record IdentityPlanRequest(
        string SchemaVersion,
        IReadOnlyList<IdentityPlanRequestDocument> Documents);

    private sealed record IdentityPlanRequestDocument(
        string SourcePath,
        string TargetPath,
        string SourceDigest,
        string? SourceSidecarDigest,
        string TargetSource,
        bool PreserveIdentity);

    private sealed record IdentityPlanOutput(
        string SchemaVersion,
        string ProjectPath,
        string SemanticConfigurationId,
        IReadOnlyList<IdentityPlanOutputDocument> Documents);

    private sealed record IdentityPlanOutputDocument(
        string SourcePath,
        string TargetPath,
        string SourceDigest,
        string? SourceSidecarDigest,
        bool PreserveIdentity,
        string TargetSidecarPath,
        string? IdentitySidecarJson);
}
