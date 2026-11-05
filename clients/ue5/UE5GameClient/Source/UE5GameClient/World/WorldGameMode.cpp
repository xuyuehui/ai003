#include "World/WorldGameMode.h"

#include "Net/NetSyncSubsystem.h"

#include "Camera/CameraActor.h"
#include "Camera/CameraComponent.h"
#include "Components/DirectionalLightComponent.h"
#include "Components/CapsuleComponent.h"
#include "Components/ExponentialHeightFogComponent.h"
#include "Components/SkyAtmosphereComponent.h"
#include "Components/SkyLightComponent.h"
#include "Engine/DirectionalLight.h"
#include "Engine/ExponentialHeightFog.h"
#include "Engine/PostProcessVolume.h"
#include "Engine/SkyLight.h"
#include "Engine/World.h"
#include "EngineUtils.h"
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
	bDemoShot = FParse::Param(FCommandLine::Get(), TEXT("AI003DemoShot"));
	bRoomShot = FParse::Param(FCommandLine::Get(), TEXT("AI003RoomShot"));
	bInputProbe = FParse::Param(FCommandLine::Get(), TEXT("AI003InputProbe"));

	if (bWorldShot)
	{
		FString Relative = FPaths::Combine(FPaths::ProjectDir(), TEXT("../../../artifacts/world-ue5.png"));
		FPaths::CollapseRelativeDirectories(Relative);
		ShotPath = FPaths::ConvertRelativePathToFull(Relative);
	}
	if (bDemoShot)
	{
		FString Relative = FPaths::Combine(FPaths::ProjectDir(), TEXT("../../../artifacts/demo/ue5.png"));
		FPaths::CollapseRelativeDirectories(Relative);
		DemoShotPath = FPaths::ConvertRelativePathToFull(Relative);
	}
	if (bRoomShot)
	{
		FString Relative = FPaths::Combine(FPaths::ProjectDir(), TEXT("../../../artifacts/demo/room-ue5.png"));
		FPaths::CollapseRelativeDirectories(Relative);
		RoomShotPath = FPaths::ConvertRelativePathToFull(Relative);
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

	if (bInputProbe)
	{
		const float Yaws[] = {0.f, 90.f, 180.f, 270.f};
		for (float Yaw : Yaws)
		{
			int32 Wx, Wz, Ax, Az, Dx, Dz, Sx, Sz;
			UNetSyncSubsystem::MapCameraRelativeInput(Yaw, 0.f, 1.f, Wx, Wz);
			UNetSyncSubsystem::MapCameraRelativeInput(Yaw, -1.f, 0.f, Ax, Az);
			UNetSyncSubsystem::MapCameraRelativeInput(Yaw, 1.f, 0.f, Dx, Dz);
			UNetSyncSubsystem::MapCameraRelativeInput(Yaw, 0.f, -1.f, Sx, Sz);
			UE_LOG(LogTemp, Display,
				TEXT("[AI003][INPUTPROBE] yaw=%.0f W=(%d,%d) A=(%d,%d) D=(%d,%d) S=(%d,%d)"),
				Yaw, Wx, Wz, Ax, Az, Dx, Dz, Sx, Sz);
		}

		GetWorldTimerManager().SetTimer(AutomationTimer,
			FTimerDelegate::CreateLambda([this]() { ExitWith(0); }), 0.5f, false);
		return;
	}

	if (bWorldSmoke || bWorldShot)
	{
		GetWorldTimerManager().SetTimer(AutomationTimer, this, &AWorldGameMode::BeginAutomation, 3.f, false);
	}
	else if (bDemoShot)
	{
		GetWorldTimerManager().SetTimer(AutomationTimer, this, &AWorldGameMode::BeginDemoShot, 3.f, false);
	}
	else if (bRoomShot)
	{
		GetWorldTimerManager().SetTimer(AutomationTimer, this, &AWorldGameMode::BeginRoomShot, 20.f, false);
	}
}

