using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.CSharp;

namespace UnifiedScript.Runtime;

public sealed record UnifiedScriptRuntimeManifest(
    string AssemblyName,
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    ProgramImageKey ProgramImageKey,
    IReadOnlyList<string> EntryTypeNames);

public sealed record UnifiedScriptRuntimeBindingRequirement(
    string ContractId,
    string ContentDigest,
    uint MinimumAbiVersion,
    uint MaximumAbiVersion);

public sealed record UnifiedScriptRuntimeBindingManifest(
    IReadOnlyList<UnifiedScriptRuntimeBindingRequirement> Requirements)
{
    public static UnifiedScriptRuntimeBindingManifest Empty { get; } = new([]);
}

public enum UnifiedScriptRuntimeDependencyKind : byte
{
    ManagedAssembly = 1,
    NativeLibrary = 2,
}

public sealed record UnifiedScriptRuntimeDependency(
    UnifiedScriptRuntimeDependencyKind Kind,
    string Name,
    string Version,
    string ContentDigest,
    bool Optional);

public sealed record UnifiedScriptRuntimeDependencyManifest(
    IReadOnlyList<UnifiedScriptRuntimeDependency> Dependencies)
{
    public static UnifiedScriptRuntimeDependencyManifest Empty { get; } = new([]);
}

public enum UnifiedScriptRuntimeStateValueKind : byte
{
    Null = 0,
    Boolean = 1,
    SignedInteger = 2,
    UnsignedInteger = 3,
    FloatingPoint = 4,
    String = 5,
    Enum = 6,
    Name = 7,
    Text = 8,
    Vector = 9,
    Rotator = 10,
    Transform = 11,
    SoftObject = 12,
    SoftClass = 13,
    Array = 14,
    Set = 15,
    Map = 16,
}

public sealed record UnifiedScriptRuntimeStateValue(
    UnifiedScriptRuntimeStateValueKind Kind,
    bool BooleanValue = false,
    long SignedIntegerValue = 0,
    ulong UnsignedIntegerValue = 0,
    double FloatingPointValue = 0,
    string? TextValue = null,
    double VectorX = 0,
    double VectorY = 0,
    double VectorZ = 0,
    double RotatorPitch = 0,
    double RotatorYaw = 0,
    double RotatorRoll = 0,
    double TransformRotationX = 0,
    double TransformRotationY = 0,
    double TransformRotationZ = 0,
    double TransformRotationW = 1,
    double TransformTranslationX = 0,
    double TransformTranslationY = 0,
    double TransformTranslationZ = 0,
    double TransformScaleX = 1,
    double TransformScaleY = 1,
    double TransformScaleZ = 1,
    IReadOnlyList<UnifiedScriptRuntimeStateValue>? ContainerValues = null);

public sealed record UnifiedScriptRuntimeStateDeclaration(
    SemanticId Id,
    string Name,
    string TypeDisplayName,
    string TypeMetadataName,
    ScriptStateTypeKind TypeKind,
    UnifiedScriptRuntimeStateValue DefaultValue,
    ScriptExposure Exposure,
    ScriptFunctionValueTypeDeclaration? UnrealType = null);

public sealed record UnifiedScriptRuntimeAssetDeclaration(
    SemanticId Id,
    string QualifiedName,
    string AssetPath,
    IReadOnlyList<UnifiedScriptRuntimeStateDeclaration> States,
    ScriptClassBindingDeclaration? ClassBinding = null);

public sealed record UnifiedScriptRuntimeDeclarationDocument(
    SemanticId SourceCompilationId,
    SemanticId SemanticConfigurationId,
    IReadOnlyList<UnifiedScriptRuntimeAssetDeclaration> Assets);

public static class UnifiedScriptRuntimeManifestBinary
{
    private static ReadOnlySpan<byte> Magic => "USRMAN\0\0"u8;

    public static byte[] Encode(UnifiedScriptRuntimeManifest manifest)
    {
        UnifiedScriptRuntimeManifest normalized = Normalize(manifest);
        var writer = new ChunkPayloadWriter(Magic);
        writer.WriteString(normalized.AssemblyName);
        writer.WriteSemanticId(normalized.SourceCompilationId);
        writer.WriteSemanticId(normalized.SemanticConfigurationId);
        writer.WriteDigest(normalized.ProgramImageKey.Value);
        writer.WriteCount(normalized.EntryTypeNames.Count);
        foreach (string entryTypeName in normalized.EntryTypeNames)
        {
            writer.WriteString(entryTypeName);
        }
        return writer.ToArray();
    }

    public static UnifiedScriptRuntimeManifest Decode(ReadOnlySpan<byte> source)
    {
        var reader = new ChunkPayloadReader(source, Magic);
        string assemblyName = reader.ReadString();
        SemanticId compilationId = reader.ReadSemanticId();
        SemanticId configurationId = reader.ReadSemanticId();
        var imageKey = new ProgramImageKey(reader.ReadDigest());
        int entryCount = reader.ReadCount();
        string[] entryTypes = new string[entryCount];
        for (int index = 0; index < entryTypes.Length; index++)
        {
            entryTypes[index] = reader.ReadString();
        }
        reader.RequireEnd();
        return Normalize(new UnifiedScriptRuntimeManifest(
            assemblyName,
            compilationId,
            configurationId,
            imageKey,
            entryTypes));
    }

    private static UnifiedScriptRuntimeManifest Normalize(UnifiedScriptRuntimeManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.AssemblyName) ||
            manifest.SourceCompilationId.IsEmpty ||
            manifest.SemanticConfigurationId.IsEmpty ||
            manifest.ProgramImageKey.IsEmpty ||
            manifest.EntryTypeNames is null ||
            manifest.EntryTypeNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("A runtime chunk manifest has invalid required fields.");
        }

        string[] entryTypes = manifest.EntryTypeNames
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (entryTypes.Length != manifest.EntryTypeNames.Count)
        {
            throw new InvalidDataException("A runtime chunk manifest has duplicate entry types.");
        }

        return manifest with { EntryTypeNames = entryTypes };
    }
}

public static class UnifiedScriptRuntimeBindingManifestBinary
{
    private static ReadOnlySpan<byte> Magic => "USRBND\0\0"u8;

    public static byte[] Encode(UnifiedScriptRuntimeBindingManifest manifest)
    {
        UnifiedScriptRuntimeBindingManifest normalized = Normalize(manifest);
        var writer = new ChunkPayloadWriter(Magic);
        writer.WriteCount(normalized.Requirements.Count);
        foreach (UnifiedScriptRuntimeBindingRequirement requirement in normalized.Requirements)
        {
            writer.WriteString(requirement.ContractId);
            writer.WriteDigest(requirement.ContentDigest);
            writer.WriteUInt32(requirement.MinimumAbiVersion);
            writer.WriteUInt32(requirement.MaximumAbiVersion);
        }
        return writer.ToArray();
    }

    public static UnifiedScriptRuntimeBindingManifest Decode(ReadOnlySpan<byte> source)
    {
        var reader = new ChunkPayloadReader(source, Magic);
        int count = reader.ReadCount();
        var requirements = new UnifiedScriptRuntimeBindingRequirement[count];
        for (int index = 0; index < requirements.Length; index++)
        {
            requirements[index] = new UnifiedScriptRuntimeBindingRequirement(
                reader.ReadString(),
                reader.ReadDigest(),
                reader.ReadUInt32(),
                reader.ReadUInt32());
        }
        reader.RequireEnd();
        return Normalize(new UnifiedScriptRuntimeBindingManifest(requirements));
    }

    private static UnifiedScriptRuntimeBindingManifest Normalize(
        UnifiedScriptRuntimeBindingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Requirements is null)
        {
            throw new InvalidDataException("A runtime binding manifest requires a requirement list.");
        }

        UnifiedScriptRuntimeBindingRequirement[] requirements = manifest.Requirements
            .OrderBy(static requirement => requirement.ContractId, StringComparer.Ordinal)
            .ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnifiedScriptRuntimeBindingRequirement? requirement in requirements)
        {
            if (requirement is null || string.IsNullOrWhiteSpace(requirement.ContractId) ||
                !ProgramImageKey.TryParse(requirement.ContentDigest, out _) ||
                requirement.MinimumAbiVersion == 0 ||
                requirement.MaximumAbiVersion < requirement.MinimumAbiVersion ||
                !ids.Add(requirement.ContractId))
            {
                throw new InvalidDataException("A runtime binding requirement is invalid or duplicated.");
            }
        }

        return manifest with { Requirements = requirements };
    }
}

public static class UnifiedScriptRuntimeDependencyManifestBinary
{
    private static ReadOnlySpan<byte> Magic => "USRDEP\0\0"u8;

