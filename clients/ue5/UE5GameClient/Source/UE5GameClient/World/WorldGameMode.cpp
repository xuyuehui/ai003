#include "World/WorldGameMode.h"

#include "Components/DirectionalLightComponent.h"
#include "Components/CapsuleComponent.h"
#include "Components/ExponentialHeightFogComponent.h"
#include "Components/SkyAtmosphereComponent.h"
#include "Components/SkyLightComponent.h"
#include "Engine/DirectionalLight.h"
#include "Engine/ExponentialHeightFog.h"
#include "Engine/SkyLight.h"
#include "Engine/World.h"
#include "GameFramework/CharacterMovementComponent.h"
#include "GameFramework/PlayerController.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformMisc.h"
#include "Kismet/GameplayStatics.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "TimerManager.h"
#include "UnrealClient.h"
#include "World/WorldCharacter.h"
#include "World/WorldData.h"
#include "World/WorldTerrainActor.h"

AWorldGameMode::AWorldGameMode()
{
	DefaultPawnClass = AWorldCharacter::StaticClass();
	PlayerControllerClass = APlayerController::StaticClass();
}

void AWorldGameMode::InitGame(const FString& MapName, const FString& Options, FString& ErrorMessage)
{
	Super::InitGame(MapName, Options, ErrorMessage);

	bWorldSmoke = FParse::Param(FCommandLine::Get(), TEXT("AI003WorldSmoke"));
	bWorldShot = FParse::Param(FCommandLine::Get(), TEXT("AI003WorldShot"));

	if (bWorldShot)
	{
		FString Relative = FPaths::Combine(FPaths::ProjectDir(), TEXT("../../../artifacts/world-ue5.png"));
		FPaths::CollapseRelativeDirectories(Relative);
		ShotPath = FPaths::ConvertRelativePathToFull(Relative);
	}

	WorldData = MakeShared<FAI003WorldData>();
	FString Error;
	const FString Directory = FPaths::Combine(FPaths::ProjectDir(), TEXT("WorldData"));
	if (!WorldData->Load(Directory, Error))
	{
		UE_LOG(LogTemp, Error, TEXT("[AI003] cannot load world: %s"), *Error);
		WorldData.Reset();
		return;
	}

	// Data is in metres, Unreal works in centimetres.
	SpawnLocation = WorldData->SpawnUE() * 100.f + FVector(0.f, 0.f, 120.f);

	UE_LOG(LogTemp, Display, TEXT("[AI003] world loaded: %s seed=%lld size=%.0f m res=%d spawn=(%.0f, %.0f, %.0f)"),
		*WorldData->Name, WorldData->Seed, WorldData->SizeMeters, WorldData->Resolution,
		WorldData->SpawnUE().X, WorldData->SpawnUE().Y, WorldData->SpawnUE().Z);
}

void AWorldGameMode::StartPlay()
{
	SpawnEnvironment();
	Super::StartPlay();

	if (bWorldSmoke || bWorldShot)
	{
		GetWorldTimerManager().SetTimer(AutomationTimer, this, &AWorldGameMode::BeginAutomation, 3.f, false);
	}
}

void AWorldGameMode::HandleStartingNewPlayer_Implementation(APlayerController* NewPlayer)
{
	if (NewPlayer == nullptr)
	{
		return;
	}

	const FRotator Rotation(0.f, 180.f, 0.f);
	RestartPlayerAtTransform(NewPlayer, FTransform(Rotation, SpawnLocation));

	NewPlayer->SetInputMode(FInputModeGameOnly());
	NewPlayer->bShowMouseCursor = false;
}

void AWorldGameMode::SpawnEnvironment()
{
	if (!WorldData.IsValid())
	{
		return;
	}

	UWorld* World = GetWorld();
	if (World == nullptr)
	{
		return;
	}

	TerrainActor = World->SpawnActor<AWorldTerrainActor>();
	if (TerrainActor != nullptr)
	{
		TerrainActor->Build(*WorldData);
	}

	// Sun.
	ADirectionalLight* Sun = World->SpawnActor<ADirectionalLight>(
		FVector::ZeroVector, FRotator(-46.f, 25.f, 0.f));
	if (Sun != nullptr)
	{
		Sun->SetMobility(EComponentMobility::Movable);
		if (UDirectionalLightComponent* LightComponent = Cast<UDirectionalLightComponent>(Sun->GetLightComponent()))
		{
			LightComponent->SetIntensity(5.f);
			LightComponent->SetLightColor(FLinearColor(1.f, 0.95f, 0.86f));
			LightComponent->SetAtmosphereSunLight(true);
			LightComponent->SetCastShadows(true);
		}
	}

	// Fill light from the opposite side: fakes sky bounce so backlit objects and
	// the character are readable, the same role Unity's ambient probe plays.
	ADirectionalLight* Fill = World->SpawnActor<ADirectionalLight>(
		FVector::ZeroVector, FRotator(-28.f, 205.f, 0.f));
	if (Fill != nullptr)
	{
		Fill->SetMobility(EComponentMobility::Movable);
		if (UDirectionalLightComponent* FillComponent = Cast<UDirectionalLightComponent>(Fill->GetLightComponent()))
		{
			FillComponent->SetIntensity(1.6f);
			FillComponent->SetLightColor(FLinearColor(0.55f, 0.68f, 0.9f));
			FillComponent->SetCastShadows(false);
			FillComponent->SetAtmosphereSunLight(false);
		}
	}

	// Sky dome and sky light.
	World->SpawnActor<ASkyAtmosphere>();

	ASkyLight* SkyLight = World->SpawnActor<ASkyLight>();
	if (SkyLight != nullptr)
	{
		if (USkyLightComponent* SkyComponent = SkyLight->GetLightComponent())
		{
			SkyComponent->SetMobility(EComponentMobility::Movable);
			SkyComponent->bRealTimeCapture = true;
			SkyComponent->SetIntensity(1.2f);
			SkyComponent->RecaptureSky();
		}
	}

	// Distance fog, close to the Unity settings.
	AExponentialHeightFog* Fog = World->SpawnActor<AExponentialHeightFog>();
	if (Fog != nullptr)
	{
		if (UExponentialHeightFogComponent* FogComponent = Fog->GetComponent())
		{
			FogComponent->SetFogDensity(0.0015f);
			FogComponent->SetFogHeightFalloff(0.08f);
			FogComponent->SetFogInscatteringColor(FLinearColor(0.52f, 0.64f, 0.78f));
			FogComponent->SetStartDistance(30000.f);
		}
	}
}

