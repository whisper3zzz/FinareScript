using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Authoring;

public static class AuthoringSurfaceJson
{
    public static string Serialize(AuthoringSurfaceDocument document, bool writeIndented = true) =>
        JsonSerializer.Serialize(NormalizeAndValidate(document), UnifiedJson.CreateOptions(writeIndented));

    public static AuthoringSurfaceDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        AuthoringSurfaceDocument? document = JsonSerializer.Deserialize<AuthoringSurfaceDocument>(
            json,
            UnifiedJson.CreateOptions());
        return document is null
            ? throw new JsonException("The authoring surface document was empty.")
            : NormalizeAndValidate(document);
    }

    public static AuthoringSurfaceDocument NormalizeAndValidate(AuthoringSurfaceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != AuthoringSurfaceDocument.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported authoring surface schema '{document.SchemaVersion}'. Expected " +
                $"'{AuthoringSurfaceDocument.CurrentSchemaVersion}'.");
        }

        if (document.SourceCompilationId.IsEmpty || document.SemanticConfigurationId.IsEmpty ||
            document.Assets is null)
        {
            throw new JsonException("An authoring surface has invalid required fields.");
        }

        var assetIds = new HashSet<SemanticId>();
        var functionIds = new HashSet<SemanticId>();
        foreach (AuthoringAssetSurface? asset in document.Assets)
        {
            ValidateAsset(asset, assetIds, functionIds);
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
        AuthoringAssetSurface? asset,
        ISet<SemanticId> assetIds,
        ISet<SemanticId> functionIds)
    {
        if (asset is null || asset.Id.IsEmpty || string.IsNullOrWhiteSpace(asset.QualifiedName) ||
            string.IsNullOrWhiteSpace(asset.AssetPath) || !IsValidSpan(asset.Span) ||
            asset.States is null || asset.Functions is null || !assetIds.Add(asset.Id))
        {
            throw new JsonException("An authoring asset has invalid or duplicate required fields.");
        }

        var stateIds = new HashSet<SemanticId>();
        foreach (ScriptStateDeclaration? state in asset.States)
        {
            if (state is null || state.Id.IsEmpty || string.IsNullOrWhiteSpace(state.Name) ||
                !stateIds.Add(state.Id))
            {
                throw new JsonException($"Authoring asset '{asset.Id}' has an invalid state.");
            }
        }

        foreach (AuthoringFunctionSurface? function in asset.Functions)
        {
            ValidateFunction(function, functionIds);
        }
    }

    private static void ValidateFunction(
        AuthoringFunctionSurface? function,
        ISet<SemanticId> functionIds)
    {
        if (function is null || function.Id.IsEmpty || string.IsNullOrWhiteSpace(function.Name) ||
            string.IsNullOrWhiteSpace(function.QualifiedName) ||
            string.IsNullOrWhiteSpace(function.ReturnTypeMetadataName) ||
            function.Parameters is null || function.RootRegionId.IsEmpty ||
            function.Regions is null || function.Actions is null || !functionIds.Add(function.Id))
        {
            throw new JsonException("An authoring function has invalid or duplicate required fields.");
        }

        var parameterIds = new HashSet<SemanticId>();
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScriptParameterDeclaration? parameter in function.Parameters)
        {
            if (parameter is null || parameter.Id.IsEmpty ||
                string.IsNullOrWhiteSpace(parameter.Name) ||
                string.IsNullOrWhiteSpace(parameter.TypeMetadataName) ||
                !parameterIds.Add(parameter.Id) || !parameterNames.Add(parameter.Name))
            {
                throw new JsonException($"Authoring function '{function.Id}' has an invalid parameter.");
            }
        }

        var actions = new Dictionary<SemanticId, AuthoringActionSurface>();
        foreach (AuthoringActionSurface? action in function.Actions)
        {
            if (action is null || action.Id.IsEmpty || string.IsNullOrWhiteSpace(action.GraphKind) ||
                string.IsNullOrWhiteSpace(action.Title) || action.Summary is null ||
                !IsValidSpan(action.Span) || action.RegionId.IsEmpty ||
                action.DebugTargetId is SemanticId debugTargetId && debugTargetId.IsEmpty ||
                action.DeclarationReferenceIds is null || action.EditCapabilities is null ||
                action.DeclarationReferenceIds.Any(static id => id.IsEmpty) ||
                action.DeclarationReferenceIds.Distinct().Count() !=
                    action.DeclarationReferenceIds.Count ||
                !actions.TryAdd(action.Id, action))
            {
                throw new JsonException($"Authoring function '{function.Id}' has an invalid action.");
            }

            ValidateEdits(action);
        }

        var regions = new Dictionary<SemanticId, AuthoringRegionSurface>();
        foreach (AuthoringRegionSurface? region in function.Regions)
        {
            if (region is null || region.Id.IsEmpty || !IsValidSpan(region.Span) ||
                region.DirectActionIds is null || region.ChildRegionIds is null ||
                !regions.TryAdd(region.Id, region))
            {
                throw new JsonException($"Authoring function '{function.Id}' has an invalid region.");
            }
        }

        if (!regions.TryGetValue(function.RootRegionId, out AuthoringRegionSurface? root) ||
            root.ParentRegionId is not null)
        {
            throw new JsonException($"Authoring function '{function.Id}' has no valid root region.");
        }

        var actionOwners = new Dictionary<SemanticId, SemanticId>();
        var childOwners = new Dictionary<SemanticId, SemanticId>();
        foreach (AuthoringRegionSurface region in regions.Values)
        {
            if (region.Id != root.Id &&
                (region.ParentRegionId is not SemanticId parentId || !regions.ContainsKey(parentId)))
            {
                throw new JsonException($"Authoring region '{region.Id}' has no valid parent.");
            }

            if (region.DirectActionIds.Any(static id => id.IsEmpty) ||
                region.DirectActionIds.Distinct().Count() != region.DirectActionIds.Count ||
                region.ChildRegionIds.Any(static id => id.IsEmpty) ||
                region.ChildRegionIds.Distinct().Count() != region.ChildRegionIds.Count)
            {
                throw new JsonException(
                    $"Authoring region '{region.Id}' has duplicate or empty members.");
            }

            foreach (SemanticId actionId in region.DirectActionIds)
            {
                if (!actions.ContainsKey(actionId) || !actionOwners.TryAdd(actionId, region.Id))
                {
                    throw new JsonException(
                        $"Authoring region '{region.Id}' has an unknown or multiply owned action.");
                }
            }

            foreach (SemanticId childId in region.ChildRegionIds)
            {
                if (!regions.TryGetValue(childId, out AuthoringRegionSurface? child) ||
                    child.ParentRegionId != region.Id || !childOwners.TryAdd(childId, region.Id))
                {
                    throw new JsonException($"Authoring region '{region.Id}' has an invalid child.");
                }
            }

            if (region.AnchorActionId is SemanticId anchorId &&
                (!actions.ContainsKey(anchorId) || !region.DirectActionIds.Contains(anchorId)))
            {
                throw new JsonException($"Authoring region '{region.Id}' has an invalid anchor action.");
            }
        }

        if (actionOwners.Count != actions.Count ||
            actions.Values.Any(action => actionOwners[action.Id] != action.RegionId))
        {
            throw new JsonException($"Authoring function '{function.Id}' has inconsistent action ownership.");
        }

        if (childOwners.Count != regions.Count - 1 || childOwners.ContainsKey(root.Id))
        {
            throw new JsonException($"Authoring function '{function.Id}' has orphaned regions.");
        }
    }

    private static void ValidateEdits(AuthoringActionSurface action)
    {
        var editIds = new HashSet<SemanticId>();
        foreach (AuthoringEditCapability? edit in action.EditCapabilities)
        {
            bool validLiteral = edit?.Kind == AuthoringEditKind.Literal &&
                edit.Operation == "replaceLiteral" && edit.ExpectedValueText is not null;
            bool validOperator = edit?.Kind == AuthoringEditKind.Operator &&
                edit.Operation == "changeOperator" && edit.ExpectedOperator is not null &&
                edit.ExpectedResultTypeId is not null && edit.ExpectedOperandTypeId is not null;
            if (edit is null || edit.Id.IsEmpty || edit.TargetNodeId.IsEmpty ||
                string.IsNullOrWhiteSpace(edit.Label) || string.IsNullOrWhiteSpace(edit.CurrentValue) ||
                edit.Options is null || !editIds.Add(edit.Id) || !(validLiteral || validOperator))
            {
                throw new JsonException($"Authoring action '{action.Id}' has an invalid edit capability.");
            }
        }
    }

    private static AuthoringAssetSurface NormalizeAsset(AuthoringAssetSurface asset) => asset with
    {
        States = asset.States
            .OrderBy(static state => state.Span.Start.Line)
            .ThenBy(static state => state.Span.Start.Column)
            .ThenBy(static state => state.Id.Value, StringComparer.Ordinal)
            .ToArray(),
        Functions = asset.Functions
            .OrderBy(static function => function.QualifiedName, StringComparer.Ordinal)
            .ThenBy(static function => function.Id.Value, StringComparer.Ordinal)
            .Select(NormalizeFunction)
            .ToArray(),
    };

    private static AuthoringFunctionSurface NormalizeFunction(AuthoringFunctionSurface function) => function with
    {
        Regions = function.Regions
            .OrderBy(static region => region.Span.Start.Line)
            .ThenBy(static region => region.Span.Start.Column)
            .ThenBy(static region => region.Id.Value, StringComparer.Ordinal)
            .ToArray(),
        Actions = function.Actions
            .OrderBy(static action => action.Span.Start.Line)
            .ThenBy(static action => action.Span.Start.Column)
            .ThenBy(static action => action.Id.Value, StringComparer.Ordinal)
            .Select(static action => action with
            {
                DeclarationReferenceIds = action.DeclarationReferenceIds
                    .Distinct()
                    .OrderBy(static id => id.Value, StringComparer.Ordinal)
                    .ToArray(),
                EditCapabilities = action.EditCapabilities
                    .OrderBy(static edit => edit.Label, StringComparer.Ordinal)
                    .ThenBy(static edit => edit.Id.Value, StringComparer.Ordinal)
                    .ToArray(),
            })
            .ToArray(),
    };

    private static bool IsValidSpan(SourceSpan? span) =>
        span is not null && !string.IsNullOrWhiteSpace(span.FilePath) &&
        span.Start is not null && span.End is not null && span.Start.Line >= 0 &&
        span.Start.Column >= 0 && span.End.Line >= span.Start.Line && span.End.Column >= 0;
}
