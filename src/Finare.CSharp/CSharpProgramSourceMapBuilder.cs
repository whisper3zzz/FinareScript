using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Finare.Core;
using Finare.Core.Hir;
using Finare.Semantics;

namespace Finare.CSharp;

internal sealed class CSharpProgramSourceMapBuilder
{
    public CSharpProgramSourceMap Build(
        string assemblyName,
        byte[] portableExecutable,
        byte[] portablePdb,
        HirCompilationUnit hir,
        BoundCompilation semantics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ArgumentNullException.ThrowIfNull(portableExecutable);
        ArgumentNullException.ThrowIfNull(portablePdb);
        ArgumentNullException.ThrowIfNull(hir);
        ArgumentNullException.ThrowIfNull(semantics);

        using var executableStream = new MemoryStream(portableExecutable, writable: false);
        using var peReader = new PEReader(executableStream);
        MetadataReader metadata = peReader.GetMetadataReader();
        using var pdbStream = new MemoryStream(portablePdb, writable: false);
        using MetadataReaderProvider pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        MetadataReader pdb = pdbProvider.GetMetadataReader();
        var typeProvider = new MetadataTypeNameProvider();
        IReadOnlyDictionary<string, TypeDefinitionHandle[]> emittedTypes = metadata.TypeDefinitions
            .Select(handle => (Name: GetTypeName(metadata, handle), Handle: handle))
            .GroupBy(static item => item.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static item => item.Handle).ToArray(),
                StringComparer.Ordinal);

