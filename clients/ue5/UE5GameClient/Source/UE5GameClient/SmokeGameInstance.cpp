// Copyright AI003. All rights reserved.

#include "SmokeGameInstance.h"

#include "Dom/JsonObject.h"
#include "IWebSocket.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "Modules/ModuleManager.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "WebSocketsModule.h"

namespace
{
	const TCHAR* SmokeTag = TEXT("[AI003][SMOKE]");
}

void USmokeGameInstance::Init()
{
	Super::Init();

	if (!FParse::Param(FCommandLine::Get(), TEXT("AI003Smoke")))
	{
		return;
	}

	Endpoint = TEXT("ws://127.0.0.1:8080/ws");
	FParse::Value(FCommandLine::Get(), TEXT("AI003Url="), Endpoint);
	StartSmoke();
}

void USmokeGameInstance::StartSmoke()
{
	StartedAt = FPlatformTime::Seconds();
	UE_LOG(LogTemp, Display, TEXT("%s starting, endpoint=%s"), SmokeTag, *Endpoint);

	if (!FModuleManager::Get().IsModuleLoaded(TEXT("WebSockets")))
	{
		FModuleManager::Get().LoadModule(TEXT("WebSockets"));
	}

	Socket = FWebSocketsModule::Get().CreateWebSocket(Endpoint, TEXT(""));
	if (!Socket.IsValid())
	{
		Finish(false, TEXT("could not create websocket"));
		return;
	}

	Socket->OnConnected().AddUObject(this, &USmokeGameInstance::HandleConnected);
	Socket->OnConnectionError().AddUObject(this, &USmokeGameInstance::HandleConnectionError);
	Socket->OnMessage().AddUObject(this, &USmokeGameInstance::HandleMessage);

	TickerHandle = FTSTicker::GetCoreTicker().AddTicker(
		FTickerDelegate::CreateUObject(this, &USmokeGameInstance::Tick), 0.05f);

	Socket->Connect();
}

void USmokeGameInstance::HandleConnected()
{
	UE_LOG(LogTemp, Display, TEXT("%s socket open"), SmokeTag);
	SendPing();
}

void USmokeGameInstance::HandleConnectionError(const FString& Error)
{
	Finish(false, FString::Printf(TEXT("connection error: %s"), *Error));
}

void USmokeGameInstance::HandleMessage(const FString& Message)
{
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Message);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		UE_LOG(LogTemp, Warning, TEXT("%s malformed frame: %s"), SmokeTag, *Message);
		return;
	}

	FString Type;
	Root->TryGetStringField(TEXT("type"), Type);

	int64 Seq = 0;
	Root->TryGetNumberField(TEXT("seq"), Seq);

	if (Type == TEXT("welcome"))
	{
		FString SessionId;
		const TSharedPtr<FJsonObject>* Body = nullptr;
		if (Root->TryGetObjectField(TEXT("body"), Body) && Body != nullptr)
		{
			(*Body)->TryGetStringField(TEXT("sessionId"), SessionId);
		}
		UE_LOG(LogTemp, Display, TEXT("%s welcome session=%s"), SmokeTag, *SessionId);
	}
	else if (Type == TEXT("pong"))
	{
		++Pongs;
		UE_LOG(LogTemp, Display, TEXT("%s pong seq=%lld (%d/%d)"),
			SmokeTag, static_cast<long long>(Seq), Pongs, RequiredPongs);
		if (Pongs >= RequiredPongs)
		{
			Finish(true, FString::Printf(TEXT("received %d/%d pongs from %s"), Pongs, RequiredPongs, *Endpoint));
			return;
		}
		SendPing();
	}
	else if (Type == TEXT("error"))
	{
		UE_LOG(LogTemp, Warning, TEXT("%s gateway error seq=%lld: %s"), SmokeTag, static_cast<long long>(Seq), *Message);
	}
	else
	{
		UE_LOG(LogTemp, Display, TEXT("%s message %s"), SmokeTag, *Message);
	}
}

void USmokeGameInstance::SendPing()
{
	if (!Socket.IsValid() || !Socket->IsConnected())
	{
		return;
	}

	const int32 Seq = ++NextSeq;

	TSharedPtr<FJsonObject> Body = MakeShared<FJsonObject>();
	Body->SetStringField(TEXT("client"), TEXT("ue5"));
	Body->SetNumberField(TEXT("sentAt"), FDateTime::UtcNow().ToUnixTimestamp() * 1000.0);

	TSharedPtr<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("type"), TEXT("ping"));
	Root->SetNumberField(TEXT("seq"), Seq);
	Root->SetObjectField(TEXT("body"), Body);

	FString Payload;
	const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Payload);
	if (!FJsonSerializer::Serialize(Root.ToSharedRef(), Writer))
	{
		Finish(false, TEXT("failed to serialize ping"));
		return;
	}

	Socket->Send(Payload);
}

bool USmokeGameInstance::Tick(float DeltaSeconds)
{
	if (bFinished)
	{
		return false;
	}

	const double Elapsed = FPlatformTime::Seconds() - StartedAt;
	if (Elapsed > TimeoutSeconds)
	{
		Finish(false, FString::Printf(TEXT("timeout after %.1fs, pongs=%d/%d"), Elapsed, Pongs, RequiredPongs));
		return false;
	}
	return true;
}

void USmokeGameInstance::Finish(bool bSuccess, const FString& Reason)
{
	if (bFinished)
	{
		return;
	}
	bFinished = true;

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

	UE_LOG(LogTemp, Display, TEXT("%s %s: %s"), SmokeTag, bSuccess ? TEXT("PASS") : TEXT("FAIL"), *Reason);
	FPlatformMisc::RequestExitWithStatus(false, bSuccess ? 0 : 1);
}

void USmokeGameInstance::Shutdown()
{
	if (!bFinished)
	{
		bFinished = true;
		if (TickerHandle.IsValid())
		{
			FTSTicker::GetCoreTicker().RemoveTicker(TickerHandle);
			TickerHandle.Reset();
		}
		if (Socket.IsValid())
		{
			Socket->Close();
			Socket.Reset();
		}
	}
	Super::Shutdown();
}
