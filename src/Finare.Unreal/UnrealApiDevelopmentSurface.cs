using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Finare.Core;

namespace Finare.Unreal;

public sealed record UnrealApiSourceSpan(
    string SourcePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

public sealed record UnrealApiDependencyRequest(
    string Id,
    string TypeName,
    string? SuggestedTypePath,
    string MemberName,
    string RequestKind,
    string SuggestedRoute,
    string SourceExpression,
    IReadOnlyList<UnrealApiSourceSpan> Locations);

/// <summary>
/// Source-owned requests for reflected APIs that are not yet present in the reviewed base
/// Contract. Unlike the Puerts migration request format, this is a normal development artifact
/// and retains exact C# spans and the generated managed namespace.
/// </summary>
public sealed record UnrealApiDependencyRequestSet(
    string SchemaVersion,
    string ProjectName,
    string ManagedNamespace,
    string SourceFingerprint,
    string BaseContractFingerprint,
    IReadOnlyList<UnrealApiDependencyRequest> Requests)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.unreal-api-dependency-requests/v0.1";
}

public sealed record UnrealApiDevelopmentSurfaceSummary(
    int RequestCount,
    int SelectedCount,
    int AlreadySelectedCount,
    int OpenDecisionCount,
    bool Applied);

public sealed record UnrealApiDevelopmentSurfaceDecision(
    string RequestId,
    string Code,
    string Severity,
    string Status,
    string Message,
    string? TypePath,
    string? Member,
    IReadOnlyList<string> Candidates,
    string SourceExpression,
    IReadOnlyList<UnrealApiSourceSpan> Locations);

/// <summary>
/// Deterministic result of resolving source requests against one reflection and base Contract.
/// The proposed Contract is diagnostic evidence. EffectiveContract changes only when every
/// request has one safe decision, which prevents partial generation on ambiguous input.
/// </summary>
public sealed record UnrealApiDevelopmentSurfaceResult(
    string SchemaVersion,
    string ProjectName,
    string ManagedNamespace,
    string SourceFingerprint,
    string ReflectionFingerprint,
    string BaseContractFingerprint,
    string EffectiveContractFingerprint,
    bool Applied,
    UnrealApiContract ProposedContract,
    UnrealApiContract EffectiveContract,
    IReadOnlyList<UnrealApiDevelopmentSurfaceDecision> Decisions,
    UnrealApiDevelopmentSurfaceSummary Summary)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.unreal-api-development-surface/v0.1";
}

public static class UnrealApiDependencyRequestJson
{
    public static string Serialize(
        UnrealApiDependencyRequestSet requests,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(Normalize(requests), CreateOptions(writeIndented))
            .ReplaceLineEndings("\n") + "\n";

    public static UnrealApiDependencyRequestSet Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        UnrealApiDependencyRequestSet? requests =
            JsonSerializer.Deserialize<UnrealApiDependencyRequestSet>(
                json,
                CreateOptions(writeIndented: true));
        return Normalize(requests ??
            throw new JsonException("The Unreal API dependency request set was empty."));
    }

    internal static UnrealApiDependencyRequestSet Normalize(
        UnrealApiDependencyRequestSet requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.SchemaVersion != UnrealApiDependencyRequestSet.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(requests.ProjectName) ||
            !IsQualifiedIdentifier(requests.ManagedNamespace) ||
            !IsSha256(requests.SourceFingerprint) ||
            !IsSha256(requests.BaseContractFingerprint) ||
            requests.Requests is null)
        {
            throw new JsonException("The Unreal API dependency request set has invalid required fields.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        UnrealApiDependencyRequest[] normalized = requests.Requests.Select(request =>
        {
            if (request is null ||
                string.IsNullOrWhiteSpace(request.Id) || !ids.Add(request.Id.Trim()) ||
                string.IsNullOrWhiteSpace(request.TypeName) ||
                request.SuggestedTypePath is not null &&
                !IsReflectedTypePath(request.SuggestedTypePath) ||
                string.IsNullOrWhiteSpace(request.MemberName) ||
                request.RequestKind != "contract-member" ||
                request.SuggestedRoute != "generated-binding" ||
                string.IsNullOrWhiteSpace(request.SourceExpression) ||
                request.Locations is null || request.Locations.Count == 0)
            {
                throw new JsonException("The Unreal API dependency request set contains an invalid request.");
            }
            UnrealApiSourceSpan[] locations = request.Locations.Select(location =>
            {
                if (location is null || string.IsNullOrWhiteSpace(location.SourcePath) ||
                    location.StartLine < 1 || location.StartColumn < 1 ||
                    location.EndLine < location.StartLine || location.EndColumn < 1 ||
                    location.EndLine == location.StartLine &&
                    location.EndColumn < location.StartColumn)
                {
                    throw new JsonException("An Unreal API dependency request has an invalid source span.");
                }
                return location with
                {
                    SourcePath = location.SourcePath.Trim().Replace('\\', '/'),
                };
            }).Distinct().OrderBy(static location => location.SourcePath, StringComparer.Ordinal)
                .ThenBy(static location => location.StartLine)
                .ThenBy(static location => location.StartColumn)
                .ThenBy(static location => location.EndLine)
                .ThenBy(static location => location.EndColumn)
                .ToArray();
            return request with
            {
                Id = request.Id.Trim(),
                TypeName = request.TypeName.Trim(),
                SuggestedTypePath = request.SuggestedTypePath?.Trim(),
                MemberName = request.MemberName.Trim(),
                SourceExpression = request.SourceExpression.Trim(),
                Locations = locations,
            };
        }).OrderBy(static request => request.Id, StringComparer.Ordinal).ToArray();

        return requests with
        {
            ProjectName = requests.ProjectName.Trim(),
            ManagedNamespace = requests.ManagedNamespace.Trim(),
            Requests = normalized,
        };
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }

