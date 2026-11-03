// Copyright AI003. All rights reserved.

#include "Net/NetSyncSubsystem.h"

#include "Dom/JsonObject.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "GameFramework/CharacterMovementComponent.h"
#include "GameFramework/PlayerController.h"
#include "IWebSocket.h"
#include "Kismet/GameplayStatics.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "Modules/ModuleManager.h"
#include "Net/RemotePlayer.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "WebSocketsModule.h"
#include "World/WorldCharacter.h"

namespace
{
	constexpr double SendIntervalSeconds = 0.1;
	constexpr double JoinRetrySeconds = 1.0;
	constexpr double ReconnectSeconds = 3.0;
}

void UNetSyncSubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
	Super::Initialize(Collection);

	if (FParse::Param(FCommandLine::Get(), TEXT("AI003NoSync")))
	{
		bEnabled = false;
		return;
	}

	FParse::Value(FCommandLine::Get(), TEXT("AI003Url="), Endpoint);
	bRoomEnabled = FParse::Param(FCommandLine::Get(), TEXT("AI003Room"));
	bRoomBot = FParse::Param(FCommandLine::Get(), TEXT("AI003RoomBot"));

	if (!FModuleManager::Get().IsModuleLoaded(TEXT("WebSockets")))
	{
		FModuleManager::Get().LoadModule(TEXT("WebSockets"));
	}

	TickerHandle = FTSTicker::GetCoreTicker().AddTicker(
		FTickerDelegate::CreateUObject(this, &UNetSyncSubsystem::Tick), 0.05f);

	Connect();
}

void UNetSyncSubsystem::Deinitialize()
{
	if (TickerHandle.IsValid())
	{
		FTSTicker::GetCoreTicker().RemoveTicker(TickerHandle);
		TickerHandle.Reset();
	}

	if (Socket.IsValid())
	{
		Socket->OnConnected().RemoveAll(this);
		Socket->OnConnectionError().RemoveAll(this);
		Socket->OnMessage().RemoveAll(this);
		Socket->Close();
		Socket.Reset();
	}

	for (const TPair<FString, TWeakObjectPtr<ARemotePlayer>>& Pair : Remotes)
	{
		if (Pair.Value.IsValid())
		{
			Pair.Value->Destroy();
		}
	}
	Remotes.Reset();

	for (const TPair<FString, TWeakObjectPtr<ARemotePlayer>>& Pair : RoomRemotes)
	{
		if (Pair.Value.IsValid())
		{
			Pair.Value->Destroy();
		}
	}
	RoomRemotes.Reset();
	RoomLastPositions.Reset();

	Super::Deinitialize();
}

void UNetSyncSubsystem::Connect()
{
	if (!bEnabled || bConnecting)
	{
		return;
	}

	bConnecting = true;

	Socket = FWebSocketsModule::Get().CreateWebSocket(Endpoint, TEXT(""));
	if (!Socket.IsValid())
	{
		bConnecting = false;
		NextConnectAt = FPlatformTime::Seconds() + ReconnectSeconds;
		UE_LOG(LogTemp, Warning, TEXT("[AI003][SYNC] could not create websocket for %s"), *Endpoint);
		return;
	}

	Socket->OnConnected().AddUObject(this, &UNetSyncSubsystem::HandleConnected);
	Socket->OnConnectionError().AddUObject(this, &UNetSyncSubsystem::HandleConnectionError);
	Socket->OnMessage().AddUObject(this, &UNetSyncSubsystem::HandleMessage);
	Socket->Connect();
}

void UNetSyncSubsystem::HandleConnected()
{
	bConnecting = false;
	bConnected = true;
	PlayerId.Reset();
	NextJoinAt = 0.0;
	NextSendAt = 0.0;
	UE_LOG(LogTemp, Display, TEXT("[AI003][SYNC] connected to %s"), *Endpoint);
}

void UNetSyncSubsystem::HandleConnectionError(const FString& Error)
{
	bConnecting = false;
	bConnected = false;
	NextConnectAt = FPlatformTime::Seconds() + ReconnectSeconds;
	UE_LOG(LogTemp, Warning, TEXT("[AI003][SYNC] connection error: %s"), *Error);
}

