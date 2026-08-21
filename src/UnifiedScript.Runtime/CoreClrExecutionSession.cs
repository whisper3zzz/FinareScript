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

    public ScriptAssetDocument ScriptAssets => image.ScriptAssets;

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
        if (descriptor.IsStatic != hirMethod.IsStatic)
        {
            throw new ExecutionInvocationException(
                "UVR1004",
                "The bound and lowered method disagree about whether a receiver is required.");
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
        object? receiver = ResolveReceiver(
            loadedAssembly,
            hirType,
            hirMethod,
            invocation.Receiver);
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
            object? returnValue = method.Invoke(receiver, arguments);
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

    private object? ResolveReceiver(
        Assembly assembly,
        HirTypeDeclaration hirType,
        HirMethodDeclaration hirMethod,
        ScriptAssetReceiver? requestedReceiver)
    {
        if (hirMethod.IsStatic)
        {
            if (requestedReceiver is not null)
            {
                throw new ExecutionInvocationException(
                    "UVR1008",
                    "A static Visual method cannot receive Script Asset instance state.");
            }

            return null;
        }

        ScriptAssetDeclaration? asset = image.ScriptAssets.Assets.SingleOrDefault(candidate =>
            string.Equals(candidate.QualifiedName, hirType.QualifiedName, StringComparison.Ordinal));
        if (asset is null)
        {
            throw new ExecutionInvocationException(
                "UVR1004",
                "An instance Visual method is executable only through a declared Script Asset receiver.");
        }

        if (requestedReceiver is null || requestedReceiver.AssetId != asset.Id)
        {
            throw new ExecutionInvocationException(
                "UVR1008",
                $"Script Asset method '{hirType.QualifiedName}.{hirMethod.Name}' requires receiver '{asset.Id}'.");
        }

        Type runtimeType = assembly.GetType(hirType.QualifiedName, throwOnError: false) ??
            throw new ExecutionInvocationException(
                "UVR1007",
                $"Emitted type '{hirType.QualifiedName}' was not found in the CoreCLR image.");
        object receiver;
        try
        {
            receiver = Activator.CreateInstance(runtimeType) ??
                throw new ExecutionInvocationException(
                    "UVR1008",
                    $"Script Asset '{hirType.QualifiedName}' could not be constructed.");
        }
        catch (Exception exception) when (exception is MissingMethodException or MemberAccessException)
        {
            throw new ExecutionInvocationException(
                "UVR1008",
                $"Script Asset '{hirType.QualifiedName}' requires an accessible parameterless constructor.");
        }

        IReadOnlyDictionary<SemanticId, ScriptStateDeclaration> states = asset.States
            .ToDictionary(static state => state.Id);
        foreach (ScriptStateOverride stateOverride in requestedReceiver.StateOverrides)
        {
            if (!states.TryGetValue(stateOverride.StateId, out ScriptStateDeclaration? state))
            {
                throw new ExecutionInvocationException(
                    "UVR1008",
                    $"State '{stateOverride.StateId}' does not belong to Script Asset '{asset.Id}'.");
            }

            PropertyInfo property = runtimeType.GetProperty(
                state.Name,
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly) ??
                throw new ExecutionInvocationException(
                    "UVR1008",
                    $"Runtime state property '{state.Name}' was not found on Script Asset '{asset.Id}'.");
            if (property.SetMethod is null)
            {
                throw new ExecutionInvocationException(
                    "UVR1008",
                    $"Runtime state property '{state.Name}' is not writable.");
            }

            object? value = ExecutionValueCodec.Decode(
                new ExecutionArgument(stateOverride.Json),
                property.PropertyType);
            property.SetValue(receiver, value);
        }

        return receiver;
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
