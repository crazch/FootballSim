// =============================================================================
// Module:  DecisionSystem.Types.cs
// Path:    FootballSim/Engine/Systems/DecisionSystem/DecisionSystem.Types.cs
// Purpose:
//   Shared result structs and output types returned by DecisionSystem scoring
//   methods to PlayerAI. These are pure data contracts with no logic — they
//   exist to clearly define the interface between DecisionSystem and PlayerAI.
//
//   All types here are consumed by:
//     PlayerAI (reads ActionPlan to execute behaviour)
//     DecisionSystem.WithBall.cs (produces ShotScore, PassCandidate)
//     DecisionSystem.Defense.cs (produces PressScore, TrackScore)
//
// Types defined:
//   ActionPlan         — complete scored plan for one player's next action
//   PassCandidate      — score result for one specific pass receiver
//   ShotScore          — score result for a shot attempt
//   PressScore         — score result for pressing the ball carrier
//   TrackScore         — score result for tracking an opponent
//
// Notes:
//   • All types are value types (struct) — no heap allocation per decision cycle.
//   • ActionPlan carries optional breakdown fields (HasScoreBreakdown,
//     HasDefensiveContext) that debug tooling and replays can consume.
//   • Bug 3 fix: ShouldSprint lives here instead of mutating PlayerState.IsSprinting.
//     PlayerAI must apply plan.ShouldSprint after accepting the plan.
// =============================================================================

using FootballSim.Engine.Models;

namespace FootballSim.Engine.Systems
{
    // =========================================================================
    // RESULT TYPES — returned by scoring methods to PlayerAI
    // =========================================================================

    /// <summary>Complete scored plan for one player's next action this decision cycle.</summary>
    public struct ActionPlan
    {
        public PlayerAction Action;
        public Vec2 TargetPosition;   // Where MovementSystem should steer
        public int PassReceiverId;   // -1 if not a pass
        public Vec2 ShotTargetPos;    // goal aim point if shooting
        public float PassSpeed;        // units/tick for BallSystem.LaunchPass
        public float PassHeight;       // 0=ground, 0.5=driven, 1.0=lofted
        public float Score;            // winning utility score [0,1]
        public float XG;              // populated when Action==Shooting

        // ── NEW: with-ball score breakdown ────────────────────────────────────
        // Populated by EvaluateWithBall. Zero when player does not have ball.

        /// <summary>
        /// Bug 3 fix: IsSprinting is no longer set inside scoring methods.
        /// DecisionSystem sets this field; PlayerAI applies it after accepting the plan.
        /// This keeps DecisionSystem side-effect free.
        /// </summary>
        public bool ShouldSprint;

        /// <summary>True when score breakdown fields were populated this evaluation.</summary>
        public bool HasScoreBreakdown;
        public float ScoreShoot;
        public float ScorePass;
        public float ScoreDribble;
        public float ScoreCross;
        public float ScoreHold;

        // Shoot detail — why was xG what it was?
        public float ShootXG;   // computed xG at this position
        public float ShootEffectiveThreshold;   // xG must exceed this to shoot
        public int ShootBlockersInLane;        // defenders in shooting lane
        public float ShootDistToGoal;            // for quick reference

        // Pass detail — best receiver context
        public bool PassIsProgressive;          // receiver is further forward
        public float PassDistance;               // distance to best receiver
        public float PassReceiverPressure;       // pressure on best receiver [0-1]

        // ── NEW: out-of-possession score breakdown ────────────────────────────
        // Populated by EvaluateOutOfPossession. Zero when player has ball.
        /// <summary>True when defensive context fields were populated.</summary>

        public bool HasDefensiveContext;

        public float ScorePress;
        public float ScoreTrack;
        public float ScoreRecover;
        public float ScoreMarkSpace;
        public int TrackTargetPlayerId;     // -1 if not tracking
        public float DistToCarrier;     // distance to ball carrier
        public float PressTriggerDist;      // press trigger radius this tick
    }

    /// <summary>Score result for one specific pass receiver candidate.</summary>
    public struct PassCandidate
    {
        public int ReceiverId;
        public float Score;
        public float PassSpeed;
        public float PassHeight;

        // ── Breakdown fields (populated by ScorePass for ActionPlan) ──────────
        public bool IsProgressive;
        public float Distance;
        public float ReceiverPressure;
    }

    /// <summary>Score result for a shot attempt.</summary>
    public struct ShotScore
    {
        public float Score;
        public float XG;
        public Vec2 TargetPosition;  // aim point inside goal

        // ── Breakdown fields (populated by ScoreShoot for ActionPlan) ────────
        public float EffectiveThreshold;
        public int BlockersInLane;
        public float DistToGoal;
    }

    /// <summary>Score result for pressing the ball carrier.</summary>
    public struct PressScore
    {
        public Vec2 TargetPosition;
        public float Score;
        public float DistToCarrier;
        public float PressTriggerDist;
    }

    /// <summary>Score result for tracking an opponent.</summary>
    public struct TrackScore
    {
        public Vec2 TargetPosition;
        public float Score;
        public int TargetPlayerId;  // -1 if no target
    }
}