    public static byte[] Encode(UnifiedScriptRuntimeDependencyManifest manifest)
    {
        UnifiedScriptRuntimeDependencyManifest normalized = Normalize(manifest);
        var writer = new ChunkPayloadWriter(Magic);
        writer.WriteCount(normalized.Dependencies.Count);
        foreach (UnifiedScriptRuntimeDependency dependency in normalized.Dependencies)
        {
            writer.WriteByte((byte)dependency.Kind);
            writer.WriteBoolean(dependency.Optional);
            writer.WriteUInt16(0);
            writer.WriteString(dependency.Name);
            writer.WriteString(dependency.Version);
            writer.WriteDigest(dependency.ContentDigest);
        }
        return writer.ToArray();
    }

    public static UnifiedScriptRuntimeDependencyManifest Decode(ReadOnlySpan<byte> source)
    {
        var reader = new ChunkPayloadReader(source, Magic);
        int count = reader.ReadCount();
        var dependencies = new UnifiedScriptRuntimeDependency[count];
        for (int index = 0; index < dependencies.Length; index++)
        {
            var kind = (UnifiedScriptRuntimeDependencyKind)reader.ReadByte();
            bool optional = reader.ReadBoolean();
            if (reader.ReadUInt16() != 0)
            {
                throw new InvalidDataException("A runtime dependency uses non-zero reserved flags.");
            }
            dependencies[index] = new UnifiedScriptRuntimeDependency(
                kind,
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadDigest(),
                optional);
        }
        reader.RequireEnd();
        return Normalize(new UnifiedScriptRuntimeDependencyManifest(dependencies));
    }

    private static UnifiedScriptRuntimeDependencyManifest Normalize(
        UnifiedScriptRuntimeDependencyManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Dependencies is null)
        {
            throw new InvalidDataException("A runtime dependency manifest requires a dependency list.");
        }

        UnifiedScriptRuntimeDependency[] dependencies = manifest.Dependencies
            .OrderBy(static dependency => dependency.Kind)
            .ThenBy(static dependency => dependency.Name, StringComparer.Ordinal)
            .ToArray();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnifiedScriptRuntimeDependency? dependency in dependencies)
        {
            if (dependency is null || !Enum.IsDefined(dependency.Kind) ||
                string.IsNullOrWhiteSpace(dependency.Name) ||
                string.IsNullOrWhiteSpace(dependency.Version) ||
                !ProgramImageKey.TryParse(dependency.ContentDigest, out _) ||
                !identities.Add($"{dependency.Kind}:{dependency.Name}"))
            {
                throw new InvalidDataException("A runtime dependency is invalid or duplicated.");
            }
        }

        return manifest with { Dependencies = dependencies };
    }
}

public static class UnifiedScriptRuntimeDeclarationBinary
{
    private static ReadOnlySpan<byte> Magic => "USRDEC\0\0"u8;
    private const int MaximumDefaultDepth = 16;

    public static byte[] Encode(UnifiedScriptRuntimeDeclarationDocument document)
    {
        UnifiedScriptRuntimeDeclarationDocument normalized = Normalize(document);
        UnifiedScriptRuntimeStateDeclaration[] allStates = normalized.Assets
            .SelectMany(static asset => asset.States)
            .ToArray();
        ushort minorVersion = normalized.Assets.Any(static asset => asset.ClassBinding is not null)
            ? (ushort)5
            : allStates.Any(static state =>
                state.TypeKind is ScriptStateTypeKind.Array or ScriptStateTypeKind.Set or ScriptStateTypeKind.Map ||
                state.DefaultValue.Kind is UnifiedScriptRuntimeStateValueKind.Array or
                    UnifiedScriptRuntimeStateValueKind.Set or UnifiedScriptRuntimeStateValueKind.Map)
            ? (ushort)4
            : allStates.Any(static state =>
                state.TypeKind is ScriptStateTypeKind.SoftObject or ScriptStateTypeKind.SoftClass ||
                state.DefaultValue.Kind is UnifiedScriptRuntimeStateValueKind.SoftObject or
                    UnifiedScriptRuntimeStateValueKind.SoftClass)
            ? (ushort)3
            : allStates.Any(static state =>
                state.DefaultValue.Kind is UnifiedScriptRuntimeStateValueKind.Rotator or
                    UnifiedScriptRuntimeStateValueKind.Transform ||
                state.UnrealType?.Kind == ScriptFunctionValueTypeKind.Enum)
                ? (ushort)2
                : (ushort)1;
        var writer = new ChunkPayloadWriter(Magic, minorVersion);
        writer.WriteSemanticId(normalized.SourceCompilationId);
        writer.WriteSemanticId(normalized.SemanticConfigurationId);
        writer.WriteCount(normalized.Assets.Count);
        foreach (UnifiedScriptRuntimeAssetDeclaration asset in normalized.Assets)
        {
            writer.WriteSemanticId(asset.Id);
            writer.WriteString(asset.QualifiedName);
            writer.WriteString(asset.AssetPath);
            if (minorVersion >= 5)
            {
                writer.WriteBoolean(asset.ClassBinding is not null);
                if (asset.ClassBinding is not null)
                {
                    writer.WriteString(asset.ClassBinding.Kind);
                    writer.WriteNullableString(asset.ClassBinding.BaseClassPath);
                    writer.WriteNullableString(asset.ClassBinding.GeneratedClassPath);
                }
            }
            writer.WriteCount(asset.States.Count);
            foreach (UnifiedScriptRuntimeStateDeclaration state in asset.States)
            {
                writer.WriteSemanticId(state.Id);
                writer.WriteString(state.Name);
                writer.WriteString(state.TypeDisplayName);
                writer.WriteString(state.TypeMetadataName);
                writer.WriteByte(checked((byte)state.TypeKind));
                WriteUnrealType(writer, state.UnrealType, 0);
                WriteDefault(writer, state.DefaultValue);
                byte exposureFlags = 0;
                if (state.Exposure.InstanceEditable) exposureFlags |= 1 << 0;
                if (state.Exposure.SaveGame) exposureFlags |= 1 << 1;
                if (state.Exposure.Replicated) exposureFlags |= 1 << 2;
                writer.WriteByte(exposureFlags);
                writer.WriteNullableString(state.Exposure.Category);
            }
        }
        return writer.ToArray();
    }

    public static UnifiedScriptRuntimeDeclarationDocument Decode(ReadOnlySpan<byte> source)
    {
        var reader = new ChunkPayloadReader(source, Magic, maximumMinorVersion: 5);
        SemanticId compilationId = reader.ReadSemanticId();
        SemanticId configurationId = reader.ReadSemanticId();
        int assetCount = reader.ReadCount();
        var assets = new UnifiedScriptRuntimeAssetDeclaration[assetCount];
        for (int assetIndex = 0; assetIndex < assets.Length; assetIndex++)
        {
            SemanticId assetId = reader.ReadSemanticId();
            string qualifiedName = reader.ReadString();
            string assetPath = reader.ReadString();
            ScriptClassBindingDeclaration? classBinding = null;
            if (reader.MinorVersion >= 5 && reader.ReadBoolean())
            {
                classBinding = new ScriptClassBindingDeclaration(
                    reader.ReadString(),
                    reader.ReadNullableString(),
                    reader.ReadNullableString());
            }
            int stateCount = reader.ReadCount();
            var states = new UnifiedScriptRuntimeStateDeclaration[stateCount];
            for (int stateIndex = 0; stateIndex < states.Length; stateIndex++)
            {
                SemanticId stateId = reader.ReadSemanticId();
                string name = reader.ReadString();
                string displayName = reader.ReadString();
                string metadataName = reader.ReadString();
                var typeKind = (ScriptStateTypeKind)reader.ReadByte();
                ScriptFunctionValueTypeDeclaration? unrealType = reader.MinorVersion >= 1
                    ? ReadUnrealType(ref reader, 0)
                    : null;
                if (typeKind == ScriptStateTypeKind.Enum && unrealType?.Kind == ScriptFunctionValueTypeKind.Enum &&
                    reader.MinorVersion < 2)
                {
                    throw new InvalidDataException(
                        $"Typed Unreal enum state '{name}' requires declaration minor version 2.");
                }
                if (typeKind is ScriptStateTypeKind.SoftObject or ScriptStateTypeKind.SoftClass &&
                    reader.MinorVersion < 3)
                {
                    throw new InvalidDataException(
                        $"Soft reference state '{name}' requires declaration minor version 3.");
                }
                if (typeKind is ScriptStateTypeKind.Array or ScriptStateTypeKind.Set or ScriptStateTypeKind.Map &&
                    reader.MinorVersion < 4)
                {
                    throw new InvalidDataException(
                        $"Collection state '{name}' requires declaration minor version 4.");
                }
                UnifiedScriptRuntimeStateValue defaultValue = ReadDefault(ref reader);
                byte exposureFlags = reader.ReadByte();
                if ((exposureFlags & ~0x07) != 0)
                {
                    throw new InvalidDataException(
                        $"Runtime state '{name}' has unsupported exposure flags.");
                }
                string? category = reader.ReadNullableString();
                states[stateIndex] = new UnifiedScriptRuntimeStateDeclaration(
                    stateId,
                    name,
                    displayName,
                    metadataName,
                    typeKind,
                    defaultValue,
                    new ScriptExposure(
                        (exposureFlags & (1 << 0)) != 0,
                        (exposureFlags & (1 << 1)) != 0,
                        (exposureFlags & (1 << 2)) != 0,
                        category),
                    unrealType);
            }
            assets[assetIndex] = new UnifiedScriptRuntimeAssetDeclaration(
                assetId,
                qualifiedName,
                assetPath,
                states,
                classBinding);
        }
        reader.RequireEnd();
        return Normalize(new UnifiedScriptRuntimeDeclarationDocument(
            compilationId,
            configurationId,
            assets));
    }

