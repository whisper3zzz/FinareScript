#include "FinareClassBinding.h"

#include "Dom/JsonObject.h"
#include "UObject/SoftObjectPath.h"
#include "FinareSoftObjectPath.h"

namespace UE::Finare::ClassBinding::Private
{
constexpr TCHAR ClassBindingSchemaVersion[] = TEXT("unifiedscript.script-asset/v0.3");
constexpr TCHAR LegacySchemaVersion[] = TEXT("unifiedscript.script-asset/v0.1");
constexpr TCHAR OverrideSchemaVersion[] = TEXT("unifiedscript.script-asset/v0.2");

bool IsKnownKind(const FString& Kind)
{
    return Kind.Equals(TEXT("Actor"), ESearchCase::CaseSensitive) ||
        Kind.Equals(TEXT("ActorComponent"), ESearchCase::CaseSensitive) ||
        Kind.Equals(TEXT("Object"), ESearchCase::CaseSensitive) ||
        Kind.Equals(TEXT("BlueprintFunctionLibrary"), ESearchCase::CaseSensitive);
}

bool IsExactClassPath(const FString& Path)
{
    if (!FinareSoftObjectPath::IsValid(Path, false) || Path.Contains(TEXT(":")))
    {
        return false;
    }
    if (Path.StartsWith(TEXT("/Script/"), ESearchCase::CaseSensitive))
    {
        const FString NativeTail = Path.Mid(8);
        int32 DotIndex = INDEX_NONE;
        if (!NativeTail.FindChar(TEXT('.'), DotIndex) || DotIndex <= 0 ||
            DotIndex == NativeTail.Len() - 1 ||
            NativeTail.Find(TEXT("."), ESearchCase::CaseSensitive, ESearchDir::FromStart, DotIndex + 1) != INDEX_NONE ||
            NativeTail.Contains(TEXT("/")))
        {
            return false;
        }
    }
    else if (!Path.EndsWith(TEXT("_C"), ESearchCase::CaseSensitive))
    {
        return false;
    }
    const FSoftClassPath ClassPath(Path);
    return ClassPath.IsValid() &&
        ClassPath.ToString().Equals(Path, ESearchCase::CaseSensitive);
}

bool ReadNullableString(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    TOptional<FString>& OutValue,
    FString& OutError)
{
    if (!Object->HasField(Field))
    {
        OutValue.Reset();
        return true;
    }
    const TSharedPtr<FJsonValue> Value = Object->TryGetField(Field);
    if (!Value.IsValid() || Value->Type == EJson::Null)
    {
        OutValue.Reset();
        return true;
    }
    FString Parsed;
    if (Value->Type != EJson::String || !Object->TryGetStringField(Field, Parsed))
    {
        OutError = FString::Printf(TEXT("Class binding field '%s' must be a string or null."), Field);
        return false;
    }
    OutValue = MoveTemp(Parsed);
    return true;
}
}

bool FFinareClassBindingDeclaration::Validate(FString& OutError) const
{
    using namespace UE::Finare::ClassBinding::Private;
    if (!IsKnownKind(Kind))
    {
        OutError = FString::Printf(TEXT("Class binding kind '%s' is unknown."), *Kind);
        return false;
    }

    const TOptional<FString>* Paths[] = {&BaseClassPath, &GeneratedClassPath};
    const TCHAR* Names[] = {TEXT("baseClassPath"), TEXT("generatedClassPath")};
    for (int32 Index = 0; Index < UE_ARRAY_COUNT(Paths); ++Index)
    {
        if (!Paths[Index]->IsSet())
        {
            continue;
        }
        const FString& Path = Paths[Index]->GetValue();
        if (Path.IsEmpty() || Path.TrimStartAndEnd() != Path || !IsExactClassPath(Path))
        {
            OutError = FString::Printf(
                TEXT("Class binding field '%s' has invalid exact class path '%s'."),
                Names[Index],
                *Path);
            return false;
        }
    }
    OutError.Reset();
    return true;
}

bool FFinareClassBindingDeclaration::ReadJson(
    const TSharedPtr<FJsonObject>& Asset,
    TOptional<FFinareClassBindingDeclaration>& OutBinding,
    FString& OutError)
{
    using namespace UE::Finare::ClassBinding::Private;
    OutBinding.Reset();
    OutError.Reset();
    if (!Asset.IsValid())
    {
        OutError = TEXT("Script declaration asset is not an object.");
        return false;
    }

    if (!Asset->HasField(TEXT("classBinding")) ||
        Asset->HasTypedField<EJson::Null>(TEXT("classBinding")))
    {
        return true;
    }
    const TSharedPtr<FJsonObject>* BindingObject = nullptr;
    if (!Asset->TryGetObjectField(TEXT("classBinding"), BindingObject) ||
        BindingObject == nullptr || !BindingObject->IsValid())
    {
        OutError = TEXT("Script declaration classBinding must be an object or null.");
        return false;
    }

    for (const TPair<FString, TSharedPtr<FJsonValue>>& Field : (*BindingObject)->Values)
    {
        if (!Field.Key.Equals(TEXT("kind"), ESearchCase::CaseSensitive) &&
            !Field.Key.Equals(TEXT("baseClassPath"), ESearchCase::CaseSensitive) &&
            !Field.Key.Equals(TEXT("generatedClassPath"), ESearchCase::CaseSensitive))
        {
            OutError = FString::Printf(
                TEXT("Script declaration classBinding contains unknown field '%s'."),
                *Field.Key);
            return false;
        }
    }

    FString Kind;
    if (!(*BindingObject)->TryGetStringField(TEXT("kind"), Kind) || Kind.IsEmpty())
    {
        OutError = TEXT("Script declaration classBinding requires a kind string.");
        return false;
    }
    FFinareClassBindingDeclaration Binding;
    Binding.Kind = MoveTemp(Kind);
    if (!ReadNullableString(*BindingObject, TEXT("baseClassPath"), Binding.BaseClassPath, OutError) ||
        !ReadNullableString(*BindingObject, TEXT("generatedClassPath"), Binding.GeneratedClassPath, OutError) ||
        !Binding.Validate(OutError))
    {
        return false;
    }
    OutBinding = MoveTemp(Binding);
    return true;
}

bool ValidateFinareClassBindingsJson(
    const TArray<TSharedPtr<FJsonValue>>& Assets,
    const FString& SchemaVersion,
    FString& OutError)
{
    using namespace UE::Finare::ClassBinding::Private;
    for (const TSharedPtr<FJsonValue>& AssetValue : Assets)
    {
        const TSharedPtr<FJsonObject> Asset = AssetValue.IsValid()
            ? AssetValue->AsObject()
            : nullptr;
        TOptional<FFinareClassBindingDeclaration> Binding;
        if (!FFinareClassBindingDeclaration::ReadJson(Asset, Binding, OutError))
        {
            return false;
        }
        if (Binding.IsSet() && !SchemaVersion.Equals(ClassBindingSchemaVersion, ESearchCase::CaseSensitive))
        {
            OutError = FString::Printf(
                TEXT("Script declaration schema '%s' cannot contain class binding metadata; use '%s'."),
                *SchemaVersion,
                ClassBindingSchemaVersion);
            return false;
        }
    }
    OutError.Reset();
    return true;
}
