#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Character.h"
#include "WorldCharacter.generated.h"

class UCameraComponent;
class USpringArmComponent;
class UAnimSequence;

/** Third person character: walk/run, jump, gravity and terrain collision. */
UCLASS()
class UE5GAMECLIENT_API AWorldCharacter : public ACharacter
{
	GENERATED_BODY()

public:
	AWorldCharacter();

	/** Test hook: run forward without input (used by the headless world smoke). */
	void SetAutoRun(bool bEnable) { bAutoRun = bEnable; }
	bool IsAutoRunning() const { return bAutoRun; }

protected:
	virtual void BeginPlay() override;
	virtual void Tick(float DeltaSeconds) override;
	virtual void SetupPlayerInputComponent(UInputComponent* PlayerInputComponent) override;

	void MoveForward(float Value);
	void MoveRight(float Value);
	void BeginSprint();
	void EndSprint();

private:
	void LoadCharacterAssets();
	void UpdateAnimation();

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USpringArmComponent> SpringArm;

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<UCameraComponent> Camera;

	UPROPERTY(Transient)
	TObjectPtr<UAnimSequence> IdleSequence;

	UPROPERTY(Transient)
	TObjectPtr<UAnimSequence> RunSequence;

	UPROPERTY(Transient)
	TObjectPtr<UAnimSequence> JumpSequence;

	UPROPERTY(Transient)
	TObjectPtr<UAnimSequence> CurrentSequence;

	bool bSprinting = false;
	bool bAutoRun = false;

	static constexpr float WalkSpeed = 600.f;
	static constexpr float SprintSpeed = 1000.f;
};
