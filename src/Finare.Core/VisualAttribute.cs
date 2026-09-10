namespace Finare;

/// <summary>
/// Marks a method as belonging to the losslessly visualizable C# profile.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class VisualAttribute : Attribute;

