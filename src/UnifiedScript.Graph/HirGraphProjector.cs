using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Semantics;

namespace UnifiedScript.Graph;

public sealed class HirGraphProjector
{
    public GraphDocument Project(HirCompilationUnit compilationUnit, BoundCompilation? semantics = null)
    {
        ArgumentNullException.ThrowIfNull(compilationUnit);
        if (semantics is not null && semantics.SourceCompilationId != compilationUnit.Id)
        {
            throw new ArgumentException(
                "The bound semantics were produced for a different HIR compilation.",
                nameof(semantics));
        }

        var semanticIndex = new SemanticIndex(semantics);
        IReadOnlyList<GraphFunction> functions = compilationUnit.Types
            .SelectMany(type => type.Methods.Select(method => ProjectMethod(type, method, semanticIndex)))
            .OrderBy(static function => function.QualifiedName, StringComparer.Ordinal)
            .ToArray();
        return new GraphDocument(
            GraphDocument.CurrentSchemaVersion,
            compilationUnit.Id,
            semantics?.ConfigurationId,
            functions);
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
        builder.AddNode(new GraphNode(
            entryId,
            "entry",
            method.Name,
            method.Span,
            [new GraphPort("then", GraphPortDirection.Output, GraphPortKind.Control)],
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

            AddNode(
                new GraphNode(
                    evaluateId,
                    "evaluate",
                    "Evaluate",
                    statement.Span,
                    ports,
                    EmptyProperties()),
                regionId);
            ConnectIncoming(incoming, evaluateId, "in");
            if (expression.Value is not null)
            {
                AddEdge(GraphEdgeKind.Data, expression.Value, new ValueEndpoint(evaluateId, "expression"));
            }

            return [new ControlEndpoint(evaluateId, "then")];
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

        private IReadOnlyList<ControlEndpoint> ProjectReturn(
            HirReturnStatement statement,
            IReadOnlyList<ControlEndpoint> incoming,
            SemanticId regionId)
        {
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

            return [];
        }

        private ExpressionProjection ProjectExpression(HirExpression expression, SemanticId regionId)
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
                        ExpressionProjection target = ProjectExpression(member.Target, regionId);
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
                case HirInvocationExpression invocation:
                    {
                        ExpressionProjection target = ProjectExpression(invocation.Target, regionId);
                        ExpressionProjection[] arguments = invocation.Arguments
                            .Select(argument => ProjectExpression(argument, regionId))
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
                        ExpressionProjection left = ProjectExpression(binary.Left, regionId);
                        ExpressionProjection right = ProjectExpression(binary.Right, regionId);
                        AddNode(ValueNode(
                            binary,
                            "binary",
                            binary.Operator,
                            KeyValuePair.Create("operator", binary.Operator),
                            [
                                new GraphPort("left", GraphPortDirection.Input, GraphPortKind.Data),
                            new GraphPort("right", GraphPortDirection.Input, GraphPortKind.Data),
                            ]),
                            regionId);
                        AddRequiredDataEdge(left.Value, new ValueEndpoint(binary.Id, "left"));
                        AddRequiredDataEdge(right.Value, new ValueEndpoint(binary.Id, "right"));
                        effects = [.. left.Effects, .. right.Effects];
                        break;
                    }
                case HirUnaryExpression unary:
                    {
                        ExpressionProjection operand = ProjectExpression(unary.Operand, regionId);
                        AddNode(ValueNode(
                            unary,
                            "unary",
                            unary.Operator,
                            KeyValuePair.Create("operator", unary.Operator),
                            [new GraphPort("operand", GraphPortDirection.Input, GraphPortKind.Data)]),
                            regionId);
                        AddRequiredDataEdge(operand.Value, new ValueEndpoint(unary.Id, "operand"));
                        effects = operand.Effects;
                        break;
                    }
                case HirAssignmentExpression assignment:
                    {
                        ExpressionProjection target = ProjectExpression(assignment.Target, regionId);
                        ExpressionProjection assignedValue = ProjectExpression(assignment.Value, regionId);
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
                        ExpressionProjection operand = ProjectExpression(awaitExpression.Operand, regionId);
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
                        ExpressionProjection operand = ProjectExpression(cast.Operand, regionId);
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
            bool includeValuePort = true)
        {
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (property is not null)
            {
                properties.Add(property.Value.Key, property.Value.Value);
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
    }

    private sealed class SemanticIndex
    {
        private readonly IReadOnlyDictionary<SemanticId, BoundNodeInfo> nodes;
        private readonly IReadOnlyDictionary<SemanticId, TypeDescriptor> types;
        private readonly IReadOnlyDictionary<SemanticId, MethodDescriptor> methods;

        public SemanticIndex(BoundCompilation? compilation)
        {
            nodes = compilation?.Nodes.ToDictionary(static node => node.NodeId) ??
                new Dictionary<SemanticId, BoundNodeInfo>();
            types = compilation?.Types.ToDictionary(static type => type.Id) ??
                new Dictionary<SemanticId, TypeDescriptor>();
            methods = compilation?.Methods.ToDictionary(static method => method.Id) ??
                new Dictionary<SemanticId, MethodDescriptor>();
        }

        public bool IsEffectful(SemanticId nodeId) =>
            nodes.TryGetValue(nodeId, out BoundNodeInfo? node) && node.Effects != MethodEffects.None;

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