    public static UnifiedScriptRuntimeDeclarationDocument FromScriptAssets(
        ScriptAssetDocument document)
    {
        ScriptAssetDocument normalized = ScriptAssetJson.NormalizeAndValidate(document);
        return Normalize(new UnifiedScriptRuntimeDeclarationDocument(
            normalized.SourceCompilationId,
            normalized.SemanticConfigurationId,
            normalized.Assets.Select(static asset =>
                new UnifiedScriptRuntimeAssetDeclaration(
                    asset.Id,
                    asset.QualifiedName,
                    asset.AssetPath,
                    asset.States.Select(static state =>
                        new UnifiedScriptRuntimeStateDeclaration(
                            state.Id,
                            state.Name,
                            state.TypeDisplayName,
                            state.TypeMetadataName,
                            state.TypeKind,
                            ConvertDefault(state),
                            state.Exposure,
                            state.UnrealType)).ToArray(),
                    asset.ClassBinding)).ToArray()));
    }

    private static UnifiedScriptRuntimeStateValue ConvertDefault(ScriptStateDeclaration state) =>
        state.TypeKind switch
        {
            ScriptStateTypeKind.Boolean => new UnifiedScriptRuntimeStateValue(
                UnifiedScriptRuntimeStateValueKind.Boolean,
                BooleanValue: state.DefaultValue.GetBoolean()),
            ScriptStateTypeKind.Integer when state.DefaultValue.TryGetInt64(out long signed) =>
                new UnifiedScriptRuntimeStateValue(
                    UnifiedScriptRuntimeStateValueKind.SignedInteger,
                    SignedIntegerValue: signed),
            ScriptStateTypeKind.Integer when state.DefaultValue.TryGetUInt64(out ulong unsigned) =>
                new UnifiedScriptRuntimeStateValue(
                    UnifiedScriptRuntimeStateValueKind.UnsignedInteger,
                    UnsignedIntegerValue: unsigned),
            ScriptStateTypeKind.FloatingPoint => new UnifiedScriptRuntimeStateValue(
                UnifiedScriptRuntimeStateValueKind.FloatingPoint,
                FloatingPointValue: state.DefaultValue.GetDouble()),
            ScriptStateTypeKind.String when state.DefaultValue.ValueKind == JsonValueKind.Null =>
                new UnifiedScriptRuntimeStateValue(UnifiedScriptRuntimeStateValueKind.Null),
            ScriptStateTypeKind.String => new UnifiedScriptRuntimeStateValue(
                UnifiedScriptRuntimeStateValueKind.String,
                TextValue: state.DefaultValue.GetString()),
            ScriptStateTypeKind.Enum => new UnifiedScriptRuntimeStateValue(
                UnifiedScriptRuntimeStateValueKind.Enum,
                TextValue: state.DefaultValue.GetString()),
            ScriptStateTypeKind.Name => new UnifiedScriptRuntimeStateValue(
                UnifiedScriptRuntimeStateValueKind.Name,
                TextValue: state.DefaultValue.GetString()),
            ScriptStateTypeKind.Text => new UnifiedScriptRuntimeStateValue(
                UnifiedScriptRuntimeStateValueKind.Text,
                TextValue: state.DefaultValue.GetString()),
            ScriptStateTypeKind.Object or ScriptStateTypeKind.Class =>
                new UnifiedScriptRuntimeStateValue(UnifiedScriptRuntimeStateValueKind.Null),
            ScriptStateTypeKind.SoftObject => new UnifiedScriptRuntimeStateValue(
                UnifiedScriptRuntimeStateValueKind.SoftObject,
                TextValue: state.DefaultValue.GetString()),
            ScriptStateTypeKind.SoftClass => new UnifiedScriptRuntimeStateValue(
                UnifiedScriptRuntimeStateValueKind.SoftClass,
                TextValue: state.DefaultValue.GetString()),
            ScriptStateTypeKind.Array => ConvertCollectionDefault(
                state,
                UnifiedScriptRuntimeStateValueKind.Array),
            ScriptStateTypeKind.Set => ConvertCollectionDefault(
                state,
                UnifiedScriptRuntimeStateValueKind.Set),
            ScriptStateTypeKind.Map => ConvertCollectionDefault(
                state,
                UnifiedScriptRuntimeStateValueKind.Map),
            ScriptStateTypeKind.Struct when
                state.TypeMetadataName == "UnifiedScript.Unreal.UnrealVector" =>
                new UnifiedScriptRuntimeStateValue(
                    UnifiedScriptRuntimeStateValueKind.Vector,
                    VectorX: ReadVectorComponent(state.DefaultValue, "x"),
                    VectorY: ReadVectorComponent(state.DefaultValue, "y"),
                    VectorZ: ReadVectorComponent(state.DefaultValue, "z")),
            ScriptStateTypeKind.Struct when
                state.TypeMetadataName == "UnifiedScript.Unreal.UnrealRotator" =>
                new UnifiedScriptRuntimeStateValue(
                    UnifiedScriptRuntimeStateValueKind.Rotator,
                    RotatorPitch: ReadVectorComponent(state.DefaultValue, "pitch"),
                    RotatorYaw: ReadVectorComponent(state.DefaultValue, "yaw"),
                    RotatorRoll: ReadVectorComponent(state.DefaultValue, "roll")),
            ScriptStateTypeKind.Struct when
                state.TypeMetadataName == "UnifiedScript.Unreal.UnrealTransform" =>
                ConvertTransformDefault(state.DefaultValue),
            _ => throw new InvalidDataException(
                $"Script state '{state.Name}' has an unsupported runtime default."),
        };

    private static double ReadVectorComponent(JsonElement value, string name) =>
        value.GetProperty(name).GetDouble();

    private static UnifiedScriptRuntimeStateValue ConvertTransformDefault(JsonElement value)
    {
        JsonElement rotation = value.GetProperty("rotation");
        JsonElement translation = value.GetProperty("translation");
        JsonElement scale = value.GetProperty("scale3D");
        return new UnifiedScriptRuntimeStateValue(
            UnifiedScriptRuntimeStateValueKind.Transform,
            TransformRotationX: rotation.GetProperty("x").GetDouble(),
            TransformRotationY: rotation.GetProperty("y").GetDouble(),
            TransformRotationZ: rotation.GetProperty("z").GetDouble(),
            TransformRotationW: rotation.GetProperty("w").GetDouble(),
            TransformTranslationX: translation.GetProperty("x").GetDouble(),
            TransformTranslationY: translation.GetProperty("y").GetDouble(),
            TransformTranslationZ: translation.GetProperty("z").GetDouble(),
            TransformScaleX: scale.GetProperty("x").GetDouble(),
            TransformScaleY: scale.GetProperty("y").GetDouble(),
            TransformScaleZ: scale.GetProperty("z").GetDouble());
    }

    private static UnifiedScriptRuntimeStateValue ConvertCollectionDefault(
        ScriptStateDeclaration state,
        UnifiedScriptRuntimeStateValueKind kind)
    {
        ScriptFunctionTypeArgumentDeclaration[] arguments =
            state.UnrealType?.TypeArguments?.ToArray() ?? throw new InvalidDataException(
                $"Collection state '{state.Name}' has no element type metadata.");
        var values = new List<UnifiedScriptRuntimeStateValue>();
        foreach (JsonElement item in state.DefaultValue.EnumerateArray())
        {
            if (kind == UnifiedScriptRuntimeStateValueKind.Map)
            {
                values.Add(ConvertCollectionLeaf(item.GetProperty("key"), arguments[0]));
                values.Add(ConvertCollectionLeaf(item.GetProperty("value"), arguments[1]));
            }
            else
            {
                values.Add(ConvertCollectionLeaf(item, arguments[0]));
            }
        }
        return new UnifiedScriptRuntimeStateValue(kind, ContainerValues: values);
    }

