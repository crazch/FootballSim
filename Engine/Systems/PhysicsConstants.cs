// =============================================================================
// Module:  PhysicsConstants.cs
// Path:    FootballSim/Engine/Systems/PhysicsConstants.cs
// Purpose:
//   Single source of truth for every numeric constant used by BallSystem
//   and MovementSystem. No constant is hardcoded inside a method — they all
//   live here so balancing, tuning, and debugging require changing one file.
//
//   All distance values are in engine pitch units (1 unit = 0.1 metres).
//   Pitch dimensions: 1050 wide × 680 tall.
//   Tick rate: 1 tick = 0.1 real seconds of match time.
//   => 1 unit/tick = 0.1 m per 0.1 s = 1 m/s in real football terms.
//
//   Reading guide (convert back to real football):
//     Speed 5.0 units/tick  = 5 m/s  = 18 km/h  (comfortable jog)
//     Speed 8.0 units/tick  = 8 m/s  = 28.8 km/h (fast run)
//     Speed 10.0 units/tick = 10 m/s = 36 km/h  (elite sprint, Mbappé range)
//     Ball pass speed 35    = 35 m/s = 126 km/h (hard driven pass)
//     Ball shot speed 55    = 55 m/s = 198 km/h (powerful shot)
//
// Dependencies: none
//
// Notes:
//   • All constants are static readonly float for zero runtime allocation.
//   • To tune the feel: start with PLAYER_SPRINT_SPEED and BALL_PASS_SPEED_MED.
//     Those two constants control the most visible behaviour.
//   • STAMINA_FULL_SPRINT_DRAIN and STAMINA_RECOVERY_WALK are the most important
//     balance values — they determine when the press collapses (minute 58 effect).
//   • BALL_ARRIVAL_THRESHOLD is the snap distance: how close the receiver must be
//     to the ball for BallSystem to transfer ownership. Too large = ball teleports.
//     Too small = receiver never picks it up. 8.0 units is reliable.
//   • PITCH_ constants define the playable boundary. BallSystem uses them for
//     out-of-play detection. Goal mouth dimensions are FIFA standard scaled to units.
// =============================================================================

namespace FootballSim.Engine.Systems
{
    /// <summary>
    /// All tunable constants for BallSystem and MovementSystem.
    /// Change values here — never hardcode numbers in system methods.
    /// </summary>
    public static class PhysicsConstants
    {
        // =====================================================================
        // PITCH GEOMETRY
        // All in engine units. 1 unit = 0.1m. Pitch = 105m × 68m.
        // =====================================================================

        /// <summary>Total pitch width in engine units. 105m = 1050 units.</summary>
        public static readonly float PITCH_WIDTH = 1050f;

        /// <summary>Total pitch height in engine units. 68m = 680 units.</summary>
        public static readonly float PITCH_HEIGHT = 680f;

        /// <summary>Left boundary X (touchline). Ball crossing this = out of play left side.</summary>
        public static readonly float PITCH_LEFT = 0f;

        /// <summary>Right boundary X (touchline).</summary>
        public static readonly float PITCH_RIGHT = 1050f;

        /// <summary>Top boundary Y (home goal end). Ball crossing Y < this = out over home byline.</summary>
        public static readonly float PITCH_TOP = 0f;

        /// <summary>Bottom boundary Y (away goal end). Ball crossing Y > this = out over away byline.</summary>
        public static readonly float PITCH_BOTTOM = 680f;

        /// <summary>
        /// Home goal mouth: left post X. Centre of pitch = 525.
        /// Goal width = 7.32m = 73.2 units. Half = 36.6 units.
        /// </summary>
        public static readonly float HOME_GOAL_LEFT_X  = 488.4f;  // 525 - 36.6
        public static readonly float HOME_GOAL_RIGHT_X = 561.6f;  // 525 + 36.6
        public static readonly float HOME_GOAL_LINE_Y  = 0f;      // same as PITCH_TOP

        /// <summary>Away goal mouth positions (bottom of pitch).</summary>
        public static readonly float AWAY_GOAL_LEFT_X  = 488.4f;
        public static readonly float AWAY_GOAL_RIGHT_X = 561.6f;
        public static readonly float AWAY_GOAL_LINE_Y  = 680f;    // same as PITCH_BOTTOM

