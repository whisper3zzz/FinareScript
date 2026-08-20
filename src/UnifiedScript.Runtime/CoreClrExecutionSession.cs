using System.Reflection;
using System.Runtime.Loader;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.CSharp;
using UnifiedScript.Semantics;

namespace UnifiedScript.Runtime;

public sealed class CoreClrExecutionSession : IDisposable
{
    private AssemblyLoadContext? loadContext;
    private Assembly? assembly;
    private readonly CSharpProgramImage image;
    private readonly object gate = new();

    internal CoreClrExecutionSession(
        AssemblyLoadContext loadContext,
        Assembly assembly,
        CSharpProgramImage image)
    {
        this.loadContext = loadContext ?? throw new ArgumentNullException(nameof(loadContext));
        this.assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        this.image = image ?? throw new ArgumentNullException(nameof(image));
        LoadContextReference = new WeakReference(loadContext, trackResurrection: false);
    }

    public WeakReference LoadContextReference { get; }

    public HirCompilationUnit Hir => image.Hir;

    public BoundCompilation Semantics => image.Semantics;

    public CSharpProgramSourceMap SourceMap => image.SourceMap;

    public IReadOnlyList<IdentitySidecarDocument> IdentitySidecars => image.IdentitySidecars;

    public SemanticId ResolveVisualMethodId(string qualifiedMethodName)
    {
        lock (gate)
        {
            return ResolveVisualMethodIdCore(qualifiedMethodName);
        }
    }

    public ExecutionResult Execute(ExecutionInvocation invocation)
    {
        lock (gate)
        {
            return ExecuteCore(invocation);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            AssemblyLoadContext? context = loadContext;
            loadContext = null;
            assembly = null;
            context?.Unload();
        }
    }

    private SemanticId ResolveVisualMethodIdCore(string qualifiedMethodName)
    {
        ObjectDisposedException.ThrowIf(loadContext is null, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedMethodName);
        HirMethodDeclaration[] matches = image.Hir.Types
            .SelectMany(type => type.Methods
                .Where(static method => method.IsVisual)
                .Where(method => string.Equals(
                    $"{type.QualifiedName}.{method.Name}",
                    qualifiedMethodName,
                    StringComparison.Ordinal)))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0].Id,
            0 => throw new ExecutionInvocationException(
                "UVR1001",
                $"Visual method '{qualifiedMethodName}' does not exist in the loaded program."),
            _ => throw new ExecutionInvocationException(
                "UVR1002",
                $"Visual method selector '{qualifiedMethodName}' is ambiguous; use its semantic ID."),
        };
    }

    private ExecutionResult ExecuteCore(ExecutionInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        Assembly loadedAssembly = assembly ?? throw new ObjectDisposedException(GetType().FullName);
        (HirTypeDeclaration hirType, HirMethodDeclaration hirMethod) = FindHirMethod(
            invocation.MethodId);
        if (!hirMethod.IsVisual)
        {
            throw new ExecutionInvocationException(
                "UVR1003",
                $"Method '{hirType.QualifiedName}.{hirMethod.Name}' is not a Visual entry point.");
        }

        MethodDescriptor descriptor = image.Semantics.Methods.Single(method =>
            method.Id == invocation.MethodId);
        if (!descriptor.IsStatic || !hirMethod.IsStatic)
        {
            throw new ExecutionInvocationException(
                "UVR1004",
                "Execution-result v0.1 requires a static Visual method; object receivers are reserved " +
                "for the engine host contract.");
        }

        if (hirMethod.IsAsync || image.Semantics.Types.Single(type =>
                type.Id == descriptor.ReturnTypeId).Kind == UnifiedTypeKind.TaskLike)
        {
            throw new ExecutionInvocationException(
                "UVR1004",
                "Execution-result v0.1 is synchronous; async Visual entry points require a later " +
                "suspension-aware host contract.");
        }

        string[] parameterTypeNames = descriptor.Parameters
            .Select(parameter => image.Semantics.Types.Single(type =>
                type.Id == parameter.TypeId).MetadataName)
            .ToArray();
        MethodInfo method = ResolveMethod(
            loadedAssembly,
            hirType,
            hirMethod,
            descriptor,
            parameterTypeNames);
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != invocation.Arguments.Count)
        {
            throw new ExecutionInvocationException(
                "UVR1005",
                $"Method '{hirType.QualifiedName}.{hirMethod.Name}' requires {parameters.Length} " +
                $"arguments, but {invocation.Arguments.Count} were supplied.");
        }

        object?[] arguments = parameters
            .Select((parameter, index) => ExecutionValueCodec.Decode(
                invocation.Arguments[index],
                parameter.ParameterType))
            .ToArray();
        try
        {
            object? returnValue = method.Invoke(null, arguments);
            TypeDescriptor returnType = image.Semantics.Types.Single(type =>
                type.Id == descriptor.ReturnTypeId);
            string json = returnType.Kind == UnifiedTypeKind.Void
                ? "null"
                : ExecutionValueCodec.EncodeScalar(returnValue, method.ReturnType);
            return ExecutionResult.Returned(
                image.Semantics.SourceCompilationId,
                image.Semantics.ConfigurationId,
                invocation.MethodId,
                new ExecutionValue(returnType.MetadataName, json));
        }
        catch (TargetInvocationException exception) when (exception.InnerException is Exception target)
        {
            return ExecutionResult.Faulted(
                image.Semantics.SourceCompilationId,
                image.Semantics.ConfigurationId,
                invocation.MethodId,
                new ExecutionFault(
                    ExecutionValueCodec.GetMetadataName(target.GetType()),
                    target.Message));
        }
    }

    private (HirTypeDeclaration Type, HirMethodDeclaration Method) FindHirMethod(
        SemanticId methodId)
    {
        foreach (HirTypeDeclaration type in image.Hir.Types)
        {
            HirMethodDeclaration? method = type.Methods.SingleOrDefault(candidate =>
                candidate.Id == methodId);
            if (method is not null)
            {
                return (type, method);
            }
        }

        throw new ExecutionInvocationException(
            "UVR1001",
            $"Visual method ID '{methodId}' does not exist in the loaded program.");
    }

    private static MethodInfo ResolveMethod(
        Assembly assembly,
        HirTypeDeclaration hirType,
        HirMethodDeclaration hirMethod,
        MethodDescriptor descriptor,
        IReadOnlyList<string> parameterTypeNames)
    {
        Type runtimeType = assembly.GetType(hirType.QualifiedName, throwOnError: false) ??
            throw new ExecutionInvocationException(
                "UVR1007",
                $"Emitted type '{hirType.QualifiedName}' was not found in the CoreCLR image.");
        MethodInfo[] matches = runtimeType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => string.Equals(method.Name, hirMethod.Name, StringComparison.Ordinal))
            .Where(method => method.IsStatic == descriptor.IsStatic)
            .Where(method => method.GetParameters().Length == descriptor.Parameters.Count)
            .Where(method => method.GetParameters()
                .Select(static parameter => ExecutionValueCodec.GetMetadataName(parameter.ParameterType))
                .SequenceEqual(parameterTypeNames))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExecutionInvocationException(
                "UVR1007",
                $"Emitted method '{descriptor.MetadataName}' was not found in the CoreCLR image."),
            _ => throw new ExecutionInvocationException(
                "UVR1007",
                $"Emitted method '{descriptor.MetadataName}' resolved ambiguously in the CoreCLR image."),
        };
    }
}