    private static UnifiedScriptRuntimeStateValue ConvertCollectionLeaf(
        JsonElement value,
        ScriptFunctionTypeArgumentDeclaration argument)
    {
        string metadata = argument.TypeMetadataName;
        if (metadata == "System.Boolean")
            return new(UnifiedScriptRuntimeStateValueKind.Boolean, BooleanValue: value.GetBoolean());
        if (metadata is "System.Byte" or "System.UInt16" or "System.UInt32" or "System.UInt64")
            return value.TryGetUInt64(out ulong unsigned)
                ? new(UnifiedScriptRuntimeStateValueKind.UnsignedInteger, UnsignedIntegerValue: unsigned)
                : throw new InvalidDataException("An unsigned collection default is out of range.");
        if (metadata is "System.SByte" or "System.Int16" or "System.Int32" or "System.Int64")
            return value.TryGetInt64(out long signed)
                ? new(UnifiedScriptRuntimeStateValueKind.SignedInteger, SignedIntegerValue: signed)
                : throw new InvalidDataException("A signed collection default is out of range.");
        if (metadata is "System.Single" or "System.Double")
            return new(UnifiedScriptRuntimeStateValueKind.FloatingPoint, FloatingPointValue: value.GetDouble());
        if (metadata == "System.String")
            return new(UnifiedScriptRuntimeStateValueKind.String, TextValue: value.GetString());
        if (metadata == "UnifiedScript.Unreal.UnrealName")
            return new(UnifiedScriptRuntimeStateValueKind.Name, TextValue: value.GetString());
        if (metadata == "UnifiedScript.Unreal.UnrealText")
            return new(UnifiedScriptRuntimeStateValueKind.Text, TextValue: value.GetString());
        if (metadata is "UnifiedScript.Unreal.UnrealObjectHandle" or "UnifiedScript.Unreal.UnrealClassHandle")
            return new(UnifiedScriptRuntimeStateValueKind.Null);
        if (metadata == "UnifiedScript.Unreal.UnrealSoftObjectHandle")
            return new(UnifiedScriptRuntimeStateValueKind.SoftObject, TextValue: value.GetString());
        if (metadata == "UnifiedScript.Unreal.UnrealSoftClassHandle")
            return new(UnifiedScriptRuntimeStateValueKind.SoftClass, TextValue: value.GetString());
        if (argument.UnrealType?.Kind == ScriptFunctionValueTypeKind.Enum)
            return new(UnifiedScriptRuntimeStateValueKind.Enum, TextValue: value.GetString());
        if (metadata == "UnifiedScript.Unreal.UnrealVector")
            return new(UnifiedScriptRuntimeStateValueKind.Vector,
                VectorX: value.GetProperty("x").GetDouble(), VectorY: value.GetProperty("y").GetDouble(),
                VectorZ: value.GetProperty("z").GetDouble());
        if (metadata == "UnifiedScript.Unreal.UnrealRotator")
            return new(UnifiedScriptRuntimeStateValueKind.Rotator,
                RotatorPitch: value.GetProperty("pitch").GetDouble(), RotatorYaw: value.GetProperty("yaw").GetDouble(),
                RotatorRoll: value.GetProperty("roll").GetDouble());
        if (metadata == "UnifiedScript.Unreal.UnrealTransform")
            return ConvertTransformDefault(value);
        throw new InvalidDataException($"Unsupported collection leaf '{metadata}'.");
    }

    private static void WriteUnrealType(
        ChunkPayloadWriter writer,
        ScriptFunctionValueTypeDeclaration? type,
        int depth)
    {
        if (depth > 16)
        {
            throw new InvalidDataException("A runtime Unreal value type exceeds the nesting limit.");
        }
        writer.WriteBoolean(type is not null);
        if (type is null)
        {
            return;
        }
        writer.WriteByte(checked((byte)type.Kind));
        writer.WriteString(type.UnrealTypePath);
        writer.WriteString(type.UnrealNativeName);
        writer.WriteCount(type.Fields.Count);
        foreach (ScriptFunctionStructFieldDeclaration field in type.Fields)
        {
            writer.WriteUInt32(checked((uint)field.Index));
            writer.WriteString(field.NativeName);
            writer.WriteString(field.TypeMetadataName);
            WriteUnrealType(writer, field.UnrealType, depth + 1);
        }
        if (type.Kind is ScriptFunctionValueTypeKind.Array or
            ScriptFunctionValueTypeKind.Set or ScriptFunctionValueTypeKind.Map)
        {
            writer.WriteCount(type.TypeArguments?.Count ?? 0);
            foreach (ScriptFunctionTypeArgumentDeclaration argument in type.TypeArguments ?? [])
            {
                writer.WriteString(argument.TypeMetadataName);
                WriteUnrealType(writer, argument.UnrealType, depth + 1);
            }
        }
    }

    private static ScriptFunctionValueTypeDeclaration? ReadUnrealType(
        ref ChunkPayloadReader reader,
        int depth)
    {
        if (depth > 16)
        {
            throw new InvalidDataException("A runtime Unreal value type exceeds the nesting limit.");
        }
        if (!reader.ReadBoolean())
        {
            return null;
        }
        var kind = (ScriptFunctionValueTypeKind)reader.ReadByte();
        string path = reader.ReadString();
        string nativeName = reader.ReadString();
        int fieldCount = reader.ReadCount();
        var fields = new ScriptFunctionStructFieldDeclaration[fieldCount];
        for (int index = 0; index < fields.Length; ++index)
        {
            int fieldIndex = checked((int)reader.ReadUInt32());
            string fieldName = reader.ReadString();
            string metadataName = reader.ReadString();
            fields[index] = new ScriptFunctionStructFieldDeclaration(
                fieldIndex,
                fieldName,
                metadataName,
                ReadUnrealType(ref reader, depth + 1));
        }
        IReadOnlyList<ScriptFunctionTypeArgumentDeclaration>? typeArguments = null;
        if (kind is ScriptFunctionValueTypeKind.Array or
            ScriptFunctionValueTypeKind.Set or ScriptFunctionValueTypeKind.Map)
        {
            int argumentCount = reader.ReadCount();
            var arguments = new ScriptFunctionTypeArgumentDeclaration[argumentCount];
            for (int index = 0; index < argumentCount; index++)
            {
                arguments[index] = new ScriptFunctionTypeArgumentDeclaration(
                    reader.ReadString(),
                    ReadUnrealType(ref reader, depth + 1));
            }
            typeArguments = arguments;
        }
        return new ScriptFunctionValueTypeDeclaration(kind, path, nativeName, fields, typeArguments);
    }

    private static void WriteDefault(
        ChunkPayloadWriter writer,
        UnifiedScriptRuntimeStateValue value,
        int depth = 0)
    {
        if (depth > MaximumDefaultDepth)
        {
            throw new InvalidDataException("A runtime collection default exceeds the nesting limit.");
        }
        writer.WriteByte((byte)value.Kind);
        switch (value.Kind)
        {
            case UnifiedScriptRuntimeStateValueKind.Null:
                break;
            case UnifiedScriptRuntimeStateValueKind.Boolean:
                writer.WriteBoolean(value.BooleanValue);
                break;
            case UnifiedScriptRuntimeStateValueKind.SignedInteger:
                writer.WriteInt64(value.SignedIntegerValue);
                break;
            case UnifiedScriptRuntimeStateValueKind.UnsignedInteger:
                writer.WriteUInt64(value.UnsignedIntegerValue);
                break;
            case UnifiedScriptRuntimeStateValueKind.FloatingPoint:
                writer.WriteDouble(value.FloatingPointValue);
                break;
            case UnifiedScriptRuntimeStateValueKind.String:
            case UnifiedScriptRuntimeStateValueKind.Enum:
            case UnifiedScriptRuntimeStateValueKind.Name:
            case UnifiedScriptRuntimeStateValueKind.Text:
            case UnifiedScriptRuntimeStateValueKind.SoftObject:
            case UnifiedScriptRuntimeStateValueKind.SoftClass:
                writer.WriteString(value.TextValue ?? throw new InvalidDataException(
                    "A runtime textual default requires text."));
                break;
            case UnifiedScriptRuntimeStateValueKind.Array:
            case UnifiedScriptRuntimeStateValueKind.Set:
            case UnifiedScriptRuntimeStateValueKind.Map:
                if (value.ContainerValues is null)
                    throw new InvalidDataException("A runtime collection default requires child values.");
                writer.WriteCount(value.ContainerValues.Count);
                foreach (UnifiedScriptRuntimeStateValue child in value.ContainerValues)
                    WriteDefault(writer, child, depth + 1);
                break;
            case UnifiedScriptRuntimeStateValueKind.Vector:
                writer.WriteDouble(value.VectorX);
                writer.WriteDouble(value.VectorY);
                writer.WriteDouble(value.VectorZ);
                break;
            case UnifiedScriptRuntimeStateValueKind.Rotator:
                writer.WriteDouble(value.RotatorPitch);
                writer.WriteDouble(value.RotatorYaw);
                writer.WriteDouble(value.RotatorRoll);
                break;
            case UnifiedScriptRuntimeStateValueKind.Transform:
                writer.WriteDouble(value.TransformRotationX);
                writer.WriteDouble(value.TransformRotationY);
                writer.WriteDouble(value.TransformRotationZ);
                writer.WriteDouble(value.TransformRotationW);
                writer.WriteDouble(value.TransformTranslationX);
                writer.WriteDouble(value.TransformTranslationY);
                writer.WriteDouble(value.TransformTranslationZ);
                writer.WriteDouble(value.TransformScaleX);
                writer.WriteDouble(value.TransformScaleY);
                writer.WriteDouble(value.TransformScaleZ);
                break;
            default:
                throw new InvalidDataException($"Unsupported runtime state value '{value.Kind}'.");
        }
    }

