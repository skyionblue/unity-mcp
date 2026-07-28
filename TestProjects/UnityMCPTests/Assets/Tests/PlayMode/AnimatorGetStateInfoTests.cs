using System.Collections;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using MCPForUnity.Editor.Tools.Animation;

namespace MCPForUnityTests.PlayMode
{
    /// <summary>
    /// PlayMode tests for animator_get_state_info. This action requires Application.isPlaying
    /// and reads live AnimatorStateInfo from the runtime, so it cannot be covered in EditMode.
    /// </summary>
    public class AnimatorGetStateInfoTests
    {
        private const string TempRoot = "Assets/Temp/PlayModeAnimationTests";
        private GameObject _go;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
                AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.CreateFolder("Assets/Temp", "PlayModeAnimationTests");

            var clip = new AnimationClip { name = "Idle" };
            clip.SetCurve("", typeof(Transform), "localPosition.x",
                AnimationCurve.Linear(0f, 0f, 1f, 1f));
            AssetDatabase.CreateAsset(clip, TempRoot + "/Idle.anim");

            var controller = AnimatorController.CreateAnimatorControllerAtPath(TempRoot + "/TestCtrl.controller");
            var sm = controller.layers[0].stateMachine;
            var state = sm.AddState("Idle");
            state.motion = clip;
            sm.defaultState = state;
            AssetDatabase.SaveAssets();

            _go = new GameObject("AnimGetStateInfoTest");
            var animator = _go.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;

            yield return null; // let the Animator initialise
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_go != null)
                Object.Destroy(_go);
            yield return null;
            if (AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.DeleteAsset(TempRoot);
        }

        [UnityTest]
        public IEnumerator GetStateInfo_AllLayers_ReturnsCurrentState()
        {
            var result = JObject.FromObject(ManageAnimation.HandleCommand(new JObject
            {
                ["action"] = "animator_get_state_info",
                ["target"] = _go.name,
                ["searchMethod"] = "by_name"
            }));

            Assert.IsTrue(result.Value<bool>("success"), result["message"]?.ToString());

            var data = result["data"] as JObject;
            Assert.IsNotNull(data);
            Assert.AreEqual(_go.name, data.Value<string>("gameObject"));

            var layers = data["layers"] as JArray;
            Assert.IsNotNull(layers);
            Assert.AreEqual(1, layers.Count);

            var layer = layers[0] as JObject;
            Assert.AreEqual(0, layer.Value<int>("layerIndex"));
            Assert.AreEqual("Base Layer", layer.Value<string>("layerName"));
            Assert.AreNotEqual(0, layer.Value<int>("currentStateHash"),
                "currentStateHash should be non-zero when an Animator is playing a state");
            Assert.GreaterOrEqual(layer.Value<float>("currentStateNormalizedTime"), 0f);
            Assert.IsFalse(layer.Value<bool>("isInTransition"));
            Assert.IsNull(layer.Value<JObject>("transition"));

            yield return null;
        }

        [UnityTest]
        public IEnumerator GetStateInfo_SpecificLayer_ReturnsOnlyThatLayer()
        {
            var result = JObject.FromObject(ManageAnimation.HandleCommand(new JObject
            {
                ["action"] = "animator_get_state_info",
                ["target"] = _go.name,
                ["searchMethod"] = "by_name",
                ["layerIndex"] = 0
            }));

            Assert.IsTrue(result.Value<bool>("success"), result["message"]?.ToString());

            var layers = (result["data"] as JObject)?["layers"] as JArray;
            Assert.IsNotNull(layers);
            Assert.AreEqual(1, layers.Count, "Requesting layer 0 should return exactly one layer");
            Assert.AreEqual(0, (layers[0] as JObject).Value<int>("layerIndex"));

            yield return null;
        }

        [UnityTest]
        public IEnumerator GetStateInfo_OutOfRangeLayer_ReturnsError()
        {
            var result = JObject.FromObject(ManageAnimation.HandleCommand(new JObject
            {
                ["action"] = "animator_get_state_info",
                ["target"] = _go.name,
                ["searchMethod"] = "by_name",
                ["layerIndex"] = 99
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["message"].ToString(), Does.Contain("out of range"));

            yield return null;
        }

        [UnityTest]
        public IEnumerator GetStateInfo_MissingTarget_ReturnsError()
        {
            var result = JObject.FromObject(ManageAnimation.HandleCommand(new JObject
            {
                ["action"] = "animator_get_state_info",
                ["target"] = "DoesNotExist_XYZ",
                ["searchMethod"] = "by_name"
            }));

            Assert.IsFalse(result.Value<bool>("success"));

            yield return null;
        }
    }
}
