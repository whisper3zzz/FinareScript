using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UnifiedScript.CSharp;
using UnifiedScript.Unreal;

namespace UnifiedScript.Cli;

/// <summary>
/// Finds generated-Unreal wrapper use before the referenced wrapper exists. Explicit aliases and
/// fully-qualified names remain supported, while ordinary imports can use bare wrapper names.
/// Roslyn symbols reject user-defined or referenced type collisions; syntax supplies the missing
/// generated identity during the bootstrap pass. Invocation argument types also select an exact
/// reflected overload when that choice is deterministic.
/// </summary>
public sealed class UnrealApiSourceDependencyScanner
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate", "do",
        "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int",
        "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void",
        "volatile", "while",
    };

    public UnrealApiDependencyRequestSet Scan(
        string projectName,
        IReadOnlyList<CSharpSourceDocument> sources,
        UnrealReflectionManifest reflection,
        UnrealApiContract baseContract) =>
        Scan(
            projectName,
            new CSharpCompilationRequest("UnifiedScript.ApiSurfaceScan", sources),
            reflection,
            baseContract);

    public UnrealApiDependencyRequestSet Scan(
        string projectName,
        CSharpCompilationRequest compilationRequest,
        UnrealReflectionManifest reflection,
        UnrealApiContract baseContract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(compilationRequest);
        UnrealReflectionManifest normalizedReflection =
            UnrealReflectionJson.NormalizeAndValidate(reflection);
        UnrealApiContract normalizedContract = UnrealApiContractJson.Deserialize(
            UnrealApiContractJson.Serialize(baseContract, writeIndented: false));
        CSharpSourceDocument[] userSources = compilationRequest.Sources
            .Where(static source => !IsGenerated(source.Source))
            .OrderBy(static source => source.LogicalPath, StringComparer.Ordinal)
            .ToArray();
        EnsureUniquePaths(userSources);

        SyntaxTree[] syntaxTrees = compilationRequest.Sources.Select(source =>
            CSharpSyntaxTree.ParseText(
                SourceText.From(source.Source, Encoding.UTF8),
                compilationRequest.Settings.ParseOptions,
                source.LogicalPath)).ToArray();
        CSharpCompilation compilation = new CSharpCompilationHost().Create(
            compilationRequest,
            syntaxTrees);
        IReadOnlyDictionary<string, SyntaxTree> treesByPath = syntaxTrees.ToDictionary(
            static tree => tree.FilePath,
            StringComparer.Ordinal);
        UsingImport[] globalImports = syntaxTrees
            .SelectMany(static tree => tree.GetCompilationUnitRoot().Usings)
            .Where(static directive =>
                directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) &&
                !directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
            .Select(CreateImport)
            .Where(static import => import is not null)
            .Cast<UsingImport>()
            .OrderBy(static import => import.Alias, StringComparer.Ordinal)
            .ThenBy(static import => import.Target, StringComparer.Ordinal)
            .ToArray();

        IReadOnlyDictionary<string, ReflectedTypeTarget[]> typeTargets =
            CreateTypeTargets(normalizedReflection);
        var occurrences = new List<RequestOccurrence>();
        foreach (CSharpSourceDocument source in userSources)
        {
            SyntaxTree tree = treesByPath[source.LogicalPath];
            ScanSource(
                source,
                tree,
                compilation.GetSemanticModel(tree, ignoreAccessibility: true),
                globalImports,
                normalizedContract.ManagedNamespace,
                typeTargets,
                occurrences);
        }

        UnrealApiDependencyRequest[] requests = occurrences
            .GroupBy(static occurrence => new RequestIdentity(
                occurrence.TypeName,
                occurrence.TypePath,
                occurrence.MemberName))
            .OrderBy(static group => group.Key.TypePath, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.TypeName, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.MemberName, StringComparer.Ordinal)
            .Select(group =>
            {
                RequestIdentity identity = group.Key;
                string requestId = "USAPI-" + StableHash(
                    $"{identity.TypePath ?? identity.TypeName}|{identity.MemberName}")[..16];
                RequestOccurrence first = group.First();
                return new UnrealApiDependencyRequest(
                    requestId,
                    identity.TypeName,
                    identity.TypePath,
                    identity.MemberName,
                    "contract-member",
                    "generated-binding",
                    $"{first.ManagedTypeName}.{first.SourceMemberName}",
                    group.Select(static occurrence => occurrence.Location)
                        .Distinct()
                        .OrderBy(static location => location.SourcePath, StringComparer.Ordinal)
                        .ThenBy(static location => location.StartLine)
                        .ThenBy(static location => location.StartColumn)
                        .ToArray());
            }).ToArray();

        return UnrealApiDependencyRequestJson.Deserialize(
            UnrealApiDependencyRequestJson.Serialize(new UnrealApiDependencyRequestSet(
                UnrealApiDependencyRequestSet.CurrentSchemaVersion,
                projectName,
                normalizedContract.ManagedNamespace,
                FingerprintSources(userSources),
                UnrealApiDevelopmentSurfaceJson.Fingerprint(normalizedContract),
                requests),
                writeIndented: false));
    }

    private static void ScanSource(
        CSharpSourceDocument source,
        SyntaxTree tree,
        SemanticModel semanticModel,
        IReadOnlyList<UsingImport> globalImports,
        string managedNamespace,
        IReadOnlyDictionary<string, ReflectedTypeTarget[]> typeTargets,
        ICollection<RequestOccurrence> occurrences)
    {
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

        var bindings = new List<TypedBinding>();
        foreach (VariableDeclarationSyntax declaration in root.DescendantNodes()
                     .OfType<VariableDeclarationSyntax>())
        {
            ReflectedTypeTarget[]? declaredTargets = null;
            bool isVar = declaration.Type is IdentifierNameSyntax identifier &&
                identifier.Identifier.ValueText == "var";
            if (!isVar)
            {
                _ = TryResolveType(
                    declaration.Type,
                    semanticModel,
                    globalImports,
                    managedNamespace,
                    typeTargets,
                    out declaredTargets);
            }
            TextSpan scope = DeclarationScope(declaration);
            foreach (VariableDeclaratorSyntax variable in declaration.Variables)
            {
                ReflectedTypeTarget[]? targets = declaredTargets;
                if (targets is null && isVar && variable.Initializer is not null)
                {
                    _ = TryResolveValueExpression(
                        variable.Initializer.Value,
                        semanticModel,
                        globalImports,
                        managedNamespace,
                        typeTargets,
                        out targets);
                }
                if (targets is null)
                {
                    continue;
                }
                bindings.Add(new TypedBinding(
                    variable.Identifier.ValueText,
                    targets,
                    scope.Start,
                    scope.End,
                    variable.SpanStart));
            }
        }
        foreach (ParameterSyntax parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (parameter.Type is null || !TryResolveType(
                    parameter.Type,
                    semanticModel,
                    globalImports,
                    managedNamespace,
                    typeTargets,
                    out ReflectedTypeTarget[]? targets))
            {
                continue;
            }
            TextSpan scope = DeclarationScope(parameter);
            bindings.Add(new TypedBinding(
                parameter.Identifier.ValueText,
                targets!,
                scope.Start,
                scope.End,
                parameter.SpanStart));
        }
        foreach (PropertyDeclarationSyntax property in root.DescendantNodes()
                     .OfType<PropertyDeclarationSyntax>())
        {
            if (!TryResolveType(
                    property.Type,
                    semanticModel,
                    globalImports,
                    managedNamespace,
                    typeTargets,
                    out ReflectedTypeTarget[]? targets))
            {
                continue;
            }
            TextSpan scope = DeclarationScope(property);
            bindings.Add(new TypedBinding(
                property.Identifier.ValueText,
                targets!,
                scope.Start,
                scope.End,
                property.SpanStart));
        }

        foreach (MemberAccessExpressionSyntax access in root.DescendantNodes()
                     .OfType<MemberAccessExpressionSyntax>())
        {
            if (!TryResolveReceiver(
                    access.Expression,
                    access.SpanStart,
                    bindings,
                    semanticModel,
                    globalImports,
                    managedNamespace,
                    typeTargets,
                    out ReflectedTypeTarget[]? targets))
            {
                continue;
            }
            string sourceMemberName = access.Name.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(sourceMemberName))
            {
                continue;
            }
            string memberName = ResolveMemberSelector(
                access,
                sourceMemberName,
                targets!,
                semanticModel);
            FileLinePositionSpan lineSpan = tree.GetLineSpan(access.Name.Span);
            var location = new UnrealApiSourceSpan(
                source.LogicalPath,
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1,
                lineSpan.EndLinePosition.Line + 1,
                lineSpan.EndLinePosition.Character + 1);
            ReflectedTypeTarget first = targets![0];
            occurrences.Add(new RequestOccurrence(
                first.TypeName,
                targets.Length == 1 ? first.TypePath : null,
                first.ManagedTypeName,
                memberName,
                sourceMemberName,
                location));
        }
    }

    private static bool TryResolveReceiver(
        ExpressionSyntax expression,
        int position,
        IReadOnlyList<TypedBinding> bindings,
        SemanticModel semanticModel,
        IReadOnlyList<UsingImport> globalImports,
        string managedNamespace,
        IReadOnlyDictionary<string, ReflectedTypeTarget[]> typeTargets,
        out ReflectedTypeTarget[]? targets)
    {
        expression = Unwrap(expression);
        if (TryResolveSemanticType(
                expression,
                semanticModel,
                managedNamespace,
                typeTargets,
                out targets))
        {
            return true;
        }
        switch (expression)
        {
            case IdentifierNameSyntax identifier:
                targets = FindBinding(identifier.Identifier.ValueText, position, bindings);
                return targets is not null || TryResolveTypeName(
                    identifier.Identifier.Text,
                    identifier,
                    semanticModel,
                    globalImports,
                    managedNamespace,
                    typeTargets,
                    out targets);
            case MemberAccessExpressionSyntax memberAccess
                when memberAccess.Expression is ThisExpressionSyntax or BaseExpressionSyntax:
                targets = FindBinding(memberAccess.Name.Identifier.ValueText, position, bindings);
                return targets is not null;
            case ObjectCreationExpressionSyntax creation:
                return TryResolveType(
                    creation.Type,
                    semanticModel,
                    globalImports,
                    managedNamespace,
                    typeTargets,
                    out targets);
            case CastExpressionSyntax cast:
                return TryResolveType(
                    cast.Type,
                    semanticModel,
                    globalImports,
                    managedNamespace,
                    typeTargets,
                    out targets);
            case DefaultExpressionSyntax defaultExpression:
                return TryResolveType(
                    defaultExpression.Type,
                    semanticModel,
                    globalImports,
                    managedNamespace,
                    typeTargets,
                    out targets);
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AsExpression) &&
                binary.Right is TypeSyntax asType:
                return TryResolveType(
                    asType,
                    semanticModel,
                    globalImports,
                    managedNamespace,
                    typeTargets,
                    out targets);
            case MemberAccessExpressionSyntax typeExpression:
                return TryResolveTypeName(
                    typeExpression.ToString(),
                    typeExpression,
                    semanticModel,
                    globalImports,
                    managedNamespace,
                    typeTargets,
                    out targets);
            default:
                targets = null;
                return false;
        }
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }
        if (expression is PostfixUnaryExpressionSyntax postfix &&
            postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression))
        {
            return Unwrap(postfix.Operand);
        }
        return expression;
    }

    private static bool TryResolveValueExpression(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        IReadOnlyList<UsingImport> globalImports,
        string managedNamespace,
        IReadOnlyDictionary<string, ReflectedTypeTarget[]> typeTargets,
        out ReflectedTypeTarget[]? targets)
    {
        expression = Unwrap(expression);
        if (TryResolveSemanticType(
                expression,
                semanticModel,
                managedNamespace,
                typeTargets,
                out targets))
        {
            return true;
        }
        return expression switch
        {
            ObjectCreationExpressionSyntax creation => TryResolveType(
                creation.Type,
                semanticModel,
                globalImports,
                managedNamespace,
                typeTargets,
                out targets),
            CastExpressionSyntax cast => TryResolveType(
                cast.Type,
                semanticModel,
                globalImports,
                managedNamespace,
                typeTargets,
                out targets),
            DefaultExpressionSyntax defaultExpression => TryResolveType(
                defaultExpression.Type,
                semanticModel,
                globalImports,
                managedNamespace,
                typeTargets,
                out targets),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AsExpression) &&
                binary.Right is TypeSyntax asType => TryResolveType(
                    asType,
                    semanticModel,
                    globalImports,
                    managedNamespace,
                    typeTargets,
                    out targets),
            _ => NoTargets(out targets),
        };
    }

    private static bool TryResolveType(
        TypeSyntax type,
        SemanticModel semanticModel,
        IReadOnlyList<UsingImport> globalImports,
        string managedNamespace,
        IReadOnlyDictionary<string, ReflectedTypeTarget[]> typeTargets,
        out ReflectedTypeTarget[]? targets)
    {
        while (type is NullableTypeSyntax nullable)
        {
            type = nullable.ElementType;
        }
        return TryResolveTypeName(
            type.ToString(),
            type,
            semanticModel,
            globalImports,
            managedNamespace,
            typeTargets,
            out targets);
    }

    private static bool TryResolveTypeName(
        string typeText,
        SyntaxNode context,
        SemanticModel semanticModel,
        IReadOnlyList<UsingImport> globalImports,
        string managedNamespace,
        IReadOnlyDictionary<string, ReflectedTypeTarget[]> typeTargets,
        out ReflectedTypeTarget[]? targets)
    {
        string normalized = NormalizeName(typeText);
        string? managedTypeName = null;
        bool bareName = false;
        string namespacePrefix = managedNamespace + ".";
        if (normalized.StartsWith(namespacePrefix, StringComparison.Ordinal))
        {
            managedTypeName = normalized[namespacePrefix.Length..];
        }
        else
        {
            ImportScope scope = GetImportScope(context, globalImports, managedNamespace);
            foreach (string alias in scope.Aliases)
            {
                string aliasPrefix = alias + ".";
                if (normalized.StartsWith(aliasPrefix, StringComparison.Ordinal))
                {
                    managedTypeName = normalized[aliasPrefix.Length..];
                    break;
                }
            }
            if (managedTypeName is null && !normalized.Contains('.') &&
                scope.NamespaceImported)
            {
                managedTypeName = normalized;
                bareName = true;
            }
        }
        if (managedTypeName is null || managedTypeName.Contains('.') ||
            !typeTargets.TryGetValue(managedTypeName, out targets))
        {
            targets = null;
            return false;
        }
        if (bareName && !CanUseBareGeneratedName(
                context,
                managedTypeName,
                semanticModel,
                managedNamespace))
        {
            targets = null;
            return false;
        }
        return true;
    }

    private static ReflectedTypeTarget[]? FindBinding(
        string name,
        int position,
        IReadOnlyList<TypedBinding> bindings) => bindings
        .Where(binding => binding.Name == name &&
            position >= binding.ScopeStart && position <= binding.ScopeEnd &&
            (binding.DeclarationPosition <= position || binding.ScopeStart == 0))
        .OrderByDescending(static binding => binding.ScopeStart)
        .ThenByDescending(static binding => binding.DeclarationPosition)
        .Select(static binding => binding.Targets)
        .FirstOrDefault();

    private static bool TryResolveSemanticType(
        SyntaxNode node,
        SemanticModel semanticModel,
        string managedNamespace,
        IReadOnlyDictionary<string, ReflectedTypeTarget[]> typeTargets,
        out ReflectedTypeTarget[]? targets)
    {
        ITypeSymbol? symbol = node switch
        {
            ExpressionSyntax expression => semanticModel.GetTypeInfo(expression).Type,
            _ => null,
        };
        if (symbol is INamedTypeSymbol named && symbol.TypeKind != TypeKind.Error &&
            IsGeneratedType(named, managedNamespace) &&
            typeTargets.TryGetValue(ToIdentifier(named.Name), out targets))
        {
            return true;
        }
        targets = null;
        return false;
    }

    private static bool CanUseBareGeneratedName(
        SyntaxNode node,
        string managedTypeName,
        SemanticModel semanticModel,
        string managedNamespace)
    {
        ITypeSymbol? type = node switch
        {
            ExpressionSyntax expression => semanticModel.GetTypeInfo(expression).Type,
            _ => null,
        };
        if (type is not null && type.TypeKind != TypeKind.Error)
        {
            return type is INamedTypeSymbol named && IsGeneratedType(named, managedNamespace);
        }

        SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(node);
        ISymbol[] resolved = (symbolInfo.Symbol is null
                ? symbolInfo.CandidateSymbols
                : [symbolInfo.Symbol])
            .Where(static symbol => symbol is not ITypeSymbol { TypeKind: TypeKind.Error })
            .ToArray();
        if (resolved.Any(symbol => symbol is not INamedTypeSymbol named ||
                !IsGeneratedType(named, managedNamespace)))
        {
            return false;
        }

        string lookupName = managedTypeName.StartsWith('@')
            ? managedTypeName[1..]
            : managedTypeName;
        return semanticModel.LookupSymbols(node.SpanStart, name: lookupName).All(symbol =>
            symbol is INamedTypeSymbol named && IsGeneratedType(named, managedNamespace));
    }

    private static bool IsGeneratedType(INamedTypeSymbol type, string managedNamespace) =>
        type.ContainingNamespace?.ToDisplayString() == managedNamespace;

    private static ImportScope GetImportScope(
        SyntaxNode context,
        IReadOnlyList<UsingImport> globalImports,
        string managedNamespace)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        bool namespaceImported = false;
        foreach (SyntaxNode container in context.AncestorsAndSelf().Where(static node =>
                     node is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax))
        {
            SyntaxList<UsingDirectiveSyntax> usings = container switch
            {
                BaseNamespaceDeclarationSyntax declaration => declaration.Usings,
                CompilationUnitSyntax unit => unit.Usings,
                _ => default,
            };
            foreach (UsingDirectiveSyntax directive in usings.Where(static directive =>
                         !directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) &&
                         !directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)))
            {
                AddImport(CreateImport(directive), aliases, managedNamespace, ref namespaceImported);
            }
        }
        foreach (UsingImport import in globalImports)
        {
            AddImport(import, aliases, managedNamespace, ref namespaceImported);
        }
        return new ImportScope(
            aliases.Where(pair => pair.Value == managedNamespace)
                .Select(static pair => pair.Key)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            namespaceImported);
    }

    private static UsingImport? CreateImport(UsingDirectiveSyntax directive)
    {
        string target = NormalizeName(directive.Name?.ToString());
        if (target.Length == 0)
        {
            return null;
        }
        return new UsingImport(
            directive.Alias?.Name.Identifier.ValueText,
            target);
    }

    private static void AddImport(
        UsingImport? import,
        IDictionary<string, string> aliases,
        string managedNamespace,
        ref bool namespaceImported)
    {
        if (import is null)
        {
            return;
        }
        if (import.Alias is null)
        {
            namespaceImported |= import.Target == managedNamespace;
            return;
        }
        aliases.TryAdd(import.Alias, import.Target);
    }

    private static bool NoTargets(out ReflectedTypeTarget[]? targets)
    {
        targets = null;
        return false;
    }

    private static string ResolveMemberSelector(
        MemberAccessExpressionSyntax access,
        string memberName,
        IReadOnlyList<ReflectedTypeTarget> targets,
        SemanticModel semanticModel)
    {
        if (targets.Count != 1)
        {
            return memberName;
        }

        UnrealReflectedDelegate? reflectedDelegate = targets[0].ReflectedType.Delegates
            .SingleOrDefault(item =>
                "Subscribe" + item.Name == memberName || item.Name + "Event" == memberName);
        if (reflectedDelegate is not null)
        {
            return reflectedDelegate.Name;
        }
        if (access.Parent is not InvocationExpressionSyntax invocation ||
            invocation.Expression != access)
        {
            return memberName;
        }

        UnrealReflectedFunction[] overloads = targets[0].ReflectedType.Functions
            .Where(function => function.Name == memberName ||
                UnrealManagedNaming.FunctionName(function) == memberName)
            .ToArray();
        if (overloads.Length <= 1)
        {
            return memberName;
        }

        UnrealReflectedFunction[] compatible = overloads.Where(function =>
            InvocationMatches(function, invocation.ArgumentList.Arguments, semanticModel)).ToArray();
        return compatible.Length == 1
            ? FunctionSelector(compatible[0])
            : memberName;
    }

    private static bool InvocationMatches(
        UnrealReflectedFunction function,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        SemanticModel semanticModel)
    {
        IReadOnlyList<UnrealReflectedParameter> parameters = function.Parameters;
        if (arguments.Count > parameters.Count)
        {
            return false;
        }

        var assigned = new bool[parameters.Count];
        int nextPositional = 0;
        foreach (ArgumentSyntax argument in arguments)
        {
            int parameterIndex;
            if (argument.NameColon is not null)
            {
                string argumentName = argument.NameColon.Name.Identifier.ValueText;
                parameterIndex = Enumerable.Range(0, parameters.Count).FirstOrDefault(
                    index => ParameterNameMatches(parameters[index].Name, argumentName),
                    -1);
            }
            else
            {
                while (nextPositional < parameters.Count && assigned[nextPositional])
                {
                    nextPositional++;
                }
                parameterIndex = nextPositional++;
            }
            if (parameterIndex < 0 || parameterIndex >= parameters.Count ||
                assigned[parameterIndex])
            {
                return false;
            }

            UnrealReflectedParameter parameter = parameters[parameterIndex];
            if (!DirectionMatches(argument.RefKindKeyword, parameter.Direction) ||
                !ArgumentTypeMatches(argument.Expression, parameter.Type, semanticModel))
            {
                return false;
            }
            assigned[parameterIndex] = true;
        }

        for (int index = 0; index < parameters.Count; index++)
        {
            if (!assigned[index] &&
                (parameters[index].Direction != UnrealParameterDirection.Input ||
                 parameters[index].DefaultValue is null))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ParameterNameMatches(string reflectedName, string argumentName) =>
        reflectedName == argumentName ||
        char.ToLowerInvariant(reflectedName[0]) + reflectedName[1..] == argumentName;

    private static bool DirectionMatches(
        SyntaxToken refKind,
        UnrealParameterDirection direction) => direction switch
        {
            UnrealParameterDirection.Input => refKind.RawKind == 0,
            UnrealParameterDirection.Output => refKind.IsKind(SyntaxKind.OutKeyword),
            UnrealParameterDirection.InputOutput => refKind.IsKind(SyntaxKind.RefKeyword),
            _ => false,
        };

    private static bool ArgumentTypeMatches(
        ExpressionSyntax expression,
        UnrealTypeReference expected,
        SemanticModel semanticModel)
    {
        if (expression.IsKind(SyntaxKind.NullLiteralExpression))
        {
            return expected.Kind is UnrealValueKind.String or UnrealValueKind.Object or
                UnrealValueKind.Class or UnrealValueKind.Interface or UnrealValueKind.SoftObject or UnrealValueKind.SoftClass or
                UnrealValueKind.Array or UnrealValueKind.Set or UnrealValueKind.Map;
        }
        if (expression.IsKind(SyntaxKind.DefaultLiteralExpression) ||
            expression is DeclarationExpressionSyntax)
        {
            return true;
        }

        TypeInfo typeInfo = semanticModel.GetTypeInfo(expression);
        ITypeSymbol? actual = typeInfo.Type ?? typeInfo.ConvertedType;
        if (actual is null || actual.TypeKind == TypeKind.Error)
        {
            return true;
        }
        if (actual.SpecialType != SpecialType.None)
        {
            return (actual.SpecialType, expected.Kind) switch
            {
                (SpecialType.System_Boolean, UnrealValueKind.Boolean) => true,
                (SpecialType.System_SByte, UnrealValueKind.Int8) => true,
                (SpecialType.System_Byte, UnrealValueKind.UInt8) => true,
                (SpecialType.System_Int16, UnrealValueKind.Int16) => true,
                (SpecialType.System_UInt16, UnrealValueKind.UInt16) => true,
                (SpecialType.System_Int32, UnrealValueKind.Int32) => true,
                (SpecialType.System_UInt32, UnrealValueKind.UInt32) => true,
                (SpecialType.System_Int64, UnrealValueKind.Int64) => true,
                (SpecialType.System_UInt64, UnrealValueKind.UInt64) => true,
                (SpecialType.System_Single, UnrealValueKind.Float32) => true,
                (SpecialType.System_Double, UnrealValueKind.Float64) => true,
                (SpecialType.System_String, UnrealValueKind.String) => true,
                _ => false,
            };
        }
        if (actual is not INamedTypeSymbol named)
        {
            return true;
        }

        string actualName = named.Name;
        return expected.Kind switch
        {
            UnrealValueKind.Name => actualName == "UnrealName",
            UnrealValueKind.Text => actualName == "UnrealText",
            UnrealValueKind.Object => actualName == "UnrealObjectHandle",
            UnrealValueKind.Interface => actualName == "UnrealObjectHandle",
            UnrealValueKind.Class => actualName == "UnrealClassHandle",
            UnrealValueKind.SoftObject => actualName == "UnrealSoftObjectHandle",
            UnrealValueKind.SoftClass => actualName == "UnrealSoftClassHandle",
            UnrealValueKind.Enum => actualName == "UnrealEnumValue" ||
                actualName == ManagedNameFromNative(expected.NativeName),
            UnrealValueKind.Struct when expected.TypePath == "/Script/CoreUObject.Vector" =>
                actualName == "UnrealVector",
            UnrealValueKind.Struct when expected.TypePath == "/Script/CoreUObject.Rotator" =>
                actualName == "UnrealRotator",
            UnrealValueKind.Struct when expected.TypePath == "/Script/CoreUObject.Transform" =>
                actualName == "UnrealTransform",
            UnrealValueKind.Struct when expected.TypePath is not null =>
                actualName == ManagedNameFromNative(expected.NativeName),
            UnrealValueKind.Array => actualName is "IReadOnlyList" or "List" or "Array",
            UnrealValueKind.Set => actualName is "IReadOnlySet" or "HashSet",
            UnrealValueKind.Map => actualName is "IReadOnlyDictionary" or "Dictionary",
            _ => true,
        };
    }

    private static string ManagedNameFromNative(string nativeName) => ToIdentifier(
        nativeName.Length > 1 && nativeName[0] == 'F' ? nativeName[1..] : nativeName);

    private static string FunctionSelector(UnrealReflectedFunction function)
    {
        int open = function.Path.IndexOf('(', StringComparison.Ordinal);
        return open >= 0
            ? function.Path[(function.Path.LastIndexOf(':') + 1)..]
            : function.Name;
    }

    private static IReadOnlyDictionary<string, ReflectedTypeTarget[]> CreateTypeTargets(
        UnrealReflectionManifest reflection)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var targets = new Dictionary<string, ReflectedTypeTarget[]>(StringComparer.Ordinal);
        foreach (UnrealReflectedType type in reflection.Types)
        {
            string candidate = ToIdentifier(
                type.Kind == UnrealReflectedTypeKind.Struct &&
                type.NativeName.Length > 1 && type.NativeName[0] == 'F'
                    ? type.NativeName[1..]
                    : type.Name);
            string managedName = used.Add(candidate)
                ? candidate
                : AddStableSuffix(candidate, type.Path, used);
            targets.Add(
                managedName,
                [new ReflectedTypeTarget(type.Name, type.Path, managedName, type)]);
        }
        return targets;
    }

    private static TextSpan DeclarationScope(SyntaxNode declaration)
    {
        SyntaxNode? scope = declaration.Ancestors().FirstOrDefault(static ancestor => ancestor is
            BlockSyntax or BaseTypeDeclarationSyntax or BaseMethodDeclarationSyntax or
            LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or
            ForStatementSyntax or ForEachStatementSyntax or UsingStatementSyntax);
        return scope?.Span ?? declaration.SyntaxTree.GetRoot().FullSpan;
    }

    private static void EnsureUniquePaths(IReadOnlyList<CSharpSourceDocument> sources)
    {
        string? duplicate = sources.GroupBy(static source => source.LogicalPath, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate managed source path '{duplicate}'.");
        }
    }

    private static string FingerprintSources(IReadOnlyList<CSharpSourceDocument> sources)
    {
        var text = new StringBuilder();
        foreach (CSharpSourceDocument source in sources)
        {
            text.Append(source.LogicalPath).Append('\0')
                .Append(source.Source.ReplaceLineEndings("\n")).Append('\0');
        }
        return StableHash(text.ToString());
    }

    private static bool IsGenerated(string source) =>
        source.AsSpan().TrimStart().StartsWith("// <auto-generated", StringComparison.Ordinal);

    private static string NormalizeName(string? value) =>
        (value ?? string.Empty).Replace("global::", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

    private static string ToIdentifier(string value)
    {
        var result = new StringBuilder(value.Length + 1);
        if (value.Length == 0 || !char.IsLetter(value[0]) && value[0] != '_')
        {
            result.Append('_');
        }
        foreach (char character in value)
        {
            result.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }
        string identifier = result.ToString();
        return Keywords.Contains(identifier) ? $"@{identifier}" : identifier;
    }

    private static string AddStableSuffix(string candidate, string path, ISet<string> used)
    {
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..8];
        string unique = $"{candidate}_{suffix}";
        if (!used.Add(unique))
        {
            throw new InvalidOperationException($"Stable binding-name collision for '{path}'.");
        }
        return unique;
    }

    private static string StableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ReflectedTypeTarget(
        string TypeName,
        string TypePath,
        string ManagedTypeName,
        UnrealReflectedType ReflectedType);

    private sealed record UsingImport(string? Alias, string Target);

    private sealed record ImportScope(
        IReadOnlyList<string> Aliases,
        bool NamespaceImported);

    private sealed record TypedBinding(
        string Name,
        ReflectedTypeTarget[] Targets,
        int ScopeStart,
        int ScopeEnd,
        int DeclarationPosition);

    private sealed record RequestOccurrence(
        string TypeName,
        string? TypePath,
        string ManagedTypeName,
        string MemberName,
        string SourceMemberName,
        UnrealApiSourceSpan Location);

    private sealed record RequestIdentity(
        string TypeName,
        string? TypePath,
        string MemberName);
}
