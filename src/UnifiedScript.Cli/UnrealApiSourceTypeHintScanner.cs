using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UnifiedScript.CSharp;
using UnifiedScript.Unreal;

namespace UnifiedScript.Cli;

public sealed record UnrealApiSourceTypeHint(
    string Id,
    string ManagedTypeName,
    IReadOnlyList<UnrealApiSourceSpan> Locations);

public sealed record UnrealApiSourceTypeHintSet(
    string SchemaVersion,
    string ProjectName,
    string SourceFingerprint,
    string ContractFingerprint,
    IReadOnlyList<UnrealApiSourceTypeHint> Hints)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.unreal-api-source-type-hints/v0.1";
}

public static class UnrealApiSourceTypeHintJson
{
    public static string Serialize(UnrealApiSourceTypeHintSet hints, bool writeIndented = true) =>
        JsonSerializer.Serialize(Normalize(hints), CreateOptions(writeIndented)) + "\n";

    public static UnrealApiSourceTypeHintSet Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        UnrealApiSourceTypeHintSet? hints = JsonSerializer.Deserialize<UnrealApiSourceTypeHintSet>(
            json,
            CreateOptions(writeIndented: true));
        return Normalize(hints ?? throw new JsonException(
            "The Unreal API source type-hint set was empty."));
    }

    private static UnrealApiSourceTypeHintSet Normalize(UnrealApiSourceTypeHintSet hints)
    {
        if (hints.SchemaVersion != UnrealApiSourceTypeHintSet.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(hints.ProjectName) ||
            !IsFingerprint(hints.SourceFingerprint) ||
            !IsFingerprint(hints.ContractFingerprint) ||
            hints.Hints is null)
        {
            throw new JsonException("The Unreal API source type-hint set is invalid.");
        }

        UnrealApiSourceTypeHint[] normalized = hints.Hints.Select(hint =>
        {
            if (hint is null || !hint.Id.StartsWith("USAPI-TYPE-", StringComparison.Ordinal) ||
                !SyntaxFacts.IsValidIdentifier(hint.ManagedTypeName) ||
                hint.Locations is null || hint.Locations.Count == 0)
            {
                throw new JsonException("The Unreal API source type-hint set contains an invalid hint.");
            }
            UnrealApiSourceSpan[] locations = hint.Locations
                .Distinct()
                .OrderBy(static location => location.SourcePath, StringComparer.Ordinal)
                .ThenBy(static location => location.StartLine)
                .ThenBy(static location => location.StartColumn)
                .ToArray();
            return hint with { Locations = locations };
        }).OrderBy(static hint => hint.ManagedTypeName, StringComparer.Ordinal).ToArray();
        if (normalized.Select(static hint => hint.Id).Distinct(StringComparer.Ordinal).Count() !=
                normalized.Length ||
            normalized.Select(static hint => hint.ManagedTypeName)
                .Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new JsonException("The Unreal API source type-hint set contains duplicate hints.");
        }
        return hints with
        {
            ProjectName = hints.ProjectName.Trim(),
            Hints = normalized,
        };
    }

    private static bool IsFingerprint(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static JsonSerializerOptions CreateOptions(bool writeIndented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = writeIndented,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// Discovers only generated-namespace type names that can be proven not to bind to a user or
/// referenced C# type. The output is a bounded bootstrap hint set for Unreal reflection export;
/// Unreal remains responsible for resolving each name to one exact reflected path.
/// </summary>
public sealed class UnrealApiSourceTypeHintScanner
{
    public UnrealApiSourceTypeHintSet Scan(
        string projectName,
        CSharpCompilationRequest compilationRequest,
        UnrealApiContract baseContract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(compilationRequest);
        UnrealApiContract normalizedContract = UnrealApiContractJson.Deserialize(
            UnrealApiContractJson.Serialize(baseContract, writeIndented: false));
        CSharpSourceDocument[] userSources = compilationRequest.Sources
            .Where(static source => !IsGenerated(source.Source))
            .OrderBy(static source => source.LogicalPath, StringComparer.Ordinal)
            .ToArray();
        EnsureUniquePaths(compilationRequest.Sources);

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
        var userPaths = userSources.Select(static source => source.LogicalPath)
            .ToHashSet(StringComparer.Ordinal);
        UsingImport[] globalImports = syntaxTrees
            .Where(tree => userPaths.Contains(tree.FilePath))
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

        var occurrences = new List<TypeHintOccurrence>();
        foreach (CSharpSourceDocument source in userSources)
        {
            SyntaxTree tree = treesByPath[source.LogicalPath];
            SemanticModel semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
            SyntaxNode[] candidates = root.DescendantNodes(descendIntoTrivia: false)
                .OfType<TypeSyntax>()
                .Where(IsTypePosition)
                .Cast<SyntaxNode>()
                .Concat(root.DescendantNodes(descendIntoTrivia: false)
                    .OfType<MemberAccessExpressionSyntax>()
                    .Select(static access => access.Expression)
                    .Where(expression => IsPotentialStaticTypeReceiver(
                        expression,
                        semanticModel)))
                .DistinctBy(static node => (node.RawKind, node.SpanStart, node.Span.Length))
                .OrderBy(static node => node.SpanStart)
                .ThenBy(static node => node.Span.Length)
                .ToArray();
            foreach (SyntaxNode candidate in candidates)
            {
                if (!TryResolveManagedTypeName(
                        candidate,
                        semanticModel,
                        globalImports,
                        normalizedContract.ManagedNamespace,
                        out string? managedTypeName))
                {
                    continue;
                }
                FileLinePositionSpan lineSpan = tree.GetLineSpan(candidate.Span);
                occurrences.Add(new TypeHintOccurrence(
                    managedTypeName!,
                    new UnrealApiSourceSpan(
                        source.LogicalPath,
                        lineSpan.StartLinePosition.Line + 1,
                        lineSpan.StartLinePosition.Character + 1,
                        lineSpan.EndLinePosition.Line + 1,
                        lineSpan.EndLinePosition.Character + 1)));
            }
        }

        UnrealApiSourceTypeHint[] hints = occurrences
            .GroupBy(static occurrence => occurrence.ManagedTypeName, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group => new UnrealApiSourceTypeHint(
                "USAPI-TYPE-" + StableHash(group.Key)[..16].ToUpperInvariant(),
                group.Key,
                group.Select(static occurrence => occurrence.Location)
                    .Distinct()
                    .OrderBy(static location => location.SourcePath, StringComparer.Ordinal)
                    .ThenBy(static location => location.StartLine)
                    .ThenBy(static location => location.StartColumn)
                    .ToArray()))
            .ToArray();
        return UnrealApiSourceTypeHintJson.Deserialize(UnrealApiSourceTypeHintJson.Serialize(
            new UnrealApiSourceTypeHintSet(
                UnrealApiSourceTypeHintSet.CurrentSchemaVersion,
                projectName,
                FingerprintSources(userSources),
                UnrealApiDevelopmentSurfaceJson.Fingerprint(normalizedContract),
                hints)));
    }

    private static bool TryResolveManagedTypeName(
        SyntaxNode node,
        SemanticModel semanticModel,
        IReadOnlyList<UsingImport> globalImports,
        string managedNamespace,
        out string? managedTypeName)
    {
        string normalized = NormalizeName(node.ToString());
        managedTypeName = null;
        if (normalized == "var")
        {
            return false;
        }
        bool bareName = false;
        string namespacePrefix = managedNamespace + ".";
        if (normalized.StartsWith(namespacePrefix, StringComparison.Ordinal))
        {
            managedTypeName = normalized[namespacePrefix.Length..];
        }
        else
        {
            ImportScope scope = GetImportScope(node, globalImports, managedNamespace);
            foreach (string alias in scope.Aliases)
            {
                string aliasPrefix = alias + ".";
                if (normalized.StartsWith(aliasPrefix, StringComparison.Ordinal))
                {
                    managedTypeName = normalized[aliasPrefix.Length..];
                    break;
                }
            }
            if (managedTypeName is null && node is IdentifierNameSyntax &&
                !normalized.Contains('.') && scope.NamespaceImported)
            {
                managedTypeName = normalized;
                bareName = true;
            }
        }
        if (managedTypeName is null || managedTypeName.Contains('.') ||
            !SyntaxFacts.IsValidIdentifier(managedTypeName) ||
            bareName && !CanUseBareGeneratedName(
                node,
                managedTypeName,
                semanticModel,
                managedNamespace))
        {
            managedTypeName = null;
            return false;
        }
        return true;
    }

    private static bool IsPotentialStaticTypeReceiver(
        ExpressionSyntax expression,
        SemanticModel semanticModel)
    {
        SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(expression);
        if (symbolInfo.Symbol is not null)
        {
            return symbolInfo.Symbol is INamedTypeSymbol;
        }
        if (symbolInfo.CandidateSymbols.Length > 0)
        {
            return symbolInfo.CandidateSymbols.All(static symbol => symbol is INamedTypeSymbol);
        }
        ITypeSymbol? type = semanticModel.GetTypeInfo(expression).Type;
        return type is null || type.TypeKind == TypeKind.Error;
    }

    private static bool IsTypePosition(TypeSyntax type) => type.Parent switch
    {
        VariableDeclarationSyntax declaration => declaration.Type == type,
        ParameterSyntax parameter => parameter.Type == type,
        PropertyDeclarationSyntax property => property.Type == type,
        EventDeclarationSyntax eventDeclaration => eventDeclaration.Type == type,
        DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.ReturnType == type,
        MethodDeclarationSyntax method => method.ReturnType == type,
        LocalFunctionStatementSyntax localFunction => localFunction.ReturnType == type,
        ConversionOperatorDeclarationSyntax conversion => conversion.Type == type,
        OperatorDeclarationSyntax operatorDeclaration => operatorDeclaration.ReturnType == type,
        ObjectCreationExpressionSyntax creation => creation.Type == type,
        CastExpressionSyntax cast => cast.Type == type,
        DefaultExpressionSyntax defaultExpression => defaultExpression.Type == type,
        TypeOfExpressionSyntax typeOfExpression => typeOfExpression.Type == type,
        CatchDeclarationSyntax catchDeclaration => catchDeclaration.Type == type,
        DeclarationPatternSyntax declarationPattern => declarationPattern.Type == type,
        RecursivePatternSyntax recursivePattern => recursivePattern.Type == type,
        BaseTypeSyntax baseType => baseType.Type == type,
        TypeConstraintSyntax constraint => constraint.Type == type,
        TupleElementSyntax tupleElement => tupleElement.Type == type,
        TypeArgumentListSyntax typeArguments => typeArguments.Arguments.Contains(type),
        NullableTypeSyntax nullable => nullable.ElementType == type && IsTypePosition(nullable),
        ArrayTypeSyntax array => array.ElementType == type && IsTypePosition(array),
        RefTypeSyntax reference => reference.Type == type && IsTypePosition(reference),
        PointerTypeSyntax pointer => pointer.ElementType == type && IsTypePosition(pointer),
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AsExpression) =>
            binary.Right == type,
        _ => false,
    };

    private static bool CanUseBareGeneratedName(
        SyntaxNode node,
        string managedTypeName,
        SemanticModel semanticModel,
        string managedNamespace)
    {
        ITypeSymbol? type = node switch
        {
            TypeSyntax typeSyntax => semanticModel.GetTypeInfo(typeSyntax).Type,
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
        return semanticModel.LookupSymbols(node.SpanStart, name: managedTypeName).All(symbol =>
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
        return target.Length == 0
            ? null
            : new UsingImport(directive.Alias?.Name.Identifier.ValueText, target);
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
        }
        else
        {
            aliases.TryAdd(import.Alias, import.Target);
        }
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

    private static string StableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record UsingImport(string? Alias, string Target);

    private sealed record ImportScope(IReadOnlyList<string> Aliases, bool NamespaceImported);

    private sealed record TypeHintOccurrence(
        string ManagedTypeName,
        UnrealApiSourceSpan Location);
}