void UNetSyncSubsystem::Send(const FString& Type, const TSharedPtr<FJsonObject>& Body)
{
	if (!Socket.IsValid() || !Socket->IsConnected())
	{
		return;
	}

	TSharedPtr<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("type"), Type);
	Root->SetNumberField(TEXT("seq"), ++NextSeq);
	if (Body.IsValid())
	{
		Root->SetObjectField(TEXT("body"), Body);
	}

	FString Payload;
	const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Payload);
	if (FJsonSerializer::Serialize(Root.ToSharedRef(), Writer))
	{
		Socket->Send(Payload);
	}
}

void UNetSyncSubsystem::SendJoin()
{
	TSharedPtr<FJsonObject> Body = MakeShared<FJsonObject>();
	Body->SetStringField(TEXT("client"), TEXT("ue5"));
	Body->SetStringField(TEXT("name"), TEXT("UE5Player"));
	Send(TEXT("join"), Body);
}

void UNetSyncSubsystem::SendMove()
{
	UWorld* World = GetWorld();
	if (World == nullptr)
	{
		return;
	}

	AWorldCharacter* Character = Cast<AWorldCharacter>(UGameplayStatics::GetPlayerPawn(World, 0));
	if (Character == nullptr)
	{
		return;
	}

	const FVector Location = Character->GetActorLocation();
	const UCharacterMovementComponent* Movement = Character->GetCharacterMovement();

	// Canonical protocol: feet position in metres (Unity transform.position is
	// already at the feet); UE's actor origin is the capsule centre.
	constexpr double CapsuleHalfHeightCm = 96.0;

	TSharedPtr<FJsonObject> Pos = MakeShared<FJsonObject>();
	Pos->SetNumberField(TEXT("x"), FMath::RoundToDouble(Location.X / 100.0));
	Pos->SetNumberField(TEXT("y"), FMath::RoundToDouble((Location.Z - CapsuleHalfHeightCm) / 100.0));
	Pos->SetNumberField(TEXT("z"), FMath::RoundToDouble(Location.Y / 100.0));

	TSharedPtr<FJsonObject> Body = MakeShared<FJsonObject>();
	Body->SetObjectField(TEXT("pos"), Pos);
	Body->SetNumberField(TEXT("yaw"), FMath::RoundToDouble(Character->GetActorRotation().Yaw));
	Body->SetNumberField(TEXT("speed"), FMath::RoundToDouble(Movement->Velocity.Size2D() / 100.0));
	Body->SetBoolField(TEXT("grounded"), Movement->IsMovingOnGround());
	Body->SetBoolField(TEXT("swimming"), false);
	Send(TEXT("move"), Body);
}

void UNetSyncSubsystem::HandleMessage(const FString& Message)
{
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Message);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		return;
	}

	FString Type;
	Root->TryGetStringField(TEXT("type"), Type);

	if (Type == TEXT("joined"))
	{
		const TSharedPtr<FJsonObject>* Body = nullptr;
		if (Root->TryGetObjectField(TEXT("body"), Body) && Body != nullptr)
		{
			(*Body)->TryGetStringField(TEXT("playerId"), PlayerId);
		}
		UE_LOG(LogTemp, Display, TEXT("[AI003][SYNC] joined as %s"), *PlayerId);
	}
	else if (Type == TEXT("snapshot"))
	{
		const TSharedPtr<FJsonObject>* Body = nullptr;
		if (Root->TryGetObjectField(TEXT("body"), Body) && Body != nullptr)
		{
			ApplySnapshot(*Body);
		}
	}
	else if (Type == TEXT("room.entered"))
	{
		const TSharedPtr<FJsonObject>* Body = nullptr;
		if (Root->TryGetObjectField(TEXT("body"), Body) && Body != nullptr)
		{
			EnterRoom(*Body);
		}
	}
	else if (Type == TEXT("room.frame"))
	{
		const TSharedPtr<FJsonObject>* Body = nullptr;
		if (Root->TryGetObjectField(TEXT("body"), Body) && Body != nullptr)
		{
			ApplyRoomFrame(*Body);
		}
	}
	else if (Type == TEXT("room.left"))
	{
		bRoomActive = false;
	}
	else if (Type == TEXT("error"))
	{
		UE_LOG(LogTemp, Warning, TEXT("[AI003][SYNC] gateway error: %s"), *Message);
	}
}

