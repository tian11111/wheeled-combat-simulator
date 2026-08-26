namespace Sim.Core;

/// <summary>
/// Deterministic random streams, ported bit-for-bit from the legacy CORE
/// (wushu_ring_sim.html): mulberry32 for the match logic stream and
/// mix32/hashString32-derived draws for per-channel sensor noise so that
/// adding or removing a sensor channel never reshuffles the main stream.
///
/// All arithmetic is int32 (JS <c>Math.imul</c> semantics) so the streams are
/// reproducible across platforms.
/// </summary>
public static class DeterministicRandom
{
    /// <summary>
    /// mulberry32 PRNG. One instance is the single ordered stream used by the
    /// legacy match logic (block placement, FSM scan direction, vision stub).
    /// </summary>
    public sealed class Mulberry32
    {
        private int _state;

        public Mulberry32(int seed) => _state = seed;

        /// <summary>Next uniform double in [0,1) — identical sequence to the JS core.</summary>
        public double Next()
        {
            unchecked
            {
                _state += unchecked((int)0x6D2B79F5);
                var a = _state;
                var t = Js.Imul(a ^ (int)((uint)a >> 15), 1 | a);
                t = (t + Js.Imul(t ^ (int)((uint)t >> 7), 61 | t)) ^ t;
                return (uint)(t ^ (int)((uint)t >> 14)) / 4294967296.0;
            }
        }
    }

    /// <summary>mix32 finalizer (fmix32 of MurmurHash3), int32 semantics.</summary>
    public static int Mix32(int value)
    {
        unchecked
        {
            var x = value;
            x = Js.Imul(x ^ (int)((uint)x >> 16), unchecked((int)0x45d9f3b));
            x = Js.Imul(x ^ (int)((uint)x >> 16), unchecked((int)0x45d9f3b));
            return x ^ (int)((uint)x >> 16);
        }
    }

    /// <summary>
    /// FNV-1a over the UTF-16 code units of the value, matching the legacy
    /// <c>hashString32</c> for BMP strings (sensor ids are ASCII).
    /// </summary>
    public static int HashString32(string? value)
    {
        unchecked
        {
            var h = unchecked((int)0x811c9dc5);
            if (value is not null)
            {
                foreach (var ch in value)
                {
                    h = Js.Imul(h ^ ch, unchecked((int)0x01000193));
                }
            }
            return h;
        }
    }

    /// <summary>Role keys used to derive the per-role sensor-noise stream.</summary>
    public const int UsRoleKey = unchecked((int)0x13579bdf);

    /// <summary>Role key for the "them" robot.</summary>
    public const int ThemRoleKey = unchecked((int)0x2468ace0);

    /// <summary>
    /// Per-channel sensor noise draw derived from (seed, step, role, channel)
    /// — the legacy <c>sensorNoiseRandom</c>. Note the step is 0-based and the
    /// legacy code mixes <c>(simStepIndex + 1)</c>.
    /// </summary>
    public static double SensorNoiseRandom(long seed, bool isUs, long simStepIndex, string channelId)
    {
        unchecked
        {
            var roleKey = isUs ? UsRoleKey : ThemRoleKey;
            var stepKey = Js.Imul((int)(simStepIndex + 1), unchecked((int)0x9e3779b9));
            var mixed = Mix32((int)seed ^ roleKey ^ stepKey ^ HashString32(channelId));
            return (uint)mixed / 4294967296.0;
        }
    }
}
