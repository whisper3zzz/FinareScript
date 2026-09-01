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
