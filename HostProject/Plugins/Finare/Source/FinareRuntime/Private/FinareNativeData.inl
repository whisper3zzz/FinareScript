// Included by FinareInvocationBridge.cpp to share the canonical property codec.
namespace UE::Finare::NativeData
{
	// UE mutation is game-thread-only, but reject worker dispatch before it queues
	// behind a game thread that may be waiting for that worker inside the callback.
	std::atomic_bool Borrowed{false};
	bool Reject(FFinareInvocationResult& Result, EFinareInvocationError Error, FString Message)
	{ Invocation::Private::Fail(Result, Error, MoveTemp(Message)); return false; }
	struct FLocation { UObject* Object = nullptr; FProperty* Root = nullptr; FProperty* Property = nullptr; void* Address = nullptr; };

	bool Writable(UObject* Object, FProperty* Property)
	{
		if (Property->HasAnyPropertyFlags(CPF_BlueprintReadOnly | CPF_EditConst)) return false;
		if (Property->HasAnyPropertyFlags(CPF_Net))
		{
			auto* Owner = Invocation::Private::NetworkOwner(Object);
			if (!Owner || !Owner->HasAuthority()) return false;
		}
		return true;
	}
	bool Locate(FFinareObjectRegistry& Registry, FFinareObjectHandle Target, const FString& Path,
		bool bWrite, FLocation& Out, FFinareInvocationResult& Result)
	{
		using namespace Invocation::Private;
		if (!IsInGameThread() || Borrowed) return Reject(Result, EFinareInvocationError::TransportUnavailable,
			TEXT("Native data requires the game thread outside a borrowed-memory callback."));
		Out.Object = ResolveTarget(Registry, Target, Result);
		if (!Out.Object) return false;
		int32 Colon;
		if (!Path.FindChar(':', Colon)) return Reject(Result, EFinareInvocationError::InvalidMemberPath, TEXT("A canonical property path is required."));
		int32 End = Colon + 1;
		while (End < Path.Len() && Path[End] != '.' && Path[End] != '[') ++End;
		UClass* Owner = nullptr; FName Name;
		if (!ResolveMemberIdentity(Out.Object, Path.Left(End), Owner, Name, Result)) return false;
		Out.Root = Out.Property = FindFProperty<FProperty>(Owner, Name);
		if (!Out.Property) return Reject(Result, EFinareInvocationError::PropertyNotFound, TEXT("Native property was not found."));
		Out.Address = Out.Property->ContainerPtrToValuePtr<void>(Out.Object);
		int32 Depth = 0; bool bInner = false;
		for (;;)
		{
			if (++Depth > 16 || Out.Property->ArrayDim != 1 ||
				(!bInner && !Out.Property->HasAnyPropertyFlags(CPF_BlueprintVisible | CPF_NativeAccessSpecifierPublic)) ||
				(bWrite && !Writable(Out.Object, Out.Property)))
				return Reject(Result, EFinareInvocationError::PropertyNotWritable, TEXT("Native view access violates property visibility, authority or shape."));
			if (End == Path.Len()) return true;
			bInner = false;
			if (Path[End] == '.')
			{
				auto* Struct = CastField<FStructProperty>(Out.Property);
				if (!Struct) return Reject(Result, EFinareInvocationError::ValueTypeMismatch, TEXT("A struct field was expected."));
				const int32 Start = ++End;
				while (End < Path.Len() && Path[End] != '.' && Path[End] != '[') ++End;
				Out.Property = FindFProperty<FProperty>(Struct->Struct, FName(*Path.Mid(Start, End - Start)));
				if (!Out.Property) return Reject(Result, EFinareInvocationError::PropertyNotFound, TEXT("Native struct field was not found."));
				Out.Address = Out.Property->ContainerPtrToValuePtr<void>(Out.Address);
			}
			else if (Path[End] == '[')
			{
				auto* Array = CastField<FArrayProperty>(Out.Property);
				const int32 Start = ++End;
				while (End < Path.Len() && FChar::IsDigit(Path[End])) ++End;
				if (!Array || End == Start || End >= Path.Len() || Path[End] != ']')
					return Reject(Result, EFinareInvocationError::InvalidMemberPath, TEXT("Invalid native array index."));
				int32 Index = INDEX_NONE;
				if (!LexTryParseString(Index, *Path.Mid(Start, End - Start)))
					return Reject(Result, EFinareInvocationError::InvalidMemberPath, TEXT("Invalid native array index."));
				++End;
				FScriptArrayHelper Helper(Array, Out.Address);
				if (!Helper.IsValidIndex(Index)) return Reject(Result, EFinareInvocationError::ValueTypeMismatch, TEXT("Native array index is out of range."));
				Out.Address = Helper.GetRawPtr(Index); Out.Property = Array->Inner;
				bInner = true;
				// Array inner properties inherit accessibility from the containing property.
				if (End == Path.Len()) return true;
			}
			else return Reject(Result, EFinareInvocationError::InvalidMemberPath, TEXT("Invalid native view path."));
		}
	}

