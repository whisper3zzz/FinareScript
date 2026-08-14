using UnifiedScript;

namespace Samples;

public sealed class Door
{
    public void Open()
    {
    }
}

public sealed class DoorController
{
    public Door? Target { get; set; }

    public float OpenDelay { get; set; } = 0.25f;

    [Visual]
    public void BeginPlay()
    {
        bool hasTarget = Target != null;
        if (hasTarget)
        {
            Delay(OpenDelay);
            Target.Open();
        }
    }

    private static void Delay(float duration)
    {
    }
}

