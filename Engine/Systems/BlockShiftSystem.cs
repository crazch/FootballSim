// =============================================================================
// Module:  BlockShiftSystem.cs (DISABLED)
// Path:    FootballSim/Engine/Systems/BlockShiftSystem.cs
// Purpose:
//   Computes and stores the per-team defensive shape shift offset each tick.
//   Called once per tick by MatchEngine BEFORE DecisionSystem evaluates targets.
//   Writes results into ctx.HomeShiftX, ctx.AwayShiftX, ctx.HomeDefLineY,
//   ctx.AwayDefLineY — four floats that DecisionSystem reads when computing
//   ComputeDefensiveAnchor() and ComputeMarkSpaceTarget().
//
//   CONCEPT — The Rigid Template Model:
//     The team's defensive shape is a rigid template (11 slot positions relative
//     to each other). The template slides along the X axis toward the ball.
//     Each player's TargetPosition = their formation slot position inside the
//     shifted template. The shape is PRESERVED — only its centre of gravity moves.
//
//     ShiftX = how far the shape's centre has moved from pitch centre (X=525).
//     Positive = shape has shifted right. Negative = left.
//     Each player's slotOffsetX (FormationAnchor.X − 525) is kept constant.
//     TargetX = 525 + ShiftX + slotOffsetX
//     TargetX is clamped so extreme shift doesn't push players off pitch.
//
//   THREE SHIFT BEHAVIOURS mapped from TacticsInput:
//     Park the Bus  (OutOfPossessionShape > 0.8, PressingIntensity < 0.3):
//       Minimal X shift (≤ 60 units). Shape stays central. Concedes wide space
//       deliberately to maintain a compact central block.
//
//     Mid Block     (OutOfPossessionShape 0.4–0.8):
//       Moderate shift (up to 120 units). Tracks ball but maintains width.
//       Opposite side stays within 80 units of centre.
//
//     High Press / Gegenpress  (PressingIntensity > 0.7):
//       Aggressive shift (up to 180 units). Whole unit chases ball aggressively.
//       Opposite side exposure is accepted — pressing is the priority.
//
//   OPPOSITE SIDE PROTECTION:
//     When ball is on the right, the shape shifts right. But the leftmost slot
//     in the template is clamped so it never goes below MIN_LEFT_COVERAGE_X.
//     This prevents the catastrophic failure case (all on right, left empty).
//     The clamp is implicit via MAX_SHIFT_X per behaviour tier.
//
//   DEFENSIVE LINE Y:
//     Separate from X shift. Computed from DefensiveLine tactic slider.
//     High line = defenders push toward halfway. Deep line = sit in own half.
//     Y shift is used by DecisionSystem for all out-of-possession Y targets.
//
//   SMOOTHING:
//     Raw shift is lerped toward target each tick.
//     SHIFT_SMOOTH_FACTOR controls how fast the shape slides.
//     High factor = instant (unnatural). Low factor = laggy response.
//     Default 0.08 = shape takes ~12 ticks (1.2 seconds) to fully reposition.
//
// API:
//   BlockShiftSystem.Tick(MatchContext ctx) → void
//     Updates ctx.HomeShiftX, ctx.AwayShiftX,
//             ctx.HomeDefLineY, ctx.AwayDefLineY.
//     Called ONCE per tick by MatchEngine after MovementSystem,
//     BEFORE DecisionSystem (PlayerAI).
//
//   BlockShiftSystem.ComputeShiftedTarget(
//       ref PlayerState player, MatchContext ctx) → Vec2
//     Returns the shifted slot position for one player. Called by
//     DecisionSystem.ComputeDefensiveAnchor() and ComputeMarkSpaceTarget().
//
//   BlockShiftSystem.ComputeOffsideLineY(int teamId, MatchContext ctx) → float
//     Returns the Y coordinate of the last defender line for a team.
//     Used by the visualisation layer (OffsideLine overlay) and by
//     DecisionSystem for striker positioning (fix 2 — striker between CBs).
//
// Tick order (locked):
//   1. MovementSystem     — positions updated
//   2. BlockShiftSystem   ← HERE: shift offsets computed, stored in ctx
//   3. PlayerAI           — reads shift offsets via ComputeShiftedTarget()
//   4. BallSystem         — ball physics
//   5. CollisionSystem    — contests
//   6. EventSystem        — events
//
// Dependencies:
//   Engine/Models/MatchContext.cs   (reads Ball.Position, Players[], Tactics)
//   Engine/Models/PlayerState.cs
//   Engine/Models/Enums.cs          (PlayerRole)
//   Engine/Systems/PhysicsConstants.cs
//   Engine/Systems/AIConstants.cs
//   Engine/Math/MathUtil.cs
//
// Notes:
//   • BlockShiftSystem NEVER writes to PlayerState.Position or .TargetPosition.
//     It only writes to ctx shift fields. DecisionSystem reads those fields when
//     it computes TargetPosition. MovementSystem then moves players toward that
//     TargetPosition over subsequent ticks. The shift is therefore automatically
//     smoothed by MovementSystem's acceleration limits.
//   • The additional ctx fields (HomeShiftX, AwayShiftX, HomeDefLineY, AwayDefLineY)
//     must be added to MatchContext.cs.
//   • GK and SK are excluded from the shift — they always stay on goal line.
//   • Players with ball are excluded — DecisionSystem handles them separately.
//
// Reason to disable:
// - Persistent bugs that made both teams push forward, hard to find bugs
// - It is a second-order refinement on top of a first-order system that is not working yet
// - It corrputs the input that every other system reads
// Status:
// BlockShiftSystem Tick is now a no-op
// ComputeShiftedTarget returns player.FormationAnchor
// ComputeOffsideLineY returns the half-pitch baseline
// =============================================================================

