using System.Text.Json.Serialization;

namespace UnifiedScript.Core.Hir;

public abstract record HirNode(SemanticId Id, SourceSpan Span);

public sealed record HirCompilationUnit(
    SemanticId Id,
    SourceSpan Span,
    IReadOnlyList<HirTypeDeclaration> Types) : HirNode(Id, Span);

public sealed record HirTypeDeclaration(
    SemanticId Id,
    SourceSpan Span,
    string Namespace,
    string Name,
    IReadOnlyList<HirMethodDeclaration> Methods) : HirNode(Id, Span)
{
    public string QualifiedName => string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";
}

public sealed record HirMethodDeclaration(
    SemanticId Id,
    SourceSpan Span,
    string Name,
    string ReturnType,
    bool IsVisual,
    bool IsStatic,
    bool IsAsync,
    IReadOnlyList<HirParameter> Parameters,
    HirBlockStatement Body) : HirNode(Id, Span)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<HirAttribute>? Attributes { get; init; }
}

/// <summary>
/// Lossless-enough declaration metadata for visual entry semantics. Attribute
/// expressions are normalized to stable strings while executable code remains HIR.
/// </summary>
public sealed record HirAttribute(
    string Name,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> NamedArguments);

public sealed record HirParameter(
    SemanticId Id,
    SourceSpan Span,
    string Name,
    string TypeName) : HirNode(Id, Span);

/// <summary>
/// Identity-only HIR node used while reconciling type-level script state.
/// Declaration IR owns the full state contract; this compact node lets the
/// existing sidecar machinery preserve identity across source edits.
/// </summary>
public sealed record HirStateDeclaration(
    SemanticId Id,
    SourceSpan Span,
    string Name,
    string TypeName) : HirNode(Id, Span);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HirBlockStatement), "block")]
[JsonDerivedType(typeof(HirVariableDeclarationStatement), "variableDeclaration")]
[JsonDerivedType(typeof(HirExpressionStatement), "expressionStatement")]
[JsonDerivedType(typeof(HirIfStatement), "if")]
[JsonDerivedType(typeof(HirWhileStatement), "while")]
[JsonDerivedType(typeof(HirDoWhileStatement), "doWhile")]
[JsonDerivedType(typeof(HirForStatement), "for")]
[JsonDerivedType(typeof(HirForEachStatement), "forEach")]
[JsonDerivedType(typeof(HirSwitchStatement), "switch")]
[JsonDerivedType(typeof(HirBreakStatement), "break")]
[JsonDerivedType(typeof(HirContinueStatement), "continue")]
[JsonDerivedType(typeof(HirReturnStatement), "return")]
public abstract record HirStatement(SemanticId Id, SourceSpan Span) : HirNode(Id, Span);

public sealed record HirBlockStatement(
    SemanticId Id,
    SourceSpan Span,
    IReadOnlyList<HirStatement> Statements) : HirStatement(Id, Span);

public sealed record HirVariableDeclarationStatement(
    SemanticId Id,
    SourceSpan Span,
    string Name,
    string TypeName,
    HirExpression? Initializer) : HirStatement(Id, Span);

public sealed record HirExpressionStatement(
    SemanticId Id,
    SourceSpan Span,
    HirExpression Expression) : HirStatement(Id, Span);

public sealed record HirIfStatement(
    SemanticId Id,
    SourceSpan Span,
    HirExpression Condition,
    HirBlockStatement Then,
    HirBlockStatement? Else) : HirStatement(Id, Span);

public sealed record HirWhileStatement(
    SemanticId Id,
    SourceSpan Span,
    HirExpression Condition,
    HirBlockStatement Body) : HirStatement(Id, Span);

public sealed record HirDoWhileStatement(
    SemanticId Id,
    SourceSpan Span,
    HirBlockStatement Body,
    HirExpression Condition) : HirStatement(Id, Span);

public sealed record HirForStatement(
    SemanticId Id,
    SourceSpan Span,
    HirForVariableDeclaration? Declaration,
    IReadOnlyList<HirExpression> Initializers,
    HirExpression? Condition,
    IReadOnlyList<HirExpression> Incrementors,
    HirBlockStatement Body) : HirStatement(Id, Span);

public enum HirForEachIterationKind
{
    Value,
    Ref,
    RefReadOnly,
}

