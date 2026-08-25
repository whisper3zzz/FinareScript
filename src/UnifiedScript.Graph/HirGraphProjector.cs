using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Semantics;

namespace UnifiedScript.Graph;

public sealed class HirGraphProjector
{
    public GraphDocument Project(
        HirCompilationUnit compilationUnit,
        BoundCompilation? semantics = null,
        IReadOnlyDictionary<SemanticId, SemanticId>? declarationReferences = null)
    {
        ArgumentNullException.ThrowIfNull(compilationUnit);
        if (semantics is not null && semantics.SourceCompilationId != compilationUnit.Id)
        {
            throw new ArgumentException(
                "The bound semantics were produced for a different HIR compilation.",
                nameof(semantics));
        }

        var semanticIndex = new SemanticIndex(semantics, declarationReferences);
        IReadOnlyList<GraphFunction> functions = compilationUnit.Types
            .SelectMany(type => type.Methods.Select(method => ProjectMethod(type, method, semanticIndex)))
            .OrderBy(static function => function.QualifiedName, StringComparer.Ordinal)
            .ToArray();
        var graph = new GraphDocument(
            GraphDocument.CurrentSchemaVersion,
            compilationUnit.Id,
            semantics?.ConfigurationId,
            functions);
        _ = new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph);
        return graph;
    }

    private static GraphFunction ProjectMethod(
        HirTypeDeclaration type,
        HirMethodDeclaration method,
        SemanticIndex semantics)
    {
        var builder = new FunctionBuilder(semantics);
        SemanticId functionRegionId = RegionId(method.Id, GraphRegionKind.Function);
        builder.AddRegion(new RegionBuilder(
            functionRegionId,
            GraphRegionKind.Function,
            null,
            method.Id,
            method.Span,
            false));
        SemanticId entryId = SemanticId.FromKey($"{method.Id}:graph:entry");
        var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
        semantics.ApplyNodeProperties(method.Id, properties);
        HirAttribute? unrealEvent = method.Attributes?.FirstOrDefault(static attribute =>
            attribute.Name == "UnrealEvent" && attribute.Arguments.Count >= 2);
        if (unrealEvent is not null)
        {
            properties["unrealEventSourceType"] = unrealEvent.Arguments[0];
            properties["unrealEventPath"] = unrealEvent.Arguments[1];
            properties["unrealEventSource"] = unrealEvent.NamedArguments.GetValueOrDefault(
                "Source",
                "Target");
            if (unrealEvent.NamedArguments.TryGetValue("SourceMember", out string? sourceMember))
            {
                properties["unrealEventSourceMember"] = sourceMember;
            }
        }
        var entryPorts = new List<GraphPort>
        {
            new("then", GraphPortDirection.Output, GraphPortKind.Control),
        };
        if (unrealEvent is not null)
        {
            for (int index = 0; index < method.Parameters.Count; index++)
            {
                var parameterProperties = new SortedDictionary<string, string>(
                    StringComparer.Ordinal);
                semantics.ApplyNodeProperties(method.Parameters[index].Id, parameterProperties);
                if (parameterProperties.TryGetValue("typeId", out string? typeId))
                {
                    properties[$"eventParameter{index}TypeId"] = typeId;
                }
                if (parameterProperties.TryGetValue("resolvedType", out string? resolvedType))
                {
                    properties[$"eventParameter{index}ResolvedType"] = resolvedType;
                }
                entryPorts.Add(new GraphPort(
                    $"eventArg{index}",
                    GraphPortDirection.Output,
                    GraphPortKind.Data));
            }
        }
        builder.AddNode(new GraphNode(
            entryId,
            unrealEvent is null ? "entry" : "unrealEventEntry",
            method.Name,
            method.Span,
            entryPorts,
            properties),
            functionRegionId);

        builder.ProjectBlock(
            method.Body,
            [new ControlEndpoint(entryId, "then")],
            functionRegionId,
            GraphRegionKind.Block,
            false);
        return new GraphFunction(
            method.Id,
            $"{type.QualifiedName}.{method.Name}",
            builder.Nodes.OrderBy(static node => node.Id.Value, StringComparer.Ordinal).ToArray(),
            builder.Edges.OrderBy(static edge => edge.Id.Value, StringComparer.Ordinal).ToArray(),
            functionRegionId,
            builder.Regions.OrderBy(static region => region.Id.Value, StringComparer.Ordinal).ToArray());
    }

    private static SemanticId RegionId(SemanticId sourceId, GraphRegionKind kind) =>
        SemanticId.FromKey($"{sourceId}:graph:region:{kind}");

    private static IReadOnlyDictionary<string, string> EmptyProperties() =>
        new SortedDictionary<string, string>(StringComparer.Ordinal);

    private sealed record ControlEndpoint(SemanticId NodeId, string Port);

    private sealed record ControlTarget(SemanticId NodeId, string Port);

    private sealed record ValueEndpoint(SemanticId NodeId, string Port);

    private sealed record ExpressionProjection(ValueEndpoint? Value, IReadOnlyList<SemanticId> Effects);

    private sealed class RegionBuilder(
        SemanticId id,
        GraphRegionKind kind,
        SemanticId? parentRegionId,
        SemanticId sourceNodeId,
        SourceSpan span,
        bool isImplicit)
    {
        public SemanticId Id { get; } = id;

        public GraphRegionKind Kind { get; } = kind;

        public SemanticId? ParentRegionId { get; } = parentRegionId;

        public SemanticId SourceNodeId { get; } = sourceNodeId;

        public SourceSpan Span { get; } = span;

        public bool IsImplicit { get; } = isImplicit;

        public List<SemanticId> NodeIds { get; } = [];

        public List<SemanticId> ChildRegionIds { get; } = [];

        public GraphRegion Build() => new(
            Id,
            Kind,
            ParentRegionId,
            SourceNodeId,
            Span,
            IsImplicit,
            NodeIds.ToArray(),
            ChildRegionIds.ToArray());
    }

    private sealed class FunctionBuilder(SemanticIndex semantics)
    {
        private readonly Dictionary<SemanticId, GraphNode> nodes = [];
        private readonly Dictionary<SemanticId, GraphEdge> edges = [];
        private readonly Dictionary<SemanticId, RegionBuilder> regions = [];
        private readonly Stack<LoopContext> loops = [];

        public IEnumerable<GraphNode> Nodes => nodes.Values;

        public IEnumerable<GraphEdge> Edges => edges.Values;

        public IEnumerable<GraphRegion> Regions => regions.Values.Select(static region => region.Build());

        public IReadOnlyList<ControlEndpoint> ProjectBlock(
            HirBlockStatement block,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId parentRegionId,
            GraphRegionKind kind,
            bool isImplicit)
        {
            SemanticId regionId = kind is GraphRegionKind.Then or GraphRegionKind.Else
                ? throw new ArgumentException("Branch regions require their owning if ID.", nameof(kind))
                : RegionId(block.Id, kind);
            AddRegion(new RegionBuilder(
                regionId,
                kind,
                parentRegionId,
                block.Id,
                block.Span,
                isImplicit));
            return ProjectBlockContents(block, incoming, regionId);
        }

        private IReadOnlyList<ControlEndpoint> ProjectBlockContents(
            HirBlockStatement block,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId regionId)
        {
            IReadOnlyList<ControlEndpoint> current = incoming;
            foreach (HirStatement statement in block.Statements)
            {
                current = ProjectStatement(statement, current, regionId);
            }

            return current;
        }

        public void AddRegion(RegionBuilder region)
        {
            if (!regions.TryAdd(region.Id, region))
            {
                throw new InvalidOperationException($"Duplicate graph region ID {region.Id}.");
            }

            if (region.ParentRegionId is SemanticId parentRegionId)
            {
                if (!regions.TryGetValue(parentRegionId, out RegionBuilder? parent))
                {
                    throw new InvalidOperationException(
                        $"Parent graph region {parentRegionId} must be created first.");
                }

                parent.ChildRegionIds.Add(region.Id);
            }
        }

        public void AddNode(GraphNode node, SemanticId regionId)
        {
            if (!nodes.TryAdd(node.Id, node))
            {
                throw new InvalidOperationException($"Duplicate graph node ID {node.Id}.");
            }

            if (!regions.TryGetValue(regionId, out RegionBuilder? region))
            {
                throw new InvalidOperationException($"Unknown graph region ID {regionId}.");
            }

            region.NodeIds.Add(node.Id);
        }

        private IReadOnlyList<ControlEndpoint> ProjectStatement(
            HirStatement statement,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId regionId) => statement switch
            {
                HirBlockStatement block => ProjectBlock(
                    block,
                    incoming,
                    regionId,
                    GraphRegionKind.Block,
                    false),
                HirVariableDeclarationStatement variable => ProjectVariable(variable, incoming, regionId),
                HirExpressionStatement expression => ProjectExpressionStatement(expression, incoming, regionId),
                HirIfStatement ifStatement => ProjectIf(ifStatement, incoming, regionId),
                HirWhileStatement whileStatement => ProjectWhile(whileStatement, incoming, regionId),
                HirDoWhileStatement doWhileStatement => ProjectDoWhile(doWhileStatement, incoming, regionId),
                HirForStatement forStatement => ProjectFor(forStatement, incoming, regionId),
                HirForEachStatement forEachStatement => ProjectForEach(
                    forEachStatement,
                    incoming,
                    regionId),
                HirBreakStatement breakStatement => ProjectBreak(breakStatement, incoming, regionId),
                HirContinueStatement continueStatement => ProjectContinue(continueStatement, incoming, regionId),
                HirReturnStatement returnStatement => ProjectReturn(returnStatement, incoming, regionId),
                _ => throw new ArgumentOutOfRangeException(nameof(statement), statement.GetType().Name),
            };

        private IReadOnlyList<ControlEndpoint> ProjectVariable(
            HirVariableDeclarationStatement variable,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId regionId)
        {
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = variable.Name,
                ["type"] = variable.TypeName,
            };
            semantics.ApplyNodeProperties(variable.Id, properties);
            var ports = new List<GraphPort>
            {
                new("in", GraphPortDirection.Input, GraphPortKind.Control),
                new("then", GraphPortDirection.Output, GraphPortKind.Control),
            };
            ExpressionProjection? initializer = null;
            if (variable.Initializer is not null)
            {
                ports.Add(new GraphPort("value", GraphPortDirection.Input, GraphPortKind.Data));
                initializer = ProjectExpression(variable.Initializer, regionId);
            }

            AddNode(
                new GraphNode(
                    variable.Id,
                    "variable",
                    $"Declare {variable.Name}",
                    variable.Span,
                    ports,
                    properties),
                regionId);
            IReadOnlyList<ControlEndpoint> current = initializer is null
                ? incoming
                : ConnectEffectChain(incoming, initializer.Effects);
            ConnectIncoming(current, variable.Id, "in");
            if (initializer?.Value is not null)
            {
                AddEdge(GraphEdgeKind.Data, initializer.Value, new ValueEndpoint(variable.Id, "value"));
            }

            return [new ControlEndpoint(variable.Id, "then")];
        }

        private IReadOnlyList<ControlEndpoint> ProjectExpressionStatement(
            HirExpressionStatement statement,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId regionId)
        {
            ExpressionProjection expression = ProjectExpression(statement.Expression, regionId);
            IReadOnlyList<ControlEndpoint> afterEffects = ConnectEffectChain(incoming, expression.Effects);
            if (expression.Effects.Count > 0)
            {
                AnnotateDebugTarget(statement.Expression.Id, statement.Id);
                return afterEffects;
            }

            SemanticId evaluateId = SemanticId.FromKey($"{statement.Id}:graph:evaluate");
            var ports = new List<GraphPort>
            {
                new("in", GraphPortDirection.Input, GraphPortKind.Control),
                new("then", GraphPortDirection.Output, GraphPortKind.Control),
            };
            if (expression.Value is not null)
            {
                ports.Add(new GraphPort("expression", GraphPortDirection.Input, GraphPortKind.Data));
            }

            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["debugTargetId"] = statement.Id.Value,
            };
            AddNode(
                new GraphNode(
                    evaluateId,
                    "evaluate",
                    "Evaluate",
                    statement.Span,
                    ports,
                    properties),
                regionId);
            ConnectIncoming(incoming, evaluateId, "in");
            if (expression.Value is not null)
            {
                AddEdge(GraphEdgeKind.Data, expression.Value, new ValueEndpoint(evaluateId, "expression"));
            }

            return [new ControlEndpoint(evaluateId, "then")];
        }

        private void AnnotateDebugTarget(SemanticId graphNodeId, SemanticId statementId)
        {
            if (!nodes.TryGetValue(graphNodeId, out GraphNode? node))
            {
                throw new InvalidOperationException(
                    $"Expression statement root '{graphNodeId}' has no projected Graph node.");
            }

            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> property in node.Properties)
            {
                properties.Add(property.Key, property.Value);
            }

            properties.Add("debugTargetId", statementId.Value);
            nodes[graphNodeId] = node with { Properties = properties };
        }

        private IReadOnlyList<ControlEndpoint> ProjectIf(
            HirIfStatement statement,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId parentRegionId)
        {
            SemanticId ifRegionId = RegionId(statement.Id, GraphRegionKind.If);
            AddRegion(new RegionBuilder(
                ifRegionId,
                GraphRegionKind.If,
                parentRegionId,
                statement.Id,
                statement.Span,
                false));
            SemanticId thenRegionId = RegionId(statement.Id, GraphRegionKind.Then);
            AddRegion(new RegionBuilder(
                thenRegionId,
                GraphRegionKind.Then,
                ifRegionId,
                statement.Then.Id,
                statement.Then.Span,
                false));
            SemanticId elseRegionId = RegionId(statement.Id, GraphRegionKind.Else);
            AddRegion(new RegionBuilder(
                elseRegionId,
                GraphRegionKind.Else,
                ifRegionId,
                statement.Else?.Id ?? statement.Id,
                statement.Else?.Span ?? statement.Span,
                statement.Else is null));

            ExpressionProjection condition = ProjectExpression(statement.Condition, ifRegionId);
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
            semantics.ApplyNodeProperties(statement.Condition.Id, properties);
            AddNode(new GraphNode(
                statement.Id,
                "branch",
                "Branch",
                statement.Span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort("then", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("else", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("condition", GraphPortDirection.Input, GraphPortKind.Data),
                ],
                properties),
                ifRegionId);
            IReadOnlyList<ControlEndpoint> current = ConnectEffectChain(incoming, condition.Effects);
            ConnectIncoming(current, statement.Id, "in");
            if (condition.Value is null)
            {
                throw new InvalidOperationException("A branch condition must produce a value.");
            }

            AddEdge(GraphEdgeKind.Data, condition.Value, new ValueEndpoint(statement.Id, "condition"));
            IReadOnlyList<ControlEndpoint> thenExits = ProjectBlockContents(
                statement.Then,
                [new ControlEndpoint(statement.Id, "then")],
                thenRegionId);
            IReadOnlyList<ControlEndpoint> elseExits = statement.Else is null
                ? [new ControlEndpoint(statement.Id, "else")]
                : ProjectBlockContents(
                    statement.Else,
                    [new ControlEndpoint(statement.Id, "else")],
                    elseRegionId);
            return thenExits.Concat(elseExits).ToArray();
        }

        private IReadOnlyList<ControlEndpoint> ProjectWhile(
            HirWhileStatement statement,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId parentRegionId)
        {
            SemanticId loopRegionId = RegionId(statement.Id, GraphRegionKind.Loop);
            AddRegion(new RegionBuilder(
                loopRegionId,
                GraphRegionKind.Loop,
                parentRegionId,
                statement.Id,
                statement.Span,
                false));
            SemanticId bodyRegionId = RegionId(statement.Id, GraphRegionKind.LoopBody);
            AddRegion(new RegionBuilder(
                bodyRegionId,
                GraphRegionKind.LoopBody,
                loopRegionId,
                statement.Body.Id,
                statement.Body.Span,
                false));

            ExpressionProjection condition = ProjectExpression(statement.Condition, loopRegionId);
            SemanticId[] conditionEffects = condition.Effects.Distinct().ToArray();
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
            semantics.ApplyNodeProperties(statement.Condition.Id, properties);
            AddNode(new GraphNode(
                statement.Id,
                "loop",
                "While",
                statement.Span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort("body", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("exit", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("condition", GraphPortDirection.Input, GraphPortKind.Data),
                ],
                properties),
                loopRegionId);
            IReadOnlyList<ControlEndpoint> beforeCondition = ConnectEffectChain(incoming, conditionEffects);
            ConnectIncoming(beforeCondition, statement.Id, "in");
            if (condition.Value is null)
            {
                throw new InvalidOperationException("A while condition must produce a value.");
            }

            AddEdge(GraphEdgeKind.Data, condition.Value, new ValueEndpoint(statement.Id, "condition"));
            ControlTarget continueTarget = conditionEffects.Length == 0
                ? new ControlTarget(statement.Id, "in")
                : new ControlTarget(conditionEffects[0], "execIn");
            var context = new LoopContext(continueTarget);
            loops.Push(context);
            IReadOnlyList<ControlEndpoint> bodyExits;
            try
            {
                bodyExits = ProjectBlockContents(
                    statement.Body,
                    [new ControlEndpoint(statement.Id, "body")],
                    bodyRegionId);
            }
            finally
            {
                _ = loops.Pop();
            }

            ConnectIncoming(bodyExits, continueTarget.NodeId, continueTarget.Port);
            return [new ControlEndpoint(statement.Id, "exit"), .. context.BreakExits];
        }

        private IReadOnlyList<ControlEndpoint> ProjectFor(
            HirForStatement statement,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId parentRegionId)
        {
            SemanticId loopRegionId = RegionId(statement.Id, GraphRegionKind.Loop);
            AddRegion(new RegionBuilder(
                loopRegionId,
                GraphRegionKind.Loop,
                parentRegionId,
                statement.Id,
                statement.Span,
                false));
            SemanticId initializerRegionId = RegionId(statement.Id, GraphRegionKind.LoopInitializer);
            AddRegion(new RegionBuilder(
                initializerRegionId,
                GraphRegionKind.LoopInitializer,
                loopRegionId,
                statement.Declaration?.Id ?? statement.Id,
                statement.Declaration?.Span ?? statement.Span,
                statement.Declaration is null && statement.Initializers.Count == 0));
            SemanticId bodyRegionId = RegionId(statement.Id, GraphRegionKind.LoopBody);
            AddRegion(new RegionBuilder(
                bodyRegionId,
                GraphRegionKind.LoopBody,
                loopRegionId,
                statement.Body.Id,
                statement.Body.Span,
                false));
            SemanticId incrementRegionId = RegionId(statement.Id, GraphRegionKind.LoopIncrement);
            AddRegion(new RegionBuilder(
                incrementRegionId,
                GraphRegionKind.LoopIncrement,
                loopRegionId,
                statement.Id,
                statement.Span,
                statement.Incrementors.Count == 0));

            ExpressionProjection? condition = statement.Condition is null
                ? null
                : ProjectExpression(statement.Condition, loopRegionId);
            SemanticId[] conditionEffects = condition?.Effects.Distinct().ToArray() ?? [];
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["loopForm"] = "for",
                ["initializerKind"] = statement.Declaration is not null
                    ? "declaration"
                    : statement.Initializers.Count > 0
                        ? "expressions"
                        : "none",
                ["conditionEvaluation"] = statement.Condition is null
                    ? "implicitTrue"
                    : "explicit",
                ["incrementorCount"] = statement.Incrementors.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["continueTarget"] = statement.Incrementors.Count > 0
                    ? "incrementors"
                    : "condition",
                ["evaluationOrder"] = "initializer,condition,body,incrementors",
            };
            if (statement.Condition is not null)
            {
                semantics.ApplyNodeProperties(statement.Condition.Id, properties);
            }

            var ports = new List<GraphPort>
            {
                new("in", GraphPortDirection.Input, GraphPortKind.Control),
                new("body", GraphPortDirection.Output, GraphPortKind.Control),
                new("exit", GraphPortDirection.Output, GraphPortKind.Control),
            };
            if (statement.Condition is not null)
            {
                ports.Add(new GraphPort("condition", GraphPortDirection.Input, GraphPortKind.Data));
            }

            AddNode(new GraphNode(
                statement.Id,
                "loop",
                "For",
                statement.Span,
                ports,
                properties),
                loopRegionId);
            if (statement.Condition is not null)
            {
                AddRequiredDataEdge(
                    condition?.Value,
                    new ValueEndpoint(statement.Id, "condition"));
            }

            ControlTarget headerTarget = conditionEffects.Length == 0
                ? new ControlTarget(statement.Id, "in")
                : new ControlTarget(conditionEffects[0], "execIn");

            IReadOnlyList<ControlEndpoint> initializerExits = incoming;
            if (statement.Declaration is not null)
            {
                foreach (HirForVariableDeclarator variable in statement.Declaration.Variables)
                {
                    initializerExits = ProjectForVariable(
                        variable,
                        statement.Declaration.TypeName,
                        initializerExits,
                        initializerRegionId);
                }
            }

            for (int index = 0; index < statement.Initializers.Count; index++)
            {
                (_, initializerExits) = ProjectLoopPhaseExpression(
                    statement.Initializers[index],
                    initializerExits,
                    initializerRegionId,
                    statement.Id,
                    "initializer",
                    index);
            }

            ConnectIncoming(initializerExits, headerTarget.NodeId, headerTarget.Port);
            IReadOnlyList<ControlEndpoint> beforeHeader = ConnectEffectChain([], conditionEffects);
            ConnectIncoming(beforeHeader, statement.Id, "in");

            ControlTarget? incrementEntry = null;
            IReadOnlyList<ControlEndpoint> incrementExits = [];
            for (int index = 0; index < statement.Incrementors.Count; index++)
            {
                (ControlTarget entry, IReadOnlyList<ControlEndpoint> exits) =
                    ProjectLoopPhaseExpression(
                        statement.Incrementors[index],
                        incrementExits,
                        incrementRegionId,
                        statement.Id,
                        "incrementor",
                        index);
                incrementEntry ??= entry;
                incrementExits = exits;
            }

            ControlTarget continueTarget = incrementEntry ?? headerTarget;
            var context = new LoopContext(continueTarget);
            loops.Push(context);
            IReadOnlyList<ControlEndpoint> bodyExits;
            try
            {
                bodyExits = ProjectBlockContents(
                    statement.Body,
                    [new ControlEndpoint(statement.Id, "body")],
                    bodyRegionId);
            }
            finally
            {
                _ = loops.Pop();
            }

            ConnectIncoming(bodyExits, continueTarget.NodeId, continueTarget.Port);
            if (incrementEntry is not null)
            {
                ConnectIncoming(incrementExits, headerTarget.NodeId, headerTarget.Port);
            }

            return [new ControlEndpoint(statement.Id, "exit"), .. context.BreakExits];
        }

        private IReadOnlyList<ControlEndpoint> ProjectDoWhile(
            HirDoWhileStatement statement,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId parentRegionId)
        {
            SemanticId loopRegionId = RegionId(statement.Id, GraphRegionKind.Loop);
            AddRegion(new RegionBuilder(
                loopRegionId,
                GraphRegionKind.Loop,
                parentRegionId,
                statement.Id,
                statement.Span,
                false));
            SemanticId bodyRegionId = RegionId(statement.Id, GraphRegionKind.LoopBody);
            AddRegion(new RegionBuilder(
                bodyRegionId,
                GraphRegionKind.LoopBody,
                loopRegionId,
                statement.Body.Id,
                statement.Body.Span,
                false));

            SemanticId entryId = SemanticId.FromKey($"{statement.Id}:graph:doEntry");
            AddNode(new GraphNode(
                entryId,
                "loopEntry",
                "Do Entry",
                statement.Span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort("then", GraphPortDirection.Output, GraphPortKind.Control),
                ],
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["phase"] = "initialBodyEntry",
                }),
                loopRegionId);
            ConnectIncoming(incoming, entryId, "in");

            ExpressionProjection condition = ProjectExpression(statement.Condition, loopRegionId);
            SemanticId[] conditionEffects = condition.Effects.Distinct().ToArray();
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["loopForm"] = "doWhile",
                ["conditionEvaluation"] = "afterBody",
                ["continueTarget"] = "condition",
                ["evaluationOrder"] = "body,condition",
            };
            semantics.ApplyNodeProperties(statement.Condition.Id, properties);
            AddNode(new GraphNode(
                statement.Id,
                "loop",
                "Do While",
                statement.Span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort("body", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("exit", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("condition", GraphPortDirection.Input, GraphPortKind.Data),
                ],
                properties),
                loopRegionId);
            IReadOnlyList<ControlEndpoint> beforeCondition = ConnectEffectChain([], conditionEffects);
            ConnectIncoming(beforeCondition, statement.Id, "in");
            if (condition.Value is null)
            {
                throw new InvalidOperationException("A do-while condition must produce a value.");
            }

            AddEdge(GraphEdgeKind.Data, condition.Value, new ValueEndpoint(statement.Id, "condition"));
            ControlTarget conditionTarget = conditionEffects.Length == 0
                ? new ControlTarget(statement.Id, "in")
                : new ControlTarget(conditionEffects[0], "execIn");
            var context = new LoopContext(conditionTarget);
            loops.Push(context);
            IReadOnlyList<ControlEndpoint> bodyExits;
            try
            {
                bodyExits = ProjectBlockContents(
                    statement.Body,
                    [
                        new ControlEndpoint(entryId, "then"),
                        new ControlEndpoint(statement.Id, "body"),
                    ],
                    bodyRegionId);
            }
            finally
            {
                _ = loops.Pop();
            }

            ConnectIncoming(bodyExits, conditionTarget.NodeId, conditionTarget.Port);
            return [new ControlEndpoint(statement.Id, "exit"), .. context.BreakExits];
        }

        private IReadOnlyList<ControlEndpoint> ProjectForEach(
            HirForEachStatement statement,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId parentRegionId)
        {
            BoundForEachInfo binding = semantics.GetForEach(statement.Id);
            if (binding.Strategy == ForEachIterationStrategy.Enumerator)
            {
                return ProjectEnumeratorForEach(statement, binding, incoming, parentRegionId);
            }

            SemanticId loopRegionId = RegionId(statement.Id, GraphRegionKind.Loop);
            AddRegion(new RegionBuilder(
                loopRegionId,
                GraphRegionKind.Loop,
                parentRegionId,
                statement.Id,
                statement.Span,
                false));
            SemanticId collectionRegionId = RegionId(
                statement.Id,
                GraphRegionKind.LoopCollection);
            AddRegion(new RegionBuilder(
                collectionRegionId,
                GraphRegionKind.LoopCollection,
                loopRegionId,
                statement.Collection.Id,
                statement.Collection.Span,
                false));
            SemanticId bodyRegionId = RegionId(statement.Id, GraphRegionKind.LoopBody);
            AddRegion(new RegionBuilder(
                bodyRegionId,
                GraphRegionKind.LoopBody,
                loopRegionId,
                statement.Body.Id,
                statement.Body.Span,
                false));

            ExpressionProjection collection = ProjectExpression(
                statement.Collection,
                collectionRegionId);
            SemanticId captureId = SemanticId.FromKey(
                $"{statement.Id}:graph:foreach:collectionCapture");
            AddNode(new GraphNode(
                captureId,
                "collectionCapture",
                "Capture Collection",
                statement.Collection.Span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort("then", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("value", GraphPortDirection.Input, GraphPortKind.Data),
                    new GraphPort("collection", GraphPortDirection.Output, GraphPortKind.Data),
                ],
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["evaluation"] = "once",
                    ["strategy"] = "indexed",
                }),
                collectionRegionId);
            IReadOnlyList<ControlEndpoint> beforeCapture = ConnectEffectChain(
                incoming,
                collection.Effects.Distinct().ToArray());
            ConnectIncoming(beforeCapture, captureId, "in");
            AddRequiredDataEdge(
                collection.Value,
                new ValueEndpoint(captureId, "value"));

            var loopProperties = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["loopForm"] = "forEach",
                ["collectionEvaluation"] = "once",
                ["collectionStrategy"] = "indexed",
                ["iterationAdvance"] = "beforeBody",
                ["continueTarget"] = "moveNext",
                ["disposal"] = "none",
                ["evaluationOrder"] = "collection,moveNext,current,body",
            };
            semantics.ApplyNodeProperties(statement.Collection.Id, loopProperties);
            AddNode(new GraphNode(
                statement.Id,
                "loop",
                "Foreach",
                statement.Span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort("body", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("exit", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("collection", GraphPortDirection.Input, GraphPortKind.Data),
                ],
                loopProperties),
                loopRegionId);
            ConnectIncoming(
                [new ControlEndpoint(captureId, "then")],
                statement.Id,
                "in");
            AddEdge(
                GraphEdgeKind.Data,
                new ValueEndpoint(captureId, "collection"),
                new ValueEndpoint(statement.Id, "collection"));

            var variableProperties = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = statement.Variable.Name,
                ["type"] = statement.Variable.TypeName,
                ["binding"] = "current",
                ["readOnly"] = "true",
            };
            semantics.ApplyNodeProperties(statement.Variable.Id, variableProperties);
            AddNode(new GraphNode(
                statement.Variable.Id,
                "iterationVariable",
                $"Current {statement.Variable.Name}",
                statement.Variable.Span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort("then", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("collection", GraphPortDirection.Input, GraphPortKind.Data),
                    new GraphPort("value", GraphPortDirection.Output, GraphPortKind.Data),
                ],
                variableProperties),
                bodyRegionId);
            ConnectIncoming(
                [new ControlEndpoint(statement.Id, "body")],
                statement.Variable.Id,
                "in");
            AddEdge(
                GraphEdgeKind.Data,
                new ValueEndpoint(captureId, "collection"),
                new ValueEndpoint(statement.Variable.Id, "collection"));

            var context = new LoopContext(new ControlTarget(statement.Id, "in"));
            loops.Push(context);
            IReadOnlyList<ControlEndpoint> bodyExits;
            try
            {
                bodyExits = ProjectBlockContents(
                    statement.Body,
                    [new ControlEndpoint(statement.Variable.Id, "then")],
                    bodyRegionId);
            }
            finally
            {
                _ = loops.Pop();
            }

            ConnectIncoming(bodyExits, statement.Id, "in");
            return [new ControlEndpoint(statement.Id, "exit"), .. context.BreakExits];
        }

        private IReadOnlyList<ControlEndpoint> ProjectEnumeratorForEach(
            HirForEachStatement statement,
            BoundForEachInfo binding,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId parentRegionId)
        {
            if (!binding.RequiresDisposal ||
                binding.EnumeratorTypeId is not SemanticId enumeratorTypeId ||
                binding.GetEnumeratorMethodId is not SemanticId getEnumeratorMethodId ||
                binding.MoveNextMethodId is not SemanticId moveNextMethodId ||
                binding.CurrentGetterMethodId is not SemanticId currentGetterMethodId ||
                binding.DisposeMethodId is not SemanticId disposeMethodId)
            {
                throw new InvalidOperationException(
                    $"Enumerator foreach '{statement.Id}' requires a complete synchronous disposal protocol.");
            }

            SemanticId loopRegionId = RegionId(statement.Id, GraphRegionKind.Loop);
            AddRegion(new RegionBuilder(
                loopRegionId,
                GraphRegionKind.Loop,
                parentRegionId,
                statement.Id,
                statement.Span,
                false));
            SemanticId collectionRegionId = RegionId(
                statement.Id,
                GraphRegionKind.LoopCollection);
            AddRegion(new RegionBuilder(
                collectionRegionId,
                GraphRegionKind.LoopCollection,
                loopRegionId,
                statement.Collection.Id,
                statement.Collection.Span,
                false));
            SemanticId bodyRegionId = RegionId(statement.Id, GraphRegionKind.LoopBody);
            AddRegion(new RegionBuilder(
                bodyRegionId,
                GraphRegionKind.LoopBody,
                loopRegionId,
                statement.Body.Id,
                statement.Body.Span,
                false));
            SemanticId cleanupRegionId = RegionId(statement.Id, GraphRegionKind.LoopCleanup);
            AddRegion(new RegionBuilder(
                cleanupRegionId,
                GraphRegionKind.LoopCleanup,
                loopRegionId,
                statement.Id,
                statement.Span,
                false));

            ExpressionProjection collection = ProjectExpression(
                statement.Collection,
                collectionRegionId);
            SemanticId captureId = SemanticId.FromKey(
                $"{statement.Id}:graph:foreach:collectionCapture");
            AddNode(new GraphNode(
                captureId,
                "collectionCapture",
                "Capture Collection",
                statement.Collection.Span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort("then", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("value", GraphPortDirection.Input, GraphPortKind.Data),
                    new GraphPort("collection", GraphPortDirection.Output, GraphPortKind.Data),
                ],
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["evaluation"] = "once",
                    ["strategy"] = "enumerator",
                }),
                collectionRegionId);
            IReadOnlyList<ControlEndpoint> beforeCapture = ConnectEffectChain(
                incoming,
                collection.Effects.Distinct().ToArray());
            ConnectIncoming(beforeCapture, captureId, "in");
            AddRequiredDataEdge(collection.Value, new ValueEndpoint(captureId, "value"));

            SemanticId acquireId = SemanticId.FromKey(
                $"{statement.Id}:graph:foreach:enumeratorAcquire");
            AddNode(new GraphNode(
                acquireId,
                "enumeratorAcquire",
                "Get Enumerator",
                statement.Collection.Span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort("then", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("collection", GraphPortDirection.Input, GraphPortKind.Data),
                    new GraphPort("enumerator", GraphPortDirection.Output, GraphPortKind.Data),
                ],
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["getEnumeratorMethodId"] = getEnumeratorMethodId.Value,
                    ["enumeratorTypeId"] = enumeratorTypeId.Value,
                }),
                collectionRegionId);
            ConnectIncoming([new ControlEndpoint(captureId, "then")], acquireId, "in");
            AddEdge(
                GraphEdgeKind.Data,
                new ValueEndpoint(captureId, "collection"),
                new ValueEndpoint(acquireId, "collection"));

            var loopProperties = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["loopForm"] = "forEach",
                ["collectionEvaluation"] = "once",
                ["collectionStrategy"] = "enumerator",
                ["iterationAdvance"] = "beforeBody",
                ["continueTarget"] = "moveNext",
                ["disposal"] = "finally",
                ["cleanupOn"] = "exhaustion,break,return,exception",
                ["evaluationOrder"] = "collection,getEnumerator,moveNext,current,body,dispose",
                ["moveNextMethodId"] = moveNextMethodId.Value,
            };
            semantics.ApplyNodeProperties(statement.Collection.Id, loopProperties);
            AddNode(new GraphNode(
                statement.Id,
                "loop",
                "Foreach Move Next",
                statement.Span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort("body", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("exit", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("enumerator", GraphPortDirection.Input, GraphPortKind.Data),
                ],
                loopProperties),
                loopRegionId);
            ConnectIncoming([new ControlEndpoint(acquireId, "then")], statement.Id, "in");
            AddEdge(
                GraphEdgeKind.Data,
                new ValueEndpoint(acquireId, "enumerator"),
                new ValueEndpoint(statement.Id, "enumerator"));

            var variableProperties = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = statement.Variable.Name,
                ["type"] = statement.Variable.TypeName,
                ["binding"] = "current",
                ["readOnly"] = "true",
                ["currentGetterMethodId"] = currentGetterMethodId.Value,
            };
            semantics.ApplyNodeProperties(statement.Variable.Id, variableProperties);
            AddNode(new GraphNode(
                statement.Variable.Id,
                "iterationVariable",
                $"Current {statement.Variable.Name}",
                statement.Variable.Span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort("then", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("enumerator", GraphPortDirection.Input, GraphPortKind.Data),
                    new GraphPort("value", GraphPortDirection.Output, GraphPortKind.Data),
                ],
                variableProperties),
                bodyRegionId);
            ConnectIncoming(
                [new ControlEndpoint(statement.Id, "body")],
                statement.Variable.Id,
                "in");
            AddEdge(
                GraphEdgeKind.Data,
                new ValueEndpoint(acquireId, "enumerator"),
                new ValueEndpoint(statement.Variable.Id, "enumerator"));

            SemanticId normalDisposeId = SemanticId.FromKey(
                $"{statement.Id}:graph:foreach:dispose:normal");
            SemanticId returnDisposeId = SemanticId.FromKey(
                $"{statement.Id}:graph:foreach:dispose:return");
            SemanticId exceptionDisposeId = SemanticId.FromKey(
                $"{statement.Id}:graph:foreach:dispose:exception");
            LoopContext? outerCleanup = FindCleanupContext();
            AddEnumeratorDisposeNode(
                normalDisposeId,
                "Dispose Enumerator",
                "exhaustionOrBreak",
                "then",
                disposeMethodId,
                acquireId,
                statement.Span,
                cleanupRegionId,
                nextExceptionalCleanupId: null);
            AddEnumeratorDisposeNode(
                returnDisposeId,
                "Dispose Before Return",
                "return",
                "then",
                disposeMethodId,
                acquireId,
                statement.Span,
                cleanupRegionId,
                nextExceptionalCleanupId: null);
            AddEnumeratorDisposeNode(
                exceptionDisposeId,
                "Dispose Before Rethrow",
                "exception",
                "throw",
                disposeMethodId,
                acquireId,
                statement.Span,
                cleanupRegionId,
                outerCleanup?.ExceptionCleanupNodeId);

            var context = new LoopContext(
                new ControlTarget(statement.Id, "in"),
                returnDisposeId,
                exceptionDisposeId);
            loops.Push(context);
            IReadOnlyList<ControlEndpoint> bodyExits;
            try
            {
                bodyExits = ProjectBlockContents(
                    statement.Body,
                    [new ControlEndpoint(statement.Variable.Id, "then")],
                    bodyRegionId);
            }
            finally
            {
                _ = loops.Pop();
            }

            ConnectIncoming(bodyExits, statement.Id, "in");
            ConnectIncoming(
                [new ControlEndpoint(statement.Id, "exit"), .. context.BreakExits],
                normalDisposeId,
                "in");
            ConnectIncoming(context.ReturnExits, returnDisposeId, "in");
            if (context.ReturnExits.Count > 0)
            {
                ControlEndpoint returnAfterCleanup = new(returnDisposeId, "then");
                LoopContext? enclosingCleanup = FindCleanupContext();
                if (enclosingCleanup is not null)
                {
                    enclosingCleanup.ReturnExits.Add(returnAfterCleanup);
                }
            }

            return [new ControlEndpoint(normalDisposeId, "then")];
        }

        private void AddEnumeratorDisposeNode(
            SemanticId nodeId,
            string label,
            string reason,
            string outputPort,
            SemanticId disposeMethodId,
            SemanticId acquireId,
            SourceSpan span,
            SemanticId cleanupRegionId,
            SemanticId? nextExceptionalCleanupId)
        {
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["cleanupSemantics"] = "finally",
                ["disposeMethodId"] = disposeMethodId.Value,
                ["reason"] = reason,
                ["preservesPendingTransfer"] = reason == "exhaustionOrBreak" ? "false" : "true",
            };
            if (reason == "exception")
            {
                properties["nextExceptionalCleanup"] =
                    nextExceptionalCleanupId?.Value ?? "functionExit";
            }

            AddNode(new GraphNode(
                nodeId,
                "enumeratorDispose",
                label,
                span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort(outputPort, GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("enumerator", GraphPortDirection.Input, GraphPortKind.Data),
                ],
                properties),
                cleanupRegionId);
            AddEdge(
                GraphEdgeKind.Data,
                new ValueEndpoint(acquireId, "enumerator"),
                new ValueEndpoint(nodeId, "enumerator"));
        }

        private IReadOnlyList<ControlEndpoint> ProjectForVariable(
            HirForVariableDeclarator variable,
            string typeName,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId regionId)
        {
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = variable.Name,
                ["type"] = typeName,
                ["declarationContext"] = "forInitializer",
            };
            semantics.ApplyNodeProperties(variable.Id, properties);
            var ports = new List<GraphPort>
            {
                new("in", GraphPortDirection.Input, GraphPortKind.Control),
                new("then", GraphPortDirection.Output, GraphPortKind.Control),
            };
            ExpressionProjection? initializer = null;
            if (variable.Initializer is not null)
            {
                ports.Add(new GraphPort("value", GraphPortDirection.Input, GraphPortKind.Data));
                initializer = ProjectExpression(variable.Initializer, regionId);
            }

            AddNode(new GraphNode(
                variable.Id,
                "variable",
                $"Declare {variable.Name}",
                variable.Span,
                ports,
                properties),
                regionId);
            IReadOnlyList<ControlEndpoint> current = initializer is null
                ? incoming
                : ConnectEffectChain(incoming, initializer.Effects);
            ConnectIncoming(current, variable.Id, "in");
            if (initializer?.Value is not null)
            {
                AddEdge(GraphEdgeKind.Data, initializer.Value, new ValueEndpoint(variable.Id, "value"));
            }

            return [new ControlEndpoint(variable.Id, "then")];
        }

        private (ControlTarget Entry, IReadOnlyList<ControlEndpoint> Exits)
            ProjectLoopPhaseExpression(
                HirExpression expression,
                IReadOnlyList<ControlEndpoint> incoming,
                SemanticId regionId,
                SemanticId loopId,
                string phase,
                int index)
        {
            ExpressionProjection projection = ProjectExpression(expression, regionId);
            SemanticId[] effects = projection.Effects.Distinct().ToArray();
            if (effects.Length > 0)
            {
                return (
                    new ControlTarget(effects[0], "execIn"),
                    ConnectEffectChain(incoming, effects));
            }

            SemanticId evaluateId = SemanticId.FromKey(
                $"{loopId}:graph:{phase}:{index}:evaluate");
            var ports = new List<GraphPort>
            {
                new("in", GraphPortDirection.Input, GraphPortKind.Control),
                new("then", GraphPortDirection.Output, GraphPortKind.Control),
            };
            if (projection.Value is not null)
            {
                ports.Add(new GraphPort("expression", GraphPortDirection.Input, GraphPortKind.Data));
            }

            AddNode(new GraphNode(
                evaluateId,
                "evaluate",
                phase == "initializer" ? "Initialize" : "Increment",
                expression.Span,
                ports,
                EmptyProperties()),
                regionId);
            ConnectIncoming(incoming, evaluateId, "in");
            if (projection.Value is not null)
            {
                AddEdge(GraphEdgeKind.Data, projection.Value, new ValueEndpoint(evaluateId, "expression"));
            }

            return (
                new ControlTarget(evaluateId, "in"),
                [new ControlEndpoint(evaluateId, "then")]);
        }

        private IReadOnlyList<ControlEndpoint> ProjectBreak(
            HirBreakStatement statement,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId regionId)
        {
            LoopContext context = RequireLoopContext(statement);
            AddNode(ControlTransferNode(statement, "break", "Break"), regionId);
            ConnectIncoming(incoming, statement.Id, "in");
            context.BreakExits.Add(new ControlEndpoint(statement.Id, "then"));
            return [];
        }

        private IReadOnlyList<ControlEndpoint> ProjectContinue(
            HirContinueStatement statement,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId regionId)
        {
            LoopContext context = RequireLoopContext(statement);
            AddNode(ControlTransferNode(statement, "continue", "Continue"), regionId);
            ConnectIncoming(incoming, statement.Id, "in");
            ConnectIncoming(
                [new ControlEndpoint(statement.Id, "then")],
                context.ContinueTarget.NodeId,
                context.ContinueTarget.Port);
            return [];
        }

        private LoopContext RequireLoopContext(HirStatement statement) => loops.TryPeek(out LoopContext? context)
            ? context
            : throw new InvalidOperationException(
                $"Loop-control statement '{statement.Id}' has no enclosing loop.");

        private static GraphNode ControlTransferNode(
            HirStatement statement,
            string kind,
            string label) => new(
                statement.Id,
                kind,
                label,
                statement.Span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort("then", GraphPortDirection.Output, GraphPortKind.Control),
                ],
                EmptyProperties());

        private IReadOnlyList<ControlEndpoint> ProjectReturn(
            HirReturnStatement statement,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId regionId)
        {
            LoopContext? cleanupContext = FindCleanupContext();
            ExpressionProjection? expression = statement.Expression is null
                ? null
                : ProjectExpression(statement.Expression, regionId);
            var ports = new List<GraphPort>
            {
                new("in", GraphPortDirection.Input, GraphPortKind.Control),
            };
            if (expression?.Value is not null)
            {
                ports.Add(new GraphPort("value", GraphPortDirection.Input, GraphPortKind.Data));
            }

            if (cleanupContext is not null)
            {
                ports.Add(new GraphPort("then", GraphPortDirection.Output, GraphPortKind.Control));
            }

            AddNode(
                new GraphNode(
                    statement.Id,
                    "return",
                    "Return",
                    statement.Span,
                    ports,
                    EmptyProperties()),
                regionId);
            IReadOnlyList<ControlEndpoint> current = expression is null
                ? incoming
                : ConnectEffectChain(incoming, expression.Effects);
            ConnectIncoming(current, statement.Id, "in");
            if (expression?.Value is not null)
            {
                AddEdge(GraphEdgeKind.Data, expression.Value, new ValueEndpoint(statement.Id, "value"));
            }

            cleanupContext?.ReturnExits.Add(new ControlEndpoint(statement.Id, "then"));

            return [];
        }

        private ExpressionProjection ProjectExpression(
            HirExpression expression,
            SemanticId regionId,
            ValueEndpoint? conditionalReceiver = null)
        {
            ValueEndpoint? value = new(expression.Id, "value");
            IReadOnlyList<SemanticId> effects = [];
            switch (expression)
            {
                case HirLiteralExpression literal:
                    AddNode(
                        ValueNode(
                            literal,
                            "literal",
                            literal.ValueText,
                            KeyValuePair.Create("value", literal.ValueText)),
                        regionId);
                    break;
                case HirIdentifierExpression identifier:
                    AddNode(
                        ValueNode(
                            identifier,
                            "identifier",
                            identifier.Name,
                            KeyValuePair.Create("name", identifier.Name)),
                        regionId);
                    break;
                case HirThisExpression @this:
                    AddNode(ValueNode(@this, "this", "this"), regionId);
                    break;
                case HirMemberAccessExpression member:
                    {
                        ExpressionProjection target = ProjectExpression(
                            member.Target,
                            regionId,
                            conditionalReceiver);
                        AddNode(ValueNode(
                            member,
                            "memberAccess",
                            member.MemberName,
                            KeyValuePair.Create("member", member.MemberName),
                            [new GraphPort("target", GraphPortDirection.Input, GraphPortKind.Data)]),
                            regionId);
                        AddRequiredDataEdge(target.Value, new ValueEndpoint(member.Id, "target"));
                        effects = target.Effects;
                        break;
                    }
                case HirConditionalAccessExpression conditionalAccess:
                    {
                        ExpressionProjection receiver = ProjectExpression(
                            conditionalAccess.Receiver,
                            regionId,
                            conditionalReceiver);
                        if (receiver.Value is null)
                        {
                            throw new InvalidOperationException(
                                "A conditional-access receiver must produce a Graph value.");
                        }

                        ExpressionProjection whenNotNull = ProjectExpression(
                            conditionalAccess.WhenNotNull,
                            regionId,
                            receiver.Value);
                        if (whenNotNull.Effects.Count > 0)
                        {
                            throw new InvalidOperationException(
                                "Conditional-access Graph branches must be effect-free to preserve " +
                                "null-conditional evaluation.");
                        }

                        AddNode(ValueNode(
                            conditionalAccess,
                            "conditionalAccess",
                            "?.",
                            KeyValuePair.Create("evaluation", "conditionalWhenNotNull"),
                            [
                                new GraphPort(
                                    "receiver",
                                    GraphPortDirection.Input,
                                    GraphPortKind.Data),
                                new GraphPort(
                                    "whenNotNull",
                                    GraphPortDirection.Input,
                                    GraphPortKind.Data),
                            ],
                            additionalProperties:
                            [
                                KeyValuePair.Create(
                                    "whenNotNullEvaluation",
                                    "whenReceiverNonNull"),
                                KeyValuePair.Create(
                                    "resultSelection",
                                    "whenNotNullOrNull"),
                            ]),
                            regionId);
                        AddRequiredDataEdge(
                            receiver.Value,
                            new ValueEndpoint(conditionalAccess.Id, "receiver"));
                        AddRequiredDataEdge(
                            whenNotNull.Value,
                            new ValueEndpoint(conditionalAccess.Id, "whenNotNull"));
                        effects = receiver.Effects;
                        break;
                    }
                case HirConditionalMemberBindingExpression memberBinding:
                    {
                        if (conditionalReceiver is null)
                        {
                            throw new InvalidOperationException(
                                "A conditional member binding requires its enclosing receiver.");
                        }

                        AddNode(ValueNode(
                            memberBinding,
                            "conditionalMemberBinding",
                            memberBinding.MemberName,
                            KeyValuePair.Create("member", memberBinding.MemberName),
                            [new GraphPort(
                                "target",
                                GraphPortDirection.Input,
                                GraphPortKind.Data)]),
                            regionId);
                        AddEdge(
                            GraphEdgeKind.Data,
                            conditionalReceiver.NodeId,
                            conditionalReceiver.Port,
                            memberBinding.Id,
                            "target");
                        break;
                    }
                case HirInvocationExpression invocation:
                    {
                        ExpressionProjection target = ProjectExpression(
                            invocation.Target,
                            regionId,
                            conditionalReceiver);
                        ExpressionProjection[] arguments = invocation.Arguments
                            .Select(argument => ProjectExpression(
                                argument,
                                regionId,
                                conditionalReceiver))
                            .ToArray();
                        bool isEffectful = semantics.IsEffectful(invocation.Id);
                        bool hasValue = !semantics.IsVoid(invocation.Id);
                        var extraPorts = new List<GraphPort>
                    {
                        new("target", GraphPortDirection.Input, GraphPortKind.Data),
                    };
                        extraPorts.AddRange(arguments.Select(
                            (_, index) => new GraphPort($"arg{index}", GraphPortDirection.Input, GraphPortKind.Data)));
                        if (isEffectful)
                        {
                            extraPorts.Add(new GraphPort("execIn", GraphPortDirection.Input, GraphPortKind.Control));
                            extraPorts.Add(new GraphPort("execOut", GraphPortDirection.Output, GraphPortKind.Control));
                        }

                        AddNode(ValueNode(
                            invocation,
                            "invocation",
                            semantics.GetMethodLabel(invocation.Id) ?? "Invoke",
                            extraPorts: extraPorts,
                            includeValuePort: hasValue),
                            regionId);
                        AddRequiredDataEdge(target.Value, new ValueEndpoint(invocation.Id, "target"));
                        for (int index = 0; index < arguments.Length; index++)
                        {
                            AddRequiredDataEdge(arguments[index].Value, new ValueEndpoint(invocation.Id, $"arg{index}"));
                        }

                        effects = [
                            .. target.Effects,
                        .. arguments.SelectMany(static argument => argument.Effects),
                        .. (isEffectful ? new[] { invocation.Id } : Array.Empty<SemanticId>()),
                    ];
                        value = hasValue ? value : null;
                        break;
                    }
                case HirBinaryExpression binary:
                    {
                        ExpressionProjection left = ProjectExpression(
                            binary.Left,
                            regionId,
                            conditionalReceiver);
                        ExpressionProjection right = ProjectExpression(
                            binary.Right,
                            regionId,
                            conditionalReceiver);
                        IReadOnlyList<KeyValuePair<string, string>> evaluationProperties =
                            binary.Operator switch
                            {
                                "&&" =>
                                [
                                    KeyValuePair.Create("evaluation", "shortCircuitRight"),
                                    KeyValuePair.Create("rightEvaluation", "whenLeftTrue"),
                                ],
                                "||" =>
                                [
                                    KeyValuePair.Create("evaluation", "shortCircuitRight"),
                                    KeyValuePair.Create("rightEvaluation", "whenLeftFalse"),
                                ],
                                "??" =>
                                [
                                    KeyValuePair.Create("evaluation", "shortCircuitRight"),
                                    KeyValuePair.Create("rightEvaluation", "whenLeftNull"),
                                    KeyValuePair.Create(
                                        "resultSelection",
                                        "leftNonNullOrRight"),
                                ],
                                _ => [],
                            };
                        if (evaluationProperties.Count > 0 && right.Effects.Count > 0)
                        {
                            throw new InvalidOperationException(
                                "Lazy-right Graph operands must be effect-free.");
                        }

                        AddNode(ValueNode(
                            binary,
                            "binary",
                            binary.Operator,
                            KeyValuePair.Create("operator", binary.Operator),
                            [
                                new GraphPort("left", GraphPortDirection.Input, GraphPortKind.Data),
                            new GraphPort("right", GraphPortDirection.Input, GraphPortKind.Data),
                            ],
                            additionalProperties: evaluationProperties),
                            regionId);
                        AddRequiredDataEdge(left.Value, new ValueEndpoint(binary.Id, "left"));
                        AddRequiredDataEdge(right.Value, new ValueEndpoint(binary.Id, "right"));
                        effects = evaluationProperties.Count > 0
                            ? left.Effects
                            : [.. left.Effects, .. right.Effects];
                        break;
                    }
                case HirUnaryExpression unary:
                    {
                        ExpressionProjection operand = ProjectExpression(
                            unary.Operand,
                            regionId,
                            conditionalReceiver);
                        bool isMutation = unary.Operator is "++" or "--";
                        var extraPorts = new List<GraphPort>
                        {
                            new("operand", GraphPortDirection.Input, GraphPortKind.Data),
                        };
                        if (isMutation)
                        {
                            extraPorts.Add(new GraphPort(
                                "execIn",
                                GraphPortDirection.Input,
                                GraphPortKind.Control));
                            extraPorts.Add(new GraphPort(
                                "execOut",
                                GraphPortDirection.Output,
                                GraphPortKind.Control));
                        }

                        AddNode(ValueNode(
                            unary,
                            "unary",
                            unary.Operator,
                            KeyValuePair.Create("operator", unary.Operator),
                            extraPorts,
                            additionalProperties:
                            [
                                KeyValuePair.Create(
                                    "position",
                                    unary.IsPostfix ? "postfix" : "prefix"),
                            ]),
                            regionId);
                        AddRequiredDataEdge(operand.Value, new ValueEndpoint(unary.Id, "operand"));
                        effects = isMutation
                            ? [.. operand.Effects, unary.Id]
                            : operand.Effects;
                        break;
                    }
                case HirAssignmentExpression assignment:
                    {
                        ExpressionProjection target = ProjectExpression(
                            assignment.Target,
                            regionId,
                            conditionalReceiver);
                        ExpressionProjection assignedValue = ProjectExpression(
                            assignment.Value,
                            regionId,
                            conditionalReceiver);
                        AddNode(ValueNode(
                            assignment,
                            "assignment",
                            assignment.Operator,
                            KeyValuePair.Create("operator", assignment.Operator),
                            [
                                new GraphPort("target", GraphPortDirection.Input, GraphPortKind.Data),
                            new GraphPort("assignedValue", GraphPortDirection.Input, GraphPortKind.Data),
                            new GraphPort("execIn", GraphPortDirection.Input, GraphPortKind.Control),
                            new GraphPort("execOut", GraphPortDirection.Output, GraphPortKind.Control),
                            ]),
                            regionId);
                        AddRequiredDataEdge(target.Value, new ValueEndpoint(assignment.Id, "target"));
                        AddRequiredDataEdge(assignedValue.Value, new ValueEndpoint(assignment.Id, "assignedValue"));
                        effects = [.. target.Effects, .. assignedValue.Effects, assignment.Id];
                        break;
                    }
                case HirAwaitExpression awaitExpression:
                    {
                        ExpressionProjection operand = ProjectExpression(
                            awaitExpression.Operand,
                            regionId,
                            conditionalReceiver);
                        AddNode(ValueNode(
                            awaitExpression,
                            "await",
                            "Await",
                            KeyValuePair.Create("effect", "latent"),
                            [
                                new GraphPort("operand", GraphPortDirection.Input, GraphPortKind.Data),
                            new GraphPort("execIn", GraphPortDirection.Input, GraphPortKind.Control),
                            new GraphPort("execOut", GraphPortDirection.Output, GraphPortKind.Control),
                            ]),
                            regionId);
                        AddRequiredDataEdge(operand.Value, new ValueEndpoint(awaitExpression.Id, "operand"));
                        effects = [.. operand.Effects, awaitExpression.Id];
                        break;
                    }
                case HirCastExpression cast:
                    {
                        ExpressionProjection operand = ProjectExpression(
                            cast.Operand,
                            regionId,
                            conditionalReceiver);
                        AddNode(ValueNode(
                            cast,
                            "cast",
                            $"Cast {cast.TypeName}",
                            KeyValuePair.Create("type", cast.TypeName),
                            [new GraphPort("operand", GraphPortDirection.Input, GraphPortKind.Data)]),
                            regionId);
                        AddRequiredDataEdge(operand.Value, new ValueEndpoint(cast.Id, "operand"));
                        effects = operand.Effects;
                        break;
                    }
                case HirConditionalExpression conditional:
                    {
                        ExpressionProjection condition = ProjectExpression(
                            conditional.Condition,
                            regionId,
                            conditionalReceiver);
                        ExpressionProjection whenTrue = ProjectExpression(
                            conditional.WhenTrue,
                            regionId,
                            conditionalReceiver);
                        ExpressionProjection whenFalse = ProjectExpression(
                            conditional.WhenFalse,
                            regionId,
                            conditionalReceiver);
                        if (whenTrue.Effects.Count > 0 || whenFalse.Effects.Count > 0)
                        {
                            throw new InvalidOperationException(
                                "Conditional Graph branches must be effect-free to preserve lazy evaluation.");
                        }

                        AddNode(ValueNode(
                            conditional,
                            "conditional",
                            "Select",
                            KeyValuePair.Create("evaluation", "lazyBranches"),
                            [
                                new GraphPort(
                                    "condition",
                                    GraphPortDirection.Input,
                                    GraphPortKind.Data),
                                new GraphPort(
                                    "whenTrue",
                                    GraphPortDirection.Input,
                                    GraphPortKind.Data),
                                new GraphPort(
                                    "whenFalse",
                                    GraphPortDirection.Input,
                                    GraphPortKind.Data),
                            ]),
                            regionId);
                        AddRequiredDataEdge(
                            condition.Value,
                            new ValueEndpoint(conditional.Id, "condition"));
                        AddRequiredDataEdge(
                            whenTrue.Value,
                            new ValueEndpoint(conditional.Id, "whenTrue"));
                        AddRequiredDataEdge(
                            whenFalse.Value,
                            new ValueEndpoint(conditional.Id, "whenFalse"));
                        effects = condition.Effects;
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(expression), expression.GetType().Name);
            }

            return new ExpressionProjection(value, effects);
        }

        private GraphNode ValueNode(
            HirExpression expression,
            string kind,
            string label,
            KeyValuePair<string, string>? property = null,
            IReadOnlyList<GraphPort>? extraPorts = null,
            bool includeValuePort = true,
            IReadOnlyList<KeyValuePair<string, string>>? additionalProperties = null)
        {
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (property is not null)
            {
                properties.Add(property.Value.Key, property.Value.Value);
            }

            if (additionalProperties is not null)
            {
                foreach (KeyValuePair<string, string> additionalProperty in additionalProperties)
                {
                    properties.Add(additionalProperty.Key, additionalProperty.Value);
                }
            }

            semantics.ApplyNodeProperties(expression.Id, properties);
            var ports = new List<GraphPort>();
            if (extraPorts is not null)
            {
                ports.AddRange(extraPorts);
            }

            if (includeValuePort)
            {
                ports.Add(new GraphPort("value", GraphPortDirection.Output, GraphPortKind.Data));
            }

            return new GraphNode(expression.Id, kind, label, expression.Span, ports, properties);
        }

        private IReadOnlyList<ControlEndpoint> ConnectEffectChain(
            IReadOnlyList<ControlEndpoint> incoming,
            IEnumerable<SemanticId> effectNodes)
        {
            IReadOnlyList<ControlEndpoint> current = incoming;
            var connected = new HashSet<SemanticId>();
            foreach (SemanticId effectNode in effectNodes)
            {
                if (!connected.Add(effectNode))
                {
                    continue;
                }

                ConnectIncoming(current, effectNode, "execIn");
                current = [new ControlEndpoint(effectNode, "execOut")];
            }

            return current;
        }

        private void AddRequiredDataEdge(ValueEndpoint? source, ValueEndpoint target)
        {
            if (source is null)
            {
                throw new InvalidOperationException(
                    $"A void expression cannot be connected to data port {target.NodeId}.{target.Port}.");
            }

            AddEdge(GraphEdgeKind.Data, source, target);
        }

        private void ConnectIncoming(
            IEnumerable<ControlEndpoint> incoming,
            SemanticId targetNode,
            string targetPort)
        {
            foreach (ControlEndpoint source in incoming)
            {
                AddEdge(GraphEdgeKind.Control, source.NodeId, source.Port, targetNode, targetPort);
            }
        }

        private void AddEdge(GraphEdgeKind kind, ValueEndpoint source, ValueEndpoint target) =>
            AddEdge(kind, source.NodeId, source.Port, target.NodeId, target.Port);

        private void AddEdge(
            GraphEdgeKind kind,
            SemanticId sourceNode,
            string sourcePort,
            SemanticId targetNode,
            string targetPort)
        {
            SemanticId edgeId = SemanticId.FromKey(
                $"edge|{kind}|{sourceNode}|{sourcePort}|{targetNode}|{targetPort}");
            edges.TryAdd(edgeId, new GraphEdge(edgeId, kind, sourceNode, sourcePort, targetNode, targetPort));
        }

        private LoopContext? FindCleanupContext() => loops.FirstOrDefault(
            static context => context.ReturnCleanupNodeId is not null);

        private sealed class LoopContext(
            ControlTarget continueTarget,
            SemanticId? returnCleanupNodeId = null,
            SemanticId? exceptionCleanupNodeId = null)
        {
            public ControlTarget ContinueTarget { get; } = continueTarget;

            public List<ControlEndpoint> BreakExits { get; } = [];

            public SemanticId? ReturnCleanupNodeId { get; } = returnCleanupNodeId;

            public SemanticId? ExceptionCleanupNodeId { get; } = exceptionCleanupNodeId;

            public List<ControlEndpoint> ReturnExits { get; } = [];
        }
    }

    private sealed class SemanticIndex
    {
        private readonly IReadOnlyDictionary<SemanticId, BoundNodeInfo> nodes;
        private readonly IReadOnlyDictionary<SemanticId, TypeDescriptor> types;
        private readonly IReadOnlyDictionary<SemanticId, MethodDescriptor> methods;
        private readonly IReadOnlyDictionary<SemanticId, BoundForEachInfo> forEachLoops;
        private readonly IReadOnlyDictionary<SemanticId, SemanticId> declarationReferences;

        public SemanticIndex(
            BoundCompilation? compilation,
            IReadOnlyDictionary<SemanticId, SemanticId>? declarationReferences)
        {
            nodes = compilation?.Nodes.ToDictionary(static node => node.NodeId) ??
                new Dictionary<SemanticId, BoundNodeInfo>();
            types = compilation?.Types.ToDictionary(static type => type.Id) ??
                new Dictionary<SemanticId, TypeDescriptor>();
            methods = compilation?.Methods.ToDictionary(static method => method.Id) ??
                new Dictionary<SemanticId, MethodDescriptor>();
            forEachLoops = compilation?.ForEachLoops.ToDictionary(static loop => loop.NodeId) ??
                new Dictionary<SemanticId, BoundForEachInfo>();
            this.declarationReferences = declarationReferences ??
                new Dictionary<SemanticId, SemanticId>();
        }

        public bool IsEffectful(SemanticId nodeId) =>
            nodes.TryGetValue(nodeId, out BoundNodeInfo? node) && node.Effects != MethodEffects.None;

        public BoundForEachInfo GetForEach(SemanticId nodeId) =>
            forEachLoops.TryGetValue(nodeId, out BoundForEachInfo? binding)
                ? binding
                : throw new InvalidOperationException(
                    $"Foreach node '{nodeId}' has no bound iteration protocol.");

        public bool IsVoid(SemanticId nodeId) =>
            nodes.TryGetValue(nodeId, out BoundNodeInfo? node) &&
            node.TypeId is SemanticId typeId &&
            types.TryGetValue(typeId, out TypeDescriptor? type) &&
            type.Kind == UnifiedTypeKind.Void;

        public string? GetMethodLabel(SemanticId nodeId)
        {
            if (!nodes.TryGetValue(nodeId, out BoundNodeInfo? node) ||
                node.InvokedMethodId is not SemanticId methodId ||
                !methods.TryGetValue(methodId, out MethodDescriptor? method))
            {
                return null;
            }

            return method.DisplayName;
        }

        public void ApplyNodeProperties(SemanticId nodeId, IDictionary<string, string> properties)
        {
            if (!nodes.TryGetValue(nodeId, out BoundNodeInfo? node))
            {
                return;
            }

            if (node.TypeId is SemanticId typeId && types.TryGetValue(typeId, out TypeDescriptor? type))
            {
                properties["typeId"] = typeId.Value;
                properties["resolvedType"] = type.DisplayName;
                properties["graphSafe"] = type.IsGraphSafe ? "true" : "false";
            }

            if (node.DeclaredSymbolId is SemanticId declaredSymbolId)
            {
                properties["declaredSymbolId"] = declaredSymbolId.Value;
            }

            if (node.ReferencedSymbolId is SemanticId referencedSymbolId)
            {
                properties["referencedSymbolId"] = referencedSymbolId.Value;
                if (declarationReferences.TryGetValue(
                        referencedSymbolId,
                        out SemanticId declarationId))
                {
                    properties["declarationId"] = declarationId.Value;
                }
            }

            if (node.InvokedMethodId is SemanticId invokedMethodId)
            {
                properties["methodId"] = invokedMethodId.Value;
                if (methods.TryGetValue(invokedMethodId, out MethodDescriptor? method))
                {
                    properties["method"] = method.MetadataName;
                }
            }

            if (node.Effects != MethodEffects.None)
            {
                properties["effects"] = FormatEffects(node.Effects);
            }
        }

        private static string FormatEffects(MethodEffects effects) => string.Join(
            '|',
            effects.ToString()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(static name => JsonNamingPolicy.CamelCase.ConvertName(name)));
    }
}