        /// <summary>
        /// Penalty area depth from goal line. 16.5m = 165 units.
        /// Home penalty box: Y in [0, 165]. Away penalty box: Y in [515, 680].
        /// </summary>
        public static readonly float PENALTY_AREA_DEPTH = 165f;

        /// <summary>
        /// Penalty area half-width from centre. 40.32m total width = 20.16m each side = 201.6 units.
        /// Home penalty box X: [323.4, 726.6]. Away: same X range.
        /// </summary>
        public static readonly float PENALTY_AREA_HALF_WIDTH = 201.6f;

        // =====================================================================
        // PLAYER MOVEMENT — SPEED (units per tick)
        // =====================================================================

        /// <summary>
        /// Maximum player sprint speed at full stamina. 10 units/tick = 10 m/s = 36 km/h.
        /// Elite forwards. Applied when IsSprinting=true and Stamina=1.0.
        /// Actual speed = BaseSpeed × StaminaCurve(Stamina).
        /// </summary>
        public static readonly float PLAYER_MAX_SPRINT_SPEED = 10.0f;

        /// <summary>
        /// Typical player sprint speed (BaseSpeed for average outfield player).
        /// 8.0 units/tick = 8 m/s = 28.8 km/h. Used as default BaseSpeed in tests.
        /// </summary>
        public static readonly float PLAYER_SPRINT_SPEED = 8.0f;

        /// <summary>
        /// Player jogging speed (purposeful movement, not sprinting).
        /// Used when player is moving to support position or returning to anchor.
        /// </summary>
        public static readonly float PLAYER_JOG_SPEED = 5.5f;

        /// <summary>
        /// Player walking speed (repositioning, set piece preparation).
        /// Lowest movement tier. Used for Idle and WalkingToAnchor with low urgency.
        /// </summary>
        public static readonly float PLAYER_WALK_SPEED = 2.0f;

        /// <summary>
        /// GK base speed. Slower than outfielders but quicker across short distances.
        /// </summary>
        public static readonly float GK_BASE_SPEED = 5.5f;

        /// <summary>
        /// Minimum speed multiplier at zero stamina. Player never fully stops due to exhaustion.
        /// Speed at Stamina=0: BaseSpeed × STAMINA_SPEED_MIN_MULTIPLIER.
        /// 0.4 = 40% of full speed when completely exhausted.
        /// </summary>
        public static readonly float STAMINA_SPEED_MIN_MULTIPLIER = 0.40f;

        // =====================================================================
        // PLAYER MOVEMENT — ACCELERATION AND TURNING
        // =====================================================================

        /// <summary>
        /// Maximum velocity change per tick when accelerating from rest.
        /// Units per tick per tick (acceleration). Player can't teleport to full speed.
        /// 2.0 means from 0 to max sprint takes ~4 ticks (0.4 seconds) — realistic.
        /// </summary>
        public static readonly float PLAYER_ACCELERATION = 2.0f;

        /// <summary>
        /// Maximum velocity change per tick when decelerating (braking).
        /// Higher than acceleration — players brake faster than they accelerate.
        /// </summary>
        public static readonly float PLAYER_DECELERATION = 3.0f;

        /// <summary>
        /// Maximum direction change per tick in normalised vector terms.
        /// Limits how sharply a player can turn mid-stride. Prevents instant 180° turns.
        /// 0.35 = roughly 20 degrees per tick maximum turn rate.
        /// Lower = more realistic but players look sluggish on close-range adjustments.
        /// Disable (set to 2.0) if turning artefacts appear in visualisation.
        /// </summary>
        public static readonly float PLAYER_TURN_RATE = 0.35f;

        /// <summary>
        /// Distance threshold at which a player is considered to have reached
        /// their TargetPosition and stops moving. Below this = snap to target.
        /// 3.0 units = 0.3m. Prevents stuttering at destination.
        /// </summary>
        public static readonly float PLAYER_ARRIVAL_THRESHOLD = 3.0f;

        // =====================================================================
        // STAMINA — DECAY AND RECOVERY
        // =====================================================================

