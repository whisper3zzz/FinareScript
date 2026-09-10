using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Finare.CSharp;
using Finare.Unreal;
using Xunit;

namespace Finare.Tests;

public sealed class UnrealManagedStaticLibraryReloadTests
{
    [Fact]
    public unsafe void StaticLibraryKeepsOldGenerationUntilCommitAndRestoresItOnAbort()
    {
        string directory = CreateTempDirectory();
        string assemblyPath = Path.Combine(directory, "ReloadLibrary.dll");
        const string typeName = "StaticReloadFixture.ReloadLibrary";
        try
        {
            EmitLibrary(assemblyPath, marker: 1, containerType: "IReadOnlyList<int>");
            UnrealWorldManagedEntryPoint.ManagedStaticLibrary library =
                UnrealWorldManagedEntryPoint.ManagedStaticLibrary.Create(
                    worldId: 901,
                    assemblyPath,
                    typeName);

            try
            {
                Assert.Equal(1, InvokeValue(library));
                Assert.Equal([4, 5], InvokeEcho(library, [4, 5]));

                EmitLibrary(
                    assemblyPath,
                    marker: 2,
                    containerType: "IReadOnlyList<int>",
                    includeAddedCallable: true);
                int discardedGeneration = library.PrepareReload();
                Assert.Throws<InvalidOperationException>(() => library.PrepareReload());
                Assert.Throws<InvalidOperationException>(
                    () => library.CommitPreparedReload(discardedGeneration + 1));
                library.AbortPreparedReload(discardedGeneration);
                Assert.Equal(1, InvokeValue(library));

                int candidateGeneration = library.PrepareReload();
                Assert.True(candidateGeneration > discardedGeneration);
                Assert.Equal(1, InvokeValue(library));
                Assert.Equal([4, 5], InvokeEcho(library, [4, 5]));

                Assert.Equal(candidateGeneration, library.CommitPreparedReload(candidateGeneration));
                Assert.Equal(2, InvokeValue(library));
                Assert.Equal([6, 7], InvokeEcho(library, [6, 7]));

                library.AbortPreparedReload(candidateGeneration);
                Assert.Equal(1, InvokeValue(library));
                Assert.Equal([8, 9], InvokeEcho(library, [8, 9]));

                EmitLibrary(
                    assemblyPath,
                    marker: 2,
                    containerType: "IReadOnlyList<int>",
                    includeAddedCallable: true);
                int finalizedGeneration = library.PrepareReload();
                Assert.True(finalizedGeneration > candidateGeneration);
                Assert.Equal(1, InvokeValue(library));
                library.CommitPreparedReload(finalizedGeneration);
                library.FinalizePreparedReload(finalizedGeneration);
                Assert.Equal(2, InvokeValue(library));
            }
            finally
            {
                library.Dispose();
            }
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void StaticLibraryRejectsChangedOrUnreadableCandidateAndRetainsCurrent()
    {
        string directory = CreateTempDirectory();
        string assemblyPath = Path.Combine(directory, "ReloadLibrary.dll");
        const string typeName = "StaticReloadFixture.ReloadLibrary";
        try
        {
            EmitLibrary(assemblyPath, marker: 1, containerType: "IReadOnlyList<int>");
            UnrealWorldManagedEntryPoint.ManagedStaticLibrary library =
                UnrealWorldManagedEntryPoint.ManagedStaticLibrary.Create(
                    worldId: 902,
                    assemblyPath,
                    typeName);
            try
            {
                EmitLibrary(assemblyPath, marker: 2, containerType: "IReadOnlySet<int>");
                InvalidOperationException signatureError = Assert.Throws<InvalidOperationException>(
                    () => library.PrepareReload());
                Assert.Contains("Echo", signatureError.Message, StringComparison.Ordinal);
                Assert.Equal(1, InvokeValue(library));

                File.WriteAllBytes(assemblyPath, [0, 1, 2, 3, 4]);
                Assert.ThrowsAny<Exception>(() => library.PrepareReload());
                Assert.Equal(1, InvokeValue(library));
            }
            finally
            {
                library.Dispose();
            }
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void StaticLibraryDisposeReleasesPendingCandidateAndRejectsFurtherOperations()
    {
        string directory = CreateTempDirectory();
        string assemblyPath = Path.Combine(directory, "ReloadLibrary.dll");
        const string typeName = "StaticReloadFixture.ReloadLibrary";
        try
        {
            EmitLibrary(assemblyPath, marker: 1, containerType: "IReadOnlyList<int>");
            UnrealWorldManagedEntryPoint.ManagedStaticLibrary library =
                UnrealWorldManagedEntryPoint.ManagedStaticLibrary.Create(
                    worldId: 903,
                    assemblyPath,
                    typeName);
            EmitLibrary(assemblyPath, marker: 2, containerType: "IReadOnlyList<int>");
            _ = library.PrepareReload();
            IReadOnlyList<WeakReference> contexts = library.GetLoadContextReferencesForTests();

            library.Dispose();

            _ = UnrealWorldManagedEntryPoint.CollectRetiredLoadContextsForTests();
            Assert.All(contexts, reference => Assert.False(reference.IsAlive));

            Assert.Throws<ObjectDisposedException>(() => library.Invoke("Value", 1));
            Assert.Throws<ObjectDisposedException>(() => library.PrepareReload());
            Assert.Throws<ObjectDisposedException>(() => library.CommitPreparedReload(2));
            Assert.Throws<ObjectDisposedException>(() => library.AbortPreparedReload(2));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void StaticLibraryComparesStructFieldsAndStringSoftObjectIdentity()
    {
        string directory = CreateTempDirectory();
        string assemblyPath = Path.Combine(directory, "ReloadLibrary.dll");
        const string typeName = "StaticReloadFixture.ReloadLibrary";
        try
        {
            EmitContractLibrary(assemblyPath, structFieldType: "int", softParameterType: "string");
            UnrealWorldManagedEntryPoint.ManagedStaticLibrary library =
                UnrealWorldManagedEntryPoint.ManagedStaticLibrary.Create(
                    worldId: 904,
                    assemblyPath,
                    typeName);
            try
            {
                EmitContractLibrary(
                    assemblyPath,
                    structFieldType: "long",
                    softParameterType: "string");
                InvalidOperationException signatureError = Assert.Throws<InvalidOperationException>(
                    () => library.PrepareReload());
                Assert.Contains("StructContract", signatureError.Message, StringComparison.Ordinal);

                EmitContractLibrary(
                    assemblyPath,
                    structFieldType: "int",
                    softParameterType: "Finare.Unreal.UnrealSoftObjectHandle");
                signatureError = Assert.Throws<InvalidOperationException>(
                    () => library.PrepareReload());
                Assert.Contains("SoftContract", signatureError.Message, StringComparison.Ordinal);

                EmitContractLibrary(
                    assemblyPath,
                    structFieldType: "int",
                    softParameterType: "string",
                    includeAddedCallable: true);
                int candidateGeneration = library.PrepareReload();
                library.AbortPreparedReload(candidateGeneration);
            }
            finally
            {
                library.Dispose();
            }
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static unsafe int InvokeValue(
        UnrealWorldManagedEntryPoint.ManagedStaticLibrary library)
    {
        UnrealNativeValueV3 result = default;
        var frame = new UnrealNativeCallFrameV3
        {
            Results = (nint)(&result),
            ResultCapacity = 1,
        };
        library.Invoke("Value", (nint)(&frame));
        Assert.Equal(UnrealNativeCallStatusV3.Success, frame.Status);
        Assert.True(UnrealNativeValueCodecV3.TryDecode(
            in result,
            ReadOnlySpan<byte>.Empty,
            0,
            out int value));
        return value;
    }

    private static unsafe IReadOnlyList<int> InvokeEcho(
        UnrealWorldManagedEntryPoint.ManagedStaticLibrary library,
        IReadOnlyList<int> values)
    {
        Span<UnrealNativeValueV3> arguments = stackalloc UnrealNativeValueV3[1];
        Span<UnrealNativeValueV3> results = stackalloc UnrealNativeValueV3[1];
        Span<byte> arena = stackalloc byte[1024];
        int byteCount = 0;
        Assert.True(UnrealNativeValueCodecV3.TryEncode(
            values,
            arena,
            ref byteCount,
            out arguments[0],
            out _));

        fixed (UnrealNativeValueV3* argumentPointer = arguments)
        fixed (UnrealNativeValueV3* resultPointer = results)
        fixed (byte* arenaPointer = arena)
        {
            var frame = new UnrealNativeCallFrameV3
            {
                Arguments = (nint)argumentPointer,
                ArgumentCount = 1,
                Results = (nint)resultPointer,
                ResultCapacity = 1,
                ByteBuffer = (nint)arenaPointer,
                ByteCapacity = (uint)arena.Length,
                ByteCount = (uint)byteCount,
            };
            library.Invoke("Echo", (nint)(&frame));
            Assert.Equal(UnrealNativeCallStatusV3.Success, frame.Status);
            Assert.True(UnrealNativeValueCodecV3.TryDecode(
                in results[0],
                arena,
                checked((int)frame.ByteCount),
                out IReadOnlyList<int>? decoded));
            return decoded!;
        }
    }

    private static void EmitLibrary(
        string assemblyPath,
        int marker,
        string containerType,
        bool includeAddedCallable = false)
    {
        string addedCallable = includeAddedCallable
            ? "[Visual] public static int AddedCallable() => 99;"
            : string.Empty;
        string source = $$"""
            using System.Collections.Generic;
            using Finare;
            using Finare.Unreal;

            namespace StaticReloadFixture;

            [UnrealScriptClass(UnrealScriptClassKind.BlueprintFunctionLibrary)]
            public static class ReloadLibrary
            {
                [Visual]
                public static int Value() => {{marker}};

                [Visual]
                public static {{containerType}} Echo({{containerType}} values) => values;

                {{addedCallable}}
            }
            """;
        CSharpCompilation compilation = new CSharpCompilationHost().Create(
            CSharpSyntaxTree.ParseText(source, path: "ReloadLibrary.cs"),
            [MetadataReference.CreateFromFile(typeof(UnrealScriptClassAttribute).Assembly.Location)]);
        using FileStream stream = new(
            assemblyPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        EmitResult result = compilation.Emit(stream);
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics));
    }

    private static void EmitContractLibrary(
        string assemblyPath,
        string structFieldType,
        string softParameterType,
        bool includeAddedCallable = false)
    {
        string addedCallable = includeAddedCallable
            ? "[Visual] public static int AddedCallable() => 99;"
            : string.Empty;
        string source = $$"""
            using Finare;
            using Finare.Unreal;

            namespace StaticReloadFixture;

            [UnrealStruct("/Script/StaticReload.FContract", "FContract")]
            public readonly record struct ContractValue(
                [property: UnrealStructField(0, "Value")] {{structFieldType}} Value);

            [UnrealScriptClass(UnrealScriptClassKind.BlueprintFunctionLibrary)]
            public static class ReloadLibrary
            {
                [Visual]
                public static int Value() => 1;

                [Visual]
                public static int StructContract(ContractValue value) => 1;

                [Visual]
                public static string SoftContract({{softParameterType}} value) => "ok";

                {{addedCallable}}
            }
            """;
        CSharpCompilation compilation = new CSharpCompilationHost().Create(
            CSharpSyntaxTree.ParseText(source, path: "ReloadLibrary.cs"),
            [MetadataReference.CreateFromFile(typeof(UnrealScriptClassAttribute).Assembly.Location)]);
        using FileStream stream = new(
            assemblyPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        EmitResult result = compilation.Emit(stream);
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics));
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "FinareStaticLibraryReload",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // The collectible context can finish unloading after the test
            // returns; leaving the temporary directory is harmless in that
            // narrow cleanup race.
        }
    }
}
