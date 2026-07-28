using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Animation
{
    internal static class ControllerOverride
    {
        public static object Create(JObject @params)
        {
            string controllerPath = @params["controllerPath"]?.ToString();
            if (string.IsNullOrEmpty(controllerPath))
                return new { success = false, message = "'controllerPath' is required (e.g. 'Assets/Animators/HeroOverride.overrideController')" };

            controllerPath = AssetPathUtility.SanitizeAssetPath(controllerPath);
            if (controllerPath == null)
                return new { success = false, message = "Invalid asset path" };

            if (!controllerPath.EndsWith(".overrideController", StringComparison.OrdinalIgnoreCase))
                controllerPath += ".overrideController";

            if (AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(controllerPath) != null)
                return new { success = false, message = $"AnimatorOverrideController already exists at '{controllerPath}'. Delete it first or use a different path." };

            string baseControllerPath = @params["baseControllerPath"]?.ToString();
            if (string.IsNullOrEmpty(baseControllerPath))
                return new { success = false, message = "'baseControllerPath' is required" };

            baseControllerPath = AssetPathUtility.SanitizeAssetPath(baseControllerPath);
            if (baseControllerPath == null)
                return new { success = false, message = "Invalid base controller path" };

            var baseController = AssetDatabase.LoadAssetAtPath<AnimatorController>(baseControllerPath);
            if (baseController == null)
                return new { success = false, message = $"AnimatorController not found at '{baseControllerPath}'" };

            string dir = Path.GetDirectoryName(controllerPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                CreateFoldersRecursive(dir);

            var overrideController = new AnimatorOverrideController(baseController);
            AssetDatabase.CreateAsset(overrideController, controllerPath);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Created AnimatorOverrideController at '{controllerPath}'",
                data = new
                {
                    path = controllerPath,
                    name = overrideController.name,
                    baseController = baseController.name,
                    baseControllerPath
                }
            };
        }

        public static object SetClip(JObject @params)
        {
            var overrideController = LoadOverrideController(@params);
            if (overrideController == null)
                return OverrideControllerNotFoundError(@params);

            string originalClipName = @params["originalClipName"]?.ToString();
            if (string.IsNullOrEmpty(originalClipName))
                return new { success = false, message = "'originalClipName' is required" };

            string overrideClipPath = @params["overrideClipPath"]?.ToString();
            if (string.IsNullOrEmpty(overrideClipPath))
                return new { success = false, message = "'overrideClipPath' is required" };

            overrideClipPath = AssetPathUtility.SanitizeAssetPath(overrideClipPath);
            if (overrideClipPath == null)
                return new { success = false, message = "Invalid override clip path" };

            var overrideClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(overrideClipPath);
            if (overrideClip == null)
                return new { success = false, message = $"AnimationClip not found at '{overrideClipPath}'" };

            var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
            overrideController.GetOverrides(overrides);

            bool found = overrides.Any(kvp => kvp.Key.name == originalClipName);
            if (!found)
            {
                var available = string.Join(", ", overrides.Select(kvp => kvp.Key.name));
                return new { success = false, message = $"Clip '{originalClipName}' not found in base controller. Available: {available}" };
            }

            Undo.RecordObject(overrideController, "Set Override Clip");
            overrideController[originalClipName] = overrideClip;
            EditorUtility.SetDirty(overrideController);
            AssetDatabase.SaveAssets();

            string controllerPath = AssetDatabase.GetAssetPath(overrideController);
            return new
            {
                success = true,
                message = $"Mapped '{originalClipName}' → '{overrideClip.name}' in '{controllerPath}'",
                data = new
                {
                    controllerPath,
                    originalClipName,
                    overrideClipName = overrideClip.name,
                    overrideClipPath
                }
            };
        }

        public static object GetClips(JObject @params)
        {
            var overrideController = LoadOverrideController(@params);
            if (overrideController == null)
                return OverrideControllerNotFoundError(@params);

            var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
            overrideController.GetOverrides(overrides);

            var clipMap = overrides.Select(kvp => new
            {
                original = kvp.Key.name,
                @override = kvp.Value?.name
            }).ToArray();

            string controllerPath = AssetDatabase.GetAssetPath(overrideController);
            return new
            {
                success = true,
                data = new
                {
                    controllerPath,
                    name = overrideController.name,
                    clipCount = clipMap.Length,
                    clips = clipMap
                }
            };
        }

        public static object AssignToGameObject(JObject @params)
        {
            var overrideController = LoadOverrideController(@params);
            if (overrideController == null)
                return OverrideControllerNotFoundError(@params);

            var go = ObjectResolver.ResolveGameObject(@params["target"], @params["searchMethod"]?.ToString());
            if (go == null)
                return new { success = false, message = "Target GameObject not found" };

            var animator = go.GetComponent<Animator>();
            if (animator == null)
            {
                Undo.RecordObject(go, "Add Animator Component");
                animator = Undo.AddComponent<Animator>(go);
            }

            Undo.RecordObject(animator, "Assign Override Controller");
            animator.runtimeAnimatorController = overrideController;
            EditorUtility.SetDirty(go);
            AssetDatabase.SaveAssets();

            string controllerPath = AssetDatabase.GetAssetPath(overrideController);
            return new
            {
                success = true,
                message = $"Assigned override controller '{overrideController.name}' to '{go.name}'",
                data = new
                {
                    gameObject = go.name,
                    controllerName = overrideController.name,
                    controllerPath
                }
            };
        }

        private static AnimatorOverrideController LoadOverrideController(JObject @params)
        {
            string path = @params["controllerPath"]?.ToString();
            if (string.IsNullOrEmpty(path))
                return null;

            path = AssetPathUtility.SanitizeAssetPath(path);
            if (path == null)
                return null;

            return AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(path);
        }

        private static object OverrideControllerNotFoundError(JObject @params)
        {
            string path = @params["controllerPath"]?.ToString() ?? "(not specified)";
            return new { success = false, message = $"AnimatorOverrideController not found at '{path}'. Provide a valid 'controllerPath' (.overrideController)." };
        }

        private static void CreateFoldersRecursive(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
                return;

            string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && parent != "Assets" && !AssetDatabase.IsValidFolder(parent))
                CreateFoldersRecursive(parent);

            string folderName = Path.GetFileName(folderPath);
            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(folderName))
                AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}
