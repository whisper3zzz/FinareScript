using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedScript.Core;

namespace UnifiedScript.Unreal;

public sealed record UnrealDataPlaneExtensionChannelSchema(
    string Name,
    string Kind,
    string SchemaId,
    string NativeRowName,
    string ManagedRowName,
    int ExpectedSize,
    uint MaximumEntries,
    ulong MaximumBytes,
    string PublicationPhase,
    string ProfileBoundary,
    IReadOnlyList<UnrealDataPlaneField> Fields);

public sealed record UnrealDataPlaneExtensionSchema(
    string SchemaVersion,
    string ExtensionId,
    string ExtensionKey,
    string CatalogName,
    uint Revision,
    uint RequiredAbiVersion,
    string RequiredLayoutCompatibilityId,
    int Alignment,
    string NativeNamespace,
    string ManagedNamespace,
    IReadOnlyList<UnrealDataPlaneExternalType> ExternalTypes,
    IReadOnlyList<UnrealDataPlaneExtensionChannelSchema> Channels)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.unreal-data-plane-extension/v0.1";
}

public sealed record UnrealDataPlaneExtensionCatalogChannel(
    string Name,
    string Kind,
    string SchemaId,
    string NativeRowName,
    string ManagedRowName,
    int EntrySize,
    uint MaximumEntries,
    ulong MaximumBytes,
    string PublicationPhase,
    string ProfileBoundary);

public sealed record UnrealDataPlaneExtensionCatalog(
    string SchemaVersion,
    string ExtensionId,
    string ExtensionKey,
    uint Revision,
    uint RequiredAbiVersion,
    string RequiredLayoutCompatibilityId,
    string SchemaFingerprint,
    IReadOnlyList<UnrealDataPlaneExtensionCatalogChannel> Channels)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.unreal-data-plane-extension-catalog/v0.1";
}

public static class UnrealDataPlaneExtensionJson
{
    public static UnrealDataPlaneExtensionSchema Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        UnrealDataPlaneExtensionSchema schema =
            JsonSerializer.Deserialize<UnrealDataPlaneExtensionSchema>(json, Options()) ??
            throw new JsonException("The Unreal data-plane extension schema was empty.");
        return UnrealDataPlaneExtensionValidator.Normalize(schema);
    }

    public static string Serialize(UnrealDataPlaneExtensionSchema schema) =>
        JsonSerializer.Serialize(
                UnrealDataPlaneExtensionValidator.Normalize(schema),
                Options(writeIndented: true))
            .ReplaceLineEndings("\n") + "\n";

    public static UnrealDataPlaneExtensionCatalog DeserializeCatalog(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        UnrealDataPlaneExtensionCatalog catalog =
            JsonSerializer.Deserialize<UnrealDataPlaneExtensionCatalog>(json, Options()) ??
            throw new JsonException("The Unreal data-plane extension catalog was empty.");
        if (catalog.SchemaVersion != UnrealDataPlaneExtensionCatalog.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(catalog.ExtensionId) ||
            catalog.Channels is null || catalog.Channels.Count == 0)
        {
            throw new JsonException("The Unreal data-plane extension catalog is malformed.");
        }
        return catalog;
    }

    private static JsonSerializerOptions Options(bool writeIndented = false)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}

public sealed record UnrealDataPlaneExtensionGenerationResult(
    string ManagedSource,
    string NativeSource,
    string CatalogJson,
    ulong SchemaFingerprint);

public sealed class UnrealDataPlaneExtensionGenerator
{
    private sealed record TypeLayout(
        string NativeType,
        string ManagedType,
        int Size,
        int Alignment);

    public UnrealDataPlaneExtensionGenerationResult Generate(
        UnrealDataPlaneExtensionSchema input)
    {
        UnrealDataPlaneExtensionSchema schema =
            UnrealDataPlaneExtensionValidator.Normalize(input);
        ulong extensionKey = UnrealDataPlaneLayoutGenerator.ParseHexId(
            schema.ExtensionKey,
            "extensionKey");
        ulong compatibilityId = UnrealDataPlaneLayoutGenerator.ParseHexId(
            schema.RequiredLayoutCompatibilityId,
            "requiredLayoutCompatibilityId");
        ulong fingerprint = ComputeFingerprint(schema);
        Dictionary<string, TypeLayout> types = CreateTypes(schema);
        UnrealDataPlaneExtensionChannelSchema[] channels = schema.Channels
            .OrderBy(static channel => channel.Name, StringComparer.Ordinal)
            .ToArray();
        ValidateLayouts(schema, channels, types);

        return new UnrealDataPlaneExtensionGenerationResult(
            GenerateManaged(schema, channels, types, extensionKey, compatibilityId, fingerprint),
            GenerateNative(schema, channels, types, extensionKey, compatibilityId, fingerprint),
            GenerateCatalog(schema, channels, fingerprint),
            fingerprint);
    }

