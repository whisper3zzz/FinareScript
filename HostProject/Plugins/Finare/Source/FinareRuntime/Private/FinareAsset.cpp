#include "FinareAsset.h"

#include "HAL/PlatformProcess.h"
#include "HAL/PlatformProperties.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/Paths.h"
#include "Components/ActorComponent.h"
#include "GameFramework/Actor.h"
#include "Kismet/BlueprintFunctionLibrary.h"

namespace UE::Finare::Asset::Private
{
	FString ResolveRuntimePath(const FString& ConfiguredPath)
	{
		FString Result = ConfiguredPath;
		FString ProjectDir = FPlatformProperties::RequiresCookedData()
			                     ? FPaths::Combine(FPlatformProcess::BaseDir(), TEXT("../.."))
			                     : FPaths::ProjectDir();
		ProjectDir = FPaths::ConvertRelativePathToFull(ProjectDir);
		FPaths::NormalizeDirectoryName(ProjectDir);

		const TSharedPtr<IPlugin> Plugin =
			IPluginManager::Get().FindPlugin(TEXT("Finare"));
		if (Plugin.IsValid())
		{
			const FString PluginBaseDir =
				FPaths::ConvertRelativePathToFull(Plugin->GetBaseDir());
			Result.ReplaceInline(
				TEXT("{PluginDir}"),
				*PluginBaseDir,
				ESearchCase::IgnoreCase);
		}
		Result.ReplaceInline(
			TEXT("{ProjectDir}"),
			*ProjectDir,
			ESearchCase::IgnoreCase);
		if (FPaths::IsRelative(Result))
		{
			Result = FPaths::Combine(ProjectDir, Result);
		}
		Result = FPaths::ConvertRelativePathToFull(Result);
		FPaths::NormalizeFilename(Result);
		return Result;
	}
}

bool UFinareAsset::ResolveConfiguration(
	FString& OutAssemblyPath,
	FString& OutTypeName,
	FString& OutError) const
{
	return ResolveConfiguration(true, OutAssemblyPath, OutTypeName, OutError);
}

bool UFinareAsset::ResolveConfiguration(
	bool bPreferCompiledChunk,
	FString& OutAssemblyPath,
	FString& OutTypeName,
	FString& OutError) const
{
	OutAssemblyPath = bPreferCompiledChunk ? CompiledChunkPath : FString();
	OutAssemblyPath.TrimStartAndEndInline();
	if (OutAssemblyPath.IsEmpty())
	{
		OutAssemblyPath = ScriptAssemblyPath;
	}
	OutTypeName = ScriptTypeName;
	OutAssemblyPath.TrimStartAndEndInline();
	OutTypeName.TrimStartAndEndInline();

	if (SchemaVersion != 1)
	{
		OutError = FString::Printf(
			TEXT("Finare Asset '%s' uses unsupported schema version %d."),
			*GetPathName(),
			SchemaVersion);
		return false;
	}
	if (OutAssemblyPath.IsEmpty() || OutTypeName.IsEmpty())
	{
		OutError = FString::Printf(
			TEXT("Finare Asset '%s' requires CompiledChunkPath or ScriptAssemblyPath, and ScriptTypeName."),
			*GetPathName());
		return false;
	}

	OutError.Reset();
	return true;
}

bool UFinareAsset::ResolveRuntimeConfiguration(
	bool bPreferCompiledChunk,
	FString& OutAssemblyPath,
	FString& OutTypeName,
	FString& OutError) const
{
	if (!ResolveConfiguration(
		bPreferCompiledChunk,
		OutAssemblyPath,
		OutTypeName,
		OutError))
	{
		return false;
	}

	OutAssemblyPath =
		UE::Finare::Asset::Private::ResolveRuntimePath(OutAssemblyPath);
	OutError.Reset();
	return true;
}

bool UFinareAsset::ValidateTarget(
	const UObject* Target,
	FString& OutError) const
{
	if (RequiredTargetClass.IsNull())
	{
		OutError.Reset();
		return true;
	}

	UClass* RequiredClass = RequiredTargetClass.LoadSynchronous();
	if (RequiredClass == nullptr)
	{
		OutError = FString::Printf(
			TEXT("Finare Asset '%s' cannot load required target class '%s'."),
			*GetPathName(),
			*RequiredTargetClass.ToSoftObjectPath().ToString());
		return false;
	}
	if (Target == nullptr || !Target->IsA(RequiredClass))
	{
		OutError = FString::Printf(
			TEXT("Finare Asset '%s' requires target class '%s', but received '%s'."),
			*GetPathName(),
			*RequiredClass->GetPathName(),
			Target != nullptr ? *Target->GetClass()->GetPathName() : TEXT("<null>"));
		return false;
	}

	OutError.Reset();
	return true;
}

