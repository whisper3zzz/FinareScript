using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedScript.Core;

namespace UnifiedScript.Unreal;

public sealed record UnrealApiGeneratedSurfaceLockType(
    string TypePath,
    string Module,
    UnrealReflectedTypeKind Kind,
    string TypeFingerprint,
    IReadOnlyList<string> MemberPaths);

public sealed record UnrealApiGeneratedSurfaceLock(
    string SchemaVersion,
    string ProjectName,
    string ManagedNamespace,
    UnrealEngineIdentity Engine,
    string BaseContractFingerprint,
    string EffectiveContractFingerprint,
    IReadOnlyList<UnrealApiGeneratedSurfaceLockType> Types,
    string Fingerprint)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.unreal-api-generated-surface-lock/v0.1";
}

public static class UnrealApiGeneratedSurfaceLockJson
{
    public static string Serialize(
        UnrealApiGeneratedSurfaceLock surfaceLock,
        bool writeIndented = true) =>
        JsonSerializer.Serialize(Normalize(surfaceLock, verifyFingerprint: true),
                CreateOptions(writeIndented))
            .ReplaceLineEndings("\n") + "\n";

    public static UnrealApiGeneratedSurfaceLock Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        UnrealApiGeneratedSurfaceLock? surfaceLock =
            JsonSerializer.Deserialize<UnrealApiGeneratedSurfaceLock>(
                json,
                CreateOptions(writeIndented: true));
        return Normalize(surfaceLock ?? throw new JsonException(
            "The Unreal generated surface lock was empty."), verifyFingerprint: true);
    }

    public static UnrealApiGeneratedSurfaceLock Create(
        string projectName,
        UnrealReflectionManifest reflection,
        UnrealApiDevelopmentSurfaceResult surface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(surface);
        if (!surface.Applied)
        {
            throw new InvalidDataException(
                "An Unreal generated surface lock requires an applied development surface.");
        }

        UnrealReflectionManifest normalizedReflection =
            UnrealReflectionJson.NormalizeAndValidate(reflection);
        string reflectionFingerprint = Sha256(
            UnrealReflectionJson.Serialize(normalizedReflection, writeIndented: false));
        if (surface.ReflectionFingerprint != reflectionFingerprint)
        {
            throw new InvalidDataException(
                "The development surface and reflection manifest have different fingerprints.");
        }

        UnrealApiContractProjection projection = UnrealApiContractProjector.Project(
            normalizedReflection,
            surface.EffectiveContract);
        UnrealApiGeneratedSurfaceLockType[] types = projection.SelectedReflection.Types
            .Select(type => new UnrealApiGeneratedSurfaceLockType(
                type.Path,
                type.Module,
                type.Kind,
                FingerprintType(type),
                type.Properties.Select(static member => member.Path)
                    .Concat(type.Functions.Select(static member => member.Path))
                    .Concat(type.Delegates.Select(static member => member.Path))
                    .Order(StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(static type => type.TypePath, StringComparer.Ordinal)
            .ToArray();
        var candidate = new UnrealApiGeneratedSurfaceLock(
            UnrealApiGeneratedSurfaceLock.CurrentSchemaVersion,
            projectName,
            surface.ManagedNamespace,
            normalizedReflection.Engine,
            surface.BaseContractFingerprint,
            surface.EffectiveContractFingerprint,
            types,
            string.Empty);
        return Normalize(candidate, verifyFingerprint: false);
    }

    public static IReadOnlyList<string> Diff(
        UnrealApiGeneratedSurfaceLock expected,
        UnrealApiGeneratedSurfaceLock candidate)
    {
        UnrealApiGeneratedSurfaceLock left = Normalize(expected, verifyFingerprint: true);
        UnrealApiGeneratedSurfaceLock right = Normalize(candidate, verifyFingerprint: true);
        var differences = new List<string>();
        if (left.ProjectName != right.ProjectName)
        {
            differences.Add($"project:{left.ProjectName}->{right.ProjectName}");
        }
        if (left.ManagedNamespace != right.ManagedNamespace)
        {
            differences.Add($"namespace:{left.ManagedNamespace}->{right.ManagedNamespace}");
        }
        if (left.Engine != right.Engine)
        {
            differences.Add("engine");
        }
        if (left.BaseContractFingerprint != right.BaseContractFingerprint)
        {
            differences.Add("base-contract");
        }
        if (left.EffectiveContractFingerprint != right.EffectiveContractFingerprint)
        {
            differences.Add("effective-contract");
        }

        IReadOnlyDictionary<string, UnrealApiGeneratedSurfaceLockType> leftTypes =
            left.Types.ToDictionary(static type => type.TypePath, StringComparer.Ordinal);
        IReadOnlyDictionary<string, UnrealApiGeneratedSurfaceLockType> rightTypes =
            right.Types.ToDictionary(static type => type.TypePath, StringComparer.Ordinal);
        differences.AddRange(rightTypes.Keys.Except(leftTypes.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(static path => $"added:{path}"));
        differences.AddRange(leftTypes.Keys.Except(rightTypes.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(static path => $"removed:{path}"));
        differences.AddRange(leftTypes.Keys.Intersect(rightTypes.Keys, StringComparer.Ordinal)
            .Where(path => !Equivalent(leftTypes[path], rightTypes[path]))
            .Order(StringComparer.Ordinal)
            .Select(static path => $"changed:{path}"));
        return differences;
    }

    private static bool Equivalent(
        UnrealApiGeneratedSurfaceLockType left,
        UnrealApiGeneratedSurfaceLockType right) =>
        left.TypePath == right.TypePath && left.Module == right.Module &&
        left.Kind == right.Kind && left.TypeFingerprint == right.TypeFingerprint &&
        left.MemberPaths.SequenceEqual(right.MemberPaths, StringComparer.Ordinal);

    private static UnrealApiGeneratedSurfaceLock Normalize(
        UnrealApiGeneratedSurfaceLock surfaceLock,
        bool verifyFingerprint)
    {
        ArgumentNullException.ThrowIfNull(surfaceLock);
        if (surfaceLock.SchemaVersion != UnrealApiGeneratedSurfaceLock.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(surfaceLock.ProjectName) ||
            !IsQualifiedIdentifier(surfaceLock.ManagedNamespace) ||
            surfaceLock.Engine is null || surfaceLock.Engine.Major < 5 ||
            surfaceLock.Engine.Minor < 0 || surfaceLock.Engine.Patch < 0 ||
            string.IsNullOrWhiteSpace(surfaceLock.Engine.Branch) ||
            !IsSha256(surfaceLock.BaseContractFingerprint) ||
            !IsSha256(surfaceLock.EffectiveContractFingerprint) ||
            surfaceLock.Types is null ||
            verifyFingerprint && !IsSha256(surfaceLock.Fingerprint))
        {
            throw new JsonException("The Unreal generated surface lock has invalid required fields.");
        }

        var typePaths = new HashSet<string>(StringComparer.Ordinal);
        UnrealApiGeneratedSurfaceLockType[] types = surfaceLock.Types.Select(type =>
        {
            if (type is null || !IsReflectedTypePath(type.TypePath) ||
                !IsIdentifier(type.Module) || !Enum.IsDefined(type.Kind) ||
                !IsSha256(type.TypeFingerprint) || type.MemberPaths is null ||
                !typePaths.Add(type.TypePath))
            {
                throw new JsonException("The Unreal generated surface lock contains an invalid type.");
            }
            string[] members = type.MemberPaths
                .Select(static path => path?.Trim() ?? string.Empty)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (members.Any(path => !path.StartsWith(type.TypePath + ":", StringComparison.Ordinal)) ||
                members.Distinct(StringComparer.Ordinal).Count() != members.Length)
            {
                throw new JsonException(
                    $"The Unreal generated surface lock type '{type.TypePath}' has invalid members.");
            }
            return type with
            {
                TypePath = type.TypePath.Trim(),
                Module = type.Module.Trim(),
                MemberPaths = members,
            };
        }).OrderBy(static type => type.TypePath, StringComparer.Ordinal).ToArray();
        UnrealApiGeneratedSurfaceLock normalized = surfaceLock with
        {
            ProjectName = surfaceLock.ProjectName.Trim(),
            ManagedNamespace = surfaceLock.ManagedNamespace.Trim(),
            Engine = surfaceLock.Engine with { Branch = surfaceLock.Engine.Branch.Trim() },
            Types = types,
            Fingerprint = string.Empty,
        };
        string fingerprint = ComputeFingerprint(normalized);
        if (verifyFingerprint && surfaceLock.Fingerprint != fingerprint)
        {
            throw new JsonException("The Unreal generated surface lock fingerprint is stale.");
        }
        return normalized with { Fingerprint = fingerprint };
    }

    private static string FingerprintType(UnrealReflectedType type) => Sha256(
        JsonSerializer.Serialize(type, CreateOptions(writeIndented: false)));

    private static string ComputeFingerprint(UnrealApiGeneratedSurfaceLock surfaceLock) => Sha256(
        JsonSerializer.Serialize(
            new
            {
                surfaceLock.SchemaVersion,
                surfaceLock.ProjectName,
                surfaceLock.ManagedNamespace,
                surfaceLock.Engine,
                surfaceLock.BaseContractFingerprint,
                surfaceLock.EffectiveContractFingerprint,
                surfaceLock.Types,
            },
            CreateOptions(writeIndented: false)));

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsReflectedTypePath(string value) =>
        value.StartsWith("/", StringComparison.Ordinal) && value.Contains('.') &&
        !value.Contains(':') && !value.Any(char.IsWhiteSpace);

    private static bool IsQualifiedIdentifier(string value) =>
        value.Split('.').All(IsIdentifier);

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(static character => char.IsLetterOrDigit(character) || character == '_');

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}
