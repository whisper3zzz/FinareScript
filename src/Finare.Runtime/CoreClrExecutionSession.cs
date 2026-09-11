using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Finare.Core;
using Finare.Core.Hir;
using Finare.CSharp;
using Finare.Semantics;

namespace Finare.Runtime;

public sealed class CoreClrExecutionSession : IDisposable
{
    private const string UnrealScriptClassAttributeFullName =
        "Finare.Unreal.UnrealScriptClassAttribute";
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

    /// <summary>
    /// Resolves any exported Script Asset function, including source-only
    /// entries. The existing visual resolver intentionally remains HIR-only
    /// so graph/debug callers cannot accidentally target opaque code.
    /// </summary>
    public SemanticId ResolveExportedMethodId(string qualifiedMethodName)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(loadContext is null, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedMethodName);
            ScriptFunctionDeclaration[] matches = image.ScriptAssets.Assets
                .SelectMany(static asset => asset.Functions)
                .Where(function => string.Equals(function.QualifiedName, qualifiedMethodName, StringComparison.Ordinal) ||
                    function.QualifiedName.StartsWith(qualifiedMethodName + "(", StringComparison.Ordinal))
                .GroupBy(static function => function.Id)
                .Select(static group => group.First())
                .ToArray();
            return matches.Length switch
            {
                1 => matches[0].Id,
                0 => throw new ExecutionInvocationException(
                    "UVR1001",
                    $"Exported method '{qualifiedMethodName}' does not exist in the loaded program."),
                _ => throw new ExecutionInvocationException(
                    "UVR1002",
                    $"Exported method selector '{qualifiedMethodName}' is ambiguous; use its semantic ID."),
            };
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
        (HirTypeDeclaration? hirType, HirMethodDeclaration? hirMethod) = FindHirMethodOrNull(
            invocation.MethodId);
        ScriptAssetDeclaration? asset = FindAssetForFunction(invocation.MethodId, invocation.Receiver);
        ScriptFunctionDeclaration? declaration = asset?.Functions.SingleOrDefault(function =>
            function.Id == invocation.MethodId) ?? image.ScriptAssets.Assets
                .SelectMany(static candidate => candidate.Functions)
                .FirstOrDefault(function => function.Id == invocation.MethodId);
        if (hirMethod is null && declaration is null)
        {
            throw new ExecutionInvocationException(
                "UVR1001",
                $"Exported method ID '{invocation.MethodId}' does not exist in the loaded program.");
        }

        if (hirMethod is not null && !hirMethod.IsVisual)
        {
            throw new ExecutionInvocationException(
                "UVR1003",
                $"Method '{hirType!.QualifiedName}.{hirMethod.Name}' is not a Visual entry point.");
        }

        MethodDescriptor descriptor = image.Semantics.Methods.SingleOrDefault(method =>
            method.Id == invocation.MethodId) ?? throw new ExecutionInvocationException(
                "UVR1001",
                $"Exported method ID '{invocation.MethodId}' has no semantic descriptor.");
        bool isStatic = hirMethod?.IsStatic ?? declaration!.IsStatic;
        if (descriptor.IsStatic != isStatic)
        {
            throw new ExecutionInvocationException(
                "UVR1004",
                "The bound and lowered method disagree about whether a receiver is required.");
        }

