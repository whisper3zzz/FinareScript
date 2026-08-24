using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedScript.Core;

namespace UnifiedScript.Unreal;

public static class UnrealReflectionJson
{
    public static string Serialize(UnrealReflectionManifest manifest, bool writeIndented = true) =>
        JsonSerializer.Serialize(NormalizeAndValidate(manifest), CreateOptions(writeIndented));

    public static UnrealReflectionManifest Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        UnrealReflectionManifest? manifest = JsonSerializer.Deserialize<UnrealReflectionManifest>(
            json,
            CreateOptions(writeIndented: true));
        if (manifest is null)
        {
            throw new JsonException("The Unreal reflection manifest was empty.");
        }

        return NormalizeAndValidate(manifest);
    }

    public static UnrealReflectionManifest NormalizeAndValidate(UnrealReflectionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != UnrealReflectionManifest.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported Unreal reflection schema '{manifest.SchemaVersion}'. " +
                $"Expected '{UnrealReflectionManifest.CurrentSchemaVersion}'.");
        }

        if (manifest.Engine is null ||
            manifest.Engine.Major < 5 ||
            manifest.Engine.Minor < 0 ||
            manifest.Engine.Patch < 0 ||
            string.IsNullOrWhiteSpace(manifest.Engine.Branch) ||
            manifest.Modules is null || manifest.Modules.Count == 0 ||
            manifest.Types is null)
        {
            throw new JsonException("The Unreal reflection manifest has invalid required fields.");
        }

        string[] modules = manifest.Modules
            .Select(static module => module?.Trim() ?? string.Empty)
            .ToArray();
        if (modules.Any(static module => !IsIdentifier(module)) ||
            modules.Distinct(StringComparer.Ordinal).Count() != modules.Length)
        {
            throw new JsonException("Unreal reflection modules must be unique identifiers.");
        }

        var typePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnrealReflectedType? type in manifest.Types)
        {
            ValidateType(type, modules, typePaths);
        }

        return manifest with
        {
            Engine = manifest.Engine with { Branch = manifest.Engine.Branch.Trim() },
            Modules = modules.Order(StringComparer.Ordinal).ToArray(),
            Types = manifest.Types
                .OrderBy(static type => type.Path, StringComparer.Ordinal)
                .Select(NormalizeType)
                .ToArray(),
        };
    }

    private static void ValidateType(
        UnrealReflectedType? type,
        IReadOnlyCollection<string> modules,
        ISet<string> typePaths)
    {
        if (type is null ||
            !Enum.IsDefined(type.Kind) ||
            !IsIdentifier(type.Name) ||
            string.IsNullOrWhiteSpace(type.NativeName) ||
            !modules.Contains(type.Module, StringComparer.Ordinal) ||
            !IsReflectedPath(type.Path, type.Module) ||
            !IsValidHeaderPath(type.HeaderPath) ||
            type.Traits is null || type.Properties is null || type.Functions is null ||
            !typePaths.Add(type.Path))
        {
            throw new JsonException("The Unreal reflection manifest contains an invalid reflected type.");
        }

        if (type.BaseTypePath is not null && !IsScriptPath(type.BaseTypePath))
        {
            throw new JsonException($"Unreal type '{type.Path}' has an invalid base type path.");
        }

        var propertyPaths = new HashSet<string>(StringComparer.Ordinal);
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnrealReflectedProperty? property in type.Properties)
        {
            if (property is null ||
                !IsIdentifier(property.Name) ||
                !IsMemberPath(property.Path, type.Path, property.Name) ||
                property.Access is null ||
                !Enum.IsDefined(property.Access.NativeAccess) ||
                !property.Access.Readable && !property.Access.Writable ||
                !propertyPaths.Add(property.Path) || !propertyNames.Add(property.Name))
            {
                throw new JsonException($"Unreal type '{type.Path}' has an invalid property.");
            }

            ValidateTypeReference(property.Type, allowVoid: false, $"property '{property.Path}'");
        }

        var functionPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnrealReflectedFunction? function in type.Functions)
        {
            if (function is null ||
                !IsIdentifier(function.Name) ||
                !IsMemberPath(function.Path, type.Path, function.Name) ||
                function.Parameters is null || function.Traits is null ||
                !Enum.IsDefined(function.Traits.NativeAccess) ||
                !function.Traits.Callable || !functionPaths.Add(function.Path))
            {
                throw new JsonException($"Unreal type '{type.Path}' has an invalid function.");
            }

            ValidateTypeReference(function.ReturnType, allowVoid: true, $"function '{function.Path}' return");
            var parameterNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnrealReflectedParameter? parameter in function.Parameters)
            {
                if (parameter is null || !IsIdentifier(parameter.Name) ||
                    !Enum.IsDefined(parameter.Direction) || !parameterNames.Add(parameter.Name))
                {
                    throw new JsonException($"Unreal function '{function.Path}' has an invalid parameter.");
                }

                ValidateTypeReference(
                    parameter.Type,
                    allowVoid: false,
                    $"parameter '{function.Path}:{parameter.Name}'");
            }
        }
    }

    private static void ValidateTypeReference(
        UnrealTypeReference? type,
        bool allowVoid,
        string context,
        int depth = 0)
    {
        if (type is null || !Enum.IsDefined(type.Kind) ||
            string.IsNullOrWhiteSpace(type.NativeName) || depth > 16)
        {
            throw new JsonException($"Unreal {context} has an invalid type reference.");
        }

        bool hasTypePath = !string.IsNullOrWhiteSpace(type.TypePath);
        bool hasElement = type.ElementType is not null;
        bool hasKey = type.KeyType is not null;
        bool hasValue = type.ValueType is not null;
        switch (type.Kind)
        {
            case UnrealValueKind.Void when allowVoid && !hasTypePath && !hasElement && !hasKey && !hasValue:
            case UnrealValueKind.Boolean or
                UnrealValueKind.Int8 or UnrealValueKind.UInt8 or
                UnrealValueKind.Int16 or UnrealValueKind.UInt16 or
                UnrealValueKind.Int32 or UnrealValueKind.UInt32 or
                UnrealValueKind.Int64 or UnrealValueKind.UInt64 or
                UnrealValueKind.Float32 or UnrealValueKind.Float64 or
                UnrealValueKind.String or UnrealValueKind.Name or UnrealValueKind.Text or
                UnrealValueKind.Unsupported
                when !hasTypePath && !hasElement && !hasKey && !hasValue:
                return;
            case UnrealValueKind.Object or UnrealValueKind.Class or
                UnrealValueKind.Struct or UnrealValueKind.Enum
                when hasTypePath && IsScriptPath(type.TypePath!) && !hasElement && !hasKey && !hasValue:
                return;
            case UnrealValueKind.Array or UnrealValueKind.Set
                when !hasTypePath && hasElement && !hasKey && !hasValue:
                ValidateTypeReference(type.ElementType, allowVoid: false, context, depth + 1);
                return;
            case UnrealValueKind.Map
                when !hasTypePath && !hasElement && hasKey && hasValue:
                ValidateTypeReference(type.KeyType, allowVoid: false, context, depth + 1);
                ValidateTypeReference(type.ValueType, allowVoid: false, context, depth + 1);
                return;
            default:
                throw new JsonException($"Unreal {context} has an inconsistent '{type.Kind}' type reference.");
        }
    }

    private static UnrealReflectedType NormalizeType(UnrealReflectedType type) => type with
    {
        NativeName = type.NativeName.Trim(),
        HeaderPath = type.HeaderPath?.Trim().Replace('\\', '/'),
        Properties = type.Properties
            .OrderBy(static property => property.Path, StringComparer.Ordinal)
            .ToArray(),
        Functions = type.Functions
            .OrderBy(static function => function.Path, StringComparer.Ordinal)
            .Select(static function => function with
            {
                Parameters = function.Parameters.ToArray(),
            })
            .ToArray(),
    };

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(static character => char.IsLetterOrDigit(character) || character == '_');

    private static bool IsScriptPath(string path) =>
        path.StartsWith("/Script/", StringComparison.Ordinal) &&
        path.Length > "/Script/.".Length &&
        path.Contains('.') &&
        !path.Any(char.IsWhiteSpace);

    private static bool IsReflectedPath(string path, string module) =>
        IsScriptPath(path) && path.StartsWith($"/Script/{module}.", StringComparison.Ordinal);

    private static bool IsMemberPath(string path, string ownerPath, string memberName) =>
        string.Equals(path, $"{ownerPath}:{memberName}", StringComparison.Ordinal);

    private static bool IsValidHeaderPath(string? value)
    {
        if (value is null)
        {
            return true;
        }

        string path = value.Trim().Replace('\\', '/');
        return path.Length > 2 &&
            path.EndsWith(".h", StringComparison.OrdinalIgnoreCase) &&
            !Path.IsPathRooted(path) &&
            !path.Split('/').Any(static segment => segment is "" or "." or "..");
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}
