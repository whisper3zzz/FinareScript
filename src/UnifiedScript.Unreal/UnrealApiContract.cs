using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedScript.Core;

namespace UnifiedScript.Unreal;

public sealed record UnrealApiContractType(
    string Path,
    IReadOnlyList<string> Members);

/// <summary>
/// Project-owned allowlist selecting the reflected Unreal surface that is shipped to managed
/// code. The same normalized selection drives managed wrappers, native thunks, coverage, and
/// visual node metadata so those artifacts cannot drift independently.
/// </summary>
public sealed record UnrealApiContract(
    string SchemaVersion,
    string ContractName,
    string ManagedNamespace,
    string NativeHeader,
    string NativeNamespace,
    IReadOnlyList<UnrealApiContractType> Types)
{
    public const string CurrentSchemaVersion = "unifiedscript.unreal-api-contract/v0.1";
}

public sealed record UnrealApiContractProjection(
    UnrealApiContract Contract,
    UnrealReflectionManifest Reflection);

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
        if (contract.SchemaVersion != UnrealApiContract.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported Unreal API contract schema '{contract.SchemaVersion}'. " +
                $"Expected '{UnrealApiContract.CurrentSchemaVersion}'.");
        }
        if (!IsContractName(contract.ContractName) ||
            !IsQualifiedIdentifier(contract.ManagedNamespace, '.') ||
            !IsQualifiedIdentifier(contract.NativeNamespace, ':') ||
            !IsHeaderPath(contract.NativeHeader) ||
            contract.Types is null || contract.Types.Count == 0)
        {
            throw new JsonException("The Unreal API contract has invalid required fields.");
        }

        var typePaths = new HashSet<string>(StringComparer.Ordinal);
        var normalizedTypes = new List<UnrealApiContractType>(contract.Types.Count);
        foreach (UnrealApiContractType? type in contract.Types)
        {
            if (type is null || !IsScriptPath(type.Path) || type.Members is null ||
                type.Members.Count == 0 || !typePaths.Add(type.Path))
            {
                throw new JsonException("The Unreal API contract contains an invalid type selector.");
            }
            string[] members = type.Members
                .Select(static member => member?.Trim() ?? string.Empty)
                .ToArray();
            if (members.Any(static member => !IsIdentifier(member)) ||
                members.Distinct(StringComparer.Ordinal).Count() != members.Length)
            {
                throw new JsonException(
                    $"Unreal API contract type '{type.Path}' has invalid or duplicate members.");
            }
            normalizedTypes.Add(new UnrealApiContractType(
                type.Path,
                members.Order(StringComparer.Ordinal).ToArray()));
        }

        return contract with
        {
            ContractName = contract.ContractName.Trim(),
            ManagedNamespace = contract.ManagedNamespace.Trim(),
            NativeHeader = contract.NativeHeader.Trim().Replace('\\', '/'),
            NativeNamespace = contract.NativeNamespace.Trim(),
            Types = normalizedTypes.OrderBy(static type => type.Path, StringComparer.Ordinal).ToArray(),
        };
    }

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

    private static bool IsScriptPath(string path) =>
        path.StartsWith("/Script/", StringComparison.Ordinal) &&
        path.Contains('.') && !path.Any(char.IsWhiteSpace);

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
        IReadOnlyDictionary<string, UnrealReflectedType> reflectedTypes = normalizedReflection.Types
            .ToDictionary(static type => type.Path, StringComparer.Ordinal);
        var selectedTypes = new List<UnrealReflectedType>(normalizedContract.Types.Count);

        foreach (UnrealApiContractType selector in normalizedContract.Types)
        {
            if (!reflectedTypes.TryGetValue(selector.Path, out UnrealReflectedType? type))
            {
                throw new JsonException(
                    $"Unreal API contract type '{selector.Path}' is absent from reflection input.");
            }

            var members = new Dictionary<string, (string Kind, object Value)>(StringComparer.Ordinal);
            AddMembers(type.Properties, "property", static value => value.Name, members, type.Path);
            AddMembers(type.Functions, "function", static value => value.Name, members, type.Path);
            AddMembers(type.Delegates, "delegate", static value => value.Name, members, type.Path);
            foreach (string member in selector.Members)
            {
                if (!members.ContainsKey(member))
                {
                    throw new JsonException(
                        $"Unreal API contract member '{selector.Path}:{member}' is absent from reflection input.");
                }
            }
            var selected = selector.Members.ToHashSet(StringComparer.Ordinal);
            selectedTypes.Add(type with
            {
                Properties = type.Properties.Where(property => selected.Contains(property.Name)).ToArray(),
                Functions = type.Functions.Where(function => selected.Contains(function.Name)).ToArray(),
                Delegates = type.Delegates.Where(item => selected.Contains(item.Name)).ToArray(),
            });
        }

        string[] modules = selectedTypes
            .Select(static type => type.Module)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        UnrealReflectionManifest selectedReflection = UnrealReflectionJson.NormalizeAndValidate(
            UnrealReflectionManifest.Create(normalizedReflection.Engine, modules, selectedTypes));
        return new UnrealApiContractProjection(normalizedContract, selectedReflection);
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
    UnrealTypeReference? Type);

