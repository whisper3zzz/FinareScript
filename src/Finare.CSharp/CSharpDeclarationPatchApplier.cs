using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Finare.Core;
using Finare.Core.Hir;

namespace Finare.CSharp;

public sealed class CSharpDeclarationPatchApplier
{
    private const string AssetAnnotationKind = "Finare.DeclarationPatch.Asset";
    private const string StateAnnotationKind = "Finare.DeclarationPatch.State";
    private const string FunctionAnnotationKind = "Finare.DeclarationPatch.Function";
    private const string ClassBaseAnnotationKind = "Finare.DeclarationPatch.ClassBase";
    private const string ConstructorAssignmentAnnotationKind =
        "Finare.DeclarationPatch.ConstructorAssignment";
    private readonly CSharpSemanticPipeline pipeline = new();
    private readonly CSharpCompilationHost compilationHost = new();

    public CSharpPatchResult Apply(
        CSharpCompilationRequest request,
        DeclarationPatchDocument patch)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(patch);
        patch = DeclarationPatchJson.NormalizeAndValidate(patch);

        CSharpAnalysisResult original = pipeline.Analyze(request);
        if (!original.Succeeded || original.ScriptAssets is null)
        {
            return Failure(original.Diagnostics);
        }

        if (original.ScriptAssets.SourceCompilationId != patch.SourceCompilationId)
        {
            return Failure(new UnifiedDiagnostic(
                DeclarationPatchDiagnosticCodes.SourceConflict,
                UnifiedDiagnosticSeverity.Error,
                "The declaration patch was authored for a different source compilation.",
                SourceSpan.Unknown("<declaration-patch>")));
        }

        if (original.ScriptAssets.SemanticConfigurationId != patch.SemanticConfigurationId)
        {
            return Failure(new UnifiedDiagnostic(
                DeclarationPatchDiagnosticCodes.ConfigurationConflict,
                UnifiedDiagnosticSeverity.Error,
                "The declaration patch was authored for a different semantic configuration.",
                SourceSpan.Unknown("<declaration-patch>")));
        }

        SyntaxTree[] syntaxTrees = request.Sources
            .Select(source => CSharpSyntaxTree.ParseText(
                source.Source,
                request.Settings.ParseOptions,
                source.LogicalPath))
            .ToArray();
        CSharpCompilation compilation = compilationHost.Create(request, syntaxTrees);
        var contexts = syntaxTrees.ToDictionary(
            static tree => tree.FilePath,
            tree => new RewriteContext(
                tree,
                tree.GetRoot(),
                compilation.GetSemanticModel(tree, ignoreAccessibility: false)),
            StringComparer.Ordinal);

        var assets = original.ScriptAssets.Assets.ToDictionary(static asset => asset.Id);
        var methods = original.Hir!.Types
            .SelectMany(static type => type.Methods)
            .ToDictionary(static method => method.Id);
        StateLocation[] stateLocations = original.ScriptAssets.Assets
            .SelectMany(asset => asset.States.Select(state => new StateLocation(asset, state)))
            .ToArray();
        if (!TryResolveStateLocations(stateLocations, contexts, out var states, out UnifiedDiagnostic? stateOwnerFailure))
        {
            return Failure(stateOwnerFailure!);
        }

        FunctionLocation[] functionLocations = original.ScriptAssets.Assets
            .SelectMany(asset => asset.Functions.Select(function => new FunctionLocation(
                asset,
                function,
                methods[function.Id])))
            .ToArray();
        if (!TryResolveFunctionLocations(functionLocations, contexts, out var functions, out UnifiedDiagnostic? functionOwnerFailure))
        {
            return Failure(functionOwnerFailure!);
        }
        var assetAdds = new List<AssetAddPlan>();
        var assetUpdates = new List<AssetUpdatePlan>();
        var classBaseUpdates = new List<ClassBaseUpdatePlan>();
        var assetRemoves = new List<AssetRemovePlan>();
        var adds = new List<AddPlan>();
        var eventAdds = new List<EventAddPlan>();
        var updates = new List<UpdatePlan>();
        var removes = new List<RemovePlan>();
        var functionAdds = new List<FunctionAddPlan>();
        var functionUpdates = new List<FunctionUpdatePlan>();
        var functionRemoves = new List<FunctionRemovePlan>();
        var lifecycleAdds = new List<LifecycleAddPlan>();
        foreach (DeclarationPatchOperation operation in patch.Operations)
        {
            switch (operation)
            {
                case AddScriptAssetPatchOperation addAsset:
                    CSharpSourceDocument? targetSource = request.Sources.SingleOrDefault(source =>
                        string.Equals(
                            source.LogicalPath,
                            addAsset.TargetSourcePath,
                            StringComparison.Ordinal));
                    if (targetSource is null)
                    {
                        return Failure(InvalidReplacement(
                            $"Target C# source '{addAsset.TargetSourcePath}' was not found in the workspace.",
                            SourceSpan.Unknown(addAsset.TargetSourcePath)));
                    }
                    if (!TryValidateAssetSpec(
                            addAsset.Asset,
                            SourceSpan.Unknown(addAsset.TargetSourcePath),
                            out UnifiedDiagnostic? assetAddFailure))
                    {
                        return Failure(assetAddFailure!);
                    }
                    assetAdds.Add(new AssetAddPlan(targetSource, addAsset.Asset));
                    break;
                case UpdateScriptAssetPatchOperation updateAsset:
                    if (!assets.TryGetValue(
                            updateAsset.TargetAssetId,
                            out ScriptAssetDeclaration? updatedAsset))
                    {
                        return TargetNotFound(updateAsset.TargetAssetId, "script asset");
                    }
                    if (!Matches(updatedAsset, updateAsset.Expected))
                    {
                        return PreconditionFailed(updatedAsset);
                    }
                    assetUpdates.Add(new AssetUpdatePlan(
                        updatedAsset,
                        updateAsset.ReplacementAssetPath));
                    break;
                case UpdateScriptClassBasePatchOperation updateClassBase:
                    if (!assets.TryGetValue(
                            updateClassBase.TargetAssetId,
                            out ScriptAssetDeclaration? classBaseAsset))
                    {
                        return TargetNotFound(updateClassBase.TargetAssetId, "script asset");
                    }
                    if (!Matches(classBaseAsset, updateClassBase.Expected))
                    {
                        return PreconditionFailed(classBaseAsset);
                    }
                    if (classBaseAsset.ClassBinding is null ||
                        classBaseAsset.ClassBinding != updateClassBase.ExpectedClassBinding)
                    {
                        return Failure(new UnifiedDiagnostic(
                            DeclarationPatchDiagnosticCodes.DeclarationPreconditionFailed,
                            UnifiedDiagnosticSeverity.Error,
                            $"Script Asset '{classBaseAsset.QualifiedName}' no longer matches the " +
                            "expected class binding.",
                            classBaseAsset.Span,
                            classBaseAsset.Id));
                    }
                    if (!TryResolveClassBaseLocation(
                            classBaseAsset,
                            contexts,
                            out ClassBaseUpdatePlan? classBasePlan,
                            out UnifiedDiagnostic? classBaseFailure))
                    {
                        return Failure(classBaseFailure!);
                    }
                    classBaseUpdates.Add(classBasePlan! with
                    {
                        ReplacementBaseClassPath = updateClassBase.ReplacementBaseClassPath,
                    });
                    break;
                case RemoveScriptAssetPatchOperation removeAsset:
                    if (!assets.TryGetValue(
                            removeAsset.TargetAssetId,
                            out ScriptAssetDeclaration? removedAsset))
                    {
                        return TargetNotFound(removeAsset.TargetAssetId, "script asset");
                    }
                    if (!Matches(removedAsset, removeAsset.Expected))
                    {
                        return PreconditionFailed(removedAsset);
                    }
                    assetRemoves.Add(new AssetRemovePlan(removedAsset));
                    break;
                case AddScriptStatePatchOperation add:
                    if (!assets.TryGetValue(add.TargetAssetId, out ScriptAssetDeclaration? stateAsset))
                    {
                        return TargetNotFound(add.TargetAssetId, "script asset");
                    }

                    if (!TryValidateSpec(add.State, stateAsset.Span, out UnifiedDiagnostic? addFailure))
                    {
                        return Failure(addFailure!);
                    }

                    adds.Add(new AddPlan(stateAsset, add.State));
                    break;
                case UpdateScriptStatePatchOperation update:
                    if (!states.TryGetValue(update.TargetStateId, out StateLocation? updateLocation))
                    {
                        return TargetNotFound(update.TargetStateId, "script state");
                    }

                    if (!Matches(updateLocation.State, update.Expected))
                    {
                        return PreconditionFailed(updateLocation.State);
                    }

                    if (!TryValidateSpec(
                            update.Replacement,
                            updateLocation.State.Span,
                            out UnifiedDiagnostic? updateFailure))
                    {
                        return Failure(updateFailure!);
                    }

                    if (!string.Equals(
                            update.Expected.Name,
                            update.Replacement.Name,
                            StringComparison.Ordinal) &&
                        GetSource(request, updateLocation.State.Span.FilePath).IdentitySidecar is null)
                    {
                        return Failure(new UnifiedDiagnostic(
                            DeclarationPatchDiagnosticCodes.PersistentIdentityRequired,
                            UnifiedDiagnosticSeverity.Error,
                            $"Renaming script state '{update.Expected.Name}' requires a .usids sidecar.",
                            updateLocation.State.Span,
                            update.TargetStateId));
                    }

                    updates.Add(new UpdatePlan(
                        updateLocation.Asset,
                        updateLocation.State,
                        update.Replacement));
                    break;
                case RemoveScriptStatePatchOperation remove:
                    if (!states.TryGetValue(remove.TargetStateId, out StateLocation? removeLocation))
                    {
                        return TargetNotFound(remove.TargetStateId, "script state");
                    }

                    if (!Matches(removeLocation.State, remove.Expected))
                    {
                        return PreconditionFailed(removeLocation.State);
                    }

                    removes.Add(new RemovePlan(removeLocation.Asset, removeLocation.State));
                    break;
                case AddScriptFunctionPatchOperation addFunction:
                    if (!assets.TryGetValue(
                            addFunction.TargetAssetId,
                            out ScriptAssetDeclaration? functionAsset))
                    {
                        return TargetNotFound(addFunction.TargetAssetId, "script asset");
                    }
                    if (!TryValidateFunctionSpec(
                            addFunction.Function,
                            functionAsset.Span,
                            out UnifiedDiagnostic? functionAddFailure))
                    {
                        return Failure(functionAddFailure!);
                    }
                    functionAdds.Add(new FunctionAddPlan(functionAsset, addFunction.Function));
                    break;
                case UpdateScriptFunctionPatchOperation updateFunction:
                    if (!functions.TryGetValue(
                            updateFunction.TargetFunctionId,
                            out FunctionLocation? updatedFunction))
                    {
                        return TargetNotFound(updateFunction.TargetFunctionId, "script function");
                    }
                    if (!Matches(updatedFunction.Function, updateFunction.Expected))
                    {
                        return PreconditionFailed(updatedFunction);
                    }
                    if (updatedFunction.IsUnrealEvent || updatedFunction.IsLifecycleHook)
                    {
                        return Failure(InvalidReplacement(
                            "Reflected Event Entries and lifecycle hooks have generated signatures; " +
                            "remove and recreate the declaration instead of editing its contract.",
                            updatedFunction.Method.Span));
                    }
                    if (updateFunction.Expected.Parameters.Count !=
                        updateFunction.Replacement.Parameters.Count)
                    {
                        return Failure(InvalidReplacement(
                            "Editing an existing function cannot change its parameter count; " +
                            "remove and recreate the function for that structural change.",
                            updatedFunction.Method.Span));
                    }
                    if (!TryValidateFunctionSpec(
                            updateFunction.Replacement,
                            updatedFunction.Method.Span,
                            out UnifiedDiagnostic? functionUpdateFailure))
                    {
                        return Failure(functionUpdateFailure!);
                    }
                    functionUpdates.Add(new FunctionUpdatePlan(
                        updatedFunction,
                        updateFunction.Replacement));
                    break;
                case RemoveScriptFunctionPatchOperation removeFunction:
                    if (!functions.TryGetValue(
                            removeFunction.TargetFunctionId,
                            out FunctionLocation? removedFunction))
                    {
                        return TargetNotFound(removeFunction.TargetFunctionId, "script function");
                    }
                    if (!Matches(removedFunction.Function, removeFunction.Expected))
                    {
                        return PreconditionFailed(removedFunction);
                    }
                    functionRemoves.Add(new FunctionRemovePlan(removedFunction));
                    break;
                case AddUnrealLifecycleHookPatchOperation addLifecycle:
                    if (!assets.TryGetValue(
                            addLifecycle.TargetAssetId,
                            out ScriptAssetDeclaration? lifecycleAsset))
                    {
                        return TargetNotFound(addLifecycle.TargetAssetId, "script asset");
                    }
                    if ((lifecycleAsset.LifecycleHooks ?? []).Any(hook =>
                            MatchesLifecycle(hook, addLifecycle.Hook)))
                    {
                        return Failure(InvalidReplacement(
                            $"Script asset '{lifecycleAsset.QualifiedName}' already declares the " +
                            $"'{addLifecycle.Hook}' lifecycle hook.",
                            lifecycleAsset.Span));
                    }
                    lifecycleAdds.Add(new LifecycleAddPlan(lifecycleAsset, addLifecycle.Hook));
                    break;
                case AddUnrealEventHandlerPatchOperation addEvent:
                    if (!assets.TryGetValue(
                            addEvent.TargetAssetId,
                            out ScriptAssetDeclaration? eventAsset))
                    {
                        return TargetNotFound(addEvent.TargetAssetId, "script asset");
                    }
                    if (!TryValidateEventHandler(
                            addEvent.Handler,
                            eventAsset.Span,
                            out UnifiedDiagnostic? eventFailure))
                    {
                        return Failure(eventFailure!);
                    }
                    eventAdds.Add(new EventAddPlan(eventAsset, addEvent.Handler));
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Validated declaration operation '{operation.GetType().Name}' was not handled.");
            }
        }

        UnifiedDiagnostic? finalContractFailure = ValidateFinalContracts(
            original.ScriptAssets,
            assetAdds,
            assetUpdates,
            classBaseUpdates,
            assetRemoves,
            adds,
            eventAdds,
            updates,
            removes,
            functionAdds,
            functionUpdates,
            functionRemoves,
            lifecycleAdds);
        if (finalContractFailure is not null)
        {
            return Failure(finalContractFailure);
        }

        if (!TryAnnotateTargets(
                contexts,
                assetUpdates,
                classBaseUpdates,
                assetRemoves,
                adds,
                eventAdds,
                updates,
                removes,
                functionAdds,
                functionUpdates,
                functionRemoves,
                lifecycleAdds,
                out UnifiedDiagnostic? annotationFailure))
        {
            return Failure(annotationFailure!);
        }