    private static Dictionary<string, TypeLayout> CreateTypes(
        UnrealDataPlaneExtensionSchema schema)
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
        return types;
    }

    private static void ValidateLayouts(
        UnrealDataPlaneExtensionSchema schema,
        IReadOnlyList<UnrealDataPlaneExtensionChannelSchema> channels,
        IReadOnlyDictionary<string, TypeLayout> types)
    {
        foreach (UnrealDataPlaneExtensionChannelSchema channel in channels)
        {
            int offset = 0;
            int largestAlignment = 1;
            foreach (UnrealDataPlaneField field in channel.Fields)
            {
                if (!types.TryGetValue(field.Type, out TypeLayout? type))
                {
                    throw new JsonException(
                        $"Extension row '{channel.Name}' uses unknown field type '{field.Type}'.");
                }
                int alignment = Math.Min(type.Alignment, schema.Alignment);
                offset = Align(offset, alignment);
                offset = checked(offset + type.Size);
                largestAlignment = Math.Max(largestAlignment, alignment);
                ValidateDefault(field, type);
            }
            int size = Align(offset, largestAlignment);
            if (size != channel.ExpectedSize)
            {
                throw new JsonException(
                    $"Extension row '{channel.Name}' computes to {size} bytes, " +
                    $"but expectedSize is {channel.ExpectedSize}.");
            }
        }
    }

    private static string GenerateManaged(
        UnrealDataPlaneExtensionSchema schema,
        IReadOnlyList<UnrealDataPlaneExtensionChannelSchema> channels,
        IReadOnlyDictionary<string, TypeLayout> types,
        ulong extensionKey,
        ulong compatibilityId,
        ulong fingerprint)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("// Generated from a versioned UnifiedScript data-plane extension schema.");
        source.AppendLine("using System.Runtime.InteropServices;");
        source.AppendLine("using UnifiedScript.Unreal;");
        source.AppendLine();
        source.AppendLine($"namespace {schema.ManagedNamespace};");
        source.AppendLine();
        foreach (UnrealDataPlaneExtensionChannelSchema channel in channels)
        {
            source.AppendLine($"[StructLayout(LayoutKind.Sequential, Pack = {schema.Alignment})]");
            source.AppendLine($"public readonly struct {channel.ManagedRowName}");
            source.AppendLine("{");
            foreach (UnrealDataPlaneField field in channel.Fields)
            {
                source.AppendLine(
                    $"    public readonly {types[field.Type].ManagedType} {field.Name};");
            }
            source.AppendLine("}");
            source.AppendLine();
        }
        source.AppendLine($"public static class {schema.CatalogName}");
        source.AppendLine("{");
        source.AppendLine("    public static readonly UnrealDataPlaneExtensionIdentity Identity = new(");
        source.AppendLine($"        \"{EscapeCSharp(schema.ExtensionId)}\",");
        source.AppendLine($"        {Hex(extensionKey, "UL")},");
        source.AppendLine($"        {schema.Revision}U,");
        source.AppendLine($"        {schema.RequiredAbiVersion}U,");
        source.AppendLine($"        {Hex(compatibilityId, "UL")},");
        source.AppendLine($"        {Hex(fingerprint, "UL")});");
        foreach (UnrealDataPlaneExtensionChannelSchema channel in channels)
        {
            source.AppendLine();
            source.AppendLine(
                $"    public static readonly UnrealDataPlaneExtensionChannel<{channel.ManagedRowName}> {channel.Name} = new(");
            source.AppendLine("        Identity,");
            source.AppendLine($"        \"{EscapeCSharp(channel.Name)}\",");
            source.AppendLine(
                $"        UnrealDataPlaneExtensionChannelKind.{KindName(channel.Kind)},");
            source.AppendLine(
                "        UnrealDataPlaneExtensionPublicationPhase.Native,");
            source.AppendLine($"        {channel.SchemaId}UL,");
            source.AppendLine($"        {channel.ExpectedSize}U,");
            source.AppendLine($"        {channel.MaximumEntries}U,");
            source.AppendLine($"        {channel.MaximumBytes}UL,");
            source.AppendLine($"        \"{EscapeCSharp(channel.ProfileBoundary)}\");");
        }
        source.AppendLine("}");
        return source.ToString().ReplaceLineEndings("\n");
    }

    private static string GenerateNative(
        UnrealDataPlaneExtensionSchema schema,
        IReadOnlyList<UnrealDataPlaneExtensionChannelSchema> channels,
        IReadOnlyDictionary<string, TypeLayout> types,
        ulong extensionKey,
        ulong compatibilityId,
        ulong fingerprint)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("// Generated from a versioned UnifiedScript data-plane extension schema.");
        source.AppendLine("#pragma once");
        source.AppendLine();
        source.AppendLine("#include \"UnifiedScriptDataPlaneExtension.h\"");
        source.AppendLine("#include \"UnifiedScriptSharedDataPlane.h\"");
        source.AppendLine();
        source.AppendLine($"namespace {schema.NativeNamespace}");
        source.AppendLine("{");
        foreach (UnrealDataPlaneExtensionChannelSchema channel in channels)
        {
            source.AppendLine($"struct alignas({schema.Alignment}) {channel.NativeRowName}");
            source.AppendLine("{");
            foreach (UnrealDataPlaneField field in channel.Fields)
            {
                TypeLayout type = types[field.Type];
                source.AppendLine(
                    $"    {type.NativeType} {field.Name} = {NativeDefault(field, type)};");
            }
            source.AppendLine("};");
            source.AppendLine();
        }
        foreach (UnrealDataPlaneExtensionChannelSchema channel in channels)
        {
            source.AppendLine(
                $"inline constexpr FUnifiedScriptDataPlaneExtensionChannelDescriptor {channel.Name} =");
            source.AppendLine("{");
            source.AppendLine($"    TEXT(\"{EscapeNative(channel.Name)}\"),");
            source.AppendLine($"    {channel.SchemaId}ull,");
            source.AppendLine($"    {channel.ExpectedSize}u,");
            source.AppendLine($"    {channel.MaximumEntries}u,");
            source.AppendLine($"    {channel.MaximumBytes}ull,");
            source.AppendLine(
                $"    EUnifiedScriptDataPlaneExtensionChannelKind::{KindName(channel.Kind)},");
            source.AppendLine(
                "    EUnifiedScriptDataPlaneExtensionPublicationPhase::Native,");
            source.AppendLine($"    TEXT(\"{EscapeNative(channel.ProfileBoundary)}\"),");
            source.AppendLine("};");
            source.AppendLine();
        }
        source.AppendLine("inline constexpr FUnifiedScriptDataPlaneExtensionChannelDescriptor Channels[] =");
        source.AppendLine("{");
        foreach (UnrealDataPlaneExtensionChannelSchema channel in channels)
        {
            source.AppendLine($"    {channel.Name},");
        }
        source.AppendLine("};");
        source.AppendLine();
        source.AppendLine("inline constexpr FUnifiedScriptDataPlaneExtensionDescriptor Descriptor =");
        source.AppendLine("{");
        source.AppendLine($"    TEXT(\"{EscapeNative(schema.ExtensionId)}\"),");
        source.AppendLine($"    {Hex(extensionKey, "ull")},");
        source.AppendLine($"    {schema.Revision}u,");
        source.AppendLine($"    {schema.RequiredAbiVersion}u,");
        source.AppendLine($"    {Hex(compatibilityId, "ull")},");
        source.AppendLine($"    {Hex(fingerprint, "ull")},");
        source.AppendLine("    Channels,");
        source.AppendLine("    UE_ARRAY_COUNT(Channels),");
        source.AppendLine("};");
        source.AppendLine("}");
        foreach (UnrealDataPlaneExtensionChannelSchema channel in channels)
        {
            source.AppendLine();
            source.AppendLine(
                $"static_assert(std::is_standard_layout_v<{schema.NativeNamespace}::{channel.NativeRowName}>);");
            source.AppendLine(
                $"static_assert(std::is_trivially_copyable_v<{schema.NativeNamespace}::{channel.NativeRowName}>);");
            source.AppendLine(
                $"static_assert(sizeof({schema.NativeNamespace}::{channel.NativeRowName}) == {channel.ExpectedSize});");
            int offset = 0;
            foreach (UnrealDataPlaneField field in channel.Fields)
            {
                TypeLayout type = types[field.Type];
                offset = Align(offset, Math.Min(type.Alignment, schema.Alignment));
                source.AppendLine(
                    $"static_assert(STRUCT_OFFSET({schema.NativeNamespace}::{channel.NativeRowName}, {field.Name}) == {offset});");
                offset += type.Size;
            }
        }
        return source.ToString().ReplaceLineEndings("\n");
    }

    private static string GenerateCatalog(
        UnrealDataPlaneExtensionSchema schema,
        IReadOnlyList<UnrealDataPlaneExtensionChannelSchema> channels,
        ulong fingerprint)
    {
        var catalog = new UnrealDataPlaneExtensionCatalog(
            UnrealDataPlaneExtensionCatalog.CurrentSchemaVersion,
            schema.ExtensionId,
            schema.ExtensionKey,
            schema.Revision,
            schema.RequiredAbiVersion,
            schema.RequiredLayoutCompatibilityId,
            $"0x{fingerprint:X16}",
            channels.Select(static channel => new UnrealDataPlaneExtensionCatalogChannel(
                channel.Name,
                channel.Kind,
                channel.SchemaId,
                channel.NativeRowName,
                channel.ManagedRowName,
                channel.ExpectedSize,
                channel.MaximumEntries,
                channel.MaximumBytes,
                channel.PublicationPhase,
                channel.ProfileBoundary)).ToArray());
        return JsonSerializer.Serialize(
                catalog,
                UnifiedJson.CreateOptions(writeIndented: true))
            .ReplaceLineEndings("\n") + "\n";
    }

    private static void ValidateDefault(UnrealDataPlaneField field, TypeLayout type)
    {
        if (field.NativeDefault is null)
        {
            return;
        }
        if (!decimal.TryParse(
                field.NativeDefault,
                System.Globalization.NumberStyles.AllowLeadingSign |
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            throw new JsonException(
                $"Extension field '{field.Name}' has unsafe nativeDefault '{field.NativeDefault}'.");
        }
    }

    private static string NativeDefault(UnrealDataPlaneField field, TypeLayout type)
    {
        if (field.NativeDefault is not null)
        {
            return field.NativeDefault;
        }
        return type.NativeType.StartsWith('F') ? "{}" :
            type.NativeType == "double" ? "0.0" : "0";
    }

    private static ulong ComputeFingerprint(UnrealDataPlaneExtensionSchema schema)
    {
        string canonical = JsonSerializer.Serialize(
            schema,
            UnifiedJson.CreateOptions(writeIndented: false));
        return BinaryPrimitives.ReadUInt64BigEndian(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string KindName(string kind) => kind switch
    {
        "snapshot" => "Snapshot",
        "event" => "Event",
        _ => throw new JsonException($"Unsupported extension channel kind '{kind}'."),
    };

    private static string Hex(ulong value, string suffix) => $"0x{value:X16}{suffix}";
    private static int Align(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);
    private static string EscapeCSharp(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    private static string EscapeNative(string value) => EscapeCSharp(value);
}

internal static class UnrealDataPlaneExtensionValidator
{
    public static UnrealDataPlaneExtensionSchema Normalize(
        UnrealDataPlaneExtensionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.SchemaVersion != UnrealDataPlaneExtensionSchema.CurrentSchemaVersion ||
            !IsQualifiedName(schema.ExtensionId, '.') ||
            !IsIdentifier(schema.CatalogName) || schema.Revision == 0 ||
            schema.RequiredAbiVersion == 0 ||
            schema.Alignment is not (1 or 2 or 4 or 8) ||
            !IsQualifiedName(schema.NativeNamespace, ':') ||
            !IsQualifiedName(schema.ManagedNamespace, '.') ||
            schema.ExternalTypes is null || schema.Channels is null ||
            schema.Channels.Count is < 1 or > 64)
        {
            throw new JsonException(
                "The Unreal data-plane extension schema has invalid required fields.");
        }
        _ = UnrealDataPlaneLayoutGenerator.ParseHexId(schema.ExtensionKey, "extensionKey");
        ulong compatibilityId = UnrealDataPlaneLayoutGenerator.ParseHexId(
            schema.RequiredLayoutCompatibilityId,
            "requiredLayoutCompatibilityId");
        if (schema.RequiredAbiVersion != UnrealSharedDataAbiV4.Version ||
            compatibilityId != UnrealDataPlaneLayoutV1.CompatibilityId)
        {
            throw new JsonException(
                "The extension must target the current generated ABI and layout compatibility ID.");
        }

        var typeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "u8", "u16", "u32", "u64", "i32", "f64",
        };
        foreach (UnrealDataPlaneExternalType? external in schema.ExternalTypes)
        {
            if (external is null || !IsIdentifier(external.Name) ||
                !IsQualifiedName(external.NativeType, ':') ||
                !IsQualifiedName(external.ManagedType, '.') ||
                external.Size <= 0 || external.Alignment is not (1 or 2 or 4 or 8) ||
                external.Size % external.Alignment != 0 || !typeNames.Add(external.Name))
            {
                throw new JsonException(
                    "The extension schema contains an invalid external type.");
            }
        }

        var channelNames = new HashSet<string>(StringComparer.Ordinal);
        var nativeRows = new HashSet<string>(StringComparer.Ordinal);
        var managedRows = new HashSet<string>(StringComparer.Ordinal);
        var schemaIds = new HashSet<ulong>();
        var profileBoundaries = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnrealDataPlaneExtensionChannelSchema? channel in schema.Channels)
        {
            if (channel is null || !IsIdentifier(channel.Name) ||
                channel.Kind is not ("snapshot" or "event") ||
                channel.PublicationPhase != "native" ||
                !IsIdentifier(channel.NativeRowName) ||
                !IsIdentifier(channel.ManagedRowName) ||
                channel.ExpectedSize <= 0 || channel.ExpectedSize % schema.Alignment != 0 ||
                channel.MaximumEntries == 0 || channel.MaximumBytes == 0 ||
                channel.Fields is null || channel.Fields.Count == 0 ||
                !IsProfileBoundary(channel.ProfileBoundary) ||
                !channelNames.Add(channel.Name) || !nativeRows.Add(channel.NativeRowName) ||
                !managedRows.Add(channel.ManagedRowName) ||
                !profileBoundaries.Add(channel.ProfileBoundary) ||
                !schemaIds.Add(UnrealDataPlaneLayoutGenerator.ParseHexId(
                    channel.SchemaId,
                    $"{channel.Name}.schemaId")))
            {
                throw new JsonException(
                    "The extension schema contains an invalid or duplicate channel.");
            }
            uint globalEntries = channel.Kind == "snapshot"
                ? UnrealSharedDataAbiV4.MaximumGeneratedSnapshotCount
                : UnrealGameplayDataAbiV4.MaximumGeneratedEventCount;
            ulong requiredBytes = checked((ulong)channel.ExpectedSize * channel.MaximumEntries);
            if (channel.MaximumEntries > globalEntries ||
                channel.MaximumBytes > UnrealSharedDataAbiV4.MaximumGeneratedChannelBytes ||
                requiredBytes > channel.MaximumBytes)
            {
                throw new JsonException(
                    $"Extension channel '{channel.Name}' exceeds the core ABI capacity envelope.");
            }
            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnrealDataPlaneField? field in channel.Fields)
            {
                if (field is null || !IsIdentifier(field.Name) ||
                    !typeNames.Contains(field.Type) || !fieldNames.Add(field.Name))
                {
                    throw new JsonException(
                        $"Extension channel '{channel.Name}' has an invalid field.");
                }
            }
        }
        return schema with
        {
            Channels = schema.Channels
                .OrderBy(static channel => channel.Name, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(static character =>
            char.IsLetterOrDigit(character) || character == '_');

    private static bool IsQualifiedName(string? value, char separator)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        string token = separator == ':' ? "::" : separator.ToString();
        return value.Split(token, StringSplitOptions.None).All(IsIdentifier);
    }

    private static bool IsProfileBoundary(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 96 &&
        value.All(static character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_');
}
