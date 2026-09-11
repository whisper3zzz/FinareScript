#pragma once

#include "SGraphNode.h"

class UFinareEdGraphNode;

/** Blueprint-style card for a semantic Authoring Surface node. */
class SFinareGraphNode final : public SGraphNode
{
public:
	SLATE_BEGIN_ARGS(SFinareGraphNode)
		{
		}

	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs, UFinareEdGraphNode* InNode);

protected:
	virtual TSharedRef<SWidget> CreateNodeContentArea() override;
};
