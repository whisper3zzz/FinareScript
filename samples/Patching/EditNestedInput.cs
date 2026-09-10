using Finare;

namespace Game;

public static class NestedInputRules
{
    [Visual]
    public static int Run(int value)
    {
        return (value + 1) * 3;
    }
}
