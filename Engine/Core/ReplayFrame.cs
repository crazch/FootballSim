// =============================================================================
// Module:  ReplayFrame.cs
// Path:    FootballSim/Engine/Core/ReplayFrame.cs
// Purpose:
//   Immutable snapshot of the simulation state at the end of one tick.
//   Contains the minimum data needed for Godot's ReplayPlayer.gd to:
//     • Move 22 player dots to correct positions
//     • Move the ball dot
//     • Set dot visual state (HasBall glow, stamina colour, action indicator)
//     • Display events in EventLogUI
//
//   What ReplayFrame stores (value copies, NO live object references):
//     • Tick index and match time
//     • Ball: position (Vec2), phase (BallPhase), height (float), ownerId (int)
//     • 22 players: position, stamina, HasBall, Action, IsActive
//       (NOT: velocity, TargetPosition, FormationAnchor — not needed for playback)
//     • Events: shallow copy of EventsThisTick (MatchEvent is a class but
//       MatchEvent instances are never mutated after emission — safe to reference)
//     • Score snapshot: HomeScore, AwayScore at this tick
//
//   What ReplayFrame does NOT store:
//     • Player velocity or TargetPosition (not needed for dot movement)
//     • BallState.Velocity (playback lerps between frames; velocity implicit)
//     • Full MatchContext (would duplicate 90% of data)
//     • Anything requiring Godot types
//
// Memory budget:
//   Per frame:
//     Timing:          8 bytes  (tick int + matchSecond float)
//     Ball:           20 bytes  (Vec2 8B + phase 4B + height 4B + ownerId 4B)
//     22 × PlayerSnap: 22 × (Vec2 8B + float 4B + byte 2B + byte 2B) = 22 × 16 = 352 bytes
//     Score:           8 bytes  (2 ints)
//     Events ref:      8 bytes  (list pointer — events shared, not copied)
//   Total per frame: ~400 bytes
//   54,000 frames × 400 bytes ≈ 21.6 MB per match — within budget.
//
// Dependencies:
//   Engine/Models/Vec2.cs
//   Engine/Models/Enums.cs   (BallPhase, PlayerAction)
//   Engine/Models/MatchEvent.cs
//
// Notes:
//   • ReplayFrame is a CLASS, not a struct, because it is stored in a List<>.
//     Boxing a 400-byte struct 54,000 times would be expensive. Class reference is 8B.
//   • PlayerSnap IS a struct (value type) stored inline in a fixed-size array.
//   • Events list is a snapshot copy (new List from EventsThisTick) NOT a reference
//     to the live ctx.EventsThisTick — that list is cleared each tick.
//   • MatchReplay.Record(ctx) is the only caller of the ReplayFrame constructor.
// =============================================================================

using System.Collections.Generic;
using FootballSim.Engine.Models;
using FootballSim.Engine.Systems;

namespace FootballSim.Engine.Core
{
    // =========================================================================
    // PLAYER SNAPSHOT — per-player data needed for one frame of playback
    // =========================================================================

    /// <summary>
    /// Minimal per-player snapshot for one replay frame.
    /// Struct — stored inline in ReplayFrame.Players[22].
    /// Fields chosen to be the minimum needed to render one player dot.
    /// </summary>
    public struct PlayerSnap
    {
        /// <summary>World position this tick. Copied from PlayerState.Position.</summary>
        public Vec2 Position;

        /// <summary>
        /// Stamina 0.0–1.0. Used by PlayerDot.gd to set dot colour saturation.
        /// Bright = fresh, faded = tired. This is what makes fatigue visible.
        /// </summary>
        public float Stamina;

        /// <summary>True when this player is the ball owner. PlayerDot.gd adds ring/glow.</summary>
        public bool HasBall;

        /// <summary>
        /// Current action. PlayerDot.gd uses this for directional indicators
        /// (e.g. arrow toward target when Pressing).
        /// </summary>
        public PlayerAction Action;

        /// <summary>False when red-carded or injured — dot hidden in visualisation.</summary>
        public bool IsActive;

        /// <summary>
        /// True when this player is sprinting. PlayerDot.gd can show sprint shimmer.
        /// </summary>
        public bool IsSprinting;
    }

    // =========================================================================
    // BALL SNAPSHOT — ball state for one replay frame
    // =========================================================================

    /// <summary>
    /// Minimal ball state for one replay frame.
    /// Struct — stored inline in ReplayFrame.
    /// </summary>
    public struct BallSnap
    {
        /// <summary>World position of the ball. Copied from BallState.Position.</summary>
        public Vec2 Position;

        /// <summary>
        /// Ball phase. ReplayPlayer.gd uses this to choose rendering style:
        ///   Owned     → dot attached near player dot
        ///   InFlight  → draw trajectory trail
        ///   Loose     → rolling free dot, slightly different appearance
        /// </summary>
        public BallPhase Phase;

        /// <summary>
        /// Ball height 0–1. BallDot.gd scales the dot size slightly for aerial balls.
        /// Also used to determine if trajectory trail arcs upward.
        /// </summary>
        public float Height;

