// =============================================================================
// Module:  MathUtil.cs
// Path:    FootballSim/Engine/MathUtil.cs
// Purpose:
//   Canonical math helpers for the entire engine layer.
//   All systems use these instead of MathF.Lerp / Math.Clamp to keep the
//   engine free of Unity/Godot/MonoGame assumptions and to ensure a single
//   consistent clamped-Lerp contract throughout the codebase.
//
// API:
//   float Clamp01(float v)                 → clamps v to [0, 1]
//   float Clamp(float v, float lo, float hi) → clamps v to [lo, hi]
//   float Lerp(float a, float b, float t)  → clamped linear interpolation
//   float LerpUnclamped(float a, float b, float t) → unclamped Lerp
//   float InverseLerp(float a, float b, float v)   → finds t given value in [a,b]
//   bool  Approximately(float a, float b, float eps) → float equality with tolerance
//
// Rules:
//   • Lerp() always clamps t to [0,1] — this prevents out-of-range extrapolation
//     in scoring formulas, stamina curves, and tactic blends.
//   • If you need unclamped interpolation, use LerpUnclamped explicitly so the
//     intent is obvious at the call site.
//   • NEVER use MathF.Lerp or Math.Clamp in any Engine/ file except this one.
//     Search the codebase before adding any new MathF calls.
//
// Dependencies: System (for MathF)
// =============================================================================

using System;

namespace FootballSim.Engine
{
    public static class MathUtil
    {
        // ── Core ──────────────────────────────────────────────────────────────

        /// <summary>Clamps v to the range [0, 1].</summary>
        public static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }

        /// <summary>Clamps v to the range [lo, hi].</summary>
        public static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /// <summary>
        /// Clamped linear interpolation. t is clamped to [0,1] before use.
        /// Lerp(a, b, 0) = a. Lerp(a, b, 1) = b.
        /// Use this for ALL scoring blends, stamina curves, and tactic modifiers.
        /// </summary>
        public static float Lerp(float a, float b, float t)
        {
            t = Clamp01(t);
            return a + (b - a) * t;
        }

        /// <summary>
        /// Unclamped linear interpolation. t is NOT clamped — can extrapolate.
        /// Use only when you explicitly want values outside [a, b].
        /// </summary>
        public static float LerpUnclamped(float a, float b, float t)
            => a + (b - a) * t;

        /// <summary>
        /// Inverse lerp: returns t such that Lerp(a, b, t) == v.
        /// Returns 0 when v == a, returns 1 when v == b.
        /// Safe: returns 0 when a == b (avoids divide-by-zero).
        /// Result is NOT clamped — can be outside [0,1].
        /// </summary>
        public static float InverseLerp(float a, float b, float v)
        {
            float denom = b - a;
            if (MathF.Abs(denom) < 1e-6f) return 0f;
            return (v - a) / denom;
        }

        /// <summary>
        /// Returns true if |a - b| ≤ eps.
        /// Default eps = 0.0001f, suitable for position/score comparisons.
        /// </summary>
        public static bool Approximately(float a, float b, float eps = 0.0001f)
            => MathF.Abs(a - b) <= eps;

        /// <summary>
        /// Maps a value from range [inMin, inMax] to range [outMin, outMax].
        /// Clamps the input before mapping. Result is unclamped — caller clamps if needed.
        /// </summary>
        public static float Remap(float v,
                                   float inMin,  float inMax,
                                   float outMin, float outMax)
        {
            float t = InverseLerp(inMin, inMax, v);
            t = Clamp01(t);
            return LerpUnclamped(outMin, outMax, t);
        }
    }
}