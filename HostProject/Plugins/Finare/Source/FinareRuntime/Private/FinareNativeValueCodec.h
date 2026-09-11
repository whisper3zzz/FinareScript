#pragma once

#include "FinareNativeTransport.h"

// Shared module-internal codecs for forward calls and reverse callback frames.
namespace UE::Finare::NativeTransport::Private
{
	bool DecodeValueV3(const FFinareNativeValueV3&, const FFinareNativeCallFrameV3&,
	                   uint32 EntryByteCount, FFinareValue&, uint32 Depth);
	bool EncodeValueV3(const FFinareValue&, FFinareNativeCallFrameV3&,
	                   FFinareNativeValueV3&, uint32& ByteCursor, uint32 Depth);
}