void UNetSyncSubsystem::ApplySnapshot(const TSharedPtr<FJsonObject>& Body)
{
	if (bRoomActive)
	{
		return; // The combat room owns the avatar and its peers.
	}

	UWorld* World = GetWorld();
	if (!Body.IsValid() || World == nullptr)
	{
		return;
	}

	const TArray<TSharedPtr<FJsonValue>>* Players = nullptr;
	if (!Body->TryGetArrayField(TEXT("players"), Players) || Players == nullptr)
	{
		return;
	}

	TSet<FString> Seen;
	for (const TSharedPtr<FJsonValue>& Entry : *Players)
	{
		const TSharedPtr<FJsonObject> Player = Entry.IsValid() ? Entry->AsObject() : nullptr;
		if (!Player.IsValid())
		{
			continue;
		}

		FString Id;
		Player->TryGetStringField(TEXT("id"), Id);
		if (Id.IsEmpty() || Id == PlayerId)
		{
			continue;
		}
		Seen.Add(Id);

		const TSharedPtr<FJsonObject>* PosObject = nullptr;
		double X = 0.0, Y = 0.0, Z = 0.0;
		if (Player->TryGetObjectField(TEXT("pos"), PosObject) && PosObject != nullptr)
		{
			(*PosObject)->TryGetNumberField(TEXT("x"), X);
			(*PosObject)->TryGetNumberField(TEXT("y"), Y);
			(*PosObject)->TryGetNumberField(TEXT("z"), Z);
		}
		double Yaw = 0.0;
		Player->TryGetNumberField(TEXT("yaw"), Yaw);
		FString Anim;
		Player->TryGetStringField(TEXT("anim"), Anim);

		const FVector Location(X * 100.0, Z * 100.0, Y * 100.0 + 96.0);
		const FRotator Rotation(0.f, Yaw, 0.f);

		TWeakObjectPtr<ARemotePlayer>& Slot = Remotes.FindOrAdd(Id);
		ARemotePlayer* Remote = Slot.Get();
		if (Remote == nullptr)
		{
			Remote = World->SpawnActor<ARemotePlayer>(Location, Rotation);
			Slot = Remote;
			UE_LOG(LogTemp, Display, TEXT("[AI003][SYNC] remote %s spawned at %s"),
				*Id, *Location.ToCompactString());
		}
		if (Remote != nullptr)
		{
			Remote->ApplySnapshot(Location, static_cast<float>(Yaw), Anim);
		}
	}

	RemoveStale(Seen);
}

void UNetSyncSubsystem::RemoveStale(const TSet<FString>& Seen)
{
	TArray<FString> Stale;
	for (const TPair<FString, TWeakObjectPtr<ARemotePlayer>>& Pair : Remotes)
	{
		if (!Seen.Contains(Pair.Key))
		{
			Stale.Add(Pair.Key);
		}
	}

	for (const FString& Id : Stale)
	{
		TWeakObjectPtr<ARemotePlayer> Remote = Remotes.FindRef(Id);
		if (Remote.IsValid())
		{
			Remote->Destroy();
		}
		Remotes.Remove(Id);
	}
}

bool UNetSyncSubsystem::Tick(float DeltaSeconds)
{
	if (!bEnabled)
	{
		return true;
	}

	const double Now = FPlatformTime::Seconds();

	if (!bConnected && !bConnecting && Now >= NextConnectAt)
	{
		Connect();
	}

	if (!bConnected)
	{
		return true;
	}

	if (Socket.IsValid() && !Socket->IsConnected())
	{
		bConnected = false;
		NextConnectAt = Now + ReconnectSeconds;
		return true;
	}

	if (PlayerId.IsEmpty())
	{
		if (Now >= NextJoinAt)
		{
			NextJoinAt = Now + JoinRetrySeconds;
			SendJoin();
		}
		return true;
	}

	if (Now >= NextSendAt)
	{
		NextSendAt = Now + SendIntervalSeconds;
		SendMove();
	}

	if (bRoomEnabled)
	{
		if (!bRoomActive && Now >= NextRoomEnterAt)
		{
			NextRoomEnterAt = Now + 1.0;
			SendRoomEnter();
		}
		else if (bRoomActive && Now >= NextRoomSendAt)
		{
			NextRoomSendAt = Now + RoomSendInterval;
			SendRoomInput();
		}

		if (bRoomActive && Now >= NextHudAt && GEngine != nullptr)
		{
			NextHudAt = Now + 0.25;
			GEngine->AddOnScreenDebugMessage(1, 0.3f, FColor::White,
				FString::Printf(TEXT("ARENA   hp %d/%d   tick %d   fighters %d   [WASD move, Space attack]"),
					RoomHP, RoomMaxHP, RoomTick, RoomFighters));
		}
	}

	return true;
}