void AWorldGameMode::HandleStartingNewPlayer_Implementation(APlayerController* NewPlayer)
{
	if (NewPlayer == nullptr)
	{
		return;
	}

	// Unity spawn yaw 0 faces +Z (north); UE forward -Y is yaw 270.
	const FRotator Rotation(0.f, 270.f, 0.f);
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
		FVector::ZeroVector, FRotator(-46.f, 124.f, 0.f));
	if (Sun != nullptr)
	{
		Sun->SetMobility(EComponentMobility::Movable);
		if (UDirectionalLightComponent* LightComponent = Cast<UDirectionalLightComponent>(Sun->GetLightComponent()))
		{
			LightComponent->SetIntensity(5.f);
			LightComponent->SetLightColor(FLinearColor(1.f, 0.95f, 0.86f));
			LightComponent->SetAtmosphereSunLight(true);
			LightComponent->SetCastShadows(true);
			// Make this the forward-shading sun so the fill light does not trigger
			// the "multiple directional lights" warning.
			LightComponent->SetForwardShadingPriority(1);
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
			FillComponent->SetForwardShadingPriority(0);
		}
	}

	// Sky dome and sky light. The luminance factor lifts the sky dome only, so the
	// sky can match Unity without over-brightening the terrain.
	if (ASkyAtmosphere* Sky = World->SpawnActor<ASkyAtmosphere>())
	{
		if (USkyAtmosphereComponent* SkyComponent = Sky->GetComponent())
		{
			SkyComponent->SetSkyLuminanceFactor(FLinearColor(1.18f, 1.18f, 1.18f));
		}
	}

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

