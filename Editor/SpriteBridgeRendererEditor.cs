#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SpriteBridge.Editor
{
    [CustomEditor(typeof(SpriteBridgeRenderer))]
    [CanEditMultipleObjects]
    public sealed class SpriteBridgeRendererEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(6);

            SpriteBridgeRenderer comp = (SpriteBridgeRenderer)target;
            SpriteBridgeManager mgr = SpriteBridgeManager.CurrentInstance;

            if (comp.IsAutoFallback)
            {
                EditorGUILayout.HelpBox(
                    $"Auto-Fallback to Native SpriteRenderer:\n{comp.FallbackReason}",
                    MessageType.Warning);
            }
            else if (comp.IsGpuActive)
            {
                string syncMode = mgr != null && mgr.IsUsingBurstSync ? "Burst SIMD Job" : "CoreModule Job";
                string animInfo = comp.ActiveLocalClipIndex >= 0 && comp.AnimationLibrary != null
                    ? $"GPU Anim: '{comp.AnimationLibrary.name}' [Clip #{comp.ActiveLocalClipIndex}]"
                    : "Static Sprite UV";
                string sortInfo = comp.SortMode.ToString();
                string statusText =
                    $"GPU Bridged Active\n" +
                    $"Slot #{comp.RegisteredSlotIndex} | Bucket #{comp.RegisteredBucketIndex} | Sort: {sortInfo}\n" +
                    $"{animInfo}\n" +
                    $"Manager: {(mgr != null ? mgr.ActiveSpriteCount : 0)} sprites ({(mgr != null ? mgr.DynamicTransformCount : 0)} dynamic via {syncMode}), {(mgr != null ? mgr.ActiveBucketCount : 0)} buckets";
                EditorGUILayout.HelpBox(statusText, MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Inactive (component disabled or awaiting initialization).", MessageType.None);
            }

            if (comp.IsTightPackedWarning)
            {
                EditorGUILayout.HelpBox(
                    "Tight-packed SpriteAtlas detected. Extracted bounding rect safely without throwing, but disable 'Tight Packing' on the SpriteAtlas if adjacent atlas pixels bleed into the quad corners.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Force Re-Sync GPU"))
                {
                    for (int i = 0; i < targets.Length; i++)
                    {
                        if (targets[i] is SpriteBridgeRenderer r)
                        {
                            r.MarkDirty();
                        }
                    }
                    if (mgr != null)
                    {
                        mgr.SyncBucketMaterialsFromSource();
                    }
                    SceneView.RepaintAll();
                }

                if (GUILayout.Button("Convert All Scene"))
                {
                    ConvertAllSceneSpriteRenderersToSpriteBridge();
                }

                if (GUILayout.Button("Revert Scene"))
                {
                    RevertAllSceneToNativeSpriteRenderers();
                }
            }

            EditorGUILayout.Space(4);
            EditorGUI.BeginChangeCheck();
            SpriteSceneViewDebugMode currentDebug = SpriteBridgeManager.PlayModeSceneViewDebugMode;
            SpriteSceneViewDebugMode nextDebug = (SpriteSceneViewDebugMode)EditorGUILayout.EnumPopup(
                new GUIContent("Play Mode Scene View", "Global Editor-only Scene View debug mode (forced Off in Player Builds)."),
                currentDebug);
            if (EditorGUI.EndChangeCheck())
            {
                SpriteBridgeManager.PlayModeSceneViewDebugMode = nextDebug;
            }
        }

        [MenuItem("Tools/SpriteBridge/Convert All Scene SpriteRenderers to SpriteBridge", false, 20)]
        public static void ConvertAllSceneSpriteRenderersToSpriteBridge()
        {
            SpriteRenderer[] allRenderers = Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None);
            int addedCount = 0;
            int skippedUnsupportedCount = 0;

            for (int i = 0; i < allRenderers.Length; i++)
            {
                SpriteRenderer sr = allRenderers[i];
                if (sr == null || sr.sprite == null)
                {
                    continue;
                }

                // Skip Sliced/Tiled or SpriteMask renderers so they remain 100% native
                if (sr.drawMode != SpriteDrawMode.Simple || sr.maskInteraction != SpriteMaskInteraction.None)
                {
                    skippedUnsupportedCount++;
                    continue;
                }

                if (!sr.TryGetComponent<SpriteBridgeRenderer>(out SpriteBridgeRenderer bridge))
                {
                    bridge = Undo.AddComponent<SpriteBridgeRenderer>(sr.gameObject);
                    // Default to DynamicTransform unless marked Editor Static
                    bridge.MotionMode = sr.gameObject.isStatic ? SpriteMotionMode.Static : SpriteMotionMode.DynamicTransform;
                    addedCount++;
                }

                bridge.MarkDirty();
            }

            SceneView.RepaintAll();
            Debug.Log($"[SpriteBridge] Converted scene SpriteRenderers: {addedCount} newly attached, {skippedUnsupportedCount} skipped (Sliced/Tiled/SpriteMask left on native SpriteRenderer).");
        }

        [MenuItem("Tools/SpriteBridge/Revert All Scene SpriteBridgeRenderers to Native", false, 21)]
        public static void RevertAllSceneToNativeSpriteRenderers()
        {
            SpriteBridgeRenderer[] allBridged = Object.FindObjectsByType<SpriteBridgeRenderer>(FindObjectsSortMode.None);
            int removedCount = 0;

            for (int i = 0; i < allBridged.Length; i++)
            {
                SpriteBridgeRenderer bridge = allBridged[i];
                if (bridge == null) continue;

                SpriteRenderer sr = bridge.NativeSpriteRenderer;
                Undo.DestroyObjectImmediate(bridge);
                if (sr != null)
                {
                    sr.forceRenderingOff = false;
                    sr.enabled = true;
                    EditorUtility.SetDirty(sr);
                }
                removedCount++;
            }

            SceneView.RepaintAll();
            Debug.Log($"[SpriteBridge] Reverted {removedCount} SpriteBridgeRenderers back to pure native Unity SpriteRenderers.");
        }
    }
}
#endif
