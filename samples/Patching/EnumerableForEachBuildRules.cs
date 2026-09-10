using System.Collections.Generic;
using Finare;

namespace Samples.Patching;

public static class EnumerableForEachBuildRules
{
    [Visual]
    public static int Last(IEnumerable<int> primary, IEnumerable<int> fallback, int seed)
    {
        int result = seed;
        return result;
    }
}
