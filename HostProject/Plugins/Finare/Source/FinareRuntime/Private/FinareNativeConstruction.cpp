#include "FinareBlueprint.h"
#include "FinareAsset.h"
#include "FinareCoreClrHost.h"
#include "FinareComponent.h"
#include "FinareInvocationBridge.h"
#include "FinareNativeTransport.h"
#include "Components/SceneComponent.h"
#include "Containers/StringConv.h"
#include "HAL/FileManager.h"
#include "Misc/SecureHash.h"
#include "Misc/ScopeExit.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"
#include "UObject/UObjectThreadContext.h"
#include "UObject/UObjectIterator.h"

namespace UE::Finare::Construction
{
	struct FScope
	{
		const FObjectInitializer& Initializer;
		FFinareObjectRegistry& Registry;
	};
	static thread_local TMap<void*, FScope*> Scopes;

	static FString Decode(const uint8* Bytes, int32 Length)
	{
		if (!Bytes || Length <= 0) return {};
		FUTF8ToTCHAR Text(reinterpret_cast<const ANSICHAR*>(Bytes), Length);
		return FString(Text.Length(), Text.Get());
	}
	static int32 Fail(uint8* Error, int32 Capacity, const FString& Message)
	{
		if (Error && Capacity > 0)
		{
			FTCHARToUTF8 Text(*Message);
			const int32 Count = FMath::Min(Capacity - 1, Text.Length());
			FMemory::Memcpy(Error, Text.Get(), Count); Error[Count] = 0;
		}
		return 1;
	}
	static int32 Execute(void* Context, int32 Operation, uint64 Target, uint64 Other,
		const uint8* NameBytes, int32 NameLength, const uint8* PathBytes, int32 PathLength,
		uint64* OutHandle, uint8* Error, int32 Capacity)
	{
		if (OutHandle) *OutHandle = 0;
		FScope* Scope = Scopes.FindRef(Context);
		if (!IsInGameThread() || !Scope || !OutHandle || !FUObjectThreadContext::Get().IsInConstructor)
			return Fail(Error, Capacity, TEXT("Default subobjects require an active synchronous UE constructor."));
		FFinareObjectHandleResult Result;
		UObject* Object = Scope->Registry.ResolveObject({Target}, Result);
		if (!Object) return Fail(Error, Capacity, Result.Message);
		const FString Name = Decode(NameBytes, NameLength);
		UObject* Subobject = nullptr;
		if (Operation == 1 || Operation == 2)
		{
			if (Object != Scope->Initializer.GetObj() || Name.IsEmpty() || Name.Len() >= NAME_SIZE ||
				Name.Contains(TEXT("/")) || Name.Contains(TEXT(".")) || Name.Contains(TEXT(":")) || FName(*Name).IsNone())
				return Fail(Error, Capacity, TEXT("A default subobject requires its constructing owner and a valid stable object name."));
			if (Operation == 2) Subobject = Object->GetDefaultSubobjectByName(FName(*Name));
			else
			{
				// Reject duplicates before the engine's fatal duplicate-name assertion.
				if (FindObjectFast<UObject>(Object, FName(*Name)))
					return Fail(Error, Capacity, FString::Printf(TEXT("Default subobject '%s' already exists."), *Name));
				const FString Path = Decode(PathBytes, PathLength);
				UClass* Class = FindObject<UClass>(nullptr, *Path);
				if (!Class || Class->HasAnyClassFlags(CLASS_Abstract | CLASS_Deprecated | CLASS_NewerVersionExists))
					return Fail(Error, Capacity, FString::Printf(TEXT("Default subobject type '%s' must be a loaded concrete UE class."), *Path));
				Subobject = Scope->Initializer.CreateDefaultSubobject(Object, FName(*Name), Class, Class, true, false);
			}
		}
		else if (Operation == 3)
		{
			USceneComponent* Child = Cast<USceneComponent>(Object);
			USceneComponent* Parent = Cast<USceneComponent>(Scope->Registry.ResolveObject({Other}, Result));
			if (!Child || !Parent || Child == Parent || Child->IsRegistered() || Parent->IsAttachedTo(Child))
				return Fail(Error, Capacity, TEXT("SetupAttachment requires unregistered SceneComponents in this constructor and no attachment cycle."));
			Child->SetupAttachment(Parent, FName(*Name));
			return 0;
		}
		else return Fail(Error, Capacity, TEXT("Unknown native construction operation."));
		if (Subobject)
		{
			*OutHandle = Scope->Registry.RegisterObject(Subobject, Result).Value;
			if (!Result.IsSuccess()) return Fail(Error, Capacity, Result.Message);
		}
		return 0;
	}
	struct FOperations
	{
		uint32 Version = 1;
		uint32 Size = sizeof(FOperations);
		decltype(&Execute) ExecuteOperation = &Execute;
	};
	static_assert(sizeof(FOperations) == 16);
	static const FOperations Operations;
	static FString ProgramHash(const FString& Program)
	{
		return LexToString(FMD5Hash::HashFile(*Program));
	}
}