AWorldCharacter* UNetSyncSubsystem::LocalCharacter() const
{
	UWorld* World = GetWorld();
	if (World == nullptr)
	{
		return nullptr;
	}
	return Cast<AWorldCharacter>(UGameplayStatics::GetPlayerPawn(World, 0));
}

void UNetSyncSubsystem::SendRoomEnter()
{
	TSharedPtr<FJsonObject> Body = MakeShared<FJsonObject>();
	Body->SetStringField(TEXT("name"), TEXT("UE5Player"));
	Send(TEXT("room.enter"), Body);
}

void UNetSyncSubsystem::SendRoomInput()
{
	int32 DX = 0;
	int32 DZ = 0;
	bool bAttack = false;

	if (bRoomBot)
	{
		// Headless demo: walk to the arena centre and swing periodically.
		if (FMath::Abs(RoomLocalX) > 500.0) DX = RoomLocalX > 0.0 ? -1 : 1;
		if (FMath::Abs(RoomLocalZ) > 500.0) DZ = RoomLocalZ > 0.0 ? -1 : 1;
		bAttack = (RoomTick % 40) < 8;
	}
	else if (AWorldCharacter* Character = LocalCharacter())
	{
		if (APlayerController* PC = Cast<APlayerController>(Character->GetController()))
		{
			if (PC->IsInputKeyDown(EKeys::A) || PC->IsInputKeyDown(EKeys::Left)) DX -= 1;
			if (PC->IsInputKeyDown(EKeys::D) || PC->IsInputKeyDown(EKeys::Right)) DX += 1;
			if (PC->IsInputKeyDown(EKeys::W) || PC->IsInputKeyDown(EKeys::Up)) DZ += 1;
			if (PC->IsInputKeyDown(EKeys::S) || PC->IsInputKeyDown(EKeys::Down)) DZ -= 1;
			bAttack = PC->IsInputKeyDown(EKeys::SpaceBar);
		}
	}

	TSharedPtr<FJsonObject> Body = MakeShared<FJsonObject>();
	Body->SetNumberField(TEXT("dx"), DX);
	Body->SetNumberField(TEXT("dz"), DZ);
	Body->SetNumberField(TEXT("buttons"), bAttack ? 1 : 0);
	Send(TEXT("room.input"), Body);
}

void UNetSyncSubsystem::EnterRoom(const TSharedPtr<FJsonObject>& Body)
{
	if (!Body.IsValid())
	{
		return;
	}

	Body->TryGetStringField(TEXT("playerId"), PlayerId);
	Body->TryGetStringField(TEXT("roomId"), RoomId);

	int32 TickRate = 20;
	Body->TryGetNumberField(TEXT("tickRate"), TickRate);
	RoomSendInterval = 1.0 / FMath::Max(1, TickRate);
	Body->TryGetNumberField(TEXT("maxHP"), RoomMaxHP);
	RoomHP = RoomMaxHP;
	Body->TryGetNumberField(TEXT("tick"), RoomTick);

	if (AWorldCharacter* Character = LocalCharacter())
	{
		RoomCenter = Character->GetActorLocation();
		Character->GetCharacterMovement()->DisableMovement();
	}

	// World peers are irrelevant while the arena owns the avatar.
	for (const TPair<FString, TWeakObjectPtr<ARemotePlayer>>& Pair : Remotes)
	{
		if (Pair.Value.IsValid()) Pair.Value->Destroy();
	}
	Remotes.Reset();

	bRoomActive = true;
	ApplyRoomFrame(Body);

	UE_LOG(LogTemp, Display, TEXT("[AI003][ROOM] entered %s as %s (%.0f Hz)"),
		*RoomId, *PlayerId, 1.0 / RoomSendInterval);
}

