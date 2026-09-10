using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Finare.Unreal;

public sealed record UnrealPuertsMigrationLocation(string SourcePath, int Line);

public sealed record UnrealPuertsMigrationItem(
    string Kind,
    string Key,
    IReadOnlyList<UnrealPuertsMigrationLocation> Locations);

public sealed record UnrealPuertsMigrationInventorySummary(
    int ScriptCount,
    int ScriptTypeCount,
    int ScriptMethodCount,
    int LifecycleHookCount,
    int ImportCount,
    int PuertsApiCount,
    int UnrealApiCount,
    int DelegateCount,
    int ResourceCount,
    int GlobalPatternCount,
    int AsyncPatternCount,
    int DecoratorCount,
    int NativeFacadeCount,
    int ItemCount);

public sealed record UnrealPuertsMigrationInventory(
    string SchemaVersion,
    string ProjectName,
    string SourceFingerprint,
    IReadOnlyList<UnrealPuertsMigrationItem> Items,
    UnrealPuertsMigrationInventorySummary Summary);

public sealed record UnrealPuertsMigrationCoverageEntry(
    string Kind,
    string Key,
    string Route,
    string Evidence,
    string Notes = "");

public sealed record UnrealPuertsMigrationCoverage(
    string SchemaVersion,
    string ProjectName,
    string InventoryFingerprint,
    IReadOnlyList<UnrealPuertsMigrationCoverageEntry> Entries);

public sealed record UnrealPuertsMigrationCoverageReport(
    string SchemaVersion,
    string ProjectName,
    string InventoryFingerprint,
    bool FingerprintMatches,
    bool Passed,
    int InventoryItemCount,
    int CoveredItemCount,
    int UncoveredItemCount,
    int StaleEntryCount,
    IReadOnlyList<UnrealPuertsMigrationItem> UncoveredItems,
    IReadOnlyList<UnrealPuertsMigrationCoverageEntry> StaleEntries,
    IReadOnlyList<string> Diagnostics);

public static class UnrealPuertsMigrationJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(UnrealPuertsMigrationInventory inventory) =>
        JsonSerializer.Serialize(inventory, Options) + "\n";

    public static string Serialize(UnrealPuertsMigrationCoverageReport report) =>
        JsonSerializer.Serialize(report, Options) + "\n";

    public static UnrealPuertsMigrationCoverage DeserializeCoverage(string json)
    {
        UnrealPuertsMigrationCoverage coverage = JsonSerializer.Deserialize<
            UnrealPuertsMigrationCoverage>(json, Options) ??
            throw new JsonException("Puerts migration coverage JSON is empty.");
        if (coverage.SchemaVersion != "unifiedscript.puerts-migration-coverage/v0.1")
        {
            throw new JsonException(
                $"Unsupported Puerts migration coverage schema '{coverage.SchemaVersion}'.");
        }
        if (string.IsNullOrWhiteSpace(coverage.ProjectName) ||
            string.IsNullOrWhiteSpace(coverage.InventoryFingerprint) ||
            coverage.Entries is null)
        {
            throw new JsonException("Puerts migration coverage is incomplete.");
        }
        return coverage;
    }
}

public sealed class UnrealPuertsMigrationInventoryBuilder
{
    private const string InventorySchema = "unifiedscript.puerts-project-inventory/v0.1";

