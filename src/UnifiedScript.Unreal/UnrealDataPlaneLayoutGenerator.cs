using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedScript.Core;

namespace UnifiedScript.Unreal;

public sealed record UnrealDataPlaneLayoutLimits(
    uint TransformSnapshots,
    uint GeneratedSnapshotEntries,
    uint GeneratedEventEntries,
    uint GeneratedChannelBytes,
    uint Commands,
    uint Events,
    uint TraceRequests,
    uint OverlapRequests);

public sealed record UnrealDataPlaneExternalType(
    string Name,
    string NativeType,
    string ManagedType,
    int Size,
    int Alignment);

public sealed record UnrealDataPlaneEnumValue(string Name, ulong Value);

public sealed record UnrealDataPlaneEnum(
    string Name,
    string NativeName,
    string ManagedName,
    string UnderlyingType,
    bool Flags,
    IReadOnlyList<UnrealDataPlaneEnumValue> Values);

public sealed record UnrealDataPlaneField(
    string Name,
    string Type,
    string? NativeDefault = null);

public sealed record UnrealDataPlaneStruct(
    string Name,
    string NativeName,
    string ManagedName,
    string ManagedVisibility,
    bool ManagedReadonly,
    string Domain,
    string Role,
    int ExpectedSize,
    IReadOnlyList<UnrealDataPlaneField> Fields,
    string? SchemaId = null,
    string? SchemaConstant = null,
    string? SizeConstant = null);

public sealed record UnrealDataPlaneLayoutSchema(
    string SchemaVersion,
    string ContractName,
    uint AbiVersion,
    uint Revision,
    int Alignment,
    string CompatibilityId,
    UnrealDataPlaneLayoutLimits Limits,
    IReadOnlyList<UnrealDataPlaneExternalType> ExternalTypes,
    IReadOnlyList<UnrealDataPlaneEnum> Enums,
    IReadOnlyList<UnrealDataPlaneStruct> Structs)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.unreal-data-plane-layout/v0.1";
}

public static class UnrealDataPlaneLayoutJson
{
    public static UnrealDataPlaneLayoutSchema Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        UnrealDataPlaneLayoutSchema? schema =
            JsonSerializer.Deserialize<UnrealDataPlaneLayoutSchema>(json, CreateOptions());
        if (schema is null)
        {
            throw new JsonException("The Unreal data-plane layout schema was empty.");
        }
        return UnrealDataPlaneLayoutValidator.Normalize(schema);
    }

    public static string Serialize(UnrealDataPlaneLayoutSchema schema) =>
        JsonSerializer.Serialize(
                UnrealDataPlaneLayoutValidator.Normalize(schema),
                CreateOptions(writeIndented: true))
            .ReplaceLineEndings("\n") + "\n";

    private static JsonSerializerOptions CreateOptions(bool writeIndented = false)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}

public sealed record UnrealDataPlaneLayoutGenerationResult(
    string ManagedSource,
    string NativeSource,
    ulong SchemaFingerprint);

public sealed class UnrealDataPlaneLayoutGenerator
{
    private sealed record TypeLayout(
        string NativeType,
        string ManagedType,
        int Size,
        int Alignment,
        UnrealDataPlaneEnum? Enum = null,
        UnrealDataPlaneStruct? Struct = null,
        bool IsPointer = false,
        bool IsConstPointer = false);

    public UnrealDataPlaneLayoutGenerationResult Generate(
        UnrealDataPlaneLayoutSchema input)
    {
        UnrealDataPlaneLayoutSchema schema =
            UnrealDataPlaneLayoutValidator.Normalize(input);
        IReadOnlyList<UnrealDataPlaneStruct> orderedStructs =
            OrderStructs(schema.Structs);
        ulong compatibilityId = ParseHexId(schema.CompatibilityId, "compatibilityId");
        ulong fingerprint = ComputeFingerprint(schema);
        Dictionary<string, TypeLayout> types = CreateTypeMap(schema, orderedStructs);

        ValidateLayouts(schema, orderedStructs, types);
        return new UnrealDataPlaneLayoutGenerationResult(
            GenerateManaged(schema, orderedStructs, types, compatibilityId, fingerprint),
            GenerateNative(schema, orderedStructs, types, compatibilityId, fingerprint),
            fingerprint);
    }

