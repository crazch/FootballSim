// =============================================================================
// Module:  TickLog.cs
// Path:    FootballSim/Engine/Debug/TickLog.cs
//
// Purpose:
//   Pure data structures that DebugLogger captures each tick.
//   No logic, no Godot dependency, no production dependency.
//
//   TickLog        — one per tick, contains all players + ball + events
//   PlayerTickLog  — one per active player per tick
//   BallTickLog    — ball state snapshot per tick
//   ScoreBreakdown — all DecisionSystem scores for one player one tick
//   ShootDetail    — why xG was what it was (distance, threshold, blockers)
//   EventTickLog   — lightweight event record (mirrors MatchEvent fields needed)
// =============================================================================

using System.Collections.Generic;
using FootballSim.Engine.Models;

namespace FootballSim.Engine.Debug
{
    // =========================================================================
    // TICK LOG — root container, one per tick
    // =========================================================================

    /// <summary>
    /// Complete debug snapshot of one tick.
    /// DebugLogger appends one of these per tick during Run().
    /// DebugLogQuery reads the list after Run() completes.
    /// </summary>
    public sealed class TickLog
    {
        public int   Tick;
        public float MatchSecond;
        public int   MatchMinute;
        public int   HomeScore;
        public int   AwayScore;
        public MatchPhase Phase;

        /// <summary>One entry per active player (IsActive=true).</summary>
        public List<PlayerTickLog> Players = new List<PlayerTickLog>(22);

        public BallTickLog Ball = new BallTickLog();

        /// <summary>Events emitted by EventSystem this tick.</summary>
        public List<EventTickLog> Events = new List<EventTickLog>(4);
    }

    // =========================================================================
    // PLAYER TICK LOG
    // =========================================================================

    /// <summary>
    /// Everything about one player's state and decision for one tick.
    /// Captures both the outcome (ActionChosen) and the reasoning (Scores).
    /// </summary>
    public sealed class PlayerTickLog
    {
        // ── Identity ──────────────────────────────────────────────────────────
        public int        PlayerId;
        public int        TeamId;
        public int        ShirtNumber;
        public PlayerRole Role;

        // ── Position and movement ─────────────────────────────────────────────
        public Vec2  Position;
        public Vec2  TargetPosition;
        public Vec2  Velocity;
        public float Speed;
        public bool  IsSprinting;

        // ── Stamina ───────────────────────────────────────────────────────────
        public float Stamina;           // 0.0–1.0

        // ── Ball relationship ─────────────────────────────────────────────────
        public bool HasBall;
        public bool IsInOpponentPenaltyBox;
        public bool IsInOwnPenaltyBox;
        public float DistToOpponentGoal;
        public float DistToBall;
        public float DistToNearestOpponent;

        // ── Decision ──────────────────────────────────────────────────────────
        public PlayerAction ActionChosen;
        public int          PassReceiverId;     // -1 if not a pass
        public Vec2         ShotTargetPos;      // only set when shooting
        public int          DecisionCooldown;   // ticks remaining before re-eval
        public bool         WasReEvaluatedThisTick; // false = cooldown tick

        // ── Score breakdown ───────────────────────────────────────────────────
        /// <summary>
        /// Populated when WasReEvaluatedThisTick = true AND the player had
        /// the ball (EvaluateWithBall was called).
        /// Null on cooldown ticks or out-of-possession evaluations.
        /// </summary>
        public ScoreBreakdown? Scores;

        // ── Out-of-possession context ─────────────────────────────────────────
        /// <summary>
        /// Populated when WasReEvaluatedThisTick = true AND the player did
        /// NOT have the ball (EvaluateOutOfPossession / EvaluateWithoutBall).
        /// </summary>
        public DefensiveContext? Defensive;
    }

    // =========================================================================
    // SCORE BREAKDOWN — with-ball decision scores
    // =========================================================================

    /// <summary>
    /// All scores DecisionSystem computed when EvaluateWithBall ran.
    /// Populated from the score fields added to ActionPlan.
    /// This is the primary data for LLM-assisted debugging:
    ///   "Why did the striker pass instead of shoot?"
    ///   → ScoreShoot=0.00 because xG=0.02 < threshold=0.10
    /// </summary>
    public sealed class ScoreBreakdown
    {
        // ── Raw scores (0.0–1.0, higher = more likely to be chosen) ──────────
        public float ScoreShoot;
        public float ScorePass;
        public float ScoreDribble;
        public float ScoreCross;
        public float ScoreHold;

        /// <summary>The winning score — matches ActionPlan.Score.</summary>
        public float WinningScore;

        // ── Shoot detail — why was xG what it was? ────────────────────────────
        public ShootDetail Shoot;

        // ── Pass detail — best receiver and why ──────────────────────────────
        public PassDetail Pass;
    }

    /// <summary>
    /// Why the shot xG was computed as it was.
    /// The most important debug data for "player won't shoot" bugs.
    /// </summary>
    public sealed class ShootDetail
    {
        public float XG;                    // computed xG (0.0–1.0)
        public float EffectiveThreshold;    // xG must exceed this to shoot
        public float DistToGoal;            // pitch units
        public float AngleFactor;           // 0.5–1.0, lateral offset penalty
        public int   BlockersInLane;        // defenders counted in shooting lane
        public bool  ThresholdBlocked;      // true = xG < threshold → no shoot
        public bool  RangeBlocked;          // true = dist > SHOOT_MAX_RANGE
    }

    /// <summary>Best pass candidate detail.</summary>
    public sealed class PassDetail
    {
        public int   BestReceiverId;        // -1 if no pass found
        public float BestReceiverScore;
        public float ReceiverPressure;      // 0.0–1.0 (normalised)
        public bool  IsProgressive;         // receiver is further toward opp goal
        public float PassDistance;
    }

    // =========================================================================
    // DEFENSIVE CONTEXT — out-of-possession state
    // =========================================================================

    /// <summary>
    /// Context when a player is out of possession.
    /// Captures press vs recover vs mark decision scores.
    /// </summary>
    public sealed class DefensiveContext
    {
        public float ScorePress;
        public float ScoreTrack;
        public float ScoreRecover;
        public float ScoreMarkSpace;
        public int   TrackTargetPlayerId;   // -1 if not tracking
        public float DistToCarrier;         // distance to ball carrier
        public float PressTriggerDist;      // the trigger distance this tick
    }

    // =========================================================================
    // BALL TICK LOG
    // =========================================================================

    public sealed class BallTickLog
    {
        public Vec2      Position;
        public Vec2      Velocity;
        public BallPhase Phase;
        public float     Height;
        public int       OwnerId;           // -1 = no owner
        public int       PassTargetId;      // -1 = not a pass
        public bool      IsShot;
        public bool      ShotOnTarget;
        public float     Speed;             // Velocity.Length()
        public int       LastTouchedBy;
        public int       LooseTicks;
    }

    // =========================================================================
    // EVENT TICK LOG
    // =========================================================================

    /// <summary>
    /// Lightweight mirror of MatchEvent — only the fields needed for debug logging.
    /// Avoids importing the full MatchEvent class into query/format code.
    /// </summary>
    public sealed class EventTickLog
    {
        public MatchEventType Type;
        public int            PrimaryPlayerId;
        public int            SecondaryPlayerId;
        public int            TeamId;
        public Vec2           Position;
        public float          ExtraFloat;    // xG, pass dist, foul severity etc.
        public bool           ExtraBool;
        public string         Description;  // pre-built human-readable string
    }
}
