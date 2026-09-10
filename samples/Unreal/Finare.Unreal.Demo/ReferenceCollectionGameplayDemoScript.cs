using System;
using System.Collections.Generic;
using Finare;
using Finare.Unreal.Generated;

namespace Finare.Unreal.Demo;

[ScriptAsset("Unreal/ReferenceCollectionGameplay")]
public sealed class ReferenceCollectionGameplayDemoScript : IUnrealManagedScript
{
    private FinareInteropFixture _fixture = null!;
    private FinareWorldLibrary _worldLibrary = null!;
    private GameplayStatics _gameplayStatics = null!;

    [Expose(Category = "Reference Containers")]
    public int FoundActorCount { get; private set; } = 0;

    [Expose(Category = "Reference Containers")]
    public int AccumulatorActorCount { get; private set; } = 0;

    [Expose(Category = "Reference Containers")]
    public int SoftPathCount { get; private set; } = 0;

    [Expose(Category = "Reference Containers")]
    public int ActorClassCount { get; private set; } = 0;

    [Visual]
    public int QueryActorCount(UnrealObjectHandle worldContextObject, UnrealClassHandle actorClass)
    {
        return QueryActorCountSource(worldContextObject, actorClass);
    }

    private int QueryActorCountSource(UnrealObjectHandle worldContextObject, UnrealClassHandle actorClass)
    {
        _gameplayStatics.GetAllActorsOfClass(worldContextObject, actorClass, out IReadOnlyList<UnrealObjectHandle> actors);
        return actors.Count;
    }

    public void OnStart(UnrealScriptContext context)
    {
        _fixture = new FinareInteropFixture(context.Host, context.Bridge);
        _worldLibrary = new FinareWorldLibrary(context.Target, context.Bridge);
        _gameplayStatics = new GameplayStatics(context.Target, context.Bridge);

        UnrealClassHandle actorClass = _worldLibrary.LoadClassByPath(
            context.Target,
            "/Script/Engine.StaticMeshActor");
        if (actorClass.IsNull)
        {
            throw new InvalidOperationException("Generated class loading returned a null Actor class.");
        }

        _gameplayStatics.GetAllActorsOfClass(
            context.Target,
            actorClass,
            out IReadOnlyList<UnrealObjectHandle> actors);
        if (actors.Count == 0)
        {
            throw new InvalidOperationException("GameplayStatics returned no Actor handles.");
        }

        IReadOnlyList<UnrealObjectHandle> echoedActors = _fixture.EchoActorArray(actors);
        IReadOnlyList<UnrealObjectHandle> actorAccumulator = Array.Empty<UnrealObjectHandle>();
        if (!_fixture.MutateActorArray(
                echoedActors,
                out IReadOnlyList<UnrealObjectHandle> outputActors,
                ref actorAccumulator) ||
            outputActors.Count != actors.Count ||
            actorAccumulator.Count != actors.Count)
        {
            throw new InvalidOperationException("Typed Actor collection exchange failed.");
        }
        _fixture.ActorArrayValue = outputActors;

        IReadOnlySet<UnrealClassHandle> classSet = new HashSet<UnrealClassHandle> { actorClass };
        _fixture.ActorClassSetValue = _fixture.EchoActorClassSet(classSet);

        IReadOnlyDictionary<UnrealName, UnrealSoftObjectHandle> softObjects =
            new Dictionary<UnrealName, UnrealSoftObjectHandle>
            {
                [new UnrealName("Item")] = new UnrealSoftObjectHandle("/Game/Items/BP_Item.BP_Item"),
                [new UnrealName("Null")] = new UnrealSoftObjectHandle(string.Empty),
            };
        IReadOnlyList<UnrealSoftClassHandle> softClasses =
        [new UnrealSoftClassHandle("/Script/Engine.Actor"), new UnrealSoftClassHandle(string.Empty)];
        IReadOnlyDictionary<UnrealName, UnrealSoftObjectHandle> objectAccumulator =
            new Dictionary<UnrealName, UnrealSoftObjectHandle>();
        if (!_fixture.MutateSoftObjectMap(
                _fixture.EchoSoftObjectMap(softObjects),
                out IReadOnlyDictionary<UnrealName, UnrealSoftObjectHandle> outputSoftObjects,
                ref objectAccumulator) ||
            outputSoftObjects.Count != softObjects.Count ||
            objectAccumulator.Count != softObjects.Count)
        {
            throw new InvalidOperationException("Typed soft object map exchange failed.");
        }
        _fixture.SoftObjectMapValue = outputSoftObjects;
        _fixture.SoftClassArrayValue = _fixture.EchoSoftClassArray(softClasses);

        FoundActorCount = outputActors.Count;
        AccumulatorActorCount = actorAccumulator.Count;
        ActorClassCount = _fixture.ActorClassSetValue.Count;
        SoftPathCount = _fixture.SoftObjectMapValue.Count + _fixture.SoftClassArrayValue.Count;
        _fixture.Int32Value = FoundActorCount;
    }

    public void OnTick(float deltaSeconds)
    {
        if (deltaSeconds < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        }
    }

    public void OnStop()
    {
        _fixture = null!;
        _worldLibrary = null!;
        _gameplayStatics = null!;
    }
}