        /// <summary>
        /// OwnerId (-1 = no owner). BallDot.gd can show the ball near the correct player.
        /// </summary>
        public int OwnerId;

        /// <summary>True when ball is heading toward goal. BallDot.gd may add shot flash.</summary>
        public bool IsShot;
    }

    // =========================================================================
    // REPLAY FRAME
    // =========================================================================

    /// <summary>
    /// Immutable snapshot of one simulation tick, ready for Godot playback.
    /// Created by MatchReplay.Record(ctx). Never mutated after creation.
    /// </summary>
    public class ReplayFrame
    {
        // ── Timing ────────────────────────────────────────────────────────────

        /// <summary>Tick index when this frame was recorded. Range 0–54000.</summary>
        public int Tick;

        /// <summary>Match time in seconds. = Tick × 0.1. Pre-calculated for display.</summary>
        public float MatchSecond;

        /// <summary>Match minute for EventLog display.</summary>
        public int MatchMinute;

        // ── Ball ──────────────────────────────────────────────────────────────

        /// <summary>Ball state snapshot. Value copy — safe to read after context clears.</summary>
        public BallSnap Ball;

        // ── Players ───────────────────────────────────────────────────────────

        /// <summary>
        /// Exactly 22 player snapshots. Index = PlayerId.
        /// Players[0..10] = home. Players[11..21] = away.
        /// Value copies — safe after context mutates.
        /// </summary>
        public PlayerSnap[] Players;  // always length 22

        // ── Score ─────────────────────────────────────────────────────────────

        /// <summary>Home score at the end of this tick.</summary>
        public int HomeScore;

        /// <summary>Away score at the end of this tick.</summary>
        public int AwayScore;

        // ── Offside / Defensive line ──────────────────────────────────────────

        /// <summary>
        /// Y coordinate of the home team's offside line this tick.
        /// = Y of the most advanced home defender (LB, RB, CB, CDM).
        /// Home attacks down (Y=680): smaller Y = higher line = more advanced.
        /// Used by DefensiveLineOverlay.gd for the accurate offside line visual.
        /// </summary>
        public float HomeOffsideY;

        /// <summary>
        /// Y coordinate of the away team's offside line this tick.
        /// Away attacks up (Y=0): larger Y = higher line = more advanced.
        /// </summary>
        public float AwayOffsideY;

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>
        /// Events emitted during this tick. Shallow copy of ctx.EventsThisTick.
        /// MatchEvent objects are never mutated after emission — reference copy is safe.
        /// Empty list for most ticks (0–3 events typical). Null if no events this tick.
        /// ReplayPlayer.gd fires EventBus signals from these entries.
        /// </summary>
        public List<MatchEvent> Events;

        // ── Match Phase ───────────────────────────────────────────────────────

        /// <summary>
        /// Match phase at this tick. ReplayPlayer.gd uses this to handle
        /// HalfTime/GoalScored pauses and FullTime stop.
        /// </summary>
        public MatchPhase Phase;

        // ── Constructor ───────────────────────────────────────────────────────

        /// <summary>
        /// Creates a ReplayFrame by copying the necessary fields from MatchContext.
        /// Called only by MatchReplay.Record(ctx).
        /// </summary>
        public static ReplayFrame Capture(MatchContext ctx)
        {
            var frame = new ReplayFrame
            {
                Tick = ctx.Tick,
                MatchSecond = ctx.MatchSecond,
                MatchMinute = ctx.MatchMinute,
                HomeScore = ctx.HomeScore,
                AwayScore = ctx.AwayScore,
                Phase = ctx.Phase,
                HomeOffsideY = BlockShiftSystem.ComputeOffsideLineY(0, ctx),
                AwayOffsideY = BlockShiftSystem.ComputeOffsideLineY(1, ctx),
            };

            // Ball snapshot — value copy
            frame.Ball = new BallSnap
            {
                Position = ctx.Ball.Position,  // Vec2 is a value type — copies cleanly
                Phase = ctx.Ball.Phase,
                Height = ctx.Ball.Height,
                OwnerId = ctx.Ball.OwnerId,
                IsShot = ctx.Ball.IsShot,
            };

            // Player snapshots — 22 value copies
            frame.Players = new PlayerSnap[22];
            for (int i = 0; i < 22; i++)
            {
                frame.Players[i] = new PlayerSnap
                {
                    Position = ctx.Players[i].Position,  // Vec2 value copy
                    Stamina = ctx.Players[i].Stamina,
                    HasBall = ctx.Players[i].HasBall,
                    Action = ctx.Players[i].Action,
                    IsActive = ctx.Players[i].IsActive,
                    IsSprinting = ctx.Players[i].IsSprinting,
                };
            }

            // Events — shallow copy of list (MatchEvent objects are immutable after emission)
            // Use null for empty tick to save allocation on the 99% of ticks with no events
            if (ctx.EventsThisTick.Count > 0)
            {
                frame.Events = new List<MatchEvent>(ctx.EventsThisTick);
            }
            else
            {
                frame.Events = null;
            }

            return frame;
        }
    }
}