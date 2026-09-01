namespace UnifiedScript.Unreal;

/// <summary>Reviewed Unreal class shape owned by one persistent Script Class.</summary>
public enum UnrealScriptClassKind
{
    Actor = 1,
    ActorComponent = 2,
    Object = 3,
    BlueprintFunctionLibrary = 4,
}

/// <summary>
/// Pins the native proxy/UClass shape independently from the Script Asset path
/// and stable semantic identity.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UnrealScriptClassAttribute(UnrealScriptClassKind kind) : Attribute
{
    public UnrealScriptClassKind Kind { get; } = kind;

    /// <summary>Optional exact Unreal base-class path for a future derived proxy.</summary>
    public string? BaseClassPath { get; set; }

    /// <summary>
    /// Exact generated Blueprint class path used by the managed state bridge.
    /// Product factories always emit this value; handwritten declarations may
    /// omit it until they are adopted as a persistent Script Class.
    /// </summary>
    public string? GeneratedClassPath { get; set; }
}

/// <summary>
/// Exposes a reviewed managed instance method as an overridable Blueprint event.
/// A Blueprint override replaces the managed default unless it explicitly uses
/// Call Parent Function, matching Unreal's BlueprintNativeEvent behavior.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class UnrealBlueprintEventAttribute : Attribute
{
    public UnrealBlueprintEventSuperMode SuperMode { get; init; } =
        UnrealBlueprintEventSuperMode.Explicit;
}

/// <summary>Controls how a Blueprint override reaches its managed default implementation.</summary>
public enum UnrealBlueprintEventSuperMode
{
    /// <summary>The Blueprint graph must use Call Parent Function explicitly.</summary>
    Explicit = 1,
}

/// <summary>Enhanced Input trigger phase projected onto a managed method.</summary>
public enum UnrealInputTriggerEvent
{
    Started = 1,
    Ongoing = 2,
    Triggered = 3,
    Completed = 4,
    Canceled = 5,
}

/// <summary>
/// Exact value shape expected from an Enhanced Input action. Axis2D uses
/// <see cref="UnrealVector"/> with Z set to zero so it shares the fixed ABI.
/// </summary>
public enum UnrealInputValueType
{
    Boolean = 1,
    Axis1D = 2,
    Axis2D = 3,
    Axis3D = 4,
}

/// <summary>
/// Binds one reviewed managed instance method to an Enhanced Input Action.
/// The object path normally names a UInputAction asset. Native fixture classes
/// are also accepted by the verification lane and resolve through their CDO.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class UnrealInputActionAttribute(string actionPath) : Attribute
{
    public string ActionPath { get; } = string.IsNullOrWhiteSpace(actionPath)
        ? throw new ArgumentException(
            "An exact Unreal Input Action object path is required.",
            nameof(actionPath))
        : actionPath;

    public UnrealInputTriggerEvent TriggerEvent { get; init; } =
        UnrealInputTriggerEvent.Triggered;

    public UnrealInputValueType ValueType { get; init; } =
        UnrealInputValueType.Boolean;

    public bool ConsumeInput { get; init; }
}

/// <summary>
/// Implements one exact native UInterface function through the generated
/// Script Class and the same per-World managed instance used by Blueprint calls.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class UnrealInterfaceFunctionAttribute(
    string interfacePath,
    string functionName) : Attribute
{
    public string InterfacePath { get; } = string.IsNullOrWhiteSpace(interfacePath)
        ? throw new ArgumentException(
            "An exact Unreal interface class path is required.",
            nameof(interfacePath))
        : interfacePath;

    public string FunctionName { get; } = string.IsNullOrWhiteSpace(functionName)
        ? throw new ArgumentException(
            "An exact Unreal interface function name is required.",
            nameof(functionName))
        : functionName;
}
