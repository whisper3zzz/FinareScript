using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedScript.Unreal;

public sealed record UnrealPuertsMigrationWorkItem(
    string Id,
    string Phase,
    string Category,
    string SourceKey,
    string Risk,
    string Status,
    string RecommendedRoute,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<UnrealPuertsMigrationLocation> Locations);

public sealed record UnrealPuertsMigrationPhase(
    string Id,
    string Name,
    string CompletionGate,
    IReadOnlyList<string> WorkItemIds);

public sealed record UnrealPuertsMigrationPlanSummary(
    int PhaseCount,
    int WorkItemCount,
    int PlannedCount,
    int ReviewCount,
    int BlockedCount);

public sealed record UnrealPuertsMigrationPlan(
    string SchemaVersion,
    string PlanId,
    string ProjectName,
    string ScanFingerprint,
    string? ContractFingerprint,
    bool SourceScanComplete,
    bool Blocked,
    IReadOnlyList<UnrealPuertsMigrationPhase> Phases,
    IReadOnlyList<UnrealPuertsMigrationWorkItem> WorkItems,
    UnrealPuertsMigrationPlanSummary Summary);

public sealed record UnrealPuertsApiCoverageEntry(
    string SourceApi,
    string TypeName,
    string? MemberName,
    string AccessKind,
    string Status,
    string? ContractTypePath,
    string? ContractMember,
    string SuggestedRoute,
    IReadOnlyList<UnrealPuertsMigrationLocation> Locations);

public sealed record UnrealPuertsApiCoverageSummary(
    int ApiCount,
    int CoveredCount,
    int TypeCoveredCount,
    int MissingCount,
    int AmbiguousCount,
    int ManualCount,
    int UnassessedCount);

public sealed record UnrealPuertsApiCoverageReport(
    string SchemaVersion,
    string ProjectName,
    string ScanFingerprint,
    string? ContractFingerprint,
    bool Assessed,
    IReadOnlyList<UnrealPuertsApiCoverageEntry> Entries,
    UnrealPuertsApiCoverageSummary Summary);

public sealed record UnrealPuertsContractRequest(
    string Id,
    string TypeName,
    string? SuggestedTypePath,
    string? MemberName,
    string RequestKind,
    string SuggestedRoute,
    IReadOnlyList<string> SourceApis,
    IReadOnlyList<UnrealPuertsMigrationLocation> Locations);

public sealed record UnrealPuertsContractRequestSet(
    string SchemaVersion,
    string ProjectName,
    string ScanFingerprint,
    string? ContractFingerprint,
    IReadOnlyList<UnrealPuertsContractRequest> Requests);

public sealed record UnrealPuertsVerificationCase(
    string Id,
    string Category,
    string SourceKey,
    string Description,
    string Status,
    IReadOnlyList<string> RequiredObservables,
    IReadOnlyList<string> WorkItemIds);

public sealed record UnrealPuertsVerificationHarness(
    string SchemaVersion,
    string ProjectName,
    string ScanFingerprint,
    string PlanId,
    bool PlanBlocked,
    string OwnershipPolicy,
    IReadOnlyList<string> RuntimeLanes,
    IReadOnlyList<UnrealPuertsVerificationCase> Cases);

public sealed record UnrealPuertsVerificationCaseEvidence(
    string CaseId,
    string Status,
    IReadOnlyDictionary<string, string> Observables);

public sealed record UnrealPuertsVerificationEvidence(
    string SchemaVersion,
    string ProjectName,
    string ScanFingerprint,
    string PlanId,
    string Runtime,
    IReadOnlyList<UnrealPuertsVerificationCaseEvidence> Cases);

public sealed record UnrealPuertsMigrationPackagePayload(
    string RelativePath,
    string MediaType,
    long Size,
    string Sha256);

public sealed record UnrealPuertsMigrationPackageManifest(
    string SchemaVersion,
    string ProjectName,
    string ScanFingerprint,
    string PlanId,
    IReadOnlyList<UnrealPuertsMigrationPackagePayload> Payloads);

public sealed record UnrealPuertsMigrationGeneratedFile(
    string RelativePath,
    string MediaType,
    string Content,
    string Sha256);