    private static readonly Regex ImportRegex = new(
        @"\b(?:from\s+|import\s*)['""](?<module>[^'""]+)['""]",
        RegexOptions.CultureInvariant);
    private static readonly Regex ClassRegex = new(
        @"\bclass\s+(?<name>[A-Za-z_$][\w$]*)\s+extends\s+(?<base>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)\s*\{",
        RegexOptions.CultureInvariant);
    private static readonly Regex FieldRegex = new(
        @"^\s*(?<name>[A-Za-z_$][\w$]*)\??\s*:\s*(?<type>(?:UE\.)?[A-Za-z_$][\w$]*)\s*(?:;|=)",
        RegexOptions.CultureInvariant);
    private static readonly Regex MethodRegex = new(
        @"^\s*(?<async>async\s+)?(?<name>[A-Za-z_$][\w$]*)\s*\([^)]*\)\s*(?::[^\{]+)?\{",
        RegexOptions.CultureInvariant);
    private static readonly Regex GlobalFunctionRegex = new(
        @"^\s*(?:export\s+)?(?<async>async\s+)?function\s+(?<name>[A-Za-z_$][\w$]*)\s*\(",
        RegexOptions.CultureInvariant);
    private static readonly Regex DecoratorRegex = new(
        @"@(?<name>[A-Za-z_$][\w$-]*(?:\.[A-Za-z_$][\w$-]*)*)",
        RegexOptions.CultureInvariant);
    private static readonly Regex ResourceRegex = new(
        @"['""](?<path>/Game/[^'""]+)['""]",
        RegexOptions.CultureInvariant);
    private static readonly Regex ExplicitUnrealRegex = new(
        @"\bUE(?:\.[A-Za-z_$][\w$]*)+",
        RegexOptions.CultureInvariant);
    private static readonly Regex ThisCallRegex = new(
        @"\bthis(?<chain>(?:\.[A-Za-z_$][\w$]*)+)(?=\s*\()",
        RegexOptions.CultureInvariant);
    private static readonly Regex ThisPropertyWriteRegex = new(
        @"\bthis\.(?<field>[A-Za-z_$][\w$]*)\.(?<member>[A-Za-z_$][\w$]*)\s*=",
        RegexOptions.CultureInvariant);
    private static readonly Regex TypedLocalRegex = new(
        @"\b(?:let|const|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*:\s*(?<type>UE\.[A-Za-z_$][\w$]*)",
        RegexOptions.CultureInvariant);
    private static readonly Regex RefLocalRegex = new(
        @"\b(?:let|const|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*\$ref<(?<type>UE\.[A-Za-z_$][\w$]*)>",
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
    private static readonly Regex PuertsApiRegex = new(
        @"\$(?<name>ref|unref)\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex ModuleAugmentationRegex = new(
        @"\bdeclare\s+module\s+['""](?<module>[^'""]+)['""]",
        RegexOptions.CultureInvariant);
    private static readonly Regex PrototypeMutationRegex = new(
        @"(?<symbol>UE(?:\.[A-Za-z_$][\w$]*)+\.prototype(?:\.[A-Za-z_$][\w$]*)+)\s*=",
        RegexOptions.CultureInvariant);
    private static readonly Regex NativeClassRegex = new(
        @"\bclass\s+(?:\w+_API\s+)?(?<name>[A-Za-z_$][\w$]*)\s+(?:final\s*)?:\s*public\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex NativeFunctionRegex = new(
        @"\b(?<name>[A-Za-z_$][\w$]*)\s*\([^;{}]*\)\s*(?:const\s*)?;",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly HashSet<string> LifecycleNames = new(StringComparer.Ordinal)
    {
        "Constructor",
        "ReceiveBeginPlay",
        "ReceiveEndPlay",
        "ReceiveTick",
        "BeginPlay",
        "EndPlay",
        "Tick",
    };

    private static readonly HashSet<string> IgnoredReceivers = new(StringComparer.Ordinal)
    {
        "console",
        "Math",
        "Promise",
        "Object",
        "Array",
        "JSON",
        "super",
        "uproperty",
    };

    public UnrealPuertsMigrationInventory Build(string projectRoot)
        => Build(projectRoot, projectRoot, projectName: null);

    public UnrealPuertsMigrationInventory Build(
        string puertsProjectRoot,
        string nativeProjectRoot,
        string? projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(puertsProjectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeProjectRoot);
        string fullRoot = Path.GetFullPath(puertsProjectRoot);
        string fullNativeRoot = Path.GetFullPath(nativeProjectRoot);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(
                $"Puerts project root does not exist: {fullRoot}");
        }
        if (!Directory.Exists(fullNativeRoot))
        {
            throw new DirectoryNotFoundException(
                $"Migration target project root does not exist: {fullNativeRoot}");
        }

        string typescriptRoot = Path.Combine(fullRoot, "TypeScript");
        string nativeRoot = Path.Combine(fullNativeRoot, "Source");
        string[] scriptPaths = Directory.Exists(typescriptRoot)
            ? Directory.EnumerateFiles(typescriptRoot, "*.ts", SearchOption.AllDirectories)
                .Where(static path => !path.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Relative(fullRoot, path), StringComparer.Ordinal)
                .ToArray()
            : [];
        string[] nativePaths = Directory.Exists(nativeRoot)
            ? Directory.EnumerateFiles(nativeRoot, "*", SearchOption.AllDirectories)
                .Where(static path =>
                    path.EndsWith(".h", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase))
                .Where(path => !Relative(fullNativeRoot, path).Contains(
                    "/Generated/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Relative(fullNativeRoot, path), StringComparer.Ordinal)
                .ToArray()
            : [];

        var items = new Dictionary<(string Kind, string Key), List<UnrealPuertsMigrationLocation>>();
        var analyzedFiles = new List<(string Path, string Text)>();
        (string Path, string Text)[] scripts = scriptPaths
            .Select(path => (Relative(fullRoot, path), File.ReadAllText(path)))
            .ToArray();
        var classBases = new Dictionary<string, string>(StringComparer.Ordinal);
        var fields = new Dictionary<(string Class, string Field), string>();
        var classMethods = new HashSet<(string Class, string Method)>();
        foreach ((_, string text) in scripts)
        {
            CollectScriptMetadata(text, classBases, fields, classMethods);
        }
        foreach ((string relativePath, string text) in scripts)
        {
            analyzedFiles.Add((relativePath, text));
            AnalyzeScript(
                relativePath,
                text,
                classBases,
                fields,
                classMethods,
                items);
        }
        foreach (string nativePath in nativePaths)
        {
            string text = File.ReadAllText(nativePath);
            string relativePath = Relative(fullNativeRoot, nativePath);
            analyzedFiles.Add((relativePath, text));
            AnalyzeNativeFacades(relativePath, text, items);
        }

        UnrealPuertsMigrationItem[] orderedItems = items
            .OrderBy(static pair => pair.Key.Kind, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.Key, StringComparer.Ordinal)
            .Select(static pair => new UnrealPuertsMigrationItem(
                pair.Key.Kind,
                pair.Key.Key,
                pair.Value
                    .Distinct()
                    .OrderBy(static location => location.SourcePath, StringComparer.Ordinal)
                    .ThenBy(static location => location.Line)
                    .ToArray()))
            .ToArray();
        var summary = new UnrealPuertsMigrationInventorySummary(
            Count(orderedItems, "script"),
            Count(orderedItems, "scriptType"),
            Count(orderedItems, "scriptMethod"),
            Count(orderedItems, "lifecycleHook"),
            Count(orderedItems, "import"),
            Count(orderedItems, "puertsApi"),
            Count(orderedItems, "unrealApi"),
            Count(orderedItems, "delegate"),
            Count(orderedItems, "resource"),
            Count(orderedItems, "globalPattern"),
            Count(orderedItems, "asyncPattern"),
            Count(orderedItems, "decorator"),
            Count(orderedItems, "nativeFacade"),
            orderedItems.Length);
        return new UnrealPuertsMigrationInventory(
            InventorySchema,
            string.IsNullOrWhiteSpace(projectName)
                ? Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar))
                : projectName,
            ComputeFingerprint(analyzedFiles),
            orderedItems,
            summary);
    }

    private static void AnalyzeScript(
        string sourcePath,
        string text,
        IReadOnlyDictionary<string, string> classBases,
        IReadOnlyDictionary<(string Class, string Field), string> fields,
        IReadOnlySet<(string Class, string Method)> classMethods,
        Dictionary<(string Kind, string Key), List<UnrealPuertsMigrationLocation>> items)
    {
        Add(items, "script", sourcePath, sourcePath, 1);
        string[] lines = Normalize(text).Split('\n');
        string?[] lineClasses = new string?[lines.Length];
        var locals = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentClass = null;
        int depth = 0;
        int classDepth = -1;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            Match classMatch = ClassRegex.Match(line);
            if (classMatch.Success)
            {
                currentClass = classMatch.Groups["name"].Value;
                string baseType = classMatch.Groups["base"].Value;
                classDepth = depth + CountChar(line, '{');
                Add(items, "scriptType", $"{currentClass} extends {baseType}", sourcePath, index + 1);
            }
            lineClasses[index] = currentClass;

            if (currentClass is not null)
            {
                Match methodMatch = MethodRegex.Match(line);
                if (methodMatch.Success)
                {
                    string methodName = methodMatch.Groups["name"].Value;
                    if (methodName is not ("if" or "for" or "while" or "switch" or
                        "catch" or "using" or "lock"))
                    {
                        string qualifiedName = $"{currentClass}.{methodName}";
                        Add(items, "scriptMethod", qualifiedName, sourcePath, index + 1);
                        if (LifecycleNames.Contains(methodName))
                        {
                            Add(items, "lifecycleHook", qualifiedName, sourcePath, index + 1);
                        }
                        if (methodMatch.Groups["async"].Success)
                        {
                            Add(items, "asyncPattern", $"async:{qualifiedName}", sourcePath, index + 1);
                        }
                    }
                }
            }
            else
            {
                Match functionMatch = GlobalFunctionRegex.Match(line);
                if (functionMatch.Success)
                {
                    string functionName = functionMatch.Groups["name"].Value;
                    Add(items, "globalPattern", $"function:{functionName}", sourcePath, index + 1);
                    if (functionMatch.Groups["async"].Success)
                    {
                        Add(items, "asyncPattern", $"async:function:{functionName}", sourcePath, index + 1);
                    }
                }
            }

            foreach (Match match in TypedLocalRegex.Matches(line))
            {
                locals[match.Groups["name"].Value] = match.Groups["type"].Value;
            }
            foreach (Match match in RefLocalRegex.Matches(line))
            {
                locals[match.Groups["name"].Value] = match.Groups["type"].Value;
            }
            Match unrefMatch = UnrefLocalRegex.Match(line);
            if (unrefMatch.Success &&
                locals.TryGetValue(unrefMatch.Groups["source"].Value, out string? unrefType))
            {
                locals[unrefMatch.Groups["name"].Value] = unrefType;
            }

            depth += CountChar(line, '{') - CountChar(line, '}');
            if (currentClass is not null && depth < classDepth)
            {
                currentClass = null;
                classDepth = -1;
            }
        }

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            int lineNumber = index + 1;
            foreach (Match match in ImportRegex.Matches(line))
            {
                Add(items, "import", match.Groups["module"].Value, sourcePath, lineNumber);
            }
            foreach (Match match in DecoratorRegex.Matches(line))
            {
                Add(items, "decorator", match.Groups["name"].Value, sourcePath, lineNumber);
            }
            foreach (Match match in PuertsApiRegex.Matches(line))
            {
                Add(items, "puertsApi", $"${match.Groups["name"].Value}", sourcePath, lineNumber);
            }
            foreach (Match match in ResourceRegex.Matches(line))
            {
                Add(items, "resource", match.Groups["path"].Value, sourcePath, lineNumber);
            }
            Match augmentation = ModuleAugmentationRegex.Match(line);
            if (augmentation.Success)
            {
                Add(items, "globalPattern", $"module-augmentation:{augmentation.Groups["module"].Value}", sourcePath, lineNumber);
            }
            Match prototype = PrototypeMutationRegex.Match(line);
            if (prototype.Success)
            {
                Add(items, "globalPattern", $"prototype-mutation:{prototype.Groups["symbol"].Value}", sourcePath, lineNumber);
            }
            if (line.Contains("setTimeout", StringComparison.Ordinal))
            {
                Add(items, "asyncPattern", "timer:setTimeout", sourcePath, lineNumber);
            }
            if (line.Contains("new Promise", StringComparison.Ordinal))
            {
                Add(items, "asyncPattern", "promise", sourcePath, lineNumber);
            }
            if (line.Contains("await ", StringComparison.Ordinal))
            {
                Add(items, "asyncPattern", "await", sourcePath, lineNumber);
            }

            foreach (Match match in ExplicitUnrealRegex.Matches(line))
            {
                string expression = match.Value;
                int next = match.Index + match.Length;
                while (next < line.Length && char.IsWhiteSpace(line[next]))
                {
                    next++;
                }
                int segments = expression.Count(static character => character == '.');
                bool isCall = next < line.Length && line[next] == '(';
                bool isConstructor = line[..match.Index].TrimEnd().EndsWith(
                    "new", StringComparison.Ordinal);
                if (segments >= 2 || isCall || isConstructor ||
                    expression.Contains(".prototype", StringComparison.Ordinal))
                {
                    Add(items, "unrealApi", expression, sourcePath, lineNumber);
                }
            }

            string? ownerClass = lineClasses[index];
            if (ownerClass is not null)
            {
                foreach (Match match in ThisCallRegex.Matches(line))
                {
                    string[] chain = match.Groups["chain"].Value
                        .Split('.', StringSplitOptions.RemoveEmptyEntries);
                    if (chain.Length == 0)
                    {
                        continue;
                    }
                    if (classMethods.Contains((ownerClass, chain[0])))
                    {
                        continue;
                    }
                    string? ownerType;
                    string member;
                    if (chain.Length >= 2 &&
                        TryResolveFieldType(
                            ownerClass,
                            chain[0],
                            classBases,
                            fields,
                            out string? fieldType))
                    {
                        if (classMethods.Contains((fieldType, chain[^1])))
                        {
                            continue;
                        }
                        ownerType = ResolveUnrealBase(fieldType, classBases);
                        member = chain[^1];
                    }
                    else
                    {
                        ownerType = ResolveUnrealBase(ownerClass, classBases);
                        member = chain[0];
                    }
                    if (ownerType is not null)
                    {
                        Add(items, "unrealApi", $"{ownerType}.{member}", sourcePath, lineNumber);
                    }
                }
                foreach (Match match in ThisPropertyWriteRegex.Matches(line))
                {
                    if (TryResolveFieldType(
                            ownerClass,
                            match.Groups["field"].Value,
                            classBases,
                            fields,
                            out string? fieldType))
                    {
                        string? ownerType = ResolveUnrealBase(fieldType, classBases);
                        if (ownerType is not null)
                        {
                            Add(
                                items,
                                "unrealApi",
                                $"{ownerType}.{match.Groups["member"].Value}:set",
                                sourcePath,
                                lineNumber);
                        }
                    }
                }
            }

            foreach (Match match in ReceiverCallRegex.Matches(line))
            {
                string receiver = match.Groups["receiver"].Value;
                string member = match.Groups["member"].Value;
                if (receiver is "UE" or "this" || IgnoredReceivers.Contains(receiver) ||
                    line[..match.Index].EndsWith("UE.", StringComparison.Ordinal) ||
                    line.Contains($"this.{receiver}.", StringComparison.Ordinal))
                {
                    continue;
                }
                if (locals.TryGetValue(receiver, out string? localType))
                {
                    Add(items, "unrealApi", $"{localType}.{member}", sourcePath, lineNumber);
                }
                else if (member.StartsWith("op_", StringComparison.Ordinal))
                {
                    Add(items, "unrealApi", $"UE.Vector.{member}", sourcePath, lineNumber);
                }
                else
                {
                    Add(items, "unrealApi", $"dynamic:{receiver}.{member}", sourcePath, lineNumber);
                }
                if (member is "Add" or "AddUnique" or "Bind" or "Remove" or
                    "RemoveAll" or "Clear" or "Execute" or "Broadcast")
                {
                    Add(items, "delegate", $"{receiver}.{member}", sourcePath, lineNumber);
                }
            }
            foreach (Match match in ChainedResultCallRegex.Matches(line))
            {
                Add(
                    items,
                    "unrealApi",
                    $"UE.Vector.{match.Groups["member"].Value}",
                    sourcePath,
                    lineNumber);
            }
        }
    }

    private static void AnalyzeNativeFacades(
        string sourcePath,
        string text,
        Dictionary<(string Kind, string Key), List<UnrealPuertsMigrationLocation>> items)
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
                    items,
                    "nativeFacade",
                    $"{currentClass}.{functionMatch.Groups["name"].Value}",
                    sourcePath,
                    annotationLine);
            }
            pendingFunction = false;
            declaration.Clear();
        }
    }

    private static void CollectScriptMetadata(
        string text,
        IDictionary<string, string> classBases,
        IDictionary<(string Class, string Field), string> fields,
        ISet<(string Class, string Method)> classMethods)
    {
        string[] lines = Normalize(text).Split('\n');
        string? currentClass = null;
        int depth = 0;
        int classDepth = -1;
        foreach (string line in lines)
        {
            Match classMatch = ClassRegex.Match(line);
            if (classMatch.Success)
            {
                currentClass = classMatch.Groups["name"].Value;
                classBases[currentClass] = classMatch.Groups["base"].Value;
                classDepth = depth + CountChar(line, '{');
            }
            if (currentClass is not null)
            {
                Match fieldMatch = FieldRegex.Match(line);
                if (fieldMatch.Success)
                {
                    fields[(currentClass, fieldMatch.Groups["name"].Value)] =
                        fieldMatch.Groups["type"].Value;
                }
                Match methodMatch = MethodRegex.Match(line);
                if (methodMatch.Success)
                {
                    string methodName = methodMatch.Groups["name"].Value;
                    if (methodName is not ("if" or "for" or "while" or "switch" or
                        "catch" or "using" or "lock"))
                    {
                        classMethods.Add((currentClass, methodName));
                    }
                }
            }
            depth += CountChar(line, '{') - CountChar(line, '}');
            if (currentClass is not null && depth < classDepth)
            {
                currentClass = null;
                classDepth = -1;
            }
        }
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

    private static void Add(
        Dictionary<(string Kind, string Key), List<UnrealPuertsMigrationLocation>> items,
        string kind,
        string key,
        string sourcePath,
        int line)
    {
        var itemKey = (kind, key);
        if (!items.TryGetValue(itemKey, out List<UnrealPuertsMigrationLocation>? locations))
        {
            locations = [];
            items.Add(itemKey, locations);
        }
        locations.Add(new UnrealPuertsMigrationLocation(sourcePath, line));
    }

    private static int Count(IReadOnlyList<UnrealPuertsMigrationItem> items, string kind) =>
        items.Count(item => item.Kind == kind);

    private static int CountChar(string value, char character) =>
        value.Count(current => current == character);

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static string ComputeFingerprint(IReadOnlyList<(string Path, string Text)> files)
    {
        var input = new StringBuilder();
        foreach ((string path, string text) in files.OrderBy(
                     static file => file.Path,
                     StringComparer.Ordinal))
        {
            input.Append(path).Append('\n').Append(Normalize(text)).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString())))
            .ToLowerInvariant();
    }
}

