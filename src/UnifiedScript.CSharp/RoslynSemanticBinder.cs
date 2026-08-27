using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Semantics;

namespace UnifiedScript.CSharp;

public sealed class RoslynSemanticBinder
{
    private static readonly SymbolDisplayFormat TypeMetadataFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly SymbolDisplayFormat MethodMetadataFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeContainingType |
            SymbolDisplayMemberOptions.IncludeExplicitInterface |
            SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private readonly Dictionary<string, SemanticId> typeIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemanticId> methodIds = new(StringComparer.Ordinal);
    private readonly Dictionary<SemanticId, TypeDescriptor> types = [];
    private readonly Dictionary<SemanticId, MethodDescriptor> methods = [];
    private readonly Dictionary<SemanticId, BoundNodeInfo> nodes = [];
    private readonly Dictionary<SemanticId, BoundForEachInfo> forEachLoops = [];
    private readonly Dictionary<ISymbol, SemanticId> declaredSymbols = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, BindingContext> contexts = new(StringComparer.Ordinal);
    private readonly MethodEffectCatalog effectCatalog;

    public RoslynSemanticBinder(MethodEffectCatalog? effectCatalog = null)
    {
        this.effectCatalog = effectCatalog ?? MethodEffectCatalog.CreateDefault();
    }

    public SemanticBindingResult Bind(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree,
        HirCompilationUnit hir) => Bind(
            compilation,
            [syntaxTree],
            hir,
            SemanticId.FromKey($"semantic-configuration|{effectCatalog.Id}"));

    public SemanticBindingResult Bind(
        CSharpCompilation compilation,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        HirCompilationUnit hir,
        SemanticId configurationId)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        ArgumentNullException.ThrowIfNull(hir);

