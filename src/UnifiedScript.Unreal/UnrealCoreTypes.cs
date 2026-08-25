namespace UnifiedScript.Unreal;

/// <summary>Strongly typed FName value used by generated Unreal bindings.</summary>
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

/// <summary>Double-precision FVector shape used by UE5 large world coordinates.</summary>
public readonly record struct UnrealVector(double X, double Y, double Z)
{
    public static UnrealVector Zero => default;

    public static UnrealVector One => new(1.0, 1.0, 1.0);
}

/// <summary>Double-precision FRotator shape in Pitch/Yaw/Roll order.</summary>
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
public readonly record struct UnrealTransform(
    UnrealQuaternion Rotation,
    UnrealVector Translation,
    UnrealVector Scale3D)
{
    public static UnrealTransform Identity =>
        new(UnrealQuaternion.Identity, UnrealVector.Zero, UnrealVector.One);
}
