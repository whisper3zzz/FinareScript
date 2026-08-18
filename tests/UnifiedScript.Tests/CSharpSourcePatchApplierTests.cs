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
    public void Apply_ReplacesNestedBinaryInputAndPreservesUnaffectedSpineIdentities()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    int offset = 2;
                    return (value + 1) * 3;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/NestedInput.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement offset = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        HirBinaryExpression outer = Assert.IsType<HirBinaryExpression>(returnStatement.Expression);
        HirBinaryExpression addition = Assert.IsType<HirBinaryExpression>(outer.Left);
        HirExpression oldInput = addition.Right;
        SemanticId intType = GetTypeId(original, addition);
        HashSet<SemanticId> preservedIds = Assert.Single(original.IdentitySidecars).Entries
            .Select(static entry => entry.Id)
            .Where(id => id != oldInput.Id)
            .ToHashSet();
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                addition.Id,
                "right",
                oldInput.Id,
                intType,
                intType,
                new LocalReferencePatchExpression(offset.Id, offset.Name, intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "return (value + offset) * 3;",
            Assert.Single(result.PatchedRequest!.Sources).Source);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement changedOffset = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirReturnStatement changedReturn = Assert.IsType<HirReturnStatement>(changedMethod.Body.Statements[1]);
        HirBinaryExpression changedOuter = Assert.IsType<HirBinaryExpression>(changedReturn.Expression);
        HirBinaryExpression changedAddition = Assert.IsType<HirBinaryExpression>(changedOuter.Left);
        HirIdentifierExpression connectedOffset = Assert.IsType<HirIdentifierExpression>(changedAddition.Right);
        Assert.Equal(returnStatement.Id, changedReturn.Id);
        Assert.Equal(outer.Id, changedOuter.Id);
        Assert.Equal(addition.Id, changedAddition.Id);
        Assert.Equal(addition.Left.Id, changedAddition.Left.Id);
        Assert.Equal(offset.Id, changedOffset.Id);
        Assert.Equal(
            changedOffset.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == connectedOffset.Id).ReferencedSymbolId);

        IdentitySidecarDocument after = Assert.Single(result.Analysis.IdentitySidecars);
        Assert.Subset(
            after.Entries.Select(static entry => entry.Id).ToHashSet(),
            preservedIds);
        Assert.Contains(after.Tombstones, tombstone => tombstone.Id == oldInput.Id);
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
    public void Apply_ReplacesInvocationArgumentInputUsingGraphPortName()
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
                static int Run(int value)
                {
                    return Api.Combine(value, 1);
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/InvocationInput.cs");
        HirInvocationExpression invocation = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>());
        SemanticId intType = GetTypeId(original, invocation.Arguments[1]);
        SemanticId invocationType = GetTypeId(original, invocation);
        HirParameter value = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirParameter>(),
            static parameter => parameter.Name == "value");
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                invocation.Id,
                "arg1",
                invocation.Arguments[1].Id,
                intType,
                invocationType,
                new BinaryPatchExpression(
                    "+",
                    new LocalReferencePatchExpression(value.Id, value.Name, intType),
                    new LiteralPatchExpression("2", intType),
                    intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "Api.Combine(value, (value + 2))",
            Assert.Single(result.PatchedRequest!.Sources).Source);
        HirInvocationExpression changedInvocation = Assert.Single(
            result.Analysis!.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>());
        Assert.Equal(invocation.Id, changedInvocation.Id);
        Assert.Equal(invocation.Arguments[0].Id, changedInvocation.Arguments[0].Id);
        Assert.IsType<HirBinaryExpression>(changedInvocation.Arguments[1]);
    }

    [Fact]
    public void Apply_ReplacesUnaryAndCastOperandPorts()
    {
        const string unarySource = """
            using UnifiedScript;

            static class UnaryScript
            {
                [Visual]
                static int Run(int value)
                {
                    return -value;
                }
            }
            """;
        (CSharpCompilationRequest unaryRequest, CSharpAnalysisResult unaryAnalysis) =
            CreatePersistentRequest(unarySource, "Game/UnaryInput.cs");
        HirUnaryExpression unary = Assert.Single(
            unaryAnalysis.Hir!.DescendantsAndSelf().OfType<HirUnaryExpression>());
        SemanticId unaryType = GetTypeId(unaryAnalysis, unary);
        SemanticPatchDocument unaryPatch = SemanticPatchDocument.Create(
            unaryAnalysis.Hir!.Id,
            unaryAnalysis.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                unary.Id,
                "operand",
                unary.Operand.Id,
                unaryType,
                unaryType,
                new LiteralPatchExpression("2", unaryType))]);

        CSharpPatchResult unaryResult = new CSharpSourcePatchApplier().Apply(unaryRequest, unaryPatch);

        Assert.True(unaryResult.Succeeded, string.Join(Environment.NewLine, unaryResult.Diagnostics));
        Assert.Contains("return -2;", Assert.Single(unaryResult.PatchedRequest!.Sources).Source);
        Assert.Equal(
            unary.Id,
            Assert.Single(unaryResult.Analysis!.Hir!.DescendantsAndSelf()
                .OfType<HirUnaryExpression>()).Id);

        const string castSource = """
            using UnifiedScript;

            static class CastScript
            {
                [Visual]
                static int Run(float value)
                {
                    return (int)value;
                }
            }
            """;
        (CSharpCompilationRequest castRequest, CSharpAnalysisResult castAnalysis) =
            CreatePersistentRequest(castSource, "Game/CastInput.cs");
        HirCastExpression cast = Assert.Single(
            castAnalysis.Hir!.DescendantsAndSelf().OfType<HirCastExpression>());
        SemanticId operandType = GetTypeId(castAnalysis, cast.Operand);
        SemanticPatchDocument castPatch = SemanticPatchDocument.Create(
            castAnalysis.Hir!.Id,
            castAnalysis.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                cast.Id,
                "operand",
                cast.Operand.Id,
                operandType,
                GetTypeId(castAnalysis, cast),
                new LiteralPatchExpression("2.0f", operandType))]);

        CSharpPatchResult castResult = new CSharpSourcePatchApplier().Apply(castRequest, castPatch);

        Assert.True(castResult.Succeeded, string.Join(Environment.NewLine, castResult.Diagnostics));
        Assert.Contains("return (int)2.0f;", Assert.Single(castResult.PatchedRequest!.Sources).Source);
        Assert.Equal(
            cast.Id,
            Assert.Single(castResult.Analysis!.Hir!.DescendantsAndSelf()
                .OfType<HirCastExpression>()).Id);
    }

    [Fact]
    public void Apply_BuildsTypedConditionalAndEditsItsExactInputPort()
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
            "Game/ConditionalPatch.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirExpression oldValue = Assert.IsAssignableFrom<HirExpression>(returnStatement.Expression);
        SemanticId boolType = GetTypeId(original, method.Parameters[0]);
        SemanticId intType = GetTypeId(original, oldValue);
        var applier = new CSharpSourcePatchApplier();
        SemanticPatchDocument buildPatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceReturnValuePatchOperation(
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
                    intType))]);

        CSharpPatchResult built = applier.Apply(request, buildPatch);

        Assert.True(built.Succeeded, string.Join(Environment.NewLine, built.Diagnostics));
        CSharpCompilationRequest builtRequest = Assert.IsType<CSharpCompilationRequest>(built.PatchedRequest);
        Assert.Contains(
            "return enabled ? value : fallback;",
            Assert.Single(builtRequest.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult builtAnalysis = Assert.IsType<CSharpAnalysisResult>(built.Analysis);
        HirCompilationUnit builtHir = Assert.IsType<HirCompilationUnit>(builtAnalysis.Hir);
        BoundCompilation builtSemantics = Assert.IsType<BoundCompilation>(builtAnalysis.Semantics);
        HirConditionalExpression conditional = Assert.Single(
            builtHir.DescendantsAndSelf().OfType<HirConditionalExpression>());
        SemanticId originalTrueId = conditional.WhenTrue.Id;
        SemanticId oldFalseId = conditional.WhenFalse.Id;
        SemanticPatchDocument editPortPatch = SemanticPatchDocument.Create(
            builtHir.Id,
            builtSemantics.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                conditional.Id,
                "whenFalse",
                oldFalseId,
                intType,
                intType,
                new BinaryPatchExpression(
                    "+",
                    new LocalReferencePatchExpression(
                        method.Parameters[1].Id,
                        method.Parameters[1].Name,
                        intType),
                    new LiteralPatchExpression("1", intType),
                    intType))]);

        IReadOnlyDictionary<string, IdentitySidecarDocument> builtSidecars = builtAnalysis.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        var advancedRequest = new CSharpCompilationRequest(
            builtRequest.AssemblyName,
            builtRequest.Sources.Select(source => new CSharpSourceDocument(
                source.LogicalPath,
                source.Source,
                builtSidecars[source.LogicalPath])),
            builtRequest.ReferencePaths,
            builtRequest.EffectCatalog);
        CSharpPatchResult edited = applier.Apply(advancedRequest, editPortPatch);

        Assert.True(edited.Succeeded, string.Join(Environment.NewLine, edited.Diagnostics));
        Assert.Contains(
            "return enabled ? value : (value + 1);",
            Assert.Single(edited.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult editedAnalysis = Assert.IsType<CSharpAnalysisResult>(edited.Analysis);
        HirCompilationUnit editedHir = Assert.IsType<HirCompilationUnit>(editedAnalysis.Hir);
        HirConditionalExpression changed = Assert.Single(
            editedHir.DescendantsAndSelf().OfType<HirConditionalExpression>());
        HirBinaryExpression newFalse = Assert.IsType<HirBinaryExpression>(changed.WhenFalse);
        Assert.Equal(conditional.Id, changed.Id);
        Assert.Equal(originalTrueId, changed.WhenTrue.Id);
        Assert.Contains(
            Assert.Single(editedAnalysis.IdentitySidecars).Tombstones,
            tombstone => tombstone.Id == oldFalseId);
        GraphFunction graph = Assert.Single(new HirGraphProjector()
            .Project(editedHir, editedAnalysis.Semantics)
            .Functions);
        Assert.Contains(
            graph.Edges,
            edge => edge.FromNode == newFalse.Id &&
                edge.ToNode == changed.Id &&
                edge.ToPort == "whenFalse");

        SemanticPatchDocument invalidConditionPatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceReturnValuePatchOperation(
                returnStatement.Id,
                oldValue.Id,
                intType,
                new ConditionalPatchExpression(
                    new LocalReferencePatchExpression(
                        method.Parameters[1].Id,
                        method.Parameters[1].Name,
                        intType),
                    new LiteralPatchExpression("1", intType),
                    new LiteralPatchExpression("2", intType),
                    intType))]);

        CSharpPatchResult invalid = applier.Apply(request, invalidConditionPatch);

        Assert.False(invalid.Succeeded);
        Assert.Contains(
            invalid.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_BuildsTypedNullCoalescingAndEditsItsExactInputPort()
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
            "Game/CoalescePatch.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirExpression oldValue = Assert.IsAssignableFrom<HirExpression>(returnStatement.Expression);
        SemanticId nullableIntType = GetTypeId(original, method.Parameters[0]);
        SemanticId intType = GetTypeId(original, method.Parameters[1]);
        var applier = new CSharpSourcePatchApplier();
        SemanticPatchDocument buildPatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceReturnValuePatchOperation(
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
                    intType))]);

        CSharpPatchResult built = applier.Apply(request, buildPatch);

        Assert.True(built.Succeeded, string.Join(Environment.NewLine, built.Diagnostics));
        CSharpCompilationRequest builtRequest = Assert.IsType<CSharpCompilationRequest>(built.PatchedRequest);
        Assert.Contains(
            "return value ?? fallback;",
            Assert.Single(builtRequest.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult builtAnalysis = Assert.IsType<CSharpAnalysisResult>(built.Analysis);
        HirCompilationUnit builtHir = Assert.IsType<HirCompilationUnit>(builtAnalysis.Hir);
        BoundCompilation builtSemantics = Assert.IsType<BoundCompilation>(builtAnalysis.Semantics);
        HirBinaryExpression coalesce = Assert.Single(
            builtHir.DescendantsAndSelf().OfType<HirBinaryExpression>(),
            static binary => binary.Operator == "??");
        SemanticId originalLeftId = coalesce.Left.Id;
        SemanticId oldRightId = coalesce.Right.Id;
        SemanticPatchDocument editPortPatch = SemanticPatchDocument.Create(
            builtHir.Id,
            builtSemantics.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                coalesce.Id,
                "right",
                oldRightId,
                intType,
                intType,
                new LiteralPatchExpression("2", intType))]);
        IReadOnlyDictionary<string, IdentitySidecarDocument> builtSidecars = builtAnalysis.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        var advancedRequest = new CSharpCompilationRequest(
            builtRequest.AssemblyName,
            builtRequest.Sources.Select(source => new CSharpSourceDocument(
                source.LogicalPath,
                source.Source,
                builtSidecars[source.LogicalPath])),
            builtRequest.ReferencePaths,
            builtRequest.EffectCatalog);

        CSharpPatchResult edited = applier.Apply(advancedRequest, editPortPatch);

        Assert.True(edited.Succeeded, string.Join(Environment.NewLine, edited.Diagnostics));
        Assert.Contains(
            "return value ?? 2;",
            Assert.Single(edited.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult editedAnalysis = Assert.IsType<CSharpAnalysisResult>(edited.Analysis);
        HirCompilationUnit editedHir = Assert.IsType<HirCompilationUnit>(editedAnalysis.Hir);
        BoundCompilation editedSemantics = Assert.IsType<BoundCompilation>(editedAnalysis.Semantics);
        HirBinaryExpression changed = Assert.Single(
            editedHir.DescendantsAndSelf().OfType<HirBinaryExpression>(),
            static binary => binary.Operator == "??");
        Assert.Equal(coalesce.Id, changed.Id);
        Assert.Equal(originalLeftId, changed.Left.Id);
        Assert.Contains(
            Assert.Single(editedAnalysis.IdentitySidecars).Tombstones,
            tombstone => tombstone.Id == oldRightId);
        GraphNode graphCoalesce = Assert.Single(
            Assert.Single(new HirGraphProjector()
                .Project(editedHir, editedSemantics)
                .Functions)
                .Nodes,
            static node => node.Kind == "binary" && node.Properties["operator"] == "??");
        Assert.Equal("whenLeftNull", graphCoalesce.Properties["rightEvaluation"]);

        SemanticPatchDocument invalidPatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new ReplaceReturnValuePatchOperation(
                returnStatement.Id,
                oldValue.Id,
                intType,
                new BinaryPatchExpression(
                    "??",
                    new LocalReferencePatchExpression(
                        method.Parameters[1].Id,
                        method.Parameters[1].Name,
                        intType),
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        method.Parameters[0].Name,
                        nullableIntType),
                    intType))]);

        CSharpPatchResult invalid = applier.Apply(request, invalidPatch);

        Assert.False(invalid.Succeeded);
        Assert.Contains(
            invalid.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_BuildsConditionalMemberAccessAndEditsItsReceiverPort()
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
            "Game/ConditionalMemberAccessPatch.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirExpression oldValue = Assert.IsAssignableFrom<HirExpression>(valueReturn.Expression);
        SemanticId nullableStringType = GetTypeId(original, method.Parameters[0]);
        SemanticId nullableIntType = GetTypeId(original, oldValue);
        var applier = new CSharpSourcePatchApplier();
        SemanticPatchDocument buildPatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceReturnValuePatchOperation(
                valueReturn.Id,
                oldValue.Id,
                nullableIntType,
                new ConditionalMemberAccessPatchExpression(
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        method.Parameters[0].Name,
                        nullableStringType),
                    "Length",
                    nullableIntType))]);

        CSharpPatchResult built = applier.Apply(request, buildPatch);

        Assert.True(built.Succeeded, string.Join(Environment.NewLine, built.Diagnostics));
        CSharpCompilationRequest builtRequest = Assert.IsType<CSharpCompilationRequest>(
            built.PatchedRequest);
        Assert.Contains(
            "return primary?.Length;",
            Assert.Single(builtRequest.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult builtAnalysis = Assert.IsType<CSharpAnalysisResult>(built.Analysis);
        HirCompilationUnit builtHir = Assert.IsType<HirCompilationUnit>(builtAnalysis.Hir);
        HirConditionalAccessExpression conditionalAccess = Assert.Single(
            builtHir.DescendantsAndSelf().OfType<HirConditionalAccessExpression>());
        HirConditionalMemberBindingExpression memberBinding =
            Assert.IsType<HirConditionalMemberBindingExpression>(conditionalAccess.WhenNotNull);
        SemanticId oldReceiverId = conditionalAccess.Receiver.Id;
        SemanticPatchDocument editPatch = SemanticPatchDocument.Create(
            builtHir.Id,
            builtAnalysis.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                conditionalAccess.Id,
                "receiver",
                oldReceiverId,
                nullableStringType,
                nullableIntType,
                new LocalReferencePatchExpression(
                    method.Parameters[1].Id,
                    method.Parameters[1].Name,
                    nullableStringType))]);
        IReadOnlyDictionary<string, IdentitySidecarDocument> sidecars = builtAnalysis.IdentitySidecars
            .ToDictionary(static sidecar => sidecar.SourcePath, StringComparer.Ordinal);
        var advancedRequest = new CSharpCompilationRequest(
            builtRequest.AssemblyName,
            builtRequest.Sources.Select(document => new CSharpSourceDocument(
                document.LogicalPath,
                document.Source,
                sidecars[document.LogicalPath])),
            builtRequest.ReferencePaths,
            builtRequest.EffectCatalog);

        CSharpPatchResult edited = applier.Apply(advancedRequest, editPatch);

        Assert.True(edited.Succeeded, string.Join(Environment.NewLine, edited.Diagnostics));
        Assert.Contains(
            "return fallback?.Length;",
            Assert.Single(edited.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult editedAnalysis = Assert.IsType<CSharpAnalysisResult>(edited.Analysis);
        HirCompilationUnit editedHir = Assert.IsType<HirCompilationUnit>(editedAnalysis.Hir);
        BoundCompilation editedSemantics = Assert.IsType<BoundCompilation>(editedAnalysis.Semantics);
        HirConditionalAccessExpression changed = Assert.Single(
            editedHir.DescendantsAndSelf().OfType<HirConditionalAccessExpression>());
        Assert.Equal(conditionalAccess.Id, changed.Id);
        Assert.Equal(memberBinding.Id, changed.WhenNotNull.Id);
        Assert.Equal(
            "fallback",
            Assert.IsType<HirIdentifierExpression>(changed.Receiver).Name);
        Assert.Contains(
            Assert.Single(editedAnalysis.IdentitySidecars).Tombstones,
            tombstone => tombstone.Id == oldReceiverId);
        GraphFunction graph = Assert.Single(new HirGraphProjector()
            .Project(editedHir, editedSemantics)
            .Functions);
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Data &&
            edge.FromNode == changed.Receiver.Id &&
            edge.ToNode == changed.Id &&
            edge.ToPort == "receiver");
        Assert.Contains(graph.Edges, edge =>
            edge.Kind == GraphEdgeKind.Data &&
            edge.FromNode == changed.Receiver.Id &&
            edge.ToNode == changed.WhenNotNull.Id &&
            edge.ToPort == "target");
    }

    [Fact]
    public void Apply_RejectsConditionalMemberAccessWithAHiddenResultConversion()
    {
        const string source = """
            #nullable enable

            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static long? Measure(string? value)
                {
                    return null;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/ConvertedConditionalMemberAccess.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(original.Succeeded, string.Join(Environment.NewLine, original.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId nullableStringType = GetTypeId(original, method.Parameters[0]);
        SemanticId nullableLongType = GetTypeId(original, valueReturn.Expression!);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceReturnValuePatchOperation(
                valueReturn.Id,
                valueReturn.Expression!.Id,
                nullableLongType,
                new ConditionalMemberAccessPatchExpression(
                    new LocalReferencePatchExpression(
                        method.Parameters[0].Id,
                        method.Parameters[0].Name,
                        nullableStringType),
                    "Length",
                    nullableLongType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code is SemanticDiagnosticCodes.ConditionalAccessTypeShapeNotSupported or
                SemanticPatchDiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void Apply_RejectsStaleOrIncompatibleExpressionInputPort()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            Source,
            "Game/RejectInputPort.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirBinaryExpression binary = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirBinaryExpression>());
        SemanticId intType = GetTypeId(original, binary);
        var applier = new CSharpSourcePatchApplier();
        SemanticPatchDocument incompatiblePort = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                binary.Id,
                "operand",
                binary.Right.Id,
                intType,
                intType,
                new LiteralPatchExpression("2", intType))]);
        SemanticPatchDocument staleEndpoint = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                binary.Id,
                "right",
                SemanticId.FromKey("stale-input-endpoint"),
                intType,
                intType,
                new LiteralPatchExpression("2", intType))]);
        SemanticId staleInputType = SemanticId.FromKey("stale-input-port-type");
        SemanticPatchDocument staleInputTypePatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                binary.Id,
                "right",
                binary.Right.Id,
                staleInputType,
                intType,
                new LiteralPatchExpression("2", staleInputType))]);
        SemanticPatchDocument staleTargetTypePatch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                binary.Id,
                "right",
                binary.Right.Id,
                intType,
                SemanticId.FromKey("stale-input-parent-type"),
                new LiteralPatchExpression("2", intType))]);

        CSharpPatchResult incompatibleResult = applier.Apply(request, incompatiblePort);
        CSharpPatchResult staleResult = applier.Apply(request, staleEndpoint);
        CSharpPatchResult staleInputTypeResult = applier.Apply(request, staleInputTypePatch);
        CSharpPatchResult staleTargetTypeResult = applier.Apply(request, staleTargetTypePatch);

        Assert.False(incompatibleResult.Succeeded);
        Assert.Null(incompatibleResult.PatchedRequest);
        Assert.Contains(
            incompatibleResult.Diagnostics,
            static diagnostic =>
                diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidExpressionInputPort);
        Assert.False(staleResult.Succeeded);
        Assert.Null(staleResult.PatchedRequest);
        Assert.Contains(
            staleResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
        Assert.False(staleInputTypeResult.Succeeded);
        Assert.Null(staleInputTypeResult.PatchedRequest);
        Assert.Contains(
            staleInputTypeResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
        Assert.False(staleTargetTypeResult.Succeeded);
        Assert.Null(staleTargetTypeResult.PatchedRequest);
        Assert.Contains(
            staleTargetTypeResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.PreconditionFailed);
    }

    [Fact]
    public void Apply_RejectsEffectfulExpressionInputAndPreservesBoundaryComments()
    {
        const string effectfulSource = """
            using UnifiedScript;

            static class Api
            {
                public static int Next() => 1;
                public static int Combine(int first, int second) => first + second;
            }

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return Api.Combine(Api.Next(), value);
                }
            }
            """;
        CSharpCompilationRequest effectfulRequest = CSharpCompilationRequest.Single(
            effectfulSource,
            "Game/RejectEffectfulInput.cs");
        CSharpAnalysisResult effectfulAnalysis = new CSharpSemanticPipeline().Analyze(effectfulRequest);
        HirInvocationExpression outerInvocation = Assert.Single(
            effectfulAnalysis.Hir!.DescendantsAndSelf().OfType<HirInvocationExpression>(),
            static invocation => invocation.Arguments.Count == 2);
        SemanticId intType = GetTypeId(effectfulAnalysis, outerInvocation.Arguments[0]);
        SemanticPatchDocument effectfulPatch = SemanticPatchDocument.Create(
            effectfulAnalysis.Hir!.Id,
            effectfulAnalysis.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                outerInvocation.Id,
                "arg0",
                outerInvocation.Arguments[0].Id,
                intType,
                GetTypeId(effectfulAnalysis, outerInvocation),
                new LiteralPatchExpression("2", intType))]);

        CSharpPatchResult effectfulResult = new CSharpSourcePatchApplier().Apply(
            effectfulRequest,
            effectfulPatch);

        Assert.False(effectfulResult.Succeeded);
        Assert.Null(effectfulResult.PatchedRequest);
        Assert.Contains(
            effectfulResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.EffectOrderViolation);

        const string commentedSource = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return value + /* keep input note */ 1;
                }
            }
            """;
        CSharpCompilationRequest commentedRequest = CSharpCompilationRequest.Single(
            commentedSource,
            "Game/RejectCommentedInput.cs");
        CSharpAnalysisResult commentedAnalysis = new CSharpSemanticPipeline().Analyze(commentedRequest);
        HirBinaryExpression commentedBinary = Assert.Single(
            commentedAnalysis.Hir!.DescendantsAndSelf().OfType<HirBinaryExpression>());
        SemanticPatchDocument commentedPatch = SemanticPatchDocument.Create(
            commentedAnalysis.Hir!.Id,
            commentedAnalysis.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                commentedBinary.Id,
                "right",
                commentedBinary.Right.Id,
                GetTypeId(commentedAnalysis, commentedBinary.Right),
                GetTypeId(commentedAnalysis, commentedBinary),
                new LiteralPatchExpression(
                    "2",
                    GetTypeId(commentedAnalysis, commentedBinary.Right))) ]);

        CSharpPatchResult commentedResult = new CSharpSourcePatchApplier().Apply(
            commentedRequest,
            commentedPatch);

        Assert.True(commentedResult.Succeeded, string.Join(Environment.NewLine, commentedResult.Diagnostics));
        Assert.Contains(
            "return value + /* keep input note */ 2;",
            Assert.Single(commentedResult.PatchedRequest!.Sources).Source);

        const string embeddedCommentSource = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int value)
                {
                    return (value + /* embedded note */ 1) * 3;
                }
            }
            """;
        CSharpCompilationRequest embeddedCommentRequest = CSharpCompilationRequest.Single(
            embeddedCommentSource,
            "Game/RejectEmbeddedInputComment.cs");
        CSharpAnalysisResult embeddedCommentAnalysis = new CSharpSemanticPipeline().Analyze(
            embeddedCommentRequest);
        HirReturnStatement embeddedReturn = Assert.Single(
            embeddedCommentAnalysis.Hir!.DescendantsAndSelf().OfType<HirReturnStatement>());
        HirBinaryExpression embeddedOuter = Assert.IsType<HirBinaryExpression>(
            embeddedReturn.Expression);
        SemanticId embeddedInputType = GetTypeId(embeddedCommentAnalysis, embeddedOuter.Left);
        SemanticPatchDocument embeddedCommentPatch = SemanticPatchDocument.Create(
            embeddedCommentAnalysis.Hir!.Id,
            embeddedCommentAnalysis.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                embeddedOuter.Id,
                "left",
                embeddedOuter.Left.Id,
                embeddedInputType,
                GetTypeId(embeddedCommentAnalysis, embeddedOuter),
                new LiteralPatchExpression("2", embeddedInputType))]);

        CSharpPatchResult embeddedCommentResult = new CSharpSourcePatchApplier().Apply(
            embeddedCommentRequest,
            embeddedCommentPatch);

        Assert.False(embeddedCommentResult.Succeeded);
        Assert.Null(embeddedCommentResult.PatchedRequest);
        Assert.Contains(
            embeddedCommentResult.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidReplacement);
        Assert.Equal(embeddedCommentSource, Assert.Single(embeddedCommentRequest.Sources).Source);
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
    public void Apply_InsertsLoopTransferIntoTextAuthoredFor(bool isContinue)
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int count)
                {
                    for (int index = 0; index < count; index++)
                    {
                    }

                    return count;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ForLoopTransfer.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirForStatement loop = Assert.IsType<HirForStatement>(method.Body.Statements[0]);
        InsertPatchStatement transfer = isContinue
            ? new ContinuePatchStatement()
            : new BreakPatchStatement();
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(loop.Body.Id, null, null, transfer)]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains(isContinue ? "continue;" : "break;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(
            Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirForStatement changedLoop = Assert.IsType<HirForStatement>(changedMethod.Body.Statements[0]);
        HirStatement insertedTransfer = Assert.Single(changedLoop.Body.Statements);
        HirUnaryExpression increment = Assert.IsType<HirUnaryExpression>(
            Assert.Single(changedLoop.Incrementors));
        GraphControlFlowLoop flow = Assert.Single(
            Assert.Single(new GraphControlFlowAnalyzer().AnalyzeAndValidate(
                new HirGraphProjector().Project(
                    result.Analysis.Hir,
                    result.Analysis.Semantics)).Functions).Loops);
        Assert.Equal(changedLoop.Id, flow.LoopNodeId);
        Assert.Equal(increment.Id, flow.ContinueTargetNodeId);
        if (isContinue)
        {
            Assert.IsType<HirContinueStatement>(insertedTransfer);
            Assert.Contains(insertedTransfer.Id, flow.ContinueNodeIds);
            Assert.Empty(flow.BreakNodeIds);
        }
        else
        {
            Assert.IsType<HirBreakStatement>(insertedTransfer);
            Assert.Contains(insertedTransfer.Id, flow.BreakNodeIds);
            Assert.Empty(flow.ContinueNodeIds);
        }
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
    public void Apply_ConstructsTypedForAndReprojectsItsPhaseContract()
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
            "Game/ForBuild.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        SemanticId intType = GetTypeId(original, method.Parameters[0]);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var index = new ForLocalReferencePatchExpression(0, "index", intType);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
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
                    [new ForMutationPatchExpression(index, "++", true, intType)]))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "for (int index = 0; index < count; index++)",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(
            Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirForStatement inserted = Assert.IsType<HirForStatement>(changedMethod.Body.Statements[0]);
        HirForVariableDeclarator variable = Assert.Single(inserted.Declaration!.Variables);
        HirUnaryExpression increment = Assert.IsType<HirUnaryExpression>(
            Assert.Single(inserted.Incrementors));
        Assert.True(increment.IsPostfix);
        Assert.Equal(method.Id, changedMethod.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(valueReturn.Id, changedMethod.Body.Statements[1].Id);
        Assert.Equal(
            variable.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId ==
                    Assert.IsType<HirIdentifierExpression>(
                        Assert.IsType<HirBinaryExpression>(inserted.Condition).Left).Id)
                .ReferencedSymbolId);

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphControlFlowLoop flow = Assert.Single(
            Assert.Single(new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph).Functions).Loops);
        Assert.Equal(inserted.Id, flow.LoopNodeId);
        Assert.NotNull(flow.InitializerRegionId);
        Assert.NotNull(flow.IncrementRegionId);
        Assert.Equal(increment.Id, flow.ContinueTargetNodeId);
    }

    [Fact]
    public void Apply_ConstructsForWithOrderedAssignmentInitializer()
    {
        const string source = """
            using UnifiedScript;

            static class Script
            {
                [Visual]
                static int Run(int count, bool enabled)
                {
                    int index = count;
                    return index;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ForAssignmentBuild.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement indexDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        SemanticId intType = GetTypeId(original, indexDefinition);
        SemanticId boolType = GetTypeId(original, method.Parameters[1]);
        var index = new LocalReferencePatchExpression(
            indexDefinition.Id,
            indexDefinition.Name,
            intType);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new InsertStatementPatchOperation(
                method.Body.Id,
                indexDefinition.Id,
                valueReturn.Id,
                new ForPatchStatement(
                    null,
                    [new ForAssignmentPatchExpression(
                        indexDefinition.Id,
                        indexDefinition.Name,
                        intType,
                        new LiteralPatchExpression("0", intType))],
                    new BinaryPatchExpression(
                        "<",
                        index,
                        new LocalReferencePatchExpression(
                            method.Parameters[0].Id,
                            method.Parameters[0].Name,
                            intType),
                        boolType),
                    [new ForMutationPatchExpression(index, "++", true, intType)]))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "for (index = 0; index < count; index++)",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult changedAnalysis = Assert.IsType<CSharpAnalysisResult>(result.Analysis);
        HirCompilationUnit changedHir = Assert.IsType<HirCompilationUnit>(changedAnalysis.Hir);
        HirForStatement inserted = Assert.IsType<HirForStatement>(
            Assert.Single(changedHir.DescendantsAndSelf().OfType<HirForStatement>()));
        Assert.Null(inserted.Declaration);
        Assert.Single(inserted.Initializers);
        GraphFunction graph = Assert.Single(new HirGraphProjector()
            .Project(changedHir, changedAnalysis.Semantics)
            .Functions);
        GraphNode loopNode = Assert.Single(graph.Nodes, node => node.Id == inserted.Id);
        Assert.Equal("expressions", loopNode.Properties["initializerKind"]);
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
    public void Apply_EditsForClauseExpressionWithoutRecreatingForStructure()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    for (int index = 0; index < 3; index++)
                    {
                        value = value + 1;
                    }

                    return value;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) =
            CreatePersistentRequest(source, "Game/ForClauseEdit.cs");
        HirCompilationUnit originalHir = Assert.IsType<HirCompilationUnit>(original.Hir);
        HirForStatement originalLoop = Assert.Single(
            originalHir.DescendantsAndSelf().OfType<HirForStatement>());
        HirForVariableDeclaration originalDeclaration = Assert.IsType<HirForVariableDeclaration>(
            originalLoop.Declaration);
        HirForVariableDeclarator originalVariable = Assert.Single(originalDeclaration.Variables);
        HirBinaryExpression originalCondition = Assert.IsType<HirBinaryExpression>(
            originalLoop.Condition);
        HirLiteralExpression originalLimit = Assert.IsType<HirLiteralExpression>(
            originalCondition.Right);
        HirUnaryExpression originalIncrement = Assert.IsType<HirUnaryExpression>(
            Assert.Single(originalLoop.Incrementors));
        SemanticId intType = GetTypeId(original, originalLimit);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            originalHir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceExpressionInputPatchOperation(
                originalCondition.Id,
                "right",
                originalLimit.Id,
                intType,
                GetTypeId(original, originalCondition),
                new LiteralPatchExpression("4", intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "for (int index = 0; index < 4; index++)",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        CSharpAnalysisResult changedAnalysis = Assert.IsType<CSharpAnalysisResult>(result.Analysis);
        HirCompilationUnit changedHir = Assert.IsType<HirCompilationUnit>(changedAnalysis.Hir);
        HirForStatement changedLoop = Assert.Single(
            changedHir.DescendantsAndSelf().OfType<HirForStatement>());
        HirForVariableDeclaration changedDeclaration = Assert.IsType<HirForVariableDeclaration>(
            changedLoop.Declaration);
        HirForVariableDeclarator changedVariable = Assert.Single(changedDeclaration.Variables);
        HirBinaryExpression changedCondition = Assert.IsType<HirBinaryExpression>(
            changedLoop.Condition);
        HirLiteralExpression changedLimit = Assert.IsType<HirLiteralExpression>(
            changedCondition.Right);
        HirUnaryExpression changedIncrement = Assert.IsType<HirUnaryExpression>(
            Assert.Single(changedLoop.Incrementors));
        Assert.Equal(originalLoop.Id, changedLoop.Id);
        Assert.Equal(originalLoop.Body.Id, changedLoop.Body.Id);
        Assert.Equal(originalDeclaration.Id, changedDeclaration.Id);
        Assert.Equal(originalVariable.Id, changedVariable.Id);
        Assert.Equal(originalCondition.Id, changedCondition.Id);
        Assert.Equal(originalIncrement.Id, changedIncrement.Id);
        Assert.NotEqual(originalLimit.Id, changedLimit.Id);
        Assert.Equal("4", changedLimit.ValueText);
        IdentitySidecarDocument identity = Assert.Single(changedAnalysis.IdentitySidecars);
        Assert.Contains(identity.Tombstones, tombstone => tombstone.Id == originalLimit.Id);

        GraphControlFlowLoop flow = Assert.Single(Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(
                new HirGraphProjector().Project(
                    changedHir,
                    changedAnalysis.Semantics)).Functions).Loops);
        Assert.Equal(changedLoop.Id, flow.LoopNodeId);
        Assert.NotNull(flow.InitializerRegionId);
        Assert.NotNull(flow.IncrementRegionId);
        Assert.Equal(changedIncrement.Id, flow.ContinueTargetNodeId);
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
    public void Apply_ReplacesDoWhileConditionWithoutRecreatingBodyOrLoopIdentity()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value, int stopAt, bool enabled)
                {
                    int result = value;
                    do
                    {
                        result = result - 1;
                    }
                    while (result > 0);

                    return result;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) =
            CreatePersistentRequest(source, "Game/DoWhileConditionEdit.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement resultDefinition = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirDoWhileStatement loop = Assert.IsType<HirDoWhileStatement>(method.Body.Statements[1]);
        HirExpressionStatement bodyStatement = Assert.IsType<HirExpressionStatement>(
            Assert.Single(loop.Body.Statements));
        SemanticId intType = GetTypeId(original, resultDefinition);
        SemanticId boolType = GetTypeId(original, loop.Condition);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceDoWhileConditionPatchOperation(
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
                    boolType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("while (enabled && (result > stopAt));", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirDoWhileStatement changedLoop = Assert.IsType<HirDoWhileStatement>(
            changedMethod.Body.Statements[1]);
        Assert.Equal(method.Id, changedMethod.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(loop.Id, changedLoop.Id);
        Assert.Equal(loop.Body.Id, changedLoop.Body.Id);
        Assert.Equal(bodyStatement.Id, Assert.Single(changedLoop.Body.Statements).Id);
        Assert.NotEqual(loop.Condition.Id, changedLoop.Condition.Id);
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

    [Fact]
    public void Apply_ReplacesIfConditionLocalInitializerAndReturnValueWithoutRecreatingStatements()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value, int stopAt, bool enabled)
                {
                    int result = value + 1;
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
            "Game/GeneralExpressionEndpointEdit.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement local = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirIfStatement branch = Assert.IsType<HirIfStatement>(method.Body.Statements[1]);
        HirReturnStatement branchReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(branch.Then.Statements));
        HirExpression originalInitializer = Assert.IsAssignableFrom<HirExpression>(local.Initializer);
        HirExpression originalReturnValue = Assert.IsAssignableFrom<HirExpression>(branchReturn.Expression);
        SemanticId intType = GetTypeId(original, local);
        SemanticId boolType = GetTypeId(original, branch.Condition);
        HashSet<SemanticId> oldEndpointIds = originalInitializer
            .DescendantsAndSelf()
            .Concat(branch.Condition.DescendantsAndSelf())
            .Concat(originalReturnValue.DescendantsAndSelf())
            .Select(static node => node.Id)
            .ToHashSet();
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new ReplaceLocalInitializerPatchOperation(
                    local.Id,
                    local.Name,
                    originalInitializer.Id,
                    intType,
                    new BinaryPatchExpression(
                        "-",
                        new LocalReferencePatchExpression(
                            method.Parameters[0].Id,
                            method.Parameters[0].Name,
                            intType),
                        new LiteralPatchExpression("2", intType),
                        intType)),
                new ReplaceIfConditionPatchOperation(
                    branch.Id,
                    branch.Condition.Id,
                    boolType,
                    new BinaryPatchExpression(
                        "&&",
                        new LocalReferencePatchExpression(
                            method.Parameters[2].Id,
                            method.Parameters[2].Name,
                            boolType),
                        new BinaryPatchExpression(
                            ">",
                            new LocalReferencePatchExpression(local.Id, local.Name, intType),
                            new LocalReferencePatchExpression(
                                method.Parameters[1].Id,
                                method.Parameters[1].Name,
                                intType),
                            boolType),
                        boolType)),
                new ReplaceReturnValuePatchOperation(
                    branchReturn.Id,
                    originalReturnValue.Id,
                    intType,
                    new BinaryPatchExpression(
                        "+",
                        new LocalReferencePatchExpression(local.Id, local.Name, intType),
                        new LocalReferencePatchExpression(
                            method.Parameters[1].Id,
                            method.Parameters[1].Name,
                            intType),
                        intType)),
            ]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains("int result = value - 2;", patchedSource, StringComparison.Ordinal);
        Assert.Contains("if (enabled && (result > stopAt))", patchedSource, StringComparison.Ordinal);
        Assert.Contains("return result + stopAt;", patchedSource, StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirVariableDeclarationStatement changedLocal = Assert.IsType<HirVariableDeclarationStatement>(
            changedMethod.Body.Statements[0]);
        HirIfStatement changedBranch = Assert.IsType<HirIfStatement>(changedMethod.Body.Statements[1]);
        HirReturnStatement changedBranchReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(changedBranch.Then.Statements));
        Assert.Equal(method.Id, changedMethod.Id);
        Assert.Equal(method.Body.Id, changedMethod.Body.Id);
        Assert.Equal(local.Id, changedLocal.Id);
        Assert.Equal(branch.Id, changedBranch.Id);
        Assert.Equal(branch.Then.Id, changedBranch.Then.Id);
        Assert.Null(branch.Else);
        Assert.Null(changedBranch.Else);
        Assert.Equal(branchReturn.Id, changedBranchReturn.Id);
        Assert.Equal(method.Body.Statements[2].Id, changedMethod.Body.Statements[2].Id);

        IdentitySidecarDocument identity = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> liveIds = identity.Entries.Select(static entry => entry.Id).ToHashSet();
        HashSet<SemanticId> tombstoneIds = identity.Tombstones
            .Select(static tombstone => tombstone.Id)
            .ToHashSet();
        Assert.All(oldEndpointIds, id => Assert.True(liveIds.Contains(id) ^ tombstoneIds.Contains(id)));
        Assert.All(
            new[] { local.Id, branch.Id, branch.Then.Id, branchReturn.Id },
            id => Assert.Contains(id, liveIds));
        Assert.NotEmpty(tombstoneIds.Intersect(oldEndpointIds));

        GraphDocument graph = new HirGraphProjector().Project(
            result.Analysis.Hir,
            result.Analysis.Semantics);
        GraphFunction function = Assert.Single(graph.Functions);
        Assert.Contains(function.Nodes, node => node.Id == local.Id && node.Kind == "variable");
        Assert.Contains(function.Nodes, node => node.Id == branch.Id && node.Kind == "branch");
        Assert.Contains(function.Nodes, node => node.Id == branchReturn.Id && node.Kind == "return");
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(graph).Functions);
        GraphControlFlowBranch branchFlow = Assert.Single(flow.Branches);
        Assert.True(flow.IsReducible);
        Assert.Equal(branch.Id, branchFlow.BranchNodeId);
        Assert.True(branchFlow.ThenExitsFunction);
        Assert.False(branchFlow.ElseExitsFunction);
    }

    [Fact]
    public void Apply_RejectsGeneralEndpointReplacementThatWouldDiscardAComment()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    int result = value /* preserve rationale */ + 1;
                    return result;
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/CommentedInitializerEdit.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement local = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        SemanticId intType = GetTypeId(original, local);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceLocalInitializerPatchOperation(
                local.Id,
                local.Name,
                local.Initializer!.Id,
                intType,
                new LiteralPatchExpression("2", intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Null(result.Analysis);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.InvalidReplacement);
    }

    [Fact]
    public void Apply_EditsEverySupportedForHeaderEndpointWithoutRecreatingStructure()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int count, bool enabled)
                {
                    int cursor = count;
                    for (int index = 0; index < count; index++)
                    {
                        cursor = cursor + index;
                    }

                    for (cursor = 0; cursor < count; cursor++)
                    {
                        cursor = cursor + 1;
                    }

                    return cursor;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ForHeaderEndpointEdit.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement cursor = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[0]);
        HirForStatement declarationLoop = Assert.IsType<HirForStatement>(method.Body.Statements[1]);
        HirForVariableDeclarator index = Assert.Single(declarationLoop.Declaration!.Variables);
        HirAssignmentExpression assignmentInitializer = Assert.IsType<HirAssignmentExpression>(
            Assert.Single(Assert.IsType<HirForStatement>(method.Body.Statements[2]).Initializers));
        HirForStatement assignmentLoop = Assert.IsType<HirForStatement>(method.Body.Statements[2]);
        HirUnaryExpression incrementor = Assert.IsType<HirUnaryExpression>(
            Assert.Single(declarationLoop.Incrementors));
        HirIdentifierExpression incrementorOperand = Assert.IsType<HirIdentifierExpression>(
            incrementor.Operand);
        HirIdentifierExpression assignmentTarget = Assert.IsType<HirIdentifierExpression>(
            assignmentInitializer.Target);
        SemanticId intType = GetTypeId(original, index);
        SemanticId boolType = GetTypeId(original, declarationLoop.Condition!);
        HashSet<SemanticId> replacedIds = index.Initializer!.DescendantsAndSelf()
            .Concat(declarationLoop.Condition!.DescendantsAndSelf())
            .Concat(assignmentInitializer.Value.DescendantsAndSelf())
            .Select(static node => node.Id)
            .ToHashSet();

        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics!.ConfigurationId,
            [
                new ReplaceForConditionPatchOperation(
                    declarationLoop.Id,
                    declarationLoop.Condition!.Id,
                    boolType,
                    new BinaryPatchExpression(
                        "&&",
                        new LocalReferencePatchExpression(
                            method.Parameters[1].Id,
                            method.Parameters[1].Name,
                            boolType),
                        new BinaryPatchExpression(
                            "<",
                            new LocalReferencePatchExpression(index.Id, index.Name, intType),
                            new LocalReferencePatchExpression(
                                method.Parameters[0].Id,
                                method.Parameters[0].Name,
                                intType),
                            boolType),
                        boolType)),
                new ReplaceForDeclarationInitializerPatchOperation(
                    index.Id,
                    declarationLoop.Id,
                    index.Name,
                    index.Initializer!.Id,
                    intType,
                    new LiteralPatchExpression("1", intType)),
                new ReplaceForAssignmentInitializerValuePatchOperation(
                    assignmentInitializer.Id,
                    assignmentLoop.Id,
                    0,
                    assignmentInitializer.Operator,
                    assignmentInitializer.Target.Id,
                    cursor.Id,
                    cursor.Name,
                    assignmentInitializer.Value.Id,
                    intType,
                    new LiteralPatchExpression("2", intType)),
                new ReplaceForIncrementorPatchOperation(
                    incrementor.Id,
                    declarationLoop.Id,
                    0,
                    incrementor.Operator,
                    incrementor.IsPostfix,
                    incrementor.Operand.Id,
                    index.Id,
                    index.Name,
                    intType,
                    "--",
                    false,
                    new LocalReferencePatchExpression(index.Id, index.Name, intType)),
            ]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        string patchedSource = Assert.Single(result.PatchedRequest!.Sources).Source;
        Assert.Contains(
            "for (int index = 1; enabled && (index < count); --index)",
            patchedSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "for (cursor = 2; cursor < count; cursor++)",
            patchedSource,
            StringComparison.Ordinal);
        HirMethodDeclaration changedMethod = Assert.Single(
            Assert.Single(result.Analysis!.Hir!.Types).Methods);
        HirForStatement changedDeclarationLoop = Assert.IsType<HirForStatement>(
            changedMethod.Body.Statements[1]);
        HirForStatement changedAssignmentLoop = Assert.IsType<HirForStatement>(
            changedMethod.Body.Statements[2]);
        HirForVariableDeclarator changedIndex = Assert.Single(
            changedDeclarationLoop.Declaration!.Variables);
        HirUnaryExpression changedIncrementor = Assert.IsType<HirUnaryExpression>(
            Assert.Single(changedDeclarationLoop.Incrementors));
        HirAssignmentExpression changedAssignmentInitializer = Assert.IsType<HirAssignmentExpression>(
            Assert.Single(changedAssignmentLoop.Initializers));
        Assert.Equal(declarationLoop.Id, changedDeclarationLoop.Id);
        Assert.Equal(declarationLoop.Body.Id, changedDeclarationLoop.Body.Id);
        Assert.Equal(index.Id, changedIndex.Id);
        Assert.Equal(incrementor.Id, changedIncrementor.Id);
        Assert.Equal(incrementorOperand.Id, changedIncrementor.Operand.Id);
        Assert.Equal(assignmentLoop.Id, changedAssignmentLoop.Id);
        Assert.Equal(assignmentInitializer.Id, changedAssignmentInitializer.Id);
        Assert.Equal(assignmentTarget.Id, changedAssignmentInitializer.Target.Id);
        Assert.Equal("--", changedIncrementor.Operator);
        Assert.False(changedIncrementor.IsPostfix);

        IdentitySidecarDocument identity = Assert.Single(result.Analysis.IdentitySidecars);
        HashSet<SemanticId> liveIds = identity.Entries.Select(static entry => entry.Id).ToHashSet();
        HashSet<SemanticId> tombstoneIds = identity.Tombstones
            .Select(static tombstone => tombstone.Id)
            .ToHashSet();
        Assert.All(replacedIds, id => Assert.True(liveIds.Contains(id) ^ tombstoneIds.Contains(id)));
        Assert.Contains(index.Initializer.Id, tombstoneIds);
        Assert.Contains(declarationLoop.Condition.Id, tombstoneIds);
        Assert.Contains(assignmentInitializer.Value.Id, tombstoneIds);
        GraphControlFlowFunction flow = Assert.Single(
            new GraphControlFlowAnalyzer().AnalyzeAndValidate(
                new HirGraphProjector().Project(
                    result.Analysis.Hir,
                    result.Analysis.Semantics)).Functions);
        Assert.Equal(2, flow.Loops.Count);
        Assert.Contains(flow.Loops, candidate =>
            candidate.LoopNodeId == changedDeclarationLoop.Id &&
            candidate.ContinueTargetNodeId == changedIncrementor.Id);
    }

    [Fact]
    public void Apply_RejectsForConditionReplacementForAnOmittedConditionAtomically()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static void Run(bool enabled)
                {
                    for (;;)
                    {
                        break;
                    }
                }
            }
            """;
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            source,
            "Game/OmittedForConditionEdit.cs");
        CSharpAnalysisResult original = new CSharpSemanticPipeline().Analyze(request);
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirForStatement loop = Assert.Single(
            original.Hir!.DescendantsAndSelf().OfType<HirForStatement>());
        SemanticId boolType = GetTypeId(original, Assert.Single(method.Parameters));
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir!.Id,
            original.Semantics!.ConfigurationId,
            [new ReplaceForConditionPatchOperation(
                loop.Id,
                SemanticId.FromKey("missing-for-condition"),
                boolType,
                new LiteralPatchExpression("false", boolType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.False(result.Succeeded);
        Assert.Null(result.PatchedRequest);
        Assert.Equal(source, Assert.Single(request.Sources).Source);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == SemanticPatchDiagnosticCodes.TargetKindMismatch);
    }

    [Fact]
    public void Apply_RebindsAForIncrementorOperandAndTombstonesTheOldUse()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int count)
                {
                    int cursor = 0;
                    int step = 0;
                    for (cursor = 0; cursor < count; cursor++)
                    {
                        step = step + 1;
                    }

                    return step;
                }
            }
            """;
        (CSharpCompilationRequest request, CSharpAnalysisResult original) = CreatePersistentRequest(
            source,
            "Game/ForIncrementorOperandEdit.cs");
        HirMethodDeclaration method = Assert.Single(Assert.Single(original.Hir!.Types).Methods);
        HirVariableDeclarationStatement step = Assert.IsType<HirVariableDeclarationStatement>(
            method.Body.Statements[1]);
        HirForStatement loop = Assert.IsType<HirForStatement>(method.Body.Statements[2]);
        HirUnaryExpression incrementor = Assert.IsType<HirUnaryExpression>(
            Assert.Single(loop.Incrementors));
        HirIdentifierExpression oldOperand = Assert.IsType<HirIdentifierExpression>(
            incrementor.Operand);
        SemanticId cursorId = Assert.Single(
            original.Semantics!.Nodes,
            binding => binding.NodeId == oldOperand.Id).ReferencedSymbolId!.Value;
        SemanticId intType = GetTypeId(original, incrementor);
        SemanticPatchDocument patch = SemanticPatchDocument.Create(
            original.Hir.Id,
            original.Semantics.ConfigurationId,
            [new ReplaceForIncrementorPatchOperation(
                incrementor.Id,
                loop.Id,
                0,
                incrementor.Operator,
                incrementor.IsPostfix,
                oldOperand.Id,
                cursorId,
                oldOperand.Name,
                intType,
                incrementor.Operator,
                incrementor.IsPostfix,
                new LocalReferencePatchExpression(step.Id, step.Name, intType))]);

        CSharpPatchResult result = new CSharpSourcePatchApplier().Apply(request, patch);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(
            "for (cursor = 0; cursor < count; step++)",
            Assert.Single(result.PatchedRequest!.Sources).Source,
            StringComparison.Ordinal);
        HirForStatement changedLoop = Assert.IsType<HirForStatement>(
            Assert.Single(Assert.Single(result.Analysis!.Hir!.Types).Methods).Body.Statements[2]);
        HirUnaryExpression changedIncrementor = Assert.IsType<HirUnaryExpression>(
            Assert.Single(changedLoop.Incrementors));
        Assert.Equal(loop.Id, changedLoop.Id);
        Assert.Equal(incrementor.Id, changedIncrementor.Id);
        Assert.NotEqual(oldOperand.Id, changedIncrementor.Operand.Id);
        Assert.Equal(
            step.Id,
            Assert.Single(
                result.Analysis.Semantics!.Nodes,
                binding => binding.NodeId == changedIncrementor.Operand.Id).ReferencedSymbolId);
        Assert.Contains(
            Assert.Single(result.Analysis.IdentitySidecars).Tombstones,
            tombstone => tombstone.Id == oldOperand.Id);
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
