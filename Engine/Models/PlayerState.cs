// =============================================================================
// Module:  PlayerState.cs
// Path:    FootballSim/Engine/Models/PlayerState.cs
// Purpose:
//   Runtime state snapshot of a single player for one tick.
//   Holds everything the systems need to read or write about a player:
//   position, velocity, stamina, current action, role, team side.
//   This is NOT a persistent data object — it is rebuilt/mutated every tick
//   by MovementSystem, PlayerAI, and DecisionSystem.
//
// API (fields — no methods, pure data):
//   PlayerId        int       Squad index 0–10 home, 11–21 away
//   TeamId          int       0 = home, 1 = away
//   ShirtNumber     int       1–11, display only
//   Role            PlayerRole  Enum: ST, CF, IW, WF, AM, CM, BBM, CDM, DLP, WB, CB, BPD, GK, SK
//   Position        Vec2      Current world position in pitch coordinates
//   TargetPosition  Vec2      Where MovementSystem is steering this player toward
//   FormationAnchor Vec2      Base anchor from FormationData — never changes mid-match
//   Velocity        Vec2      Current movement vector (pixels per tick)
//   Speed           float     Current speed scalar (affected by stamina, sprint flag)
//   Stamina         float     0.0 – 1.0. Decays during sprint, recovers at walk
//   IsSprinting     bool      True when chasing ball or making run — higher stamina drain
//   HasBall         bool      True when this player is the ball owner
//   Action          PlayerAction  Enum: current action this tick
//   DecisionCooldown int      Ticks remaining before PlayerAI re-evaluates decision
//   IsActive        bool      False = red card / injury — skip all system updates
//
// Dependencies:
//   Engine/Models/Vec2.cs         (lightweight vector, no Godot)
//   Engine/Models/PlayerRole.cs   (role enum)
//   Engine/Models/PlayerAction.cs (action enum)
//
// Notes:
//   • Vec2 is a custom struct — NOT Godot.Vector2. Engine has zero Godot dependency.
//   • FormationAnchor is set once at match start from FormationData and never touched again.
//   • DecisionCooldown counts down each tick; PlayerAI skips re-evaluation until 0.
//   • Stamina consequence: Speed = BaseSpeed * StaminaSpeedCurve(Stamina).
//     The curve is applied in MovementSystem, not stored here.
// =============================================================================

namespace FootballSim.Engine.Models
{
    /// <summary>
    /// Runtime state of one player. Mutated each tick by MovementSystem, PlayerAI,
    /// DecisionSystem, and CollisionSystem. Pure data — no methods.
    /// </summary>
    public struct PlayerState
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>
        /// Global player index across both teams: 0–10 = home, 11–21 = away.
        /// Used as array index in MatchContext.Players[].
        /// </summary>
        public int PlayerId;

        /// <summary>0 = home team, 1 = away team.</summary>
        public int TeamId;

        /// <summary>Shirt number 1–11. Display only — not used for logic.</summary>
        public int ShirtNumber;

        /// <summary>
        /// Tactical role. Drives spatial anchors, decision priorities, and tendency weights.
        /// </summary>
        public PlayerRole Role;

        // ── Spatial ───────────────────────────────────────────────────────────

        /// <summary>
        /// World position on pitch in engine coordinates (0,0 = top-left corner).
        /// Pitch is 1050 × 680 units matching standard 105m × 68m scaled to int pixels.
        /// Updated every tick by MovementSystem.
        /// </summary>
        public Vec2 Position;

        /// <summary>
        /// The position MovementSystem is steering this player toward this tick.
        /// Set by PlayerAI or DecisionSystem each decision cycle.
        /// MovementSystem reads this and moves Position closer each tick.
        /// </summary>
        public Vec2 TargetPosition;

        /// <summary>
        /// Normalised base position from FormationData for this player's role slot.
        /// Set once at match start. Never modified during the match.
        /// Used as fallback target when player has no higher-priority instruction.
        /// </summary>
        public Vec2 FormationAnchor;

        /// <summary>
        /// Current movement delta applied to Position each tick.
        /// Vec2(0,0) when standing still.
        /// Magnitude is clamped to Speed by MovementSystem.
        /// </summary>
        public Vec2 Velocity;

        // ── Physical ──────────────────────────────────────────────────────────

        /// <summary>
        /// Effective speed this tick in units-per-tick.
        /// Derived from player attributes × StaminaSpeedCurve(Stamina).
        /// MovementSystem reads this, does NOT write it — written by PlayerAI.
        /// </summary>
        public float Speed;

        /// <summary>
        /// Stamina level 0.0 (exhausted) – 1.0 (fully fresh).
        /// Decays when IsSprinting = true, recovers slowly when walking.
        /// Affects Speed and press trigger willingness.
        /// </summary>
        public float Stamina;

        /// <summary>
        /// True when player is in a sprint (chasing loose ball, making run, pressing).
        /// Triggers higher stamina drain rate in MovementSystem.
        /// </summary>
        public bool IsSprinting;

        // ── Ball Relationship ─────────────────────────────────────────────────

        /// <summary>
        /// True when BallState.OwnerId == this.PlayerId and BallState.Phase == OWNED.
        /// Written by BallSystem only. Other systems read this.
        /// </summary>
        public bool HasBall;

        // ── Decision State ────────────────────────────────────────────────────

        /// <summary>
        /// The action this player is executing this tick.
        /// Read by MovementSystem and BallSystem to know what to simulate.
        /// Written by PlayerAI/DecisionSystem.
        /// </summary>
        public PlayerAction Action;

        /// <summary>
        /// Ticks remaining before PlayerAI runs a full re-evaluation for this player.
        /// Decremented each tick in TickSystem. When 0, PlayerAI re-evaluates and resets this.
        /// Range: 0 – DECISION_INTERVAL (defined in TickSystem, default 3).
        /// </summary>
        public int DecisionCooldown;

        // ── Match Status ──────────────────────────────────────────────────────

        /// <summary>
        /// False when player is removed from active simulation (red card, injury).
        /// All systems skip this player when IsActive = false.
        /// </summary>
        public bool IsActive;

        // ── Attributes (read-only during match, set at match start) ───────────

        /// <summary>
        /// Base movement speed before stamina curve is applied. Units per tick.
        /// Loaded from player data at match start. Never changes mid-match.
        /// </summary>
        public float BaseSpeed;

        /// <summary>
        /// Passing skill 0.0–1.0. Used by DecisionSystem to score pass options.
        /// </summary>
        public float PassingAbility;

        /// <summary>
        /// Shooting skill 0.0–1.0. Used by DecisionSystem for xG calculation.
        /// </summary>
        public float ShootingAbility;

        /// <summary>
        /// Dribbling skill 0.0–1.0. Used by DecisionSystem to score dribble option.
        /// </summary>
        public float DribblingAbility;

        /// <summary>
        /// Defending skill 0.0–1.0. Used by CollisionSystem for tackle contests.
        /// </summary>
        public float DefendingAbility;

        /// <summary>
        /// Reaction speed 0.0–1.0. Used by CollisionSystem for intercept contests.
        /// </summary>
        public float Reactions;

        /// <summary>
        /// Ego modifier 0.0–1.0. High ego biases DecisionSystem toward shooting over passing.
        /// Drives visible behaviour: high-ego ST shoots from bad positions.
        /// </summary>
        public float Ego;

        /// <summary>
        /// Stamina attribute 0.0–1.0. Controls how fast stamina decays and recovers.
        /// Higher = slower decay, faster recovery.
        /// </summary>
        public float StaminaAttribute;
    }
}