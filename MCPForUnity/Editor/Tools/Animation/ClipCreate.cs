using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Animation
{
    internal static class ClipCreate
    {
        public static object Create(JObject @params)
        {
            string clipPath = @params["clipPath"]?.ToString();
            if (string.IsNullOrEmpty(clipPath))
                return new { success = false, message = "'clipPath' is required (e.g. 'Assets/Animations/Walk.anim')" };

            clipPath = AssetPathUtility.SanitizeAssetPath(clipPath);
            if (clipPath == null)
                return new { success = false, message = "Invalid asset path" };

            if (!clipPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                clipPath += ".anim";

            // Ensure directory exists
            string dir = Path.GetDirectoryName(clipPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                CreateFoldersRecursive(dir);
            }

            // Check if already exists
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (existing != null)
                return new { success = false, message = $"AnimationClip already exists at '{clipPath}'. Delete it first or use a different path." };

            var clip = new AnimationClip();
            string name = @params["name"]?.ToString();
            clip.name = !string.IsNullOrEmpty(name)
                ? name
                : Path.GetFileNameWithoutExtension(clipPath);

            float length = @params["length"]?.ToObject<float>() ?? 1f;
            clip.frameRate = @params["frameRate"]?.ToObject<float>() ?? 60f;

            bool loop = @params["loop"]?.ToObject<bool>() ?? false;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            settings.stopTime = length;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            AssetDatabase.CreateAsset(clip, clipPath);

            // Set m_WrapMode via SerializedObject — clip.wrapMode is a runtime property
            // that doesn't serialize to m_WrapMode, so we set it directly for the legacy system
            if (loop)
            {
                var so = new SerializedObject(clip);
                var wrapProp = so.FindProperty("m_WrapMode");
                if (wrapProp != null)
                {
                    wrapProp.intValue = (int)WrapMode.Loop;
                    so.ApplyModifiedProperties();
                }
            }

            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Created AnimationClip at '{clipPath}'",
                data = new
                {
                    path = clipPath,
                    name = clip.name,
                    length,
                    frameRate = clip.frameRate,
                    isLooping = loop
                }
            };
        }

        public static object GetInfo(JObject @params)
        {
            string clipPath = @params["clipPath"]?.ToString();
            if (string.IsNullOrEmpty(clipPath))
                return new { success = false, message = "'clipPath' is required" };

            clipPath = AssetPathUtility.SanitizeAssetPath(clipPath);
            if (clipPath == null)
                return new { success = false, message = "Invalid asset path" };

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
                return new { success = false, message = $"AnimationClip not found at '{clipPath}'" };

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            var bindings = AnimationUtility.GetCurveBindings(clip);

            var curves = new List<object>();
            foreach (var binding in bindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                curves.Add(new
                {
                    path = binding.path,
                    propertyName = binding.propertyName,
                    type = binding.type.Name,
                    keyCount = curve?.length ?? 0
                });
            }

            var events = AnimationUtility.GetAnimationEvents(clip);
            var eventList = events.Select(e => new
            {
                time = e.time,
                functionName = e.functionName,
                stringParameter = e.stringParameter,
                floatParameter = e.floatParameter,
                intParameter = e.intParameter
            }).ToArray();

            return new
            {
                success = true,
                data = new
                {
                    path = clipPath,
                    name = clip.name,
                    length = clip.length,
                    frameRate = clip.frameRate,
                    isLooping = settings.loopTime,
                    wrapMode = clip.wrapMode.ToString(),
                    curveCount = bindings.Length,
                    curves,
                    eventCount = events.Length,
                    events = eventList
                }
            };
        }

        public static object AddCurve(JObject @params)
        {
            return SetOrAddCurve(@params, append: true);
        }

        public static object SetCurve(JObject @params)
        {
            return SetOrAddCurve(@params, append: false);
        }

        private static object SetOrAddCurve(JObject @params, bool append)
        {
            string clipPath = @params["clipPath"]?.ToString();
            if (string.IsNullOrEmpty(clipPath))
                return new { success = false, message = "'clipPath' is required" };

            clipPath = AssetPathUtility.SanitizeAssetPath(clipPath);
            if (clipPath == null)
                return new { success = false, message = "Invalid asset path" };

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
                return new { success = false, message = $"AnimationClip not found at '{clipPath}'" };

            string propertyPath = @params["propertyPath"]?.ToString();
            if (string.IsNullOrEmpty(propertyPath))
                return new { success = false, message = "'propertyPath' is required (e.g. 'localPosition.x')" };

            string typeName = @params["type"]?.ToString() ?? "Transform";
            Type componentType = ResolveType(typeName);
            if (componentType == null)
                return new { success = false, message = $"Could not resolve type '{typeName}'" };

            string relativePath = @params["relativePath"]?.ToString() ?? "";

            JToken keysToken = @params["keys"];
            if (keysToken == null)
                return new { success = false, message = "'keys' is required" };

            var keyframes = ParseKeyframes(keysToken);
            if (keyframes == null || keyframes.Length == 0)
                return new { success = false, message = "Failed to parse keyframes. Use [{\"time\":0,\"value\":0},...] or [[0,0],[1,1],...]" };

            AnimationCurve curve;
            var binding = EditorCurveBinding.FloatCurve(relativePath, componentType, propertyPath);

            if (append)
            {
                curve = AnimationUtility.GetEditorCurve(clip, binding) ?? new AnimationCurve();
                foreach (var kf in keyframes)
                {
                    curve.AddKey(kf);
                }
            }
            else
            {
                curve = new AnimationCurve(keyframes);
            }

            // Use AnimationUtility.SetEditorCurve instead of clip.SetCurve to avoid
            // marking the clip as legacy — legacy clips cannot be used in Mecanim BlendTrees.
            Undo.RecordObject(clip, append ? "Add Animation Curve" : "Set Animation Curve");
            AnimationUtility.SetEditorCurve(clip, binding, curve);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            string verb = append ? "Added" : "Set";
            return new
            {
                success = true,
                message = $"{verb} curve on '{propertyPath}' ({typeName}) with {keyframes.Length} keyframes",
                data = new
                {
                    clipPath,
                    propertyPath,
                    type = typeName,
                    keyframeCount = curve.length
                }
            };
        }

        public static object SetVectorCurve(JObject @params)
        {
            string clipPath = @params["clipPath"]?.ToString();
            if (string.IsNullOrEmpty(clipPath))
                return new { success = false, message = "'clipPath' is required" };

            clipPath = AssetPathUtility.SanitizeAssetPath(clipPath);
            if (clipPath == null)
                return new { success = false, message = "Invalid asset path" };

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
                return new { success = false, message = $"AnimationClip not found at '{clipPath}'" };

            // Accept both 'property' and 'propertyPath' for consistency with add_curve/set_curve
            string property = @params["property"]?.ToString() ?? @params["propertyPath"]?.ToString();
            if (string.IsNullOrEmpty(property))
                return new { success = false, message = "'property' (or 'propertyPath') is required (e.g. 'localPosition', 'localEulerAngles', 'localScale')" };

            string typeName = @params["type"]?.ToString() ?? "Transform";
            Type componentType = ResolveType(typeName);
            if (componentType == null)
                return new { success = false, message = $"Could not resolve type '{typeName}'" };

            string relativePath = @params["relativePath"]?.ToString() ?? "";

            JToken keysToken = @params["keys"];
            if (keysToken == null || keysToken is not JArray keysArray || keysArray.Count == 0)
                return new { success = false, message = "'keys' is required. Use [{\"time\":0,\"value\":[0,1,0]},...]" };

            // Map property group to axis suffixes
            string[] suffixes;
            switch (property.ToLowerInvariant())
            {
                case "localposition":
                    property = "localPosition";
                    suffixes = new[] { ".x", ".y", ".z" };
                    break;
                case "localeulerangles":
                    property = "localEulerAngles";
                    suffixes = new[] { ".x", ".y", ".z" };
                    break;
                case "localscale":
                    property = "localScale";
                    suffixes = new[] { ".x", ".y", ".z" };
                    break;
                default:
                    suffixes = new[] { ".x", ".y", ".z" };
                    break;
            }

            var xKeys = new List<Keyframe>();
            var yKeys = new List<Keyframe>();
            var zKeys = new List<Keyframe>();

            foreach (var item in keysArray)
            {
                if (item is not JObject keyObj)
                    return new { success = false, message = "Each key must be an object with 'time' and 'value' (Vector3 array)" };

                float time = keyObj["time"]?.ToObject<float>() ?? 0f;
                JToken valueToken = keyObj["value"];
                if (valueToken is not JArray valArray || valArray.Count < 3)
                    return new { success = false, message = $"Key at time {time}: 'value' must be a 3-element array [x, y, z]" };

                float vx = valArray[0].ToObject<float>();
                float vy = valArray[1].ToObject<float>();
                float vz = valArray[2].ToObject<float>();

                xKeys.Add(new Keyframe(time, vx));
                yKeys.Add(new Keyframe(time, vy));
                zKeys.Add(new Keyframe(time, vz));
            }

            // Use AnimationUtility.SetEditorCurve instead of clip.SetCurve to avoid
            // marking the clip as legacy — legacy clips cannot be used in Mecanim BlendTrees.
            Undo.RecordObject(clip, "Set Vector Curve");
            var bindingX = EditorCurveBinding.FloatCurve(relativePath, componentType, property + suffixes[0]);
            var bindingY = EditorCurveBinding.FloatCurve(relativePath, componentType, property + suffixes[1]);
            var bindingZ = EditorCurveBinding.FloatCurve(relativePath, componentType, property + suffixes[2]);
            AnimationUtility.SetEditorCurve(clip, bindingX, new AnimationCurve(xKeys.ToArray()));
            AnimationUtility.SetEditorCurve(clip, bindingY, new AnimationCurve(yKeys.ToArray()));
            AnimationUtility.SetEditorCurve(clip, bindingZ, new AnimationCurve(zKeys.ToArray()));
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Set 3 curves on '{property}' ({typeName}) with {keysArray.Count} vector keyframes",
                data = new
                {
                    clipPath,
                    property,
                    type = typeName,
                    curves = new[] { property + suffixes[0], property + suffixes[1], property + suffixes[2] },
                    keyframeCount = keysArray.Count
                }
            };
        }

        public static object Assign(JObject @params)
        {
            var go = ObjectResolver.ResolveGameObject(@params["target"], @params["searchMethod"]?.ToString());
            if (go == null)
                return new { success = false, message = "Target GameObject not found" };

            string clipPath = @params["clipPath"]?.ToString();
            if (string.IsNullOrEmpty(clipPath))
                return new { success = false, message = "'clipPath' is required" };

            clipPath = AssetPathUtility.SanitizeAssetPath(clipPath);
            if (clipPath == null)
                return new { success = false, message = "Invalid asset path" };

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
                return new { success = false, message = $"AnimationClip not found at '{clipPath}'" };

            // Try legacy Animation component first
            var legacyAnim = go.GetComponent<UnityEngine.Animation>();
            if (legacyAnim != null)
            {
                var wasLegacy = clip.legacy;
                SetupLegacyClip(clip);
                Undo.RecordObject(legacyAnim, "Assign Animation Clip");
                legacyAnim.clip = clip;
                legacyAnim.AddClip(clip, clip.name);
                legacyAnim.playAutomatically = true;
                EditorUtility.SetDirty(legacyAnim);
                AssetDatabase.SaveAssets();

                // Warn about AnimationEvents if present — they require a MonoBehaviour receiver
                var events = AnimationUtility.GetAnimationEvents(clip);
                string warning = "";
                if (events != null && events.Length > 0)
                {
                    var eventNames = new System.Collections.Generic.List<string>();
                    foreach (var e in events)
                        eventNames.Add(e.functionName);
                    warning = $" Warning: This clip has {events.Length} AnimationEvent(s) ({string.Join(", ", eventNames)}). " +
                              $"'{go.name}' must have a MonoBehaviour with matching method(s) to receive them, " +
                              "otherwise Unity will log 'AnimationEvent has no receiver' errors.";
                }

                if (!wasLegacy) warning += " Warning: clip was converted to legacy and will not be usable in Mecanim/BlendTrees.";

                return new { success = true, message = $"Assigned clip '{clip.name}' to Animation component on '{go.name}'.{warning}" };
            }

            // Add Animation component if no Animator or Animation exists
            var animator = go.GetComponent<Animator>();
            if (animator == null)
            {
                var wasLegacy = clip.legacy;
                SetupLegacyClip(clip);
                Undo.RecordObject(go, "Add Animation Component");
                legacyAnim = Undo.AddComponent<UnityEngine.Animation>(go);
                legacyAnim.clip = clip;
                legacyAnim.AddClip(clip, clip.name);
                legacyAnim.playAutomatically = true;
                EditorUtility.SetDirty(go);
                AssetDatabase.SaveAssets();
                var legacyWarning = !wasLegacy ? " Warning: clip was converted to legacy and will not be usable in Mecanim/BlendTrees." : "";
                return new { success = true, message = $"Added Animation component and assigned clip '{clip.name}' to '{go.name}'.{legacyWarning}" };
            }

            // Has Animator - we can't programmatically assign clips to Animator states easily,
            // so report what the user should do
            return new
            {
                success = true,
                message = $"GameObject '{go.name}' has an Animator component. The clip '{clip.name}' is available at '{clipPath}'. " +
                          "Assign it to an Animator Controller state via the Animator window or create an AnimatorOverrideController."
            };
        }

        private static void SetupLegacyClip(AnimationClip clip)
        {
            var so = new SerializedObject(clip);
            bool changed = false;

            if (!clip.legacy)
            {
                var legacyProp = so.FindProperty("m_Legacy");
                if (legacyProp != null)
                {
                    legacyProp.boolValue = true;
                    changed = true;
                }
            }

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (settings.loopTime)
            {
                var wrapProp = so.FindProperty("m_WrapMode");
                if (wrapProp != null && wrapProp.intValue != (int)WrapMode.Loop)
                {
                    wrapProp.intValue = (int)WrapMode.Loop;
                    changed = true;
                }
            }

            if (changed)
                so.ApplyModifiedProperties();
        }

        private static Keyframe[] ParseKeyframes(JToken keysToken)
        {
            if (keysToken is JArray array && array.Count > 0)
            {
                var keyframes = new List<Keyframe>();

                foreach (var item in array)
                {
                    if (item is JArray pair && pair.Count >= 2)
                    {
                        // Shorthand: [time, value]
                        float time = pair[0].ToObject<float>();
                        float value = pair[1].ToObject<float>();
                        keyframes.Add(new Keyframe(time, value));
                    }
                    else if (item is JObject obj)
                    {
                        // Full form: {"time":0, "value":0, "inTangent":0, "outTangent":0}
                        float time = obj["time"]?.ToObject<float>() ?? 0f;
                        float value = obj["value"]?.ToObject<float>() ?? 0f;

                        var kf = new Keyframe(time, value);
                        if (obj["inTangent"] != null)
                            kf.inTangent = obj["inTangent"].ToObject<float>();
                        if (obj["outTangent"] != null)
                            kf.outTangent = obj["outTangent"].ToObject<float>();
                        if (obj["inWeight"] != null)
                            kf.inWeight = obj["inWeight"].ToObject<float>();
                        if (obj["outWeight"] != null)
                            kf.outWeight = obj["outWeight"].ToObject<float>();

                        keyframes.Add(kf);
                    }
                }

                return keyframes.ToArray();
            }

            return null;
        }

        private static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return typeof(Transform);

            // Try common Unity types
            Type type = Type.GetType($"UnityEngine.{typeName}, UnityEngine.CoreModule");
            if (type != null) return type;

            type = Type.GetType($"UnityEngine.{typeName}, UnityEngine.AnimationModule");
            if (type != null) return type;

            type = Type.GetType($"UnityEngine.{typeName}, UnityEngine");
            if (type != null) return type;

            // Try fully qualified
            type = Type.GetType(typeName);
            if (type != null) return type;

            // Fallback: search all loaded assemblies
            foreach (var assembly in UnityAssembliesCompat.GetLoadedAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null) return type;

                type = assembly.GetType($"UnityEngine.{typeName}");
                if (type != null) return type;
            }

            return null;
        }

        public static object AddEvent(JObject @params)
        {
            string clipPath = @params["clipPath"]?.ToString();
            if (string.IsNullOrEmpty(clipPath))
                return new { success = false, message = "'clipPath' is required" };

            clipPath = AssetPathUtility.SanitizeAssetPath(clipPath);
            if (clipPath == null)
                return new { success = false, message = "Invalid asset path" };

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
                return new { success = false, message = $"AnimationClip not found at '{clipPath}'" };

            float time = @params["time"]?.ToObject<float>() ?? 0f;
            string functionName = @params["functionName"]?.ToString();
            if (string.IsNullOrEmpty(functionName))
                return new { success = false, message = "'functionName' is required" };

            var animEvent = new AnimationEvent
            {
                time = time,
                functionName = functionName,
                stringParameter = @params["stringParameter"]?.ToString() ?? "",
                floatParameter = @params["floatParameter"]?.ToObject<float>() ?? 0f,
                intParameter = @params["intParameter"]?.ToObject<int>() ?? 0
            };

            var events = AnimationUtility.GetAnimationEvents(clip).ToList();
            events.Add(animEvent);
            AnimationUtility.SetAnimationEvents(clip, events.ToArray());

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Added event '{functionName}' at time {time} to '{clipPath}'",
                data = new
                {
                    clipPath,
                    time,
                    functionName,
                    stringParameter = animEvent.stringParameter,
                    floatParameter = animEvent.floatParameter,
                    intParameter = animEvent.intParameter
                }
            };
        }

        public static object RemoveEvent(JObject @params)
        {
            string clipPath = @params["clipPath"]?.ToString();
            if (string.IsNullOrEmpty(clipPath))
                return new { success = false, message = "'clipPath' is required" };

            clipPath = AssetPathUtility.SanitizeAssetPath(clipPath);
            if (clipPath == null)
                return new { success = false, message = "Invalid asset path" };

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
                return new { success = false, message = $"AnimationClip not found at '{clipPath}'" };

            var events = AnimationUtility.GetAnimationEvents(clip).ToList();
            int originalCount = events.Count;

            int? eventIndex = @params["eventIndex"]?.ToObject<int?>();
            if (eventIndex.HasValue)
            {
                if (eventIndex.Value < 0 || eventIndex.Value >= events.Count)
                    return new { success = false, message = $"Event index {eventIndex.Value} out of range (0-{events.Count - 1})" };

                events.RemoveAt(eventIndex.Value);
            }
            else
            {
                string functionName = @params["functionName"]?.ToString();
                if (string.IsNullOrEmpty(functionName))
                    return new { success = false, message = "Either 'eventIndex' or 'functionName' is required" };

                float? timeFilter = @params["time"]?.ToObject<float?>();
                events.RemoveAll(e =>
                {
                    bool matchesFunction = e.functionName == functionName;
                    bool matchesTime = !timeFilter.HasValue || Mathf.Approximately(e.time, timeFilter.Value);
                    return matchesFunction && matchesTime;
                });
            }

            int removedCount = originalCount - events.Count;
            if (removedCount == 0)
                return new { success = false, message = "No matching events found to remove" };

            AnimationUtility.SetAnimationEvents(clip, events.ToArray());
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Removed {removedCount} event(s) from '{clipPath}'",
                data = new
                {
                    clipPath,
                    removedCount,
                    remainingCount = events.Count
                }
            };
        }

        public static object RemoveCurve(JObject @params)
        {
            string clipPath = @params["clipPath"]?.ToString();
            if (string.IsNullOrEmpty(clipPath))
                return new { success = false, message = "'clipPath' is required" };

            clipPath = AssetPathUtility.SanitizeAssetPath(clipPath);
            if (clipPath == null)
                return new { success = false, message = "Invalid asset path" };

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
                return new { success = false, message = $"AnimationClip not found at '{clipPath}'" };

            string propertyPath = @params["propertyPath"]?.ToString();

            Undo.RecordObject(clip, "Remove Animation Curve");

            if (string.IsNullOrEmpty(propertyPath))
            {
                var allBindings = AnimationUtility.GetCurveBindings(clip);
                foreach (var binding in allBindings)
                    AnimationUtility.SetEditorCurve(clip, binding, null);

                EditorUtility.SetDirty(clip);
                AssetDatabase.SaveAssets();

                return new
                {
                    success = true,
                    message = $"Removed all {allBindings.Length} curves from '{clipPath}'",
                    data = new { clipPath, removedCount = allBindings.Length }
                };
            }

            string typeName = @params["type"]?.ToString() ?? "Transform";
            Type componentType = ResolveType(typeName);
            if (componentType == null)
                return new { success = false, message = $"Could not resolve type '{typeName}'" };

            string relativePath = @params["relativePath"]?.ToString() ?? "";
            var targetBinding = EditorCurveBinding.FloatCurve(relativePath, componentType, propertyPath);

            var existing = AnimationUtility.GetEditorCurve(clip, targetBinding);
            if (existing == null)
                return new { success = false, message = $"Curve '{propertyPath}' ({typeName}) not found on this clip" };

            AnimationUtility.SetEditorCurve(clip, targetBinding, null);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Removed curve '{propertyPath}' ({typeName}) from '{clipPath}'",
                data = new { clipPath, propertyPath, type = typeName }
            };
        }

        public static object SetLoopSettings(JObject @params)
        {
            string clipPath = @params["clipPath"]?.ToString();
            if (string.IsNullOrEmpty(clipPath))
                return new { success = false, message = "'clipPath' is required" };

            clipPath = AssetPathUtility.SanitizeAssetPath(clipPath);
            if (clipPath == null)
                return new { success = false, message = "Invalid asset path" };

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
                return new { success = false, message = $"AnimationClip not found at '{clipPath}'" };

            JToken loopTimeToken = @params["loopTime"];
            JToken loopPoseToken = @params["loopPose"];
            JToken cycleOffsetToken = @params["cycleOffset"];
            JToken frameRateToken = @params["frameRate"];

            if (loopTimeToken == null && loopPoseToken == null && cycleOffsetToken == null && frameRateToken == null)
                return new { success = false, message = "No settings provided. Pass at least one of: loopTime, loopPose, cycleOffset, frameRate" };

            Undo.RecordObject(clip, "Set Clip Loop Settings");
            var settings = AnimationUtility.GetAnimationClipSettings(clip);

            if (loopTimeToken != null) settings.loopTime = loopTimeToken.ToObject<bool>();
            if (loopPoseToken != null) settings.loopBlend = loopPoseToken.ToObject<bool>();
            if (cycleOffsetToken != null) settings.cycleOffset = cycleOffsetToken.ToObject<float>();
            if (frameRateToken != null) clip.frameRate = frameRateToken.ToObject<float>();

            AnimationUtility.SetAnimationClipSettings(clip, settings);

            // Sync m_WrapMode for legacy compatibility (mirrors Create's approach)
            var so = new SerializedObject(clip);
            var wrapProp = so.FindProperty("m_WrapMode");
            if (wrapProp != null)
            {
                wrapProp.intValue = settings.loopTime ? (int)WrapMode.Loop : (int)WrapMode.Default;
                so.ApplyModifiedProperties();
            }

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            var finalSettings = AnimationUtility.GetAnimationClipSettings(clip);
            return new
            {
                success = true,
                message = $"Updated loop settings on '{clipPath}'",
                data = new
                {
                    clipPath,
                    loopTime = finalSettings.loopTime,
                    loopPose = finalSettings.loopBlend,
                    cycleOffset = finalSettings.cycleOffset,
                    frameRate = clip.frameRate
                }
            };
        }

        public static object Duplicate(JObject @params)
        {
            string clipPath = @params["clipPath"]?.ToString();
            if (string.IsNullOrEmpty(clipPath))
                return new { success = false, message = "'clipPath' is required" };

            clipPath = AssetPathUtility.SanitizeAssetPath(clipPath);
            if (clipPath == null)
                return new { success = false, message = "Invalid asset path" };

            var source = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (source == null)
                return new { success = false, message = $"AnimationClip not found at '{clipPath}'" };

            string destPath = @params["destPath"]?.ToString();
            if (string.IsNullOrEmpty(destPath))
                return new { success = false, message = "'destPath' is required (e.g. 'Assets/Animations/WalkFast.anim')" };

            destPath = AssetPathUtility.SanitizeAssetPath(destPath);
            if (destPath == null)
                return new { success = false, message = "Invalid destination asset path" };

            if (!destPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                destPath += ".anim";

            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(destPath) != null)
                return new { success = false, message = $"AnimationClip already exists at '{destPath}'. Delete it first or use a different path." };

            string destDir = Path.GetDirectoryName(destPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(destDir) && !AssetDatabase.IsValidFolder(destDir))
                CreateFoldersRecursive(destDir);

            bool copied = AssetDatabase.CopyAsset(clipPath, destPath);
            if (!copied)
                return new { success = false, message = $"Failed to copy '{clipPath}' to '{destPath}'" };

            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                message = $"Duplicated '{clipPath}' to '{destPath}'",
                data = new { sourcePath = clipPath, destPath }
            };
        }

        public static object CopyCurves(JObject @params)
        {
            string clipPath = @params["clipPath"]?.ToString();
            if (string.IsNullOrEmpty(clipPath))
                return new { success = false, message = "'clipPath' is required (source clip)" };

            clipPath = AssetPathUtility.SanitizeAssetPath(clipPath);
            if (clipPath == null)
                return new { success = false, message = "Invalid asset path" };

            var sourceClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (sourceClip == null)
                return new { success = false, message = $"Source AnimationClip not found at '{clipPath}'" };

            string destClipPath = @params["destClipPath"]?.ToString();
            if (string.IsNullOrEmpty(destClipPath))
                return new { success = false, message = "'destClipPath' is required (destination clip)" };

            destClipPath = AssetPathUtility.SanitizeAssetPath(destClipPath);
            if (destClipPath == null)
                return new { success = false, message = "Invalid destination asset path" };

            var destClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(destClipPath);
            if (destClip == null)
                return new { success = false, message = $"Destination AnimationClip not found at '{destClipPath}'" };

            bool overwrite = @params["overwrite"]?.ToObject<bool>() ?? false;

            var sourceBindings = AnimationUtility.GetCurveBindings(sourceClip);
            if (sourceBindings.Length == 0)
                return new { success = false, message = "Source clip has no curves to copy" };

            Undo.RecordObject(destClip, "Copy Animation Curves");

            int copiedCount = 0;
            int skippedCount = 0;

            foreach (var binding in sourceBindings)
            {
                var existingCurve = AnimationUtility.GetEditorCurve(destClip, binding);
                if (existingCurve != null && !overwrite)
                {
                    skippedCount++;
                    continue;
                }

                var curve = AnimationUtility.GetEditorCurve(sourceClip, binding);
                AnimationUtility.SetEditorCurve(destClip, binding, curve);
                copiedCount++;
            }

            EditorUtility.SetDirty(destClip);
            AssetDatabase.SaveAssets();

            string skippedMsg = skippedCount > 0 ? $" ({skippedCount} skipped, already exist)" : "";
            return new
            {
                success = true,
                message = $"Copied {copiedCount} curves from '{clipPath}' to '{destClipPath}'{skippedMsg}",
                data = new { sourcePath = clipPath, destPath = destClipPath, copiedCount, skippedCount }
            };
        }

        private static void CreateFoldersRecursive(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
                return;

            string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && parent != "Assets" && !AssetDatabase.IsValidFolder(parent))
            {
                CreateFoldersRecursive(parent);
            }

            string folderName = Path.GetFileName(folderPath);
            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(folderName))
            {
                AssetDatabase.CreateFolder(parent, folderName);
            }
        }
    }
}