public sealed record UnrealApiNodeDescriptor(
    string Id,
    string MemberPath,
    string MemberId,
    string Kind,
    string Title,
    string Category,
    string Tooltip,
    bool Pure,
    IReadOnlyList<UnrealApiNodePin> Pins);

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
    public const string CurrentSchemaVersion = "unifiedscript.unreal-api-node-catalog/v0.1";
}

public static class UnrealApiNodeCatalogGenerator
{
    public static UnrealApiNodeCatalog Generate(UnrealApiContractProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        IReadOnlyDictionary<string, string> managedNames = CreateManagedTypeNames(
            projection.Reflection.Types);
        UnrealApiNodeType[] types = projection.Reflection.Types.Select(type =>
        {
            string managedName = managedNames[type.Path];
            string category = $"Unreal API|{type.Name}";
            var nodes = new List<UnrealApiNodeDescriptor>();
            UnrealTypeReference receiverType = new(
                UnrealValueKind.Object,
                type.NativeName + "*",
                type.Path);
            foreach (UnrealReflectedProperty property in type.Properties)
            {
                if (property.Access.Readable)
                {
                    nodes.Add(CreateNode(
                        $"ue.get|{managedName}|{property.Name}",
                        property.Path,
                        "propertyRead",
                        property.Name,
                        category + "|Properties",
                        $"Read {type.Name}.{property.Name}.",
                        pure: true,
                        [
                            new("Target", "input", "data", receiverType),
                            new("Value", "output", "data", property.Type),
                        ]));
                }
                if (property.Access.Writable)
                {
                    nodes.Add(CreateNode(
                        $"ue.set|{managedName}|{property.Name}",
                        property.Path,
                        "propertyWrite",
                        $"Set {property.Name}",
                        category + "|Properties",
                        $"Write {type.Name}.{property.Name}.",
                        pure: false,
                        [
                            new("In", "input", "control", null),
                            new("Target", "input", "data", receiverType),
                            new("Value", "input", "data", property.Type),
                            new("Out", "output", "control", null),
                        ]));
                }
            }
            foreach (UnrealReflectedFunction function in type.Functions)
            {
                var pins = new List<UnrealApiNodePin>();
                if (!function.Traits.Pure)
                {
                    pins.Add(new("In", "input", "control", null));
                }
                pins.Add(new("Target", "input", "data", receiverType));
                foreach (UnrealReflectedParameter parameter in function.Parameters)
                {
                    if (parameter.Direction != UnrealParameterDirection.Output)
                    {
                        pins.Add(new(parameter.Name, "input", "data", parameter.Type));
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
                    $"ue.call|{managedName}|{function.Name}",
                    function.Path,
                    "function",
                    function.Name,
                    category,
                    $"Call {type.Name}.{function.Name}(...).",
                    function.Traits.Pure,
                    pins));
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
                    $"Event {reflectedDelegate.Name}",
                    $"Unreal Events|{type.Name}",
                    $"Handle {type.Name}.{reflectedDelegate.Name}.",
                    pure: false,
                    pins));
            }
            return new UnrealApiNodeType(
                type.Path,
                type.Name,
                managedName,
                category,
                type.Properties,
                type.Functions,
                type.Delegates,
                nodes.OrderBy(static node => node.Id, StringComparer.Ordinal).ToArray());
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
        IReadOnlyList<UnrealApiNodePin> pins) =>
        new(
            id,
            path,
            $"0x{UnrealMemberId.FromPath(path):X16}",
            kind,
            title,
            category,
            tooltip,
            pure,
            pins);

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
