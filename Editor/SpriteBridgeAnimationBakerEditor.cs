#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SpriteBridge.Editor
{
    /// <summary>
    /// Editor utility & Inspector for baking Unity 2D AnimationClips (SpriteRenderer.m_Sprite ObjectReference curves)
    /// into a GPU-ready SpriteBridgeAnimationLibrary ScriptableObject.
    /// </summary>
    [CustomEditor(typeof(SpriteBridgeAnimationLibrary))]
    public sealed class SpriteBridgeAnimationLibraryEditor : UnityEditor.Editor
    {
        private readonly List<AnimationClip> _sourceClips = new List<AnimationClip>();
        private string _lastBakeReport;
        private MessageType _lastBakeMessageType = MessageType.Info;

        public override void OnInspectorGUI()
        {
            SpriteBridgeAnimationLibrary lib = (SpriteBridgeAnimationLibrary)target;

            EditorGUILayout.HelpBox(
                $"Atlas Texture: {(lib.AtlasTexture != null ? lib.AtlasTexture.name : "None")}\n" +
                $"Baked Clips: {lib.ClipCount} | Total GPU Frames: {lib.FrameCount} ({lib.FrameCount * GpuSpriteAnimFrame.StrideBytes} B)",
                MessageType.Info);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Bake From Unity AnimationClips (m_Sprite)", EditorStyles.boldLabel);

            int clipCount = EditorGUILayout.IntField("Source Clip Count", _sourceClips.Count);
            clipCount = Mathf.Clamp(clipCount, 0, 128);
            while (_sourceClips.Count < clipCount) _sourceClips.Add(null);
            while (_sourceClips.Count > clipCount) _sourceClips.RemoveAt(_sourceClips.Count - 1);

            for (int i = 0; i < _sourceClips.Count; i++)
            {
                _sourceClips[i] = (AnimationClip)EditorGUILayout.ObjectField(
                    $"Clip {i}",
                    _sourceClips[i],
                    typeof(AnimationClip),
                    false);
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Selected Clips"))
                {
                    Object[] sel = Selection.objects;
                    for (int i = 0; i < sel.Length; i++)
                    {
                        if (sel[i] is AnimationClip clip && !_sourceClips.Contains(clip))
                        {
                            _sourceClips.Add(clip);
                        }
                    }
                }

                GUI.enabled = _sourceClips.Count > 0;
                if (GUILayout.Button("Bake Clips To Library"))
                {
                    BakeAnimationClipsIntoLibrary(lib, _sourceClips, out _lastBakeReport, out _lastBakeMessageType);
                }
                GUI.enabled = true;
            }

            if (!string.IsNullOrEmpty(_lastBakeReport))
            {
                EditorGUILayout.HelpBox(_lastBakeReport, _lastBakeMessageType);
            }

            EditorGUILayout.Space(8);
            DrawDefaultInspector();
        }

        [MenuItem("Tools/SpriteBridge/Bake Selected AnimationClips to Library", false, 30)]
        public static void BakeSelectedClipsMenu()
        {
            List<AnimationClip> clips = new List<AnimationClip>();
            Object[] selection = Selection.objects;
            for (int i = 0; i < selection.Length; i++)
            {
                if (selection[i] is AnimationClip clip)
                {
                    clips.Add(clip);
                }
            }

            if (clips.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "SpriteBridge Animation Baker",
                    "Select one or more 2D Sprite AnimationClip assets in the Project window first.",
                    "OK");
                return;
            }

            string savePath = EditorUtility.SaveFilePanelInProject(
                "Save SpriteBridge Animation Library",
                "SpriteBridgeAnimLibrary",
                "asset",
                "Choose where to save the baked GPU Sprite Animation Library.");

            if (string.IsNullOrEmpty(savePath))
            {
                return;
            }

            SpriteBridgeAnimationLibrary lib = AssetDatabase.LoadAssetAtPath<SpriteBridgeAnimationLibrary>(savePath);
            if (lib == null)
            {
                lib = ScriptableObject.CreateInstance<SpriteBridgeAnimationLibrary>();
                AssetDatabase.CreateAsset(lib, savePath);
            }

            BakeAnimationClipsIntoLibrary(lib, clips, out string report, out _);
            Selection.activeObject = lib;
            Debug.Log($"[SpriteBridge] {report}");
        }

        /// <summary>
        /// Quantizes SpriteRenderer.m_Sprite ObjectReference curves from Unity AnimationClips
        /// into fixed-step GPU lookup frames preserving arbitrary artist keyframe timing.
        /// </summary>
        public static int BakeAnimationClipsIntoLibrary(
            SpriteBridgeAnimationLibrary library,
            IList<AnimationClip> clips,
            out string report,
            out MessageType messageType)
        {
            if (library == null)
            {
                report = "Target library is null.";
                messageType = MessageType.Error;
                return 0;
            }

            Undo.RecordObject(library, "Bake SpriteBridge Animation Library");
            library.Clear();

            int bakedCount = 0;
            List<string> warnings = new List<string>();
            List<Sprite> sampledFrames = new List<Sprite>(64);

            for (int c = 0; c < clips.Count; c++)
            {
                AnimationClip clip = clips[c];
                if (clip == null)
                {
                    continue;
                }

                ObjectReferenceKeyframe[] keyframes = ExtractSpriteKeyframes(clip);
                if (keyframes == null || keyframes.Length == 0)
                {
                    warnings.Add($"Skipped '{clip.name}': no SpriteRenderer.m_Sprite track found.");
                    continue;
                }

                float sampleRate = Mathf.Clamp(clip.frameRate > 0.1f ? clip.frameRate : 12f, 4f, 60f);
                float clipLength = Mathf.Max(clip.length, 1f / sampleRate);
                int totalSteps = Mathf.Max(1, Mathf.RoundToInt(clipLength * sampleRate));

                sampledFrames.Clear();
                for (int step = 0; step < totalSteps; step++)
                {
                    float sampleTime = (step + 0.001f) / sampleRate;
                    Sprite chosen = EvaluateSteppedSpriteKeyframe(keyframes, sampleTime);
                    if (chosen != null)
                    {
                        sampledFrames.Add(chosen);
                    }
                }

                if (sampledFrames.Count == 0)
                {
                    warnings.Add($"Skipped '{clip.name}': all sampled sprite keyframes were null.");
                    continue;
                }

                int idx = library.AddClipFromSprites(
                    clip.name,
                    sampledFrames,
                    sampleRate,
                    clip.isLooping,
                    out string clipWarn);

                if (idx >= 0)
                {
                    bakedCount++;
                }

                if (!string.IsNullOrEmpty(clipWarn))
                {
                    warnings.Add(clipWarn);
                }
            }

            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();

            if (warnings.Count > 0)
            {
                report = $"Baked {bakedCount} clip(s) ({library.FrameCount} frames) with warnings:\n- " +
                         string.Join("\n- ", warnings);
                messageType = MessageType.Warning;
            }
            else
            {
                report = $"Successfully baked {bakedCount} clip(s) ({library.FrameCount} GPU frames) into '{library.name}'.";
                messageType = MessageType.Info;
            }

            return bakedCount;
        }

        private static ObjectReferenceKeyframe[] ExtractSpriteKeyframes(AnimationClip clip)
        {
            EditorCurveBinding[] bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding b = bindings[i];
                if (b.type == typeof(SpriteRenderer) && b.propertyName == "m_Sprite")
                {
                    return AnimationUtility.GetObjectReferenceCurve(clip, b);
                }
            }
            return null;
        }

        private static Sprite EvaluateSteppedSpriteKeyframe(ObjectReferenceKeyframe[] keyframes, float time)
        {
            Sprite current = keyframes[0].value as Sprite;
            for (int i = 0; i < keyframes.Length; i++)
            {
                if (keyframes[i].time <= time)
                {
                    current = keyframes[i].value as Sprite;
                }
                else
                {
                    break;
                }
            }
            return current;
        }
    }
}
#endif