    private static bool IsQualifiedIdentifier(string value) =>
        value.Split('.').All(static segment =>
            segment.Length > 0 &&
            (char.IsLetter(segment[0]) || segment[0] == '_') &&
            segment.Skip(1).All(static character =>
                char.IsLetterOrDigit(character) || character == '_'));

    private static bool IsReflectedTypePath(string value) =>
        value.StartsWith("/", StringComparison.Ordinal) &&
        value.Contains('.') && !value.Contains(':') && !value.Any(char.IsWhiteSpace);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public static class UnrealApiDevelopmentSurfaceJson
{
    public static string Serialize(
        UnrealApiDevelopmentSurfaceResult result,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(Normalize(result), CreateOptions(writeIndented))
            .ReplaceLineEndings("\n") + "\n";

    public static UnrealApiDevelopmentSurfaceResult Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        UnrealApiDevelopmentSurfaceResult? result =
            JsonSerializer.Deserialize<UnrealApiDevelopmentSurfaceResult>(
                json,
                CreateOptions(writeIndented: true));
        return Normalize(result ??
            throw new JsonException("The Unreal API development surface result was empty."));
    }

    private static UnrealApiDevelopmentSurfaceResult Normalize(
        UnrealApiDevelopmentSurfaceResult result)
    {
        if (result.SchemaVersion != UnrealApiDevelopmentSurfaceResult.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(result.ProjectName) ||
            string.IsNullOrWhiteSpace(result.ManagedNamespace) ||
            !IsSha256(result.SourceFingerprint) ||
            !IsSha256(result.ReflectionFingerprint) ||
            !IsSha256(result.BaseContractFingerprint) ||
            !IsSha256(result.EffectiveContractFingerprint) ||
            result.ProposedContract is null || result.EffectiveContract is null ||
            result.Decisions is null || result.Summary is null)
        {
            throw new JsonException("The Unreal API development surface result has invalid required fields.");
        }

        UnrealApiContract proposed = UnrealApiContractJson.Deserialize(
            UnrealApiContractJson.Serialize(result.ProposedContract, writeIndented: false));
        UnrealApiContract effective = UnrealApiContractJson.Deserialize(
            UnrealApiContractJson.Serialize(result.EffectiveContract, writeIndented: false));
        string[] statuses =
        [
            "selected",
            "already-selected",
            "manual-review",
            "unresolved-type",
            "unresolved-member",
            "ambiguous-type",
            "ambiguous-member",
            "unsupported-member",
        ];
        if (result.Decisions.Any(static decision => decision is null) ||
            result.Decisions.Select(static decision => decision.RequestId)
                .Distinct(StringComparer.Ordinal).Count() != result.Decisions.Count ||
            result.Decisions.Any(decision =>
                string.IsNullOrWhiteSpace(decision.RequestId) ||
                string.IsNullOrWhiteSpace(decision.Code) ||
                decision.Severity is not ("information" or "error") ||
                !statuses.Contains(decision.Status, StringComparer.Ordinal) ||
                string.IsNullOrWhiteSpace(decision.Message) ||
                string.IsNullOrWhiteSpace(decision.SourceExpression) ||
                decision.Candidates is null ||
                decision.Locations is null || decision.Locations.Count == 0))
        {
            throw new JsonException("The Unreal API development surface contains invalid decisions.");
        }
        UnrealApiDevelopmentSurfaceDecision[] decisions = result.Decisions
            .Select(static decision => decision with
            {
                Candidates = decision.Candidates.Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToArray(),
                Locations = decision.Locations.Distinct()
                    .OrderBy(static location => location.SourcePath, StringComparer.Ordinal)
                    .ThenBy(static location => location.StartLine)
                    .ThenBy(static location => location.StartColumn)
                    .ToArray(),
            })
            .OrderBy(static decision => decision.RequestId, StringComparer.Ordinal)
            .ToArray();
        int open = decisions.Count(static decision => decision.Status is
            "manual-review" or "unresolved-type" or "unresolved-member" or
            "ambiguous-type" or "ambiguous-member" or "unsupported-member");
        UnrealApiDevelopmentSurfaceSummary expected = new(
            decisions.Length,
            decisions.Count(static decision => decision.Status == "selected"),
            decisions.Count(static decision => decision.Status == "already-selected"),
            open,
            result.Applied);
        if (result.Summary != expected || result.Applied != (open == 0) ||
            result.ManagedNamespace != effective.ManagedNamespace ||
            result.EffectiveContractFingerprint != Fingerprint(effective))
        {
            throw new JsonException("The Unreal API development surface result is inconsistent.");
        }

        return result with
        {
            ProjectName = result.ProjectName.Trim(),
            ManagedNamespace = result.ManagedNamespace.Trim(),
            ProposedContract = proposed,
            EffectiveContract = effective,
            Decisions = decisions,
            Summary = expected,
        };
    }

    public static string Fingerprint(UnrealApiContract contract) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            UnrealApiContractJson.Serialize(contract, writeIndented: false))))
            .ToLowerInvariant();

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}