public sealed record HirForEachStatement(
    SemanticId Id,
    SourceSpan Span,
    HirForEachVariable Variable,
    HirExpression Collection,
    HirBlockStatement Body) : HirStatement(Id, Span)
{
    public HirForEachIterationKind IterationKind { get; init; } =
        HirForEachIterationKind.Value;

    public bool IsAsync { get; init; }
}

public sealed record HirForEachVariable(
    SemanticId Id,
    SourceSpan Span,
    string Name,
    string TypeName) : HirNode(Id, Span);

public sealed record HirSwitchStatement(
    SemanticId Id,
    SourceSpan Span,
    HirExpression GoverningExpression,
    IReadOnlyList<HirSwitchSection> Sections) : HirStatement(Id, Span);

public sealed record HirSwitchSection(
    SemanticId Id,
    SourceSpan Span,
    IReadOnlyList<HirSwitchLabel> Labels,
    HirBlockStatement Body) : HirNode(Id, Span);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HirPatternSwitchLabel), "pattern")]
[JsonDerivedType(typeof(HirDefaultSwitchLabel), "default")]
public abstract record HirSwitchLabel(
    SemanticId Id,
    SourceSpan Span,
    string DisplayText) : HirNode(Id, Span);

public sealed record HirPatternSwitchLabel(
    SemanticId Id,
    SourceSpan Span,
    string DisplayText,
    HirPattern Pattern,
    HirExpression? Guard) : HirSwitchLabel(Id, Span, DisplayText);

public sealed record HirDefaultSwitchLabel(
    SemanticId Id,
    SourceSpan Span,
    string DisplayText) : HirSwitchLabel(Id, Span, DisplayText);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HirConstantPattern), "constant")]
[JsonDerivedType(typeof(HirDeclarationPattern), "declaration")]
[JsonDerivedType(typeof(HirTypePattern), "type")]
[JsonDerivedType(typeof(HirVarPattern), "var")]
[JsonDerivedType(typeof(HirDiscardPattern), "discard")]
[JsonDerivedType(typeof(HirRelationalPattern), "relational")]
[JsonDerivedType(typeof(HirLogicalPattern), "logical")]
[JsonDerivedType(typeof(HirNotPattern), "not")]
[JsonDerivedType(typeof(HirParenthesizedPattern), "parenthesized")]
public abstract record HirPattern(
    SemanticId Id,
    SourceSpan Span,
    string DisplayText) : HirNode(Id, Span);

public sealed record HirConstantPattern(
    SemanticId Id,
    SourceSpan Span,
    string DisplayText,
    HirExpression Value) : HirPattern(Id, Span, DisplayText);

public sealed record HirDeclarationPattern(
    SemanticId Id,
    SourceSpan Span,
    string DisplayText,
    string TypeName,
    HirPatternVariable? Variable,
    bool IsDiscardDesignation) : HirPattern(Id, Span, DisplayText);

public sealed record HirTypePattern(
    SemanticId Id,
    SourceSpan Span,
    string DisplayText,
    string TypeName) : HirPattern(Id, Span, DisplayText);

public sealed record HirVarPattern(
    SemanticId Id,
    SourceSpan Span,
    string DisplayText,
    HirPatternVariable? Variable,
    bool IsDiscardDesignation) : HirPattern(Id, Span, DisplayText);

public sealed record HirDiscardPattern(
    SemanticId Id,
    SourceSpan Span,
    string DisplayText) : HirPattern(Id, Span, DisplayText);

public sealed record HirRelationalPattern(
    SemanticId Id,
    SourceSpan Span,
    string DisplayText,
    string Operator,
    HirExpression Value) : HirPattern(Id, Span, DisplayText);

public sealed record HirLogicalPattern(
    SemanticId Id,
    SourceSpan Span,
    string DisplayText,
    string Operator,
    HirPattern Left,
    HirPattern Right) : HirPattern(Id, Span, DisplayText);

public sealed record HirNotPattern(
    SemanticId Id,
    SourceSpan Span,
    string DisplayText,
    HirPattern Operand) : HirPattern(Id, Span, DisplayText);

public sealed record HirParenthesizedPattern(
    SemanticId Id,
    SourceSpan Span,
    string DisplayText,
    HirPattern Pattern) : HirPattern(Id, Span, DisplayText);

