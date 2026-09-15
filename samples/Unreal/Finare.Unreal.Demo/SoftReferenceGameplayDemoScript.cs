using Finare;
using Finare.Unreal.Generated;
using UI = Finare.Unreal.Generated.UI;

namespace Finare.Unreal.Demo;

/// <summary>
/// Editable soft references remain paths until the gameplay operation asks the
/// World-owned async service to resolve them. Materialization does not load
/// either reference.
/// </summary>
[ScriptAsset("Unreal/SoftReferenceGameplay")]
[UnrealScriptClass(
    UnrealScriptClassKind.Actor,
    GeneratedClassPath = "/Game/UnifiedScriptDemo/BP_SoftReferenceGameplay.BP_SoftReferenceGameplay_C")]
public sealed partial class SoftReferenceGameplayDemoScript : UnrealActorScript
{
    [Expose(InstanceEditable = true, SaveGame = true, Category = "Soft References")]
    public partial UnrealSoftObjectHandle PreviewMesh { get; set; } =
        new("/Engine/BasicShapes/Cube.Cube");

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Soft References")]
    public partial UnrealSoftClassHandle PreviewActorClass { get; set; } =
        new("/Script/Engine.StaticMeshActor");

    private FinareWorldLibrary _world = null!;
    private FinareComponent _host = null!;
    private UnrealObjectHandle _spawned;
    private Task? _load;

    protected override void OnBeginPlay()
    {
        _world = new FinareWorldLibrary(TargetHandle, Bridge);
        _host = new FinareActor(HostHandle, Bridge).GetFinareComponent()
            ?? throw new InvalidOperationException("Soft reference Actor script has no Finare component.");
        _load = LoadPreviewAsync();
    }

    [Visual]
    public void RetryPreview()
    {
        if (!Lifetime.IsCancellationRequested && (_load == null || _load.IsCompleted))
        {
            _load = LoadPreviewAsync();
        }
    }

    [Visual]
    public void UseAlternatePreviewMesh()
    {
        ApplyAlternatePreviewMesh();
    }

    private void ApplyAlternatePreviewMesh()
    {
        PreviewMesh = new("/Engine/BasicShapes/Cube.Cube");
        RetryPreview();
    }

    private async Task LoadPreviewAsync()
    {
        CancellationToken lifetime = Lifetime;
        if (!lifetime.IsCancellationRequested)
        {
            _host.ManagedState = "SoftReferences:Loading";
        }
        try
        {
            UnrealObjectHandle mesh = await LoadObjectAsync(PreviewMesh, 10.0f, lifetime);
            lifetime.ThrowIfCancellationRequested();
            UnrealObjectHandle actorClass = await LoadClassAsync(PreviewActorClass, 10.0f, lifetime);
            lifetime.ThrowIfCancellationRequested();

            UnrealObjectHandle candidate = (_world.SpawnActor(
                UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(TargetHandle, Bridge),
                new UnrealClassHandle(actorClass.Value),
                new UnrealTransform(
                    UnrealQuaternion.Identity,
                    new UnrealVector(200.0, 0.0, 40.0),
                    UnrealVector.One)))?.Handle ?? default;
            if (candidate.IsNull)
            {
                throw new InvalidOperationException("Soft reference preview SpawnActor returned null.");
            }

            try
            {
                var actor = new UI.StaticMeshActor(candidate, Bridge);
                actor.SetMobility(UI.EComponentMobility.Movable);
                if (!new UI.StaticMeshComponent((actor.StaticMeshComponent)?.Handle ?? default, Bridge)
                        .SetStaticMesh(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(mesh, Bridge)))
                {
                    throw new InvalidOperationException("The loaded preview mesh was not accepted.");
                }
                if (!_spawned.IsNull)
                {
                    _world.DestroyActor(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(TargetHandle, Bridge), UnrealObject.Wrap<global::Finare.Unreal.Generated.Actor>(_spawned, Bridge));
                }
                _spawned = candidate;
                candidate = default;
                _host.ManagedState = "SoftReferences:Loaded";
            }
            finally
            {
                if (!candidate.IsNull)
                {
                    _world.DestroyActor(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(TargetHandle, Bridge), UnrealObject.Wrap<global::Finare.Unreal.Generated.Actor>(candidate, Bridge));
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!lifetime.IsCancellationRequested)
            {
                _host.ManagedState = $"SoftReferences:Error:{error.Message}";
            }
        }
        finally
        {
            _load = null;
        }
    }

    protected override void OnEndPlay()
    {
        if (!_spawned.IsNull)
        {
            _world.DestroyActor(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(TargetHandle, Bridge), UnrealObject.Wrap<global::Finare.Unreal.Generated.Actor>(_spawned, Bridge));
            _spawned = default;
        }
        _load = null;
    }
}
