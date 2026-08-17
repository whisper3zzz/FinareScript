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

        var bound = new BoundCompilation(
            BoundCompilation.CurrentSchemaVersion,
            hir.Id,
            configurationId,
            types.Values.OrderBy(static type => type.Id.Value, StringComparer.Ordinal).ToArray(),
            methods.Values.OrderBy(static method => method.Id.Value, StringComparer.Ordinal).ToArray(),
            nodes.Values.OrderBy(static node => node.NodeId.Value, StringComparer.Ordinal).ToArray());
        return new SemanticBindingResult(bound, diagnostics);
    }

    private void Reset()
    {
        typeIds.Clear();
        methodIds.Clear();
        types.Clear();
        methods.Clear();
        nodes.Clear();
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
                        DeclaredSymbolId: parameter.Id);
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
                        DeclaredSymbolId: local.Id);
                }
            }
        }
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
                Effects: effects);
        }
    }

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

        (UnifiedTypeKind kind, UnifiedNullability nullability, bool isGraphSafe) = ClassifyType(type);
        SemanticId id = preferredId ?? SemanticId.FromKey($"roslyn-type|{canonicalName}");
        var descriptor = new TypeDescriptor(
            id,
            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            type.ToDisplayString(TypeMetadataFormat),
            kind,
            nullability,
            isGraphSafe);
        typeIds.Add(canonicalName, id);
        types.Add(id, descriptor);
        return descriptor;
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

        string assemblyName = symbol.ContainingAssembly?.Identity.Name ?? "source";
        string displayName = TrimGlobalPrefix(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        return SemanticId.FromKey($"roslyn-symbol|{assemblyName}|{symbol.Kind}|{displayName}");
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

        return type switch
        {
            IArrayTypeSymbol => (UnifiedTypeKind.Array, nullability, false),
            ITypeParameterSymbol => (UnifiedTypeKind.TypeParameter, nullability, false),
            INamedTypeSymbol { TypeKind: TypeKind.Enum } =>
                (UnifiedTypeKind.Enum, nullability, true),
            INamedTypeSymbol { TypeKind: TypeKind.Interface } =>
                (UnifiedTypeKind.Interface, nullability, false),
            INamedTypeSymbol { TypeKind: TypeKind.Delegate } =>
                (UnifiedTypeKind.Delegate, nullability, false),
            INamedTypeSymbol { TypeKind: TypeKind.Struct } =>
                (UnifiedTypeKind.Struct, nullability, false),
            INamedTypeSymbol { TypeKind: TypeKind.Class } =>
                (UnifiedTypeKind.Object, nullability, false),
            _ => ClassifySpecialType(type.SpecialType, nullability),
        };
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

    private static string TrimGlobalPrefix(string value) =>
        value.Replace("global::", string.Empty, StringComparison.Ordinal);

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
            }
        }
    }

    private static IEnumerable<HirExpression> EnumerateExpressions(HirBlockStatement block)
    {
        foreach (HirStatement statement in EnumerateStatements(block))
        {
            HirExpression? root = statement switch
            {
                HirVariableDeclarationStatement variable => variable.Initializer,
                HirExpressionStatement expression => expression.Expression,
                HirIfStatement ifStatement => ifStatement.Condition,
                HirWhileStatement whileStatement => whileStatement.Condition,
                HirReturnStatement returnStatement => returnStatement.Expression,
                _ => null,
            };
            if (root is null)
            {
                continue;
            }

            foreach (HirExpression expression in EnumerateExpressionTree(root))
            {
                yield return expression;
            }
        }
    }

    private static IEnumerable<HirExpression> EnumerateExpressionTree(HirExpression expression)
    {
        yield return expression;
        IEnumerable<HirExpression> children = expression switch
        {
            HirMemberAccessExpression member => [member.Target],
            HirInvocationExpression invocation => [invocation.Target, .. invocation.Arguments],
            HirBinaryExpression binary => [binary.Left, binary.Right],
            HirUnaryExpression unary => [unary.Operand],
            HirAssignmentExpression assignment => [assignment.Target, assignment.Value],
            HirAwaitExpression awaitExpression => [awaitExpression.Operand],
            HirCastExpression cast => [cast.Operand],
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
