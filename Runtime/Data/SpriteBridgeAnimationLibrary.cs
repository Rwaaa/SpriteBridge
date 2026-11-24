using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpriteBridge
{
    /// <summary>
    /// Baked GPU 2D Sprite Animation Library (ScriptableObject).
    /// Stores flat GPU-ready GpuSpriteAnimClip & GpuSpriteAnimFrame tables so thousands of animated
    /// 2D sprites run entirely on the GPU with Animator.enabled = false (0.000 ms CPU).
    /// Supports both Editor AnimationClip baking and Runtime procedural Sprite[] baking.
    /// </summary>
    [CreateAssetMenu(fileName = "SpriteBridgeAnimationLibrary", menuName = "SpriteBridge/Animation Library", order = 10)]
    public sealed class SpriteBridgeAnimationLibrary : ScriptableObject
    {
        [Serializable]
        public struct BakedClipMetadata
        {
            public string ClipName;
            public int FrameStartIndex;
            public int FrameCount;
            public float Duration;
            public bool Loop;
            public float SampleRate;
        }

        [SerializeField] private Texture2D _atlasTexture;
        [SerializeField] private List<BakedClipMetadata> _clips = new List<BakedClipMetadata>();
        [SerializeField] private List<GpuSpriteAnimFrame> _frames = new List<GpuSpriteAnimFrame>();
        [SerializeField] private int _revision = 1;

        private Dictionary<string, int> _nameToIndex;
        private int _cachedNameMapRevision = -1;

        public Texture2D AtlasTexture => _atlasTexture;
        public int ClipCount => _clips != null ? _clips.Count : 0;
        public int FrameCount => _frames != null ? _frames.Count : 0;
        public int Revision => _revision;

        public IReadOnlyList<BakedClipMetadata> Clips => _clips;
        public IReadOnlyList<GpuSpriteAnimFrame> Frames => _frames;

        /// <summary>
        /// Clears all clips and frames from this library (Editor baking or Runtime regeneration).
        /// </summary>
        public void Clear()
        {
            _atlasTexture = null;
            _clips.Clear();
            _frames.Clear();
            _revision++;
            _cachedNameMapRevision = -1;
        }

        /// <summary>
        /// Bakes a sprite sequence directly into GPU frame entries (works in both Editor and Runtime).
        /// Returns the local clip index, or -1 if invalid.
        /// </summary>
        public int AddClipFromSprites(
            string clipName,
            IList<Sprite> sprites,
            float framesPerSecond,
            bool loop,
            out string warning)
        {
            warning = null;
            if (sprites == null || sprites.Count == 0)
            {
                warning = $"Clip '{clipName}' has no sprite frames.";
                return -1;
            }

            float safeFps = Mathf.Max(0.1f, framesPerSecond);
            int startIndex = _frames.Count;
            int validFrames = 0;

            for (int i = 0; i < sprites.Count; i++)
            {
                Sprite sp = sprites[i];
                if (sp == null)
                {
                    continue;
                }

                if (!SpriteGeometryUtility.TryExtractSpriteGeometry(
                        sp,
                        out Texture2D tex,
                        out Vector4 uvRect,
                        out Vector4 pivotSize,
                        out _,
                        out _))
                {
                    continue;
                }

                if (_atlasTexture == null)
                {
                    _atlasTexture = tex;
                }
                else if (tex != null && tex != _atlasTexture)
                {
                    warning = $"Clip '{clipName}' references texture '{tex.name}' which differs from library atlas '{_atlasTexture.name}'. Pack all animation frames into one SpriteAtlas/SpriteSheet!";
                }

                _frames.Add(new GpuSpriteAnimFrame
                {
                    UvRect = uvRect,
                    PivotSize = pivotSize
                });
                validFrames++;
            }

            if (validFrames == 0)
            {
                warning = $"Clip '{clipName}' had zero valid sprites.";
                return -1;
            }

            int clipIndex = _clips.Count;
            _clips.Add(new BakedClipMetadata
            {
                ClipName = string.IsNullOrEmpty(clipName) ? $"Clip_{clipIndex}" : clipName,
                FrameStartIndex = startIndex,
                FrameCount = validFrames,
                Duration = validFrames / safeFps,
                Loop = loop,
                SampleRate = safeFps
            });

            _revision++;
            _cachedNameMapRevision = -1;
            return clipIndex;
        }

        /// <summary>
        /// Looks up a local clip index by name (O(1) cached dictionary). Returns -1 if not found.
        /// </summary>
        public int FindClipIndex(string clipName)
        {
            if (string.IsNullOrEmpty(clipName) || _clips == null || _clips.Count == 0)
            {
                return -1;
            }

            EnsureNameMap();
            return _nameToIndex.TryGetValue(clipName, out int idx) ? idx : -1;
        }

        public bool TryGetClip(int clipIndex, out BakedClipMetadata meta)
        {
            if (_clips != null && clipIndex >= 0 && clipIndex < _clips.Count)
            {
                meta = _clips[clipIndex];
                return true;
            }
            meta = default;
            return false;
        }

        public bool TryGetFirstFrameGeometry(int clipIndex, out Vector4 uvRect, out Vector4 pivotSize)
        {
            if (TryGetClip(clipIndex, out BakedClipMetadata meta) &&
                meta.FrameCount > 0 &&
                meta.FrameStartIndex >= 0 &&
                meta.FrameStartIndex < _frames.Count)
            {
                GpuSpriteAnimFrame f = _frames[meta.FrameStartIndex];
                uvRect = f.UvRect;
                pivotSize = f.PivotSize;
                return true;
            }

            uvRect = new Vector4(0f, 0f, 1f, 1f);
            pivotSize = new Vector4(-0.5f, -0.5f, 1f, 1f);
            return false;
        }

        private void EnsureNameMap()
        {
            if (_nameToIndex != null && _cachedNameMapRevision == _revision)
            {
                return;
            }

            if (_nameToIndex == null)
            {
                _nameToIndex = new Dictionary<string, int>(_clips.Count, StringComparer.Ordinal);
            }
            else
            {
                _nameToIndex.Clear();
            }

            for (int i = 0; i < _clips.Count; i++)
            {
                string key = _clips[i].ClipName;
                if (!string.IsNullOrEmpty(key) && !_nameToIndex.ContainsKey(key))
                {
                    _nameToIndex.Add(key, i);
                }
            }

            _cachedNameMapRevision = _revision;
        }
    }
}
