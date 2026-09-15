using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Finare.Core;

namespace Finare.Unreal;

/// <summary>
/// A type-level discovery rule used by the v0.3 contract. Empty includes mean
/// every reflected member on the type; excludes are applied to discovered
/// members before the effective contract is materialized.
/// </summary>
public sealed record UnrealApiContractTypeRule(string Path)
{
    public IReadOnlyList<string> IncludeMembers { get; init; } = [];

    public IReadOnlyList<string> ExcludeMembers { get; init; } = [];
}

/// <summary>
/// Optional module/type discovery selectors. The projector turns these rules
/// into the same explicit reflected member lists consumed by every generator.
/// </summary>
public sealed record UnrealApiContractDiscovery(
    IReadOnlyList<string> Modules,
    IReadOnlyList<UnrealApiContractTypeRule> Types)
{
    /// <summary>
    /// Exact reflected type paths excluded from module discovery. Explicit
    /// selection of an excluded type is a configuration error.
    /// </summary>
    public IReadOnlyList<string> ExcludedTypePaths { get; init; } = [];
}

public sealed record UnrealApiContractType(
    string Path,
    IReadOnlyList<string> Members);

/// <summary>
/// Selection shared by managed wrappers, native thunks, coverage, and visual node metadata.
/// Source-driven builds resolve this in memory; explicit project-owned contracts remain
/// supported for existing configurations.
/// </summary>
public sealed record UnrealApiContract(
    string SchemaVersion,
    string ContractName,
    string ManagedNamespace,
    string NativeHeader,
    string NativeNamespace,
    IReadOnlyList<UnrealApiContractType> Types)
{
    public const string CurrentSchemaVersion = "unifiedscript.unreal-api-contract/v0.3";

    public const string PreviousSchemaVersion = "unifiedscript.unreal-api-contract/v0.2";

    public const string LegacySchemaVersion = "unifiedscript.unreal-api-contract/v0.1";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UnrealApiContractDiscovery? Discovery { get; init; }

    public UnrealApiPolicy Policy { get; init; } = UnrealApiPolicy.AllowSelected;

    public UnrealBindingCoverageBaseline? CoverageBaseline { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UnrealNativeExtensionManifest? NativeExtensions { get; init; }
}

public sealed record UnrealApiContractProjection(
    UnrealApiContract Contract,
    UnrealReflectionManifest Reflection)
{
    /// <summary>
    /// The resolved v0.3 contract for a discovery input. Explicit v0.1/v0.2
    /// contracts remain unchanged so their serialized and generated forms stay
    /// byte-compatible. This value is suitable for lock/fingerprint output.
    /// </summary>
    public UnrealApiContract EffectiveContract { get; init; } = Contract;

    public UnrealReflectionManifest SelectedReflection { get; init; } = Reflection;

    public UnrealReflectionManifest NativeReflection { get; init; } = Reflection;

    public IReadOnlySet<string>? ReflectedStructPaths { get; init; }

    public UnrealApiPolicyEvaluation PolicyEvaluation { get; init; } =
        UnrealApiPolicyEvaluator.Evaluate(Reflection, UnrealApiPolicy.AllowSelected);
}

public static class UnrealApiContractJson
{
    public static string Serialize(UnrealApiContract contract, bool writeIndented = true) =>
        JsonSerializer.Serialize(Normalize(contract), CreateOptions(writeIndented))
            .ReplaceLineEndings("\n") + "\n";

    public static UnrealApiContract Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        UnrealApiContract? contract = JsonSerializer.Deserialize<UnrealApiContract>(
            json,
            CreateOptions(writeIndented: true));
        if (contract is null)
        {
            throw new JsonException("The Unreal API contract was empty.");
        }
        return Normalize(contract);
    }

    private static UnrealApiContract Normalize(UnrealApiContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.SchemaVersion != UnrealApiContract.CurrentSchemaVersion &&
            contract.SchemaVersion != UnrealApiContract.PreviousSchemaVersion &&
            contract.SchemaVersion != UnrealApiContract.LegacySchemaVersion)
        {
            throw new JsonException(
                $"Unsupported Unreal API contract schema '{contract.SchemaVersion}'. " +
                $"Expected '{UnrealApiContract.CurrentSchemaVersion}'.");
        }
        if (contract.SchemaVersion != UnrealApiContract.CurrentSchemaVersion &&
            contract.Discovery is not null)
        {
            throw new JsonException(
                $"Unreal API contract discovery requires schema '{UnrealApiContract.CurrentSchemaVersion}'.");
        }
        if (!IsContractName(contract.ContractName) ||
            !IsQualifiedIdentifier(contract.ManagedNamespace, '.') ||
            !IsQualifiedIdentifier(contract.NativeNamespace, ':') ||
            !IsHeaderPath(contract.NativeHeader) ||
            contract.Types is null ||
            contract.Types.Count == 0 && contract.SchemaVersion != UnrealApiContract.CurrentSchemaVersion &&
                !HasDiscoverySelectors(contract.Discovery))
        {
            throw new JsonException("The Unreal API contract has invalid required fields.");
        }

        var typePaths = new HashSet<string>(StringComparer.Ordinal);
        var normalizedTypes = new List<UnrealApiContractType>(contract.Types.Count);
        foreach (UnrealApiContractType? type in contract.Types)
        {
            if (type is null || !IsReflectedTypePath(type.Path) || type.Members is null ||
                !typePaths.Add(type.Path))
            {
                throw new JsonException("The Unreal API contract contains an invalid type selector.");
            }
            if (contract.SchemaVersion == UnrealApiContract.LegacySchemaVersion &&
                type.Members.Count == 0)
            {
                throw new JsonException(
                    $"Unreal API contract schema '{UnrealApiContract.LegacySchemaVersion}' " +
                    $"does not support an empty member selector for '{type.Path}'.");
            }
            string[] members = type.Members
                .Select(static member => member?.Trim() ?? string.Empty)
                .ToArray();
            if (members.Any(static member => !IsMemberSelector(member)) ||
                members.Distinct(StringComparer.Ordinal).Count() != members.Length)
            {
                throw new JsonException(
                    $"Unreal API contract type '{type.Path}' has invalid or duplicate members.");
            }
            normalizedTypes.Add(new UnrealApiContractType(
                type.Path,
                members.Order(StringComparer.Ordinal).ToArray()));
        }

        UnrealApiContractDiscovery? discovery = NormalizeDiscovery(contract.Discovery);
        return contract with
        {
            SchemaVersion = contract.SchemaVersion == UnrealApiContract.LegacySchemaVersion
                    ? UnrealApiContract.PreviousSchemaVersion
                    : contract.SchemaVersion,
            ContractName = contract.ContractName.Trim(),
            ManagedNamespace = contract.ManagedNamespace.Trim(),
            NativeHeader = contract.NativeHeader.Trim().Replace('\\', '/'),
            NativeNamespace = contract.NativeNamespace.Trim(),
            Types = normalizedTypes.OrderBy(static type => type.Path, StringComparer.Ordinal).ToArray(),
            Discovery = discovery,
            Policy = UnrealApiPolicyEvaluator.Normalize(contract.Policy),
            CoverageBaseline = NormalizeBaseline(contract.CoverageBaseline),
            NativeExtensions = contract.NativeExtensions is null
                ? null : UnrealNativeExtensions.Normalize(contract.NativeExtensions),
        };
    }

    private static bool HasDiscoverySelectors(UnrealApiContractDiscovery? discovery) =>
        discovery is not null &&
        (discovery.Modules is { Count: > 0 } || discovery.Types is { Count: > 0 });

    private static UnrealApiContractDiscovery? NormalizeDiscovery(
        UnrealApiContractDiscovery? discovery)
    {
        if (discovery is null)
        {
            return null;
        }
        if (discovery.Modules is null || discovery.Types is null ||
            !HasDiscoverySelectors(discovery))
        {
            throw new JsonException("Unreal API contract discovery requires a module or type selector.");
        }

        string[] modules = discovery.Modules
            .Select(static module => module?.Trim() ?? string.Empty)
            .ToArray();
        if (modules.Any(static module => !IsIdentifier(module)) ||
            modules.Distinct(StringComparer.Ordinal).Count() != modules.Length)
        {
            throw new JsonException("Unreal API discovery modules must be unique identifiers.");
        }

        if (discovery.ExcludedTypePaths is null)
        {
            throw new JsonException("Unreal API discovery excluded type paths cannot be null.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        UnrealApiContractTypeRule[] types = discovery.Types.Select(rule =>
        {
            string path = rule?.Path?.Trim() ?? string.Empty;
            if (rule is null || !IsReflectedTypePath(path) ||
                !paths.Add(path) || rule.IncludeMembers is null ||
                rule.ExcludeMembers is null)
            {
                throw new JsonException("Unreal API discovery contains an invalid type rule.");
            }

            string[] includes = NormalizeSignatureRules(
                rule.IncludeMembers,
                path,
                "include");
            string[] excludes = NormalizeSignatureRules(
                rule.ExcludeMembers,
                path,
                "exclude");
            if (includes.Intersect(excludes, StringComparer.Ordinal).Any())
            {
                throw new JsonException(
                    $"Unreal API discovery type '{path}' includes and excludes the same member rule.");
            }
            return new UnrealApiContractTypeRule(path)
            {
                IncludeMembers = includes.Order(StringComparer.Ordinal).ToArray(),
                ExcludeMembers = excludes.Order(StringComparer.Ordinal).ToArray(),
            };
        }).OrderBy(static rule => rule.Path, StringComparer.Ordinal).ToArray();

        string[] excludedTypes = discovery.ExcludedTypePaths
            .Select(static path => path?.Trim() ?? string.Empty)
            .ToArray();
        if (excludedTypes.Any(static path => !IsReflectedTypePath(path)) ||
            excludedTypes.Distinct(StringComparer.Ordinal).Count() != excludedTypes.Length ||
            excludedTypes.Intersect(types.Select(static type => type.Path), StringComparer.Ordinal).Any())
        {
            throw new JsonException(
                "Unreal API discovery excluded type paths must be unique and cannot have type rules.");
        }

        return new UnrealApiContractDiscovery(
            modules.Order(StringComparer.Ordinal).ToArray(),
            types)
        {
            ExcludedTypePaths = excludedTypes.Order(StringComparer.Ordinal).ToArray(),
        };
    }

    private static string[] NormalizeSignatureRules(
        IReadOnlyList<string> rules,
        string typePath,
        string ruleKind)
    {
        string[] normalized = rules
            .Select(static rule => rule?.Trim() ?? string.Empty)
            .ToArray();
        if (normalized.Any(rule => !IsSignatureRule(rule)) ||
            normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new JsonException(
                $"Unreal API discovery type '{typePath}' has invalid or duplicate {ruleKind} signature rules.");
        }
        return normalized;
    }

    private static bool IsSignatureRule(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl) &&
        !value.Contains(':') &&
        !value.Contains("->", StringComparison.Ordinal);

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }

    private static bool IsContractName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(static character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' or '.');

    private static bool IsQualifiedIdentifier(string? value, char separator)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        string[] segments = separator == ':'
            ? value.Split("::", StringSplitOptions.None)
            : value.Split(separator);
        return segments.Length > 0 && segments.All(IsIdentifier);
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
            value.Skip(1).All(static character => char.IsLetterOrDigit(character) || character == '_');

    private static bool IsMemberSelector(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            return false;
        }

        int open = value.IndexOf('(');
        if (open < 0)
        {
            return !value.Any(static character =>
                char.IsControl(character) || character is ':' or '(' or ')');
        }

        if (open == 0 || !value.EndsWith(')'))
        {
            return false;
        }

        string parameters = value[(open + 1)..^1];
        if (
            value.IndexOf('(', open + 1) >= 0 ||
            parameters.Any(static character =>
                character is '(' or ')' || char.IsWhiteSpace(character)))
        {
            return false;
        }

        // Overload selectors are generated as Name(NativeType,...).  Keep the
        // original signature support while allowing UE Blueprint names such as
        // "Health Changed" in the name portion.
        string name = value[..open];
        return !string.IsNullOrWhiteSpace(name) &&
            string.Equals(name, name.Trim(), StringComparison.Ordinal) &&
            !name.Any(static character => character is ':' or '(' or ')');
    }

    private static UnrealBindingCoverageBaseline? NormalizeBaseline(
        UnrealBindingCoverageBaseline? baseline)
    {
        if (baseline is null)
        {
            return null;
        }
        if (baseline.MinimumMemberCount < 0 || baseline.MinimumDirectThunkCount < 0 ||
            baseline.MinimumCallFrameCount < 0 || baseline.MinimumDataPlaneOperationCount < 0 ||
            baseline.MinimumReflectionFallbackCount < 0 || baseline.MaximumRejectedCount < 0)
        {
            throw new JsonException("The Unreal API Contract coverage baseline cannot contain negative counts.");
        }
        return baseline;
    }

    private static bool IsReflectedTypePath(string path) =>
        path.StartsWith("/", StringComparison.Ordinal) &&
        path.Contains('.') && !path.Contains(':') && !path.Any(char.IsWhiteSpace);

    private static bool IsHeaderPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            return false;
        }
        string normalized = value.Trim().Replace('\\', '/');
        return normalized.EndsWith(".h", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Split('/').Any(static segment => segment is "" or "." or "..");
    }
}

