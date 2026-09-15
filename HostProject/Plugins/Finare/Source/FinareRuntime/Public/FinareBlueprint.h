#pragma once

#include "CoreMinimal.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "FinareBlueprint.generated.h"

class UFinareAsset;

UENUM()
enum class EFinareRpcTarget : uint8 { None, Server, Client, Multicast };

inline EFunctionFlags FinareRpcFlags(EFinareRpcTarget Target, bool bReliable)
{
	EFunctionFlags Direction;
	switch (Target)
	{
	case EFinareRpcTarget::Server: Direction = FUNC_NetServer;
		break;
	case EFinareRpcTarget::Client: Direction = FUNC_NetClient;
		break;
	case EFinareRpcTarget::Multicast: Direction = FUNC_NetMulticast;
		break;
	default: return FUNC_None;
	}
	return FUNC_Net | FUNC_Event | Direction | (bReliable ? FUNC_NetReliable : FUNC_None);
}

UENUM()
enum class EFinareBlueprintEventSuperMode : uint8
{
	Explicit = 1,
};

UENUM()
enum class EFinareManagedLifecycleHookKind : uint8
{
	BeginPlay = 1,
	Tick = 2,
	EndPlay = 3,
};

UENUM()
enum class EFinareManagedInputTriggerEvent : uint8
{
	Started = 1,
	Ongoing = 2,
	Triggered = 3,
	Completed = 4,
	Canceled = 5,
};

UENUM()
enum class EFinareManagedInputValueType : uint8
{
	Boolean = 1,
	Axis1D = 2,
	Axis2D = 3,
	Axis3D = 4,
};

UENUM()
enum class EFinareManagedDelegateSource : uint8
{
	Target = 1,
	Host = 2,
};

UENUM()
enum class EFinareManagedParameterDirection : uint8
{
	Input = 0,
	Output = 1,
	InputOutput = 2,
};

USTRUCT()
struct FFinareManagedLifecycleBinding
{
	GENERATED_BODY()

	/** Exact managed lifecycle override discovered from reviewed source. */
	UPROPERTY()
	FName ManagedName;

	/** Stable Roslyn declaration identity, independent from method-body edits. */
	UPROPERTY()
	FGuid HookId;
};

USTRUCT()
struct FFinareManagedInputActionBinding
{
	GENERATED_BODY()

	UPROPERTY()
	FName ManagedName;

	UPROPERTY()
	FGuid BindingId;

	/** Exact UInputAction asset path, or a native UInputAction class fixture path. */
	UPROPERTY()
	FString ActionPath;

	UPROPERTY()
	EFinareManagedInputTriggerEvent TriggerEvent =
		EFinareManagedInputTriggerEvent::Triggered;

	UPROPERTY()
	EFinareManagedInputValueType ValueType =
		EFinareManagedInputValueType::Boolean;

	UPROPERTY()
	bool bConsumeInput = false;
};

USTRUCT()
struct FFinareManagedDelegateBinding
{
	GENERATED_BODY()

	UPROPERTY()
	FName ManagedName;

	UPROPERTY()
	FGuid BindingId;

	UPROPERTY()
	FString SourceTypePath;

	UPROPERTY()
	FString EventPath;

	UPROPERTY()
	EFinareManagedDelegateSource Source =
		EFinareManagedDelegateSource::Target;

	UPROPERTY()
	FString SourceMember;
};

USTRUCT()
struct FFinareManagedInterfaceFunctionBinding
{
	GENERATED_BODY()

	UPROPERTY()
	FString InterfacePath;

	UPROPERTY()
	FName FunctionName;

	UPROPERTY()
	FName ManagedName;

	UPROPERTY()
	FGuid BindingId;

	UPROPERTY()
	FGuid FunctionId;
};

USTRUCT()
struct FFinareManagedStructFieldPath
{
	GENERATED_BODY()

	/** Native field path; $element/$key/$value select container components. */
	UPROPERTY()
	TArray<FName> Segments;
};

USTRUCT()
struct FFinareManagedValueTypeBinding
{
	GENERATED_BODY()

	/** Exact reflected UEnum or UScriptStruct path; empty for built-in values. */
	UPROPERTY()
	FString UnrealTypePath;

	/** Stable generated field order, flattened depth-first for recursive structs. */
	UPROPERTY()
	TArray<FFinareManagedStructFieldPath> StructFields;
};

USTRUCT()
struct FFinareManagedFunctionBinding
{
	GENERATED_BODY()

	/** Exact managed method name owned by the stable Blueprint function identity. */
	UPROPERTY()
	FString ManagedName;

	/** Stable declaration identity retained across source-level method renames. */
	UPROPERTY()
	FGuid FunctionId;

	/** Versioned call contract. Zero and one retain the scalar v1 compatibility path. */
	UPROPERTY()
	int32 AbiVersion = 1;

	/** Static libraries resolve their managed program from the generated-class sidecar. */
	UPROPERTY()
	bool bStatic = false;

	UPROPERTY()
	EFinareRpcTarget RpcTarget = EFinareRpcTarget::None;

	UPROPERTY()
	bool bReliable = false;

	/** Rebind after Blueprint compilation, which only retains zero-argument notifies. */
	UPROPERTY()
	FName RepNotifyProperty;

	/** Makes the generated base function an ordinary overridable Blueprint event. */
	UPROPERTY()
	bool bBlueprintEvent = false;

