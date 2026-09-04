using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.CSharp;

public sealed record CSharpDebugInstrumentation
{
    public const string ProbeTypeName = "global::UnifiedScript.Runtime.CoreClrDebugProbe";

    public CSharpDebugInstrumentation(string probeAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(probeAssemblyPath);
        ProbeAssemblyPath = Path.GetFullPath(probeAssemblyPath);
        if (!File.Exists(ProbeAssemblyPath))
        {
            throw new FileNotFoundException("The debug probe assembly does not exist.", ProbeAssemblyPath);
        }
    }

    public string ProbeAssemblyPath { get; }
}

internal sealed class CSharpDebugInstrumenter
{
    public IReadOnlyList<SyntaxTree> Instrument(
        CSharpCompilationRequest request,
        HirCompilationUnit hir,
        CSharpCompilationHost compilationHost)
    {
        SyntaxTree[] syntaxTrees = request.Sources
            .Select(source => CSharpSyntaxTree.ParseText(
                SourceText.From(source.Source, System.Text.Encoding.UTF8),
                request.Settings.ParseOptions,
                source.LogicalPath))
            .ToArray();
        CSharpCompilation compilation = compilationHost.Create(request, syntaxTrees);
        var statementMaps = hir.Types
            .SelectMany(static type => type.Methods)
            .SelectMany(method => method.Body.DescendantsAndSelf()
                .OfType<HirStatement>()
                .Where(static statement => statement is not HirBlockStatement)
                .Select(statement => new StatementProbe(method.Id, statement.Id, statement.Span)))
            .GroupBy(static probe => NormalizePath(probe.Span.FilePath), StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToDictionary(static probe => SpanKey.From(probe.Span)),
                StringComparer.Ordinal);
        var methodMaps = hir.Types
            .SelectMany(static type => type.Methods)
            .GroupBy(static method => NormalizePath(method.Span.FilePath), StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToDictionary(
                    static method => SpanKey.From(method.Span),
                    static method => method.Id),
                StringComparer.Ordinal);

        var rewritten = new SyntaxTree[syntaxTrees.Length];
        for (int index = 0; index < syntaxTrees.Length; index++)
        {
            SyntaxTree tree = syntaxTrees[index];
            string path = NormalizePath(tree.FilePath);
            if (!statementMaps.TryGetValue(path, out Dictionary<SpanKey, StatementProbe>? probes))
            {
                rewritten[index] = tree;
                continue;
            }

            var rewriter = new ProbeRewriter(
                compilation.GetSemanticModel(tree, ignoreAccessibility: false),
                path,
                probes,
                methodMaps.TryGetValue(path, out Dictionary<SpanKey, SemanticId>? methods)
                    ? methods
                    : new Dictionary<SpanKey, SemanticId>());
            SyntaxNode root = tree.GetRoot();
            SyntaxNode instrumented = rewriter.Visit(root) ?? root;
            rewritten[index] = CSharpSyntaxTree.Create(
                (CSharpSyntaxNode)instrumented,
                request.Settings.ParseOptions,
                tree.FilePath,
                System.Text.Encoding.UTF8);
        }

        return rewritten;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private sealed class ProbeRewriter(
        SemanticModel semanticModel,
        string filePath,
        IReadOnlyDictionary<SpanKey, StatementProbe> probes,
        IReadOnlyDictionary<SpanKey, SemanticId> methods) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            SyntaxNode? visited = base.Visit(node);
            if (node is StatementSyntax statement &&
                statement is not BlockSyntax &&
                statement.Parent is not BlockSyntax &&
                visited is StatementSyntax rewritten &&
                TryGetProbe(statement, out StatementProbe? probe))
            {
                return SyntaxFactory.Block(
                        CreateProbeStatement(statement, probe!).WithoutLeadingTrivia(),
                        rewritten.WithoutLeadingTrivia())
                    .WithTriviaFrom(rewritten);
            }

            return visited;
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var statements = new List<StatementSyntax>(node.Statements.Count * 2);
            foreach (StatementSyntax statement in node.Statements)
            {
                StatementSyntax rewritten = (StatementSyntax)(Visit(statement) ?? statement);
                if (TryGetProbe(statement, out StatementProbe? probe))
                {
                    statements.Add(CreateProbeStatement(statement, probe!));
                    rewritten = rewritten.WithoutLeadingTrivia();
                }

                statements.Add(rewritten);
            }

            return node.WithStatements(SyntaxFactory.List(statements));
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var rewritten = (MethodDeclarationSyntax)(base.VisitMethodDeclaration(node) ?? node);
            if (rewritten.Body is null ||
                !methods.TryGetValue(
                    SpanKey.From(RoslynSourceSpan.FromNode(node, filePath)),
                    out SemanticId methodId))
            {
                return rewritten;
            }

            UsingStatementSyntax scope = SyntaxFactory.UsingStatement(
                declaration: null,
                expression: SyntaxFactory.ParseExpression(
                    $"{CSharpDebugInstrumentation.ProbeTypeName}.Enter(\"{methodId.Value}\")"),
                statement: rewritten.Body.WithoutTrivia());
            return rewritten.WithBody(
                SyntaxFactory.Block(scope)
                    .WithTriviaFrom(rewritten.Body));
        }