    private static UnifiedScriptRuntimeStateValue ReadDefault(
        ref ChunkPayloadReader reader,
        int depth = 0)
    {
        if (depth > MaximumDefaultDepth)
        {
            throw new InvalidDataException("A runtime collection default exceeds the nesting limit.");
        }
        var kind = (UnifiedScriptRuntimeStateValueKind)reader.ReadByte();
        return kind switch
        {
            UnifiedScriptRuntimeStateValueKind.Null => new(kind),
            UnifiedScriptRuntimeStateValueKind.Boolean => new(
                kind,
                BooleanValue: reader.ReadBoolean()),
            UnifiedScriptRuntimeStateValueKind.SignedInteger => new(
                kind,
                SignedIntegerValue: reader.ReadInt64()),
            UnifiedScriptRuntimeStateValueKind.UnsignedInteger => new(
                kind,
                UnsignedIntegerValue: reader.ReadUInt64()),
            UnifiedScriptRuntimeStateValueKind.FloatingPoint => new(
                kind,
                FloatingPointValue: reader.ReadDouble()),
            UnifiedScriptRuntimeStateValueKind.String or
                UnifiedScriptRuntimeStateValueKind.Enum or
                UnifiedScriptRuntimeStateValueKind.Name or
                UnifiedScriptRuntimeStateValueKind.Text => new(
                    kind,
                    TextValue: reader.ReadString()),
            UnifiedScriptRuntimeStateValueKind.Vector => new(
                kind,
                VectorX: reader.ReadDouble(),
                VectorY: reader.ReadDouble(),
                VectorZ: reader.ReadDouble()),
            UnifiedScriptRuntimeStateValueKind.Rotator when reader.MinorVersion >= 2 => new(
                kind,
                RotatorPitch: reader.ReadDouble(),
                RotatorYaw: reader.ReadDouble(),
                RotatorRoll: reader.ReadDouble()),
            UnifiedScriptRuntimeStateValueKind.Transform when reader.MinorVersion >= 2 => new(
                kind,
                TransformRotationX: reader.ReadDouble(),
                TransformRotationY: reader.ReadDouble(),
                TransformRotationZ: reader.ReadDouble(),
                TransformRotationW: reader.ReadDouble(),
                TransformTranslationX: reader.ReadDouble(),
                TransformTranslationY: reader.ReadDouble(),
                TransformTranslationZ: reader.ReadDouble(),
                TransformScaleX: reader.ReadDouble(),
                TransformScaleY: reader.ReadDouble(),
                TransformScaleZ: reader.ReadDouble()),
            UnifiedScriptRuntimeStateValueKind.SoftObject or
                UnifiedScriptRuntimeStateValueKind.SoftClass when reader.MinorVersion >= 3 => new(
                    kind,
                    TextValue: reader.ReadString()),
            UnifiedScriptRuntimeStateValueKind.Array or
                UnifiedScriptRuntimeStateValueKind.Set or
                UnifiedScriptRuntimeStateValueKind.Map when reader.MinorVersion >= 4 =>
                ReadCollectionDefault(ref reader, kind, depth),
            _ => throw new InvalidDataException($"Unsupported runtime state value '{kind}'."),
        };
    }

    private static UnifiedScriptRuntimeStateValue ReadCollectionDefault(
        ref ChunkPayloadReader reader,
        UnifiedScriptRuntimeStateValueKind kind,
        int depth)
    {
        int count = reader.ReadCount();
        var values = new UnifiedScriptRuntimeStateValue[count];
        for (int index = 0; index < count; index++)
            values[index] = ReadDefault(ref reader, depth + 1);
        return new UnifiedScriptRuntimeStateValue(kind, ContainerValues: values);
    }

    private static UnifiedScriptRuntimeDeclarationDocument Normalize(
        UnifiedScriptRuntimeDeclarationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SourceCompilationId.IsEmpty ||
            document.SemanticConfigurationId.IsEmpty || document.Assets is null)
        {
            throw new InvalidDataException("A runtime declaration has invalid required fields.");
        }