using System;
using FootballSim.Engine.Models;
using FootballSim.Engine.Tactics;

namespace FootballSim.Engine.Systems
{
    /// <summary>
    /// Computes the team-wide defensive shape shift each tick.
    /// Writes shift offsets into MatchContext for DecisionSystem to consume.
    /// </summary>
    public static class BlockShiftSystem
    {
        // ── DEBUG ─────────────────────────────────────────────────────────────

        /// <summary>
        /// When true, logs the computed shift X and defensive line Y for both teams
        /// every SHIFT_DEBUG_INTERVAL ticks. Useful for checking shift responsiveness.
        /// </summary>
        public static bool DEBUG = false;
        private const int SHIFT_DEBUG_INTERVAL = 300; // every 30 seconds match time

        // ── Shift smoothing ───────────────────────────────────────────────────

        /// <summary>
        /// How fast the shift lerps toward the target position per tick.
        /// 0.08 = takes ~12 ticks (1.2 seconds) to close 90% of the gap.
        /// Increase for snappier response. Decrease for more inertia (realistic fatigue).
        /// </summary>
        private const float SHIFT_SMOOTH_FACTOR = 0.12f;

        // ── Shift magnitude limits per behaviour tier ─────────────────────────

        /// <summary>Max X shift for Park the Bus teams. 60 units = 6m from centre.</summary>
        private const float MAX_SHIFT_PARK_BUS = 60f;

        /// <summary>Max X shift for Mid Block teams. 130 units = 13m from centre.</summary>
        private const float MAX_SHIFT_MID_BLOCK = 130f;

        /// <summary>Max X shift for High Press teams. 200 units = 20m from centre.</summary>
        private const float MAX_SHIFT_HIGH_PRESS = 150;

        /// <summary>
        /// Absolute maximum shift regardless of tier. Prevents edge players from
        /// going off pitch even under aggressive shift. 220 units = pitch has 525
        /// centre, widest player slot is ~180 from centre, so 525-180-220 = 125 margin.
        /// </summary>
        private const float SHIFT_ABSOLUTE_MAX = 220f;

        // ── Defensive line Y constants ────────────────────────────────────────
        // These are pitch-fraction values, multiplied by PITCH_HEIGHT in computation.
        // Home attacks down (toward Y=680). Away attacks up (toward Y=0).
        // "Deep" for home = high Y (near Y=680). "High" for home = low Y (near half).

        /// <summary>
        /// Deepest defensive line Y fraction for home team. 0.75 × 680 = 510.
        /// Home defenders sit at Y=510 when DefensiveLine=0 (deepest).
        /// </summary>
        private const float HOME_DEF_LINE_DEEP = 0.82f;

        /// <summary>
        /// Highest defensive line Y fraction for home team. 0.38 × 680 = 258.
        /// Home defenders sit at Y=258 when DefensiveLine=1 (highest line).
        /// </summary>
        private const float HOME_DEF_LINE_HIGH = 0.45f;

        // Away is the mirror: deep = 0.25 × 680 = 170. High = 0.62 × 680 = 422.
        private const float AWAY_DEF_LINE_DEEP = 0.18f;
        private const float AWAY_DEF_LINE_HIGH = 0.55f;

        // =====================================================================
        // MAIN TICK
        // =====================================================================

