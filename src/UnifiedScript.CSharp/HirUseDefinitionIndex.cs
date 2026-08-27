using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Semantics;

namespace UnifiedScript.CSharp;

internal sealed class HirUseDefinitionIndex
{
    private readonly Dictionary<SemanticId, HirNode> definitions = [];
    private readonly Dictionary<SemanticId, List<HirExpression>> uses = [];
    private readonly IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes;

    public HirUseDefinitionIndex(
        HirCompilationUnit compilation,
        IReadOnlyDictionary<SemanticId, BoundNodeInfo> boundNodes)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(boundNodes);
        this.boundNodes = boundNodes;

        foreach (HirParameter parameter in compilation.Types
                     .SelectMany(static type => type.Methods)
                     .SelectMany(static method => method.Parameters))
        {
            definitions.Add(parameter.Id, parameter);
        }

        foreach (HirVariableDeclarationStatement variable in compilation
                     .DescendantsAndSelf()
                     .OfType<HirVariableDeclarationStatement>())
        {
            definitions.Add(variable.Id, variable);
        }

        foreach (HirForEachVariable variable in compilation
                     .DescendantsAndSelf()
                     .OfType<HirForEachVariable>())
        {
            definitions.Add(variable.Id, variable);
        }

        foreach (HirForVariableDeclarator variable in compilation
                     .DescendantsAndSelf()
                     .OfType<HirForVariableDeclarator>())
        {
            definitions.Add(variable.Id, variable);
        }

        foreach (HirPatternVariable variable in compilation
                     .DescendantsAndSelf()
                     .OfType<HirPatternVariable>())
        {
            definitions.Add(variable.Id, variable);
        }

        foreach (HirExpression expression in compilation
                     .DescendantsAndSelf()
                     .OfType<HirExpression>())
        {
            if (!boundNodes.TryGetValue(expression.Id, out BoundNodeInfo? binding) ||
                binding.ReferencedSymbolId is not SemanticId definitionId ||
                !definitions.ContainsKey(definitionId))
            {
                continue;
            }

            if (!uses.TryGetValue(definitionId, out List<HirExpression>? definitionUses))
            {
                definitionUses = [];
                uses.Add(definitionId, definitionUses);
            }

            definitionUses.Add(expression);
        }
    }

    public bool HasUses(SemanticId definitionId) =>
        uses.TryGetValue(definitionId, out List<HirExpression>? definitionUses) &&
        definitionUses.Count > 0;

    public IReadOnlyList<HirExpression> GetUses(SemanticId definitionId) =>
        uses.TryGetValue(definitionId, out List<HirExpression>? definitionUses)
            ? definitionUses
            : [];

    public bool IsValueReference(HirExpression expression) =>
        boundNodes.TryGetValue(expression.Id, out BoundNodeInfo? binding) &&
        binding.ReferencedSymbolId is SemanticId definitionId &&
        definitions.ContainsKey(definitionId);

    public bool References(HirExpression expression, SemanticId definitionId) =>
        definitions.ContainsKey(definitionId) &&
        boundNodes.TryGetValue(expression.Id, out BoundNodeInfo? binding) &&
        binding.ReferencedSymbolId == definitionId;

    public bool TryGetReferencedDefinition(
        HirExpression expression,
        out HirNode? definition)
    {
        definition = null;
        if (!boundNodes.TryGetValue(expression.Id, out BoundNodeInfo? binding) ||
            binding.ReferencedSymbolId is not SemanticId definitionId)
        {
            return false;
        }
        return definitions.TryGetValue(definitionId, out definition);
    }
}