        var methods = new List<CSharpMethodSourceMap>();
        foreach (HirTypeDeclaration hirType in hir.Types)
        {
            if (!emittedTypes.TryGetValue(hirType.QualifiedName, out TypeDefinitionHandle[]? typeHandles) ||
                typeHandles.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Emitted type '{hirType.QualifiedName}' did not resolve uniquely for source mapping.");
            }

            TypeDefinition emittedType = metadata.GetTypeDefinition(typeHandles[0]);
            foreach (HirMethodDeclaration hirMethod in hirType.Methods)
            {
                MethodDescriptor descriptor = semantics.Methods.Single(method => method.Id == hirMethod.Id);
                string[] parameterTypes = descriptor.Parameters
                    .Select(parameter => NormalizeExpectedTypeName(
                        semantics.Types.Single(type => type.Id == parameter.TypeId)))
                    .ToArray();
                string returnType = NormalizeExpectedTypeName(
                    semantics.Types.Single(type => type.Id == descriptor.ReturnTypeId));
                MethodDefinitionHandle[] nameAndArityMatches = emittedType.GetMethods()
                    .Where(handle => IsNameAndArityMatch(
                        metadata,
                        handle,
                        hirMethod.Name,
                        parameterTypes.Length,
                        typeProvider))
                    .ToArray();
                MethodDefinitionHandle[] exactMatches = nameAndArityMatches
                    .Where(handle => IsMethodMatch(
                        metadata,
                        handle,
                        parameterTypes,
                        returnType,
                        typeProvider))
                    .ToArray();
                MethodDefinitionHandle[] matches = exactMatches.Length == 0 &&
                    nameAndArityMatches.Length == 1
                        ? nameAndArityMatches
                        : exactMatches;
                if (matches.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"Emitted method '{descriptor.MetadataName}' did not resolve uniquely for source mapping.");
                }

                methods.Add(BuildMethodMap(pdb, matches[0], hirMethod, descriptor));
            }
        }

        return CSharpProgramSourceMapJson.NormalizeAndValidate(new CSharpProgramSourceMap(
            CSharpProgramSourceMap.CurrentSchemaVersion,
            assemblyName,
            semantics.SourceCompilationId,
            semantics.ConfigurationId,
            methods));
    }

    private static CSharpMethodSourceMap BuildMethodMap(
        MetadataReader pdb,
        MethodDefinitionHandle methodHandle,
        HirMethodDeclaration hirMethod,
        MethodDescriptor descriptor)
    {
        int rowNumber = MetadataTokens.GetRowNumber(methodHandle);
        MethodDebugInformation debugInformation = pdb.GetMethodDebugInformation(
            MetadataTokens.MethodDebugInformationHandle(rowNumber));
        CSharpSequencePoint[] sequencePoints = debugInformation
            .GetSequencePoints()
            .Where(static point => !point.IsHidden)
            .Select(point => CreateSequencePoint(pdb, debugInformation, point))
            .GroupBy(static point => point.IlOffset)
            .Select(static group => group.First())
            .OrderBy(static point => point.IlOffset)
            .ToArray();
        CSharpNodeIlMap[] nodes = hirMethod.Body
            .DescendantsAndSelf()
            .Where(IsExecutableNode)
            .Select(node => new CSharpNodeIlMap(
                node.Id,
                node.Span,
                sequencePoints
                    .Where(point => SpansOverlap(node.Span, point.Span))
                    .Select(static point => point.IlOffset)
                    .Distinct()
                    .Order()
                    .ToArray()))
            .Where(static node => node.IlOffsets.Count > 0)
            .ToArray();

        return new CSharpMethodSourceMap(
            hirMethod.Id,
            descriptor.MetadataName,
            MetadataTokens.GetToken(methodHandle),
            sequencePoints.Length == 0 ? null : sequencePoints[0].IlOffset,
            sequencePoints,
            nodes);
    }

    private static CSharpSequencePoint CreateSequencePoint(
        MetadataReader pdb,
        MethodDebugInformation debugInformation,
        SequencePoint point)
    {
        DocumentHandle documentHandle = point.Document.IsNil
            ? debugInformation.Document
            : point.Document;
        if (documentHandle.IsNil)
        {
            throw new InvalidOperationException(
                $"Portable-PDB sequence point at IL_{point.Offset:x4} has no source document.");
        }

        Document document = pdb.GetDocument(documentHandle);
        string path = pdb.GetString(document.Name).Replace('\\', '/');
        return new CSharpSequencePoint(
            point.Offset,
            new SourceSpan(
                path,
                new SourcePosition(point.StartLine, point.StartColumn),
                new SourcePosition(point.EndLine, point.EndColumn)));
    }

    private static bool IsMethodMatch(
        MetadataReader metadata,
        MethodDefinitionHandle handle,
        IReadOnlyList<string> expectedParameterTypes,
        string expectedReturnType,
        MetadataTypeNameProvider typeProvider)
    {
        MethodDefinition method = metadata.GetMethodDefinition(handle);
        MethodSignature<string> signature = method.DecodeSignature(typeProvider, genericContext: null);
        return string.Equals(signature.ReturnType, expectedReturnType, StringComparison.Ordinal) &&
            signature.ParameterTypes.SequenceEqual(expectedParameterTypes);
    }

    private static bool IsNameAndArityMatch(
        MetadataReader metadata,
        MethodDefinitionHandle handle,
        string expectedName,
        int expectedParameterCount,
        MetadataTypeNameProvider typeProvider)
    {
        MethodDefinition method = metadata.GetMethodDefinition(handle);
        return string.Equals(metadata.GetString(method.Name), expectedName, StringComparison.Ordinal) &&
            method.DecodeSignature(typeProvider, genericContext: null).ParameterTypes.Length ==
                expectedParameterCount;
    }

    private static string NormalizeExpectedTypeName(TypeDescriptor type)
    {
        string metadataName = type.MetadataName;
        if (!metadataName.EndsWith("?", StringComparison.Ordinal))
        {
            return metadataName;
        }

        string underlyingName = metadataName[..^1];
        return type.Kind is UnifiedTypeKind.Boolean or
            UnifiedTypeKind.Integer or
            UnifiedTypeKind.FloatingPoint or
            UnifiedTypeKind.Enum or
            UnifiedTypeKind.Struct
                ? $"System.Nullable<{underlyingName}>"
                : underlyingName;
    }

    private static bool IsExecutableNode(HirNode node) => node is
        HirStatement and not HirBlockStatement or
        HirExpression or
        HirForVariableDeclaration or
        HirForVariableDeclarator or
        HirForEachVariable;

    private static bool SpansOverlap(SourceSpan left, SourceSpan right)
    {
        if (!string.Equals(
                NormalizePath(left.FilePath),
                NormalizePath(right.FilePath),
                StringComparison.Ordinal))
        {
            return false;
        }

        return Compare(left.Start, right.End) < 0 && Compare(right.Start, left.End) < 0;
    }

    private static int Compare(SourcePosition left, SourcePosition right)
    {
        int line = left.Line.CompareTo(right.Line);
        return line != 0 ? line : left.Column.CompareTo(right.Column);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string GetTypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        string name = TrimArity(metadata.GetString(type.Name));
        TypeDefinitionHandle declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return $"{GetTypeName(metadata, declaringType)}.{name}";
        }

        string @namespace = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static string GetTypeName(MetadataReader metadata, TypeReferenceHandle handle)
    {
        TypeReference type = metadata.GetTypeReference(handle);
        string name = TrimArity(metadata.GetString(type.Name));
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{GetTypeName(metadata, (TypeReferenceHandle)type.ResolutionScope)}.{name}";
        }

        string @namespace = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static string TrimArity(string name)
    {
        int index = name.IndexOf('`');
        return index < 0 ? name : name[..index];
    }

    private sealed class MetadataTypeNameProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) =>
            shape.Rank == 1
                ? $"{elementType}[*]"
                : $"{elementType}[{new string(',', shape.Rank - 1)}]";

        public string GetByReferenceType(string elementType) => elementType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "System.IntPtr";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"{genericType}<{string.Join(", ", typeArguments)}>";

        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";

        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
            unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => $"{elementType}*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.TypedReference => "System.TypedReference",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.Void => "System.Void",
            _ => throw new InvalidOperationException($"Unsupported primitive type code '{typeCode}'."),
        };

        public string GetSZArrayType(string elementType) => $"{elementType}[]";

        public string GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) => GetTypeName(reader, handle);

        public string GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => GetTypeName(reader, handle);

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => reader
                .GetTypeSpecification(handle)
                .DecodeSignature(this, genericContext);
    }
}