        /// <summary>
        /// Main update. Called once per tick by MatchEngine, after MovementSystem,
        /// before PlayerAI. Updates all four shift fields in ctx.
        /// </summary>
        public static void Tick(MatchContext ctx)
        {
            // BlockShiftSystem disabled: do not modify context shift values.
            // This method intentionally does nothing so formation anchors remain
            // the single source of truth for player targets.
            return;
        }

        // =====================================================================
        // PUBLIC — used by DecisionSystem and overlay
        // =====================================================================

        /// <summary>
        /// Computes the shifted defensive slot position for one player.
        /// Called by DecisionSystem.ComputeDefensiveAnchor() and
        /// DecisionSystem.ComputeMarkSpaceTarget() instead of raw FormationAnchor.
        ///
        /// The shift is applied to the X axis only.
        /// Y is determined by the defensive line height (DefLineY from ctx).
        /// GK and SK are excluded — they return their goal-line anchor unchanged.
        ///
        /// Formula:
        ///   slotOffsetX = FormationAnchor.X − pitchCentreX
        ///   shiftedX    = pitchCentreX + shiftX + slotOffsetX
        ///   shiftedX    = clamped to [MIN_X_MARGIN, PITCH_WIDTH − MIN_X_MARGIN]
        ///   shiftedY    = defensive line Y for this team
        /// </summary>
        public static Vec2 ComputeShiftedTarget(ref PlayerState player, MatchContext ctx)
        {
            // Return the raw formation anchor unchanged so DecisionSystem uses
            // the original formation positions (BlockShift disabled).
            return player.FormationAnchor;
        }

        /// <summary>
        /// Returns the Y coordinate of the offside line for a team.
        /// Offside line = Y of the last outfield defender (not GK).
        /// "Last" means furthest from own goal = closest to halfway/opponent.
        ///
        /// Home attacks down (Y=680): last defender = smallest Y among defenders.
        /// Away attacks up  (Y=0):    last defender = largest Y among defenders.
        ///
        /// Used by:
        ///   - Offside line GDScript overlay (visual dashed line)
        ///   - DecisionSystem striker positioning (fix 2)
        /// </summary>
        public static float ComputeOffsideLineY(int teamId, MatchContext ctx)
        {
            // BlockShift disabled — return a stable, sensible offside reference.
            // Use the halfway line as a neutral offside baseline so striker logic
            // and overlays have a predictable value.
            return PhysicsConstants.PITCH_HEIGHT * 0.5f;
        }

        // =====================================================================
        // PRIVATE — shift computation helpers
        // =====================================================================

        /// <summary>
        /// Computes the target X shift for the defensive shape based on ball position
        /// and tactical settings. Returns signed offset from pitch centre (positive = right).
        /// </summary>
        private static float ComputeTargetShiftX(Vec2 ballPos, TacticsInput tactics,
                                                   float pitchCentreX)
        {
            // Raw ball offset from centre: positive = ball is on right
            float ballOffsetX = ballPos.X - pitchCentreX;

            // Determine shift tier from tactics
            float maxShift = ComputeMaxShiftForTactics(tactics);

            // Shift intensity: how strongly this team tracks the ball
            // OutOfPossessionShape: higher = more compact = LESS shift (stay central)
            // PressingIntensity:    higher = more aggressive = MORE shift
            float shiftIntensity = ComputeShiftIntensity(tactics);

            // Target shift = ball offset × intensity, clamped to tier max
            float targetShift = ballOffsetX * shiftIntensity;
            targetShift = Math.Clamp(targetShift, -maxShift, maxShift);
            targetShift = Math.Clamp(targetShift, -SHIFT_ABSOLUTE_MAX, SHIFT_ABSOLUTE_MAX);

            return targetShift;
        }

        /// <summary>
        /// Returns the maximum shift magnitude allowed for the given tactics.
        /// Determined by which of the three shift tiers the tactics map to.
        /// </summary>
        private static float ComputeMaxShiftForTactics(TacticsInput tactics)
        {
            bool isParkTheBus = tactics.OutOfPossessionShape > 0.75f
                             && tactics.PressingIntensity < 0.35f;

            bool isHighPress = tactics.PressingIntensity > 0.65f;

            if (isParkTheBus) return MAX_SHIFT_PARK_BUS;
            if (isHighPress) return MAX_SHIFT_HIGH_PRESS;
            return MAX_SHIFT_MID_BLOCK;
        }