bool UFinareAsset::ValidateDeclarationIdentity(
	const FGuid& DeclarationId,
	const FString& DeclarationQualifiedName,
	FString& OutError) const
{
	if (!ScriptClassId.IsValid())
	{
		OutError.Reset();
		return true;
	}

	if (DeclarationId != ScriptClassId)
	{
		OutError = FString::Printf(
			TEXT(
				"Finare Class '%s' expects semantic ID '%s', but the selected declaration has '%s'. Rebuild or recreate the stale class proxy instead of binding by name."),
			*GetPathName(),
			*ScriptClassId.ToString(EGuidFormats::Digits),
			*DeclarationId.ToString(EGuidFormats::Digits));
		return false;
	}
	FString ExpectedTypeName = ScriptTypeName;
	ExpectedTypeName.TrimStartAndEndInline();
	if (DeclarationQualifiedName != ExpectedTypeName)
	{
		OutError = FString::Printf(
			TEXT("Finare Class '%s' expects managed type '%s', but the selected declaration contains '%s'."),
			*GetPathName(),
			*ExpectedTypeName,
			*DeclarationQualifiedName);
		return false;
	}

	OutError.Reset();
	return true;
}

bool UFinareAsset::ValidateDeclarationClassBinding(
	const TOptional<FFinareClassBindingDeclaration>& Binding,
	FString& OutError) const
{
	if (!Binding.IsSet())
	{
		OutError.Reset();
		return true;
	}

	const FFinareClassBindingDeclaration& Declaration = Binding.GetValue();
	if (!Declaration.Validate(OutError))
	{
		return false;
	}

	if (ScriptClassKind == EFinareClassKind::None)
	{
		if (Declaration.BaseClassPath.IsSet() || Declaration.GeneratedClassPath.IsSet())
		{
			OutError = FString::Printf(
				TEXT("Finare Asset '%s' has class binding paths but no persistent class kind."),
				*GetPathName());
			return false;
		}
		OutError.Reset();
		return true;
	}

	if (!ScriptClassId.IsValid() || !GeneratedClassPath.IsValid())
	{
		OutError = FString::Printf(
			TEXT("Finare Asset '%s' has no loadable persistent generated owner for class binding validation."),
			*GetPathName());
		return false;
	}

	const TCHAR* ExpectedKind = nullptr;
	switch (ScriptClassKind)
	{
	case EFinareClassKind::Actor:
		ExpectedKind = TEXT("Actor");
		break;
	case EFinareClassKind::ActorComponent:
		ExpectedKind = TEXT("ActorComponent");
		break;
	case EFinareClassKind::Object:
		ExpectedKind = TEXT("Object");
		break;
	case EFinareClassKind::BlueprintFunctionLibrary:
		ExpectedKind = TEXT("BlueprintFunctionLibrary");
		break;
	default:
		break;
	}
	if (ExpectedKind == nullptr ||
		!Declaration.Kind.Equals(ExpectedKind, ESearchCase::CaseSensitive))
	{
		OutError = FString::Printf(
			TEXT("Finare Asset '%s' class binding kind '%s' does not match persistent kind '%s'."),
			*GetPathName(),
			*Declaration.Kind,
			ExpectedKind != nullptr ? ExpectedKind : TEXT("<unknown>"));
		return false;
	}

	UClass* OwnerClass = GeneratedClassPath.TryLoadClass<UObject>();
	if (OwnerClass == nullptr)
	{
		OutError = FString::Printf(
			TEXT("Finare Asset '%s' cannot load generated class owner '%s' for class binding validation."),
			*GetPathName(),
			*GeneratedClassPath.ToString());
		return false;
	}
	const FString ActualOwnerPath = OwnerClass->GetPathName();
	if (!ActualOwnerPath.Equals(GeneratedClassPath.ToString(), ESearchCase::CaseSensitive))
	{
		OutError = FString::Printf(
			TEXT("Finare Asset '%s' configured generated owner '%s' resolves to '%s'."),
			*GetPathName(),
			*GeneratedClassPath.ToString(),
			*ActualOwnerPath);
		return false;
	}

	if (Declaration.GeneratedClassPath.IsSet())
	{
		const FString& ExpectedPath = Declaration.GeneratedClassPath.GetValue();
		if (!ActualOwnerPath.Equals(ExpectedPath, ESearchCase::CaseSensitive))
		{
			OutError = FString::Printf(
				TEXT("Finare Asset '%s' class binding generated class '%s' does not match selected owner '%s'."),
				*GetPathName(),
				*ExpectedPath,
				*GeneratedClassPath.ToString());
			return false;
		}
	}

	if (OwnerClass != nullptr)
	{
		bool bFamilyMatches = false;
		if (Declaration.Kind.Equals(TEXT("Actor"), ESearchCase::CaseSensitive))
		{
			bFamilyMatches = OwnerClass->IsChildOf(AActor::StaticClass());
		}
		else if (Declaration.Kind.Equals(TEXT("ActorComponent"), ESearchCase::CaseSensitive))
		{
			bFamilyMatches = OwnerClass->IsChildOf(UActorComponent::StaticClass());
		}
		else if (Declaration.Kind.Equals(TEXT("Object"), ESearchCase::CaseSensitive))
		{
			bFamilyMatches = OwnerClass->IsChildOf(UObject::StaticClass()) &&
				!OwnerClass->IsChildOf(AActor::StaticClass()) &&
				!OwnerClass->IsChildOf(UActorComponent::StaticClass()) &&
				!OwnerClass->IsChildOf(UBlueprintFunctionLibrary::StaticClass());
		}
		else if (Declaration.Kind.Equals(TEXT("BlueprintFunctionLibrary"), ESearchCase::CaseSensitive))
		{
			bFamilyMatches = OwnerClass->IsChildOf(UBlueprintFunctionLibrary::StaticClass());
		}
		if (!bFamilyMatches)
		{
			OutError = FString::Printf(
				TEXT("Finare Asset '%s' generated owner '%s' is not in class-binding family '%s'."),
				*GetPathName(),
				*OwnerClass->GetPathName(),
				*Declaration.Kind);
			return false;
		}

		if (Declaration.BaseClassPath.IsSet())
		{
			const FString ActualBasePath = OwnerClass->GetSuperClass() != nullptr
				                               ? OwnerClass->GetSuperClass()->GetPathName()
				                               : FString();
			if (!ActualBasePath.Equals(
				Declaration.BaseClassPath.GetValue(), ESearchCase::CaseSensitive))
			{
				OutError = FString::Printf(
					TEXT("Finare Asset '%s' class binding base '%s' does not match owner base '%s'."),
					*GetPathName(),
					*Declaration.BaseClassPath.GetValue(),
					*ActualBasePath);
				return false;
			}
		}
	}
	OutError.Reset();
	return true;
}