bool UFinareBlueprintGeneratedClass::ConfigureManagedConstruction(UFinareAsset* Asset, FString& OutError)
{
	// Compiler/preflight/reinstancer copies do not own the source class identity.
	// Their old native parent may not yet match the candidate declaration.
	if (!Asset || GetName().StartsWith(TEXT("SKEL_")) || Asset->GeneratedClassPath.ToString() != GetPathName())
	{
		ManagedScriptAsset = Asset;
		bHasManagedConstruction = false;
		ManagedConstructionProgramHash.Reset();
		ManagedConstructionError.Reset();
		ManagedConstructionProperties.Reset();
		ManagedConstructionDefaults.Reset();
		return true;
	}
	FString Program, Type;
	// New Script Class creation may precede its first managed build.
	if (Asset->ScriptTypeName.IsEmpty() || (Asset->CompiledChunkPath.IsEmpty() && Asset->ScriptAssemblyPath.IsEmpty()))
	{
		if (bHasManagedConstruction) { OutError = TEXT("The native construction program configuration is missing."); return false; }
		ManagedScriptAsset = Asset;
		OutError.Reset(); return true;
	}
	if (!Asset->ResolveRuntimeConfiguration(true, Program, Type, OutError)) return false;
	if (!IFileManager::Get().FileExists(*Program))
	{
		if (bHasManagedConstruction) { OutError = TEXT("The native construction program is missing."); return false; }
		ManagedScriptAsset = Asset;
		OutError.Reset(); return true;
	}
	bool bCandidateHasHook = false;
	if (!FFinareCoreClrHost::Get().HasManagedConstruction(Program, Type, bCandidateHasHook, OutError)) return false;
	const FString CandidateHash = UE::Finare::Construction::ProgramHash(Program);
	for (TObjectIterator<UFinareComponent> It; It; ++It)
	{
		const auto* Active = It->bScriptRunning ? It->GetActiveManagedGeneratedClass() : nullptr;
		if (Active && Active->ManagedScriptAsset == Asset && (Active->bHasManagedConstruction || bCandidateHasHook) &&
			Active->ManagedConstructionProgramHash != CandidateHash)
		{
			OutError = TEXT("Stop gameplay before rebuilding a class with changed native constructor code.");
			return false;
		}
	}
	bHasManagedConstruction = bCandidateHasHook;
	ManagedScriptAsset = Asset;
	ManagedConstructionProgramHash = CandidateHash;
	ManagedConstructionError.Reset();
	if (!bCandidateHasHook)
	{
		ManagedConstructionProperties.Reset();
		ManagedConstructionDefaults.Reset();
	}
	return true;
}

bool UFinareBlueprintGeneratedClass::ValidateManagedConstruction(const FString& Program, FString& OutError) const
{
	if (!ManagedConstructionError.IsEmpty()) { OutError = ManagedConstructionError; return false; }
	if (bHasManagedConstruction && (ManagedConstructionProgramHash.IsEmpty() ||
		ManagedConstructionProgramHash != UE::Finare::Construction::ProgramHash(Program)))
	{
		OutError = TEXT("Native constructor program changed. Stop gameplay and rebuild the Script Class CDO before starting or reloading; the existing class was not reconstructed.");
		return false;
	}
	return true;
}

