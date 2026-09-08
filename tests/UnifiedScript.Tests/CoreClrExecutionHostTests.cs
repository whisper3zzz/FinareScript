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
    public void Execute_AppliesStateOverrideToPrivateBaseDeclarationOnLeafReceiver()
    {
        const string source = """
            using UnifiedScript;

            namespace Tests.Execution;

            [ScriptAsset("Tests/InheritedBase")]
            public class InheritedBase
            {
                private int BaseValue { get; set; } = 7;

                protected int ReadBaseValueFromBase()
                {
                    return BaseValue;
                }
            }

            [ScriptAsset("Tests/InheritedLeaf")]
            public sealed class InheritedLeaf : InheritedBase
            {
                [Visual]
                public int ReadBaseValue()
                {
                    return ReadBaseValueFromBase();
                }
            }
            """;
        using CoreClrExecutionSession session = CreateSession(source);
        Core.ScriptAssetDeclaration asset = session.ScriptAssets.Assets.Single(
            static candidate => candidate.QualifiedName == "Tests.Execution.InheritedLeaf");
        Core.ScriptStateDeclaration state = asset.States.Single(
            static candidate => candidate.Name == "BaseValue");
        Core.SemanticId methodId = session.ResolveVisualMethodId(
            "Tests.Execution.InheritedLeaf.ReadBaseValue");

        ExecutionResult result = session.Execute(new ExecutionInvocation(
            methodId,
            [],
            new ScriptAssetReceiver(
                asset.Id,
                [new ScriptStateOverride(state.Id, "42")])));

        Assert.Equal(ExecutionOutcome.Returned, result.Outcome);
        Assert.Equal("42", result.ReturnValue!.Json);
    }

    [Fact]
    public void Execute_UsesProjectedInheritedMethodWithSelectedLeafReceiver()
    {
        const string source = """
            using UnifiedScript;

            namespace Tests.Execution;

            [ScriptAsset("Tests/InheritedBase")]
            public class InheritedBase
            {
                protected int BaseValue { get; set; } = 3;

                [Visual]
                public int ReadBaseValue()
                {
                    return BaseValue;
                }
            }

            [ScriptAsset("Tests/InheritedOverride")]
            public sealed class InheritedOverride : InheritedBase
            {
                public InheritedOverride()
                {
                    BaseValue = 77;
                }
            }

            [ScriptAsset("Tests/InheritedFollower")]
            public sealed class InheritedFollower : InheritedBase
            {
            }

            [ScriptAsset("Tests/UnrelatedReceiver")]
            public sealed class UnrelatedReceiver
            {
            }
            """;
        using CoreClrExecutionSession session = CreateSession(source);
        Core.ScriptAssetDeclaration baseAsset = session.ScriptAssets.Assets.Single(
            static candidate => candidate.QualifiedName == "Tests.Execution.InheritedBase");
        Core.ScriptAssetDeclaration overrideAsset = session.ScriptAssets.Assets.Single(
            static candidate => candidate.QualifiedName == "Tests.Execution.InheritedOverride");
        Core.ScriptAssetDeclaration followerAsset = session.ScriptAssets.Assets.Single(
            static candidate => candidate.QualifiedName == "Tests.Execution.InheritedFollower");
        Core.ScriptAssetDeclaration unrelatedAsset = session.ScriptAssets.Assets.Single(
            static candidate => candidate.QualifiedName == "Tests.Execution.UnrelatedReceiver");
        Core.ScriptStateDeclaration overrideState = Assert.Single(overrideAsset.States);
        Assert.Equal("3", Assert.Single(baseAsset.States).DefaultValue.GetRawText());
        Assert.Equal("77", overrideState.DefaultValue.GetRawText());
        Assert.Equal("3", Assert.Single(followerAsset.States).DefaultValue.GetRawText());

        // The method is declared on the base HIR type, while the selected
        // receiver asset is a projected leaf with its own effective default.
        Core.SemanticId methodId = session.ResolveVisualMethodId(
            "Tests.Execution.InheritedBase.ReadBaseValue");
        ExecutionResult overrideDefault = session.Execute(new ExecutionInvocation(
            methodId,
            [],
            new ScriptAssetReceiver(overrideAsset.Id)));
        ExecutionResult overrideExplicit = session.Execute(new ExecutionInvocation(
            methodId,
            [],
            new ScriptAssetReceiver(
                overrideAsset.Id,
                [new ScriptStateOverride(overrideState.Id, "99")])));
        ExecutionResult followerDefault = session.Execute(new ExecutionInvocation(
            methodId,
            [],
            new ScriptAssetReceiver(followerAsset.Id)));

        Assert.Equal("77", overrideDefault.ReturnValue!.Json);
        Assert.Equal("99", overrideExplicit.ReturnValue!.Json);
        Assert.Equal("3", followerDefault.ReturnValue!.Json);

        ExecutionInvocationException unrelated = Assert.Throws<ExecutionInvocationException>(() =>
            session.Execute(new ExecutionInvocation(
                methodId,
                [],
                new ScriptAssetReceiver(unrelatedAsset.Id))));
        Assert.Equal("UVR1008", unrelated.Code);
        Assert.Contains("not projected", unrelated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_AppliesInheritedNullableReferenceStateOverride()
    {
        const string source = """
            #nullable enable
            using UnifiedScript;

            namespace Tests.Execution;

            [ScriptAsset("Tests/NullableInheritedBase")]
            public class NullableInheritedBase
            {
                public string? Label { get; set; } = "base";
            }

            [ScriptAsset("Tests/NullableInheritedLeaf")]
            public sealed class NullableInheritedLeaf : NullableInheritedBase
            {
                [Visual]
                public string ReadLabel()
                {
                    return Label ?? "missing";
                }
            }
            """;
        using CoreClrExecutionSession session = CreateSession(source);
        Core.ScriptAssetDeclaration asset = session.ScriptAssets.Assets.Single(
            static candidate => candidate.QualifiedName == "Tests.Execution.NullableInheritedLeaf");
        Core.ScriptStateDeclaration state = asset.States.Single(
            static candidate => candidate.Name == "Label");
        Core.SemanticId methodId = session.ResolveVisualMethodId(
            "Tests.Execution.NullableInheritedLeaf.ReadLabel");

        ExecutionResult result = session.Execute(new ExecutionInvocation(
            methodId,
            [],
            new ScriptAssetReceiver(
                asset.Id,
                [new ScriptStateOverride(state.Id, "\"override\"")])));

        Assert.Equal(ExecutionOutcome.Returned, result.Outcome);
        Assert.Equal("\"override\"", result.ReturnValue!.Json);
    }

    [Fact]
    public void Execute_RejectsInstanceEntryPointWithoutScriptAssetReceiverContract()
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

        Assert.Equal("UVR1008", exception.Code);
        Assert.Contains("requires receiver", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_RunsScriptAssetInstanceWithTypedStateOverrides()
    {
        const string source = """
            using System;
            using UnifiedScript;

            namespace Tests.Execution;

            [ScriptAsset("Tests/DamagePolicy")]
            public sealed class DamagePolicy
            {
                public int BaseDamage { get; set; } = 20;

                public float CriticalMultiplier { get; set; } = 2.0f;

                public int MaxDamage { get; set; } = 999;

                [Visual]
                public float ApplyDamage(int incomingDamage, bool critical)
                {
                    float scaled = incomingDamage * BaseDamage;
                    if (critical)
                    {
                        scaled *= CriticalMultiplier;
                    }

                    return Math.Clamp(scaled, 0f, MaxDamage);
                }
            }
            """;
        using CoreClrExecutionSession session = CreateSession(source);
        Core.ScriptAssetDeclaration asset = Assert.Single(session.ScriptAssets.Assets);
        IReadOnlyDictionary<string, Core.ScriptStateDeclaration> states = asset.States
            .ToDictionary(static state => state.Name, StringComparer.Ordinal);
        Core.SemanticId methodId = session.ResolveVisualMethodId(
            "Tests.Execution.DamagePolicy.ApplyDamage");

        ExecutionResult defaults = session.Execute(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("12"), new ExecutionArgument("true")],
            new ScriptAssetReceiver(asset.Id)));
        ExecutionResult overridden = session.Execute(new ExecutionInvocation(
            methodId,
            [new ExecutionArgument("12"), new ExecutionArgument("true")],
            new ScriptAssetReceiver(asset.Id,
            [
                new ScriptStateOverride(states["BaseDamage"].Id, "10"),
                new ScriptStateOverride(states["CriticalMultiplier"].Id, "3.0"),
                new ScriptStateOverride(states["MaxDamage"].Id, "1000"),
            ])));

        Assert.Equal("480", defaults.ReturnValue!.Json);
        Assert.Equal("360", overridden.ReturnValue!.Json);
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
        CSharpMethodSourceMap sourceMap = Assert.Single(session.SourceMap.Methods);
        Assert.Null(sourceMap.EntryIlOffset);
        Assert.Empty(sourceMap.SequencePoints);
        Assert.Empty(sourceMap.Nodes);

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
