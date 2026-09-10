#pragma once

#include "CoreMinimal.h"

class FJsonObject;
class FJsonValue;

/** Native representation of the engine-neutral ScriptAsset class binding. */
struct FINARERUNTIME_API FFinareClassBindingDeclaration
{
    FString Kind;
    TOptional<FString> BaseClassPath;
    TOptional<FString> GeneratedClassPath;

    bool Validate(FString& OutError) const;

    static bool ReadJson(
        const TSharedPtr<FJsonObject>& Asset,
        TOptional<FFinareClassBindingDeclaration>& OutBinding,
        FString& OutError);
};

/** Validates bindings on every asset before a selected projection is used. */
FINARERUNTIME_API bool ValidateFinareClassBindingsJson(
    const TArray<TSharedPtr<FJsonValue>>& Assets,
    const FString& SchemaVersion,
    FString& OutError);