        private bool TryGetProbe(StatementSyntax statement, out StatementProbe? probe) =>
            probes.TryGetValue(SpanKey.From(RoslynSourceSpan.FromNode(statement, filePath)), out probe);

        private StatementSyntax CreateProbeStatement(
            StatementSyntax statement,
            StatementProbe probe)
        {
            string captures = string.Join(
                ", ",
                GetCapturableSymbols(statement).Select(static symbol =>
                {
                    string identifier = EscapeIdentifier(symbol.Name);
                    return $"{CSharpDebugInstrumentation.ProbeTypeName}.Capture(\"" +
                        EscapeString(symbol.Name) + $"\", {identifier})";
                }));
            string suffix = captures.Length == 0 ? string.Empty : $", {captures}";
            StatementSyntax result = SyntaxFactory.ParseStatement(
                $"{CSharpDebugInstrumentation.ProbeTypeName}.Hit(\"{probe.MethodId.Value}\", " +
                $"\"{probe.NodeId.Value}\"{suffix});");
            return result.WithLeadingTrivia(statement.GetLeadingTrivia())
                .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
        }

        private IEnumerable<ISymbol> GetCapturableSymbols(StatementSyntax statement)
        {
            DataFlowAnalysis? flow = semanticModel.AnalyzeDataFlow(statement);
            foreach (ISymbol symbol in semanticModel.LookupSymbols(statement.SpanStart)
                         .Where(static symbol => symbol is IParameterSymbol or ILocalSymbol)
                         .OrderBy(static symbol => symbol.Name, StringComparer.Ordinal))
            {
                // A probe runs before the statement. Capturing an out parameter
                // before its first definite assignment makes valid user code fail
                // CS0269; after assignment it is an ordinary inspectable value.
                if (symbol is IParameterSymbol { RefKind: not RefKind.Out } ||
                    flow?.DefinitelyAssignedOnEntry.Contains(symbol, SymbolEqualityComparer.Default) == true)
                {
                    yield return symbol;
                }
            }
        }

        private static string EscapeIdentifier(string value) =>
            SyntaxFacts.GetKeywordKind(value) == SyntaxKind.None ? value : $"@{value}";

        private static string EscapeString(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private sealed record StatementProbe(
        SemanticId MethodId,
        SemanticId NodeId,
        SourceSpan Span);

    private readonly record struct SpanKey(
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn)
    {
        public static SpanKey From(SourceSpan span) => new(
            span.Start.Line,
            span.Start.Column,
            span.End.Line,
            span.End.Column);
    }
}