    private static Dictionary<string, TypeLayout> CreateTypeMap(
        UnrealDataPlaneLayoutSchema schema,
        IReadOnlyList<UnrealDataPlaneStruct> orderedStructs)
    {
        var types = new Dictionary<string, TypeLayout>(StringComparer.Ordinal)
        {
            ["u8"] = new("uint8", "byte", 1, 1),
            ["u16"] = new("uint16", "ushort", 2, 2),
            ["u32"] = new("uint32", "uint", 4, 4),
            ["u64"] = new("uint64", "ulong", 8, 8),
            ["i32"] = new("int32", "int", 4, 4),
            ["f64"] = new("double", "double", 8, 8),
        };
        foreach (UnrealDataPlaneExternalType external in schema.ExternalTypes)
        {
            types.Add(
                external.Name,
                new TypeLayout(
                    external.NativeType,
                    external.ManagedType,
                    external.Size,
                    external.Alignment));
        }
        foreach (UnrealDataPlaneEnum item in schema.Enums)
        {
            TypeLayout underlying = types[item.UnderlyingType];
            types.Add(
                item.Name,
                new TypeLayout(
                    item.NativeName,
                    item.ManagedName,
                    underlying.Size,
                    underlying.Alignment,
                    Enum: item));
        }
        foreach (UnrealDataPlaneStruct item in orderedStructs)
        {
            types.Add(
                item.Name,
                new TypeLayout(
                    item.NativeName,
                    item.ManagedName,
                    item.ExpectedSize,
                    schema.Alignment,
                    Struct: item));
        }
        return types;
    }

    private static void ValidateLayouts(
        UnrealDataPlaneLayoutSchema schema,
        IReadOnlyList<UnrealDataPlaneStruct> structs,
        IReadOnlyDictionary<string, TypeLayout> types)
    {
        foreach (UnrealDataPlaneStruct item in structs)
        {
            int offset = 0;
            int largestAlignment = 1;
            foreach (UnrealDataPlaneField field in item.Fields)
            {
                TypeLayout fieldType = ResolveType(field.Type, types);
                int alignment = Math.Min(fieldType.Alignment, schema.Alignment);
                offset = Align(offset, alignment);
                offset = checked(offset + fieldType.Size);
                largestAlignment = Math.Max(largestAlignment, alignment);
                ValidateNativeDefault(field, fieldType);
            }
            int size = Align(offset, largestAlignment);
            if (size != item.ExpectedSize)
            {
                throw new JsonException(
                    $"Data-plane struct '{item.Name}' computes to {size} bytes, " +
                    $"but expectedSize is {item.ExpectedSize}.");
            }
        }
    }

    private static string GenerateManaged(
        UnrealDataPlaneLayoutSchema schema,
        IReadOnlyList<UnrealDataPlaneStruct> structs,
        IReadOnlyDictionary<string, TypeLayout> types,
        ulong compatibilityId,
        ulong fingerprint)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("// Generated from the versioned UnifiedScript data-plane layout schema.");
        source.AppendLine("using System.Runtime.InteropServices;");
        source.AppendLine();
        source.AppendLine("namespace UnifiedScript.Unreal;");
        source.AppendLine();
        source.AppendLine("public static class UnrealDataPlaneLayoutV1");
        source.AppendLine("{");
        source.AppendLine($"    public const uint Revision = {schema.Revision}U;");
        source.AppendLine($"    public const ulong CompatibilityId = {Hex(compatibilityId, "UL")};");
        source.AppendLine($"    public const ulong SchemaFingerprint = {Hex(fingerprint, "UL")};");
        source.AppendLine("}");
        source.AppendLine();
        EmitManagedConstants(source, schema, structs, "shared", "UnrealSharedDataAbiV4");
        source.AppendLine();
        EmitManagedConstants(source, schema, structs, "gameplay", "UnrealGameplayDataAbiV4");

        foreach (UnrealDataPlaneEnum item in schema.Enums)
        {
            source.AppendLine();
            if (item.Flags)
            {
                source.AppendLine("[Flags]");
            }
            source.AppendLine(
                $"public enum {item.ManagedName} : {types[item.UnderlyingType].ManagedType}");
            source.AppendLine("{");
            foreach (UnrealDataPlaneEnumValue value in item.Values)
            {
                source.AppendLine($"    {value.Name} = {ManagedEnumLiteral(value.Value, item.UnderlyingType)},");
            }
            source.AppendLine("}");
        }