        UnifiedScriptRuntimeAssetDeclaration[] assets = document.Assets
            .OrderBy(static asset => asset.AssetPath, StringComparer.Ordinal)
            .ThenBy(static asset => asset.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var assetIds = new HashSet<SemanticId>();
        var assetPaths = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < assets.Length; index++)
        {
            UnifiedScriptRuntimeAssetDeclaration? asset = assets[index];
            if (asset is null || asset.Id.IsEmpty ||
                string.IsNullOrWhiteSpace(asset.QualifiedName) ||
                string.IsNullOrWhiteSpace(asset.AssetPath) || asset.States is null ||
                !assetIds.Add(asset.Id) || !assetPaths.Add(asset.AssetPath))
            {
                throw new InvalidDataException("A runtime asset declaration is invalid or duplicated.");
            }

            UnifiedScriptRuntimeStateDeclaration[] states = asset.States
                .OrderBy(static state => state.Name, StringComparer.Ordinal)
                .ThenBy(static state => state.Id.Value, StringComparer.Ordinal)
                .ToArray();
            var stateIds = new HashSet<SemanticId>();
            var stateNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnifiedScriptRuntimeStateDeclaration? state in states)
            {
                if (state is null || state.Id.IsEmpty ||
                    string.IsNullOrWhiteSpace(state.Name) ||
                    string.IsNullOrWhiteSpace(state.TypeDisplayName) ||
                    string.IsNullOrWhiteSpace(state.TypeMetadataName) ||
                    !Enum.IsDefined(state.TypeKind) || state.DefaultValue is null ||
                    state.Exposure is null ||
                    !stateIds.Add(state.Id) || !stateNames.Add(state.Name) ||
                    state.Exposure.Category is not null &&
                        string.IsNullOrWhiteSpace(state.Exposure.Category))
                {
                    throw new InvalidDataException(
                        $"Runtime asset '{asset.AssetPath}' has an invalid or duplicate state.");
                }
                ValidateDefault(state);
            }
            if (asset.ClassBinding is not null)
            {
                try
                {
                    ScriptAssetJson.ValidateClassBinding(asset.ClassBinding, asset.AssetPath);
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException(
                        $"Runtime asset '{asset.AssetPath}' has an invalid class binding.",
                        exception);
                }
            }
            assets[index] = asset with { States = states };
        }

        return document with { Assets = assets };
    }

    private static void ValidateDefault(UnifiedScriptRuntimeStateDeclaration state)
    {
        bool hasStructIdentity = state.UnrealType?.Kind == ScriptFunctionValueTypeKind.Struct &&
            !string.IsNullOrWhiteSpace(state.UnrealType.UnrealTypePath) &&
            !string.IsNullOrWhiteSpace(state.UnrealType.UnrealNativeName);
        if (state.TypeKind == ScriptStateTypeKind.Struct != hasStructIdentity)
        {
            throw new InvalidDataException(
                $"Runtime state '{state.Name}' has an incompatible Unreal type identity.");
        }
        if (state.TypeKind is ScriptStateTypeKind.Array or ScriptStateTypeKind.Set or ScriptStateTypeKind.Map)
        {
            ValidateCollectionDefault(state);
        }
        if (state.TypeKind == ScriptStateTypeKind.Enum && state.UnrealType is not null &&
            (state.UnrealType.Kind != ScriptFunctionValueTypeKind.Enum ||
             string.IsNullOrWhiteSpace(state.UnrealType.UnrealTypePath) ||
             string.IsNullOrWhiteSpace(state.UnrealType.UnrealNativeName) ||
             state.UnrealType.Fields.Count != 0 ||
             state.DefaultValue.TextValue is null ||
             !long.TryParse(
                 state.DefaultValue.TextValue,
                 System.Globalization.NumberStyles.Integer,
                 System.Globalization.CultureInfo.InvariantCulture,
                 out _)))
        {
            throw new InvalidDataException(
                $"Runtime enum state '{state.Name}' has an incompatible Unreal type identity.");
        }
        if (state.TypeKind is ScriptStateTypeKind.SoftObject or ScriptStateTypeKind.SoftClass)
        {
            string expectedMetadata = state.TypeKind == ScriptStateTypeKind.SoftObject
                ? "UnifiedScript.Unreal.UnrealSoftObjectHandle"
                : "UnifiedScript.Unreal.UnrealSoftClassHandle";
            UnifiedScriptRuntimeStateValueKind expectedValueKind =
                state.TypeKind == ScriptStateTypeKind.SoftObject
                    ? UnifiedScriptRuntimeStateValueKind.SoftObject
                    : UnifiedScriptRuntimeStateValueKind.SoftClass;
            if (state.TypeMetadataName != expectedMetadata || state.UnrealType is not null ||
                state.DefaultValue.Kind != expectedValueKind ||
                !UnrealObjectPathValidator.IsValid(state.DefaultValue.TextValue))
            {
                throw new InvalidDataException(
                    $"Runtime soft reference state '{state.Name}' has an incompatible identity or path.");
            }
        }
        if (state.TypeKind is not (ScriptStateTypeKind.Enum or ScriptStateTypeKind.Struct or
            ScriptStateTypeKind.Array or ScriptStateTypeKind.Set or ScriptStateTypeKind.Map) &&
            state.UnrealType is not null)
        {
            throw new InvalidDataException(
                $"Runtime state '{state.Name}' has an unexpected Unreal type identity.");
        }

        bool valid = state.TypeKind switch
        {
            ScriptStateTypeKind.Boolean =>
                state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.Boolean,
            ScriptStateTypeKind.Integer =>
                state.DefaultValue.Kind is UnifiedScriptRuntimeStateValueKind.SignedInteger or
                    UnifiedScriptRuntimeStateValueKind.UnsignedInteger,
            ScriptStateTypeKind.FloatingPoint =>
                state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.FloatingPoint &&
                double.IsFinite(state.DefaultValue.FloatingPointValue),
            ScriptStateTypeKind.String =>
                state.DefaultValue.Kind is UnifiedScriptRuntimeStateValueKind.Null or
                    UnifiedScriptRuntimeStateValueKind.String,
            ScriptStateTypeKind.Enum =>
                state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.Enum,
            ScriptStateTypeKind.Name =>
                state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.Name &&
                state.DefaultValue.TextValue is not null,
            ScriptStateTypeKind.Text =>
                state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.Text &&
                state.DefaultValue.TextValue is not null,
            ScriptStateTypeKind.Object or ScriptStateTypeKind.Class =>
                state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.Null,
            ScriptStateTypeKind.SoftObject =>
                state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.SoftObject &&
                UnrealObjectPathValidator.IsValid(state.DefaultValue.TextValue),
            ScriptStateTypeKind.SoftClass =>
                state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.SoftClass &&
                UnrealObjectPathValidator.IsValid(state.DefaultValue.TextValue),
            ScriptStateTypeKind.Array => state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.Array,
            ScriptStateTypeKind.Set => state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.Set,
            ScriptStateTypeKind.Map => state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.Map,
            ScriptStateTypeKind.Struct =>
                IsValidStructDefault(state),
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException(
                $"Runtime state '{state.Name}' has an incompatible default value.");
        }
    }

    private static void ValidateCollectionDefault(UnifiedScriptRuntimeStateDeclaration state)
    {
        ScriptFunctionValueTypeKind expected = state.TypeKind switch
        {
            ScriptStateTypeKind.Array => ScriptFunctionValueTypeKind.Array,
            ScriptStateTypeKind.Set => ScriptFunctionValueTypeKind.Set,
            _ => ScriptFunctionValueTypeKind.Map,
        };
        ScriptFunctionValueTypeDeclaration? type = state.UnrealType;
        int arity = expected == ScriptFunctionValueTypeKind.Map ? 2 : 1;
        string definition = expected switch
        {
            ScriptFunctionValueTypeKind.Array => "System.Collections.Generic.IReadOnlyList",
            ScriptFunctionValueTypeKind.Set => "System.Collections.Generic.IReadOnlySet",
            _ => "System.Collections.Generic.IReadOnlyDictionary",
        };
        string expectedMetadata = type?.TypeArguments is null
            ? string.Empty
            : $"{definition}<{string.Join(", ", type.TypeArguments.Select(static a => a.TypeMetadataName))}>";
        if (type?.Kind != expected || type.UnrealTypePath != string.Empty ||
            type.Fields.Count != 0 || type.TypeArguments is null || type.TypeArguments.Count != arity ||
            type.UnrealNativeName != (expected == ScriptFunctionValueTypeKind.Array ? "TArray" :
                expected == ScriptFunctionValueTypeKind.Set ? "TSet" : "TMap") ||
            state.TypeMetadataName != expectedMetadata ||
            type.TypeArguments.Any(static argument => !ScriptAssetJson.IsSupportedCollectionLeaf(argument)) ||
            type.TypeArguments.Any(static argument => argument.UnrealType?.Kind is
                ScriptFunctionValueTypeKind.Array or ScriptFunctionValueTypeKind.Set or ScriptFunctionValueTypeKind.Map) ||
            state.DefaultValue.ContainerValues is null)
        {
            throw new InvalidDataException($"Runtime collection state '{state.Name}' has invalid type identity.");
        }
        try
        {
            IReadOnlyList<UnifiedScriptRuntimeStateValue> values = state.DefaultValue.ContainerValues;
            if (expected == ScriptFunctionValueTypeKind.Map && values.Count % 2 != 0)
                throw new InvalidDataException();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < values.Count; index++)
            {
                int argumentIndex = expected == ScriptFunctionValueTypeKind.Map ? index % 2 : 0;
                ScriptFunctionTypeArgumentDeclaration argument = type.TypeArguments[argumentIndex];
                UnifiedScriptRuntimeStateValue child = values[index];
                ValidateCollectionLeaf(child, argument);
                if (expected == ScriptFunctionValueTypeKind.Set &&
                    !identities.Add(CollectionIdentity(child, argument)) ||
                    expected == ScriptFunctionValueTypeKind.Map && index % 2 == 0 &&
                    !identities.Add(CollectionIdentity(child, argument)))
                    throw new InvalidDataException();
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidDataException)
        {
            throw new InvalidDataException($"Runtime collection state '{state.Name}' has an invalid default payload.", exception);
        }
    }

    private static void ValidateCollectionLeaf(
        UnifiedScriptRuntimeStateValue value,
        ScriptFunctionTypeArgumentDeclaration argument)
    {
        string metadata = argument.TypeMetadataName;
        if (!ScriptAssetJson.IsSupportedCollectionLeaf(argument))
            throw new InvalidDataException($"Collection leaf '{metadata}' is unsupported.");
        bool valid = metadata == "System.Boolean"
            ? value.Kind == UnifiedScriptRuntimeStateValueKind.Boolean
            : metadata == "System.Byte"
                ? value.Kind == UnifiedScriptRuntimeStateValueKind.UnsignedInteger &&
                    value.UnsignedIntegerValue <= byte.MaxValue
                : metadata == "System.Int32"
                    ? value.Kind == UnifiedScriptRuntimeStateValueKind.SignedInteger &&
                        value.SignedIntegerValue is >= int.MinValue and <= int.MaxValue
                    : metadata == "System.Int64"
                        ? value.Kind == UnifiedScriptRuntimeStateValueKind.SignedInteger
                        : metadata is "System.Single" or "System.Double"
                            ? value.Kind == UnifiedScriptRuntimeStateValueKind.FloatingPoint &&
                                (metadata != "System.Single" || float.IsFinite((float)value.FloatingPointValue)) &&
                                double.IsFinite(value.FloatingPointValue)
                            : metadata == "System.String"
                                ? value.Kind == UnifiedScriptRuntimeStateValueKind.String && value.TextValue is not null
                                : metadata == "UnifiedScript.Unreal.UnrealName"
                                    ? value.Kind == UnifiedScriptRuntimeStateValueKind.Name && value.TextValue is not null
                                    : metadata == "UnifiedScript.Unreal.UnrealText"
                                        ? value.Kind == UnifiedScriptRuntimeStateValueKind.Text && value.TextValue is not null
                                        : metadata is "UnifiedScript.Unreal.UnrealObjectHandle" or "UnifiedScript.Unreal.UnrealClassHandle"
                                            ? value.Kind == UnifiedScriptRuntimeStateValueKind.Null
                                            : metadata == "UnifiedScript.Unreal.UnrealSoftObjectHandle"
                                                ? value.Kind == UnifiedScriptRuntimeStateValueKind.SoftObject && UnrealObjectPathValidator.IsValid(value.TextValue)
                                                : metadata == "UnifiedScript.Unreal.UnrealSoftClassHandle"
                                                    ? value.Kind == UnifiedScriptRuntimeStateValueKind.SoftClass && UnrealObjectPathValidator.IsValid(value.TextValue)
                                                    : argument.UnrealType?.Kind == ScriptFunctionValueTypeKind.Enum
                                                        ? value.Kind == UnifiedScriptRuntimeStateValueKind.Enum &&
                                                            long.TryParse(value.TextValue,
                                                                System.Globalization.NumberStyles.Integer,
                                                                System.Globalization.CultureInfo.InvariantCulture,
                                                                out _)
                                                        : metadata == "UnifiedScript.Unreal.UnrealVector"
                                                            ? value.Kind == UnifiedScriptRuntimeStateValueKind.Vector &&
                                                                double.IsFinite(value.VectorX) &&
                                                                double.IsFinite(value.VectorY) &&
                                                                double.IsFinite(value.VectorZ)
                                                            : metadata == "UnifiedScript.Unreal.UnrealRotator"
                                                                ? value.Kind == UnifiedScriptRuntimeStateValueKind.Rotator &&
                                                                    double.IsFinite(value.RotatorPitch) &&
                                                                    double.IsFinite(value.RotatorYaw) &&
                                                                    double.IsFinite(value.RotatorRoll)
                                                                : metadata == "UnifiedScript.Unreal.UnrealTransform"
                                                                    ? IsValidCollectionTransform(value)
                                                                    : false;
        if (!valid)
            throw new InvalidDataException($"Collection leaf '{metadata}' has an incompatible default kind.");
    }

    private static bool IsValidCollectionTransform(UnifiedScriptRuntimeStateValue value)
    {
        double lengthSquared = value.TransformRotationX * value.TransformRotationX +
            value.TransformRotationY * value.TransformRotationY +
            value.TransformRotationZ * value.TransformRotationZ +
            value.TransformRotationW * value.TransformRotationW;
        return value.Kind == UnifiedScriptRuntimeStateValueKind.Transform &&
            double.IsFinite(lengthSquared) && Math.Abs(lengthSquared - 1.0) <= 1e-6 &&
            double.IsFinite(value.TransformTranslationX) &&
            double.IsFinite(value.TransformTranslationY) &&
            double.IsFinite(value.TransformTranslationZ) &&
            double.IsFinite(value.TransformScaleX) &&
            double.IsFinite(value.TransformScaleY) &&
            double.IsFinite(value.TransformScaleZ);
    }

    private static string CollectionIdentity(
        UnifiedScriptRuntimeStateValue value,
        ScriptFunctionTypeArgumentDeclaration argument) =>
        ScriptAssetJson.CollectionIdentity(ToIdentityJson(value, argument), argument);

    private static JsonElement ToIdentityJson(
        UnifiedScriptRuntimeStateValue value,
        ScriptFunctionTypeArgumentDeclaration argument)
    {
        string metadata = argument.TypeMetadataName;
        if (metadata == "System.Boolean")
            return JsonSerializer.SerializeToElement(value.BooleanValue);
        if (metadata == "System.Byte")
            return JsonSerializer.SerializeToElement(value.UnsignedIntegerValue);
        if (metadata is "System.Int32" or "System.Int64")
            return JsonSerializer.SerializeToElement(value.SignedIntegerValue);
        if (metadata is "System.Single" or "System.Double")
            return JsonSerializer.SerializeToElement(value.FloatingPointValue);
        if (metadata == "UnifiedScript.Unreal.UnrealVector")
            return JsonSerializer.SerializeToElement(new
            {
                x = value.VectorX,
                y = value.VectorY,
                z = value.VectorZ,
            });
        if (metadata == "UnifiedScript.Unreal.UnrealRotator")
            return JsonSerializer.SerializeToElement(new
            {
                pitch = value.RotatorPitch,
                yaw = value.RotatorYaw,
                roll = value.RotatorRoll,
            });
        if (metadata == "UnifiedScript.Unreal.UnrealTransform")
            return JsonSerializer.SerializeToElement(new
            {
                rotation = new
                {
                    x = value.TransformRotationX,
                    y = value.TransformRotationY,
                    z = value.TransformRotationZ,
                    w = value.TransformRotationW,
                },
                translation = new
                {
                    x = value.TransformTranslationX,
                    y = value.TransformTranslationY,
                    z = value.TransformTranslationZ,
                },
                scale3D = new
                {
                    x = value.TransformScaleX,
                    y = value.TransformScaleY,
                    z = value.TransformScaleZ,
                },
            });
        if (metadata is "UnifiedScript.Unreal.UnrealObjectHandle" or
            "UnifiedScript.Unreal.UnrealClassHandle")
            return JsonSerializer.SerializeToElement<object?>(null);
        return JsonSerializer.SerializeToElement(value.TextValue ?? string.Empty);
    }

    private static bool IsValidStructDefault(UnifiedScriptRuntimeStateDeclaration state)
    {
        if (state.TypeMetadataName == "UnifiedScript.Unreal.UnrealVector")
        {
            return state.UnrealType?.UnrealTypePath == "/Script/CoreUObject.Vector" &&
                state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.Vector &&
                double.IsFinite(state.DefaultValue.VectorX) &&
                double.IsFinite(state.DefaultValue.VectorY) &&
                double.IsFinite(state.DefaultValue.VectorZ);
        }
        if (state.TypeMetadataName == "UnifiedScript.Unreal.UnrealRotator")
        {
            return state.UnrealType?.UnrealTypePath == "/Script/CoreUObject.Rotator" &&
                state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.Rotator &&
                double.IsFinite(state.DefaultValue.RotatorPitch) &&
                double.IsFinite(state.DefaultValue.RotatorYaw) &&
                double.IsFinite(state.DefaultValue.RotatorRoll);
        }
        if (state.TypeMetadataName == "UnifiedScript.Unreal.UnrealTransform")
        {
            double lengthSquared = state.DefaultValue.TransformRotationX * state.DefaultValue.TransformRotationX +
                state.DefaultValue.TransformRotationY * state.DefaultValue.TransformRotationY +
                state.DefaultValue.TransformRotationZ * state.DefaultValue.TransformRotationZ +
                state.DefaultValue.TransformRotationW * state.DefaultValue.TransformRotationW;
            return state.UnrealType?.UnrealTypePath == "/Script/CoreUObject.Transform" &&
                state.DefaultValue.Kind == UnifiedScriptRuntimeStateValueKind.Transform &&
                double.IsFinite(lengthSquared) && Math.Abs(lengthSquared - 1.0) <= 1e-6 &&
                double.IsFinite(state.DefaultValue.TransformTranslationX) &&
                double.IsFinite(state.DefaultValue.TransformTranslationY) &&
                double.IsFinite(state.DefaultValue.TransformTranslationZ) &&
                double.IsFinite(state.DefaultValue.TransformScaleX) &&
                double.IsFinite(state.DefaultValue.TransformScaleY) &&
                double.IsFinite(state.DefaultValue.TransformScaleZ);
        }
        return false;
    }
}