public static class UnrealApiContractProjector
{
    public static UnrealApiContractProjection Project(
        UnrealReflectionManifest reflection,
        UnrealApiContract contract)
    {
        UnrealReflectionManifest normalizedReflection =
            UnrealReflectionJson.NormalizeAndValidate(reflection);
        UnrealApiContract normalizedContract = UnrealApiContractJson.Deserialize(
            UnrealApiContractJson.Serialize(contract, writeIndented: false));
        if (normalizedContract.NativeExtensions is not null)
        {
            normalizedReflection = UnrealNativeExtensions.Apply(normalizedReflection, normalizedContract.NativeExtensions);
        }
        IReadOnlyDictionary<string, UnrealReflectedType> reflectedTypes = normalizedReflection.Types
            .ToDictionary(static type => type.Path, StringComparer.Ordinal);
        UnrealApiContractDiscovery? discovery = normalizedContract.Discovery;
        if (discovery is not null)
        {
            foreach (string module in discovery.Modules)
            {
                if (!normalizedReflection.Modules.Contains(module, StringComparer.Ordinal))
                {
                    throw new JsonException(
                        $"Unreal API discovery module '{module}' is absent from reflection input.");
                }
            }
            foreach (UnrealApiContractTypeRule rule in discovery.Types)
            {
                if (!reflectedTypes.ContainsKey(rule.Path))
                {
                    throw new JsonException(
                        $"Unreal API discovery type '{rule.Path}' is absent from reflection input.");
                }
            }
        }

        HashSet<string> excludedTypePaths = discovery?.ExcludedTypePaths
            .ToHashSet(StringComparer.Ordinal) ?? [];
        IReadOnlyDictionary<string, UnrealApiContractType> explicitTypes = normalizedContract.Types
            .ToDictionary(static selector => selector.Path, StringComparer.Ordinal);
        string[] explicitDiscoveryConflicts = explicitTypes.Keys
            .Intersect(excludedTypePaths, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (explicitDiscoveryConflicts.Length != 0)
        {
            throw new JsonException(
                "Unreal API contract explicitly selects discovery-excluded type(s): " +
                string.Join(", ", explicitDiscoveryConflicts));
        }
        foreach (string path in explicitTypes.Keys)
        {
            if (!reflectedTypes.ContainsKey(path))
            {
                throw new JsonException(
                    $"Unreal API contract type '{path}' is absent from reflection input.");
            }
        }
        var selectedTypes = new List<UnrealReflectedType>();
        foreach (UnrealReflectedType type in normalizedReflection.Types)
        {
            explicitTypes.TryGetValue(type.Path, out UnrealApiContractType? explicitSelector);
            UnrealApiContractTypeRule[] typeRules = discovery?.Types
                .Where(rule => rule.Path == type.Path)
                .ToArray() ?? [];
            bool selectedByModule = discovery?.Modules.Contains(
                type.Module,
                StringComparer.Ordinal) == true && !excludedTypePaths.Contains(type.Path);
            if (explicitSelector is null && !selectedByModule && typeRules.Length == 0)
            {
                continue;
            }

            var selectedPaths = new HashSet<string>(StringComparer.Ordinal);
            if (explicitSelector is not null)
            {
                if (explicitSelector.Members.Count == 0)
                {
                    if (type.Kind != UnrealReflectedTypeKind.Enum &&
                        !(normalizedContract.SchemaVersion == UnrealApiContract.CurrentSchemaVersion &&
                          type.Kind is UnrealReflectedTypeKind.Class or UnrealReflectedTypeKind.Struct or UnrealReflectedTypeKind.NativeObject))
                    {
                        throw new JsonException(
                            $"Unreal API contract type '{type.Path}' uses an empty member " +
                            "selector, but only reflected enum types may be selected without members.");
                    }
                }
                else
                {
                    IReadOnlyDictionary<string, (string Kind, object Value)> members =
                        CreateMemberMap(type);
                    foreach (string member in explicitSelector.Members)
                    {
                        string selectedPath = GetSelectedMember(members, member, type.Path);
                        if (typeRules.Any(rule => rule.ExcludeMembers.Any(
                                pattern => MatchesMemberRule(members[member].Value, pattern))))
                        {
                            throw new JsonException(
                                $"Unreal API contract member '{type.Path}:{member}' " +
                                "conflicts with a discovery exclusion rule.");
                        }
                        selectedPaths.Add(selectedPath);
                    }
                }
            }

            if (selectedByModule || typeRules.Length != 0)
            {
                IReadOnlyDictionary<string, (string Kind, object Value)> members =
                    CreateMemberMap(type);
                foreach (string memberPath in DiscoverMembers(type, members, typeRules, selectedByModule))
                {
                    selectedPaths.Add(memberPath);
                }
            }

            if (type.Kind == UnrealReflectedTypeKind.Enum)
            {
                selectedTypes.Add(type with
                {
                    Properties = [],
                    Functions = [],
                    Delegates = [],
                });
                continue;
            }

            selectedTypes.Add(type with
            {
                Properties = type.Properties.Where(property => selectedPaths.Contains(property.Path)).ToArray(),
                Functions = type.Functions.Where(function => selectedPaths.Contains(function.Path)).ToArray(),
                Delegates = type.Delegates.Where(item => selectedPaths.Contains(item.Path)).ToArray(),
            });
        }

        AddDependencyClosure(selectedTypes, reflectedTypes, excludedTypePaths);

        string[] modules = selectedTypes
            .Select(static type => type.Module)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        UnrealReflectionManifest selectedReflection = UnrealReflectionJson.NormalizeAndValidate(
            UnrealReflectionManifest.Create(normalizedReflection.Engine,
                modules.Length == 0 ? normalizedReflection.Modules : modules, selectedTypes));
        UnrealApiPolicyEvaluation evaluation = UnrealApiPolicyEvaluator.Evaluate(
            selectedReflection,
            normalizedContract.Policy);
        UnrealReflectionManifest managedReflection = UnrealApiPolicyEvaluator.Filter(
            selectedReflection,
            evaluation,
            native: false);
        UnrealReflectionManifest nativeReflection = UnrealApiPolicyEvaluator.Filter(
            selectedReflection,
            evaluation,
            native: true);
        ValidateNativeExtensionRoutes(selectedReflection, nativeReflection, evaluation);
        UnrealApiContract effectiveContract = discovery is null
            ? normalizedContract
            : CreateEffectiveContract(
                normalizedContract,
                normalizedReflection,
                selectedTypes,
                explicitTypes,
                excludedTypePaths);
        return new UnrealApiContractProjection(normalizedContract, managedReflection)
        {
            EffectiveContract = effectiveContract,
            SelectedReflection = selectedReflection,
            NativeReflection = nativeReflection,
            PolicyEvaluation = evaluation,
        };
    }

    private static UnrealApiContract CreateEffectiveContract(
        UnrealApiContract contract,
        UnrealReflectionManifest reflection,
        IReadOnlyList<UnrealReflectedType> selectedTypes,
        IReadOnlyDictionary<string, UnrealApiContractType> explicitTypes,
        IReadOnlySet<string> excludedTypePaths)
    {
        var roots = new HashSet<string>(explicitTypes.Keys, StringComparer.Ordinal);
        if (contract.Discovery is not null)
        {
            foreach (UnrealReflectedType type in reflection.Types)
            {
                if (!excludedTypePaths.Contains(type.Path) &&
                    (contract.Discovery.Modules.Contains(type.Module, StringComparer.Ordinal) ||
                     contract.Discovery.Types.Any(rule => rule.Path == type.Path)))
                {
                    roots.Add(type.Path);
                }
            }
        }

        UnrealApiContractType[] types = selectedTypes
            .Where(type => roots.Contains(type.Path))
            .Select(type => CreateResolvedTypeSelector(
                type,
                reflection.Types.Single(candidate => candidate.Path == type.Path)))
            .ToArray();
        if (types.Length == 0)
        {
            throw new JsonException(
                "Unreal API discovery resolved no selected types; check module and type rules.");
        }
        return contract with
        {
            SchemaVersion = UnrealApiContract.CurrentSchemaVersion,
            Types = types,
            Discovery = null,
        };
    }

    private static UnrealApiContractType CreateResolvedTypeSelector(
        UnrealReflectedType type,
        UnrealReflectedType originalType)
    {
        if (type.Kind == UnrealReflectedTypeKind.Enum)
        {
            return new UnrealApiContractType(type.Path, []);
        }

        var selectors = new List<string>(
            type.Properties.Count + type.Functions.Count + type.Delegates.Count);
        selectors.AddRange(type.Properties.Select(static property => property.Name));
        selectors.AddRange(type.Delegates.Select(static item => item.Name));
        IReadOnlyDictionary<string, int> originalOverloadCounts = originalType.Functions
            .GroupBy(static function => function.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        foreach (UnrealReflectedFunction function in type.Functions)
        {
            selectors.Add(originalOverloadCounts[function.Name] == 1
                ? function.Name
                : FunctionSelector(function));
        }
        return new UnrealApiContractType(
            type.Path,
            selectors.Order(StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyDictionary<string, (string Kind, object Value)> CreateMemberMap(
        UnrealReflectedType type)
    {
        var members = new Dictionary<string, (string Kind, object Value)>(StringComparer.Ordinal);
        AddMembers(type.Properties, "property", static value => value.Name, members, type.Path);
        AddFunctionMembers(type.Functions, members, type.Path);
        AddMembers(type.Delegates, "delegate", static value => value.Name, members, type.Path);
        return members;
    }

    private static IReadOnlyList<string> DiscoverMembers(
        UnrealReflectedType type,
        IReadOnlyDictionary<string, (string Kind, object Value)> members,
        IReadOnlyList<UnrealApiContractTypeRule> typeRules,
        bool selectedByModule)
    {
        if (type.Kind == UnrealReflectedTypeKind.Enum)
        {
            return [];
        }

        // A module selector discovers every member unless its type has an
        // explicit rule. A type rule without a module selector discovers only
        // the signatures it names (or all signatures when includes are empty).
        IReadOnlyList<UnrealApiContractTypeRule> rules = typeRules.Count == 0 && selectedByModule
            ? [new UnrealApiContractTypeRule(type.Path)]
            : typeRules;
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnrealApiContractTypeRule rule in rules)
        {
            string[] candidates = rule.IncludeMembers.Count == 0
                ? members.Keys.ToArray()
                : members.Keys.Where(member => rule.IncludeMembers.Any(
                    pattern => MatchesMemberRule(members[member].Value, pattern))).ToArray();
            if (rule.IncludeMembers.Count != 0 && candidates.Length == 0)
            {
                throw new JsonException(
                    $"Unreal API discovery type '{type.Path}' include rules matched no reflected members.");
            }

            foreach (string candidate in candidates)
            {
                if (!rule.ExcludeMembers.Any(pattern => MatchesMemberRule(members[candidate].Value, pattern)))
                {
                    selected.Add(candidate);
                }
            }
        }

        return selected
            .Select(member => members[member].Value switch
            {
                UnrealReflectedProperty property => property.Path,
                UnrealReflectedFunction function => function.Path,
                UnrealReflectedDelegate reflectedDelegate => reflectedDelegate.Path,
                _ => throw new InvalidOperationException("Unknown reflected member kind."),
            })
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool MatchesMemberRule(object member, string pattern) => member switch
    {
        UnrealReflectedFunction function => MatchesSignatureRule(function.Name, pattern) ||
            MatchesSignatureRule(FunctionSelector(function), pattern),
        UnrealReflectedProperty property => MatchesSignatureRule(property.Name, pattern),
        UnrealReflectedDelegate item => MatchesSignatureRule(item.Name, pattern),
        _ => false,
    };

    private static bool MatchesSignatureRule(string value, string pattern)
    {
        if (pattern == "*")
        {
            return true;
        }
        int wildcard = pattern.IndexOf('*');
        if (wildcard < 0)
        {
            return string.Equals(value, pattern, StringComparison.Ordinal);
        }

        int valueIndex = 0;
        int patternIndex = 0;
        int starIndex = -1;
        int retryValueIndex = -1;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && pattern[patternIndex] == value[valueIndex])
            {
                valueIndex++;
                patternIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryValueIndex = valueIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++retryValueIndex;
            }
            else
            {
                return false;
            }
        }
        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }
        return patternIndex == pattern.Length;
    }

    private static void ValidateNativeExtensionRoutes(
        UnrealReflectionManifest selected,
        UnrealReflectionManifest native,
        UnrealApiPolicyEvaluation evaluation)
    {
        HashSet<string> paths = selected.Types.SelectMany(static type =>
            type.Properties.Where(static field => field.NativeExtension is not null).Select(static field => field.Path)
                .Concat(type.Functions.Where(static method => method.NativeBinding is not null).Select(static method => method.Path)))
            .Where(path => evaluation.For(path).Action == UnrealApiPolicyAction.Allow)
            .ToHashSet(StringComparer.Ordinal);
        if (paths.Count == 0) return;
        foreach (string path in paths)
        {
            if (evaluation.For(path).Route is UnrealBindingRoute.ReflectionFallback or UnrealBindingRoute.DataPlaneOperation ||
                path.StartsWith("/Native/", StringComparison.Ordinal) && evaluation.For(path).Route == UnrealBindingRoute.CallFrame)
            {
                throw new JsonException($"USUE5405: Native-only member '{path}' requires a generated native route, not reflection or a data-plane substitution.");
            }
        }
        UnrealNativeThunkDiagnostic? unsupported = new UnrealNativeThunkGenerator().Generate(native).Diagnostics
            .FirstOrDefault(diagnostic => paths.Contains(diagnostic.MemberPath));
        if (unsupported is not null)
        {
            throw new JsonException($"USUE5405: Native-only member '{unsupported.MemberPath}' cannot be generated: {unsupported.Message}");
        }
    }

    private static string GetSelectedMember(
        IReadOnlyDictionary<string, (string Kind, object Value)> members,
        string selector,
        string ownerPath)
    {
        if (!members.TryGetValue(selector, out (string Kind, object Value) selected))
        {
            throw new JsonException(
                $"Unreal API contract member '{ownerPath}:{selector}' is absent from reflection input.");
        }
        return selected.Value switch
        {
            UnrealReflectedProperty property => property.Path,
            UnrealReflectedFunction function => function.Path,
            UnrealReflectedDelegate reflectedDelegate => reflectedDelegate.Path,
            _ => throw new InvalidOperationException("Unknown reflected member kind."),
        };
    }

    private static void AddFunctionMembers(
        IReadOnlyList<UnrealReflectedFunction> functions,
        IDictionary<string, (string Kind, object Value)> members,
        string ownerPath)
    {
        foreach (IGrouping<string, UnrealReflectedFunction> group in functions.GroupBy(
                     static function => function.Name,
                     StringComparer.Ordinal))
        {
            UnrealReflectedFunction[] overloads = group.ToArray();
            if (overloads.Length == 1)
            {
                members.Add(group.Key, ("function", overloads[0]));
                continue;
            }
            foreach (UnrealReflectedFunction function in overloads)
            {
                string selector = FunctionSelector(function);
                if (!members.TryAdd(selector, ("function", function)))
                {
                    throw new JsonException(
                        $"Reflected type '{ownerPath}' has an ambiguous overload selector '{selector}'.");
                }
            }
        }
    }

    internal static string FunctionSelector(UnrealReflectedFunction function)
    {
        int open = function.Path.IndexOf('(', StringComparison.Ordinal);
        return open >= 0 ? function.Path[(function.Path.IndexOf(':') + 1)..] :
            $"{function.Name}({string.Join(',', function.Parameters.Select(static parameter => parameter.Type.NativeName))})";
    }

    private static void AddDependencyClosure(
        ICollection<UnrealReflectedType> selectedTypes,
        IReadOnlyDictionary<string, UnrealReflectedType> reflectedTypes,
        IReadOnlySet<string> excludedTypePaths)
    {
        var selectedPaths = selectedTypes.Select(static type => type.Path)
            .ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<string>(selectedTypes.SelectMany(EnumerateDependencies));
        while (pending.TryDequeue(out string? path))
        {
            if (excludedTypePaths.Contains(path))
            {
                throw new JsonException(
                    $"Unreal API discovery excluded dependency type '{path}' required by the selected surface.");
            }
            if (!selectedPaths.Add(path) || !reflectedTypes.TryGetValue(path, out UnrealReflectedType? dependency))
            {
                continue;
            }
            selectedTypes.Add(dependency.Kind == UnrealReflectedTypeKind.NativeObject
                ? dependency with { Properties = [], Functions = [] } : dependency);
            if (dependency.Kind == UnrealReflectedTypeKind.NativeObject)
            {
                if (dependency.BaseTypePath is not null) pending.Enqueue(dependency.BaseTypePath);
                continue;
            }
            foreach (string nested in EnumerateDependencies(dependency))
            {
                pending.Enqueue(nested);
            }
        }
    }

    private static IEnumerable<string> EnumerateDependencies(UnrealReflectedType type)
    {
        if (type.BaseTypePath is not null)
        {
            yield return type.BaseTypePath;
        }
        foreach (string path in type.InterfacePaths)
        {
            yield return path;
        }
        IEnumerable<UnrealTypeReference> references = type.Properties.Select(static item => item.Type)
            .Concat(type.Functions.Select(static item => item.ReturnType))
            .Concat(type.Functions.SelectMany(static item => item.Parameters.Select(static parameter => parameter.Type)))
            .Concat(type.Delegates.Select(static item => item.ReturnType))
            .Concat(type.Delegates.SelectMany(static item => item.Parameters.Select(static parameter => parameter.Type)));
        foreach (UnrealTypeReference reference in references.SelectMany(EnumerateTypeReferences))
        {
            if (reference.TypePath is not null && reference.Kind is UnrealValueKind.Struct or UnrealValueKind.Enum or UnrealValueKind.NativeObject or UnrealValueKind.Interface)
            {
                yield return reference.TypePath;
            }
        }
    }

    private static IEnumerable<UnrealTypeReference> EnumerateTypeReferences(UnrealTypeReference type)
    {
        yield return type;
        if (type.ElementType is not null)
        {
            foreach (UnrealTypeReference nested in EnumerateTypeReferences(type.ElementType)) yield return nested;
        }
        if (type.KeyType is not null)
        {
            foreach (UnrealTypeReference nested in EnumerateTypeReferences(type.KeyType)) yield return nested;
        }
        if (type.ValueType is not null)
        {
            foreach (UnrealTypeReference nested in EnumerateTypeReferences(type.ValueType)) yield return nested;
        }
        if (type.CallbackSignature is { } signature)
        {
            foreach (var nested in EnumerateTypeReferences(signature.ReturnType)) yield return nested;
            foreach (var parameter in signature.Parameters)
                foreach (var nested in EnumerateTypeReferences(parameter.Type)) yield return nested;
        }
    }

    private static void AddMembers<T>(
        IEnumerable<T> values,
        string kind,
        Func<T, string> getName,
        IDictionary<string, (string Kind, object Value)> members,
        string ownerPath)
        where T : notnull
    {
        foreach (T value in values)
        {
            string name = getName(value);
            if (!members.TryAdd(name, (kind, value)))
            {
                throw new JsonException(
                    $"Reflected type '{ownerPath}' has an ambiguous member name '{name}'.");
            }
        }
    }
}

public sealed record UnrealApiNodePin(
    string Name,
    string Direction,
    string Kind,
    UnrealTypeReference? Type)
{
    public string? DefaultValue { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UnrealNativeParameterPassing? NativePassing { get; init; }
}

public enum UnrealApiThreadRequirement
{
    GameThread,
    AnyThread,
}

public enum UnrealApiWorldRequirement
{
    None,
    WorldContext,
}

public sealed record UnrealApiNodeDescriptor(
    string Id,
    string MemberPath,
    string MemberId,
    string Kind,
    string Title,
    string Category,
    string Tooltip,
    bool Pure,
    IReadOnlyList<UnrealApiNodePin> Pins)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeExtension { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UnrealNativeOperation? NativeOperation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UnrealNativeReturnOwnership? ReturnOwnership { get; init; }

    public bool Deprecated { get; init; }

    public string? DeprecationMessage { get; init; }

    public bool Latent { get; init; }

    public string? LatentInfoParameter { get; init; }

    public UnrealApiThreadRequirement ThreadRequirement { get; init; }

    public UnrealApiWorldRequirement WorldRequirement { get; init; }

    public string? WorldContextParameter { get; init; }

    public bool CallableWithoutWorldContext { get; init; }
}

public sealed record UnrealApiNodeType(
    string Path,
    string Name,
    string ManagedTypeName,
    string Category,
    IReadOnlyList<UnrealReflectedProperty> Properties,
    IReadOnlyList<UnrealReflectedFunction> Functions,
    IReadOnlyList<UnrealReflectedDelegate> Delegates,
    IReadOnlyList<UnrealApiNodeDescriptor> Nodes);

public sealed record UnrealApiNodeCatalog(
    string SchemaVersion,
    string ContractName,
    string ManagedNamespace,
    UnrealEngineIdentity Engine,
    IReadOnlyList<UnrealApiNodeType> Types)
{
    public const string CurrentSchemaVersion = "unifiedscript.unreal-api-node-catalog/v0.2";
}

public static class UnrealApiNodeCatalogGenerator
{
    public static UnrealApiNodeCatalog Generate(UnrealApiContractProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        IReadOnlyDictionary<string, string> managedNames = UnrealBindingGenerator.CreateManagedTypeNames(
            projection.Reflection.Types);
        UnrealApiNodeType[] types = projection.Reflection.Types.Select(type =>
        {
            string managedName = managedNames[type.Path];
            string category = type.Metadata.Category ?? $"Unreal API|{type.Name}";
            var nodes = new List<UnrealApiNodeDescriptor>();
            UnrealTypeReference receiverType = new(
                type.Kind == UnrealReflectedTypeKind.NativeObject ? UnrealValueKind.NativeObject : UnrealValueKind.Object,
                type.NativeName + "*",
                type.Path);
            if (type.Kind == UnrealReflectedTypeKind.Struct)
            {
                UnrealTypeReference structType = new(
                    UnrealValueKind.Struct,
                    type.NativeName,
                    type.Path);
                nodes.Add(CreateNode(
                    $"ue.struct.make|{managedName}",
                    type.Path + ":__Make",
                    "structMake",
                    $"Make {type.Name}",
                    category + "|Structs",
                    $"Construct {type.Name} from reflected fields.",
                    pure: true,
                    [
                        .. type.Properties.Select(property => new UnrealApiNodePin(
                            property.Name,
                            "input",
                            "data",
                            property.Type)),
                        new("Value", "output", "data", structType),
                    ]));
                nodes.Add(CreateNode(
                    $"ue.struct.break|{managedName}",
                    type.Path + ":__Break",
                    "structBreak",
                    $"Break {type.Name}",
                    category + "|Structs",
                    $"Expose the reflected fields of {type.Name}.",
                    pure: true,
                    [
                        new("Value", "input", "data", structType),
                        .. type.Properties.Select(property => new UnrealApiNodePin(
                            property.Name,
                            "output",
                            "data",
                            property.Type)),
                    ]));
            }
            foreach (UnrealReflectedProperty property in type.Properties)
            {
                if (type.Kind == UnrealReflectedTypeKind.Struct)
                {
                    continue;
                }
                if (property.Access.Readable)
                {
                    nodes.Add(CreateNode(
                        $"ue.get|{managedName}|{property.Name}",
                        property.Path,
                        "propertyRead",
                        property.Metadata.DisplayName ?? property.Name,
                        category + "|Properties",
                        property.Metadata.Tooltip ?? $"Read {type.Name}.{property.Name}.",
                        pure: true,
                        [
                            new("Target", "input", "data", receiverType),
                            new("Value", "output", "data", property.Type),
                        ],
                        property.Metadata));
                }
                if (property.Access.Writable)
                {
                    nodes.Add(CreateNode(
                        $"ue.set|{managedName}|{property.Name}",
                        property.Path,
                        "propertyWrite",
                        $"Set {property.Metadata.DisplayName ?? property.Name}",
                        category + "|Properties",
                        property.Metadata.Tooltip ?? $"Write {type.Name}.{property.Name}.",
                        pure: false,
                        [
                            new("In", "input", "control", null),
                            new("Target", "input", "data", receiverType),
                            new("Value", "input", "data", property.Type),
                            new("Out", "output", "control", null),
                        ],
                        property.Metadata));
                }
            }
            foreach (UnrealReflectedFunction function in type.Functions)
            {
                string managedFunctionName = UnrealManagedNaming.FunctionName(function, type);
                (int FirstDefault, string?[] Expressions)? projectedDefaults =
                    UnrealBindingGenerator.GetProjectableDefaultExpressions(function, managedNames);
                bool overloaded = type.Functions.Count(candidate =>
                    string.Equals(
                        UnrealManagedNaming.FunctionName(candidate, type),
                        managedFunctionName,
                        StringComparison.Ordinal)) > 1;
                var pins = new List<UnrealApiNodePin>();
                if (!function.Traits.Pure)
                {
                    pins.Add(new("In", "input", "control", null));
                }
                pins.Add(new(
                    function.Traits.Static ? "Context" : "Target",
                    "input",
                    "data",
                    function.Traits.Static
                        ? type.Kind == UnrealReflectedTypeKind.NativeObject
                            ? new UnrealTypeReference(UnrealValueKind.InvocationContext, "Finare.Unreal.IUnrealInvocationBridge")
                            : new UnrealTypeReference(
                            UnrealValueKind.Object,
                            "UObject*",
                            "/Script/CoreUObject.Object")
                        : receiverType));
                for (int parameterIndex = 0; parameterIndex < function.Parameters.Count; parameterIndex++)
                {
                    UnrealReflectedParameter parameter = function.Parameters[parameterIndex];
                    if (parameter.Direction != UnrealParameterDirection.Output)
                    {
                        pins.Add(new(parameter.Name, "input", "data", parameter.Type)
                        {
                            DefaultValue = projectedDefaults is { } defaults &&
                                parameterIndex >= defaults.FirstDefault &&
                                defaults.Expressions[parameterIndex] is not null
                                ? parameter.DefaultValue
                                : null,
                            NativePassing = parameter.Type.Kind == UnrealValueKind.NativeObject
                                ? function.NativeBinding?.Parameters[parameterIndex] : null,
                        });
                    }
                    if (parameter.Direction != UnrealParameterDirection.Input)
                    {
                        pins.Add(new(parameter.Name, "output", "data", parameter.Type));
                    }
                }
                if (function.ReturnType.Kind != UnrealValueKind.Void)
                {
                    pins.Add(new("ReturnValue", "output", "data", function.ReturnType));
                }
                if (!function.Traits.Pure)
                {
                    pins.Add(new("Out", "output", "control", null));
                }
                nodes.Add(CreateNode(
                    overloaded
                        ? $"ue.call|{managedName}|{managedFunctionName}|{UnrealMemberId.FromPath(function.Path):X16}"
                        : $"ue.call|{managedName}|{managedFunctionName}",
                    function.Path,
                    "function",
                    function.Metadata.DisplayName ?? function.Name,
                    function.Metadata.Category ?? category,
                    (function.Metadata.Tooltip ?? $"Call {type.Name}.{function.Name}(...).") +
                        string.Concat(function.Parameters.Select((p, i) => p.Type.Kind == UnrealValueKind.NativeObject
                            ? $"\n{p.Name}: {UnrealNativeObjectBindings.ParameterCppType(p.Type, function.NativeBinding!.Parameters[i])}; " +
                              $"{(UnrealNativeObjectBindings.AllowsNull(function.NativeBinding.Parameters[i]) ? "nullable" : "non-null")}, caller-owned, synchronous use only."
                            : "")),
                    function.Traits.Pure,
                    pins,
                    function.Metadata));
            }
            foreach (UnrealReflectedDelegate reflectedDelegate in type.Delegates)
            {
                var pins = new List<UnrealApiNodePin>
                {
                    new("Then", "output", "control", null),
                };
                pins.AddRange(reflectedDelegate.Parameters.Select(parameter => new UnrealApiNodePin(
                    parameter.Name,
                    "output",
                    "data",
                    parameter.Type)));
                nodes.Add(CreateNode(
                    $"ue.event|{managedName}|{reflectedDelegate.Name}",
                    reflectedDelegate.Path,
                    "event",
                    $"Event {reflectedDelegate.Metadata.DisplayName ?? reflectedDelegate.Name}",
                    reflectedDelegate.Metadata.Category ?? $"Unreal Events|{type.Name}",
                    reflectedDelegate.Metadata.Tooltip ??
                        $"Handle {type.Name}.{reflectedDelegate.Name}.",
                    pure: false,
                    pins,
                    reflectedDelegate.Metadata));
            }
            return new UnrealApiNodeType(
                type.Path,
                type.Name,
                managedName,
                category,
                type.Properties,
                type.Functions,
                type.Delegates,
                nodes.Select(node => node with
                {
                    NativeExtension = type.Properties.FirstOrDefault(field => field.Path == node.MemberPath)?.NativeExtension
                        ?? type.Functions.FirstOrDefault(method => method.Path == node.MemberPath)?.NativeBinding?.Extension,
                    NativeOperation = type.Kind == UnrealReflectedTypeKind.NativeObject
                        ? type.Functions.FirstOrDefault(method => method.Path == node.MemberPath)?.NativeBinding?.Operation : null,
                    ReturnOwnership = type.Kind == UnrealReflectedTypeKind.NativeObject
                        ? type.Functions.FirstOrDefault(method => method.Path == node.MemberPath)?.NativeBinding?.ReturnOwnership : null,
                }).OrderBy(static node => node.Id, StringComparer.Ordinal).ToArray());
        }).ToArray();
        return new UnrealApiNodeCatalog(
            UnrealApiNodeCatalog.CurrentSchemaVersion,
            projection.Contract.ContractName,
            projection.Contract.ManagedNamespace,
            projection.Reflection.Engine,
            types);
    }

    public static string Serialize(UnrealApiNodeCatalog catalog, bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SchemaVersion != UnrealApiNodeCatalog.CurrentSchemaVersion)
        {
            throw new ArgumentException("Unsupported Unreal API node catalog schema.", nameof(catalog));
        }
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return JsonSerializer.Serialize(catalog, options).ReplaceLineEndings("\n") + "\n";
    }

    private static UnrealApiNodeDescriptor CreateNode(
        string id,
        string path,
        string kind,
        string title,
        string category,
        string tooltip,
        bool pure,
        IReadOnlyList<UnrealApiNodePin> pins,
        UnrealBlueprintMetadata? metadata = null) =>
        new(
            id,
            path,
            $"0x{UnrealMemberId.FromPath(path):X16}",
            kind,
            title,
            category,
            tooltip,
            pure,
            pins)
        {
            Deprecated = metadata?.Deprecated ?? false,
            DeprecationMessage = metadata?.DeprecationMessage,
            Latent = metadata?.Latent ?? false,
            LatentInfoParameter = metadata?.LatentInfoParameter,
            ThreadRequirement = metadata?.ThreadSafe == true
                ? UnrealApiThreadRequirement.AnyThread
                : UnrealApiThreadRequirement.GameThread,
            WorldRequirement = metadata?.WorldContextParameter is not null &&
                metadata.CallableWithoutWorldContext != true
                ? UnrealApiWorldRequirement.WorldContext
                : UnrealApiWorldRequirement.None,
            WorldContextParameter = metadata?.WorldContextParameter,
            CallableWithoutWorldContext = metadata?.CallableWithoutWorldContext ?? false,
        };

    private static IReadOnlyDictionary<string, string> CreateManagedTypeNames(
        IReadOnlyList<UnrealReflectedType> types)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnrealReflectedType type in types)
        {
            string candidate = ToIdentifier(type.Name);
            string name = used.Add(candidate) ? candidate : AddStableSuffix(candidate, type.Path, used);
            result.Add(type.Path, name);
        }
        return result;
    }

    private static string ToIdentifier(string value)
    {
        var identifier = new StringBuilder(value.Length + 1);
        if (value.Length == 0 || !char.IsLetter(value[0]) && value[0] != '_')
        {
            identifier.Append('_');
        }
        foreach (char character in value)
        {
            identifier.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }
        return identifier.ToString();
    }

    private static string AddStableSuffix(string candidate, string path, ISet<string> used)
    {
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..8];
        string unique = $"{candidate}_{suffix}";
        if (!used.Add(unique))
        {
            throw new InvalidOperationException($"Stable API node type-name collision for '{path}'.");
        }
        return unique;
    }
}
