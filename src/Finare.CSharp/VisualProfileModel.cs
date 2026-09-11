using Finare.Core;

namespace Finare.CSharp;

public enum VisualMethodRepresentation
{
    VisualGraph,
    OpaqueCode,
}

public sealed record OpaqueCodeBoundaryContract(
    string MarkerAttribute,
    bool UnmarkedMethodsAreSourceOnly,
    bool CallsProjectAsSingleNodes,
    bool BodiesAreGraphEditable,
    bool RequiresGraphSafeSignature,
    bool AllowsRefParameters);

public sealed record VisualCSharpProfileContract(
    string SchemaVersion,
    string DisplayName,
    string LanguageVersion,
    IReadOnlyList<string> Statements,
    IReadOnlyList<string> Expressions,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string> RejectedFeatures,
    OpaqueCodeBoundaryContract OpaqueCodeBoundary)
{
    public const string CurrentSchemaVersion = "unifiedscript.visual-csharp-profile/v0.3";

    public const string CurrentDisplayName = "Visual C# profile v0.3";

    public static VisualCSharpProfileContract Current { get; } = new(
        CurrentSchemaVersion,
        CurrentDisplayName,
        "C# preview parsed by Roslyn",
        [
            "block",
            "localDeclaration",
            "expressionStatement",
            "if",
            "while",
            "doWhile",
            "for",
            "foreach",
            "awaitForeach",
            "switch",
            "break",
            "continue",
            "return",
            "empty",
        ],
        [
            "literal",
            "identifier",
            "this",
            "memberAccess",
            "conditionalAccess",
            "invocation",
            "binary",
            "prefixUnary",
            "postIncrementDecrement",
            "parenthesized",
            "assignment",
            "await",
            "cast",
            "conditional",
        ],
        [
            "constant",
            "declaration",
            "type",
            "var",
            "discard",
            "relational",
            "not",
            "and",
            "or",
            "parenthesized",
        ],
        [
            "dynamic",
            "unsafeAndPointers",
            "goto",
            "yield",
            "localFunctions",
            "lambdas",
            "exceptionControlFlow",
            "lock",
            "querySyntax",
            "stackalloc",
            "mutableRefForeach",
            "borrowedAwaitForeach",
            "refReturns",
            "refOutInParameters",
            "defaultParameters",
            "namedArguments",
            "multipleLocalDeclarators",
        ],
        new OpaqueCodeBoundaryContract(
            "[Visual]",
            UnmarkedMethodsAreSourceOnly: true,
            CallsProjectAsSingleNodes: true,
            BodiesAreGraphEditable: false,
            RequiresGraphSafeSignature: true,
            AllowsRefParameters: false));
}

public sealed record VisualMethodProfile(
    SemanticId Id,
    string Name,
    string QualifiedName,
    SourceSpan Span,
    VisualMethodRepresentation Representation,
    bool GraphEditable,
    string AuthoringBoundary,
    IReadOnlyList<UnifiedDiagnostic> Diagnostics)
{
    /// <summary>
    /// Non-fatal graph capability findings for an implicit Unreal entry that
    /// is exposed as source-only.  They are kept separate from fatal Visual
    /// profile diagnostics so the opaque boundary remains valid.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<UnifiedDiagnostic>? CapabilityDiagnostics { get; init; }
}

public sealed record VisualProfileReport(
    string SchemaVersion,
    VisualCSharpProfileContract Profile,
    IReadOnlyList<VisualMethodProfile> Methods)
{
    public const string CurrentSchemaVersion = "unifiedscript.visual-csharp-profile-report/v0.1";

    public bool Succeeded => Methods
        .SelectMany(static method => method.Diagnostics)
        .All(static diagnostic => diagnostic.Severity != UnifiedDiagnosticSeverity.Error);
}
