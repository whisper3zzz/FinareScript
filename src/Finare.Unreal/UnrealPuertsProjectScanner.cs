using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Finare.Unreal;

public sealed record UnrealPuertsProjectScanOptions(
    string ProjectRoot,
    string? NativeProjectRoot = null,
    string? ProjectName = null,
    IReadOnlyList<string>? ScriptRoots = null,
    IReadOnlyList<string>? NativeRoots = null,
    int MaximumFileCount = 50_000,
    long MaximumFileBytes = 2 * 1024 * 1024,
    long MaximumTotalBytes = 256 * 1024 * 1024);

public sealed record UnrealPuertsProjectScanFinding(
    string Category,
    string Key,
    string Risk,
    string MigrationAction,
    IReadOnlyList<UnrealPuertsMigrationLocation> Locations);

public sealed record UnrealPuertsProjectScanSummary(
    int ScriptFileCount,
    int NativeHeaderCount,
    int ConfigurationFileCount,
    int ModuleCount,
    int ScriptTypeCount,
    int UnrealApiCount,
    int PuertsApiCount,
    int LifecycleHookCount,
    int AssetReferenceCount,
    int DelegatePatternCount,
    int AsyncPatternCount,
    int UnsupportedDynamicBehaviorCount,
    int NativeSurfaceCount,
    int ReviewCount,
    int BlockerCount,
    int FindingCount,
    long AnalyzedBytes);

public sealed record UnrealPuertsProjectScan(
    string SchemaVersion,
    string ProjectName,
    string SourceFingerprint,
    bool Complete,
    IReadOnlyList<string> ScriptRoots,
    IReadOnlyList<string> NativeRoots,
    IReadOnlyList<string> ConfigurationFiles,
    IReadOnlyList<UnrealPuertsProjectScanFinding> Findings,
    UnrealPuertsProjectScanSummary Summary,
    IReadOnlyList<string> Diagnostics);

public static class UnrealPuertsProjectScanJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(UnrealPuertsProjectScan scan) =>
        JsonSerializer.Serialize(scan, Options) + "\n";

    public static UnrealPuertsProjectScan Deserialize(string json)
    {
        UnrealPuertsProjectScan scan = JsonSerializer.Deserialize<UnrealPuertsProjectScan>(
            json,
            Options) ?? throw new JsonException("Puerts project scan JSON is empty.");
        if (scan.SchemaVersion != "unifiedscript.puerts-project-scan/v0.1")
        {
            throw new JsonException(
                $"Unsupported Puerts project scan schema '{scan.SchemaVersion}'.");
        }
        if (string.IsNullOrWhiteSpace(scan.ProjectName) ||
            string.IsNullOrWhiteSpace(scan.SourceFingerprint) ||
            scan.ScriptRoots is null ||
            scan.NativeRoots is null ||
            scan.ConfigurationFiles is null ||
            scan.Findings is null ||
            scan.Summary is null ||
            scan.Diagnostics is null)
        {
            throw new JsonException("Puerts project scan is incomplete.");
        }
        return scan;
    }
}

