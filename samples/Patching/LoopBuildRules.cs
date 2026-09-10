using Finare;

namespace Samples.Patching;

public static class LoopBuildRules
{
    [Visual]
    public static int Run(int value, int stopAt, bool enabled)
    {
        int result = value;
        return result;
    }
}