	struct FTemporary
	{
		FProperty* Property; void* Data;
		explicit FTemporary(FProperty* InProperty) : Property(InProperty),
			Data(FMemory::Malloc(Property->GetSize(), Property->GetMinAlignment())) { Property->InitializeValue(Data); }
		~FTemporary() { Property->DestroyValue(Data); FMemory::Free(Data); }
	};
}

bool Finare_IsNativeMemoryBorrowed() { return UE::Finare::NativeData::Borrowed; }
bool Finare_WithNativeMemory(void* Data, int32 Count, uint32 ElementSize, FFinareMemoryCallback Callback, void* UserData)
{
	using namespace UE::Finare::NativeData;
	if (!IsInGameThread() || Borrowed || !Callback || Count < 0 || (Count && !Data) || !ElementSize ||
		static_cast<uint64>(Count) * ElementSize > FFinareByteBuffer::MaximumCallBytes) return false;
	Borrowed = true; ON_SCOPE_EXIT { Borrowed = false; };
	Callback(UserData, Data, Count); return true;
}

bool FFinareInvocationBridge::AccessNativeData(FFinareObjectHandle Target, const FString& Path,
	uint8 Operation, TConstArrayView<FFinareValue> Arguments, FFinareValue& Value, FFinareInvocationResult& Result)
{
	using namespace UE::Finare::Invocation::Private;
	using namespace UE::Finare::NativeData;
	const bool bWrite = Operation == 1 || Operation == 3 || Operation == 4 || Operation == 5 || Operation == 7;
	FLocation Location;
	if (!Locate(Registry, Target, Path, bWrite, Location, Result)) return false;
	FProperty* Property = Location.Property; void* Address = Location.Address;
	auto* Array = CastField<FArrayProperty>(Property);
	auto* Set = CastField<FSetProperty>(Property);
	auto* Map = CastField<FMapProperty>(Property);
	const auto Invalid = [&]() { return Reject(Result, EFinareInvocationError::ValueTypeMismatch, TEXT("Native data operation has incompatible arguments or property type.")); };
	if (Operation == 0 && Arguments.IsEmpty()) return ReadValue(Registry, Property, Address, Value, Result, true);
	if (Operation == 1 && Arguments.Num() == 1)
	{
		if (!WriteValue(Registry, Property, Address, Arguments[0], Result, true)) return false;
	}
	else if (Operation == 2 && Arguments.IsEmpty())
	{
		int32 Count = Array ? FScriptArrayHelper(Array, Address).Num() : Set ? FScriptSetHelper(Set, Address).Num() : Map ? FScriptMapHelper(Map, Address).Num() : -1;
		if (Count < 0) return Invalid(); Value = FFinareValue::FromInt32(Count);
	}
	else if (Operation == 5 && Arguments.IsEmpty())
	{
		if (Array) FScriptArrayHelper(Array, Address).EmptyValues();
		else if (Set) FScriptSetHelper(Set, Address).EmptyElements();
		else if (Map) FScriptMapHelper(Map, Address).EmptyValues(); else return Invalid();
	}
	else if (Array && Operation == 3 && Arguments.Num() == 1)
	{
		FTemporary Element(Array->Inner);
		if (!WriteValue(Registry, Array->Inner, Element.Data, Arguments[0], Result, true)) return false;
		FScriptArrayHelper Helper(Array, Address);
		const int32 Index = Helper.AddValue(); Array->Inner->CopySingleValue(Helper.GetRawPtr(Index), Element.Data);
	}
	else if (Array && Operation == 4 && Arguments.Num() == 1)
	{
		int32 Index; FScriptArrayHelper Helper(Array, Address);
		if (!Arguments[0].TryGetInt32(Index) || !Helper.IsValidIndex(Index)) return Invalid();
		Helper.RemoveValues(Index);
	}
	else if (Set && Arguments.Num() == 1 && (Operation == 3 || Operation == 4 || Operation == 6))
	{
		if (!Set->ElementProp->HasAllPropertyFlags(CPF_HasGetValueTypeHash)) return Invalid();
		FTemporary Element(Set->ElementProp);
		if (!WriteValue(Registry, Set->ElementProp, Element.Data, Arguments[0], Result, true)) return false;
		FScriptSetHelper Helper(Set, Address); const int32 Index = Helper.FindElementIndexFromHash(Element.Data);
		Value = FFinareValue::FromBoolean(Index != INDEX_NONE);
		if (Operation == 3) { Helper.AddElement(Element.Data); Value = FFinareValue::FromBoolean(Index == INDEX_NONE); }
		else if (Operation == 4 && Index != INDEX_NONE) Helper.RemoveAt(Index);
	}
	else if (Map && ((Operation == 7 && Arguments.Num() == 2) || ((Operation == 4 || Operation == 6) && Arguments.Num() == 1)))
	{
		if (!Map->KeyProp->HasAllPropertyFlags(CPF_HasGetValueTypeHash)) return Invalid();
		FTemporary Key(Map->KeyProp);
		if (!WriteValue(Registry, Map->KeyProp, Key.Data, Arguments[0], Result, true)) return false;
		FScriptMapHelper Helper(Map, Address); const int32 Index = Helper.FindMapIndexWithKey(Key.Data);
		if (Operation == 7)
		{
			FTemporary Item(Map->ValueProp);
			if (!WriteValue(Registry, Map->ValueProp, Item.Data, Arguments[1], Result, true)) return false;
			Helper.AddPair(Key.Data, Item.Data);
		}
		else if (Operation == 4) { Value = FFinareValue::FromBoolean(Index != INDEX_NONE); if (Index != INDEX_NONE) Helper.RemoveAt(Index); }
		else { if (Index == INDEX_NONE) return Invalid(); return ReadValue(Registry, Map->ValueProp, Helper.GetValuePtr(Index), Value, Result, true); }
	}
	else if (Operation == 8 && Arguments.Num() == 1 && (Set || Map))
	{
		int32 Position;
		if (!Arguments[0].TryGetInt32(Position) || Position < 0) return Invalid();
		const int32 Maximum = Set ? FScriptSetHelper(Set, Address).GetMaxIndex() : FScriptMapHelper(Map, Address).GetMaxIndex();
		for (int32 Index = 0; Index < Maximum; ++Index)
		{
			if (!(Set ? FScriptSetHelper(Set, Address).IsValidIndex(Index) : FScriptMapHelper(Map, Address).IsValidIndex(Index))) continue;
			if (Position-- == 0) return ReadValue(Registry, Set ? Set->ElementProp : Map->KeyProp,
				Set ? FScriptSetHelper(Set, Address).GetElementPtr(Index) : FScriptMapHelper(Map, Address).GetKeyPtr(Index), Value, Result, true);
		}
		return Invalid();
	}
	else return Invalid();
	if (bWrite) MARK_PROPERTY_DIRTY(Location.Object, Location.Root);
	Succeed(Result); return true;
}

