namespace UnifiedScript;

/// <summary>
/// Marks a C# type as a UnifiedScript-authored asset. The path is engine-neutral
/// and becomes the stable lookup key consumed by editor and engine adapters.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class ScriptAssetAttribute(string path) : Attribute
{
    public string Path { get; } = path;

    /// <summary>
    /// Optional persistent class identity shared with generated Unreal assets.
    /// New Script Class scaffolds always write this value explicitly.
    /// </summary>
    public string? ClassId { get; set; }
}

/// <summary>
/// Describes how a script-state declaration is presented to an engine adapter.
/// The declaration remains owned by C# even when one or more flags are enabled.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class ExposeAttribute : Attribute
{
    public bool InstanceEditable { get; set; }

    public bool SaveGame { get; set; }

    public bool Replicated { get; set; }

    public string? Category { get; set; }
}
