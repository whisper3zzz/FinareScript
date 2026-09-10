using System.Collections.Generic;
using Finare;

namespace Finare.Tests.Fixtures.AuthoringInheritance;

[ScriptAsset("Game/ZBase", ClassId = "us_4a1f3c2d7e8b4a6190c5d8e2f7a314b6")]
public abstract partial class SharedBase
{
}

public abstract partial class SharedBase
{
    [Expose(InstanceEditable = true, Category = "Inherited")]
    public int Seed { get; set; } = 1;

    [Expose(InstanceEditable = true, Category = "Inherited")]
    public IReadOnlyList<int> Values { get; set; } = new List<int> { 2, 4 };

    protected SharedBase()
    {
        Seed = 3;
    }

    [Visual]
    public int ReadSeed(int value)
    {
        return Seed + value;
    }

    [Visual]
    public int Forward(int value)
    {
        return ReadSeed(value);
    }
}

[ScriptAsset("Game/AOverride")]
public sealed class AOverride : SharedBase
{
    public AOverride()
    {
        Seed = 77;
    }
}

[ScriptAsset("Game/BFollower")]
public sealed class BFollower : SharedBase
{
}
