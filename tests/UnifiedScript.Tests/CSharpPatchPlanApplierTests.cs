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
