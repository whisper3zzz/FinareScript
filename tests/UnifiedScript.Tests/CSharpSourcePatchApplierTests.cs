using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.Graph;
using UnifiedScript.Semantics;

namespace UnifiedScript.Tests;

public sealed class CSharpSourcePatchApplierTests
{
    private const string Source = """
        using UnifiedScript;

        namespace Game;

        public static class Calculator
        {
            [Visual]
            public static int Scale(int value)
            {
                // This comment and the surrounding formatting must survive graph edits.
                return value + 1; // keep trailing trivia
            }
        }
        """;

    [Fact]
    public void Apply_ReplacesLiteralPreservesTriviaAndReanalyzesGraph()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        (CSharpAnalysisResult original, HirLiteralExpression literal) = AnalyzeLiteral(request);
        SemanticPatchDocument patch = CreatePatch(original, literal, "42");

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("return value + 42; // keep trailing trivia", patchedSource);
        Assert.Contains("// This comment and the surrounding formatting must survive graph edits.", patchedSource);
        Assert.NotEqual(original.Hir!.Id, result.Analysis!.Hir!.Id);
        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        Assert.Equal(result.Analysis.Semantics!.ConfigurationId, graph.SemanticConfigurationId);
        GraphNode graphLiteral = Assert.Single(
            Assert.Single(graph.Functions)
            .Nodes,
            static node => node.Kind == "literal");
        Assert.Equal("42", graphLiteral.Properties["value"]);
    }

    [Fact]
    public void Apply_RejectsStaleSourceCompilationBaseline()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        (_, HirLiteralExpression literal) = AnalyzeLiteral(request);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            SemanticId.FromKey("stale-source"),
            AnalyzeConfiguration(request),
            [new ReplaceLiteralPatchOperation(literal.Id, literal.ValueText, "2")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.BaselineMismatch);
    }

    [Fact]
    public void Apply_RejectsChangedLiteralPrecondition()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        (CSharpAnalysisResult original, HirLiteralExpression literal) = AnalyzeLiteral(request);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceLiteralPatchOperation(literal.Id, "999", "2")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
    }

    [Fact]
    public void Apply_RejectsChangedSemanticConfiguration()
    {
        CSharpCompilationRequest originalRequest = CSharpCompilationRequest.Single(
            Source,
            "Game/Calculator.cs");
        (CSharpAnalysisResult original, HirLiteralExpression literal) = AnalyzeLiteral(originalRequest);
        MethodEffectCatalog changedCatalog = MethodEffectCatalog.CreateDefault().WithOverrides(
        [
            new MethodEffectRule(
                "patch.configuration-change",
                "*",
                "Game.Api",
                "*",
                MethodEffects.None),
        ]);
        CSharpCompilationRequest changedRequest = CSharpCompilationRequest.Single(
            Source,
            "Game/Calculator.cs",
            changedCatalog);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(
            changedRequest,
            CreatePatch(original, literal, "2"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.ConfigurationMismatch);
    }

    [Fact]
    public void Apply_RejectsNonLiteralTarget()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirIdentifierExpression identifier = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirIdentifierExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceLiteralPatchOperation(identifier.Id, identifier.Name, "2")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TargetKindMismatch);
    }

    [Fact]
    public void Apply_RejectsReplacementThatChangesGraphTopology()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        (CSharpAnalysisResult original, HirLiteralExpression literal) = AnalyzeLiteral(request);
        SemanticPatchDocument patch = CreatePatch(original, literal, "1 + 2");

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidReplacement);
    }

    [Fact]
    public void Apply_RejectsSemanticallyInvalidResultAtomically()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        (CSharpAnalysisResult original, HirLiteralExpression literal) = AnalyzeLiteral(request);
        SemanticPatchDocument patch = CreatePatch(original, literal, "\"not an integer\"");

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code.StartsWith("CS", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_ChangesOnlyTheTargetedDocumentInMultiFileRequest()
    {
        const string supportSource = """
            namespace Game;

            public static class Support
            {
                public static int Identity(int value) => value;
            }
            """;
        var request = new CSharpCompilationRequest(
            "Patch.MultiFile.Tests",
            [
                new CSharpSourceDocument("Game/Support.cs", supportSource),
                new CSharpSourceDocument("Game/Calculator.cs", Source),
            ]);
        (CSharpAnalysisResult original, HirLiteralExpression literal) = AnalyzeLiteral(request);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(
            request,
            CreatePatch(original, literal, "7"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        CSharpSourceDocument patchedSupport = Assert.Single(
            result.PatchedRequest!.Sources,
            static source => source.LogicalPath == "Game/Support.cs");
        CSharpSourceDocument patchedCalculator = Assert.Single(
            result.PatchedRequest.Sources,
            static source => source.LogicalPath == "Game/Calculator.cs");
        Assert.Equal(supportSource, patchedSupport.Source);
        Assert.Contains("return value + 7;", patchedCalculator.Source);
    }

    [Fact]
    public void Apply_ChangesBinaryOperatorAndPreservesPersistentIdentity()
    {
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            Source,
            "Game/Calculator.cs");
        HirBinaryExpression binary = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirBinaryExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ChangeOperatorPatchOperation(
                binary.Id,
                "+",
                "-",
                GetTypeId(original, binary),
                GetTypeId(original, binary.Left),
                GetTypeId(original, binary.Right))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("return value - 1; // keep trailing trivia", Assert.Single(result.PatchedRequest!.Sources).Source);
        HirBinaryExpression changed = Assert.Single(
            result.Analysis!.Hir!.DescendantsAndSelf().OfType<HirBinaryExpression>());
        Assert.Equal("-", changed.Operator);
        Assert.Equal(binary.Id, changed.Id);
        Assert.Equal(
            Assert.Single(original.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet(),
            Assert.Single(result.Analysis.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet());
        Assert.Empty(Assert.Single(result.Analysis.IdentitySidecars).Tombstones);
    }

    [Fact]
    public void Apply_ChangesUnaryOperatorWithinTheSameTypeFamily()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return -value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/Unary.cs");
        HirUnaryExpression unary = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirUnaryExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ChangeOperatorPatchOperation(
                unary.Id,
                "-",
                "+",
                GetTypeId(original, unary),
                GetTypeId(original, unary.Operand))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("return +value;", Assert.Single(result.PatchedRequest!.Sources).Source);
        HirUnaryExpression changed = Assert.Single(
            result.Analysis!.Hir!.DescendantsAndSelf().OfType<HirUnaryExpression>());
        Assert.Equal("+", changed.Operator);
        Assert.Equal(unary.Id, changed.Id);
    }

    [Fact]
    public void Apply_RejectsOperatorChangeAcrossResultFamilies()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirBinaryExpression binary = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirBinaryExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ChangeOperatorPatchOperation(
                binary.Id,
                "+",
                "<",
                GetTypeId(original, binary),
                GetTypeId(original, binary.Left),
                GetTypeId(original, binary.Right))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidOperator);
    }

    [Fact]
    public void Apply_RejectsStaleOperatorTypePrecondition()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/Calculator.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirBinaryExpression binary = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirBinaryExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ChangeOperatorPatchOperation(
                binary.Id,
                "+",
                "-",
                SemanticId.FromKey("stale-result-type"),
                GetTypeId(original, binary.Left),
                GetTypeId(original, binary.Right))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
    }

    [Fact]
    public void Apply_ReconnectsPureSameTypeArgumentsAndPreservesTheirIdentities()
    {
        const string source = """
            using UnifiedScript;

            static class Api
            {
                public static int Combine(int first, int second) => (first * 10) + second;
            }

            static class Script
            {
                [Visual]
                static int Run(int first, int second)
                {
                    return Api.Combine(first, second);
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/Reconnect.cs");
        HirInvocationExpression invocation = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>());
        MethodDescriptor method = GetInvokedMethod(original, invocation);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectArgumentPatchOperation(
                invocation.Id,
                0,
                invocation.Arguments[0].Id,
                1,
                invocation.Arguments[1].Id,
                method.Parameters[0].TypeId,
                method.Parameters[1].TypeId)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("Api.Combine(second, first)", Assert.Single(result.PatchedRequest!.Sources).Source);
        HirInvocationExpression changed = Assert.Single(
            result.Analysis!.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>());
        Assert.Equal(invocation.Id, changed.Id);
        Assert.Equal(invocation.Arguments[1].Id, changed.Arguments[0].Id);
        Assert.Equal(invocation.Arguments[0].Id, changed.Arguments[1].Id);
        Assert.Equal(
            Assert.Single(original.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet(),
            Assert.Single(result.Analysis.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet());

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir!,
            result.Analysis.Semantics);
        GraphFunction function = Assert.Single(graph.Functions);
        Assert.Contains(
            function.Edges,
            edge => edge.FromNode == invocation.Arguments[1].Id &&
                edge.ToNode == invocation.Id && edge.ToPort == "arg0");
        Assert.Contains(
            function.Edges,
            edge => edge.FromNode == invocation.Arguments[0].Id &&
                edge.ToNode == invocation.Id && edge.ToPort == "arg1");
    }

    [Fact]
    public void Apply_RejectsArgumentReconnectionAcrossDifferentPortTypes()
    {
        const string source = """
            using UnifiedScript;

            static class Api
            {
                public static int Pick(int number, string text) => number;
            }

            static class Script
            {
                [Visual]
                static int Run(int number, string text)
                {
                    return Api.Pick(number, text);
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, "Game/TypedReconnect.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirInvocationExpression invocation = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>());
        MethodDescriptor method = GetInvokedMethod(original, invocation);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectArgumentPatchOperation(
                invocation.Id,
                0,
                invocation.Arguments[0].Id,
                1,
                invocation.Arguments[1].Id,
                method.Parameters[0].TypeId,
                method.Parameters[1].TypeId)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_RejectsArgumentReconnectionThatWouldReorderEffects()
    {
        const string source = """
            using UnifiedScript;

            static class Api
            {
                public static int Next() => 1;
                public static int Combine(int first, int second) => first + second;
            }

            static class Script
            {
                [Visual]
                static int Run()
                {
                    return Api.Combine(Api.Next(), Api.Next());
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, "Game/EffectReconnect.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirInvocationExpression invocation = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>(),
            static candidate => candidate.Arguments.Count == 2);
        MethodDescriptor method = GetInvokedMethod(original, invocation);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectArgumentPatchOperation(
                invocation.Id,
                0,
                invocation.Arguments[0].Id,
                1,
                invocation.Arguments[1].Id,
                method.Parameters[0].TypeId,
                method.Parameters[1].TypeId)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.EffectOrderViolation);
    }

    [Fact]
    public void Apply_RejectsOverlappingStructuralOperationsAtomically()
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
                static int Run(int first, int second)
                {
                    return Api.Combine(first + 1, second);
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, "Game/Overlap.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirInvocationExpression invocation = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>());
        HirBinaryExpression binary = Assert.IsType<HirBinaryExpression>(invocation.Arguments[0]);
        MethodDescriptor method = GetInvokedMethod(original, invocation);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [
                new ChangeOperatorPatchOperation(
                    binary.Id,
                    "+",
                    "-",
                    GetTypeId(original, binary),
                    GetTypeId(original, binary.Left),
                    GetTypeId(original, binary.Right)),
                new ReconnectArgumentPatchOperation(
                    invocation.Id,
                    0,
                    invocation.Arguments[0].Id,
                    1,
                    invocation.Arguments[1].Id,
                    method.Parameters[0].TypeId,
                    method.Parameters[1].TypeId),
            ]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.OverlappingOperations);
    }

    [Fact]
    public void Apply_InsertsLocalLiteralAndAllocatesOnlyNewStatementIdentities()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage)
                {
                    return damage;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/InsertLocal.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        SemanticId floatType = GetTypeId(original, returnStatement.Expression!);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                returnStatement.Id,
                new LocalLiteralDeclarationPatchStatement("scale", "float", "2.0f", floatType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("float scale = 2.0f;\n        return damage;", patchedSource);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement inserted = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        Assert.Equal("scale", inserted.Name);
        Assert.Equal("2.0f", Assert.IsType<HirLiteralExpression>(inserted.Initializer).ValueText);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(returnStatement.Id, changedMethod.Body.Statements[1].Id);

        IdentitySidecarDocument before = Assert.Single(original.IdentitySidecars);
        IdentitySidecarDocument after = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> afterIds = after.Entries.Select(static entry => entry.Id).ToHashSet();
        Assert.All(before.Entries, entry => Assert.Contains(entry.Id, afterIds));
        Assert.Equal(before.Entries.Count + 2, after.Entries.Count);
        Assert.Empty(after.Tombstones);

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        Assert.Contains(
            Assert.Single(graph.Functions).Nodes,
            node => node.Id == inserted.Id && node.Kind == "variable");
    }

    [Fact]
    public void Apply_InsertsCfgCheckedLocalAssignmentWithTypedPureValue()
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
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/InsertAssignment.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, resultDefinition);
        var assignment = new LocalAssignmentPatchStatement(
            resultDefinition.Id,
            resultDefinition.Name,
            intType,
            new BinaryPatchExpression(
                "+",
                new LocalReferencePatchExpression(
                    resultDefinition.Id,
                    resultDefinition.Name,
                    intType),
                new LiteralPatchExpression("1", intType),
                intType));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                resultDefinition.Id,
                returnStatement.Id,
                assignment)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "int result = seed;\n        result = result + 1;\n        return result;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirExpressionStatement assignmentStatement = Assert.IsType<HirExpressionStatement>(
            changedMethod.Body.Statements[1]);
        HirAssignmentExpression assignmentExpression = Assert.IsType<HirAssignmentExpression>(
            assignmentStatement.Expression);
        Assert.Equal("=", assignmentExpression.Operator);
        HirIdentifierExpression assignmentTarget = Assert.IsType<HirIdentifierExpression>(
            assignmentExpression.Target);
        Assert.Equal(
            resultDefinition.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == assignmentTarget.Id).ReferencedSymbolId);

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph).Functions);
        GraphControlFlowNode assignmentNode = Assert.Single(
            flow.Nodes,
            node => node.Id == assignmentExpression.Id);
        Assert.True(assignmentNode.IsReachable);
        Assert.Contains(resultDefinition.Id, assignmentNode.DominatorIds);
    }

    [Fact]
    public void Apply_InsertsAssignmentAtReachableBranchEndUsingBranchLocal()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    int result = 0;
                    if (ready)
                    {
                        int branchValue = 2;
                    }

                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/InsertBranchAssignment.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirIfStatement ifStatement = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        HirVariableDeclarationStatement branchValue = Assert.IsType<HirVariableDeclarationStatement>(
            Assert.Single(ifStatement.Then.Statements));
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                ifStatement.Then.Id,
                branchValue.Id,
                null,
                new LocalAssignmentPatchStatement(
                    resultDefinition.Id,
                    resultDefinition.Name,
                    intType,
                    new LocalReferencePatchExpression(
                        branchValue.Id,
                        branchValue.Name,
                        intType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "int branchValue = 2;\n            result = branchValue;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
        HirIfStatement changedIf = Assert.IsType<HirIfStatement>(
            Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods).Body.Statements[1]);
        Assert.IsType<HirAssignmentExpression>(
            Assert.IsType<HirExpressionStatement>(changedIf.Then.Statements[1]).Expression);
    }

    [Fact]
    public void Apply_RejectsAssignmentToBranchLocalOutsideItsCfgScope()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    if (ready)
                    {
                        int branchValue = 2;
                    }

                    return 0;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectBranchAssignmentTarget.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement ifStatement = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        HirVariableDeclarationStatement branchValue = Assert.IsType<HirVariableDeclarationStatement>(
            Assert.Single(ifStatement.Then.Statements));
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, branchValue);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                ifStatement.Id,
                returnStatement.Id,
                new LocalAssignmentPatchStatement(
                    branchValue.Id,
                    branchValue.Name,
                    intType,
                    new LiteralPatchExpression("3", intType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code ==
                SemanticPatchDiagnosticCodes.AssignmentTargetNotAvailable);
    }

    [Fact]
    public void Apply_RejectsAssignmentAfterBothBranchesTerminate()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    int result = 0;
                    if (ready)
                    {
                        return 1;
                    }
                    else
                    {
                        return 2;
                    }
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectUnreachableAssignment.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirIfStatement terminalIf = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                terminalIf.Id,
                null,
                new LocalAssignmentPatchStatement(
                    resultDefinition.Id,
                    resultDefinition.Name,
                    intType,
                    new LiteralPatchExpression("3", intType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnreachableInsertion);
    }

    [Fact]
    public void Apply_RejectsAssignmentAtEndOfBranchWhoseNestedPathsTerminate()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool outer, bool inner)
                {
                    int result = 0;
                    if (outer)
                    {
                        if (inner)
                        {
                            return 1;
                        }
                        else
                        {
                            return 2;
                        }
                    }

                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectNestedTerminalAssignment.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirIfStatement outerIf = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        HirIfStatement terminalInnerIf = Assert.IsType<HirIfStatement>(
            Assert.Single(outerIf.Then.Statements));
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                outerIf.Then.Id,
                terminalInnerIf.Id,
                null,
                new LocalAssignmentPatchStatement(
                    resultDefinition.Id,
                    resultDefinition.Name,
                    intType,
                    new LiteralPatchExpression("3", intType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnreachableInsertion);
    }

    [Fact]
    public void Apply_RejectsAssignmentWhenTargetTypePreconditionIsWrong()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int seed, bool flag)
                {
                    int result = seed;
                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectAssignmentType.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId boolType = Assert.Single(
            original.Semantics!.Types,
            static type => type.MetadataName == "System.Boolean").Id;
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                resultDefinition.Id,
                returnStatement.Id,
                new LocalAssignmentPatchStatement(
                    resultDefinition.Id,
                    resultDefinition.Name,
                    boolType,
                    new LiteralPatchExpression("true", boolType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_RejectsAssignmentWhenTargetNamePreconditionIsStale()
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
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectAssignmentName.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                resultDefinition.Id,
                returnStatement.Id,
                new LocalAssignmentPatchStatement(
                    resultDefinition.Id,
                    "renamedResult",
                    intType,
                    new LiteralPatchExpression("3", intType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code ==
                SemanticPatchDiagnosticCodes.AssignmentTargetNotAvailable);
    }

    [Fact]
    public void Apply_InsertsIntoEmptyExplicitBlockWithStableIndentation()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static void Run(int seed)
                {
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/InsertEmpty.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        SemanticId intType = GetTypeId(original, Assert.Single(method.Parameters));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                null,
                new LocalLiteralDeclarationPatchStatement("value", "int", "1", intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("{\n        int value = 1;\n    }", Assert.Single(result.PatchedRequest!.Sources).Source);
    }

    [Fact]
    public void Apply_InsertsTypedIfWithExplicitBlocksAndSynchronizesGraphLayout()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(long damage, bool disabled)
                {
                    int marker = 10;
                    return !disabled;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/InsertIf.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement marker = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId longType = GetTypeId(original, method.Parameters[0]);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        SemanticId intType = GetTypeId(original, marker);
        PatchExpression condition = new BinaryPatchExpression(
            "&&",
            new BinaryPatchExpression(
                ">",
                new CastPatchExpression(
                    "int",
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        "damage",
                        longType),
                    intType),
                new BinaryPatchExpression(
                    "+",
                    new LocalReferencePatchExpression(marker.Id, marker.Name, intType),
                    new LiteralPatchExpression("10", intType),
                    intType),
                boolType),
            new UnaryPatchExpression(
                "!",
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    "disabled",
                    boolType),
                boolType),
            boolType);
        GraphDocument originalGraph = new HirGraphProjector().Project(
            original.Hir,
            original.Semantics);
        GraphFunction originalFunction = Assert.Single(originalGraph.Functions);
        var originalLayout = new GraphLayoutDocument(
            GraphLayoutDocument.CurrentSchemaVersion,
            [new GraphFunctionLayout(
                originalFunction.Id,
                originalFunction.Nodes.Select((node, index) => new GraphNodeLayout(
                    node.Id,
                    GraphNodePlacement.Placed,
                    new GraphLayoutPoint(index * 200, index * 80),
                    new GraphLayoutSize(220, 90),
                    false)).ToArray(),
                [],
                [])],
            []);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                marker.Id,
                returnStatement.Id,
                new IfPatchStatement(condition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("if (", patchedSource);
        Assert.Contains("damage", patchedSource);
        Assert.Contains("!disabled", patchedSource);
        Assert.Contains("        {\n        }\n        else\n        {\n        }", patchedSource);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirIfStatement inserted = Assert.IsType<HirIfStatement>(changedMethod.Body.Statements[1]);
        Assert.Empty(inserted.Then.Statements);
        Assert.NotNull(inserted.Else);
        Assert.Empty(inserted.Else.Statements);
        HirBinaryExpression logicalAnd = Assert.IsType<HirBinaryExpression>(inserted.Condition);
        Assert.Equal("&&", logicalAnd.Operator);
        HirBinaryExpression comparison = Assert.IsType<HirBinaryExpression>(logicalAnd.Left);
        Assert.Equal(">", comparison.Operator);
        Assert.IsType<HirCastExpression>(comparison.Left);
        HirBinaryExpression offset = Assert.IsType<HirBinaryExpression>(comparison.Right);
        HirIdentifierExpression markerReference = Assert.IsType<HirIdentifierExpression>(offset.Left);
        Assert.Equal(
            marker.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == markerReference.Id).ReferencedSymbolId);
        Assert.IsType<HirUnaryExpression>(logicalAnd.Right);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(marker.Id, changedMethod.Body.Statements[0].Id);
        Assert.Equal(returnStatement.Id, changedMethod.Body.Statements[2].Id);

        IdentitySidecarDocument beforeIdentity = Assert.Single(original.IdentitySidecars);
        IdentitySidecarDocument afterIdentity = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> beforeIds = beforeIdentity.Entries.Select(static entry => entry.Id).ToHashSet();
        HashSet<SemanticId> afterIds = afterIdentity.Entries.Select(static entry => entry.Id).ToHashSet();
        Assert.All(beforeIds, id => Assert.Contains(id, afterIds));
        Assert.Equal(12, afterIds.Count - beforeIds.Count);
        Assert.Empty(afterIdentity.Tombstones);

        GraphDocument patchedGraph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphFunction patchedFunction = Assert.Single(patchedGraph.Functions);
        Assert.Contains(patchedFunction.Nodes, node => node.Id == inserted.Id && node.Kind == "branch");
        GraphLayoutDocument synchronized = new GraphLayoutSynchronizer().Synchronize(
            patchedGraph,
            originalLayout);
        GraphFunctionLayout synchronizedFunction = Assert.Single(synchronized.Functions);
        HashSet<SemanticId> originalNodeIds = originalFunction.Nodes
            .Select(static node => node.Id)
            .ToHashSet();
        Assert.Equal(
            originalNodeIds.Count,
            synchronizedFunction.Nodes.Count(node => originalNodeIds.Contains(node.NodeId)));
        Assert.All(
            synchronizedFunction.Nodes.Where(node => originalNodeIds.Contains(node.NodeId)),
            static node => Assert.Equal(GraphNodePlacement.Placed, node.Placement));
        GraphNodeLayout[] newLayouts = synchronizedFunction.Nodes
            .Where(node => !originalNodeIds.Contains(node.NodeId))
            .ToArray();
        Assert.Equal(10, newLayouts.Length);
        Assert.All(newLayouts, static node => Assert.Equal(GraphNodePlacement.Unplaced, node.Placement));
        Assert.Empty(synchronized.OrphanedNodes);
    }

    [Fact]
    public void Apply_RejectsIfConditionThatReferencesALaterLocal()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run()
                {
                    bool ready = true;
                    return ready;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectLaterIfSource.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement ready = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        SemanticId boolType = GetTypeId(original, ready);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                ready.Id,
                new IfPatchStatement(new LocalReferencePatchExpression(
                    ready.Id,
                    ready.Name,
                    boolType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.ValueSourceNotAvailable);
    }

    [Fact]
    public void Apply_InsertsTypedEarlyReturnAtTheEndOfAnExistingBranch()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(bool ready)
                {
                    if (ready)
                    {
                    }

                    return false;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/InsertEarlyReturn.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement existingIf = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        SemanticId boolType = GetTypeId(original, method);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                existingIf.Then.Id,
                null,
                null,
                new ReturnPatchStatement(
                    new LiteralPatchExpression("true", boolType),
                    boolType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("if (ready)\n        {\n            return true;", Assert.Single(
            result.PatchedRequest!.Sources).Source, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirIfStatement changedIf = Assert.IsType<HirIfStatement>(changedMethod.Body.Statements[0]);
        HirReturnStatement insertedReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(changedIf.Then.Statements));
        Assert.Equal("true", Assert.IsType<HirLiteralExpression>(insertedReturn.Expression).ValueText);
        Assert.Equal(existingIf.Then.Id, changedIf.Then.Id);
        Assert.Empty(Assert.Single(result.Analysis.IdentitySidecars).Tombstones);
    }

    [Fact]
    public void Apply_InsertsVoidReturnWithoutAValue()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static void Run(bool ready)
                {
                    if (ready)
                    {
                    }
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/InsertVoidReturn.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement existingIf = Assert.IsType<HirIfStatement>(Assert.Single(method.Body.Statements));
        SemanticId voidType = GetTypeId(original, method);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                existingIf.Then.Id,
                null,
                null,
                new ReturnPatchStatement(null, voidType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("{\n            return;\n        }", Assert.Single(
            result.PatchedRequest!.Sources).Source, StringComparison.Ordinal);
        HirIfStatement changedIf = Assert.IsType<HirIfStatement>(Assert.Single(
            Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods).Body.Statements));
        Assert.Null(Assert.IsType<HirReturnStatement>(Assert.Single(changedIf.Then.Statements)).Expression);
    }

    [Fact]
    public void Apply_RejectsWrongReturnTypeAndInsertionAfterReturn()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(int value)
                {
                    if (value > 0)
                    {
                    }

                    return false;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectInvalidReturn.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement existingIf = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        HirReturnStatement existingReturn = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId boolType = GetTypeId(original, method);
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticPatchDocument wrongType = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                existingIf.Then.Id,
                null,
                null,
                new ReturnPatchStatement(
                    new LiteralPatchExpression("1", intType),
                    boolType))]);
        SemanticPatchDocument unreachable = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                existingReturn.Id,
                null,
                new LocalLiteralDeclarationPatchStatement("late", "bool", "true", boolType))]);

        CSharpPatchResult wrongTypeResult = new CSharpSourcePatchApplier().Apply(request, wrongType);
        CSharpPatchResult unreachableResult = new CSharpSourcePatchApplier().Apply(request, unreachable);

        Assert.False(wrongTypeResult.Succeeded);
        Assert.Contains(
            wrongTypeResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
        Assert.False(unreachableResult.Succeeded);
        Assert.Contains(
            unreachableResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidStatement);
    }

    [Fact]
    public void Apply_RejectsReturnInsertionForAsyncTaskLikeMethod()
    {
        const string source = """
            using System.Threading.Tasks;
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static async Task<int> Run(bool ready)
                {
                    if (ready)
                    {
                    }

                    return 1;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectAsyncReturn.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement existingIf = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        SemanticId taskType = GetTypeId(original, method);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                existingIf.Then.Id,
                null,
                null,
                new ReturnPatchStatement(null, taskType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidStatement);
    }

    [Fact]
    public void Apply_RejectsUnsafeGraphAuthoredOperator()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(int value)
                {
                    return value > 0;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectUnsafeIfExpression.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirBinaryExpression comparison = Assert.IsType<HirBinaryExpression>(returnStatement.Expression);
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticId boolType = GetTypeId(original, comparison);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                returnStatement.Id,
                new IfPatchStatement(new BinaryPatchExpression(
                    ">",
                    new BinaryPatchExpression(
                        "/",
                        new LocalReferencePatchExpression(
                            method.Parameters[0].Id,
                            "value",
                            intType),
                        new LiteralPatchExpression("0", intType),
                        intType),
                    new LiteralPatchExpression("0", intType),
                    boolType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidGraphExpression);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_RejectsLoopTransferOutsideAnEnclosingLoop(bool isContinue)
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static void Run()
                {
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectUnscopedLoopTransfer.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        Assert.Empty(method.Body.Statements);
        InsertPatchStatement transfer = isContinue
            ? new ContinuePatchStatement()
            : new BreakPatchStatement();
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                null,
                transfer)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidStatement);
    }

    [Fact]
    public void Apply_RejectsWhileInsertionAfterAnAllPathsReturnBranch()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(bool ready)
                {
                    if (ready)
                    {
                        return 1;
                    }
                    else
                    {
                        return 0;
                    }
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectUnreachableWhile.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement existingIf = Assert.IsType<HirIfStatement>(Assert.Single(method.Body.Statements));
        SemanticId boolType = GetTypeId(original, method.Parameters[0]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                existingIf.Id,
                null,
                new WhilePatchStatement(new LiteralPatchExpression("true", boolType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnreachableInsertion);
    }

    [Fact]
    public void Apply_RejectsWhileConditionThatIsNotSystemBoolean()
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
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectNonBooleanWhile.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                returnStatement.Id,
                new WhilePatchStatement(new LiteralPatchExpression("1", intType)))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_RejectsInsertionWhenSiblingNameOrTypePreconditionIsStale()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(Source, "Game/InsertConflict.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, returnStatement.Expression!);
        var applier = new CSharpSourcePatchApplier();
        SemanticPatchDocument staleSibling = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                SemanticId.FromKey("stale-next-sibling"),
                new LocalLiteralDeclarationPatchStatement("scale", "int", "2", intType))]);
        SemanticPatchDocument nameConflict = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                returnStatement.Id,
                new LocalLiteralDeclarationPatchStatement("value", "int", "2", intType))]);
        SemanticPatchDocument wrongType = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                null,
                returnStatement.Id,
                new LocalLiteralDeclarationPatchStatement(
                    "scale",
                    "float",
                    "2.0f",
                    SemanticId.FromKey("stale-type")))]);

        CSharpPatchResult staleResult = applier.Apply(request, staleSibling);
        CSharpPatchResult conflictResult = applier.Apply(request, nameConflict);
        CSharpPatchResult typeResult = applier.Apply(request, wrongType);

        Assert.False(staleResult.Succeeded);
        Assert.Contains(
            staleResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.SiblingPreconditionFailed);
        Assert.False(conflictResult.Succeeded);
        Assert.Contains(
            conflictResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.NameConflict);
        Assert.False(typeResult.Succeeded);
        Assert.Contains(
            typeResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_DeletesUnusedLiteralLocalAndTombstonesItsSubtree()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage)
                {
                    // Preserve this design note after deleting the graph node.
                    float unused = 2.0f;
                    return damage;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/DeleteLocal.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement variable = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HashSet<SemanticId> removedIds = variable.DescendantsAndSelf()
            .Select(static node => node.Id)
            .ToHashSet();
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new DeleteStatementPatchOperation(
                variable.Id,
                method.Body.Id,
                null,
                returnStatement.Id)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.DoesNotContain("float unused", patchedSource);
        Assert.Contains(
            "// Preserve this design note after deleting the graph node.\n        return damage;",
            patchedSource);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        Assert.Equal(returnStatement.Id, Assert.Single(changedMethod.Body.Statements).Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);

        IdentitySidecarDocument before = Assert.Single(original.IdentitySidecars);
        IdentitySidecarDocument after = Assert.Single(result.Analysis.IdentitySidecars);
        Assert.All(removedIds, id => Assert.Contains(after.Tombstones, tombstone => tombstone.Id == id));
        Assert.Equal(
            before.Entries.Select(static entry => entry.Id).Where(id => !removedIds.Contains(id)).ToHashSet(),
            after.Entries.Select(static entry => entry.Id).ToHashSet());
    }

    [Fact]
    public void Apply_RejectsDeletingReferencedLocal()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage)
                {
                    float scale = 2.0f;
                    return damage * scale;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, "Game/DeleteReferenced.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement variable = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new DeleteStatementPatchOperation(
                variable.Id,
                method.Body.Id,
                null,
                method.Body.Statements[1].Id)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.SymbolStillReferenced);
    }

    [Fact]
    public void Apply_DeletesLastStatementAndMovesTrailingCommentBeforeCloseBrace()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static void Run()
                {
                    int unused = 1; // Preserve this trailing note.
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, "Game/DeleteLast.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirStatement statement = Assert.Single(method.Body.Statements);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new DeleteStatementPatchOperation(statement.Id, method.Body.Id, null, null)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.DoesNotContain("int unused", patchedSource);
        Assert.Contains("        // Preserve this trailing note.\n    }", patchedSource);
    }

    [Fact]
    public void Apply_DeletesPureStaticCallStatementButRejectsEffectfulCall()
    {
        const string pureSource = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float value)
                {
                    System.MathF.Abs(value);
                    return value;
                }
            }
            """;
        const string effectfulSource = """
            using UnifiedScript;

            static class Api
            {
                public static void Notify(int value) { }
            }

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    Api.Notify(value);
                    return value;
                }
            }
            """;
        CSharpPatchResult pureResult = ApplyDeleteFirstStatement(pureSource, "Game/DeletePureCall.cs");
        CSharpPatchResult effectfulResult = ApplyDeleteFirstStatement(
            effectfulSource,
            "Game/DeleteEffectfulCall.cs");

        Assert.True(pureResult.Succeeded, string.Join(Environment.NewLine, pureResult.Diagnostics));
        string purePatchedSource = Assert.Single(pureResult.PatchedRequest!.Sources).Source;
        Assert.DoesNotContain("MathF.Abs", purePatchedSource);
        Assert.Contains("{\n        return value;", purePatchedSource);
        Assert.False(effectfulResult.Succeeded);
        Assert.Null(effectfulResult.PatchedRequest);
        Assert.Contains(
            effectfulResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnsafeDeletion);
    }

    [Fact]
    public void Apply_IntroducesTemporaryForNestedPureExpressionAndPreservesExistingIdentities()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage)
                {
                    return damage * 2.0f + 1.0f;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/IntroduceTemporary.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        HirBinaryExpression target = Assert.Single(
            returnStatement.DescendantsAndSelf()
                .OfType<HirBinaryExpression>(),
            static binary => binary.Operator == "*");
        HirBinaryExpression outer = Assert.IsType<HirBinaryExpression>(returnStatement.Expression);
        SemanticId floatType = GetTypeId(original, target);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new IntroduceTemporaryPatchOperation(
                target.Id,
                returnStatement.Id,
                method.Body.Id,
                null,
                null,
                "sharedDamage",
                "float",
                floatType)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains(
            "float sharedDamage = damage * 2.0f;\n        return sharedDamage + 1.0f;",
            patchedSource);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement temporary = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirBinaryExpression movedTarget = Assert.IsType<HirBinaryExpression>(temporary.Initializer);
        HirReturnStatement changedReturn = Assert.IsType<HirReturnStatement>(changedMethod.Body.Statements[1]);
        HirBinaryExpression changedOuter = Assert.IsType<HirBinaryExpression>(changedReturn.Expression);
        HirIdentifierExpression temporaryUse = Assert.IsType<HirIdentifierExpression>(changedOuter.Left);
        Assert.Equal(target.Id, movedTarget.Id);
        Assert.Equal(outer.Id, changedOuter.Id);
        Assert.Equal(returnStatement.Id, changedReturn.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);

        BoundNodeInfo useBinding = Assert.Single(
            result.Analysis.Semantics!.Nodes,
            binding => binding.NodeId == temporaryUse.Id);
        Assert.Equal(temporary.Id, useBinding.ReferencedSymbolId);

        IdentitySidecarDocument before = Assert.Single(original.IdentitySidecars);
        IdentitySidecarDocument after = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> afterIds = after.Entries.Select(static entry => entry.Id).ToHashSet();
        Assert.All(before.Entries, entry => Assert.Contains(entry.Id, afterIds));
        Assert.Equal(before.Entries.Count + 2, after.Entries.Count);
        Assert.Empty(after.Tombstones);

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphNode graphUse = Assert.Single(
            Assert.Single(graph.Functions).Nodes,
            node => node.Id == temporaryUse.Id);
        Assert.Equal(temporary.Id.Value, graphUse.Properties["referencedSymbolId"]);
    }

    [Fact]
    public void Apply_IntroducesTemporaryBeforeLocalInitializerWithSiblingGuards()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage)
                {
                    float scaled = damage * 2.0f;
                    return scaled;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/IntroduceBeforeLocal.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement local = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirExpression target = local.Initializer!;
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new IntroduceTemporaryPatchOperation(
                target.Id,
                local.Id,
                method.Body.Id,
                null,
                returnStatement.Id,
                "sharedDamage",
                "float",
                GetTypeId(original, target))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "float sharedDamage = damage * 2.0f;\n        float scaled = sharedDamage;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
    }

    [Fact]
    public void Apply_IntroducesTemporaryForResolvedPureStaticCall()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float value)
                {
                    return System.MathF.Abs(value) + 1.0f;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/PureCallTemporary.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        HirInvocationExpression invocation = Assert.Single(
            returnStatement.DescendantsAndSelf().OfType<HirInvocationExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new IntroduceTemporaryPatchOperation(
                invocation.Id,
                returnStatement.Id,
                method.Body.Id,
                null,
                null,
                "magnitude",
                "float",
                GetTypeId(original, invocation))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "float magnitude = System.MathF.Abs(value);\n        return magnitude + 1.0f;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
    }

    [Fact]
    public void Apply_RejectsTemporaryIntroductionAcrossEffectfulEvaluationOrStaleContext()
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
                static float Run(float value)
                {
                    return Api.Read() + value;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/UnsafeTemporary.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(
            Assert.Single(original.Hir!.Types, static type => type.Name == "Script").Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        HirIdentifierExpression target = Assert.Single(
            returnStatement.DescendantsAndSelf()
                .OfType<HirIdentifierExpression>(),
            static identifier => identifier.Name == "value");
        SemanticId floatType = GetTypeId(original, target);
        IntroduceTemporaryPatchOperation operation = new(
            target.Id,
            returnStatement.Id,
            method.Body.Id,
            null,
            null,
            "sharedValue",
            "float",
            floatType);
        SemanticPatchDocument unsafePatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [operation]);
        SemanticPatchDocument stalePatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [operation with { ExpectedContainingStatementId = SemanticId.FromKey("stale-statement") }]);

        CSharpPatchResult unsafeResult = new CSharpSourcePatchApplier().Apply(request, unsafePatch);
        CSharpPatchResult staleResult = new CSharpSourcePatchApplier().Apply(request, stalePatch);

        Assert.False(unsafeResult.Succeeded);
        Assert.Contains(
            unsafeResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnsafeTemporaryIntroduction);
        Assert.False(staleResult.Succeeded);
        Assert.Contains(
            staleResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
    }

    [Fact]
    public void Apply_RejectsTemporaryIntroductionAcrossShortCircuitEvaluation()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static bool Run(bool enabled, int value, int divisor)
                {
                    return enabled && value / divisor > 1;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/ShortCircuitTemporary.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(Assert.Single(method.Body.Statements));
        HirBinaryExpression division = Assert.Single(
            returnStatement.DescendantsAndSelf().OfType<HirBinaryExpression>(),
            static binary => binary.Operator == "/");
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new IntroduceTemporaryPatchOperation(
                division.Id,
                returnStatement.Id,
                method.Body.Id,
                null,
                null,
                "quotient",
                "int",
                GetTypeId(original, division))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnsafeTemporaryIntroduction);
    }

    [Fact]
    public void Apply_ReconnectsConsumerToDominatingLocalAndTombstonesRemovedExpression()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage, float bonus)
                {
                    float sharedDamage = damage * 2.0f;
                    return bonus + 1.0f;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ReconnectValue.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirBinaryExpression outer = Assert.IsType<HirBinaryExpression>(returnStatement.Expression);
        HirLiteralExpression target = Assert.IsType<HirLiteralExpression>(outer.Right);
        HashSet<SemanticId> removedIds = target.DescendantsAndSelf()
            .Select(static node => node.Id)
            .ToHashSet();
        SemanticId floatType = GetTypeId(original, target);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                returnStatement.Id,
                method.Body.Id,
                sourceDefinition.Id,
                null,
                floatType,
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "float sharedDamage = damage * 2.0f;\n        return bonus + sharedDamage;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement changedDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirReturnStatement changedReturn = Assert.IsType<HirReturnStatement>(changedMethod.Body.Statements[1]);
        HirBinaryExpression changedOuter = Assert.IsType<HirBinaryExpression>(changedReturn.Expression);
        HirIdentifierExpression newUse = Assert.IsType<HirIdentifierExpression>(changedOuter.Right);
        Assert.Equal(sourceDefinition.Id, changedDefinition.Id);
        Assert.Equal(outer.Id, changedOuter.Id);
        Assert.Equal(returnStatement.Id, changedReturn.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);

        BoundNodeInfo useBinding = Assert.Single(
            result.Analysis.Semantics!.Nodes,
            binding => binding.NodeId == newUse.Id);
        Assert.Equal(sourceDefinition.Id, useBinding.ReferencedSymbolId);

        IdentitySidecarDocument before = Assert.Single(original.IdentitySidecars);
        IdentitySidecarDocument after = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> beforeIds = before.Entries.Select(static entry => entry.Id).ToHashSet();
        HashSet<SemanticId> afterIds = after.Entries.Select(static entry => entry.Id).ToHashSet();
        Assert.All(
            beforeIds.Where(id => !removedIds.Contains(id)),
            id => Assert.Contains(id, afterIds));
        Assert.All(
            removedIds,
            id => Assert.Contains(after.Tombstones, tombstone => tombstone.Id == id));
        SemanticId allocatedUseId = Assert.Single(afterIds, id => !beforeIds.Contains(id));
        Assert.Equal(newUse.Id, allocatedUseId);

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphNode graphUse = Assert.Single(
            Assert.Single(graph.Functions).Nodes,
            node => node.Id == newUse.Id);
        Assert.Equal("identifier", graphUse.Kind);
        Assert.Equal(sourceDefinition.Id.Value, graphUse.Properties["referencedSymbolId"]);
    }

    [Fact]
    public void Apply_ReconnectsNestedConsumerWhenRootLocalDominatesBranch()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage, bool enabled)
                {
                    float sharedDamage = damage * 2.0f;
                    if (enabled)
                    {
                        return 1.0f;
                    }

                    return sharedDamage;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/ReconnectNestedValue.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirIfStatement ifStatement = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        HirReturnStatement nestedReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(ifStatement.Then.Statements));
        HirExpression target = Assert.IsType<HirLiteralExpression>(nestedReturn.Expression);
        SemanticId floatType = GetTypeId(original, target);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                nestedReturn.Id,
                ifStatement.Then.Id,
                null,
                null,
                floatType,
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("return sharedDamage;", patchedSource);
        Assert.Equal(2, patchedSource.Split("return sharedDamage;", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Apply_RejectsValueSourceThatDoesNotDominateConsumer()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float damage, bool enabled)
                {
                    if (enabled)
                    {
                        float branchValue = damage * 2.0f;
                    }

                    return 1.0f;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectNonDominatingValue.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirIfStatement ifStatement = Assert.IsType<HirIfStatement>(method.Body.Statements[0]);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            Assert.Single(ifStatement.Then.Statements));
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirExpression target = Assert.IsType<HirLiteralExpression>(returnStatement.Expression);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                returnStatement.Id,
                method.Body.Id,
                ifStatement.Id,
                null,
                GetTypeId(original, target),
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.ValueSourceNotAvailable);
    }

    [Fact]
    public void Apply_RejectsValueReconnectionIntoUnreachableConsumer()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run()
                {
                    int sourceValue = 1;
                    return sourceValue;
                    int lateValue = 2;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectUnreachableReconnect.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirVariableDeclarationStatement lateDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[2]);
        HirExpression target = Assert.IsType<HirLiteralExpression>(lateDefinition.Initializer);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                lateDefinition.Id,
                method.Body.Id,
                returnStatement.Id,
                null,
                GetTypeId(original, target),
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.ValueSourceNotAvailable);
    }

    [Fact]
    public void Apply_RejectsValueReconnectionThatRetargetsAssignmentWrite()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run()
                {
                    int sourceValue = 1;
                    int targetValue = 2;
                    targetValue = 3;
                    return targetValue;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectAssignmentRetarget.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirVariableDeclarationStatement targetDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[1]);
        HirExpressionStatement assignmentStatement = Assert.IsType<HirExpressionStatement>(
            method.Body.Statements[2]);
        HirAssignmentExpression assignment = Assert.IsType<HirAssignmentExpression>(
            assignmentStatement.Expression);
        HirIdentifierExpression target = Assert.IsType<HirIdentifierExpression>(assignment.Target);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[3]);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                assignmentStatement.Id,
                method.Body.Id,
                targetDefinition.Id,
                returnStatement.Id,
                GetTypeId(original, target),
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnsafeValueReconnection);
    }

    [Fact]
    public void Apply_RejectsRemovingEffectfulConsumerDuringValueReconnection()
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
                static float Run(float value)
                {
                    float sharedValue = value * 2.0f;
                    return Api.Read();
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectEffectfulReconnect.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(
            Assert.Single(original.Hir!.Types, static type => type.Name == "Script").Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirExpression target = Assert.IsType<HirInvocationExpression>(returnStatement.Expression);
        SemanticId floatType = GetTypeId(original, target);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                returnStatement.Id,
                method.Body.Id,
                sourceDefinition.Id,
                null,
                floatType,
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnsafeValueReconnection);
    }

    [Fact]
    public void Apply_RejectsValueReconnectionThatWouldDiscardCommentTrivia()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float value)
                {
                    float sharedValue = value * 2.0f;
                    return (value + /* keep this rationale */ 1.0f);
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectReconnectComment.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirBinaryExpression target = Assert.IsType<HirBinaryExpression>(returnStatement.Expression);
        SemanticId floatType = GetTypeId(original, target);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                returnStatement.Id,
                method.Body.Id,
                sourceDefinition.Id,
                null,
                floatType,
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.UnsafeValueReconnection);
    }

    [Fact]
    public void Apply_RejectsValueReconnectionAcrossDifferentSemanticTypes()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static float Run(float value)
                {
                    float sharedValue = value * 2.0f;
                    int consumer = 1;
                    return sharedValue + consumer;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/RejectReconnectType.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement sourceDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirVariableDeclarationStatement consumer = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[1]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[2]);
        HirExpression target = Assert.IsType<HirLiteralExpression>(consumer.Initializer);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReconnectValuePatchOperation(
                target.Id,
                sourceDefinition.Id,
                consumer.Id,
                method.Body.Id,
                sourceDefinition.Id,
                returnStatement.Id,
                GetTypeId(original, target),
                GetTypeId(original, sourceDefinition))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Analyze_SourceCompilationIdChangesWhenSingleFileContentChanges()
    {
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(Source, "Game/Calculator.cs");
        CSharpAnalysisResult changed = new CSharpSemanticPipeline().Analyze(
            Source.Replace("value + 1", "value + 2", StringComparison.Ordinal),
            "Game/Calculator.cs");

        Assert.NotEqual(original.Hir!.Id, changed.Hir!.Id);
    }

    private static SemanticPatchDocument CreatePatch(
        CSharpAnalysisResult original,
        HirLiteralExpression literal,
        string newValue) => SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceLiteralPatchOperation(literal.Id, literal.ValueText, newValue)]);

    private static SemanticId AnalyzeConfiguration(CSharpCompilationRequest request) =>
        new CSharpSemanticPipeline().Analyze(request).Semantics!.ConfigurationId;

    private static SemanticId GetTypeId(CSharpAnalysisResult analysis, HirNode node) =>
        Assert.Single(analysis.Semantics!.Nodes, candidate => candidate.NodeId == node.Id).TypeId!.Value;

    private static MethodDescriptor GetInvokedMethod(
        CSharpAnalysisResult analysis,
        HirInvocationExpression invocation)
    {
        BoundNodeInfo binding = Assert.Single(
            analysis.Semantics!.Nodes,
            candidate => candidate.NodeId == invocation.Id);
        return Assert.Single(
            analysis.Semantics.Methods,
            method => method.Id == binding.InvokedMethodId);
    }

    private static CSharpPatchResult ApplyDeleteFirstStatement(string source, string path)
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(source, path);
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirStatement target = method.Body.Statements[0];
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new DeleteStatementPatchOperation(
                target.Id,
                method.Body.Id,
                null,
                method.Body.Statements[1].Id)]);
        return new CSharpSourcePatchApplier().Apply(request, patch);
    }

    [Fact]
    public void Apply_PatchesLoopConditionWithoutRecreatingLoopIdentity()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    while (value > 10)
                    {
                        value = value - 1;
                    }

                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) =
            CreatePersistentRequest(source, "Game/LoopPatch.cs");
        HirCompilationUnit originalHir = original.Hir!;
        HirWhileStatement originalLoop = Assert.Single(
            originalHir.DescendantsAndSelf().OfType<HirWhileStatement>());
        HirLiteralExpression literal = Assert.Single(
            originalLoop.Condition.DescendantsAndSelf().OfType<HirLiteralExpression>());
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            originalHir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceLiteralPatchOperation(literal.Id, "10", "100")]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("while (value > 100)", Assert.Single(result.PatchedRequest!.Sources).Source);
        CSharpAnalysisResult changedAnalysis = result.Analysis!;
        HirCompilationUnit changedHir = changedAnalysis.Hir!;
        HirWhileStatement changedLoop = Assert.Single(
            changedHir.DescendantsAndSelf().OfType<HirWhileStatement>());
        Assert.Equal(originalLoop.Id, changedLoop.Id);
        Assert.Equal(
            Assert.Single(original.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet(),
            Assert.Single(changedAnalysis.IdentitySidecars).Entries.Select(static entry => entry.Id).ToHashSet());
        Assert.Empty(Assert.Single(changedAnalysis.IdentitySidecars).Tombstones);
        GraphControlFlowLoop loop = Assert.Single(Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(
                new HirGraphProjector().Project(changedHir, changedAnalysis.Semantics)).Functions).Loops);
        Assert.Equal(changedLoop.Id, loop.LoopNodeId);
    }

    [Fact]
    public void Apply_ReplacesAssignmentValueAndWhileConditionWithoutRecreatingStructure()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
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
        (CSharpCompilationRequest request, CSharpAnalysisResult original) =
            CreatePersistentRequest(source, "Game/StructuredExpressionEdit.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirWhileStatement loop = Assert.IsType<HirWhileStatement>(method.Body.Statements[1]);
        HirExpressionStatement assignmentStatement = Assert.IsType<HirExpressionStatement>(
            Assert.Single(loop.Body.Statements));
        HirAssignmentExpression assignment = Assert.IsType<HirAssignmentExpression>(
            assignmentStatement.Expression);
        HirIdentifierExpression assignmentTarget = Assert.IsType<HirIdentifierExpression>(assignment.Target);
        HashSet<SemanticId> replacedExpressionIds = assignment.Value
            .DescendantsAndSelf()
            .Concat(loop.Condition.DescendantsAndSelf())
            .Select(static node => node.Id)
            .ToHashSet();
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticId boolType = GetTypeId(original, loop.Condition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new ReplaceAssignmentValuePatchOperation(
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
                new ReplaceWhileConditionPatchOperation(
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

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

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
        Assert.Equal(method.Id, changedMethod.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(loop.Id, changedLoop.Id);
        Assert.Equal(loop.Body.Id, changedLoop.Body.Id);
        Assert.Equal(assignmentStatement.Id, changedAssignmentStatement.Id);
        Assert.Equal(assignment.Id, changedAssignment.Id);
        Assert.Equal(assignmentTarget.Id, changedAssignment.Target.Id);
        Assert.Equal(
            resultDefinition.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == changedAssignment.Target.Id).ReferencedSymbolId);

        IdentitySidecarDocument afterIdentity = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> liveIds = afterIdentity.Entries
            .Select(static entry => entry.Id)
            .ToHashSet();
        HashSet<SemanticId> tombstoneIds = afterIdentity.Tombstones
            .Select(static tombstone => tombstone.Id)
            .ToHashSet();
        Assert.All(
            replacedExpressionIds,
            id => Assert.True(liveIds.Contains(id) ^ tombstoneIds.Contains(id)));
        Assert.Contains(loop.Condition.Id, tombstoneIds);
        Assert.Contains(
            Assert.Single(assignment.Value.DescendantsAndSelf().OfType<HirLiteralExpression>()).Id,
            tombstoneIds);
        Assert.Contains(
            Assert.Single(loop.Condition.DescendantsAndSelf().OfType<HirLiteralExpression>()).Id,
            tombstoneIds);
        SemanticId[] preservedIds =
        [
            method.Body.Id,
            resultDefinition.Id,
            loop.Id,
            loop.Body.Id,
            assignmentStatement.Id,
            assignment.Id,
            assignmentTarget.Id,
        ];
        Assert.All(
            preservedIds,
            id => Assert.Contains(afterIdentity.Entries, entry => entry.Id == id));

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphFunction function = Assert.Single(graph.Functions);
        Assert.Contains(function.Nodes, node => node.Id == loop.Id && node.Kind == "loop");
        Assert.Contains(function.Nodes, node => node.Id == assignment.Id && node.Kind == "assignment");
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph).Functions);
        GraphControlFlowLoop changedFlow = Assert.Single(flow.Loops);
        Assert.True(flow.IsReducible);
        Assert.Equal(loop.Id, changedFlow.LoopNodeId);
        Assert.Equal(assignment.Id, changedFlow.BodyEntryNodeId);
        Assert.NotEmpty(changedFlow.BackEdgeArcIds);
    }

    [Fact]
    public void Apply_RejectsAssignmentValueReplacementWithAStaleEndpoint()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    int result = value;
                    result = result - 1;
                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/StaleAssignmentValueEdit.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement definition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirAssignmentExpression assignment = Assert.IsType<HirAssignmentExpression>(
            Assert.IsType<HirExpressionStatement>(method.Body.Statements[1]).Expression);
        SemanticId intType = GetTypeId(original, definition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceAssignmentValuePatchOperation(
                assignment.Id,
                assignment.Operator,
                assignment.Target.Id,
                definition.Id,
                definition.Name,
                SemanticId.FromKey("stale-assignment-value"),
                intType,
                new LiteralPatchExpression("2", intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
    }

    [Fact]
    public void Apply_RejectsWhileConditionReplacementThatWouldDiscardAComment()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value, bool enabled)
                {
                    while (value /* keep threshold rationale */ > 0)
                    {
                        value = value - 1;
                    }

                    return value;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/CommentedLoopConditionEdit.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirWhileStatement loop = Assert.IsType<HirWhileStatement>(method.Body.Statements[0]);
        SemanticId boolType = GetTypeId(original, loop.Condition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceWhileConditionPatchOperation(
                loop.Id,
                loop.Condition.Id,
                boolType,
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    method.Parameters[1].Name,
                    boolType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidReplacement);
    }

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

    private static (CSharpAnalysisResult Analysis, HirLiteralExpression Literal) AnalyzeLiteral(
        CSharpCompilationRequest request)
    {
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        return (
            analysis,
            Assert.Single(analysis.Hir!.DescendantsAndSelf().OfType<HirLiteralExpression>()));
    }
}
