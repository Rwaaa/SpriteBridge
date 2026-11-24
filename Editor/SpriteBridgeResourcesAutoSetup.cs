#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SpriteBridge.Editor
{
    /// <summary>
    /// Automatically creates and populates SpriteBridgeResources.asset on Editor load and before every Player Build,
    /// provides Tools/SpriteBridge/Play Mode Scene View menu items, and enforces PlayModeSceneViewDebugMode = Off
    /// prior to all Player Builds.
    /// </summary>
    [InitializeOnLoad]
    public sealed class SpriteBridgeResourcesAutoSetup : IPreprocessBuildWithReport
    {
        private const string AssetsResourcesDir = "Assets/SpriteBridge/Runtime/Resources";
        private const string AssetsResourceAssetPath = "Assets/SpriteBridge/Runtime/Resources/SpriteBridgeResources.asset";
        private const string ProjectFallbackResourcesDir = "Assets/Resources";
        private const string ProjectFallbackResourceAssetPath = "Assets/Resources/SpriteBridgeResources.asset";

        private const string MenuPathSceneViewOff = "Tools/SpriteBridge/Play Mode Scene View/Off (Default - Max FPS)";
        private const string MenuPathSceneViewGpu = "Tools/SpriteBridge/Play Mode Scene View/GPU Sprites (High FPS + Click Pick)";
        private const string MenuPathSceneViewNative = "Tools/SpriteBridge/Play Mode Scene View/Native SpriteRenderer (100% Native Scene View + A-B Compare)";

        public int callbackOrder => -100;

        static SpriteBridgeResourcesAutoSetup()
        {
            EditorApplication.delayCall += EnsureResourcesAssetPopulated;
        }

        [MenuItem(MenuPathSceneViewOff, false, 50)]
        private static void SetSceneViewModeOff()
        {
            SpriteBridgeManager.PlayModeSceneViewDebugMode = SpriteSceneViewDebugMode.Off;
            Debug.Log("[SpriteBridge] Play Mode Scene View Debug Mode: Off (Max FPS)");
        }

        [MenuItem(MenuPathSceneViewOff, true)]
        private static bool ValidateSceneViewModeOff()
        {
            Menu.SetChecked(MenuPathSceneViewOff, SpriteBridgeManager.PlayModeSceneViewDebugMode == SpriteSceneViewDebugMode.Off);
            return true;
        }

        [MenuItem(MenuPathSceneViewGpu, false, 51)]
        private static void SetSceneViewModeGpu()
        {
            SpriteBridgeManager.PlayModeSceneViewDebugMode = SpriteSceneViewDebugMode.GpuSprites;
            Debug.Log("[SpriteBridge] Play Mode Scene View Debug Mode: GpuSprites (High FPS + Individual Click Pick)");
        }

        [MenuItem(MenuPathSceneViewGpu, true)]
        private static bool ValidateSceneViewModeGpu()
        {
            Menu.SetChecked(MenuPathSceneViewGpu, SpriteBridgeManager.PlayModeSceneViewDebugMode == SpriteSceneViewDebugMode.GpuSprites);
            return true;
        }

        [MenuItem(MenuPathSceneViewNative, false, 52)]
        private static void SetSceneViewModeNative()
        {
            SpriteBridgeManager.PlayModeSceneViewDebugMode = SpriteSceneViewDebugMode.NativeSpriteRenderer;
            Debug.Log("[SpriteBridge] Play Mode Scene View Debug Mode: NativeSpriteRenderer (Scene View = 100% Native SpriteRenderer, Game View = GPU SpriteBridge)");
        }

        [MenuItem(MenuPathSceneViewNative, true)]
        private static bool ValidateSceneViewModeNative()
        {
            Menu.SetChecked(MenuPathSceneViewNative, SpriteBridgeManager.PlayModeSceneViewDebugMode == SpriteSceneViewDebugMode.NativeSpriteRenderer);
            return true;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            EnsureResourcesAssetPopulated();

            if (SpriteBridgeManager.PlayModeSceneViewDebugMode != SpriteSceneViewDebugMode.Off)
            {
                SpriteBridgeManager.PlayModeSceneViewDebugMode = SpriteSceneViewDebugMode.Off;
                Debug.Log("[SpriteBridge] Forced PlayModeSceneViewDebugMode = Off for Player Build.");
            }

            SpriteBridgeResources res = SpriteBridgeResources.Load();
            if (res != null && res.PlayModeSceneViewDebugMode != SpriteSceneViewDebugMode.Off)
            {
                res.PlayModeSceneViewDebugMode = SpriteSceneViewDebugMode.Off;
                EditorUtility.SetDirty(res);
                AssetDatabase.SaveAssets();
                SpriteBridgeResources.InvalidateCache();
            }
        }

        [MenuItem("Tools/SpriteBridge/Verify Build Shader Resources", false, 100)]
        public static void EnsureResourcesAssetPopulated()
        {
            bool livesInAssets = Directory.Exists(Path.GetFullPath("Assets/SpriteBridge/Runtime"));

            string targetDir;
            string targetAssetPath;

            if (livesInAssets)
            {
                targetDir = AssetsResourcesDir;
                targetAssetPath = AssetsResourceAssetPath;
            }
            else
            {
                SpriteBridgeResources existing = SpriteBridgeResources.Load();
                if (existing != null && existing.HasCoreReferences)
                {
                    return;
                }
                targetDir = ProjectFallbackResourcesDir;
                targetAssetPath = ProjectFallbackResourceAssetPath;
            }

            if (!Directory.Exists(Path.GetFullPath(targetDir)))
            {
                Directory.CreateDirectory(Path.GetFullPath(targetDir));
            }

            SpriteBridgeResources res = AssetDatabase.LoadAssetAtPath<SpriteBridgeResources>(targetAssetPath);
            bool created = false;
            if (res == null)
            {
                res = ScriptableObject.CreateInstance<SpriteBridgeResources>();
                AssetDatabase.CreateAsset(res, targetAssetPath);
                created = true;
            }

            bool dirty = created;

            if (res.SimulationCompute == null)
            {
                res.SimulationCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/SpriteBridge/Shaders/SpriteBridgeSimulation.compute");
                if (res.SimulationCompute == null)
                {
                    res.SimulationCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                        "Packages/com.spritebridge.core/Shaders/SpriteBridgeSimulation.compute");
                }
                if (res.SimulationCompute != null) dirty = true;
            }

            if (res.UnlitShader == null)
            {
                res.UnlitShader = Shader.Find("SpriteBridge/URP/Sprite-Unlit");
                if (res.UnlitShader != null) dirty = true;
            }

            if (res.Lit2DShader == null)
            {
                res.Lit2DShader = Shader.Find("SpriteBridge/URP 2D/Sprite-Lit");
                if (res.Lit2DShader != null) dirty = true;
            }

            if (res.Lit3DShader == null)
            {
                res.Lit3DShader = Shader.Find("SpriteBridge/URP 3D/Sprite-Lit-2.5D");
                if (res.Lit3DShader != null) dirty = true;
            }

            if (dirty)
            {
                EditorUtility.SetDirty(res);
                AssetDatabase.SaveAssets();
                SpriteBridgeResources.InvalidateCache();
            }
        }
    }
}
#endif