        var constructorDefaultUpdates = new HashSet<SemanticId>();
        IReadOnlyList<ConstructorDefaultLocation> constructorDefaultLocations =
            PrepareConstructorDefaultChanges(contexts, updates);
        RenameReferences(contexts, updates, functionUpdates);
        ApplyConstructorDefaultChanges(constructorDefaultLocations, constructorDefaultUpdates);
        ApplyClassBaseChanges(classBaseUpdates);
        ApplyPropertyChanges(contexts, updates, constructorDefaultUpdates, removes);
        ApplyAssetChanges(contexts, assetUpdates, assetRemoves);
        ApplyFunctionChanges(contexts, functionUpdates, functionRemoves);
        ApplyAdditions(contexts, adds);
        ApplyFunctionAdditions(contexts, functionAdds);
        ApplyLifecycleAdditions(contexts, lifecycleAdds);
        ApplyEventAdditions(contexts, eventAdds);
        if (!TryApplyAssetAdditions(
                contexts,
                assetAdds,
                out UnifiedDiagnostic? assetAdditionFailure))
        {
            return Failure(assetAdditionFailure!);
        }

        IReadOnlyDictionary<string, IdentitySidecarDocument> effectiveSidecars = original.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        CSharpSourceDocument[] patchedSources = request.Sources
            .Select(source => CreatePatchedSource(
                source,
                contexts[source.LogicalPath].Root,
                effectiveSidecars.GetValueOrDefault(source.LogicalPath),
                updates.Where(update => update.State.Span.FilePath == source.LogicalPath).ToArray(),
                functionUpdates.Where(update =>
                    update.Location.Method.Span.FilePath == source.LogicalPath).ToArray()))
            .ToArray();
        var patchedRequest = new CSharpCompilationRequest(
            request.AssemblyName,
            patchedSources,
            request.ReferencePaths,
            request.EffectCatalog,
            request.Settings,
            request.RuntimeReferencePaths,
            request.IncludeDefaultReferences, request.SourceGeneratorPaths);
        CSharpAnalysisResult patchedAnalysis = pipeline.Analyze(patchedRequest);
        if (!patchedAnalysis.Succeeded || patchedAnalysis.ScriptAssets is null)
        {
            return Failure(patchedAnalysis.Diagnostics);
        }

        UnifiedDiagnostic? postconditionFailure = ValidatePostconditions(
            patch,
            original.ScriptAssets,
            patchedAnalysis.ScriptAssets,
            patchedAnalysis.Hir!,
            states,
            functions,
            functionUpdates
                .Where(update => request.Sources.Any(source =>
                    source.IdentitySidecar?.Entries.Any(entry =>
                        entry.Id == update.Location.Function.Id &&
                        entry.Kind == "function") == true))
                .Select(update => update.Location.Function.Id)
                .ToHashSet());
        if (postconditionFailure is not null)
        {
            return Failure(postconditionFailure);
        }

