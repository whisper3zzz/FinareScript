using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnifiedScript.Unreal;

public enum UnrealNativeParameterPassing
{
    Value,
    ConstReference,
    Reference,
}

public sealed record UnrealNativeFunctionBinding(
    string Extension,
    bool Const,
    IReadOnlyList<UnrealNativeParameterPassing> Parameters);

public sealed record UnrealNativeExtensionFunction(
    UnrealReflectedFunction Declaration,
    bool Const,
    IReadOnlyList<UnrealNativeParameterPassing> Parameters);

public sealed record UnrealNativeExtensionType(
    string Path,
    string NativeName,
    string HeaderPath,
    IReadOnlyList<UnrealReflectedProperty> Properties,
    IReadOnlyList<UnrealNativeExtensionFunction> Functions);

/// <summary>Explicit public C++ declarations, not claims of UHT reflection.</summary>
public sealed record UnrealNativeExtensionManifest(
    string SchemaVersion,
    string Name,
    IReadOnlyList<UnrealNativeExtensionType> Types)
{
    public const string CurrentSchemaVersion = "unifiedscript.unreal-native-extensions/v0.1";
}

public static class UnrealNativeExtensions
{
    public static UnrealNativeExtensionManifest Normalize(UnrealNativeExtensionManifest manifest)
    {
        if (manifest.SchemaVersion != UnrealNativeExtensionManifest.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(manifest.Name) ||
            !Regex.IsMatch(manifest.Name, @"\A[A-Za-z_][A-Za-z0-9_.-]*\z") ||
            manifest.Types is null || manifest.Types.Count == 0)
        {
            throw new JsonException("USUE5401: Invalid native-extension schema, name or types.");
        }
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<UnrealNativeExtensionType>();
        foreach (UnrealNativeExtensionType? type in manifest.Types)
        {
            if (type is null || type.Path is null ||
                !Regex.IsMatch(type.Path, @"\A/Script/[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*\z") ||
                !paths.Add(type.Path) || string.IsNullOrWhiteSpace(type.NativeName) ||
                !Regex.IsMatch(type.NativeName, @"\A[A-Za-z_][A-Za-z0-9_]*\z") ||
                string.IsNullOrWhiteSpace(type.HeaderPath) ||
                !Regex.IsMatch(type.HeaderPath.Replace('\\', '/'), @"\A[A-Za-z0-9_./-]+\.h\z") ||
                type.Properties is null || type.Functions is null ||
                type.Properties.Count + type.Functions.Count == 0 ||
                type.Properties.Any(static field => field is null || field.NativeExtension is not null) ||
                type.Functions.Any(static method => method is null || method.Declaration is null ||
                    method.Declaration.NativeBinding is not null || method.Parameters is null))
            {
                throw new JsonException("USUE5401: Invalid or duplicate native-extension owner/declaration.");
            }
            UnrealReflectedType projected = MakeType(type, manifest.Name);
            UnrealReflectionJson.NormalizeAndValidate(UnrealReflectionManifest.Create(
                new UnrealEngineIdentity(5, 0, 0, "native-extension"), [projected.Module], [projected]));
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnrealReflectedFunction function in projected.Functions)
            {
                string signature = function.Name + "(" + string.Join(",", function.Parameters.Select(parameter =>
                    ManagedTypeKey(parameter.Type) + (parameter.Direction == UnrealParameterDirection.Input ? "" : "&"))) + ")";
                if (!signatures.Add(signature) || projected.Properties.Any(field => field.Name == function.Name))
                    throw new JsonException($"USUE5403: Native overload '{function.Path}' cannot have a distinct managed signature or collides with a field.");
            }
            normalized.Add(type with
            {
                HeaderPath = type.HeaderPath.Replace('\\', '/'),
                Properties = type.Properties.OrderBy(static item => item.Path, StringComparer.Ordinal).ToArray(),
                Functions = type.Functions.OrderBy(static item => item.Declaration.Path, StringComparer.Ordinal).ToArray(),
            });
        }
        return manifest with { Types = normalized.OrderBy(static type => type.Path, StringComparer.Ordinal).ToArray() };
    }

    public static UnrealReflectionManifest Apply(
        UnrealReflectionManifest reflection,
        UnrealNativeExtensionManifest manifest)
    {
        manifest = Normalize(manifest);
        var types = reflection.Types.ToDictionary(static type => type.Path, StringComparer.Ordinal);
        foreach (UnrealNativeExtensionType extension in manifest.Types)
        {
            if (!types.TryGetValue(extension.Path, out UnrealReflectedType? owner) ||
                owner.Kind != UnrealReflectedTypeKind.Class || owner.Traits.Interface ||
                owner.NativeName != extension.NativeName ||
                owner.HeaderPath?.Replace('\\', '/') != extension.HeaderPath)
            {
                throw new JsonException($"USUE5402: Native-extension owner '{extension.Path}' must match an exported native UObject class, C++ name and header.");
            }
            UnrealReflectedType added = MakeType(extension, manifest.Name);
            var names = owner.Properties.Select(static item => item.Name)
                .Concat(owner.Functions.Select(static item => item.Name))
                .Concat(owner.Delegates.Select(static item => item.Name))
                .ToHashSet(StringComparer.Ordinal);
            if (added.Properties.Any(field => names.Contains(field.Name)) ||
                added.Functions.Any(method => names.Contains(method.Name)))
            {
                throw new JsonException($"USUE5403: Native extension '{manifest.Name}' collides with an existing member on '{owner.Path}'.");
            }
            types[owner.Path] = owner with
            {
                Properties = owner.Properties.Concat(added.Properties).ToArray(),
                Functions = owner.Functions.Concat(added.Functions).ToArray(),
            };
        }
        return UnrealReflectionJson.NormalizeAndValidate(reflection with { Types = types.Values.ToArray() });
    }

    internal static void ValidateBinding(UnrealReflectedType owner, UnrealReflectedFunction function)
    {
        UnrealNativeFunctionBinding? binding = function.NativeBinding;
        if (binding is null) return;
        ValidateOwner(owner, binding.Extension);
        if (function.Traits.NativeAccess != UnrealNativeAccess.Public ||
            function.Traits.Static && binding.Const || function.Metadata.Latent ||
            binding.Parameters is null || binding.Parameters.Count != function.Parameters.Count)
        {
            throw new JsonException($"USUE5404: Invalid native signature for '{function.Path}'.");
        }
        for (int index = 0; index < binding.Parameters.Count; index++)
        {
            UnrealNativeParameterPassing passing = binding.Parameters[index];
            UnrealParameterDirection direction = function.Parameters[index].Direction;
            if (!Enum.IsDefined(passing) ||
                (direction != UnrealParameterDirection.Input) != (passing == UnrealNativeParameterPassing.Reference))
            {
                throw new JsonException($"USUE5404: Parameter '{function.Parameters[index].Name}' has incompatible C++ passing and ref/out direction.");
            }
        }
        string expectedPath = $"{owner.Path}:{function.Name}({string.Join(",", function.Parameters.Select((p, index) =>
            p.Type.NativeName.Replace(" ", "") + (binding.Parameters[index] switch
            {
                UnrealNativeParameterPassing.ConstReference => "const&",
                UnrealNativeParameterPassing.Reference => "&",
                _ => "",
            })))})";
        if (function.Path != expectedPath)
        {
            throw new JsonException($"USUE5404: Native overload identity must be '{expectedPath}'.");
        }
    }

    internal static void ValidateBinding(UnrealReflectedType owner, UnrealReflectedProperty field)
    {
        if (field.NativeExtension is null) return;
        ValidateOwner(owner, field.NativeExtension);
        if (field.Access.NativeAccess != UnrealNativeAccess.Public || field.Access.EditorVisible)
        {
            throw new JsonException($"USUE5404: Native field '{field.Path}' must be public and cannot claim reflected Editor visibility.");
        }
    }

    private static void ValidateOwner(UnrealReflectedType owner, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || owner.Kind != UnrealReflectedTypeKind.Class ||
            owner.Traits.Interface || !owner.Path.StartsWith("/Script/", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(owner.HeaderPath))
        {
            throw new JsonException("USUE5404: Native bindings require an explicit extension and native UObject owner.");
        }
    }

    private static string ManagedTypeKey(UnrealTypeReference type) =>
        $"{type.Kind}:{type.TypePath}" + (type.ElementType is null ? "" : $"<{ManagedTypeKey(type.ElementType)}>") +
        (type.KeyType is null ? "" : $"<{ManagedTypeKey(type.KeyType)},{ManagedTypeKey(type.ValueType!)}>");

    private static UnrealReflectedType MakeType(UnrealNativeExtensionType type, string extension) =>
        new(type.Path, type.Path[(type.Path.IndexOf('.') + 1)..], type.NativeName,
            type.Path["/Script/".Length..type.Path.IndexOf('.')], UnrealReflectedTypeKind.Class,
            null, new UnrealTypeTraits(false, false),
            type.Properties.Select(field => field with { NativeExtension = extension }).ToArray(),
            type.Functions.Select(method => method.Declaration with
            {
                NativeBinding = new UnrealNativeFunctionBinding(extension, method.Const, method.Parameters),
            }).ToArray(), type.HeaderPath);
}
