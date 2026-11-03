using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace AI003.EditorTools
{
    /// <summary>
    /// One-off setup for the Kenney protagonists character:
    /// loop flags, URP material and the Idle/Run/Jump animator controller.
    /// Assets live under Resources so WorldBootstrap can load them at runtime.
    /// </summary>
    public static class SetupPlayerCharacter
    {
        private const string Root = "Assets/Resources/Characters/KenneyProtagonists/";
        private const string ModelPath = Root + "characterMedium.fbx";
        private const string IdlePath = Root + "idle.fbx";
        private const string RunPath = Root + "run.fbx";
        private const string JumpPath = Root + "jump.fbx";
        private const string TexturePath = Root + "skaterMaleA.png";
        private const string MaterialPath = Root + "M_Player.mat";
        private const string ControllerPath = Root + "PlayerAnimator.controller";

        public static void Run()
        {
            ConfigureModel();
            MakeLoops();
            CreateMaterial();
            CreateController();
            CreatePrefab();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[AI003] player character setup complete (material + animator + prefab)");
        }

        /// <summary>
        /// Generic rigs need an Avatar on the Animator or bone curves never bind
        /// (the character stays in T-pose while the state machine advances).
        /// </summary>
        private static void ConfigureModel()
        {
            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (importer == null) return;

            bool changed = false;
            if (importer.animationType != ModelImporterAnimationType.Generic)
            {
                importer.animationType = ModelImporterAnimationType.Generic;
                changed = true;
            }
            if (importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
            {
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                changed = true;
            }
            if (changed)
            {
                importer.SaveAndReimport();
            }
        }

        private static void MakeLoops()
        {
            foreach (var path in new[] { IdlePath, RunPath, JumpPath })
            {
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null) continue;

                var clips = importer.clipAnimations;
                if (clips == null || clips.Length == 0)
                {
                    clips = importer.defaultClipAnimations;
                }

                bool changed = false;
                foreach (var clip in clips)
                {
                    bool shouldLoop = path != JumpPath;
                    if (clip.loopTime != shouldLoop)
                    {
                        clip.loopTime = shouldLoop;
                        changed = true;
                    }
                }

                if (changed)
                {
                    importer.clipAnimations = clips;
                    importer.SaveAndReimport();
                }
            }
        }

        private static void CreateMaterial()
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, MaterialPath);
            }

            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", 0.2f);
            material.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(material);
        }

        private static void CreateController()
        {
            AssetDatabase.DeleteAsset(ControllerPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Grounded", AnimatorControllerParameterType.Bool);

            var sourceClips = LoadClips(IdlePath, RunPath, JumpPath);
            var clips = new Dictionary<string, AnimationClip>
            {
                ["Idle"] = SanitizeClip(sourceClips["Idle"], Root + "Idle.anim", true),
                ["Run"] = SanitizeClip(sourceClips["Run"], Root + "Run.anim", true),
                ["Jump"] = SanitizeClip(sourceClips["Jump"], Root + "Jump.anim", false),
            };

            var stateMachine = controller.layers[0].stateMachine;

            var idle = stateMachine.AddState("Idle");
            idle.motion = clips["Idle"];
            var run = stateMachine.AddState("Run");
            run.motion = clips["Run"];
            var jump = stateMachine.AddState("Jump");
            jump.motion = clips["Jump"];
            stateMachine.defaultState = idle;

            AddTransition(idle, run, 0.12f, ("Speed", AnimatorConditionMode.Greater, 0.12f), ("Grounded", AnimatorConditionMode.If, 0f));
            AddTransition(run, idle, 0.15f, ("Speed", AnimatorConditionMode.Less, 0.12f), ("Grounded", AnimatorConditionMode.If, 0f));
            AddTransition(idle, jump, 0.05f, ("Grounded", AnimatorConditionMode.IfNot, 0f));
            AddTransition(run, jump, 0.05f, ("Grounded", AnimatorConditionMode.IfNot, 0f));
            AddTransition(jump, idle, 0.1f, ("Grounded", AnimatorConditionMode.If, 0f), ("Speed", AnimatorConditionMode.Less, 0.12f));
            AddTransition(jump, run, 0.1f, ("Grounded", AnimatorConditionMode.If, 0f), ("Speed", AnimatorConditionMode.Greater, 0.12f));

            Debug.Log($"[AI003] animator: idle={clips["Idle"].length:0.00}s run={clips["Run"].length:0.00}s jump={clips["Jump"].length:0.00}s");
        }

        /// <summary>
        /// Bakes a Resources prefab with the FBX avatar bound. A Generic Animator
        /// cannot bind bone curves without an Avatar, which would leave a T-pose.
        /// </summary>
        private static void CreatePrefab()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            var avatar = FindAvatar(ModelPath);
            if (model == null || controller == null || avatar == null)
            {
                Debug.LogError("[AI003] cannot build player prefab (model/controller/avatar missing)");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            instance.name = "PlayerCharacter";
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            instance.transform.localScale = Vector3.one * 0.45f;

            var animator = instance.GetComponent<Animator>();
            if (animator == null) animator = instance.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;

            foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
            {
                renderer.sharedMaterial = material;
            }

            PrefabUtility.SaveAsPrefabAsset(instance, Root + "Player.prefab");
            Object.DestroyImmediate(instance);
            Debug.Log($"[AI003] player prefab built with avatar '{avatar.name}'");
        }

        private static Avatar FindAvatar(string modelPath)
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(modelPath))
            {
                if (asset is Avatar avatar) return avatar;
            }
            return null;
        }

        /// <summary>
        /// Copies a clip without its root scale tracks. The Kenney FBX bakes a 100x
        /// root scale into every take, which would override the visual's 0.45 scale.
        /// </summary>
        private static AnimationClip SanitizeClip(AnimationClip source, string assetPath, bool loop)
        {
            if (source == null)
            {
                Debug.LogError($"[AI003] missing source clip for {assetPath}");
                return null;
            }

            AssetDatabase.DeleteAsset(assetPath);
            var clip = new AnimationClip { name = System.IO.Path.GetFileNameWithoutExtension(assetPath) };
            clip.frameRate = source.frameRate;

            foreach (var binding in AnimationUtility.GetCurveBindings(source))
            {
                // The Kenney takes animate the root transform itself (including a 100x
                // scale and the Z-up orientation). Drop those so the visual's own
                // rotation/scale survive; bone curves stay untouched.
                if (binding.path.Length == 0 &&
                    (binding.propertyName.StartsWith("m_LocalScale") ||
                     binding.propertyName.StartsWith("m_LocalRotation") ||
                     binding.propertyName.StartsWith("m_LocalPosition")))
                {
                    continue;
                }

                var curve = AnimationUtility.GetEditorCurve(source, binding);
                if (curve != null)
                {
                    // The takes are authored relative to the armature root; the
                    // imported hierarchy keeps an extra "Root" object, so re-path.
                    var target = binding;
                    if (!string.IsNullOrEmpty(binding.path) && !binding.path.StartsWith("Root/"))
                    {
                        target.path = "Root/" + binding.path;
                    }
                    AnimationUtility.SetEditorCurve(clip, target, curve);
                }
            }

            var settings = AnimationUtility.GetAnimationClipSettings(source);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            AssetDatabase.CreateAsset(clip, assetPath);
            return clip;
        }

        private static Dictionary<string, AnimationClip> LoadClips(string idlePath, string runPath, string jumpPath)
        {
            return new Dictionary<string, AnimationClip>
            {
                ["Idle"] = FindClip(idlePath, "Idle"),
                ["Run"] = FindClip(runPath, "Run"),
                ["Jump"] = FindClip(jumpPath, "Jump"),
            };
        }

        private static AnimationClip FindClip(string path, string suffix)
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var clip = asset as AnimationClip;
                if (clip != null && !clip.name.StartsWith("__preview__") && clip.name.EndsWith(suffix))
                {
                    return clip;
                }
            }
            return null;
        }

        private static void AddTransition(
            AnimatorState from,
            AnimatorState to,
            float duration,
            params (string Parameter, AnimatorConditionMode Mode, float Threshold)[] conditions)
        {
            var transition = from.AddTransition(to);
            transition.hasExitTime = false;
            transition.duration = duration;
            foreach (var condition in conditions)
            {
                transition.AddCondition(condition.Mode, condition.Threshold, condition.Parameter);
            }
        }
    }
}
