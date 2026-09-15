using System;
using System.Linq;
using Finare;
using Finare.Unreal;
using Finare.NativeComponentCreation.Generated;

namespace Finare.NativeComponentCreation;

[ScriptAsset("NativeComponentCreation/Actor", ClassId = "us_531c398ad9ef460491b2f8b724015e63")]
[UnrealScriptClass(UnrealScriptClassKind.Actor, BaseClassPath = "/Script/Engine.Actor",
    GeneratedClassPath = "/Game/FinareAutomation/NativeComponentCreation/BP_Creator.BP_Creator_C")]
public sealed class ComponentCreator : UnrealActorScript
{
    private Actor _actor = null!;
    private SceneComponent? _root;
    private StaticMeshComponent? _mesh;
    private SceneComponent? _first;
    private SceneComponent? _second;

    protected override void OnBeginPlay()
    {
        _actor = new Actor(HostHandle, Bridge);
        var world = new FinareWorldLibrary(HostHandle, Bridge);
        UnrealClassHandle sceneClass = world.LoadClassByPath(_actor, "/Script/Engine.SceneComponent");
        UnrealClassHandle meshClass = world.LoadClassByPath(_actor, "/Script/Engine.StaticMeshComponent");
        UnrealClassHandle scriptClass = world.LoadClassByPath(_actor,
            "/Game/FinareAutomation/NativeComponentCreation/BP_Observer.BP_Observer_C");
        try
        {
            // A null class follows the UE API contract and creates nothing.
            if (_actor.AddComponentByClass(default, false, UnrealTransform.Identity, false) is not null)
                throw new InvalidOperationException("A null component class created an object.");
            _root = _actor.AddComponentByClass(sceneClass, false, UnrealTransform.Identity, false)?.As<SceneComponent>()
                ?? throw new InvalidOperationException("Root creation failed.");
            _root.ComponentTags = [new UnrealName("CreatedRoot")];
            _mesh = _actor.AddComponentByClass(meshClass, true, UnrealTransform.Identity, true)?.As<StaticMeshComponent>()
                ?? throw new InvalidOperationException("Mesh creation failed.");
            _mesh.ComponentTags = [new UnrealName("CreatedMesh")];
            _mesh.SetMobility(EComponentMobility.Movable);
            if (!_mesh.AttachToComponent(_root, default, EAttachmentRule.KeepRelative,
                    EAttachmentRule.KeepRelative, EAttachmentRule.KeepRelative, false))
                throw new InvalidOperationException("Mesh attachment failed.");
            var mesh = world.LoadObjectByPath(_actor, "/Engine/BasicShapes/Cube.Cube")?.As<StaticMesh>();
            if (!_mesh.SetStaticMesh(mesh)) throw new InvalidOperationException("Mesh assignment failed.");
            _actor.FinishAddComponent(_mesh, true, UnrealTransform.Identity);

            _first = CreateObserver(scriptClass, "First", "41");
            _second = CreateObserver(scriptClass, "Second", "73");
            _actor.Tags = [.. _actor.Tags, new UnrealName($"CreatedGeneration={Generation}")];
        }
        catch
        {
            ReleaseComponents();
            throw;
        }
    }

    private SceneComponent CreateObserver(UnrealClassHandle scriptClass, string label, string input)
    {
        var component = _actor.AddComponentByClass(scriptClass, false, UnrealTransform.Identity, true)?.As<SceneComponent>()
            ?? throw new InvalidOperationException("Script component creation failed.");
        try
        {
            component.ComponentTags = [new UnrealName(label), new UnrealName(input)];
            // No managed BeginPlay is allowed before UE finishes registration.
            if (component.ComponentTags.Count != 2) throw new InvalidOperationException("Premature component activation.");
            _actor.FinishAddComponent(component, false, UnrealTransform.Identity);
            return component;
        }
        catch
        {
            component.DestroyComponent(_actor);
            throw;
        }
    }

    [Visual]
    public void RemoveFirst() { RemoveFirstSource(); }

    private void RemoveFirstSource()
    {
        SceneComponent old = _first ?? throw new InvalidOperationException("First component is missing.");
        _first = null;
        old.DestroyComponent(_actor);
        bool rejected = false;
        try { _ = old.ComponentTags; }
        catch (UnrealInvocationException error) when (error.Error == UnrealInvocationError.ObjectHandleFailure)
        { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Destroyed component remained accessible.");
        _actor.Tags = [.. _actor.Tags, new UnrealName("DestroyedAccessRejected")];
    }

    protected override void OnEndPlay() => ReleaseComponents();

    private void ReleaseComponents()
    {
        // These four objects belong to this behavior. Other Actor components
        // remain owned by their creator and are never swept by type or name.
        _second?.DestroyComponent(_actor);
        _second = null;
        _first?.DestroyComponent(_actor);
        _first = null;
        _mesh?.DestroyComponent(_actor);
        _mesh = null;
        _root?.DestroyComponent(_actor);
        _root = null;
    }
}

[ScriptAsset("NativeComponentCreation/Observer", ClassId = "us_38784520235347f5a1d79f1fc343a613")]
[UnrealScriptClass(UnrealScriptClassKind.ActorComponent, BaseClassPath = "/Script/Engine.SceneComponent",
    GeneratedClassPath = "/Game/FinareAutomation/NativeComponentCreation/BP_Observer.BP_Observer_C")]
public sealed class ComponentObserver : UnrealActorComponentScript
{
    private SceneComponent _receiver = null!;
    private string _label = "";
    private int _ticks;

    protected override void OnBeginPlay()
    {
        _receiver = new SceneComponent(HostHandle, Bridge);
        var input = _receiver.ComponentTags;
        _label = input[0].Value;
        _receiver.ComponentTags = [.. input, new UnrealName($"Begin={input[1].Value}")];
    }

    protected override void OnTick(float deltaSeconds)
    {
        _receiver.ComponentTags = [.. _receiver.ComponentTags.Where(t => !t.Value.StartsWith("Tick=")),
            new UnrealName($"Tick={++_ticks}")];
    }

    protected override void OnEndPlay()
    {
        Actor owner = _receiver.GetOwner() ?? throw new InvalidOperationException("Component lost its owner.");
        owner.Tags = [.. owner.Tags, new UnrealName($"End={_label}")];
    }
}
