#include "World/WorldCharacter.h"

#include "Camera/CameraComponent.h"
#include "Components/CapsuleComponent.h"
#include "Components/InputComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "GameFramework/CharacterMovementComponent.h"
#include "GameFramework/SpringArmComponent.h"
#include "Materials/MaterialInterface.h"

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

	BodyMesh = CreateDefaultSubobject<UStaticMeshComponent>(TEXT("BodyMesh"));
	BodyMesh->SetupAttachment(GetCapsuleComponent());
	BodyMesh->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	BodyMesh->SetRelativeScale3D(FVector(0.84f, 0.84f, 1.92f));

	FacingMesh = CreateDefaultSubobject<UStaticMeshComponent>(TEXT("FacingMesh"));
	FacingMesh->SetupAttachment(GetCapsuleComponent());
	FacingMesh->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	FacingMesh->SetRelativeLocation(FVector(52.f, 0.f, 40.f));
	FacingMesh->SetRelativeScale3D(FVector(0.4f, 0.16f, 0.12f));
}

void AWorldCharacter::BeginPlay()
{
	Super::BeginPlay();

	UStaticMesh* Cylinder = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cylinder.Cylinder"));
	UStaticMesh* Cube = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cube.Cube"));
	UMaterialInterface* BodyMaterial = LoadObject<UMaterialInterface>(nullptr, TEXT("/Game/AI003/M_PlayerBody.M_PlayerBody"));
	UMaterialInterface* AccentMaterial = LoadObject<UMaterialInterface>(nullptr, TEXT("/Game/AI003/M_PlayerAccent.M_PlayerAccent"));

	if (Cylinder != nullptr) BodyMesh->SetStaticMesh(Cylinder);
	if (Cube != nullptr) FacingMesh->SetStaticMesh(Cube);
	if (BodyMaterial != nullptr) BodyMesh->SetMaterial(0, BodyMaterial);
	if (AccentMaterial != nullptr) FacingMesh->SetMaterial(0, AccentMaterial);

	// Keep the camera from clipping through the skull cylinder.
	BodyMesh->SetCastShadow(true);
}

void AWorldCharacter::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);

	if (bAutoRun && Controller != nullptr)
	{
		AddMovementInput(GetActorForwardVector(), 1.f);
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
