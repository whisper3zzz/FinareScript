using UnifiedScript.Core;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.CSharp;

internal sealed class HirPatchIndex
{
    private readonly Dictionary<SemanticId, BlockLocation> blocks = [];
    private readonly Dictionary<SemanticId, StatementLocation> statements = [];

    public HirPatchIndex(HirCompilationUnit compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        foreach (HirMethodDeclaration method in compilation.Types
                     .SelectMany(static type => type.Methods))
        {
            AddBlock(method.Body, method);
        }
    }

    public bool TryGetBlock(SemanticId id, out BlockLocation? location) =>
        blocks.TryGetValue(id, out location);

    public bool TryGetStatement(SemanticId id, out StatementLocation? location) =>
        statements.TryGetValue(id, out location);

    private void AddBlock(HirBlockStatement block, HirMethodDeclaration method)
    {
        blocks.Add(block.Id, new BlockLocation(block, method));
        for (int index = 0; index < block.Statements.Count; index++)
        {
            HirStatement statement = block.Statements[index];
            statements.Add(statement.Id, new StatementLocation(statement, block, method, index));
            switch (statement)
            {
                case HirBlockStatement nested:
                    AddBlock(nested, method);
                    break;
                case HirIfStatement ifStatement:
                    AddBlock(ifStatement.Then, method);
                    if (ifStatement.Else is not null)
                    {
                        AddBlock(ifStatement.Else, method);
                    }

                    break;
            }
        }
    }

    internal sealed record BlockLocation(
        HirBlockStatement Block,
        HirMethodDeclaration Method);

    internal sealed record StatementLocation(
        HirStatement Statement,
        HirBlockStatement ParentBlock,
        HirMethodDeclaration Method,
        int Index);
}
