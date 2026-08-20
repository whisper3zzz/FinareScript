using System.Runtime.CompilerServices;
using UnifiedScript.CSharp;
using UnifiedScript.Runtime;
using Core = UnifiedScript.Core;

namespace UnifiedScript.Tests;

public sealed class CoreClrExecutionHostTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using UnifiedScript;

        namespace Tests.Execution;

        public enum Mode
        {
            Off,
            On,
        }

        public static class Rules
        {
            [Visual]
            public static int Add(int left, int right)
            {
                return left + right;
            }

            [Visual]
            public static int Last(IEnumerable<int> values, int seed)
            {
                int result = seed;
                foreach (int value in values)
                {
                    result = value;
                }

                return result;
            }

            [Visual]
            public static int Absolute(int value)
            {
                return Math.Abs(value);
            }

            [Visual]
            public static Mode Echo(Mode value)
            {
                return value;
            }
        }
        """;

    [Fact]
    public void Execute_ReturnsCanonicalScalarValueBySemanticMethodId()
    {
        using CoreClrExecutionSession session = CreateSession();
        Core.SemanticId methodId = session.ResolveVisualMethodId("Tests.Execution.Rules.Add");

        ExecutionResult result = session.Execute(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("20"), new ExecutionArgument("22")]));

        Assert.Equal(ExecutionOutcome.Returned, result.Outcome);
        Assert.Equal("System.Int32", result.ReturnValue!.TypeMetadataName);
        Assert.Equal("42", result.ReturnValue.Json);
        Assert.Null(result.Fault);
        Assert.Equal(session.Hir.Id, result.SourceCompilationId);
        Assert.Equal(session.Semantics.ConfigurationId, result.SemanticConfigurationId);
    }

    [Fact]
    public void Execute_MaterializesJsonArrayForEnumerableParameter()
    {
        using CoreClrExecutionSession session = CreateSession();
        Core.SemanticId methodId = session.ResolveVisualMethodId("Tests.Execution.Rules.Last");

        ExecutionResult result = session.Execute(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("[1,5,9]"), new ExecutionArgument("-1")]));

        Assert.Equal(ExecutionOutcome.Returned, result.Outcome);
        Assert.Equal("9", result.ReturnValue!.Json);
    }

    [Fact]
    public void Execute_CapturesTargetExceptionWithoutLeakingReflectionWrapper()
    {
        using CoreClrExecutionSession session = CreateSession();
        Core.SemanticId methodId = session.ResolveVisualMethodId("Tests.Execution.Rules.Absolute");

        ExecutionResult result = session.Execute(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("-2147483648")]));

        Assert.Equal(ExecutionOutcome.Faulted, result.Outcome);
        Assert.Null(result.ReturnValue);
        Assert.Equal("System.OverflowException", result.Fault!.TypeMetadataName);
        Assert.NotEmpty(result.Fault.Message);
    }

    [Fact]
    public void Execute_RoundTripsSourceEnumWithoutEscapingCollectibleContext()
    {
        using CoreClrExecutionSession session = CreateSession();
        Core.SemanticId methodId = session.ResolveVisualMethodId("Tests.Execution.Rules.Echo");

        ExecutionResult result = session.Execute(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("\"On\"")]));

        Assert.Equal(ExecutionOutcome.Returned, result.Outcome);
        Assert.Equal("Tests.Execution.Mode", result.ReturnValue!.TypeMetadataName);
        Assert.Equal("\"On\"", result.ReturnValue.Json);
    }

    [Fact]
    public void Execute_RejectsArgumentCountBeforeReflectionInvocation()
    {
        using CoreClrExecutionSession session = CreateSession();
        Core.SemanticId methodId = session.ResolveVisualMethodId("Tests.Execution.Rules.Add");

        ExecutionInvocationException exception = Assert.Throws<ExecutionInvocationException>(() =>
            session.Execute(new ExecutionInvocation(
                methodId,
                [new ExecutionArgument("1")])));

        Assert.Equal("UVR1005", exception.Code);
    }

    [Fact]
    public void Execute_RejectsInstanceEntryPointUntilReceiverContractExists()
    {
        const string source = """
            using UnifiedScript;

            namespace Tests.Execution;

            public sealed class InstanceRules
            {
                [Visual]
                public int Echo(int value)
                {
                    return value;
                }
            }
            """;
        using CoreClrExecutionSession session = CreateSession(source);
        Core.SemanticId methodId = session.ResolveVisualMethodId(
            "Tests.Execution.InstanceRules.Echo");

        ExecutionInvocationException exception = Assert.Throws<ExecutionInvocationException>(() =>
            session.Execute(new ExecutionInvocation(
                methodId,
                [new ExecutionArgument("1")])));

        Assert.Equal("UVR1004", exception.Code);
        Assert.Contains("object receivers", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_RejectsAsyncEntryPointUntilSuspensionContractExists()
    {
        const string source = """
            using System.Threading.Tasks;
            using UnifiedScript;

            namespace Tests.Execution;

            public static class AsyncRules
            {
                [Visual]
                public static async Task<int> Echo(int value)
                {
                    await Task.Yield();
                    return value;
                }
            }
            """;
        using CoreClrExecutionSession session = CreateSession(source);
        Core.SemanticId methodId = session.ResolveVisualMethodId(
            "Tests.Execution.AsyncRules.Echo");

        ExecutionInvocationException exception = Assert.Throws<ExecutionInvocationException>(() =>
            session.Execute(new ExecutionInvocation(
                methodId,
                [new ExecutionArgument("1")])));

        Assert.Equal("UVR1004", exception.Code);
        Assert.Contains("suspension-aware", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_ReleasesCollectibleAssemblyLoadContext()
    {
        WeakReference loadContext = ExecuteAndDispose();

        for (int attempt = 0; attempt < 10 && loadContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(loadContext.IsAlive);
    }

    private static CoreClrExecutionSession CreateSession() =>
        CreateSession(Source);

    private static CoreClrExecutionSession CreateSession(string source) =>
        new CoreClrExecutionHost().CompileAndLoad(CSharpCompilationRequest.Single(
            source,
            "Tests/Execution/Rules.cs"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExecuteAndDispose()
    {
        CoreClrExecutionSession session = CreateSession();
        WeakReference reference = session.LoadContextReference;
        Core.SemanticId methodId = session.ResolveVisualMethodId("Tests.Execution.Rules.Echo");
        _ = session.Execute(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("\"On\"")]));
        session.Dispose();
        return reference;
    }
}
