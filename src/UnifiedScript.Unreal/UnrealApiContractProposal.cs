using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedScript.Core;

namespace UnifiedScript.Unreal;

public sealed record UnrealApiContractProposalDecision(
    string RequestId,
    string RequestKind,
    string SuggestedRoute,
    string Status,
    string Message,
    string? TypePath,
    string? Member,
    IReadOnlyList<string> Candidates);

public sealed record UnrealApiContractProposalSummary(
    int RequestCount,
    int SelectedCount,
    int AlreadySelectedCount,
    int ManualReviewCount,
    int UnresolvedCount,
    int AmbiguousCount,
    int OpenDecisionCount);

/// <summary>
/// A deterministic, review-only merge proposal. It never changes the project API Contract and
/// records one explicit decision for every source dependency request.
/// </summary>
public sealed record UnrealApiContractProposal(
    string SchemaVersion,
    string ProjectName,
    string ReflectionFingerprint,
    string RequestFingerprint,
    string BaseContractFingerprint,
    bool ReviewRequired,
    UnrealApiContract ProposedContract,
    IReadOnlyList<UnrealApiContractProposalDecision> Decisions,
    UnrealApiContractProposalSummary Summary)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.unreal-api-contract-proposal/v0.1";
}

public static class UnrealApiContractProposalJson
{
    public static string Serialize(
        UnrealApiContractProposal proposal,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(Validate(proposal), CreateOptions(writeIndented))
            .ReplaceLineEndings("\n") + "\n";

    public static UnrealApiContractProposal Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        UnrealApiContractProposal? proposal =
            JsonSerializer.Deserialize<UnrealApiContractProposal>(
                json,
                CreateOptions(writeIndented: true));
        return Validate(proposal ??
            throw new JsonException("The Unreal API Contract proposal was empty."));
    }

    private static UnrealApiContractProposal Validate(UnrealApiContractProposal proposal)
    {
        if (proposal.SchemaVersion != UnrealApiContractProposal.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported Unreal API Contract proposal schema '{proposal.SchemaVersion}'.");
        }
        if (string.IsNullOrWhiteSpace(proposal.ProjectName) ||
            !IsSha256(proposal.ReflectionFingerprint) ||
            !IsSha256(proposal.RequestFingerprint) ||
            !IsSha256(proposal.BaseContractFingerprint) ||
            !proposal.ReviewRequired ||
            proposal.ProposedContract is null ||
            proposal.Decisions is null ||
            proposal.Summary is null)
        {
            throw new JsonException("The Unreal API Contract proposal has invalid required fields.");
        }

        UnrealApiContract contract = UnrealApiContractJson.Deserialize(
            UnrealApiContractJson.Serialize(proposal.ProposedContract, writeIndented: false));
        string[] validStatuses =
        [
            "selected",
            "already-selected",
            "manual-review",
            "unresolved-type",
            "unresolved-member",
            "ambiguous-type",
            "ambiguous-member",
        ];
        if (proposal.Decisions.Any(static decision => decision is null) ||
            proposal.Decisions.Select(static decision => decision.RequestId)
                .Distinct(StringComparer.Ordinal).Count() != proposal.Decisions.Count ||
            proposal.Decisions.Any(decision =>
                string.IsNullOrWhiteSpace(decision.RequestId) ||
                string.IsNullOrWhiteSpace(decision.RequestKind) ||
                string.IsNullOrWhiteSpace(decision.SuggestedRoute) ||
                string.IsNullOrWhiteSpace(decision.Message) ||
                !validStatuses.Contains(decision.Status, StringComparer.Ordinal) ||
                decision.Candidates is null ||
                decision.Candidates.Any(string.IsNullOrWhiteSpace)))
        {
            throw new JsonException("The Unreal API Contract proposal contains invalid decisions.");
        }

        UnrealApiContractProposalDecision[] decisions = proposal.Decisions
            .Select(static decision => decision with
            {
                Candidates = decision.Candidates
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
            })
            .OrderBy(static decision => decision.RequestId, StringComparer.Ordinal)
            .ToArray();
        UnrealApiContractProposalSummary expected = Summarize(decisions);
        if (proposal.Summary != expected)
        {
            throw new JsonException("The Unreal API Contract proposal summary does not match its decisions.");
        }
        return proposal with
        {
            ProjectName = proposal.ProjectName.Trim(),
            ProposedContract = contract,
            Decisions = decisions,
            Summary = expected,
        };
    }

