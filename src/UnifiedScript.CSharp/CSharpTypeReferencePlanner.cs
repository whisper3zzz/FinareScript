using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnifiedScript.CSharp;

public enum CSharpTypeReferenceEditKind
{
    TypeDeclaration,
    ConstructorDeclaration,
    TypeReference,
}

public sealed record CSharpTypeReferenceEdit(
    int Start,
    int Length,
    int Line,
    int Column,
    CSharpTypeReferenceEditKind Kind,
    string OriginalText,
    string? Replacement);

public sealed record CSharpTypeReferenceDocument(
    string LogicalPath,
    string Path,
    string SourceDigest,
    IReadOnlyList<CSharpTypeReferenceEdit> Edits);

public sealed record CSharpTypeReferencePlan(
    string SchemaVersion,
    string ProjectPath,
    string SemanticConfigurationId,
    string ManagedTypeName,
    string? ReplacementTypeName,
    IReadOnlyList<CSharpTypeReferenceDocument> Documents)
{
    public const string CurrentSchemaVersion =
        "unifiedscript.csharp-type-reference-plan/v0.1";

    public int EditCount => Documents.Sum(static document => document.Edits.Count);
}

/// <summary>
/// Builds a deterministic Roslyn-bound plan for every declaration and use of
/// one project-owned type. Text that merely shares the identifier is excluded.
/// </summary>
public sealed class CSharpTypeReferencePlanner
{
    private readonly CSharpCompilationHost compilationHost = new();

    public CSharpTypeReferencePlan Plan(
        CSharpProjectLoadResult project,
        string managedTypeName,
        string? replacementTypeName = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedTypeName);
        if (replacementTypeName is not null &&
            !SyntaxFacts.IsValidIdentifier(replacementTypeName))
        {
            throw new ArgumentException(
                $"'{replacementTypeName}' is not a valid C# type identifier.",
                nameof(replacementTypeName));
        }

        SyntaxTree[] syntaxTrees = project.Request.Sources
            .Select(source => CSharpSyntaxTree.ParseText(
                source.Source,
                project.Request.Settings.ParseOptions,
                source.LogicalPath,
                Encoding.UTF8))
            .ToArray();
        CSharpCompilation compilation = compilationHost.Create(
            project.Request,
            syntaxTrees);
        INamedTypeSymbol target = compilation.GetTypeByMetadataName(managedTypeName) ??
            throw new ArgumentException(
                $"Managed type '{managedTypeName}' was not found uniquely in " +
                $"project '{project.ProjectPath}'.",
                nameof(managedTypeName));
        var physicalPaths = project.Sources.ToDictionary(
            static source => source.LogicalPath,
            static source => Path.GetFullPath(source.Path),
            StringComparer.Ordinal);

        var documents = new List<CSharpTypeReferenceDocument>();
        for (int index = 0; index < syntaxTrees.Length; index++)
        {
            SyntaxTree tree = syntaxTrees[index];
            CSharpSourceDocument source = project.Request.Sources[index];
            if (!physicalPaths.TryGetValue(source.LogicalPath, out string? physicalPath))
            {
                throw new ArgumentException(
                    $"Project source '{source.LogicalPath}' has no physical path mapping.",
                    nameof(project));
            }
            SemanticModel model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            SyntaxNode root = tree.GetRoot();
            var edits = new Dictionary<(int Start, int Length), CSharpTypeReferenceEdit>();

            foreach (TypeDeclarationSyntax declaration in
                root.DescendantNodes(descendIntoTrivia: true).OfType<TypeDeclarationSyntax>())
            {
                if (SymbolEqualityComparer.Default.Equals(
                        model.GetDeclaredSymbol(declaration),
                        target))
                {
                    AddEdit(
                        tree,
                        declaration.Identifier,
                        CSharpTypeReferenceEditKind.TypeDeclaration,
                        replacementTypeName,
                        edits);
                }
            }
            foreach (ConstructorDeclarationSyntax declaration in
                root.DescendantNodes(descendIntoTrivia: true)
                    .OfType<ConstructorDeclarationSyntax>())
            {
                IMethodSymbol? symbol = model.GetDeclaredSymbol(declaration);
                if (symbol is not null && IsTargetType(symbol.ContainingType, target))
                {
                    AddEdit(
                        tree,
                        declaration.Identifier,
                        CSharpTypeReferenceEditKind.ConstructorDeclaration,
                        replacementTypeName,
                        edits);
                }
            }
            foreach (DestructorDeclarationSyntax declaration in
                root.DescendantNodes(descendIntoTrivia: true)
                    .OfType<DestructorDeclarationSyntax>())
            {
                IMethodSymbol? symbol = model.GetDeclaredSymbol(declaration);
                if (symbol is not null && IsTargetType(symbol.ContainingType, target))
                {
                    AddEdit(
                        tree,
                        declaration.Identifier,
                        CSharpTypeReferenceEditKind.ConstructorDeclaration,
                        replacementTypeName,
                        edits);
                }
            }
            foreach (SimpleNameSyntax name in
                root.DescendantNodes(descendIntoTrivia: true).OfType<SimpleNameSyntax>())
            {
                if (model.GetAliasInfo(name) is not null)
                {
                    continue;
                }
                SymbolInfo symbolInfo = model.GetSymbolInfo(name);
                ISymbol? symbol = symbolInfo.Symbol ??
                    symbolInfo.CandidateSymbols.FirstOrDefault();
                bool matches = symbol is INamedTypeSymbol namedType &&
                        IsTargetType(namedType, target) ||
                    symbol is IMethodSymbol method &&
                        method.MethodKind == MethodKind.Constructor &&
                        IsTargetType(method.ContainingType, target);
                if (matches)
                {
                    AddEdit(
                        tree,
                        name.Identifier,
                        CSharpTypeReferenceEditKind.TypeReference,
                        replacementTypeName,
                        edits);
                }
            }

            if (edits.Count > 0)
            {
                documents.Add(new CSharpTypeReferenceDocument(
                    source.LogicalPath,
                    physicalPath,
                    DigestFile(physicalPath),
                    edits.Values
                        .OrderBy(static edit => edit.Start)
                        .ThenBy(static edit => edit.Length)
                        .ToArray()));
            }
        }

        return new CSharpTypeReferencePlan(
            CSharpTypeReferencePlan.CurrentSchemaVersion,
            Path.GetFullPath(project.ProjectPath),
            project.Request.ConfigurationId.ToString(),
            managedTypeName,
            replacementTypeName,
            documents
                .OrderBy(static document => document.Path, StringComparer.Ordinal)
                .ToArray());
    }

    private static bool IsTargetType(
        INamedTypeSymbol candidate,
        INamedTypeSymbol target) => SymbolEqualityComparer.Default.Equals(
        candidate.OriginalDefinition,
        target.OriginalDefinition);

    private static void AddEdit(
        SyntaxTree tree,
        SyntaxToken token,
        CSharpTypeReferenceEditKind kind,
        string? replacement,
        IDictionary<(int Start, int Length), CSharpTypeReferenceEdit> edits)
    {
        FileLinePositionSpan lineSpan = tree.GetLineSpan(token.Span);
        var edit = new CSharpTypeReferenceEdit(
            token.SpanStart,
            token.Span.Length,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            kind,
            token.Text,
            replacement);
        edits.TryAdd((edit.Start, edit.Length), edit);
    }

    private static string DigestFile(string path) => "sha256:" +
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();
}
