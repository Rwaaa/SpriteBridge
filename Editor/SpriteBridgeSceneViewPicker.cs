#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SpriteBridge.Editor
{
    /// <summary>
    /// Editor-only Scene View exact ray-vs-quad GPU sprite picker & wireframe highlighter.
    /// Active in Edit Mode and during Play Mode when PlayModeSceneViewDebugMode == GpuSprites.
    /// Zero per-frame cost when idle; runs only on clean left-mouse click and never intercepts
    /// gizmo drags, marquee selection, or Alt camera navigation.
    /// </summary>
    [InitializeOnLoad]
    public static class SpriteBridgeSceneViewPicker
    {
        private struct PickedSpriteInfo
        {
            public bool Valid;
            public SpriteBridgeRenderer Owner;
            public int SlotIndex;
            public Vector3 OriginWS;
            public Vector3 RightWS;
            public Vector3 UpWS;
        }

        private static bool s_TrackingClick;
        private static Vector2 s_MouseDownPos;
        private static PickedSpriteInfo s_LastPicked;

        static SpriteBridgeSceneViewPicker()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            SpriteBridgeManager manager = SpriteBridgeManager.CurrentInstance;
            if (manager == null || !manager.IsSupportedAndReady || manager.ActiveSpriteCount <= 0)
            {
                s_LastPicked.Valid = false;
                return;
            }

            if (Application.isPlaying &&
                SpriteBridgeManager.PlayModeSceneViewDebugMode != SpriteSceneViewDebugMode.GpuSprites)
            {
                s_LastPicked.Valid = false;
                return;
            }

            Event evt = Event.current;
            if (evt == null)
            {
                return;
            }

            if (evt.type == EventType.MouseDown && evt.button == 0)
            {
                if (!evt.alt &&
                    Tools.viewTool == ViewTool.None &&
                    GUIUtility.hotControl == 0 &&
                    HandleUtility.nearestControl == 0)
                {
                    s_TrackingClick = true;
                    s_MouseDownPos = evt.mousePosition;
                }
                else
                {
                    s_TrackingClick = false;
                }
            }
            else if (evt.type == EventType.MouseUp && evt.button == 0)
            {
                bool wasCleanClick = s_TrackingClick &&
                                     !evt.alt &&
                                     (evt.mousePosition - s_MouseDownPos).sqrMagnitude < 16f;
                s_TrackingClick = false;

                if (wasCleanClick)
                {
                    TryPickSpriteInSceneView(manager, sceneView, evt);
                }
            }
            else if (evt.type == EventType.Repaint)
            {
                DrawPickedOverlay(manager, sceneView);
            }
        }

        private static void TryPickSpriteInSceneView(SpriteBridgeManager manager, SceneView sceneView, Event evt)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
            Camera sceneCam = sceneView != null ? sceneView.camera : null;

            float bestRayT = float.MaxValue;
            GameObject nativePicked = HandleUtility.PickGameObject(evt.mousePosition, false);
            if (nativePicked != null && nativePicked.GetComponent<SpriteBridgeRenderer>() == null)
            {
                if (TryEstimateGameObjectRayDistance(nativePicked, ray, out float solidDist))
                {
                    bestRayT = solidDist;
                }
            }

            int count = manager.EditorSlotCount;
            bool foundHit = false;
            PickedSpriteInfo bestInfo = default;
            float bestSortKey = float.NegativeInfinity;

            for (int i = 0; i < count; i++)
            {
                if (!manager.EditorTryGetSlot(i, out SpriteBridgeRenderer owner, out GpuSpriteInstance inst))
                {
                    continue;
                }

                ComputeSpriteQuadWS(in inst, sceneCam, out Vector3 originWS, out Vector3 rightWS, out Vector3 upWS);

                if (RayIntersectsOrientedQuad(ray, originWS, rightWS, upWS, out float hitT))
                {
                    bool closer = hitT < bestRayT - 0.0005f;
                    bool coplanarHigherOrder = Mathf.Abs(hitT - bestRayT) <= 0.0005f && inst.SortKey >= bestSortKey;

                    if (closer || coplanarHigherOrder)
                    {
                        bestRayT = hitT;
                        bestSortKey = inst.SortKey;
                        foundHit = true;
                        bestInfo = new PickedSpriteInfo
                        {
                            Valid = true,
                            Owner = owner,
                            SlotIndex = i,
                            OriginWS = originWS,
                            RightWS = rightWS,
                            UpWS = upWS
                        };
                    }
                }
            }

            if (!foundHit || bestInfo.Owner == null)
            {
                if (nativePicked == null)
                {
                    s_LastPicked.Valid = false;
                }
                return;
            }

            s_LastPicked = bestInfo;
            SelectGameObject(bestInfo.Owner.gameObject, evt.shift || evt.control || evt.command);
            GUIUtility.hotControl = 0;
            evt.Use();
            sceneView.Repaint();
        }

        private static void DrawPickedOverlay(SpriteBridgeManager manager, SceneView sceneView)
        {
            if (!s_LastPicked.Valid || s_LastPicked.Owner == null || Selection.activeGameObject != s_LastPicked.Owner.gameObject)
            {
                s_LastPicked.Valid = false;
                return;
            }

            int slot = s_LastPicked.Owner.RegisteredSlotIndex;
            if (!manager.EditorTryGetSlot(slot, out _, out GpuSpriteInstance inst))
            {
                return;
            }

            Camera sceneCam = sceneView != null ? sceneView.camera : null;
            ComputeSpriteQuadWS(in inst, sceneCam, out Vector3 p0, out Vector3 rightWS, out Vector3 upWS);
            Vector3 p1 = p0 + rightWS;
            Vector3 p2 = p1 + upWS;
            Vector3 p3 = p0 + upWS;

            Color prev = Handles.color;
            Handles.color = new Color(0.15f, 0.95f, 1.0f, 0.95f);
            Handles.DrawLine(p0, p1);
            Handles.DrawLine(p1, p2);
            Handles.DrawLine(p2, p3);
            Handles.DrawLine(p3, p0);

            // Pivot crosshair
            float crossSize = HandleUtility.GetHandleSize(inst.Position) * 0.08f;
            Handles.color = new Color(1.0f, 0.85f, 0.2f, 0.95f);
            Handles.DrawLine(inst.Position - Vector3.right * crossSize, inst.Position + Vector3.right * crossSize);
            Handles.DrawLine(inst.Position - Vector3.up * crossSize, inst.Position + Vector3.up * crossSize);

            string label = $"{s_LastPicked.Owner.name} [Slot #{slot} | Bucket #{s_LastPicked.Owner.RegisteredBucketIndex} | {s_LastPicked.Owner.MotionMode}]";
            Handles.Label(p3 + Vector3.up * (crossSize * 0.75f), label);
            Handles.color = prev;
        }

        private static void ComputeSpriteQuadWS(
            in GpuSpriteInstance inst,
            Camera cam,
            out Vector3 originWS,
            out Vector3 rightWS,
            out Vector3 upWS)
        {
            uint flags = (inst.PackedMeta >> 16) & 0xFFu;
            Vector3 basisRight;
            Vector3 basisUp;

            if ((flags & (uint)GpuSpriteFlags.BillboardFull) != 0u && cam != null)
            {
                basisRight = cam.transform.right;
                basisUp = cam.transform.up;
            }
            else if ((flags & (uint)GpuSpriteFlags.BillboardYAxis) != 0u && cam != null)
            {
                Vector3 flatR = new Vector3(cam.transform.right.x, 0f, cam.transform.right.z);
                basisRight = flatR.sqrMagnitude > 1e-6f ? flatR.normalized : Vector3.right;
                basisUp = Vector3.up;
            }
            else
            {
                Quaternion q = new Quaternion(inst.Rotation.x, inst.Rotation.y, inst.Rotation.z, inst.Rotation.w);
                basisRight = q * Vector3.right;
                basisUp = q * Vector3.up;
            }

            originWS = inst.Position +
                       basisRight * (inst.PivotSize.x * inst.Scale.x) +
                       basisUp * (inst.PivotSize.y * inst.Scale.y);
            rightWS = basisRight * (inst.PivotSize.z * inst.Scale.x);
            upWS = basisUp * (inst.PivotSize.w * inst.Scale.y);
        }

        private static bool RayIntersectsOrientedQuad(
            Ray ray,
            Vector3 origin,
            Vector3 right,
            Vector3 up,
            out float hitT)
        {
            hitT = float.MaxValue;
            Vector3 normal = Vector3.Cross(right, up);
            float denom = Vector3.Dot(normal, ray.direction);
            if (Mathf.Abs(denom) < 1e-6f)
            {
                return false;
            }

            float t = Vector3.Dot(origin - ray.origin, normal) / denom;
            if (t < 0f)
            {
                return false;
            }

            Vector3 hitPoint = ray.origin + ray.direction * t;
            Vector3 rel = hitPoint - origin;

            float rightLenSq = right.sqrMagnitude;
            float upLenSq = up.sqrMagnitude;
            if (rightLenSq < 1e-8f || upLenSq < 1e-8f)
            {
                return false;
            }

            float u = Vector3.Dot(rel, right) / rightLenSq;
            float v = Vector3.Dot(rel, up) / upLenSq;

            if (u >= 0f && u <= 1f && v >= 0f && v <= 1f)
            {
                hitT = t;
                return true;
            }

            return false;
        }

        private static void SelectGameObject(GameObject targetGo, bool additiveToggle)
        {
            if (!additiveToggle)
            {
                Selection.activeGameObject = targetGo;
                return;
            }

            Object[] current = Selection.objects;
            List<Object> list = new List<Object>(current.Length + 1);
            bool alreadySelected = false;
            for (int i = 0; i < current.Length; i++)
            {
                if (current[i] == targetGo)
                {
                    alreadySelected = true;
                    continue;
                }
                list.Add(current[i]);
            }

            if (!alreadySelected)
            {
                list.Add(targetGo);
            }
            Selection.objects = list.ToArray();
        }

        private static bool TryEstimateGameObjectRayDistance(GameObject go, Ray ray, out float outDist)
        {
            outDist = float.MaxValue;
            if (go.TryGetComponent(out Collider col) && col.Raycast(ray, out RaycastHit hit, 10000f))
            {
                outDist = hit.distance;
                return true;
            }
            if (go.TryGetComponent(out Renderer rend) && rend.bounds.IntersectRay(ray, out float bDist) && bDist > 0f)
            {
                outDist = bDist;
                return true;
            }
            return false;
        }
    }
}
#endif
