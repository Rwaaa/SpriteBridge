using System.Collections.Generic;
using UnityEngine;

namespace SpriteBridge.Samples
{
    /// <summary>
    /// Standalone UPM Sample: 20,000-Sprite GPU Animated + Bitonic Y-Sorted Horde Showcase.
    /// Zero external package dependencies (compiles in any Unity 2022.3+ URP project).
    /// </summary>
    public sealed class SpriteBridgeHordeBenchmarkSample : MonoBehaviour
    {
        [SerializeField] private int _spriteCount = 20000;
        [SerializeField] private bool _enableGpuYSort = true;

        private Texture2D _atlas;
        private SpriteBridgeAnimationLibrary _animLib;
        private readonly List<Sprite> _frames = new List<Sprite>(8);
        private readonly List<SpriteBridgeHandle> _handles = new List<SpriteBridgeHandle>(20000);
        private Vector2[] _baseXY;
        private float[] _phases;
        private float _smoothedFps = 60f;

        private void Start()
        {
            BuildProceduralSheet();
            SpawnZeroGoHorde();
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0.0001f) _smoothedFps = Mathf.Lerp(_smoothedFps, 1f / dt, 0.08f);

            SpriteBridgeManager mgr = SpriteBridgeManager.CurrentInstance;
            if (mgr == null || _handles.Count == 0) return;

            float t = Time.time;
            var instances = mgr.AcquireWritableInstanceBuffer(out int activeCount);
            for (int i = 0; i < _handles.Count; i++)
            {
                if (!mgr.TryGetSlotIndex(_handles[i], out int slot) || slot >= activeCount) continue;
                Vector2 b = _baseXY[i];
                float ph = _phases[i];
                GpuSpriteInstance inst = instances[slot];
                inst.Position = new Vector3(
                    b.x + Mathf.Sin(t * 1.8f + ph) * 0.25f,
                    b.y + Mathf.Cos(t * 1.3f + ph) * 0.35f,
                    0f);
                instances[slot] = inst;
            }
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _frames.Count; i++)
            {
                if (_frames[i] != null) Destroy(_frames[i]);
            }
            if (_animLib != null) Destroy(_animLib);
            if (_atlas != null) Destroy(_atlas);
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(14, 14, 380, 90), GUI.skin.box);
            GUILayout.Label("<b>SpriteBridge 20,000-Unit Horde Sample</b>");
            GUILayout.Label($"FPS: {_smoothedFps:F1} | Active Sprites: {_handles.Count}");
            GUILayout.Label($"GPU Bitonic Y-Sort: {(_enableGpuYSort ? "ON" : "OFF")}");
            GUILayout.EndArea();
        }

        private void SpawnZeroGoHorde()
        {
            SpriteBridgeManager mgr = SpriteBridgeManager.GetOrCreate();
            int bucket = mgr.RegisterBatchBucket(
                null,
                _atlas,
                _enableGpuYSort ? SpriteSortMode.GpuYSort : SpriteSortMode.None,
                0,
                0);

            _baseXY = new Vector2[_spriteCount];
            _phases = new float[_spriteCount];
            int cols = Mathf.CeilToInt(Mathf.Sqrt(_spriteCount * 1.8f));
            float spacing = 0.22f;
            float halfW = (cols - 1) * spacing * 0.5f;

            for (int i = 0; i < _spriteCount; i++)
            {
                int col = i % cols;
                int row = i / cols;
                Vector2 b = new Vector2(col * spacing - halfW, 5f - row * spacing);
                _baseXY[i] = b;
                _phases[i] = i * 0.17f;

                SpriteBridgeHandle h = mgr.SpawnAnimatedSprite(
                    bucket,
                    _animLib,
                    0,
                    new Vector3(b.x, b.y, 0f),
                    Quaternion.identity,
                    Vector3.one * 0.32f,
                    Color.HSVToRGB((i * 0.6180339f) % 1f, 0.7f, 0.96f),
                    1.0f,
                    (i & 7) * 0.1f);
                _handles.Add(h);
            }
        }

        private void BuildProceduralSheet()
        {
            const int frameCount = 8;
            const int cell = 64;
            _atlas = new Texture2D(cell * frameCount, cell, TextureFormat.RGBA32, false, false);
            Color32[] px = new Color32[cell * frameCount * cell];
            for (int f = 0; f < frameCount; f++)
            {
                float bounce = Mathf.Abs(Mathf.Sin((f / 8f) * Mathf.PI * 2f)) * 0.15f;
                for (int y = 0; y < cell; y++)
                {
                    for (int x = 0; x < cell; x++)
                    {
                        float u = (x + 0.5f) / cell - 0.5f;
                        float v = (y + 0.5f) / cell - (0.48f + bounce);
                        float d = (u * u) / 0.10f + (v * v) / 0.11f;
                        px[y * (cell * frameCount) + f * cell + x] = d <= 1f
                            ? (Color32)new Color(1f - d * 0.35f, 1f - d * 0.35f, 1f - d * 0.35f, 1f)
                            : new Color32(0, 0, 0, 0);
                    }
                }
                _frames.Add(Sprite.Create(_atlas, new Rect(f * cell, 0, cell, cell), new Vector2(0.5f, 0.15f), 64f));
            }
            _atlas.SetPixels32(px);
            _atlas.Apply(false, false);

            _animLib = ScriptableObject.CreateInstance<SpriteBridgeAnimationLibrary>();
            _animLib.AddClipFromSprites("Walk8", _frames, 10f, true, out _);
        }
    }
}
