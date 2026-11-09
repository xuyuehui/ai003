#include "Mass/FighterMass.h"

#include "MassExecutionContext.h"

UAI003FighterInterpProcessor::UAI003FighterInterpProcessor()
	: EntityQuery(*this)
{
	ExecutionFlags = static_cast<int32>(EProcessorExecutionFlags::All);
	bAutoRegisterWithProcessingPhases = true;
}

void UAI003FighterInterpProcessor::ConfigureQueries(const TSharedRef<FMassEntityManager>& EntityManager)
{
	EntityQuery.AddRequirement<FAI003FighterFragment>(EMassFragmentAccess::ReadWrite);
	EntityQuery.AddTagRequirement<FAI003RemoteFighterTag>(EMassFragmentPresence::All);
}

void UAI003FighterInterpProcessor::Execute(FMassEntityManager& EntityManager, FMassExecutionContext& Context)
{
	EntityQuery.ForEachEntityChunk(Context, [](FMassExecutionContext& Chunk)
	{
		const double Now = FPlatformTime::Seconds();
		const float Delta = FMath::Clamp(Chunk.GetDeltaTimeSeconds(), 0.001f, 0.1f);
		auto Fragments = Chunk.GetMutableFragmentView<FAI003FighterFragment>();
		for (int32 i = 0; i < Chunk.GetNumEntities(); ++i)
		{
			FAI003FighterFragment& F = Fragments[i];
			const double Stale = FMath::Max(0.0, Now - F.LastUpdate);
			FVector Desired = F.TargetLocation;
			if (Stale > 0.2)
			{
				Desired += F.Velocity * static_cast<float>(FMath::Min(Stale, 0.5));
			}
			const float Alpha = 1.f - FMath::Exp(-12.f * Delta);
			F.RenderLocation = FMath::Lerp(F.RenderLocation, Desired, Alpha);

			// Shortest-arc yaw interpolation.
			float DeltaYaw = FMath::FindDeltaAngleDegrees(F.RenderYaw, F.Yaw);
			F.RenderYaw = FMath::UnwindDegrees(F.RenderYaw + DeltaYaw * Alpha);
		}
	});
}
