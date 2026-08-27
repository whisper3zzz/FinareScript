using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.Graph;

namespace UnifiedScript.Authoring;

public sealed class AuthoringSurfaceProjector
{
    public AuthoringSurfaceDocument Project(
        ScriptAssetDocument scriptAssets,
        GraphDocument graph)
    {
        ArgumentNullException.ThrowIfNull(scriptAssets);
        ArgumentNullException.ThrowIfNull(graph);
        scriptAssets = ScriptAssetJson.NormalizeAndValidate(scriptAssets);
        graph = GraphJson.NormalizeAndValidate(graph);

        if (scriptAssets.SourceCompilationId != graph.SourceCompilationId)
        {
            throw new InvalidOperationException(
                "Script declarations and Graph IR must come from the same compilation.");
        }

        if (graph.SemanticConfigurationId is not SemanticId configurationId ||
            configurationId != scriptAssets.SemanticConfigurationId)
        {
            throw new InvalidOperationException(
                "Script declarations and Graph IR must use the same semantic configuration.");
        }

        IReadOnlyDictionary<SemanticId, GraphFunction> graphFunctions = graph.Functions
            .ToDictionary(static function => function.Id);
        var assets = new List<AuthoringAssetSurface>();
        foreach (ScriptAssetDeclaration asset in scriptAssets.Assets)
        {
            var functions = new List<AuthoringFunctionSurface>();
            foreach (ScriptFunctionDeclaration declaration in asset.Functions)
            {
                if (!graphFunctions.TryGetValue(declaration.Id, out GraphFunction? function))
                {
                    throw new InvalidOperationException(
                        $"Script function '{declaration.QualifiedName}' has no Graph projection.");
                }

                functions.Add(ProjectFunction(declaration, function));
            }

            assets.Add(new AuthoringAssetSurface(
                asset.Id,
                asset.QualifiedName,
                asset.AssetPath,
                asset.Span,
                asset.States,
                functions));
        }

        return AuthoringSurfaceJson.NormalizeAndValidate(new AuthoringSurfaceDocument(
            AuthoringSurfaceDocument.CurrentSchemaVersion,
            scriptAssets.SourceCompilationId,
            scriptAssets.SemanticConfigurationId,
            assets));
    }

