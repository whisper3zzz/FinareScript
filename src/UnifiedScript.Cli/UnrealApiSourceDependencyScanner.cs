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
/// Finds generated-Unreal wrapper use before the referenced wrapper exists. The scanner accepts
/// only an explicit alias or fully-qualified generated namespace and resolves a receiver through
/// its syntactic declaration. It therefore never guesses that an unrelated user type is Unreal.
/// Roslyn semantic compilation happens after the resulting safe overlay has generated wrappers.
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
        UnrealApiContract baseContract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(sources);
        UnrealReflectionManifest normalizedReflection =
            UnrealReflectionJson.NormalizeAndValidate(reflection);
        UnrealApiContract normalizedContract = UnrealApiContractJson.Deserialize(
            UnrealApiContractJson.Serialize(baseContract, writeIndented: false));
        CSharpSourceDocument[] userSources = sources
            .Where(static source => !IsGenerated(source.Source))
            .OrderBy(static source => source.LogicalPath, StringComparer.Ordinal)
            .ToArray();
        EnsureUniquePaths(userSources);

        IReadOnlyDictionary<string, ReflectedTypeTarget[]> typeTargets =
            CreateTypeTargets(normalizedReflection);
        var occurrences = new List<RequestOccurrence>();
        foreach (CSharpSourceDocument source in userSources)
        {
            ScanSource(
                source,
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
                    $"{first.ManagedTypeName}.{identity.MemberName}",
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
        string managedNamespace,
        IReadOnlyDictionary<string, ReflectedTypeTarget[]> typeTargets,
        ICollection<RequestOccurrence> occurrences)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source.Source, path: source.LogicalPath);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        string[] aliases = root.DescendantNodes(descendIntoTrivia: false)
            .OfType<UsingDirectiveSyntax>()
            .Where(usingDirective => usingDirective.Alias is not null &&
                NormalizeName(usingDirective.Name?.ToString()) == managedNamespace)
            .Select(static usingDirective => usingDirective.Alias!.Name.Identifier.ValueText)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (aliases.Length == 0 &&
            !source.Source.Contains(managedNamespace + ".", StringComparison.Ordinal))
        {
            return;
        }

        var bindings = new List<TypedBinding>();
        foreach (VariableDeclarationSyntax declaration in root.DescendantNodes()
                     .OfType<VariableDeclarationSyntax>())
        {
            if (!TryResolveType(
                    declaration.Type,
                    aliases,
                    managedNamespace,
                    typeTargets,
                    out ReflectedTypeTarget[]? targets))
            {
                continue;
            }
            TextSpan scope = DeclarationScope(declaration);
            foreach (VariableDeclaratorSyntax variable in declaration.Variables)
            {
                bindings.Add(new TypedBinding(
                    variable.Identifier.ValueText,
                    targets!,
                    scope.Start,
                    scope.End,
                    variable.SpanStart));
            }
        }
        foreach (ParameterSyntax parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (parameter.Type is null || !TryResolveType(
                    parameter.Type,
                    aliases,
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
                    aliases,
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
                    aliases,
                    managedNamespace,
                    typeTargets,
                    out ReflectedTypeTarget[]? targets))
            {
                continue;
            }
            string memberName = access.Name.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(memberName))
            {
                continue;
            }
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
                location));
        }
    }

    private static bool TryResolveReceiver(
        ExpressionSyntax expression,
        int position,
        IReadOnlyList<TypedBinding> bindings,
        IReadOnlyList<string> aliases,
        string managedNamespace,
        IReadOnlyDictionary<string, ReflectedTypeTarget[]> typeTargets,
        out ReflectedTypeTarget[]? targets)
    {
        expression = Unwrap(expression);
        switch (expression)
        {
            case IdentifierNameSyntax identifier:
                targets = bindings
                    .Where(binding => binding.Name == identifier.Identifier.ValueText &&
                        position >= binding.ScopeStart && position <= binding.ScopeEnd &&
                        (binding.DeclarationPosition <= position || binding.ScopeStart == 0))
                    .OrderByDescending(static binding => binding.ScopeStart)
                    .ThenByDescending(static binding => binding.DeclarationPosition)
                    .Select(static binding => binding.Targets)
                    .FirstOrDefault();
                return targets is not null;
            case MemberAccessExpressionSyntax memberAccess
                when memberAccess.Expression is ThisExpressionSyntax or BaseExpressionSyntax:
                return TryResolveReceiver(
                    SyntaxFactory.IdentifierName(memberAccess.Name.Identifier),
                    position,
                    bindings,
                    aliases,
                    managedNamespace,
                    typeTargets,
                    out targets);
            case ObjectCreationExpressionSyntax creation:
                return TryResolveType(
                    creation.Type,
                    aliases,
                    managedNamespace,
                    typeTargets,
                    out targets);
            case CastExpressionSyntax cast:
                return TryResolveType(
                    cast.Type,
                    aliases,
                    managedNamespace,
                    typeTargets,
                    out targets);
            case MemberAccessExpressionSyntax typeExpression:
                return TryResolveTypeName(
                    typeExpression.ToString(),
                    aliases,
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
        return expression;
    }

    private static bool TryResolveType(
        TypeSyntax type,
        IReadOnlyList<string> aliases,
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
            aliases,
            managedNamespace,
            typeTargets,
            out targets);
    }

    private static bool TryResolveTypeName(
        string typeText,
        IReadOnlyList<string> aliases,
        string managedNamespace,
        IReadOnlyDictionary<string, ReflectedTypeTarget[]> typeTargets,
        out ReflectedTypeTarget[]? targets)
    {
        string normalized = NormalizeName(typeText);
        string? managedTypeName = null;
        string namespacePrefix = managedNamespace + ".";
        if (normalized.StartsWith(namespacePrefix, StringComparison.Ordinal))
        {
            managedTypeName = normalized[namespacePrefix.Length..];
        }
        else
        {
            foreach (string alias in aliases)
            {
                string aliasPrefix = alias + ".";
                if (normalized.StartsWith(aliasPrefix, StringComparison.Ordinal))
                {
                    managedTypeName = normalized[aliasPrefix.Length..];
                    break;
                }
            }
        }
        if (managedTypeName is null || managedTypeName.Contains('.') ||
            !typeTargets.TryGetValue(managedTypeName, out targets))
        {
            targets = null;
            return false;
        }
        return true;
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
                [new ReflectedTypeTarget(type.Name, type.Path, managedName)]);
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
        string ManagedTypeName);

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
        UnrealApiSourceSpan Location);

    private sealed record RequestIdentity(
        string TypeName,
        string? TypePath,
        string MemberName);
}
