using Finare;

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
        Door? target = Target;
        if (target != null)
        {
            Delay(OpenDelay);
            target.Open();
        }
    }

    private static void Delay(float duration)
    {
    }
}