public sealed record UnrealPuertsMigrationPackage(
    UnrealPuertsMigrationPlan Plan,
    UnrealPuertsApiCoverageReport ApiCoverage,
    UnrealPuertsContractRequestSet ContractRequests,
    UnrealPuertsVerificationHarness VerificationHarness,
    UnrealPuertsVerificationEvidence PuertsEvidenceTemplate,
    UnrealPuertsVerificationEvidence UnifiedScriptEvidenceTemplate,
    UnrealPuertsMigrationPackageManifest Manifest,
    IReadOnlyList<UnrealPuertsMigrationGeneratedFile> Files);

public static class UnrealPuertsMigrationPackageJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(UnrealPuertsMigrationPlan value) => SerializeCore(value);

    public static string Serialize(UnrealPuertsApiCoverageReport value) => SerializeCore(value);

    public static string Serialize(UnrealPuertsContractRequestSet value) => SerializeCore(value);

    public static string Serialize(UnrealPuertsVerificationHarness value) => SerializeCore(value);

    public static string Serialize(UnrealPuertsVerificationEvidence value) => SerializeCore(value);

    public static string Serialize(UnrealPuertsMigrationPackageManifest value) => SerializeCore(value);

    public static UnrealPuertsMigrationPlan DeserializePlan(string json) =>
        Deserialize<UnrealPuertsMigrationPlan>(
            json,
            "unifiedscript.puerts-migration-plan/v0.1",
            static value => value.SchemaVersion,
            "Puerts migration plan");

    public static UnrealPuertsVerificationHarness DeserializeHarness(string json) =>
        Deserialize<UnrealPuertsVerificationHarness>(
            json,
            "unifiedscript.puerts-verification-harness/v0.1",
            static value => value.SchemaVersion,
            "Puerts verification harness");

    public static UnrealPuertsVerificationEvidence DeserializeEvidence(string json) =>
        Deserialize<UnrealPuertsVerificationEvidence>(
            json,
            "unifiedscript.puerts-verification-evidence/v0.1",
            static value => value.SchemaVersion,
            "Puerts verification evidence");

    private static string SerializeCore<T>(T value) =>
        JsonSerializer.Serialize(value, Options).ReplaceLineEndings("\n") + "\n";

    private static T Deserialize<T>(
        string json,
        string expectedSchema,
        Func<T, string> getSchema,
        string description)
    {
        ArgumentNullException.ThrowIfNull(json);
        T value = JsonSerializer.Deserialize<T>(json, Options) ??
            throw new JsonException($"{description} JSON is empty.");
        if (getSchema(value) != expectedSchema)
        {
            throw new JsonException(
                $"Unsupported {description} schema '{getSchema(value)}'.");
        }
        return value;
    }
}

public sealed class UnrealPuertsMigrationPlanner
{
    private static readonly HashSet<string> CSharpKeywords = new(
        [
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
            "char", "checked", "class", "const", "continue", "decimal", "default",
            "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
            "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
            "implicit", "in", "int", "interface", "internal", "is", "lock", "long",
            "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte",
            "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct",
            "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
            "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile",
            "while",
        ],
        StringComparer.Ordinal);
    private static readonly IReadOnlyList<PhaseDefinition> PhaseDefinitions =
    [
        new(
            "foundation",
            "Project foundation",
            "Every module and project setting has an owned UnifiedScript or coexistence route."),
        new(
            "api-contract",
            "API Contract and assets",
            "Every Unreal API, resource, and native surface has generated or explicit evidence."),
        new(
            "managed-port",
            "Managed source port",
            "Every script type and Puerts-specific API has a typed managed replacement."),
        new(
            "lifecycle",
            "World lifecycle and asynchronous ownership",
            "Lifecycle, delegates, and async work are generation-owned and teardown-safe."),
        new(
            "manual-rewrite",
            "Unsupported dynamic rewrites",
            "No blocker or unreviewed dynamic behavior remains."),
    ];

