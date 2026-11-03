#include "World/WorldCharacter.h"

#include "Animation/AnimSequence.h"
#include "Camera/CameraComponent.h"
#include "Components/CapsuleComponent.h"
#include "Components/InputComponent.h"
#include "Components/SkeletalMeshComponent.h"
#include "Engine/SkeletalMesh.h"
#include "GameFramework/CharacterMovementComponent.h"
#include "GameFramework/SpringArmComponent.h"
#include "GameFramework/WorldSettings.h"

AWorldCharacter::AWorldCharacter()
{
	PrimaryActorTick.bCanEverTick = true;

	GetCapsuleComponent()->InitCapsuleSize(42.f, 96.f);

	bUseControllerRotationPitch = false;
	bUseControllerRotationYaw = false;
	bUseControllerRotationRoll = false;

	UCharacterMovementComponent* Movement = GetCharacterMovement();
	Movement->bOrientRotationToMovement = true;
	Movement->RotationRate = FRotator(0.f, 720.f, 0.f);
	Movement->MaxWalkSpeed = WalkSpeed;
	Movement->JumpZVelocity = 520.f;
	Movement->AirControl = 0.35f;
	Movement->MaxStepHeight = 45.f;
	Movement->SetWalkableFloorAngle(48.f);
	Movement->BrakingDecelerationWalking = 2000.f;

	SpringArm = CreateDefaultSubobject<USpringArmComponent>(TEXT("SpringArm"));
	SpringArm->SetupAttachment(RootComponent);
	SpringArm->TargetArmLength = 650.f;
	SpringArm->SocketOffset = FVector(0.f, 0.f, 80.f);
	SpringArm->bUsePawnControlRotation = true;
	SpringArm->bEnableCameraLag = true;
	SpringArm->CameraLagSpeed = 12.f;

	Camera = CreateDefaultSubobject<UCameraComponent>(TEXT("Camera"));
	Camera->SetupAttachment(SpringArm, USpringArmComponent::SocketName);
	Camera->bUsePawnControlRotation = false;
	Camera->FieldOfView = 62.f;

	// The Kenney mesh is attached in BeginPlay; no static placeholder parts.
}

void AWorldCharacter::BeginPlay()
{
	Super::BeginPlay();
	LoadCharacterAssets();
}

void AWorldCharacter::LoadCharacterAssets()
{
	USkeletalMesh* CharacterMesh = LoadObject<USkeletalMesh>(
		nullptr, TEXT("/Game/Characters/KenneyProtagonists/characterMedium.characterMedium"));
	if (CharacterMesh == nullptr)
	{
		UE_LOG(LogTemp, Error, TEXT("[AI003] missing character mesh /Game/Characters/KenneyProtagonists/characterMedium"));
		return;
	}

	IdleSequence = LoadObject<UAnimSequence>(
		nullptr, TEXT("/Game/Characters/KenneyProtagonists/Anims/idle.idle"));
	RunSequence = LoadObject<UAnimSequence>(
		nullptr, TEXT("/Game/Characters/KenneyProtagonists/Anims/run.run"));
	JumpSequence = LoadObject<UAnimSequence>(
		nullptr, TEXT("/Game/Characters/KenneyProtagonists/Anims/jump.jump"));

	USkeletalMeshComponent* MeshComponent = GetMesh();
	MeshComponent->SetSkeletalMeshAsset(CharacterMesh);
	// Mesh pivot is at the feet: lift it to the capsule centre and scale the
	// 3.76 m source down to a 1.7 m character.
	MeshComponent->SetRelativeLocation(FVector(0.f, 0.f, -96.f));
	// The Kenney FBX faces -Y after UE's axis conversion; turn it to the actor's +X.
	MeshComponent->SetRelativeRotation(FRotator(0.f, -90.f, 0.f));
	MeshComponent->SetRelativeScale3D(FVector(0.45f));
	MeshComponent->SetCollisionEnabled(ECollisionEnabled::NoCollision);

	CurrentSequence = nullptr;
	UpdateAnimation();
}

