using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Finare;
using Finare.Unreal.Generated;
using UI = Finare.Unreal.Generated.UI;

namespace Finare.Unreal.Demo;

/// <summary>
/// A gameplay example whose three editable collection states jointly select the
/// mesh loaded for the preview actor.  The collections are native property
/// snapshots while this script asks the World-owned async service to load the
/// selected path.
/// </summary>
[ScriptAsset("Unreal/CollectionStateGameplay")]
[UnrealScriptClass(
    UnrealScriptClassKind.Actor,
    GeneratedClassPath = "/Game/UnifiedScriptDemo/BP_CollectionStateGameplay.BP_CollectionStateGameplay_C")]
public sealed partial class CollectionStateGameplayDemoScript : UnrealActorScript
{
    [Expose(InstanceEditable = true, SaveGame = true, Category = "Collection State")]
    public partial IReadOnlyList<UnrealSoftObjectHandle> Meshes { get; set; } =
    [
        new UnrealSoftObjectHandle("/Engine/BasicShapes/Cube.Cube"),
        new UnrealSoftObjectHandle("/Engine/BasicShapes/Sphere.Sphere"),
    ];

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Collection State")]
    public partial IReadOnlySet<UnrealName> EnabledChoices { get; set; } =
        new HashSet<UnrealName> { new("Cube"), new("Sphere") };

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Collection State")]
    public partial IReadOnlyDictionary<UnrealName, int> ChoiceIndices { get; set; } =
        new Dictionary<UnrealName, int>
        {
            { new UnrealName("Cube"), 0 },
            { new UnrealName("Sphere"), 1 },
        };

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Collection State")]
    public partial UnrealName SelectedChoice { get; set; } = new("Cube");

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Collection State")]
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
            ?? throw new InvalidOperationException("Collection state Actor script has no Finare component.");
        _load = LoadPreviewAsync();
    }

    [Visual]
    public void SelectSpherePreview()
    {
        SelectSpherePreviewSource();
    }

    [Visual]
    public void SelectCubePreview()
    {
        SelectCubePreviewSource();
    }

    [Visual]
    public void RetryMissingPreview()
    {
        RetryMissingPreviewSource();
    }

    private void SelectSpherePreviewSource()
    {
        if (Lifetime.IsCancellationRequested || _load is { IsCompleted: false })
        {
            return;
        }
        Meshes = CollectionStateGameplayDefaults.SphereMeshes;
        EnabledChoices = CollectionStateGameplayDefaults.SphereChoices;
        ChoiceIndices = CollectionStateGameplayDefaults.SphereChoiceIndices;
        SelectedChoice = new UnrealName("Sphere");
        RetryPreview();
    }

    private void RetryMissingPreviewSource()
    {
        if (Lifetime.IsCancellationRequested || _load is { IsCompleted: false })
        {
            return;
        }
        Meshes = CollectionStateGameplayDefaults.MissingMeshes;
        EnabledChoices = CollectionStateGameplayDefaults.MissingChoices;
        ChoiceIndices = CollectionStateGameplayDefaults.MissingChoiceIndices;
        SelectedChoice = new UnrealName("Missing");
        RetryPreview();
    }

    private void SelectCubePreviewSource()
    {
        if (Lifetime.IsCancellationRequested || _load is { IsCompleted: false })
        {
            return;
        }
        Meshes = CollectionStateGameplayDefaults.CubeMeshes;
        EnabledChoices = CollectionStateGameplayDefaults.CubeChoices;
        ChoiceIndices = CollectionStateGameplayDefaults.CubeChoiceIndices;
        SelectedChoice = new UnrealName("Cube");
        RetryPreview();
    }

    [Visual]
    public void RetryPreview()
    {
        if (!Lifetime.IsCancellationRequested && (_load == null || _load.IsCompleted))
        {
            _load = LoadPreviewAsync();
        }
    }

    private async Task LoadPreviewAsync()
    {
        CancellationToken lifetime = Lifetime;

        try
        {
            // Snapshot every collection and the scalar class before the first await.
            // A later native property write can replace the collections while an old request
            // is resolving; that request must finish with one coherent selection.
            IReadOnlyList<UnrealSoftObjectHandle> meshes = Meshes;
            IReadOnlySet<UnrealName> enabledChoices = EnabledChoices;
            IReadOnlyDictionary<UnrealName, int> choiceIndices = ChoiceIndices;
            UnrealName selectedChoice = SelectedChoice;
            UnrealSoftClassHandle actorClassPath = PreviewActorClass;
            if (lifetime.IsCancellationRequested)
            {
                return;
            }
            _host.ManagedState = "CollectionState:Loading";

            if (!enabledChoices.Contains(selectedChoice) ||
                !choiceIndices.TryGetValue(selectedChoice, out int meshIndex) ||
                (uint)meshIndex >= (uint)meshes.Count)
            {
                throw new InvalidOperationException(
                    $"Collection selection '{selectedChoice.Value}' is not enabled or indexed.");
            }
            UnrealSoftObjectHandle meshPath = meshes[meshIndex];
            UnrealObjectHandle mesh = await LoadObjectAsync(meshPath, 10.0f, lifetime);
            lifetime.ThrowIfCancellationRequested();
            UnrealObjectHandle actorClass = await LoadClassAsync(actorClassPath, 10.0f, lifetime);
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
                throw new InvalidOperationException("Collection preview SpawnActor returned null.");
            }

            try
            {
                var actor = new UI.StaticMeshActor(candidate, Bridge);
                actor.SetMobility(UI.EComponentMobility.Movable);
                if (!new UI.StaticMeshComponent((actor.StaticMeshComponent)?.Handle ?? default, Bridge).SetStaticMesh(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(mesh, Bridge)))
                {
                    throw new InvalidOperationException("The selected collection mesh was not accepted.");
                }
                if (!_spawned.IsNull)
                {
                    _world.DestroyActor(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(TargetHandle, Bridge), UnrealObject.Wrap<global::Finare.Unreal.Generated.Actor>(_spawned, Bridge));
                }
                _spawned = candidate;
                candidate = default;
                _host.ManagedState = $"CollectionState:Loaded:{meshPath.AssetPath}";
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
                _host.ManagedState = $"CollectionState:Error:{error.Message}";
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
