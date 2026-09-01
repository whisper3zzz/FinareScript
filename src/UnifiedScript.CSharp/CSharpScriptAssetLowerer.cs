using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Semantics;

namespace UnifiedScript.CSharp;

internal sealed record ScriptAssetLoweringResult(
    ScriptAssetDocument? Document,
    IReadOnlyDictionary<SemanticId, SemanticId> DeclarationReferences,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics);

internal sealed class CSharpScriptAssetLowerer
{
    private const string ScriptAssetAttributeName = "UnifiedScript.ScriptAssetAttribute";
    private const string ExposeAttributeName = "UnifiedScript.ExposeAttribute";
    private const string UnrealEnumAttributeName = "UnifiedScript.Unreal.UnrealEnumAttribute";
    private const string UnrealStructAttributeName = "UnifiedScript.Unreal.UnrealStructAttribute";
    private const string UnrealStructFieldAttributeName =
        "UnifiedScript.Unreal.UnrealStructFieldAttribute";
    private const string UnrealScriptClassAttributeName =
        "UnifiedScript.Unreal.UnrealScriptClassAttribute";
    private const string UnrealBlueprintEventAttributeName =
        "UnifiedScript.Unreal.UnrealBlueprintEventAttribute";
    private const string UnrealInputActionAttributeName =
        "UnifiedScript.Unreal.UnrealInputActionAttribute";
    private const string UnrealInterfaceFunctionAttributeName =
        "UnifiedScript.Unreal.UnrealInterfaceFunctionAttribute";
    private const string UnrealEventAttributeName =
        "UnifiedScript.Unreal.UnrealEventAttribute";
    private const string UnrealTypeAttributeName =
        "UnifiedScript.Unreal.UnrealTypeAttribute";

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
        IReadOnlyDictionary<SemanticId, SemanticId>? declarationIdentities = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        ArgumentNullException.ThrowIfNull(hir);
        ArgumentNullException.ThrowIfNull(semantics);