void AWorldCharacter::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);

	if (bAutoRun && Controller != nullptr)
	{
		AddMovementInput(GetActorForwardVector(), 1.f);
	}

	UpdateAnimation();
}

void AWorldCharacter::UpdateAnimation()
{
	USkeletalMeshComponent* MeshComponent = GetMesh();
	if (MeshComponent == nullptr || IdleSequence == nullptr)
	{
		return;
	}

	const float Speed = GetVelocity().Size2D();
	const bool bGrounded = GetCharacterMovement()->IsMovingOnGround();

	UAnimSequence* Wanted = bGrounded
		? (Speed > 10.f ? RunSequence.Get() : IdleSequence.Get())
		: JumpSequence.Get();
	if (Wanted == nullptr)
	{
		return;
	}

	if (Wanted != CurrentSequence)
	{
		// Loop locomotion, play the jump once and hold its last pose until landing.
		MeshComponent->PlayAnimation(Wanted, Wanted != JumpSequence);
		CurrentSequence = Wanted;
		UE_LOG(LogTemp, Display, TEXT("[AI003][ANIM] %s grounded=%d speed=%.0f z=%.0f"),
			*Wanted->GetName(), bGrounded ? 1 : 0, Speed, GetActorLocation().Z);
	}

	if (Wanted == RunSequence)
	{
		MeshComponent->SetPlayRate(FMath::Clamp(Speed / 300.f, 0.6f, 1.6f));
	}
	else if (Wanted == IdleSequence)
	{
		MeshComponent->SetPlayRate(1.f);
	}
}

void AWorldCharacter::SetupPlayerInputComponent(UInputComponent* PlayerInputComponent)
{
	Super::SetupPlayerInputComponent(PlayerInputComponent);
	if (PlayerInputComponent == nullptr)
	{
		return;
	}

	PlayerInputComponent->BindAxis(TEXT("MoveForward"), this, &AWorldCharacter::MoveForward);
	PlayerInputComponent->BindAxis(TEXT("MoveRight"), this, &AWorldCharacter::MoveRight);
	PlayerInputComponent->BindAxis(TEXT("Turn"), this, &AWorldCharacter::AddControllerYawInput);
	PlayerInputComponent->BindAxis(TEXT("LookUp"), this, &AWorldCharacter::AddControllerPitchInput);

	PlayerInputComponent->BindAction(TEXT("Jump"), IE_Pressed, this, &ACharacter::Jump);
	PlayerInputComponent->BindAction(TEXT("Jump"), IE_Released, this, &ACharacter::StopJumping);
	PlayerInputComponent->BindAction(TEXT("Sprint"), IE_Pressed, this, &AWorldCharacter::BeginSprint);
	PlayerInputComponent->BindAction(TEXT("Sprint"), IE_Released, this, &AWorldCharacter::EndSprint);
}

void AWorldCharacter::MoveForward(float Value)
{
	if (Controller == nullptr || FMath::IsNearlyZero(Value))
	{
		return;
	}

	const FRotator YawRotation(0.f, Controller->GetControlRotation().Yaw, 0.f);
	const FVector Direction = FRotationMatrix(YawRotation).GetUnitAxis(EAxis::X);
	AddMovementInput(Direction, Value);
}

void AWorldCharacter::MoveRight(float Value)
{
	if (Controller == nullptr || FMath::IsNearlyZero(Value))
	{
		return;
	}

	const FRotator YawRotation(0.f, Controller->GetControlRotation().Yaw, 0.f);
	const FVector Direction = FRotationMatrix(YawRotation).GetUnitAxis(EAxis::Y);
	AddMovementInput(Direction, Value);
}

void AWorldCharacter::BeginSprint()
{
	bSprinting = true;
	GetCharacterMovement()->MaxWalkSpeed = SprintSpeed;
}

void AWorldCharacter::EndSprint()
{
	bSprinting = false;
	GetCharacterMovement()->MaxWalkSpeed = WalkSpeed;
}
