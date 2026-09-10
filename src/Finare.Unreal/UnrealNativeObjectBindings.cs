using System.Text.Json;
using System.Text.RegularExpressions;

namespace Finare.Unreal;

/// <summary>Validation shared by declared native objects and all generated surfaces.</summary>
internal static class UnrealNativeObjectBindings
{
    internal static void ValidateFunction(UnrealReflectedType owner, UnrealReflectedFunction function)
    {
        var binding = function.NativeBinding!;
        bool objectReturn = function.ReturnType.Kind == UnrealValueKind.NativeObject;
        if (objectReturn != (binding.ReturnOwnership != UnrealNativeReturnOwnership.Value) || objectReturn && function.Traits.Pure ||
            binding.ReturnOwnership == UnrealNativeReturnOwnership.Borrowed && function.Traits.Static ||
            binding.Operation == UnrealNativeOperation.Constructor &&
                (!function.Traits.Static || binding.Const || !objectReturn ||
                 function.ReturnType.TypePath != owner.Path || binding.ReturnOwnership != UnrealNativeReturnOwnership.Owned) ||
            function.Metadata.ThreadSafe || function.Metadata.WorldContextParameter is not null ||
            function.Metadata.Latent || function.Name is "Dispose" or "Handle" or "Scope" or "Lease" || function.Name == owner.Name)
            throw new JsonException($"USUE5411: Native object member '{function.Path}' has invalid constructor, ownership or lifecycle metadata.");
    }

    internal static void ValidateReferences(UnrealReflectionManifest manifest)
    {
        var types = manifest.Types.ToDictionary(t => t.Path, StringComparer.Ordinal);
        foreach (var owner in manifest.Types.Where(t => t.Kind == UnrealReflectedTypeKind.NativeObject))
        {
            if (owner.Path != $"/Native/{owner.Module}.{owner.Name}" || string.IsNullOrEmpty(owner.HeaderPath) ||
                !Regex.IsMatch(owner.NativeName, @"\A[A-Za-z_][A-Za-z0-9_]*(::[A-Za-z_][A-Za-z0-9_]*)*\z") ||
                owner.Properties.Any(p => p.Name == owner.Name || p.Metadata.ThreadSafe))
                throw new JsonException($"USUE5412: Invalid native C++ identity or field declaration for '{owner.Path}'.");
            foreach (var reference in owner.Properties.Select(p => p.Type).Concat(owner.Functions.SelectMany(f =>
                         f.Parameters.Select(p => p.Type).Append(f.ReturnType))))
            {
                if (IsFixed(reference, true) && reference.NativeName != CppType(reference))
                    throw new JsonException($"USUE5412: Native value kind and C++ name disagree for '{reference.NativeName}'.");
                if (reference.Kind == UnrealValueKind.String && !IsText(reference))
                    throw new JsonException($"USUE5413: Native object text must use the exact FString or std::string spelling, not '{reference.NativeName}'.");
                if (reference.Kind == UnrealValueKind.Enum &&
                    (!types.TryGetValue(reference.TypePath!, out var enumType) || enumType.Kind != UnrealReflectedTypeKind.Enum ||
                     enumType.NativeName != reference.NativeName || !Regex.IsMatch(reference.NativeName, @"\A[A-Za-z_][A-Za-z0-9_]*(::[A-Za-z_][A-Za-z0-9_]*)*\z")))
                    throw new JsonException($"USUE5412: Native enum '{reference.NativeName}' must match a declared enum.");
                if (reference.Kind == UnrealValueKind.NativeObject &&
                    (!types.TryGetValue(reference.TypePath!, out var target) || target.Kind != UnrealReflectedTypeKind.NativeObject ||
                     reference.NativeName != target.NativeName + "*" || reference.HeaderPath != target.HeaderPath))
                    throw new JsonException($"USUE5412: Native object reference '{reference.NativeName}' must match a declared type, pointer and header.");
            }
        }
    }

    internal static string CppType(UnrealTypeReference type) => type.Kind switch
    {
        UnrealValueKind.Void => "void", UnrealValueKind.Boolean => "bool",
        UnrealValueKind.Int8 => "int8", UnrealValueKind.UInt8 => "uint8",
        UnrealValueKind.Int16 => "int16", UnrealValueKind.UInt16 => "uint16",
        UnrealValueKind.Int32 => "int32", UnrealValueKind.UInt32 => "uint32",
        UnrealValueKind.Int64 => "int64", UnrealValueKind.UInt64 => "uint64",
        UnrealValueKind.Float32 => "float", UnrealValueKind.Float64 => "double",
        UnrealValueKind.Struct when type.TypePath == "/Script/CoreUObject.Vector" => "FVector",
        UnrealValueKind.Struct when type.TypePath == "/Script/CoreUObject.Rotator" => "FRotator",
        _ => type.NativeName,
    };