void AWorldGameMode::BeginAutomation()
{
	if (bWorldShot)
	{
		IFileManager::Get().MakeDirectory(*FPaths::GetPath(ShotPath), true);
		FScreenshotRequest::RequestScreenshot(ShotPath, false, false);
		UE_LOG(LogTemp, Display, TEXT("[AI003][SHOT] requested %s"), *ShotPath);
		GetWorldTimerManager().SetTimer(
			AutomationTimer, FTimerDelegate::CreateLambda([this]() { ExitWith(0); }), 2.f, false);
		return;
	}

	RunSmokeStage1();
}

void AWorldGameMode::RunSmokeStage1()
{
	AWorldCharacter* Character = Cast<AWorldCharacter>(UGameplayStatics::GetPlayerPawn(this, 0));
	if (Character == nullptr || !WorldData.IsValid())
	{
		UE_LOG(LogTemp, Error, TEXT("[AI003][WORLD] FAIL: no player pawn or world data"));
		ExitWith(1);
		return;
	}

	const FVector Location = Character->GetActorLocation();
	const float GroundMetres = WorldData->HeightAt(Location.X / 100.f, Location.Y / 100.f);
	const float AboveGround = Location.Z - GroundMetres * 100.f;
	const float CapsuleHalfHeight = Character->GetCapsuleComponent()->GetScaledCapsuleHalfHeight();
	const bool bGrounded = Character->GetCharacterMovement()->IsMovingOnGround();

	UE_LOG(LogTemp, Display, TEXT("[AI003][WORLD] player at (%.0f, %.0f, %.0f) cm, ground %.0f cm, above %.0f cm (capsule half %.0f), grounded=%d"),
		Location.X, Location.Y, Location.Z, GroundMetres * 100.f, AboveGround, CapsuleHalfHeight, bGrounded ? 1 : 0);

	// The movement component may take a frame to flip to walking after landing,
	// so accept "resting exactly one capsule half height above the ground" too.
	const bool bStanding = bGrounded || FMath::Abs(AboveGround - CapsuleHalfHeight) <= 60.f;
	if (!bStanding || FMath::Abs(AboveGround) > 250.f)
	{
		UE_LOG(LogTemp, Error, TEXT("[AI003][WORLD] FAIL: player is not standing on the terrain"));
		ExitWith(1);
		return;
	}

	SmokeStartLocation = Location;
	SmokePlayer = Character;
	Character->SetAutoRun(true);
	GetWorldTimerManager().SetTimer(AutomationTimer, this, &AWorldGameMode::RunSmokeStage2, 1.5f, false);
}

void AWorldGameMode::RunSmokeStage2()
{
	AWorldCharacter* Character = SmokePlayer.Get();
	if (Character == nullptr)
	{
		UE_LOG(LogTemp, Error, TEXT("[AI003][WORLD] FAIL: player disappeared"));
		ExitWith(1);
		return;
	}

	Character->SetAutoRun(false);
	const FVector Location = Character->GetActorLocation();
	const float Travelled = FVector::Dist2D(Location, SmokeStartLocation);

	UE_LOG(LogTemp, Display, TEXT("[AI003][WORLD] travelled %.0f cm in 1.5 s"), Travelled);

	if (Travelled < 300.f)
	{
		UE_LOG(LogTemp, Error, TEXT("[AI003][WORLD] FAIL: player barely moved"));
		ExitWith(1);
		return;
	}

	UE_LOG(LogTemp, Display, TEXT("[AI003][WORLD] PASS: terrain built, player grounded and running"));
	ExitWith(0);
}

void AWorldGameMode::ExitWith(int32 Code)
{
	UE_LOG(LogTemp, Display, TEXT("[AI003] exiting with code %d"), Code);
	FPlatformMisc::RequestExitWithStatus(false, static_cast<uint8>(Code));
}
