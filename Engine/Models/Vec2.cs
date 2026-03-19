// =============================================================================
// Module:  Vec2.cs
// Path:    FootballSim/Engine/Models/Vec2.cs
// Purpose:
//   Lightweight 2D vector struct for the simulation engine.
//   Replaces Godot.Vector2 inside Engine/ folder — engine has ZERO Godot dependency.
//   Only basic operations needed by MovementSystem, BallSystem, CollisionSystem.
//
// API:
//   Vec2(float x, float y)          Constructor
//   Vec2 Zero                        Static: (0, 0)
//   Vec2 + Vec2                      Add
//   Vec2 - Vec2                      Subtract
//   Vec2 * float                     Scale
//   Vec2 / float                     Divide (safe — returns Zero if divisor == 0)
//   float Length()                   Magnitude
//   float LengthSquared()            Magnitude squared (cheaper, use for comparisons)
//   float DistanceTo(Vec2 other)     Euclidean distance
//   float DistanceSquaredTo(Vec2)    Squared distance (for range checks — avoids sqrt)
//   Vec2  Normalized()               Unit vector (safe — returns Zero if length == 0)
//   Vec2  Lerp(Vec2 to, float t)     Linear interpolation
//   float Dot(Vec2 other)            Dot product
//   string ToString()                "Vec2(x, y)" for debug output
//
// Dependencies: none
//
// Notes:
//   • struct (value type) for zero heap allocation in tight tick loop.
//   • DistanceSquaredTo is the primary method used in CollisionSystem range checks.
//     Always compare squared distances to squared radii — skip the sqrt.
//   • MatchEngineBridge.cs converts Vec2 ↔ Godot.Vector2 at the boundary.
//     No conversion happens inside Engine/.
// =============================================================================

using System;

namespace FootballSim.Engine.Models
{
    /// <summary>
    /// Lightweight 2D vector. Value type (struct). No Godot dependency.
    /// Used for all positions and velocities inside Engine/.
    /// </summary>
    public struct Vec2
    {
        public float X;
        public float Y;

        // ── Constructor ───────────────────────────────────────────────────────

        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        // ── Static Helpers ────────────────────────────────────────────────────

        /// <summary>Vec2(0, 0). Use instead of default(Vec2) for clarity.</summary>
        public static readonly Vec2 Zero = new Vec2(0f, 0f);

        // ── Operators ─────────────────────────────────────────────────────────

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator *(float s, Vec2 a) => new Vec2(a.X * s, a.Y * s);

        /// <summary>Safe divide — returns Vec2.Zero if divisor is zero.</summary>
        public static Vec2 operator /(Vec2 a, float s)
        {
            if (MathF.Abs(s) < 0.0001f) return Zero;
            return new Vec2(a.X / s, a.Y / s);
        }

        public static bool operator ==(Vec2 a, Vec2 b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(Vec2 a, Vec2 b) => !(a == b);

        public override bool Equals(object obj)
        {
            if (obj is Vec2 other) return this == other;
            return false;
        }

        public override int GetHashCode() => HashCode.Combine(X, Y);

        // ── Methods ───────────────────────────────────────────────────────────

        /// <summary>
        /// Euclidean magnitude. Use LengthSquared() for comparisons — it skips sqrt.
        /// </summary>
        public float Length() => MathF.Sqrt(X * X + Y * Y);

        /// <summary>
        /// Magnitude squared. Use for range checks: distSq < radiusSq.
        /// Avoids sqrt — significantly cheaper in tight loops.
        /// </summary>
        public float LengthSquared() => X * X + Y * Y;

        /// <summary>Euclidean distance to another Vec2.</summary>
        public float DistanceTo(Vec2 other)
        {
            float dx = X - other.X;
            float dy = Y - other.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Squared distance to another Vec2. Preferred for CollisionSystem range checks.
        /// Usage: if (a.DistanceSquaredTo(b) < tackleRadius * tackleRadius)
        /// </summary>
        public float DistanceSquaredTo(Vec2 other)
        {
            float dx = X - other.X;
            float dy = Y - other.Y;
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// Returns unit vector (length 1.0) pointing in same direction.
        /// Returns Vec2.Zero safely if this vector has near-zero length.
        /// </summary>
        public Vec2 Normalized()
        {
            float len = Length();
            if (len < 0.0001f) return Zero;
            return new Vec2(X / len, Y / len);
        }

        /// <summary>
        /// Linear interpolation between this and 'to' by factor t (0.0 – 1.0).
        /// t=0 returns this. t=1 returns 'to'. Unclamped — t can exceed [0,1].
        /// </summary>
        public Vec2 Lerp(Vec2 to, float t)
        {
            return new Vec2(
                X + (to.X - X) * t,
                Y + (to.Y - Y) * t
            );
        }

        /// <summary>
        /// Dot product. Used to determine if two vectors point in similar directions.
        /// Positive = same direction, Negative = opposing, Zero = perpendicular.
        /// </summary>
        public float Dot(Vec2 other) => X * other.X + Y * other.Y;

        /// <summary>Debug output: "Vec2(340.00, 210.50)"</summary>
        public override string ToString() => $"Vec2({X:F2}, {Y:F2})";
    }
}