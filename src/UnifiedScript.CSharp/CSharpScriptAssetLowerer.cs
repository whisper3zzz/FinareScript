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
                        typeSymbol,
                        hirType,
                        hirTypes,
                        semanticTypes,
                        semanticMethods);
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
                    functions));
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

            if (!TryClassifyType(property.Type, out ScriptStateTypeKind typeKind))
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.UnsupportedStateType,
                    UnifiedDiagnosticSeverity.Error,
                    $"Script state '{property.Name}' uses '{property.Type.ToDisplayString()}', but Declaration IR v0.1 supports only scalar and enum state.",
                    RoslynSourceSpan.FromNode(stateSyntax.Type, stateSyntax.SyntaxTree.FilePath)));
                continue;
            }

            if (stateSyntax.Initializer is null)
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.StateDefaultRequired,
                    UnifiedDiagnosticSeverity.Error,
                    $"Script state '{property.Name}' requires an explicit compile-time constant default.",
                    RoslynSourceSpan.FromNode(stateSyntax, stateSyntax.SyntaxTree.FilePath)));
                continue;
            }

            SemanticModel semanticModel = compilation.GetSemanticModel(stateSyntax.SyntaxTree);
            Optional<object?> constant = semanticModel.GetConstantValue(stateSyntax.Initializer.Value);
            if (!constant.HasValue || !TryCreateDefaultValue(
                    property.Type,
                    constant.Value,
                    stateSyntax.Initializer.Value,
                    semanticModel,
                    out JsonElement defaultValue))
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    DeclarationDiagnosticCodes.StateDefaultRequired,
                    UnifiedDiagnosticSeverity.Error,
                    $"Script state '{property.Name}' requires a finite, JSON-representable compile-time constant default.",
                    RoslynSourceSpan.FromNode(stateSyntax.Initializer.Value, stateSyntax.SyntaxTree.FilePath)));
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
                exposure));
        }

        return states;
    }

    private static IReadOnlyList<ScriptFunctionDeclaration> LowerFunctions(
        INamedTypeSymbol typeSymbol,
        HirTypeDeclaration type,
        IReadOnlyDictionary<string, HirTypeDeclaration> hirTypes,
        IReadOnlyDictionary<SemanticId, TypeDescriptor> types,
        IReadOnlyDictionary<SemanticId, MethodDescriptor> methods)
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
            ScriptParameterDeclaration[] parameters = method.Parameters
                .Select((parameter, index) => new ScriptParameterDeclaration(
                    parameter.Id,
                    parameter.Name,
                    parameterTypeNames[index]))
                .ToArray();
            string signature = $"{type.QualifiedName}.{method.Name}({string.Join(',', parameterTypeNames)})";
            functions.Add(new ScriptFunctionDeclaration(
                method.Id,
                method.Name,
                signature,
                types[descriptor.ReturnTypeId].MetadataName,
                method.IsStatic,
                method.IsPublic,
                method.Attributes?.Any(static attribute =>
                    attribute.Name == "UnrealEvent") == true,
                parameters));
        }

        return functions;
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

    private static bool TryClassifyType(ITypeSymbol type, out ScriptStateTypeKind kind)
    {
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
            _ => default,
        };
        return type.SpecialType is SpecialType.System_Boolean or
            SpecialType.System_SByte or SpecialType.System_Byte or
            SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Int32 or SpecialType.System_UInt32 or
            SpecialType.System_Int64 or SpecialType.System_UInt64 or
            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal or
            SpecialType.System_String || type.TypeKind == TypeKind.Enum;
    }

    private static bool TryCreateDefaultValue(
        ITypeSymbol type,
        object? constant,
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out JsonElement value)
    {
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