	/** Implements a native Blueprint interface through the managed dispatcher. */
	UPROPERTY()
	bool bInterfaceFunction = false;

	/** Blueprint overrides reach the managed default only through Call Parent Function. */
	UPROPERTY()
	EFinareBlueprintEventSuperMode BlueprintEventSuperMode =
		EFinareBlueprintEventSuperMode::Explicit;

	/** Exact native class object path declaring the inherited Blueprint event, when present. */
	UPROPERTY()
	FString BlueprintOverrideDeclaringTypePath;

	/** Original inherited native event name selected by the managed override declaration, when present. */
	UPROPERTY()
	FString BlueprintOverrideFunctionName;

	/** One type contract per managed argument, excluding injected World context. */
	UPROPERTY()
	TArray<FFinareManagedValueTypeBinding> ParameterTypes;

	/** Stable source parameter order used when Unreal reorders output properties. */
	UPROPERTY()
	TArray<FName> ParameterNames;

	/** One source-level input/out/ref direction per managed argument. */
	UPROPERTY()
	TArray<EFinareManagedParameterDirection> ParameterDirections;

	/** Exact return value contract; empty for void and built-in values. */
	UPROPERTY()
	FFinareManagedValueTypeBinding ReturnType;
};

/**
 * Generated-class carrier for persistent managed function bindings.
 *
 * Blueprint graphs own the durable typed signature and call-node identity;
 * this class rebinds their implementation to one generic native dispatcher
 * after load, so cooked assets do not require one handwritten thunk per method.
 */
UCLASS()
class FINARERUNTIME_API UFinareBlueprintGeneratedClass
	: public UBlueprintGeneratedClass
{
	GENERATED_BODY()

public:
	UPROPERTY()
	TMap<FName, FFinareManagedFunctionBinding> ManagedFunctionBindings;

	/** Reviewed Actor/ActorComponent lifecycle identities retained across reload/cook. */
	UPROPERTY()
	TMap<EFinareManagedLifecycleHookKind, FFinareManagedLifecycleBinding>
	ManagedLifecycleBindings;

	/** Enhanced Input routes owned by the live Script Component generation. */
	UPROPERTY()
	TArray<FFinareManagedInputActionBinding> ManagedInputActionBindings;

	/** Reflected delegate routes validated against generation-owned subscriptions. */
	UPROPERTY()
	TArray<FFinareManagedDelegateBinding> ManagedDelegateBindings;

	/** Native interfaces and exact functions implemented by this generated class. */
	UPROPERTY()
	TArray<FFinareManagedInterfaceFunctionBinding> ManagedInterfaceBindings;

	/** Cook-retained program identity used by generated static FunctionLibrary calls. */
	UPROPERTY()
	TObjectPtr<UFinareAsset> ManagedScriptAsset;

	void BindManagedFunctions();
	void PrepareManagedFunctionsForRecompile();
	bool IsManagedFunctionBound(FName FunctionName) const;

	/**
	 * Builds the native-authoritative Blueprint override activation contract
	 * retained by this exact generated class.  The result is always a valid
	 * v0.1 JSON document on success, including an explicit empty route set.
	 */
	bool BuildBlueprintOverrideActivationContract(
		FString& OutJson,
		FString& OutError) const;

	/** Finds the persistent managed override route whose raw member identity matches. */
	const FFinareManagedFunctionBinding* FindManagedOverrideBinding(
		uint64 MemberId) const;

	virtual void Bind() override;
	virtual void PurgeClass(bool bRecompilingOnLoad) override;
	virtual void PostLoad() override;
	virtual void PostLoadDefaultObject(UObject* Object) override;
	virtual void PostInitInstance(
		UObject* InObj,
		FObjectInstancingGraph* InstanceGraph) override;

private:
	void RegisterManagedNativeFunctions();
	void BuildRuntimeLifecycleFunctionBindings();
	void BindManagedFunction(
		FName FunctionName,
		const FFinareManagedFunctionBinding& Binding,
		bool& bOutAllFunctionsBound);

	/** Runtime-only guard; persistent bindings remain the serialized source of truth. */
	bool bManagedFunctionsBound = false;

	/** Prevent duplicate entries in UClass's transient native lookup table. */
	TSet<FName> RegisteredManagedNativeFunctions;

	/**
	 * Runtime-only thunks for lifecycle hooks inherited from ordinary UE
	 * ActorComponent parents. They never enter the serialized managed contract.
	 */
	TMap<FName, FFinareManagedFunctionBinding> RuntimeLifecycleFunctionBindings;

	DECLARE_FUNCTION(execDispatchManagedFunction);
};

/**
 * Product-owned Blueprint proxy for one complete Finare Class.
 *
 * The distinct asset class lets Content Browser route rename and duplicate
 * through the complete source/sidecar/deployment transaction instead of the
 * generic Blueprint-only operations.
 */
UCLASS(BlueprintType)
class FINARERUNTIME_API UFinareBlueprint : public UBlueprint
{
	GENERATED_BODY()

public:
	/** Exact sidecar owned by this generated proxy, independent of its native parent kind. */
	UPROPERTY(VisibleAnywhere, Category="Finare|Class")
	TObjectPtr<UFinareAsset> ScriptAsset;

	/** Keep the product-owned binding carrier for every Blueprint parent kind. */
#if WITH_EDITOR
	virtual UClass* GetBlueprintClass() const override;
#endif
};