    private static AuthoringFunctionSurface ProjectFunction(
        ScriptFunctionDeclaration declaration,
        GraphFunction function)
    {
        var nodes = function.Nodes.ToDictionary(static node => node.Id);
        var incomingData = function.Edges
            .Where(static edge => edge.Kind == GraphEdgeKind.Data)
            .GroupBy(static edge => (edge.ToNode, edge.ToPort))
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static edge => edge.Id.Value, StringComparer.Ordinal).First());
        var nodeOwners = function.Regions
            .SelectMany(static region => region.NodeIds.Select(nodeId => (nodeId, region.Id)))
            .ToDictionary(static item => item.nodeId, static item => item.Id);
        GraphNode[] surfaceNodes = function.Nodes
            .OrderBy(static node => node.Span.Start.Line)
            .ThenBy(static node => node.Span.Start.Column)
            .ThenBy(static node => node.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var surfaceNodeIds = surfaceNodes.Select(static node => node.Id).ToHashSet();

        var actions = new List<AuthoringActionSurface>();
        foreach (GraphNode node in surfaceNodes)
        {
            if (!nodeOwners.TryGetValue(node.Id, out SemanticId regionId))
            {
                throw new InvalidOperationException(
                    $"Graph node '{node.Id}' has no direct region owner.");
            }

            var formatter = new ExpressionFormatter(nodes, incomingData);
            actions.Add(new AuthoringActionSurface(
                node.Id,
                node.Kind,
                Classify(node),
                ActionTitle(node, declaration, formatter),
                ActionSummary(node, declaration, formatter),
                node.Span,
                regionId,
                DebugTarget(node),
                CollectDeclarationReferences(node.Id, nodes, incomingData),
                CollectEditCapabilities(node.Id, nodes, incomingData),
                ProjectPorts(node, nodes, incomingData)));
        }

        AuthoringRegionSurface[] regions = function.Regions.Select(region =>
            new AuthoringRegionSurface(
                region.Id,
                region.Kind,
                region.ParentRegionId,
                region.SourceNodeId,
                region.Span,
                region.IsImplicit,
                surfaceNodeIds.Contains(region.SourceNodeId) &&
                    region.NodeIds.Contains(region.SourceNodeId)
                    ? region.SourceNodeId
                    : null,
                region.NodeIds
                    .Where(id => surfaceNodeIds.Contains(id) && IsStatementNode(nodes[id]))
                    .Concat(region.ChildRegionIds
                        .Select(id => function.Regions.Single(candidate => candidate.Id == id))
                        .Where(static child => child.Kind is
                            GraphRegionKind.If or GraphRegionKind.Loop or GraphRegionKind.Switch)
                        .Select(static child => child.SourceNodeId)
                        .Where(surfaceNodeIds.Contains))
                    .Distinct()
                    .OrderBy(id => nodes[id].Span.Start.Line)
                    .ThenBy(id => nodes[id].Span.Start.Column)
                    .ThenBy(static id => id.Value, StringComparer.Ordinal)
                    .ToArray(),
                region.NodeIds
                    .Where(surfaceNodeIds.Contains)
                    .OrderBy(id => nodes[id].Span.Start.Line)
                    .ThenBy(id => nodes[id].Span.Start.Column)
                    .ThenBy(static id => id.Value, StringComparer.Ordinal)
                    .ToArray(),
                region.ChildRegionIds))
            .ToArray();

        GraphNode? eventEntry = function.Nodes.FirstOrDefault(
            static node => node.Kind == "unrealEventEntry");
        AuthoringParameterSurface[] parameters = declaration.Parameters.Select((parameter, index) =>
        {
            GraphNode? reference = function.Nodes.FirstOrDefault(node =>
                node.Properties.TryGetValue("referencedSymbolId", out string? declarationId) &&
                declarationId == parameter.Id.Value &&
                node.Properties.ContainsKey("typeId"));
            SemanticId? typeId = eventEntry is not null &&
                eventEntry.Properties.TryGetValue(
                    $"eventParameter{index}TypeId",
                    out string? eventTypeId) &&
                SemanticId.TryParse(eventTypeId, out SemanticId parsedEventTypeId)
                    ? parsedEventTypeId
                    : reference is not null &&
                SemanticId.TryParse(reference.Properties["typeId"], out SemanticId parsedTypeId)
                    ? parsedTypeId
                    : null;
            string? typeDisplayName = eventEntry?.Properties.GetValueOrDefault(
                $"eventParameter{index}ResolvedType") ??
                reference?.Properties.GetValueOrDefault("resolvedType");
            return new AuthoringParameterSurface(
                parameter.Id,
                parameter.Name,
                parameter.TypeMetadataName,
                typeId,
                typeDisplayName);
        }).ToArray();

        return new AuthoringFunctionSurface(
            declaration.Id,
            declaration.Name,
            declaration.QualifiedName,
            declaration.ReturnTypeMetadataName,
            declaration.IsStatic,
            parameters,
            function.RootRegionId,
            regions,
            actions,
            function.Edges.Select(static edge => new AuthoringEdgeSurface(
                edge.Id,
                edge.Kind,
                edge.FromNode,
                edge.FromPort,
                edge.ToNode,
                edge.ToPort)).ToArray());
    }

    private static bool IsStatementNode(GraphNode node) => node.Ports.Any(static port =>
        port.Kind == GraphPortKind.Control);

    private static IReadOnlyList<AuthoringPortSurface> ProjectPorts(
        GraphNode node,
        IReadOnlyDictionary<SemanticId, GraphNode> nodes,
        IReadOnlyDictionary<(SemanticId NodeId, string Port), GraphEdge> incomingData)
    {
        return node.Ports.Select(port =>
        {
            if (port.Kind == GraphPortKind.Control)
            {
                return new AuthoringPortSurface(port.Name, port.Direction, port.Kind, null, null);
            }

            GraphNode typeSource = node;
            if (node.Kind == "unrealEventEntry" &&
                port.Name.StartsWith("eventArg", StringComparison.Ordinal) &&
                int.TryParse(port.Name.AsSpan("eventArg".Length), out int eventParameterIndex))
            {
                SemanticId? eventTypeId = node.Properties.TryGetValue(
                        $"eventParameter{eventParameterIndex}TypeId",
                        out string? eventTypeValue) &&
                    SemanticId.TryParse(eventTypeValue, out SemanticId parsedEventTypeId)
                        ? parsedEventTypeId
                        : null;
                return new AuthoringPortSurface(
                    port.Name,
                    port.Direction,
                    port.Kind,
                    eventTypeId,
                    node.Properties.GetValueOrDefault(
                        $"eventParameter{eventParameterIndex}ResolvedType"));
            }
            if (port.Direction == GraphPortDirection.Input &&
                incomingData.TryGetValue((node.Id, port.Name), out GraphEdge? incoming) &&
                nodes.TryGetValue(incoming.FromNode, out GraphNode? source))
            {
                typeSource = source;
            }

            SemanticId? typeId = TryIdProperty(typeSource, "typeId", out SemanticId parsedTypeId)
                ? parsedTypeId
                : null;
            string? typeDisplayName = typeSource.Properties.GetValueOrDefault("resolvedType");
            return new AuthoringPortSurface(
                port.Name,
                port.Direction,
                port.Kind,
                typeId,
                typeDisplayName);
        }).ToArray();
    }

    private static AuthoringActionCategory Classify(GraphNode node) => node.Kind switch
    {
        "entry" or "unrealEventEntry" => AuthoringActionCategory.Entry,
        "variable" or "assignment" => AuthoringActionCategory.State,
        "branch" or "loop" or "loopEntry" or "break" or "continue" =>
            AuthoringActionCategory.Flow,
        "invocation" or "await" or "enumeratorAcquire" or "enumeratorMoveNext" or
            "enumeratorCurrent" or "enumeratorDispose" => AuthoringActionCategory.Call,
        "return" => AuthoringActionCategory.Exit,
        _ => AuthoringActionCategory.Value,
    };

    private static string ActionTitle(
        GraphNode node,
        ScriptFunctionDeclaration declaration,
        ExpressionFormatter formatter) => node.Kind switch
    {
        "entry" => declaration.Name,
        "unrealEventEntry" => $"Event {declaration.Name}",
        "assignment" when formatter.TryInput(node.Id, "target", out string target) =>
            $"Set {target}",
        "identifier" => $"Get {Property(node, "name", node.Label)}",
        _ => node.Label,
    };

    private static string ActionSummary(
        GraphNode node,
        ScriptFunctionDeclaration declaration,
        ExpressionFormatter formatter) => node.Kind switch
    {
        "entry" => FormatSignature(declaration),
        "unrealEventEntry" => $"UE {Property(node, "unrealEventPath", "delegate")}\n" +
            FormatSignature(declaration),
        "variable" => FormatVariable(node, formatter),
        "assignment" => $"{formatter.Input(node.Id, "target")} " +
            $"{Property(node, "operator", node.Label)} {formatter.Input(node.Id, "assignedValue")}",
        "branch" => $"if ({formatter.Input(node.Id, "condition")})",
        "loop" => FormatLoop(node, formatter),
        "return" => formatter.TryInput(node.Id, "value", out string? value)
            ? $"return {value}"
            : "return",
        "break" => "break",
        "continue" => "continue",
        "evaluate" => formatter.Input(node.Id, "expression"),
        "invocation" => formatter.Format(node.Id),
        "await" => $"await {formatter.Input(node.Id, "operand")}",
        _ => formatter.FormatActionInputs(node),
    };

    private static string FormatSignature(ScriptFunctionDeclaration function)
    {
        string parameters = string.Join(
            ", ",
            function.Parameters.Select(static parameter =>
                $"{ShortType(parameter.TypeMetadataName)} {parameter.Name}"));
        return $"{ShortType(function.ReturnTypeMetadataName)} {function.Name}({parameters})";
    }

    private static string FormatVariable(GraphNode node, ExpressionFormatter formatter)
    {
        string declaration = $"{Property(node, "type", "var")} {Property(node, "name", node.Label)}";
        return formatter.TryInput(node.Id, "value", out string? value)
            ? $"{declaration} = {value}"
            : declaration;
    }

    private static string FormatLoop(GraphNode node, ExpressionFormatter formatter)
    {
        string form = Property(node, "loopForm", node.Label).ToLowerInvariant();
        if (form == "foreach" || form == "foreacharray" || form == "foreachenumerable")
        {
            string variable = Property(node, "variableName", "item");
            return $"foreach ({variable} in {formatter.Input(node.Id, "collection")})";
        }

        if (form == "for")
        {
            string condition = formatter.TryInput(node.Id, "condition", out string value)
                ? value
                : "true";
            return $"for (…; {condition}; …)";
        }

        string loopCondition = formatter.TryInput(node.Id, "condition", out string text)
            ? text
            : "true";
        return form == "dowhile"
            ? $"do … while ({loopCondition})"
            : $"while ({loopCondition})";
    }

    private static SemanticId? DebugTarget(GraphNode node)
    {
        if (node.Properties.TryGetValue("debugTargetId", out string? value) &&
            SemanticId.TryParse(value, out SemanticId debugTarget))
        {
            return debugTarget;
        }

        return node.Kind is "variable" or "branch" or "loop" or "break" or "continue" or "return"
            ? node.Id
            : null;
    }

    private static IReadOnlyList<SemanticId> CollectDeclarationReferences(
        SemanticId rootId,
        IReadOnlyDictionary<SemanticId, GraphNode> nodes,
        IReadOnlyDictionary<(SemanticId NodeId, string Port), GraphEdge> incomingData)
    {
        var references = new HashSet<SemanticId>();
        VisitDataClosure(rootId, nodes, incomingData, node =>
        {
            if (node.Properties.TryGetValue("declarationId", out string? value) &&
                SemanticId.TryParse(value, out SemanticId declarationId))
            {
                references.Add(declarationId);
            }
        });
        return references.OrderBy(static id => id.Value, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<AuthoringEditCapability> CollectEditCapabilities(
        SemanticId rootId,
        IReadOnlyDictionary<SemanticId, GraphNode> nodes,
        IReadOnlyDictionary<(SemanticId NodeId, string Port), GraphEdge> incomingData)
    {
        var edits = new Dictionary<SemanticId, AuthoringEditCapability>();
        VisitDataClosure(rootId, nodes, incomingData, node =>
        {
            AuthoringEditCapability? edit = node.Kind switch
            {
                "literal" when node.Properties.TryGetValue("value", out string? value) =>
                    LiteralEdit(rootId, node, value),
                "binary" or "unary" => OperatorEdit(rootId, node, nodes, incomingData),
                _ => null,
            };
            if (edit is not null)
            {
                edits.TryAdd(edit.TargetNodeId, edit);
            }
        });
        return edits.Values
            .OrderBy(static edit => edit.Label, StringComparer.Ordinal)
            .ThenBy(static edit => edit.TargetNodeId.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static AuthoringEditCapability LiteralEdit(
        SemanticId rootId,
        GraphNode node,
        string value) => new(
            SemanticId.FromKey($"{rootId}:authoring:edit:{node.Id}:literal"),
            AuthoringEditKind.Literal,
            $"Literal · line {node.Span.Start.Line}",
            "replaceLiteral",
            node.Id,
            value,
            value,
            null,
            null,
            null,
            null,
            []);

    private static AuthoringEditCapability? OperatorEdit(
        SemanticId rootId,
        GraphNode node,
        IReadOnlyDictionary<SemanticId, GraphNode> nodes,
        IReadOnlyDictionary<(SemanticId NodeId, string Port), GraphEdge> incomingData)
    {
        if (!node.Properties.TryGetValue("operator", out string? currentOperator) ||
            !TryIdProperty(node, "typeId", out SemanticId resultTypeId))
        {
            return null;
        }

        string leftPort = node.Kind == "binary" ? "left" : "operand";
        if (!TryInputNode(node.Id, leftPort, incomingData, nodes, out GraphNode operand) ||
            !TryIdProperty(operand, "typeId", out SemanticId operandTypeId))
        {
            return null;
        }

        SemanticId? rightTypeId = null;
        if (node.Kind == "binary")
        {
            if (!TryInputNode(node.Id, "right", incomingData, nodes, out GraphNode right) ||
                !TryIdProperty(right, "typeId", out SemanticId parsedRightTypeId))
            {
                return null;
            }

            rightTypeId = parsedRightTypeId;
        }

        return new AuthoringEditCapability(
            SemanticId.FromKey($"{rootId}:authoring:edit:{node.Id}:operator"),
            AuthoringEditKind.Operator,
            $"Operator · line {node.Span.Start.Line}",
            "changeOperator",
            node.Id,
            currentOperator,
            null,
            currentOperator,
            resultTypeId,
            operandTypeId,
            rightTypeId,
            CompatibleOperators(currentOperator));
    }

    private static IReadOnlyList<string> CompatibleOperators(string currentOperator) =>
        currentOperator switch
        {
            "+" or "-" or "*" or "/" or "%" => ["+", "-", "*", "/", "%"],
            "<" or "<=" or ">" or ">=" => ["<", "<=", ">", ">="],
            "==" or "!=" => ["==", "!="],
            "&&" or "||" => ["&&", "||"],
            "++" or "--" => ["++", "--"],
            "!" => ["!"],
            "~" => ["~"],
            "??" => ["??"],
            _ => [currentOperator],
        };

    private static void VisitDataClosure(
        SemanticId rootId,
        IReadOnlyDictionary<SemanticId, GraphNode> nodes,
        IReadOnlyDictionary<(SemanticId NodeId, string Port), GraphEdge> incomingData,
        Action<GraphNode> visitor)
    {
        var visited = new HashSet<SemanticId>();
        var pending = new Stack<SemanticId>();
        pending.Push(rootId);
        while (pending.TryPop(out SemanticId currentId))
        {
            if (!visited.Add(currentId) || !nodes.TryGetValue(currentId, out GraphNode? current))
            {
                continue;
            }

            visitor(current);
            foreach (GraphPort port in current.Ports.Where(static port =>
                port.Kind == GraphPortKind.Data && port.Direction == GraphPortDirection.Input))
            {
                if (incomingData.TryGetValue((current.Id, port.Name), out GraphEdge? edge))
                {
                    pending.Push(edge.FromNode);
                }
            }
        }
    }

    private static bool TryInputNode(
        SemanticId nodeId,
        string port,
        IReadOnlyDictionary<(SemanticId NodeId, string Port), GraphEdge> incomingData,
        IReadOnlyDictionary<SemanticId, GraphNode> nodes,
        out GraphNode input)
    {
        input = null!;
        if (!incomingData.TryGetValue((nodeId, port), out GraphEdge? edge))
        {
            return false;
        }

        return nodes.TryGetValue(edge.FromNode, out input!);
    }

    private static bool TryIdProperty(GraphNode node, string name, out SemanticId id)
    {
        id = default;
        return node.Properties.TryGetValue(name, out string? value) &&
            SemanticId.TryParse(value, out id);
    }

    private static string Property(GraphNode node, string name, string fallback) =>
        node.Properties.TryGetValue(name, out string? value) ? value : fallback;

    private static string ShortType(string metadataName)
    {
        string? keyword = metadataName switch
        {
            "System.Boolean" => "bool",
            "System.Byte" => "byte",
            "System.SByte" => "sbyte",
            "System.Int16" => "short",
            "System.UInt16" => "ushort",
            "System.Int32" => "int",
            "System.UInt32" => "uint",
            "System.Int64" => "long",
            "System.UInt64" => "ulong",
            "System.Single" => "float",
            "System.Double" => "double",
            "System.Decimal" => "decimal",
            "System.Char" => "char",
            "System.String" => "string",
            "System.Object" => "object",
            "System.Void" => "void",
            _ => null,
        };
        if (keyword is not null)
        {
            return keyword;
        }

        int separator = Math.Max(metadataName.LastIndexOf('.'), metadataName.LastIndexOf('+'));
        return separator >= 0 ? metadataName[(separator + 1)..] : metadataName;
    }

    private sealed class ExpressionFormatter(
        IReadOnlyDictionary<SemanticId, GraphNode> nodes,
        IReadOnlyDictionary<(SemanticId NodeId, string Port), GraphEdge> incomingData)
    {
        public string Input(SemanticId nodeId, string port) =>
            TryInput(nodeId, port, out string value) ? value : "…";

        public bool TryInput(SemanticId nodeId, string port, out string value)
        {
            if (incomingData.TryGetValue((nodeId, port), out GraphEdge? edge))
            {
                value = Format(edge.FromNode);
                return true;
            }

            value = string.Empty;
            return false;
        }

        public string Format(SemanticId nodeId) => Format(nodeId, new HashSet<SemanticId>(), 0);

        public string FormatActionInputs(GraphNode node)
        {
            string[] values = node.Ports
                .Where(static port => port.Kind == GraphPortKind.Data &&
                    port.Direction == GraphPortDirection.Input)
                .Select(port => Input(node.Id, port.Name))
                .Where(static value => value != "…")
                .ToArray();
            return values.Length == 0 ? node.Label : $"{node.Label} · {string.Join(", ", values)}";
        }

        private string Format(SemanticId nodeId, ISet<SemanticId> visiting, int depth)
        {
            if (depth > 12 || !nodes.TryGetValue(nodeId, out GraphNode? node) || !visiting.Add(nodeId))
            {
                return "…";
            }

            string InputAt(string port) =>
                incomingData.TryGetValue((node.Id, port), out GraphEdge? edge)
                    ? Format(edge.FromNode, visiting, depth + 1)
                    : "…";
            string text = node.Kind switch
            {
                "literal" => Property(node, "value", node.Label),
                "identifier" => Property(node, "name", node.Label),
                "this" => "this",
                "memberAccess" => $"{InputAt("target")}.{Property(node, "member", node.Label)}",
                "conditionalMemberBinding" => Property(node, "member", node.Label),
                "conditionalAccess" => $"{InputAt("receiver")}?.{InputAt("whenNotNull")}",
                "invocation" => FormatInvocation(node, InputAt),
                "binary" => $"({InputAt("left")} {Property(node, "operator", node.Label)} " +
                    $"{InputAt("right")})",
                "unary" => Property(node, "position", "prefix") == "postfix"
                    ? $"{InputAt("operand")}{Property(node, "operator", node.Label)}"
                    : $"{Property(node, "operator", node.Label)}{InputAt("operand")}",
                "assignment" => $"{InputAt("target")} {Property(node, "operator", node.Label)} " +
                    $"{InputAt("assignedValue")}",
                "cast" => $"({Property(node, "type", node.Label)}){InputAt("operand")}",
                "conditional" => $"{InputAt("condition")} ? {InputAt("whenTrue")} : " +
                    $"{InputAt("whenFalse")}",
                "await" => $"await {InputAt("operand")}",
                _ => node.Label,
            };
            visiting.Remove(nodeId);
            return text;
        }

        private static string FormatInvocation(GraphNode node, Func<string, string> input)
        {
            string[] arguments = node.Ports
                .Where(static port => port.Kind == GraphPortKind.Data &&
                    port.Direction == GraphPortDirection.Input && port.Name.StartsWith("arg", StringComparison.Ordinal))
                .OrderBy(static port => ParseArgumentIndex(port.Name))
                .Select(port => input(port.Name))
                .ToArray();
            return $"{input("target")}({string.Join(", ", arguments)})";
        }

        private static int ParseArgumentIndex(string port) =>
            int.TryParse(port.AsSpan(3), out int value) ? value : int.MaxValue;
    }
}