void UNetSyncSubsystem::ApplyRoomFrame(const TSharedPtr<FJsonObject>& Body)
{
	if (!Body.IsValid())
	{
		return;
	}

	Body->TryGetNumberField(TEXT("tick"), RoomTick);
	const TArray<TSharedPtr<FJsonValue>>* Players = nullptr;
	if (!Body->TryGetArrayField(TEXT("players"), Players) || Players == nullptr)
	{
		return;
	}
	RoomFighters = Players->Num();

	TSet<FString> Seen;
	for (const TSharedPtr<FJsonValue>& Entry : *Players)
	{
		const TSharedPtr<FJsonObject> Player = Entry.IsValid() ? Entry->AsObject() : nullptr;
		if (!Player.IsValid()) continue;

		FString Id;
		Player->TryGetStringField(TEXT("id"), Id);
		if (Id.IsEmpty()) continue;
		Seen.Add(Id);

		double X = 0.0, Z = 0.0;
		Player->TryGetNumberField(TEXT("x"), X);
		Player->TryGetNumberField(TEXT("z"), Z);
		int32 FacingX = 0, FacingZ = 1;
		Player->TryGetNumberField(TEXT("facingX"), FacingX);
		Player->TryGetNumberField(TEXT("facingZ"), FacingZ);
		int32 HPValue = RoomMaxHP;
		Player->TryGetNumberField(TEXT("hp"), HPValue);
		bool bAlive = true;
		Player->TryGetBoolField(TEXT("alive"), bAlive);

		const FVector Target = RoomToWorld(X / 1000.0, Z / 1000.0);
		const float Yaw = (FacingX == 0 && FacingZ == 0)
			? 0.f
			: FMath::RadiansToDegrees(FMath::Atan2(static_cast<float>(FacingX), static_cast<float>(FacingZ)));

		if (Id == PlayerId)
		{
			if (HPValue != RoomHP)
			{
				UE_LOG(LogTemp, Display, TEXT("[AI003][ROOM] hp %d/%d"), HPValue, RoomMaxHP);
			}
			RoomHP = HPValue;
			RoomYaw = Yaw;
			RoomLocalX = X;
			RoomLocalZ = Z;
			if (AWorldCharacter* Character = LocalCharacter())
			{
				Character->SetActorLocation(Target);
				Character->SetActorRotation(FRotator(0.f, Yaw, 0.f));
			}
			continue;
		}

		TWeakObjectPtr<ARemotePlayer>& Slot = RoomRemotes.FindOrAdd(Id);
		ARemotePlayer* Remote = Slot.Get();
		if (Remote == nullptr)
		{
			Remote = GetWorld() != nullptr
				? GetWorld()->SpawnActor<ARemotePlayer>(Target, FRotator(0.f, Yaw, 0.f))
				: nullptr;
			Slot = Remote;
		}

		if (Remote != nullptr)
		{
			FString Anim = TEXT("idle");
			const FVector* Previous = RoomLastPositions.Find(Id);
			if (bAlive && Previous != nullptr)
			{
				const double Speed = FVector::Dist(*Previous, Target) / 100.0 / FMath::Max(0.001, RoomSendInterval);
				Anim = Speed > 6.5 ? TEXT("run") : Speed > 0.2 ? TEXT("walk") : TEXT("idle");
			}
			RoomLastPositions.Add(Id, Target);
			Remote->ApplySnapshot(Target, Yaw, bAlive ? Anim : TEXT("idle"));
		}
	}

	TArray<FString> Stale;
	for (const TPair<FString, TWeakObjectPtr<ARemotePlayer>>& Pair : RoomRemotes)
	{
		if (!Seen.Contains(Pair.Key)) Stale.Add(Pair.Key);
	}
	for (const FString& Id : Stale)
	{
		TWeakObjectPtr<ARemotePlayer> Remote = RoomRemotes.FindRef(Id);
		if (Remote.IsValid()) Remote->Destroy();
		RoomRemotes.Remove(Id);
		RoomLastPositions.Remove(Id);
	}
}

FVector UNetSyncSubsystem::RoomToWorld(double XMeters, double ZMeters) const
{
	return RoomCenter + FVector(XMeters * 100.0, ZMeters * 100.0, 0.0);
}
