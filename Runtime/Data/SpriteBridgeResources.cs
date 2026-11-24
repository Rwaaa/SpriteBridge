using UnityEngine;

namespace SpriteBridge
{
    /// <summary>
    /// Serialized shader and compute shader registry stored in Runtime/Resources/SpriteBridgeResources.asset.
    /// Ensures Unity includes SpriteBridgeSimulation.compute and URP 2D/3D shaders in all Player Builds
    /// without shader stripping.
    /// </summary>
    public sealed class SpriteBridgeResources : ScriptableObject
    {
        public const string ResourceLoadPath = "SpriteBridgeResources";

        public ComputeShader SimulationCompute;
        public Shader UnlitShader;
        public Shader Lit2DShader;
        public Shader Lit3DShader;

        [Header("Editor Debugging (Forced OFF in Player Builds)")]
        [Tooltip("Editor-only Play Mode Scene View debugging mode (Off / GpuSprites / NativeSpriteRenderer). Automatically forced to Off before every Player Build.")]
        public SpriteSceneViewDebugMode PlayModeSceneViewDebugMode = SpriteSceneViewDebugMode.Off;

        public bool HasCoreReferences =>
            SimulationCompute != null &&
            UnlitShader != null &&
            Lit2DShader != null;

        private int PopulatedReferenceCount
        {
            get
            {
                int count = 0;
                if (SimulationCompute != null) count++;
                if (UnlitShader != null) count++;
                if (Lit2DShader != null) count++;
                if (Lit3DShader != null) count++;
                return count;
            }
        }

        private static SpriteBridgeResources s_Cached;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticCache()
        {
            s_Cached = null;
        }

        public static void InvalidateCache()
        {
            s_Cached = null;
        }

        public static SpriteBridgeResources Load()
        {
            if (s_Cached == null)
            {
                SpriteBridgeResources[] all = Resources.LoadAll<SpriteBridgeResources>(ResourceLoadPath);
                if (all != null && all.Length > 0)
                {
                    SpriteBridgeResources best = all[0];
                    int bestScore = best != null ? best.PopulatedReferenceCount : -1;
                    for (int i = 1; i < all.Length; i++)
                    {
                        if (all[i] != null && all[i].PopulatedReferenceCount > bestScore)
                        {
                            best = all[i];
                            bestScore = all[i].PopulatedReferenceCount;
                        }
                    }
                    s_Cached = best;
                }
                else
                {
                    s_Cached = Resources.Load<SpriteBridgeResources>(ResourceLoadPath);
                }
            }
            return s_Cached;
        }
    }
}
