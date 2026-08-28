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
}

public sealed record UnifiedScriptRuntimeStateValue(
    UnifiedScriptRuntimeStateValueKind Kind,
    bool BooleanValue = false,
    long SignedIntegerValue = 0,
    ulong UnsignedIntegerValue = 0,
    double FloatingPointValue = 0,
    string? TextValue = null);

public sealed record UnifiedScriptRuntimeStateDeclaration(
    SemanticId Id,
    string Name,
    string TypeDisplayName,
    string TypeMetadataName,
    ScriptStateTypeKind TypeKind,
    UnifiedScriptRuntimeStateValue DefaultValue,
    ScriptExposure Exposure);

public sealed record UnifiedScriptRuntimeAssetDeclaration(
    SemanticId Id,
    string QualifiedName,
    string AssetPath,
    IReadOnlyList<UnifiedScriptRuntimeStateDeclaration> States);

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

    public static byte[] Encode(UnifiedScriptRuntimeDeclarationDocument document)
    {
        UnifiedScriptRuntimeDeclarationDocument normalized = Normalize(document);
        var writer = new ChunkPayloadWriter(Magic);
        writer.WriteSemanticId(normalized.SourceCompilationId);
        writer.WriteSemanticId(normalized.SemanticConfigurationId);
        writer.WriteCount(normalized.Assets.Count);
        foreach (UnifiedScriptRuntimeAssetDeclaration asset in normalized.Assets)
        {
            writer.WriteSemanticId(asset.Id);
            writer.WriteString(asset.QualifiedName);
            writer.WriteString(asset.AssetPath);
            writer.WriteCount(asset.States.Count);
            foreach (UnifiedScriptRuntimeStateDeclaration state in asset.States)
            {
                writer.WriteSemanticId(state.Id);
                writer.WriteString(state.Name);
                writer.WriteString(state.TypeDisplayName);
                writer.WriteString(state.TypeMetadataName);
                writer.WriteByte(checked((byte)state.TypeKind));
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
        var reader = new ChunkPayloadReader(source, Magic);
        SemanticId compilationId = reader.ReadSemanticId();
        SemanticId configurationId = reader.ReadSemanticId();
        int assetCount = reader.ReadCount();
        var assets = new UnifiedScriptRuntimeAssetDeclaration[assetCount];
        for (int assetIndex = 0; assetIndex < assets.Length; assetIndex++)
        {
            SemanticId assetId = reader.ReadSemanticId();
            string qualifiedName = reader.ReadString();
            string assetPath = reader.ReadString();
            int stateCount = reader.ReadCount();
            var states = new UnifiedScriptRuntimeStateDeclaration[stateCount];
            for (int stateIndex = 0; stateIndex < states.Length; stateIndex++)
            {
                SemanticId stateId = reader.ReadSemanticId();
                string name = reader.ReadString();
                string displayName = reader.ReadString();
                string metadataName = reader.ReadString();
                var typeKind = (ScriptStateTypeKind)reader.ReadByte();
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
                        category));
            }
            assets[assetIndex] = new UnifiedScriptRuntimeAssetDeclaration(
                assetId,
                qualifiedName,
                assetPath,
                states);
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
                            state.Exposure)).ToArray())).ToArray()));
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
            _ => throw new InvalidDataException(
                $"Script state '{state.Name}' has an unsupported runtime default."),
        };

    private static void WriteDefault(
        ChunkPayloadWriter writer,
        UnifiedScriptRuntimeStateValue value)
    {
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
                writer.WriteString(value.TextValue ?? throw new InvalidDataException(
                    "A runtime string/enum default requires text."));
                break;
            default:
                throw new InvalidDataException($"Unsupported runtime state value '{value.Kind}'.");
        }
    }

    private static UnifiedScriptRuntimeStateValue ReadDefault(ref ChunkPayloadReader reader)
    {
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
                UnifiedScriptRuntimeStateValueKind.Enum => new(
                    kind,
                    TextValue: reader.ReadString()),
            _ => throw new InvalidDataException($"Unsupported runtime state value '{kind}'."),
        };
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
            assets[index] = asset with { States = states };
        }

        return document with { Assets = assets };
    }

    private static void ValidateDefault(UnifiedScriptRuntimeStateDeclaration state)
    {
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
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException(
                $"Runtime state '{state.Name}' has an incompatible default value.");
        }
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

    public ChunkPayloadWriter(ReadOnlySpan<byte> magic)
    {
        if (magic.Length != 8)
        {
            throw new ArgumentException("A chunk payload magic must contain eight bytes.", nameof(magic));
        }
        stream.Write(magic);
        WriteUInt16(1);
        WriteUInt16(0);
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

    public ChunkPayloadReader(ReadOnlySpan<byte> source, ReadOnlySpan<byte> expectedMagic)
    {
        if (source.Length < 16 || !source[..8].SequenceEqual(expectedMagic))
        {
            throw new InvalidDataException("Compiled chunk payload magic is invalid.");
        }
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(source[8..]);
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(source[10..]);
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(source[12..]);
        if (major != 1 || minor != 0 || reserved != 0)
        {
            throw new InvalidDataException(
                $"Unsupported compiled chunk payload format {major}.{minor}.");
        }
        this.source = source;
        offset = 16;
    }

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