        Reset();
        foreach (SyntaxTree syntaxTree in syntaxTrees)
        {
            string path = string.IsNullOrEmpty(syntaxTree.FilePath) ? "<memory>" : syntaxTree.FilePath;
            if (!contexts.TryAdd(
                    path,
                    new BindingContext(
                        path,
                        compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: false),
                        new SyntaxIndex(syntaxTree.GetRoot()))))
            {
                throw new ArgumentException($"Duplicate syntax tree path '{path}'.", nameof(syntaxTrees));
            }
        }

        var diagnostics = new List<UnifiedDiagnostic>();

        RegisterTypeDeclarations(hir);
        RegisterMethodAndValueDeclarations(hir, diagnostics);
        BindExpressions(hir);
        ValidateForEachStatements(hir, diagnostics);
        ValidateConditionalExpressions(hir, diagnostics);
        ValidateShortCircuitExpressions(hir, diagnostics);
        ValidateCoalesceExpressions(hir, diagnostics);
        ValidateConditionalAccessExpressions(hir, diagnostics);

        var bound = new BoundCompilation(
            BoundCompilation.CurrentSchemaVersion,
            hir.Id,
            configurationId,
            types.Values.OrderBy(static type => type.Id.Value, StringComparer.Ordinal).ToArray(),
            methods.Values.OrderBy(static method => method.Id.Value, StringComparer.Ordinal).ToArray(),
            nodes.Values.OrderBy(static node => node.NodeId.Value, StringComparer.Ordinal).ToArray(),
            forEachLoops.Values
                .OrderBy(static loop => loop.NodeId.Value, StringComparer.Ordinal)
                .ToArray());
        return new SemanticBindingResult(bound, diagnostics);
    }

    private void Reset()
    {
        typeIds.Clear();
        methodIds.Clear();
        types.Clear();
        methods.Clear();
        nodes.Clear();
        forEachLoops.Clear();
        declaredSymbols.Clear();
        contexts.Clear();
    }

    private void RegisterTypeDeclarations(HirCompilationUnit hir)
    {
        foreach (HirTypeDeclaration type in hir.Types)
        {
            BindingContext context = GetContext(type);
            TypeDeclarationSyntax syntax = context.SyntaxIndex.Find<TypeDeclarationSyntax>(type);
            INamedTypeSymbol symbol = context.SemanticModel.GetDeclaredSymbol(syntax) ??
                throw new InvalidOperationException($"Roslyn did not bind type '{type.QualifiedName}'.");
            declaredSymbols[symbol] = type.Id;
            TypeDescriptor descriptor = GetTypeDescriptor(symbol, type.Id);
            nodes[type.Id] = new BoundNodeInfo(
                type.Id,
                descriptor.Id,
                DeclaredSymbolId: type.Id);
        }
    }

    private void RegisterMethodAndValueDeclarations(
        HirCompilationUnit hir,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        foreach (HirTypeDeclaration type in hir.Types)
        {
            foreach (HirMethodDeclaration method in type.Methods)
            {
                BindingContext context = GetContext(method);
                MethodDeclarationSyntax methodSyntax = context.SyntaxIndex.Find<MethodDeclarationSyntax>(method);
                IMethodSymbol methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodSyntax) ??
                    throw new InvalidOperationException($"Roslyn did not bind method '{method.Name}'.");
                declaredSymbols[methodSymbol] = method.Id;
                MethodDescriptor methodDescriptor = GetMethodDescriptor(methodSymbol, method.Id);
                nodes[method.Id] = new BoundNodeInfo(
                    method.Id,
                    methodDescriptor.ReturnTypeId,
                    DeclaredSymbolId: method.Id,
                    Effects: methodDescriptor.Effects);
                ValidateGraphSafeType(
                    methodDescriptor.ReturnTypeId,
                    "return type",
                    methodSyntax.ReturnType,
                    context.FilePath,
                    diagnostics);

                foreach (HirParameter parameter in method.Parameters)
                {
                    ParameterSyntax parameterSyntax = context.SyntaxIndex.Find<ParameterSyntax>(parameter);
                    IParameterSymbol parameterSymbol = context.SemanticModel.GetDeclaredSymbol(parameterSyntax) ??
                        throw new InvalidOperationException($"Roslyn did not bind parameter '{parameter.Name}'.");
                    declaredSymbols[parameterSymbol] = parameter.Id;
                    TypeDescriptor parameterType = GetTypeDescriptor(parameterSymbol.Type);
                    nodes[parameter.Id] = new BoundNodeInfo(
                        parameter.Id,
                        parameterType.Id,
                        DeclaredSymbolId: parameter.Id,
                        NullFlowState: GetInitialNullFlowState(parameterSymbol.Type));
                    ValidateGraphSafeType(
                        parameterType.Id,
                        $"parameter '{parameter.Name}'",
                        (SyntaxNode?)parameterSyntax.Type ?? parameterSyntax,
                        context.FilePath,
                        diagnostics);
                }

                foreach (HirVariableDeclarationStatement local in EnumerateStatements(method.Body)
                             .OfType<HirVariableDeclarationStatement>())
                {
                    LocalDeclarationStatementSyntax localSyntax =
                        context.SyntaxIndex.Find<LocalDeclarationStatementSyntax>(local);
                    VariableDeclaratorSyntax declarator = localSyntax.Declaration.Variables.Single();
                    ILocalSymbol localSymbol = context.SemanticModel.GetDeclaredSymbol(declarator) as ILocalSymbol ??
                        throw new InvalidOperationException($"Roslyn did not bind local '{local.Name}'.");
                    declaredSymbols[localSymbol] = local.Id;
                    TypeDescriptor localType = GetTypeDescriptor(localSymbol.Type);
                    nodes[local.Id] = new BoundNodeInfo(
                        local.Id,
                        localType.Id,
                        DeclaredSymbolId: local.Id,
                        NullFlowState: GetInitialNullFlowState(localSymbol.Type));
                }

                foreach (HirForVariableDeclarator local in method.Body
                             .DescendantsAndSelf()
                             .OfType<HirForVariableDeclarator>())
                {
                    VariableDeclaratorSyntax declarator =
                        context.SyntaxIndex.Find<VariableDeclaratorSyntax>(local);
                    ILocalSymbol localSymbol = context.SemanticModel.GetDeclaredSymbol(declarator) as ILocalSymbol ??
                        throw new InvalidOperationException($"Roslyn did not bind for local '{local.Name}'.");
                    declaredSymbols[localSymbol] = local.Id;
                    TypeDescriptor localType = GetTypeDescriptor(localSymbol.Type);
                    nodes[local.Id] = new BoundNodeInfo(
                        local.Id,
                        localType.Id,
                        DeclaredSymbolId: local.Id,
                        NullFlowState: GetInitialNullFlowState(localSymbol.Type));
                }

                foreach (HirForEachStatement forEach in method.Body
                             .DescendantsAndSelf()
                             .OfType<HirForEachStatement>())
                {
                    ForEachStatementSyntax forEachSyntax =
                        context.SyntaxIndex.Find<ForEachStatementSyntax>(forEach);
                    ILocalSymbol localSymbol = context.SemanticModel.GetDeclaredSymbol(forEachSyntax) ??
                        throw new InvalidOperationException(
                            $"Roslyn did not bind foreach local '{forEach.Variable.Name}'.");
                    declaredSymbols[localSymbol] = forEach.Variable.Id;
                    TypeDescriptor localType = GetTypeDescriptor(localSymbol.Type);
                    nodes[forEach.Variable.Id] = new BoundNodeInfo(
                        forEach.Variable.Id,
                        localType.Id,
                        DeclaredSymbolId: forEach.Variable.Id,
                        NullFlowState: GetInitialNullFlowState(localSymbol.Type));
                }

                foreach (HirPatternVariable patternVariable in method.Body
                             .DescendantsAndSelf()
                             .OfType<HirPatternVariable>())
                {
                    SingleVariableDesignationSyntax designation =
                        context.SyntaxIndex.Find<SingleVariableDesignationSyntax>(patternVariable);
                    ILocalSymbol localSymbol =
                        context.SemanticModel.GetDeclaredSymbol(designation) as ILocalSymbol ??
                        throw new InvalidOperationException(
                            $"Roslyn did not bind pattern local '{patternVariable.Name}'.");
                    declaredSymbols[localSymbol] = patternVariable.Id;
                    TypeDescriptor localType = GetTypeDescriptor(localSymbol.Type);
                    nodes[patternVariable.Id] = new BoundNodeInfo(
                        patternVariable.Id,
                        localType.Id,
                        DeclaredSymbolId: patternVariable.Id,
                        NullFlowState: GetInitialNullFlowState(localSymbol.Type));
                }
            }
        }
    }

    private void ValidateForEachStatements(
        HirCompilationUnit hir,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        foreach (HirForEachStatement forEach in hir
                     .DescendantsAndSelf()
                     .OfType<HirForEachStatement>())
        {
            BindingContext context = GetContext(forEach);
            ForEachStatementSyntax syntax = context.SyntaxIndex.Find<ForEachStatementSyntax>(forEach);
            ITypeSymbol? collectionType = context.SemanticModel.GetTypeInfo(syntax.Expression).Type;
            ILocalSymbol? iterationVariable = context.SemanticModel.GetDeclaredSymbol(syntax);
            ForEachIterationKind iterationKind = forEach.IterationKind switch
            {
                HirForEachIterationKind.Value => ForEachIterationKind.Value,
                HirForEachIterationKind.Ref => ForEachIterationKind.Ref,
                HirForEachIterationKind.RefReadOnly => ForEachIterationKind.RefReadOnly,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(forEach.IterationKind),
                    forEach.IterationKind,
                    "Unknown foreach iteration kind."),
            };
            ITypeSymbol? indexedElementType = collectionType switch
            {
                { SpecialType: SpecialType.System_String } =>
                    context.SemanticModel.Compilation.GetSpecialType(SpecialType.System_Char),
                IArrayTypeSymbol { Rank: 1, IsSZArray: true } array => array.ElementType,
                _ => null,
            };
            bool isIndexedCollection = collectionType?.SpecialType == SpecialType.System_String ||
                collectionType is IArrayTypeSymbol
                {
                    Rank: 1,
                    IsSZArray: true,
                } supportedArray && GetTypeDescriptor(supportedArray.ElementType).IsGraphSafe;
            bool hasExactIndexedElementType = indexedElementType is not null &&
                iterationVariable is not null &&
                SymbolEqualityComparer.IncludeNullability.Equals(
                    indexedElementType,
                    iterationVariable.Type);
            if (isIndexedCollection &&
                hasExactIndexedElementType &&
                iterationKind == ForEachIterationKind.Value)
            {
                TypeDescriptor collectionDescriptor = GetTypeDescriptor(collectionType!);
                TypeDescriptor elementDescriptor = GetTypeDescriptor(indexedElementType!);
                forEachLoops.Add(forEach.Id, new BoundForEachInfo(
                    forEach.Id,
                    ForEachIterationStrategy.Indexed,
                    iterationKind,
                    collectionDescriptor.Id,
                    elementDescriptor.Id,
                    null,
                    null,
                    null,
                    null,
                    null,
                    ForEachDisposalKind.None));
                continue;
            }

            ForEachStatementInfo statementInfo = context.SemanticModel.GetForEachStatementInfo(syntax);
            TypeDescriptor? enumerableCollection = collectionType is null
                ? null
                : GetTypeDescriptor(collectionType);
            TypeDescriptor? enumerableElement = statementInfo.ElementType is null
                ? null
                : GetTypeDescriptor(statementInfo.ElementType);
            bool hasGraphSafeCollectionContract =
                (enumerableCollection is
                    {
                        Kind: UnifiedTypeKind.Enumerable,
                        IsGraphSafe: true,
                    } && enumerableCollection.ElementTypeId == enumerableElement?.Id) ||
                (enumerableCollection is
                    {
                        Kind: UnifiedTypeKind.BorrowedSequence,
                    } && enumerableCollection.ElementTypeId == enumerableElement?.Id) ||
                (enumerableCollection is
                    {
                        IsGraphSafe: true,
                    } && enumerableCollection.Kind is not (
                        UnifiedTypeKind.Enumerable or UnifiedTypeKind.BorrowedSequence));
            bool hasCompatibleCurrentReference = iterationKind switch
            {
                ForEachIterationKind.Value => true,
                ForEachIterationKind.RefReadOnly =>
                    statementInfo.CurrentProperty is { ReturnsByRef: true } or
                        { ReturnsByRefReadonly: true },
                _ => false,
            };
            bool supportsEnumerator =
                !statementInfo.IsAsynchronous &&
                hasGraphSafeCollectionContract &&
                enumerableElement is { IsGraphSafe: true } &&
                iterationVariable is not null &&
                SymbolEqualityComparer.IncludeNullability.Equals(
                    statementInfo.ElementType,
                    iterationVariable.Type) &&
                statementInfo.ElementConversion.IsIdentity &&
                statementInfo.CurrentConversion.IsIdentity &&
                statementInfo.GetEnumeratorMethod is not null &&
                statementInfo.MoveNextMethod is not null &&
                statementInfo.CurrentProperty?.GetMethod is not null &&
                hasCompatibleCurrentReference;
            if (supportsEnumerator)
            {
                MethodDescriptor getEnumerator = GetMethodDescriptor(statementInfo.GetEnumeratorMethod!);
                MethodDescriptor moveNext = GetMethodDescriptor(statementInfo.MoveNextMethod!);
                MethodDescriptor getCurrent = GetMethodDescriptor(statementInfo.CurrentProperty!.GetMethod!);
                ForEachDisposalKind disposalKind = GetForEachDisposalKind(
                    context.SemanticModel.Compilation,
                    statementInfo);
                MethodDescriptor? dispose = disposalKind == ForEachDisposalKind.None
                    ? null
                    : GetMethodDescriptor(statementInfo.DisposeMethod!);
                TypeDescriptor enumerator = GetTypeDescriptor(
                    statementInfo.GetEnumeratorMethod!.ReturnType);
                forEachLoops.Add(forEach.Id, new BoundForEachInfo(
                    forEach.Id,
                    ForEachIterationStrategy.Enumerator,
                    iterationKind,
                    enumerableCollection!.Id,
                    enumerableElement!.Id,
                    enumerator.Id,
                    getEnumerator.Id,
                    moveNext.Id,
                    getCurrent.Id,
                    dispose?.Id,
                    disposalKind));
                continue;
            }

            diagnostics.Add(new UnifiedDiagnostic(
                SemanticDiagnosticCodes.ForEachCollectionNotSupported,
                UnifiedDiagnosticSeverity.Error,
                "Foreach requires System.String, a one-dimensional zero-based array, a borrowed " +
                "Span<T>/ReadOnlySpan<T>, or a graph-safe synchronous enumerable/pattern collection. " +
                "The iteration variable and Current value must use the exact graph-safe element " +
                "type without hidden conversions; borrowed references must be ref readonly.",
                forEach.Collection.Span,
                forEach.Collection.Id));
        }
    }

    private static ForEachDisposalKind GetForEachDisposalKind(
        Compilation compilation,
        ForEachStatementInfo statementInfo)
    {
        if (statementInfo.DisposeMethod is not IMethodSymbol disposeMethod ||
            statementInfo.GetEnumeratorMethod?.ReturnType is not ITypeSymbol enumeratorType)
        {
            return ForEachDisposalKind.None;
        }

        INamedTypeSymbol? disposable = compilation.GetTypeByMetadataName("System.IDisposable");
        if (disposable is null ||
            !SymbolEqualityComparer.Default.Equals(
                disposeMethod.ContainingType.OriginalDefinition,
                disposable))
        {
            return ForEachDisposalKind.Required;
        }

        if (compilation is CSharpCompilation csharpCompilation &&
            csharpCompilation.ClassifyConversion(enumeratorType, disposable).IsImplicit)
        {
            return ForEachDisposalKind.Required;
        }

        return enumeratorType.IsReferenceType &&
            enumeratorType is not INamedTypeSymbol { IsSealed: true }
                ? ForEachDisposalKind.Conditional
                : ForEachDisposalKind.None;
    }

    private void BindExpressions(HirCompilationUnit hir)
    {
        foreach (HirExpression expression in hir.Types
                     .SelectMany(static type => type.Methods)
                     .SelectMany(static method => EnumerateExpressions(method.Body)))
        {
            BindingContext context = GetContext(expression);
            ExpressionSyntax syntax = context.SyntaxIndex.Find<ExpressionSyntax>(expression);
            TypeInfo typeInfo = context.SemanticModel.GetTypeInfo(syntax);
            ITypeSymbol? expressionType = typeInfo.ConvertedType ?? typeInfo.Type;
            SemanticId? typeId = expressionType is null ? null : GetTypeDescriptor(expressionType).Id;
            SymbolInfo symbolInfo = context.SemanticModel.GetSymbolInfo(syntax);
            ISymbol? referencedSymbol = symbolInfo.Symbol;
            SemanticId? referencedSymbolId = referencedSymbol is null
                ? null
                : GetSymbolId(referencedSymbol);
            SemanticId? invokedMethodId = null;
            MethodEffects effects = MethodEffects.None;

            if (syntax is InvocationExpressionSyntax invocationSyntax &&
                context.SemanticModel.GetSymbolInfo(invocationSyntax).Symbol is IMethodSymbol invokedMethod)
            {
                MethodDescriptor descriptor = GetMethodDescriptor(invokedMethod);
                invokedMethodId = descriptor.Id;
                referencedSymbolId = descriptor.Id;
                effects = descriptor.Effects;
            }

            nodes[expression.Id] = new BoundNodeInfo(
                expression.Id,
                typeId,
                ReferencedSymbolId: referencedSymbolId,
                InvokedMethodId: invokedMethodId,
                Effects: effects,
                NullFlowState: GetNullFlowState(typeInfo));
        }
    }

    private static UnifiedNullFlowState GetNullFlowState(TypeInfo typeInfo)
    {
        NullableFlowState flowState = typeInfo.ConvertedType is not null
            ? typeInfo.ConvertedNullability.FlowState
            : typeInfo.Nullability.FlowState;
        return flowState switch
        {
            NullableFlowState.NotNull => UnifiedNullFlowState.NotNull,
            NullableFlowState.MaybeNull => UnifiedNullFlowState.MaybeNull,
            _ => UnifiedNullFlowState.Unknown,
        };
    }

    private static UnifiedNullFlowState GetInitialNullFlowState(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol nullable &&
            nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return UnifiedNullFlowState.MaybeNull;
        }

        if (type.IsValueType)
        {
            return UnifiedNullFlowState.NotNull;
        }

        return type.NullableAnnotation switch
        {
            NullableAnnotation.Annotated => UnifiedNullFlowState.MaybeNull,
            NullableAnnotation.NotAnnotated => UnifiedNullFlowState.NotNull,
            _ => UnifiedNullFlowState.Unknown,
        };
    }

    private void ValidateConditionalExpressions(
        HirCompilationUnit hir,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        var reportedNodes = new HashSet<SemanticId>();
        foreach (HirConditionalExpression conditional in hir
                     .DescendantsAndSelf()
                     .OfType<HirConditionalExpression>())
        {
            HirExpression? effectfulBranchNode = FindEffectfulExpression(
                [conditional.WhenTrue, conditional.WhenFalse]);
            if (effectfulBranchNode is null || !reportedNodes.Add(effectfulBranchNode.Id))
            {
                continue;
            }

            diagnostics.Add(new UnifiedDiagnostic(
                SemanticDiagnosticCodes.ConditionalBranchEffectsNotSupported,
                UnifiedDiagnosticSeverity.Error,
                "Conditional-expression branches must be effect-free so Graph can preserve " +
                "C# lazy branch evaluation. Move mutation, await, or effectful calls into " +
                "explicit if/else statements.",
                effectfulBranchNode.Span,
                effectfulBranchNode.Id));
        }
    }

    private void ValidateShortCircuitExpressions(
        HirCompilationUnit hir,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        var reportedNodes = new HashSet<SemanticId>();
        foreach (HirBinaryExpression binary in hir
                     .DescendantsAndSelf()
                     .OfType<HirBinaryExpression>()
                     .Where(static binary => binary.Operator is "&&" or "||"))
        {
            HirExpression? effectfulRightNode = FindEffectfulExpression([binary.Right]);
            if (effectfulRightNode is null || !reportedNodes.Add(effectfulRightNode.Id))
            {
                continue;
            }

            diagnostics.Add(new UnifiedDiagnostic(
                SemanticDiagnosticCodes.ShortCircuitRightEffectsNotSupported,
                UnifiedDiagnosticSeverity.Error,
                $"The right operand of '{binary.Operator}' must be effect-free so Graph can " +
                "preserve C# short-circuit evaluation. Move mutation, await, or effectful " +
                "calls into explicit control flow.",
                effectfulRightNode.Span,
                effectfulRightNode.Id));
        }
    }

    private void ValidateCoalesceExpressions(
        HirCompilationUnit hir,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        var reportedNodes = new HashSet<SemanticId>();
        foreach (HirBinaryExpression binary in hir
                     .DescendantsAndSelf()
                     .OfType<HirBinaryExpression>()
                     .Where(static binary => binary.Operator == "??"))
        {
            TypeDescriptor? leftType = GetBoundType(binary.Left);
            TypeDescriptor? rightType = GetBoundType(binary.Right);
            TypeDescriptor? resultType = GetBoundType(binary);
            if (leftType is null ||
                rightType is null ||
                resultType is null ||
                !IsSupportedCoalesceTypeShape(leftType, rightType, resultType))
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    SemanticDiagnosticCodes.CoalesceConversionNotSupported,
                    UnifiedDiagnosticSeverity.Error,
                    "Null coalescing in the Visual Profile requires a nullable, graph-safe " +
                    "left operand plus right and result types with the same base metadata " +
                    "type. Implicit and user-defined coalescing conversions are not yet " +
                    "serialized for shared VM execution.",
                    binary.Span,
                    binary.Id));
            }

            HirExpression? effectfulRightNode = FindEffectfulExpression([binary.Right]);
            if (effectfulRightNode is null || !reportedNodes.Add(effectfulRightNode.Id))
            {
                continue;
            }

            diagnostics.Add(new UnifiedDiagnostic(
                SemanticDiagnosticCodes.CoalesceRightEffectsNotSupported,
                UnifiedDiagnosticSeverity.Error,
                "The right operand of '??' must be effect-free so Graph can preserve C# " +
                "null-coalescing evaluation. Move mutation, await, or effectful calls into " +
                "explicit control flow.",
                effectfulRightNode.Span,
                effectfulRightNode.Id));
        }
    }

    private void ValidateConditionalAccessExpressions(
        HirCompilationUnit hir,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        var reportedEffectNodes = new HashSet<SemanticId>();
        foreach (HirConditionalAccessExpression conditionalAccess in hir
                     .DescendantsAndSelf()
                     .OfType<HirConditionalAccessExpression>())
        {
            TypeDescriptor? receiverType = GetBoundType(conditionalAccess.Receiver);
            TypeDescriptor? whenNotNullType = GetBoundType(conditionalAccess.WhenNotNull);
            TypeDescriptor? resultType = GetBoundType(conditionalAccess);
            if (receiverType is null ||
                whenNotNullType is null ||
                resultType is null ||
                !receiverType.IsGraphSafe ||
                !whenNotNullType.IsGraphSafe ||
                !resultType.IsGraphSafe ||
                resultType.Kind == UnifiedTypeKind.Void ||
                resultType.Nullability != UnifiedNullability.Nullable ||
                whenNotNullType.Kind != resultType.Kind ||
                !string.Equals(
                    whenNotNullType.MetadataName.TrimEnd('?'),
                    resultType.MetadataName.TrimEnd('?'),
                    StringComparison.Ordinal))
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    SemanticDiagnosticCodes.ConditionalAccessTypeShapeNotSupported,
                    UnifiedDiagnosticSeverity.Error,
                    "Null-conditional access in the Visual Profile requires graph-safe receiver, " +
                    "selected, and nullable result types whose selected/result metadata types " +
                    "differ only by nullability. Void and hidden conversion shapes are not yet " +
                    "serialized for shared VM execution.",
                    conditionalAccess.Span,
                    conditionalAccess.Id));
            }

            HirExpression? effectfulNode = FindEffectfulExpression(
                [conditionalAccess.WhenNotNull]);
            if (effectfulNode is null || !reportedEffectNodes.Add(effectfulNode.Id))
            {
                continue;
            }

            diagnostics.Add(new UnifiedDiagnostic(
                SemanticDiagnosticCodes.ConditionalAccessEffectsNotSupported,
                UnifiedDiagnosticSeverity.Error,
                "The conditionally evaluated portion of '?.' must be effect-free so Graph can " +
                "preserve C# null-conditional evaluation. Move mutation, await, or effectful " +
                "calls into explicit control flow.",
                effectfulNode.Span,
                effectfulNode.Id));
        }
    }

    private TypeDescriptor? GetBoundType(HirExpression expression) =>
        nodes.TryGetValue(expression.Id, out BoundNodeInfo? binding) &&
        binding.TypeId is SemanticId typeId &&
        types.TryGetValue(typeId, out TypeDescriptor? type)
            ? type
            : null;

    private static bool IsSupportedCoalesceTypeShape(
        TypeDescriptor left,
        TypeDescriptor right,
        TypeDescriptor result) =>
        left.IsGraphSafe &&
        right.IsGraphSafe &&
        result.IsGraphSafe &&
        left.Nullability == UnifiedNullability.Nullable &&
        right.Id == result.Id &&
        left.Kind == result.Kind &&
        string.Equals(
            left.MetadataName.TrimEnd('?'),
            result.MetadataName.TrimEnd('?'),
            StringComparison.Ordinal);

    private HirExpression? FindEffectfulExpression(IEnumerable<HirExpression> roots) => roots
        .SelectMany(static root => root.DescendantsAndSelf())
        .OfType<HirExpression>()
        .FirstOrDefault(node =>
            node is HirAssignmentExpression or HirAwaitExpression ||
            node is HirUnaryExpression { Operator: "++" or "--" } ||
            nodes.TryGetValue(node.Id, out BoundNodeInfo? binding) &&
            binding.Effects != MethodEffects.None);

    private void ValidateGraphSafeType(
        SemanticId typeId,
        string role,
        SyntaxNode syntax,
        string sourcePath,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        TypeDescriptor descriptor = types[typeId];
        if (descriptor.IsGraphSafe)
        {
            return;
        }

        diagnostics.Add(new UnifiedDiagnostic(
            SemanticDiagnosticCodes.GraphUnsafeSignature,
            UnifiedDiagnosticSeverity.Error,
            $"The {role} '{descriptor.DisplayName}' is not graph-safe in semantic profile v0.1.",
            RoslynSourceSpan.FromNode(syntax, sourcePath)));
    }

    private TypeDescriptor GetTypeDescriptor(ITypeSymbol type, SemanticId? preferredId = null)
    {
        string canonicalName = GetTypeCanonicalName(type);
        if (typeIds.TryGetValue(canonicalName, out SemanticId existingId))
        {
            return types[existingId];
        }

        SemanticId id = preferredId ?? SemanticId.FromKey($"roslyn-type|{canonicalName}");
        typeIds.Add(canonicalName, id);
        types.Add(id, new TypeDescriptor(
            id,
            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            type.ToDisplayString(TypeMetadataFormat),
            UnifiedTypeKind.Object,
            UnifiedNullability.Oblivious,
            false));
        try
        {
            (UnifiedTypeKind kind, UnifiedNullability nullability, bool isGraphSafe) =
                ClassifyType(type);
            var descriptor = new TypeDescriptor(
                id,
                type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                type.ToDisplayString(TypeMetadataFormat),
                kind,
                nullability,
                isGraphSafe,
                kind is UnifiedTypeKind.Enumerable or UnifiedTypeKind.BorrowedSequence &&
                    TryGetSequenceElementType(type, out ITypeSymbol? element)
                    ? GetTypeDescriptor(element!).Id
                    : null);
            types[id] = descriptor;
            return descriptor;
        }
        catch
        {
            _ = typeIds.Remove(canonicalName);
            _ = types.Remove(id);
            throw;
        }
    }

    private MethodDescriptor GetMethodDescriptor(IMethodSymbol method, SemanticId? preferredId = null)
    {
        IMethodSymbol definition = method.OriginalDefinition;
        string canonicalName = GetMethodCanonicalName(definition);
        if (methodIds.TryGetValue(canonicalName, out SemanticId existingId))
        {
            return methods[existingId];
        }

        TypeDescriptor containingType = GetTypeDescriptor(method.ContainingType);
        TypeDescriptor returnType = GetTypeDescriptor(method.ReturnType);
        IReadOnlyList<ParameterDescriptor> parameters = method.Parameters
            .Select(parameter => new ParameterDescriptor(
                parameter.Name,
                GetTypeDescriptor(parameter.Type).Id))
            .ToArray();
        SemanticId id = preferredId ?? SemanticId.FromKey($"roslyn-method|{canonicalName}");
        var descriptor = new MethodDescriptor(
            id,
            method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            method.ToDisplayString(MethodMetadataFormat),
            containingType.Id,
            returnType.Id,
            parameters,
            method.IsStatic,
            InferEffects(method));
        methodIds.Add(canonicalName, id);
        methods.Add(id, descriptor);
        return descriptor;
    }

    private SemanticId GetSymbolId(ISymbol symbol)
    {
        if (declaredSymbols.TryGetValue(symbol, out SemanticId declaredId))
        {
            return declaredId;
        }

        if (symbol is IMethodSymbol method)
        {
            return GetMethodDescriptor(method).Id;
        }

        return CSharpSemanticIds.ForRoslynSymbol(symbol);
    }

    private (UnifiedTypeKind Kind, UnifiedNullability Nullability, bool IsGraphSafe) ClassifyType(
        ITypeSymbol type)
    {
        UnifiedNullability nullability = type.NullableAnnotation switch
        {
            NullableAnnotation.Annotated => UnifiedNullability.Nullable,
            NullableAnnotation.NotAnnotated => UnifiedNullability.NotNull,
            _ => UnifiedNullability.Oblivious,
        };

        if (type is INamedTypeSymbol nullable &&
            nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            TypeDescriptor underlying = GetTypeDescriptor(nullable.TypeArguments[0]);
            return (underlying.Kind, UnifiedNullability.Nullable, underlying.IsGraphSafe);
        }

        if (IsTaskLike(type, out INamedTypeSymbol? task))
        {
            bool safeResult = task!.TypeArguments.Length == 0 ||
                task.TypeArguments.All(argument => GetTypeDescriptor(argument).IsGraphSafe);
            return (UnifiedTypeKind.TaskLike, nullability, safeResult);
        }

        if (type.SpecialType != SpecialType.None)
        {
            return ClassifySpecialType(type.SpecialType, nullability);
        }

        if (TryGetEnumerableElementType(type, out ITypeSymbol? enumerableElement))
        {
            return (
                UnifiedTypeKind.Enumerable,
                nullability,
                GetTypeDescriptor(enumerableElement!).IsGraphSafe);
        }

        if (TryGetBorrowedSequenceElementType(type, out _))
        {
            return (UnifiedTypeKind.BorrowedSequence, nullability, false);
        }

        return type switch
        {
            IArrayTypeSymbol { Rank: 1, IsSZArray: true } array =>
                (UnifiedTypeKind.Array, nullability, GetTypeDescriptor(array.ElementType).IsGraphSafe),
            IArrayTypeSymbol => (UnifiedTypeKind.Array, nullability, false),
            ITypeParameterSymbol => (UnifiedTypeKind.TypeParameter, nullability, false),
            INamedTypeSymbol { TypeKind: TypeKind.Enum } =>
                (UnifiedTypeKind.Enum, nullability, true),
            INamedTypeSymbol { TypeKind: TypeKind.Interface } =>
                (UnifiedTypeKind.Interface, nullability, false),
            INamedTypeSymbol { TypeKind: TypeKind.Delegate } =>
                (UnifiedTypeKind.Delegate, nullability, false),
            INamedTypeSymbol { TypeKind: TypeKind.Struct } unrealValue
                when IsUnrealGraphValueType(unrealValue) =>
                (UnifiedTypeKind.Struct, nullability, true),
            INamedTypeSymbol { TypeKind: TypeKind.Struct } =>
                (UnifiedTypeKind.Struct, nullability, false),
            INamedTypeSymbol { TypeKind: TypeKind.Class } unrealType
                when IsUnrealReflectedType(unrealType) =>
                (UnifiedTypeKind.Object, nullability, true),
            INamedTypeSymbol { TypeKind: TypeKind.Class } =>
                (UnifiedTypeKind.Object, nullability, false),
            _ => ClassifySpecialType(type.SpecialType, nullability),
        };
    }

    private static bool IsUnrealReflectedType(INamedTypeSymbol type) =>
        type.GetAttributes().Any(static attribute =>
            string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                "UnifiedScript.Unreal.UnrealTypeAttribute",
                StringComparison.Ordinal));

    private static bool IsUnrealGraphValueType(INamedTypeSymbol type) =>
        type.GetAttributes().Any(static attribute =>
            string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                "UnifiedScript.Unreal.UnrealValueAttribute",
                StringComparison.Ordinal));

    private static bool TryGetEnumerableElementType(
        ITypeSymbol type,
        out ITypeSymbol? elementType)
    {
        if (type is not INamedTypeSymbol named)
        {
            elementType = null;
            return false;
        }

        INamedTypeSymbol[] candidates = [named, .. named.AllInterfaces];
        ITypeSymbol[] candidateElements = candidates
            .Where(static candidate =>
                candidate.OriginalDefinition.SpecialType ==
                SpecialType.System_Collections_Generic_IEnumerable_T)
            .Select(static candidate => candidate.TypeArguments[0])
            .ToArray();
        var elements = new List<ITypeSymbol>();
        foreach (ITypeSymbol candidateElement in candidateElements)
        {
            if (!elements.Any(existing =>
                    SymbolEqualityComparer.IncludeNullability.Equals(existing, candidateElement)))
            {
                elements.Add(candidateElement);
            }
        }

        if (elements.Count != 1)
        {
            elementType = null;
            return false;
        }

        elementType = elements[0];
        return true;
    }

    private static bool TryGetSequenceElementType(
        ITypeSymbol type,
        out ITypeSymbol? elementType) =>
        TryGetEnumerableElementType(type, out elementType) ||
        TryGetBorrowedSequenceElementType(type, out elementType);

    private static bool TryGetBorrowedSequenceElementType(
        ITypeSymbol type,
        out ITypeSymbol? elementType)
    {
        if (type is INamedTypeSymbol
            {
                Arity: 1,
                ContainingNamespace: { } containingNamespace,
            } named &&
            containingNamespace.ToDisplayString() == "System" &&
            named.Name is "Span" or "ReadOnlySpan")
        {
            elementType = named.TypeArguments[0];
            return true;
        }

        elementType = null;
        return false;
    }

    private static (UnifiedTypeKind Kind, UnifiedNullability Nullability, bool IsGraphSafe)
        ClassifySpecialType(SpecialType specialType, UnifiedNullability nullability) => specialType switch
        {
            SpecialType.System_Void => (UnifiedTypeKind.Void, UnifiedNullability.NotNull, true),
            SpecialType.System_Boolean => (UnifiedTypeKind.Boolean, nullability, true),
            SpecialType.System_Char or
            SpecialType.System_SByte or
            SpecialType.System_Byte or
            SpecialType.System_Int16 or
            SpecialType.System_UInt16 or
            SpecialType.System_Int32 or
            SpecialType.System_UInt32 or
            SpecialType.System_Int64 or
            SpecialType.System_UInt64 or
            SpecialType.System_IntPtr or
            SpecialType.System_UIntPtr => (UnifiedTypeKind.Integer, nullability, true),
            SpecialType.System_Single or
            SpecialType.System_Double or
            SpecialType.System_Decimal => (UnifiedTypeKind.FloatingPoint, nullability, true),
            SpecialType.System_String => (UnifiedTypeKind.String, nullability, true),
            SpecialType.System_Object => (UnifiedTypeKind.Object, nullability, false),
            _ => (UnifiedTypeKind.Unknown, UnifiedNullability.Unknown, false),
        };

    private MethodEffects InferEffects(IMethodSymbol method)
    {
        MethodEffects effects = method.MethodKind == MethodKind.BuiltinOperator
            ? MethodEffects.None
            : effectCatalog.Resolve(new MethodEffectSubject(
                method.ContainingAssembly?.Identity.Name ?? "source",
                method.ContainingType.ToDisplayString(TypeMetadataFormat),
                method.MetadataName));
        if (method.IsAsync || IsTaskLike(method.ReturnType, out _))
        {
            effects |= MethodEffects.Async | MethodEffects.Latent;
        }

        return effects;
    }

    private BindingContext GetContext(HirNode node)
    {
        if (contexts.TryGetValue(node.Span.FilePath, out BindingContext? context))
        {
            return context;
        }

        throw new InvalidOperationException(
            $"No Roslyn syntax tree was registered for HIR path '{node.Span.FilePath}'.");
    }

    private static bool IsTaskLike(ITypeSymbol type, out INamedTypeSymbol? namedType)
    {
        namedType = type as INamedTypeSymbol;
        return namedType is not null &&
            namedType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" &&
            namedType.Name is "Task" or "ValueTask";
    }

    private static string GetTypeCanonicalName(ITypeSymbol type)
    {
        string assemblyName = type.ContainingAssembly?.Identity.Name ?? "source";
        string displayName = type.ToDisplayString(TypeMetadataFormat);
        return $"{assemblyName}|{displayName}|{type.NullableAnnotation}";
    }

    private static string GetMethodCanonicalName(IMethodSymbol method)
    {
        string assemblyName = method.ContainingAssembly?.Identity.Name ?? "source";
        string displayName = method.ToDisplayString(MethodMetadataFormat);
        return $"{assemblyName}|{displayName}";
    }

    private static IEnumerable<HirStatement> EnumerateStatements(HirBlockStatement block)
    {
        foreach (HirStatement statement in block.Statements)
        {
            yield return statement;
            switch (statement)
            {
                case HirBlockStatement nested:
                    foreach (HirStatement child in EnumerateStatements(nested))
                    {
                        yield return child;
                    }

                    break;
                case HirIfStatement ifStatement:
                    foreach (HirStatement child in EnumerateStatements(ifStatement.Then))
                    {
                        yield return child;
                    }

                    if (ifStatement.Else is not null)
                    {
                        foreach (HirStatement child in EnumerateStatements(ifStatement.Else))
                        {
                            yield return child;
                        }
                    }

                    break;
                case HirWhileStatement whileStatement:
                    foreach (HirStatement child in EnumerateStatements(whileStatement.Body))
                    {
                        yield return child;
                    }

                    break;
                case HirDoWhileStatement doWhileStatement:
                    foreach (HirStatement child in EnumerateStatements(doWhileStatement.Body))
                    {
                        yield return child;
                    }

                    break;
                case HirForStatement forStatement:
                    foreach (HirStatement child in EnumerateStatements(forStatement.Body))
                    {
                        yield return child;
                    }

                    break;
                case HirForEachStatement forEachStatement:
                    foreach (HirStatement child in EnumerateStatements(forEachStatement.Body))
                    {
                        yield return child;
                    }

                    break;
                case HirSwitchStatement switchStatement:
                    foreach (HirSwitchSection section in switchStatement.Sections)
                    {
                        foreach (HirStatement child in EnumerateStatements(section.Body))
                        {
                            yield return child;
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<HirExpression> EnumerateExpressions(HirBlockStatement block)
    {
        foreach (HirStatement statement in EnumerateStatements(block))
        {
            IEnumerable<HirExpression> roots = statement switch
            {
                HirVariableDeclarationStatement { Initializer: not null } variable =>
                    [variable.Initializer],
                HirExpressionStatement expression => [expression.Expression],
                HirIfStatement ifStatement => [ifStatement.Condition],
                HirWhileStatement whileStatement => [whileStatement.Condition],
                HirDoWhileStatement doWhileStatement => [doWhileStatement.Condition],
                HirForStatement forStatement => GetForExpressionRoots(forStatement),
                HirForEachStatement forEachStatement => [forEachStatement.Collection],
                HirSwitchStatement switchStatement => GetSwitchExpressionRoots(switchStatement),
                HirReturnStatement { Expression: not null } returnStatement =>
                    [returnStatement.Expression],
                _ => [],
            };
            foreach (HirExpression root in roots)
            {
                foreach (HirExpression expression in EnumerateExpressionTree(root))
                {
                    yield return expression;
                }
            }
        }
    }

    private static IEnumerable<HirExpression> GetForExpressionRoots(HirForStatement statement)
    {
        if (statement.Declaration is not null)
        {
            foreach (HirExpression initializer in statement.Declaration.Variables
                         .Where(static variable => variable.Initializer is not null)
                         .Select(static variable => variable.Initializer!))
            {
                yield return initializer;
            }
        }

        foreach (HirExpression initializer in statement.Initializers)
        {
            yield return initializer;
        }

        if (statement.Condition is not null)
        {
            yield return statement.Condition;
        }

        foreach (HirExpression incrementor in statement.Incrementors)
        {
            yield return incrementor;
        }
    }

    private static IEnumerable<HirExpression> GetSwitchExpressionRoots(
        HirSwitchStatement statement)
    {
        yield return statement.GoverningExpression;
        foreach (HirPatternSwitchLabel label in statement.Sections
                     .SelectMany(static section => section.Labels)
                     .OfType<HirPatternSwitchLabel>())
        {
            foreach (HirExpression expression in label.Pattern
                         .DescendantsAndSelf()
                         .OfType<HirExpression>())
            {
                yield return expression;
            }

            if (label.Guard is not null)
            {
                yield return label.Guard;
            }
        }
    }

    private static IEnumerable<HirExpression> EnumerateExpressionTree(HirExpression expression)
    {
        yield return expression;
        IEnumerable<HirExpression> children = expression switch
        {
            HirMemberAccessExpression member => [member.Target],
            HirConditionalAccessExpression conditionalAccess =>
                [conditionalAccess.Receiver, conditionalAccess.WhenNotNull],
            HirInvocationExpression invocation => [invocation.Target, .. invocation.Arguments],
            HirBinaryExpression binary => [binary.Left, binary.Right],
            HirUnaryExpression unary => [unary.Operand],
            HirAssignmentExpression assignment => [assignment.Target, assignment.Value],
            HirAwaitExpression awaitExpression => [awaitExpression.Operand],
            HirCastExpression cast => [cast.Operand],
            HirConditionalExpression conditional =>
                [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
            _ => [],
        };
        foreach (HirExpression child in children)
        {
            foreach (HirExpression descendant in EnumerateExpressionTree(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed record BindingContext(
        string FilePath,
        SemanticModel SemanticModel,
        SyntaxIndex SyntaxIndex);

    private readonly record struct SpanKey(int StartLine, int StartColumn, int EndLine, int EndColumn)
    {
        public static SpanKey From(SourceSpan span) => new(
            span.Start.Line,
            span.Start.Column,
            span.End.Line,
            span.End.Column);

        public static SpanKey From(SyntaxNode syntax)
        {
            FileLinePositionSpan span = syntax.GetLocation().GetLineSpan();
            return new SpanKey(
                span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1,
                span.EndLinePosition.Line + 1,
                span.EndLinePosition.Character + 1);
        }
    }

    private sealed class SyntaxIndex
    {
        private readonly IReadOnlyDictionary<SpanKey, IReadOnlyList<SyntaxNode>> nodesBySpan;

        public SyntaxIndex(SyntaxNode root)
        {
            nodesBySpan = root
                .DescendantNodesAndSelf()
                .GroupBy(SpanKey.From)
                .ToDictionary(
                    static group => group.Key,
                    static group => (IReadOnlyList<SyntaxNode>)group.ToArray());
        }

        public T Find<T>(HirNode hirNode)
            where T : SyntaxNode
        {
            SpanKey key = SpanKey.From(hirNode.Span);
            if (nodesBySpan.TryGetValue(key, out IReadOnlyList<SyntaxNode>? candidates))
            {
                T? match = candidates.OfType<T>().FirstOrDefault();
                if (match is not null)
                {
                    return match;
                }
            }

            throw new InvalidOperationException(
                $"Could not correlate HIR node {hirNode.Id} ({hirNode.GetType().Name}) with Roslyn syntax.");
        }
    }
}