        IdentitySidecarDocument[] persistedSidecars = patchedAnalysis.IdentitySidecars.ToArray();
        var persistedSources = patchedRequest.Sources
            .Select(source => new CSharpSourceDocument(
                source.LogicalPath,
                source.Source,
                persistedSidecars.SingleOrDefault(sidecar =>
                    string.Equals(sidecar.SourcePath, source.LogicalPath, StringComparison.Ordinal))))
            .ToArray();
        var persistedRequest = new CSharpCompilationRequest(
            patchedRequest.AssemblyName,
            persistedSources,
            patchedRequest.ReferencePaths,
            patchedRequest.EffectCatalog,
            patchedRequest.Settings,
            patchedRequest.RuntimeReferencePaths,
            patchedRequest.IncludeDefaultReferences, patchedRequest.SourceGeneratorPaths);
        return new CSharpPatchResult(persistedRequest, patchedAnalysis, patchedAnalysis.Diagnostics);
    }

    private static bool TryResolveClassBaseLocation(
        ScriptAssetDeclaration asset,
        IReadOnlyDictionary<string, RewriteContext> contexts,
        out ClassBaseUpdatePlan? plan,
        out UnifiedDiagnostic? failure)
    {
        plan = null;
        failure = null;
        if (!contexts.TryGetValue(asset.Span.FilePath, out RewriteContext? assetContext))
        {
            failure = InvalidReplacement(
                $"The Script Asset '{asset.QualifiedName}' is not backed by a source in the " +
                "current workspace.",
                asset.Span);
            return false;
        }

        TypeDeclarationSyntax[] typeMatches = assetContext.Tree.GetRoot()
            .DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(type => RoslynSourceSpan.FromNode(type, asset.Span.FilePath) == asset.Span)
            .ToArray();
        if (typeMatches.Length != 1)
        {
            failure = MissingSyntax(asset.Id, asset.Span, "Script Asset type");
            return false;
        }

        INamedTypeSymbol? typeSymbol = assetContext.SemanticModel.GetDeclaredSymbol(typeMatches[0]);
        if (typeSymbol is null)
        {
            failure = InvalidReplacement(
                $"Roslyn could not resolve the Script Asset type '{asset.QualifiedName}'.",
                asset.Span);
            return false;
        }

        AttributeData[] attributes = typeSymbol.GetAttributes()
            .Where(IsUnrealScriptClassAttribute)
            .ToArray();
        if (attributes.Length != 1 ||
            attributes[0].ApplicationSyntaxReference?.GetSyntax() is not AttributeSyntax attribute)
        {
            failure = InvalidReplacement(
                $"Script Asset '{asset.QualifiedName}' must declare its own " +
                "UnrealScriptClassAttribute in the current source workspace.",
                asset.Span);
            return false;
        }

        string attributePath = attribute.SyntaxTree.FilePath;
        if (!contexts.TryGetValue(attributePath, out RewriteContext? attributeContext))
        {
            failure = InvalidReplacement(
                $"The class binding attribute for '{asset.QualifiedName}' is outside the " +
                "current source workspace.",
                SourceSpan.Unknown(attributePath));
            return false;
        }

        plan = new ClassBaseUpdatePlan(asset, null, attributeContext, attribute);
        return true;
    }

    private static bool IsUnrealScriptClassAttribute(AttributeData attribute) =>
        string.Equals(
            attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            "Finare.Unreal.UnrealScriptClassAttribute",
            StringComparison.Ordinal);

    private static bool TryAnnotateTargets(
        IReadOnlyDictionary<string, RewriteContext> contexts,
        IReadOnlyList<AssetUpdatePlan> assetUpdates,
        IReadOnlyList<ClassBaseUpdatePlan> classBaseUpdates,
        IReadOnlyList<AssetRemovePlan> assetRemoves,
        IReadOnlyList<AddPlan> adds,
        IReadOnlyList<EventAddPlan> eventAdds,
        IReadOnlyList<UpdatePlan> updates,
        IReadOnlyList<RemovePlan> removes,
        IReadOnlyList<FunctionAddPlan> functionAdds,
        IReadOnlyList<FunctionUpdatePlan> functionUpdates,
        IReadOnlyList<FunctionRemovePlan> functionRemoves,
        IReadOnlyList<LifecycleAddPlan> lifecycleAdds,
        out UnifiedDiagnostic? failure)
    {
        failure = null;
        foreach (IGrouping<string, ScriptAssetDeclaration> sourceAssets in assetUpdates
                     .Select(static update => update.Asset)
                     .Concat(assetRemoves.Select(static remove => remove.Asset))
                     .Concat(adds.Select(static add => add.Asset))
                     .Concat(eventAdds.Select(static add => add.Asset))
                     .Concat(functionAdds.Select(static add => add.Asset))
                     .Concat(functionUpdates.Select(static update => update.Location.Asset))
                     .Concat(functionRemoves.Select(static remove => remove.Location.Asset))
                     .Concat(lifecycleAdds.Select(static add => add.Asset))
                     .DistinctBy(static asset => asset.Id)
                     .GroupBy(static asset => asset.Span.FilePath, StringComparer.Ordinal))
        {
            RewriteContext context = contexts[sourceAssets.Key];
            foreach (ScriptAssetDeclaration asset in sourceAssets)
            {
                TypeDeclarationSyntax[] matches = context.Root.DescendantNodes()
                    .OfType<TypeDeclarationSyntax>()
                    .Where(type => RoslynSourceSpan.FromNode(type, sourceAssets.Key) == asset.Span)
                    .ToArray();
                if (matches.Length != 1)
                {
                    failure = MissingSyntax(asset.Id, asset.Span, "script asset type");
                    return false;
                }

                context.Root = context.Root.ReplaceNode(
                    matches[0],
                    matches[0].WithAdditionalAnnotations(AssetAnnotation(asset.Id)));
            }
        }

        foreach (ClassBaseUpdatePlan update in classBaseUpdates)
        {
            RewriteContext context = update.Context;
            AttributeSyntax attribute = context.Root.DescendantNodes()
                .OfType<AttributeSyntax>()
                .SingleOrDefault(candidate => candidate.Span == update.Attribute.Span)
                ?? throw new InvalidOperationException(
                    $"The source syntax for the class binding of '{update.Asset.QualifiedName}' " +
                    "was not found.");
            context.Root = context.Root.ReplaceNode(
                attribute,
                attribute.WithAdditionalAnnotations(ClassBaseAnnotation(update.Asset.Id)));
        }

        foreach (StateMutationPlan mutation in updates.Cast<StateMutationPlan>().Concat(removes))
        {
            RewriteContext context = contexts[mutation.State.Span.FilePath];
            PropertyDeclarationSyntax[] matches = context.Root.DescendantNodes()
                .OfType<PropertyDeclarationSyntax>()
                .Where(property =>
                    RoslynSourceSpan.FromNode(property, mutation.State.Span.FilePath) == mutation.State.Span)
                .ToArray();
            if (matches.Length != 1)
            {
                failure = MissingSyntax(mutation.State.Id, mutation.State.Span, "script state property");
                return false;
            }

            context.Root = context.Root.ReplaceNode(
                matches[0],
                matches[0].WithAdditionalAnnotations(StateAnnotation(mutation.State.Id)));
        }

        foreach (FunctionMutationPlan mutation in functionUpdates
                     .Cast<FunctionMutationPlan>()
                     .Concat(functionRemoves))
        {
            FunctionLocation location = mutation.Location;
            RewriteContext context = contexts[location.Method.Span.FilePath];
            MethodDeclarationSyntax[] matches = context.Root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(method => RoslynSourceSpan.FromNode(
                    method,
                    location.Method.Span.FilePath) == location.Method.Span)
                .ToArray();
            if (matches.Length != 1)
            {
                failure = MissingSyntax(
                    location.Function.Id,
                    location.Method.Span,
                    "script function method");
                return false;
            }
            context.Root = context.Root.ReplaceNode(
                matches[0],
                matches[0].WithAdditionalAnnotations(
                    FunctionAnnotation(location.Function.Id)));
        }

        return true;
    }

    private static void RenameReferences(
        IReadOnlyDictionary<string, RewriteContext> contexts,
        IReadOnlyList<UpdatePlan> updates,
        IReadOnlyList<FunctionUpdatePlan> functionUpdates)
    {
        UpdatePlan[] renames = updates
            .Where(static update => !string.Equals(
                update.State.Name,
                update.Replacement.Name,
                StringComparison.Ordinal))
            .ToArray();
        var symbols = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        foreach (UpdatePlan update in renames)
        {
            RewriteContext declarationContext = contexts[update.State.Span.FilePath];
            PropertyDeclarationSyntax declaration = declarationContext.Tree.GetRoot()
                .DescendantNodes()
                .OfType<PropertyDeclarationSyntax>()
                .Single(property =>
                    RoslynSourceSpan.FromNode(property, update.State.Span.FilePath) == update.State.Span);
            IPropertySymbol symbol = declarationContext.SemanticModel.GetDeclaredSymbol(declaration) ??
                throw new InvalidOperationException("Roslyn did not bind a script-state property.");
            symbols.Add(symbol, update.Replacement.Name);
        }

        foreach (FunctionUpdatePlan update in functionUpdates)
        {
            RewriteContext declarationContext = contexts[update.Location.Method.Span.FilePath];
            MethodDeclarationSyntax declaration = declarationContext.Tree.GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Single(method => RoslynSourceSpan.FromNode(
                    method,
                    update.Location.Method.Span.FilePath) == update.Location.Method.Span);
            IMethodSymbol symbol = declarationContext.SemanticModel.GetDeclaredSymbol(declaration) ??
                throw new InvalidOperationException("Roslyn did not bind a script function method.");
            if (symbol.Name != update.Replacement.Name)
            {
                symbols.Add(symbol, update.Replacement.Name);
            }
            for (int index = 0; index < symbol.Parameters.Length; index++)
            {
                string replacementName = update.Replacement.Parameters[index].Name;
                if (symbol.Parameters[index].Name != replacementName)
                {
                    symbols.Add(symbol.Parameters[index], replacementName);
                }
            }
        }

        if (symbols.Count == 0)
        {
            return;
        }

        foreach (RewriteContext context in contexts.Values)
        {
            Dictionary<TextSpan, string> replacements = context.Tree.GetRoot()
                .DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Select(identifier => new
                {
                    Identifier = identifier,
                    Symbol = context.SemanticModel.GetSymbolInfo(identifier).Symbol,
                })
                .Where(binding => binding.Symbol is not null && symbols.ContainsKey(binding.Symbol))
                .ToDictionary(
                    static binding => binding.Identifier.Span,
                    binding => symbols[binding.Symbol!]);
            if (replacements.Count == 0)
            {
                continue;
            }

            IdentifierNameSyntax[] currentNodes = context.Root.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(identifier => replacements.ContainsKey(identifier.Span))
                .ToArray();
            context.Root = context.Root.ReplaceNodes(
                currentNodes,
                (original, _) => original.WithIdentifier(
                    SyntaxFactory.Identifier(
                        original.Identifier.LeadingTrivia,
                        replacements[original.Span],
                        original.Identifier.TrailingTrivia)));
        }
    }

    private static void ApplyPropertyChanges(
        IReadOnlyDictionary<string, RewriteContext> contexts,
        IReadOnlyList<UpdatePlan> updates,
        IReadOnlySet<SemanticId> constructorDefaultUpdates,
        IReadOnlyList<RemovePlan> removes)
    {
        foreach (UpdatePlan update in updates)
        {
            RewriteContext context = contexts[update.State.Span.FilePath];
            PropertyDeclarationSyntax property = GetAnnotatedNode<PropertyDeclarationSyntax>(
                context.Root,
                StateAnnotationKind,
                update.State.Id);
            PropertyDeclarationSyntax replacement = RewriteProperty(
                    property,
                    update.Replacement,
                    context.SemanticModel,
                    !constructorDefaultUpdates.Contains(update.State.Id))
                .WithAdditionalAnnotations(StateAnnotation(update.State.Id));
            context.Root = context.Root.ReplaceNode(property, replacement);
        }

        foreach (RemovePlan remove in removes)
        {
            RewriteContext context = contexts[remove.State.Span.FilePath];
            PropertyDeclarationSyntax property = GetAnnotatedNode<PropertyDeclarationSyntax>(
                context.Root,
                StateAnnotationKind,
                remove.State.Id);
            context.Root = context.Root.RemoveNode(property, SyntaxRemoveOptions.KeepExteriorTrivia) ??
                throw new InvalidOperationException("Removing a script-state property removed the source root.");
        }
    }

    private static IReadOnlyList<ConstructorDefaultLocation> PrepareConstructorDefaultChanges(
        IReadOnlyDictionary<string, RewriteContext> contexts,
        IReadOnlyList<UpdatePlan> updates)
    {
        // Resolve every final assignment against the original semantic trees before
        // replacing any RHS.  A longer replacement in one constructor otherwise
        // moves the absolute Span of subsequent assignments in the mutable root.
        var locations = new List<ConstructorDefaultLocation>();
        foreach (UpdatePlan update in updates)
        {
            RewriteContext propertyContext = contexts[update.State.Span.FilePath];
            PropertyDeclarationSyntax property = propertyContext.Tree.GetRoot()
                .DescendantNodes()
                .OfType<PropertyDeclarationSyntax>()
                .Single(candidate => RoslynSourceSpan.FromNode(
                    candidate,
                    update.State.Span.FilePath) == update.State.Span);
            IPropertySymbol? propertySymbol = propertyContext.SemanticModel.GetDeclaredSymbol(property);
            if (propertySymbol is null)
            {
                continue;
            }

            IMethodSymbol[] constructors = propertySymbol.ContainingType.InstanceConstructors
                .Where(static constructor =>
                    !constructor.IsImplicitlyDeclared &&
                    constructor.Parameters.Length == 0)
                .ToArray();
            if (constructors.Length == 0)
            {
                continue;
            }

            IMethodSymbol constructor = constructors.Single();
            ConstructorDeclarationSyntax? sourceConstructor = constructor.DeclaringSyntaxReferences
                .Select(static reference => reference.GetSyntax())
                .OfType<ConstructorDeclarationSyntax>()
                .SingleOrDefault();
            if (sourceConstructor is null ||
                !contexts.TryGetValue(sourceConstructor.SyntaxTree.FilePath, out RewriteContext? constructorContext))
            {
                continue;
            }

            AssignmentExpressionSyntax[] assignments = sourceConstructor
                .DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(assignment =>
                    TargetsCurrentInstanceProperty(
                        assignment.Left,
                        propertySymbol,
                        constructorContext.SemanticModel) &&
                    assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                    ((assignment.Parent is ExpressionStatementSyntax statement &&
                        statement.Parent == sourceConstructor.Body) ||
                        sourceConstructor.ExpressionBody?.Expression == assignment))
                .ToArray();
            if (assignments.Length == 0)
            {
                continue;
            }

            AssignmentExpressionSyntax currentAssignment = constructorContext.Root
                .DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Single(candidate => candidate.Span == assignments[^1].Span);
            locations.Add(new ConstructorDefaultLocation(
                update,
                constructorContext,
                currentAssignment));
        }

        // Attach stable annotations in one pass per syntax tree.  Each assignment
        // is still the node from the original tree, so no earlier replacement can
        // invalidate the locator used for a later update.
        foreach (IGrouping<RewriteContext, ConstructorDefaultLocation> group in locations.GroupBy(
                     static location => location.Context))
        {
            Dictionary<AssignmentExpressionSyntax, ConstructorDefaultLocation> byAssignment =
                group.ToDictionary(static location => location.Assignment);
            group.First().Context.Root = group.First().Context.Root.ReplaceNodes(
                byAssignment.Keys,
                (original, rewritten) => rewritten.WithAdditionalAnnotations(
                    ConstructorAssignmentAnnotation(byAssignment[(AssignmentExpressionSyntax)original]
                        .Update.State.Id)));
        }

        return locations;
    }

    private static void ApplyConstructorDefaultChanges(
        IReadOnlyList<ConstructorDefaultLocation> locations,
        ISet<SemanticId> constructorDefaultUpdates)
    {
        foreach (ConstructorDefaultLocation location in locations)
        {
            AssignmentExpressionSyntax currentAssignment = GetAnnotatedNode<AssignmentExpressionSyntax>(
                location.Context.Root,
                ConstructorAssignmentAnnotationKind,
                location.Update.State.Id);
            ExpressionSyntax replacement = SyntaxFactory.ParseExpression(
                    RenderDefault(
                        location.Update.Replacement,
                        location.Context.SemanticModel,
                        location.Assignment.Right.SpanStart))
                .WithTriviaFrom(currentAssignment.Right);
            location.Context.Root = location.Context.Root.ReplaceNode(
                currentAssignment,
                currentAssignment.WithRight(replacement));
            constructorDefaultUpdates.Add(location.Update.State.Id);
        }
    }

    private static void ApplyAssetChanges(
        IReadOnlyDictionary<string, RewriteContext> contexts,
        IReadOnlyList<AssetUpdatePlan> updates,
        IReadOnlyList<AssetRemovePlan> removes)
    {
        foreach (AssetUpdatePlan update in updates)
        {
            RewriteContext context = contexts[update.Asset.Span.FilePath];
            TypeDeclarationSyntax type = GetAnnotatedNode<TypeDeclarationSyntax>(
                context.Root,
                AssetAnnotationKind,
                update.Asset.Id);
            TypeDeclarationSyntax replacement = RewriteAssetPath(type, update.ReplacementAssetPath)
                .WithAdditionalAnnotations(AssetAnnotation(update.Asset.Id));
            context.Root = context.Root.ReplaceNode(type, replacement);
        }

        foreach (AssetRemovePlan remove in removes)
        {
            RewriteContext context = contexts[remove.Asset.Span.FilePath];
            TypeDeclarationSyntax type = GetAnnotatedNode<TypeDeclarationSyntax>(
                context.Root,
                AssetAnnotationKind,
                remove.Asset.Id);
            context.Root = context.Root.RemoveNode(type, SyntaxRemoveOptions.KeepExteriorTrivia) ??
                throw new InvalidOperationException("Removing a Script Asset removed the source root.");
        }
    }

    private static void ApplyClassBaseChanges(IReadOnlyList<ClassBaseUpdatePlan> updates)
    {
        foreach (ClassBaseUpdatePlan update in updates)
        {
            AttributeSyntax attribute = GetAnnotatedNode<AttributeSyntax>(
                update.Context.Root,
                ClassBaseAnnotationKind,
                update.Asset.Id);
            AttributeArgumentListSyntax arguments = attribute.ArgumentList ??
                SyntaxFactory.AttributeArgumentList();
            int baseIndex = -1;
            for (int index = 0; index < arguments.Arguments.Count; index++)
            {
                AttributeArgumentSyntax argument = arguments.Arguments[index];
                if (argument.NameEquals?.Name.Identifier.ValueText == "BaseClassPath")
                {
                    baseIndex = index;
                    break;
                }
            }

            if (update.ReplacementBaseClassPath is null)
            {
                if (baseIndex >= 0)
                {
                    AttributeArgumentSyntax removed = arguments.Arguments[baseIndex];
                    bool removesTrailingSeparator = baseIndex == arguments.Arguments.Count - 1;
                    bool hasSeparator = arguments.Arguments.Count > 1;
                    SyntaxTriviaList preservedTrivia = CollectRemovedArgumentTrivia(
                        removed,
                        hasSeparator
                            ? removesTrailingSeparator
                                ? arguments.Arguments.GetSeparator(baseIndex - 1)
                                : arguments.Arguments.GetSeparator(baseIndex)
                            : default,
                        removesTrailingSeparator,
                        hasSeparator);
                    SeparatedSyntaxList<AttributeArgumentSyntax> remaining =
                        arguments.Arguments.RemoveAt(baseIndex);
                    if (remaining.Count > 0)
                    {
                        int neighborIndex = baseIndex < remaining.Count
                            ? baseIndex
                            : remaining.Count - 1;
                        AttributeArgumentSyntax neighbor = remaining[neighborIndex];
                        neighbor = neighborIndex == baseIndex
                            ? neighbor.WithLeadingTrivia(
                                preservedTrivia.AddRange(neighbor.GetLeadingTrivia()))
                            : neighbor.WithTrailingTrivia(
                                neighbor.GetTrailingTrivia().AddRange(preservedTrivia));
                        remaining = remaining.Replace(remaining[neighborIndex], neighbor);
                    }
                    else
                    {
                        arguments = arguments.WithOpenParenToken(
                            arguments.OpenParenToken.WithTrailingTrivia(
                                arguments.OpenParenToken.TrailingTrivia.AddRange(preservedTrivia)));
                    }
                    arguments = arguments.WithArguments(remaining);
                }
            }
            else
            {
                if (baseIndex >= 0)
                {
                    AttributeArgumentSyntax original = arguments.Arguments[baseIndex];
                    ExpressionSyntax replacementExpression = SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(update.ReplacementBaseClassPath))
                        .WithTriviaFrom(original.Expression);
                    SyntaxTrivia[] outerTrivia = original.Expression.GetLeadingTrivia()
                        .Concat(original.Expression.GetTrailingTrivia())
                        .ToArray();
                    SyntaxTrivia[] innerTrivia = original.Expression.DescendantTokens()
                        .SelectMany(static token => token.LeadingTrivia.Concat(token.TrailingTrivia))
                        .Where(trivia => !outerTrivia.Any(outer => outer.Span == trivia.Span))
                        .ToArray();
                    if (innerTrivia.Length > 0)
                    {
                        replacementExpression = replacementExpression.WithTrailingTrivia(
                            SyntaxFactory.TriviaList(innerTrivia).AddRange(
                                original.Expression.GetTrailingTrivia()));
                    }
                    AttributeArgumentSyntax replacement = original.WithExpression(replacementExpression);
                    arguments = arguments.WithArguments(
                        arguments.Arguments.Replace(original, replacement));
                }
                else
                {
                    AttributeArgumentSyntax replacement = SyntaxFactory.AttributeArgument(
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal(update.ReplacementBaseClassPath)))
                        .WithNameEquals(SyntaxFactory.NameEquals(
                            SyntaxFactory.IdentifierName("BaseClassPath")));
                    arguments = arguments.WithArguments(arguments.Arguments.Add(replacement));
                }
            }

            AttributeSyntax rewritten = attribute.WithArgumentList(arguments)
                .WithAdditionalAnnotations(ClassBaseAnnotation(update.Asset.Id));
            update.Context.Root = update.Context.Root.ReplaceNode(attribute, rewritten);
        }
    }

    private static SyntaxTriviaList CollectRemovedArgumentTrivia(
        AttributeArgumentSyntax argument,
        SyntaxToken separator,
        bool separatorPrecedesArgument,
        bool hasSeparator)
    {
        var trivia = new List<SyntaxTrivia>();
        void AddTokenTrivia(SyntaxToken token)
        {
            trivia.AddRange(token.LeadingTrivia);
            trivia.AddRange(token.TrailingTrivia);
        }

        if (hasSeparator && separatorPrecedesArgument)
        {
            AddTokenTrivia(separator);
        }
        foreach (SyntaxToken token in argument.DescendantTokens())
        {
            AddTokenTrivia(token);
        }
        if (hasSeparator && !separatorPrecedesArgument)
        {
            AddTokenTrivia(separator);
        }
        return SyntaxFactory.TriviaList(trivia);
    }

    private static void ApplyFunctionChanges(
        IReadOnlyDictionary<string, RewriteContext> contexts,
        IReadOnlyList<FunctionUpdatePlan> updates,
        IReadOnlyList<FunctionRemovePlan> removes)
    {
        foreach (FunctionUpdatePlan update in updates)
        {
            RewriteContext context = contexts[update.Location.Method.Span.FilePath];
            MethodDeclarationSyntax method = GetAnnotatedNode<MethodDeclarationSyntax>(
                context.Root,
                FunctionAnnotationKind,
                update.Location.Function.Id);
            MethodDeclarationSyntax replacement = RewriteFunction(method, update.Replacement)
                .WithAdditionalAnnotations(FunctionAnnotation(update.Location.Function.Id));
            context.Root = context.Root.ReplaceNode(method, replacement);
        }

        foreach (FunctionRemovePlan remove in removes)
        {
            RewriteContext context = contexts[remove.Location.Method.Span.FilePath];
            MethodDeclarationSyntax method = GetAnnotatedNode<MethodDeclarationSyntax>(
                context.Root,
                FunctionAnnotationKind,
                remove.Location.Function.Id);
            context.Root = context.Root.RemoveNode(method, SyntaxRemoveOptions.KeepExteriorTrivia) ??
                throw new InvalidOperationException("Removing a script function removed the source root.");
        }
    }

    private static void ApplyAdditions(
        IReadOnlyDictionary<string, RewriteContext> contexts,
        IReadOnlyList<AddPlan> adds)
    {
        foreach (IGrouping<SemanticId, AddPlan> assetAdds in adds.GroupBy(static add => add.Asset.Id))
        {
            AddPlan first = assetAdds.First();
            RewriteContext context = contexts[first.Asset.Span.FilePath];
            TypeDeclarationSyntax type = GetAnnotatedNode<TypeDeclarationSyntax>(
                context.Root,
                AssetAnnotationKind,
                first.Asset.Id);
            int insertionIndex = type.Members
                .Select((member, index) => new { member, index })
                .FirstOrDefault(static item => item.member is MethodDeclarationSyntax)
                ?.index ?? type.Members.Count;
            SyntaxList<MemberDeclarationSyntax> members = type.Members;
            foreach (AddPlan add in assetAdds)
            {
                members = members.Insert(
                    insertionIndex++,
                    CreateProperty(add.State, type, context.SemanticModel, first.Asset.ClassBinding is not null));
            }

            TypeDeclarationSyntax replacement = type.WithMembers(members)
                .WithAdditionalAnnotations(AssetAnnotation(first.Asset.Id));
            if (first.Asset.ClassBinding is not null && !replacement.Modifiers.Any(SyntaxKind.PartialKeyword))
                replacement = replacement.AddModifiers(SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space));
            context.Root = context.Root.ReplaceNode(type, replacement);
        }
    }

    private static void ApplyFunctionAdditions(
        IReadOnlyDictionary<string, RewriteContext> contexts,
        IReadOnlyList<FunctionAddPlan> adds)
    {
        foreach (IGrouping<SemanticId, FunctionAddPlan> assetAdds in
                 adds.GroupBy(static add => add.Asset.Id))
        {
            FunctionAddPlan first = assetAdds.First();
            RewriteContext context = contexts[first.Asset.Span.FilePath];
            TypeDeclarationSyntax type = GetAnnotatedNode<TypeDeclarationSyntax>(
                context.Root,
                AssetAnnotationKind,
                first.Asset.Id);
            SyntaxList<MemberDeclarationSyntax> members = type.Members;
            foreach (FunctionAddPlan add in assetAdds)
            {
                members = members.Add(CreateFunction(add.Function, type));
            }
            TypeDeclarationSyntax replacement = type.WithMembers(members)
                .WithAdditionalAnnotations(AssetAnnotation(first.Asset.Id));
            if (first.Asset.ClassBinding is not null && !replacement.Modifiers.Any(SyntaxKind.PartialKeyword))
                replacement = replacement.AddModifiers(SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space));
            context.Root = context.Root.ReplaceNode(type, replacement);
        }
    }

    private static void ApplyLifecycleAdditions(
        IReadOnlyDictionary<string, RewriteContext> contexts,
        IReadOnlyList<LifecycleAddPlan> adds)
    {
        foreach (IGrouping<SemanticId, LifecycleAddPlan> assetAdds in
                 adds.GroupBy(static add => add.Asset.Id))
        {
            LifecycleAddPlan first = assetAdds.First();
            RewriteContext context = contexts[first.Asset.Span.FilePath];
            TypeDeclarationSyntax type = GetAnnotatedNode<TypeDeclarationSyntax>(
                context.Root,
                AssetAnnotationKind,
                first.Asset.Id);
            SyntaxList<MemberDeclarationSyntax> members = type.Members;
            foreach (LifecycleAddPlan add in assetAdds)
            {
                members = members.Add(CreateLifecycleHook(add.Hook, type));
            }
            TypeDeclarationSyntax replacement = type.WithMembers(members)
                .WithAdditionalAnnotations(AssetAnnotation(first.Asset.Id));
            if (first.Asset.ClassBinding is not null && !replacement.Modifiers.Any(SyntaxKind.PartialKeyword))
                replacement = replacement.AddModifiers(SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space));
            context.Root = context.Root.ReplaceNode(type, replacement);
        }
    }

    private static bool TryApplyAssetAdditions(
        IReadOnlyDictionary<string, RewriteContext> contexts,
        IReadOnlyList<AssetAddPlan> adds,
        out UnifiedDiagnostic? failure)
    {
        failure = null;
        foreach (AssetAddPlan add in adds)
        {
            RewriteContext context = contexts[add.Source.LogicalPath];
            CompilationUnitSyntax root = (CompilationUnitSyntax)context.Root;
            FileScopedNamespaceDeclarationSyntax? fileNamespace = root.Members
                .OfType<FileScopedNamespaceDeclarationSyntax>()
                .SingleOrDefault();
            if (fileNamespace is not null &&
                !string.Equals(
                    fileNamespace.Name.ToString(),
                    add.Asset.Namespace,
                    StringComparison.Ordinal))
            {
                failure = InvalidReplacement(
                    $"Source '{add.Source.LogicalPath}' uses file-scoped namespace " +
                    $"'{fileNamespace.Name}'; the new Script Asset must use the same namespace.",
                    SourceSpan.Unknown(add.Source.LogicalPath));
                return false;
            }

            string newline = context.Root.SyntaxTree.GetText().ToString()
                .Contains("\r\n", StringComparison.Ordinal)
                    ? "\r\n"
                    : "\n";
            string declaration = RenderAsset(add.Asset, fileNamespace is not null, newline);
            string source = context.Root.ToFullString().TrimEnd() + newline + newline + declaration + newline;
            context.Root = CSharpSyntaxTree.ParseText(
                source,
                context.Tree.Options as CSharpParseOptions,
                add.Source.LogicalPath).GetRoot();
        }
        return true;
    }

    private static void ApplyEventAdditions(
        IReadOnlyDictionary<string, RewriteContext> contexts,
        IReadOnlyList<EventAddPlan> eventAdds)
    {
        foreach (IGrouping<SemanticId, EventAddPlan> assetAdds in
                 eventAdds.GroupBy(static add => add.Asset.Id))
        {
            EventAddPlan first = assetAdds.First();
            RewriteContext context = contexts[first.Asset.Span.FilePath];
            TypeDeclarationSyntax type = GetAnnotatedNode<TypeDeclarationSyntax>(
                context.Root,
                AssetAnnotationKind,
                first.Asset.Id);
            SyntaxList<MemberDeclarationSyntax> members = type.Members;
            foreach (EventAddPlan add in assetAdds)
            {
                members = members.Add(CreateEventHandler(add.Handler, type));
            }
            TypeDeclarationSyntax replacement = type.WithMembers(members)
                .WithAdditionalAnnotations(AssetAnnotation(first.Asset.Id));
            if (first.Asset.ClassBinding is not null && !replacement.Modifiers.Any(SyntaxKind.PartialKeyword))
                replacement = replacement.AddModifiers(SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space));
            context.Root = context.Root.ReplaceNode(type, replacement);
        }
    }

    private static TypeDeclarationSyntax RewriteAssetPath(
        TypeDeclarationSyntax type,
        string assetPath)
    {
        AttributeSyntax? attribute = type.AttributeLists
            .SelectMany(static list => list.Attributes)
            .SingleOrDefault(static candidate => IsScriptAssetAttribute(candidate.Name.ToString()));
        AttributeArgumentSyntax argument = SyntaxFactory.AttributeArgument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(assetPath)));
        if (attribute is null)
        {
            AttributeListSyntax list = SyntaxFactory.AttributeList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(
                        SyntaxFactory.ParseName("global::Finare.ScriptAsset"),
                        SyntaxFactory.AttributeArgumentList(
                            SyntaxFactory.SingletonSeparatedList(argument)))));
            return type.WithAttributeLists(type.AttributeLists.Insert(0, list));
        }

        AttributeArgumentListSyntax arguments = attribute.ArgumentList ??
            SyntaxFactory.AttributeArgumentList();
        SeparatedSyntaxList<AttributeArgumentSyntax> values = arguments.Arguments.Count == 0
            ? SyntaxFactory.SingletonSeparatedList(argument)
            : arguments.Arguments.Replace(arguments.Arguments[0], argument);
        AttributeSyntax replacement = attribute.WithArgumentList(arguments.WithArguments(values));
        return type.ReplaceNode(attribute, replacement);
    }

    private static MethodDeclarationSyntax RewriteFunction(
        MethodDeclarationSyntax method,
        ScriptFunctionPatchSpec spec)
    {
        SyntaxTokenList modifiers = new(method.Modifiers
            .Where(static modifier => !modifier.IsKind(SyntaxKind.StaticKeyword)));
        if (spec.IsStatic)
        {
            modifiers = modifiers.Add(SyntaxFactory.Token(
                SyntaxTriviaList.Empty,
                SyntaxKind.StaticKeyword,
                SyntaxFactory.TriviaList(SyntaxFactory.Space)));
        }
        SeparatedSyntaxList<ParameterSyntax> parameters = SyntaxFactory.SeparatedList(
            spec.Parameters.Select(parameter => SyntaxFactory.Parameter(
                    SyntaxFactory.Identifier(parameter.Name))
                .WithType(SyntaxFactory.ParseTypeName(parameter.TypeName)
                    .WithTrailingTrivia(SyntaxFactory.Space))));
        return method
            .WithReturnType(SyntaxFactory.ParseTypeName(spec.ReturnTypeName)
                .WithTrailingTrivia(SyntaxFactory.Space))
            .WithIdentifier(SyntaxFactory.Identifier(
                method.Identifier.LeadingTrivia,
                spec.Name,
                method.Identifier.TrailingTrivia))
            .WithModifiers(modifiers)
            .WithParameterList(method.ParameterList.WithParameters(parameters).NormalizeWhitespace());
    }

    private static MethodDeclarationSyntax CreateFunction(
        ScriptFunctionPatchSpec spec,
        TypeDeclarationSyntax containingType)
    {
        string parameters = string.Join(
            ", ",
            spec.Parameters.Select(static parameter => $"{parameter.TypeName} {parameter.Name}"));
        string staticModifier = spec.IsStatic ? " static" : string.Empty;
        string body = CreateDefaultFunctionBody(spec.ReturnTypeName);
        string source = "[global::Finare.Visual]\n" +
            $"public{staticModifier} {spec.ReturnTypeName} {spec.Name}({parameters})\n" +
            $"{{\n{body}\n}}";
        MethodDeclarationSyntax method = SyntaxFactory.ParseMemberDeclaration(source)
            as MethodDeclarationSyntax ??
            throw new InvalidOperationException("A validated script function did not parse as a method.");
        return NormalizeMethod(method, containingType);
    }

    private static MethodDeclarationSyntax CreateLifecycleHook(
        string hook,
        TypeDeclarationSyntax containingType)
    {
        string signature = hook switch
        {
            "beginPlay" => "OnBeginPlay()",
            "tick" => "OnTick(float deltaSeconds)",
            "endPlay" => "OnEndPlay()",
            _ => throw new InvalidOperationException($"Unsupported lifecycle hook '{hook}'."),
        };
        MethodDeclarationSyntax method = SyntaxFactory.ParseMemberDeclaration(
            $"protected override void {signature}\n{{\n}}") as MethodDeclarationSyntax ??
            throw new InvalidOperationException("A validated lifecycle hook did not parse as a method.");
        return NormalizeMethod(method, containingType);
    }

    private static MethodDeclarationSyntax NormalizeMethod(
        MethodDeclarationSyntax method,
        TypeDeclarationSyntax containingType)
    {
        string newline = containingType.SyntaxTree?.GetText().ToString()
            .Contains("\r\n", StringComparison.Ordinal) == true
                ? "\r\n"
                : "\n";
        int indentationWidth = containingType.GetLocation()
            .GetLineSpan().StartLinePosition.Character + 4;
        string indentation = new(' ', indentationWidth);
        string normalized = method
            .WithoutLeadingTrivia()
            .WithoutTrailingTrivia()
            .NormalizeWhitespace(indentation: "    ", eol: newline)
            .ToFullString()
            .Replace(newline, newline + indentation, StringComparison.Ordinal);
        method = SyntaxFactory.ParseMemberDeclaration(normalized) as MethodDeclarationSyntax ??
            throw new InvalidOperationException("A normalized script function did not parse as a method.");
        return method
            .WithLeadingTrivia(
                SyntaxFactory.EndOfLine(newline),
                SyntaxFactory.Whitespace(indentation))
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(newline));
    }

    private static string CreateDefaultFunctionBody(string returnTypeName)
    {
        string type = returnTypeName.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();
        return type switch
        {
            "void" => string.Empty,
            "bool" or "System.Boolean" => "    return false;",
            "float" or "System.Single" => "    return 0.0f;",
            "double" or "System.Double" => "    return 0.0d;",
            "decimal" or "System.Decimal" => "    return 0.0m;",
            "string" or "System.String" => "    return \"\";",
            _ => "    return 0;",
        };
    }

    private static string RenderAsset(
        ScriptAssetPatchSpec spec,
        bool insideFileScopedNamespace,
        string newline)
    {
        string baseType = spec.BaseTypeName ??
            (spec.IncludeActorLifecycle
                ? "global::Finare.Unreal.UnrealActorScript"
                : string.Empty);
        string inheritance = baseType.Length == 0 ? string.Empty : $" : {baseType}";
        var lines = new List<string>
        {
            $"[global::Finare.ScriptAsset({SymbolDisplay.FormatLiteral(spec.AssetPath, true)})]",
            $"public sealed class {spec.Name}{inheritance}",
            "{",
        };
        if (spec.IncludeActorLifecycle)
        {
            lines.Add("    protected override void OnBeginPlay()");
            lines.Add("    {");
            lines.Add("    }");
            lines.Add(string.Empty);
            lines.Add("    protected override void OnTick(float deltaSeconds)");
            lines.Add("    {");
            lines.Add("    }");
            lines.Add(string.Empty);
            lines.Add("    protected override void OnEndPlay()");
            lines.Add("    {");
            lines.Add("    }");
        }
        lines.Add("}");
        string type = string.Join(newline, lines);
        if (insideFileScopedNamespace || spec.Namespace.Length == 0)
        {
            return type;
        }
        string indented = string.Join(
            newline,
            type.Split(["\r\n", "\n"], StringSplitOptions.None)
                .Select(static line => line.Length == 0 ? line : "    " + line));
        return $"namespace {spec.Namespace}{newline}{{{newline}{indented}{newline}}}";
    }

    private static bool IsScriptAssetAttribute(string name) =>
        name is "ScriptAsset" or "ScriptAssetAttribute" or "Finare.ScriptAsset" or
        "Finare.ScriptAssetAttribute" or "global::Finare.ScriptAsset" or
        "global::Finare.ScriptAssetAttribute";

    private static MethodDeclarationSyntax CreateEventHandler(
        UnrealEventHandlerPatchSpec spec,
        TypeDeclarationSyntax containingType)
    {
        string source = RenderEventHandler(spec);
        MethodDeclarationSyntax method = SyntaxFactory.ParseMemberDeclaration(source)
            as MethodDeclarationSyntax ??
            throw new InvalidOperationException(
                "A validated Unreal Event Entry did not parse as a method.");
        string newline = containingType.SyntaxTree?.GetText().ToString()
            .Contains("\r\n", StringComparison.Ordinal) == true
                ? "\r\n"
                : "\n";
        int indentationWidth = containingType.GetLocation()
            .GetLineSpan().StartLinePosition.Character + 4;
        string indentation = new(' ', indentationWidth);
        string normalized = method
            .WithoutLeadingTrivia()
            .WithoutTrailingTrivia()
            .NormalizeWhitespace(indentation: "    ", eol: newline)
            .ToFullString()
            .Replace(newline, newline + indentation, StringComparison.Ordinal);
        method = SyntaxFactory.ParseMemberDeclaration(normalized) as MethodDeclarationSyntax ??
            throw new InvalidOperationException(
                "A normalized Unreal Event Entry did not parse as a method.");
        return method
            .WithLeadingTrivia(
                SyntaxFactory.EndOfLine(newline),
                SyntaxFactory.Whitespace(indentation))
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(newline));
    }

    private static string RenderEventHandler(UnrealEventHandlerPatchSpec spec)
    {
        string eventPath = SymbolDisplay.FormatLiteral(spec.EventPath, quote: true);
        var bindingArguments = new List<string>
        {
            $"typeof({spec.SourceTypeName})",
            eventPath,
        };
        if (spec.Source == "host")
        {
            bindingArguments.Add(
                "Source = global::Finare.Unreal.UnrealEventSource.Host");
        }
        if (spec.SourceMember is not null)
        {
            bindingArguments.Add(
                "SourceMember = " + SymbolDisplay.FormatLiteral(spec.SourceMember, quote: true));
        }
        string parameters = string.Join(
            ", ",
            spec.Parameters.Select(static parameter => $"{parameter.TypeName} {parameter.Name}"));
        return "[global::Finare.Visual]\n" +
            $"[global::Finare.Unreal.UnrealEvent({string.Join(", ", bindingArguments)})]\n" +
            $"private void {spec.Name}({parameters})\n{{\n}}";
    }

    private static PropertyDeclarationSyntax RewriteProperty(
        PropertyDeclarationSyntax property,
        ScriptStatePatchSpec spec,
        SemanticModel semanticModel,
        bool rewriteInitializer)
    {
        SyntaxTriviaList leading = property.GetLeadingTrivia();
        SyntaxTriviaList trailing = property.GetTrailingTrivia();
        SyntaxList<AttributeListSyntax> attributes = RemoveExposureAttributes(property.AttributeLists);
        AttributeListSyntax? exposure = CreateExposureAttribute(spec.Exposure);
        if (exposure is not null)
        {
            attributes = attributes.Add(exposure);
        }

        PropertyDeclarationSyntax updated = property
            .WithAttributeLists(attributes)
            .WithType(SyntaxFactory.ParseTypeName(spec.TypeName))
            .WithIdentifier(SyntaxFactory.Identifier(
                property.Identifier.LeadingTrivia,
                spec.Name,
                property.Identifier.TrailingTrivia))
            .WithInitializer(rewriteInitializer
                ? SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.ParseExpression(RenderDefault(
                        spec,
                        semanticModel,
                        property.SpanStart)))
                : property.Initializer)
            .WithSemicolonToken(property.SemicolonToken.IsKind(SyntaxKind.None)
                ? SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                : property.SemicolonToken);
        string newline = property.SyntaxTree?.GetText().ToString().Contains("\r\n", StringComparison.Ordinal) == true
            ? "\r\n"
            : "\n";
        int indentationWidth = property.GetLocation().GetLineSpan().StartLinePosition.Character;
        return NormalizeProperty(updated, indentationWidth, newline)
            .WithLeadingTrivia(leading)
            .WithTrailingTrivia(trailing);
    }

    private static bool TargetsCurrentInstanceProperty(
        ExpressionSyntax left,
        IPropertySymbol property,
        SemanticModel semanticModel)
    {
        ISymbol? target = semanticModel.GetSymbolInfo(left).Symbol;
        if (!SymbolEqualityComparer.Default.Equals(target, property))
        {
            return false;
        }

        return left is IdentifierNameSyntax ||
            left is MemberAccessExpressionSyntax
            {
                Expression: ThisExpressionSyntax or BaseExpressionSyntax,
            };
    }

    private static PropertyDeclarationSyntax CreateProperty(
        ScriptStatePatchSpec spec,
        TypeDeclarationSyntax containingType,
        SemanticModel semanticModel,
        bool nativeState)
    {
        string source = RenderProperty(
            spec,
            semanticModel,
            containingType.SpanStart);
        PropertyDeclarationSyntax property = SyntaxFactory.ParseMemberDeclaration(source)
            as PropertyDeclarationSyntax ??
            throw new InvalidOperationException("A validated state specification did not parse as a property.");
        // Adding a state to a class with generated native properties must keep
        // its existing ownership model instead of introducing a mirrored value.
        // containingType already includes earlier edits in this transaction;
        // it is no longer a node from semanticModel's original syntax tree.
        if (nativeState || containingType.Members.OfType<PropertyDeclarationSyntax>().Any(existing =>
                existing.Modifiers.Any(SyntaxKind.PartialKeyword)))
        {
            property = property.AddModifiers(SyntaxFactory.Token(SyntaxKind.PartialKeyword));
        }
        string newline = containingType.SyntaxTree?.GetText().ToString().Contains("\r\n", StringComparison.Ordinal) == true
            ? "\r\n"
            : "\n";
        int indentationWidth = containingType.GetLocation().GetLineSpan().StartLinePosition.Character + 4;
        return NormalizeProperty(property, indentationWidth, newline)
            .WithLeadingTrivia(
                SyntaxFactory.EndOfLine(newline),
                SyntaxFactory.Whitespace(new string(' ', indentationWidth)))
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(newline));
    }

    private static PropertyDeclarationSyntax NormalizeProperty(
        PropertyDeclarationSyntax property,
        int indentationWidth,
        string newline)
    {
        string normalized = property
            .WithoutLeadingTrivia()
            .WithoutTrailingTrivia()
            .NormalizeWhitespace(indentation: "    ", eol: newline)
            .ToFullString();
        if (indentationWidth > 0)
        {
            normalized = normalized.Replace(
                newline,
                newline + new string(' ', indentationWidth),
                StringComparison.Ordinal);
        }

        return SyntaxFactory.ParseMemberDeclaration(normalized) as PropertyDeclarationSyntax ??
            throw new InvalidOperationException("A normalized script-state property did not parse.");
    }

    private static string RenderProperty(
        ScriptStatePatchSpec spec,
        SemanticModel semanticModel,
        int position)
    {
        string exposure = RenderExposureAttribute(spec.Exposure);
        return $"{exposure}public {spec.TypeName} {spec.Name} {{ get; set; }} = " +
            $"{RenderDefault(spec, semanticModel, position)};";
    }

    private static string RenderDefault(
        ScriptStatePatchSpec spec,
        SemanticModel? semanticModel = null,
        int position = 0)
    {
        if (spec.TypeKind == ScriptStateTypeKind.String)
        {
            return spec.DefaultValue.ValueKind == JsonValueKind.Null
                ? "null"
                : Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
                    spec.DefaultValue.GetString()!,
                    quote: true);
        }

        if (spec.TypeKind == ScriptStateTypeKind.Enum)
        {
            string enumValue = spec.DefaultValue.GetString() ?? string.Empty;
            return long.TryParse(
                    enumValue,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long numericValue)
                ? $"new {spec.TypeName}({numericValue}L)"
                : $"{spec.TypeName}.{enumValue}";
        }

        if (spec.TypeKind is ScriptStateTypeKind.Name or ScriptStateTypeKind.Text)
        {
            string text = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
                spec.DefaultValue.GetString()!,
                quote: true);
            return $"new {spec.TypeName}({text})";
        }

        if (spec.TypeKind is ScriptStateTypeKind.Object or ScriptStateTypeKind.Class)
        {
            return "default";
        }

        if (spec.TypeKind is ScriptStateTypeKind.SoftObject or ScriptStateTypeKind.SoftClass)
        {
            string path = spec.DefaultValue.GetString() ?? string.Empty;
            return path.Length == 0
                ? "default"
                : $"new {spec.TypeName}(" +
                    Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(path, quote: true) + ")";
        }

        if (spec.TypeKind is ScriptStateTypeKind.Array or ScriptStateTypeKind.Set or ScriptStateTypeKind.Map)
        {
            TypeSyntax collectionSyntax = SyntaxFactory.ParseTypeName(spec.TypeName);
            ITypeSymbol? collectionSymbol = semanticModel?.GetSpeculativeTypeInfo(
                position,
                collectionSyntax,
                SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
            string[] typeArguments;
            ITypeSymbol[] elementSymbols;
            if (collectionSymbol is INamedTypeSymbol namedCollection)
            {
                string[] explicitArguments = GetCollectionTypeArguments(collectionSyntax);
                typeArguments = explicitArguments.Length == namedCollection.TypeArguments.Length
                    ? explicitArguments
                    : namedCollection.TypeArguments
                        .Select(static argument => argument.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            .Replace("global::", string.Empty, StringComparison.Ordinal))
                        .ToArray();
                elementSymbols = namedCollection.TypeArguments.ToArray();
            }
            else
            {
                typeArguments = GetCollectionTypeArguments(collectionSyntax);
                elementSymbols = [];
            }
            if (spec.TypeKind == ScriptStateTypeKind.Map)
            {
                if (typeArguments.Length != 2)
                    throw new InvalidOperationException("A map state requires key and value types.");
                string entries = string.Join(", ", spec.DefaultValue.EnumerateArray().Select(entry =>
                    $"{{ {RenderCollectionLeafDefault(entry.GetProperty("key"), typeArguments[0],
                        elementSymbols.Length > 0 ? elementSymbols[0] : null)}, " +
                    $"{RenderCollectionLeafDefault(entry.GetProperty("value"), typeArguments[1],
                        elementSymbols.Length > 1 ? elementSymbols[1] : null)} }}"));
                return $"new global::System.Collections.Generic.Dictionary<{typeArguments[0]}, {typeArguments[1]}> {{ {entries} }}";
            }
            string elementType = typeArguments.Length == 1 ? typeArguments[0] : throw new InvalidOperationException("A collection state requires one element type.");
            string values = string.Join(", ", spec.DefaultValue.EnumerateArray()
                .Select(item => RenderCollectionLeafDefault(
                    item,
                    elementType,
                    elementSymbols.Length == 1 ? elementSymbols[0] : null)));
            string implementation = spec.TypeKind == ScriptStateTypeKind.Set
                ? "global::System.Collections.Generic.HashSet"
                : "global::System.Collections.Generic.List";
            return $"new {implementation}<{elementType}> {{ {values} }}";
        }

        if (spec.TypeKind == ScriptStateTypeKind.Struct)
        {
            if (spec.DefaultValue.TryGetProperty("pitch", out JsonElement pitch) &&
                spec.DefaultValue.TryGetProperty("yaw", out JsonElement yaw) &&
                spec.DefaultValue.TryGetProperty("roll", out JsonElement roll))
            {
                return $"new {spec.TypeName}({pitch.GetRawText()}d, {yaw.GetRawText()}d, {roll.GetRawText()}d)";
            }
            if (spec.DefaultValue.TryGetProperty("rotation", out JsonElement rotation) &&
                spec.DefaultValue.TryGetProperty("translation", out JsonElement translation) &&
                spec.DefaultValue.TryGetProperty("scale3D", out JsonElement scale))
            {
                string quaternion =
                    $"new global::Finare.Unreal.UnrealQuaternion(" +
                    $"{rotation.GetProperty("x").GetRawText()}d, " +
                    $"{rotation.GetProperty("y").GetRawText()}d, " +
                    $"{rotation.GetProperty("z").GetRawText()}d, " +
                    $"{rotation.GetProperty("w").GetRawText()}d)";
                string vector(JsonElement value) =>
                    $"new global::Finare.Unreal.UnrealVector(" +
                    $"{value.GetProperty("x").GetRawText()}d, " +
                    $"{value.GetProperty("y").GetRawText()}d, " +
                    $"{value.GetProperty("z").GetRawText()}d)";
                return $"new {spec.TypeName}({quaternion}, {vector(translation)}, {vector(scale)})";
            }
            string x = spec.DefaultValue.GetProperty("x").GetRawText();
            string y = spec.DefaultValue.GetProperty("y").GetRawText();
            string z = spec.DefaultValue.GetProperty("z").GetRawText();
            return $"new {spec.TypeName}({x}d, {y}d, {z}d)";
        }

        string value = spec.DefaultValue.GetRawText();
        string type = spec.TypeName.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();
        return type switch
        {
            "float" or "System.Single" => value + "f",
            "double" or "System.Double" => value + "d",
            "decimal" or "System.Decimal" => value + "m",
            "uint" or "System.UInt32" => value + "u",
            "long" or "System.Int64" => value + "L",
            "ulong" or "System.UInt64" => value + "UL",
            _ => value,
        };
    }

    private static string[] GetCollectionTypeArguments(TypeSyntax syntax)
    {
        GenericNameSyntax? generic = syntax.DescendantNodesAndSelf()
            .OfType<GenericNameSyntax>()
            .FirstOrDefault();
        if (generic is null)
        {
            return [];
        }
        return generic.TypeArgumentList.Arguments
            .Select(static argument => argument.ToString())
            .ToArray();
    }

    private static string RenderCollectionLeafDefault(
        JsonElement value,
        string typeName,
        ITypeSymbol? resolvedType)
    {
        if (resolvedType is INamedTypeSymbol named && value.ValueKind == JsonValueKind.Null)
            for (INamedTypeSymbol? current = named; current is not null; current = current.BaseType)
                if (current.ToDisplayString() == "Finare.Unreal.UnrealObject") return "null";
        string normalizedType = (resolvedType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? typeName)
            .Replace("global::", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (normalizedType is "string" or "System.String")
        {
            return value.ValueKind == JsonValueKind.String
                ? Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value.GetString()!, quote: true)
                : throw new InvalidOperationException("A string collection default must be a JSON string.");
        }
        if (normalizedType is "bool" or "System.Boolean")
        {
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetRawText()
                : throw new InvalidOperationException("A boolean collection default is invalid.");
        }
        if (normalizedType is "byte" or "System.Byte" or "int" or "System.Int32" or
            "long" or "System.Int64" or "float" or "System.Single" or
            "double" or "System.Double")
        {
            if (value.ValueKind != JsonValueKind.Number)
                throw new InvalidOperationException("A numeric collection default is invalid.");
            string suffix = normalizedType switch
            {
                "long" or "System.Int64" => "L",
                "float" or "System.Single" => "f",
                "double" or "System.Double" => "d",
                _ => string.Empty,
            };
            return value.GetRawText() + suffix;
        }
        if (normalizedType is "Finare.Unreal.UnrealName" or
            "Finare.Unreal.UnrealText")
        {
            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("A name/text collection default must be a JSON string.");
            string escaped = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
                value.GetString()!, quote: true);
            return $"new {typeName}({escaped})";
        }
        if (normalizedType is "Finare.Unreal.UnrealSoftObjectHandle" or
            "Finare.Unreal.UnrealSoftClassHandle")
        {
            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("A soft reference collection default must be a JSON string.");
            string path = value.GetString()!;
            return path.Length == 0
                ? "default"
                : $"new {typeName}(" +
                    Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(path, quote: true) + ")";
        }
        if (normalizedType is "Finare.Unreal.UnrealObjectHandle" or
            "Finare.Unreal.UnrealClassHandle")
        {
            return value.ValueKind == JsonValueKind.Null
                ? "default"
                : throw new InvalidOperationException("An object collection default must be null.");
        }
        if (normalizedType == "Finare.Unreal.UnrealVector" &&
            value.ValueKind == JsonValueKind.Object)
        {
            return $"new {typeName}({RenderDouble(value, "x")}, {RenderDouble(value, "y")}, {RenderDouble(value, "z")})";
        }
        if (normalizedType == "Finare.Unreal.UnrealRotator" &&
            value.ValueKind == JsonValueKind.Object)
        {
            return $"new {typeName}({RenderDouble(value, "pitch")}, {RenderDouble(value, "yaw")}, {RenderDouble(value, "roll")})";
        }
        if (normalizedType == "Finare.Unreal.UnrealTransform" &&
            value.ValueKind == JsonValueKind.Object)
        {
            JsonElement rotation = value.GetProperty("rotation");
            JsonElement translation = value.GetProperty("translation");
            JsonElement scale = value.GetProperty("scale3D");
            string quaternion =
                $"new global::Finare.Unreal.UnrealQuaternion({RenderDouble(rotation, "x")}, " +
                $"{RenderDouble(rotation, "y")}, {RenderDouble(rotation, "z")}, {RenderDouble(rotation, "w")})";
            string vector(JsonElement vectorValue) =>
                $"new global::Finare.Unreal.UnrealVector({RenderDouble(vectorValue, "x")}, " +
                $"{RenderDouble(vectorValue, "y")}, {RenderDouble(vectorValue, "z")})";
            return $"new {typeName}({quaternion}, {vector(translation)}, {vector(scale)})";
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            string enumValue = value.GetString()!;
            if (long.TryParse(enumValue,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long numericValue))
            {
                return $"new {typeName}({numericValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}L)";
            }
            if (Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(enumValue))
                return $"{typeName}.{enumValue}";
        }
        throw new InvalidOperationException(
            $"Collection default for '{typeName}' is not a supported scalar literal.");
    }

    private static string RenderDouble(JsonElement value, string propertyName)
    {
        JsonElement component = value.GetProperty(propertyName);
        if (component.ValueKind != JsonValueKind.Number || !component.TryGetDouble(out double number) ||
            !double.IsFinite(number))
            throw new InvalidOperationException($"Collection struct component '{propertyName}' is invalid.");
        return number.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d";
    }

    private static SyntaxList<AttributeListSyntax> RemoveExposureAttributes(
        SyntaxList<AttributeListSyntax> lists)
    {
        var result = new List<AttributeListSyntax>();
        foreach (AttributeListSyntax list in lists)
        {
            AttributeSyntax[] retained = list.Attributes
                .Where(static attribute => !IsExposeAttribute(attribute.Name.ToString()))
                .ToArray();
            if (retained.Length > 0)
            {
                result.Add(list.WithAttributes(SyntaxFactory.SeparatedList(retained)));
            }
        }

        return SyntaxFactory.List(result);
    }

    private static AttributeListSyntax? CreateExposureAttribute(ScriptExposure exposure)
    {
        string text = RenderExposureAttribute(exposure);
        if (text.Length == 0)
        {
            return null;
        }

        PropertyDeclarationSyntax property = SyntaxFactory.ParseMemberDeclaration(
            $"{text}public int __State {{ get; set; }}") as PropertyDeclarationSyntax ??
            throw new InvalidOperationException("A rendered exposure attribute did not parse.");
        return property.AttributeLists.Single();
    }

    private static string RenderExposureAttribute(ScriptExposure exposure)
    {
        var arguments = new List<string>();
        if (exposure.InstanceEditable)
        {
            arguments.Add("InstanceEditable = true");
        }

        if (exposure.SaveGame)
        {
            arguments.Add("SaveGame = true");
        }

        if (exposure.Replicated)
        {
            arguments.Add("Replicated = true");
        }

        if (exposure.ReplicationCondition != global::Finare.ReplicationCondition.None)
            arguments.Add($"ReplicationCondition = global::Finare.ReplicationCondition.{exposure.ReplicationCondition}");

        if (exposure.Category is not null)
        {
            arguments.Add("Category = " + Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
                exposure.Category,
                quote: true));
        }

        return arguments.Count == 0
            ? string.Empty
            : $"[Finare.Expose({string.Join(", ", arguments)})] ";
    }

    private static bool IsExposeAttribute(string name) =>
        name is "Expose" or "ExposeAttribute" or "Finare.Expose" or
        "Finare.ExposeAttribute" or "global::Finare.Expose" or
        "global::Finare.ExposeAttribute";

    private static CSharpSourceDocument CreatePatchedSource(
        CSharpSourceDocument source,
        SyntaxNode patchedRoot,
        IdentitySidecarDocument? effectiveSidecar,
        IReadOnlyList<UpdatePlan> updates,
        IReadOnlyList<FunctionUpdatePlan> functionUpdates)
    {
        string patchedText = patchedRoot.ToFullString();
        if (effectiveSidecar is null)
        {
            return new CSharpSourceDocument(source.LogicalPath, patchedText);
        }

        CSharpIdentityPin[] statePins = updates.Select(update =>
        {
            PropertyDeclarationSyntax property = GetAnnotatedNode<PropertyDeclarationSyntax>(
                patchedRoot,
                StateAnnotationKind,
                update.State.Id);
            return new CSharpIdentityPin(
                update.State.Id,
                "state",
                RoslynSourceSpan.FromNode(property, source.LogicalPath));
        }).ToArray();
        CSharpIdentityPin[] functionPins = functionUpdates
            .Where(update => effectiveSidecar.Entries.Any(entry =>
                entry.Id == update.Location.Function.Id &&
                entry.Kind == "function"))
            .Select(update =>
        {
            MethodDeclarationSyntax method = GetAnnotatedNode<MethodDeclarationSyntax>(
                patchedRoot,
                FunctionAnnotationKind,
                update.Location.Function.Id);
            return new CSharpIdentityPin(
                update.Location.Function.Id,
                "function",
                RoslynSourceSpan.FromNode(method, source.LogicalPath));
        }).ToArray();
        return new CSharpSourceDocument(
            source.LogicalPath,
            patchedText,
            effectiveSidecar,
            statePins.Concat(functionPins));
    }

    private static UnifiedDiagnostic? ValidatePostconditions(
        DeclarationPatchDocument patch,
        ScriptAssetDocument before,
        ScriptAssetDocument after,
        HirCompilationUnit afterHir,
        IReadOnlyDictionary<SemanticId, StateLocation> beforeStates,
        IReadOnlyDictionary<SemanticId, FunctionLocation> beforeFunctions,
        IReadOnlySet<SemanticId> persistentFunctionIds)
    {
        var beforeAssets = before.Assets.ToDictionary(static asset => asset.Id);
        var afterAssets = after.Assets.ToDictionary(static asset => asset.Id);
        var afterStates = after.Assets
            .SelectMany(static asset => asset.States)
            .GroupBy(static state => state.Id)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        foreach (DeclarationPatchOperation operation in patch.Operations)
        {
            switch (operation)
            {
                case AddScriptAssetPatchOperation addAsset:
                    string qualifiedName = string.IsNullOrEmpty(addAsset.Asset.Namespace)
                        ? addAsset.Asset.Name
                        : $"{addAsset.Asset.Namespace}.{addAsset.Asset.Name}";
                    if (after.Assets.Count(asset =>
                            asset.QualifiedName == qualifiedName &&
                            asset.AssetPath == addAsset.Asset.AssetPath) != 1)
                    {
                        return PostconditionFailed(
                            SemanticId.FromKey(qualifiedName),
                            "added Script Asset contract");
                    }
                    break;
                case UpdateScriptAssetPatchOperation updateAsset:
                    if (!afterAssets.TryGetValue(
                            updateAsset.TargetAssetId,
                            out ScriptAssetDeclaration? updatedAsset) ||
                        updatedAsset.AssetPath != updateAsset.ReplacementAssetPath)
                    {
                        return PostconditionFailed(
                            updateAsset.TargetAssetId,
                            "updated Script Asset path and identity");
                    }
                    break;
                case UpdateScriptClassBasePatchOperation updateClassBase:
                    if (!afterAssets.TryGetValue(
                            updateClassBase.TargetAssetId,
                            out ScriptAssetDeclaration? updatedClassBaseAsset) ||
                        updatedClassBaseAsset.ClassBinding is null ||
                        updatedClassBaseAsset.ClassBinding !=
                        (updateClassBase.ExpectedClassBinding with
                        {
                            BaseClassPath = updateClassBase.ReplacementBaseClassPath,
                        }))
                    {
                        return PostconditionFailed(
                            updateClassBase.TargetAssetId,
                            "updated Script Class BaseClassPath and identity");
                    }
                    break;
                case RemoveScriptAssetPatchOperation removeAsset:
                    if (afterAssets.ContainsKey(removeAsset.TargetAssetId) ||
                        !beforeAssets.ContainsKey(removeAsset.TargetAssetId))
                    {
                        return PostconditionFailed(removeAsset.TargetAssetId, "Script Asset removal");
                    }
                    break;
                case AddScriptStatePatchOperation add:
                    if (!afterAssets.TryGetValue(add.TargetAssetId, out ScriptAssetDeclaration? asset) ||
                        asset.States.Count(state => Matches(state, add.State)) != 1)
                    {
                        return PostconditionFailed(add.TargetAssetId, "added state contract");
                    }

                    break;
                case UpdateScriptStatePatchOperation update:
                {
                    if (!beforeStates.TryGetValue(update.TargetStateId, out StateLocation? beforeStateLocation))
                    {
                        return PostconditionFailed(update.TargetStateId, "updated state contract and identity");
                    }

                    ScriptAssetDeclaration? afterStateOwner = afterAssets.GetValueOrDefault(
                        beforeStateLocation.Asset.Id);
                    if (afterStateOwner is null ||
                        !afterStateOwner.States.Any(state =>
                            state.Id == update.TargetStateId && Matches(state, update.Replacement)))
                    {
                        return PostconditionFailed(update.TargetStateId, "updated state contract and identity");
                    }

                    break;
                }
                case RemoveScriptStatePatchOperation remove:
                    if (afterStates.ContainsKey(remove.TargetStateId) ||
                        beforeAssets.Values.All(asset => asset.States.All(state => state.Id != remove.TargetStateId)))
                    {
                        return PostconditionFailed(remove.TargetStateId, "state removal");
                    }

                    break;
                case AddScriptFunctionPatchOperation addFunction:
                    if (!afterAssets.TryGetValue(
                            addFunction.TargetAssetId,
                            out ScriptAssetDeclaration? functionAsset) ||
                        functionAsset.Functions.Count(function =>
                            Matches(function, addFunction.Function)) != 1)
                    {
                        return PostconditionFailed(
                            addFunction.TargetAssetId,
                            "added visual function contract");
                    }
                    break;
                case UpdateScriptFunctionPatchOperation updateFunction:
                {
                    if (!beforeFunctions.TryGetValue(
                            updateFunction.TargetFunctionId,
                            out FunctionLocation? beforeFunctionLocation))
                    {
                        return PostconditionFailed(
                            updateFunction.TargetFunctionId,
                            "updated visual function contract");
                    }

                    ScriptAssetDeclaration? afterFunctionOwner = afterAssets.GetValueOrDefault(
                        beforeFunctionLocation.Asset.Id);
                    if (afterFunctionOwner is null ||
                        !afterFunctionOwner.Functions.Any(function =>
                            (!persistentFunctionIds.Contains(updateFunction.TargetFunctionId) ||
                                function.Id == updateFunction.TargetFunctionId) &&
                            Matches(function, updateFunction.Replacement)))
                    {
                        return PostconditionFailed(
                            updateFunction.TargetFunctionId,
                            "updated visual function contract");
                    }
                    break;
                }
                case RemoveScriptFunctionPatchOperation removeFunction:
                    if (after.Assets.SelectMany(static asset => asset.Functions)
                            .Any(function => function.Id == removeFunction.TargetFunctionId) ||
                        !before.Assets.SelectMany(static asset => asset.Functions)
                            .Any(function => function.Id == removeFunction.TargetFunctionId))
                    {
                        return PostconditionFailed(
                            removeFunction.TargetFunctionId,
                            "visual function removal");
                    }
                    break;
                case AddUnrealLifecycleHookPatchOperation addLifecycle:
                    if (!afterAssets.TryGetValue(
                            addLifecycle.TargetAssetId,
                            out ScriptAssetDeclaration? lifecycleAsset) ||
                        (lifecycleAsset.LifecycleHooks ?? []).Count(hook =>
                            MatchesLifecycle(hook, addLifecycle.Hook)) != 1)
                    {
                        return PostconditionFailed(
                            addLifecycle.TargetAssetId,
                            $"{addLifecycle.Hook} lifecycle hook");
                    }
                    break;
                case AddUnrealEventHandlerPatchOperation addEvent:
                    HirMethodDeclaration[] matchingHandlers = afterHir.Types
                        .SelectMany(static type => type.Methods)
                        .Where(method => method.Name == addEvent.Handler.Name &&
                            method.Parameters.Select(static parameter => parameter.TypeName)
                                .SequenceEqual(addEvent.Handler.Parameters.Select(
                                    static parameter => parameter.TypeName)) &&
                            method.Attributes?.Any(attribute =>
                                attribute.Name == "UnrealEvent" &&
                                attribute.Arguments.Count >= 2 &&
                                attribute.Arguments[0] == addEvent.Handler.SourceTypeName &&
                                attribute.Arguments[1] == addEvent.Handler.EventPath) == true)
                        .ToArray();
                    if (matchingHandlers.Length != 1)
                    {
                        return PostconditionFailed(
                            addEvent.TargetAssetId,
                            "visual Unreal Event Entry contract");
                    }
                    break;
            }
        }

        return null;
    }

    private static bool Matches(
        ScriptStateDeclaration state,
        ScriptStatePatchPrecondition expected) =>
        string.Equals(state.Name, expected.Name, StringComparison.Ordinal) &&
        string.Equals(state.TypeMetadataName, expected.TypeMetadataName, StringComparison.Ordinal) &&
        state.TypeKind == expected.TypeKind &&
        JsonElement.DeepEquals(state.DefaultValue, expected.DefaultValue) &&
        state.Exposure == expected.Exposure;

    private static bool Matches(ScriptStateDeclaration state, ScriptStatePatchSpec expected) =>
        string.Equals(state.Name, expected.Name, StringComparison.Ordinal) &&
        MatchesSourceType(state, expected.TypeName) &&
        state.TypeKind == expected.TypeKind &&
        JsonElement.DeepEquals(state.DefaultValue, expected.DefaultValue) &&
        state.Exposure == expected.Exposure;

    private static bool Matches(
        ScriptAssetDeclaration asset,
        ScriptAssetPatchPrecondition expected) =>
        asset.QualifiedName == expected.QualifiedName && asset.AssetPath == expected.AssetPath;

    private static bool Matches(
        ScriptFunctionDeclaration function,
        ScriptFunctionPatchPrecondition expected) =>
        function.Name == expected.Name &&
        function.ReturnTypeMetadataName == expected.ReturnTypeMetadataName &&
        function.IsStatic == expected.IsStatic &&
        function.Parameters.Count == expected.Parameters.Count &&
        function.Parameters.Zip(expected.Parameters).All(pair =>
            pair.First.Name == pair.Second.Name &&
            pair.First.TypeMetadataName == pair.Second.TypeMetadataName);

    private static bool Matches(
        ScriptFunctionDeclaration function,
        ScriptFunctionPatchSpec expected) =>
        function.Name == expected.Name &&
        MatchesSourceType(function.ReturnTypeMetadataName, expected.ReturnTypeName) &&
        function.IsStatic == expected.IsStatic &&
        function.Parameters.Count == expected.Parameters.Count &&
        function.Parameters.Zip(expected.Parameters).All(pair =>
            pair.First.Name == pair.Second.Name &&
            MatchesSourceType(pair.First.TypeMetadataName, pair.Second.TypeName));

    private static bool MatchesLifecycle(ScriptFunctionDeclaration function, string hook) =>
        hook switch
        {
            "beginPlay" => function.Name == "OnBeginPlay" &&
                function.ReturnTypeMetadataName == "System.Void" &&
                function.Parameters.Count == 0,
            "tick" => function.Name == "OnTick" &&
                function.ReturnTypeMetadataName == "System.Void" &&
                function.Parameters.Count == 1 &&
                function.Parameters[0].TypeMetadataName == "System.Single",
            "endPlay" => function.Name == "OnEndPlay" &&
                function.ReturnTypeMetadataName == "System.Void" &&
                function.Parameters.Count == 0,
            _ => false,
        };

    private static bool MatchesLifecycle(
        ScriptLifecycleHookDeclaration hook,
        string expected) => expected switch
        {
            "beginPlay" => hook.Kind == ScriptLifecycleHookKind.BeginPlay &&
                hook.ManagedName == "OnBeginPlay",
            "tick" => hook.Kind == ScriptLifecycleHookKind.Tick &&
                hook.ManagedName == "OnTick",
            "endPlay" => hook.Kind == ScriptLifecycleHookKind.EndPlay &&
                hook.ManagedName == "OnEndPlay",
            _ => false,
        };

    private static bool MatchesSourceType(ScriptStateDeclaration state, string sourceTypeName)
    {
        string sourceName = sourceTypeName.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();
        IReadOnlyDictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bool"] = "System.Boolean",
            ["byte"] = "System.Byte",
            ["sbyte"] = "System.SByte",
            ["short"] = "System.Int16",
            ["ushort"] = "System.UInt16",
            ["int"] = "System.Int32",
            ["uint"] = "System.UInt32",
            ["long"] = "System.Int64",
            ["ulong"] = "System.UInt64",
            ["float"] = "System.Single",
            ["double"] = "System.Double",
            ["decimal"] = "System.Decimal",
            ["string"] = "System.String",
        };
        string metadataName = aliases.GetValueOrDefault(sourceName, sourceName);
        return string.Equals(state.TypeMetadataName, metadataName, StringComparison.Ordinal) ||
            string.Equals(state.TypeDisplayName, sourceName, StringComparison.Ordinal) ||
            (!sourceName.Contains('.', StringComparison.Ordinal) &&
                state.TypeMetadataName.EndsWith('.' + sourceName, StringComparison.Ordinal)) ||
            IsAliasedCollectionType(state, sourceName);
    }

    private static bool IsAliasedCollectionType(
        ScriptStateDeclaration state,
        string sourceName) =>
        !sourceName.Contains('.', StringComparison.Ordinal) &&
        sourceName.IndexOf('<') < 0 &&
        state.TypeKind is ScriptStateTypeKind.Array or ScriptStateTypeKind.Set or ScriptStateTypeKind.Map &&
        state.UnrealType?.Kind switch
        {
            ScriptFunctionValueTypeKind.Array => state.TypeMetadataName.StartsWith(
                "System.Collections.Generic.IReadOnlyList<", StringComparison.Ordinal),
            ScriptFunctionValueTypeKind.Set => state.TypeMetadataName.StartsWith(
                "System.Collections.Generic.IReadOnlySet<", StringComparison.Ordinal),
            ScriptFunctionValueTypeKind.Map => state.TypeMetadataName.StartsWith(
                "System.Collections.Generic.IReadOnlyDictionary<", StringComparison.Ordinal),
            _ => false,
        };

    private static bool MatchesSourceType(string metadataTypeName, string sourceTypeName)
    {
        string sourceName = sourceTypeName.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();
        IReadOnlyDictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["void"] = "System.Void",
            ["bool"] = "System.Boolean",
            ["byte"] = "System.Byte",
            ["sbyte"] = "System.SByte",
            ["short"] = "System.Int16",
            ["ushort"] = "System.UInt16",
            ["int"] = "System.Int32",
            ["uint"] = "System.UInt32",
            ["long"] = "System.Int64",
            ["ulong"] = "System.UInt64",
            ["float"] = "System.Single",
            ["double"] = "System.Double",
            ["decimal"] = "System.Decimal",
            ["string"] = "System.String",
        };
        string metadataName = aliases.GetValueOrDefault(sourceName, sourceName);
        return metadataTypeName == metadataName ||
            (!sourceName.Contains('.', StringComparison.Ordinal) &&
                metadataTypeName.EndsWith('.' + sourceName, StringComparison.Ordinal));
    }

    private static bool TryValidateAssetSpec(
        ScriptAssetPatchSpec spec,
        SourceSpan span,
        out UnifiedDiagnostic? failure)
    {
        if (!SyntaxFacts.IsValidIdentifier(spec.Name) ||
            SyntaxFacts.GetKeywordKind(spec.Name) != SyntaxKind.None)
        {
            failure = InvalidReplacement(
                $"'{spec.Name}' is not a valid C# Script Asset type name.",
                span);
            return false;
        }
        if (spec.Namespace.Length > 0 && spec.Namespace.Split('.').Any(segment =>
                !SyntaxFacts.IsValidIdentifier(segment) ||
                SyntaxFacts.GetKeywordKind(segment) != SyntaxKind.None))
        {
            failure = InvalidReplacement(
                $"'{spec.Namespace}' is not a valid C# namespace.",
                span);
            return false;
        }
        if (spec.BaseTypeName is not null &&
            SyntaxFactory.ParseTypeName(spec.BaseTypeName).ContainsDiagnostics)
        {
            failure = InvalidReplacement(
                $"'{spec.BaseTypeName}' is not valid C# base-type syntax.",
                span);
            return false;
        }
        if (spec.IncludeActorLifecycle && spec.BaseTypeName is not null &&
            !spec.BaseTypeName.EndsWith("UnrealActorScript", StringComparison.Ordinal) &&
            !spec.BaseTypeName.EndsWith("UnrealActorComponentScript", StringComparison.Ordinal))
        {
            failure = InvalidReplacement(
                "Lifecycle hooks require UnrealActorScript or UnrealActorComponentScript as the Script Asset base type.",
                span);
            return false;
        }
        failure = null;
        return true;
    }

    private static bool TryValidateFunctionSpec(
        ScriptFunctionPatchSpec spec,
        SourceSpan span,
        out UnifiedDiagnostic? failure)
    {
        if (!SyntaxFacts.IsValidIdentifier(spec.Name) ||
            SyntaxFacts.GetKeywordKind(spec.Name) != SyntaxKind.None)
        {
            failure = InvalidReplacement(
                $"'{spec.Name}' is not a valid C# function name.",
                span);
            return false;
        }
        TypeSyntax returnType = SyntaxFactory.ParseTypeName(spec.ReturnTypeName);
        if (returnType.ContainsDiagnostics || !CanCreateDefaultFunctionBody(spec.ReturnTypeName))
        {
            failure = InvalidReplacement(
                $"'{spec.ReturnTypeName}' is not a supported authoring return type. " +
                "Use void, bool, an integer scalar, float, double, decimal, or string.",
                span);
            return false;
        }
        if (spec.Parameters.Count > 16)
        {
            failure = InvalidReplacement(
                "A visually authored function supports at most 16 parameters.",
                span);
            return false;
        }
        foreach (ScriptFunctionParameterPatchSpec parameter in spec.Parameters)
        {
            if (!SyntaxFacts.IsValidIdentifier(parameter.Name) ||
                SyntaxFacts.GetKeywordKind(parameter.Name) != SyntaxKind.None ||
                SyntaxFactory.ParseTypeName(parameter.TypeName).ContainsDiagnostics)
            {
                failure = InvalidReplacement(
                    $"Function parameter '{parameter.TypeName} {parameter.Name}' is not valid C# syntax.",
                    span);
                return false;
            }
        }
        failure = null;
        return true;
    }

    private static bool CanCreateDefaultFunctionBody(string returnTypeName)
    {
        string type = returnTypeName.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();
        return type is "void" or "bool" or "System.Boolean" or
            "byte" or "System.Byte" or "sbyte" or "System.SByte" or
            "short" or "System.Int16" or "ushort" or "System.UInt16" or
            "int" or "System.Int32" or "uint" or "System.UInt32" or
            "long" or "System.Int64" or "ulong" or "System.UInt64" or
            "float" or "System.Single" or "double" or "System.Double" or
            "decimal" or "System.Decimal" or "string" or "System.String";
    }

    private static bool TryValidateSpec(
        ScriptStatePatchSpec spec,
        SourceSpan span,
        out UnifiedDiagnostic? failure)
    {
        if (!SyntaxFacts.IsValidIdentifier(spec.Name))
        {
            failure = InvalidReplacement($"'{spec.Name}' is not a valid C# identifier.", span);
            return false;
        }

        TypeSyntax type = SyntaxFactory.ParseTypeName(spec.TypeName);
        if (type.ContainsDiagnostics)
        {
            failure = InvalidReplacement($"'{spec.TypeName}' is not valid C# type syntax.", span);
            return false;
        }

        if (spec.TypeKind == ScriptStateTypeKind.Enum &&
            !SyntaxFacts.IsValidIdentifier(spec.DefaultValue.GetString()!) &&
            !long.TryParse(
                spec.DefaultValue.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            failure = InvalidReplacement("The enum default is not a valid member name or integral Unreal value.", span);
            return false;
        }

        failure = null;
        return true;
    }

    private static bool TryValidateEventHandler(
        UnrealEventHandlerPatchSpec spec,
        SourceSpan span,
        out UnifiedDiagnostic? failure)
    {
        if (!SyntaxFacts.IsValidIdentifier(spec.Name) ||
            SyntaxFacts.GetKeywordKind(spec.Name) != SyntaxKind.None)
        {
            failure = InvalidReplacement(
                $"'{spec.Name}' is not a valid C# event-handler identifier.",
                span);
            return false;
        }
        if (SyntaxFactory.ParseTypeName(spec.SourceTypeName).ContainsDiagnostics)
        {
            failure = InvalidReplacement(
                $"'{spec.SourceTypeName}' is not valid C# wrapper type syntax.",
                span);
            return false;
        }
        if (!spec.EventPath.StartsWith("/Script/", StringComparison.Ordinal) ||
            !spec.EventPath.Contains(':', StringComparison.Ordinal))
        {
            failure = InvalidReplacement(
                $"'{spec.EventPath}' is not a canonical reflected UE event path.",
                span);
            return false;
        }
        if (spec.SourceMember is not null &&
            (!SyntaxFacts.IsValidIdentifier(spec.SourceMember) ||
                SyntaxFacts.GetKeywordKind(spec.SourceMember) != SyntaxKind.None))
        {
            failure = InvalidReplacement(
                $"'{spec.SourceMember}' is not a valid C# event-source member name.",
                span);
            return false;
        }
        foreach (UnrealEventHandlerParameterPatchSpec parameter in spec.Parameters)
        {
            if (!SyntaxFacts.IsValidIdentifier(parameter.Name) ||
                SyntaxFacts.GetKeywordKind(parameter.Name) != SyntaxKind.None ||
                SyntaxFactory.ParseTypeName(parameter.TypeName).ContainsDiagnostics)
            {
                failure = InvalidReplacement(
                    $"Event parameter '{parameter.TypeName} {parameter.Name}' is not valid C# syntax.",
                    span);
                return false;
            }
        }
        failure = null;
        return true;
    }

    private static UnifiedDiagnostic? ValidateFinalContracts(
        ScriptAssetDocument document,
        IReadOnlyList<AssetAddPlan> assetAdds,
        IReadOnlyList<AssetUpdatePlan> assetUpdates,
        IReadOnlyList<ClassBaseUpdatePlan> classBaseUpdates,
        IReadOnlyList<AssetRemovePlan> assetRemoves,
        IReadOnlyList<AddPlan> adds,
        IReadOnlyList<EventAddPlan> eventAdds,
        IReadOnlyList<UpdatePlan> updates,
        IReadOnlyList<RemovePlan> removes,
        IReadOnlyList<FunctionAddPlan> functionAdds,
        IReadOnlyList<FunctionUpdatePlan> functionUpdates,
        IReadOnlyList<FunctionRemovePlan> functionRemoves,
        IReadOnlyList<LifecycleAddPlan> lifecycleAdds)
    {
        IReadOnlySet<SemanticId> removedAssetIds = assetRemoves
            .Select(static remove => remove.Asset.Id)
            .ToHashSet();
        SemanticId? conflictingRemovedAsset = adds.Select(static add => add.Asset.Id)
            .Concat(updates.Select(static update => update.Asset.Id))
            .Concat(classBaseUpdates.Select(static update => update.Asset.Id))
            .Concat(removes.Select(static remove => remove.Asset.Id))
            .Concat(eventAdds.Select(static add => add.Asset.Id))
            .Concat(functionAdds.Select(static add => add.Asset.Id))
            .Concat(functionUpdates.Select(static update => update.Location.Asset.Id))
            .Concat(functionRemoves.Select(static remove => remove.Location.Asset.Id))
            .Concat(lifecycleAdds.Select(static add => add.Asset.Id))
            .FirstOrDefault(removedAssetIds.Contains);
        if (conflictingRemovedAsset is { IsEmpty: false })
        {
            return InvalidReplacement(
                "A Script Asset cannot be removed and edited in the same declaration transaction.",
                document.Assets.Single(asset => asset.Id == conflictingRemovedAsset.Value).Span);
        }

        IReadOnlyDictionary<SemanticId, AssetUpdatePlan> assetUpdatesById = assetUpdates
            .ToDictionary(static update => update.Asset.Id);
        string[] finalAssetPaths = document.Assets
            .Where(asset => !removedAssetIds.Contains(asset.Id))
            .Select(asset => assetUpdatesById.TryGetValue(asset.Id, out AssetUpdatePlan? update)
                ? update.ReplacementAssetPath
                : asset.AssetPath)
            .Concat(assetAdds.Select(static add => add.Asset.AssetPath))
            .ToArray();
        string? duplicateAssetPath = finalAssetPaths
            .GroupBy(static path => path, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (duplicateAssetPath is not null)
        {
            return InvalidReplacement(
                $"The declaration transaction would contain duplicate Script Asset path " +
                $"'{duplicateAssetPath}'.",
                SourceSpan.Unknown("<declaration-patch>"));
        }
        string[] finalQualifiedNames = document.Assets
            .Where(asset => !removedAssetIds.Contains(asset.Id))
            .Select(static asset => asset.QualifiedName)
            .Concat(assetAdds.Select(add => string.IsNullOrEmpty(add.Asset.Namespace)
                ? add.Asset.Name
                : $"{add.Asset.Namespace}.{add.Asset.Name}"))
            .ToArray();
        string? duplicateQualifiedName = finalQualifiedNames
            .GroupBy(static name => name, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (duplicateQualifiedName is not null)
        {
            return InvalidReplacement(
                $"The declaration transaction would contain duplicate C# type " +
                $"'{duplicateQualifiedName}'.",
                SourceSpan.Unknown("<declaration-patch>"));
        }

        IReadOnlyDictionary<SemanticId, UpdatePlan> updatesById = updates.ToDictionary(
            static update => update.State.Id);
        IReadOnlySet<SemanticId> removedIds = removes
            .Select(static remove => remove.State.Id)
            .ToHashSet();
        IReadOnlyDictionary<SemanticId, FunctionUpdatePlan> functionUpdatesById = functionUpdates
            .ToDictionary(static update => update.Location.Function.Id);
        IReadOnlySet<SemanticId> removedFunctionIds = functionRemoves
            .Select(static remove => remove.Location.Function.Id)
            .ToHashSet();
        foreach (ScriptAssetDeclaration asset in document.Assets.Where(
                     asset => !removedAssetIds.Contains(asset.Id)))
        {
            string[] finalNames = asset.States
                .Where(state => !removedIds.Contains(state.Id))
                .Select(state => updatesById.TryGetValue(state.Id, out UpdatePlan? update)
                    ? update.Replacement.Name
                    : state.Name)
                .Concat(adds.Where(add => add.Asset.Id == asset.Id).Select(static add => add.State.Name))
                .ToArray();
            string? duplicateName = finalNames
                .GroupBy(static name => name, StringComparer.Ordinal)
                .FirstOrDefault(static group => group.Count() > 1)
                ?.Key;
            if (duplicateName is not null)
            {
                return InvalidReplacement(
                    $"Script asset '{asset.QualifiedName}' would contain duplicate state " +
                    $"name '{duplicateName}'.",
                    asset.Span);
            }
            string? duplicateFunction = asset.Functions
                .Where(function => !removedFunctionIds.Contains(function.Id))
                .Select(function => functionUpdatesById.TryGetValue(
                        function.Id,
                        out FunctionUpdatePlan? update)
                    ? update.Replacement.Name
                    : function.Name)
                .Concat((asset.LifecycleHooks ?? [])
                    .Select(static hook => hook.ManagedName))
                .Concat(functionAdds
                    .Where(add => add.Asset.Id == asset.Id)
                    .Select(static add => add.Function.Name))
                .Concat(eventAdds
                    .Where(add => add.Asset.Id == asset.Id)
                    .Select(static add => add.Handler.Name))
                .Concat(lifecycleAdds
                    .Where(add => add.Asset.Id == asset.Id)
                    .Select(static add => LifecycleMethodName(add.Hook)))
                .GroupBy(static name => name, StringComparer.Ordinal)
                .FirstOrDefault(static group => group.Count() > 1)
                ?.Key;
            if (duplicateFunction is not null)
            {
                return InvalidReplacement(
                    $"Script asset '{asset.QualifiedName}' would contain duplicate visual " +
                    $"function name '{duplicateFunction}'.",
                    asset.Span);
            }
        }

        return null;
    }

    private static string LifecycleMethodName(string hook) => hook switch
    {
        "beginPlay" => "OnBeginPlay",
        "tick" => "OnTick",
        "endPlay" => "OnEndPlay",
        _ => throw new InvalidOperationException($"Unsupported lifecycle hook '{hook}'."),
    };

    private static bool TryResolveStateLocations(
        IReadOnlyList<StateLocation> locations,
        IReadOnlyDictionary<string, RewriteContext> contexts,
        out IReadOnlyDictionary<SemanticId, StateLocation> resolved,
        out UnifiedDiagnostic? failure)
    {
        var result = new Dictionary<SemanticId, StateLocation>();
        foreach (IGrouping<SemanticId, StateLocation> group in locations.GroupBy(
                     static location => location.State.Id))
        {
            StateLocation[] candidates = group.ToArray();
            string spanKey = SpanKey(candidates[0].State.Span);
            if (candidates.Any(candidate =>
                    !string.Equals(SpanKey(candidate.State.Span), spanKey, StringComparison.Ordinal)))
            {
                failure = InvalidReplacement(
                    $"State identity '{group.Key}' resolves to multiple source declarations; the declaration owner is ambiguous.",
                    candidates[0].State.Span);
                resolved = result;
                return false;
            }

            if (!contexts.TryGetValue(candidates[0].State.Span.FilePath, out RewriteContext? context))
            {
                failure = MissingSyntax(group.Key, candidates[0].State.Span, "script state property source");
                resolved = result;
                return false;
            }

            PropertyDeclarationSyntax[] syntax = context.Root.DescendantNodes()
                .OfType<PropertyDeclarationSyntax>()
                .Where(property => RoslynSourceSpan.FromNode(
                    property,
                    candidates[0].State.Span.FilePath) == candidates[0].State.Span)
                .ToArray();
            if (syntax.Length != 1)
            {
                failure = MissingSyntax(group.Key, candidates[0].State.Span, "script state property");
                resolved = result;
                return false;
            }

            IPropertySymbol? symbol = context.SemanticModel.GetDeclaredSymbol(syntax[0]);
            INamedTypeSymbol? containingType = symbol?.ContainingType;
            if (containingType is null)
            {
                failure = InvalidReplacement(
                    $"State identity '{group.Key}' has no Roslyn declaration owner.",
                    candidates[0].State.Span);
                resolved = result;
                return false;
            }

            string ownerName = GetMetadataName(containingType);
            StateLocation[] ownerLocations = candidates
                .Where(candidate => string.Equals(
                    candidate.Asset.QualifiedName,
                    ownerName,
                    StringComparison.Ordinal))
                .ToArray();
            if (ownerLocations.Length != 1)
            {
                failure = InvalidReplacement(
                    $"State identity '{group.Key}' source owner '{ownerName}' does not map to exactly one Script Asset projection.",
                    candidates[0].State.Span);
                resolved = result;
                return false;
            }

            result.Add(group.Key, ownerLocations[0]);
        }

        resolved = result;
        failure = null;
        return true;
    }

    private static bool TryResolveFunctionLocations(
        IReadOnlyList<FunctionLocation> locations,
        IReadOnlyDictionary<string, RewriteContext> contexts,
        out IReadOnlyDictionary<SemanticId, FunctionLocation> resolved,
        out UnifiedDiagnostic? failure)
    {
        var result = new Dictionary<SemanticId, FunctionLocation>();
        foreach (IGrouping<SemanticId, FunctionLocation> group in locations.GroupBy(
                     static location => location.Function.Id))
        {
            FunctionLocation[] candidates = group.ToArray();
            string spanKey = SpanKey(candidates[0].Method.Span);
            if (candidates.Any(candidate =>
                    !string.Equals(SpanKey(candidate.Method.Span), spanKey, StringComparison.Ordinal)))
            {
                failure = InvalidReplacement(
                    $"Function identity '{group.Key}' resolves to multiple source declarations; the declaration owner is ambiguous.",
                    candidates[0].Method.Span);
                resolved = result;
                return false;
            }

            if (!contexts.TryGetValue(candidates[0].Method.Span.FilePath, out RewriteContext? context))
            {
                failure = MissingSyntax(group.Key, candidates[0].Method.Span, "script function source");
                resolved = result;
                return false;
            }

            MethodDeclarationSyntax[] syntax = context.Root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(method => RoslynSourceSpan.FromNode(
                    method,
                    candidates[0].Method.Span.FilePath) == candidates[0].Method.Span)
                .ToArray();
            if (syntax.Length != 1)
            {
                failure = MissingSyntax(group.Key, candidates[0].Method.Span, "script function method");
                resolved = result;
                return false;
            }

            IMethodSymbol? symbol = context.SemanticModel.GetDeclaredSymbol(syntax[0]);
            INamedTypeSymbol? containingType = symbol?.ContainingType;
            if (containingType is null)
            {
                failure = InvalidReplacement(
                    $"Function identity '{group.Key}' has no Roslyn declaration owner.",
                    candidates[0].Method.Span);
                resolved = result;
                return false;
            }

            string ownerName = GetMetadataName(containingType);
            FunctionLocation[] ownerLocations = candidates
                .Where(candidate => string.Equals(
                    candidate.Asset.QualifiedName,
                    ownerName,
                    StringComparison.Ordinal))
                .ToArray();
            if (ownerLocations.Length != 1)
            {
                failure = InvalidReplacement(
                    $"Function identity '{group.Key}' source owner '{ownerName}' does not map to exactly one Script Asset projection.",
                    candidates[0].Method.Span);
                resolved = result;
                return false;
            }

            result.Add(group.Key, ownerLocations[0]);
        }

        resolved = result;
        failure = null;
        return true;
    }

    private static string GetMetadataName(INamedTypeSymbol type) =>
        type.ToDisplayString(new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));

    private static string SpanKey(SourceSpan span) =>
        $"{span.FilePath}\u0000{span.Start.Line:D8}:{span.Start.Column:D8}\u0000" +
        $"{span.End.Line:D8}:{span.End.Column:D8}";

    private static CSharpSourceDocument GetSource(CSharpCompilationRequest request, string path) =>
        request.Sources.Single(source => string.Equals(source.LogicalPath, path, StringComparison.Ordinal));

    private static SyntaxAnnotation AssetAnnotation(SemanticId id) =>
        new(AssetAnnotationKind, id.Value);

    private static SyntaxAnnotation StateAnnotation(SemanticId id) =>
        new(StateAnnotationKind, id.Value);

    private static SyntaxAnnotation ConstructorAssignmentAnnotation(SemanticId id) =>
        new(ConstructorAssignmentAnnotationKind, id.Value);

    private static SyntaxAnnotation FunctionAnnotation(SemanticId id) =>
        new(FunctionAnnotationKind, id.Value);

    private static SyntaxAnnotation ClassBaseAnnotation(SemanticId id) =>
        new(ClassBaseAnnotationKind, id.Value);

    private static TNode GetAnnotatedNode<TNode>(
        SyntaxNode root,
        string annotationKind,
        SemanticId id)
        where TNode : SyntaxNode => root
        .GetAnnotatedNodes(annotationKind)
        .OfType<TNode>()
        .Single(node => node.GetAnnotations(annotationKind)
            .Any(annotation => string.Equals(annotation.Data, id.Value, StringComparison.Ordinal)));

    private static CSharpPatchResult TargetNotFound(SemanticId id, string target) => Failure(
        new UnifiedDiagnostic(
            DeclarationPatchDiagnosticCodes.TargetNotFound,
            UnifiedDiagnosticSeverity.Error,
            $"Declaration patch target {target} '{id}' was not found.",
            SourceSpan.Unknown("<declaration-patch>"),
            id));

    private static CSharpPatchResult PreconditionFailed(ScriptStateDeclaration state) => Failure(
        new UnifiedDiagnostic(
            DeclarationPatchDiagnosticCodes.StatePreconditionFailed,
            UnifiedDiagnosticSeverity.Error,
            $"Script state '{state.Name}' no longer matches the patch precondition.",
            state.Span,
            state.Id));

    private static CSharpPatchResult PreconditionFailed(ScriptAssetDeclaration asset) => Failure(
        new UnifiedDiagnostic(
            DeclarationPatchDiagnosticCodes.DeclarationPreconditionFailed,
            UnifiedDiagnosticSeverity.Error,
            $"Script Asset '{asset.QualifiedName}' no longer matches the patch precondition.",
            asset.Span,
            asset.Id));

    private static CSharpPatchResult PreconditionFailed(FunctionLocation location) => Failure(
        new UnifiedDiagnostic(
            DeclarationPatchDiagnosticCodes.DeclarationPreconditionFailed,
            UnifiedDiagnosticSeverity.Error,
            $"Script function '{location.Function.QualifiedName}' no longer matches the patch precondition.",
            location.Method.Span,
            location.Function.Id));

    private static UnifiedDiagnostic MissingSyntax(SemanticId id, SourceSpan span, string target) => new(
        DeclarationPatchDiagnosticCodes.TargetNotFound,
        UnifiedDiagnosticSeverity.Error,
        $"The source syntax for {target} '{id}' was not found exactly once.",
        span,
        id);

    private static UnifiedDiagnostic InvalidReplacement(string message, SourceSpan span) => new(
        DeclarationPatchDiagnosticCodes.InvalidReplacement,
        UnifiedDiagnosticSeverity.Error,
        message,
        span);

    private static UnifiedDiagnostic PostconditionFailed(SemanticId id, string contract) => new(
        DeclarationPatchDiagnosticCodes.PostconditionFailed,
        UnifiedDiagnosticSeverity.Error,
        $"Patched analysis did not contain the expected {contract}.",
        SourceSpan.Unknown("<declaration-patch>"),
        id);

    private static CSharpPatchResult Failure(params UnifiedDiagnostic[] diagnostics) =>
        new(null, null, diagnostics);

    private static CSharpPatchResult Failure(IReadOnlyList<UnifiedDiagnostic> diagnostics) =>
        new(null, null, diagnostics);

    private sealed record StateLocation(
        ScriptAssetDeclaration Asset,
        ScriptStateDeclaration State);

    private sealed record FunctionLocation(
        ScriptAssetDeclaration Asset,
        ScriptFunctionDeclaration Function,
        HirMethodDeclaration Method)
    {
        public bool IsUnrealEvent => Method.Attributes?.Any(
            static attribute => attribute.Name == "UnrealEvent") == true;

        public bool IsLifecycleHook =>
            Function.Name == "OnBeginPlay" && Function.Parameters.Count == 0 ||
            Function.Name == "OnTick" && Function.Parameters.Count == 1 &&
                Function.Parameters[0].TypeMetadataName == "System.Single" ||
            Function.Name == "OnEndPlay" && Function.Parameters.Count == 0;
    }

    private abstract record StateMutationPlan(
        ScriptAssetDeclaration Asset,
        ScriptStateDeclaration State);

    private sealed record AddPlan(
        ScriptAssetDeclaration Asset,
        ScriptStatePatchSpec State);

    private sealed record AssetAddPlan(
        CSharpSourceDocument Source,
        ScriptAssetPatchSpec Asset);

    private sealed record AssetUpdatePlan(
        ScriptAssetDeclaration Asset,
        string ReplacementAssetPath);

    private sealed record ClassBaseUpdatePlan(
        ScriptAssetDeclaration Asset,
        string? ReplacementBaseClassPath,
        RewriteContext Context,
        AttributeSyntax Attribute);

    private sealed record AssetRemovePlan(ScriptAssetDeclaration Asset);

    private sealed record EventAddPlan(
        ScriptAssetDeclaration Asset,
        UnrealEventHandlerPatchSpec Handler);

    private sealed record FunctionAddPlan(
        ScriptAssetDeclaration Asset,
        ScriptFunctionPatchSpec Function);

    private abstract record FunctionMutationPlan(FunctionLocation Location);

    private sealed record FunctionUpdatePlan(
        FunctionLocation Location,
        ScriptFunctionPatchSpec Replacement) : FunctionMutationPlan(Location);

    private sealed record FunctionRemovePlan(
        FunctionLocation Location) : FunctionMutationPlan(Location);

    private sealed record LifecycleAddPlan(
        ScriptAssetDeclaration Asset,
        string Hook);

    private sealed record UpdatePlan(
        ScriptAssetDeclaration Asset,
        ScriptStateDeclaration State,
        ScriptStatePatchSpec Replacement) : StateMutationPlan(Asset, State);

    private sealed record ConstructorDefaultLocation(
        UpdatePlan Update,
        RewriteContext Context,
        AssignmentExpressionSyntax Assignment);

    private sealed record RemovePlan(
        ScriptAssetDeclaration Asset,
        ScriptStateDeclaration State) : StateMutationPlan(Asset, State);

    private sealed class RewriteContext(
        SyntaxTree tree,
        SyntaxNode root,
        SemanticModel semanticModel)
    {
        public SyntaxTree Tree { get; } = tree;

        public SyntaxNode Root { get; set; } = root;

        public SemanticModel SemanticModel { get; } = semanticModel;
    }
}