bool FFinareInvocationBridge::BorrowNativeData(FFinareObjectHandle Target, const FString& Path,
	uint8 ElementKind, uint32 ElementSize, bool bWrite, FFinareMemoryCallback Callback, void* UserData, FString& Error)
{
	using namespace UE::Finare::NativeData;
	FFinareInvocationResult Result; FLocation Location;
	if (!Callback || !Locate(Registry, Target, Path, bWrite, Location, Result)) { Error = Result.Message; return false; }
	FProperty* Element = Location.Property; void* Data = Location.Address; int32 Count = 1;
	if (auto* Array = CastField<FArrayProperty>(Element))
	{
		FScriptArrayHelper Helper(Array, Data); Count = Helper.Num(); Data = Count ? Helper.GetRawPtr(0) : nullptr; Element = Array->Inner;
	}
	const bool bCompatible =
		(ElementKind == 1 && CastField<FByteProperty>(Element) && !CastFieldChecked<FByteProperty>(Element)->Enum) ||
		(ElementKind == 2 && CastField<FIntProperty>(Element)) ||
		(ElementKind == 3 && CastField<FFloatProperty>(Element)) ||
		(ElementKind == 4 && CastField<FDoubleProperty>(Element)) ||
		(ElementKind == 5 && CastField<FInt64Property>(Element)) ||
		(ElementKind == 6 && CastField<FUInt32Property>(Element));
	if (!bCompatible || Element->GetElementSize() != ElementSize || Element->ArrayDim != 1 ||
		static_cast<uint64>(Count) * ElementSize > FFinareByteBuffer::MaximumCallBytes)
	{ Error = TEXT("Span element layout must exactly match supported contiguous native numeric storage."); return false; }
	TStrongObjectPtr<UObject> KeepAlive(Location.Object);
	Borrowed = true;
	ON_SCOPE_EXIT { Borrowed = false; };
	Callback(UserData, Data, Count);
	Borrowed = false;
	if (bWrite) MARK_PROPERTY_DIRTY(Location.Object, Location.Root);
	return true;
}