    internal static bool IsFixed(UnrealTypeReference type, bool allowVoid = false) =>
        type.Kind is UnrealValueKind.Boolean or UnrealValueKind.Int8 or UnrealValueKind.UInt8 or
            UnrealValueKind.Int16 or UnrealValueKind.UInt16 or UnrealValueKind.Int32 or UnrealValueKind.UInt32 or
            UnrealValueKind.Int64 or UnrealValueKind.UInt64 or UnrealValueKind.Float32 or UnrealValueKind.Float64 or UnrealValueKind.Enum ||
        type.Kind == UnrealValueKind.Struct && type.TypePath is "/Script/CoreUObject.Vector" or "/Script/CoreUObject.Rotator" ||
        allowVoid && type.Kind == UnrealValueKind.Void;

    internal static bool IsText(UnrealTypeReference type) =>
        type.Kind == UnrealValueKind.String && type.NativeName is ("FString" or "std::string");

    internal static bool IsTextPassing(UnrealTypeReference type, UnrealNativeParameterPassing passing) =>
        IsText(type) && passing is (UnrealNativeParameterPassing.Value or UnrealNativeParameterPassing.ConstReference);

    internal static bool IsObjectPassing(UnrealNativeParameterPassing passing) => passing is
        UnrealNativeParameterPassing.Pointer or UnrealNativeParameterPassing.ConstPointer or
        UnrealNativeParameterPassing.NullablePointer or UnrealNativeParameterPassing.NullableConstPointer or
        UnrealNativeParameterPassing.ObjectReference or UnrealNativeParameterPassing.ConstObjectReference;

    internal static bool AllowsNull(UnrealNativeParameterPassing passing) => passing is
        UnrealNativeParameterPassing.NullablePointer or UnrealNativeParameterPassing.NullableConstPointer;

    internal static bool IsObjectReference(UnrealNativeParameterPassing passing) => passing is
        UnrealNativeParameterPassing.ObjectReference or UnrealNativeParameterPassing.ConstObjectReference;

    internal static string ParameterCppType(UnrealTypeReference type, UnrealNativeParameterPassing passing)
    {
        if (IsObjectPassing(passing))
        {
            string native = type.NativeName.EndsWith('*') ? type.NativeName[..^1] : type.NativeName;
            return passing switch
            {
                UnrealNativeParameterPassing.ConstPointer or UnrealNativeParameterPassing.NullableConstPointer => native + " const*",
                UnrealNativeParameterPassing.ObjectReference => native + "&",
                UnrealNativeParameterPassing.ConstObjectReference => native + " const&",
                _ => native + "*",
            };
        }
        return type.NativeName + (passing switch
        {
            UnrealNativeParameterPassing.ConstReference => " const&",
            UnrealNativeParameterPassing.Reference => "&",
            _ => "",
        });
    }

    internal static string? Unsupported(UnrealReflectedFunction function) =>
        function.Parameters.Count > 16 || function.Parameters.Where((p, i) => p.Direction != UnrealParameterDirection.Input ||
            p.DefaultValue is not null || !(IsFixed(p.Type) || IsText(p.Type) && IsTextPassing(p.Type, function.NativeBinding!.Parameters[i]) ||
                p.Type.Kind == UnrealValueKind.NativeObject && IsObjectPassing(function.NativeBinding!.Parameters[i]))).Any() ||
        !(IsFixed(function.ReturnType, true) || IsText(function.ReturnType) || function.ReturnType.Kind == UnrealValueKind.NativeObject)
            ? "Native-object calls support up to 16 fixed, exact text, or explicitly qualified object inputs and fixed, text, or ownership-qualified object returns. Defaults, containers and ref/out require another codec path."
            : null;

    internal static string? Unsupported(UnrealReflectedProperty field) =>
        !(IsFixed(field.Type) || IsText(field.Type)) || field.Name is "Dispose" or "Handle" or "Scope" or "Lease"
            ? "Native-object fields currently require fixed values or exact FString/std::string text and names distinct from generated lifecycle members."
            : null;
}
