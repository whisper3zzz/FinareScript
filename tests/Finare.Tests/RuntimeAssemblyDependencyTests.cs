using System.Reflection;

using Finare.Runtime;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Runtime")]
public sealed class RuntimeAssemblyDependencyTests
{
    [Fact]
    public void RuntimeAndUnrealReferenceClosuresExcludeCompilerAssemblies()
    {
        AssertNoCompilerAssembly(
            typeof(CoreClrExecutionHost).Assembly,
            "Finare.Runtime");
        AssertNoCompilerAssembly(
            typeof(UnrealWorldManagedEntryPoint).Assembly,
            "Finare.Unreal");
    }

    private static void AssertNoCompilerAssembly(Assembly root, string rootName)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<Assembly>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            Assembly assembly = pending.Pop();
            string assemblyName = assembly.GetName().Name ?? "<unnamed>";
            if (!visited.Add(assemblyName))
            {
                continue;
            }

            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                string referencedName = reference.Name ?? "<unnamed>";
                if (IsForbidden(referencedName))
                {
                    Assert.Fail(
                        $"{rootName} dependency closure reaches forbidden compiler assembly " +
                        $"'{referencedName}' through '{assemblyName}'.");
                }

                if (IsFinareOrRoslyn(referencedName))
                {
                    Assembly dependency;
                    try
                    {
                        dependency = Assembly.Load(reference);
                    }
                    catch (Exception exception) when (
                        exception is FileNotFoundException or
                        FileLoadException or
                        BadImageFormatException)
                    {
                        throw new Xunit.Sdk.XunitException(
                            $"Could not load '{referencedName}' while walking the " +
                            $"{rootName} dependency closure from '{assemblyName}'.", exception);
                    }

                    pending.Push(dependency);
                }
            }
        }
    }

    private static bool IsFinareOrRoslyn(string assemblyName) =>
        assemblyName.StartsWith("Finare.", StringComparison.Ordinal) ||
        assemblyName.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal);

    private static bool IsForbidden(string assemblyName) =>
        string.Equals(assemblyName, "Finare.CSharp", StringComparison.Ordinal) ||
        string.Equals(assemblyName, "Finare.Compilation", StringComparison.Ordinal) ||
        assemblyName.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal);
}
