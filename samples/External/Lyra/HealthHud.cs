using Finare;
using Finare.Unreal;
using LyraStarterGame.Finare.Generated;

namespace LyraStarterGame.Finare;

[ScriptAsset("LyraStarterGame/HealthHud", ClassId = "us_22d5d092487a142d439ae8814ca123b5")]
[UnrealScriptClass(UnrealScriptClassKind.ActorComponent, GeneratedClassPath = "/Game/FinarePilot/HealthHud.HealthHud_C")]
public sealed class HealthHud : UnrealActorComponentScript
{
    private Controller? _controller;
    private W_Healthbar_C? _widget;
    private LyraHealthComponent? _healthComponent;
    private IUnrealEventSubscription? _controllerSubscription;
    private IUnrealEventSubscription? _healthSubscription;
    private IUnrealEventSubscription? _maxHealthSubscription;
    private UnrealObjectHandle _currentPawn;
    private UnrealObjectReference? _currentPawnReference;
    private UnrealObjectHandle _boundHealthHandle;
    private UnrealObjectHandle _lastHealthCallbackHandle;
    private int _healthUpdateCount;
    private int _maxHealthUpdateCount;
    private int _pawnChangeCount;
    private int _healthBindingAttemptCount;
    private int _healthLookupCount;
    private int _healthSubscriptionCount;
    private int _receivedHealthCount;

    [Expose(InstanceEditable = true, Category = "Finare")]
    public int StartValue { get; set; }

    [Expose(InstanceEditable = true, Category = "Finare|State")]
    public UnrealName StateTag { get; set; }

    [Expose(InstanceEditable = true, Category = "Finare|State")]
    public UnrealText DisplayText { get; set; }

    [Expose(InstanceEditable = true, Category = "Finare|References")]
    public UnrealObjectHandle LinkedObject { get; set; }

    [Expose(InstanceEditable = true, Category = "Finare|References")]
    public UnrealClassHandle LinkedClass { get; set; }

    [Expose(InstanceEditable = true, Category = "Finare|References")]
    public UnrealObjectHandle WidgetHandle { get; set; }

    [Expose(InstanceEditable = true, Category = "Finare|References")]
    public UnrealObjectHandle ObservedPawnHandle { get; set; }

    [Expose(InstanceEditable = true, Category = "Finare|References")]
    public UnrealObjectHandle InitializedWidgetHandle { get; set; }

    [Expose(InstanceEditable = true, Category = "Finare|State")]
    public UnrealVector SpawnOffset { get; set; }

    public HealthHud()
    {
        StartValue = 1;
        StateTag = new UnrealName("Demo");
        DisplayText = new UnrealText("Ready");
        LinkedObject = default;
        LinkedClass = default;
        WidgetHandle = default;
        ObservedPawnHandle = default;
        InitializedWidgetHandle = default;
        SpawnOffset = new UnrealVector(100.0, 0.0, 0.0);
    }

    [Visual]
    public int AddOne(int value)
    {
        return value + 1;
    }

    [UnrealBlueprintEvent]
    public void OnManagedActivated(int amount)
    {
        StartValue += amount;
    }

    [Visual]
    public void Initialize(UnrealObjectHandle widget)
    {
        WidgetHandle = widget;
        if (!IsRunning)
        {
            return;
        }

        RebindWidget();
        UnrealObjectHandle observedPawn = ObservedPawnHandle;
        if (observedPawn.IsWellFormed &&
            InitializedWidgetHandle != WidgetHandle)
        {
            InitializeWidgetForPawn(observedPawn);
        }
        else if (observedPawn.IsWellFormed)
        {
            SyncCurrentHealthToWidget();
        }
    }

    [Visual]
    public int GetHealthUpdateCount()
    {
        return _healthUpdateCount;
    }

    [Visual]
    public int GetMaxHealthUpdateCount()
    {
        return _maxHealthUpdateCount;
    }

    [Visual]
    public int GetPawnChangeCount()
    {
        return _pawnChangeCount;
    }

    [Visual]
    public UnrealObjectHandle GetCurrentPawnHandle()
    {
        return _currentPawn;
    }

    [Visual]
    public UnrealObjectHandle GetBoundHealthHandle()
    {
        return _boundHealthHandle;
    }

    [Visual]
    public int GetReceivedHealthCount()
    {
        return _receivedHealthCount;
    }

    [Visual]
    public UnrealObjectHandle GetLastHealthCallbackHandle()
    {
        return _lastHealthCallbackHandle;
    }

    [Visual]
    public int GetHealthBindingAttemptCount()
    {
        return _healthBindingAttemptCount;
    }

    [Visual]
    public int GetHealthLookupCount()
    {
        return _healthLookupCount;
    }

    [Visual]
    public int GetHealthSubscriptionCount()
    {
        return _healthSubscriptionCount;
    }

    protected override void OnBeginPlay()
    {
        try
        {
            StartValue = AddOne(StartValue);
            _controller = new Controller(TargetHandle, Bridge);
            _controllerSubscription = _controller.SubscribeOnPossessedPawnChanged(OnPossessedPawnChanged);
            RebindWidget();
            UnrealObjectHandle pawn = _controller.GetPawn();
            UnrealObjectHandle previousPawn = ObservedPawnHandle;
            bool samePawn = previousPawn == pawn;
            HandlePawnChanged(samePawn ? default : previousPawn, pawn, !samePawn);
        }
        catch
        {
            DisposeSubscriptions();
            _controller = null;
            _widget = null;
            _healthComponent = null;
            DisposeCurrentPawnReference();
            _currentPawn = default;
            _boundHealthHandle = default;
            throw;
        }
    }