public static class UnrealPuertsMigrationCoverageValidator
{
    private static readonly HashSet<string> AllowedRoutes = new(StringComparer.Ordinal)
    {
        "managed-script",
        "generated-binding",
        "data-plane",
        "project-native",
        "verification-only",
        "script-asset",
        "core-runtime",
        "eliminated",
    };

    public static UnrealPuertsMigrationCoverageReport Validate(
        UnrealPuertsMigrationInventory inventory,
        UnrealPuertsMigrationCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(coverage);
        if (!string.Equals(inventory.ProjectName, coverage.ProjectName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Coverage project '{coverage.ProjectName}' does not match inventory project " +
                $"'{inventory.ProjectName}'.");
        }

        var diagnostics = new List<string>();
        bool fingerprintMatches = string.Equals(
            inventory.SourceFingerprint,
            coverage.InventoryFingerprint,
            StringComparison.Ordinal);
        if (!fingerprintMatches)
        {
            diagnostics.Add(
                "Coverage inventoryFingerprint does not match the analyzed Puerts/native sources.");
        }

        var inventoryKeys = inventory.Items.ToDictionary(
            static item => (item.Kind, item.Key),
            static item => item);
        var coverageKeys = new Dictionary<
            (string Kind, string Key),
            UnrealPuertsMigrationCoverageEntry>();
        foreach (UnrealPuertsMigrationCoverageEntry entry in coverage.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Kind) ||
                string.IsNullOrWhiteSpace(entry.Key) ||
                string.IsNullOrWhiteSpace(entry.Route) ||
                string.IsNullOrWhiteSpace(entry.Evidence))
            {
                throw new ArgumentException(
                    "Every Puerts migration coverage entry requires kind, key, route, and evidence.");
            }
            if (!coverageKeys.TryAdd((entry.Kind, entry.Key), entry))
            {
                throw new ArgumentException(
                    $"Duplicate Puerts migration coverage entry '{entry.Kind}:{entry.Key}'.");
            }
            if (!AllowedRoutes.Contains(entry.Route))
            {
                diagnostics.Add(
                    $"Coverage entry '{entry.Kind}:{entry.Key}' uses unsupported route " +
                    $"'{entry.Route}'.");
            }
            if (entry.Kind == "nativeFacade" && string.IsNullOrWhiteSpace(entry.Notes))
            {
                diagnostics.Add(
                    $"Native facade '{entry.Key}' requires an explicit boundary explanation.");
            }
        }

        UnrealPuertsMigrationItem[] uncovered = inventory.Items
            .Where(item => !coverageKeys.ContainsKey((item.Kind, item.Key)))
            .ToArray();
        UnrealPuertsMigrationCoverageEntry[] stale = coverage.Entries
            .Where(entry => !inventoryKeys.ContainsKey((entry.Kind, entry.Key)))
            .OrderBy(static entry => entry.Kind, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        if (uncovered.Length > 0)
        {
            diagnostics.Add($"{uncovered.Length} Puerts inventory item(s) have no migration route.");
        }
        if (stale.Length > 0)
        {
            diagnostics.Add($"{stale.Length} migration route(s) no longer match the source inventory.");
        }
        int covered = inventory.Items.Count - uncovered.Length;
        bool passed = fingerprintMatches && uncovered.Length == 0 && stale.Length == 0 &&
            diagnostics.Count == 0;
        return new UnrealPuertsMigrationCoverageReport(
            "unifiedscript.puerts-migration-report/v0.1",
            inventory.ProjectName,
            inventory.SourceFingerprint,
            fingerprintMatches,
            passed,
            inventory.Items.Count,
            covered,
            uncovered.Length,
            stale.Length,
            uncovered,
            stale,
            diagnostics);
    }
}