    public UnrealPuertsMigrationPackage Generate(
        UnrealPuertsProjectScan scan,
        UnrealApiContract? contract = null,
        string? managedNamespace = null)
    {
        ValidateScan(scan);
        UnrealApiContract? normalizedContract = contract is null
            ? null
            : UnrealApiContractJson.Deserialize(UnrealApiContractJson.Serialize(contract));
        string? contractFingerprint = normalizedContract is null
            ? null
            : Sha256(UnrealApiContractJson.Serialize(normalizedContract, writeIndented: false));
        string migrationNamespace = NormalizeNamespace(
            managedNamespace ?? SanitizeIdentifier(scan.ProjectName) + ".UnifiedScript.Migration");
        UnrealPuertsMigrationWorkItem[] workItems = BuildWorkItems(scan);
        string planId = "plan-" + Sha256(
            UnrealPuertsProjectScanJson.Serialize(scan) +
            contractFingerprint + "\n" +
            migrationNamespace)[7..23];
        UnrealPuertsMigrationPhase[] phases = PhaseDefinitions
            .Select(definition => new UnrealPuertsMigrationPhase(
                definition.Id,
                definition.Name,
                definition.CompletionGate,
                workItems
                    .Where(item => item.Phase == definition.Id)
                    .Select(static item => item.Id)
                    .ToArray()))
            .ToArray();
        var plan = new UnrealPuertsMigrationPlan(
            "unifiedscript.puerts-migration-plan/v0.1",
            planId,
            scan.ProjectName,
            scan.SourceFingerprint,
            contractFingerprint,
            scan.Complete,
            !scan.Complete || workItems.Any(static item => item.Status == "blocked"),
            phases,
            workItems,
            new UnrealPuertsMigrationPlanSummary(
                phases.Length,
                workItems.Length,
                workItems.Count(static item => item.Status == "planned"),
                workItems.Count(static item => item.Status == "review"),
                workItems.Count(static item => item.Status == "blocked")));
        UnrealPuertsApiCoverageReport coverage = BuildCoverage(
            scan,
            normalizedContract,
            contractFingerprint);
        UnrealPuertsContractRequestSet requests = BuildContractRequests(coverage);
        string stubs = BuildMigrationStubs(plan, migrationNamespace);
        UnrealPuertsVerificationHarness harness = BuildHarness(plan);
        UnrealPuertsVerificationEvidence puertsTemplate = BuildEvidenceTemplate(
            harness,
            "puerts");
        UnrealPuertsVerificationEvidence unifiedTemplate = BuildEvidenceTemplate(
            harness,
            "unifiedScript");
        var files = new List<UnrealPuertsMigrationGeneratedFile>
        {
            CreateFile("migration-plan.json", "application/json", UnrealPuertsMigrationPackageJson.Serialize(plan)),
            CreateFile("api-coverage.json", "application/json", UnrealPuertsMigrationPackageJson.Serialize(coverage)),
            CreateFile("contract-requests.json", "application/json", UnrealPuertsMigrationPackageJson.Serialize(requests)),
            CreateFile("MigrationStubs.cs", "text/x-csharp", stubs),
            CreateFile("verification-harness.json", "application/json", UnrealPuertsMigrationPackageJson.Serialize(harness)),
            CreateFile(
                "evidence/puerts.template.json",
                "application/json",
                UnrealPuertsMigrationPackageJson.Serialize(puertsTemplate)),
            CreateFile(
                "evidence/unifiedscript.template.json",
                "application/json",
                UnrealPuertsMigrationPackageJson.Serialize(unifiedTemplate)),
        };
        var manifest = new UnrealPuertsMigrationPackageManifest(
            "unifiedscript.puerts-migration-package/v0.1",
            scan.ProjectName,
            scan.SourceFingerprint,
            planId,
            files.Select(static file => new UnrealPuertsMigrationPackagePayload(
                    file.RelativePath,
                    file.MediaType,
                    Encoding.UTF8.GetByteCount(file.Content),
                    file.Sha256))
                .ToArray());
        files.Add(CreateFile(
            "migration-package.json",
            "application/json",
            UnrealPuertsMigrationPackageJson.Serialize(manifest)));
        return new UnrealPuertsMigrationPackage(
            plan,
            coverage,
            requests,
            harness,
            puertsTemplate,
            unifiedTemplate,
            manifest,
            files);
    }

