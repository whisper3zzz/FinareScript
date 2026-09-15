namespace Finare.Unreal;

/// <summary>Available, executable API declarations independent of gameplay source usage.</summary>
public static class UnrealApiLibrary
{
    public static UnrealApiContractProjection Project(
        UnrealReflectionManifest reflection, string managedNamespace,
        string nativeHeader = "FinareGeneratedThunks.h", string nativeNamespace = "UE::Finare::Generated")
    {
        UnrealReflectionManifest manifest = UnrealReflectionJson.NormalizeAndValidate(reflection);
        // Value wrappers expose fields the native value transport can represent. An
        // unsupported field must not make all otherwise usable fields disappear.
        var structs = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (UnrealReflectedType type in manifest.Types.Where(type => type.Kind == UnrealReflectedTypeKind.Struct))
                if (type.Properties.Any(property => property.Access.NativeAccess == UnrealNativeAccess.Public &&
                    SupportsStructField(property.Type, structs)))
                    changed |= structs.Add(type.Path);
        } while (changed);
        manifest = manifest with
        {
            Types = manifest.Types.Select(type => type.Kind != UnrealReflectedTypeKind.Struct ? type : type with
            {
                Properties = type.Properties.Where(property => property.Access.NativeAccess == UnrealNativeAccess.Public &&
                    SupportsStructField(property.Type, structs)).ToArray(),
            }).ToArray(),
        };
        UnrealBindingGenerationResult managed = new UnrealBindingGenerator().Generate(manifest,
            new UnrealBindingGeneratorOptions(managedNamespace));
        UnrealNativeThunkGenerationResult native = new UnrealNativeThunkGenerator().Generate(manifest,
            new UnrealNativeThunkGeneratorOptions(nativeHeader, nativeNamespace));
        UnrealBindingCoverageReport coverage = UnrealBindingCoverageReport.Create(manifest, managed, native);
        var accepted = coverage.Entries.Where(entry => entry.Disposition != UnrealBindingDisposition.Rejected)
            .Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var managedRejected = managed.Diagnostics.Where(item => item.Code != "USUE2008").Select(item => item.MemberPath).ToHashSet(StringComparer.Ordinal);
        // Every supported field participates in the reflected value layout, even
        // when its module-private header prevents generation of a C++ codec.
        foreach (var type in manifest.Types.Where(type => type.Kind == UnrealReflectedTypeKind.Struct))
            foreach (var property in type.Properties)
                if (!managedRejected.Contains(property.Path)) accepted.Add(property.Path);
        foreach (var type in manifest.Types.Where(type => type.Kind == UnrealReflectedTypeKind.Class && !type.Traits.Interface))
        {
            foreach (var property in type.Properties)
                if (property.NativeExtension is null && !managedRejected.Contains(property.Path) &&
                    UnrealBindingCoverageReport.SupportsReflectionType(property.Type, false, structs)) accepted.Add(property.Path);
            foreach (var function in type.Functions)
                if (function.NativeBinding is null && !managedRejected.Contains(function.Path) && !function.Metadata.Latent &&
                    UnrealBindingCoverageReport.SupportsReflectionType(function.ReturnType, true, structs) &&
                    function.Parameters.All(parameter => UnrealBindingCoverageReport.SupportsReflectionType(parameter.Type, false, structs))) accepted.Add(function.Path);
        }
        var routes = new List<UnrealApiPolicyRule>();
        foreach (UnrealReflectedType type in manifest.Types.Where(type => type.Kind == UnrealReflectedTypeKind.Class))
        {
            foreach (UnrealReflectedProperty property in type.Properties.Where(property => accepted.Contains(property.Path)))
                if (property.NativeExtension is null && UnrealBindingCoverageReport.SupportsReflectionType(property.Type, false, structs))
                    routes.Add(new(property.Path, UnrealApiPolicyAction.Allow, UnrealBindingRoute.ReflectionFallback));
            foreach (UnrealReflectedFunction function in type.Functions.Where(function => accepted.Contains(function.Path)))
                if (function.NativeBinding is null && !type.Traits.Interface &&
                    UnrealBindingCoverageReport.SupportsReflectionType(function.ReturnType, true, structs) &&
                    function.Parameters.All(parameter => UnrealBindingCoverageReport.SupportsReflectionType(parameter.Type, false, structs)))
                    routes.Add(new(function.Path, UnrealApiPolicyAction.Allow, UnrealBindingRoute.ReflectionFallback));
        }
        var selection = new UnrealApiContract(UnrealApiContract.CurrentSchemaVersion, "AvailableApi",
            managedNamespace, nativeHeader, nativeNamespace,
            manifest.Types.Select(type => new UnrealApiContractType(type.Path,
                type.Properties.Where(property => accepted.Contains(property.Path)).Select(property => property.Name)
                    .Concat(type.Delegates.Where(item => accepted.Contains(item.Path)).Select(item => item.Name))
                    .Concat(type.Functions.Where(function => accepted.Contains(function.Path)).Select(function =>
                        type.Functions.Count(other => other.Name == function.Name) == 1 ? function.Name :
                            function.Path[(type.Path.Length + 1)..])).ToArray())).ToArray())
        {
            Policy = new UnrealApiPolicy(UnrealApiPolicyAction.Allow, routes),
        };
        var projection = UnrealApiContractProjector.Project(manifest, selection);
        // Value declarations are available in C# even when no native dispatcher
        // consumes them. Only actual native signatures need compiled codecs.
        var nativeStructs = new HashSet<string>(StringComparer.Ordinal);
        void Require(UnrealTypeReference reference)
        {
            foreach (var nested in UnrealCallbackBindings.References(reference))
                if (nested.Kind == UnrealValueKind.Struct && nested.TypePath is not null)
                    nativeStructs.Add(nested.TypePath);
        }
        foreach (var type in projection.NativeReflection.Types.Where(type => type.Kind != UnrealReflectedTypeKind.Struct))
        {
            foreach (var property in type.Properties) Require(property.Type);
            foreach (var function in type.Functions)
            {
                Require(function.ReturnType);
                foreach (var parameter in function.Parameters) Require(parameter.Type);
            }
        }
        int previous;
        do
        {
            previous = nativeStructs.Count;
            foreach (var type in projection.NativeReflection.Types.Where(type => nativeStructs.Contains(type.Path)))
                foreach (var property in type.Properties) Require(property.Type);
        } while (previous != nativeStructs.Count);
        return projection with { ReflectedStructPaths = structs, NativeReflection = projection.NativeReflection with
        {
            Types = projection.NativeReflection.Types.Where(type =>
                type.Kind != UnrealReflectedTypeKind.Struct || nativeStructs.Contains(type.Path)).ToArray(),
        } };
    }

    private static bool SupportsStructField(UnrealTypeReference type, IReadOnlySet<string> structs)
    {
        if (type.Kind == UnrealValueKind.Callback) return false;
        return UnrealBindingCoverageReport.SupportsReflectionType(type, false, structs);
    }
}
