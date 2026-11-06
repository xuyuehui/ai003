// Copyright AI003. All rights reserved.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"

#include "RemotePlayer.generated.h"

class USkeletalMeshComponent;
class USceneComponent;
class UAnimSequence;

/**
 * Lightweight proxy for another player, driven by gateway snapshots.
 * Reuses the Kenney skeletal mesh and the idle/run/jump sequences; positions are
 * interpolated towards the authoritative value (the server sends 10 Hz).
 */
UCLASS()
class UE5GAMECLIENT_API ARemotePlayer : public AActor
{
	GENERATED_BODY()

public:
	ARemotePlayer();

	/** Feed the newest snapshot; smoothing happens in Tick. */
	void ApplySnapshot(const FVector& LocationCm, float Yaw, const FString& AnimName);

	void TriggerAttack();
	void TriggerHit();
	void SetAlive(bool bInAlive);

protected:
	virtual void BeginPlay() override;
	virtual void Tick(float DeltaSeconds) override;

private:
	void LoadVisual();
	void PlayAnim(const FString& AnimName);

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USceneComponent> Root;

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USkeletalMeshComponent> Mesh;

	UPROPERTY(Transient)
	TObjectPtr<UAnimSequence> IdleSequence;

	UPROPERTY(Transient)
	TObjectPtr<UAnimSequence> RunSequence;

	UPROPERTY(Transient)
	TObjectPtr<UAnimSequence> JumpSequence;

	UPROPERTY(Transient)
	TObjectPtr<UAnimSequence> CurrentSequence;

	FVector TargetLocation = FVector::ZeroVector;
	FVector PreviousTarget = FVector::ZeroVector;
	FVector SnapshotVelocity = FVector::ZeroVector;
	float LastSnapshotAt = 0.f;
	bool bHasPreviousTarget = false;
	float TargetYaw = 0.f;
	float CurrentYaw = 0.f;
	FString TargetAnim = TEXT("idle");
	FString CurrentAnim;
	float PlayRate = 1.f;
	float AttackUntil = 0.f;
	float HitUntil = 0.f;
	float DeathAt = 0.f;
	bool bAlive = true;
};