void AWorldGameMode::BeginDemoShot()
{
	UWorld* World = GetWorld();
	if (World == nullptr)
	{
		ExitWith(1);
		return;
	}

	// Match the Unity capture exactly: Unity places the avatar by its feet, while
	// UE's actor origin is the capsule centre, so lift by half the capsule height.
	const FVector Ground = WorldData.IsValid() ? WorldData->SpawnUE() * 100.f : SpawnLocation;
	const FVector FeetCenter = Ground + FVector(0.f, 0.f, 96.f);
	if (AWorldCharacter* Character = Cast<AWorldCharacter>(UGameplayStatics::GetPlayerPawn(this, 0)))
	{
		Character->SetActorLocation(FeetCenter);
		// Unity avatar faces -Z (towards the camera at -Z); UE forward = -Y is yaw 90.
		Character->SetActorRotation(FRotator(0.f, 90.f, 0.f));
	}

	// Front-light the character for the comparison: the demo camera is on +Y
	// (UE Y = -Unity z), so the sun shines from that side.
	for (TActorIterator<ADirectionalLight> It(World); It; ++It)
	{
		ADirectionalLight* Light = *It;
		if (Light != nullptr)
		{
			Light->SetActorRotation(FRotator(-46.f, 270.f, 0.f));
			break;
		}
	}

	const FVector CameraLocation = Ground + FVector(0.f, 600.f, 320.f);
	const FVector LookAt = Ground + FVector(0.f, 0.f, 120.f);
	const FRotator CameraRotation = (LookAt - CameraLocation).Rotation();

	UE_LOG(LogTemp, Display,
		TEXT("[AI003][DEMO-SHOT] ground=(%.0f,%.0f,%.0f) cam=(%.0f,%.0f,%.0f) rot=(%.2f,%.2f,%.2f) fov=%.2f"),
		Ground.X, Ground.Y, Ground.Z, CameraLocation.X, CameraLocation.Y, CameraLocation.Z,
		CameraRotation.Pitch, CameraRotation.Yaw, CameraRotation.Roll, 93.77);

	ACameraActor* Camera = World->SpawnActor<ACameraActor>(CameraLocation, CameraRotation);
	if (Camera != nullptr)
	{
		// Unity's fieldOfView is vertical; Unreal's is horizontal by default.
		// 62° vertical at 16:9 ≈ 93.77° horizontal.
		if (UCameraComponent* CameraComponent = Camera->GetCameraComponent())
		{
			CameraComponent->SetFieldOfView(93.77f);
		}
		if (APlayerController* PC = UGameplayStatics::GetPlayerController(this, 0))
		{
			PC->SetViewTarget(Camera);
		}
	}

	// Deterministic exposure via an unbound post-process volume instead of the
	// camera component, which rendered black with AEM_Manual.
	APostProcessVolume* Volume = World->SpawnActor<APostProcessVolume>();
	if (Volume != nullptr)
	{
		Volume->bUnbound = true;
		Volume->BlendWeight = 1.f;
		FPostProcessSettings& PostProcess = Volume->Settings;
		PostProcess.bOverride_AutoExposureMethod = true;
		PostProcess.AutoExposureMethod = EAutoExposureMethod::AEM_Histogram;
		PostProcess.bOverride_AutoExposureBias = true;
		PostProcess.AutoExposureBias = 0.f;
		PostProcess.bOverride_AutoExposureMinBrightness = true;
		PostProcess.AutoExposureMinBrightness = 0.8f;
		PostProcess.bOverride_AutoExposureMaxBrightness = true;
		PostProcess.AutoExposureMaxBrightness = 0.8f;
		PostProcess.bOverride_ToneCurveAmount = true;
		PostProcess.ToneCurveAmount = 1.f;
	}

	IFileManager::Get().MakeDirectory(*FPaths::GetPath(DemoShotPath), true);
	// Give the view target one frame (or a few) to take effect before capturing.
	GetWorldTimerManager().SetTimer(AutomationTimer, FTimerDelegate::CreateLambda([this]()
	{
		if (APlayerController* PC = UGameplayStatics::GetPlayerController(this, 0))
		{
			if (APlayerCameraManager* CameraManager = PC->PlayerCameraManager)
			{
				const FVector ViewLocation = CameraManager->GetCameraLocation();
				const FRotator ViewRotation = CameraManager->GetCameraRotation();
				UE_LOG(LogTemp, Display,
					TEXT("[AI003][DEMO-SHOT] view=(%.0f,%.0f,%.0f) rot=(%.2f,%.2f,%.2f) fov=%.2f"),
					ViewLocation.X, ViewLocation.Y, ViewLocation.Z,
					ViewRotation.Pitch, ViewRotation.Yaw, ViewRotation.Roll, CameraManager->GetFOVAngle());
			}
		}
		FScreenshotRequest::RequestScreenshot(DemoShotPath, false, false);
		UE_LOG(LogTemp, Display, TEXT("[AI003][DEMO-SHOT] requested %s"), *DemoShotPath);
		GetWorldTimerManager().SetTimer(
			AutomationTimer, FTimerDelegate::CreateLambda([this]() { ExitWith(0); }), 1.5f, false);
	}), 0.5f, false);
}

void AWorldGameMode::BeginRoomShot()
{
	IFileManager::Get().MakeDirectory(*FPaths::GetPath(RoomShotPath), true);
	FScreenshotRequest::RequestScreenshot(RoomShotPath, false, false);
	UE_LOG(LogTemp, Display, TEXT("[AI003][ROOM-SHOT] requested %s"), *RoomShotPath);

	GetWorldTimerManager().SetTimer(
		AutomationTimer, FTimerDelegate::CreateLambda([this]() { ExitWith(0); }), 2.f, false);
}

void AWorldGameMode::BeginAutomation()
{
	if (bWorldShot)
	{
		if (AWorldCharacter* Player = Cast<AWorldCharacter>(UGameplayStatics::GetPlayerPawn(this, 0)))
		{
			const UCharacterMovementComponent* Movement = Player->GetCharacterMovement();
			UE_LOG(LogTemp, Display,
				TEXT("[AI003][SHOT] player z=%.0f mode=%d vz=%.0f gravity=%.1f controller=%d"),
				Player->GetActorLocation().Z, static_cast<int32>(Movement->MovementMode),
				Movement->Velocity.Z, Movement->GravityScale, Player->GetController() != nullptr ? 1 : 0);
		}

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