    protected override void OnTick(float deltaSeconds)
    {
    }

    protected override void OnEndPlay()
    {
        DisposeSubscriptions();
        _controller = null;
        _widget = null;
        _healthComponent = null;
        DisposeCurrentPawnReference();
        // Keep the exposed identities for stop/start comparison, but release
        // the private runtime lease while the component is stopped.
        _currentPawn = default;
        _boundHealthHandle = default;
    }

    private void RebindWidget()
    {
        _widget = WidgetHandle.IsWellFormed
            ? new W_Healthbar_C(WidgetHandle, Bridge)
            : null;
        if (_widget is null)
        {
            InitializedWidgetHandle = default;
        }

    }

    private void OnPossessedPawnChanged(
        UnrealObjectHandle oldPawn,
        UnrealObjectHandle newPawn)
    {
        _pawnChangeCount++;
        HandlePawnChanged(oldPawn, newPawn, true);
    }

    private void HandlePawnChanged(
        UnrealObjectHandle oldPawn,
        UnrealObjectHandle newPawn,
        bool notifyPawnChange)
    {
        _healthBindingAttemptCount++;
        DisposeHealthSubscriptions();
        DisposeCurrentPawnReference();
        _currentPawn = newPawn;
        ObservedPawnHandle = newPawn;
        if (newPawn.IsWellFormed)
        {
            _currentPawnReference = UnrealObjectReference.Retain(Bridge, newPawn);
        }

        bool widgetNeedsInitialization = _widget is not null &&
            InitializedWidgetHandle != WidgetHandle;
        if (_widget is not null && (notifyPawnChange || widgetNeedsInitialization))
        {
            _widget.OnPosessedPawnChanged(notifyPawnChange ? oldPawn : default, newPawn);
            InitializedWidgetHandle = WidgetHandle;
        }

        if (!newPawn.IsWellFormed)
        {
            return;
        }

        LyraHealthComponent healthLookup = new(newPawn, Bridge);
        _healthLookupCount++;
        UnrealObjectHandle healthHandle = healthLookup.FindHealthComponent(newPawn);
        _boundHealthHandle = healthHandle;
        if (!healthHandle.IsWellFormed)
        {
            return;
        }

        _healthComponent = new LyraHealthComponent(healthHandle, Bridge);
        _healthSubscription = _healthComponent.SubscribeOnHealthChanged(OnHealthChanged);
        _maxHealthSubscription = _healthComponent.SubscribeOnMaxHealthChanged(OnMaxHealthChanged);
        _healthSubscriptionCount++;
        if (!notifyPawnChange)
        {
            SyncCurrentHealthToWidget();
        }
    }

    private void InitializeWidgetForPawn(UnrealObjectHandle pawn)
    {
        if (_widget is null || !pawn.IsWellFormed)
        {
            return;
        }
        _widget.OnPosessedPawnChanged(default, pawn);
        InitializedWidgetHandle = WidgetHandle;
        SyncCurrentHealthToWidget();
    }

    private void SyncCurrentHealthToWidget()
    {
        if (_healthComponent is null || _widget is null || !_boundHealthHandle.IsWellFormed)
        {
            return;
        }
        float current = _healthComponent.GetHealth();
        _widget.NormalizedHealth = _healthComponent.GetHealthNormalized();
        _widget.InitializeBarVisuals();
        _widget.Health_Changed(_boundHealthHandle, current, current, default);
    }

    private void OnHealthChanged(
        UnrealObjectHandle healthComponent,
        float oldValue,
        float newValue,
        UnrealObjectHandle instigator)
    {
        _receivedHealthCount++;
        _lastHealthCallbackHandle = healthComponent;
        if (_healthComponent is null || _healthComponent.Handle != healthComponent)
        {
            return;
        }

        _healthUpdateCount++;
        _widget?.Health_Changed(healthComponent, oldValue, newValue, instigator);
    }

    private void OnMaxHealthChanged(
        UnrealObjectHandle healthComponent,
        float oldValue,
        float newValue,
        UnrealObjectHandle instigator)
    {
        if (_healthComponent is null || _healthComponent.Handle != healthComponent)
        {
            return;
        }

        _maxHealthUpdateCount++;
        if (_widget is null)
        {
            return;
        }

        _widget.NormalizedHealth = _healthComponent.GetHealthNormalized();
        _widget.InitializeBarVisuals();
    }

    private void DisposeHealthSubscriptions()
    {
        _healthSubscription?.Dispose();
        _healthSubscription = null;
        _maxHealthSubscription?.Dispose();
        _maxHealthSubscription = null;
        _healthComponent = null;
        _boundHealthHandle = default;
    }

    private void DisposeCurrentPawnReference()
    {
        _currentPawnReference?.Dispose();
        _currentPawnReference = null;
    }

    private void DisposeSubscriptions()
    {
        DisposeHealthSubscriptions();
        _controllerSubscription?.Dispose();
        _controllerSubscription = null;
    }
}
