using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Animation
{
    internal static class ControllerAvatarMask
    {
        public static object CreateAvatarMask(JObject @params)
        {
            string maskPath = @params["maskPath"]?.ToString();
            if (string.IsNullOrEmpty(maskPath))
                return new { success = false, message = "'maskPath' is required (e.g. 'Assets/Animations/UpperBodyMask.mask')" };

            maskPath = AssetPathUtility.SanitizeAssetPath(maskPath);
            if (maskPath == null)
                return new { success = false, message = "Invalid asset path" };

            if (!maskPath.EndsWith(".mask", StringComparison.OrdinalIgnoreCase))
                maskPath += ".mask";

            if (AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath) != null)
                return new { success = false, message = $"AvatarMask already exists at '{maskPath}'. Delete it first or use a different path." };

            string dir = Path.GetDirectoryName(maskPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                CreateFoldersRecursive(dir);

            var mask = new AvatarMask();

            JToken bodyPartsToken = @params["bodyParts"];
            bool hasBodyPartsList = bodyPartsToken != null && bodyPartsToken.Type == JTokenType.Array;
            var enabledParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (hasBodyPartsList)
            {
                foreach (var item in (JArray)bodyPartsToken)
                    enabledParts.Add(item.ToString());
            }

            foreach (AvatarMaskBodyPart part in Enum.GetValues(typeof(AvatarMaskBodyPart)))
            {
                if (part == AvatarMaskBodyPart.LastBodyPart) continue;
                // No list provided → enable all; list provided → enable only named parts
                mask.SetHumanoidBodyPartActive(part, !hasBodyPartsList || enabledParts.Contains(part.ToString()));
            }

            JToken transformPathsToken = @params["transformPaths"];
            if (transformPathsToken is JArray transformPathsArray && transformPathsArray.Count > 0)
            {
                mask.transformCount = transformPathsArray.Count;
                for (int i = 0; i < transformPathsArray.Count; i++)
                {
                    mask.SetTransformPath(i, transformPathsArray[i].ToString());
                    mask.SetTransformActive(i, true);
                }
            }

            AssetDatabase.CreateAsset(mask, maskPath);
            AssetDatabase.SaveAssets();

            var activeParts = new List<string>();
            foreach (AvatarMaskBodyPart part in Enum.GetValues(typeof(AvatarMaskBodyPart)))
            {
                if (part == AvatarMaskBodyPart.LastBodyPart) continue;
                if (mask.GetHumanoidBodyPartActive(part)) activeParts.Add(part.ToString());
            }

            return new
            {
                success = true,
                message = $"Created AvatarMask at '{maskPath}'",
                data = new
                {
                    path = maskPath,
                    name = mask.name,
                    activeBodyParts = activeParts.ToArray(),
                    transformCount = mask.transformCount
                }
            };
        }

        public static object AssignToLayer(JObject @params)
        {
            string controllerPath = @params["controllerPath"]?.ToString();
            if (string.IsNullOrEmpty(controllerPath))
                return new { success = false, message = "'controllerPath' is required" };

            controllerPath = AssetPathUtility.SanitizeAssetPath(controllerPath);
            if (controllerPath == null)
                return new { success = false, message = "Invalid asset path" };

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null)
                return new { success = false, message = $"AnimatorController not found at '{controllerPath}'" };

            int layerIndex = @params["layerIndex"]?.ToObject<int>() ?? 0;
            if (layerIndex < 0 || layerIndex >= controller.layers.Length)
                return new { success = false, message = $"Layer index {layerIndex} out of range (controller has {controller.layers.Length} layers)" };

            string maskPath = @params["maskPath"]?.ToString();
            if (string.IsNullOrEmpty(maskPath))
                return new { success = false, message = "'maskPath' is required" };

            maskPath = AssetPathUtility.SanitizeAssetPath(maskPath);
            if (maskPath == null)
                return new { success = false, message = "Invalid mask path" };

            var avatarMask = AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath);
            if (avatarMask == null)
                return new { success = false, message = $"AvatarMask not found at '{maskPath}'" };

            Undo.RecordObject(controller, "Assign Avatar Mask");
            var layers = controller.layers;
            var layer = layers[layerIndex];
            layer.avatarMask = avatarMask;
            layers[layerIndex] = layer;
            controller.layers = layers;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Assigned mask '{avatarMask.name}' to layer {layerIndex} ('{layer.name}') of '{controllerPath}'",
                data = new
                {
                    controllerPath,
                    layerIndex,
                    layerName = layer.name,
                    maskPath,
                    maskName = avatarMask.name
                }
            };
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
