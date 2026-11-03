using UnrealBuildTool;

public class UE5GameClient : ModuleRules
{
	public UE5GameClient(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		PublicDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"InputCore",
			"WebSockets",
			"Json",
			"JsonUtilities",
		});
	}
}
