namespace Finare.Unreal;

/// <summary>
/// Marks a partial property whose generated accessors read and write the live
/// Unreal object. Before activation the declaration initializer supplies the
/// default; after activation the native property owns the value.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class UnrealNativePropertyAttribute : Attribute
{
    public bool Replicated { get; init; }
}
