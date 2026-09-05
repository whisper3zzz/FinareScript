using UnifiedScript.CSharp;
using UnifiedScript.Core;
using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealReferenceContainerSupportTests
{
    [Fact]
    public void CollectionExpressionDefaultsLowerThroughTheSemanticPipeline()
    {
        const string source = """
            using System.Collections.Generic;
            using UnifiedScript;
            using UnifiedScript.Unreal;

            [ScriptAsset("Game.CollectionExpressionDefaults")]
            public sealed class CollectionExpressionDefaults
            {
                public IReadOnlyList<UnrealSoftObjectHandle> Meshes { get; set; } =
                    [new UnrealSoftObjectHandle("/Game/Props/Cube.Cube")];
                public IReadOnlyList<UnrealName> Choices { get; set; } =
                    [new UnrealName("Cube"), new UnrealName("Sphere")];
            }
            """;

        CSharpAnalysisResult result = new CSharpSemanticPipeline().Analyze(
            new CSharpCompilationRequest(
                "CollectionExpressionDefaults",
                [new CSharpSourceDocument("Game/CollectionExpressionDefaults.cs", source)],
                [typeof(UnrealObjectScript).Assembly.Location]));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        IReadOnlyDictionary<string, ScriptStateDeclaration> states =
            Assert.Single(result.ScriptAssets!.Assets).States.ToDictionary(
                static state => state.Name,
                StringComparer.Ordinal);
        Assert.Equal(
            "/Game/Props/Cube.Cube",
            states["Meshes"].DefaultValue[0].GetString());
        Assert.Equal("Cube", states["Choices"].DefaultValue[0].GetString());
        Assert.Equal("Sphere", states["Choices"].DefaultValue[1].GetString());
        Assert.Equal(
            "UnifiedScript.Unreal.UnrealSoftObjectHandle",
            states["Meshes"].UnrealType?.TypeArguments?[0].TypeMetadataName);
    }

    [Fact]
    public void CodecRejectsNativeIdentityDuplicatesBeforePublishingAContainer()
    {
        IReadOnlySet<UnrealName> names = new HashSet<UnrealName>
        {
            new UnrealName("Alpha"),
            new UnrealName("alpha"),
        };
        Span<byte> setArena = stackalloc byte[1024];
        int setBytes = 0;
        Assert.False(UnrealNativeValueCodecV3.TryEncode(
            names,
            setArena,
            ref setBytes,
            out UnrealNativeValueV3 _,
            out _));

        IReadOnlyDictionary<UnrealName, int> map = new Dictionary<UnrealName, int>
        {
            [new UnrealName("None")] = 1,
            [new UnrealName(string.Empty)] = 2,
        };
        Span<byte> mapArena = stackalloc byte[1024];
        int mapBytes = 0;
        Assert.False(UnrealNativeValueCodecV3.TryEncode(
            map,
            mapArena,
            ref mapBytes,
            out UnrealNativeValueV3 _,
            out _));
    }

    [Fact]
    public void CodecUsesCanonicalWireNamesForReferenceContainersAndRejectsWrongOuterId()
    {
        var objects = new List<UnrealObjectHandle>
        {
            UnrealObjectHandle.Create(1, 2),
            UnrealObjectHandle.Create(3, 2),
        };
        Span<byte> objectArena = stackalloc byte[1024];
        int objectByteCount = 0;
        Assert.True(UnrealNativeValueCodecV3.TryEncode<IReadOnlyList<UnrealObjectHandle>>(
            objects,
            objectArena,
            ref objectByteCount,
            out UnrealNativeValueV3 encodedObjects,
            out _));
        Assert.Equal(
            UnrealTypeIdV3.FromPath("TArray<UObject*>"),
            encodedObjects.TypeId);
        Assert.True(UnrealNativeValueCodecV3.TryCreateResultDescriptor<IReadOnlyList<UnrealObjectHandle>>(
            out UnrealNativeValueV3 objectDescriptor,
            out _));
        Assert.Equal(encodedObjects.TypeId, objectDescriptor.TypeId);
        Assert.True(UnrealNativeValueCodecV3.TryDecode(
            encodedObjects,
            objectArena,
            objectByteCount,
            out IReadOnlyList<UnrealObjectHandle>? decodedObjects));
        Assert.Equal(objects, decodedObjects);

        UnrealNativeValueV3 wrongOuterId = encodedObjects;
        wrongOuterId.TypeId = UnrealTypeIdV3.FromPath("TArray<AActor*>");
        Assert.False(UnrealNativeValueCodecV3.TryDecode(
            wrongOuterId,
            objectArena,
            objectByteCount,
            out IReadOnlyList<UnrealObjectHandle>? _));

        IReadOnlySet<UnrealClassHandle> classes = new HashSet<UnrealClassHandle>
        {
            UnrealClassHandle.Create(4, 3),
        };
        Span<byte> classArena = stackalloc byte[1024];
        int classByteCount = 0;
        Assert.True(UnrealNativeValueCodecV3.TryEncode<IReadOnlySet<UnrealClassHandle>>(
            classes,
            classArena,
            ref classByteCount,
            out UnrealNativeValueV3 encodedClasses,
            out _));
        Assert.Equal(
            UnrealTypeIdV3.FromPath("TSet<UClass*>"),
            encodedClasses.TypeId);
        Assert.True(UnrealNativeValueCodecV3.TryDecode(
            encodedClasses,
            classArena,
            classByteCount,
            out IReadOnlySet<UnrealClassHandle>? decodedClasses));
        Assert.Equal(classes, decodedClasses);

        IReadOnlyDictionary<UnrealSoftObjectHandle, UnrealSoftClassHandle> soft =
            new Dictionary<UnrealSoftObjectHandle, UnrealSoftClassHandle>
            {
                [new UnrealSoftObjectHandle("/Game/Items/BP_Item.BP_Item")] =
                    new UnrealSoftClassHandle("/Game/Pawns/BP_Pawn.BP_Pawn_C"),
                [new UnrealSoftObjectHandle(string.Empty)] =
                    new UnrealSoftClassHandle(string.Empty),
            };
        Span<byte> softArena = stackalloc byte[2048];
        int softByteCount = 0;
        Assert.True(UnrealNativeValueCodecV3.TryEncode<
            IReadOnlyDictionary<UnrealSoftObjectHandle, UnrealSoftClassHandle>>(
            soft,
            softArena,
            ref softByteCount,
            out UnrealNativeValueV3 encodedSoft,
            out _));
        Assert.Equal(
            UnrealTypeIdV3.FromPath("TMap<FSoftObjectPath,FSoftClassPath>"),
            encodedSoft.TypeId);
        Assert.True(UnrealNativeValueCodecV3.TryCreateResultDescriptor<
            IReadOnlyDictionary<UnrealSoftObjectHandle, UnrealSoftClassHandle>>(
            out UnrealNativeValueV3 softDescriptor,
            out _));
        Assert.Equal(encodedSoft.TypeId, softDescriptor.TypeId);
        Assert.True(UnrealNativeValueCodecV3.TryDecode(
            encodedSoft,
            softArena,
            softByteCount,
            out IReadOnlyDictionary<UnrealSoftObjectHandle, UnrealSoftClassHandle>? decodedSoft));
        Assert.Equal(soft, decodedSoft);

        Span<byte> primitiveArena = stackalloc byte[512];
        int primitiveByteCount = 0;
        Assert.True(UnrealNativeValueCodecV3.TryEncode<IReadOnlyList<int>>(
            new[] { 7, 11 },
            primitiveArena,
            ref primitiveByteCount,
            out UnrealNativeValueV3 encodedPrimitives,
            out _));
        Assert.Equal(UnrealTypeIdV3.FromPath("TArray<int32>"), encodedPrimitives.TypeId);
    }

    [Fact]
    public void NativeGeneratorUsesWireNamesForContainerDescriptorsReadsAndWrites()
    {
        UnrealReflectionManifest manifest = CreateContainerManifest();
        UnrealNativeThunkGenerationResult result = new UnrealNativeThunkGenerator().Generate(manifest);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            $"0x{UnrealTypeIdV3.FromPath("TArray<UObject*>"):X16}ull",
            result.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            $"0x{UnrealTypeIdV3.FromPath("TSet<UClass*>"):X16}ull",
            result.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            $"0x{UnrealTypeIdV3.FromPath("TMap<FSoftObjectPath,FSoftClassPath>"):X16}ull",
            result.Source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"0x{UnrealTypeIdV3.FromPath("TArray<AActor*>"):X16}ull",
            result.Source,
            StringComparison.Ordinal);
        Assert.Contains("TArray<AActor*> NativeValue", result.Source, StringComparison.Ordinal);
        Assert.Contains("TSet<TSubclassOf<AActor>> NativeValue", result.Source, StringComparison.Ordinal);
        Assert.Contains(
            "TMap<TSoftObjectPtr<UObject>,TSoftClassPtr<AActor>> NativeValue",
            result.Source,
            StringComparison.Ordinal);
        Assert.Contains("ExchangeObjects(Argument0, Argument1, Argument2)", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeGeneratorEmitsTypedClassDecoderForSupportedStructFields()
    {
        const string structPath = "/Script/FixtureRuntime.TypedClassValue";
        const string ownerPath = "/Script/FixtureRuntime.StructReferenceFixture";
        UnrealTypeReference actorClass = new(
            UnrealValueKind.Class,
            "TSubclassOf<AActor>",
            "/Script/Engine.Actor",
            HeaderPath: "GameFramework/Actor.h");
        UnrealReflectedType valueStruct = new(
            structPath,
            "TypedClassValue",
            "FTypedClassValue",
            "FixtureRuntime",
            UnrealReflectedTypeKind.Struct,
            null,
            new UnrealTypeTraits(false, true),
            [new UnrealReflectedProperty(
                $"{structPath}:ClassValue",
                "ClassValue",
                actorClass,
                new UnrealPropertyAccess(true, true, true))],
            [],
            "TypedClassValue.h");
        UnrealTypeReference valueStructReference = new(
            UnrealValueKind.Struct,
            "FTypedClassValue",
            structPath,
            HeaderPath: "TypedClassValue.h");
        UnrealReflectionManifest manifest = UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 8, 1, "UE5"),
            ["FixtureRuntime"],
            [
                valueStruct,
                new UnrealReflectedType(
                    ownerPath,
                    "StructReferenceFixture",
                    "UStructReferenceFixture",
                    "FixtureRuntime",
                    UnrealReflectedTypeKind.Class,
                    "/Script/CoreUObject.Object",
                    new UnrealTypeTraits(false, true),
                    [new UnrealReflectedProperty(
                        $"{ownerPath}:Value",
                        "Value",
                        valueStructReference,
                        new UnrealPropertyAccess(true, true, true))],
                    [],
                    "StructReferenceFixture.h"),
            ]);

        UnrealNativeThunkGenerationResult result = new UnrealNativeThunkGenerator().Generate(manifest);

        Assert.Empty(result.Diagnostics);
        Assert.Contains("TSubclassOf<TObjectType>& OutClass", result.Source, StringComparison.Ordinal);
        Assert.Contains("ResolvedClass->IsChildOf(TObjectType::StaticClass())", result.Source, StringComparison.Ordinal);
        Assert.Contains("TSubclassOf<AActor> DecodedField0", result.Source, StringComparison.Ordinal);
        Assert.Contains("DecodeObject(Registry, Fields[0]", result.Source, StringComparison.Ordinal);
    }

    private static UnrealReflectionManifest CreateContainerManifest()
    {
        const string ownerPath = "/Script/FixtureRuntime.ReferenceFixture";
        UnrealTypeReference actor = new(
            UnrealValueKind.Object,
            "AActor*",
            "/Script/Engine.Actor",
            HeaderPath: "GameFramework/Actor.h");
        UnrealTypeReference actorArray = new(
            UnrealValueKind.Array,
            "TArray<AActor*>",
            ElementType: actor,
            HeaderPath: "Containers/Array.h");
        UnrealTypeReference actorClass = new(
            UnrealValueKind.Class,
            "TSubclassOf<AActor>",
            "/Script/Engine.Actor",
            HeaderPath: "GameFramework/Actor.h");
        UnrealTypeReference actorClassSet = new(
            UnrealValueKind.Set,
            "TSet<TSubclassOf<AActor>>",
            ElementType: actorClass,
            HeaderPath: "Containers/Set.h");
        UnrealTypeReference softObject = new(
            UnrealValueKind.SoftObject,
            "TSoftObjectPtr<UObject>",
            "/Script/CoreUObject.Object",
            HeaderPath: "UObject/SoftObjectPath.h");
        UnrealTypeReference softClass = new(
            UnrealValueKind.SoftClass,
            "TSoftClassPtr<AActor>",
            "/Script/Engine.Actor",
            HeaderPath: "UObject/SoftObjectPath.h");
        UnrealTypeReference softMap = new(
            UnrealValueKind.Map,
            "TMap<TSoftObjectPtr<UObject>,TSoftClassPtr<AActor>>",
            KeyType: softObject,
            ValueType: softClass,
            HeaderPath: "Containers/Map.h");
        UnrealTypeReference voidType = new(UnrealValueKind.Void, "void");
        UnrealReflectedParameter Input(string name, UnrealTypeReference type) =>
            new(name, type, UnrealParameterDirection.Input);
        UnrealReflectedParameter Output(string name, UnrealTypeReference type) =>
            new(name, type, UnrealParameterDirection.Output);
        UnrealReflectedParameter Ref(string name, UnrealTypeReference type) =>
            new(name, type, UnrealParameterDirection.InputOutput);

        return UnrealReflectionManifest.Create(
            new UnrealEngineIdentity(5, 8, 1, "UE5"),
            ["FixtureRuntime"],
            [new UnrealReflectedType(
                ownerPath,
                "ReferenceFixture",
                "UReferenceFixture",
                "FixtureRuntime",
                UnrealReflectedTypeKind.Class,
                "/Script/CoreUObject.Object",
                new UnrealTypeTraits(false, true),
                [
                    new UnrealReflectedProperty(
                        $"{ownerPath}:Actors",
                        "Actors",
                        actorArray,
                        new UnrealPropertyAccess(true, true, true)),
                    new UnrealReflectedProperty(
                        $"{ownerPath}:Classes",
                        "Classes",
                        actorClassSet,
                        new UnrealPropertyAccess(true, true, true)),
                    new UnrealReflectedProperty(
                        $"{ownerPath}:SoftPaths",
                        "SoftPaths",
                        softMap,
                        new UnrealPropertyAccess(true, true, true)),
                ],
                [new UnrealReflectedFunction(
                    $"{ownerPath}:ExchangeObjects",
                    "ExchangeObjects",
                    actorArray,
                    [
                        Input("Input", actorArray),
                        Ref("Accumulator", actorArray),
                        Output("Output", actorArray),
                    ],
                    new UnrealFunctionTraits(false, false, true))],
                "ReferenceFixture.h")]);
    }
}