public sealed class UnrealApiDevelopmentSurfacePlanner
{
    public UnrealApiDevelopmentSurfaceResult Plan(
        UnrealReflectionManifest reflection,
        UnrealApiDependencyRequestSet requests,
        UnrealApiContract baseContract)
    {
        UnrealApiDependencyRequestSet normalizedRequests =
            UnrealApiDependencyRequestJson.Normalize(requests);
        UnrealApiContract normalizedContract = UnrealApiContractJson.Deserialize(
            UnrealApiContractJson.Serialize(baseContract, writeIndented: false));
        string baseFingerprint = UnrealApiDevelopmentSurfaceJson.Fingerprint(normalizedContract);
        if (normalizedRequests.ManagedNamespace != normalizedContract.ManagedNamespace ||
            normalizedRequests.BaseContractFingerprint != baseFingerprint)
        {
            throw new InvalidDataException(
                "The source dependency requests were produced for a different API Contract.");
        }

        var migrationRequests = new UnrealPuertsContractRequestSet(
            "unifiedscript.puerts-contract-requests/v0.1",
            normalizedRequests.ProjectName,
            normalizedRequests.SourceFingerprint,
            baseFingerprint,
            normalizedRequests.Requests.Select(request => new UnrealPuertsContractRequest(
                request.Id,
                request.TypeName,
                request.SuggestedTypePath,
                request.MemberName,
                request.RequestKind,
                "contract",
                [request.SourceExpression],
                request.Locations.Select(static location =>
                    new UnrealPuertsMigrationLocation(
                        location.SourcePath,
                        location.StartLine)).ToArray())).ToArray());
        UnrealApiContractProposal proposal = new UnrealApiContractProposalGenerator().Generate(
            reflection,
            migrationRequests,
            normalizedContract);
        IReadOnlyDictionary<string, UnrealApiDependencyRequest> requestsById =
            normalizedRequests.Requests.ToDictionary(static request => request.Id, StringComparer.Ordinal);
        IReadOnlyDictionary<(string OwnerPath, string Selector), UnrealBindingCoverageEntry>
            coverageByMember = CreateCoverageMap(reflection, proposal.ProposedContract);
        UnrealApiDevelopmentSurfaceDecision[] decisions = proposal.Decisions.Select(decision =>
        {
            UnrealApiDependencyRequest request = requestsById[decision.RequestId];
            string status = decision.Status;
            string message = decision.Message;
            IReadOnlyList<string> candidates = decision.Candidates;
            if (status is "selected" or "already-selected" &&
                decision.TypePath is not null && decision.Member is not null &&
                coverageByMember.TryGetValue(
                    (decision.TypePath, decision.Member),
                    out UnrealBindingCoverageEntry? coverage) &&
                coverage.Disposition == UnrealBindingDisposition.Rejected)
            {
                status = "unsupported-member";
                message = string.IsNullOrWhiteSpace(coverage.Message)
                    ? "The reflected member has no supported generated or safe fallback route."
                    : coverage.Message;
                candidates = coverage.DiagnosticCodes;
            }
            bool open = status is not ("selected" or "already-selected");
            return new UnrealApiDevelopmentSurfaceDecision(
                decision.RequestId,
                DiagnosticCode(status),
                open ? "error" : "information",
                status,
                message,
                decision.TypePath,
                decision.Member,
                candidates,
                request.SourceExpression,
                request.Locations);
        }).OrderBy(static decision => decision.RequestId, StringComparer.Ordinal).ToArray();
        int openDecisionCount = decisions.Count(static decision =>
            decision.Status is not ("selected" or "already-selected"));
        bool applied = openDecisionCount == 0;
        UnrealApiContract effective = applied ? proposal.ProposedContract : normalizedContract;
        var summary = new UnrealApiDevelopmentSurfaceSummary(
            decisions.Length,
            decisions.Count(static decision => decision.Status == "selected"),
            decisions.Count(static decision => decision.Status == "already-selected"),
            openDecisionCount,
            applied);
        return new UnrealApiDevelopmentSurfaceResult(
            UnrealApiDevelopmentSurfaceResult.CurrentSchemaVersion,
            normalizedRequests.ProjectName,
            normalizedRequests.ManagedNamespace,
            normalizedRequests.SourceFingerprint,
            proposal.ReflectionFingerprint,
            baseFingerprint,
            UnrealApiDevelopmentSurfaceJson.Fingerprint(effective),
            applied,
            proposal.ProposedContract,
            effective,
            decisions,
            summary);
    }

