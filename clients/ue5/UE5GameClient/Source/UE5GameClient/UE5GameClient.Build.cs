using UnrealBuildTool;

public class UE5GameClient : ModuleRules
{
	public UE5GameClient(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		// Headers live next to the sources (World/...), so expose the module root.
		PublicIncludePaths.Add(ModuleDirectory);

		PublicDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"InputCore",
			"WebSockets",
			"Sockets",
			"Networking",
			"Json",
			"JsonUtilities",
			"ProceduralMeshComponent",
		});
	}
}