public sealed class UnrealPuertsProjectScanner
{
    private const string ScanSchema = "unifiedscript.puerts-project-scan/v0.1";
    private const int HardMaximumFileCount = 250_000;
    private const long HardMaximumFileBytes = 32 * 1024 * 1024;
    private const long HardMaximumTotalBytes = 2L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> ScriptExtensions = new(
        [".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> HeaderExtensions = new(
        [".h", ".hpp", ".hh", ".inl"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExcludedDirectories = new(
        [
            ".git",
            ".vs",
            "Binaries",
            "DerivedDataCache",
            "Intermediate",
            "node_modules",
            "obj",
            "Saved",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly string[] DefaultSourceScriptRoots =
    [
        "TypeScript",
        "Scripts",
        "src",
    ];
    private static readonly string[] DefaultDeployedScriptRoots =
    [
        "Content/JavaScript",
        "JavaScript",
    ];
    private static readonly Regex NamespaceImportRegex = new(
        @"\bimport\s+\*\s+as\s+(?<alias>[A-Za-z_$][\w$]*)\s+from\s+['""](?<module>ue|puerts)['""]",
        RegexOptions.CultureInvariant);
    private static readonly Regex DefaultImportRegex = new(
        @"\bimport\s+(?<alias>[A-Za-z_$][\w$]*)\s+from\s+['""](?<module>ue|puerts)['""]",
        RegexOptions.CultureInvariant);
    private static readonly Regex RequireAliasRegex = new(
        @"\b(?:const|let|var)\s+(?<alias>[A-Za-z_$][\w$]*)\s*=\s*require\(\s*['""](?<module>ue|puerts)['""]\s*\)",
        RegexOptions.CultureInvariant);
    private static readonly Regex FromModuleRegex = new(
        @"\b(?:from|import)\s*(?:\(\s*)?['""](?<module>[^'""]+)['""]",
        RegexOptions.CultureInvariant);
    private static readonly Regex RequireModuleRegex = new(
        @"\brequire\(\s*['""](?<module>[^'""]+)['""]\s*\)",
        RegexOptions.CultureInvariant);
    private static readonly Regex TypeRegex = new(
        @"\bclass\s+(?<name>[A-Za-z_$][\w$]*)\s+extends\s+(?<base>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)",
        RegexOptions.CultureInvariant);
    private static readonly Regex FieldRegex = new(
        @"^\s*(?<name>[A-Za-z_$][\w$]*)\??\s*:\s*(?<type>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)?)\s*(?:;|=)",
        RegexOptions.CultureInvariant);
    private static readonly Regex MethodDefinitionRegex = new(
        @"^\s*(?:public\s+|private\s+|protected\s+|static\s+|override\s+|async\s+)*(?<name>[A-Za-z_$][\w$]*)\s*\(",
        RegexOptions.CultureInvariant);
    private static readonly Regex LifecycleMethodRegex = new(
        @"\b(?<name>Constructor|ReceiveBeginPlay|ReceiveEndPlay|ReceiveTick|BeginPlay|EndPlay|Tick|OnInit|OnStart|OnStop|OnDestroy)\s*\(",
        RegexOptions.CultureInvariant);
    private static readonly Regex MemberChainRegex = new(
        @"\b(?<root>[A-Za-z_$][\w$]*)(?<chain>(?:\.[A-Za-z_$][\w$]*)+)",
        RegexOptions.CultureInvariant);
    private static readonly Regex ThisCallRegex = new(
        @"\bthis(?<chain>(?:\.[A-Za-z_$][\w$]*)+)(?=\s*\()",
        RegexOptions.CultureInvariant);
    private static readonly Regex ThisPropertyWriteRegex = new(
        @"\bthis\.(?<field>[A-Za-z_$][\w$]*)\.(?<member>[A-Za-z_$][\w$]*)\s*=",
        RegexOptions.CultureInvariant);
    private static readonly Regex TypedLocalRegex = new(
        @"\b(?:let|const|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*:\s*(?<type>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)?)",
        RegexOptions.CultureInvariant);
    private static readonly Regex RefLocalRegex = new(
        @"\b(?:let|const|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*\$ref<(?<type>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)?)>",
        RegexOptions.CultureInvariant);
    private static readonly Regex UnrefLocalRegex = new(
        @"\b(?:let|const|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*\$unref\((?<source>[A-Za-z_$][\w$]*)\)",
        RegexOptions.CultureInvariant);
    private static readonly Regex ReceiverCallRegex = new(
        @"\b(?<receiver>[A-Za-z_$][\w$]*)\.(?<member>[A-Za-z_$][\w$]*)\s*\(",
        RegexOptions.CultureInvariant);
    private static readonly Regex ChainedResultCallRegex = new(
        @"\)\.(?<member>op_[A-Za-z_$][\w$]*)\s*\(",
        RegexOptions.CultureInvariant);
    private static readonly Regex PuertsGlobalApiRegex = new(
        @"\$(?<name>ref|unref|generic|typeof|extension|promise|callback)\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex AssetReferenceRegex = new(
        @"['""](?<path>/(?:Game|Engine|Script|Plugin|[A-Za-z][A-Za-z0-9_]*)/[^'""]+)['""]",
        RegexOptions.CultureInvariant);
    private static readonly Regex DelegateRegex = new(
        @"\b(?<receiver>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)\.(?<operation>Add|AddUnique|Bind|BindUFunction|Remove|RemoveAll|Clear|Execute|ExecuteIfBound|Broadcast)\s*\(",
        RegexOptions.CultureInvariant);
    private static readonly Regex DecoratorRegex = new(
        @"@(?<name>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)",
        RegexOptions.CultureInvariant);
    private static readonly Regex DynamicModuleRegex = new(
        @"\b(?<kind>require|import)\s*\(\s*(?!['""])[^)]+\)",
        RegexOptions.CultureInvariant);
    private static readonly Regex EvalRegex = new(
        @"\beval\s*\(",
        RegexOptions.CultureInvariant);
    private static readonly Regex FunctionConstructorRegex = new(
        @"\bnew\s+Function\s*\(",
        RegexOptions.CultureInvariant);
    private static readonly Regex ProxyRegex = new(
        @"\bnew\s+Proxy\s*\(",
        RegexOptions.CultureInvariant);
    private static readonly Regex PrototypeMutationRegex = new(
        @"(?<symbol>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*\.prototype(?:\.[A-Za-z_$][\w$]*)+)\s*=",
        RegexOptions.CultureInvariant);
    private static readonly Regex ModuleAugmentationRegex = new(
        @"\bdeclare\s+module\s+['""](?<module>[^'""]+)['""]",
        RegexOptions.CultureInvariant);
    private static readonly Regex GlobalMutationRegex = new(
        @"\b(?:globalThis|global)\.(?<member>[A-Za-z_$][\w$]*)\s*=",
        RegexOptions.CultureInvariant);
    private static readonly Regex NativeClassRegex = new(
        @"\bclass\s+(?:\w+_API\s+)?(?<name>[A-Za-z_$][\w$]*)\s+(?:final\s*)?:\s*public\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex NativeFunctionRegex = new(
        @"\b(?<name>[A-Za-z_$][\w$]*)\s*\([^;{}]*\)\s*(?:const\s*)?;",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly HashSet<string> IgnoredReceivers = new(
        [
            "Array",
            "console",
            "JSON",
            "Math",
            "Object",
            "Promise",
            "super",
            "uproperty",
        ],
        StringComparer.Ordinal);

    public UnrealPuertsProjectScan Scan(UnrealPuertsProjectScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        string projectRoot = ResolveExistingDirectory(options.ProjectRoot, "Puerts project root");
        string nativeProjectRoot = ResolveExistingDirectory(
            options.NativeProjectRoot ?? projectRoot,
            "native project root");
        var diagnostics = new List<string>();
        string[] scriptRoots = ResolveRoots(
            projectRoot,
            options.ScriptRoots,
            DiscoverScriptRoots(projectRoot, diagnostics),
            "script");
        string[] nativeRoots = ResolveRoots(
            nativeProjectRoot,
            options.NativeRoots,
            DiscoverNativeRoots(nativeProjectRoot),
            "native");

        var candidates = new List<SourceCandidate>();
        AddCandidates(
            candidates,
            scriptRoots,
            projectRoot,
            "script",
            static path => IsScriptPath(path),
            options.MaximumFileCount,
            diagnostics);
        AddCandidates(
            candidates,
            nativeRoots,
            nativeProjectRoot,
            "native",
            static path => HeaderExtensions.Contains(Path.GetExtension(path)),
            options.MaximumFileCount,
            diagnostics);
        string[] configurationPaths = DiscoverConfigurationFiles(
            projectRoot,
            scriptRoots,
            options.MaximumFileCount,
            diagnostics);
        foreach (string path in configurationPaths)
        {
            candidates.Add(new SourceCandidate(
                Path.GetFullPath(path),
                Relative(projectRoot, path),
                "configuration"));
        }

        SourceCandidate[] orderedCandidates = candidates
            .DistinctBy(static candidate => candidate.FullPath, PathComparer)
            .OrderBy(static candidate => candidate.Kind, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var findings = new Dictionary<(string Category, string Key), FindingAccumulator>();
        var analyzedFiles = new List<(string Identity, string Text)>();
        var scriptSources = new List<(string Path, string Text)>();
        var configurationFiles = new List<string>();
        long analyzedBytes = 0;
        int analyzedFileCount = 0;
        int scriptFileCount = 0;
        int nativeHeaderCount = 0;
        bool complete = diagnostics.Count == 0;
        foreach (string diagnostic in diagnostics)
        {
            Add(
                findings,
                "scanLimit",
                "directory-walk",
                "blocker",
                diagnostic,
                ".",
                1);
        }
        foreach (SourceCandidate candidate in orderedCandidates)
        {
            if (analyzedFileCount >= options.MaximumFileCount)
            {
                complete = false;
                AddLimitFinding(
                    findings,
                    "file-count",
                    candidate.RelativePath,
                    $"Maximum file count {options.MaximumFileCount} was reached.");
                continue;
            }
            var info = new FileInfo(candidate.FullPath);
            if (info.Length > options.MaximumFileBytes)
            {
                complete = false;
                AddLimitFinding(
                    findings,
                    "file-bytes",
                    candidate.RelativePath,
                    $"File exceeds the {options.MaximumFileBytes}-byte per-file limit.");
                continue;
            }
            if (analyzedBytes > options.MaximumTotalBytes - info.Length)
            {
                complete = false;
                AddLimitFinding(
                    findings,
                    "total-bytes",
                    candidate.RelativePath,
                    $"Maximum analyzed byte count {options.MaximumTotalBytes} was reached.");
                continue;
            }

            string text = File.ReadAllText(candidate.FullPath);
            analyzedBytes += info.Length;
            analyzedFileCount++;
            analyzedFiles.Add(($"{candidate.Kind}/{candidate.RelativePath}", text));
            switch (candidate.Kind)
            {
                case "script":
                    scriptFileCount++;
                    scriptSources.Add((candidate.RelativePath, text));
                    break;
                case "native":
                    nativeHeaderCount++;
                    AnalyzeNativeSurface(candidate.RelativePath, text, findings);
                    break;
                case "configuration":
                    configurationFiles.Add(candidate.RelativePath);
                    AnalyzeConfiguration(candidate.RelativePath, text, findings);
                    break;
            }
        }

        ScriptMetadata metadata = CollectScriptMetadata(scriptSources);
        foreach ((string path, string text) in scriptSources)
        {
            AnalyzeScript(path, text, metadata, findings);
        }

        if (scriptFileCount == 0)
        {
            complete = false;
            const string diagnostic =
                "No Puerts TypeScript or JavaScript sources were found under the selected roots.";
            diagnostics.Add(diagnostic);
            Add(
                findings,
                "scanCoverage",
                "no-script-sources",
                "blocker",
                diagnostic,
                ".",
                1);
        }
        foreach (UnrealPuertsProjectScanFinding limit in BuildFindings(findings)
                     .Where(static finding => finding.Category == "scanLimit"))
        {
            diagnostics.Add($"{limit.Key}: {limit.MigrationAction}");
        }

        UnrealPuertsProjectScanFinding[] orderedFindings = BuildFindings(findings);
        var summary = new UnrealPuertsProjectScanSummary(
            scriptFileCount,
            nativeHeaderCount,
            configurationFiles.Count,
            Count(orderedFindings, "module"),
            Count(orderedFindings, "scriptType"),
            Count(orderedFindings, "unrealApi"),
            Count(orderedFindings, "puertsApi"),
            Count(orderedFindings, "lifecycleHook"),
            Count(orderedFindings, "assetReference"),
            Count(orderedFindings, "delegatePattern"),
            Count(orderedFindings, "asyncPattern"),
            Count(orderedFindings, "unsupportedDynamicBehavior"),
            Count(orderedFindings, "nativeSurface"),
            orderedFindings.Count(static finding => finding.Risk == "review"),
            orderedFindings.Count(static finding => finding.Risk == "blocker"),
            orderedFindings.Length,
            analyzedBytes);
        string projectName = string.IsNullOrWhiteSpace(options.ProjectName)
            ? DiscoverProjectName(projectRoot)
            : options.ProjectName.Trim();
        return new UnrealPuertsProjectScan(
            ScanSchema,
            projectName,
            ComputeFingerprint(analyzedFiles),
            complete,
            scriptRoots.Select(path => RelativeOrDot(projectRoot, path)).ToArray(),
            nativeRoots.Select(path => RelativeOrDot(nativeProjectRoot, path)).ToArray(),
            configurationFiles.Order(StringComparer.Ordinal).ToArray(),
            orderedFindings,
            summary,
            diagnostics.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static ScriptMetadata CollectScriptMetadata(
        IReadOnlyList<(string Path, string Text)> scripts)
    {
        var classBases = new Dictionary<string, string>(StringComparer.Ordinal);
        var fields = new Dictionary<(string Class, string Field), string>();
        var classMethods = new HashSet<(string Class, string Method)>();
        foreach ((_, string text) in scripts)
        {
            string normalized = RemoveCommentsPreservingStrings(Normalize(text));
            string[] lines = normalized.Split('\n');
            string[] codeLines = MaskStrings(normalized).Split('\n');
            var unrealAliases = new HashSet<string>(["UE"], StringComparer.Ordinal);
            var puertsAliases = new HashSet<string>(["puerts"], StringComparer.Ordinal);
            CollectAliases(normalized, unrealAliases, puertsAliases);
            string? currentClass = null;
            int depth = 0;
            int classDepth = -1;
            for (int index = 0; index < codeLines.Length; index++)
            {
                string codeLine = codeLines[index];
                Match typeMatch = TypeRegex.Match(codeLine);
                if (typeMatch.Success)
                {
                    currentClass = typeMatch.Groups["name"].Value;
                    classBases[currentClass] = NormalizeType(
                        typeMatch.Groups["base"].Value,
                        unrealAliases);
                    classDepth = depth + 1;
                }
                if (currentClass is not null && depth == classDepth)
                {
                    Match fieldMatch = FieldRegex.Match(codeLine);
                    if (fieldMatch.Success)
                    {
                        fields[(currentClass, fieldMatch.Groups["name"].Value)] = NormalizeType(
                            fieldMatch.Groups["type"].Value,
                            unrealAliases);
                    }
                    Match methodMatch = MethodDefinitionRegex.Match(codeLine);
                    if (methodMatch.Success && methodMatch.Groups["name"].Value is not
                        ("if" or "for" or "while" or "switch" or "catch" or "using" or "lock"))
                    {
                        classMethods.Add((currentClass, methodMatch.Groups["name"].Value));
                    }
                }
                depth += CountChar(lines[index], '{') - CountChar(lines[index], '}');
                if (currentClass is not null && !typeMatch.Success && depth < classDepth)
                {
                    currentClass = null;
                    classDepth = -1;
                }
            }
        }
        return new ScriptMetadata(classBases, fields, classMethods);
    }

    private static void AnalyzeScript(
        string sourcePath,
        string text,
        ScriptMetadata metadata,
        Dictionary<(string Category, string Key), FindingAccumulator> findings)
    {
        string normalized = RemoveCommentsPreservingStrings(Normalize(text));
        string[] lines = normalized.Split('\n');
        string[] codeLines = MaskStrings(normalized).Split('\n');
        var unrealAliases = new HashSet<string>(["UE"], StringComparer.Ordinal);
        var puertsAliases = new HashSet<string>(["puerts"], StringComparer.Ordinal);
        CollectAliases(normalized, unrealAliases, puertsAliases);
        var locals = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentClass = null;
        int depth = 0;
        int classDepth = -1;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string codeLine = codeLines[index];
            int lineNumber = index + 1;
            Match typeMatch = TypeRegex.Match(codeLine);
            if (typeMatch.Success)
            {
                currentClass = typeMatch.Groups["name"].Value;
                classDepth = depth + 1;
                Add(
                    findings,
                    "scriptType",
                    $"{currentClass} extends {typeMatch.Groups["base"].Value}",
                    "info",
                    "generate-managed-script-type",
                    sourcePath,
                    lineNumber);
            }
            if (currentClass is not null)
            {
                foreach (Match methodMatch in LifecycleMethodRegex.Matches(codeLine))
                {
                    Add(
                        findings,
                        "lifecycleHook",
                        $"{currentClass}.{methodMatch.Groups["name"].Value}",
                        "review",
                        "map-world-generation-lifecycle",
                        sourcePath,
                        lineNumber);
                }
            }

            AddModuleFindings(line, sourcePath, lineNumber, findings);
            foreach (Match match in ModuleAugmentationRegex.Matches(line))
            {
                Add(
                    findings,
                    "unsupportedDynamicBehavior",
                    $"module-augmentation:{match.Groups["module"].Value}",
                    "review",
                    "convert-to-api-contract-extension",
                    sourcePath,
                    lineNumber);
            }
            foreach (Match match in DecoratorRegex.Matches(codeLine))
            {
                Add(
                    findings,
                    "puertsApi",
                    $"decorator:{match.Groups["name"].Value}",
                    "review",
                    "map-decorator-to-script-asset-metadata",
                    sourcePath,
                    lineNumber);
            }
            foreach (Match match in PuertsGlobalApiRegex.Matches(codeLine))
            {
                Add(
                    findings,
                    "puertsApi",
                    $"${match.Groups["name"].Value}",
                    "review",
                    "replace-with-generated-managed-binding",
                    sourcePath,
                    lineNumber);
            }
            foreach (Match match in MemberChainRegex.Matches(codeLine))
            {
                string root = match.Groups["root"].Value;
                string chain = match.Groups["chain"].Value;
                if (unrealAliases.Contains(root))
                {
                    Add(
                        findings,
                        "unrealApi",
                        "UE" + chain,
                        "info",
                        "generate-api-contract-binding",
                        sourcePath,
                        lineNumber);
                }
                else if (puertsAliases.Contains(root))
                {
                    Add(
                        findings,
                        "puertsApi",
                        "puerts" + chain,
                        "review",
                        "replace-with-finare-runtime-api",
                        sourcePath,
                        lineNumber);
                }
            }
            foreach (Match match in TypedLocalRegex.Matches(codeLine))
            {
                locals[match.Groups["name"].Value] = NormalizeType(
                    match.Groups["type"].Value,
                    unrealAliases);
            }
            foreach (Match match in RefLocalRegex.Matches(codeLine))
            {
                locals[match.Groups["name"].Value] = NormalizeType(
                    match.Groups["type"].Value,
                    unrealAliases);
            }
            Match unrefMatch = UnrefLocalRegex.Match(codeLine);
            if (unrefMatch.Success &&
                locals.TryGetValue(unrefMatch.Groups["source"].Value, out string? unrefType))
            {
                locals[unrefMatch.Groups["name"].Value] = unrefType;
            }
            if (currentClass is not null)
            {
                foreach (Match match in ThisCallRegex.Matches(codeLine))
                {
                    string[] chain = match.Groups["chain"].Value.Split(
                        '.',
                        StringSplitOptions.RemoveEmptyEntries);
                    if (chain.Length == 0 || IsScriptMethod(currentClass, chain[0], metadata))
                    {
                        continue;
                    }
                    string? ownerType;
                    string member;
                    if (chain.Length >= 2 && TryResolveFieldType(
                            currentClass,
                            chain[0],
                            metadata.ClassBases,
                            metadata.Fields,
                            out string? fieldType))
                    {
                        if (IsScriptMethod(fieldType, chain[^1], metadata))
                        {
                            continue;
                        }
                        ownerType = ResolveUnrealBase(fieldType, metadata.ClassBases);
                        member = chain[^1];
                    }
                    else if (chain.Length >= 2)
                    {
                        continue;
                    }
                    else
                    {
                        ownerType = ResolveUnrealBase(currentClass, metadata.ClassBases);
                        member = chain[0];
                    }
                    if (ownerType is not null)
                    {
                        Add(
                            findings,
                            "unrealApi",
                            $"{ownerType}.{member}",
                            "info",
                            "generate-api-contract-binding",
                            sourcePath,
                            lineNumber);
                    }
                }
                foreach (Match match in ThisPropertyWriteRegex.Matches(codeLine))
                {
                    if (TryResolveFieldType(
                            currentClass,
                            match.Groups["field"].Value,
                            metadata.ClassBases,
                            metadata.Fields,
                            out string? fieldType))
                    {
                        string? ownerType = ResolveUnrealBase(fieldType, metadata.ClassBases);
                        if (ownerType is not null)
                        {
                            Add(
                                findings,
                                "unrealApi",
                                $"{ownerType}.{match.Groups["member"].Value}:set",
                                "info",
                                "generate-api-contract-binding",
                                sourcePath,
                                lineNumber);
                        }
                    }
                }
            }
            foreach (Match match in ReceiverCallRegex.Matches(codeLine))
            {
                string receiver = match.Groups["receiver"].Value;
                string member = match.Groups["member"].Value;
                string prefix = codeLine[..match.Index];
                if (receiver == "this" || unrealAliases.Contains(receiver) ||
                    puertsAliases.Contains(receiver) || IgnoredReceivers.Contains(receiver) ||
                    unrealAliases.Any(alias => prefix.EndsWith(alias + ".", StringComparison.Ordinal)) ||
                    codeLine.Contains($"this.{receiver}.", StringComparison.Ordinal))
                {
                    continue;
                }
                if (locals.TryGetValue(receiver, out string? localType))
                {
                    string? ownerType = ResolveUnrealBase(localType, metadata.ClassBases) ??
                        (localType.StartsWith("UE.", StringComparison.Ordinal) ? localType : null);
                    if (ownerType is not null)
                    {
                        Add(
                            findings,
                            "unrealApi",
                            $"{ownerType}.{member}",
                            "info",
                            "generate-api-contract-binding",
                            sourcePath,
                            lineNumber);
                    }
                }
                else if (member.StartsWith("op_", StringComparison.Ordinal))
                {
                    Add(
                        findings,
                        "unrealApi",
                        $"UE.Vector.{member}",
                        "info",
                        "generate-api-contract-binding",
                        sourcePath,
                        lineNumber);
                }
            }
            foreach (Match match in ChainedResultCallRegex.Matches(codeLine))
            {
                Add(
                    findings,
                    "unrealApi",
                    $"UE.Vector.{match.Groups["member"].Value}",
                    "info",
                    "generate-api-contract-binding",
                    sourcePath,
                    lineNumber);
            }
            foreach (Match match in AssetReferenceRegex.Matches(line))
            {
                Add(
                    findings,
                    "assetReference",
                    match.Groups["path"].Value,
                    "info",
                    "generate-typed-resource-loader",
                    sourcePath,
                    lineNumber);
            }
            foreach (Match match in DelegateRegex.Matches(codeLine))
            {
                Add(
                    findings,
                    "delegatePattern",
                    $"{match.Groups["receiver"].Value}.{match.Groups["operation"].Value}",
                    "review",
                    "generate-generation-owned-delegate-binding",
                    sourcePath,
                    lineNumber);
            }
            AnalyzeAsyncPatterns(codeLine, sourcePath, lineNumber, findings);
            AnalyzeDynamicPatterns(
                codeLine,
                sourcePath,
                lineNumber,
                unrealAliases,
                findings);

            depth += CountChar(line, '{') - CountChar(line, '}');
            if (currentClass is not null && !typeMatch.Success && depth < classDepth)
            {
                currentClass = null;
                classDepth = -1;
            }
        }
    }

    private static void CollectAliases(
        string text,
        ISet<string> unrealAliases,
        ISet<string> puertsAliases)
    {
        foreach (Regex regex in new[] { NamespaceImportRegex, DefaultImportRegex, RequireAliasRegex })
        {
            foreach (Match match in regex.Matches(text))
            {
                ISet<string> target = match.Groups["module"].Value == "ue"
                    ? unrealAliases
                    : puertsAliases;
                target.Add(match.Groups["alias"].Value);
            }
        }
    }

    private static string NormalizeType(string type, IReadOnlySet<string> unrealAliases)
    {
        int separator = type.IndexOf('.');
        return separator > 0 && unrealAliases.Contains(type[..separator])
            ? "UE" + type[separator..]
            : type;
    }

    private static bool TryResolveFieldType(
        string ownerClass,
        string field,
        IReadOnlyDictionary<string, string> classBases,
        IReadOnlyDictionary<(string Class, string Field), string> fields,
        out string fieldType)
    {
        string current = ownerClass;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(current))
        {
            if (fields.TryGetValue((current, field), out fieldType!))
            {
                return true;
            }
            if (!classBases.TryGetValue(current, out current!))
            {
                break;
            }
        }
        fieldType = string.Empty;
        return false;
    }

    private static string? ResolveUnrealBase(
        string type,
        IReadOnlyDictionary<string, string> classBases)
    {
        string current = type;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!current.StartsWith("UE.", StringComparison.Ordinal))
        {
            if (!visited.Add(current) || !classBases.TryGetValue(current, out current!))
            {
                return null;
            }
        }
        return current;
    }

    private static bool IsScriptMethod(
        string type,
        string method,
        ScriptMetadata metadata)
    {
        string current = type;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!current.StartsWith("UE.", StringComparison.Ordinal) && visited.Add(current))
        {
            if (metadata.ClassMethods.Contains((current, method)))
            {
                return true;
            }
            if (!metadata.ClassBases.TryGetValue(current, out current!))
            {
                break;
            }
        }
        return false;
    }

    private static void AddModuleFindings(
        string line,
        string sourcePath,
        int lineNumber,
        Dictionary<(string Category, string Key), FindingAccumulator> findings)
    {
        foreach (Regex regex in new[] { FromModuleRegex, RequireModuleRegex })
        {
            foreach (Match match in regex.Matches(line))
            {
                Add(
                    findings,
                    "module",
                    match.Groups["module"].Value,
                    "info",
                    "map-module-dependency",
                    sourcePath,
                    lineNumber);
            }
        }
    }

    private static void AnalyzeAsyncPatterns(
        string line,
        string sourcePath,
        int lineNumber,
        Dictionary<(string Category, string Key), FindingAccumulator> findings)
    {
        AddWhenPresent("async ", "async-function", "map-to-task", line);
        AddWhenPresent("await ", "await", "map-to-task", line);
        AddWhenPresent("new Promise", "promise", "map-to-task-completion-source", line);
        AddWhenPresent("setTimeout", "timer:setTimeout", "map-to-world-owned-timer", line);
        AddWhenPresent("setInterval", "timer:setInterval", "map-to-world-owned-timer", line);
        AddWhenPresent("queueMicrotask", "microtask", "map-to-world-phase-scheduler", line);

        void AddWhenPresent(string token, string key, string action, string value)
        {
            if (value.Contains(token, StringComparison.Ordinal))
            {
                Add(
                    findings,
                    "asyncPattern",
                    key,
                    "review",
                    action,
                    sourcePath,
                    lineNumber);
            }
        }
    }

    private static void AnalyzeDynamicPatterns(
        string line,
        string sourcePath,
        int lineNumber,
        IReadOnlySet<string> unrealAliases,
        Dictionary<(string Category, string Key), FindingAccumulator> findings)
    {
        AddMatch(EvalRegex, "eval", "blocker", "rewrite-runtime-code-generation");
        AddMatch(
            FunctionConstructorRegex,
            "function-constructor",
            "blocker",
            "rewrite-runtime-code-generation");
        AddMatch(ProxyRegex, "proxy", "blocker", "replace-proxy-with-typed-wrapper");
        foreach (Match match in DynamicModuleRegex.Matches(line))
        {
            Add(
                findings,
                "unsupportedDynamicBehavior",
                $"dynamic-{match.Groups["kind"].Value}",
                "blocker",
                "replace-with-static-module-reference",
                sourcePath,
                lineNumber);
        }
        foreach (string alias in unrealAliases)
        {
            var computedMember = new Regex(
                $@"\b{Regex.Escape(alias)}\s*\[\s*(?!['""])",
                RegexOptions.CultureInvariant);
            if (computedMember.IsMatch(line))
            {
                Add(
                    findings,
                    "unsupportedDynamicBehavior",
                    "computed-unreal-member",
                    "blocker",
                    "replace-with-generated-typed-member-access",
                    sourcePath,
                    lineNumber);
            }
        }
        foreach (Match match in PrototypeMutationRegex.Matches(line))
        {
            Add(
                findings,
                "unsupportedDynamicBehavior",
                $"prototype-mutation:{match.Groups["symbol"].Value}",
                "review",
                "replace-with-managed-extension-or-generated-binding",
                sourcePath,
                lineNumber);
        }
        foreach (Match match in GlobalMutationRegex.Matches(line))
        {
            Add(
                findings,
                "unsupportedDynamicBehavior",
                $"global-mutation:{match.Groups["member"].Value}",
                "review",
                "replace-with-world-scoped-service",
                sourcePath,
                lineNumber);
        }

        void AddMatch(Regex regex, string key, string risk, string action)
        {
            if (regex.IsMatch(line))
            {
                Add(
                    findings,
                    "unsupportedDynamicBehavior",
                    key,
                    risk,
                    action,
                    sourcePath,
                    lineNumber);
            }
        }
    }

    private static void AnalyzeNativeSurface(
        string sourcePath,
        string text,
        Dictionary<(string Category, string Key), FindingAccumulator> findings)
    {
        string[] lines = Normalize(text).Split('\n');
        string? currentClass = null;
        bool pendingFunction = false;
        int annotationLine = 0;
        var declaration = new StringBuilder();
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            Match classMatch = NativeClassRegex.Match(line);
            if (classMatch.Success)
            {
                currentClass = classMatch.Groups["name"].Value;
            }
            if (line.Contains("UFUNCTION", StringComparison.Ordinal))
            {
                pendingFunction = true;
                annotationLine = index + 1;
                declaration.Clear();
                int closingParenthesis = line.LastIndexOf(')');
                if (closingParenthesis >= 0 && closingParenthesis + 1 < line.Length)
                {
                    declaration.Append(line[(closingParenthesis + 1)..]);
                }
                continue;
            }
            if (!pendingFunction)
            {
                continue;
            }
            declaration.Append(' ').Append(line.Trim());
            if (!line.Contains(';', StringComparison.Ordinal))
            {
                continue;
            }
            Match functionMatch = NativeFunctionRegex.Match(declaration.ToString());
            if (functionMatch.Success && currentClass is not null)
            {
                Add(
                    findings,
                    "nativeSurface",
                    $"{currentClass}.{functionMatch.Groups["name"].Value}",
                    "review",
                    "classify-contract-or-project-native-boundary",
                    sourcePath,
                    annotationLine);
            }
            pendingFunction = false;
            declaration.Clear();
        }
    }

    private static void AnalyzeConfiguration(
        string sourcePath,
        string text,
        Dictionary<(string Category, string Key), FindingAccumulator> findings)
    {
        if (text.Contains("\"Puerts\"", StringComparison.OrdinalIgnoreCase) ||
            sourcePath.Contains("Puerts", StringComparison.OrdinalIgnoreCase))
        {
            Add(
                findings,
                "configuration",
                $"puerts-reference:{sourcePath}",
                "review",
                "plan-incremental-runtime-coexistence",
                sourcePath,
                1);
        }
        if (text.Contains("\"experimentalDecorators\"", StringComparison.Ordinal) &&
            text.Contains("true", StringComparison.OrdinalIgnoreCase))
        {
            Add(
                findings,
                "configuration",
                "typescript:experimentalDecorators",
                "review",
                "map-decorators-to-script-asset-metadata",
                sourcePath,
                1);
        }
    }

    private static string[] DiscoverScriptRoots(string projectRoot, ICollection<string> diagnostics)
    {
        var roots = new HashSet<string>(PathComparer);
        AddExistingRoots(DefaultSourceScriptRoots);
        if (roots.Count == 0)
        {
            AddExistingRoots(DefaultDeployedScriptRoots);
        }
        if (roots.Count == 0)
        {
            string[] configPaths = WalkFiles(
                projectRoot,
                static path => Path.GetFileName(path).StartsWith(
                        "tsconfig",
                        StringComparison.OrdinalIgnoreCase) &&
                    Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase),
                10_000,
                diagnostics);
            foreach (string configPath in configPaths)
            {
                roots.Add(Path.GetDirectoryName(configPath)!);
            }
        }
        if (roots.Count == 0 && Directory.EnumerateFiles(projectRoot, "*", SearchOption.TopDirectoryOnly)
                .Any(IsScriptPath))
        {
            roots.Add(projectRoot);
        }
        return RemoveNestedRoots(roots);

        void AddExistingRoots(IEnumerable<string> candidates)
        {
            foreach (string candidate in candidates)
            {
                string path = Path.Combine(
                    projectRoot,
                    candidate.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(path))
                {
                    roots.Add(Path.GetFullPath(path));
                }
            }
        }
    }

    private static string[] DiscoverNativeRoots(string projectRoot)
    {
        var roots = new HashSet<string>(PathComparer);
        string sourceRoot = Path.Combine(projectRoot, "Source");
        if (Directory.Exists(sourceRoot))
        {
            roots.Add(Path.GetFullPath(sourceRoot));
        }
        string pluginsRoot = Path.Combine(projectRoot, "Plugins");
        if (Directory.Exists(pluginsRoot))
        {
            foreach (string pluginRoot in Directory.EnumerateDirectories(
                         pluginsRoot,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                string pluginName = Path.GetFileName(pluginRoot);
                if (pluginName.Equals("Puerts", StringComparison.OrdinalIgnoreCase) ||
                    pluginName.Equals("Finare", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string pluginSource = Path.Combine(pluginRoot, "Source");
                if (Directory.Exists(pluginSource))
                {
                    roots.Add(Path.GetFullPath(pluginSource));
                }
            }
        }
        return roots.Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] DiscoverConfigurationFiles(
        string projectRoot,
        IReadOnlyList<string> scriptRoots,
        int maximumFileCount,
        ICollection<string> diagnostics)
    {
        var paths = new HashSet<string>(PathComparer);
        foreach (string path in Directory.EnumerateFiles(projectRoot, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (IsTypeScriptConfiguration(name) ||
                name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(name).Equals(".uproject", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(Path.GetFullPath(path));
            }
        }
        foreach (string scriptRoot in scriptRoots)
        {
            foreach (string path in WalkFiles(
                         scriptRoot,
                         static candidate =>
                         {
                             string name = Path.GetFileName(candidate);
                             return IsTypeScriptConfiguration(name) ||
                                 name.Equals("package.json", StringComparison.OrdinalIgnoreCase);
                         },
                         maximumFileCount,
                         diagnostics))
            {
                paths.Add(Path.GetFullPath(path));
            }
        }
        string pluginsRoot = Path.Combine(projectRoot, "Plugins");
        if (Directory.Exists(pluginsRoot))
        {
            foreach (string pluginRoot in Directory.EnumerateDirectories(
                         pluginsRoot,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                foreach (string descriptor in Directory.EnumerateFiles(
                             pluginRoot,
                             "*.uplugin",
                             SearchOption.TopDirectoryOnly))
                {
                    paths.Add(Path.GetFullPath(descriptor));
                }
            }
        }
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsTypeScriptConfiguration(string name) =>
        name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase) &&
        Path.GetExtension(name).Equals(".json", StringComparison.OrdinalIgnoreCase);

    private static void AddCandidates(
        ICollection<SourceCandidate> candidates,
        IReadOnlyList<string> roots,
        string ownerRoot,
        string kind,
        Func<string, bool> predicate,
        int maximumFileCount,
        ICollection<string> diagnostics)
    {
        foreach (string root in roots)
        {
            foreach (string path in WalkFiles(root, predicate, maximumFileCount, diagnostics))
            {
                candidates.Add(new SourceCandidate(
                    Path.GetFullPath(path),
                    Relative(ownerRoot, path),
                    kind));
            }
        }
    }

    private static string[] WalkFiles(
        string root,
        Func<string, bool> predicate,
        int maximumFileCount,
        ICollection<string> diagnostics)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        int visitedFiles = 0;
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"Skipped unreadable directory '{directory}': {exception.Message}");
                continue;
            }
            foreach (string file in files.Order(StringComparer.Ordinal))
            {
                visitedFiles++;
                if (visitedFiles > maximumFileCount)
                {
                    diagnostics.Add(
                        $"Directory walk under '{root}' stopped at {maximumFileCount} files.");
                    return result.Order(StringComparer.Ordinal).ToArray();
                }
                if (predicate(file))
                {
                    result.Add(file);
                }
            }
            foreach (string child in directories.OrderDescending(StringComparer.Ordinal))
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(child)) &&
                    !IsReparsePoint(child))
                {
                    pending.Push(child);
                }
            }
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string[] ResolveRoots(
        string ownerRoot,
        IReadOnlyList<string>? requested,
        IReadOnlyList<string> discovered,
        string kind)
    {
        IReadOnlyList<string> selected = requested is { Count: > 0 } ? requested : discovered;
        var roots = new HashSet<string>(PathComparer);
        foreach (string root in selected)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException($"The {kind} root list contains an empty path.");
            }
            string fullPath = Path.GetFullPath(
                Path.IsPathFullyQualified(root) ? root : Path.Combine(ownerRoot, root));
            EnsureUnderRoot(ownerRoot, fullPath, kind);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException($"Puerts {kind} root does not exist: {fullPath}");
            }
            roots.Add(fullPath);
        }
        return RemoveNestedRoots(roots);
    }

    private static string[] RemoveNestedRoots(IEnumerable<string> roots)
    {
        string[] ordered = roots
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .OrderBy(static path => path.Length)
            .ThenBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        return ordered
            .Where((path, index) => !ordered.Take(index).Any(parent => IsUnderRoot(parent, path)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void EnsureUnderRoot(string root, string path, string kind)
    {
        if (!IsUnderRoot(root, path))
        {
            throw new ArgumentException(
                $"Puerts {kind} root must stay inside '{root}': {path}");
        }
    }

    private static bool IsUnderRoot(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative == "." ||
            !Path.IsPathFullyQualified(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void ValidateOptions(UnrealPuertsProjectScanOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProjectRoot);
        if (options.MaximumFileCount is <= 0 or > HardMaximumFileCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"MaximumFileCount must be between 1 and {HardMaximumFileCount}.");
        }
        if (options.MaximumFileBytes is <= 0 or > HardMaximumFileBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"MaximumFileBytes must be between 1 and {HardMaximumFileBytes}.");
        }
        if (options.MaximumTotalBytes is <= 0 or > HardMaximumTotalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"MaximumTotalBytes must be between 1 and {HardMaximumTotalBytes}.");
        }
    }

    private static string ResolveExistingDirectory(string path, string description)
    {
        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"{description} does not exist: {fullPath}");
        }
        return fullPath;
    }