    private static string DiagnosticCode(string status) => status switch
    {
        "selected" => "USUE3000",
        "already-selected" => "USUE3001",
        "manual-review" => "USUE3100",
        "unresolved-type" => "USUE3101",
        "unresolved-member" => "USUE3102",
        "ambiguous-type" => "USUE3103",
        "ambiguous-member" => "USUE3104",
        "unsupported-member" => "USUE3105",
        _ => throw new InvalidDataException($"Unknown Unreal API surface decision '{status}'."),
    };

    private static IReadOnlyDictionary<(string OwnerPath, string Selector), UnrealBindingCoverageEntry>
        CreateCoverageMap(
            UnrealReflectionManifest reflection,
            UnrealApiContract contract)
    {
        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(
            reflection,
            contract);
        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(
            projection.Reflection,
            new UnrealBindingGeneratorOptions(projection.Contract.ManagedNamespace));
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(
            projection.NativeReflection,
            new UnrealNativeThunkGeneratorOptions(
                projection.Contract.NativeHeader,
                projection.Contract.NativeNamespace));
        UnrealBindingCoverageReport coverage = UnrealBindingCoverageReport.Create(
            projection,
            managed,
            native);
        var result = new Dictionary<(string OwnerPath, string Selector), UnrealBindingCoverageEntry>();
        foreach (UnrealReflectedType type in projection.SelectedReflection.Types)
        {
            foreach (UnrealReflectedProperty property in type.Properties)
            {
                AddCoverage(result, coverage, type.Path, property.Name, property.Path);
            }
            foreach (UnrealReflectedFunction function in type.Functions)
            {
                AddCoverage(
                    result,
                    coverage,
                    type.Path,
                    type.Functions.Count(candidate => candidate.Name == function.Name) == 1
                        ? function.Name
                        : UnrealApiContractProjector.FunctionSelector(function),
                    function.Path);
            }
            foreach (UnrealReflectedDelegate reflectedDelegate in type.Delegates)
            {
                AddCoverage(
                    result,
                    coverage,
                    type.Path,
                    reflectedDelegate.Name,
                    reflectedDelegate.Path);
            }
        }
        return result;
    }

    private static void AddCoverage(
        IDictionary<(string OwnerPath, string Selector), UnrealBindingCoverageEntry> result,
        UnrealBindingCoverageReport coverage,
        string ownerPath,
        string selector,
        string memberPath)
    {
        UnrealBindingCoverageEntry entry = coverage.Entries.Single(candidate =>
            candidate.Path == memberPath);
        result.Add((ownerPath, selector), entry);
    }
}
