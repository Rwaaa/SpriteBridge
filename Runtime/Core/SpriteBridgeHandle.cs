using System;

namespace SpriteBridge
{
    /// <summary>
    /// Generational handle identifying a sprite slot managed by SpriteBridgeManager.
    /// Used internally and exposed for Tier 2 Zero-GameObject high-density batch APIs (Phase 2).
    /// </summary>
    public readonly struct SpriteBridgeHandle : IEquatable<SpriteBridgeHandle>
    {
        public static readonly SpriteBridgeHandle Invalid = new SpriteBridgeHandle(-1, 0u);

        public readonly int SlotIndex;
        public readonly uint Generation;

        public bool IsValid => SlotIndex >= 0 && Generation != 0u;

        public SpriteBridgeHandle(int slotIndex, uint generation)
        {
            SlotIndex = slotIndex;
            Generation = generation;
        }

        public bool Equals(SpriteBridgeHandle other)
        {
            return SlotIndex == other.SlotIndex && Generation == other.Generation;
        }

        public override bool Equals(object obj)
        {
            return obj is SpriteBridgeHandle other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (SlotIndex * 397) ^ (int)Generation;
            }
        }

        public static bool operator ==(SpriteBridgeHandle left, SpriteBridgeHandle right) => left.Equals(right);
        public static bool operator !=(SpriteBridgeHandle left, SpriteBridgeHandle right) => !left.Equals(right);
    }
}