        /// <summary>
        /// Stamina drain per tick when sprinting at full intensity (IsSprinting=true).
        /// 0.00035 per tick × 54000 ticks = 18.9 total drain over 90 minutes.
        /// With StaminaAttribute=0.7: effective drain = 0.00035 × (1 - 0.7×0.5) = 0.000228.
        /// At that rate: Stamina hits 0.5 (press-collapse threshold) around tick 22000 = minute 37.
        /// At StaminaAttribute=0.9 (BBM): hits 0.5 around tick 34000 = minute 57. Realistic.
        /// </summary>
        public static readonly float STAMINA_SPRINT_DRAIN_PER_TICK = 0.00035f;

        /// <summary>
        /// Stamina drain per tick when jogging (not sprinting, active movement).
        /// Much lower than sprint drain. Jogging barely affects stamina over a match.
        /// </summary>
        public static readonly float STAMINA_JOG_DRAIN_PER_TICK = 0.00008f;

        /// <summary>
        /// Stamina recovery per tick when walking or idle (not sprinting).
        /// 0.00012 per tick × walking ticks ≈ gradual recovery during possession phases.
        /// A player at 0.3 stamina recovers to ~0.5 in about 1667 ticks = 2.8 minutes of walking.
        /// </summary>
        public static readonly float STAMINA_RECOVERY_WALK_PER_TICK = 0.00012f;

        /// <summary>
        /// Stamina level below which a player's pressing willingness drops sharply.
        /// At Stamina < STAMINA_PRESS_COLLAPSE_THRESHOLD the player's PressBias is
        /// multiplied by (Stamina / STAMINA_PRESS_COLLAPSE_THRESHOLD).
        /// This is what makes the Gegenpress visually collapse after minute 60.
        /// </summary>
        public static readonly float STAMINA_PRESS_COLLAPSE_THRESHOLD = 0.45f;

        /// <summary>
        /// Stamina partially restored at half-time. Not full recovery.
        /// 0.25 stamina added at the interval break (15 minutes rest).
        /// Still reflects cumulative fatigue from first half.
        /// </summary>
        public static readonly float STAMINA_HALFTIME_RECOVERY = 0.25f;

        // =====================================================================
        // BALL PHYSICS — SPEED (units per tick)
        // =====================================================================

        /// <summary>
        /// Initial speed of a hard driven pass (line-drive ground pass).
        /// 30 units/tick = 30 m/s = 108 km/h. Realistic for a firm 20m pass.
        /// </summary>
        public static readonly float BALL_PASS_SPEED_HARD = 30.0f;

        /// <summary>
        /// Initial speed of a medium pass (typical short/medium range delivery).
        /// </summary>
        public static readonly float BALL_PASS_SPEED_MED = 22.0f;

        /// <summary>
        /// Initial speed of a soft pass (short layoff, back-heel, quick one-two).
        /// </summary>
        public static readonly float BALL_PASS_SPEED_SOFT = 14.0f;

        /// <summary>
        /// Initial speed of a shot on goal (powerful strike).
        /// 55 units/tick = 55 m/s = 198 km/h. Top end of real shot speeds.
        /// </summary>
        public static readonly float BALL_SHOT_SPEED_POWER = 55.0f;

        /// <summary>
        /// Initial speed of a placed/finessed shot.
        /// Lower power, higher accuracy. GK has more time to react.
        /// </summary>
        public static readonly float BALL_SHOT_SPEED_PLACED = 38.0f;

        /// <summary>
        /// Initial speed of a cross delivery (lofted or driven).
        /// </summary>
        public static readonly float BALL_CROSS_SPEED = 25.0f;

        // =====================================================================
        // BALL PHYSICS — DECELERATION / FRICTION
        // =====================================================================

        /// <summary>
        /// Friction multiplier applied to ball velocity each tick when LOOSE (rolling on ground).
        /// 0.94 per tick means ball loses 6% speed per tick.
        /// At BALL_PASS_SPEED_MED (22 units/tick): reaches < 1 unit/tick in ~49 ticks (4.9 seconds).
        /// Realistic for a firm pass that rolls to a stop on grass.
        /// </summary>
        public static readonly float BALL_GROUND_FRICTION = 0.94f;

        /// <summary>
        /// Air resistance multiplier applied to ball velocity each tick when IN_FLIGHT.
        /// Higher than ground friction (less deceleration in air for driven balls).
        /// 0.985 per tick: minimal speed loss for driven passes, more for lofted balls.
        /// </summary>
        public static readonly float BALL_AIR_RESISTANCE = 0.985f;