    private static string DiscoverProjectName(string projectRoot)
    {
        string? projectFile = Directory.EnumerateFiles(
                projectRoot,
                "*.uproject",
                SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        return projectFile is null
            ? Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar))
            : Path.GetFileNameWithoutExtension(projectFile);
    }

    private static void AddLimitFinding(
        Dictionary<(string Category, string Key), FindingAccumulator> findings,
        string limit,
        string sourcePath,
        string diagnostic) =>
        Add(
            findings,
            "scanLimit",
            $"{limit}:{sourcePath}",
            "blocker",
            diagnostic,
            sourcePath,
            1);

    private static void Add(
        Dictionary<(string Category, string Key), FindingAccumulator> findings,
        string category,
        string key,
        string risk,
        string migrationAction,
        string sourcePath,
        int line)
    {
        var findingKey = (category, key);
        if (!findings.TryGetValue(findingKey, out FindingAccumulator? finding))
        {
            finding = new FindingAccumulator(category, key, risk, migrationAction);
            findings.Add(findingKey, finding);
        }
        finding.Locations.Add(new UnrealPuertsMigrationLocation(sourcePath, line));
    }

    private static UnrealPuertsProjectScanFinding[] BuildFindings(
        IReadOnlyDictionary<(string Category, string Key), FindingAccumulator> findings) =>
        findings.Values
            .OrderBy(static finding => finding.Category, StringComparer.Ordinal)
            .ThenBy(static finding => finding.Key, StringComparer.Ordinal)
            .Select(static finding => new UnrealPuertsProjectScanFinding(
                finding.Category,
                finding.Key,
                finding.Risk,
                finding.MigrationAction,
                finding.Locations
                    .Distinct()
                    .OrderBy(static location => location.SourcePath, StringComparer.Ordinal)
                    .ThenBy(static location => location.Line)
                    .ToArray()))
            .ToArray();

    private static int Count(
        IReadOnlyList<UnrealPuertsProjectScanFinding> findings,
        string category) =>
        findings.Count(finding => finding.Category == category);

    private static bool IsScriptPath(string path) =>
        ScriptExtensions.Contains(Path.GetExtension(path)) &&
        !path.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase);

    private static int CountChar(string value, char character) =>
        value.Count(current => current == character);

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string RelativeOrDot(string root, string path)
    {
        string relative = Relative(root, path);
        return relative.Length == 0 ? "." : relative;
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string RemoveCommentsPreservingStrings(string value)
    {
        var result = new StringBuilder(value.Length);
        bool inLineComment = false;
        bool inBlockComment = false;
        char quote = '\0';
        bool escaped = false;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            char next = index + 1 < value.Length ? value[index + 1] : '\0';
            if (inLineComment)
            {
                if (current == '\n')
                {
                    inLineComment = false;
                    result.Append(current);
                }
                else
                {
                    result.Append(' ');
                }
                continue;
            }
            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    result.Append("  ");
                    index++;
                    inBlockComment = false;
                }
                else
                {
                    result.Append(current == '\n' ? '\n' : ' ');
                }
                continue;
            }
            if (quote != '\0')
            {
                result.Append(current);
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (current is '\'' or '"' or '`')
            {
                quote = current;
                result.Append(current);
            }
            else if (current == '/' && next == '/')
            {
                result.Append("  ");
                index++;
                inLineComment = true;
            }
            else if (current == '/' && next == '*')
            {
                result.Append("  ");
                index++;
                inBlockComment = true;
            }
            else
            {
                result.Append(current);
            }
        }
        return result.ToString();
    }

    private static string MaskStrings(string value)
    {
        var result = new StringBuilder(value.Length);
        char quote = '\0';
        bool escaped = false;
        foreach (char current in value)
        {
            if (quote == '\0')
            {
                if (current is '\'' or '"' or '`')
                {
                    quote = current;
                    result.Append(' ');
                }
                else
                {
                    result.Append(current);
                }
                continue;
            }
            result.Append(current == '\n' ? '\n' : ' ');
            if (escaped)
            {
                escaped = false;
            }
            else if (current == '\\')
            {
                escaped = true;
            }
            else if (current == quote)
            {
                quote = '\0';
            }
        }
        return result.ToString();
    }

    private static string ComputeFingerprint(IReadOnlyList<(string Identity, string Text)> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string identity, string text) in files.OrderBy(
                     static file => file.Identity,
                     StringComparer.Ordinal))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(
                identity + "\n" + Normalize(text) + "\n");
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record SourceCandidate(string FullPath, string RelativePath, string Kind);

    private sealed record ScriptMetadata(
        IReadOnlyDictionary<string, string> ClassBases,
        IReadOnlyDictionary<(string Class, string Field), string> Fields,
        IReadOnlySet<(string Class, string Method)> ClassMethods);

    private sealed class FindingAccumulator(
        string category,
        string key,
        string risk,
        string migrationAction)
    {
        public string Category { get; } = category;

        public string Key { get; } = key;

        public string Risk { get; } = risk;

        public string MigrationAction { get; } = migrationAction;

        public List<UnrealPuertsMigrationLocation> Locations { get; } = [];
    }
}
