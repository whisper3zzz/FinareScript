using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.CSharp;

public sealed class CSharpIdentityReconciler
{
    private const int ContextTokenCount = 4;
    private readonly Func<SemanticId> idFactory;

    public CSharpIdentityReconciler(Func<SemanticId>? idFactory = null)
    {
        this.idFactory = idFactory ?? SemanticId.New;
    }

    public IdentityReconciliationResult Reconcile(
        CSharpSourceDocument source,
        SyntaxTree syntaxTree,
        HirCompilationUnit compilation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(syntaxTree);
        ArgumentNullException.ThrowIfNull(compilation);
        IdentitySidecarDocument previous = source.IdentitySidecar ??
            throw new ArgumentException("Identity reconciliation requires a source sidecar.", nameof(source));
        var diagnostics = new List<UnifiedDiagnostic>();
        List<IdentityCandidate> candidates = CreateCandidates(source, syntaxTree, compilation);
        var oldEntries = previous.Entries.ToDictionary(static entry => entry.Id);
        var tombstoneIds = previous.Tombstones.Select(static tombstone => tombstone.Id).ToHashSet();
        var usedOldIds = new HashSet<SemanticId>();
        var assignedIds = compilation.DescendantsAndSelf()
            .Where(static node => node is HirCompilationUnit or HirTypeDeclaration or HirMethodDeclaration)
            .Select(static node => node.Id)
            .ToHashSet();

        ApplyPins(source.IdentityPins, candidates, oldEntries, tombstoneIds, usedOldIds, assignedIds, diagnostics);
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == UnifiedDiagnosticSeverity.Error))
        {
            return new IdentityReconciliationResult(compilation, previous, diagnostics);
        }

        MatchExact(candidates, previous.Entries, usedOldIds, assignedIds);
        MatchStructural(candidates, previous.Entries, usedOldIds, assignedIds, diagnostics);
        MatchUniqueShapes(candidates, previous.Entries, usedOldIds, assignedIds);

        string sourceDigest = GetDigest(source.Source);
        int generation = string.Equals(sourceDigest, previous.SourceDigest, StringComparison.Ordinal)
            ? previous.Generation
            : checked(previous.Generation + 1);
        var unavailableIds = previous.Entries.Select(static entry => entry.Id)
            .Concat(previous.Tombstones.Select(static tombstone => tombstone.Id))
            .Concat(assignedIds)
            .ToHashSet();
        var entries = new List<IdentityEntry>(candidates.Count);
        var rewriteMap = new Dictionary<SemanticId, SemanticId>();
        foreach (IdentityCandidate candidate in candidates)
        {
            SemanticId id = candidate.AssignedId ?? AllocateId(unavailableIds, assignedIds);
            assignedIds.Add(id);
            unavailableIds.Add(id);
            rewriteMap.Add(candidate.Node.Id, id);
            IdentityEntry? oldEntry = oldEntries.GetValueOrDefault(id);
            int entryGeneration = oldEntry is not null && CandidateMatchesEntry(candidate, oldEntry)
                ? oldEntry.Generation
                : generation;
            entries.Add(candidate.ToEntry(id, entryGeneration));
        }

        var tombstones = previous.Tombstones.ToList();
        tombstones.AddRange(previous.Entries
            .Where(entry => !usedOldIds.Contains(entry.Id))
            .Select(entry => new IdentityTombstone(entry.Id, entry.Kind, entry.Owner, generation)));
        IdentitySidecarDocument sidecar = IdentitySidecarJson.NormalizeAndValidate(new IdentitySidecarDocument(
            IdentitySidecarDocument.CurrentSchemaVersion,
            source.LogicalPath,
            sourceDigest,
            generation,
            entries,
            tombstones));
        return new IdentityReconciliationResult(
            HirIdentityRewriter.Rewrite(compilation, rewriteMap),
            sidecar,
            diagnostics);
    }

    private static void ApplyPins(
        IEnumerable<CSharpIdentityPin> pins,
        IReadOnlyList<IdentityCandidate> candidates,
        IReadOnlyDictionary<SemanticId, IdentityEntry> oldEntries,
        IReadOnlySet<SemanticId> tombstoneIds,
        ISet<SemanticId> usedOldIds,
        ISet<SemanticId> assignedIds,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        foreach (CSharpIdentityPin pin in pins)
        {
            IdentityCandidate[] matches = candidates
                .Where(candidate => candidate.Kind == pin.Kind && candidate.Node.Span == pin.CurrentSpan)
                .ToArray();
            if (matches.Length != 1 ||
                !oldEntries.TryGetValue(pin.Id, out IdentityEntry? pinnedEntry) ||
                pinnedEntry.Kind != pin.Kind ||
                tombstoneIds.Contains(pin.Id) ||
                !assignedIds.Add(pin.Id))
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    IdentityDiagnosticCodes.InvalidPin,
                    UnifiedDiagnosticSeverity.Error,
                    $"Identity pin '{pin.Id}' did not resolve to exactly one live '{pin.Kind}' node.",
                    pin.CurrentSpan,
                    pin.Id));
                continue;
            }

            matches[0].AssignedId = pin.Id;
            usedOldIds.Add(pin.Id);
        }
    }

    private static void MatchExact(
        IEnumerable<IdentityCandidate> candidates,
        IReadOnlyList<IdentityEntry> oldEntries,
        ISet<SemanticId> usedOldIds,
        ISet<SemanticId> assignedIds)
    {
        foreach (IdentityCandidate candidate in candidates.Where(static candidate => candidate.AssignedId is null))
        {
            IdentityEntry[] matches = oldEntries
                .Where(entry => !usedOldIds.Contains(entry.Id) && ExactMatch(candidate, entry))
                .ToArray();
            if (matches.Length == 1)
            {
                Assign(candidate, matches[0], usedOldIds, assignedIds);
            }
        }
    }

    private static void MatchStructural(
        IEnumerable<IdentityCandidate> candidates,
        IReadOnlyList<IdentityEntry> oldEntries,
        ISet<SemanticId> usedOldIds,
        ISet<SemanticId> assignedIds,
        ICollection<UnifiedDiagnostic> diagnostics)
    {
        foreach (IdentityCandidate candidate in candidates.Where(static candidate => candidate.AssignedId is null))
        {
            IdentityEntry[] pool = oldEntries
                .Where(entry =>
                    !usedOldIds.Contains(entry.Id) &&
                    entry.Owner == candidate.Owner &&
                    entry.Kind == candidate.Kind &&
                    entry.Role == candidate.Role &&
                    entry.Fingerprint == candidate.Fingerprint)
                .ToArray();
            if (pool.Length == 0)
            {
                continue;
            }

            var scored = pool
                .Select(entry => new
                {
                    Entry = entry,
                    Score = GetMatchScore(candidate, entry),
                })
                .OrderByDescending(static match => match.Score)
                .ThenBy(static match => match.Entry.Id.Value, StringComparer.Ordinal)
                .ToArray();
            int bestScore = scored[0].Score;
            IdentityEntry[] best = scored
                .Where(match => match.Score == bestScore)
                .Select(static match => match.Entry)
                .ToArray();
            if (bestScore >= 4 && best.Length == 1)
            {
                Assign(candidate, best[0], usedOldIds, assignedIds);
            }
            else if (pool.Length > 1)
            {
                diagnostics.Add(new UnifiedDiagnostic(
                    IdentityDiagnosticCodes.AmbiguousMatch,
                    UnifiedDiagnosticSeverity.Warning,
                    $"Identity reconciliation found {pool.Length} plausible '{candidate.Kind}' " +
                    "matches without enough unique evidence; a new identity was allocated instead of guessing.",
                    candidate.Node.Span));
            }
        }
    }

    private static void MatchUniqueShapes(
        IReadOnlyList<IdentityCandidate> candidates,
        IReadOnlyList<IdentityEntry> oldEntries,
        ISet<SemanticId> usedOldIds,
        ISet<SemanticId> assignedIds)
    {
        foreach (IdentityCandidate candidate in candidates.Where(static candidate => candidate.AssignedId is null))
        {
            IdentityCandidate[] currentGroup = candidates
                .Where(other =>
                    other.AssignedId is null &&
                    other.Owner == candidate.Owner &&
                    other.Kind == candidate.Kind &&
                    other.Role == candidate.Role &&
                    other.ShapeFingerprint == candidate.ShapeFingerprint)
                .ToArray();
            IdentityEntry[] oldGroup = oldEntries
                .Where(entry =>
                    !usedOldIds.Contains(entry.Id) &&
                    entry.Owner == candidate.Owner &&
                    entry.Kind == candidate.Kind &&
                    entry.Role == candidate.Role &&
                    entry.ShapeFingerprint == candidate.ShapeFingerprint)
                .ToArray();
            if (currentGroup.Length == 1 && oldGroup.Length == 1)
            {
                Assign(candidate, oldGroup[0], usedOldIds, assignedIds);
            }
        }
    }

    private SemanticId AllocateId(ISet<SemanticId> unavailableIds, ISet<SemanticId> assignedIds)
    {
        for (int attempt = 0; attempt < 1024; attempt++)
        {
            SemanticId id = idFactory();
            if (!id.IsEmpty && !unavailableIds.Contains(id) && !assignedIds.Contains(id))
            {
                return id;
            }
        }

        throw new InvalidOperationException("The identity allocator did not produce a unique ID after 1024 attempts.");
    }

    private static void Assign(
        IdentityCandidate candidate,
        IdentityEntry entry,
        ISet<SemanticId> usedOldIds,
        ISet<SemanticId> assignedIds)
    {
        if (!assignedIds.Add(entry.Id))
        {
            return;
        }

        candidate.AssignedId = entry.Id;
        usedOldIds.Add(entry.Id);
    }

    private static bool ExactMatch(IdentityCandidate candidate, IdentityEntry entry) =>
        CandidateMatchesEntry(candidate, entry) && candidate.Anchor == entry.Anchor;

    private static bool CandidateMatchesEntry(IdentityCandidate candidate, IdentityEntry entry) =>
        candidate.Kind == entry.Kind &&
        candidate.Owner == entry.Owner &&
        candidate.Role == entry.Role &&
        candidate.Fingerprint == entry.Fingerprint &&
        candidate.ShapeFingerprint == entry.ShapeFingerprint &&
        candidate.ParentFingerprint == entry.ParentFingerprint &&
        candidate.ContextBefore == entry.ContextBefore &&
        candidate.ContextAfter == entry.ContextAfter &&
        candidate.Anchor == entry.Anchor;

    private static int GetMatchScore(IdentityCandidate candidate, IdentityEntry entry) =>
        (candidate.ParentFingerprint == entry.ParentFingerprint ? 4 : 0) +
        (candidate.ContextBefore == entry.ContextBefore ? 2 : 0) +
        (candidate.ContextAfter == entry.ContextAfter ? 2 : 0) +
        (candidate.Anchor.Start == entry.Anchor.Start ? 1 : 0);

    private static List<IdentityCandidate> CreateCandidates(
        CSharpSourceDocument source,
        SyntaxTree syntaxTree,
        HirCompilationUnit compilation)
    {
        SourceText sourceText = syntaxTree.GetText();
        SyntaxToken[] tokens = syntaxTree.GetRoot().DescendantTokens().ToArray();
        var fingerprintCache = new Dictionary<HirNode, string>(ReferenceEqualityComparer.Instance);
        var candidates = new List<IdentityCandidate>();
        foreach (HirTypeDeclaration type in compilation.Types)
        {
            foreach (HirMethodDeclaration method in type.Methods)
            {
                string owner = $"{type.QualifiedName}.{method.Name}(" +
                    string.Join(',', method.Parameters.Select(static parameter => parameter.TypeName)) + ")";
                string methodParent = GetDigest($"method|{owner}");
                foreach (HirParameter parameter in method.Parameters)
                {
                    Add(parameter, owner, "parameter", methodParent);
                }

                Visit(method.Body, owner, "body", methodParent);
            }
        }

        return candidates;

        void Visit(HirNode node, string owner, string role, string parentFingerprint)
        {
            Add(node, owner, role, parentFingerprint);
            string childParent = GetDigest(GetShapeKey(node));
            switch (node)
            {
                case HirBlockStatement block:
                    foreach (HirStatement statement in block.Statements)
                    {
                        Visit(statement, owner, "statement", childParent);
                    }

                    break;
                case HirVariableDeclarationStatement { Initializer: not null } variable:
                    Visit(variable.Initializer, owner, "initializer", childParent);
                    break;
                case HirExpressionStatement statement:
                    Visit(statement.Expression, owner, "expression", childParent);
                    break;
                case HirIfStatement ifStatement:
                    Visit(ifStatement.Condition, owner, "condition", childParent);
                    Visit(ifStatement.Then, owner, "then", childParent);
                    if (ifStatement.Else is not null)
                    {
                        Visit(ifStatement.Else, owner, "else", childParent);
                    }

                    break;
                case HirWhileStatement whileStatement:
                    Visit(whileStatement.Condition, owner, "condition", childParent);
                    Visit(whileStatement.Body, owner, "body", childParent);
                    break;
                case HirDoWhileStatement doWhileStatement:
                    Visit(doWhileStatement.Body, owner, "body", childParent);
                    Visit(doWhileStatement.Condition, owner, "condition", childParent);
                    break;
                case HirForStatement forStatement:
                    if (forStatement.Declaration is not null)
                    {
                        Visit(forStatement.Declaration, owner, "initializerDeclaration", childParent);
                    }

                    foreach (HirExpression initializer in forStatement.Initializers)
                    {
                        Visit(initializer, owner, "initializer", childParent);
                    }

                    if (forStatement.Condition is not null)
                    {
                        Visit(forStatement.Condition, owner, "condition", childParent);
                    }

                    foreach (HirExpression incrementor in forStatement.Incrementors)
                    {
                        Visit(incrementor, owner, "incrementor", childParent);
                    }

                    Visit(forStatement.Body, owner, "body", childParent);
                    break;
                case HirForEachStatement forEachStatement:
                    Visit(forEachStatement.Variable, owner, "iterationVariable", childParent);
                    Visit(forEachStatement.Collection, owner, "collection", childParent);
                    Visit(forEachStatement.Body, owner, "body", childParent);
                    break;
                case HirForVariableDeclaration declaration:
                    foreach (HirForVariableDeclarator variable in declaration.Variables)
                    {
                        Visit(variable, owner, "variable", childParent);
                    }

                    break;
                case HirForVariableDeclarator { Initializer: not null } variable:
                    Visit(variable.Initializer, owner, "initializer", childParent);
                    break;
                case HirReturnStatement { Expression: not null } returnStatement:
                    Visit(returnStatement.Expression, owner, "returnValue", childParent);
                    break;
                case HirMemberAccessExpression member:
                    Visit(member.Target, owner, "target", childParent);
                    break;
                case HirConditionalAccessExpression conditionalAccess:
                    Visit(conditionalAccess.Receiver, owner, "receiver", childParent);
                    Visit(conditionalAccess.WhenNotNull, owner, "whenNotNull", childParent);
                    break;
                case HirInvocationExpression invocation:
                    Visit(invocation.Target, owner, "target", childParent);
                    foreach (HirExpression argument in invocation.Arguments)
                    {
                        Visit(argument, owner, "argument", childParent);
                    }

                    break;
                case HirBinaryExpression binary:
                    Visit(binary.Left, owner, "left", childParent);
                    Visit(binary.Right, owner, "right", childParent);
                    break;
                case HirUnaryExpression unary:
                    Visit(unary.Operand, owner, "operand", childParent);
                    break;
                case HirAssignmentExpression assignment:
                    Visit(assignment.Target, owner, "target", childParent);
                    Visit(assignment.Value, owner, "assignedValue", childParent);
                    break;
                case HirAwaitExpression awaitExpression:
                    Visit(awaitExpression.Operand, owner, "operand", childParent);
                    break;
                case HirCastExpression cast:
                    Visit(cast.Operand, owner, "operand", childParent);
                    break;
                case HirConditionalExpression conditional:
                    Visit(conditional.Condition, owner, "condition", childParent);
                    Visit(conditional.WhenTrue, owner, "whenTrue", childParent);
                    Visit(conditional.WhenFalse, owner, "whenFalse", childParent);
                    break;
            }
        }

        void Add(HirNode node, string owner, string role, string parentFingerprint)
        {
            IdentityAnchor anchor = GetAnchor(node.Span, sourceText);
            candidates.Add(new IdentityCandidate(
                node,
                GetKind(node),
                owner,
                role,
                GetStructuralFingerprint(node, fingerprintCache),
                GetDigest(GetShapeKey(node)),
                parentFingerprint,
                anchor,
                GetContext(tokens, anchor.Start, before: true),
                GetContext(tokens, anchor.Start + anchor.Length, before: false)));
        }
    }

    private static string GetStructuralFingerprint(
        HirNode node,
        IDictionary<HirNode, string> cache)
    {
        if (cache.TryGetValue(node, out string? fingerprint))
        {
            return fingerprint;
        }

        string key = node switch
        {
            HirBlockStatement block => GetShapeKey(block) + "[" + string.Join(
                ',',
                block.Statements.Select(statement => GetStructuralFingerprint(statement, cache))) + "]",
            HirVariableDeclarationStatement variable => GetShapeKey(variable) +
                Child(variable.Initializer),
            HirExpressionStatement statement => GetShapeKey(statement) + Child(statement.Expression),
            HirIfStatement ifStatement => GetShapeKey(ifStatement) +
                Child(ifStatement.Condition) + Child(ifStatement.Then) + Child(ifStatement.Else),
            HirWhileStatement whileStatement => GetShapeKey(whileStatement) +
                Child(whileStatement.Condition) + Child(whileStatement.Body),
            HirDoWhileStatement doWhileStatement => GetShapeKey(doWhileStatement) +
                Child(doWhileStatement.Body) + Child(doWhileStatement.Condition),
            HirForStatement forStatement => GetShapeKey(forStatement) +
                Child(forStatement.Declaration) +
                "[" + string.Join(',', forStatement.Initializers.Select(initializer =>
                    GetStructuralFingerprint(initializer, cache))) + "]" +
                Child(forStatement.Condition) +
                "[" + string.Join(',', forStatement.Incrementors.Select(incrementor =>
                    GetStructuralFingerprint(incrementor, cache))) + "]" +
                Child(forStatement.Body),
            HirForEachStatement forEachStatement => GetShapeKey(forEachStatement) +
                Child(forEachStatement.Variable) +
                Child(forEachStatement.Collection) +
                Child(forEachStatement.Body),
            HirForVariableDeclaration declaration => GetShapeKey(declaration) +
                "[" + string.Join(',', declaration.Variables.Select(variable =>
                    GetStructuralFingerprint(variable, cache))) + "]",
            HirForVariableDeclarator variable => GetShapeKey(variable) + Child(variable.Initializer),
            HirReturnStatement returnStatement => GetShapeKey(returnStatement) +
                Child(returnStatement.Expression),
            HirMemberAccessExpression member => GetShapeKey(member) + Child(member.Target),
            HirConditionalAccessExpression conditionalAccess => GetShapeKey(conditionalAccess) +
                Child(conditionalAccess.Receiver) + Child(conditionalAccess.WhenNotNull),
            HirInvocationExpression invocation => GetShapeKey(invocation) + Child(invocation.Target) +
                "[" + string.Join(',', invocation.Arguments.Select(argument =>
                    GetStructuralFingerprint(argument, cache))) + "]",
            HirBinaryExpression binary => GetShapeKey(binary) + Child(binary.Left) + Child(binary.Right),
            HirUnaryExpression unary => GetShapeKey(unary) + Child(unary.Operand),
            HirAssignmentExpression assignment => GetShapeKey(assignment) +
                Child(assignment.Target) + Child(assignment.Value),
            HirAwaitExpression awaitExpression => GetShapeKey(awaitExpression) + Child(awaitExpression.Operand),
            HirCastExpression cast => GetShapeKey(cast) + Child(cast.Operand),
            HirConditionalExpression conditional => GetShapeKey(conditional) +
                Child(conditional.Condition) +
                Child(conditional.WhenTrue) +
                Child(conditional.WhenFalse),
            _ => GetShapeKey(node),
        };
        fingerprint = GetDigest(key);
        cache.Add(node, fingerprint);
        return fingerprint;

        string Child(HirNode? child) => child is null ? "<null>" : GetStructuralFingerprint(child, cache);
    }

    private static string GetShapeKey(HirNode node) => node switch
    {
        HirParameter parameter => $"parameter|{parameter.Name}|{parameter.TypeName}",
        HirBlockStatement => "block",
        HirVariableDeclarationStatement variable => $"variable|{variable.Name}|{variable.TypeName}",
        HirExpressionStatement => "expressionStatement",
        HirIfStatement => "if",
        HirWhileStatement => "while",
        HirDoWhileStatement => "doWhile",
        HirForStatement forStatement =>
            $"for|{forStatement.Declaration is not null}|{forStatement.Initializers.Count}|" +
            $"{forStatement.Condition is not null}|{forStatement.Incrementors.Count}",
        HirForEachStatement => "forEach",
        HirForEachVariable variable => $"forEachVariable|{variable.Name}|{variable.TypeName}",
        HirForVariableDeclaration declaration =>
            $"forVariableDeclaration|{declaration.TypeName}|{declaration.Variables.Count}",
        HirForVariableDeclarator variable => $"forVariable|{variable.Name}",
        HirBreakStatement => "break",
        HirContinueStatement => "continue",
        HirReturnStatement => "return",
        HirLiteralExpression literal => $"literal|{literal.ValueText}",
        HirIdentifierExpression identifier => $"identifier|{identifier.Name}",
        HirThisExpression => "this",
        HirMemberAccessExpression member => $"memberAccess|{member.MemberName}",
        HirConditionalAccessExpression => "conditionalAccess",
        HirConditionalMemberBindingExpression member =>
            $"conditionalMemberBinding|{member.MemberName}",
        HirInvocationExpression invocation => $"invocation|{invocation.Arguments.Count}",
        HirBinaryExpression binary => $"binary|{binary.Operator}",
        HirUnaryExpression unary => $"unary|{unary.Operator}|{unary.IsPostfix}",
        HirAssignmentExpression assignment => $"assignment|{assignment.Operator}",
        HirAwaitExpression => "await",
        HirCastExpression cast => $"cast|{cast.TypeName}",
        HirConditionalExpression => "conditional",
        _ => node.GetType().Name,
    };

    private static string GetKind(HirNode node) => node switch
    {
        HirParameter => "parameter",
        HirBlockStatement => "block",
        HirVariableDeclarationStatement => "variableDeclaration",
        HirExpressionStatement => "expressionStatement",
        HirIfStatement => "if",
        HirWhileStatement => "while",
        HirDoWhileStatement => "doWhile",
        HirForStatement => "for",
        HirForEachStatement => "forEach",
        HirForEachVariable => "forEachVariable",
        HirForVariableDeclaration => "forVariableDeclaration",
        HirForVariableDeclarator => "forVariable",
        HirBreakStatement => "break",
        HirContinueStatement => "continue",
        HirReturnStatement => "return",
        HirLiteralExpression => "literal",
        HirIdentifierExpression => "identifier",
        HirThisExpression => "this",
        HirMemberAccessExpression => "memberAccess",
        HirConditionalAccessExpression => "conditionalAccess",
        HirConditionalMemberBindingExpression => "conditionalMemberBinding",
        HirInvocationExpression => "invocation",
        HirBinaryExpression => "binary",
        HirUnaryExpression => "unary",
        HirAssignmentExpression => "assignment",
        HirAwaitExpression => "await",
        HirCastExpression => "cast",
        HirConditionalExpression => "conditional",
        _ => node.GetType().Name,
    };

    private static IdentityAnchor GetAnchor(SourceSpan span, SourceText source)
    {
        int start = GetPosition(span.Start, source);
        int end = GetPosition(span.End, source);
        return new IdentityAnchor(start, end - start);
    }

    private static int GetPosition(SourcePosition position, SourceText source)
    {
        int lineIndex = position.Line - 1;
        if (lineIndex < 0 || lineIndex >= source.Lines.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "HIR source line is outside the source text.");
        }

        TextLine line = source.Lines[lineIndex];
        int character = position.Column - 1;
        if (character < 0 || character > line.SpanIncludingLineBreak.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "HIR source column is outside the source line.");
        }

        return line.Start + character;
    }

    private static string GetContext(IEnumerable<SyntaxToken> tokens, int position, bool before)
    {
        IEnumerable<SyntaxToken> selected = before
            ? tokens.Where(token => token.Span.End <= position).TakeLast(ContextTokenCount)
            : tokens.Where(token => token.SpanStart >= position).Take(ContextTokenCount);
        return GetDigest(string.Join('|', selected.Select(static token =>
            $"{token.RawKind}:{token.ValueText}")));
    }

    private static string GetDigest(string value)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private sealed class IdentityCandidate(
        HirNode node,
        string kind,
        string owner,
        string role,
        string fingerprint,
        string shapeFingerprint,
        string parentFingerprint,
        IdentityAnchor anchor,
        string contextBefore,
        string contextAfter)
    {
        public HirNode Node { get; } = node;

        public string Kind { get; } = kind;

        public string Owner { get; } = owner;

        public string Role { get; } = role;

        public string Fingerprint { get; } = fingerprint;

        public string ShapeFingerprint { get; } = shapeFingerprint;

        public string ParentFingerprint { get; } = parentFingerprint;

        public IdentityAnchor Anchor { get; } = anchor;

        public string ContextBefore { get; } = contextBefore;

        public string ContextAfter { get; } = contextAfter;

        public SemanticId? AssignedId { get; set; }

        public IdentityEntry ToEntry(SemanticId id, int generation) => new(
            id,
            Kind,
            Owner,
            Role,
            Fingerprint,
            ShapeFingerprint,
            ParentFingerprint,
            Anchor,
            ContextBefore,
            ContextAfter,
            generation);
    }
}
