using UnifiedScript;
using UnifiedScript.Unreal.Generated;
using UI = UnifiedScript.Unreal.Generated.UI;

namespace UnifiedScript.Unreal.Demo;

/// <summary>UMG events and asynchronous resources, without a gameplay-specific bridge.</summary>
[ScriptAsset("Unreal/AsyncUiDemo", ClassId = "us_762043152e714d68b05a31426591ee03")]
[UnrealScriptClass(UnrealScriptClassKind.Actor,
    GeneratedClassPath = "/Game/UnifiedScriptDemo/BP_AsyncUiDemo.BP_AsyncUiDemo_C")]
public sealed class AsyncUiDemoScript : UnrealActorScript
{
    [Expose]
    public string WidgetClassPath { get; set; } = "/Script/UEUnifiedScriptHost.UnifiedScriptAsyncDemoWidget";

    [Expose]
    public string MeshPath { get; set; } = "/Engine/BasicShapes/Cube.Cube";

    private UnifiedScriptWorldLibrary _world = null!;
    private UnifiedScriptComponent _host = null!;
    private UI.UserWidget? _hud;
    private UI.TextBlock? _status;
    private UI.Widget? _spawnButton;
    private UI.Widget? _cancelButton;
    private IUnrealEventSubscription? _spawnSubscription;
    private IUnrealEventSubscription? _cancelSubscription;
    private CancellationTokenSource? _request;
    private UnrealClassHandle _actorClass;
    private UnrealObjectHandle _mesh;
    private string? _loadedMeshPath;
    private UnrealObjectHandle _spawned;
    private int _spawnCount;

    protected override void OnBeginPlay()
    {
        _world = new UnifiedScriptWorldLibrary(TargetHandle, Bridge);
        _host = new UnifiedScriptComponent(HostHandle, Bridge);
        _ = InitializeHudAsync();
    }

    // Visual entrypoints remain editable; Task orchestration is a source-only helper.
    [Visual]
    public void RequestSpawn()
    {
        BeginSpawn();
    }

    [Visual]
    public void CancelSpawn()
    {
        CancelPendingSpawn();
    }

    // Edit this value in C# or Graph, then reload and spawn another preview.
    [Visual]
    public float PreviewHeight()
    {
        return 100.0f;
    }

    private async Task InitializeHudAsync()
    {
        CancellationToken lifetime = Lifetime;
        try
        {
            _host.ManagedState = "UI:Initializing";
            UnrealObjectHandle widgetClass = await LoadClassAsync(WidgetClassPath, 10.0f, lifetime);
            lifetime.ThrowIfCancellationRequested();
            UnrealObjectHandle widget = new UI.WidgetBlueprintLibrary(TargetHandle, Bridge)
                .Create(TargetHandle, new UnrealClassHandle(widgetClass.Value), default);
            if (widget.IsNull) throw new InvalidOperationException("CreateWidget returned null.");
            _hud = new UI.UserWidget(widget, Bridge);
            var spawn = new UI.Button(RequireWidget("SpawnButton"), Bridge);
            var cancel = new UI.Button(RequireWidget("CancelButton"), Bridge);
            _status = new UI.TextBlock(RequireWidget("StatusText"), Bridge);
            _spawnButton = new UI.Widget(spawn.Handle, Bridge);
            _cancelButton = new UI.Widget(cancel.Handle, Bridge);
            _spawnSubscription = spawn.SubscribeOnClicked(RequestSpawn);
            _cancelSubscription = cancel.SubscribeOnClicked(CancelSpawn);
            // Wrappers hold weak handles; the viewport is the native owner of this view.
            _hud.AddToViewport();
            SetStatus("Ready");
            SetBusy(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            CleanupView();
            _host.ManagedState = $"UI:Error:{error.Message}";
        }
    }

    private UnrealObjectHandle RequireWidget(string name)
    {
        UnrealObjectHandle handle = _hud!.GetWidgetFromName(new UnrealName(name));
        return handle.IsWellFormed ? handle :
            throw new InvalidOperationException($"The HUD requires a widget named '{name}'.");
    }

    private void BeginSpawn()
    {
        if (_status is null || _request is not null) return;
        _request = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        _ = SpawnAsync(_request);
    }

    private void CancelPendingSpawn() => _request?.Cancel();

    private async Task SpawnAsync(CancellationTokenSource request)
    {
        CancellationToken lifetime = Lifetime;
        CancellationToken cancellation = request.Token;
        UnrealObjectHandle candidate = default;
        try
        {
            SetBusy(true);
            SetStatus("Loading");
            if (_actorClass.IsNull)
            {
                UnrealObjectHandle loaded = await LoadClassAsync(
                    "/Script/Engine.StaticMeshActor", 10.0f, cancellation);
                cancellation.ThrowIfCancellationRequested();
                _actorClass = new UnrealClassHandle(loaded.Value);
            }
            string requestedMesh = MeshPath;
            if (_mesh.IsNull || _loadedMeshPath != requestedMesh)
            {
                UnrealObjectHandle loaded = await LoadObjectAsync(requestedMesh, 10.0f, cancellation);
                cancellation.ThrowIfCancellationRequested();
                _mesh = loaded;
                _loadedMeshPath = requestedMesh;
            }
            cancellation.ThrowIfCancellationRequested();
            candidate = _world.SpawnActor(TargetHandle, _actorClass,
                new UnrealTransform(new UnrealQuaternion(0, 0, 0, 1),
                    new UnrealVector(200, 0, PreviewHeight()), new UnrealVector(1, 1, 1)));
            if (candidate.IsNull) throw new InvalidOperationException("SpawnActor returned null.");
            var actor = new UI.StaticMeshActor(candidate, Bridge);
            actor.SetMobility(UI.EComponentMobility.Movable);
            if (!new UI.StaticMeshComponent(actor.StaticMeshComponent, Bridge).SetStaticMesh(_mesh))
                throw new InvalidOperationException("The loaded object is not a usable StaticMesh.");

            // Preserve the previous successful preview until its replacement is ready.
            if (!_spawned.IsNull) _world.DestroyActor(TargetHandle, _spawned);
            _spawned = candidate;
            candidate = default;
            _spawnCount++;
            SetStatus($"Spawned:{_spawnCount}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!lifetime.IsCancellationRequested) SetStatus("Canceled");
        }
        catch (Exception error)
        {
            if (!lifetime.IsCancellationRequested) SetStatus($"Error:{error.Message}");
        }
        finally
        {
            if (!candidate.IsNull) _world.DestroyActor(TargetHandle, candidate);
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
            if (!lifetime.IsCancellationRequested) SetBusy(false);
        }
    }

    private void SetStatus(string text)
    {
        _status!.SetText(new UnrealText(text));
        _host.ManagedState = $"UI:{text}";
    }

    private void SetBusy(bool busy)
    {
        _spawnButton!.SetIsEnabled(!busy);
        _cancelButton!.SetIsEnabled(busy);
    }

    protected override void OnEndPlay()
    {
        // The base cancels Lifetime and the native async owner before this callback.
        CleanupView();
        if (!_spawned.IsNull)
        {
            _world.DestroyActor(TargetHandle, _spawned);
            _spawned = default;
        }
    }

    private void CleanupView()
    {
        _spawnSubscription?.Dispose();
        _cancelSubscription?.Dispose();
        _spawnSubscription = null;
        _cancelSubscription = null;
        if (_hud is not null) new UI.Widget(_hud.Handle, Bridge).RemoveFromParent();
        _hud = null;
        _status = null;
        _spawnButton = null;
        _cancelButton = null;
    }
}