bool UFinareAsset::ValidateGeneratedClassIdentity(
	const UObject* Host,
	const UObject* Target,
	FString& OutError) const
{
	if (ScriptClassKind == EFinareClassKind::None)
	{
		OutError.Reset();
		return true;
	}
	if (!ScriptClassId.IsValid() || !GeneratedClassPath.IsValid())
	{
		OutError = FString::Printf(
			TEXT("Finare Class '%s' has incomplete persistent class identity."),
			*GetPathName());
		return false;
	}

	UClass* ExpectedClass = GeneratedClassPath.TryLoadClass<UObject>();
	const UObject* Candidate =
		ScriptClassKind == EFinareClassKind::Actor ||
		ScriptClassKind == EFinareClassKind::Object
			? Target
			: Host;
	if (ExpectedClass == nullptr || Candidate == nullptr || !Candidate->IsA(ExpectedClass))
	{
		OutError = FString::Printf(
			TEXT("Finare Class '%s' requires exact generated class '%s', but received '%s'."),
			*GetPathName(),
			*GeneratedClassPath.ToString(),
			Candidate != nullptr ? *Candidate->GetClass()->GetPathName() : TEXT("<null>"));
		return false;
	}

	OutError.Reset();
	return true;
}

FString UFinareAsset::ResolveRequiredTargetClassPath() const
{
	return RequiredTargetClass.IsNull()
		       ? FString()
		       : RequiredTargetClass.ToSoftObjectPath().ToString();
}

FString UFinareAsset::ResolveCompiledChunkPath() const
{
	FString Result = CompiledChunkPath;
	Result.TrimStartAndEndInline();
	return Result;
}

void UFinareAsset::ResolveDeclarationConfiguration(
	FString& OutDeclarationPath,
	FString& OutDeclarationAssetPath) const
{
	OutDeclarationPath = DeclarationPath;
	OutDeclarationAssetPath = DeclarationAssetPath;
	OutDeclarationPath.TrimStartAndEndInline();
	OutDeclarationAssetPath.TrimStartAndEndInline();
}

FString UFinareAsset::ResolveAuthoringSurfacePath() const
{
	FString Result = AuthoringSurfacePath;
	Result.TrimStartAndEndInline();
	return Result;
}

FString UFinareAsset::ResolveApiNodeCatalogPath() const
{
	FString Result = ApiNodeCatalogPath;
	Result.TrimStartAndEndInline();
	return Result;
}

FString UFinareAsset::ResolveAuthoringServerUrl() const
{
	FString Result = AuthoringServerUrl;
	Result.TrimStartAndEndInline();
	while (Result.EndsWith(TEXT("/")))
	{
		Result.LeftChopInline(1);
	}
	return Result;
}