public sealed record HirPatternVariable(
    SemanticId Id,
    SourceSpan Span,
    string Name,
    string TypeName) : HirNode(Id, Span);

public sealed record HirForVariableDeclaration(
    SemanticId Id,
    SourceSpan Span,
    string TypeName,
    IReadOnlyList<HirForVariableDeclarator> Variables) : HirNode(Id, Span);

public sealed record HirForVariableDeclarator(
    SemanticId Id,
    SourceSpan Span,
    string Name,
    HirExpression? Initializer) : HirNode(Id, Span);

public sealed record HirBreakStatement(
    SemanticId Id,
    SourceSpan Span) : HirStatement(Id, Span);

public sealed record HirContinueStatement(
    SemanticId Id,
    SourceSpan Span) : HirStatement(Id, Span);

public sealed record HirReturnStatement(
    SemanticId Id,
    SourceSpan Span,
    HirExpression? Expression) : HirStatement(Id, Span);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HirLiteralExpression), "literal")]
[JsonDerivedType(typeof(HirIdentifierExpression), "identifier")]
[JsonDerivedType(typeof(HirThisExpression), "this")]
[JsonDerivedType(typeof(HirMemberAccessExpression), "memberAccess")]
[JsonDerivedType(typeof(HirConditionalAccessExpression), "conditionalAccess")]
[JsonDerivedType(typeof(HirConditionalMemberBindingExpression), "conditionalMemberBinding")]
[JsonDerivedType(typeof(HirInvocationExpression), "invocation")]
[JsonDerivedType(typeof(HirBinaryExpression), "binary")]
[JsonDerivedType(typeof(HirUnaryExpression), "unary")]
[JsonDerivedType(typeof(HirAssignmentExpression), "assignment")]
[JsonDerivedType(typeof(HirAwaitExpression), "await")]
[JsonDerivedType(typeof(HirCastExpression), "cast")]
[JsonDerivedType(typeof(HirConditionalExpression), "conditional")]
public abstract record HirExpression(SemanticId Id, SourceSpan Span) : HirNode(Id, Span);

public sealed record HirLiteralExpression(
    SemanticId Id,
    SourceSpan Span,
    string ValueText) : HirExpression(Id, Span);

public sealed record HirIdentifierExpression(
    SemanticId Id,
    SourceSpan Span,
    string Name) : HirExpression(Id, Span);

public sealed record HirThisExpression(
    SemanticId Id,
    SourceSpan Span) : HirExpression(Id, Span);

public sealed record HirMemberAccessExpression(
    SemanticId Id,
    SourceSpan Span,
    HirExpression Target,
    string MemberName) : HirExpression(Id, Span);

public sealed record HirConditionalAccessExpression(
    SemanticId Id,
    SourceSpan Span,
    HirExpression Receiver,
    HirExpression WhenNotNull) : HirExpression(Id, Span);

public sealed record HirConditionalMemberBindingExpression(
    SemanticId Id,
    SourceSpan Span,
    string MemberName) : HirExpression(Id, Span);

public sealed record HirInvocationExpression(
    SemanticId Id,
    SourceSpan Span,
    HirExpression Target,
    IReadOnlyList<HirExpression> Arguments) : HirExpression(Id, Span);

public sealed record HirBinaryExpression(
    SemanticId Id,
    SourceSpan Span,
    string Operator,
    HirExpression Left,
    HirExpression Right) : HirExpression(Id, Span);

public sealed record HirUnaryExpression(
    SemanticId Id,
    SourceSpan Span,
    string Operator,
    HirExpression Operand,
    bool IsPostfix = false) : HirExpression(Id, Span);

public sealed record HirAssignmentExpression(
    SemanticId Id,
    SourceSpan Span,
    string Operator,
    HirExpression Target,
    HirExpression Value) : HirExpression(Id, Span);

public sealed record HirAwaitExpression(
    SemanticId Id,
    SourceSpan Span,
    HirExpression Operand) : HirExpression(Id, Span);

public sealed record HirCastExpression(
    SemanticId Id,
    SourceSpan Span,
    string TypeName,
    HirExpression Operand) : HirExpression(Id, Span);

public sealed record HirConditionalExpression(
    SemanticId Id,
    SourceSpan Span,
    HirExpression Condition,
    HirExpression WhenTrue,
    HirExpression WhenFalse) : HirExpression(Id, Span);
