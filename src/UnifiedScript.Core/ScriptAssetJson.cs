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
        if (document.SchemaVersion != ScriptAssetDocument.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported script-asset schema '{document.SchemaVersion}'. " +
                $"Expected '{ScriptAssetDocument.CurrentSchemaVersion}'.");
        }

        if (document.SourceCompilationId.IsEmpty ||
            document.SemanticConfigurationId.IsEmpty ||
            document.Assets is null)
        {
            throw new JsonException("A script-asset document has invalid required fields.");
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

        var stateIds = new HashSet<SemanticId>();
        var stateNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScriptStateDeclaration? state in asset.States)
        {
            ValidateState(asset, state, stateIds, stateNames);
        }

        var functionIds = new HashSet<SemanticId>();
        foreach (ScriptFunctionDeclaration? function in asset.Functions)
        {
            ValidateFunction(asset, function, functionIds);
        }
    }

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
            ScriptStateTypeKind.Enum => state.DefaultValue.ValueKind == JsonValueKind.String,
            _ => false,
        };
        if (!validDefault)
        {
            throw new JsonException(
                $"Script state '{state.Name}' has a default incompatible with '{state.TypeKind}'.");
        }
    }

    private static void ValidateFunction(
        ScriptAssetDeclaration asset,
        ScriptFunctionDeclaration? function,
        ISet<SemanticId> functionIds)
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

        ValidateFunctionValueType(function.ReturnUnrealType, function.ReturnTypeMetadataName, 0);

        var parameterIds = new HashSet<SemanticId>();
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScriptParameterDeclaration? parameter in function.Parameters)
        {
            if (parameter is null || parameter.Id.IsEmpty ||
                string.IsNullOrWhiteSpace(parameter.Name) ||
                string.IsNullOrWhiteSpace(parameter.TypeMetadataName) ||
                !parameterIds.Add(parameter.Id) || !parameterNames.Add(parameter.Name))
            {
                throw new JsonException($"Script function '{function.QualifiedName}' has an invalid parameter.");
            }
            ValidateFunctionValueType(parameter.UnrealType, parameter.TypeMetadataName, 0);
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
        if (depth > 16 || !Enum.IsDefined(type.Kind) ||
            string.IsNullOrWhiteSpace(type.UnrealTypePath) ||
            !type.UnrealTypePath.StartsWith("/Script/", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(type.UnrealNativeName) ||
            type.Fields is null ||
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
            })
            .ToArray(),
    };

    private static bool IsValidAssetPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.StartsWith("/", StringComparison.Ordinal) &&
        !path.EndsWith("/", StringComparison.Ordinal) &&
        !path.Contains("//", StringComparison.Ordinal) &&
        !path.Split('/').Any(static segment => string.IsNullOrWhiteSpace(segment) ||
            segment is "." or "..");

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