public sealed record CSharpProgramChunkOptions(
    UnifiedScriptRuntimeBindingManifest? Bindings = null,
    IReadOnlyList<string>? RuntimeReferencePaths = null,
    bool IncludePortablePdb = true,
    IReadOnlyList<string>? EntryTypeNames = null);

public sealed record CSharpProgramChunkEmitResult(
    byte[] Content,
    UnifiedScriptChunk Chunk,
    UnifiedScriptRuntimeManifest RuntimeManifest,
    UnifiedScriptRuntimeDeclarationDocument Declarations,
    UnifiedScriptRuntimeBindingManifest Bindings,
    UnifiedScriptRuntimeDependencyManifest Dependencies);

public sealed class CSharpProgramChunkCompiler
{
    public CSharpProgramChunkEmitResult Compile(
        CSharpProgramImage image,
        CSharpProgramChunkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= new CSharpProgramChunkOptions();
        ProgramImageKey imageKey = ProgramImageKey.FromImage(image);
        string[] availableTypes = image.Hir.Types
            .Select(static type => type.QualifiedName)
            .Concat(image.ScriptAssets.Assets.Select(static asset => asset.QualifiedName))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] entryTypes = (options.EntryTypeNames ?? availableTypes)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (entryTypes.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("Chunk entry type names cannot be blank.");
        }
        var runtimeManifest = new UnifiedScriptRuntimeManifest(
            image.AssemblyName,
            image.Semantics.SourceCompilationId,
            image.Semantics.ConfigurationId,
            imageKey,
            entryTypes);
        UnifiedScriptRuntimeDeclarationDocument declarations =
            UnifiedScriptRuntimeDeclarationBinary.FromScriptAssets(image.ScriptAssets);
        UnifiedScriptRuntimeBindingManifest bindings = options.Bindings ??
            UnifiedScriptRuntimeBindingManifest.Empty;
        UnifiedScriptRuntimeDependencyManifest dependencies = CreateDependencies(
            options.RuntimeReferencePaths ?? []);
        var sections = new List<UnifiedScriptChunkSection>
        {
            RuntimeSection(
                UnifiedScriptChunkSectionKind.RuntimeManifest,
                UnifiedScriptRuntimeManifestBinary.Encode(runtimeManifest)),
            RuntimeSection(
                UnifiedScriptChunkSectionKind.ManagedAssembly,
                image.PortableExecutable),
            RuntimeSection(
                UnifiedScriptChunkSectionKind.DeclarationData,
                UnifiedScriptRuntimeDeclarationBinary.Encode(declarations)),
            RuntimeSection(
                UnifiedScriptChunkSectionKind.BindingManifest,
                UnifiedScriptRuntimeBindingManifestBinary.Encode(bindings)),
            RuntimeSection(
                UnifiedScriptChunkSectionKind.DependencyManifest,
                UnifiedScriptRuntimeDependencyManifestBinary.Encode(dependencies)),
        };
        if (options.IncludePortablePdb)
        {
            sections.Add(new UnifiedScriptChunkSection(
                UnifiedScriptChunkSectionKind.PortablePdb,
                UnifiedScriptChunkSectionFlags.Debug,
                image.PortablePdb));
        }

