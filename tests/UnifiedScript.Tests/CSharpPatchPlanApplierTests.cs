using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Semantics;

namespace UnifiedScript.Tests;

public sealed class CSharpPatchPlanApplierTests
{
    private const string SharedExpressionSource = """
        using UnifiedScript;

        static class Script
        {
            [Visual]
            static float Run(float damage)
            {
                return damage * 2.0f + damage * 3.0f + 1.0f;
            }
        }
        """;

    [Fact]
    public void Apply_IntroducesTemporaryAndReconnectsMultipleOverlappingConsumersAtomically()
    {
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            SharedExpressionSource,
            "Game/SharedExpressionPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        HirBinaryExpression sourceExpression = FindMultiplication(returnStatement, "2.0f");
        HirBinaryExpression secondConsumer = FindMultiplication(returnStatement, "3.0f");
        HirLiteralExpression thirdConsumer = Assert.Single(
            returnStatement.DescendantsAndSelf().OfType<HirLiteralExpression>(),
            static literal => literal.ValueText == "1.0f");
        HashSet<SemanticId> removedIds = secondConsumer
            .DescendantsAndSelf()
            .Concat(thirdConsumer.DescendantsAndSelf())
            .Select(static node => node.Id)
            .ToHashSet();
        SemanticId floatType = GetTypeId(original, sourceExpression);
        SemanticPatchPlanDocument plan = CreatePlan(
            original,
            method,
            returnStatement,
            sourceExpression,
            secondConsumer,
            thirdConsumer,
            floatType);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains(
            "float sharedDamage = damage * 2.0f;\n        " +
            "return sharedDamage + sharedDamage + sharedDamage;",
            patchedSource);

        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement introduced = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirBinaryExpression movedSource = Assert.IsType<HirBinaryExpression>(introduced.Initializer);
        HirReturnStatement changedReturn = Assert.IsType<HirReturnStatement>(changedMethod.Body.Statements[1]);
        HirIdentifierExpression[] uses = changedReturn
            .DescendantsAndSelf()
            .OfType<HirIdentifierExpression>()
            .Where(static identifier => identifier.Name == "sharedDamage")
            .ToArray();
        Assert.Equal(sourceExpression.Id, movedSource.Id);
        Assert.Equal(returnStatement.Id, changedReturn.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(3, uses.Length);
        Assert.All(
            uses,
            use => Assert.Equal(
                introduced.Id,
                Assert.Single(
                    result.Analysis.Semantics!.Nodes,
                    binding => binding.NodeId == use.Id).ReferencedSymbolId));

        IdentitySidecarDocument after = Assert.Single(result.Analysis.IdentitySidecars);
        Assert.All(
            removedIds,
            id => Assert.Contains(after.Tombstones, tombstone => tombstone.Id == id));
        Assert.Contains(after.Entries, entry => entry.Id == introduced.Id);
        CSharpSourceDocument finalDocument = Assert.Single(result.PatchedRequest.Sources);
        Assert.NotNull(finalDocument.IdentitySidecar);
        Assert.Equal(after.SourceDigest, finalDocument.IdentitySidecar.SourceDigest);
        Assert.Equal(after.Generation, finalDocument.IdentitySidecar.Generation);
        Assert.Empty(finalDocument.IdentityPins);
    }

    [Fact]
    public void Apply_RollsBackEveryEarlierStageWhenALaterConsumerIsEffectful()
    {
        const string source = """
            using UnifiedScript;

            static class Api
            {
                public static float Read() => 1.0f;
            }

            static class Script
            {
                [Visual]
                static float Run(float damage)
                {
                    float first = damage * 2.0f + 1.0f;
                    return Api.Read();
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/FailingSharedExpressionPlan.cs");
        HirMethodDeclaration method = Assert.Single(
            Assert.Single(original.Hir!.Types, static type => type.Name == "Script").Methods);
        HirVariableDeclarationStatement first = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirBinaryExpression firstExpression = Assert.IsType<HirBinaryExpression>(first.Initializer);
        HirBinaryExpression sourceExpression = Assert.Single(
            firstExpression.DescendantsAndSelf().OfType<HirBinaryExpression>(),
            static binary => binary.Operator == "*");
        HirLiteralExpression safeConsumer = Assert.Single(
            firstExpression.DescendantsAndSelf().OfType<HirLiteralExpression>(),
            static literal => literal.ValueText == "1.0f");
        HirInvocationExpression unsafeConsumer = Assert.Single(
            returnStatement.DescendantsAndSelf().OfType<HirInvocationExpression>());
        SemanticId floatType = GetTypeId(original, sourceExpression);
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new IntroduceTemporaryPatchPlanStep(
                    "sharedDamage",
                    sourceExpression.Id,
                    first.Id,
                    method.Body.Id,
                    null,
                    returnStatement.Id,
                    "sharedDamage",
                    "float",
                    floatType),
                new ReconnectValuePatchPlanStep(
                    "connectSafe",
                    safeConsumer.Id,
                    "sharedDamage",
                    first.Id,
                    method.Body.Id,
                    null,
                    returnStatement.Id,
                    floatType),
                new ReconnectValuePatchPlanStep(
                    "connectUnsafe",
                    unsafeConsumer.Id,
                    "sharedDamage",
                    returnStatement.Id,
                    method.Body.Id,
                    first.Id,
                    null,
                    floatType),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnsafeValueReconnection);
    }

    [Fact]
    public void Apply_RequiresPersistentIdentityBeforeExecutingAnyPlanStep()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            SharedExpressionSource,
            "Game/PlanWithoutIdentity.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        HirBinaryExpression sourceExpression = FindMultiplication(returnStatement, "2.0f");
        HirBinaryExpression secondConsumer = FindMultiplication(returnStatement, "3.0f");
        HirLiteralExpression thirdConsumer = Assert.Single(
            returnStatement.DescendantsAndSelf().OfType<HirLiteralExpression>(),
            static literal => literal.ValueText == "1.0f");
        SemanticPatchPlanDocument plan = CreatePlan(
            original,
            method,
            returnStatement,
            sourceExpression,
            secondConsumer,
            thirdConsumer,
            GetTypeId(original, sourceExpression));

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == SemanticPatchDiagnosticCodes.PlanRequiresPersistentIdentity);
    }

    [Fact]
    public void Apply_ValidatesEveryOriginalSiblingContextBeforeRebasingStages()
    {
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            SharedExpressionSource,
            "Game/StaleSharedExpressionPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        HirBinaryExpression sourceExpression = FindMultiplication(returnStatement, "2.0f");
        HirBinaryExpression secondConsumer = FindMultiplication(returnStatement, "3.0f");
        HirLiteralExpression thirdConsumer = Assert.Single(
            returnStatement.DescendantsAndSelf().OfType<HirLiteralExpression>(),
            static literal => literal.ValueText == "1.0f");
        SemanticId floatType = GetTypeId(original, sourceExpression);
        SemanticPatchPlanDocument valid = CreatePlan(
            original,
            method,
            returnStatement,
            sourceExpression,
            secondConsumer,
            thirdConsumer,
            floatType);
        var staleIntroduction = Assert.IsType<IntroduceTemporaryPatchPlanStep>(valid.Steps[0]) with
        {
            ExpectedNextSiblingId = SemanticId.FromKey("stale-next-sibling"),
        };
        SemanticPatchPlanDocument stale = SemanticPatchPlanDocument.Create(
            valid.SourceCompilationId,
            valid.SemanticConfigurationId,
            [staleIntroduction, .. valid.Steps.Skip(1)]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, stale);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == SemanticPatchDiagnosticCodes.SiblingPreconditionFailed);
    }

    private static SemanticPatchPlanDocument CreatePlan(
        CSharpAnalysisResult original,
        HirMethodDeclaration method,
        HirReturnStatement returnStatement,
        HirBinaryExpression sourceExpression,
        HirBinaryExpression secondConsumer,
        HirLiteralExpression thirdConsumer,
        SemanticId floatType) => SemanticPatchPlanDocument.Create(
        original.Hir!.Id,
        original.Semantics!.ConfigurationId,
        [
            Introduction(sourceExpression, returnStatement, method.Body, floatType),
            Reconnection("connectSecond", secondConsumer, returnStatement, method.Body, floatType),
            Reconnection("connectThird", thirdConsumer, returnStatement, method.Body, floatType),
        ]);

    private static IntroduceTemporaryPatchPlanStep Introduction(
        HirExpression target,
        HirStatement containingStatement,
        HirBlockStatement block,
        SemanticId typeId) => new(
        "sharedDamage",
        target.Id,
        containingStatement.Id,
        block.Id,
        null,
        null,
        "sharedDamage",
        "float",
        typeId);

    private static ReconnectValuePatchPlanStep Reconnection(
        string stepId,
        HirExpression target,
        HirStatement containingStatement,
        HirBlockStatement block,
        SemanticId typeId) => new(
        stepId,
        target.Id,
        "sharedDamage",
        containingStatement.Id,
        block.Id,
        null,
        null,
        typeId);

    private static HirBinaryExpression FindMultiplication(
        HirReturnStatement returnStatement,
        string rightLiteral) => Assert.Single(
        returnStatement.DescendantsAndSelf().OfType<HirBinaryExpression>(),
        binary => binary.Operator == "*" &&
            binary.Right is HirLiteralExpression literal &&
            literal.ValueText == rightLiteral);

    private static SemanticId GetTypeId(CSharpAnalysisResult analysis, HirNode node) =>
        Assert.Single(
            analysis.Semantics!.Nodes,
            candidate => candidate.NodeId == node.Id).TypeId!.Value;

    private static (CSharpCompilationRequest Request, CSharpAnalysisResult Analysis) CreatePersistentRequest(
        string source,
        string path)
    {
        var initializingRequest = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(path, source, IdentitySidecarDocument.CreateEmpty(path))]);
        CSharpAnalysisResult initialized = new CSharpSemanticPipeline().Analyze(initializingRequest);
        Assert.True(initialized.Succeeded, string.Join(Environment.NewLine, initialized.Diagnostics));
        var request = new CSharpCompilationRequest(
            "UnifiedScript.Analysis",
            [new CSharpSourceDocument(path, source, Assert.Single(initialized.IdentitySidecars))]);
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        return (request, analysis);
    }
}
