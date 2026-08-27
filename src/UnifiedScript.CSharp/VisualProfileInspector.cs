using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnifiedScript.Core;

namespace UnifiedScript.CSharp;

public sealed class VisualProfileInspector
{
    private readonly VisualProfileValidator validator = new();

    public VisualProfileReport Inspect(IEnumerable<SyntaxTree> syntaxTrees)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        var methods = new List<VisualMethodProfile>();
        foreach (SyntaxTree syntaxTree in syntaxTrees)
        {
            string filePath = string.IsNullOrEmpty(syntaxTree.FilePath)
                ? "<memory>"
                : syntaxTree.FilePath;
            foreach (TypeDeclarationSyntax type in syntaxTree.GetRoot()
                         .DescendantNodes()
                         .OfType<TypeDeclarationSyntax>())
            {
                MethodDeclarationSyntax[] typeMethods = type.Members
                    .OfType<MethodDeclarationSyntax>()
                    .ToArray();
                if (!typeMethods.Any(VisualProfileValidator.IsVisualMethod))
                {
                    continue;
                }

                string typeName = GetQualifiedTypeName(type);
                foreach (MethodDeclarationSyntax method in typeMethods)
                {
                    bool isVisual = VisualProfileValidator.IsVisualMethod(method);
                    IReadOnlyList<UnifiedDiagnostic> diagnostics = isVisual
                        ? validator.ValidateMethod(method)
                        : [];
                    string signature = GetMethodSignature(typeName, method);
                    methods.Add(new VisualMethodProfile(
                        CSharpSemanticIds.ForSymbol("method", signature),
                        method.Identifier.ValueText,
                        signature,
                        RoslynSourceSpan.FromNode(method, filePath),
                        isVisual
                            ? VisualMethodRepresentation.VisualGraph
                            : VisualMethodRepresentation.OpaqueCode,
                        isVisual && diagnostics.All(static diagnostic =>
                            diagnostic.Severity != UnifiedDiagnosticSeverity.Error),
                        isVisual ? "visualGraph" : "sourceOnly",
                        diagnostics));
                }
            }
        }

        return new VisualProfileReport(
            VisualProfileReport.CurrentSchemaVersion,
            VisualCSharpProfileContract.Current,
            methods
                .OrderBy(static method => method.QualifiedName, StringComparer.Ordinal)
                .ThenBy(static method => method.Span.FilePath, StringComparer.Ordinal)
                .ThenBy(static method => method.Span.Start.Line)
                .ToArray());
    }

    internal static string GetQualifiedTypeName(TypeDeclarationSyntax type)
    {
        string @namespace = string.Join(
            ".",
            type.Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .Reverse()
                .Select(static declaration => declaration.Name.ToString()));
        string nestedName = string.Join(
            ".",
            type.AncestorsAndSelf()
                .OfType<TypeDeclarationSyntax>()
                .Reverse()
                .Select(static declaration => declaration.Identifier.ValueText));
        return string.IsNullOrEmpty(@namespace) ? nestedName : $"{@namespace}.{nestedName}";
    }

    internal static string GetMethodSignature(
        string qualifiedTypeName,
        MethodDeclarationSyntax method)
    {
        string parameterSignature = string.Join(
            ",",
            method.ParameterList.Parameters.Select(static parameter =>
                parameter.Type?.ToString() ?? "?"));
        return $"{qualifiedTypeName}.{method.Identifier.ValueText}({parameterSignature})";
    }
}
