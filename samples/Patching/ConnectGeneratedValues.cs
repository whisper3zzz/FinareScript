using Finare;

namespace Samples.Patching;

public static class ConnectGeneratedValues
{
    [Visual]
    public static int Run(int value, int stopAt, bool enabled)
    {
        return value;
    }
}
