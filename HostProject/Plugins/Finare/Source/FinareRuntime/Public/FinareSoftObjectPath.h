#pragma once

#include "CoreMinimal.h"

namespace FinareSoftObjectPath
{
    inline bool IsPackageName(const FString& Value)
    {
        if (Value.IsEmpty() || Value == TEXT(".") || Value == TEXT(".."))
        {
            return false;
        }
        for (const TCHAR Character : Value)
        {
            if (Character < 0x20 || (Character >= 0x7f && Character <= 0x9f) ||
                Character == TEXT('\\') || Character == TEXT(':') ||
                Character == TEXT('*') || Character == TEXT('?') ||
                Character == TEXT('"') || Character == TEXT('<') ||
                Character == TEXT('>') || Character == TEXT('|') ||
                Character == TEXT('\'') || Character == TEXT(' ') ||
                Character == TEXT(',') || Character == TEXT('.') ||
                Character == TEXT('&') || Character == TEXT('!') ||
                Character == TEXT('~') || Character == TEXT('\n') ||
                Character == TEXT('\r') || Character == TEXT('\t') ||
                Character == TEXT('@') || Character == TEXT('#'))
            {
                return false;
            }
        }
        return true;
    }

    inline bool IsObjectName(const FString& Value)
    {
        if (Value.IsEmpty() || Value == TEXT(".") || Value == TEXT(".."))
        {
            return false;
        }
        for (const TCHAR Character : Value)
        {
            if (Character < 0x20 || (Character >= 0x7f && Character <= 0x9f) || Character == TEXT('"') ||
                Character == TEXT('\'') || Character == TEXT(' ') ||
                Character == TEXT(',') || Character == TEXT('/') || Character == TEXT('\\') ||
                Character == TEXT('.') || Character == TEXT(':') ||
                Character == TEXT('?') || Character == TEXT('*') ||
                Character == TEXT('<') || Character == TEXT('>') ||
                Character == TEXT('|') || Character == TEXT('&') ||
                Character == TEXT('!') || Character == TEXT('~') ||
                Character == TEXT('@') || Character == TEXT('#') ||
                Character == TEXT('(') || Character == TEXT(')') ||
                Character == TEXT('{') || Character == TEXT('}') ||
                Character == TEXT('[') || Character == TEXT(']') ||
                Character == TEXT('=') || Character == TEXT(';') ||
                Character == TEXT('^') || Character == TEXT('%') ||
                Character == TEXT('$') || Character == TEXT('`'))
            {
                return false;
            }
        }
        return true;
    }

    inline bool IsValid(const FString& Path, bool bAllowEmpty = true)
    {
        if (Path.IsEmpty())
        {
            return bAllowEmpty;
        }
        if (!Path.StartsWith(TEXT("/")) || Path.Len() == 1 ||
            Path.Contains(TEXT("//")) || Path.Contains(TEXT("\\")) || Path.EndsWith(TEXT("/")))
        {
            return false;
        }
        for (const TCHAR Character : Path)
        {
            if (Character == 0)
            {
                return false;
            }
        }

        int32 ColonIndex = INDEX_NONE;
        if (Path.FindChar(TEXT(':'), ColonIndex))
        {
            if (Path.Find(TEXT(":"), ESearchCase::CaseSensitive, ESearchDir::FromStart, ColonIndex + 1) != INDEX_NONE)
            {
                return false;
            }
        }
        const FString ObjectPath = ColonIndex == INDEX_NONE
            ? Path
            : Path.Left(ColonIndex);
        const FString SubobjectPath = ColonIndex == INDEX_NONE
            ? FString()
            : Path.Mid(ColonIndex + 1);
        if (ObjectPath.IsEmpty() || ObjectPath.EndsWith(TEXT("/")) ||
            (ColonIndex != INDEX_NONE && SubobjectPath.IsEmpty()))
        {
            return false;
        }

        TArray<FString> Segments;
        ObjectPath.Mid(1).ParseIntoArray(Segments, TEXT("/"), true);
        if (Segments.Num() < 2)
        {
            return false;
        }
        for (int32 Index = 0; Index < Segments.Num() - 1; ++Index)
        {
            if (!IsPackageName(Segments[Index]))
            {
                return false;
            }
        }

        const FString& Leaf = Segments.Last();
        int32 DotIndex = INDEX_NONE;
        if (!Leaf.FindChar(TEXT('.'), DotIndex) || DotIndex <= 0 ||
            DotIndex == Leaf.Len() - 1 ||
            Leaf.Find(TEXT("."), ESearchCase::CaseSensitive, ESearchDir::FromStart, DotIndex + 1) != INDEX_NONE)
        {
            return false;
        }
        const FString ObjectName = Leaf.Left(DotIndex);
        const FString AssetName = Leaf.Mid(DotIndex + 1);
        if (!IsPackageName(ObjectName) || !IsObjectName(AssetName))
        {
            return false;
        }

        const FString PackageName = ObjectPath.Left(ObjectPath.Len() - Leaf.Len() + DotIndex);
        if (PackageName.Len() > 1023 || AssetName.Len() > 1023)
        {
            return false;
        }

        if (ColonIndex != INDEX_NONE)
        {
            TArray<FString> Subobjects;
            SubobjectPath.ParseIntoArray(Subobjects, TEXT("."), false);
            for (const FString& Subobject : Subobjects)
            {
                if (!IsObjectName(Subobject))
                {
                    return false;
                }
            }
        }
        return true;
    }
}