        /// <summary>
        /// Additional deceleration multiplier for lofted/aerial balls (Height > 0.5).
        /// Applied on top of AIR_RESISTANCE. Lofted balls slow down faster in real football.
        /// </summary>
        public static readonly float BALL_LOFT_EXTRA_DECELERATION = 0.975f;

        /// <summary>
        /// Ball speed threshold below which the ball is considered stopped.
        /// When LOOSE ball velocity magnitude drops below this, BallSystem
        /// clears velocity to Vec2.Zero to prevent infinite micro-movement.
        /// </summary>
        public static readonly float BALL_STOP_THRESHOLD = 0.5f;

        // =====================================================================
        // BALL PHYSICS — DRIBBLE OFFSET
        // =====================================================================

        /// <summary>
        /// Distance from player centre to ball position when dribbling.
        /// Ball sits slightly ahead of the player in their movement direction.
        /// 8.0 units = 0.8m. Visible in 2D as the ball dot leading slightly.
        /// </summary>
        public static readonly float BALL_DRIBBLE_OFFSET = 8.0f;

        /// <summary>
        /// Distance from player centre to ball position when holding (standing still).
        /// Smaller than dribble offset — ball is at feet when stationary.
        /// </summary>
        public static readonly float BALL_HOLD_OFFSET = 4.0f;

        // =====================================================================
        // BALL OWNERSHIP — TRANSFER THRESHOLDS
        // =====================================================================

        /// <summary>
        /// Distance at which a player "receives" an IN_FLIGHT pass and gains ownership.
        /// When PassTargetId player comes within this distance of ball position,
        /// BallSystem transfers Phase → Owned.
        /// 8.0 units = 0.8m. Realistic "first touch" reach.
        /// </summary>
        public static readonly float BALL_ARRIVAL_THRESHOLD = 8.0f;

        /// <summary>
        /// Distance at which ANY player can contest a LOOSE ball.
        /// When any player is within this range of a Loose ball, they can
        /// potentially claim it (CollisionSystem resolves contests).
        /// 12.0 units = 1.2m.
        /// </summary>
        public static readonly float BALL_CONTEST_RADIUS = 12.0f;

        /// <summary>
        /// Distance at which a GK can claim a ball without a contest.
        /// GK inside penalty box + ball within this range = auto-claim.
        /// Larger than BALL_CONTEST_RADIUS because GK has priority in their area.
        /// </summary>
        public static readonly float GK_CLAIM_RADIUS = 20.0f;

        // =====================================================================
        // BALL HEIGHT DECAY (for aerial balls returning to ground)
        // =====================================================================

        /// <summary>
        /// Height decay per tick for aerial balls. Applied each tick when Height > 0.
        /// 0.04 per tick means ball at Height=1.0 reaches ground (Height=0) in 25 ticks (2.5 seconds).
        /// Adjust for desired "hang time" on lofted passes.
        /// </summary>
        public static readonly float BALL_HEIGHT_DECAY_PER_TICK = 0.04f;

        // =====================================================================
        // PITCH BOUNDARY — BOUNCE AND SNAP
        // =====================================================================

        /// <summary>
        /// Margin from pitch boundary within which out-of-play is detected.
        /// Ball centre must cross this line before IsOutOfPlay is set.
        /// 0 = no margin. Increase if ball appears to go "through" the line.
        /// </summary>
        public static readonly float BOUNDARY_DETECTION_MARGIN = 0f;

        // =====================================================================
        // TEMPO MOVEMENT MULTIPLIER
        // =====================================================================

        /// <summary>
        /// Minimum speed scale factor applied from TacticsInput.Tempo = 0.0.
        /// At Tempo=0, players move at 70% of their natural jog speed.
        /// Prevents the team from completely standing still on low tempo.
        /// </summary>
        public static readonly float TEMPO_MIN_SPEED_SCALE = 0.70f;

        /// <summary>
        /// Maximum speed scale factor from TacticsInput.Tempo = 1.0.
        /// At Tempo=1, players push their movement speed 20% above the natural rate.
        /// Stacks on top of stamina curve — high tempo + low stamina causes rapid collapse.
        /// </summary>
        public static readonly float TEMPO_MAX_SPEED_SCALE = 1.20f;
    }
}