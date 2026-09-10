using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Finare.Core;
using Finare.Core.Hir;
using Finare.Semantics;

namespace Finare.CSharp;

internal sealed record ScriptAssetLoweringResult(
    ScriptAssetDocument? Document,
    IReadOnlyDictionary<SemanticId, SemanticId> DeclarationReferences,
    IReadOnlyDictionary<SemanticId, SemanticId> DeclarationOwners,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics);

internal sealed class CSharpScriptAssetLowerer
{
    private const string ScriptAssetAttributeName = "Finare.ScriptAssetAttribute";
    private const string ExposeAttributeName = "Finare.ExposeAttribute";
    private const string UnrealEnumAttributeName = "Finare.Unreal.UnrealEnumAttribute";
    private const string UnrealEnumConstantAttributeName =
        "Finare.Unreal.UnrealEnumConstantAttribute";
    private const string UnrealStructAttributeName = "Finare.Unreal.UnrealStructAttribute";
    private const string UnrealStructFieldAttributeName =
        "Finare.Unreal.UnrealStructFieldAttribute";
    private const string UnrealScriptClassAttributeName =
        "Finare.Unreal.UnrealScriptClassAttribute";
    private const string UnrealBlueprintEventAttributeName =
        "Finare.Unreal.UnrealBlueprintEventAttribute";
    private const string UnrealBlueprintOverrideAttributeName =
        "Finare.Unreal.UnrealBlueprintOverrideAttribute";
    private const string UnrealRpcAttributeName = "Finare.Unreal.UnrealRpcAttribute";
    private const string UnrealRepNotifyAttributeName = "Finare.Unreal.UnrealRepNotifyAttribute";
    private const string UnrealInputActionAttributeName =
        "Finare.Unreal.UnrealInputActionAttribute";
    private const string UnrealInterfaceFunctionAttributeName =
        "Finare.Unreal.UnrealInterfaceFunctionAttribute";
    private const string UnrealEventAttributeName =
        "Finare.Unreal.UnrealEventAttribute";
    private const string UnrealTypeAttributeName =
        "Finare.Unreal.UnrealTypeAttribute";

    private static readonly SymbolDisplayFormat TypeMetadataFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public ScriptAssetLoweringResult Lower(
        CSharpCompilation compilation,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        HirCompilationUnit hir,
        BoundCompilation semantics,
        IReadOnlyDictionary<SemanticId, SemanticId> declarationIdentities)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        ArgumentNullException.ThrowIfNull(hir);
        ArgumentNullException.ThrowIfNull(semantics);

