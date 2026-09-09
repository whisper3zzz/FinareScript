using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedScript.Core;

public static class ScriptAssetJson
{
    public static string Serialize(ScriptAssetDocument document, bool writeIndented = true)
    {
        ScriptAssetDocument normalized = NormalizeAndValidate(document);
        return JsonSerializer.Serialize(normalized, CreateOptions(writeIndented));
    }

    public static ScriptAssetDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ScriptAssetDocument? document = JsonSerializer.Deserialize<ScriptAssetDocument>(
            json,
            CreateOptions(writeIndented: true));
        if (document is null)
        {
            throw new JsonException("The script-asset document was empty.");
        }

        return NormalizeAndValidate(document);
    }

    public static ScriptAssetDocument NormalizeAndValidate(ScriptAssetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion is not ScriptAssetDocument.CurrentSchemaVersion and
            not ScriptAssetDocument.OverrideSchemaVersion and
            not ScriptAssetDocument.ClassBindingSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported script-asset schema '{document.SchemaVersion}'. " +
                $"Expected '{ScriptAssetDocument.CurrentSchemaVersion}' or " +
                $"'{ScriptAssetDocument.OverrideSchemaVersion}' or " +
                $"'{ScriptAssetDocument.ClassBindingSchemaVersion}'.");
        }

        if (document.SourceCompilationId.IsEmpty ||
            document.SemanticConfigurationId.IsEmpty ||
            document.Assets is null)
        {
            throw new JsonException("A script-asset document has invalid required fields.");
        }

        bool hasBlueprintOverride = document.Assets.Any(static asset =>
            asset?.Functions?.Any(static function => function?.BlueprintEvent?.Override is not null) == true);
        if (document.SchemaVersion == ScriptAssetDocument.CurrentSchemaVersion && hasBlueprintOverride)
        {
            throw new JsonException(
                $"Script-asset schema '{ScriptAssetDocument.CurrentSchemaVersion}' cannot carry Blueprint override metadata; " +
                $"use '{ScriptAssetDocument.OverrideSchemaVersion}'.");
        }

        bool hasClassBinding = document.Assets.Any(static asset => asset?.ClassBinding is not null);
        if (document.SchemaVersion is not ScriptAssetDocument.ClassBindingSchemaVersion && hasClassBinding)
        {
            throw new JsonException(
                $"Script-asset schema '{document.SchemaVersion}' cannot carry class binding metadata; " +
                $"use '{ScriptAssetDocument.ClassBindingSchemaVersion}'.");
        }

        var assetIds = new HashSet<SemanticId>();
        var assetPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScriptAssetDeclaration? asset in document.Assets)
        {
            ValidateAsset(asset, assetIds, assetPaths);
        }

        return document with
        {
            Assets = document.Assets
                .OrderBy(static asset => asset.AssetPath, StringComparer.Ordinal)
                .ThenBy(static asset => asset.Id.Value, StringComparer.Ordinal)
                .Select(NormalizeAsset)
                .ToArray(),
        };
    }

    private static void ValidateAsset(
        ScriptAssetDeclaration? asset,
        ISet<SemanticId> assetIds,
        ISet<string> assetPaths)
    {
        if (asset is null || asset.Id.IsEmpty ||
            string.IsNullOrWhiteSpace(asset.QualifiedName) ||
            !IsValidAssetPath(asset.AssetPath) ||
            !IsValidSpan(asset.Span) ||
            asset.States is null || asset.Functions is null)
        {
            throw new JsonException("A script asset has invalid required fields.");
        }

        if (!assetIds.Add(asset.Id))
        {
            throw new JsonException($"Duplicate script-asset ID '{asset.Id}'.");
        }

        if (!assetPaths.Add(asset.AssetPath))
        {
            throw new JsonException($"Duplicate script-asset path '{asset.AssetPath}'.");
        }

        if (asset.ClassBinding is not null)
        {
            ValidateClassBinding(asset.ClassBinding, asset.AssetPath);
        }

        var stateIds = new HashSet<SemanticId>();
        var stateNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScriptStateDeclaration? state in asset.States)
        {
            ValidateState(asset, state, stateIds, stateNames);
        }

        var functionIds = new HashSet<SemanticId>();
        var blueprintOverrideRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ScriptFunctionDeclaration? function in asset.Functions)
        {
            ValidateFunction(asset, function, functionIds, blueprintOverrideRoutes);
        }

        var notifiedStates = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScriptFunctionDeclaration function in asset.Functions.Where(f => f.RepNotifyProperty is not null))
        {
            ScriptStateDeclaration? state = asset.States.SingleOrDefault(s => s.Name == function.RepNotifyProperty);
            if (state is null || !state.Exposure.Replicated || !notifiedStates.Add(state.Name) ||
                function.Parameters.Count > 1 || (function.Parameters.Count == 1 &&
                    function.Parameters[0].TypeMetadataName != state.TypeMetadataName))
                throw new JsonException($"RepNotify '{function.Name}' requires one replicated state and zero arguments or its exact previous-value type; each state has one notification owner.");
        }

        var lifecycleIds = new HashSet<SemanticId>();
        var lifecycleKinds = new HashSet<ScriptLifecycleHookKind>();
        foreach (ScriptLifecycleHookDeclaration? hook in asset.LifecycleHooks ?? [])
        {
            ValidateLifecycleHook(asset, hook, lifecycleIds, lifecycleKinds);
        }
    }

    /// <summary>Validates the engine-neutral class binding contract.</summary>
    public static void ValidateClassBinding(
        ScriptClassBindingDeclaration binding,
        string? assetPath = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!IsValidOpaqueBindingText(binding.Kind))
        {
            throw new JsonException(
                $"Script asset '{assetPath ?? "<unknown>"}' has an invalid class binding kind '{binding.Kind}'.");
        }

        ValidateClassBindingPath(binding.BaseClassPath, nameof(binding.BaseClassPath), assetPath);
        ValidateClassBindingPath(binding.GeneratedClassPath, nameof(binding.GeneratedClassPath), assetPath);
    }

    private static void ValidateClassBindingPath(
        string? path,
        string fieldName,
        string? assetPath)
    {
        if (path is null)
        {
            return;
        }

        if (!IsValidOpaqueBindingText(path))
        {
            throw new JsonException(
                $"Script asset '{assetPath ?? "<unknown>"}' has an invalid {fieldName} '{path}'.");
        }
    }

    private static bool IsValidOpaqueBindingText(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.All(static character => !char.IsControl(character));

    private static void ValidateState(
        ScriptAssetDeclaration asset,
        ScriptStateDeclaration? state,
        ISet<SemanticId> stateIds,
        ISet<string> stateNames)
    {
        if (state is null || state.Id.IsEmpty ||
            string.IsNullOrWhiteSpace(state.Name) ||
            string.IsNullOrWhiteSpace(state.TypeDisplayName) ||
            string.IsNullOrWhiteSpace(state.TypeMetadataName) ||
            !Enum.IsDefined(state.TypeKind) ||
            state.DefaultValue.ValueKind == JsonValueKind.Undefined ||
            !IsValidSpan(state.Span) || state.Exposure is null)
        {
            throw new JsonException($"Script asset '{asset.AssetPath}' has an invalid state declaration.");
        }

        if (!stateIds.Add(state.Id) || !stateNames.Add(state.Name))
        {
            throw new JsonException(
                $"Script asset '{asset.AssetPath}' has duplicate state '{state.Name}' or ID '{state.Id}'.");
        }

        if (state.Exposure.Category is not null && string.IsNullOrWhiteSpace(state.Exposure.Category))
        {
            throw new JsonException($"Script state '{state.Name}' has a blank exposure category.");
        }
        if (!Enum.IsDefined(state.Exposure.ReplicationCondition) ||
            !state.Exposure.Replicated && state.Exposure.ReplicationCondition != global::UnifiedScript.ReplicationCondition.None)
            throw new JsonException($"Script state '{state.Name}' has an invalid replication condition or is not replicated.");

        ValidateFunctionValueType(state.UnrealType, state.TypeMetadataName, 0);
        if (state.TypeKind == ScriptStateTypeKind.Struct)
        {
            if (state.UnrealType?.Kind != ScriptFunctionValueTypeKind.Struct)
            {
                throw new JsonException(
                    $"Struct state '{state.Name}' requires an exact Unreal struct identity.");
            }
        }
        else if (state.TypeKind == ScriptStateTypeKind.Enum)
        {
            if (state.UnrealType is not null && state.UnrealType.Kind != ScriptFunctionValueTypeKind.Enum)
            {
                throw new JsonException(
                    $"Enum state '{state.Name}' has an invalid Unreal enum identity.");
            }
        }
        else if (state.TypeKind is ScriptStateTypeKind.SoftObject or ScriptStateTypeKind.SoftClass)
        {
            if (state.UnrealType is not null ||
                state.TypeMetadataName is not
                    ("UnifiedScript.Unreal.UnrealSoftObjectHandle" or
                     "UnifiedScript.Unreal.UnrealSoftClassHandle") ||
                state.TypeKind == ScriptStateTypeKind.SoftObject &&
                    state.TypeMetadataName != "UnifiedScript.Unreal.UnrealSoftObjectHandle" ||
                state.TypeKind == ScriptStateTypeKind.SoftClass &&
                    state.TypeMetadataName != "UnifiedScript.Unreal.UnrealSoftClassHandle")
            {
                throw new JsonException(
                    $"Soft reference state '{state.Name}' requires its exact managed handle type and no Unreal type identity.");
            }
        }
        else if (state.TypeKind is ScriptStateTypeKind.Array or ScriptStateTypeKind.Set or ScriptStateTypeKind.Map)
        {
            ValidateStateContainerType(state);
        }
        else if (state.UnrealType is not null)
        {
            throw new JsonException(
                $"Non-struct state '{state.Name}' cannot carry an Unreal struct identity.");
        }

        bool validDefault = state.TypeKind switch
        {
            ScriptStateTypeKind.Boolean => state.DefaultValue.ValueKind == JsonValueKind.True ||
                state.DefaultValue.ValueKind == JsonValueKind.False,
            ScriptStateTypeKind.Integer =>
                state.DefaultValue.ValueKind == JsonValueKind.Number &&
                (state.DefaultValue.TryGetInt64(out _) || state.DefaultValue.TryGetUInt64(out _)),
            ScriptStateTypeKind.FloatingPoint =>
                state.DefaultValue.ValueKind == JsonValueKind.Number &&
                state.DefaultValue.TryGetDouble(out double floatingValue) && double.IsFinite(floatingValue),
            ScriptStateTypeKind.String =>
                state.DefaultValue.ValueKind is JsonValueKind.String or JsonValueKind.Null,
            ScriptStateTypeKind.Enum => state.DefaultValue.ValueKind == JsonValueKind.String &&
                (state.UnrealType is not null
                    ? long.TryParse(
                        state.DefaultValue.GetString(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out _)
                    : !string.IsNullOrWhiteSpace(state.DefaultValue.GetString())),
            ScriptStateTypeKind.Name or ScriptStateTypeKind.Text =>
                state.DefaultValue.ValueKind == JsonValueKind.String,
            ScriptStateTypeKind.Object or ScriptStateTypeKind.Class =>
                state.DefaultValue.ValueKind == JsonValueKind.Null,
            ScriptStateTypeKind.SoftObject or ScriptStateTypeKind.SoftClass =>
                state.DefaultValue.ValueKind == JsonValueKind.String &&
                UnrealObjectPathValidator.IsValid(state.DefaultValue.GetString()),
            ScriptStateTypeKind.Struct => IsValidStructDefault(state),
            ScriptStateTypeKind.Array or ScriptStateTypeKind.Set or ScriptStateTypeKind.Map =>
                IsValidCollectionDefault(state),
            _ => false,
        };
        if (!validDefault)
        {
            throw new JsonException(
                $"Script state '{state.Name}' has a default incompatible with '{state.TypeKind}'.");
        }
    }

    private static void ValidateStateContainerType(ScriptStateDeclaration state)
    {
        ScriptFunctionValueTypeKind expectedKind = state.TypeKind switch
        {
            ScriptStateTypeKind.Array => ScriptFunctionValueTypeKind.Array,
            ScriptStateTypeKind.Set => ScriptFunctionValueTypeKind.Set,
            _ => ScriptFunctionValueTypeKind.Map,
        };
        ScriptFunctionValueTypeDeclaration? type = state.UnrealType;
        int arity = expectedKind == ScriptFunctionValueTypeKind.Map ? 2 : 1;
        string definition = expectedKind switch
        {
            ScriptFunctionValueTypeKind.Array => "System.Collections.Generic.IReadOnlyList",
            ScriptFunctionValueTypeKind.Set => "System.Collections.Generic.IReadOnlySet",
            _ => "System.Collections.Generic.IReadOnlyDictionary",
        };
        string expectedMetadata = $"{definition}<{string.Join(", ", type?.TypeArguments?.Select(static a => a.TypeMetadataName) ?? [])}>";
        if (type?.Kind != expectedKind || type.UnrealTypePath != string.Empty ||
            type.UnrealNativeName != (expectedKind == ScriptFunctionValueTypeKind.Array ? "TArray" :
                expectedKind == ScriptFunctionValueTypeKind.Set ? "TSet" : "TMap") ||
            type.Fields.Count != 0 || type.TypeArguments is null || type.TypeArguments.Count != arity ||
            state.TypeMetadataName != expectedMetadata ||
            type.TypeArguments.Any(static argument => argument.UnrealType?.Kind is
                ScriptFunctionValueTypeKind.Array or ScriptFunctionValueTypeKind.Set or ScriptFunctionValueTypeKind.Map))
        {
            throw new JsonException($"Collection state '{state.Name}' has invalid flat container type metadata.");
        }

        foreach (ScriptFunctionTypeArgumentDeclaration argument in type.TypeArguments)
        {
            if (!IsSupportedCollectionLeaf(argument))
            {
                throw new JsonException(
                    $"Collection state '{state.Name}' has an unsupported or non-hashable leaf type '{argument.TypeMetadataName}'.");
            }
        }
    }

    public static bool IsSupportedCollectionLeaf(ScriptFunctionTypeArgumentDeclaration argument)
    {
        string metadata = argument.TypeMetadataName;
        if (argument.UnrealType?.Kind is ScriptFunctionValueTypeKind.Array or
            ScriptFunctionValueTypeKind.Set or ScriptFunctionValueTypeKind.Map)
        {
            return false;
        }
        if (metadata is "System.Boolean" or "System.Byte" or "System.Int32" or
            "System.Int64" or "System.Single" or "System.Double" or
            "System.String" or "UnifiedScript.Unreal.UnrealName" or
            "UnifiedScript.Unreal.UnrealText" or "UnifiedScript.Unreal.UnrealObjectHandle" or
            "UnifiedScript.Unreal.UnrealClassHandle" or "UnifiedScript.Unreal.UnrealSoftObjectHandle" or
            "UnifiedScript.Unreal.UnrealSoftClassHandle")
        {
            return argument.UnrealType is null;
        }
        if (metadata is "UnifiedScript.Unreal.UnrealVector" or
            "UnifiedScript.Unreal.UnrealRotator" or "UnifiedScript.Unreal.UnrealTransform")
        {
            string expectedPath = metadata switch
            {
                "UnifiedScript.Unreal.UnrealVector" => "/Script/CoreUObject.Vector",
                "UnifiedScript.Unreal.UnrealRotator" => "/Script/CoreUObject.Rotator",
                _ => "/Script/CoreUObject.Transform",
            };
            string expectedNativeName = metadata switch
            {
                "UnifiedScript.Unreal.UnrealVector" => "Vector",
                "UnifiedScript.Unreal.UnrealRotator" => "Rotator",
                _ => "Transform",
            };
            return argument.UnrealType is
                {
                    Kind: ScriptFunctionValueTypeKind.Struct,
                    UnrealTypePath: var path,
                    UnrealNativeName: var nativeName,
                    Fields: { Count: var fieldCount } fields,
                    TypeArguments: null,
                } && path == expectedPath && nativeName == expectedNativeName &&
                (metadata == "UnifiedScript.Unreal.UnrealVector"
                    ? fieldCount == 3 &&
                        fields.Select(static field => field.NativeName).SequenceEqual(["X", "Y", "Z"]) &&
                        fields.All(static field => field.TypeMetadataName == "System.Double" && field.UnrealType is null)
                    : fields.Count == 0);
        }
        return argument.UnrealType is
            {
                Kind: ScriptFunctionValueTypeKind.Enum,
                UnrealTypePath: var enumPath,
                UnrealNativeName: var enumNativeName,
                Fields: { Count: 0 },
                TypeArguments: null,
            } && enumPath.StartsWith("/Script/", StringComparison.Ordinal) &&
            enumNativeName.Length != 0;
    }

    private static bool IsValidCollectionDefault(ScriptStateDeclaration state)
    {
        if (state.DefaultValue.ValueKind != JsonValueKind.Array || state.UnrealType?.TypeArguments is null)
        {
            return false;
        }
        ScriptFunctionValueTypeKind kind = state.UnrealType.Kind;
        ScriptFunctionTypeArgumentDeclaration[] arguments = state.UnrealType.TypeArguments.ToArray();
        if (kind == ScriptFunctionValueTypeKind.Map)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement entry in state.DefaultValue.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    entry.EnumerateObject().Count() != 2 ||
                    !entry.TryGetProperty("key", out JsonElement key) ||
                    !entry.TryGetProperty("value", out JsonElement value) ||
                    !IsValidCollectionLeafDefault(key, arguments[0]) ||
                    !IsValidCollectionLeafDefault(value, arguments[1]) ||
                    !keys.Add(CollectionIdentity(key, arguments[0])))
                {
                    return false;
                }
            }
            return true;
        }

        var values = kind == ScriptFunctionValueTypeKind.Set
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        foreach (JsonElement item in state.DefaultValue.EnumerateArray())
        {
            if (!IsValidCollectionLeafDefault(item, arguments[0]) ||
                values is not null && !values.Add(CollectionIdentity(item, arguments[0])))
            {
                return false;
            }
        }
        return true;
    }

    public static bool IsValidCollectionLeafDefault(
        JsonElement value,
        ScriptFunctionTypeArgumentDeclaration argument)
    {
        if (!IsSupportedCollectionLeaf(argument))
        {
            return false;
        }
        string metadata = argument.TypeMetadataName;
        if (metadata == "System.Boolean") return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        if (metadata == "System.Byte")
            return value.ValueKind == JsonValueKind.Number &&
                value.TryGetUInt64(out ulong byteValue) && byteValue <= byte.MaxValue;
        if (metadata == "System.Int32")
            return value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out long intValue) && intValue is >= int.MinValue and <= int.MaxValue;
        if (metadata == "System.Int64")
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _);
        if (metadata == "System.Single")
            return value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float single) && float.IsFinite(single);
        if (metadata == "System.Double")
            return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number);
        if (metadata == "System.String" || metadata is "UnifiedScript.Unreal.UnrealName" or
            "UnifiedScript.Unreal.UnrealText") return value.ValueKind == JsonValueKind.String;
        if (metadata is "UnifiedScript.Unreal.UnrealObjectHandle" or "UnifiedScript.Unreal.UnrealClassHandle")
            return value.ValueKind == JsonValueKind.Null;
        if (metadata is "UnifiedScript.Unreal.UnrealSoftObjectHandle" or "UnifiedScript.Unreal.UnrealSoftClassHandle")
            return value.ValueKind == JsonValueKind.String && UnrealObjectPathValidator.IsValid(value.GetString());
        if (metadata == "UnifiedScript.Unreal.UnrealVector")
            return argument.UnrealType?.Kind == ScriptFunctionValueTypeKind.Struct &&
                argument.UnrealType.UnrealTypePath == "/Script/CoreUObject.Vector" &&
                IsFiniteVectorDefault(value, "x", "y", "z");
        if (metadata == "UnifiedScript.Unreal.UnrealRotator")
            return argument.UnrealType?.Kind == ScriptFunctionValueTypeKind.Struct &&
                argument.UnrealType.UnrealTypePath == "/Script/CoreUObject.Rotator" &&
                IsFiniteVectorDefault(value, "pitch", "yaw", "roll");
        if (metadata == "UnifiedScript.Unreal.UnrealTransform")
            return argument.UnrealType?.Kind == ScriptFunctionValueTypeKind.Struct &&
                argument.UnrealType.UnrealTypePath == "/Script/CoreUObject.Transform" &&
                IsValidTransformDefault(value);
        if (argument.UnrealType?.Kind == ScriptFunctionValueTypeKind.Enum)
            return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(),
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _);
        return false;
    }

    private static bool IsFiniteVectorDefault(JsonElement value, string first, string second, string third) =>
        value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 3 &&
        TryGetFiniteNumber(value, first, out _) && TryGetFiniteNumber(value, second, out _) &&
        TryGetFiniteNumber(value, third, out _);

    private static bool IsValidTransformDefault(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3 ||
            !TryGetFiniteVector(value, "translation") || !TryGetFiniteVector(value, "scale3D") ||
            !value.TryGetProperty("rotation", out JsonElement rotation) ||
            rotation.ValueKind != JsonValueKind.Object || rotation.EnumerateObject().Count() != 4 ||
            !TryGetFiniteNumber(rotation, "x", out double x) || !TryGetFiniteNumber(rotation, "y", out double y) ||
            !TryGetFiniteNumber(rotation, "z", out double z) || !TryGetFiniteNumber(rotation, "w", out double w))
            return false;
        double lengthSquared = x * x + y * y + z * z + w * w;
        return double.IsFinite(lengthSquared) && Math.Abs(lengthSquared - 1.0) <= 1e-6;
    }

    public static string CollectionIdentity(
        JsonElement value,
        ScriptFunctionTypeArgumentDeclaration argument)
    {
        string metadata = argument.TypeMetadataName;
        if (metadata == "System.Boolean")
            return value.GetBoolean() ? "bool:1" : "bool:0";
        if (metadata == "System.Byte")
            return $"byte:{value.GetUInt64()}";
        if (metadata == "System.Int32")
            return $"int:{value.GetInt64()}";
        if (metadata == "System.Int64")
            return $"long:{value.GetInt64()}";
        if (metadata == "System.Single")
        {
            float single = value.GetSingle();
            return $"single:{CanonicalFloat(single)}";
        }
        if (metadata == "System.Double")
            return $"double:{CanonicalDouble(value.GetDouble())}";
        if (metadata == "System.String")
            return "string:" + (value.GetString() ?? string.Empty);
        if (metadata == "UnifiedScript.Unreal.UnrealName")
            return "name:" + CanonicalName(value.GetString());
        if (metadata == "UnifiedScript.Unreal.UnrealText")
            return "text:" + (value.GetString() ?? string.Empty);
        if (metadata is "UnifiedScript.Unreal.UnrealSoftObjectHandle" or
            "UnifiedScript.Unreal.UnrealSoftClassHandle")
            return "soft:" + CanonicalSoftPath(value.GetString());
        if (metadata is "UnifiedScript.Unreal.UnrealObjectHandle" or
            "UnifiedScript.Unreal.UnrealClassHandle")
            return "null";
        if (metadata == "UnifiedScript.Unreal.UnrealVector")
            return "vector:" + CanonicalVector(value, "x", "y", "z");
        if (metadata == "UnifiedScript.Unreal.UnrealRotator")
            return "rotator:" + CanonicalVector(value, "pitch", "yaw", "roll");
        if (metadata == "UnifiedScript.Unreal.UnrealTransform")
        {
            JsonElement rotation = value.GetProperty("rotation");
            JsonElement translation = value.GetProperty("translation");
            JsonElement scale = value.GetProperty("scale3D");
            return "transform:" + CanonicalVector(rotation, "x", "y", "z", "w") + ";" +
                CanonicalVector(translation, "x", "y", "z") + ";" +
                CanonicalVector(scale, "x", "y", "z");
        }
        if (argument.UnrealType?.Kind == ScriptFunctionValueTypeKind.Enum)
            return "enum:" + long.Parse(value.GetString()!, CultureInfo.InvariantCulture);
        throw new JsonException($"Unsupported collection identity leaf '{metadata}'.");
    }

    private static string CanonicalName(string? value) =>
        string.IsNullOrEmpty(value) || string.Equals(value, "None", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : value.ToUpperInvariant();

    private static string CanonicalSoftPath(string? value)
    {
        if (string.IsNullOrEmpty(value) || string.Equals(value, "None", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        int colon = value.IndexOf(':');
        string prefix = colon < 0 ? value : value[..colon];
        string subobject = colon < 0 ? string.Empty : value[(colon + 1)..];
        return prefix.ToUpperInvariant() + "\u001f" + subobject;
    }

    private static string CanonicalVector(
        JsonElement value,
        params string[] components) =>
        string.Join(",", components.Select(component => CanonicalDouble(value.GetProperty(component).GetDouble())));

    private static string CanonicalFloat(float value) =>
        value == 0
            ? "0"
            : BitConverter.SingleToInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture);

    private static string CanonicalDouble(double value) =>
        value == 0
            ? "0"
            : BitConverter.DoubleToInt64Bits(value).ToString("X16", CultureInfo.InvariantCulture);

    private static bool IsValidStructDefault(ScriptStateDeclaration state)
    {
        if (state.DefaultValue.ValueKind != JsonValueKind.Object ||
            state.UnrealType?.Kind != ScriptFunctionValueTypeKind.Struct)
        {
            return false;
        }

        if (state.TypeMetadataName == "UnifiedScript.Unreal.UnrealVector" &&
            state.UnrealType.UnrealTypePath == "/Script/CoreUObject.Vector")
        {
            return TryGetFiniteNumber(state.DefaultValue, "x", out _) &&
                TryGetFiniteNumber(state.DefaultValue, "y", out _) &&
                TryGetFiniteNumber(state.DefaultValue, "z", out _) &&
                state.DefaultValue.EnumerateObject().Count() == 3;
        }
        if (state.TypeMetadataName == "UnifiedScript.Unreal.UnrealRotator" &&
            state.UnrealType.UnrealTypePath == "/Script/CoreUObject.Rotator")
        {
            return TryGetFiniteNumber(state.DefaultValue, "pitch", out _) &&
                TryGetFiniteNumber(state.DefaultValue, "yaw", out _) &&
                TryGetFiniteNumber(state.DefaultValue, "roll", out _) &&
                state.DefaultValue.EnumerateObject().Count() == 3;
        }
        if (state.TypeMetadataName == "UnifiedScript.Unreal.UnrealTransform" &&
            state.UnrealType.UnrealTypePath == "/Script/CoreUObject.Transform" &&
            state.DefaultValue.EnumerateObject().Count() == 3 &&
            TryGetFiniteVector(state.DefaultValue, "translation") &&
            TryGetFiniteVector(state.DefaultValue, "scale3D") &&
            state.DefaultValue.TryGetProperty("rotation", out JsonElement rotation) &&
            rotation.ValueKind == JsonValueKind.Object &&
            rotation.EnumerateObject().Count() == 4 &&
            TryGetFiniteNumber(rotation, "x", out double qx) &&
            TryGetFiniteNumber(rotation, "y", out double qy) &&
            TryGetFiniteNumber(rotation, "z", out double qz) &&
            TryGetFiniteNumber(rotation, "w", out double qw))
        {
            double lengthSquared = qx * qx + qy * qy + qz * qz + qw * qw;
            return double.IsFinite(lengthSquared) && Math.Abs(lengthSquared - 1.0) <= 1e-6;
        }

        return false;
    }

    private static bool TryGetFiniteVector(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out JsonElement vector) ||
            vector.ValueKind != JsonValueKind.Object ||
            vector.EnumerateObject().Count() != 3)
        {
            return false;
        }
        return TryGetFiniteNumber(vector, "x", out _) &&
            TryGetFiniteNumber(vector, "y", out _) &&
            TryGetFiniteNumber(vector, "z", out _);
    }

    private static bool TryGetFiniteNumber(JsonElement value, string name, out double number)
    {
        number = default;
        return value.TryGetProperty(name, out JsonElement component) &&
            component.ValueKind == JsonValueKind.Number &&
            component.TryGetDouble(out number) && double.IsFinite(number);
    }

    private static void ValidateFunction(
        ScriptAssetDeclaration asset,
        ScriptFunctionDeclaration? function,
        ISet<SemanticId> functionIds,
        ISet<string> blueprintOverrideRoutes)
    {
        if (function is null || function.Id.IsEmpty ||
            string.IsNullOrWhiteSpace(function.Name) ||
            string.IsNullOrWhiteSpace(function.QualifiedName) ||
            string.IsNullOrWhiteSpace(function.ReturnTypeMetadataName) ||
            function.Parameters is null)
        {
            throw new JsonException($"Script asset '{asset.AssetPath}' has an invalid function declaration.");
        }

        if (!functionIds.Add(function.Id))
        {
            throw new JsonException($"Duplicate script function ID '{function.Id}'.");
        }

        if (function.Rpc is not null || function.RepNotifyProperty is not null)
        {
            if (!function.IsPublic || function.IsStatic || function.IsEvent ||
                function.ReturnTypeMetadataName != "System.Void" ||
                function.Parameters.Any(p => p is null || p.Direction != ScriptParameterDirection.Input) ||
                function.BlueprintEvent is not null || (function.InputActions?.Count ?? 0) != 0 ||
                (function.DelegateBindings?.Count ?? 0) != 0 || (function.InterfaceFunctions?.Count ?? 0) != 0 ||
                function.Rpc is not null && (function.RepNotifyProperty is not null || !Enum.IsDefined(function.Rpc.Target)) ||
                function.RepNotifyProperty is not null && !IsIdentifier(function.RepNotifyProperty))
                throw new JsonException($"Network method '{function.Name}' must be public, instance, void and input-only with one RPC/RepNotify binding and no conflicting gameplay binding.");
        }

        if (function.BlueprintEvent is not null)
        {
            if (!function.IsPublic || function.IsStatic || function.IsEvent ||
                !Enum.IsDefined(function.BlueprintEvent.SuperMode) ||
                (function.InputActions?.Count ?? 0) != 0 ||
                (function.DelegateBindings?.Count ?? 0) != 0 ||
                (function.InterfaceFunctions?.Count ?? 0) != 0)
            {
                throw new JsonException(
                    $"Blueprint event '{function.QualifiedName}' must be a public instance method " +
                        "with a valid Super policy and cannot own another gameplay binding.");
            }

            if (function.BlueprintEvent.Override is { } blueprintOverride)
            {
                if (!IsValidBlueprintOverrideClassPath(blueprintOverride.DeclaringTypePath) ||
                    !IsValidBlueprintOverrideFunctionName(blueprintOverride.FunctionName))
                {
                    throw new JsonException(
                        $"Blueprint override '{function.QualifiedName}' has an invalid declaring class path or function name.");
                }

                string route = $"{blueprintOverride.DeclaringTypePath}\u001f{blueprintOverride.FunctionName}";
                if (!blueprintOverrideRoutes.Add(route))
                {
                    throw new JsonException(
                        $"Script asset '{asset.AssetPath}' declares duplicate Blueprint override route " +
                        $"'{blueprintOverride.DeclaringTypePath}:{blueprintOverride.FunctionName}' (case-insensitive).");
                }
            }
        }

        ValidateFunctionValueType(function.ReturnUnrealType, function.ReturnTypeMetadataName, 0);

        var parameterIds = new HashSet<SemanticId>();
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScriptParameterDeclaration? parameter in function.Parameters)
        {
            if (parameter is null || parameter.Id.IsEmpty ||
                string.IsNullOrWhiteSpace(parameter.Name) ||
                string.IsNullOrWhiteSpace(parameter.TypeMetadataName) ||
                !Enum.IsDefined(parameter.Direction) ||
                !parameterIds.Add(parameter.Id) || !parameterNames.Add(parameter.Name))
            {
                throw new JsonException($"Script function '{function.QualifiedName}' has an invalid parameter.");
            }
            ValidateFunctionValueType(parameter.UnrealType, parameter.TypeMetadataName, 0);
        }

        ValidateInputActions(function);
        ValidateDelegateBindings(function);
        ValidateInterfaceFunctions(function);
    }

    private static void ValidateInputActions(ScriptFunctionDeclaration function)
    {
        IReadOnlyList<ScriptInputActionBindingDeclaration> bindings =
            function.InputActions ?? [];
        if (bindings.Count == 0)
        {
            return;
        }
        if (!function.IsPublic || function.IsStatic || function.IsEvent ||
            function.BlueprintEvent is not null ||
            function.ReturnTypeMetadataName != "System.Void" ||
            function.Parameters.Count != 1)
        {
            throw new JsonException(
                $"Input handler '{function.QualifiedName}' must be a public instance void " +
                "method with exactly one reviewed value parameter.");
        }

        var ids = new HashSet<SemanticId>();
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScriptInputActionBindingDeclaration? binding in bindings)
        {
            string parameterType = function.Parameters[0].TypeMetadataName;
            bool typeMatches = binding?.ValueType switch
            {
                ScriptInputValueType.Boolean => parameterType == "System.Boolean",
                ScriptInputValueType.Axis1D => parameterType == "System.Single",
                ScriptInputValueType.Axis2D or ScriptInputValueType.Axis3D =>
                    parameterType == "UnifiedScript.Unreal.UnrealVector",
                _ => false,
            };
            string route = binding is null
                ? string.Empty
                : $"{binding.ActionPath}|{binding.TriggerEvent}";
            if (binding is null || binding.Id.IsEmpty ||
                !IsUnrealObjectPath(binding.ActionPath) ||
                !Enum.IsDefined(binding.TriggerEvent) ||
                !Enum.IsDefined(binding.ValueType) || !typeMatches ||
                function.Parameters[0].Direction != ScriptParameterDirection.Input ||
                !ids.Add(binding.Id) || !routes.Add(route))
            {
                throw new JsonException(
                    $"Input handler '{function.QualifiedName}' has an invalid, duplicate, " +
                    "or type-incompatible Enhanced Input binding.");
            }
        }
    }

    private static void ValidateDelegateBindings(ScriptFunctionDeclaration function)
    {
        IReadOnlyList<ScriptDelegateBindingDeclaration> bindings =
            function.DelegateBindings ?? [];
        if (bindings.Count == 0)
        {
            return;
        }
        if (!function.IsEvent || function.IsStatic || function.BlueprintEvent is not null ||
            (function.InputActions?.Count ?? 0) != 0 ||
            (function.InterfaceFunctions?.Count ?? 0) != 0 ||
            function.Parameters.Any(static parameter =>
                parameter.Direction != ScriptParameterDirection.Input))
        {
            throw new JsonException(
                $"Delegate handler '{function.QualifiedName}' has an incompatible gameplay binding.");
        }

        var ids = new HashSet<SemanticId>();
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScriptDelegateBindingDeclaration? binding in bindings)
        {
            string route = binding is null
                ? string.Empty
                : $"{binding.SourceTypePath}|{binding.EventPath}|{binding.Source}|{binding.SourceMember}";
            if (binding is null || binding.Id.IsEmpty ||
                !IsScriptTypePath(binding.SourceTypePath) ||
                !IsMemberPath(binding.EventPath) ||
                !Enum.IsDefined(binding.Source) ||
                binding.SourceMember is not null &&
                    string.IsNullOrWhiteSpace(binding.SourceMember) ||
                !ids.Add(binding.Id) || !routes.Add(route))
            {
                throw new JsonException(
                    $"Delegate handler '{function.QualifiedName}' has an invalid or duplicate binding.");
            }
        }
    }

    private static void ValidateInterfaceFunctions(ScriptFunctionDeclaration function)
    {
        IReadOnlyList<ScriptInterfaceFunctionDeclaration> bindings =
            function.InterfaceFunctions ?? [];
        if (bindings.Count == 0)
        {
            return;
        }
        if (!function.IsPublic || function.IsStatic || function.IsEvent ||
            function.BlueprintEvent is not null ||
            (function.InputActions?.Count ?? 0) != 0 ||
            (function.DelegateBindings?.Count ?? 0) != 0)
        {
            throw new JsonException(
                $"Interface implementation '{function.QualifiedName}' must be a public " +
                "instance method with no competing gameplay binding.");
        }

        var ids = new HashSet<SemanticId>();
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScriptInterfaceFunctionDeclaration? binding in bindings)
        {
            string route = binding is null
                ? string.Empty
                : $"{binding.InterfacePath}|{binding.FunctionName}";
            if (binding is null || binding.Id.IsEmpty ||
                !IsScriptTypePath(binding.InterfacePath) ||
                !IsIdentifier(binding.FunctionName) ||
                !ids.Add(binding.Id) || !routes.Add(route))
            {
                throw new JsonException(
                    $"Interface implementation '{function.QualifiedName}' has an invalid or duplicate binding.");
            }
        }
    }

    private static void ValidateLifecycleHook(
        ScriptAssetDeclaration asset,
        ScriptLifecycleHookDeclaration? hook,
        ISet<SemanticId> hookIds,
        ISet<ScriptLifecycleHookKind> hookKinds)
    {
        if (hook is null || hook.Id.IsEmpty || !Enum.IsDefined(hook.Kind) ||
            string.IsNullOrWhiteSpace(hook.ManagedName) || !IsValidSpan(hook.Span) ||
            !hookIds.Add(hook.Id) || !hookKinds.Add(hook.Kind))
        {
            throw new JsonException(
                $"Script asset '{asset.AssetPath}' has an invalid or duplicate lifecycle hook.");
        }

        string expectedName = hook.Kind switch
        {
            ScriptLifecycleHookKind.BeginPlay => "OnBeginPlay",
            ScriptLifecycleHookKind.Tick => "OnTick",
            ScriptLifecycleHookKind.EndPlay => "OnEndPlay",
            _ => string.Empty,
        };
        if (hook.ManagedName != expectedName)
        {
            throw new JsonException(
                $"Lifecycle hook '{hook.Kind}' must bind the reviewed managed method '{expectedName}'.");
        }
    }

    private static void ValidateFunctionValueType(
        ScriptFunctionValueTypeDeclaration? type,
        string metadataName,
        int depth)
    {
        if (type is null)
        {
            return;
        }
        if (type.Kind is ScriptFunctionValueTypeKind.Array or
            ScriptFunctionValueTypeKind.Set or ScriptFunctionValueTypeKind.Map)
        {
            string definition = type.Kind switch
            {
                ScriptFunctionValueTypeKind.Array => "System.Collections.Generic.IReadOnlyList",
                ScriptFunctionValueTypeKind.Set => "System.Collections.Generic.IReadOnlySet",
                _ => "System.Collections.Generic.IReadOnlyDictionary",
            };
            string nativeName = type.Kind switch
            {
                ScriptFunctionValueTypeKind.Array => "TArray",
                ScriptFunctionValueTypeKind.Set => "TSet",
                _ => "TMap",
            };
            int arity = type.Kind == ScriptFunctionValueTypeKind.Map ? 2 : 1;
            if (depth > 16 || type.UnrealTypePath != string.Empty ||
                type.UnrealNativeName != nativeName || type.Fields is not { Count: 0 } ||
                type.TypeArguments is null || type.TypeArguments.Count != arity ||
                type.TypeArguments.Any(static argument => argument is null ||
                    string.IsNullOrWhiteSpace(argument.TypeMetadataName)))
            {
                throw new JsonException($"Container value type '{metadataName}' has invalid type arguments.");
            }
            string expected = $"{definition}<{string.Join(", ", type.TypeArguments.Select(
                static argument => argument.TypeMetadataName))}>";
            if (metadataName != expected)
            {
                throw new JsonException($"Container value type '{metadataName}' does not match '{expected}'.");
            }
            foreach (ScriptFunctionTypeArgumentDeclaration argument in type.TypeArguments)
            {
                ValidateFunctionValueType(argument.UnrealType, argument.TypeMetadataName, depth + 1);
            }
            return;
        }
        if (depth > 16 || !Enum.IsDefined(type.Kind) ||
            string.IsNullOrWhiteSpace(type.UnrealTypePath) ||
            !type.UnrealTypePath.StartsWith("/Script/", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(type.UnrealNativeName) ||
            type.Fields is null || type.TypeArguments is not null ||
            string.IsNullOrWhiteSpace(metadataName))
        {
            throw new JsonException($"Function value type '{metadataName}' has invalid Unreal identity.");
        }
        if (type.Kind == ScriptFunctionValueTypeKind.Enum && type.Fields.Count != 0)
        {
            throw new JsonException($"Enum value type '{metadataName}' cannot declare struct fields.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < type.Fields.Count; ++index)
        {
            ScriptFunctionStructFieldDeclaration? field = type.Fields[index];
            if (type.Kind != ScriptFunctionValueTypeKind.Struct || field is null ||
                field.Index != index || string.IsNullOrWhiteSpace(field.NativeName) ||
                string.IsNullOrWhiteSpace(field.TypeMetadataName) ||
                !names.Add(field.NativeName))
            {
                throw new JsonException($"Struct value type '{metadataName}' has invalid field identity.");
            }
            ValidateFunctionValueType(field.UnrealType, field.TypeMetadataName, depth + 1);
        }
    }

    private static ScriptAssetDeclaration NormalizeAsset(ScriptAssetDeclaration asset) => asset with
    {
        States = asset.States
            .OrderBy(static state => state.Name, StringComparer.Ordinal)
            .ThenBy(static state => state.Id.Value, StringComparer.Ordinal)
            .Select(static state => state with
            {
                DefaultValue = state.DefaultValue.Clone(),
            })
            .ToArray(),
        Functions = asset.Functions
            .OrderBy(static function => function.QualifiedName, StringComparer.Ordinal)
            .ThenBy(static function => function.Id.Value, StringComparer.Ordinal)
            .Select(static function => function with
            {
                Parameters = function.Parameters.ToArray(),
                InputActions = (function.InputActions ?? [])
                    .OrderBy(static binding => binding.ActionPath, StringComparer.Ordinal)
                    .ThenBy(static binding => binding.TriggerEvent)
                    .ThenBy(static binding => binding.Id.Value, StringComparer.Ordinal)
                    .ToArray(),
                DelegateBindings = (function.DelegateBindings ?? [])
                    .OrderBy(static binding => binding.EventPath, StringComparer.Ordinal)
                    .ThenBy(static binding => binding.SourceTypePath, StringComparer.Ordinal)
                    .ThenBy(static binding => binding.Id.Value, StringComparer.Ordinal)
                    .ToArray(),
                InterfaceFunctions = (function.InterfaceFunctions ?? [])
                    .OrderBy(static binding => binding.InterfacePath, StringComparer.Ordinal)
                    .ThenBy(static binding => binding.FunctionName, StringComparer.Ordinal)
                    .ThenBy(static binding => binding.Id.Value, StringComparer.Ordinal)
                    .ToArray(),
            })
            .ToArray(),
        LifecycleHooks = (asset.LifecycleHooks ?? [])
            .OrderBy(static hook => hook.Kind)
            .ThenBy(static hook => hook.Id.Value, StringComparer.Ordinal)
            .ToArray(),
    };

    private static bool IsValidAssetPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.StartsWith("/", StringComparison.Ordinal) &&
        !path.EndsWith("/", StringComparison.Ordinal) &&
        !path.Contains("//", StringComparison.Ordinal) &&
        !path.Split('/').Any(static segment => string.IsNullOrWhiteSpace(segment) ||
            segment is "." or "..");

    private static bool IsUnrealObjectPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith("/", StringComparison.Ordinal) &&
        !path.Contains(' ') && !path.EndsWith("/", StringComparison.Ordinal);

    public static bool IsValidBlueprintOverrideClassPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal) ||
            path.Contains('\\') || path.Contains(':') || path.Any(char.IsWhiteSpace) ||
            path.Any(static character => character is '<' or '>' or '|' or '*' or '?' or '#') ||
            path.Contains("//", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        if (path.StartsWith("/Script/", StringComparison.Ordinal))
        {
            string nativePath = path["/Script/".Length..];
            int separator = nativePath.IndexOf('.');
            return separator > 0 && separator == nativePath.LastIndexOf('.') &&
                separator + 1 < nativePath.Length &&
                !nativePath[..separator].Contains('/') &&
                IsIdentifier(nativePath[..separator]) &&
                IsIdentifier(nativePath[(separator + 1)..]);
        }

        string[] segments = path[1..].Split('/');
        if (segments.Length < 2 || segments[..^1].Any(static segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".." || segment.Contains('.')))
        {
            return false;
        }

        string objectName = segments[^1];
        int separatorIndex = objectName.IndexOf('.');
        return separatorIndex > 0 && separatorIndex == objectName.LastIndexOf('.') &&
            separatorIndex + 1 < objectName.Length &&
            objectName[(separatorIndex + 1)..].EndsWith("_C", StringComparison.Ordinal) &&
            IsIdentifier(objectName[..separatorIndex]) &&
            IsIdentifier(objectName[(separatorIndex + 1)..]);
    }

    public static bool IsValidBlueprintOverrideFunctionName(string? name) =>
        IsIdentifier(name);

    private static bool IsScriptTypePath(string? path) =>
        IsUnrealObjectPath(path) && path!.StartsWith("/Script/", StringComparison.Ordinal) &&
        path.Contains('.') && !path.Contains(':');

    private static bool IsMemberPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        int separator = path.LastIndexOf(':');
        return separator > 0 && separator + 1 < path.Length &&
            IsScriptTypePath(path[..separator]) && IsIdentifier(path[(separator + 1)..]);
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(static character => char.IsLetterOrDigit(character) || character == '_');

    private static bool IsValidSpan(SourceSpan? span)
    {
        if (span is null || string.IsNullOrWhiteSpace(span.FilePath) ||
            span.Start is null || span.End is null)
        {
            return false;
        }

        bool unknown = span.Start.Line == 0 && span.Start.Column == 0 &&
            span.End.Line == 0 && span.End.Column == 0;
        bool orderedEnd = span.End.Line > span.Start.Line ||
            span.End.Line == span.Start.Line && span.End.Column >= span.Start.Column;
        return unknown ||
            span.Start.Line >= 1 && span.Start.Column >= 1 &&
            span.End.Line >= span.Start.Line && span.End.Column >= 1 && orderedEnd;
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}