        /// <summary>
        /// Returns a 0–1 multiplier for how aggressively this team tracks ball X.
        /// Higher = shape moves more per unit of ball X offset.
        ///
        /// Pressing intensity pulls the shape toward the ball (aggressive tracking).
        /// OutOfPossessionShape compactness resists the pull (stay central).
        /// Net intensity = pressing pull minus compactness resistance, clamped to [0.1, 0.8].
        ///
        /// 0.1 = shape barely moves (park the bus: stay compact central)
        /// 0.8 = shape moves 80% of the ball's offset (high press: chase the ball)
        /// The fraction of 0.8 not 1.0 ensures the opposite side is never fully empty.
        /// </summary>
        private static float ComputeShiftIntensity(TacticsInput tactics)
        {
            float pressPull = tactics.PressingIntensity * 0.5f;          // 0–0.5
            float compactResist = tactics.OutOfPossessionShape * 0.3f;       // 0–0.3
            float intensity = pressPull - compactResist + 0.15f;         // bias above zero
            return Math.Clamp(intensity, 0.10f, 0.80f);
        }

        /// <summary>
        /// Computes the defensive line Y coordinate from the DefensiveLine tactic slider.
        /// This is the Y that the deepest defensive unit targets.
        /// Home team: deep = Y close to 680 (own goal). High = Y close to 340 (halfway).
        /// Away team: deep = Y close to 0 (own goal). High = Y close to 340.
        /// </summary>
        private static float ComputeDefLineY(TacticsInput tactics, bool isHome)
        {
            float pitchH = PhysicsConstants.PITCH_HEIGHT;
            float t = tactics.DefensiveLine; // 0 = deep, 1 = high

            float lineYNorm = isHome
                ? MathUtil.Lerp(HOME_DEF_LINE_DEEP, HOME_DEF_LINE_HIGH, t)
                : MathUtil.Lerp(AWAY_DEF_LINE_DEEP, AWAY_DEF_LINE_HIGH, t);

            return lineYNorm * pitchH;
        }

        /// <summary>
        /// Returns a Y offset from the defensive line anchor for each role.
        /// This preserves vertical structure within the defensive block.
        /// Positive = further from own goal (more advanced).
        /// Negative = closer to own goal (deeper).
        ///
        /// Home attacks down: "further from own goal" = smaller Y (closer to top).
        ///   So for home: positive offset = negative Y offset in world.
        /// Away attacks up: "further from own goal" = larger Y.
        ///   So for away: positive offset = positive Y offset in world.
        ///
        /// Values are in engine units. The defensive line Y is for the CB line.
        /// Everyone else is offset relative to that.
        /// </summary>
        private static float ComputeRoleYOffset(PlayerRole role, bool attacksDown)
        {
            // Positive = more advanced (further from own goal).
            // We apply sign flip based on attacking direction.
            float advanceAmount;

            switch (role)
            {
                // Deepest — sit level with or just behind the CB line
                case PlayerRole.CB:
                case PlayerRole.BPD:
                    advanceAmount = 0f;
                    break;

                // Full backs / wing backs — level with CBs
                case PlayerRole.RB:
                case PlayerRole.LB:
                case PlayerRole.WB:
                    advanceAmount = 0f;
                    break;

                // Defensive midfielders — one line ahead of CBs
                case PlayerRole.CDM:
                case PlayerRole.DLP:
                    advanceAmount = -60f; // 60 units ahead of CB line
                    break;

                // Central midfielders — two lines ahead
                case PlayerRole.CM:
                case PlayerRole.BBM:
                    advanceAmount = -110f;
                    break;

                // Wide and attacking players — track deeper when out of possession
                case PlayerRole.AM:
                case PlayerRole.IW:
                case PlayerRole.WF:
                    advanceAmount = -150f;
                    break;

                // Forwards — sit high to provide outlet, but not in opponent box
                case PlayerRole.CF:
                case PlayerRole.ST:
                case PlayerRole.PF:
                    advanceAmount = -200f; // furthest from own goal when defending
                    break;

                default:
                    advanceAmount = -80f;
                    break;
            }

            // Home attacks down → more advanced = lower Y (negative world Y offset)
            // Away attacks up   → more advanced = higher Y (positive world Y offset)
            return attacksDown ? advanceAmount : -advanceAmount;
        }

        /// <summary>
        /// Returns true if this role is part of the defensive unit that sets the
        /// offside line. Midfielders and forwards do NOT set the offside line.
        /// </summary>
        private static bool IsDefenderRole(PlayerRole role)
        {
            switch (role)
            {
                case PlayerRole.CB:
                case PlayerRole.BPD:
                case PlayerRole.RB:
                case PlayerRole.LB:
                case PlayerRole.WB:
                case PlayerRole.CDM:  // CDM often serves as the last player in high press
                case PlayerRole.DLP:
                    return true;
                default:
                    return false;
            }
        }
    }
}