        var diagnostics = new List<UnifiedDiagnostic>();
        // A partial type is lowered once per syntax tree, so one qualified
        // name can legitimately have several HIR shards. Keep every shard;
        // collapsing them with ToDictionary either throws or loses methods
        // from whichever file happened to be discarded.
        var hirTypes = hir.Types
            .GroupBy(static type => type.QualifiedName, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<HirTypeDeclaration>)group.ToArray(),
                StringComparer.Ordinal);
        var semanticTypes = semantics.Types.ToDictionary(static type => type.Id);
        var semanticMethods = semantics.Methods.ToDictionary(static method => method.Id);
        var declarations = new List<ScriptAssetDeclaration>();
        var declarationReferences = new Dictionary<SemanticId, SemanticId>();
        var declarationOwnerNames = new Dictionary<SemanticId, string>();
        var visitedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (SyntaxTree syntaxTree in syntaxTrees)
        {
            SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: false);
            foreach (TypeDeclarationSyntax typeSyntax in syntaxTree.GetRoot()
                         .DescendantNodes()
                         .OfType<TypeDeclarationSyntax>())
            {
                INamedTypeSymbol? typeSymbol = semanticModel.GetDeclaredSymbol(typeSyntax);
                if (typeSymbol is null || !visitedTypes.Add(typeSymbol))
                {
                    continue;
                }

                string qualifiedName = GetMetadataName(typeSymbol);
                AttributeData? assetAttribute = FindAttribute(typeSymbol, ScriptAssetAttributeName);
                bool hasExposedMember = typeSymbol.GetMembers()
                    .Any(static member => FindAttribute(member, ExposeAttributeName) is not null);
                hirTypes.TryGetValue(qualifiedName, out IReadOnlyList<HirTypeDeclaration>? hirTypeShards);
                HirTypeDeclaration? firstHirType = hirTypeShards?.FirstOrDefault();
                if (assetAttribute is null && hirTypeShards is null && !hasExposedMember)
                {
                    continue;
                }

                string assetPath = assetAttribute is null
                    ? qualifiedName.Replace('.', '/')
                    : assetAttribute.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty;
                SourceSpan typeSpan = RoslynSourceSpan.FromNode(typeSyntax, syntaxTree.FilePath);
                if (!IsValidAssetPath(assetPath))
                {
                    diagnostics.Add(new UnifiedDiagnostic(
                        DeclarationDiagnosticCodes.InvalidAssetPath,
                        UnifiedDiagnosticSeverity.Error,
                        $"Script asset '{qualifiedName}' requires a non-empty relative path without empty, '.' or '..' segments.",
                        typeSpan));
                    continue;
                }

                IReadOnlyList<ScriptStateDeclaration> states = LowerStates(
                    typeSymbol,
                    compilation,
                    declarationIdentities,
                    declarationReferences,
                    declarationOwnerNames,
                    diagnostics);
                IReadOnlyList<ScriptFunctionDeclaration> functions = LowerFunctions(
                    compilation,
                    typeSymbol,
                    qualifiedName,
                    hirTypeShards?.Sum(static shard => shard.Methods.Count) ?? 0,
                    hirTypes,
                    semanticTypes,
                    semanticMethods,
                    declarationOwnerNames,
                    diagnostics);
                IReadOnlyList<ScriptLifecycleHookDeclaration> lifecycleHooks =
                    LowerLifecycleHooks(typeSymbol);
                ScriptClassBindingDeclaration? classBinding = null;
                AttributeData? scriptClassAttribute = FindAttribute(typeSymbol, UnrealScriptClassAttributeName);
                if (scriptClassAttribute is not null &&
                    !TryLowerClassBinding(
                        scriptClassAttribute,
                        qualifiedName,
                        typeSpan,
                        diagnostics,
                        out classBinding))
                {
                    continue;
                }
                SemanticId assetId = firstHirType?.Id ?? CSharpSemanticIds.ForSymbol("type", qualifiedName);
                if (assetAttribute is not null &&
                    TryReadNamedString(
                        assetAttribute,
                        nameof(global::Finare.ScriptAssetAttribute.ClassId),
                        out string? classId))
                {
                    if (!SemanticId.TryParse(classId, out assetId))
                    {
                        diagnostics.Add(new UnifiedDiagnostic(
                            DeclarationDiagnosticCodes.InvalidScriptClassId,
                            UnifiedDiagnosticSeverity.Error,
                            $"Script asset '{qualifiedName}' has invalid persistent class ID '{classId}'. Expected 'us_' followed by 32 lowercase hexadecimal digits.",
                            typeSpan));
                        continue;
                    }
                }
                declarations.Add(new ScriptAssetDeclaration(
                    assetId,
                    qualifiedName,
                    assetPath,
                    typeSpan,
                    states,
                    functions,
                    lifecycleHooks,
                    classBinding));
            }
        }

        foreach (IGrouping<string, ScriptAssetDeclaration> duplicate in declarations
                     .GroupBy(static asset => asset.AssetPath, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1))
        {
            foreach (ScriptAssetDeclaration asset in duplicate)
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.DuplicateAssetPath,
                    UnifiedDiagnosticSeverity.Error,
                    $"Script asset path '{duplicate.Key}' is also used by another source type.",
                    asset.Span,
                    asset.Id));
            }
        }

        foreach (IGrouping<SemanticId, ScriptAssetDeclaration> duplicate in declarations
                     .GroupBy(static asset => asset.Id)
                     .Where(static group => group.Count() > 1))
        {
            foreach (ScriptAssetDeclaration asset in duplicate)
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.DuplicateScriptClassId,
                    UnifiedDiagnosticSeverity.Error,
                    $"Script class ID '{duplicate.Key}' is also used by another source type.",
                    asset.Span,
                    asset.Id));
            }
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error))
        {
            return new ScriptAssetLoweringResult(null, declarationReferences, new Dictionary<SemanticId, SemanticId>(), diagnostics);
        }

        var assetsByQualifiedName = declarations.ToDictionary(
            static asset => asset.QualifiedName,
            StringComparer.Ordinal);
        var declarationOwners = new Dictionary<SemanticId, SemanticId>();
        foreach ((SemanticId declarationId, string ownerName) in declarationOwnerNames)
        {
            if (!assetsByQualifiedName.TryGetValue(ownerName, out ScriptAssetDeclaration? owner))
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.UnsupportedStateShape,
                    UnifiedDiagnosticSeverity.Error,
                    $"Declaration '{declarationId}' is owned by source type '{ownerName}', but that type has no Script Asset projection.",
                    hir.Span,
                    declarationId));
                continue;
            }

            declarationOwners[declarationId] = owner.Id;
        }
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error))
        {
            return new ScriptAssetLoweringResult(null, declarationReferences, new Dictionary<SemanticId, SemanticId>(), diagnostics);
        }

        try
        {
            ScriptAssetDocument document = ScriptAssetJson.NormalizeAndValidate(ScriptAssetDocument.Create(
                hir.Id, semantics.ConfigurationId, declarations));
            return new ScriptAssetLoweringResult(document, declarationReferences, declarationOwners, diagnostics);
        }
        catch (JsonException exception) when (declarations.Any(a => a.Functions.Any(f => f.Rpc is not null || f.RepNotifyProperty is not null) ||
            a.States.Any(s => s.Exposure.ReplicationCondition != global::Finare.ReplicationCondition.None)))
        {
            diagnostics.Add(new UnifiedDiagnostic(DeclarationDiagnosticCodes.InvalidNetworkingContract,
                UnifiedDiagnosticSeverity.Error, exception.Message, hir.Span));
            return new ScriptAssetLoweringResult(null, declarationReferences, new Dictionary<SemanticId, SemanticId>(), diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new UnifiedDiagnostic(
                DeclarationDiagnosticCodes.StateDefaultRequired,
                UnifiedDiagnosticSeverity.Error,
                exception.Message,
                hir.Span));
            return new ScriptAssetLoweringResult(null, declarationReferences, new Dictionary<SemanticId, SemanticId>(), diagnostics);
        }
    }

    private static bool TryReadNamedString(
        AttributeData attribute,
        string name,
        out string? value)
    {
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (string.Equals(argument.Key, name, StringComparison.Ordinal))
            {
                value = argument.Value.Value as string;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryLowerClassBinding(
        AttributeData attribute,
        string qualifiedName,
        SourceSpan span,
        ICollection<UnifiedDiagnostic> diagnostics,
        out ScriptClassBindingDeclaration? binding)
    {
        binding = null;
        int? kindValue = attribute.ConstructorArguments.FirstOrDefault().Value switch
        {
            int value => value,
            byte value => value,
            short value => value,
            long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
            _ => null,
        };
        string? kind = kindValue switch
        {
            1 => "Actor",
            2 => "ActorComponent",
            3 => "Object",
            4 => "BlueprintFunctionLibrary",
            _ => null,
        };
        if (kind is null)
        {
            AddClassBindingDiagnostic(
                diagnostics,
                qualifiedName,
                span,
                $"has unsupported UnrealScriptClassKind value '{kindValue?.ToString() ?? "<missing>"}'.");
            return false;
        }

        if (!TryReadClassBindingPath(attribute, "BaseClassPath",
                qualifiedName, span, diagnostics, out string? baseClassPath) ||
            !TryReadClassBindingPath(attribute, "GeneratedClassPath",
                qualifiedName, span, diagnostics, out string? generatedClassPath))
        {
            return false;
        }

        binding = new ScriptClassBindingDeclaration(kind, baseClassPath, generatedClassPath);
        return true;
    }

    private static bool TryReadClassBindingPath(
        AttributeData attribute,
        string name,
        string qualifiedName,
        SourceSpan span,
        ICollection<UnifiedDiagnostic> diagnostics,
        out string? value)
    {
        value = null;
        TypedConstant? constant = attribute.NamedArguments
            .Where(argument => string.Equals(argument.Key, name, StringComparison.Ordinal))
            .Select(static argument => (TypedConstant?)argument.Value)
            .SingleOrDefault();
        if (constant is null || constant.Value.Value is null)
        {
            return true;
        }

        if (constant.Value.Kind != TypedConstantKind.Primitive || constant.Value.Value is not string text)
        {
            AddClassBindingDiagnostic(
                diagnostics,
                qualifiedName,
                span,
                $"has a non-string {name}.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(text) || !IsValidUnrealClassBindingPath(text))
        {
            AddClassBindingDiagnostic(
                diagnostics,
                qualifiedName,
                span,
                $"has an invalid {name} '{text}'.");
            return false;
        }

        value = text;
        return true;
    }

    private static void AddClassBindingDiagnostic(
        ICollection<UnifiedDiagnostic> diagnostics,
        string qualifiedName,
        SourceSpan span,
        string detail) => diagnostics.Add(new UnifiedDiagnostic(
            DeclarationDiagnosticCodes.InvalidScriptClassBinding,
            UnifiedDiagnosticSeverity.Error,
            $"Script asset '{qualifiedName}' {detail}",
            span));

    private static bool IsValidUnrealClassBindingPath(string? path)
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

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(static character => char.IsLetterOrDigit(character) || character == '_');

    private static void RecordDeclarationOwner(
        IDictionary<SemanticId, string> ownerNames,
        SemanticId declarationId,
        string ownerName,
        SourceSpan span,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        if (ownerNames.TryGetValue(declarationId, out string? existingOwner) &&
            !string.Equals(existingOwner, ownerName, StringComparison.Ordinal))
        {
            diagnostics.Add(new UnifiedDiagnostic(
                DeclarationDiagnosticCodes.UnsupportedStateShape,
                UnifiedDiagnosticSeverity.Error,
                $"Declaration '{declarationId}' has conflicting source owners '{existingOwner}' and '{ownerName}'.",
                span,
                declarationId));
            return;
        }

        ownerNames[declarationId] = ownerName;
    }

    private static IReadOnlyList<ScriptStateDeclaration> LowerStates(
        INamedTypeSymbol type,
        CSharpCompilation compilation,
        IReadOnlyDictionary<SemanticId, SemanticId> declarationIdentities,
        IDictionary<SemanticId, SemanticId> declarationReferences,
        IDictionary<SemanticId, string> declarationOwnerNames,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        var states = new List<ScriptStateDeclaration>();
        StringComparer stateNameComparer = FindAttribute(type, UnrealScriptClassAttributeName) is not null
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var selectedProperties = new Dictionary<string, (INamedTypeSymbol Owner, IPropertySymbol Property, AttributeData? Exposure)>(stateNameComparer);
        foreach (INamedTypeSymbol owner in EnumerateTypeHierarchy(type))
        {
            bool ownsAllProperties = FindAttribute(owner, ScriptAssetAttributeName) is not null;
            foreach (ISymbol member in owner.GetMembers()
                         .OrderBy(static member => member.MetadataName, StringComparer.Ordinal))
            {
                IPropertySymbol? property = member as IPropertySymbol;
                AttributeData? exposureAttribute = FindAttribute(member, ExposeAttributeName);
                bool isCandidate =
                    (property is not null && !property.IsStatic && ownsAllProperties) ||
                    exposureAttribute is not null;
                if (!isCandidate)
                {
                    if (property is not null && !property.IsStatic && selectedProperties.ContainsKey(property.Name))
                    {
                        diagnostics.Add(new UnifiedDiagnostic(
                            DeclarationDiagnosticCodes.UnsupportedStateShape,
                            UnifiedDiagnosticSeverity.Error,
                            $"Script state property '{property.Name}' on '{GetMetadataName(owner)}' hides an inherited state property; use the inherited property instead of declaring a second state owner.",
                            GetMemberSpan(member, type),
                            CSharpSemanticIds.ForRoslynSymbol(property)));
                    }
                    continue;
                }

                if (property is null || property.IsStatic)
                {
                    diagnostics.Add(new UnifiedDiagnostic(
                        DeclarationDiagnosticCodes.UnsupportedStateShape,
                        UnifiedDiagnosticSeverity.Error,
                        $"Script state '{member.Name}' must be a non-static, non-indexed auto-property with get and set accessors.",
                        GetMemberSpan(member, type),
                        CSharpSemanticIds.ForRoslynSymbol(member)));
                    continue;
                }

                if (selectedProperties.ContainsKey(property.Name))
                {
                    diagnostics.Add(new UnifiedDiagnostic(
                        DeclarationDiagnosticCodes.UnsupportedStateShape,
                        UnifiedDiagnosticSeverity.Error,
                        $"Script state property '{property.Name}' on '{GetMetadataName(owner)}' conflicts with an inherited state property. Hiding or overriding a projected state is not supported.",
                        GetMemberSpan(member, type),
                        CSharpSemanticIds.ForRoslynSymbol(property)));
                    continue;
                }

                selectedProperties[property.Name] = (owner, property, exposureAttribute);
            }
        }

        foreach ((INamedTypeSymbol owner, IPropertySymbol property, AttributeData? exposureAttribute) in selectedProperties.Values)
        {
            if (property.IsIndexer ||
                !TryGetAutoPropertySyntax(property, out PropertyDeclarationSyntax? propertySyntax) ||
                property.GetMethod is null || property.SetMethod is null)
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.UnsupportedStateShape,
                    UnifiedDiagnosticSeverity.Error,
                    $"Script state '{property.Name}' must be a non-static, non-indexed auto-property with get and set accessors.",
                    GetPropertySpan(property, type),
                    CSharpSemanticIds.ForRoslynSymbol(property)));
                continue;
            }

            PropertyDeclarationSyntax stateSyntax = propertySyntax!;

            if (!TryClassifyType(
                    compilation,
                    property.Type,
                    out ScriptStateTypeKind typeKind,
                    out ScriptFunctionValueTypeDeclaration? unrealType))
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.UnsupportedStateType,
                    UnifiedDiagnosticSeverity.Error,
                    $"Script state '{property.Name}' uses '{property.Type.ToDisplayString()}', but the reviewed Blueprint state matrix supports scalar, enum, FName, FText, UObject/UClass handles, and FVector values.",
                    RoslynSourceSpan.FromNode(stateSyntax.Type, stateSyntax.SyntaxTree.FilePath),
                    CSharpSemanticIds.ForRoslynSymbol(property)));
                continue;
            }

            if (!TryResolveProjectedStateDefault(
                owner,
                property,
                stateSyntax,
                type,
                compilation,
                out JsonElement defaultValue,
                out SyntaxNode defaultDiagnosticNode,
                out string defaultDiagnosticMessage))
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.StateDefaultRequired,
                    UnifiedDiagnosticSeverity.Error,
                    $"Script state '{property.Name}' {defaultDiagnosticMessage}",
                    RoslynSourceSpan.FromNode(
                        defaultDiagnosticNode,
                        defaultDiagnosticNode.SyntaxTree.FilePath),
                    CSharpSemanticIds.ForRoslynSymbol(property)));
                continue;
            }

            string containingName = GetMetadataName(owner);
            SemanticId deterministicId = CSharpSemanticIds.ForSymbol(
                "state",
                $"{containingName}.{property.MetadataName}");
            ScriptExposure exposure = GetExposure(exposureAttribute);
            if (exposure.Category is not null && string.IsNullOrWhiteSpace(exposure.Category))
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.InvalidExposureMetadata,
                    UnifiedDiagnosticSeverity.Error,
                    $"Script state '{property.Name}' exposure category cannot be blank.",
                    RoslynSourceSpan.FromNode(stateSyntax, stateSyntax.SyntaxTree.FilePath),
                    deterministicId));
                continue;
            }

            SemanticId declarationId = declarationIdentities.GetValueOrDefault(
                deterministicId,
                deterministicId);
            SemanticId propertyReference = CSharpSemanticIds.ForRoslynSymbol(property);
            if (declarationReferences.TryGetValue(propertyReference, out SemanticId existingDeclarationId) &&
                existingDeclarationId != declarationId)
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.UnsupportedStateShape,
                    UnifiedDiagnosticSeverity.Error,
                    $"Script state '{property.Name}' has conflicting persistent declaration identities '{existingDeclarationId}' and '{declarationId}'.",
                    RoslynSourceSpan.FromNode(stateSyntax, stateSyntax.SyntaxTree.FilePath),
                    propertyReference));
                continue;
            }
            declarationReferences[propertyReference] = declarationId;
            RecordDeclarationOwner(
                declarationOwnerNames,
                declarationId,
                containingName,
                RoslynSourceSpan.FromNode(stateSyntax, stateSyntax.SyntaxTree.FilePath),
                diagnostics);
            states.Add(new ScriptStateDeclaration(
                declarationId,
                property.Name,
                property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                property.Type.ToDisplayString(TypeMetadataFormat),
                typeKind,
                defaultValue,
                RoslynSourceSpan.FromNode(stateSyntax, stateSyntax.SyntaxTree.FilePath),
                exposure,
                unrealType));
        }

        return states;
    }

    private static IReadOnlyList<ScriptFunctionDeclaration> LowerFunctions(
        CSharpCompilation compilation,
        INamedTypeSymbol typeSymbol,
        string projectionQualifiedName,
        int initialCapacity,
        IReadOnlyDictionary<string, IReadOnlyList<HirTypeDeclaration>> hirTypes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods,
        IDictionary<SemanticId, string> declarationOwnerNames,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        var declarations = new List<HirMethodDeclaration>(initialCapacity);
        var declarationOwnerNamesByMethod = new Dictionary<SemanticId, string>();
        var shadowedNames = new HashSet<string>(StringComparer.Ordinal);
        INamedTypeSymbol? currentSymbol = typeSymbol;
        bool inherited = false;
        while (currentSymbol is not null)
        {
            string currentName = GetMetadataName(currentSymbol);
            if (hirTypes.TryGetValue(currentName, out IReadOnlyList<HirTypeDeclaration>? currentTypeShards))
            {
                foreach (HirMethodDeclaration method in currentTypeShards.SelectMany(static shard => shard.Methods))
                {
                    if (!inherited ||
                        (method.IsPublic && !method.IsStatic && !shadowedNames.Contains(method.Name)))
                    {
                        declarations.Add(method);
                        declarationOwnerNamesByMethod[method.Id] = currentName;
                    }
                }
            }

            foreach (IMethodSymbol declaredMethod in currentSymbol.GetMembers().OfType<IMethodSymbol>())
            {
                shadowedNames.Add(declaredMethod.Name);
            }
            currentSymbol = currentSymbol.BaseType;
            inherited = true;
        }

        var functions = new List<ScriptFunctionDeclaration>(declarations.Count);
        var blueprintOverrideRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HirMethodDeclaration method in declarations)
        {
            MethodDescriptor descriptor = methods[method.Id];
            string[] parameterTypeNames = descriptor.Parameters
                .Select(parameter => types[parameter.TypeId].MetadataName)
                .ToArray();
            IMethodSymbol? methodSymbol = FindMethodSymbol(
                typeSymbol,
                method,
                parameterTypeNames);
            if (methodSymbol is { ReturnsByRef: true } or { ReturnsByRefReadonly: true } ||
                methodSymbol?.Parameters.Any(static parameter => parameter.RefKind == RefKind.In) == true)
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.UnsupportedFunctionSignature,
                    UnifiedDiagnosticSeverity.Error,
                    $"Blueprint-callable function '{projectionQualifiedName}.{method.Name}' cannot use " +
                    "a ref return or C# in parameter. Use a value return and input, out, or ref parameters.",
                    method.Span,
                    method.Id));
                continue;
            }
            string? unsupportedShapeReason = null;
            if (methodSymbol is not null)
            {
                bool hasUnsupportedShape = HasUnsupportedFunctionContainerShape(
                    compilation,
                    methodSymbol.ReturnType,
                    allowVoid: true,
                    out unsupportedShapeReason);
                if (!hasUnsupportedShape)
                {
                    // Keep the parameter failure reason too; using Any(out _)
                    // here would erase the contract detail for parameter-only
                    // failures.
                    foreach (IParameterSymbol parameter in methodSymbol.Parameters)
                    {
                        if (HasUnsupportedFunctionContainerShape(
                                compilation,
                                parameter.Type,
                                allowVoid: false,
                                out unsupportedShapeReason))
                        {
                            hasUnsupportedShape = true;
                            break;
                        }
                    }
                }
            }
            if (unsupportedShapeReason is not null)
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.UnsupportedFunctionSignature,
                    UnifiedDiagnosticSeverity.Error,
                    $"Blueprint-callable function '{projectionQualifiedName}.{method.Name}' has an unsupported " +
                    $"container value shape: {unsupportedShapeReason}",
                    method.Span,
                    method.Id));
                continue;
            }
            ScriptParameterDeclaration[] parameters = method.Parameters
                .Select((parameter, index) => new ScriptParameterDeclaration(
                    parameter.Id,
                    parameter.Name,
                    parameterTypeNames[index],
                    LowerFunctionValueType(
                        compilation,
                        methodSymbol?.Parameters[index].Type ??
                            compilation.GetTypeByMetadataName(parameterTypeNames[index])),
                    parameter.Direction switch
                    {
                        HirParameterDirection.Output => ScriptParameterDirection.Output,
                        HirParameterDirection.InputOutput => ScriptParameterDirection.InputOutput,
                        _ => ScriptParameterDirection.Input,
                    }))
                .ToArray();
            string signature = $"{projectionQualifiedName}.{method.Name}({string.Join(',',
                parameterTypeNames.Select((parameterType, index) =>
                    method.Parameters[index].Direction switch
                    {
                        HirParameterDirection.Output => $"out {parameterType}",
                        HirParameterDirection.InputOutput => $"ref {parameterType}",
                        _ => parameterType,
                    }))})";
            IReadOnlyList<ScriptInputActionBindingDeclaration> inputActions =
                LowerInputActions(method, methodSymbol, parameterTypeNames, diagnostics);
            IReadOnlyList<ScriptDelegateBindingDeclaration> delegateBindings =
                LowerDelegateBindings(method, methodSymbol, diagnostics);
            IReadOnlyList<ScriptInterfaceFunctionDeclaration> interfaceFunctions =
                LowerInterfaceFunctions(method, methodSymbol, diagnostics);
            AttributeData? blueprintEventAttribute = methodSymbol is null
                ? null
                : FindAttribute(methodSymbol, UnrealBlueprintEventAttributeName);
            bool blueprintEvent = blueprintEventAttribute is not null ||
                method.Attributes?.Any(static attribute =>
                    attribute.Name == "UnrealBlueprintEvent") == true;
            AttributeData? blueprintOverrideAttribute = methodSymbol is null
                ? null
                : FindAttribute(methodSymbol, UnrealBlueprintOverrideAttributeName);
            AttributeData? rpcAttribute = methodSymbol is null ? null : FindAttribute(methodSymbol, UnrealRpcAttributeName);
            AttributeData? notifyAttribute = methodSymbol is null ? null : FindAttribute(methodSymbol, UnrealRepNotifyAttributeName);
            ScriptRpcDeclaration? rpc = rpcAttribute is null ? null : new(
                (ScriptRpcTarget)(int)(rpcAttribute.ConstructorArguments[0].Value ?? 0),
                rpcAttribute.NamedArguments.Any(a => a.Key == "Reliable" && a.Value.Value is true));
            string? repNotifyProperty = notifyAttribute?.ConstructorArguments[0].Value as string;
            ScriptFunctionOverrideDeclaration? blueprintOverride = null;
            ScriptBlueprintEventSuperMode blueprintEventSuperMode =
                ScriptBlueprintEventSuperMode.Explicit;
            if (blueprintOverrideAttribute is not null)
            {
                string declaringClassPath = blueprintOverrideAttribute.ConstructorArguments
                    .ElementAtOrDefault(0).Value as string ?? string.Empty;
                string functionName = blueprintOverrideAttribute.ConstructorArguments
                    .ElementAtOrDefault(1).Value as string ?? string.Empty;
                int superModeValue = ReadNamedEnumValue(
                    blueprintOverrideAttribute,
                    "SuperMode",
                    1);
                if (superModeValue == 1)
                {
                    blueprintEventSuperMode = ScriptBlueprintEventSuperMode.Explicit;
                }
                if (!method.IsPublic || method.IsStatic ||
                    !ScriptAssetJson.IsValidBlueprintOverrideClassPath(declaringClassPath) ||
                    !ScriptAssetJson.IsValidBlueprintOverrideFunctionName(functionName) ||
                    superModeValue != 1)
                {
                    diagnostics.Add(new UnifiedDiagnostic(
                        DeclarationDiagnosticCodes.InvalidBlueprintOverride,
                        UnifiedDiagnosticSeverity.Error,
                        $"Blueprint override '{projectionQualifiedName}.{method.Name}' requires a public instance method, an exact declaring class path, a valid Unreal function identifier, and only SuperMode=Explicit.",
                        method.Span,
                        method.Id));
                }
                else if (blueprintEvent || inputActions.Count != 0 || delegateBindings.Count != 0 ||
                    interfaceFunctions.Count != 0 || rpcAttribute is not null || notifyAttribute is not null)
                {
                    diagnostics.Add(new UnifiedDiagnostic(
                        DeclarationDiagnosticCodes.InvalidBlueprintOverride,
                        UnifiedDiagnosticSeverity.Error,
                        $"Blueprint override '{projectionQualifiedName}.{method.Name}' cannot be combined with BlueprintEvent, InputAction, Delegate, Interface, Rpc, or RepNotify bindings.",
                        method.Span,
                        method.Id));
                }
                else
                {
                    string route = $"{declaringClassPath}\u001f{functionName}";
                    if (!blueprintOverrideRoutes.Add(route))
                    {
                        diagnostics.Add(new UnifiedDiagnostic(
                            DeclarationDiagnosticCodes.InvalidBlueprintOverride,
                            UnifiedDiagnosticSeverity.Error,
                            $"Script asset '{projectionQualifiedName}' declares duplicate Blueprint override route '{declaringClassPath}:{functionName}' (case-insensitive) on method '{projectionQualifiedName}.{method.Name}'.",
                            method.Span,
                            method.Id));
                    }
                    else
                    {
                        blueprintOverride = new ScriptFunctionOverrideDeclaration(
                            declaringClassPath,
                            functionName);
                    }
                }
            }
            if (blueprintOverrideAttribute is not null)
            {
                blueprintEvent = true;
            }
            if (notifyAttribute is not null && string.IsNullOrWhiteSpace(repNotifyProperty))
                diagnostics.Add(new UnifiedDiagnostic(DeclarationDiagnosticCodes.InvalidNetworkingContract,
                    UnifiedDiagnosticSeverity.Error, "RepNotify requires an exact replicated property name.", method.Span, method.Id));
            if (rpcAttribute is not null || notifyAttribute is not null)
            {
                AttributeData? scriptClass = FindAttribute(typeSymbol, UnrealScriptClassAttributeName);
                if (scriptClass?.ConstructorArguments[0].Value is not int kind || kind is not (1 or 2))
                    diagnostics.Add(new UnifiedDiagnostic(DeclarationDiagnosticCodes.InvalidNetworkingContract,
                        UnifiedDiagnosticSeverity.Error, "RPC and RepNotify require an Actor or ActorComponent Script Class.", method.Span, method.Id));
            }
            if (rpc is not null && (parameters.Length > 4 || parameters.Any(p =>
                p.UnrealType?.Kind is ScriptFunctionValueTypeKind.Array or ScriptFunctionValueTypeKind.Set or ScriptFunctionValueTypeKind.Map ||
                p.TypeMetadataName.StartsWith("Finare.Unreal.UnrealSoft", StringComparison.Ordinal) ||
                p.UnrealType?.Kind == ScriptFunctionValueTypeKind.Struct &&
                    p.UnrealType.UnrealTypePath is not ("/Script/CoreUObject.Vector" or "/Script/CoreUObject.Rotator" or "/Script/CoreUObject.Transform"))))
                diagnostics.Add(new UnifiedDiagnostic(DeclarationDiagnosticCodes.InvalidNetworkingContract,
                    UnifiedDiagnosticSeverity.Error,
                    "RPC sends currently support 0-4 input values: scalars, strings/names/text, enums, object/class handles, Vector/Rotator/Transform. Collections, Soft References and complex structures are not supported RPC payloads yet.",
                    method.Span, method.Id));
            functions.Add(new ScriptFunctionDeclaration(
                method.Id,
                method.Name,
                signature,
                types[descriptor.ReturnTypeId].MetadataName,
                method.IsStatic,
                method.IsPublic,
                delegateBindings.Count != 0 || method.Attributes?.Any(static attribute =>
                    attribute.Name == "UnrealEvent") == true,
                parameters,
                LowerFunctionValueType(
                    compilation,
                    methodSymbol?.ReturnType ?? compilation.GetTypeByMetadataName(
                        types[descriptor.ReturnTypeId].MetadataName)),
                blueprintEvent
                    ? new ScriptBlueprintEventDeclaration(
                        blueprintEventSuperMode,
                        blueprintOverride)
                    : null,
                inputActions,
                delegateBindings,
                interfaceFunctions,
                rpc,
                repNotifyProperty));
            RecordDeclarationOwner(
                declarationOwnerNames,
                method.Id,
                methodSymbol is null
                    ? declarationOwnerNamesByMethod[method.Id]
                    : GetMetadataName(methodSymbol.ContainingType),
                method.Span,
                diagnostics);
        }

        return functions;
    }

    private static IMethodSymbol? FindMethodSymbol(
        INamedTypeSymbol typeSymbol,
        HirMethodDeclaration method,
        IReadOnlyList<string> parameterTypeNames)
    {
        for (INamedTypeSymbol? current = typeSymbol;
             current is not null;
             current = current.BaseType)
        {
            IMethodSymbol[] matches = current.GetMembers(method.Name)
                .OfType<IMethodSymbol>()
                .Where(candidate => candidate.IsStatic == method.IsStatic &&
                    candidate.Parameters.Length == parameterTypeNames.Count &&
                    candidate.Parameters.Select(static parameter =>
                            parameter.Type.ToDisplayString(TypeMetadataFormat))
                        .SequenceEqual(parameterTypeNames, StringComparer.Ordinal))
                .ToArray();
            if (matches.Length == 1)
            {
                return matches[0];
            }
        }
        return null;
    }

    private static IReadOnlyList<ScriptInputActionBindingDeclaration> LowerInputActions(
        HirMethodDeclaration method,
        IMethodSymbol? methodSymbol,
        IReadOnlyList<string> parameterTypeNames,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        if (methodSymbol is null)
        {
            return [];
        }
        AttributeData[] attributes = FindAttributes(
            methodSymbol,
            UnrealInputActionAttributeName).ToArray();
        if (attributes.Length == 0)
        {
            return [];
        }

        var bindings = new List<ScriptInputActionBindingDeclaration>(attributes.Length);
        foreach (AttributeData attribute in attributes)
        {
            string actionPath = attribute.ConstructorArguments.FirstOrDefault().Value as string ??
                string.Empty;
            int triggerValue = ReadNamedEnumValue(
                attribute,
                "TriggerEvent",
                3);
            int valueTypeValue = ReadNamedEnumValue(
                attribute,
                "ValueType",
                1);
            bool consumeInput = ReadNamedBoolean(
                attribute,
                "ConsumeInput");
            ScriptInputTriggerEvent? trigger = triggerValue switch
            {
                1 => ScriptInputTriggerEvent.Started,
                2 => ScriptInputTriggerEvent.Ongoing,
                3 => ScriptInputTriggerEvent.Triggered,
                4 => ScriptInputTriggerEvent.Completed,
                5 => ScriptInputTriggerEvent.Canceled,
                _ => null,
            };
            ScriptInputValueType? valueType = valueTypeValue switch
            {
                1 => ScriptInputValueType.Boolean,
                2 => ScriptInputValueType.Axis1D,
                3 => ScriptInputValueType.Axis2D,
                4 => ScriptInputValueType.Axis3D,
                _ => null,
            };
            bool parameterMatches = valueType switch
            {
                ScriptInputValueType.Boolean => parameterTypeNames is ["System.Boolean"],
                ScriptInputValueType.Axis1D => parameterTypeNames is ["System.Single"],
                ScriptInputValueType.Axis2D or ScriptInputValueType.Axis3D =>
                    parameterTypeNames is ["Finare.Unreal.UnrealVector"],
                _ => false,
            };
            if (!method.IsPublic || method.IsStatic ||
                methodSymbol.ReturnType.SpecialType != SpecialType.System_Void ||
                string.IsNullOrWhiteSpace(actionPath) || trigger is null ||
                valueType is null || !parameterMatches)
            {
                diagnostics.Add(InvalidGameplayBindingDiagnostic(
                    methodSymbol,
                    $"Enhanced Input handler '{methodSymbol.ToDisplayString()}' must be a public instance void method with one parameter matching its reviewed ValueType and an exact action path."));
                continue;
            }

            bindings.Add(new ScriptInputActionBindingDeclaration(
                CSharpSemanticIds.ForSymbol(
                    "input-action",
                    $"{method.Id.Value}|{actionPath}|{trigger}"),
                actionPath,
                trigger.Value,
                valueType.Value,
                consumeInput));
        }
        return bindings;
    }

    private static IReadOnlyList<ScriptDelegateBindingDeclaration> LowerDelegateBindings(
        HirMethodDeclaration method,
        IMethodSymbol? methodSymbol,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        if (methodSymbol is null)
        {
            return [];
        }
        var bindings = new List<ScriptDelegateBindingDeclaration>();
        foreach (AttributeData attribute in FindAttributes(
                     methodSymbol,
                     UnrealEventAttributeName))
        {
            INamedTypeSymbol? sourceType = attribute.ConstructorArguments.ElementAtOrDefault(0).Value
                as INamedTypeSymbol;
            string eventPath = attribute.ConstructorArguments.ElementAtOrDefault(1).Value as string ??
                string.Empty;
            string sourceTypePath = ReadUnrealTypePath(sourceType);
            if (sourceTypePath.Length == 0)
            {
                int separator = eventPath.LastIndexOf(':');
                sourceTypePath = separator > 0 ? eventPath[..separator] : string.Empty;
            }
            int sourceValue = ReadNamedEnumValue(
                attribute,
                "Source",
                0);
            ScriptDelegateSource? source = sourceValue switch
            {
                0 => ScriptDelegateSource.Target,
                1 => ScriptDelegateSource.Host,
                _ => null,
            };
            string? sourceMember = ReadNamedString(
                attribute,
                "SourceMember");
            if (method.IsStatic || source is null ||
                string.IsNullOrWhiteSpace(sourceTypePath) ||
                string.IsNullOrWhiteSpace(eventPath) ||
                sourceMember is not null && string.IsNullOrWhiteSpace(sourceMember))
            {
                diagnostics.Add(InvalidGameplayBindingDiagnostic(
                    methodSymbol,
                    $"Delegate handler '{methodSymbol.ToDisplayString()}' has an invalid source, event path, or source member."));
                continue;
            }
            bindings.Add(new ScriptDelegateBindingDeclaration(
                CSharpSemanticIds.ForSymbol(
                    "delegate-binding",
                    $"{method.Id.Value}|{sourceTypePath}|{eventPath}|{source}|{sourceMember}"),
                sourceTypePath,
                eventPath,
                source.Value,
                sourceMember));
        }
        return bindings;
    }

    private static IReadOnlyList<ScriptInterfaceFunctionDeclaration> LowerInterfaceFunctions(
        HirMethodDeclaration method,
        IMethodSymbol? methodSymbol,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        if (methodSymbol is null)
        {
            return [];
        }
        var bindings = new List<ScriptInterfaceFunctionDeclaration>();
        foreach (AttributeData attribute in FindAttributes(
                     methodSymbol,
                     UnrealInterfaceFunctionAttributeName))
        {
            string interfacePath = attribute.ConstructorArguments.ElementAtOrDefault(0).Value as string ??
                string.Empty;
            string functionName = attribute.ConstructorArguments.ElementAtOrDefault(1).Value as string ??
                string.Empty;
            if (!method.IsPublic || method.IsStatic ||
                string.IsNullOrWhiteSpace(interfacePath) ||
                string.IsNullOrWhiteSpace(functionName))
            {
                diagnostics.Add(InvalidGameplayBindingDiagnostic(
                    methodSymbol,
                    $"Interface implementation '{methodSymbol.ToDisplayString()}' must be a public instance method with exact interface and function paths."));
                continue;
            }
            bindings.Add(new ScriptInterfaceFunctionDeclaration(
                CSharpSemanticIds.ForSymbol(
                    "interface-function",
                    $"{method.Id.Value}|{interfacePath}|{functionName}"),
                interfacePath,
                functionName));
        }
        return bindings;
    }

    private static UnifiedDiagnostic InvalidGameplayBindingDiagnostic(
        IMethodSymbol method,
        string message)
    {
        SyntaxNode? syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        string path = syntax?.SyntaxTree.FilePath ??
            method.Locations.FirstOrDefault()?.SourceTree?.FilePath ?? "<compilation>";
        return new UnifiedDiagnostic(
            DeclarationDiagnosticCodes.InvalidGameplayBinding,
            UnifiedDiagnosticSeverity.Error,
            message,
            syntax is null
                ? SourceSpan.Unknown(path)
                : RoslynSourceSpan.FromNode(syntax, path));
    }

    private static int ReadNamedEnumValue(
        AttributeData attribute,
        string name,
        int defaultValue)
    {
        TypedConstant value = attribute.NamedArguments
            .Where(argument => argument.Key == name)
            .Select(static argument => argument.Value)
            .FirstOrDefault();
        return value.Value is null
            ? defaultValue
            : Convert.ToInt32(value.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool ReadNamedBoolean(AttributeData attribute, string name) =>
        attribute.NamedArguments
            .Where(argument => argument.Key == name)
            .Select(static argument => argument.Value.Value)
            .OfType<bool>()
            .FirstOrDefault();

    private static string? ReadNamedString(AttributeData attribute, string name) =>
        attribute.NamedArguments
            .Where(argument => argument.Key == name)
            .Select(static argument => argument.Value.Value as string)
            .FirstOrDefault();

    private static string ReadUnrealTypePath(INamedTypeSymbol? type)
    {
        AttributeData? attribute = type is null
            ? null
            : FindAttribute(type, UnrealTypeAttributeName);
        return attribute?.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty;
    }

    private static IReadOnlyList<ScriptLifecycleHookDeclaration> LowerLifecycleHooks(
        INamedTypeSymbol typeSymbol)
    {
        AttributeData? classAttribute = FindAttribute(
            typeSymbol,
            UnrealScriptClassAttributeName);
        int classKind = classAttribute?.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;
        bool gameplayBase = false;
        for (INamedTypeSymbol? baseType = typeSymbol.BaseType;
             baseType is not null;
             baseType = baseType.BaseType)
        {
            string baseName = GetMetadataName(baseType);
            if (baseName is "Finare.Unreal.UnrealActorScript" or
                "Finare.Unreal.UnrealActorComponentScript")
            {
                gameplayBase = true;
                break;
            }
        }
        if (classKind is not (1 or 2) && !gameplayBase)
        {
            return [];
        }

        var hooks = new List<ScriptLifecycleHookDeclaration>(3);
        foreach (IMethodSymbol method in typeSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            ScriptLifecycleHookKind? kind = TryGetLifecycleHookKind(method);
            SyntaxNode? syntax = method.DeclaringSyntaxReferences
                .Select(static reference => reference.GetSyntax())
                .SingleOrDefault();
            if (kind is null || syntax is null)
            {
                continue;
            }

            hooks.Add(new ScriptLifecycleHookDeclaration(
                CSharpSemanticIds.ForRoslynSymbol(method),
                kind.Value,
                method.Name,
                RoslynSourceSpan.FromNode(syntax, syntax.SyntaxTree.FilePath)));
        }
        return hooks;
    }

    private static ScriptLifecycleHookKind? TryGetLifecycleHookKind(IMethodSymbol method)
    {
        if (!method.IsOverride || method.IsStatic || !method.ReturnsVoid)
        {
            return null;
        }
        if (method.Name == "OnBeginPlay" && method.Parameters.Length == 0)
        {
            return ScriptLifecycleHookKind.BeginPlay;
        }
        if (method.Name == "OnTick" && method.Parameters.Length == 1 &&
            method.Parameters[0].Type.SpecialType == SpecialType.System_Single)
        {
            return ScriptLifecycleHookKind.Tick;
        }
        return method.Name == "OnEndPlay" && method.Parameters.Length == 0
            ? ScriptLifecycleHookKind.EndPlay
            : null;
    }

    private static ScriptFunctionValueTypeDeclaration? LowerFunctionValueType(
        CSharpCompilation compilation,
        ITypeSymbol? symbol,
        int depth = 0)
    {
        if (symbol is not INamedTypeSymbol type || depth > 16)
        {
            return null;
        }

        ScriptFunctionValueTypeKind? containerKind = null;
        foreach ((string metadataName, ScriptFunctionValueTypeKind kind) in new[]
        {
            ("System.Collections.Generic.IReadOnlyList`1", ScriptFunctionValueTypeKind.Array),
            ("System.Collections.Generic.IReadOnlySet`1", ScriptFunctionValueTypeKind.Set),
            ("System.Collections.Generic.IReadOnlyDictionary`2", ScriptFunctionValueTypeKind.Map),
        })
        {
            if (SymbolEqualityComparer.Default.Equals(
                    type.OriginalDefinition, compilation.GetTypeByMetadataName(metadataName)))
            {
                containerKind = kind;
                break;
            }
        }
        if (containerKind is { } collection)
        {
            return new ScriptFunctionValueTypeDeclaration(
                collection,
                string.Empty,
                collection switch
                {
                    ScriptFunctionValueTypeKind.Array => "TArray",
                    ScriptFunctionValueTypeKind.Set => "TSet",
                    _ => "TMap",
                },
                [],
                type.TypeArguments.Select(argument => new ScriptFunctionTypeArgumentDeclaration(
                    argument.ToDisplayString(TypeMetadataFormat),
                    LowerFunctionValueType(compilation, argument, depth + 1))).ToArray());
        }

        string knownValueMetadata = type.ToDisplayString(TypeMetadataFormat);
        if (knownValueMetadata is "Finare.Unreal.UnrealVector" or
            "Finare.Unreal.UnrealRotator" or
            "Finare.Unreal.UnrealTransform")
        {
            string path = knownValueMetadata switch
            {
                "Finare.Unreal.UnrealVector" => "/Script/CoreUObject.Vector",
                "Finare.Unreal.UnrealRotator" => "/Script/CoreUObject.Rotator",
                _ => "/Script/CoreUObject.Transform",
            };
            string nativeName = knownValueMetadata switch
            {
                "Finare.Unreal.UnrealVector" => "Vector",
                "Finare.Unreal.UnrealRotator" => "Rotator",
                _ => "Transform",
            };
            return new ScriptFunctionValueTypeDeclaration(
                ScriptFunctionValueTypeKind.Struct,
                path,
                nativeName,
                knownValueMetadata == "Finare.Unreal.UnrealVector"
                    ? [
                        new ScriptFunctionStructFieldDeclaration(0, "X", "System.Double"),
                        new ScriptFunctionStructFieldDeclaration(1, "Y", "System.Double"),
                        new ScriptFunctionStructFieldDeclaration(2, "Z", "System.Double"),
                    ]
                    : []);
        }

        AttributeData? enumAttribute = FindAttribute(type, UnrealEnumAttributeName);
        if (TryReadUnrealTypeIdentity(
                enumAttribute,
                out string enumPath,
                out string enumNativeName))
        {
            return new ScriptFunctionValueTypeDeclaration(
                ScriptFunctionValueTypeKind.Enum,
                enumPath,
                enumNativeName,
                []);
        }

        AttributeData? structAttribute = FindAttribute(type, UnrealStructAttributeName);
        if (!TryReadUnrealTypeIdentity(
                structAttribute,
                out string structPath,
                out string structNativeName))
        {
            return null;
        }

        ScriptFunctionStructFieldDeclaration[] fields = depth >= 16
            ? []
            : type.GetMembers()
            .OfType<IPropertySymbol>()
            .Select(property => (
                Property: property,
                Attribute: FindAttribute(property, UnrealStructFieldAttributeName)))
            .Where(static pair => pair.Attribute is not null &&
                pair.Attribute.ConstructorArguments.Length == 2 &&
                pair.Attribute.ConstructorArguments[0].Value is int &&
                pair.Attribute.ConstructorArguments[1].Value is string)
            .Select(pair => new ScriptFunctionStructFieldDeclaration(
                (int)pair.Attribute!.ConstructorArguments[0].Value!,
                (string)pair.Attribute.ConstructorArguments[1].Value!,
                pair.Property.Type.ToDisplayString(TypeMetadataFormat),
                LowerFunctionValueType(
                    compilation,
                    pair.Property.Type,
                    depth + 1)))
            .OrderBy(static field => field.Index)
            .ToArray();
        return new ScriptFunctionValueTypeDeclaration(
            ScriptFunctionValueTypeKind.Struct,
            structPath,
            structNativeName,
            fields);
    }

    private static bool TryReadUnrealTypeIdentity(
        AttributeData? attribute,
        out string path,
        out string nativeName)
    {
        path = attribute?.ConstructorArguments.ElementAtOrDefault(0).Value as string ?? string.Empty;
        nativeName = attribute?.ConstructorArguments.ElementAtOrDefault(1).Value as string ?? string.Empty;
        return !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(nativeName);
    }

    private static bool TryGetAutoPropertySyntax(
        IPropertySymbol property,
        out PropertyDeclarationSyntax? syntax)
    {
        PropertyDeclarationSyntax[] declarations = property.DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .OfType<PropertyDeclarationSyntax>()
            .ToArray();
        syntax = declarations.Length == 1 ? declarations[0] : null;
        if (syntax?.AccessorList is null || syntax.ExpressionBody is not null)
        {
            return false;
        }

        AccessorDeclarationSyntax? getter = syntax.AccessorList.Accessors
            .SingleOrDefault(static accessor => accessor.Keyword.ValueText == "get");
        AccessorDeclarationSyntax? setter = syntax.AccessorList.Accessors
            .SingleOrDefault(static accessor => accessor.Keyword.ValueText is "set" or "init");
        return getter is { Body: null, ExpressionBody: null } &&
            setter is { Body: null, ExpressionBody: null };
    }

    private static bool HasUnsupportedFunctionContainerShape(
        CSharpCompilation compilation,
        ITypeSymbol type,
        bool allowVoid,
        out string? reason)
    {
        reason = null;
        if (allowVoid && type.SpecialType == SpecialType.System_Void)
        {
            return false;
        }

        if (type is INamedTypeSymbol namedType &&
            TryGetFunctionContainerKind(compilation, namedType, out ScriptFunctionValueTypeKind kind))
        {
            if (namedType.TypeArguments.Any(argument =>
                    argument is INamedTypeSymbol nested &&
                    TryGetFunctionContainerKind(compilation, nested, out _)))
            {
                reason = "nested containers are not supported";
                return true;
            }
            if ((kind == ScriptFunctionValueTypeKind.Map ||
                 kind == ScriptFunctionValueTypeKind.Set) &&
                    IsUnhashableFunctionKey(namedType.TypeArguments[0]))
            {
                reason = kind == ScriptFunctionValueTypeKind.Set
                    ? "the set element type has no Blueprint hash identity"
                    : "the map key type has no Blueprint hash identity";
                return true;
            }
            foreach (ITypeSymbol argument in namedType.TypeArguments)
            {
                if (HasUnsupportedFunctionContainerShape(
                        compilation,
                        argument,
                        allowVoid: false,
                        out reason))
                {
                    return true;
                }
            }
            return false;
        }

        return false;
    }

    private static bool TryGetFunctionContainerKind(
        CSharpCompilation compilation,
        ITypeSymbol type,
        out ScriptFunctionValueTypeKind kind)
    {
        kind = default;
        if (type is not INamedTypeSymbol namedType || !namedType.IsGenericType)
        {
            return false;
        }

        foreach ((string metadataName, ScriptFunctionValueTypeKind candidate) in new[]
        {
            ("System.Collections.Generic.IReadOnlyList`1", ScriptFunctionValueTypeKind.Array),
            ("System.Collections.Generic.IReadOnlySet`1", ScriptFunctionValueTypeKind.Set),
            ("System.Collections.Generic.IReadOnlyDictionary`2", ScriptFunctionValueTypeKind.Map),
        })
        {
            if (SymbolEqualityComparer.Default.Equals(
                    namedType.OriginalDefinition,
                    compilation.GetTypeByMetadataName(metadataName)))
            {
                kind = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool IsUnhashableFunctionKey(ITypeSymbol type)
    {
        string metadataName = type.ToDisplayString(TypeMetadataFormat);
        return type.SpecialType == SpecialType.System_Boolean ||
            metadataName == "Finare.Unreal.UnrealText";
    }

    private static bool TryClassifyType(
        CSharpCompilation compilation,
        ITypeSymbol type,
        out ScriptStateTypeKind kind,
        out ScriptFunctionValueTypeDeclaration? unrealType)
    {
        string metadataName = type.ToDisplayString(TypeMetadataFormat);
        unrealType = null;
        ScriptFunctionValueTypeDeclaration? classifiedValueType =
            LowerFunctionValueType(compilation, type);
        if (classifiedValueType?.Kind is ScriptFunctionValueTypeKind.Array or
            ScriptFunctionValueTypeKind.Set or ScriptFunctionValueTypeKind.Map)
        {
            ITypeSymbol[] collectionArguments = type is INamedTypeSymbol namedType
                ? namedType.TypeArguments.ToArray()
                : [];
            IReadOnlyList<ScriptFunctionTypeArgumentDeclaration>? declaredArguments =
                classifiedValueType.TypeArguments;
            bool validLeaf = declaredArguments is not null &&
                declaredArguments.Count == collectionArguments.Length;
            ScriptFunctionTypeArgumentDeclaration[] normalizedArguments =
                declaredArguments?.ToArray() ?? [];
            if (validLeaf)
            {
                for (int index = 0; index < collectionArguments.Length; index++)
                {
                    if (normalizedArguments[index].UnrealType?.Kind is
                        ScriptFunctionValueTypeKind.Array or ScriptFunctionValueTypeKind.Set or
                        ScriptFunctionValueTypeKind.Map ||
                        !TryClassifyType(
                            compilation,
                            collectionArguments[index],
                            out ScriptStateTypeKind leafKind,
                            out ScriptFunctionValueTypeDeclaration? leafType) ||
                        leafKind is ScriptStateTypeKind.Array or ScriptStateTypeKind.Set or
                            ScriptStateTypeKind.Map ||
                        leafKind == ScriptStateTypeKind.Enum && leafType is null)
                    {
                        validLeaf = false;
                        break;
                    }
                    normalizedArguments[index] = normalizedArguments[index] with
                    {
                        UnrealType = leafType,
                    };
                }
            }
            if (!validLeaf)
            {
                kind = default;
                return false;
            }
            kind = classifiedValueType.Kind switch
            {
                ScriptFunctionValueTypeKind.Array => ScriptStateTypeKind.Array,
                ScriptFunctionValueTypeKind.Set => ScriptStateTypeKind.Set,
                _ => ScriptStateTypeKind.Map,
            };
            unrealType = classifiedValueType with { TypeArguments = normalizedArguments };
            return true;
        }
        bool isGeneratedUnrealEnum = type is INamedTypeSymbol generatedEnum &&
            TryReadUnrealTypeIdentity(
                FindAttribute(generatedEnum, UnrealEnumAttributeName),
                out _,
                out _) &&
            generatedEnum.AllInterfaces.Any(interfaceType =>
                GetMetadataName(interfaceType) == "Finare.Unreal.IUnrealEnumValue") &&
            generatedEnum.GetMembers("Value").OfType<IPropertySymbol>().Any(property =>
                property.Type.SpecialType == SpecialType.System_Int64);
        kind = isGeneratedUnrealEnum
            ? ScriptStateTypeKind.Enum
            : type.SpecialType switch
        {
            SpecialType.System_Boolean => ScriptStateTypeKind.Boolean,
            SpecialType.System_SByte or SpecialType.System_Byte or
                SpecialType.System_Int16 or SpecialType.System_UInt16 or
                SpecialType.System_Int32 or SpecialType.System_UInt32 or
                SpecialType.System_Int64 or SpecialType.System_UInt64 => ScriptStateTypeKind.Integer,
            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal =>
                ScriptStateTypeKind.FloatingPoint,
            SpecialType.System_String => ScriptStateTypeKind.String,
            _ when type.TypeKind == TypeKind.Enum => ScriptStateTypeKind.Enum,
            _ when metadataName == "Finare.Unreal.UnrealName" => ScriptStateTypeKind.Name,
            _ when metadataName == "Finare.Unreal.UnrealText" => ScriptStateTypeKind.Text,
            _ when metadataName == "Finare.Unreal.UnrealObjectHandle" => ScriptStateTypeKind.Object,
            _ when metadataName == "Finare.Unreal.UnrealClassHandle" => ScriptStateTypeKind.Class,
            _ when metadataName == "Finare.Unreal.UnrealSoftObjectHandle" => ScriptStateTypeKind.SoftObject,
            _ when metadataName == "Finare.Unreal.UnrealSoftClassHandle" => ScriptStateTypeKind.SoftClass,
            _ when metadataName is "Finare.Unreal.UnrealVector" or
                "Finare.Unreal.UnrealRotator" or
                "Finare.Unreal.UnrealTransform" => ScriptStateTypeKind.Struct,
            _ => default,
        };
        bool supported = isGeneratedUnrealEnum || type.SpecialType is SpecialType.System_Boolean or
            SpecialType.System_SByte or SpecialType.System_Byte or
            SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Int32 or SpecialType.System_UInt32 or
            SpecialType.System_Int64 or SpecialType.System_UInt64 or
            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal or
            SpecialType.System_String || type.TypeKind == TypeKind.Enum ||
            metadataName is "Finare.Unreal.UnrealName" or
                "Finare.Unreal.UnrealText" or
                "Finare.Unreal.UnrealObjectHandle" or
                "Finare.Unreal.UnrealClassHandle" or
                "Finare.Unreal.UnrealSoftObjectHandle" or
                "Finare.Unreal.UnrealSoftClassHandle" or
                "Finare.Unreal.UnrealVector" or
                "Finare.Unreal.UnrealRotator" or
                "Finare.Unreal.UnrealTransform";
        if (supported && kind == ScriptStateTypeKind.Enum && type is INamedTypeSymbol enumType)
        {
            AttributeData? enumAttribute = FindAttribute(enumType, UnrealEnumAttributeName);
            if (TryReadUnrealTypeIdentity(
                    enumAttribute,
                    out string enumPath,
                    out string enumNativeName))
            {
                unrealType = new ScriptFunctionValueTypeDeclaration(
                    ScriptFunctionValueTypeKind.Enum,
                    enumPath,
                    enumNativeName,
                    []);
            }
        }
        else if (supported && kind == ScriptStateTypeKind.Struct)
        {
            string path = metadataName switch
            {
                "Finare.Unreal.UnrealRotator" => "/Script/CoreUObject.Rotator",
                "Finare.Unreal.UnrealTransform" => "/Script/CoreUObject.Transform",
                _ => "/Script/CoreUObject.Vector",
            };
            string nativeName = metadataName switch
            {
                "Finare.Unreal.UnrealRotator" => "Rotator",
                "Finare.Unreal.UnrealTransform" => "Transform",
                _ => "Vector",
            };
            unrealType = new ScriptFunctionValueTypeDeclaration(
                ScriptFunctionValueTypeKind.Struct,
                path,
                nativeName,
                metadataName == "Finare.Unreal.UnrealVector"
                    ? [
                        new ScriptFunctionStructFieldDeclaration(0, "X", "System.Double"),
                        new ScriptFunctionStructFieldDeclaration(1, "Y", "System.Double"),
                        new ScriptFunctionStructFieldDeclaration(2, "Z", "System.Double"),
                    ]
                    : []);
        }
        return supported;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypeHierarchy(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            chain.Add(current);
        }

        chain.Reverse();
        return chain;
    }

    private static SourceSpan GetPropertySpan(IPropertySymbol property, INamedTypeSymbol fallbackType)
    {
        return GetMemberSpan(property, fallbackType);
    }

    private static SourceSpan GetMemberSpan(ISymbol member, INamedTypeSymbol fallbackType)
    {
        SyntaxNode? syntax = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        return syntax is not null
            ? RoslynSourceSpan.FromNode(syntax, syntax.SyntaxTree.FilePath)
            : SourceSpan.Unknown(fallbackType.Locations.FirstOrDefault()?.SourceTree?.FilePath ?? "<compilation>");
    }

    private static bool TryResolveProjectedStateDefault(
        INamedTypeSymbol owner,
        IPropertySymbol property,
        PropertyDeclarationSyntax propertySyntax,
        INamedTypeSymbol projectionType,
        CSharpCompilation compilation,
        out JsonElement value,
        out SyntaxNode diagnosticNode,
        out string diagnosticMessage)
    {
        if (!TryResolveDeclaredStateDefault(
                owner,
                property,
                propertySyntax,
                compilation,
                out value,
                out diagnosticNode,
                out diagnosticMessage))
        {
            return false;
        }

        bool reachedOwner = false;
        foreach (INamedTypeSymbol current in EnumerateTypeHierarchy(projectionType))
        {
            if (!reachedOwner)
            {
                reachedOwner = SymbolEqualityComparer.Default.Equals(current, owner);
                continue;
            }

            if (!TryApplyConstructorStateOverride(
                    current,
                    property,
                    compilation,
                    ref value,
                    out diagnosticNode,
                    out diagnosticMessage))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryApplyConstructorStateOverride(
        INamedTypeSymbol containingType,
        IPropertySymbol property,
        CSharpCompilation compilation,
        ref JsonElement value,
        out SyntaxNode diagnosticNode,
        out string diagnosticMessage)
    {
        diagnosticNode = property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() ??
            containingType.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() ??
            property.Locations.FirstOrDefault()?.SourceTree?.GetRoot() ??
            throw new InvalidOperationException("A source syntax node is required for state default analysis.");
        diagnosticMessage = string.Empty;

        IMethodSymbol[] explicitConstructors = containingType.InstanceConstructors
            .Where(static constructor => !constructor.IsImplicitlyDeclared)
            .ToArray();
        if (explicitConstructors.Length == 0)
        {
            return true;
        }

        IMethodSymbol? parameterlessConstructor = explicitConstructors
            .SingleOrDefault(static constructor => constructor.Parameters.Length == 0);
        if (parameterlessConstructor is null)
        {
            diagnosticNode = containingType.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() ?? diagnosticNode;
            diagnosticMessage =
                $"requires a deterministic default; '{GetMetadataName(containingType)}' has no parameterless constructor for the inherited state contract.";
            return false;
        }

        ConstructorDeclarationSyntax? constructorSyntax = parameterlessConstructor
            .DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .OfType<ConstructorDeclarationSyntax>()
            .SingleOrDefault();
        if (constructorSyntax is null)
        {
            return true;
        }

        if (constructorSyntax.Initializer is { ArgumentList.Arguments.Count: > 0 })
        {
            diagnosticNode = constructorSyntax.Initializer;
            diagnosticMessage =
                "requires a deterministic default; parameter-dependent base/this constructor chains are not supported.";
            return false;
        }

        SemanticModel constructorModel = compilation.GetSemanticModel(
            constructorSyntax.SyntaxTree,
            ignoreAccessibility: false);
        if (TryFindUnsupportedUnaryStateWrite(constructorSyntax, property, constructorModel, out SyntaxNode? unaryWrite))
        {
            diagnosticNode = unaryWrite!;
            diagnosticMessage =
                "requires a deterministic default; increment/decrement constructor writes are not supported.";
            return false;
        }
        var assignments = new List<AssignmentExpressionSyntax>();
        foreach (AssignmentExpressionSyntax assignment in constructorSyntax
                     .DescendantNodes()
                     .OfType<AssignmentExpressionSyntax>())
        {
            if (!TargetsCurrentInstanceProperty(assignment.Left, property, constructorModel))
            {
                continue;
            }

            bool isDirectBodyStatement = assignment.Parent is ExpressionStatementSyntax statement &&
                statement.Parent == constructorSyntax.Body;
            bool isDirectExpressionBody = constructorSyntax.ExpressionBody?.Expression == assignment;
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                (!isDirectBodyStatement && !isDirectExpressionBody))
            {
                diagnosticNode = assignment;
                diagnosticMessage =
                    "requires a deterministic default; constructor assignments must be unconditional top-level simple assignments.";
                return false;
            }

            assignments.Add(assignment);
        }

        if (assignments.Count == 0)
        {
            return true;
        }

        AssignmentExpressionSyntax finalAssignment = assignments[^1];
        if (TryFindConstructorControlFlowBypass(
                constructorSyntax,
                finalAssignment,
                constructorModel,
                out SyntaxNode? bypass))
        {
            diagnosticNode = bypass!;
            diagnosticMessage =
                "requires a deterministic default; constructor control flow can bypass the selected state assignment.";
            return false;
        }
        if (!TryCreateDefaultValue(
                property.Type,
                finalAssignment.Right,
                constructorModel,
                out JsonElement overrideValue))
        {
            diagnosticNode = finalAssignment.Right;
            diagnosticMessage =
                "requires a finite, JSON-representable compile-time constant default in every derived parameterless constructor.";
            return false;
        }

        value = overrideValue;
        return true;
    }

    private static bool TryResolveDeclaredStateDefault(
        INamedTypeSymbol containingType,
        IPropertySymbol property,
        PropertyDeclarationSyntax propertySyntax,
        CSharpCompilation compilation,
        out JsonElement value,
        out SyntaxNode diagnosticNode,
        out string diagnosticMessage)
    {
        ExpressionSyntax? expression = propertySyntax.Initializer?.Value;
        SemanticModel? semanticModel = expression is null
            ? null
            : compilation.GetSemanticModel(expression.SyntaxTree);

        IMethodSymbol[] explicitConstructors = containingType.InstanceConstructors
            .Where(static constructor => !constructor.IsImplicitlyDeclared)
            .ToArray();
        IMethodSymbol? parameterlessConstructor = explicitConstructors
            .SingleOrDefault(static constructor => constructor.Parameters.Length == 0);
        if (explicitConstructors.Length != 0 && parameterlessConstructor is null)
        {
            value = default;
            diagnosticNode = containingType.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() ??
                propertySyntax;
            diagnosticMessage =
                $"requires a deterministic default; '{GetMetadataName(containingType)}' has no parameterless constructor for the inherited state contract.";
            return false;
        }
        if (parameterlessConstructor is not null &&
            parameterlessConstructor.DeclaringSyntaxReferences
                .Select(static reference => reference.GetSyntax())
                .OfType<ConstructorDeclarationSyntax>()
                .SingleOrDefault() is ConstructorDeclarationSyntax constructorSyntax)
        {
            if (constructorSyntax.Initializer is { ArgumentList.Arguments.Count: > 0 })
            {
                value = default;
                diagnosticNode = constructorSyntax.Initializer;
                diagnosticMessage =
                    "requires a deterministic default; parameter-dependent base/this constructor chains are not supported.";
                return false;
            }

            SemanticModel constructorModel = compilation.GetSemanticModel(
                constructorSyntax.SyntaxTree);
            if (TryFindUnsupportedUnaryStateWrite(constructorSyntax, property, constructorModel, out SyntaxNode? unaryWrite))
            {
                value = default;
                diagnosticNode = unaryWrite!;
                diagnosticMessage =
                    "requires a deterministic default; increment/decrement constructor writes are not supported.";
                return false;
            }
            var directAssignments = new List<AssignmentExpressionSyntax>();
            foreach (AssignmentExpressionSyntax assignment in constructorSyntax
                         .DescendantNodes()
                         .OfType<AssignmentExpressionSyntax>())
            {
                if (!TargetsCurrentInstanceProperty(assignment.Left, property, constructorModel))
                {
                    continue;
                }

                if (IsDeferredConstructorAssignment(assignment, constructorSyntax))
                {
                    value = default;
                    diagnosticNode = assignment;
                    diagnosticMessage =
                        "requires a deterministic default; deferred constructor assignments are not supported.";
                    return false;
                }

                bool isDirectBodyStatement = assignment.Parent is ExpressionStatementSyntax statement &&
                    statement.Parent == constructorSyntax.Body;
                bool isDirectExpressionBody = constructorSyntax.ExpressionBody?.Expression == assignment;
                if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                    (!isDirectBodyStatement && !isDirectExpressionBody))
                {
                    value = default;
                    diagnosticNode = assignment;
                    diagnosticMessage =
                        "requires a deterministic default; constructor assignments must be unconditional top-level simple assignments.";
                    return false;
                }

                directAssignments.Add(assignment);
            }

            if (directAssignments.Count != 0)
            {
                AssignmentExpressionSyntax finalAssignment = directAssignments[^1];
                if (TryFindConstructorControlFlowBypass(
                        constructorSyntax,
                        finalAssignment,
                        constructorModel,
                        out SyntaxNode? bypass))
                {
                    value = default;
                    diagnosticNode = bypass!;
                    diagnosticMessage =
                        "requires a deterministic default; constructor control flow can bypass the selected state assignment.";
                    return false;
                }
                expression = finalAssignment.Right;
                semanticModel = constructorModel;
            }
        }

        if (expression is null || semanticModel is null)
        {
            value = default;
            diagnosticNode = propertySyntax;
            diagnosticMessage =
                "requires an explicit compile-time constant property initializer or unconditional parameterless-constructor assignment.";
            return false;
        }

        value = default;
        if (!TryCreateDefaultValue(
                property.Type,
                expression,
                semanticModel,
                out value))
        {
            diagnosticNode = expression;
            diagnosticMessage = property.Type.ToDisplayString(TypeMetadataFormat) ==
                "Finare.Unreal.UnrealTransform"
                ? "requires finite components and a normalized quaternion; use UnrealTransform.Identity or an explicit valid constant."
                : "requires a finite, JSON-representable compile-time constant default.";
            return false;
        }

        diagnosticNode = expression;
        diagnosticMessage = string.Empty;
        return true;
    }

    private static bool IsDeferredConstructorAssignment(
        AssignmentExpressionSyntax assignment,
        ConstructorDeclarationSyntax constructor)
    {
        foreach (SyntaxNode ancestor in assignment.Ancestors())
        {
            if (ancestor == constructor)
            {
                return false;
            }
            if (ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            {
                return true;
            }
        }
        return true;
    }

    private static bool TryFindUnsupportedUnaryStateWrite(
        ConstructorDeclarationSyntax constructor,
        IPropertySymbol property,
        SemanticModel semanticModel,
        out SyntaxNode? write)
    {
        foreach (PrefixUnaryExpressionSyntax expression in constructor
                     .DescendantNodes()
                     .OfType<PrefixUnaryExpressionSyntax>()
                     .Where(static expression =>
                         expression.IsKind(SyntaxKind.PreIncrementExpression) ||
                         expression.IsKind(SyntaxKind.PreDecrementExpression)))
        {
            if (TargetsCurrentInstanceProperty(expression.Operand, property, semanticModel))
            {
                write = expression;
                return true;
            }
        }

        foreach (PostfixUnaryExpressionSyntax expression in constructor
                     .DescendantNodes()
                     .OfType<PostfixUnaryExpressionSyntax>()
                     .Where(static expression =>
                         expression.IsKind(SyntaxKind.PostIncrementExpression) ||
                         expression.IsKind(SyntaxKind.PostDecrementExpression)))
        {
            if (TargetsCurrentInstanceProperty(expression.Operand, property, semanticModel))
            {
                write = expression;
                return true;
            }
        }

        write = null;
        return false;
    }

    private static bool TryFindConstructorControlFlowBypass(
        ConstructorDeclarationSyntax constructor,
        AssignmentExpressionSyntax selectedAssignment,
        SemanticModel semanticModel,
        out SyntaxNode? bypass)
    {
        if (constructor.Body is not BlockSyntax body)
        {
            bypass = null;
            return false;
        }

        // Analyze the complete body so Roslyn resolves exits through nested
        // branches. Only exits before the selected top-level assignment can
        // leave a successfully constructed object with the inherited value.
        ControlFlowAnalysis? flow = semanticModel.AnalyzeControlFlow(body);
        SyntaxNode? exit = flow?.ExitPoints
            .Where(candidate => candidate.SpanStart < selectedAssignment.SpanStart)
            .Where(candidate => IsConstructorExecutableNode(candidate, constructor))
            .OrderBy(static candidate => candidate.SpanStart)
            .FirstOrDefault();
        if (exit is not null)
        {
            bypass = exit;
            return true;
        }

        // A goto whose label is after the selected assignment is an explicit
        // source-level bypass. Roslyn's ExitPoints does not expose this jump
        // as an exit from the enclosing constructor body.
        Dictionary<string, LabeledStatementSyntax> labels = body
            .DescendantNodes()
            .OfType<LabeledStatementSyntax>()
            .Where(label => IsConstructorExecutableNode(label, constructor))
            .GroupBy(static label => label.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        GotoStatementSyntax? jump = body
            .DescendantNodes()
            .OfType<GotoStatementSyntax>()
            .Where(jumpNode => jumpNode.Expression is IdentifierNameSyntax)
            .Where(jumpNode => jumpNode.SpanStart < selectedAssignment.SpanStart)
            .Where(jumpNode => IsConstructorExecutableNode(jumpNode, constructor))
            .FirstOrDefault(jumpNode => labels.TryGetValue(
                jumpNode.Expression!.ToString(),
                out LabeledStatementSyntax? label) &&
                label.SpanStart > selectedAssignment.SpanStart);
        if (jump is not null)
        {
            bypass = jump;
            return true;
        }

        bypass = null;
        return false;
    }

    private static bool IsConstructorExecutableNode(
        SyntaxNode node,
        ConstructorDeclarationSyntax constructor)
    {
        for (SyntaxNode? current = node.Parent; current is not null && current != constructor; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            {
                return false;
            }
        }
        return node.AncestorsAndSelf().Contains(constructor);
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

    private static bool TryCreateDefaultValue(
        ITypeSymbol type,
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out JsonElement value)
    {
        string metadataName = type.ToDisplayString(TypeMetadataFormat);
        if (type is INamedTypeSymbol collectionType &&
            collectionType.IsGenericType &&
            collectionType.OriginalDefinition.MetadataName is "IReadOnlyList`1" or
                "IReadOnlySet`1" or "IReadOnlyDictionary`2")
        {
            return TryCreateCollectionDefault(collectionType, expression, semanticModel, out value);
        }
        if (type is INamedTypeSymbol typedEnum &&
            TryReadUnrealTypeIdentity(
                FindAttribute(typedEnum, UnrealEnumAttributeName),
                out string enumPath,
                out _))
        {
            if (IsDefaultValueExpression(expression))
            {
                value = JsonSerializer.SerializeToElement("0");
                return true;
            }
            if (TryReadConstructorArguments(
                    type,
                    expression,
                    semanticModel,
                    out IReadOnlyList<ExpressionSyntax> enumArguments) &&
                enumArguments.Count == 1 &&
                TryReadSignedInteger(enumArguments[0], semanticModel, out long constructorValue))
            {
                value = JsonSerializer.SerializeToElement(
                    constructorValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            if (TryReadUnrealEnumConstant(
                    typedEnum,
                    expression,
                    semanticModel,
                    enumPath,
                    out long namedValue))
            {
                value = JsonSerializer.SerializeToElement(
                    namedValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }

            value = default;
            return false;
        }
        if (metadataName is "Finare.Unreal.UnrealName" or
            "Finare.Unreal.UnrealText")
        {
            if (IsDefaultValueExpression(expression))
            {
                value = JsonSerializer.SerializeToElement(string.Empty);
                return true;
            }
            if (TryReadConstructorArguments(
                    type,
                    expression,
                    semanticModel,
                    out IReadOnlyList<ExpressionSyntax> arguments))
            {
                if (arguments.Count == 0)
                {
                    value = JsonSerializer.SerializeToElement(string.Empty);
                    return true;
                }
                if (arguments.Count == 1 &&
                    TryReadStringConstant(arguments[0], semanticModel, out string text))
                {
                    value = JsonSerializer.SerializeToElement(text);
                    return true;
                }
            }

            value = default;
            return false;
        }

        if (metadataName == "Finare.Unreal.UnrealVector")
        {
            if (IsDefaultValueExpression(expression) || IsNamedStaticValue(
                    type,
                    expression,
                    semanticModel,
                    "Zero"))
            {
                value = JsonSerializer.SerializeToElement(new { x = 0.0, y = 0.0, z = 0.0 });
                return true;
            }
            if (TryReadConstructorArguments(
                    type,
                    expression,
                    semanticModel,
                    out IReadOnlyList<ExpressionSyntax> arguments) &&
                (arguments.Count == 0 || arguments.Count == 3))
            {
                if (arguments.Count == 0)
                {
                    value = JsonSerializer.SerializeToElement(new { x = 0.0, y = 0.0, z = 0.0 });
                    return true;
                }
                if (TryReadFiniteNumber(arguments[0], semanticModel, out double x) &&
                    TryReadFiniteNumber(arguments[1], semanticModel, out double y) &&
                    TryReadFiniteNumber(arguments[2], semanticModel, out double z))
                {
                    value = JsonSerializer.SerializeToElement(new { x, y, z });
                    return true;
                }
            }

            value = default;
            return false;
        }

        if (metadataName == "Finare.Unreal.UnrealRotator")
        {
            if (IsDefaultValueExpression(expression) || IsNamedStaticValue(
                    type,
                    expression,
                    semanticModel,
                    "Zero"))
            {
                value = JsonSerializer.SerializeToElement(new { pitch = 0.0, yaw = 0.0, roll = 0.0 });
                return true;
            }
            if (TryReadConstructorArguments(
                    type,
                    expression,
                    semanticModel,
                    out IReadOnlyList<ExpressionSyntax> arguments) &&
                (arguments.Count == 0 || arguments.Count == 3))
            {
                double pitch = 0.0;
                double yaw = 0.0;
                double roll = 0.0;
                if (arguments.Count == 3 &&
                    (!TryReadFiniteNumber(arguments[0], semanticModel, out pitch) ||
                     !TryReadFiniteNumber(arguments[1], semanticModel, out yaw) ||
                     !TryReadFiniteNumber(arguments[2], semanticModel, out roll)))
                {
                    value = default;
                    return false;
                }
                value = JsonSerializer.SerializeToElement(new { pitch, yaw, roll });
                return true;
            }

            value = default;
            return false;
        }

        if (metadataName == "Finare.Unreal.UnrealTransform")
        {
            double qx = 0.0;
            double qy = 0.0;
            double qz = 0.0;
            double qw = 1.0;
            double tx = 0.0;
            double ty = 0.0;
            double tz = 0.0;
            double sx = 1.0;
            double sy = 1.0;
            double sz = 1.0;
            bool valid = IsNamedStaticValue(type, expression, semanticModel, "Identity");
            if (!valid && TryReadConstructorArguments(
                    type,
                    expression,
                    semanticModel,
                    out IReadOnlyList<ExpressionSyntax> transformArguments))
            {
                valid = transformArguments.Count == 3 &&
                    TryReadQuaternion(transformArguments[0], semanticModel, out qx, out qy,
                        out qz, out qw) &&
                    TryReadVector(transformArguments[1], semanticModel, out tx, out ty,
                        out tz) &&
                    TryReadVector(transformArguments[2], semanticModel, out sx, out sy,
                        out sz) &&
                    IsNormalizedQuaternion(qx, qy, qz, qw);
            }
            if (valid)
            {
                value = JsonSerializer.SerializeToElement(new
                {
                    rotation = new { x = qx, y = qy, z = qz, w = qw },
                    translation = new { x = tx, y = ty, z = tz },
                    scale3D = new { x = sx, y = sy, z = sz },
                });
                return true;
            }

            value = default;
            return false;
        }

        if (metadataName is "Finare.Unreal.UnrealObjectHandle" or
            "Finare.Unreal.UnrealClassHandle")
        {
            bool isNull = IsDefaultValueExpression(expression);
            if (!isNull && TryReadConstructorArguments(
                    type,
                    expression,
                    semanticModel,
                    out IReadOnlyList<ExpressionSyntax> arguments))
            {
                isNull = arguments.Count == 0 ||
                    arguments.Count == 1 &&
                    TryReadUnsignedInteger(arguments[0], semanticModel, out ulong raw) && raw == 0;
            }
            if (isNull)
            {
                value = JsonSerializer.SerializeToElement<object?>(null);
                return true;
            }

            value = default;
            return false;
        }

        if (metadataName is "Finare.Unreal.UnrealSoftObjectHandle" or
            "Finare.Unreal.UnrealSoftClassHandle")
        {
            if (IsDefaultValueExpression(expression))
            {
                value = JsonSerializer.SerializeToElement(string.Empty);
                return true;
            }
            if (TryReadConstructorArguments(
                    type,
                    expression,
                    semanticModel,
                    out IReadOnlyList<ExpressionSyntax> arguments))
            {
                string path = string.Empty;
                bool valid = arguments.Count == 0 ||
                    arguments.Count == 1 &&
                    TryReadStringConstant(arguments[0], semanticModel, out path) &&
                    UnrealObjectPathValidator.IsValid(path);
                if (!valid)
                {
                    value = default;
                    return false;
                }
                value = JsonSerializer.SerializeToElement(
                    arguments.Count == 0 ? string.Empty : path);
                return true;
            }

            value = default;
            return false;
        }

        Optional<object?> constantValue = semanticModel.GetConstantValue(expression);
        if (!constantValue.HasValue)
        {
            value = default;
            return false;
        }
        object? constant = constantValue.Value;
        if (constant is float floatValue && !float.IsFinite(floatValue) ||
            constant is double doubleValue && !double.IsFinite(doubleValue))
        {
            value = default;
            return false;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            IFieldSymbol? enumMember = semanticModel.GetSymbolInfo(expression).Symbol as IFieldSymbol;
            if (enumMember is null || !SymbolEqualityComparer.Default.Equals(enumMember.ContainingType, type))
            {
                enumMember = ((INamedTypeSymbol)type).GetMembers()
                    .OfType<IFieldSymbol>()
                    .Where(static field => field.HasConstantValue)
                    .SingleOrDefault(field => Equals(field.ConstantValue, constant));
            }

            if (enumMember is null)
            {
                value = default;
                return false;
            }

            value = JsonSerializer.SerializeToElement(enumMember.Name);
            return true;
        }

        value = JsonSerializer.SerializeToElement(constant, constant?.GetType() ?? typeof(object));
        return true;
    }

    private static bool TryCreateCollectionDefault(
        INamedTypeSymbol collectionType,
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out JsonElement value)
    {
        value = default;
        IReadOnlyList<ExpressionSyntax>? collectionItems = null;
        InitializerExpressionSyntax? initializer = expression switch
        {
            ObjectCreationExpressionSyntax creation => creation.Initializer,
            ImplicitObjectCreationExpressionSyntax creation => creation.Initializer,
            _ => null,
        };
        if (initializer is null && expression is CollectionExpressionSyntax collection &&
            collection.Elements.All(static element => element is ExpressionElementSyntax))
        {
            // Keep the original expressions attached to the source tree. A
            // synthesized InitializerExpression has no SyntaxTree, so asking
            // the source SemanticModel for constant/type information on its
            // children throws before the declaration can produce a diagnostic.
            collectionItems = collection.Elements
                .Cast<ExpressionElementSyntax>()
                .Select(static element => element.Expression)
                .ToArray();
        }
        if (initializer is null && collectionItems is null)
        {
            BaseArgumentListSyntax? arguments = expression switch
            {
                ObjectCreationExpressionSyntax creation => creation.ArgumentList,
                ImplicitObjectCreationExpressionSyntax creation => creation.ArgumentList,
                _ => null,
            };
            if (arguments?.Arguments.Count == 0 &&
                IsReviewedCollectionConstructor(semanticModel, expression, collectionType))
            {
                value = JsonSerializer.SerializeToElement(Array.Empty<object>());
                return true;
            }
            return false;
        }

        var items = new List<JsonElement>();
        var types = collectionType.TypeArguments;
        if (expression is ObjectCreationExpressionSyntax creationWithInitializer &&
            !IsReviewedCollectionConstructor(semanticModel, creationWithInitializer, collectionType) ||
            expression is ImplicitObjectCreationExpressionSyntax implicitWithInitializer &&
            !IsReviewedCollectionConstructor(semanticModel, implicitWithInitializer, collectionType))
        {
            return false;
        }
        IEnumerable<ExpressionSyntax> sourceItems = collectionItems ?? initializer!.Expressions;
        foreach (ExpressionSyntax item in sourceItems)
        {
            if (collectionType.OriginalDefinition.MetadataName == "IReadOnlyDictionary`2")
            {
                if (item is not InitializerExpressionSyntax pair || pair.Expressions.Count != 2 ||
                    !TryCreateDefaultValue(types[0], pair.Expressions[0], semanticModel, out JsonElement key) ||
                    !TryCreateDefaultValue(types[1], pair.Expressions[1], semanticModel, out JsonElement mapped))
                {
                    return false;
                }
                items.Add(JsonSerializer.SerializeToElement(new { key, value = mapped }));
            }
            else if (!TryCreateDefaultValue(types[0], item, semanticModel, out JsonElement element))
            {
                return false;
            }
            else
            {
                items.Add(element);
            }
        }
        value = JsonSerializer.SerializeToElement(items);
        return true;
    }

    private static bool IsReviewedCollectionConstructor(
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        INamedTypeSymbol collectionType)
    {
        if (expression is CollectionExpressionSyntax)
            return true;
        BaseArgumentListSyntax? arguments = expression switch
        {
            ObjectCreationExpressionSyntax creation => creation.ArgumentList,
            ImplicitObjectCreationExpressionSyntax creation => creation.ArgumentList,
            _ => null,
        };
        if (arguments is not null && arguments.Arguments.Count != 0)
            return false;
        INamedTypeSymbol? constructedType = semanticModel.GetTypeInfo(expression).Type as INamedTypeSymbol;
        if (constructedType is null)
            return false;
        string expectedMetadataName = collectionType.OriginalDefinition.MetadataName switch
        {
            "IReadOnlyList`1" => "System.Collections.Generic.List`1",
            "IReadOnlySet`1" => "System.Collections.Generic.HashSet`1",
            "IReadOnlyDictionary`2" => "System.Collections.Generic.Dictionary`2",
            _ => string.Empty,
        };
        INamedTypeSymbol? expectedDefinition = string.IsNullOrEmpty(expectedMetadataName)
            ? null
            : semanticModel.Compilation.GetTypeByMetadataName(expectedMetadataName);
        return expectedDefinition is not null &&
            SymbolEqualityComparer.Default.Equals(constructedType.OriginalDefinition, expectedDefinition) &&
            constructedType.TypeArguments.Length == collectionType.TypeArguments.Length &&
            SymbolEqualityComparer.Default.Equals(constructedType.TypeArguments[0], collectionType.TypeArguments[0]) &&
            (collectionType.TypeArguments.Length == 1 ||
                SymbolEqualityComparer.Default.Equals(constructedType.TypeArguments[1], collectionType.TypeArguments[1]));
    }

    private static bool TryReadUnrealEnumConstant(
        INamedTypeSymbol type,
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        string enumPath,
        out long value)
    {
        value = default;
        ISymbol? symbol = semanticModel.GetSymbolInfo(expression).Symbol;
        if (symbol is not IPropertySymbol property ||
            !property.IsStatic ||
            !SymbolEqualityComparer.Default.Equals(property.ContainingType, type))
        {
            return false;
        }

        AttributeData? typeAttribute = FindAttribute(type, UnrealEnumAttributeName);
        if (!TryReadUnrealTypeIdentity(typeAttribute, out string actualPath, out _) ||
            !string.Equals(actualPath, enumPath, StringComparison.Ordinal))
        {
            return false;
        }
        AttributeData? valueAttribute = FindAttribute(property, UnrealEnumConstantAttributeName);
        if (valueAttribute?.ConstructorArguments.FirstOrDefault().Value is not long constant)
        {
            return false;
        }
        value = constant;
        return true;
    }

    private static bool TryReadSignedInteger(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out long value)
    {
        value = default;
        Optional<object?> constant = semanticModel.GetConstantValue(expression);
        if (!constant.HasValue || constant.Value is null)
        {
            return false;
        }
        try
        {
            value = Convert.ToInt64(constant.Value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadVector(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out double x,
        out double y,
        out double z)
    {
        x = y = z = default;
        ITypeSymbol? type = semanticModel.GetTypeInfo(expression).Type;
        if (type is null || type.ToDisplayString(TypeMetadataFormat) != "Finare.Unreal.UnrealVector")
        {
            return false;
        }
        if (IsDefaultValueExpression(expression) || IsNamedStaticValue(type, expression, semanticModel, "Zero"))
        {
            return true;
        }
        if (IsNamedStaticValue(type, expression, semanticModel, "One"))
        {
            x = y = z = 1.0;
            return true;
        }
        return TryReadConstructorArguments(type, expression, semanticModel, out IReadOnlyList<ExpressionSyntax> arguments) &&
            arguments.Count == 3 &&
            TryReadFiniteNumber(arguments[0], semanticModel, out x) &&
            TryReadFiniteNumber(arguments[1], semanticModel, out y) &&
            TryReadFiniteNumber(arguments[2], semanticModel, out z);
    }

    private static bool TryReadQuaternion(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out double x,
        out double y,
        out double z,
        out double w)
    {
        x = y = z = w = default;
        ITypeSymbol? type = semanticModel.GetTypeInfo(expression).Type;
        if (type is null || type.ToDisplayString(TypeMetadataFormat) != "Finare.Unreal.UnrealQuaternion")
        {
            return false;
        }
        if (IsNamedStaticValue(type, expression, semanticModel, "Identity"))
        {
            w = 1.0;
            return true;
        }
        return TryReadConstructorArguments(type, expression, semanticModel, out IReadOnlyList<ExpressionSyntax> arguments) &&
            arguments.Count == 4 &&
            TryReadFiniteNumber(arguments[0], semanticModel, out x) &&
            TryReadFiniteNumber(arguments[1], semanticModel, out y) &&
            TryReadFiniteNumber(arguments[2], semanticModel, out z) &&
            TryReadFiniteNumber(arguments[3], semanticModel, out w);
    }

    private static bool IsNormalizedQuaternion(double x, double y, double z, double w)
    {
        double lengthSquared = x * x + y * y + z * z + w * w;
        return double.IsFinite(lengthSquared) && Math.Abs(lengthSquared - 1.0) <= 1e-6;
    }

    private static bool IsDefaultValueExpression(ExpressionSyntax expression) =>
        expression.IsKind(SyntaxKind.DefaultLiteralExpression) ||
        expression is DefaultExpressionSyntax;

    private static bool IsNamedStaticValue(
        ITypeSymbol type,
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        string name) =>
        semanticModel.GetSymbolInfo(expression).Symbol is IPropertySymbol property &&
        property.IsStatic && property.Name == name &&
        SymbolEqualityComparer.Default.Equals(property.ContainingType, type);

    private static bool TryReadConstructorArguments(
        ITypeSymbol type,
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out IReadOnlyList<ExpressionSyntax> arguments)
    {
        arguments = [];
        if (!SymbolEqualityComparer.Default.Equals(semanticModel.GetTypeInfo(expression).Type, type))
        {
            return false;
        }

        BaseArgumentListSyntax? argumentList = expression switch
        {
            ObjectCreationExpressionSyntax creation => creation.ArgumentList,
            ImplicitObjectCreationExpressionSyntax creation => creation.ArgumentList,
            _ => null,
        };
        if (argumentList is null)
        {
            return false;
        }
        ArgumentSyntax[] syntaxArguments = argumentList.Arguments.ToArray();
        IMethodSymbol? constructor = semanticModel.GetSymbolInfo(expression).Symbol as IMethodSymbol;
        if (constructor is not null && syntaxArguments.Any(static argument => argument.NameColon is not null))
        {
            if (syntaxArguments.Length != constructor.Parameters.Length)
            {
                return false;
            }
            var ordered = new ExpressionSyntax?[constructor.Parameters.Length];
            int nextPositional = 0;
            foreach (ArgumentSyntax argument in syntaxArguments)
            {
                int parameterIndex;
                if (argument.NameColon is not null)
                {
                    parameterIndex = -1;
                    for (int index = 0; index < constructor.Parameters.Length; index++)
                    {
                        if (constructor.Parameters[index].Name == argument.NameColon.Name.Identifier.ValueText)
                        {
                            parameterIndex = index;
                            break;
                        }
                    }
                    if (parameterIndex < 0)
                    {
                        return false;
                    }
                }
                else
                {
                    while (nextPositional < ordered.Length && ordered[nextPositional] is not null)
                    {
                        nextPositional++;
                    }
                    parameterIndex = nextPositional++;
                    if (parameterIndex >= ordered.Length)
                    {
                        return false;
                    }
                }
                if (ordered[parameterIndex] is not null)
                {
                    return false;
                }
                ordered[parameterIndex] = argument.Expression;
            }
            if (ordered.Any(static argument => argument is null))
            {
                return false;
            }
            arguments = ordered.Select(static argument => argument!).ToArray();
        }
        else
        {
            arguments = syntaxArguments.Select(static argument => argument.Expression).ToArray();
        }
        return true;
    }

    private static bool TryReadStringConstant(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out string value)
    {
        Optional<object?> constant = semanticModel.GetConstantValue(expression);
        value = constant.HasValue ? constant.Value as string ?? string.Empty : string.Empty;
        return constant.HasValue && constant.Value is string;
    }

    private static bool TryReadFiniteNumber(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out double value)
    {
        Optional<object?> constant = semanticModel.GetConstantValue(expression);
        try
        {
            value = constant.HasValue && constant.Value is not null
                ? Convert.ToDouble(constant.Value, System.Globalization.CultureInfo.InvariantCulture)
                : double.NaN;
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            value = double.NaN;
        }
        return double.IsFinite(value);
    }

    private static bool TryReadUnsignedInteger(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out ulong value)
    {
        Optional<object?> constant = semanticModel.GetConstantValue(expression);
        try
        {
            value = constant.HasValue && constant.Value is not null
                ? Convert.ToUInt64(constant.Value, System.Globalization.CultureInfo.InvariantCulture)
                : ulong.MaxValue;
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            value = ulong.MaxValue;
        }
        return constant.HasValue && value != ulong.MaxValue;
    }

    private static ScriptExposure GetExposure(AttributeData? attribute)
    {
        if (attribute is null)
        {
            return ScriptExposure.None;
        }

        bool instanceEditable = false;
        bool saveGame = false;
        bool replicated = false;
        global::Finare.ReplicationCondition replicationCondition = default;
        string? category = null;
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            switch (argument.Key)
            {
                case nameof(Finare.ExposeAttribute.InstanceEditable):
                    instanceEditable = argument.Value.Value is true;
                    break;
                case nameof(Finare.ExposeAttribute.SaveGame):
                    saveGame = argument.Value.Value is true;
                    break;
                case nameof(Finare.ExposeAttribute.Replicated):
                    replicated = argument.Value.Value is true;
                    break;
                case nameof(Finare.ExposeAttribute.ReplicationCondition):
                    replicationCondition = (global::Finare.ReplicationCondition)(int)(argument.Value.Value ?? 0);
                    break;
                case nameof(Finare.ExposeAttribute.Category):
                    category = argument.Value.Value as string;
                    break;
            }
        }

        return new ScriptExposure(instanceEditable, saveGame, replicated, category, replicationCondition);
    }

    private static AttributeData? FindAttribute(ISymbol symbol, string metadataName) => symbol
        .GetAttributes()
        .SingleOrDefault(attribute => string.Equals(
            attribute.AttributeClass?.ToDisplayString(TypeMetadataFormat),
            metadataName,
            StringComparison.Ordinal));

    private static IEnumerable<AttributeData> FindAttributes(
        ISymbol symbol,
        string metadataName) => symbol
        .GetAttributes()
        .Where(attribute => string.Equals(
            attribute.AttributeClass?.ToDisplayString(TypeMetadataFormat),
            metadataName,
            StringComparison.Ordinal));

    private static string GetMetadataName(INamedTypeSymbol type) =>
        type.ToDisplayString(TypeMetadataFormat);

    private static bool IsValidAssetPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.StartsWith("/", StringComparison.Ordinal) &&
        !path.EndsWith("/", StringComparison.Ordinal) &&
        !path.Contains("//", StringComparison.Ordinal) &&
        !path.Split('/').Any(static segment => string.IsNullOrWhiteSpace(segment) ||
            segment is "." or "..");
}