        byte[] content = UnifiedScriptChunkCodec.Write(sections);
        UnifiedScriptChunk chunk = UnifiedScriptProgramChunk.Read(content);
        return new CSharpProgramChunkEmitResult(
            content,
            chunk,
            runtimeManifest,
            declarations,
            bindings,
            dependencies);
    }

    private static UnifiedScriptChunkSection RuntimeSection(
        UnifiedScriptChunkSectionKind kind,
        byte[] content) => new(
            kind,
            UnifiedScriptChunkSectionFlags.RuntimeRequired,
            content);

    private static UnifiedScriptRuntimeDependencyManifest CreateDependencies(
        IEnumerable<string> paths)
    {
        UnifiedScriptRuntimeDependency[] dependencies = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path =>
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("Runtime chunk dependency does not exist.", path);
                }
                AssemblyName assemblyName = AssemblyName.GetAssemblyName(path);
                using FileStream stream = File.OpenRead(path);
                string digest = "sha256:" +
                    Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                return new UnifiedScriptRuntimeDependency(
                    UnifiedScriptRuntimeDependencyKind.ManagedAssembly,
                    assemblyName.Name ?? Path.GetFileNameWithoutExtension(path),
                    assemblyName.Version?.ToString() ?? "0.0.0.0",
                    digest,
                    Optional: false);
            })
            .ToArray();
        return new UnifiedScriptRuntimeDependencyManifest(dependencies);
    }
}

internal sealed class ChunkPayloadWriter
{
    private readonly MemoryStream stream = new();

    public ChunkPayloadWriter(ReadOnlySpan<byte> magic, ushort minorVersion = 0)
    {
        if (magic.Length != 8)
        {
            throw new ArgumentException("A chunk payload magic must contain eight bytes.", nameof(magic));
        }
        stream.Write(magic);
        WriteUInt16(1);
        WriteUInt16(minorVersion);
        WriteUInt32(0);
    }

    public void WriteByte(byte value) => stream.WriteByte(value);

    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    public void WriteUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    public void WriteUInt64(ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    public void WriteInt64(long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    public void WriteDouble(double value) =>
        WriteInt64(BitConverter.DoubleToInt64Bits(value));

    public void WriteCount(int value)
    {
        if (value < 0 || value > 1_000_000)
        {
            throw new InvalidDataException($"Chunk payload count '{value}' is outside supported bounds.");
        }
        WriteUInt32(checked((uint)value));
    }

    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("A chunk payload string exceeds 16 MiB.");
        }
        WriteUInt32(checked((uint)bytes.Length));
        stream.Write(bytes);
    }

    public void WriteNullableString(string? value)
    {
        WriteBoolean(value is not null);
        if (value is not null)
        {
            WriteString(value);
        }
    }

    public void WriteSemanticId(SemanticId id) => WriteString(id.Value);

    public void WriteDigest(string digest)
    {
        if (!ProgramImageKey.TryParse(digest, out _))
        {
            throw new InvalidDataException($"Chunk payload digest '{digest}' is invalid.");
        }
        WriteString(digest);
    }

    public byte[] ToArray() => stream.ToArray();
}

internal ref struct ChunkPayloadReader
{
    private readonly ReadOnlySpan<byte> source;
    private int offset;

    public ChunkPayloadReader(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> expectedMagic,
        ushort maximumMinorVersion = 0)
    {
        if (source.Length < 16 || !source[..8].SequenceEqual(expectedMagic))
        {
            throw new InvalidDataException("Compiled chunk payload magic is invalid.");
        }
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(source[8..]);
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(source[10..]);
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(source[12..]);
        if (major != 1 || minor > maximumMinorVersion || reserved != 0)
        {
            throw new InvalidDataException(
                $"Unsupported compiled chunk payload format {major}.{minor}.");
        }
        this.source = source;
        offset = 16;
        MinorVersion = minor;
    }

    public ushort MinorVersion { get; }

    public byte ReadByte()
    {
        Require(sizeof(byte));
        return source[offset++];
    }

    public bool ReadBoolean() => ReadByte() switch
    {
        0 => false,
        1 => true,
        byte value => throw new InvalidDataException(
            $"Chunk payload boolean byte '{value}' is invalid."),
    };

    public ushort ReadUInt16()
    {
        Require(sizeof(ushort));
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
        offset += sizeof(ushort);
        return value;
    }

    public uint ReadUInt32()
    {
        Require(sizeof(uint));
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
        offset += sizeof(uint);
        return value;
    }

    public ulong ReadUInt64()
    {
        Require(sizeof(ulong));
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);
        offset += sizeof(ulong);
        return value;
    }

    public long ReadInt64()
    {
        Require(sizeof(long));
        long value = BinaryPrimitives.ReadInt64LittleEndian(source[offset..]);
        offset += sizeof(long);
        return value;
    }

    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

    public int ReadCount()
    {
        uint value = ReadUInt32();
        if (value > 1_000_000)
        {
            throw new InvalidDataException($"Chunk payload count '{value}' exceeds the limit.");
        }
        return checked((int)value);
    }

    public string ReadString()
    {
        uint rawLength = ReadUInt32();
        if (rawLength > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("A chunk payload string exceeds 16 MiB.");
        }
        int length = checked((int)rawLength);
        Require(length);
        string value;
        try
        {
            value = new UTF8Encoding(false, true).GetString(source.Slice(offset, length));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Chunk payload contains invalid UTF-8.", exception);
        }
        offset += length;
        return value;
    }

    public string? ReadNullableString() => ReadBoolean() ? ReadString() : null;

    public SemanticId ReadSemanticId()
    {
        string value = ReadString();
        return SemanticId.TryParse(value, out SemanticId id)
            ? id
            : throw new InvalidDataException($"Chunk payload semantic ID '{value}' is invalid.");
    }

    public string ReadDigest()
    {
        string value = ReadString();
        return ProgramImageKey.TryParse(value, out _)
            ? value
            : throw new InvalidDataException($"Chunk payload digest '{value}' is invalid.");
    }

    public void RequireEnd()
    {
        if (offset != source.Length)
        {
            throw new InvalidDataException("Compiled chunk payload contains trailing bytes.");
        }
    }

    private void Require(int length)
    {
        if (length < 0 || length > source.Length - offset)
        {
            throw new InvalidDataException("Compiled chunk payload is truncated.");
        }
    }
}
