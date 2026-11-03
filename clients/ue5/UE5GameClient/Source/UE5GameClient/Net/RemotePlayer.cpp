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
	TargetLocation = LocationCm;
	TargetYaw = Yaw;
	TargetAnim = AnimName.IsEmpty() ? TEXT("idle") : AnimName;
	PlayAnim(TargetAnim);
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
	Super::Tick(DeltaSeconds);

	SetActorLocation(FMath::VInterpTo(GetActorLocation(), TargetLocation, DeltaSeconds, 12.f));
	CurrentYaw = FMath::FInterpTo(CurrentYaw, TargetYaw, DeltaSeconds, 12.f);
	SetActorRotation(FRotator(0.f, CurrentYaw, 0.f));

	if (Mesh != nullptr && CurrentSequence != nullptr)
	{
		Mesh->SetPlayRate(PlayRate);
	}
}