void UFinareBlueprintGeneratedClass::ConstructFinareObject(const FObjectInitializer& Initializer)
{
	using namespace UE::Finare::Construction;
	UClass* ActualClass = Initializer.GetClass();
	UClass* NativeClass = ActualClass;
	while (NativeClass && NativeClass->ClassConstructor == &ConstructFinareObject) NativeClass = NativeClass->GetSuperClass();
	check(NativeClass && NativeClass->ClassConstructor);
	NativeClass->ClassConstructor(Initializer);

	// The most-derived managed receiver owns the callback. A Blueprint-only
	// descendant inherits it; ordinary C# base.OnConstruct follows CLR dispatch.
	UFinareBlueprintGeneratedClass* Owner = nullptr;
	for (UClass* Class = ActualClass; Class; Class = Class->GetSuperClass())
		if (auto* Candidate = Cast<UFinareBlueprintGeneratedClass>(Class); Candidate && Candidate->bHasManagedConstruction)
		{ Owner = Candidate; break; }
	if (!Owner) return;
	if (Owner->ManagedScriptAsset && Owner->ManagedScriptAsset->GeneratedClassPath.ToString() != Owner->GetPathName()) return;
	const auto Report = [&](const FString& Message)
	{
		Owner->ManagedConstructionError = Message;
		UE_LOG(LogTemp, Error, TEXT("Finare native construction for %s failed: %s"), *ActualClass->GetPathName(), *Message);
	};
	if (!IsInGameThread())
	{
		Report(TEXT("Managed native construction currently requires synchronous game-thread class loading; async loading is unsupported."));
		return;
	}
	FString Program, Type, Error;
	if (!Owner->ManagedScriptAsset ||
		!Owner->ManagedScriptAsset->ResolveRuntimeConfiguration(true, Program, Type, Error) ||
		!Owner->ValidateManagedConstruction(Program, Error))
	{ Report(Error.IsEmpty() ? TEXT("Missing managed construction program.") : Error); return; }

	UObject* Object = Initializer.GetObj();
	const bool bIsCDO = Object->HasAnyFlags(RF_ClassDefaultObject);
	if (bIsCDO && ActualClass == Owner)
	{
		Owner->ManagedConstructionProperties.Reset();
		Owner->ManagedConstructionDefaults.Reset();
	}
	// Blueprint CDOs initialize inherited properties after ClassConstructor.
	// Retain actual changed property values, not an executable construction list.
	TSharedPtr<FStructOnScope> Before = MakeShared<FStructOnScope>(ActualClass);
	if (bIsCDO)
		for (TFieldIterator<FProperty> It(ActualClass); It; ++It)
			It->CopyCompleteValue_InContainer(Before->GetStructMemory(), Object);
	FFinareObjectRegistry Registry(*Object);
	FFinareInvocationBridge Bridge(Registry);
	auto* Context = new FFinareNativeBridgeContext(Bridge);
	Context->SuspendWorkerDispatch();
	FScope Scope{Initializer, Registry};
	Scopes.Add(Context, &Scope);
	ON_SCOPE_EXIT { Scopes.Remove(Context); Context->Detach(); Context->Release(); };
	FFinareObjectHandleResult HandleResult;
	const FFinareObjectHandle Handle = Registry.RegisterObject(Object, HandleResult);
	if (!HandleResult.IsSuccess() || !FFinareCoreClrHost::Get().ConstructManagedObject(Context,
		Finare_GetNativeTransportV2(), &Operations, Handle.Value, bIsCDO, Program, Type, Error))
	{ Report(Error.IsEmpty() ? HandleResult.Message : Error); return; }
	if (bIsCDO)
	{
		TArray<FProperty*> Changed;
		auto Values = MakeShared<FStructOnScope>(ActualClass);
		for (TFieldIterator<FProperty> It(ActualClass); It; ++It)
			if (!It->Identical_InContainer(Object, Before->GetStructMemory()))
			{
				Changed.Add(*It);
				if (ActualClass == Owner)
				{
					Owner->ManagedConstructionProperties.Add(It->GetFName());
					FString Text;
					It->ExportText_InContainer(0, Text, Object, nullptr, Object, PPF_None);
					Owner->ManagedConstructionDefaults.Add(It->GetFName(), MoveTemp(Text));
				}
				It->CopyCompleteValue_InContainer(Values->GetStructMemory(), Object);
			}
		const_cast<FObjectInitializer&>(Initializer).AddPropertyPostInitCallback(
			[Object, Values, Changed = MoveTemp(Changed)]()
			{ for (FProperty* Property : Changed) Property->CopyCompleteValue_InContainer(Object, Values->GetStructMemory()); });
	}
}
