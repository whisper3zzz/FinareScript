using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.CSharp;

public sealed class CSharpDeclarationPatchApplier
{
    private const string AssetAnnotationKind = "UnifiedScript.DeclarationPatch.Asset";
    private const string StateAnnotationKind = "UnifiedScript.DeclarationPatch.State";
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

        var assets = original.ScriptAssets.Assets.ToDictionary(static asset => asset.Id);
        var states = original.ScriptAssets.Assets
            .SelectMany(asset => asset.States.Select(state => new StateLocation(asset, state)))
            .ToDictionary(static location => location.State.Id);
        var adds = new List<AddPlan>();
        var eventAdds = new List<EventAddPlan>();
        var updates = new List<UpdatePlan>();
        var removes = new List<RemovePlan>();
        foreach (DeclarationPatchOperation operation in patch.Operations)
        {
            switch (operation)
            {
                case AddScriptStatePatchOperation add:
                    if (!assets.TryGetValue(add.TargetAssetId, out ScriptAssetDeclaration? addAsset))
                    {
                        return TargetNotFound(add.TargetAssetId, "script asset");
                    }

                    if (!TryValidateSpec(add.State, addAsset.Span, out UnifiedDiagnostic? addFailure))
                    {
                        return Failure(addFailure!);
                    }

                    adds.Add(new AddPlan(addAsset, add.State));
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
            adds,
            eventAdds,
            updates,
            removes);
        if (finalContractFailure is not null)
        {
            return Failure(finalContractFailure);
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

        if (!TryAnnotateTargets(
                contexts,
                adds,
                eventAdds,
                updates,
                removes,
                out UnifiedDiagnostic? annotationFailure))
        {
            return Failure(annotationFailure!);
        }

        RenameReferences(contexts, updates);
        ApplyPropertyChanges(contexts, updates, removes);
        ApplyAdditions(contexts, adds);
        ApplyEventAdditions(contexts, eventAdds);

        IReadOnlyDictionary<string, IdentitySidecarDocument> effectiveSidecars = original.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        CSharpSourceDocument[] patchedSources = request.Sources
            .Select(source => CreatePatchedSource(
                source,
                contexts[source.LogicalPath].Root,
                effectiveSidecars.GetValueOrDefault(source.LogicalPath),
                updates.Where(update => update.State.Span.FilePath == source.LogicalPath).ToArray()))
            .ToArray();
        var patchedRequest = new CSharpCompilationRequest(
            request.AssemblyName,
            patchedSources,
            request.ReferencePaths,
            request.EffectCatalog,
            request.Settings,
            request.RuntimeReferencePaths,
            request.IncludeDefaultReferences);
        CSharpAnalysisResult patchedAnalysis = pipeline.Analyze(patchedRequest);
        if (!patchedAnalysis.Succeeded || patchedAnalysis.ScriptAssets is null)
        {
            return Failure(patchedAnalysis.Diagnostics);
        }

        UnifiedDiagnostic? postconditionFailure = ValidatePostconditions(
            patch,
            original.ScriptAssets,
            patchedAnalysis.ScriptAssets,
            patchedAnalysis.Hir!);
        if (postconditionFailure is not null)
        {
            return Failure(postconditionFailure);
        }

        return new CSharpPatchResult(patchedRequest, patchedAnalysis, patchedAnalysis.Diagnostics);
    }

    private static bool TryAnnotateTargets(
        IReadOnlyDictionary<string, RewriteContext> contexts,
        IReadOnlyList<AddPlan> adds,
        IReadOnlyList<EventAddPlan> eventAdds,
        IReadOnlyList<UpdatePlan> updates,
        IReadOnlyList<RemovePlan> removes,
        out UnifiedDiagnostic? failure)
    {
        failure = null;
        foreach (IGrouping<string, ScriptAssetDeclaration> sourceAssets in adds
                     .Select(static add => add.Asset)
                     .Concat(eventAdds.Select(static add => add.Asset))
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

        return true;
    }

    private static void RenameReferences(
        IReadOnlyDictionary<string, RewriteContext> contexts,
        IReadOnlyList<UpdatePlan> updates)
    {
        UpdatePlan[] renames = updates
            .Where(static update => !string.Equals(
                update.State.Name,
                update.Replacement.Name,
                StringComparison.Ordinal))
            .ToArray();
        if (renames.Length == 0)
        {
            return;
        }

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
        IReadOnlyList<RemovePlan> removes)
    {
        foreach (UpdatePlan update in updates)
        {
            RewriteContext context = contexts[update.State.Span.FilePath];
            PropertyDeclarationSyntax property = GetAnnotatedNode<PropertyDeclarationSyntax>(
                context.Root,
                StateAnnotationKind,
                update.State.Id);
            PropertyDeclarationSyntax replacement = RewriteProperty(property, update.Replacement)
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
                members = members.Insert(insertionIndex++, CreateProperty(add.State, type));
            }

            TypeDeclarationSyntax replacement = type.WithMembers(members)
                .WithAdditionalAnnotations(AssetAnnotation(first.Asset.Id));
            context.Root = context.Root.ReplaceNode(type, replacement);
        }
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
            context.Root = context.Root.ReplaceNode(type, replacement);
        }
    }

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
                "Source = global::UnifiedScript.Unreal.UnrealEventSource.Host");
        }
        if (spec.SourceMember is not null)
        {
            bindingArguments.Add(
                "SourceMember = " + SymbolDisplay.FormatLiteral(spec.SourceMember, quote: true));
        }
        string parameters = string.Join(
            ", ",
            spec.Parameters.Select(static parameter => $"{parameter.TypeName} {parameter.Name}"));
        return "[global::UnifiedScript.Visual]\n" +
            $"[global::UnifiedScript.Unreal.UnrealEvent({string.Join(", ", bindingArguments)})]\n" +
            $"private void {spec.Name}({parameters})\n{{\n}}";
    }

    private static PropertyDeclarationSyntax RewriteProperty(
        PropertyDeclarationSyntax property,
        ScriptStatePatchSpec spec)
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
            .WithInitializer(SyntaxFactory.EqualsValueClause(
                SyntaxFactory.ParseExpression(RenderDefault(spec))))
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

    private static PropertyDeclarationSyntax CreateProperty(
        ScriptStatePatchSpec spec,
        TypeDeclarationSyntax containingType)
    {
        string source = RenderProperty(spec);
        PropertyDeclarationSyntax property = SyntaxFactory.ParseMemberDeclaration(source)
            as PropertyDeclarationSyntax ??
            throw new InvalidOperationException("A validated state specification did not parse as a property.");
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

    private static string RenderProperty(ScriptStatePatchSpec spec)
    {
        string exposure = RenderExposureAttribute(spec.Exposure);
        return $"{exposure}public {spec.TypeName} {spec.Name} {{ get; set; }} = {RenderDefault(spec)};";
    }

    private static string RenderDefault(ScriptStatePatchSpec spec)
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
            return $"{spec.TypeName}.{spec.DefaultValue.GetString()}";
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

        if (exposure.Category is not null)
        {
            arguments.Add("Category = " + Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
                exposure.Category,
                quote: true));
        }

        return arguments.Count == 0
            ? string.Empty
            : $"[UnifiedScript.Expose({string.Join(", ", arguments)})] ";
    }

    private static bool IsExposeAttribute(string name) =>
        name is "Expose" or "ExposeAttribute" or "UnifiedScript.Expose" or
        "UnifiedScript.ExposeAttribute" or "global::UnifiedScript.Expose" or
        "global::UnifiedScript.ExposeAttribute";

    private static CSharpSourceDocument CreatePatchedSource(
        CSharpSourceDocument source,
        SyntaxNode patchedRoot,
        IdentitySidecarDocument? effectiveSidecar,
        IReadOnlyList<UpdatePlan> updates)
    {
        string patchedText = patchedRoot.ToFullString();
        if (effectiveSidecar is null)
        {
            return new CSharpSourceDocument(source.LogicalPath, patchedText);
        }

        CSharpIdentityPin[] pins = updates.Select(update =>
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
        return new CSharpSourceDocument(
            source.LogicalPath,
            patchedText,
            effectiveSidecar,
            pins);
    }

    private static UnifiedDiagnostic? ValidatePostconditions(
        DeclarationPatchDocument patch,
        ScriptAssetDocument before,
        ScriptAssetDocument after,
        HirCompilationUnit afterHir)
    {
        var beforeAssets = before.Assets.ToDictionary(static asset => asset.Id);
        var afterAssets = after.Assets.ToDictionary(static asset => asset.Id);
        var afterStates = after.Assets
            .SelectMany(static asset => asset.States)
            .ToDictionary(static state => state.Id);
        foreach (DeclarationPatchOperation operation in patch.Operations)
        {
            switch (operation)
            {
                case AddScriptStatePatchOperation add:
                    if (!afterAssets.TryGetValue(add.TargetAssetId, out ScriptAssetDeclaration? asset) ||
                        asset.States.Count(state => Matches(state, add.State)) != 1)
                    {
                        return PostconditionFailed(add.TargetAssetId, "added state contract");
                    }

                    break;
                case UpdateScriptStatePatchOperation update:
                    if (!afterStates.TryGetValue(update.TargetStateId, out ScriptStateDeclaration? state) ||
                        !Matches(state, update.Replacement))
                    {
                        return PostconditionFailed(update.TargetStateId, "updated state contract and identity");
                    }

                    break;
                case RemoveScriptStatePatchOperation remove:
                    if (afterStates.ContainsKey(remove.TargetStateId) ||
                        beforeAssets.Values.All(asset => asset.States.All(state => state.Id != remove.TargetStateId)))
                    {
                        return PostconditionFailed(remove.TargetStateId, "state removal");
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
                state.TypeMetadataName.EndsWith('.' + sourceName, StringComparison.Ordinal));
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
            !SyntaxFacts.IsValidIdentifier(spec.DefaultValue.GetString()!))
        {
            failure = InvalidReplacement("The enum default is not a valid member name.", span);
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
        IReadOnlyList<AddPlan> adds,
        IReadOnlyList<EventAddPlan> eventAdds,
        IReadOnlyList<UpdatePlan> updates,
        IReadOnlyList<RemovePlan> removes)
    {
        IReadOnlyDictionary<SemanticId, UpdatePlan> updatesById = updates.ToDictionary(
            static update => update.State.Id);
        IReadOnlySet<SemanticId> removedIds = removes
            .Select(static remove => remove.State.Id)
            .ToHashSet();
        foreach (ScriptAssetDeclaration asset in document.Assets)
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
            string? duplicateFunction = asset.Functions.Select(static function => function.Name)
                .Concat(eventAdds
                    .Where(add => add.Asset.Id == asset.Id)
                    .Select(static add => add.Handler.Name))
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

    private static CSharpSourceDocument GetSource(CSharpCompilationRequest request, string path) =>
        request.Sources.Single(source => string.Equals(source.LogicalPath, path, StringComparison.Ordinal));

    private static SyntaxAnnotation AssetAnnotation(SemanticId id) =>
        new(AssetAnnotationKind, id.Value);

    private static SyntaxAnnotation StateAnnotation(SemanticId id) =>
        new(StateAnnotationKind, id.Value);

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

    private abstract record StateMutationPlan(
        ScriptAssetDeclaration Asset,
        ScriptStateDeclaration State);

    private sealed record AddPlan(
        ScriptAssetDeclaration Asset,
        ScriptStatePatchSpec State);

    private sealed record EventAddPlan(
        ScriptAssetDeclaration Asset,
        UnrealEventHandlerPatchSpec Handler);

    private sealed record UpdatePlan(
        ScriptAssetDeclaration Asset,
        ScriptStateDeclaration State,
        ScriptStatePatchSpec Replacement) : StateMutationPlan(Asset, State);

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
