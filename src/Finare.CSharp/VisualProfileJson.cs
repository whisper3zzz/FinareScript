using System.Text.Json;
using Finare.Core;

namespace Finare.CSharp;

public static class VisualProfileJson
{
    public static string Serialize(VisualProfileReport report, bool writeIndented = true) =>
        JsonSerializer.Serialize(NormalizeAndValidate(report), UnifiedJson.CreateOptions(writeIndented));

    public static VisualProfileReport Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        VisualProfileReport? report = JsonSerializer.Deserialize<VisualProfileReport>(
            json,
            UnifiedJson.CreateOptions());
        return report is null
            ? throw new JsonException("The Visual C# profile report was empty.")
            : NormalizeAndValidate(report);
    }

    public static VisualProfileReport NormalizeAndValidate(VisualProfileReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.SchemaVersion != VisualProfileReport.CurrentSchemaVersion ||
            report.Profile is null || report.Methods is null)
        {
            throw new JsonException("The Visual C# profile report has invalid required fields.");
        }

        ValidateContract(report.Profile);
        var methodIds = new HashSet<SemanticId>();
        var qualifiedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (VisualMethodProfile? method in report.Methods)
        {
            if (method is null || method.Id.IsEmpty || string.IsNullOrWhiteSpace(method.Name) ||
                string.IsNullOrWhiteSpace(method.QualifiedName) || method.Span is null ||
                string.IsNullOrWhiteSpace(method.Span.FilePath) || method.Diagnostics is null ||
                !Enum.IsDefined(method.Representation) || !methodIds.Add(method.Id) ||
                !qualifiedNames.Add(method.QualifiedName))
            {
                throw new JsonException("The Visual C# profile report has an invalid method.");
            }

            bool hasErrors = method.Diagnostics.Any(static diagnostic =>
                diagnostic.Severity == UnifiedDiagnosticSeverity.Error);
            bool validVisual = method.Representation == VisualMethodRepresentation.VisualGraph &&
                method.AuthoringBoundary == "visualGraph" && method.GraphEditable == !hasErrors;
            bool validOpaque = method.Representation == VisualMethodRepresentation.OpaqueCode &&
                method.AuthoringBoundary == "sourceOnly" && !method.GraphEditable &&
                method.Diagnostics.Count == 0;
            if (!(validVisual || validOpaque) || method.Diagnostics.Any(static diagnostic =>
                    string.IsNullOrWhiteSpace(diagnostic.Code) ||
                    string.IsNullOrWhiteSpace(diagnostic.Message)))
            {
                throw new JsonException(
                    $"Visual C# profile method '{method.QualifiedName}' has inconsistent representation metadata.");
            }
        }

        return report with
        {
            Profile = VisualCSharpProfileContract.Current,
            Methods = report.Methods
                .OrderBy(static method => method.QualifiedName, StringComparer.Ordinal)
                .ThenBy(static method => method.Span.FilePath, StringComparer.Ordinal)
                .ThenBy(static method => method.Span.Start.Line)
                .Select(static method => method with
                {
                    Diagnostics = method.Diagnostics
                        .OrderBy(static diagnostic => diagnostic.Span.FilePath, StringComparer.Ordinal)
                        .ThenBy(static diagnostic => diagnostic.Span.Start.Line)
                        .ThenBy(static diagnostic => diagnostic.Span.Start.Column)
                        .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                        .ToArray(),
                })
                .ToArray(),
        };
    }

    private static void ValidateContract(VisualCSharpProfileContract profile)
    {
        VisualCSharpProfileContract current = VisualCSharpProfileContract.Current;
        if (profile.SchemaVersion != current.SchemaVersion ||
            profile.DisplayName != current.DisplayName ||
            profile.LanguageVersion != current.LanguageVersion ||
            profile.Statements is null || !profile.Statements.SequenceEqual(current.Statements) ||
            profile.Expressions is null || !profile.Expressions.SequenceEqual(current.Expressions) ||
            profile.Patterns is null || !profile.Patterns.SequenceEqual(current.Patterns) ||
            profile.RejectedFeatures is null ||
                !profile.RejectedFeatures.SequenceEqual(current.RejectedFeatures) ||
            profile.OpaqueCodeBoundary != current.OpaqueCodeBoundary)
        {
            throw new JsonException(
                $"Unsupported Visual C# profile contract. Expected '{current.SchemaVersion}'.");
        }
    }
}
