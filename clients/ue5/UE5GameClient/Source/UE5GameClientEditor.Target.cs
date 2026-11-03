using UnrealBuildTool;
using System.Collections.Generic;

public class UE5GameClientEditorTarget : TargetRules
{
	public UE5GameClientEditorTarget(TargetInfo Target) : base(Target)
	{
		Type = TargetType.Editor;
		DefaultBuildSettings = BuildSettingsVersion.V7;
		IncludeOrderVersion = EngineIncludeOrderVersion.Unreal5_8;
		ExtraModuleNames.Add("UE5GameClient");
	}
}
