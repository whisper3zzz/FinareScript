using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Graph;
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

    [Fact]
    public void Apply_ComposesInsertionHandleReconnectionOperatorChangeAndDeletion()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage)
                {
                    float obsolete = 1.0f;
                    return damage * 2.0f;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/MixedPatchPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement obsolete = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirBinaryExpression binary = Assert.IsType<HirBinaryExpression>(returnStatement.Expression);
        HirLiteralExpression consumer = Assert.IsType<HirLiteralExpression>(binary.Right);
        SemanticId floatType = GetTypeId(original, consumer);
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new InsertStatementPatchPlanStep(
                    "insertScale",
                    method.Body.Id,
                    null,
                    obsolete.Id,
                    new LocalLiteralDeclarationPatchStatement(
                        "scale",
                        "float",
                        "2.0f",
                        floatType)),
                new ReconnectValuePatchPlanStep(
                    "connectScale",
                    consumer.Id,
                    "insertScale",
                    returnStatement.Id,
                    method.Body.Id,
                    obsolete.Id,
                    null,
                    floatType),
                new ChangeOperatorPatchPlanStep(
                    "divideDamage",
                    binary.Id,
                    "*",
                    "/",
                    GetTypeId(original, binary),
                    GetTypeId(original, binary.Left),
                    GetTypeId(original, binary.Right)),
                new DeleteStatementPatchPlanStep(
                    "deleteObsolete",
                    obsolete.Id,
                    method.Body.Id,
                    null,
                    returnStatement.Id),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("float scale = 2.0f;\n        return damage / scale;", patchedSource);
        Assert.DoesNotContain("obsolete", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement scale = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirReturnStatement changedReturn = Assert.IsType<HirReturnStatement>(
            changedMethod.Body.Statements[1]);
        HirBinaryExpression changedBinary = Assert.IsType<HirBinaryExpression>(changedReturn.Expression);
        HirIdentifierExpression scaleUse = Assert.IsType<HirIdentifierExpression>(changedBinary.Right);
        Assert.Equal("/", changedBinary.Operator);
        Assert.Equal(
            scale.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == scaleUse.Id).ReferencedSymbolId);
        Assert.Contains(
            Assert.Single(result.Analysis.IdentitySidecars).Tombstones,
            tombstone => tombstone.Id == obsolete.Id);
    }

    [Fact]
    public void Apply_PreservesPlanOrderForRepeatedInsertionsIntoTheSameGap()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/RepeatedInsertPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, returnStatement.Expression!);
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new InsertStatementPatchPlanStep(
                    "insertFirst",
                    method.Body.Id,
                    null,
                    returnStatement.Id,
                    new LocalLiteralDeclarationPatchStatement("first", "int", "1", intType)),
                new InsertStatementPatchPlanStep(
                    "insertSecond",
                    method.Body.Id,
                    null,
                    returnStatement.Id,
                    new LocalLiteralDeclarationPatchStatement("second", "int", "2", intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "int first = 1;\n        int second = 2;\n        return value;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
    }

    [Fact]
    public void Apply_ComposesNonProducingIfInsertionWithLaterDirectStep()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(bool ready)
                {
                    return true;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/IfPatchPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirLiteralExpression literal = Assert.IsType<HirLiteralExpression>(returnStatement.Expression);
        SemanticId boolType = GetTypeId(original, method.Parameters[0]);
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new InsertStatementPatchPlanStep(
                    "insertGuard",
                    method.Body.Id,
                    null,
                    returnStatement.Id,
                    new IfPatchStatement(new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        "ready",
                        boolType))),
                new ReplaceLiteralPatchPlanStep(
                    "replaceReturn",
                    literal.Id,
                    "true",
                    "false"),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("if (ready)", patchedSource);
        Assert.Contains("return false;", patchedSource);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirIfStatement inserted = Assert.IsType<HirIfStatement>(changedMethod.Body.Statements[0]);
        Assert.NotNull(inserted.Else);
        HirReturnStatement changedReturn = Assert.IsType<HirReturnStatement>(
            changedMethod.Body.Statements[1]);
        Assert.Equal(literal.Id, Assert.IsType<HirLiteralExpression>(changedReturn.Expression).Id);
    }

    [Fact]
    public void Apply_InsertsCfgCheckedAssignmentAsDirectPlanStep()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int seed)
                {
                    int result = seed;
                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/AssignmentPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement definition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, definition);
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchPlanStep(
                "incrementResult",
                method.Body.Id,
                definition.Id,
                returnStatement.Id,
                new LocalAssignmentPatchStatement(
                    definition.Id,
                    definition.Name,
                    intType,
                    new BinaryPatchExpression(
                        "+",
                        new LocalReferencePatchExpression(
                            definition.Id,
                            definition.Name,
                            intType),
                        new LiteralPatchExpression("1", intType),
                        intType))) ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "int result = seed;\n        result = result + 1;\n        return result;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
        HirMethodDeclaration changed = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        Assert.IsType<HirAssignmentExpression>(
            Assert.IsType<HirExpressionStatement>(changed.Body.Statements[1]).Expression);
    }

    [Fact]
    public void Apply_PopulatesNewThenAndElseBlocksWithAssignmentsToOuterLocal()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    int result = 0;
                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/BranchAssignmentPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement definition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, definition);
        SemanticId boolType = GetTypeId(original, method.Parameters[0]);
        var guard = new InsertStatementPatchPlanStep(
            "insertGuard",
            method.Body.Id,
            definition.Id,
            returnStatement.Id,
            new IfPatchStatement(new LocalReferencePatchExpression(
                method.Parameters[0].Id,
                method.Parameters[0].Name,
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                guard,
                new InsertBranchStatementPatchPlanStep(
                    "assignThen",
                    method.Body.Id,
                    guard.StepId,
                    IfBranchKind.Then,
                    new LocalAssignmentPatchStatement(
                        definition.Id,
                        definition.Name,
                        intType,
                        new LiteralPatchExpression("1", intType))),
                new InsertBranchStatementPatchPlanStep(
                    "assignElse",
                    method.Body.Id,
                    guard.StepId,
                    IfBranchKind.Else,
                    new LocalAssignmentPatchStatement(
                        definition.Id,
                        definition.Name,
                        intType,
                        new LiteralPatchExpression("2", intType))),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("result = 1;", patchedSource);
        Assert.Contains("result = 2;", patchedSource);
        HirMethodDeclaration changed = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirIfStatement insertedIf = Assert.IsType<HirIfStatement>(changed.Body.Statements[1]);
        Assert.IsType<HirAssignmentExpression>(
            Assert.IsType<HirExpressionStatement>(Assert.Single(insertedIf.Then.Statements)).Expression);
        Assert.IsType<HirAssignmentExpression>(
            Assert.IsType<HirExpressionStatement>(Assert.Single(insertedIf.Else!.Statements)).Expression);
    }

    [Fact]
    public void Apply_RecursivelyPopulatesNestedProducedBranches()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool enabled, bool critical)
                {
                    int result = 0;
                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/NestedBranchPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId boolType = GetTypeId(original, method.Parameters[0]);
        SemanticId intType = GetTypeId(original, resultDefinition);
        IdentitySidecarDocument beforeIdentity = Assert.Single(original.IdentitySidecars);
        GraphDocument beforeGraph = new HirGraphProjector().Project(original.Hir, original.Semantics);
        GraphFunction beforeFunction = Assert.Single(beforeGraph.Functions);
        GraphLayoutDocument beforeLayout = new(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(
                beforeFunction.Id,
                beforeFunction.Nodes.Select((node, index) => new GraphNodeLayout(
                    node.Id,
                    GraphNodePlacement.Placed,
                    new GraphLayoutPoint(index * 100, index * 50),
                    new GraphLayoutSize(160, 80),
                    false)).ToArray(),
                [],
                [])],
            []);
        var outer = new InsertStatementPatchPlanStep(
            "insertOuter",
            method.Body.Id,
            resultDefinition.Id,
            returnStatement.Id,
            new IfPatchStatement(new LocalReferencePatchExpression(
                method.Parameters[0].Id,
                method.Parameters[0].Name,
                boolType)));
        var nested = new InsertBranchStatementPatchPlanStep(
            "insertNested",
            method.Body.Id,
            outer.StepId,
            IfBranchKind.Then,
            new IfPatchStatement(new LocalReferencePatchExpression(
                method.Parameters[1].Id,
                method.Parameters[1].Name,
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                outer,
                nested,
                new InsertBranchStatementPatchPlanStep(
                    "assignCritical",
                    method.Body.Id,
                    nested.StepId,
                    IfBranchKind.Then,
                    new LocalAssignmentPatchStatement(
                        resultDefinition.Id,
                        resultDefinition.Name,
                        intType,
                        new LiteralPatchExpression("2", intType))),
                new InsertBranchStatementPatchPlanStep(
                    "assignNormal",
                    method.Body.Id,
                    nested.StepId,
                    IfBranchKind.Else,
                    new LocalAssignmentPatchStatement(
                        resultDefinition.Id,
                        resultDefinition.Name,
                        intType,
                        new LiteralPatchExpression("1", intType))),
                new InsertBranchStatementPatchPlanStep(
                    "assignDisabled",
                    method.Body.Id,
                    outer.StepId,
                    IfBranchKind.Else,
                    new LocalAssignmentPatchStatement(
                        resultDefinition.Id,
                        resultDefinition.Name,
                        intType,
                        new LiteralPatchExpression("0", intType))),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("if (enabled)", patchedSource, StringComparison.Ordinal);
        Assert.Contains("if (critical)", patchedSource, StringComparison.Ordinal);
        Assert.Contains("result = 2;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("result = 1;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("result = 0;", patchedSource, StringComparison.Ordinal);

        HirMethodDeclaration changed = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirIfStatement changedOuter = Assert.IsType<HirIfStatement>(changed.Body.Statements[1]);
        HirIfStatement changedNested = Assert.IsType<HirIfStatement>(Assert.Single(changedOuter.Then.Statements));
        HirAssignmentExpression[] assignments =
        [
            Assert.IsType<HirAssignmentExpression>(
                Assert.IsType<HirExpressionStatement>(Assert.Single(changedNested.Then.Statements)).Expression),
            Assert.IsType<HirAssignmentExpression>(
                Assert.IsType<HirExpressionStatement>(Assert.Single(changedNested.Else!.Statements)).Expression),
            Assert.IsType<HirAssignmentExpression>(
                Assert.IsType<HirExpressionStatement>(Assert.Single(changedOuter.Else!.Statements)).Expression),
        ];
        Assert.All(
            assignments.Select(static assignment => Assert.IsType<HirIdentifierExpression>(assignment.Target)),
            target => Assert.Equal(
                resultDefinition.Id,
                Assert.Single(
                    result.Analysis.Semantics!.Nodes,
                    binding => binding.NodeId == target.Id).ReferencedSymbolId));

        IdentitySidecarDocument afterIdentity = Assert.Single(result.Analysis.IdentitySidecars);
        Assert.All(
            beforeIdentity.Entries,
            entry => Assert.Contains(afterIdentity.Entries, candidate => candidate.Id == entry.Id));
        Assert.Empty(afterIdentity.Tombstones);

        GraphDocument afterGraph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphFunction afterFunction = Assert.Single(afterGraph.Functions);
        GraphRegion outerIfRegion = Assert.Single(
            afterFunction.Regions,
            region => region.Kind == GraphRegionKind.If && region.SourceNodeId == changedOuter.Id);
        GraphRegion outerThenRegion = Assert.Single(
            afterFunction.Regions,
            region => region.Kind == GraphRegionKind.Then && region.ParentRegionId == outerIfRegion.Id);
        GraphRegion nestedIfRegion = Assert.Single(
            afterFunction.Regions,
            region => region.Kind == GraphRegionKind.If && region.SourceNodeId == changedNested.Id);
        Assert.Equal(outerThenRegion.Id, nestedIfRegion.ParentRegionId);
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(afterGraph).Functions);
        Assert.True(flow.IsReducible);
        Assert.Equal(2, flow.Branches.Count);
        Assert.All(flow.Branches, static branch => Assert.True(branch.MergeNodeId.HasValue));

        GraphLayoutDocument synchronized = new GraphLayoutSynchronizer().Synchronize(
            afterGraph,
            beforeLayout);
        GraphFunctionLayout synchronizedFunction = Assert.Single(synchronized.Functions);
        HashSet<SemanticId> oldNodeIds = beforeFunction.Nodes.Select(static node => node.Id).ToHashSet();
        Assert.All(
            synchronizedFunction.Nodes.Where(node => oldNodeIds.Contains(node.NodeId)),
            node => Assert.Equal(GraphNodePlacement.Placed, node.Placement));
        Assert.All(
            synchronizedFunction.Nodes.Where(node => !oldNodeIds.Contains(node.NodeId)),
            node => Assert.Equal(GraphNodePlacement.Unplaced, node.Placement));
        Assert.Empty(synchronized.OrphanedNodes);
    }

    [Fact]
    public void Apply_InsertsTerminalNestedBranchesWithoutAnOuterFallthrough()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool enabled, bool critical)
                {
                    return 0;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/NestedReturnPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement fallback = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        SemanticId boolType = GetTypeId(original, method.Parameters[0]);
        SemanticId intType = GetTypeId(original, fallback.Expression!);
        var outer = new InsertStatementPatchPlanStep(
            "insertOuter",
            method.Body.Id,
            null,
            fallback.Id,
            new IfPatchStatement(new LocalReferencePatchExpression(
                method.Parameters[0].Id,
                method.Parameters[0].Name,
                boolType)));
        var nested = new InsertBranchStatementPatchPlanStep(
            "insertNested",
            method.Body.Id,
            outer.StepId,
            IfBranchKind.Then,
            new IfPatchStatement(new LocalReferencePatchExpression(
                method.Parameters[1].Id,
                method.Parameters[1].Name,
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                outer,
                nested,
                new InsertBranchStatementPatchPlanStep(
                    "returnCritical",
                    method.Body.Id,
                    nested.StepId,
                    IfBranchKind.Then,
                    new ReturnPatchStatement(new LiteralPatchExpression("2", intType), intType)),
                new InsertBranchStatementPatchPlanStep(
                    "returnNormal",
                    method.Body.Id,
                    nested.StepId,
                    IfBranchKind.Else,
                    new ReturnPatchStatement(new LiteralPatchExpression("1", intType), intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirMethodDeclaration changed = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirIfStatement changedOuter = Assert.IsType<HirIfStatement>(changed.Body.Statements[0]);
        HirIfStatement changedNested = Assert.IsType<HirIfStatement>(Assert.Single(changedOuter.Then.Statements));
        Assert.IsType<HirReturnStatement>(Assert.Single(changedNested.Then.Statements));
        Assert.IsType<HirReturnStatement>(Assert.Single(changedNested.Else!.Statements));
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(
                new HirGraphProjector().Project(result.Analysis.Hir, result.Analysis.Semantics)).Functions);
        GraphControlFlowBranch nestedFlow = Assert.Single(
            flow.Branches,
            branch => branch.BranchNodeId == changedNested.Id);
        Assert.True(nestedFlow.ThenExitsFunction);
        Assert.True(nestedFlow.ElseExitsFunction);
        Assert.Null(nestedFlow.MergeNodeId);
    }

    [Fact]
    public void Apply_RollsBackWhenANestedProducedIfHasTheWrongConditionType()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool enabled, int count)
                {
                    return 0;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/NestedBranchRollback.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement fallback = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        SemanticId boolType = GetTypeId(original, method.Parameters[0]);
        SemanticId intType = GetTypeId(original, method.Parameters[1]);
        var outer = new InsertStatementPatchPlanStep(
            "insertOuter",
            method.Body.Id,
            null,
            fallback.Id,
            new IfPatchStatement(new LocalReferencePatchExpression(
                method.Parameters[0].Id,
                method.Parameters[0].Name,
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                outer,
                new InsertBranchStatementPatchPlanStep(
                    "invalidNested",
                    method.Body.Id,
                    outer.StepId,
                    IfBranchKind.Then,
                    new IfPatchStatement(new LocalReferencePatchExpression(
                        method.Parameters[1].Id,
                        method.Parameters[1].Name,
                        intType))),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_PopulatesProducedThenAndElseBlocksInPlanOrder()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(int damage, int threshold)
                {
                    return true;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/BranchBodyPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticId boolType = GetTypeId(original, returnStatement.Expression!);
        IdentitySidecarDocument beforeIdentity = Assert.Single(original.IdentitySidecars);
        GraphDocument beforeGraph = new HirGraphProjector().Project(original.Hir, original.Semantics);
        GraphFunction beforeFunction = Assert.Single(beforeGraph.Functions);
        GraphLayoutDocument beforeLayout = new(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(
                beforeFunction.Id,
                beforeFunction.Nodes.Select((node, index) => new GraphNodeLayout(
                    node.Id,
                    GraphNodePlacement.Placed,
                    new GraphLayoutPoint(index * 100, index * 50),
                    new GraphLayoutSize(160, 80),
                    false)).ToArray(),
                [],
                [])],
            []);
        var guard = new InsertStatementPatchPlanStep(
            "insertGuard",
            method.Body.Id,
            null,
            returnStatement.Id,
            new IfPatchStatement(new BinaryPatchExpression(
                ">",
                new LocalReferencePatchExpression(
                    method.Parameters[0].Id,
                    "damage",
                    intType),
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    "threshold",
                    intType),
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                guard,
                new InsertBranchStatementPatchPlanStep(
                    "insertHighDamage",
                    method.Body.Id,
                    guard.StepId,
                    IfBranchKind.Then,
                    new LocalLiteralDeclarationPatchStatement("highDamage", "int", "2", intType)),
                new InsertBranchStatementPatchPlanStep(
                    "insertBonus",
                    method.Body.Id,
                    guard.StepId,
                    IfBranchKind.Then,
                    new LocalLiteralDeclarationPatchStatement("bonus", "int", "3", intType)),
                new InsertBranchStatementPatchPlanStep(
                    "insertFallback",
                    method.Body.Id,
                    guard.StepId,
                    IfBranchKind.Else,
                    new LocalLiteralDeclarationPatchStatement("fallback", "int", "1", intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("if (damage > threshold)", patchedSource, StringComparison.Ordinal);
        Assert.Contains(
            "int highDamage = 2;\n            int bonus = 3;",
            patchedSource,
            StringComparison.Ordinal);
        Assert.Contains("else\n        {\n            int fallback = 1;", patchedSource, StringComparison.Ordinal);

        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirIfStatement inserted = Assert.IsType<HirIfStatement>(changedMethod.Body.Statements[0]);
        HirVariableDeclarationStatement highDamage = Assert.IsType<HirVariableDeclarationStatement>(
            inserted.Then.Statements[0]);
        HirVariableDeclarationStatement bonus = Assert.IsType<HirVariableDeclarationStatement>(
            inserted.Then.Statements[1]);
        HirVariableDeclarationStatement fallback = Assert.IsType<HirVariableDeclarationStatement>(
            Assert.Single(inserted.Else!.Statements));
        Assert.Equal("highDamage", highDamage.Name);
        Assert.Equal("bonus", bonus.Name);
        Assert.Equal("fallback", fallback.Name);
        Assert.Equal(returnStatement.Id, changedMethod.Body.Statements[1].Id);

        IdentitySidecarDocument afterIdentity = Assert.Single(result.Analysis.IdentitySidecars);
        Assert.All(
            beforeIdentity.Entries,
            entry => Assert.Contains(afterIdentity.Entries, candidate => candidate.Id == entry.Id));
        Assert.Empty(afterIdentity.Tombstones);

        GraphDocument afterGraph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphFunction afterFunction = Assert.Single(afterGraph.Functions);
        Assert.Contains(afterFunction.Nodes, node => node.Id == inserted.Id && node.Kind == "branch");
        Assert.Contains(afterFunction.Nodes, node => node.Id == highDamage.Id && node.Kind == "variable");
        Assert.Contains(afterFunction.Nodes, node => node.Id == bonus.Id && node.Kind == "variable");
        Assert.Contains(afterFunction.Nodes, node => node.Id == fallback.Id && node.Kind == "variable");
        Assert.Contains(
            afterFunction.Edges,
            edge => edge.Kind == GraphEdgeKind.Control &&
                edge.FromNode == inserted.Id &&
                edge.FromPort == "then" &&
                edge.ToNode == highDamage.Id);
        Assert.Contains(
            afterFunction.Edges,
            edge => edge.Kind == GraphEdgeKind.Control &&
                edge.FromNode == inserted.Id &&
                edge.FromPort == "else" &&
                edge.ToNode == fallback.Id);

        GraphLayoutDocument synchronized = new GraphLayoutSynchronizer().Synchronize(
            afterGraph,
            beforeLayout);
        GraphFunctionLayout synchronizedFunction = Assert.Single(synchronized.Functions);
        HashSet<SemanticId> oldGraphNodeIds = beforeFunction.Nodes
            .Select(static node => node.Id)
            .ToHashSet();
        Assert.All(
            synchronizedFunction.Nodes.Where(node => oldGraphNodeIds.Contains(node.NodeId)),
            node => Assert.Equal(GraphNodePlacement.Placed, node.Placement));
        Assert.All(
            synchronizedFunction.Nodes.Where(node => !oldGraphNodeIds.Contains(node.NodeId)),
            node => Assert.Equal(GraphNodePlacement.Unplaced, node.Placement));
        Assert.Empty(synchronized.OrphanedNodes);
    }

    [Fact]
    public void Apply_InsertsTypedEarlyReturnIntoProducedThenBlock()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(bool ready)
                {
                    return true;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/BranchReturnPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement fallbackReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirLiteralExpression fallbackLiteral = Assert.IsType<HirLiteralExpression>(fallbackReturn.Expression);
        SemanticId boolType = GetTypeId(original, method);
        IdentitySidecarDocument beforeIdentity = Assert.Single(original.IdentitySidecars);
        GraphDocument beforeGraph = new HirGraphProjector().Project(original.Hir, original.Semantics);
        GraphFunction beforeFunction = Assert.Single(beforeGraph.Functions);
        GraphLayoutDocument beforeLayout = new(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(
                beforeFunction.Id,
                beforeFunction.Nodes.Select((node, index) => new GraphNodeLayout(
                    node.Id,
                    GraphNodePlacement.Placed,
                    new GraphLayoutPoint(index * 100, index * 50),
                    new GraphLayoutSize(160, 80),
                    false)).ToArray(),
                [],
                [])],
            []);
        var guard = new InsertStatementPatchPlanStep(
            "insertGuard",
            method.Body.Id,
            null,
            fallbackReturn.Id,
            new IfPatchStatement(new LocalReferencePatchExpression(
                method.Parameters[0].Id,
                "ready",
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                guard,
                new InsertBranchStatementPatchPlanStep(
                    "returnWhenReady",
                    method.Body.Id,
                    guard.StepId,
                    IfBranchKind.Then,
                    new ReturnPatchStatement(
                        new LiteralPatchExpression("true", boolType),
                        boolType)),
                new ReplaceLiteralPatchPlanStep(
                    "replaceFallback",
                    fallbackLiteral.Id,
                    "true",
                    "false"),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("if (ready)\n        {\n            return true;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("return false;", patchedSource, StringComparison.Ordinal);

        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirIfStatement insertedIf = Assert.IsType<HirIfStatement>(changedMethod.Body.Statements[0]);
        HirReturnStatement branchReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(insertedIf.Then.Statements));
        Assert.Empty(insertedIf.Else!.Statements);
        HirReturnStatement changedFallback = Assert.IsType<HirReturnStatement>(
            changedMethod.Body.Statements[1]);
        Assert.Equal(fallbackReturn.Id, changedFallback.Id);
        Assert.Equal(
            fallbackLiteral.Id,
            Assert.IsType<HirLiteralExpression>(changedFallback.Expression).Id);
        Assert.Equal(
            "true",
            Assert.IsType<HirLiteralExpression>(branchReturn.Expression).ValueText);
        Assert.Equal(
            "false",
            Assert.IsType<HirLiteralExpression>(changedFallback.Expression).ValueText);

        IdentitySidecarDocument afterIdentity = Assert.Single(result.Analysis.IdentitySidecars);
        Assert.All(
            beforeIdentity.Entries,
            entry => Assert.Contains(afterIdentity.Entries, candidate => candidate.Id == entry.Id));
        Assert.Empty(afterIdentity.Tombstones);

        GraphDocument afterGraph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphFunction afterFunction = Assert.Single(afterGraph.Functions);
        Assert.Contains(afterFunction.Nodes, node => node.Id == insertedIf.Id && node.Kind == "branch");
        Assert.Contains(afterFunction.Nodes, node => node.Id == branchReturn.Id && node.Kind == "return");
        Assert.Contains(
            afterFunction.Edges,
            edge => edge.Kind == GraphEdgeKind.Control &&
                edge.FromNode == insertedIf.Id &&
                edge.FromPort == "then" &&
                edge.ToNode == branchReturn.Id);
        Assert.Contains(
            afterFunction.Edges,
            edge => edge.Kind == GraphEdgeKind.Control &&
                edge.FromNode == insertedIf.Id &&
                edge.FromPort == "else" &&
                edge.ToNode == fallbackReturn.Id);
        Assert.All(
            beforeFunction.Regions,
            region => Assert.Contains(afterFunction.Regions, candidate => candidate.Id == region.Id));
        GraphRegion branchRegion = Assert.Single(
            afterFunction.Regions,
            region => region.Kind == GraphRegionKind.If && region.SourceNodeId == insertedIf.Id);
        GraphRegion thenRegion = Assert.Single(
            afterFunction.Regions,
            region => region.Kind == GraphRegionKind.Then && region.ParentRegionId == branchRegion.Id);
        GraphRegion elseRegion = Assert.Single(
            afterFunction.Regions,
            region => region.Kind == GraphRegionKind.Else && region.ParentRegionId == branchRegion.Id);
        GraphRegion bodyRegion = Assert.Single(
            afterFunction.Regions,
            region => region.Kind == GraphRegionKind.Block &&
                region.SourceNodeId == changedMethod.Body.Id);
        Assert.Equal([thenRegion.Id, elseRegion.Id], branchRegion.ChildRegionIds);
        Assert.Contains(branchReturn.Id, thenRegion.NodeIds);
        Assert.Empty(elseRegion.NodeIds);
        Assert.False(elseRegion.IsImplicit);
        Assert.Contains(changedFallback.Id, bodyRegion.NodeIds);

        GraphLayoutDocument synchronized = new GraphLayoutSynchronizer().Synchronize(
            afterGraph,
            beforeLayout);
        GraphFunctionLayout synchronizedFunction = Assert.Single(synchronized.Functions);
        HashSet<SemanticId> oldGraphNodeIds = beforeFunction.Nodes
            .Select(static node => node.Id)
            .ToHashSet();
        Assert.All(
            synchronizedFunction.Nodes.Where(node => oldGraphNodeIds.Contains(node.NodeId)),
            node => Assert.Equal(GraphNodePlacement.Placed, node.Placement));
        Assert.All(
            synchronizedFunction.Nodes.Where(node => !oldGraphNodeIds.Contains(node.NodeId)),
            node => Assert.Equal(GraphNodePlacement.Unplaced, node.Placement));
        Assert.Empty(synchronized.OrphanedNodes);
    }

    [Fact]
    public void Apply_RollsBackIfProducedBranchReturnHasTheWrongValueType()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(bool ready, int value)
                {
                    return false;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/BranchReturnRollback.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement fallbackReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId boolType = GetTypeId(original, method);
        SemanticId intType = GetTypeId(original, method.Parameters[1]);
        var guard = new InsertStatementPatchPlanStep(
            "insertGuard",
            method.Body.Id,
            null,
            fallbackReturn.Id,
            new IfPatchStatement(new LocalReferencePatchExpression(
                method.Parameters[0].Id,
                "ready",
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                guard,
                new InsertBranchStatementPatchPlanStep(
                    "invalidReturn",
                    method.Body.Id,
                    guard.StepId,
                    IfBranchKind.Then,
                    new ReturnPatchStatement(
                        new LiteralPatchExpression("1", intType),
                        boolType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_RollsBackWhenBranchLocalEscapesItsProducedBlock()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    return 0;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/BranchHandleRollback.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirLiteralExpression consumer = Assert.IsType<HirLiteralExpression>(returnStatement.Expression);
        SemanticId intType = GetTypeId(original, consumer);
        SemanticId boolType = GetTypeId(original, method.Parameters[0]);
        var guard = new InsertStatementPatchPlanStep(
            "insertGuard",
            method.Body.Id,
            null,
            returnStatement.Id,
            new IfPatchStatement(new LocalReferencePatchExpression(
                method.Parameters[0].Id,
                "ready",
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                guard,
                new InsertBranchStatementPatchPlanStep(
                    "insertBranchValue",
                    method.Body.Id,
                    guard.StepId,
                    IfBranchKind.Then,
                    new LocalLiteralDeclarationPatchStatement(
                        "branchValue",
                        "int",
                        "1",
                        intType)),
                new ReconnectValuePatchPlanStep(
                    "escapeBranchValue",
                    consumer.Id,
                    "insertBranchValue",
                    returnStatement.Id,
                    method.Body.Id,
                    null,
                    null,
                    intType),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.ValueSourceNotAvailable);
    }

    [Fact]
    public void Apply_RebasesArgumentEndpointsAfterAnEarlierLiteralReplacement()
    {
        const string source = """
            using UnifiedScript;

            static class Api
            {
                public static int Combine(int first, int second) => first + second;
            }

            static class Script
            {
                [Visual]
                static int Run()
                {
                    return Api.Combine(1, 2);
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ArgumentPlan.cs");
        HirMethodDeclaration method = Assert.Single(
            Assert.Single(original.Hir!.Types, static type => type.Name == "Script").Methods);
        HirInvocationExpression invocation = Assert.Single(
            method.DescendantsAndSelf().OfType<HirInvocationExpression>());
        HirLiteralExpression first = Assert.IsType<HirLiteralExpression>(invocation.Arguments[0]);
        HirLiteralExpression second = Assert.IsType<HirLiteralExpression>(invocation.Arguments[1]);
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new ReplaceLiteralPatchPlanStep("replaceFirst", first.Id, "1", "10"),
                new ReconnectArgumentPatchPlanStep(
                    "swapArguments",
                    invocation.Id,
                    0,
                    first.Id,
                    1,
                    second.Id,
                    GetTypeId(original, first),
                    GetTypeId(original, second)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("return Api.Combine(2, 10);", Assert.Single(result.PatchedRequest!.Sources).Source);
    }

    [Fact]
    public void Apply_ConstructsTypedWhileBodyWithNearestLoopTransfersAtomically()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value, int stopAt, bool enabled)
                {
                    int result = value;
                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/LoopBuildPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticId boolType = GetTypeId(original, method.Parameters[2]);
        IdentitySidecarDocument beforeIdentity = Assert.Single(original.IdentitySidecars);
        GraphDocument beforeGraph = new HirGraphProjector().Project(original.Hir, original.Semantics);
        GraphFunction beforeFunction = Assert.Single(beforeGraph.Functions);
        GraphLayoutDocument beforeLayout = new(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(
                beforeFunction.Id,
                beforeFunction.Nodes.Select((node, index) => new GraphNodeLayout(
                    node.Id,
                    GraphNodePlacement.Placed,
                    new GraphLayoutPoint(index * 110, index * 60),
                    new GraphLayoutSize(160, 80),
                    false)).ToArray(),
                [],
                [])],
            []);
        var loop = new InsertStatementPatchPlanStep(
            "insertLoop",
            method.Body.Id,
            resultDefinition.Id,
            returnStatement.Id,
            new WhilePatchStatement(new BinaryPatchExpression(
                ">",
                new LocalReferencePatchExpression(
                    resultDefinition.Id,
                    resultDefinition.Name,
                    intType),
                new LiteralPatchExpression("0", intType),
                boolType)));
        var stopGuard = new InsertLoopBodyStatementPatchPlanStep(
            "insertStopGuard",
            method.Body.Id,
            loop.StepId,
            new IfPatchStatement(new BinaryPatchExpression(
                "==",
                new LocalReferencePatchExpression(
                    resultDefinition.Id,
                    resultDefinition.Name,
                    intType),
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    method.Parameters[1].Name,
                    intType),
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                loop,
                stopGuard,
                new InsertBranchStatementPatchPlanStep(
                    "breakAtStop",
                    method.Body.Id,
                    stopGuard.StepId,
                    IfBranchKind.Then,
                    new BreakPatchStatement()),
                new InsertLoopBodyStatementPatchPlanStep(
                    "decrementResult",
                    method.Body.Id,
                    loop.StepId,
                    new LocalAssignmentPatchStatement(
                        resultDefinition.Id,
                        resultDefinition.Name,
                        intType,
                        new BinaryPatchExpression(
                            "-",
                            new LocalReferencePatchExpression(
                                resultDefinition.Id,
                                resultDefinition.Name,
                                intType),
                            new LiteralPatchExpression("1", intType),
                            intType))),
                new InsertLoopBodyStatementPatchPlanStep(
                    "continueLoop",
                    method.Body.Id,
                    loop.StepId,
                    new ContinuePatchStatement()),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("while (result > 0)", patchedSource, StringComparison.Ordinal);
        Assert.Contains("if (result == stopAt)", patchedSource, StringComparison.Ordinal);
        Assert.Contains("break;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("result = result - 1;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("continue;", patchedSource, StringComparison.Ordinal);

        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        Assert.Equal(method.Id, changedMethod.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(resultDefinition.Id, changedMethod.Body.Statements[0].Id);
        HirWhileStatement insertedLoop = Assert.IsType<HirWhileStatement>(changedMethod.Body.Statements[1]);
        HirIfStatement insertedGuard = Assert.IsType<HirIfStatement>(insertedLoop.Body.Statements[0]);
        HirBreakStatement insertedBreak = Assert.IsType<HirBreakStatement>(
            Assert.Single(insertedGuard.Then.Statements));
        Assert.Empty(insertedGuard.Else!.Statements);
        HirAssignmentExpression assignment = Assert.IsType<HirAssignmentExpression>(
            Assert.IsType<HirExpressionStatement>(insertedLoop.Body.Statements[1]).Expression);
        HirContinueStatement insertedContinue = Assert.IsType<HirContinueStatement>(
            insertedLoop.Body.Statements[2]);
        Assert.Equal(returnStatement.Id, changedMethod.Body.Statements[2].Id);
        HirIdentifierExpression assignmentTarget = Assert.IsType<HirIdentifierExpression>(assignment.Target);
        Assert.Equal(
            resultDefinition.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == assignmentTarget.Id).ReferencedSymbolId);

        IdentitySidecarDocument afterIdentity = Assert.Single(result.Analysis.IdentitySidecars);
        Assert.All(
            beforeIdentity.Entries,
            entry => Assert.Contains(afterIdentity.Entries, candidate => candidate.Id == entry.Id));
        Assert.Contains(afterIdentity.Entries, entry => entry.Id == insertedLoop.Id);
        Assert.Contains(afterIdentity.Entries, entry => entry.Id == insertedLoop.Body.Id);
        Assert.Empty(afterIdentity.Tombstones);

        GraphDocument afterGraph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphFunction afterFunction = Assert.Single(afterGraph.Functions);
        GraphRegion loopRegion = Assert.Single(
            afterFunction.Regions,
            region => region.Kind == GraphRegionKind.Loop && region.SourceNodeId == insertedLoop.Id);
        GraphRegion loopBodyRegion = Assert.Single(
            afterFunction.Regions,
            region => region.Kind == GraphRegionKind.LoopBody && region.ParentRegionId == loopRegion.Id);
        GraphRegion guardRegion = Assert.Single(
            afterFunction.Regions,
            region => region.Kind == GraphRegionKind.If &&
                region.ParentRegionId == loopBodyRegion.Id &&
                region.SourceNodeId == insertedGuard.Id);
        Assert.Contains(guardRegion.Id, loopBodyRegion.ChildRegionIds);
        Assert.Contains(insertedContinue.Id, loopBodyRegion.NodeIds);
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(afterGraph).Functions);
        GraphControlFlowLoop loopFlow = Assert.Single(flow.Loops);
        Assert.True(flow.IsReducible);
        Assert.Equal(insertedLoop.Id, loopFlow.LoopNodeId);
        Assert.Equal(insertedGuard.Id, loopFlow.BodyEntryNodeId);
        Assert.Equal(returnStatement.Id, loopFlow.ExitNodeId);
        Assert.Contains(insertedBreak.Id, loopFlow.BreakNodeIds);
        Assert.Contains(insertedContinue.Id, loopFlow.ContinueNodeIds);
        Assert.NotEmpty(loopFlow.BackEdgeArcIds);

        GraphLayoutDocument synchronized = new GraphLayoutSynchronizer().Synchronize(
            afterGraph,
            beforeLayout);
        GraphFunctionLayout synchronizedFunction = Assert.Single(synchronized.Functions);
        HashSet<SemanticId> oldGraphNodeIds = beforeFunction.Nodes
            .Select(static node => node.Id)
            .ToHashSet();
        Assert.All(
            synchronizedFunction.Nodes.Where(node => oldGraphNodeIds.Contains(node.NodeId)),
            node => Assert.Equal(GraphNodePlacement.Placed, node.Placement));
        Assert.All(
            synchronizedFunction.Nodes.Where(node => !oldGraphNodeIds.Contains(node.NodeId)),
            node => Assert.Equal(GraphNodePlacement.Unplaced, node.Placement));
        Assert.Empty(synchronized.OrphanedNodes);
    }

    [Fact]
    public void Apply_ConstructsForAndUsesItsProducedLocalInsideBodyAtomically()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int count, bool enabled)
                {
                    int total = 0;
                    return total;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ForBuildPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement total = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, total);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var index = new ForLocalReferencePatchExpression(0, "index", intType);
        var insertFor = new InsertStatementPatchPlanStep(
            "insertFor",
            method.Body.Id,
            total.Id,
            valueReturn.Id,
            new ForPatchStatement(
                new ForVariableDeclarationPatch(
                    "int",
                    [new ForVariableDeclaratorPatch(
                        "index",
                        new LiteralPatchExpression("0", intType),
                        intType)]),
                [],
                new BinaryPatchExpression(
                    "<",
                    index,
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        method.Parameters[0].Name,
                        intType),
                    boolType),
                [new ForMutationPatchExpression(index, "++", true, intType)]));
        var producedIndex = new ProducedLocalReferencePatchExpression(
            insertFor.StepId,
            "index",
            intType);
        var skipGuard = new InsertLoopBodyStatementPatchPlanStep(
            "skipTwo",
            method.Body.Id,
            insertFor.StepId,
            new IfPatchStatement(new BinaryPatchExpression(
                "==",
                producedIndex,
                new LiteralPatchExpression("2", intType),
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                insertFor,
                skipGuard,
                new InsertBranchStatementPatchPlanStep(
                    "continueAtTwo",
                    method.Body.Id,
                    skipGuard.StepId,
                    IfBranchKind.Then,
                    new ContinuePatchStatement()),
                new InsertLoopBodyStatementPatchPlanStep(
                    "accumulate",
                    method.Body.Id,
                    insertFor.StepId,
                    new LocalAssignmentPatchStatement(
                        total.Id,
                        total.Name,
                        intType,
                        new BinaryPatchExpression(
                            "+",
                            new LocalReferencePatchExpression(total.Id, total.Name, intType),
                            producedIndex,
                            intType))),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("for (int index = 0; index < count; index++)", patchedSource,
            StringComparison.Ordinal);
        Assert.Contains("if (index == 2)", patchedSource, StringComparison.Ordinal);
        Assert.Contains("continue;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("total = total + index;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(
            Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirForStatement insertedFor = Assert.IsType<HirForStatement>(changedMethod.Body.Statements[1]);
        HirForVariableDeclarator insertedIndex = Assert.Single(insertedFor.Declaration!.Variables);
        HirIfStatement insertedGuard = Assert.IsType<HirIfStatement>(insertedFor.Body.Statements[0]);
        HirContinueStatement insertedContinue = Assert.IsType<HirContinueStatement>(
            Assert.Single(insertedGuard.Then.Statements));
        HirAssignmentExpression insertedAssignment = Assert.IsType<HirAssignmentExpression>(
            Assert.IsType<HirExpressionStatement>(insertedFor.Body.Statements[1]).Expression);
        HirIdentifierExpression assignedIndex = Assert.IsType<HirIdentifierExpression>(
            Assert.IsType<HirBinaryExpression>(insertedAssignment.Value).Right);
        Assert.Equal(
            insertedIndex.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == assignedIndex.Id).ReferencedSymbolId);
        HirUnaryExpression increment = Assert.IsType<HirUnaryExpression>(
            Assert.Single(insertedFor.Incrementors));
        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphControlFlowLoop flow = Assert.Single(
            Assert.Single(new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph).Functions).Loops);
        Assert.Equal(insertedFor.Id, flow.LoopNodeId);
        Assert.Equal(increment.Id, flow.ContinueTargetNodeId);
        Assert.Contains(insertedContinue.Id, flow.ContinueNodeIds);
        Assert.Equal(valueReturn.Id, changedMethod.Body.Statements[2].Id);
        Assert.Empty(Assert.Single(result.Analysis.IdentitySidecars).Tombstones);
    }

    [Fact]
    public void Apply_RollsBackForPlanWhenBodyShadowsItsProducedLocal()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int count, bool enabled)
                {
                    return count;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ForRollbackPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var index = new ForLocalReferencePatchExpression(0, "index", intType);
        var insertFor = new InsertStatementPatchPlanStep(
            "insertFor",
            method.Body.Id,
            null,
            valueReturn.Id,
            new ForPatchStatement(
                new ForVariableDeclarationPatch(
                    "int",
                    [new ForVariableDeclaratorPatch(
                        "index",
                        new LiteralPatchExpression("0", intType),
                        intType)]),
                [],
                new BinaryPatchExpression(
                    "<",
                    index,
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        method.Parameters[0].Name,
                        intType),
                    boolType),
                [new ForMutationPatchExpression(index, "++", true, intType)]));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                insertFor,
                new InsertLoopBodyStatementPatchPlanStep(
                    "shadowIndex",
                    method.Body.Id,
                    insertFor.StepId,
                    new LocalLiteralDeclarationPatchStatement("index", "int", "1", intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.NameConflict);
    }

    [Fact]
    public void Apply_RollsBackForPlanWhenItsProducedLocalEscapesTheLoop()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int count, bool enabled)
                {
                    return count;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ForLocalEscapeRollback.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var index = new ForLocalReferencePatchExpression(0, "index", intType);
        var insertFor = new InsertStatementPatchPlanStep(
            "insertFor",
            method.Body.Id,
            null,
            valueReturn.Id,
            new ForPatchStatement(
                new ForVariableDeclarationPatch(
                    "int",
                    [new ForVariableDeclaratorPatch(
                        "index",
                        new LiteralPatchExpression("0", intType),
                        intType)]),
                [],
                new BinaryPatchExpression(
                    "<",
                    index,
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        method.Parameters[0].Name,
                        intType),
                    boolType),
                [new ForMutationPatchExpression(index, "++", true, intType)]));
        var producedIndex = new ProducedLocalReferencePatchExpression(
            insertFor.StepId,
            "index",
            intType);
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                insertFor,
                new ReplaceReturnValuePatchPlanStep(
                    "escapeIndex",
                    valueReturn.Id,
                    valueReturn.Expression!.Id,
                    intType,
                    producedIndex),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.ValueSourceNotAvailable);
    }

    [Fact]
    public void Apply_ReplacesExistingAssignmentValueAndWhileConditionAtomically()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value, int stopAt, bool enabled)
                {
                    int result = value;
                    while (result > 0)
                    {
                        result = result - 1;
                    }

                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/DirectStructuredExpressionPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirWhileStatement loop = Assert.IsType<HirWhileStatement>(method.Body.Statements[1]);
        HirExpressionStatement assignmentStatement = Assert.IsType<HirExpressionStatement>(
            Assert.Single(loop.Body.Statements));
        HirAssignmentExpression assignment = Assert.IsType<HirAssignmentExpression>(
            assignmentStatement.Expression);
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticId boolType = GetTypeId(original, loop.Condition);
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new ReplaceAssignmentValuePatchPlanStep(
                    "editDecrement",
                    assignment.Id,
                    assignment.Operator,
                    assignment.Target.Id,
                    resultDefinition.Id,
                    resultDefinition.Name,
                    assignment.Value.Id,
                    intType,
                    new BinaryPatchExpression(
                        "-",
                        new LocalReferencePatchExpression(
                            resultDefinition.Id,
                            resultDefinition.Name,
                            intType),
                        new LiteralPatchExpression("2", intType),
                        intType)),
                new ReplaceWhileConditionPatchPlanStep(
                    "editLoopCondition",
                    loop.Id,
                    loop.Condition.Id,
                    boolType,
                    new BinaryPatchExpression(
                        "&&",
                        new LocalReferencePatchExpression(
                            method.Parameters[2].Id,
                            method.Parameters[2].Name,
                            boolType),
                        new BinaryPatchExpression(
                            ">",
                            new LocalReferencePatchExpression(
                                resultDefinition.Id,
                                resultDefinition.Name,
                                intType),
                            new LocalReferencePatchExpression(
                                method.Parameters[1].Id,
                                method.Parameters[1].Name,
                                intType),
                            boolType),
                        boolType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("while (enabled && (result > stopAt))", patchedSource, StringComparison.Ordinal);
        Assert.Contains("result = result - 2;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirWhileStatement changedLoop = Assert.IsType<HirWhileStatement>(changedMethod.Body.Statements[1]);
        HirExpressionStatement changedAssignmentStatement = Assert.IsType<HirExpressionStatement>(
            Assert.Single(changedLoop.Body.Statements));
        HirAssignmentExpression changedAssignment = Assert.IsType<HirAssignmentExpression>(
            changedAssignmentStatement.Expression);
        Assert.Equal(loop.Id, changedLoop.Id);
        Assert.Equal(loop.Body.Id, changedLoop.Body.Id);
        Assert.Equal(assignmentStatement.Id, changedAssignmentStatement.Id);
        Assert.Equal(assignment.Id, changedAssignment.Id);
        Assert.Equal(assignment.Target.Id, changedAssignment.Target.Id);
        IdentitySidecarDocument identity = Assert.Single(result.Analysis.IdentitySidecars);
        Assert.Contains(identity.Tombstones, tombstone => tombstone.Id == loop.Condition.Id);
        SemanticId oldAssignmentLiteralId = Assert.Single(
            assignment.Value.DescendantsAndSelf().OfType<HirLiteralExpression>()).Id;
        Assert.Contains(identity.Tombstones, tombstone => tombstone.Id == oldAssignmentLiteralId);
    }

    [Fact]
    public void Apply_RepeatedlyEditsAProducedWhileConditionThroughItsStablePlanHandle()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value, int stopAt, bool enabled)
                {
                    int result = value;
                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedLoopConditionPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticId boolType = GetTypeId(original, method.Parameters[2]);
        GraphDocument beforeGraph = new HirGraphProjector().Project(original.Hir, original.Semantics);
        GraphFunction beforeFunction = Assert.Single(beforeGraph.Functions);
        GraphLayoutDocument beforeLayout = new(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(
                beforeFunction.Id,
                beforeFunction.Nodes.Select((node, index) => new GraphNodeLayout(
                    node.Id,
                    GraphNodePlacement.Placed,
                    new GraphLayoutPoint(index * 100, index * 50),
                    new GraphLayoutSize(160, 80),
                    false)).ToArray(),
                [],
                [])],
            []);
        var loopProducer = new InsertStatementPatchPlanStep(
            "insertLoop",
            method.Body.Id,
            resultDefinition.Id,
            returnStatement.Id,
            new WhilePatchStatement(new LocalReferencePatchExpression(
                method.Parameters[2].Id,
                method.Parameters[2].Name,
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                loopProducer,
                new ReplaceProducedWhileConditionPatchPlanStep(
                    "addPositiveGuard",
                    method.Body.Id,
                    loopProducer.StepId,
                    new BinaryPatchExpression(
                        "&&",
                        new LocalReferencePatchExpression(
                            method.Parameters[2].Id,
                            method.Parameters[2].Name,
                            boolType),
                        new BinaryPatchExpression(
                            ">",
                            new LocalReferencePatchExpression(
                                resultDefinition.Id,
                                resultDefinition.Name,
                                intType),
                            new LiteralPatchExpression("0", intType),
                            boolType),
                        boolType)),
                new ReplaceProducedWhileConditionPatchPlanStep(
                    "useStopAt",
                    method.Body.Id,
                    loopProducer.StepId,
                    new BinaryPatchExpression(
                        "&&",
                        new LocalReferencePatchExpression(
                            method.Parameters[2].Id,
                            method.Parameters[2].Name,
                            boolType),
                        new BinaryPatchExpression(
                            ">",
                            new LocalReferencePatchExpression(
                                resultDefinition.Id,
                                resultDefinition.Name,
                                intType),
                            new LocalReferencePatchExpression(
                                method.Parameters[1].Id,
                                method.Parameters[1].Name,
                                intType),
                            boolType),
                        boolType)),
                new InsertLoopBodyStatementPatchPlanStep(
                    "decrementResult",
                    method.Body.Id,
                    loopProducer.StepId,
                    new LocalAssignmentPatchStatement(
                        resultDefinition.Id,
                        resultDefinition.Name,
                        intType,
                        new BinaryPatchExpression(
                            "-",
                            new LocalReferencePatchExpression(
                                resultDefinition.Id,
                                resultDefinition.Name,
                                intType),
                            new LiteralPatchExpression("1", intType),
                            intType))),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("while (enabled && (result > stopAt))", patchedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("result > 0", patchedSource, StringComparison.Ordinal);
        Assert.Contains("result = result - 1;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirWhileStatement insertedLoop = Assert.IsType<HirWhileStatement>(changedMethod.Body.Statements[1]);
        HirAssignmentExpression insertedAssignment = Assert.IsType<HirAssignmentExpression>(
            Assert.IsType<HirExpressionStatement>(Assert.Single(insertedLoop.Body.Statements)).Expression);
        Assert.Equal(method.Id, changedMethod.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(resultDefinition.Id, changedMethod.Body.Statements[0].Id);
        Assert.Equal(returnStatement.Id, changedMethod.Body.Statements[2].Id);
        Assert.Equal(
            resultDefinition.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == insertedAssignment.Target.Id).ReferencedSymbolId);

        GraphDocument afterGraph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(afterGraph).Functions);
        GraphControlFlowLoop loopFlow = Assert.Single(flow.Loops);
        Assert.True(flow.IsReducible);
        Assert.Equal(insertedLoop.Id, loopFlow.LoopNodeId);
        Assert.Equal(insertedAssignment.Id, loopFlow.BodyEntryNodeId);
        Assert.Equal(returnStatement.Id, loopFlow.ExitNodeId);
        Assert.NotEmpty(loopFlow.BackEdgeArcIds);

        GraphLayoutDocument synchronized = new GraphLayoutSynchronizer().Synchronize(
            afterGraph,
            beforeLayout);
        GraphFunctionLayout synchronizedFunction = Assert.Single(synchronized.Functions);
        HashSet<SemanticId> beforeNodeIds = beforeFunction.Nodes
            .Select(static node => node.Id)
            .ToHashSet();
        Assert.All(
            synchronizedFunction.Nodes.Where(node => beforeNodeIds.Contains(node.NodeId)),
            node => Assert.Equal(GraphNodePlacement.Placed, node.Placement));
        Assert.All(
            synchronizedFunction.Nodes.Where(node => !beforeNodeIds.Contains(node.NodeId)),
            node => Assert.Equal(GraphNodePlacement.Unplaced, node.Placement));
        Assert.Empty(synchronized.OrphanedNodes);
    }

    [Fact]
    public void Apply_ConstructsDoWhileBodyAndEditsItsProducedConditionHandle()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value, bool enabled)
                {
                    int result = value;
                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedDoWhilePlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var loopProducer = new InsertStatementPatchPlanStep(
            "insertDoLoop",
            method.Body.Id,
            resultDefinition.Id,
            returnStatement.Id,
            new DoWhilePatchStatement(new LocalReferencePatchExpression(
                method.Parameters[1].Id,
                method.Parameters[1].Name,
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                loopProducer,
                new ReplaceProducedDoWhileConditionPatchPlanStep(
                    "guardDoLoop",
                    method.Body.Id,
                    loopProducer.StepId,
                    new BinaryPatchExpression(
                        "&&",
                        new LocalReferencePatchExpression(
                            method.Parameters[1].Id,
                            method.Parameters[1].Name,
                            boolType),
                        new BinaryPatchExpression(
                            ">",
                            new LocalReferencePatchExpression(
                                resultDefinition.Id,
                                resultDefinition.Name,
                                intType),
                            new LiteralPatchExpression("0", intType),
                            boolType),
                        boolType)),
                new InsertLoopBodyStatementPatchPlanStep(
                    "decrementResult",
                    method.Body.Id,
                    loopProducer.StepId,
                    new LocalAssignmentPatchStatement(
                        resultDefinition.Id,
                        resultDefinition.Name,
                        intType,
                        new BinaryPatchExpression(
                            "-",
                            new LocalReferencePatchExpression(
                                resultDefinition.Id,
                                resultDefinition.Name,
                                intType),
                            new LiteralPatchExpression("1", intType),
                            intType))),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("do", patchedSource, StringComparison.Ordinal);
        Assert.Contains("result = result - 1;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("while (enabled && (result > 0));", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirDoWhileStatement insertedLoop = Assert.IsType<HirDoWhileStatement>(
            changedMethod.Body.Statements[1]);
        Assert.Single(insertedLoop.Body.Statements);
        Assert.Equal(returnStatement.Id, changedMethod.Body.Statements[2].Id);
        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphNode graphLoop = Assert.Single(
            Assert.Single(graph.Functions).Nodes,
            node => node.Id == insertedLoop.Id);
        Assert.Equal("doWhile", graphLoop.Properties["loopForm"]);
    }

    [Fact]
    public void Apply_RollsBackProducedLoopWhenItsConditionHandleReferencesAMissingLocal()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value, bool enabled)
                {
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedLoopConditionRollback.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var loopProducer = new InsertStatementPatchPlanStep(
            "insertLoop",
            method.Body.Id,
            null,
            returnStatement.Id,
            new WhilePatchStatement(new LocalReferencePatchExpression(
                method.Parameters[1].Id,
                method.Parameters[1].Name,
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                loopProducer,
                new ReplaceProducedWhileConditionPatchPlanStep(
                    "invalidCondition",
                    method.Body.Id,
                    loopProducer.StepId,
                    new LocalReferencePatchExpression(
                        SemanticId.FromKey("missing-condition-local"),
                        "missingCondition",
                        boolType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.ValueSourceNotAvailable);
    }

    [Fact]
    public void Apply_RollsBackGeneratedLoopWhenALaterBodyStatementHasAStaleType()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value, bool enabled)
                {
                    int result = value;
                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/LoopBuildRollback.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var loop = new InsertStatementPatchPlanStep(
            "insertLoop",
            method.Body.Id,
            resultDefinition.Id,
            returnStatement.Id,
            new WhilePatchStatement(new LiteralPatchExpression("true", boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                loop,
                new InsertLoopBodyStatementPatchPlanStep(
                    "invalidAssignment",
                    method.Body.Id,
                    loop.StepId,
                    new LocalAssignmentPatchStatement(
                        resultDefinition.Id,
                        resultDefinition.Name,
                        boolType,
                        new LiteralPatchExpression("false", boolType))),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
        Assert.NotEqual(intType, boolType);
    }

    [Fact]
    public void Apply_ResolvesNestedLoopBodiesAndBindsTransfersToTheNearestLoop()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value, bool outerEnabled, bool innerEnabled)
                {
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/NestedLoopBuildPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var outerLoop = new InsertStatementPatchPlanStep(
            "insertOuterLoop",
            method.Body.Id,
            null,
            returnStatement.Id,
            new WhilePatchStatement(new LocalReferencePatchExpression(
                method.Parameters[1].Id,
                method.Parameters[1].Name,
                boolType)));
        var innerLoop = new InsertLoopBodyStatementPatchPlanStep(
            "insertInnerLoop",
            method.Body.Id,
            outerLoop.StepId,
            new WhilePatchStatement(new LocalReferencePatchExpression(
                method.Parameters[2].Id,
                method.Parameters[2].Name,
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                outerLoop,
                innerLoop,
                new InsertLoopBodyStatementPatchPlanStep(
                    "breakInnerLoop",
                    method.Body.Id,
                    innerLoop.StepId,
                    new BreakPatchStatement()),
                new InsertLoopBodyStatementPatchPlanStep(
                    "continueOuterLoop",
                    method.Body.Id,
                    outerLoop.StepId,
                    new ContinuePatchStatement()),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("while (outerEnabled)", patchedSource, StringComparison.Ordinal);
        Assert.Contains("while (innerEnabled)", patchedSource, StringComparison.Ordinal);
        Assert.Contains("break;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("continue;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirWhileStatement changedOuter = Assert.IsType<HirWhileStatement>(changedMethod.Body.Statements[0]);
        HirWhileStatement changedInner = Assert.IsType<HirWhileStatement>(changedOuter.Body.Statements[0]);
        HirBreakStatement innerBreak = Assert.IsType<HirBreakStatement>(
            Assert.Single(changedInner.Body.Statements));
        HirContinueStatement outerContinue = Assert.IsType<HirContinueStatement>(
            changedOuter.Body.Statements[1]);
        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph).Functions);
        GraphControlFlowLoop outerFlow = Assert.Single(
            flow.Loops,
            loop => loop.LoopNodeId == changedOuter.Id);
        GraphControlFlowLoop innerFlow = Assert.Single(
            flow.Loops,
            loop => loop.LoopNodeId == changedInner.Id);
        Assert.Contains(innerBreak.Id, innerFlow.BreakNodeIds);
        Assert.DoesNotContain(innerBreak.Id, outerFlow.BreakNodeIds);
        Assert.Contains(outerContinue.Id, outerFlow.ContinueNodeIds);
        Assert.DoesNotContain(outerContinue.Id, innerFlow.ContinueNodeIds);
        Assert.True(flow.IsReducible);
        Assert.Empty(Assert.Single(result.Analysis.IdentitySidecars).Tombstones);
    }

    [Fact]
    public void Apply_ReplacesExistingGeneralExpressionEndpointsAtomically()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value, bool enabled)
                {
                    int result = value;
                    if (enabled)
                    {
                        return result;
                    }

                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/DirectGeneralEndpointPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement local = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirIfStatement branch = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        HirReturnStatement branchReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(branch.Then.Statements));
        HirExpression initializer = Assert.IsAssignableFrom<HirExpression>(local.Initializer);
        HirExpression returnValue = Assert.IsAssignableFrom<HirExpression>(branchReturn.Expression);
        SemanticId intType = GetTypeId(original, local);
        SemanticId boolType = GetTypeId(original, branch.Condition);
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new ReplaceLocalInitializerPatchPlanStep(
                    "editInitializer",
                    local.Id,
                    local.Name,
                    initializer.Id,
                    intType,
                    new LiteralPatchExpression("2", intType)),
                new ReplaceIfConditionPatchPlanStep(
                    "editIf",
                    branch.Id,
                    branch.Condition.Id,
                    boolType,
                    new LiteralPatchExpression("false", boolType)),
                new ReplaceReturnValuePatchPlanStep(
                    "editReturn",
                    branchReturn.Id,
                    returnValue.Id,
                    intType,
                    new BinaryPatchExpression(
                        "+",
                        new LocalReferencePatchExpression(local.Id, local.Name, intType),
                        new LiteralPatchExpression("1", intType),
                        intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("int result = 2;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("if (false)", patchedSource, StringComparison.Ordinal);
        Assert.Contains("return result + 1;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement changedLocal = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirIfStatement changedBranch = Assert.IsType<HirIfStatement>(changedMethod.Body.Statements[1]);
        HirReturnStatement changedReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(changedBranch.Then.Statements));
        Assert.Equal(local.Id, changedLocal.Id);
        Assert.Equal(branch.Id, changedBranch.Id);
        Assert.Equal(branch.Then.Id, changedBranch.Then.Id);
        Assert.Equal(branchReturn.Id, changedReturn.Id);
    }

    [Fact]
    public void Apply_EditsProducedLocalIfAndReturnEndpointsWithoutPredictingIds()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value, int stopAt, bool enabled)
                {
                    int result = value;
                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedGeneralEndpointPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement originalReturn = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticId boolType = GetTypeId(original, method.Parameters[2]);
        GraphDocument beforeGraph = new HirGraphProjector().Project(original.Hir, original.Semantics);
        GraphFunction beforeFunction = Assert.Single(beforeGraph.Functions);
        GraphLayoutDocument beforeLayout = new(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(
                beforeFunction.Id,
                beforeFunction.Nodes.Select((node, index) => new GraphNodeLayout(
                    node.Id,
                    GraphNodePlacement.Placed,
                    new GraphLayoutPoint(index * 100, index * 50),
                    new GraphLayoutSize(160, 80),
                    false)).ToArray(),
                [],
                [])],
            []);
        var localProducer = new InsertStatementPatchPlanStep(
            "insertOffset",
            method.Body.Id,
            resultDefinition.Id,
            originalReturn.Id,
            new LocalLiteralDeclarationPatchStatement("offset", "int", "1", intType));
        var ifProducer = new InsertStatementPatchPlanStep(
            "insertGuard",
            method.Body.Id,
            resultDefinition.Id,
            originalReturn.Id,
            new IfPatchStatement(new LocalReferencePatchExpression(
                method.Parameters[2].Id,
                method.Parameters[2].Name,
                boolType)));
        var returnProducer = new InsertBranchStatementPatchPlanStep(
            "insertGuardReturn",
            method.Body.Id,
            ifProducer.StepId,
            IfBranchKind.Then,
            new ReturnPatchStatement(new LiteralPatchExpression("0", intType), intType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                localProducer,
                new ReplaceProducedLocalInitializerPatchPlanStep(
                    "editOffset",
                    method.Body.Id,
                    localProducer.StepId,
                    new BinaryPatchExpression(
                        "+",
                        new LocalReferencePatchExpression(
                            method.Parameters[0].Id,
                            method.Parameters[0].Name,
                            intType),
                        new LiteralPatchExpression("2", intType),
                        intType)),
                ifProducer,
                new ReplaceProducedIfConditionPatchPlanStep(
                    "editGuard",
                    method.Body.Id,
                    ifProducer.StepId,
                    new BinaryPatchExpression(
                        "&&",
                        new LocalReferencePatchExpression(
                            method.Parameters[2].Id,
                            method.Parameters[2].Name,
                            boolType),
                        new BinaryPatchExpression(
                            ">",
                            new ProducedLocalReferencePatchExpression(
                                localProducer.StepId,
                                "offset",
                                intType),
                            new LocalReferencePatchExpression(
                                method.Parameters[1].Id,
                                method.Parameters[1].Name,
                                intType),
                            boolType),
                        boolType)),
                returnProducer,
                new ReplaceProducedReturnValuePatchPlanStep(
                    "editGuardReturn",
                    method.Body.Id,
                    returnProducer.StepId,
                    new BinaryPatchExpression(
                        "+",
                        new LocalReferencePatchExpression(
                            resultDefinition.Id,
                            resultDefinition.Name,
                            intType),
                        new ProducedLocalReferencePatchExpression(
                            localProducer.StepId,
                            "offset",
                            intType),
                        intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("int offset = value + 2;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("if (enabled && (offset > stopAt))", patchedSource, StringComparison.Ordinal);
        Assert.Contains("return result + offset;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement producedLocal = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[1]);
        HirIfStatement producedIf = Assert.IsType<HirIfStatement>(changedMethod.Body.Statements[2]);
        HirReturnStatement producedReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(producedIf.Then.Statements));
        Assert.Equal(resultDefinition.Id, changedMethod.Body.Statements[0].Id);
        Assert.Equal(originalReturn.Id, changedMethod.Body.Statements[3].Id);
        Assert.Equal("offset", producedLocal.Name);
        Assert.IsType<HirBinaryExpression>(producedLocal.Initializer);
        Assert.IsType<HirBinaryExpression>(producedIf.Condition);
        Assert.IsType<HirBinaryExpression>(producedReturn.Expression);

        GraphDocument afterGraph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(afterGraph).Functions);
        GraphControlFlowBranch branchFlow = Assert.Single(flow.Branches);
        Assert.True(flow.IsReducible);
        Assert.Equal(producedIf.Id, branchFlow.BranchNodeId);
        Assert.True(branchFlow.ThenExitsFunction);
        GraphLayoutDocument synchronized = new GraphLayoutSynchronizer().Synchronize(
            afterGraph,
            beforeLayout);
        GraphFunctionLayout synchronizedFunction = Assert.Single(synchronized.Functions);
        HashSet<SemanticId> beforeNodeIds = beforeFunction.Nodes
            .Select(static node => node.Id)
            .ToHashSet();
        Assert.All(
            synchronizedFunction.Nodes.Where(node => beforeNodeIds.Contains(node.NodeId)),
            node => Assert.Equal(GraphNodePlacement.Placed, node.Placement));
        Assert.All(
            synchronizedFunction.Nodes.Where(node => !beforeNodeIds.Contains(node.NodeId)),
            node => Assert.Equal(GraphNodePlacement.Unplaced, node.Placement));
        Assert.Empty(synchronized.OrphanedNodes);
    }

    [Fact]
    public void Apply_RollsBackProducedEndpointsWhenALateReturnValueSourceIsUnavailable()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value, bool enabled)
                {
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedEndpointRollback.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement originalReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var ifProducer = new InsertStatementPatchPlanStep(
            "insertGuard",
            method.Body.Id,
            null,
            originalReturn.Id,
            new IfPatchStatement(new LocalReferencePatchExpression(
                method.Parameters[1].Id,
                method.Parameters[1].Name,
                boolType)));
        var returnProducer = new InsertBranchStatementPatchPlanStep(
            "insertGuardReturn",
            method.Body.Id,
            ifProducer.StepId,
            IfBranchKind.Then,
            new ReturnPatchStatement(new LiteralPatchExpression("0", intType), intType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                ifProducer,
                new ReplaceProducedIfConditionPatchPlanStep(
                    "editGuard",
                    method.Body.Id,
                    ifProducer.StepId,
                    new LiteralPatchExpression("true", boolType)),
                returnProducer,
                new ReplaceProducedReturnValuePatchPlanStep(
                    "invalidReturnValue",
                    method.Body.Id,
                    returnProducer.StepId,
                    new LocalReferencePatchExpression(
                        SemanticId.FromKey("missing-produced-return-source"),
                        "missingValue",
                        intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.ValueSourceNotAvailable);
    }

    [Fact]
    public void Apply_ConsumesProducedLocalInsideRootBranchAndExistingReturnExpressions()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value, int stopAt, bool enabled)
                {
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedValueExpressions.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement fallbackReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirExpression fallbackValue = Assert.IsAssignableFrom<HirExpression>(fallbackReturn.Expression);
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticId boolType = GetTypeId(original, method.Parameters[2]);
        var localProducer = new InsertStatementPatchPlanStep(
            "insertOffset",
            method.Body.Id,
            null,
            fallbackReturn.Id,
            new LocalLiteralDeclarationPatchStatement("offset", "int", "2", intType));
        PatchExpression producedOffset = new ProducedLocalReferencePatchExpression(
            localProducer.StepId,
            "offset",
            intType);
        var ifProducer = new InsertStatementPatchPlanStep(
            "insertGuard",
            method.Body.Id,
            null,
            fallbackReturn.Id,
            new IfPatchStatement(new BinaryPatchExpression(
                "&&",
                new LocalReferencePatchExpression(
                    method.Parameters[2].Id,
                    method.Parameters[2].Name,
                    boolType),
                new BinaryPatchExpression(
                    ">",
                    producedOffset,
                    new LocalReferencePatchExpression(
                        method.Parameters[1].Id,
                        method.Parameters[1].Name,
                        intType),
                    boolType),
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                localProducer,
                ifProducer,
                new InsertBranchStatementPatchPlanStep(
                    "insertGuardReturn",
                    method.Body.Id,
                    ifProducer.StepId,
                    IfBranchKind.Then,
                    new ReturnPatchStatement(
                        new BinaryPatchExpression(
                            "+",
                            new LocalReferencePatchExpression(
                                method.Parameters[0].Id,
                                method.Parameters[0].Name,
                                intType),
                            producedOffset,
                            intType),
                        intType)),
                new ReplaceReturnValuePatchPlanStep(
                    "editFallback",
                    fallbackReturn.Id,
                    fallbackValue.Id,
                    intType,
                    new BinaryPatchExpression(
                        "-",
                        new LocalReferencePatchExpression(
                            method.Parameters[0].Id,
                            method.Parameters[0].Name,
                            intType),
                        producedOffset,
                        intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("int offset = 2;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("if (enabled && (offset > stopAt))", patchedSource, StringComparison.Ordinal);
        Assert.Contains("return value + offset;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("return value - offset;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement producedLocal = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirIfStatement producedIf = Assert.IsType<HirIfStatement>(changedMethod.Body.Statements[1]);
        HirReturnStatement producedReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(producedIf.Then.Statements));
        HirReturnStatement changedFallback = Assert.IsType<HirReturnStatement>(
            changedMethod.Body.Statements[2]);
        Assert.Equal("offset", producedLocal.Name);
        Assert.Equal(fallbackReturn.Id, changedFallback.Id);
        Assert.IsType<HirBinaryExpression>(producedIf.Condition);
        Assert.IsType<HirBinaryExpression>(producedReturn.Expression);
        Assert.IsType<HirBinaryExpression>(changedFallback.Expression);
        Assert.Equal(5, Assert.Single(result.Analysis.IdentitySidecars).Generation);

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphFunction function = Assert.Single(graph.Functions);
        Assert.Contains(function.Nodes, node => node.Id == producedLocal.Id && node.Kind == "variable");
        Assert.Contains(function.Nodes, node => node.Id == producedIf.Id && node.Kind == "branch");
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph).Functions);
        Assert.True(flow.IsReducible);
        Assert.True(Assert.Single(flow.Branches).ThenExitsFunction);
    }

    [Fact]
    public void Apply_RollsBackWhenProducedBranchLocalIsConsumedFromSiblingBranch()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value, bool enabled)
                {
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedValueBranchEscape.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement fallbackReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var ifProducer = new InsertStatementPatchPlanStep(
            "insertGuard",
            method.Body.Id,
            null,
            fallbackReturn.Id,
            new IfPatchStatement(new LocalReferencePatchExpression(
                method.Parameters[1].Id,
                method.Parameters[1].Name,
                boolType)));
        var branchLocalProducer = new InsertBranchStatementPatchPlanStep(
            "insertThenOffset",
            method.Body.Id,
            ifProducer.StepId,
            IfBranchKind.Then,
            new LocalLiteralDeclarationPatchStatement("offset", "int", "2", intType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                ifProducer,
                branchLocalProducer,
                new InsertBranchStatementPatchPlanStep(
                    "insertElseReturn",
                    method.Body.Id,
                    ifProducer.StepId,
                    IfBranchKind.Else,
                    new ReturnPatchStatement(
                        new ProducedLocalReferencePatchExpression(
                            branchLocalProducer.StepId,
                            "offset",
                            intType),
                        intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.ValueSourceNotAvailable);
    }

    [Fact]
    public void Apply_RejectsProducedLocalReferenceAcrossMethodsBeforeExecutingStages()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int First(int value)
                {
                    return value;
                }

                [Visual]
                static int Second(int value)
                {
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedValueCrossMethod.cs");
        HirTypeDeclaration type = Assert.Single(original.Hir!.Types);
        HirMethodDeclaration first = type.Methods.Single(static method => method.Name == "First");
        HirMethodDeclaration second = type.Methods.Single(static method => method.Name == "Second");
        HirReturnStatement firstReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(first.Body.Statements));
        HirReturnStatement secondReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(second.Body.Statements));
        SemanticId intType = GetTypeId(original, first.Parameters[0]);
        var localProducer = new InsertStatementPatchPlanStep(
            "insertFirstOffset",
            first.Body.Id,
            null,
            firstReturn.Id,
            new LocalLiteralDeclarationPatchStatement("offset", "int", "2", intType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                localProducer,
                new ReplaceReturnValuePatchPlanStep(
                    "editSecondReturn",
                    secondReturn.Id,
                    secondReturn.Expression!.Id,
                    intType,
                    new ProducedLocalReferencePatchExpression(
                        localProducer.StepId,
                        "offset",
                        intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidPatchPlan);
    }

    [Fact]
    public void Apply_ConnectsProducedLocalToNestedExpressionInputPort()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return (value + 1) * 3;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedNestedInput.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirBinaryExpression outer = Assert.IsType<HirBinaryExpression>(returnStatement.Expression);
        HirBinaryExpression addition = Assert.IsType<HirBinaryExpression>(outer.Left);
        HirExpression oldInput = addition.Right;
        SemanticId intType = GetTypeId(original, addition);
        var producer = new InsertStatementPatchPlanStep(
            "insertOffset",
            method.Body.Id,
            null,
            returnStatement.Id,
            new LocalLiteralDeclarationPatchStatement("offset", "int", "2", intType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                producer,
                new ReplaceExpressionInputPatchPlanStep(
                    "connectOffset",
                    addition.Id,
                    "right",
                    oldInput.Id,
                    intType,
                    intType,
                    new ProducedLocalReferencePatchExpression(
                        producer.StepId,
                        "offset",
                        intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "int offset = 2;\n        return (value + offset) * 3;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement offset = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirReturnStatement changedReturn = Assert.IsType<HirReturnStatement>(
            changedMethod.Body.Statements[1]);
        HirBinaryExpression changedOuter = Assert.IsType<HirBinaryExpression>(changedReturn.Expression);
        HirBinaryExpression changedAddition = Assert.IsType<HirBinaryExpression>(changedOuter.Left);
        HirIdentifierExpression connectedOffset = Assert.IsType<HirIdentifierExpression>(
            changedAddition.Right);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(returnStatement.Id, changedReturn.Id);
        Assert.Equal(outer.Id, changedOuter.Id);
        Assert.Equal(addition.Id, changedAddition.Id);
        Assert.Equal(
            offset.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == connectedOffset.Id).ReferencedSymbolId);
        IdentitySidecarDocument sidecar = Assert.Single(result.Analysis.IdentitySidecars);
        Assert.Equal(3, sidecar.Generation);
        Assert.Contains(sidecar.Tombstones, tombstone => tombstone.Id == oldInput.Id);
        GraphFunction function = Assert.Single(new HirGraphProjector()
            .Project(result.Analysis.Hir, result.Analysis.Semantics)
            .Functions);
        Assert.Contains(
            function.Edges,
            edge => edge.FromNode == connectedOffset.Id &&
                edge.ToNode == changedAddition.Id &&
                edge.ToPort == "right");
    }

    [Fact]
    public void Apply_RollsBackProducedLocalWhenNestedInputPortFailsAtItsStage()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return value + 1;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/RollbackNestedInput.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirBinaryExpression addition = Assert.IsType<HirBinaryExpression>(returnStatement.Expression);
        SemanticId intType = GetTypeId(original, addition);
        var producer = new InsertStatementPatchPlanStep(
            "insertOffset",
            method.Body.Id,
            null,
            returnStatement.Id,
            new LocalLiteralDeclarationPatchStatement("offset", "int", "2", intType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                producer,
                new ReplaceExpressionInputPatchPlanStep(
                    "connectInvalidPort",
                    addition.Id,
                    "operand",
                    addition.Right.Id,
                    intType,
                    intType,
                    new ProducedLocalReferencePatchExpression(
                        producer.StepId,
                        "offset",
                        intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidExpressionInputPort);
    }

    [Fact]
    public void Apply_ChainsProducedExpressionHandlesWithoutPredictingGeneratedIds()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedExpressionChain.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirIdentifierExpression oldValue = Assert.IsType<HirIdentifierExpression>(valueReturn.Expression);
        SemanticId intType = GetTypeId(original, oldValue);
        var replaceReturn = new ReplaceReturnValuePatchPlanStep(
            "replaceReturn",
            valueReturn.Id,
            oldValue.Id,
            intType,
            new BinaryPatchExpression(
                "+",
                new LocalReferencePatchExpression(
                    method.Parameters[0].Id,
                    method.Parameters[0].Name,
                    intType),
                new LiteralPatchExpression("1", intType),
                intType));
        var replaceGeneratedRight = new ReplaceProducedExpressionInputPatchPlanStep(
            "replaceGeneratedRight",
            valueReturn.Id,
            replaceReturn.StepId,
            [],
            "right",
            intType,
            intType,
            new BinaryPatchExpression(
                "*",
                new LocalReferencePatchExpression(
                    method.Parameters[0].Id,
                    method.Parameters[0].Name,
                    intType),
                new LiteralPatchExpression("2", intType),
                intType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                replaceReturn,
                replaceGeneratedRight,
                new ReplaceProducedExpressionInputPatchPlanStep(
                    "replaceNestedGeneratedRight",
                    valueReturn.Id,
                    replaceGeneratedRight.StepId,
                    [],
                    "right",
                    intType,
                    intType,
                    new LiteralPatchExpression("3", intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "return value + (value * 3);",
            Assert.Single(result.PatchedRequest!.Sources).Source);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirReturnStatement changedReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(changedMethod.Body.Statements));
        HirBinaryExpression addition = Assert.IsType<HirBinaryExpression>(changedReturn.Expression);
        HirBinaryExpression multiplication = Assert.IsType<HirBinaryExpression>(addition.Right);
        HirLiteralExpression three = Assert.IsType<HirLiteralExpression>(multiplication.Right);
        Assert.Equal("+", addition.Operator);
        Assert.Equal("*", multiplication.Operator);
        Assert.Equal("3", three.ValueText);
        Assert.Equal(valueReturn.Id, changedReturn.Id);
        Assert.Equal(4, Assert.Single(result.Analysis.IdentitySidecars).Generation);
        GraphFunction function = Assert.Single(new HirGraphProjector()
            .Project(result.Analysis.Hir, result.Analysis.Semantics)
            .Functions);
        Assert.Contains(
            function.Edges,
            edge => edge.FromNode == three.Id &&
                edge.ToNode == multiplication.Id &&
                edge.ToPort == "right");
    }

    [Fact]
    public void Apply_ChainsProducedHandlesThroughConditionalPorts()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Select(bool enabled, int value, int fallback)
                {
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedConditional.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirExpression oldValue = Assert.IsAssignableFrom<HirExpression>(returnStatement.Expression);
        SemanticId boolType = GetTypeId(original, method.Parameters[0]);
        SemanticId intType = GetTypeId(original, oldValue);
        var replaceReturn = new ReplaceReturnValuePatchPlanStep(
            "replaceReturn",
            returnStatement.Id,
            oldValue.Id,
            intType,
            new ConditionalPatchExpression(
                new LocalReferencePatchExpression(
                    method.Parameters[0].Id,
                    method.Parameters[0].Name,
                    boolType),
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    method.Parameters[1].Name,
                    intType),
                new LocalReferencePatchExpression(
                    method.Parameters[2].Id,
                    method.Parameters[2].Name,
                    intType),
                intType));
        var replaceGeneratedFalse = new ReplaceProducedExpressionInputPatchPlanStep(
            "replaceGeneratedFalse",
            returnStatement.Id,
            replaceReturn.StepId,
            [],
            "whenFalse",
            intType,
            intType,
            new BinaryPatchExpression(
                "+",
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    method.Parameters[1].Name,
                    intType),
                new LiteralPatchExpression("1", intType),
                intType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                replaceReturn,
                replaceGeneratedFalse,
                new ReplaceProducedExpressionInputPatchPlanStep(
                    "replaceGeneratedFalseRight",
                    returnStatement.Id,
                    replaceGeneratedFalse.StepId,
                    [],
                    "right",
                    intType,
                    intType,
                    new LiteralPatchExpression("2", intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "return enabled ? value : (value + 2);",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult analysis = Assert.IsType<CSharpAnalysisResult>(result.Analysis);
        HirCompilationUnit hir = Assert.IsType<HirCompilationUnit>(analysis.Hir);
        HirConditionalExpression conditional = Assert.Single(
            hir.DescendantsAndSelf().OfType<HirConditionalExpression>());
        HirBinaryExpression whenFalse = Assert.IsType<HirBinaryExpression>(conditional.WhenFalse);
        HirLiteralExpression two = Assert.IsType<HirLiteralExpression>(whenFalse.Right);
        Assert.Equal("2", two.ValueText);
        Assert.Equal(returnStatement.Id, Assert.Single(
            hir.DescendantsAndSelf().OfType<HirReturnStatement>()).Id);
        Assert.Equal(4, Assert.Single(analysis.IdentitySidecars).Generation);
        GraphFunction graph = Assert.Single(new HirGraphProjector()
            .Project(hir, analysis.Semantics)
            .Functions);
        Assert.Contains(
            graph.Edges,
            edge => edge.FromNode == whenFalse.Id &&
                edge.ToNode == conditional.Id &&
                edge.ToPort == "whenFalse");
    }

    [Fact]
    public void Apply_ChainsProducedHandlesThroughNullCoalescingPorts()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Resolve(int? value, int fallback)
                {
                    return fallback;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedCoalesce.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirExpression oldValue = Assert.IsAssignableFrom<HirExpression>(returnStatement.Expression);
        SemanticId nullableIntType = GetTypeId(original, method.Parameters[0]);
        SemanticId intType = GetTypeId(original, method.Parameters[1]);
        var replaceReturn = new ReplaceReturnValuePatchPlanStep(
            "replaceReturn",
            returnStatement.Id,
            oldValue.Id,
            intType,
            new BinaryPatchExpression(
                "??",
                new LocalReferencePatchExpression(
                    method.Parameters[0].Id,
                    method.Parameters[0].Name,
                    nullableIntType),
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    method.Parameters[1].Name,
                    intType),
                intType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                replaceReturn,
                new ReplaceProducedExpressionInputPatchPlanStep(
                    "replaceGeneratedRight",
                    returnStatement.Id,
                    replaceReturn.StepId,
                    [],
                    "right",
                    intType,
                    intType,
                    new LiteralPatchExpression("2", intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "return value ?? 2;",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult analysis = Assert.IsType<CSharpAnalysisResult>(result.Analysis);
        HirCompilationUnit hir = Assert.IsType<HirCompilationUnit>(analysis.Hir);
        HirBinaryExpression coalesce = Assert.Single(
            hir.DescendantsAndSelf().OfType<HirBinaryExpression>(),
            static binary => binary.Operator == "??");
        Assert.Equal("2", Assert.IsType<HirLiteralExpression>(coalesce.Right).ValueText);
        Assert.Equal(returnStatement.Id, Assert.Single(
            hir.DescendantsAndSelf().OfType<HirReturnStatement>()).Id);
        Assert.Equal(3, Assert.Single(analysis.IdentitySidecars).Generation);
        GraphNode graphCoalesce = Assert.Single(
            Assert.Single(new HirGraphProjector()
                .Project(hir, analysis.Semantics)
                .Functions)
                .Nodes,
            static node => node.Kind == "binary" && node.Properties["operator"] == "??");
        Assert.Equal("whenLeftNull", graphCoalesce.Properties["rightEvaluation"]);
    }

    [Fact]
    public void Apply_ChainsProducedHandleThroughConditionalAccessReceiverPort()
    {
        const string source = """
            #nullable enable

            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int? Measure(string? primary, string? fallback)
                {
                    return null;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedConditionalAccess.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirExpression oldValue = Assert.IsAssignableFrom<HirExpression>(valueReturn.Expression);
        SemanticId nullableStringType = GetTypeId(original, method.Parameters[0]);
        SemanticId nullableIntType = GetTypeId(original, oldValue);
        var replaceReturn = new ReplaceReturnValuePatchPlanStep(
            "replaceReturn",
            valueReturn.Id,
            oldValue.Id,
            nullableIntType,
            new ConditionalMemberAccessPatchExpression(
                new LocalReferencePatchExpression(
                    method.Parameters[0].Id,
                    method.Parameters[0].Name,
                    nullableStringType),
                "Length",
                nullableIntType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                replaceReturn,
                new ReplaceProducedExpressionInputPatchPlanStep(
                    "replaceGeneratedReceiver",
                    valueReturn.Id,
                    replaceReturn.StepId,
                    [],
                    "receiver",
                    nullableStringType,
                    nullableIntType,
                    new LocalReferencePatchExpression(
                        method.Parameters[1].Id,
                        method.Parameters[1].Name,
                        nullableStringType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "return fallback?.Length;",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult analysis = Assert.IsType<CSharpAnalysisResult>(result.Analysis);
        HirCompilationUnit hir = Assert.IsType<HirCompilationUnit>(analysis.Hir);
        BoundCompilation semantics = Assert.IsType<BoundCompilation>(analysis.Semantics);
        HirConditionalAccessExpression conditionalAccess = Assert.Single(
            hir.DescendantsAndSelf().OfType<HirConditionalAccessExpression>());
        Assert.Equal(
            "fallback",
            Assert.IsType<HirIdentifierExpression>(conditionalAccess.Receiver).Name);
        Assert.Equal(
            "Length",
            Assert.IsType<HirConditionalMemberBindingExpression>(
                conditionalAccess.WhenNotNull).MemberName);
        Assert.Equal(3, Assert.Single(analysis.IdentitySidecars).Generation);
        GraphFunction graph = Assert.Single(new HirGraphProjector()
            .Project(hir, semantics)
            .Functions);
        GraphNode conditionalNode = Assert.Single(
            graph.Nodes,
            node => node.Id == conditionalAccess.Id && node.Kind == "conditionalAccess");
        Assert.Equal("whenReceiverNonNull", conditionalNode.Properties["whenNotNullEvaluation"]);
    }

    [Fact]
    public void Apply_ResolvesNestedPathInsideAnInsertedExpressionTree()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool enabled, int value)
                {
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/InsertedExpressionPath.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId boolType = GetTypeId(original, method.Parameters[0]);
        SemanticId intType = GetTypeId(original, method.Parameters[1]);
        var insertOffset = new InsertStatementPatchPlanStep(
            "insertOffset",
            method.Body.Id,
            null,
            valueReturn.Id,
            new LocalLiteralDeclarationPatchStatement("offset", "int", "2", intType));
        var insertIf = new InsertStatementPatchPlanStep(
            "insertIf",
            method.Body.Id,
            null,
            valueReturn.Id,
            new IfPatchStatement(new BinaryPatchExpression(
                "&&",
                new LocalReferencePatchExpression(
                    method.Parameters[0].Id,
                    method.Parameters[0].Name,
                    boolType),
                new BinaryPatchExpression(
                    ">",
                    new BinaryPatchExpression(
                        "+",
                        new LocalReferencePatchExpression(
                            method.Parameters[1].Id,
                            method.Parameters[1].Name,
                            intType),
                        new LiteralPatchExpression("1", intType),
                        intType),
                    new LiteralPatchExpression("0", intType),
                    boolType),
                boolType)));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                insertOffset,
                insertIf,
                new ReplaceProducedExpressionInputPatchPlanStep(
                    "connectOffsetToComparison",
                    method.Body.Id,
                    insertIf.StepId,
                    ["right", "left"],
                    "right",
                    intType,
                    intType,
                    new ProducedLocalReferencePatchExpression(
                        insertOffset.StepId,
                        "offset",
                        intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("int offset = 2;", patchedSource);
        Assert.Contains("value + offset", patchedSource);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement offset = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirIfStatement branch = Assert.IsType<HirIfStatement>(changedMethod.Body.Statements[1]);
        HirBinaryExpression conjunction = Assert.IsType<HirBinaryExpression>(branch.Condition);
        HirBinaryExpression comparison = Assert.IsType<HirBinaryExpression>(conjunction.Right);
        HirBinaryExpression addition = Assert.IsType<HirBinaryExpression>(comparison.Left);
        HirIdentifierExpression offsetUse = Assert.IsType<HirIdentifierExpression>(addition.Right);
        Assert.Equal(
            offset.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == offsetUse.Id).ReferencedSymbolId);
        Assert.Equal(4, Assert.Single(result.Analysis.IdentitySidecars).Generation);
    }

    [Fact]
    public void Apply_RollsBackWhenAProducedExpressionEditFailsAtRuntime()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/RollbackProducedExpression.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirIdentifierExpression oldValue = Assert.IsType<HirIdentifierExpression>(valueReturn.Expression);
        SemanticId intType = GetTypeId(original, oldValue);
        var producer = new ReplaceReturnValuePatchPlanStep(
            "replaceReturn",
            valueReturn.Id,
            oldValue.Id,
            intType,
            new BinaryPatchExpression(
                "+",
                new LocalReferencePatchExpression(
                    method.Parameters[0].Id,
                    method.Parameters[0].Name,
                    intType),
                new LiteralPatchExpression("1", intType),
                intType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                producer,
                new ReplaceProducedExpressionInputPatchPlanStep(
                    "connectMissingLocal",
                    valueReturn.Id,
                    producer.StepId,
                    [],
                    "right",
                    intType,
                    intType,
                    new LocalReferencePatchExpression(
                        SemanticId.FromKey("missing-local"),
                        "missing",
                        intType)),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Apply_RollsBackDirectStepsWhenAnEarlierDeletionRemovesALaterTarget()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    int unused = 1;
                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/DirectRollbackPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement unused = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirLiteralExpression literal = Assert.IsType<HirLiteralExpression>(unused.Initializer);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new DeleteStatementPatchPlanStep(
                    "deleteUnused",
                    unused.Id,
                    method.Body.Id,
                    null,
                    returnStatement.Id),
                new ReplaceLiteralPatchPlanStep("replaceDeletedLiteral", literal.Id, "1", "2"),
            ]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TargetNotFound);
    }

    [Fact]
    public void Apply_ReplacesAProducedForConditionUsingItsProducedLocal()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int count, bool enabled)
                {
                    return count;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ProducedForConditionPlan.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var authoredIndex = new ForLocalReferencePatchExpression(0, "index", intType);
        var insertFor = new InsertStatementPatchPlanStep(
            "insertFor",
            method.Body.Id,
            null,
            valueReturn.Id,
            new ForPatchStatement(
                new ForVariableDeclarationPatch(
                    "int",
                    [new ForVariableDeclaratorPatch(
                        "index",
                        new LiteralPatchExpression("0", intType),
                        intType)]),
                [],
                new BinaryPatchExpression(
                    "<",
                    authoredIndex,
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        method.Parameters[0].Name,
                        intType),
                    boolType),
                [new ForMutationPatchExpression(authoredIndex, "++", true, intType)]));
        var producedIndex = new ProducedLocalReferencePatchExpression(
            insertFor.StepId,
            "index",
            intType);
        var replaceCondition = new ReplaceProducedForConditionPatchPlanStep(
            "tightenForCondition",
            method.Body.Id,
            insertFor.StepId,
            new BinaryPatchExpression(
                "&&",
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    method.Parameters[1].Name,
                    boolType),
                new BinaryPatchExpression(
                    "<",
                    producedIndex,
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        method.Parameters[0].Name,
                        intType),
                    boolType),
                boolType));
        SemanticPatchPlanDocument plan = SemanticPatchPlanDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [insertFor, replaceCondition]);

        CSharpPatchResult result = new CSharpPatchPlanApplier().Apply(request, plan);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "for (int index = 0; enabled && (index < count); index++)",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(
            Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirForStatement inserted = Assert.IsType<HirForStatement>(changedMethod.Body.Statements[0]);
        HirForVariableDeclarator insertedIndex = Assert.Single(inserted.Declaration!.Variables);
        HirBinaryExpression condition = Assert.IsType<HirBinaryExpression>(inserted.Condition);
        HirBinaryExpression comparison = Assert.IsType<HirBinaryExpression>(condition.Right);
        HirIdentifierExpression indexReference = Assert.IsType<HirIdentifierExpression>(comparison.Left);
        Assert.Equal(
            insertedIndex.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == indexReference.Id).ReferencedSymbolId);
        Assert.Equal(valueReturn.Id, changedMethod.Body.Statements[1].Id);
        Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(
                new HirGraphProjector().Project(
                    result.Analysis.Hir,
                    result.Analysis.Semantics)).Functions[0].Loops);
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