    internal static UnrealApiContractProposalSummary Summarize(
        IReadOnlyList<UnrealApiContractProposalDecision> decisions)
    {
        int manual = decisions.Count(static decision => decision.Status == "manual-review");
        int unresolved = decisions.Count(static decision =>
            decision.Status is "unresolved-type" or "unresolved-member");
        int ambiguous = decisions.Count(static decision =>
            decision.Status is "ambiguous-type" or "ambiguous-member");
        return new UnrealApiContractProposalSummary(
            decisions.Count,
            decisions.Count(static decision => decision.Status == "selected"),
            decisions.Count(static decision => decision.Status == "already-selected"),
            manual,
            unresolved,
            ambiguous,
            manual + unresolved + ambiguous);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}

public sealed class UnrealApiContractProposalGenerator
{
    public UnrealApiContractProposal Generate(
        UnrealReflectionManifest reflection,
        UnrealPuertsContractRequestSet requests,
        UnrealApiContract baseContract)
    {
        UnrealReflectionManifest normalizedReflection =
            UnrealReflectionJson.NormalizeAndValidate(reflection);
        UnrealApiContract normalizedContract = UnrealApiContractJson.Deserialize(
            UnrealApiContractJson.Serialize(baseContract, writeIndented: false));
        UnrealPuertsContractRequestSet normalizedRequests = NormalizeRequests(requests);

        // A proposal must start from a valid, current Contract rather than repairing stale input.
        _ = UnrealApiContractProjector.Project(normalizedReflection, normalizedContract);

        string reflectionFingerprint = Sha256(
            UnrealReflectionJson.Serialize(normalizedReflection, writeIndented: false));
        string requestFingerprint = Sha256(
            UnrealPuertsMigrationPackageJson.Serialize(normalizedRequests));
        string contractFingerprint = Sha256(
            UnrealApiContractJson.Serialize(normalizedContract, writeIndented: false));
        if (normalizedRequests.ContractFingerprint is not null &&
            NormalizeSha256(normalizedRequests.ContractFingerprint) != contractFingerprint)
        {
            throw new InvalidDataException(
                "The contract request set was assessed against a different API Contract. " +
                "Regenerate the migration plan before proposing Contract changes.");
        }

        var selected = normalizedContract.Types.ToDictionary(
            static type => type.Path,
            static type => type.Members.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var decisions = new List<UnrealApiContractProposalDecision>(
            normalizedRequests.Requests.Count);
        UnrealReflectionManifest available = normalizedContract.NativeExtensions is null
            ? normalizedReflection
            : UnrealNativeExtensions.Apply(normalizedReflection, normalizedContract.NativeExtensions);
        foreach (UnrealPuertsContractRequest request in normalizedRequests.Requests)
        {
            decisions.Add(ResolveRequest(request, available.Types, selected));
        }

        UnrealApiContract proposedContract = normalizedContract with
        {
            Types = selected
                .Select(static pair => new UnrealApiContractType(
                    pair.Key,
                    pair.Value.Order(StringComparer.Ordinal).ToArray()))
                .OrderBy(static type => type.Path, StringComparer.Ordinal)
                .ToArray(),
        };
        proposedContract = UnrealApiContractJson.Deserialize(
            UnrealApiContractJson.Serialize(proposedContract, writeIndented: false));
        _ = UnrealApiContractProjector.Project(normalizedReflection, proposedContract);

        UnrealApiContractProposalDecision[] orderedDecisions = decisions
            .OrderBy(static decision => decision.RequestId, StringComparer.Ordinal)
            .ToArray();
        return new UnrealApiContractProposal(
            UnrealApiContractProposal.CurrentSchemaVersion,
            normalizedRequests.ProjectName,
            reflectionFingerprint,
            requestFingerprint,
            contractFingerprint,
            ReviewRequired: true,
            proposedContract,
            orderedDecisions,
            UnrealApiContractProposalJson.Summarize(orderedDecisions));
    }

    private static UnrealApiContractProposalDecision ResolveRequest(
        UnrealPuertsContractRequest request,
        IReadOnlyList<UnrealReflectedType> reflectedTypes,
        IDictionary<string, HashSet<string>> selected)
    {
        if (request.SuggestedRoute == "data-plane")
        {
            return Decision(
                request,
                "manual-review",
                "The request targets a data-plane operation and must be reviewed against its schema, not added as a reflected member.",
                null,
                null,
                []);
        }

        UnrealReflectedType[] typeCandidates = ResolveTypes(request, reflectedTypes);
        if (typeCandidates.Length == 0)
        {
            return Decision(
                request,
                "unresolved-type",
                $"No reflected Unreal type matches '{request.TypeName}'.",
                null,
                null,
                []);
        }
        if (typeCandidates.Length > 1)
        {
            return Decision(
                request,
                "ambiguous-type",
                $"Multiple reflected Unreal types match '{request.TypeName}'.",
                null,
                null,
                typeCandidates.Select(static type => type.Path).ToArray());
        }

        UnrealReflectedType type = typeCandidates[0];
        if (request.MemberName is null)
        {
            if (selected.ContainsKey(type.Path))
            {
                return Decision(
                    request,
                    "already-selected",
                    "The requested type is already present in the API Contract.",
                    type.Path,
                    null,
                    []);
            }
            return Decision(
                request,
                "manual-review",
                "Type construction requires an explicit Script Class/constructor policy; " +
                "the member allowlist cannot expose a type implicitly.",
                type.Path,
                null,
                []);
        }

        if (type.Kind == UnrealReflectedTypeKind.Enum &&
            type.EnumValues.Any(value => value.Name == request.MemberName))
        {
            return Decision(
                request,
                "manual-review",
                "The dependency is an enum value, not a callable member. Review explicit enum-type inclusion before expanding the Contract.",
                type.Path,
                null,
                []);
        }

        string[] memberCandidates = ResolveMembers(type, request.MemberName);
        if (memberCandidates.Length == 0)
        {
            return Decision(
                request,
                "unresolved-member",
                $"Reflected type '{type.Path}' has no member matching '{request.MemberName}'.",
                type.Path,
                null,
                []);
        }
        if (memberCandidates.Length > 1)
        {
            return Decision(
                request,
                "ambiguous-member",
                $"Member '{request.MemberName}' has multiple overloads; select an exact signature.",
                type.Path,
                null,
                memberCandidates);
        }

        string member = memberCandidates[0];
        if (selected.TryGetValue(type.Path, out HashSet<string>? members) &&
            members.Contains(member))
        {
            return Decision(
                request,
                "already-selected",
                "The exact reflected member is already selected by the API Contract.",
                type.Path,
                member,
                []);
        }

        if (members is null)
        {
            members = new HashSet<string>(StringComparer.Ordinal);
            selected.Add(type.Path, members);
        }
        members.Add(member);
        return Decision(
            request,
            "selected",
            "The unique reflected member was added to the review-only Contract proposal.",
            type.Path,
            member,
            []);
    }

    private static UnrealReflectedType[] ResolveTypes(
        UnrealPuertsContractRequest request,
        IReadOnlyList<UnrealReflectedType> reflectedTypes)
    {
        if (request.SuggestedTypePath is not null)
        {
            UnrealReflectedType? exact = reflectedTypes.SingleOrDefault(type =>
                type.Path == request.SuggestedTypePath);
            if (exact is not null)
            {
                return [exact];
            }
        }
        return reflectedTypes.Where(type =>
                type.Name == request.TypeName ||
                type.NativeName == request.TypeName ||
                NativeNameWithoutPrefix(type.NativeName) == request.TypeName)
            .OrderBy(static type => type.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NativeNameWithoutPrefix(string nativeName) =>
        nativeName.Length > 1 && nativeName[0] is 'A' or 'U' or 'F' or 'E'
            ? nativeName[1..]
            : nativeName;

    private static string[] ResolveMembers(UnrealReflectedType type, string requestedMember)
    {
        var candidates = new List<string>();
        candidates.AddRange(type.Properties
            .Where(property => property.Name == requestedMember)
            .Select(static property => property.Name));
        candidates.AddRange(type.Delegates
            .Where(item => item.Name == requestedMember)
            .Select(static item => item.Name));

        IGrouping<string, UnrealReflectedFunction>[] functionGroups = type.Functions
            .GroupBy(static function => function.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (IGrouping<string, UnrealReflectedFunction> group in functionGroups)
        {
            UnrealReflectedFunction[] functions = group.ToArray();
            string managedName = UnrealManagedNaming.FunctionName(functions[0]);
            if (functions.Length == 1)
            {
                if (group.Key == requestedMember || managedName == requestedMember)
                {
                    candidates.Add(group.Key);
                }
                continue;
            }
            foreach (UnrealReflectedFunction function in functions)
            {
                string selector = UnrealApiContractProjector.FunctionSelector(function);
                string managedSelector = managedName + selector[group.Key.Length..];
                if (requestedMember == group.Key || requestedMember == managedName ||
                    requestedMember == selector || requestedMember == managedSelector)
                {
                    candidates.Add(selector);
                }
            }
        }
        return candidates.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static UnrealApiContractProposalDecision Decision(
        UnrealPuertsContractRequest request,
        string status,
        string message,
        string? typePath,
        string? member,
        IReadOnlyList<string> candidates) =>
        new(
            request.Id,
            request.RequestKind,
            request.SuggestedRoute,
            status,
            message,
            typePath,
            member,
            candidates.Order(StringComparer.Ordinal).ToArray());

    private static UnrealPuertsContractRequestSet NormalizeRequests(
        UnrealPuertsContractRequestSet requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.SchemaVersion != "unifiedscript.puerts-contract-requests/v0.1" ||
            string.IsNullOrWhiteSpace(requests.ProjectName) ||
            !IsSha256(requests.ScanFingerprint) ||
            requests.Requests is null)
        {
            throw new JsonException("The Puerts contract request set has invalid required fields.");
        }
        if (requests.ContractFingerprint is not null &&
            !IsSupportedSha256(requests.ContractFingerprint))
        {
            throw new JsonException("The Puerts contract request set has an invalid Contract fingerprint.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        UnrealPuertsContractRequest[] normalized = requests.Requests.Select(request =>
        {
            string requestId = request?.Id?.Trim() ?? string.Empty;
            if (request is null ||
                requestId.Length == 0 || !ids.Add(requestId) ||
                string.IsNullOrWhiteSpace(request.TypeName) ||
                request.RequestKind is not ("runtime-service-route" or "type-construction" or "contract-member") ||
                string.IsNullOrWhiteSpace(request.SuggestedRoute) ||
                request.SourceApis is null || request.SourceApis.Count == 0 ||
                request.SourceApis.Any(string.IsNullOrWhiteSpace) ||
                request.Locations is null || request.Locations.Any(static location =>
                    location is null || string.IsNullOrWhiteSpace(location.SourcePath) ||
                    location.Line < 1))
            {
                throw new JsonException("The Puerts contract request set contains an invalid request.");
            }
            return request with
            {
                Id = requestId,
                TypeName = request.TypeName.Trim(),
                SuggestedTypePath = NullIfWhiteSpace(request.SuggestedTypePath),
                MemberName = NullIfWhiteSpace(request.MemberName),
                RequestKind = request.RequestKind.Trim(),
                SuggestedRoute = request.SuggestedRoute.Trim(),
                SourceApis = request.SourceApis
                    .Select(static value => value.Trim())
                    .Where(static value => value.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                Locations = request.Locations
                    .OrderBy(static location => location.SourcePath, StringComparer.Ordinal)
                    .ThenBy(static location => location.Line)
                    .ToArray(),
            };
        }).OrderBy(static request => request.Id, StringComparer.Ordinal).ToArray();
        if (normalized.Any(static request => request.SourceApis.Count == 0))
        {
            throw new JsonException("Every Puerts contract request must retain at least one source API.");
        }
        return requests with
        {
            ProjectName = requests.ProjectName.Trim(),
            Requests = normalized,
        };
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSupportedSha256(string value) =>
        IsSha256(value) ||
        value.StartsWith("sha256:", StringComparison.Ordinal) && IsSha256(value[7..]);

    private static string NormalizeSha256(string value) =>
        value.StartsWith("sha256:", StringComparison.Ordinal) ? value[7..] : value;

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