        bool isAsync = hirMethod?.IsAsync == true || image.Semantics.Types.Single(type =>
                type.Id == descriptor.ReturnTypeId).Kind == UnifiedTypeKind.TaskLike;
        if (isAsync)
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
        MethodInfo method = hirMethod is not null
            ? ResolveMethod(loadedAssembly, hirType!, hirMethod, descriptor, parameterTypeNames)
            : ResolveExportedMethod(loadedAssembly, invocation.MethodId, descriptor, declaration);
        if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is not null ||
            method.GetCustomAttribute<AsyncIteratorStateMachineAttribute>() is not null)
        {
            throw new ExecutionInvocationException(
                "UVR1004",
                "Execution-result v0.1 is synchronous; async exported entry points require a later suspension-aware host contract.");
        }
        object? receiver = ResolveReceiver(
            loadedAssembly,
            method.DeclaringType ?? throw new ExecutionInvocationException(
                "UVR1007",
                $"Emitted method '{descriptor.MetadataName}' has no declaring type."),
            isStatic,
            invocation.MethodId,
            declaration?.QualifiedName ?? descriptor.MetadataName,
            invocation.Receiver,
            asset);
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != invocation.Arguments.Count)
        {
            throw new ExecutionInvocationException(
                "UVR1005",
                $"Method '{declaration?.QualifiedName ?? descriptor.MetadataName}' requires {parameters.Length} " +
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
        Type declaringRuntimeType,
        bool isStatic,
        SemanticId methodId,
        string methodDisplayName,
        ScriptAssetReceiver? requestedReceiver,
        ScriptAssetDeclaration? receiverAsset)
    {
        if (isStatic)
        {
            if (requestedReceiver is not null)
            {
                throw new ExecutionInvocationException(
                    "UVR1008",
                    "A static Visual method cannot receive Script Asset instance state.");
            }

            return null;
        }

        if (requestedReceiver is null)
        {
            throw new ExecutionInvocationException(
                "UVR1008",
                $"Script Asset method '{methodDisplayName}' requires receiver asset.");
        }

        if (receiverAsset is null)
        {
            throw new ExecutionInvocationException(
                "UVR1008",
                $"Receiver Script Asset '{requestedReceiver.AssetId}' is unknown.");
        }

        if (receiverAsset.Id != requestedReceiver.AssetId)
        {
            throw new ExecutionInvocationException(
                "UVR1008",
                $"Receiver Script Asset '{requestedReceiver.AssetId}' is unknown.");
        }

        if (!receiverAsset.Functions.Any(candidate => candidate.Id == methodId))
        {
            throw new ExecutionInvocationException(
                "UVR1008",
                $"Exported method '{methodDisplayName}' is not projected by receiver asset '{receiverAsset.Id}'.");
        }

        Type runtimeType = assembly.GetType(receiverAsset.QualifiedName, throwOnError: false) ??
            throw new ExecutionInvocationException(
                "UVR1007",
                $"Emitted receiver type '{receiverAsset.QualifiedName}' was not found in the CoreCLR image.");
        if (!declaringRuntimeType.IsAssignableFrom(runtimeType))
        {
            throw new ExecutionInvocationException(
                "UVR1008",
                    $"Receiver asset '{receiverAsset.Id}' type '{receiverAsset.QualifiedName}' is unrelated to declaring type '{declaringRuntimeType.FullName}'.");
        }

        object receiver;
        try
        {
            receiver = Activator.CreateInstance(runtimeType) ??
                throw new ExecutionInvocationException(
                    "UVR1008",
                    $"Script Asset '{receiverAsset.QualifiedName}' could not be constructed.");
        }
        catch (Exception exception) when (exception is MissingMethodException or MemberAccessException)
        {
            throw new ExecutionInvocationException(
                "UVR1008",
                $"Script Asset '{receiverAsset.QualifiedName}' requires an accessible parameterless constructor.");
        }

        IReadOnlyDictionary<SemanticId, ScriptStateDeclaration> states = receiverAsset.States
            .ToDictionary(static state => state.Id);
        foreach (ScriptStateOverride stateOverride in requestedReceiver.StateOverrides)
        {
            if (!states.TryGetValue(stateOverride.StateId, out ScriptStateDeclaration? state))
            {
                throw new ExecutionInvocationException(
                    "UVR1008",
                    $"State '{stateOverride.StateId}' does not belong to Script Asset '{receiverAsset.Id}'.");
            }

            PropertyInfo? property = null;
            bool sawNonCandidateHider = false;
            StringComparison propertyNameComparison = HasUnrealScriptClassAttribute(runtimeType)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
            for (Type? current = runtimeType; current is not null; current = current.BaseType)
            {
                PropertyInfo[] matches = current.GetProperties(
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(candidate => string.Equals(
                        candidate.Name,
                        state.Name,
                        propertyNameComparison))
                    .ToArray();
                if (matches.Length == 0)
                {
                    continue;
                }

                if (matches.Length != 1)
                {
                    throw new ExecutionInvocationException(
                        "UVR1008",
                        $"Runtime state property '{state.Name}' is ambiguous in the inheritance hierarchy for Script Asset '{receiverAsset.Id}'.");
                }

                PropertyInfo candidate = matches[0];
                bool propertyIsStatic = candidate.GetMethod?.IsStatic == true ||
                    candidate.SetMethod?.IsStatic == true;
                bool ownsAllProperties = current.GetCustomAttribute<ScriptAssetAttribute>(inherit: false) is not null;
                bool isCandidate = !propertyIsStatic &&
                    (ownsAllProperties || candidate.GetCustomAttribute<ExposeAttribute>(inherit: false) is not null);
                if (!isCandidate)
                {
                    if (!propertyIsStatic && property is null)
                    {
                        sawNonCandidateHider = true;
                    }
                    continue;
                }

                if (property is not null || sawNonCandidateHider)
                {
                    throw new ExecutionInvocationException(
                        "UVR1008",
                        $"Runtime state property '{state.Name}' is ambiguous in the inheritance hierarchy for Script Asset '{receiverAsset.Id}'.");
                }

                property = candidate;
            }

            if (property is null)
            {
                throw new ExecutionInvocationException(
                    "UVR1008",
                    $"Runtime state property '{state.Name}' was not found on Script Asset '{receiverAsset.Id}'.");
            }

            string runtimeTypeName = ExecutionValueCodec.GetMetadataName(property.PropertyType);
            if (!MetadataTypesMatch(runtimeTypeName, state.TypeMetadataName))
            {
                throw new ExecutionInvocationException(
                    "UVR1008",
                    $"Runtime state property '{state.Name}' has type '{runtimeTypeName}', expected '{state.TypeMetadataName}'.");
            }
            if (property.GetIndexParameters().Length != 0 ||
                property.GetMethod is null ||
                property.SetMethod is null)
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

    private static bool MetadataTypesMatch(string runtimeTypeName, string declaredTypeName) =>
        string.Equals(runtimeTypeName, declaredTypeName, StringComparison.Ordinal) ||
        string.Equals(
            runtimeTypeName.Replace("?", string.Empty, StringComparison.Ordinal),
            declaredTypeName.Replace("?", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);

    private static bool HasUnrealScriptClassAttribute(Type type) => type
        .GetCustomAttributes(inherit: false)
        .Any(attribute => string.Equals(
            attribute.GetType().FullName,
            UnrealScriptClassAttributeFullName,
            StringComparison.Ordinal));

    private ScriptAssetDeclaration? FindAssetForFunction(
        SemanticId methodId,
        ScriptAssetReceiver? requestedReceiver)
    {
        if (requestedReceiver is not null)
        {
            return image.ScriptAssets.Assets.SingleOrDefault(asset =>
                asset.Id == requestedReceiver.AssetId);
        }

        ScriptAssetDeclaration[] matches = image.ScriptAssets.Assets
            .Where(asset => asset.Functions.Any(function => function.Id == methodId))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private MethodInfo ResolveExportedMethod(
        Assembly loadedAssembly,
        SemanticId methodId,
        MethodDescriptor descriptor,
        ScriptFunctionDeclaration? declaration)
    {
        CSharpMethodSourceMap sourceMap = image.SourceMap.Methods.SingleOrDefault(method =>
            method.MethodId == methodId) ?? throw new ExecutionInvocationException(
                "UVR1007",
                $"Exported method '{methodId}' has no source-map method metadata.");
        try
        {
            MethodBase? resolved = loadedAssembly.ManifestModule.ResolveMethod(sourceMap.MetadataToken);
            if (resolved is MethodInfo method)
            {
                string expectedDeclaringType = image.Semantics.Types.Single(type =>
                    type.Id == descriptor.ContainingTypeId).MetadataName;
                string actualDeclaringType = method.DeclaringType is null
                    ? string.Empty
                    : ExecutionValueCodec.GetMetadataName(method.DeclaringType);
                bool parameterSignatureMatches = method.GetParameters().Length == descriptor.Parameters.Count &&
                    method.GetParameters()
                        .Select(static parameter => ExecutionValueCodec.GetMetadataName(parameter.ParameterType))
                        .Zip(
                            descriptor.Parameters.Select(parameter => image.Semantics.Types.Single(type =>
                                type.Id == parameter.TypeId).MetadataName),
                            static (runtimeName, declaredName) =>
                                MetadataTypesMatch(runtimeName, declaredName))
                        .All(static match => match);
                bool signatureMatches =
                    (declaration is null || string.Equals(
                        method.Name,
                        declaration.Name,
                        StringComparison.Ordinal)) &&
                    method.IsStatic == descriptor.IsStatic &&
                    string.Equals(actualDeclaringType, expectedDeclaringType, StringComparison.Ordinal) &&
                    parameterSignatureMatches &&
                    MetadataTypesMatch(
                        ExecutionValueCodec.GetMetadataName(method.ReturnType),
                        image.Semantics.Types.Single(type => type.Id == descriptor.ReturnTypeId).MetadataName);
                if (!signatureMatches)
                {
                    throw new ExecutionInvocationException(
                        "UVR1007",
                        $"Exported method '{sourceMap.MetadataName}' does not match its semantic/source-map declaration.");
                }

                return method;
            }
        }
        catch (ArgumentException exception)
        {
            throw new ExecutionInvocationException(
                "UVR1007",
                $"Exported method '{sourceMap.MetadataName}' could not be resolved from metadata token: {exception.Message}");
        }

        throw new ExecutionInvocationException(
            "UVR1007",
            $"Exported method '{sourceMap.MetadataName}' is not an invokable method.");
    }

    private (HirTypeDeclaration? Type, HirMethodDeclaration? Method) FindHirMethodOrNull(
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

        return (null, null);
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
