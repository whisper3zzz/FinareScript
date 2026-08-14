using UnifiedScript.Core;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.Graph;

public sealed class HirGraphProjector
{
    public GraphDocument Project(HirCompilationUnit compilationUnit)
    {
        ArgumentNullException.ThrowIfNull(compilationUnit);
        IReadOnlyList<GraphFunction> functions = compilationUnit.Types
            .SelectMany(type => type.Methods.Select(method => ProjectMethod(type, method)))
            .OrderBy(static function => function.QualifiedName, StringComparer.Ordinal)
            .ToArray();
        return new GraphDocument("unifiedscript.graph/v0.1", compilationUnit.Id, functions);
    }

    private static GraphFunction ProjectMethod(HirTypeDeclaration type, HirMethodDeclaration method)
    {
        var builder = new FunctionBuilder();
        SemanticId entryId = SemanticId.FromKey($"{method.Id}:graph:entry");
        builder.AddNode(new GraphNode(
            entryId,
            "entry",
            method.Name,
            method.Span,
            [new GraphPort("then", GraphPortDirection.Output, GraphPortKind.Control)],
            EmptyProperties()));

        builder.ProjectBlock(method.Body, [new ControlEndpoint(entryId, "then")]);
        return new GraphFunction(
            method.Id,
            $"{type.QualifiedName}.{method.Name}",
            builder.Nodes.OrderBy(static node => node.Id.Value, StringComparer.Ordinal).ToArray(),
            builder.Edges.OrderBy(static edge => edge.Id.Value, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyDictionary<string, string> EmptyProperties() =>
        new SortedDictionary<string, string>(StringComparer.Ordinal);

    private sealed record ControlEndpoint(SemanticId NodeId, string Port);

    private sealed record ValueEndpoint(SemanticId NodeId, string Port);

    private sealed class FunctionBuilder
    {
        private readonly Dictionary<SemanticId, GraphNode> nodes = [];
        private readonly Dictionary<SemanticId, GraphEdge> edges = [];

        public IEnumerable<GraphNode> Nodes => nodes.Values;

        public IEnumerable<GraphEdge> Edges => edges.Values;

        public IReadOnlyList<ControlEndpoint> ProjectBlock(
            HirBlockStatement block,
            IReadOnlyList<ControlEndpoint> incoming)
        {
            IReadOnlyList<ControlEndpoint> current = incoming;
            foreach (HirStatement statement in block.Statements)
            {
                current = ProjectStatement(statement, current);
            }

            return current;
        }

        public void AddNode(GraphNode node)
        {
            if (!nodes.TryAdd(node.Id, node))
            {
                throw new InvalidOperationException($"Duplicate graph node ID {node.Id}.");
            }
        }

        private IReadOnlyList<ControlEndpoint> ProjectStatement(
            HirStatement statement,
            IReadOnlyList<ControlEndpoint> incoming) => statement switch
            {
                HirBlockStatement block => ProjectBlock(block, incoming),
                HirVariableDeclarationStatement variable => ProjectVariable(variable, incoming),
                HirExpressionStatement expression => ProjectExpressionStatement(expression, incoming),
                HirIfStatement ifStatement => ProjectIf(ifStatement, incoming),
                HirReturnStatement returnStatement => ProjectReturn(returnStatement, incoming),
                _ => throw new ArgumentOutOfRangeException(nameof(statement), statement.GetType().Name),
            };

        private IReadOnlyList<ControlEndpoint> ProjectVariable(
            HirVariableDeclarationStatement variable,
            IReadOnlyList<ControlEndpoint> incoming)
        {
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = variable.Name,
                ["type"] = variable.TypeName,
            };
            var ports = new List<GraphPort>
            {
                new("in", GraphPortDirection.Input, GraphPortKind.Control),
                new("then", GraphPortDirection.Output, GraphPortKind.Control),
            };
            if (variable.Initializer is not null)
            {
                ports.Add(new GraphPort("value", GraphPortDirection.Input, GraphPortKind.Data));
            }

            AddNode(new GraphNode(variable.Id, "variable", $"Declare {variable.Name}", variable.Span, ports, properties));
            ConnectIncoming(incoming, variable.Id, "in");
            if (variable.Initializer is not null)
            {
                ValueEndpoint value = ProjectExpression(variable.Initializer);
                AddEdge(GraphEdgeKind.Data, value, new ValueEndpoint(variable.Id, "value"));
            }

            return [new ControlEndpoint(variable.Id, "then")];
        }

        private IReadOnlyList<ControlEndpoint> ProjectExpressionStatement(
            HirExpressionStatement statement,
            IReadOnlyList<ControlEndpoint> incoming)
        {
            ValueEndpoint expression = ProjectExpression(statement.Expression);
            SemanticId evaluateId = SemanticId.FromKey($"{statement.Id}:graph:evaluate");
            AddNode(new GraphNode(
                evaluateId,
                "evaluate",
                "Evaluate",
                statement.Span,
                [
                    new GraphPort("in", GraphPortDirection.Input, GraphPortKind.Control),
                    new GraphPort("then", GraphPortDirection.Output, GraphPortKind.Control),
                    new GraphPort("expression", GraphPortDirection.Input, GraphPortKind.Data),
                ],
                EmptyProperties()));
            ConnectIncoming(incoming, evaluateId, "in");
            AddEdge(GraphEdgeKind.Data, expression, new ValueEndpoint(evaluateId, "expression"));
            return [new ControlEndpoint(evaluateId, "then")];
        }

        private IReadOnlyList<ControlEndpoint> ProjectIf(
            HirIfStatement statement,
            IReadOnlyList<ControlEndpoint> incoming)
        {
            ValueEndpoint condition = ProjectExpression(statement.Condition);
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
                EmptyProperties()));
            ConnectIncoming(incoming, statement.Id, "in");
            AddEdge(GraphEdgeKind.Data, condition, new ValueEndpoint(statement.Id, "condition"));

            IReadOnlyList<ControlEndpoint> thenExits = ProjectBlock(
                statement.Then,
                [new ControlEndpoint(statement.Id, "then")]);
            IReadOnlyList<ControlEndpoint> elseExits = statement.Else is null
                ? [new ControlEndpoint(statement.Id, "else")]
                : ProjectBlock(statement.Else, [new ControlEndpoint(statement.Id, "else")]);
            return thenExits.Concat(elseExits).ToArray();
        }

        private IReadOnlyList<ControlEndpoint> ProjectReturn(
            HirReturnStatement statement,
            IReadOnlyList<ControlEndpoint> incoming)
        {
            var ports = new List<GraphPort>
            {
                new("in", GraphPortDirection.Input, GraphPortKind.Control),
            };
            if (statement.Expression is not null)
            {
                ports.Add(new GraphPort("value", GraphPortDirection.Input, GraphPortKind.Data));
            }

            AddNode(new GraphNode(statement.Id, "return", "Return", statement.Span, ports, EmptyProperties()));
            ConnectIncoming(incoming, statement.Id, "in");
            if (statement.Expression is not null)
            {
                ValueEndpoint value = ProjectExpression(statement.Expression);
                AddEdge(GraphEdgeKind.Data, value, new ValueEndpoint(statement.Id, "value"));
            }

            return [];
        }

        private ValueEndpoint ProjectExpression(HirExpression expression)
        {
            switch (expression)
            {
                case HirLiteralExpression literal:
                    AddNode(ValueNode(
                        literal,
                        "literal",
                        literal.ValueText,
                        KeyValuePair.Create("value", literal.ValueText)));
                    break;
                case HirIdentifierExpression identifier:
                    AddNode(ValueNode(
                        identifier,
                        "identifier",
                        identifier.Name,
                        KeyValuePair.Create("name", identifier.Name)));
                    break;
                case HirThisExpression @this:
                    AddNode(ValueNode(@this, "this", "this"));
                    break;
                case HirMemberAccessExpression member:
                    {
                        ValueEndpoint target = ProjectExpression(member.Target);
                        AddNode(ValueNode(
                            member,
                            "memberAccess",
                            member.MemberName,
                            KeyValuePair.Create("member", member.MemberName),
                            [new GraphPort("target", GraphPortDirection.Input, GraphPortKind.Data)]));
                        AddEdge(GraphEdgeKind.Data, target, new ValueEndpoint(member.Id, "target"));
                        break;
                    }
                case HirInvocationExpression invocation:
                    {
                        ValueEndpoint target = ProjectExpression(invocation.Target);
                        var extraPorts = new List<GraphPort>
                    {
                        new("target", GraphPortDirection.Input, GraphPortKind.Data),
                    };
                        extraPorts.AddRange(invocation.Arguments.Select(
                            (_, index) => new GraphPort($"arg{index}", GraphPortDirection.Input, GraphPortKind.Data)));
                        AddNode(ValueNode(invocation, "invocation", "Invoke", extraPorts: extraPorts));
                        AddEdge(GraphEdgeKind.Data, target, new ValueEndpoint(invocation.Id, "target"));
                        for (int index = 0; index < invocation.Arguments.Count; index++)
                        {
                            ValueEndpoint argument = ProjectExpression(invocation.Arguments[index]);
                            AddEdge(GraphEdgeKind.Data, argument, new ValueEndpoint(invocation.Id, $"arg{index}"));
                        }

                        break;
                    }
                case HirBinaryExpression binary:
                    {
                        ValueEndpoint left = ProjectExpression(binary.Left);
                        ValueEndpoint right = ProjectExpression(binary.Right);
                        AddNode(ValueNode(
                            binary,
                            "binary",
                            binary.Operator,
                            KeyValuePair.Create("operator", binary.Operator),
                            [
                                new GraphPort("left", GraphPortDirection.Input, GraphPortKind.Data),
                            new GraphPort("right", GraphPortDirection.Input, GraphPortKind.Data),
                            ]));
                        AddEdge(GraphEdgeKind.Data, left, new ValueEndpoint(binary.Id, "left"));
                        AddEdge(GraphEdgeKind.Data, right, new ValueEndpoint(binary.Id, "right"));
                        break;
                    }
                case HirUnaryExpression unary:
                    {
                        ValueEndpoint operand = ProjectExpression(unary.Operand);
                        AddNode(ValueNode(
                            unary,
                            "unary",
                            unary.Operator,
                            KeyValuePair.Create("operator", unary.Operator),
                            [new GraphPort("operand", GraphPortDirection.Input, GraphPortKind.Data)]));
                        AddEdge(GraphEdgeKind.Data, operand, new ValueEndpoint(unary.Id, "operand"));
                        break;
                    }
                case HirAssignmentExpression assignment:
                    {
                        ValueEndpoint target = ProjectExpression(assignment.Target);
                        ValueEndpoint value = ProjectExpression(assignment.Value);
                        AddNode(ValueNode(
                            assignment,
                            "assignment",
                            assignment.Operator,
                            KeyValuePair.Create("operator", assignment.Operator),
                            [
                                new GraphPort("target", GraphPortDirection.Input, GraphPortKind.Data),
                            new GraphPort("value", GraphPortDirection.Input, GraphPortKind.Data),
                            ]));
                        AddEdge(GraphEdgeKind.Data, target, new ValueEndpoint(assignment.Id, "target"));
                        AddEdge(GraphEdgeKind.Data, value, new ValueEndpoint(assignment.Id, "value"));
                        break;
                    }
                case HirAwaitExpression awaitExpression:
                    {
                        ValueEndpoint operand = ProjectExpression(awaitExpression.Operand);
                        AddNode(ValueNode(
                            awaitExpression,
                            "await",
                            "Await",
                            KeyValuePair.Create("effect", "latent"),
                            [new GraphPort("operand", GraphPortDirection.Input, GraphPortKind.Data)]));
                        AddEdge(GraphEdgeKind.Data, operand, new ValueEndpoint(awaitExpression.Id, "operand"));
                        break;
                    }
                case HirCastExpression cast:
                    {
                        ValueEndpoint operand = ProjectExpression(cast.Operand);
                        AddNode(ValueNode(
                            cast,
                            "cast",
                            $"Cast {cast.TypeName}",
                            KeyValuePair.Create("type", cast.TypeName),
                            [new GraphPort("operand", GraphPortDirection.Input, GraphPortKind.Data)]));
                        AddEdge(GraphEdgeKind.Data, operand, new ValueEndpoint(cast.Id, "operand"));
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(expression), expression.GetType().Name);
            }

            return new ValueEndpoint(expression.Id, "value");
        }

        private static GraphNode ValueNode(
            HirExpression expression,
            string kind,
            string label,
            KeyValuePair<string, string>? property = null,
            IReadOnlyList<GraphPort>? extraPorts = null)
        {
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (property is not null)
            {
                properties.Add(property.Value.Key, property.Value.Value);
            }

            var ports = new List<GraphPort>();
            if (extraPorts is not null)
            {
                ports.AddRange(extraPorts);
            }

            ports.Add(new GraphPort("value", GraphPortDirection.Output, GraphPortKind.Data));
            return new GraphNode(expression.Id, kind, label, expression.Span, ports, properties);
        }

        private void ConnectIncoming(
            IEnumerable<ControlEndpoint> incoming,
            SemanticId targetNode,
            string targetPort)
        {
            foreach (ControlEndpoint source in incoming)
            {
                AddEdge(
                    GraphEdgeKind.Control,
                    source.NodeId,
                    source.Port,
                    targetNode,
                    targetPort);
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
}
