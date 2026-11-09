// Copyright AI003. All rights reserved.

#include "Net/RemotePlayer.h"

#include "Animation/AnimSequence.h"
#include "Components/SkeletalMeshComponent.h"
#include "Components/SceneComponent.h"
#include "Engine/SkeletalMesh.h"

namespace
{
	const TCHAR* CharacterMeshPath = TEXT("/Game/Characters/KenneyProtagonists/characterMedium.characterMedium");
	const TCHAR* IdlePath = TEXT("/Game/Characters/KenneyProtagonists/Anims/idle.idle");
	const TCHAR* RunPath = TEXT("/Game/Characters/KenneyProtagonists/Anims/run.run");
	const TCHAR* JumpPath = TEXT("/Game/Characters/KenneyProtagonists/Anims/jump.jump");
}

ARemotePlayer::ARemotePlayer()
{
	PrimaryActorTick.bCanEverTick = true;

	Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	SetRootComponent(Root);

	Mesh = CreateDefaultSubobject<USkeletalMeshComponent>(TEXT("Mesh"));
	Mesh->SetupAttachment(Root);
	Mesh->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Mesh->SetRelativeLocation(FVector(0.f, 0.f, -96.f));
	Mesh->SetRelativeRotation(FRotator(0.f, -90.f, 0.f));
	Mesh->SetRelativeScale3D(FVector(0.45f));
}

void ARemotePlayer::BeginPlay()
{
	Super::BeginPlay();
	LoadVisual();
}

void ARemotePlayer::LoadVisual()
{
	USkeletalMesh* SkeletalMesh = LoadObject<USkeletalMesh>(nullptr, CharacterMeshPath);
	if (SkeletalMesh == nullptr)
	{
		UE_LOG(LogTemp, Error, TEXT("[AI003][SYNC] missing remote character mesh"));
		return;
	}

	IdleSequence = LoadObject<UAnimSequence>(nullptr, IdlePath);
	RunSequence = LoadObject<UAnimSequence>(nullptr, RunPath);
	JumpSequence = LoadObject<UAnimSequence>(nullptr, JumpPath);

	Mesh->SetSkeletalMeshAsset(SkeletalMesh);
	PlayAnim(TargetAnim);
}

void ARemotePlayer::ApplySnapshot(const FVector& LocationCm, float Yaw, const FString& AnimName)
{
	const float Now = GetWorld() != nullptr ? GetWorld()->GetTimeSeconds() : 0.f;
	if (bHasPreviousTarget && Now > LastSnapshotAt)
	{
		SnapshotVelocity = (LocationCm - PreviousTarget) / FMath::Max(0.001f, Now - LastSnapshotAt);
	}
	PreviousTarget = LocationCm;
	bHasPreviousTarget = true;
	LastSnapshotAt = Now;
	TargetLocation = LocationCm;
	TargetYaw = Yaw;
	TargetAnim = AnimName.IsEmpty() ? TEXT("idle") : AnimName;
	PlayAnim(TargetAnim);
}

void ARemotePlayer::SetMassPose(const FVector& Location, float Yaw)
{
	SetActorLocationAndRotation(Location, FRotator(0.f, Yaw, 0.f));
}

void ARemotePlayer::SetMassVisual(const FString& AnimName, bool bInAlive)
{
	if (AnimName != CurrentAnim)
	{
		PlayAnim(AnimName);
	}
	if (bInAlive != bAlive)
	{
		SetAlive(bInAlive);
	}
}

void ARemotePlayer::TriggerAttack()
{
	AttackUntil = GetWorld() != nullptr ? GetWorld()->GetTimeSeconds() + 0.18f : 0.f;
}

void ARemotePlayer::TriggerHit()
{
	HitUntil = GetWorld() != nullptr ? GetWorld()->GetTimeSeconds() + 0.15f : 0.f;
}

void ARemotePlayer::SetAlive(bool bInAlive)
{
	if (!bInAlive && bAlive && GetWorld() != nullptr)
	{
		DeathAt = GetWorld()->GetTimeSeconds();
	}
	bAlive = bInAlive;
}

void ARemotePlayer::PlayAnim(const FString& AnimName)
{
	if (Mesh == nullptr || IdleSequence == nullptr || AnimName == CurrentAnim)
	{
		return;
	}

	UAnimSequence* Wanted = IdleSequence;
	float Rate = 1.f;

	if (AnimName == TEXT("run"))
	{
		Wanted = RunSequence;
	}
	else if (AnimName == TEXT("walk"))
	{
		Wanted = RunSequence;
		Rate = 0.6f;
	}
	else if (AnimName == TEXT("jump"))
	{
		Wanted = JumpSequence;
	}

	if (Wanted == nullptr)
	{
		return;
	}

	const bool bLoop = AnimName != TEXT("jump");
	Mesh->PlayAnimation(Wanted, bLoop);
	Mesh->SetPlayRate(Rate);
	PlayRate = Rate;
	CurrentSequence = Wanted;
	CurrentAnim = AnimName;
}

void ARemotePlayer::Tick(float DeltaSeconds)
{
	if (bMassDriven)
	{
		Super::Tick(DeltaSeconds);
		return;
	}
	Super::Tick(DeltaSeconds);

	const float Now = GetWorld() != nullptr ? GetWorld()->GetTimeSeconds() : 0.f;

	FVector Target = TargetLocation;
	const float Stale = Now - LastSnapshotAt;
	if (bHasPreviousTarget && Stale > 0.2f)
	{
		// Extrapolate up to 0.5 s when snapshots stop arriving (Quake-style).
		Target += SnapshotVelocity * FMath::Min(Stale, 0.5f);
	}
	if (Now < AttackUntil)
	{
		const float Progress = 1.f - (AttackUntil - Now) / 0.18f;
		Target += FRotator(0.f, TargetYaw, 0.f).Vector() * (FMath::Sin(Progress * PI) * 35.f);
	}
	SetActorLocation(FMath::VInterpTo(GetActorLocation(), Target, DeltaSeconds, 12.f));

	CurrentYaw = FMath::FInterpTo(CurrentYaw, TargetYaw, DeltaSeconds, 12.f);
	if (bAlive)
	{
		SetActorRotation(FRotator(0.f, CurrentYaw, 0.f));
	}
	else
	{
		const float Collapse = FMath::Clamp((Now - DeathAt) / 0.25f, 0.f, 1.f);
		SetActorRotation(FRotator(FMath::Lerp(0.f, 90.f, Collapse), CurrentYaw, 0.f));
	}

	// Hit feedback without material assets: a short scale pulse.
	const float Pulse = Now < HitUntil ? 1.08f : 1.f;
	SetActorScale3D(FVector(Pulse));

	if (Mesh != nullptr && CurrentSequence != nullptr)
	{
		Mesh->SetPlayRate(bAlive ? PlayRate : 0.f);
	}
}