        var diagnostics = new List<UnifiedDiagnostic>();
        var hirTypes = hir.Types.ToDictionary(static type => type.QualifiedName, StringComparer.Ordinal);
        var semanticTypes = semantics.Types.ToDictionary(static type => type.Id);
        var semanticMethods = semantics.Methods.ToDictionary(static method => method.Id);
        var declarations = new List<ScriptAssetDeclaration>();
        var declarationReferences = new Dictionary<SemanticId, SemanticId>();
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
                hirTypes.TryGetValue(qualifiedName, out HirTypeDeclaration? hirType);
                if (assetAttribute is null && hirType is null && !hasExposedMember)
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
                    assetAttribute is not null,
                    compilation,
                    declarationIdentities ?? new Dictionary<SemanticId, SemanticId>(),
                    declarationReferences,
                    diagnostics);
                IReadOnlyList<ScriptFunctionDeclaration> functions = hirType is null
                    ? []
                    : LowerFunctions(
                        compilation,
                        typeSymbol,
                        hirType,
                        hirTypes,
                        semanticTypes,
                        semanticMethods,
                        diagnostics);
                IReadOnlyList<ScriptLifecycleHookDeclaration> lifecycleHooks =
                    LowerLifecycleHooks(typeSymbol);
                SemanticId assetId = hirType?.Id ?? CSharpSemanticIds.ForSymbol("type", qualifiedName);
                if (assetAttribute is not null &&
                    TryReadNamedString(
                        assetAttribute,
                        nameof(global::UnifiedScript.ScriptAssetAttribute.ClassId),
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
                    lifecycleHooks));
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
            return new ScriptAssetLoweringResult(null, declarationReferences, diagnostics);
        }

        ScriptAssetDocument document = ScriptAssetJson.NormalizeAndValidate(ScriptAssetDocument.Create(
            hir.Id,
            semantics.ConfigurationId,
            declarations));
        return new ScriptAssetLoweringResult(document, declarationReferences, diagnostics);
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

    private static IReadOnlyList<ScriptStateDeclaration> LowerStates(
        INamedTypeSymbol type,
        bool ownsAllProperties,
        CSharpCompilation compilation,
        IReadOnlyDictionary<SemanticId, SemanticId> declarationIdentities,
        IDictionary<SemanticId, SemanticId> declarationReferences,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        var states = new List<ScriptStateDeclaration>();
        foreach (ISymbol member in type.GetMembers().OrderBy(static member => member.MetadataName, StringComparer.Ordinal))
        {
            AttributeData? exposureAttribute = FindAttribute(member, ExposeAttributeName);
            bool isCandidate =
                (member is IPropertySymbol candidateProperty &&
                    !candidateProperty.IsStatic && ownsAllProperties) ||
                exposureAttribute is not null;
            if (!isCandidate)
            {
                continue;
            }

            if (member is not IPropertySymbol property || property.IsStatic || property.IsIndexer ||
                !TryGetAutoPropertySyntax(property, out PropertyDeclarationSyntax? propertySyntax) ||
                property.GetMethod is null || property.SetMethod is null)
            {
                SyntaxNode? syntax = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.UnsupportedStateShape,
                    UnifiedDiagnosticSeverity.Error,
                    $"Script state '{member.Name}' must be a non-static, non-indexed auto-property with get and set accessors.",
                    syntax is null
                        ? SourceSpan.Unknown(type.Locations.FirstOrDefault()?.SourceTree?.FilePath ?? "<compilation>")
                        : RoslynSourceSpan.FromNode(syntax, syntax.SyntaxTree.FilePath)));
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
                    RoslynSourceSpan.FromNode(stateSyntax.Type, stateSyntax.SyntaxTree.FilePath)));
                continue;
            }

            if (!TryResolveStateDefault(
                    type,
                    property,
                    stateSyntax,
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
                        defaultDiagnosticNode.SyntaxTree.FilePath)));
                continue;
            }

            string containingName = GetMetadataName(type);
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
            declarationReferences.Add(CSharpSemanticIds.ForRoslynSymbol(property), declarationId);
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
        HirTypeDeclaration type,
        IReadOnlyDictionary<string, HirTypeDeclaration> hirTypes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        var declarations = new List<HirMethodDeclaration>(type.Methods.Count);
        var shadowedNames = new HashSet<string>(StringComparer.Ordinal);
        INamedTypeSymbol? currentSymbol = typeSymbol;
        bool inherited = false;
        while (currentSymbol is not null)
        {
            string currentName = GetMetadataName(currentSymbol);
            if (hirTypes.TryGetValue(currentName, out HirTypeDeclaration? currentType))
            {
                foreach (HirMethodDeclaration method in currentType.Methods)
                {
                    if (!inherited ||
                        (method.IsPublic && !method.IsStatic && !shadowedNames.Contains(method.Name)))
                    {
                        declarations.Add(method);
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
            ScriptParameterDeclaration[] parameters = method.Parameters
                .Select((parameter, index) => new ScriptParameterDeclaration(
                    parameter.Id,
                    parameter.Name,
                    parameterTypeNames[index],
                    LowerFunctionValueType(compilation, parameterTypeNames[index])))
                .ToArray();
            string signature = $"{type.QualifiedName}.{method.Name}({string.Join(',', parameterTypeNames)})";
            IReadOnlyList<ScriptInputActionBindingDeclaration> inputActions =
                LowerInputActions(method, methodSymbol, parameterTypeNames, diagnostics);
            IReadOnlyList<ScriptDelegateBindingDeclaration> delegateBindings =
                LowerDelegateBindings(method, methodSymbol, diagnostics);
            IReadOnlyList<ScriptInterfaceFunctionDeclaration> interfaceFunctions =
                LowerInterfaceFunctions(method, methodSymbol, diagnostics);
            bool blueprintEvent = methodSymbol is not null
                ? FindAttribute(methodSymbol, UnrealBlueprintEventAttributeName) is not null
                : method.Attributes?.Any(static attribute =>
                    attribute.Name == "UnrealBlueprintEvent") == true;
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
                    types[descriptor.ReturnTypeId].MetadataName),
                blueprintEvent
                    ? new ScriptBlueprintEventDeclaration(
                        ScriptBlueprintEventSuperMode.Explicit)
                    : null,
                inputActions,
                delegateBindings,
                interfaceFunctions));
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
                    parameterTypeNames is ["UnifiedScript.Unreal.UnrealVector"],
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
            if (baseName is "UnifiedScript.Unreal.UnrealActorScript" or
                "UnifiedScript.Unreal.UnrealActorComponentScript")
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
        string metadataName,
        int depth = 0)
    {
        string normalizedName = metadataName.EndsWith("?", StringComparison.Ordinal)
            ? metadataName[..^1]
            : metadataName;
        INamedTypeSymbol? type = compilation.GetTypeByMetadataName(normalizedName);
        if (type is null)
        {
            return null;
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
                    pair.Property.Type.ToDisplayString(TypeMetadataFormat),
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

    private static bool TryClassifyType(
        CSharpCompilation compilation,
        ITypeSymbol type,
        out ScriptStateTypeKind kind,
        out ScriptFunctionValueTypeDeclaration? unrealType)
    {
        string metadataName = type.ToDisplayString(TypeMetadataFormat);
        unrealType = null;
        kind = type.SpecialType switch
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
            _ when metadataName == "UnifiedScript.Unreal.UnrealName" => ScriptStateTypeKind.Name,
            _ when metadataName == "UnifiedScript.Unreal.UnrealText" => ScriptStateTypeKind.Text,
            _ when metadataName == "UnifiedScript.Unreal.UnrealObjectHandle" => ScriptStateTypeKind.Object,
            _ when metadataName == "UnifiedScript.Unreal.UnrealClassHandle" => ScriptStateTypeKind.Class,
            _ when metadataName == "UnifiedScript.Unreal.UnrealVector" => ScriptStateTypeKind.Struct,
            _ => default,
        };
        bool supported = type.SpecialType is SpecialType.System_Boolean or
            SpecialType.System_SByte or SpecialType.System_Byte or
            SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Int32 or SpecialType.System_UInt32 or
            SpecialType.System_Int64 or SpecialType.System_UInt64 or
            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal or
            SpecialType.System_String || type.TypeKind == TypeKind.Enum ||
            metadataName is "UnifiedScript.Unreal.UnrealName" or
                "UnifiedScript.Unreal.UnrealText" or
                "UnifiedScript.Unreal.UnrealObjectHandle" or
                "UnifiedScript.Unreal.UnrealClassHandle" or
                "UnifiedScript.Unreal.UnrealVector";
        if (supported && kind == ScriptStateTypeKind.Struct)
        {
            unrealType = new ScriptFunctionValueTypeDeclaration(
                ScriptFunctionValueTypeKind.Struct,
                "/Script/CoreUObject.Vector",
                "Vector",
                [
                    new ScriptFunctionStructFieldDeclaration(0, "X", "System.Double"),
                    new ScriptFunctionStructFieldDeclaration(1, "Y", "System.Double"),
                    new ScriptFunctionStructFieldDeclaration(2, "Z", "System.Double"),
                ]);
        }
        return supported;
    }

    private static bool TryResolveStateDefault(
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

        IMethodSymbol? parameterlessConstructor = containingType.InstanceConstructors
            .SingleOrDefault(static constructor =>
                !constructor.IsImplicitlyDeclared &&
                constructor.Parameters.Length == 0);
        if (parameterlessConstructor is not null &&
            parameterlessConstructor.DeclaringSyntaxReferences
                .Select(static reference => reference.GetSyntax())
                .OfType<ConstructorDeclarationSyntax>()
                .SingleOrDefault() is ConstructorDeclarationSyntax constructorSyntax)
        {
            SemanticModel constructorModel = compilation.GetSemanticModel(
                constructorSyntax.SyntaxTree);
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
            diagnosticMessage =
                "requires a finite, JSON-representable compile-time constant default.";
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
            left is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax };
    }

    private static bool TryCreateDefaultValue(
        ITypeSymbol type,
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out JsonElement value)
    {
        string metadataName = type.ToDisplayString(TypeMetadataFormat);
        if (metadataName is "UnifiedScript.Unreal.UnrealName" or
            "UnifiedScript.Unreal.UnrealText")
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

        if (metadataName == "UnifiedScript.Unreal.UnrealVector")
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

        if (metadataName is "UnifiedScript.Unreal.UnrealObjectHandle" or
            "UnifiedScript.Unreal.UnrealClassHandle")
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
        arguments = argumentList.Arguments.Select(static argument => argument.Expression).ToArray();
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
        string? category = null;
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            switch (argument.Key)
            {
                case nameof(UnifiedScript.ExposeAttribute.InstanceEditable):
                    instanceEditable = argument.Value.Value is true;
                    break;
                case nameof(UnifiedScript.ExposeAttribute.SaveGame):
                    saveGame = argument.Value.Value is true;
                    break;
                case nameof(UnifiedScript.ExposeAttribute.Replicated):
                    replicated = argument.Value.Value is true;
                    break;
                case nameof(UnifiedScript.ExposeAttribute.Category):
                    category = argument.Value.Value as string;
                    break;
            }
        }

        return new ScriptExposure(instanceEditable, saveGame, replicated, category);
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