        foreach (UnrealDataPlaneStruct item in structs)
        {
            source.AppendLine();
            source.AppendLine($"[StructLayout(LayoutKind.Sequential, Pack = {schema.Alignment})]");
            string readonlyToken = item.ManagedReadonly ? " readonly" : string.Empty;
            source.AppendLine(
                $"{item.ManagedVisibility}{readonlyToken} partial struct {item.ManagedName}");
            source.AppendLine("{");
            foreach (UnrealDataPlaneField field in item.Fields)
            {
                TypeLayout fieldType = ResolveType(field.Type, types);
                string fieldReadonly = item.ManagedReadonly ? " readonly" : string.Empty;
                source.AppendLine(
                    $"    public{fieldReadonly} {fieldType.ManagedType} {field.Name};");
            }
            source.AppendLine("}");
        }
        return source.ToString().ReplaceLineEndings("\n");
    }

    private static void EmitManagedConstants(
        StringBuilder source,
        UnrealDataPlaneLayoutSchema schema,
        IReadOnlyList<UnrealDataPlaneStruct> structs,
        string domain,
        string className)
    {
        source.AppendLine($"public static partial class {className}");
        source.AppendLine("{");
        source.AppendLine($"    public const uint Version = {schema.AbiVersion}U;");
        if (domain == "shared")
        {
            source.AppendLine(
                $"    public const uint MaximumTransformSnapshotCount = {schema.Limits.TransformSnapshots}U;");
            source.AppendLine(
                $"    public const uint MaximumGeneratedSnapshotCount = {schema.Limits.GeneratedSnapshotEntries}U;");
            source.AppendLine(
                $"    public const uint MaximumGeneratedChannelBytes = {schema.Limits.GeneratedChannelBytes}U;");
        }
        else
        {
            source.AppendLine($"    public const uint MaximumCommandCount = {schema.Limits.Commands}U;");
            source.AppendLine($"    public const uint MaximumEventCount = {schema.Limits.Events}U;");
            source.AppendLine($"    public const uint MaximumTraceCount = {schema.Limits.TraceRequests}U;");
            source.AppendLine($"    public const uint MaximumOverlapCount = {schema.Limits.OverlapRequests}U;");
            source.AppendLine(
                $"    public const uint MaximumGeneratedEventCount = {schema.Limits.GeneratedEventEntries}U;");
            source.AppendLine(
                $"    public const uint MaximumGeneratedChannelBytes = {schema.Limits.GeneratedChannelBytes}U;");
        }
        foreach (UnrealDataPlaneStruct item in structs.Where(item => item.Domain == domain))
        {
            if (item.SchemaConstant is not null)
            {
                ulong schemaId = ParseHexId(item.SchemaId!, $"{item.Name}.schemaId");
                source.AppendLine(
                    $"    public const ulong {item.SchemaConstant} = {Hex(schemaId, "UL")};");
            }
            if (item.SizeConstant is not null)
            {
                source.AppendLine(
                    $"    public const int {item.SizeConstant} = {item.ExpectedSize};");
            }
        }
        source.AppendLine("}");
    }

    private static string GenerateNative(
        UnrealDataPlaneLayoutSchema schema,
        IReadOnlyList<UnrealDataPlaneStruct> structs,
        IReadOnlyDictionary<string, TypeLayout> types,
        ulong compatibilityId,
        ulong fingerprint)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("// Generated from the versioned UnifiedScript data-plane layout schema.");
        source.AppendLine("#pragma once");
        source.AppendLine();
        source.AppendLine("namespace UE::UnifiedScript::DataPlaneSchema");
        source.AppendLine("{");
        source.AppendLine($"inline constexpr uint32 Revision = {schema.Revision}u;");
        source.AppendLine($"inline constexpr uint64 CompatibilityId = {Hex(compatibilityId, "ull")};");
        source.AppendLine($"inline constexpr uint64 SchemaFingerprint = {Hex(fingerprint, "ull")};");
        source.AppendLine("}");
        source.AppendLine();
        EmitNativeConstants(source, schema, structs, "shared", "SharedData");
        source.AppendLine();
        EmitNativeConstants(source, schema, structs, "gameplay", "GameplayData");

        foreach (UnrealDataPlaneEnum item in schema.Enums)
        {
            source.AppendLine();
            source.AppendLine(
                $"enum class {item.NativeName} : {types[item.UnderlyingType].NativeType}");
            source.AppendLine("{");
            foreach (UnrealDataPlaneEnumValue value in item.Values)
            {
                source.AppendLine($"    {value.Name} = {NativeEnumLiteral(value.Value, item.UnderlyingType)},");
            }
            source.AppendLine("};");
        }

        foreach (UnrealDataPlaneStruct item in structs)
        {
            source.AppendLine();
            source.AppendLine($"struct alignas({schema.Alignment}) {item.NativeName}");
            source.AppendLine("{");
            foreach (UnrealDataPlaneField field in item.Fields)
            {
                TypeLayout fieldType = ResolveType(field.Type, types);
                string nativeDefault = GetNativeDefault(field, fieldType);
                source.AppendLine(
                    $"    {fieldType.NativeType} {field.Name} = {nativeDefault};");
            }
            source.AppendLine("};");
        }

        foreach (UnrealDataPlaneStruct item in structs)
        {
            source.AppendLine();
            source.AppendLine($"static_assert(std::is_standard_layout_v<{item.NativeName}>);");
            source.AppendLine($"static_assert(std::is_trivially_copyable_v<{item.NativeName}>);");
            source.AppendLine($"static_assert(sizeof({item.NativeName}) == {item.ExpectedSize});");
            int offset = 0;
            foreach (UnrealDataPlaneField field in item.Fields)
            {
                TypeLayout fieldType = ResolveType(field.Type, types);
                offset = Align(offset, Math.Min(fieldType.Alignment, schema.Alignment));
                source.AppendLine(
                    $"static_assert(STRUCT_OFFSET({item.NativeName}, {field.Name}) == {offset});");
                offset += fieldType.Size;
            }
        }
        return source.ToString().ReplaceLineEndings("\n");
    }

    private static void EmitNativeConstants(
        StringBuilder source,
        UnrealDataPlaneLayoutSchema schema,
        IReadOnlyList<UnrealDataPlaneStruct> structs,
        string domain,
        string namespaceName)
    {
        source.AppendLine($"namespace UE::UnifiedScript::{namespaceName}");
        source.AppendLine("{");
        source.AppendLine($"inline constexpr uint32 AbiVersion = {schema.AbiVersion}u;");
        if (domain == "shared")
        {
            source.AppendLine(
                $"inline constexpr uint32 MaximumTransformSnapshotCount = {schema.Limits.TransformSnapshots}u;");
            source.AppendLine(
                $"inline constexpr uint32 MaximumGeneratedSnapshotCount = {schema.Limits.GeneratedSnapshotEntries}u;");
            source.AppendLine(
                $"inline constexpr uint32 MaximumGeneratedChannelBytes = {schema.Limits.GeneratedChannelBytes}u;");
        }
        else
        {
            source.AppendLine($"inline constexpr uint32 MaximumCommandCount = {schema.Limits.Commands}u;");
            source.AppendLine($"inline constexpr uint32 MaximumEventCount = {schema.Limits.Events}u;");
            source.AppendLine($"inline constexpr uint32 MaximumTraceCount = {schema.Limits.TraceRequests}u;");
            source.AppendLine($"inline constexpr uint32 MaximumOverlapCount = {schema.Limits.OverlapRequests}u;");
            source.AppendLine(
                $"inline constexpr uint32 MaximumGeneratedEventCount = {schema.Limits.GeneratedEventEntries}u;");
            source.AppendLine(
                $"inline constexpr uint32 MaximumGeneratedChannelBytes = {schema.Limits.GeneratedChannelBytes}u;");
        }
        foreach (UnrealDataPlaneStruct item in structs.Where(item => item.Domain == domain))
        {
            if (item.SchemaConstant is not null)
            {
                ulong schemaId = ParseHexId(item.SchemaId!, $"{item.Name}.schemaId");
                source.AppendLine(
                    $"inline constexpr uint64 {item.SchemaConstant} = {Hex(schemaId, "ull")};");
            }
        }
        source.AppendLine("}");
    }

    private static IReadOnlyList<UnrealDataPlaneStruct> OrderStructs(
        IReadOnlyList<UnrealDataPlaneStruct> structs)
    {
        var byName = structs.ToDictionary(static item => item.Name, StringComparer.Ordinal);
        var ordered = new List<UnrealDataPlaneStruct>(structs.Count);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(UnrealDataPlaneStruct item)
        {
            if (visited.Contains(item.Name))
            {
                return;
            }
            if (!visiting.Add(item.Name))
            {
                throw new JsonException(
                    $"Data-plane struct '{item.Name}' participates in a value-layout cycle.");
            }
            foreach (string dependency in item.Fields
                         .Select(static field => field.Type)
                         .Select(static type =>
                             TryParsePointer(type, out string target, out _)
                                 ? target
                                 : type)
                         .Where(byName.ContainsKey)
                         .Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                Visit(byName[dependency]);
            }
            visiting.Remove(item.Name);
            visited.Add(item.Name);
            ordered.Add(item);
        }

        foreach (UnrealDataPlaneStruct item in structs.OrderBy(
                     static item => item.Name,
                     StringComparer.Ordinal))
        {
            Visit(item);
        }
        return ordered;
    }

    private static TypeLayout ResolveType(
        string typeName,
        IReadOnlyDictionary<string, TypeLayout> types)
    {
        if (TryParsePointer(typeName, out string? target, out bool isConst))
        {
            if (!types.TryGetValue(target, out TypeLayout? targetType))
            {
                throw new JsonException($"Unknown data-plane pointer target '{target}'.");
            }
            return new TypeLayout(
                $"{(isConst ? "const " : string.Empty)}{targetType.NativeType}*",
                "nint",
                8,
                8,
                IsPointer: true,
                IsConstPointer: isConst);
        }
        if (!types.TryGetValue(typeName, out TypeLayout? type))
        {
            throw new JsonException($"Unknown data-plane field type '{typeName}'.");
        }
        return type;
    }

    private static bool TryParsePointer(
        string typeName,
        out string target,
        out bool isConst)
    {
        const string mutablePrefix = "ptr<";
        const string constPrefix = "const-ptr<";
        string? prefix = typeName.StartsWith(mutablePrefix, StringComparison.Ordinal)
            ? mutablePrefix
            : typeName.StartsWith(constPrefix, StringComparison.Ordinal)
                ? constPrefix
                : null;
        if (prefix is null || !typeName.EndsWith('>'))
        {
            target = string.Empty;
            isConst = false;
            return false;
        }
        target = typeName[prefix.Length..^1];
        isConst = prefix == constPrefix;
        return target.Length > 0;
    }

    private static void ValidateNativeDefault(
        UnrealDataPlaneField field,
        TypeLayout type)
    {
        if (field.NativeDefault is null)
        {
            return;
        }
        if (type.IsPointer || type.Enum is not null || type.Struct is not null)
        {
            throw new JsonException(
                $"Field '{field.Name}' cannot override the native default for type '{field.Type}'.");
        }
        if (!decimal.TryParse(
                field.NativeDefault,
                System.Globalization.NumberStyles.AllowLeadingSign |
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            throw new JsonException(
                $"Field '{field.Name}' has unsafe nativeDefault '{field.NativeDefault}'.");
        }
    }

    private static string GetNativeDefault(
        UnrealDataPlaneField field,
        TypeLayout type)
    {
        if (field.NativeDefault is not null)
        {
            return field.NativeDefault;
        }
        if (type.IsPointer)
        {
            return "nullptr";
        }
        if (type.Enum is not null)
        {
            UnrealDataPlaneEnumValue zero = type.Enum.Values.First(
                static value => value.Value == 0);
            return $"{type.NativeType}::{zero.Name}";
        }
        if (type.Struct is not null || type.NativeType.StartsWith('F'))
        {
            return "{}";
        }
        return type.NativeType == "double" ? "0.0" : "0";
    }

    private static ulong ComputeFingerprint(UnrealDataPlaneLayoutSchema schema)
    {
        string canonical = JsonSerializer.Serialize(
            schema,
            UnifiedJson.CreateOptions(writeIndented: false));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    internal static ulong ParseHexId(string value, string fieldName)
    {
        if (!value.StartsWith("0x", StringComparison.Ordinal) || value.Length != 18 ||
            !ulong.TryParse(
                value.AsSpan(2),
                System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture,
                out ulong parsed) || parsed == 0)
        {
            throw new JsonException(
                $"Data-plane {fieldName} must be a non-zero 64-bit 0x-prefixed hex value.");
        }
        return parsed;
    }

    private static string Hex(ulong value, string suffix) =>
        $"0x{value:X16}{suffix}";

    private static string ManagedEnumLiteral(ulong value, string underlyingType) =>
        underlyingType switch
        {
            "u8" => $"{value}",
            "u16" => $"{value}",
            "u32" => $"{value}U",
            "u64" => $"{value}UL",
            _ => throw new JsonException(
                $"Unsupported enum underlying type '{underlyingType}'."),
        };

    private static string NativeEnumLiteral(ulong value, string underlyingType) =>
        underlyingType switch
        {
            "u8" or "u16" => $"{value}",
            "u32" => $"{value}u",
            "u64" => $"{value}ull",
            _ => throw new JsonException(
                $"Unsupported enum underlying type '{underlyingType}'."),
        };

    private static int Align(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);
}

internal static class UnrealDataPlaneLayoutValidator
{
    public static UnrealDataPlaneLayoutSchema Normalize(
        UnrealDataPlaneLayoutSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.SchemaVersion != UnrealDataPlaneLayoutSchema.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported Unreal data-plane layout schema '{schema.SchemaVersion}'.");
        }
        if (!IsIdentifier(schema.ContractName) || schema.AbiVersion == 0 ||
            schema.Revision == 0 || schema.Alignment is not (1 or 2 or 4 or 8) ||
            schema.Limits is null || schema.ExternalTypes is null ||
            schema.Enums is null || schema.Structs is null || schema.Structs.Count == 0)
        {
            throw new JsonException(
                "The Unreal data-plane layout schema has invalid required fields.");
        }
        _ = UnrealDataPlaneLayoutGenerator.ParseHexId(
            schema.CompatibilityId,
            "compatibilityId");
        if (schema.Limits.TransformSnapshots == 0 ||
            schema.Limits.GeneratedSnapshotEntries == 0 ||
            schema.Limits.GeneratedEventEntries == 0 ||
            schema.Limits.GeneratedChannelBytes == 0 ||
            schema.Limits.Commands == 0 ||
            schema.Limits.Events == 0 || schema.Limits.TraceRequests == 0 ||
            schema.Limits.OverlapRequests == 0 ||
            schema.Limits.TransformSnapshots > int.MaxValue ||
            schema.Limits.GeneratedSnapshotEntries > int.MaxValue ||
            schema.Limits.GeneratedEventEntries > int.MaxValue ||
            schema.Limits.GeneratedChannelBytes > int.MaxValue ||
            schema.Limits.Commands > int.MaxValue ||
            schema.Limits.Events > int.MaxValue ||
            schema.Limits.TraceRequests > int.MaxValue ||
            schema.Limits.OverlapRequests > int.MaxValue)
        {
            throw new JsonException("All data-plane capacity limits must fit a positive int32.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "u8", "u16", "u32", "u64", "i32", "f64",
        };
        var nativeNames = new HashSet<string>(StringComparer.Ordinal);
        var managedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnrealDataPlaneExternalType? item in schema.ExternalTypes)
        {
            if (item is null || !IsIdentifier(item.Name) ||
                !IsQualifiedNativeType(item.NativeType) ||
                !IsQualifiedManagedType(item.ManagedType) ||
                item.Size <= 0 || item.Alignment is not (1 or 2 or 4 or 8) ||
                item.Size % item.Alignment != 0 || !names.Add(item.Name))
            {
                throw new JsonException("The data-plane schema contains an invalid external type.");
            }
        }

        foreach (UnrealDataPlaneEnum? item in schema.Enums)
        {
            if (item is null || !IsIdentifier(item.Name) ||
                !IsIdentifier(item.NativeName) || !IsIdentifier(item.ManagedName) ||
                item.UnderlyingType is not ("u8" or "u16" or "u32" or "u64") ||
                item.Values is null || item.Values.Count == 0 ||
                !item.Values.Any(static value => value.Value == 0) ||
                !names.Add(item.Name) || !nativeNames.Add(item.NativeName) ||
                !managedNames.Add(item.ManagedName))
            {
                throw new JsonException("The data-plane schema contains an invalid enum.");
            }
            var valueNames = new HashSet<string>(StringComparer.Ordinal);
            var values = new HashSet<ulong>();
            ulong maximum = item.UnderlyingType switch
            {
                "u8" => byte.MaxValue,
                "u16" => ushort.MaxValue,
                "u32" => uint.MaxValue,
                _ => ulong.MaxValue,
            };
            foreach (UnrealDataPlaneEnumValue? value in item.Values)
            {
                if (value is null || !IsIdentifier(value.Name) || value.Value > maximum ||
                    !valueNames.Add(value.Name) || !values.Add(value.Value))
                {
                    throw new JsonException(
                        $"Data-plane enum '{item.Name}' has invalid or duplicate values.");
                }
            }
        }

        var schemaIds = new HashSet<ulong>();
        var sizeConstants = new HashSet<string>(StringComparer.Ordinal);
        var schemaConstants = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnrealDataPlaneStruct? item in schema.Structs)
        {
            if (item is null || !IsIdentifier(item.Name) ||
                !IsIdentifier(item.NativeName) || !IsIdentifier(item.ManagedName) ||
                item.ManagedVisibility is not ("public" or "internal") ||
                item.Domain is not ("shared" or "gameplay") ||
                !IsRole(item.Role) || item.ExpectedSize <= 0 ||
                item.ExpectedSize % schema.Alignment != 0 || item.Fields is null ||
                item.Fields.Count == 0 || !names.Add(item.Name) ||
                !nativeNames.Add(item.NativeName) || !managedNames.Add(item.ManagedName))
            {
                throw new JsonException("The data-plane schema contains an invalid struct.");
            }
            if ((item.SchemaId is null) != (item.SchemaConstant is null) ||
                item.SchemaConstant is not null &&
                (!IsIdentifier(item.SchemaConstant) ||
                 !schemaConstants.Add(item.SchemaConstant) ||
                 !schemaIds.Add(UnrealDataPlaneLayoutGenerator.ParseHexId(
                     item.SchemaId!,
                     $"{item.Name}.schemaId"))))
            {
                throw new JsonException(
                    $"Data-plane struct '{item.Name}' has an invalid or duplicate schema ID.");
            }
            if (item.SizeConstant is not null &&
                (!IsIdentifier(item.SizeConstant) ||
                 !sizeConstants.Add($"{item.Domain}:{item.SizeConstant}")))
            {
                throw new JsonException(
                    $"Data-plane struct '{item.Name}' has an invalid or duplicate size constant.");
            }
            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnrealDataPlaneField? field in item.Fields)
            {
                if (field is null || !IsIdentifier(field.Name) ||
                    string.IsNullOrWhiteSpace(field.Type) || !fieldNames.Add(field.Name))
                {
                    throw new JsonException(
                        $"Data-plane struct '{item.Name}' has an invalid or duplicate field.");
                }
            }
        }

        var allTypes = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (UnrealDataPlaneStruct item in schema.Structs)
        {
            foreach (UnrealDataPlaneField field in item.Fields)
            {
                string type = field.Type;
                if (TryGetPointerTarget(type, out string target))
                {
                    type = target;
                }
                if (!allTypes.Contains(type))
                {
                    throw new JsonException(
                        $"Data-plane field '{item.Name}.{field.Name}' uses unknown type '{field.Type}'.");
                }
            }
        }
        return schema;
    }

    private static bool IsRole(string value) => value is
        "snapshot-row" or "borrowed-view" or "command-row" or
        "completion-event" or "query-request" or "query-result" or "telemetry";

    private static bool TryGetPointerTarget(string value, out string target)
    {
        string? prefix = value.StartsWith("ptr<", StringComparison.Ordinal)
            ? "ptr<"
            : value.StartsWith("const-ptr<", StringComparison.Ordinal)
                ? "const-ptr<"
                : null;
        if (prefix is null || !value.EndsWith('>'))
        {
            target = string.Empty;
            return false;
        }
        target = value[prefix.Length..^1];
        return target.Length > 0;
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(static character =>
            char.IsLetterOrDigit(character) || character == '_');

    private static bool IsQualifiedNativeType(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Split("::", StringSplitOptions.None).All(IsIdentifier);

    private static bool IsQualifiedManagedType(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Split('.').All(IsIdentifier);
}