    private static UnrealPuertsMigrationWorkItem[] BuildWorkItems(
        UnrealPuertsProjectScan scan)
    {
        UnrealPuertsProjectScanFinding[] ordered = scan.Findings
            .OrderBy(static finding => PhaseOrder(PhaseFor(finding.Category)))
            .ThenBy(static finding => finding.Category, StringComparer.Ordinal)
            .ThenBy(static finding => finding.Key, StringComparer.Ordinal)
            .ToArray();
        var idsByType = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].Category == "scriptType")
            {
                idsByType[TypeNameFromScriptType(ordered[index].Key)] = WorkItemId(index);
            }
        }
        return ordered.Select((finding, index) =>
        {
            string[] dependencies = finding.Category == "lifecycleHook" &&
                idsByType.TryGetValue(TypeNameFromQualifiedMember(finding.Key), out string? typeId)
                    ? [typeId]
                    : [];
            return new UnrealPuertsMigrationWorkItem(
                WorkItemId(index),
                PhaseFor(finding.Category),
                finding.Category,
                finding.Key,
                finding.Risk,
                finding.Risk switch
                {
                    "blocker" => "blocked",
                    "review" => "review",
                    _ => "planned",
                },
                finding.MigrationAction,
                dependencies,
                finding.Locations);
        }).ToArray();
    }

    private static UnrealPuertsApiCoverageReport BuildCoverage(
        UnrealPuertsProjectScan scan,
        UnrealApiContract? contract,
        string? contractFingerprint)
    {
        var contractTypes = contract?.Types ?? [];
        var entries = new List<UnrealPuertsApiCoverageEntry>();
        foreach (UnrealPuertsProjectScanFinding finding in scan.Findings
                     .Where(static item => item.Category == "unrealApi")
                     .OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            ApiReference reference = ParseApiReference(finding.Key);
            entries.Add(MatchContract(reference, finding.Locations, contractTypes, contract is not null));
        }
        UnrealPuertsApiCoverageEntry[] ordered = entries.ToArray();
        return new UnrealPuertsApiCoverageReport(
            "unifiedscript.puerts-api-coverage/v0.1",
            scan.ProjectName,
            scan.SourceFingerprint,
            contractFingerprint,
            contract is not null,
            ordered,
            new UnrealPuertsApiCoverageSummary(
                ordered.Length,
                ordered.Count(static entry => entry.Status.StartsWith(
                    "covered",
                    StringComparison.Ordinal)),
                CountStatus(ordered, "type-covered"),
                CountStatus(ordered, "missing"),
                CountStatus(ordered, "ambiguous"),
                CountStatus(ordered, "manual"),
                CountStatus(ordered, "unassessed")));
    }

    private static UnrealPuertsApiCoverageEntry MatchContract(
        ApiReference reference,
        IReadOnlyList<UnrealPuertsMigrationLocation> locations,
        IReadOnlyList<UnrealApiContractType> contractTypes,
        bool assessed)
    {
        if (reference.Manual)
        {
            return CoverageEntry(reference, "manual", null, null, "manual-rewrite", locations);
        }
        if (!assessed)
        {
            return CoverageEntry(
                reference,
                "unassessed",
                null,
                null,
                SuggestedRoute(reference),
                locations);
        }
        UnrealApiContractType[] exactTypes = contractTypes
            .Where(type => TypeLeaf(type.Path) == reference.TypeName)
            .ToArray();
        if (reference.MemberName is null)
        {
            return exactTypes.Length switch
            {
                1 => CoverageEntry(
                    reference,
                    "type-covered",
                    exactTypes[0].Path,
                    null,
                    "existing-contract-type",
                    locations),
                > 1 => CoverageEntry(
                    reference,
                    "ambiguous",
                    null,
                    null,
                    "select-contract-owner",
                    locations),
                _ => CoverageEntry(
                    reference,
                    "missing",
                    null,
                    null,
                    SuggestedRoute(reference),
                    locations),
            };
        }
        UnrealApiContractType[] exactMembers = exactTypes
            .Where(type => type.Members.Contains(reference.MemberName, StringComparer.Ordinal))
            .ToArray();
        if (exactMembers.Length == 1)
        {
            return CoverageEntry(
                reference,
                "covered",
                exactMembers[0].Path,
                reference.MemberName,
                "existing-contract",
                locations);
        }
        if (exactMembers.Length > 1)
        {
            return CoverageEntry(
                reference,
                "ambiguous",
                null,
                null,
                "select-contract-owner",
                locations);
        }
        if (reference.AccessKind == "property-write")
        {
            string setterName = "Set" + reference.MemberName;
            UnrealApiContractType[] setterMatches = exactTypes
                .Where(type => type.Members.Contains(setterName, StringComparer.Ordinal))
                .ToArray();
            if (setterMatches.Length == 1)
            {
                return CoverageEntry(
                    reference,
                    "covered-adapter",
                    setterMatches[0].Path,
                    setterName,
                    "property-setter-adapter",
                    locations);
            }
            if (setterMatches.Length > 1)
            {
                return CoverageEntry(
                    reference,
                    "ambiguous",
                    null,
                    null,
                    "select-contract-owner",
                    locations);
            }
        }
        string suggestedRoute = SuggestedRoute(reference);
        (string route, string? routePath, string? routeMember) = RuntimeRouteTarget(
            reference.TypeName,
            reference.MemberName,
            suggestedRoute);
        if (route is "world-service" or "resource-loader" &&
            routePath is not null && routeMember is not null)
        {
            UnrealApiContractType[] routedMatches = contractTypes
                .Where(type => type.Path == routePath &&
                    type.Members.Contains(routeMember, StringComparer.Ordinal))
                .ToArray();
            if (routedMatches.Length == 1)
            {
                return CoverageEntry(
                    reference,
                    "covered-route",
                    routedMatches[0].Path,
                    routeMember,
                    route,
                    locations);
            }
            if (routedMatches.Length > 1)
            {
                return CoverageEntry(
                    reference,
                    "ambiguous",
                    null,
                    null,
                    "select-contract-owner",
                    locations);
            }
        }
        UnrealApiContractType[] ownerMatches = contractTypes
            .Where(type => type.Members.Contains(reference.MemberName, StringComparer.Ordinal))
            .ToArray();
        return ownerMatches.Length switch
        {
            1 => CoverageEntry(
                reference,
                "covered-owner",
                ownerMatches[0].Path,
                reference.MemberName,
                "existing-contract-owner",
                locations),
            > 1 => CoverageEntry(
                reference,
                "ambiguous",
                null,
                null,
                "select-contract-owner",
                locations),
            _ => CoverageEntry(
                reference,
                "missing",
                null,
                null,
                SuggestedRoute(reference),
                locations),
        };
    }

    private static UnrealPuertsApiCoverageEntry CoverageEntry(
        ApiReference reference,
        string status,
        string? contractTypePath,
        string? contractMember,
        string suggestedRoute,
        IReadOnlyList<UnrealPuertsMigrationLocation> locations) =>
        new(
            reference.SourceApi,
            reference.TypeName,
            reference.MemberName,
            reference.AccessKind,
            status,
            contractTypePath,
            contractMember,
            suggestedRoute,
            locations);

    private static UnrealPuertsContractRequestSet BuildContractRequests(
        UnrealPuertsApiCoverageReport coverage)
    {
        var groups = coverage.Entries
            .Where(static entry => entry.Status is "missing" or "unassessed" or "ambiguous")
            .GroupBy(static entry => ContractRequestIdentity(entry))
            .OrderBy(static group => group.Key.RequestKind, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.SuggestedTypePath, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.MemberName, StringComparer.Ordinal)
            .ToArray();
        UnrealPuertsContractRequest[] requests = groups.Select((group, index) =>
        {
            UnrealPuertsApiCoverageEntry first = group.First();
            return new UnrealPuertsContractRequest(
                $"REQ-{index + 1:0000}",
                first.TypeName,
                group.Key.SuggestedTypePath,
                group.Key.MemberName,
                group.Key.RequestKind,
                first.SuggestedRoute,
                group.Select(static entry => entry.SourceApi)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                group.SelectMany(static entry => entry.Locations)
                    .Distinct()
                    .OrderBy(static location => location.SourcePath, StringComparer.Ordinal)
                    .ThenBy(static location => location.Line)
                    .ToArray());
        }).ToArray();
        return new UnrealPuertsContractRequestSet(
            "unifiedscript.puerts-contract-requests/v0.1",
            coverage.ProjectName,
            coverage.ScanFingerprint,
            coverage.ContractFingerprint,
            requests);
    }

    private static RequestIdentity ContractRequestIdentity(UnrealPuertsApiCoverageEntry entry)
    {
        (string route, string? path, string? member) = RuntimeRouteTarget(entry);
        string kind = route switch
        {
            "data-plane" or "world-service" or "resource-loader" => "runtime-service-route",
            _ when member is null => "type-construction",
            _ => "contract-member",
        };
        return new RequestIdentity(kind, path, member);
    }

    private static (string Route, string? Path, string? Member) RuntimeRouteTarget(
        UnrealPuertsApiCoverageEntry entry) =>
        RuntimeRouteTarget(entry.TypeName, entry.MemberName, entry.SuggestedRoute);

    private static (string Route, string? Path, string? Member) RuntimeRouteTarget(
        string typeName,
        string? memberName,
        string route)
    {
        if (route == "data-plane")
        {
            return (route, null, memberName);
        }
        if (route == "resource-loader")
        {
            string member = typeName == "Class" ? "LoadClassByPath" : "LoadObjectByPath";
            return (
                route,
                "/Script/UEUnifiedScriptRuntime.UnifiedScriptWorldLibrary",
                member);
        }
        if (route == "world-service")
        {
            string? member = memberName switch
            {
                "BeginDeferredActorSpawnFromClass" or "FinishSpawningActor" or "SpawnActor" =>
                    "SpawnActor",
                _ => memberName,
            };
            return (
                route,
                "/Script/UEUnifiedScriptRuntime.UnifiedScriptWorldLibrary",
                member);
        }
        return (
            route,
            $"/Script/Engine.{typeName}",
            memberName);
    }

    private static string BuildMigrationStubs(
        UnrealPuertsMigrationPlan plan,
        string managedNamespace)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("#nullable enable");
        source.Append("namespace ").Append(managedNamespace).AppendLine(";");
        source.AppendLine();
        source.AppendLine("[global::System.AttributeUsage(");
        source.AppendLine("    global::System.AttributeTargets.Class | global::System.AttributeTargets.Method,");
        source.AppendLine("    AllowMultiple = true)]");
        source.AppendLine("internal sealed class PuertsMigrationTodoAttribute(");
        source.AppendLine("    string workItemId,");
        source.AppendLine("    string sourceKey) : global::System.Attribute");
        source.AppendLine("{");
        source.AppendLine("    public string WorkItemId { get; } = workItemId;");
        source.AppendLine();
        source.AppendLine("    public string SourceKey { get; } = sourceKey;");
        source.AppendLine("}");
        source.AppendLine();

        UnrealPuertsMigrationWorkItem[] typeItems = plan.WorkItems
            .Where(static item => item.Category == "scriptType")
            .ToArray();
        var usedClassNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnrealPuertsMigrationWorkItem typeItem in typeItems)
        {
            string sourceType = TypeNameFromScriptType(typeItem.SourceKey);
            string className = SanitizeIdentifier(sourceType) + "MigrationStub";
            if (!usedClassNames.Add(className))
            {
                className += "_" + Sha256(sourceType)[7..15];
                usedClassNames.Add(className);
            }
            source.Append("[PuertsMigrationTodo(")
                .Append(CSharpString(typeItem.Id)).Append(", ")
                .Append(CSharpString(typeItem.SourceKey)).AppendLine(")]");
            source.Append("internal abstract partial class ").Append(className).AppendLine();
            source.AppendLine("{");
            UnrealPuertsMigrationWorkItem[] lifecycleItems = plan.WorkItems
                .Where(item => item.Category == "lifecycleHook" &&
                    TypeNameFromQualifiedMember(item.SourceKey) == sourceType)
                .ToArray();
            if (lifecycleItems.Length == 0)
            {
                source.AppendLine("    // Add migrated behavior only after API and ownership review.");
            }
            foreach (UnrealPuertsMigrationWorkItem lifecycle in lifecycleItems)
            {
                string method = SanitizeIdentifier(MemberNameFromQualifiedMember(lifecycle.SourceKey));
                source.Append("    [PuertsMigrationTodo(")
                    .Append(CSharpString(lifecycle.Id)).Append(", ")
                    .Append(CSharpString(lifecycle.SourceKey)).AppendLine(")]");
                source.Append("    protected virtual void Port_").Append(method).AppendLine("() =>");
                source.Append("        throw new global::System.NotImplementedException(")
                    .Append(CSharpString(
                        $"{lifecycle.Id} requires signature, World ownership, and behavior migration."))
                    .AppendLine(");");
            }
            source.AppendLine("}");
            source.AppendLine();
        }

        UnrealPuertsMigrationWorkItem[] manualItems = plan.WorkItems
            .Where(static item => item.Status is "review" or "blocked")
            .ToArray();
        source.AppendLine("internal static class PuertsMigrationManualReviews");
        source.AppendLine("{");
        if (manualItems.Length == 0)
        {
            source.AppendLine("    public const string None = \"No manual review item was discovered.\";");
        }
        foreach (UnrealPuertsMigrationWorkItem item in manualItems)
        {
            source.Append("    public const string ").Append(SanitizeIdentifier(item.Id))
                .Append(" = ").Append(CSharpString(item.SourceKey)).AppendLine(";");
        }
        source.AppendLine("}");
        return source.ToString().ReplaceLineEndings("\n");
    }

    private static UnrealPuertsVerificationHarness BuildHarness(UnrealPuertsMigrationPlan plan)
    {
        UnrealPuertsMigrationWorkItem[] verifiable = plan.WorkItems
            .Where(static item => item.Category != "configuration")
            .ToArray();
        UnrealPuertsVerificationCase[] cases = verifiable.Select((item, index) =>
            new UnrealPuertsVerificationCase(
                $"CASE-{index + 1:0000}",
                item.Category,
                item.SourceKey,
                DescribeVerification(item),
                item.Status == "blocked" ? "blocked" : "required",
                RequiredObservables(item.Category),
                [item.Id])).ToArray();
        return new UnrealPuertsVerificationHarness(
            "unifiedscript.puerts-verification-harness/v0.1",
            plan.ProjectName,
            plan.ScanFingerprint,
            plan.PlanId,
            plan.Blocked,
            "exactly-one-runtime-owns-each-world-lifecycle",
            ["puerts", "unifiedScript"],
            cases);
    }

    private static UnrealPuertsVerificationEvidence BuildEvidenceTemplate(
        UnrealPuertsVerificationHarness harness,
        string runtime) =>
        new(
            "unifiedscript.puerts-verification-evidence/v0.1",
            harness.ProjectName,
            harness.ScanFingerprint,
            harness.PlanId,
            runtime,
            harness.Cases.Select(static item => new UnrealPuertsVerificationCaseEvidence(
                item.Id,
                "pending",
                item.RequiredObservables.ToDictionary(
                    static observable => observable,
                    static _ => "TODO",
                    StringComparer.Ordinal))).ToArray());

    private static string DescribeVerification(UnrealPuertsMigrationWorkItem item) =>
        item.Category switch
        {
            "lifecycleHook" => $"Compare lifecycle order and World generation for {item.SourceKey}.",
            "unrealApi" => $"Compare result, side effects, and bridge evidence for {item.SourceKey}.",
            "assetReference" => $"Resolve and load the same asset {item.SourceKey}.",
            "delegatePattern" => $"Compare bind, callback, reload, and unbind behavior for {item.SourceKey}.",
            "asyncPattern" => $"Compare completion and retired-generation behavior for {item.SourceKey}.",
            "unsupportedDynamicBehavior" => $"Verify the reviewed static replacement for {item.SourceKey}.",
            _ => $"Compare Puerts and UnifiedScript behavior for {item.SourceKey}.",
        };

    private static string[] RequiredObservables(string category) =>
        category switch
        {
            "lifecycleHook" => ["invocationCount", "order", "worldGeneration"],
            "unrealApi" => ["callCount", "resultDigest", "sideEffectDigest"],
            "assetReference" => ["resolvedPath", "loadSucceeded"],
            "delegatePattern" => ["bindCount", "callbackCount", "unbindCountAfterReload"],
            "asyncPattern" => ["completionCount", "retiredGenerationCallbackCount"],
            "nativeSurface" => ["callCount", "resultDigest", "ownershipBoundary"],
            "scriptType" => ["createdCount", "startedCount", "stoppedCount"],
            "module" => ["resolvedModule", "loadSucceeded"],
            "puertsApi" => ["replacementRoute", "resultDigest"],
            "unsupportedDynamicBehavior" => ["replacementRoute", "behaviorDigest"],
            _ => ["resultDigest"],
        };

    private static UnrealPuertsMigrationGeneratedFile CreateFile(
        string relativePath,
        string mediaType,
        string content) =>
        new(relativePath, mediaType, content, Sha256(content));

    private static ApiReference ParseApiReference(string sourceApi)
    {
        if (!sourceApi.StartsWith("UE.", StringComparison.Ordinal))
        {
            return new ApiReference(sourceApi, "Unknown", null, "dynamic", Manual: true);
        }
        string normalized = sourceApi[3..];
        bool propertyWrite = normalized.EndsWith(":set", StringComparison.Ordinal);
        if (propertyWrite)
        {
            normalized = normalized[..^4];
        }
        string[] segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return new ApiReference(sourceApi, "Unknown", null, "dynamic", Manual: true);
        }
        bool manual = segments.Contains("prototype", StringComparer.Ordinal) ||
            segments.Any(static segment => segment.StartsWith("dynamic:", StringComparison.Ordinal));
        return new ApiReference(
            sourceApi,
            segments[0],
            segments.Length > 1 ? segments[^1] : null,
            propertyWrite ? "property-write" : segments.Length == 1 ? "type-construction" : "member",
            manual);
    }

    private static string SuggestedRoute(ApiReference reference)
    {
        if (reference.MemberName is "ApplyDamage" or "SpawnEmitterAtLocation")
        {
            return "data-plane";
        }
        if (reference.MemberName is "LineTraceSingle" or "SpawnActor" or
            "BeginDeferredActorSpawnFromClass" or "FinishSpawningActor" or "DestroyActor")
        {
            return "world-service";
        }
        if (reference.MemberName == "Load")
        {
            return "resource-loader";
        }
        if (reference.MemberName is null)
        {
            return "managed-struct-or-constructor";
        }
        return "generate-contract";
    }

    private static string PhaseFor(string category) =>
        category switch
        {
            "configuration" or "module" or "scanCoverage" or "scanLimit" => "foundation",
            "unrealApi" or "assetReference" or "nativeSurface" => "api-contract",
            "scriptType" or "puertsApi" => "managed-port",
            "lifecycleHook" or "delegatePattern" or "asyncPattern" => "lifecycle",
            _ => "manual-rewrite",
        };

    private static int PhaseOrder(string phase) =>
        PhaseDefinitions.Select(static definition => definition.Id).ToList().IndexOf(phase);

    private static string WorkItemId(int zeroBasedIndex) => $"MIG-{zeroBasedIndex + 1:0000}";

    private static string TypeNameFromScriptType(string key)
    {
        int separator = key.IndexOf(" extends ", StringComparison.Ordinal);
        return separator < 0 ? key : key[..separator];
    }

    private static string TypeNameFromQualifiedMember(string key)
    {
        int separator = key.LastIndexOf('.');
        return separator < 0 ? key : key[..separator];
    }

    private static string MemberNameFromQualifiedMember(string key)
    {
        int separator = key.LastIndexOf('.');
        return separator < 0 ? key : key[(separator + 1)..];
    }

    private static string TypeLeaf(string path)
    {
        int separator = path.LastIndexOf('.');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static int CountStatus(IReadOnlyList<UnrealPuertsApiCoverageEntry> entries, string status) =>
        entries.Count(entry => entry.Status == status);

    private static void ValidateScan(UnrealPuertsProjectScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (scan.SchemaVersion != "unifiedscript.puerts-project-scan/v0.1" ||
            string.IsNullOrWhiteSpace(scan.ProjectName) ||
            string.IsNullOrWhiteSpace(scan.SourceFingerprint) ||
            scan.Findings is null ||
            scan.Summary is null)
        {
            throw new ArgumentException("Puerts project scan is incomplete or unsupported.", nameof(scan));
        }
    }

    private static string NormalizeNamespace(string value)
    {
        string[] segments = value.Split('.');
        if (segments.Length == 0 || segments.Any(static segment =>
                segment != SanitizeIdentifier(segment)))
        {
            throw new ArgumentException(
                $"Managed migration namespace '{value}' is not a qualified C# identifier.");
        }
        return value;
    }

    private static string SanitizeIdentifier(string value)
    {
        var result = new StringBuilder(value.Length + 1);
        foreach (char character in value)
        {
            result.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }
        if (result.Length == 0 || !char.IsLetter(result[0]) && result[0] != '_')
        {
            result.Insert(0, '_');
        }
        if (CSharpKeywords.Contains(result.ToString()))
        {
            result.Insert(0, '_');
        }
        return result.ToString();
    }

    private static string CSharpString(string value) => JsonSerializer.Serialize(value);

    private static string Sha256(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record PhaseDefinition(string Id, string Name, string CompletionGate);

    private sealed record ApiReference(
        string SourceApi,
        string TypeName,
        string? MemberName,
        string AccessKind,
        bool Manual);

    private sealed record RequestIdentity(
        string RequestKind,
        string? SuggestedTypePath,
        string? MemberName);
}
