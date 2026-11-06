// Copyright AI003. All rights reserved.

#include "Net/NetSyncSubsystem.h"
#include "Net/RudpTransport.h"

#include "Dom/JsonObject.h"
#include "DrawDebugHelpers.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "GameFramework/CharacterMovementComponent.h"
#include "GameFramework/PlayerController.h"
#include "IWebSocket.h"
#include "Kismet/GameplayStatics.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
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
	bRoomPusher = FParse::Param(FCommandLine::Get(), TEXT("AI003RoomPusher"));
	bRoomArena = FParse::Param(FCommandLine::Get(), TEXT("AI003Arena"));
	UE_LOG(LogTemp, Display, TEXT("[AI003][ARENA] flags room=%d arena=%d"), bRoomEnabled ? 1 : 0, bRoomArena ? 1 : 0);
	ClientID = LoadOrCreateClientId();

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
	bRoomActive = false;
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
	Body->SetStringField(TEXT("clientId"), ClientID);
	if (bRoomArena)
	{
		Body->SetStringField(TEXT("sync"), TEXT("arena"));
	}
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
	// UE Y = -Unity z.
	Pos->SetNumberField(TEXT("z"), FMath::RoundToDouble(-Location.Y / 100.0));

	TSharedPtr<FJsonObject> Body = MakeShared<FJsonObject>();
	Body->SetObjectField(TEXT("pos"), Pos);
	// UE yaw = Unity yaw - 90 (both are left-handed, yaw grows clockwise).
	Body->SetNumberField(TEXT("yaw"), FMath::RoundToDouble(Character->GetActorRotation().Yaw + 90.0));
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
	else if (Type == TEXT("room.arena.snap") || Type == TEXT("room.arena.delta"))
	{
		const TSharedPtr<FJsonObject>* Body = nullptr;
		if (Root->TryGetObjectField(TEXT("body"), Body) && Body != nullptr)
		{
			ApplyArenaSnapshot(*Body);
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

		const FVector Location(X * 100.0, -Z * 100.0, Y * 100.0 + 96.0);
		const FRotator Rotation(0.f, Yaw - 90.0, 0.f);

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
			Remote->ApplySnapshot(Location, static_cast<float>(Yaw - 90.0), Anim);
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
	if (Udp != nullptr)
	{
		TArray<FString> UdpMessages;
		Udp->Tick(DeltaSeconds, UdpMessages);
		for (const FString& UdpMessage : UdpMessages)
		{
			HandleMessage(UdpMessage);
		}
	}
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
		bRoomActive = false;
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
		else if (bRoomActive)
		{
			// Frame-sync rooms predict a few ticks ahead; arena rooms follow snapshots.
			if (!bRoomArena)
			{
				const int32 PredictionAhead = 3;
				const int32 Desired = ServerTick + PredictionAhead;
				int32 Guard = 0;
				while (PredictedTick < Desired && Guard++ < 8)
				{
					PredictTick();
				}
			}

			if (bRoomArena)
			{
				if (Now >= NextRoomSendAt)
				{
					NextRoomSendAt = Now + RoomSendInterval;
					SendArenaCmd(ReadRoomCommand());
				}
			}
			else if (Now >= NextRoomSendAt)
			{
				NextRoomSendAt = Now + RoomSendInterval;
				PredictTick();
			}
		}

		if (bRoomActive && GEngine != nullptr && GetWorld() != nullptr)
		{
			DrawDebugBox(GetWorld(), RoomCenter + FVector(0, 0, 35), FVector(2000, 2000, 40),
				FColor(255, 200, 0), false, 0.3f, 0, 18.f);
		}

		if (bRoomActive && Now >= NextHudAt && GEngine != nullptr)
		{
			NextHudAt = Now + 0.25;
			GEngine->AddOnScreenDebugMessage(1, 0.3f, FColor::White,
				FString::Printf(TEXT("%s   hp %d/%d   tick %d / pred %d   fighters %d   corr %d   [WASD camera-relative, Space attack]"),
					*RoomId, RoomHP, RoomMaxHP, RoomTick, PredictedTick, RoomFighters, Corrections));
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
	Body->SetStringField(TEXT("clientId"), ClientID);
	if (bRoomArena)
	{
		Body->SetStringField(TEXT("sync"), TEXT("arena"));
	}

	FString RoomName;
	if (FParse::Value(FCommandLine::Get(), TEXT("AI003RoomName="), RoomName) && !RoomName.IsEmpty())
	{
		Body->SetStringField(TEXT("room"), RoomName);
	}

	UE_LOG(LogTemp, Display, TEXT("[AI003][ARENA] sending room.enter sync=%s"), bRoomArena ? TEXT("arena") : TEXT("frame"));
	Send(TEXT("room.enter"), Body);
}

void UNetSyncSubsystem::StepArenaLocal(const FRoomCommand& Command)
{
	const double Step = (Command.DX != 0 && Command.DZ != 0) ? 0.177 : 0.25;
	RoomLocalX += Command.DX * Step;
	RoomLocalZ += Command.DZ * Step;
	if (AWorldCharacter* Character = LocalCharacter())
	{
		FVector Location = RoomToWorld(RoomLocalX, RoomLocalZ);
		Location.Z = Character->GetActorLocation().Z;
		Character->SetActorLocation(Location);
	}
}

void UNetSyncSubsystem::SendRoomEnvelope(const FString& Type, const TSharedPtr<FJsonObject>& Body)
{
	if (bUseUdp && Udp != nullptr)
	{
		TSharedPtr<FJsonObject> Root = MakeShared<FJsonObject>();
		Root->SetStringField(TEXT("type"), Type);
		Root->SetObjectField(TEXT("body"), Body);
		FString Out;
		TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Out);
		FJsonSerializer::Serialize(Root.ToSharedRef(), Writer);
		Udp->Send(Out);
		return;
	}
	Send(Type, Body);
}
void UNetSyncSubsystem::SendArenaCmd(const FRoomCommand& Command)
{
	auto Body = MakeShared<FJsonObject>();
	ArenaSeq++;
	ArenaPending.Add(ArenaSeq, Command);
	StepArenaLocal(Command);
	Body->SetNumberField(TEXT("seq"), ArenaSeq);
	Body->SetNumberField(TEXT("msec"), 50);
	Body->SetNumberField(TEXT("yaw"), 0);
	Body->SetNumberField(TEXT("pitch"), 0);
	Body->SetNumberField(TEXT("forward"), Command.DZ);
	Body->SetNumberField(TEXT("side"), Command.DX);
	Body->SetNumberField(TEXT("up"), 0);
	Body->SetNumberField(TEXT("buttons"), Command.bAttack ? 1 : 0);
	SendRoomEnvelope(TEXT("room.arena.cmd"), Body);
}

void UNetSyncSubsystem::ApplyArenaSnapshot(const TSharedPtr<FJsonObject>& Body)
{
	double Seq = 0.0;
	double AckSeqValue = 0.0;
	Body->TryGetNumberField(TEXT("seq"), Seq);
	Body->TryGetNumberField(TEXT("ack"), AckSeqValue);

	const TArray<TSharedPtr<FJsonValue>>* Players = nullptr;
	if (Body->TryGetArrayField(TEXT("players"), Players) && Players != nullptr)
	{
		for (const TSharedPtr<FJsonValue>& Value : *Players)
		{
			const TSharedPtr<FJsonObject> Entry = Value->AsObject();
			if (!Entry.IsValid())
			{
				continue;
			}
			int32 Id = 0;
			Entry->TryGetNumberField(TEXT("id"), Id);
			const TArray<TSharedPtr<FJsonValue>>* Origin = nullptr;
			if (Entry->TryGetArrayField(TEXT("origin"), Origin) && Origin != nullptr && Origin->Num() == 3)
			{
				const double X = (*Origin)[0]->AsNumber() / 1000.0;
				const double Z = (*Origin)[2]->AsNumber() / 1000.0;
				ArenaOrigins.Add(Id, FVector(X, Z, 0.0));
				if (Id != ArenaSlot + 1)
				{
					const FString Key = FString::Printf(TEXT("arena-%d"), Id);
					TWeakObjectPtr<ARemotePlayer>& Slot = RoomRemotes.FindOrAdd(Key);
					ARemotePlayer* Remote = Slot.Get();
					const FVector RemoteTarget = RoomToWorld(X, Z);
					if (Remote == nullptr && GetWorld() != nullptr)
					{
						Remote = GetWorld()->SpawnActor<ARemotePlayer>(RemoteTarget, FRotator::ZeroRotator);
						Slot = Remote;
					}
					if (Remote != nullptr)
					{
						Remote->ApplySnapshot(RemoteTarget, 0.f, TEXT("idle"));
					}
				}
				if (Id == ArenaSlot + 1)
				{
					// Rollback to the authoritative position, replay unacked cmds.
					RoomLocalX = X;
					RoomLocalZ = Z;
					const int32 AckSeq = static_cast<int32>(AckSeqValue);
					TArray<int32> Stale;
					for (const TPair<int32, FRoomCommand>& Pending : ArenaPending)
					{
						if (Pending.Key <= AckSeq)
						{
							Stale.Add(Pending.Key);
						}
					}
					for (int32 Key : Stale)
					{
						ArenaPending.Remove(Key);
					}
					for (const TPair<int32, FRoomCommand>& Pending : ArenaPending)
					{
						StepArenaLocal(Pending.Value);
					}
				}
			}
		}
	}

	if (Seq <= 2.0)
	{
		UE_LOG(LogTemp, Display, TEXT("[AI003][ARENA] first snapshot seq=%.0f players=%d local=(%.2f, %.2f)"),
			Seq, ArenaOrigins.Num(), RoomLocalX, RoomLocalZ);
	}

	TSharedPtr<FJsonObject> Ack = MakeShared<FJsonObject>();
	Ack->SetNumberField(TEXT("seq"), Seq);
	SendRoomEnvelope(TEXT("room.arena.ack"), Ack);
}
void UNetSyncSubsystem::SendRoomInput(int32 Tick, const FRoomCommand& Command)
{
	TSharedPtr<FJsonObject> Body = MakeShared<FJsonObject>();
	Body->SetNumberField(TEXT("tick"), Tick);
	Body->SetNumberField(TEXT("dx"), Command.DX);
	Body->SetNumberField(TEXT("dz"), Command.DZ);
	Body->SetNumberField(TEXT("buttons"), Command.bAttack ? 1 : 0);
	if (bUseUdp) { static int DebugInputs = 0; if (DebugInputs < 3) { DebugInputs++; UE_LOG(LogTemp, Display, TEXT("[AI003][ROOM] input over udp")); } }
	SendRoomEnvelope(TEXT("room.input"), Body);
}

UNetSyncSubsystem::FRoomCommand UNetSyncSubsystem::ReadRoomCommand() const
{
	FRoomCommand Command;

	if (bRoomBot)
	{
		// Headless demo: walk towards the nearest fighter, stop at ~2.5 m and swing.
		float NearestDist = TNumericLimits<float>::Max();
		FVector NearestPos = RoomCenter;
		if (RoomRemotes.Num() == 0)
		{
			NearestPos = PredictedPos + FVector(1000.f, 0.f, 0.f);
		}
		for (const TPair<FString, TWeakObjectPtr<ARemotePlayer>>& Pair : RoomRemotes)
		{
			if (ARemotePlayer* Remote = Pair.Value.Get())
			{
				const float Dist = FVector::Dist2D(Remote->GetActorLocation(), PredictedPos);
				if (Dist < NearestDist)
				{
					NearestDist = Dist;
					NearestPos = Remote->GetActorLocation();
				}
			}
		}

		// Pusher mode walks into the other fighter on purpose (separation test).
		if (NearestDist > (bRoomPusher ? 0.f : 180.f))
		{
			const float DeltaX = NearestPos.X - PredictedPos.X;
			const float DeltaY = NearestPos.Y - PredictedPos.Y;
			Command.DX = DeltaX > 0.f ? 1 : (DeltaX < 0.f ? -1 : 0);
			// Protocol z = -UE Y.
			Command.DZ = DeltaY > 0.f ? -1 : (DeltaY < 0.f ? 1 : 0);
		}
		// Attack whenever in range; the server cooldown gates the swing.
		Command.bAttack = NearestDist <= 300.f;
		return Command;
	}

	if (AWorldCharacter* Character = LocalCharacter())
	{
		if (APlayerController* PC = Cast<APlayerController>(Character->GetController()))
		{
			float InputX = 0.f;
			float InputY = 0.f;
			if (PC->IsInputKeyDown(EKeys::A) || PC->IsInputKeyDown(EKeys::Left)) InputX -= 1.f;
			if (PC->IsInputKeyDown(EKeys::D) || PC->IsInputKeyDown(EKeys::Right)) InputX += 1.f;
			if (PC->IsInputKeyDown(EKeys::W) || PC->IsInputKeyDown(EKeys::Up)) InputY += 1.f;
			if (PC->IsInputKeyDown(EKeys::S) || PC->IsInputKeyDown(EKeys::Down)) InputY -= 1.f;

			if (!FMath::IsNearlyZero(InputX) || !FMath::IsNearlyZero(InputY))
			{
				const FRotator CameraRotation = PC->PlayerCameraManager != nullptr
					? PC->PlayerCameraManager->GetCameraRotation()
					: PC->GetControlRotation();
				MapCameraRelativeInput(CameraRotation.Yaw, InputX, InputY, Command.DX, Command.DZ);
			}
			Command.bAttack = PC->IsInputKeyDown(EKeys::SpaceBar);
		}
	}
	return Command;
}

void UNetSyncSubsystem::MapCameraRelativeInput(float CameraYawDegrees, float InputX, float InputY,
	int32& OutDX, int32& OutDZ)
{
	OutDX = 0;
	OutDZ = 0;

	// Camera-relative input, matching the Unity client: W always moves where the
	// camera looks. Snapped to the eight protocol directions.
	const float YawRadians = FMath::DegreesToRadians(CameraYawDegrees);
	const FVector2D Forward(FMath::Cos(YawRadians), FMath::Sin(YawRadians));
	const FVector2D Right(-FMath::Sin(YawRadians), FMath::Cos(YawRadians));
	const FVector2D Wish = Forward * InputY + Right * InputX;
	if (Wish.IsNearlyZero()) return;

	constexpr float SnapStep = PI / 4.f;
	const float Angle = FMath::Atan2(Wish.Y, Wish.X);
	int32 Index = FMath::RoundToInt(Angle / SnapStep);
	Index = ((Index % 8) + 8) % 8;

	// Lookup keeps the cardinal axes exact; sign(cos(90°)) would be -1 due to
	// floating point noise.
	static const int32 DirectionX[8] = {1, 1, 0, -1, -1, -1, 0, 1};
	static const int32 DirectionY[8] = {0, 1, 1, 1, 0, -1, -1, -1};
	OutDX = DirectionX[Index];
	// Protocol z = -UE Y.
	OutDZ = -DirectionY[Index];
}

void UNetSyncSubsystem::EnterRoom(const TSharedPtr<FJsonObject>& Body)
{
	if (!Body.IsValid())
	{
		return;
	}

	Body->TryGetStringField(TEXT("playerId"), PlayerId);
	Body->TryGetStringField(TEXT("roomId"), RoomId);
	const TSharedPtr<FJsonObject>* UdpInfo = nullptr;
	if (Body->TryGetObjectField(TEXT("udp"), UdpInfo) && UdpInfo != nullptr)
	{
		FString UdpAddr, UdpToken;
		(*UdpInfo)->TryGetStringField(TEXT("addr"), UdpAddr);
		(*UdpInfo)->TryGetStringField(TEXT("token"), UdpToken);
		if (!UdpAddr.IsEmpty() && !UdpToken.IsEmpty())
		{
			Udp = new FRudpTransport();
			bUseUdp = Udp->Connect(UdpAddr, UdpToken);
			UE_LOG(LogTemp, Display, TEXT("[AI003][RUDP] connect %s ok=%d"), *UdpAddr, bUseUdp ? 1 : 0);
		}
	}
	FString SyncMode;
	if (Body->TryGetStringField(TEXT("sync"), SyncMode) && SyncMode == TEXT("arena"))
	{
		bRoomArena = true;
	}

	int32 TickRate = 20;
	Body->TryGetNumberField(TEXT("tickRate"), TickRate);
	RoomSendInterval = 1.0 / FMath::Max(1, TickRate);
	Body->TryGetNumberField(TEXT("maxHP"), RoomMaxHP);
	RoomHP = RoomMaxHP;
	const TSharedPtr<FJsonObject>* State = nullptr;
	if (Body->TryGetObjectField(TEXT("state"), State) && State != nullptr)
	{
		(*State)->TryGetNumberField(TEXT("hp"), RoomHP);
		(*State)->TryGetNumberField(TEXT("slot"), ArenaSlot);
	}
	Body->TryGetNumberField(TEXT("tick"), RoomTick);

	double SpeedMM = 250.0, DiagMM = 177.0;
	Body->TryGetNumberField(TEXT("speedMMPerTick"), SpeedMM);
	Body->TryGetNumberField(TEXT("diagonalMMPerTick"), DiagMM);
	RoomSpeedCm = static_cast<float>(SpeedMM / 10.0);
	RoomDiagCm = static_cast<float>(DiagMM / 10.0);

	if (State != nullptr)
	{
		double X = 0.0, Z = 0.0;
		(*State)->TryGetNumberField(TEXT("x"), X);
		(*State)->TryGetNumberField(TEXT("z"), Z);
		PredictedPos = RoomToWorld(X / 1000.0, Z / 1000.0);
	}
	PredictedTick = RoomTick;
	ServerTick = RoomTick;
	Corrections = 0;
	LastErrorCm = 0.f;
	PendingCommands.Reset();
	PredictedHistory.Reset();
	NextRoomSendAt = FPlatformTime::Seconds();

	if (AWorldCharacter* Character = LocalCharacter())
	{
		if (!bRoomActive)
		{
			RoomCenter = Character->GetActorLocation();
		}
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
	{
		static int DebugFrames = 0;
		if (DebugFrames < 3)
		{
			DebugFrames++;
			UE_LOG(LogTemp, Display, TEXT("[AI003][ROOM] frame #%d over udp=%d"), DebugFrames, bUseUdp ? 1 : 0);
		}
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
		const float ProtocolYaw = (FacingX == 0 && FacingZ == 0)
			? 0.f
			: FMath::RadiansToDegrees(FMath::Atan2(static_cast<float>(FacingX), static_cast<float>(FacingZ)));
		const float Yaw = ProtocolYaw - 90.f;

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
			Reconcile(Target, RoomTick);
			ApplyPrediction();
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

	HandleRoomEvents(Body);
}

void UNetSyncSubsystem::HandleRoomEvents(const TSharedPtr<FJsonObject>& Body)
{
	const TArray<TSharedPtr<FJsonValue>>* Events = nullptr;
	if (!Body.IsValid() || !Body->TryGetArrayField(TEXT("events"), Events) || Events == nullptr)
	{
		return;
	}

	for (const TSharedPtr<FJsonValue>& Entry : *Events)
	{
		const TSharedPtr<FJsonObject> Event = Entry.IsValid() ? Entry->AsObject() : nullptr;
		if (!Event.IsValid()) continue;

		FString Type, Source, Target;
		Event->TryGetStringField(TEXT("type"), Type);
		Event->TryGetStringField(TEXT("source"), Source);
		Event->TryGetStringField(TEXT("target"), Target);
		int32 Damage = 0;
		Event->TryGetNumberField(TEXT("damage"), Damage);

		ARemotePlayer* SourceAvatar = RoomRemotes.FindRef(Source).Get();
		ARemotePlayer* TargetAvatar = RoomRemotes.FindRef(Target).Get();

		if (Type == TEXT("attack"))
		{
			if (SourceAvatar != nullptr) SourceAvatar->TriggerAttack();
		}
		else if (Type == TEXT("hit"))
		{
			if (TargetAvatar != nullptr)
			{
				TargetAvatar->TriggerHit();
				DrawDebugString(GetWorld(), TargetAvatar->GetActorLocation() + FVector(0, 0, 220),
					FString::Printf(TEXT("-%d"), Damage), nullptr, FColor::Yellow, 1.f);
				DrawDebugSphere(GetWorld(), TargetAvatar->GetActorLocation() + FVector(0, 0, 110), 28.f, 8,
					FColor::Red, false, 0.2f);
			}
			else if (Target == PlayerId)
			{
				if (AWorldCharacter* Character = LocalCharacter())
				{
					DrawDebugString(GetWorld(), Character->GetActorLocation() + FVector(0, 0, 220),
						FString::Printf(TEXT("-%d"), Damage), nullptr, FColor::Yellow, 1.f);
					DrawDebugSphere(GetWorld(), Character->GetActorLocation() + FVector(0, 0, 110), 28.f, 8,
						FColor::Red, false, 0.2f);
				}
			}
		}
		else if (Type == TEXT("death"))
		{
			if (TargetAvatar != nullptr) TargetAvatar->SetAlive(false);
		}
		else if (Type == TEXT("respawn"))
		{
			if (SourceAvatar != nullptr) SourceAvatar->SetAlive(true);
		}
	}
}

FVector UNetSyncSubsystem::RoomToWorld(double XMeters, double ZMeters) const
{
	return RoomCenter + FVector(XMeters * 100.0, -ZMeters * 100.0, 0.0);
}

FString UNetSyncSubsystem::LoadOrCreateClientId()
{
	const FString Path = FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("ClientId.txt"));

	FString Value;
	if (FFileHelper::LoadFileToString(Value, *Path))
	{
		Value.TrimStartAndEndInline();
		if (!Value.IsEmpty())
		{
			return Value;
		}
	}

	Value = FGuid::NewGuid().ToString(EGuidFormats::Digits).ToLower();
	FFileHelper::SaveStringToFile(Value, *Path);
	UE_LOG(LogTemp, Display, TEXT("[AI003][SYNC] generated clientId %s"), *Value);
	return Value;
}

void UNetSyncSubsystem::PredictTick()
{
	if (!bRoomActive)
	{
		return;
	}

	const FRoomCommand Command = ReadRoomCommand();
	const int32 Tick = ++PredictedTick;
	PendingCommands.Add(Tick, Command);
	StepPrediction(Command);
	PredictedHistory.Add(Tick, PredictedPos);
	SendRoomInput(Tick, Command);
	ApplyPrediction();
}

void UNetSyncSubsystem::ApplyPrediction()
{
	if (AWorldCharacter* Character = LocalCharacter())
	{
		Character->SetActorLocation(PredictedPos);
		Character->SetActorRotation(FRotator(0.f, RoomYaw, 0.f));
	}
}

void UNetSyncSubsystem::StepPrediction(const FRoomCommand& Command)
{
	if (Command.DX == 0 && Command.DZ == 0)
	{
		return;
	}

	const float Step = (Command.DX != 0 && Command.DZ != 0) ? RoomDiagCm : RoomSpeedCm;
	// Protocol x maps to UE X, protocol z maps to UE Y with a sign flip.
	PredictedPos.X += Command.DX * Step;
	PredictedPos.Y -= Command.DZ * Step;

	const float Half = 2000.f;
	PredictedPos.X = FMath::Clamp(PredictedPos.X, RoomCenter.X - Half, RoomCenter.X + Half);
	PredictedPos.Y = FMath::Clamp(PredictedPos.Y, RoomCenter.Y - Half, RoomCenter.Y + Half);

	// UE actor yaw for a protocol direction (DX, DZ): forward = (DX, -DZ).
	RoomYaw = FMath::RadiansToDegrees(FMath::Atan2(static_cast<float>(-Command.DZ), static_cast<float>(Command.DX)));
}

void UNetSyncSubsystem::Reconcile(const FVector& ServerPosition, int32 InServerTick)
{
	ServerTick = InServerTick;
	bool bReplay = false;

	if (const FVector* Predicted = PredictedHistory.Find(InServerTick))
	{
		LastErrorCm = FVector::Dist(*Predicted, ServerPosition);
		if (LastErrorCm > 5.f)
		{
			PredictedPos = ServerPosition;
			++Corrections;
			bReplay = true;
		}
	}
	else
	{
		// Joined mid-stream or lost a frame: snap to the server state.
		PredictedPos = ServerPosition;
		bReplay = true;
	}

	if (bReplay)
	{
		for (int32 Tick = InServerTick + 1; Tick <= PredictedTick; ++Tick)
		{
			const FRoomCommand* Command = PendingCommands.Find(Tick);
			if (Command == nullptr)
			{
				break;
			}
			StepPrediction(*Command);
			PredictedHistory.Add(Tick, PredictedPos);
		}
	}

	PrunePrediction(InServerTick);
}

void UNetSyncSubsystem::PrunePrediction(int32 InServerTick)
{
	for (auto It = PredictedHistory.CreateIterator(); It; ++It)
	{
		if (It.Key() <= InServerTick)
		{
			It.RemoveCurrent();
		}
	}
	for (auto It = PendingCommands.CreateIterator(); It; ++It)
	{
		if (It.Key() <= InServerTick)
		{
			It.RemoveCurrent();
		}
	}
}
