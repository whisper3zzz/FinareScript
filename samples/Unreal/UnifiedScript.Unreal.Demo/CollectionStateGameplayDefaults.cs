using System.Collections.Generic;
using UnifiedScript.Unreal;

namespace UnifiedScript.Unreal.Demo;

internal static class CollectionStateGameplayDefaults
{
    public static IReadOnlyList<UnrealSoftObjectHandle> CubeMeshes { get; } =
        new[] { new UnrealSoftObjectHandle("/Engine/BasicShapes/Cube.Cube") };

    public static IReadOnlySet<UnrealName> CubeChoices { get; } =
        new HashSet<UnrealName> { new("Cube") };

    public static IReadOnlyDictionary<UnrealName, int> CubeChoiceIndices { get; } =
        new Dictionary<UnrealName, int> { [new UnrealName("Cube")] = 0 };

    public static IReadOnlyList<UnrealSoftObjectHandle> SphereMeshes { get; } =
        new[] { new UnrealSoftObjectHandle("/Engine/BasicShapes/Sphere.Sphere") };

    public static IReadOnlySet<UnrealName> SphereChoices { get; } =
        new HashSet<UnrealName> { new("Sphere") };

    public static IReadOnlyDictionary<UnrealName, int> SphereChoiceIndices { get; } =
        new Dictionary<UnrealName, int> { [new UnrealName("Sphere")] = 0 };

    public static IReadOnlyList<UnrealSoftObjectHandle> MissingMeshes { get; } =
        new[] { new UnrealSoftObjectHandle("/Game/UnifiedScriptDemo/__MissingCollectionPreview.Asset") };

    public static IReadOnlySet<UnrealName> MissingChoices { get; } =
        new HashSet<UnrealName> { new("Missing") };

    public static IReadOnlyDictionary<UnrealName, int> MissingChoiceIndices { get; } =
        new Dictionary<UnrealName, int> { [new UnrealName("Missing")] = 0 };
}
