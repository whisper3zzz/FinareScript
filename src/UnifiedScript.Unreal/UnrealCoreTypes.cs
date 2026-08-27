namespace UnifiedScript.Unreal;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class UnrealInheritanceAttribute(string? baseTypePath, params string[] interfacePaths) : Attribute
{
    public string? BaseTypePath { get; } = baseTypePath;

    public IReadOnlyList<string> InterfacePaths { get; } = interfacePaths;
}

[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Method,
    AllowMultiple = false,
    Inherited = false)]
public sealed class UnrealBlueprintMetadataAttribute : Attribute
{
    public string? DisplayName { get; set; }

    public string? Category { get; set; }

    public string? Tooltip { get; set; }

    public string? WorldContextParameter { get; set; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class UnrealStaticFunctionAttribute : Attribute;

/// <summary>Identifies a generated, field-addressable UScriptStruct value.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class UnrealStructAttribute(string path, string nativeName) : Attribute
{
    public string Path { get; } = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A canonical Unreal struct path is required.", nameof(path))
        : path;

    public string NativeName { get; } = string.IsNullOrWhiteSpace(nativeName)
        ? throw new ArgumentException("A native Unreal struct name is required.", nameof(nativeName))
        : nativeName;
}

/// <summary>Preserves the canonical reflected field order used by ABI v3.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class UnrealStructFieldAttribute(int index) : Attribute
{
    public int Index { get; } = index >= 0
        ? index
        : throw new ArgumentOutOfRangeException(nameof(index));
}

/// <summary>Strongly typed FName value used by generated Unreal bindings.</summary>
[UnrealValue]
public readonly record struct UnrealName
{
    private readonly string? _value;

    public UnrealName(string value)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Value => _value ?? string.Empty;

    public override string ToString() => Value;
}

/// <summary>Strongly typed FText value used by generated Unreal bindings.</summary>
[UnrealValue]
public readonly record struct UnrealText
{
    private readonly string? _value;

    public UnrealText(string value)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Value => _value ?? string.Empty;

    public override string ToString() => Value;
}

/// <summary>Type-checked value for a reflected UEnum whose symbols are not yet exported.</summary>
[UnrealValue]
public readonly record struct UnrealEnumValue
{
    public UnrealEnumValue(string typePath, long value)
    {
        if (string.IsNullOrWhiteSpace(typePath))
        {
            throw new ArgumentException("A canonical Unreal enum type path is required.", nameof(typePath));
        }

        TypePath = typePath;
        Value = value;
    }

    public string TypePath { get; }

    public long Value { get; }
}

/// <summary>Asset-path value for a reflected TSoftObjectPtr without forcing a synchronous load.</summary>
[UnrealValue]
public readonly record struct UnrealSoftObjectHandle
{
    public UnrealSoftObjectHandle(string assetPath)
    {
        AssetPath = assetPath ?? throw new ArgumentNullException(nameof(assetPath));
    }

    public string AssetPath { get; }

    public bool IsNull => string.IsNullOrEmpty(AssetPath);

    public override string ToString() => AssetPath ?? string.Empty;
}

/// <summary>Asset-path value for a reflected TSoftClassPtr without loading the class.</summary>
[UnrealValue]
public readonly record struct UnrealSoftClassHandle
{
    public UnrealSoftClassHandle(string assetPath)
    {
        AssetPath = assetPath ?? throw new ArgumentNullException(nameof(assetPath));
    }

    public string AssetPath { get; }

    public bool IsNull => string.IsNullOrEmpty(AssetPath);

    public override string ToString() => AssetPath ?? string.Empty;
}

/// <summary>Double-precision FVector shape used by UE5 large world coordinates.</summary>
[UnrealValue]
public readonly record struct UnrealVector(double X, double Y, double Z)
{
    public static UnrealVector Zero => default;

    public static UnrealVector One => new(1.0, 1.0, 1.0);
}

/// <summary>Double-precision FRotator shape in Pitch/Yaw/Roll order.</summary>
[UnrealValue]
public readonly record struct UnrealRotator(double Pitch, double Yaw, double Roll)
{
    public static UnrealRotator Zero => default;
}

/// <summary>Double-precision FQuat shape in X/Y/Z/W order.</summary>
public readonly record struct UnrealQuaternion(double X, double Y, double Z, double W)
{
    public static UnrealQuaternion Identity => new(0.0, 0.0, 0.0, 1.0);
}

/// <summary>FTransform shape with quaternion rotation and double-precision vectors.</summary>
[UnrealValue]
public readonly record struct UnrealTransform(
    UnrealQuaternion Rotation,
    UnrealVector Translation,
    UnrealVector Scale3D)
{
    public static UnrealTransform Identity =>
        new(UnrealQuaternion.Identity, UnrealVector.Zero, UnrealVector.One);
}
