// =============================================================================
// Module:  AIConstants.cs
// Path:    FootballSim/Engine/Systems/AIConstants.cs
// Purpose:
//   Single source of truth for every numeric constant used by DecisionSystem
//   and PlayerAI. Mirrors the role of PhysicsConstants.cs for the physics layer.
//   No AI constant is hardcoded inside a scoring method — all live here.
//
//   Tuning philosophy:
//     Most values are distances (pitch units) or score weights (dimensionless).
//     Distances use the same unit system as PhysicsConstants: 1 unit = 0.1m.
//     Score weights are relative — only their ratio to each other matters.
//     When a behaviour looks wrong visually, find the constant here and adjust.
//
//   Scoring formula overview (DecisionSystem):
//     raw_score  = base_weight × attribute_factor × role_bias × tactic_modifier
//     final_score = Clamp01(raw_score) with situational bonuses/penalties added
//     Winning action = argmax over all scored options for this player this tick.
//
// Dependencies: none
//
// Notes:
//   • All constants are static readonly float.
//   • Decision interval (how often PlayerAI re-evaluates) is also here:
//     DECISION_INTERVAL_TICKS controls the cooldown. 3 ticks = 0.3 seconds.
//     Reduce to 1 for maximum responsiveness; increase to 5 for more inertia.
//   • XG thresholds match real football data from StatsBomb / Opta:
//     0.03 = header from edge of box, 0.35 = one-on-one, 0.70 = open goal.
// =============================================================================

namespace FootballSim.Engine.Systems
{
    public static class AIConstants
    {
        // =====================================================================
        // DECISION CYCLE
        // =====================================================================

        /// <summary>Exponent for xG distance decay curve. Higher = steeper falloff from close range.</summary>
        public static readonly float XG_DISTANCE_DECAY = 2.5f;

        /// <summary>
        /// How many ticks between full AI re-evaluations per player.
        /// 3 ticks = 0.3 seconds match time. Players re-evaluate every 0.3s.
        /// Set to 1 to re-evaluate every tick (expensive but maximally reactive).
        /// Set to 5 for more inertia — players stick to decisions longer.
        /// </summary>
        public static readonly int DECISION_INTERVAL_TICKS = 3;

        /// <summary>
        /// Randomised jitter added to each player's decision cooldown at match start.
        /// Prevents all 22 players re-evaluating on the same tick (CPU spike).
        /// Range: [0, DECISION_STAGGER_MAX]. Each player gets a random offset.
        /// </summary>
        public static readonly int DECISION_STAGGER_MAX = 2;

        // =====================================================================
        // PRESS TRIGGER — DISTANCES (pitch units = 0.1m each)
        // =====================================================================

        /// <summary>
        /// Press trigger distance when TacticsInput.PressingIntensity = 0.0 (passive).
        /// 50 units = 5m. Player only presses when almost on top of ball carrier.
        /// </summary>
        public static readonly float PRESS_TRIGGER_DIST_MIN = 50f;

        /// <summary>
        /// Press trigger distance when TacticsInput.PressingIntensity = 1.0 (Gegenpress).
        /// 250 units = 25m. Player presses from very far — almost anywhere on pitch.
        /// </summary>
        public static readonly float PRESS_TRIGGER_DIST_MAX = 250f;

        /// <summary>
        /// Stamina threshold below which press willingness scales down linearly.
        /// Mirrors PhysicsConstants.STAMINA_PRESS_COLLAPSE_THRESHOLD.
        /// At Stamina = threshold: full press willingness.
        /// At Stamina = 0: press willingness = 0. No pressing when exhausted.
        /// </summary>
        public static readonly float PRESS_STAMINA_THRESHOLD = 0.45f;

        // =====================================================================
        // SHOOTING — XG THRESHOLDS AND RANGES
        // =====================================================================

        /// <summary>
        /// Maximum shooting range. Beyond this distance, shot score = 0.
        /// 350 units = 35m. Long-range shots from outside this are never attempted.
        /// </summary>
        public static readonly float SHOOT_MAX_RANGE = 350f;

        /// <summary>
        /// Optimal shooting range centre. xG peaks near this distance.
        /// 120 units = 12m. Inside the six-yard box outward to edge of box.
        /// </summary>
        public static readonly float SHOOT_OPTIMAL_RANGE = 120f;

        /// <summary>
        /// Minimum xG threshold below which a player will NOT shoot (before ego modifier).
        /// At TacticsInput.ShootingThreshold = 1.0: only shoot at xG >= 0.25.
        /// At TacticsInput.ShootingThreshold = 0.0: shoot at any xG >= 0.02.
        /// These two values are lerped by ShootingThreshold.
        /// </summary>
        public static readonly float XG_THRESHOLD_LOW = 0.02f;  // permissive (ShootingThreshold=0)
        public static readonly float XG_THRESHOLD_HIGH = 0.25f;  // strict    (ShootingThreshold=1)

        /// <summary>
        /// xG penalty per defender between shooter and goal, within DEFENDER_BLOCK_RANGE.
        /// Each blocking defender reduces xG by this fraction.
        /// 0.15 = each defender in the way costs 15% of base xG.
        /// </summary>
        public static readonly float XG_DEFENDER_BLOCK_PENALTY = 0.15f;

        /// <summary>
        /// Distance within which a defender is considered "in the shooting lane"
        /// for xG block penalty calculation.
        /// 80 units = 8m lateral proximity to the straight line from shooter to goal.
        /// </summary>
        public static readonly float DEFENDER_BLOCK_LANE_WIDTH = 80f;

        /// <summary>
        /// Ego modifier range. At Ego=1.0, the effective xG threshold is reduced by
        /// this fraction (player shoots from worse positions).
        /// At Ego=0.0: no reduction. Full threshold applies.
        /// 0.6 = high-ego player accepts positions 60% worse than threshold.
        /// </summary>
        public static readonly float EGO_XG_THRESHOLD_REDUCTION = 0.60f;

        // =====================================================================
        // PASSING — SCORING WEIGHTS AND DISTANCES
        // =====================================================================

        /// <summary>
        /// Maximum pass distance considered. Beyond this, pass score = 0.
        /// 600 units = 60m. Longest viable pass in football.
        /// DLP with LongPassBias=0.8 may attempt up to this range.
        /// </summary>
        public static readonly float PASS_MAX_DISTANCE = 600f;

        /// <summary>
        /// Short pass distance threshold. Passes within this are always "safe" passes.
        /// 120 units = 12m. Typical one-two range.
        /// </summary>
        public static readonly float PASS_SHORT_THRESHOLD = 120f;

        /// <summary>
        /// Long pass distance threshold. Passes beyond this are "long ball" territory.
        /// 350 units = 35m.
        /// </summary>
        public static readonly float PASS_LONG_THRESHOLD = 350f;

        /// <summary>
        /// Score bonus applied to passes that move the ball forward (progressive pass).
        /// Forward = target Y is closer to opponent goal than sender Y.
        /// Multiplied by TacticsInput.PossessionFocus (inverted) — direct teams bonus more.
        /// </summary>
        public static readonly float PASS_PROGRESSIVE_BONUS = 0.25f;

        /// <summary>
        /// Score penalty for passing to a receiver who has a defender very close.
        /// "Passing into pressure" — reduces pass score significantly.
        /// 0.35 = 35% score reduction per nearby defender on the receiver.
        /// </summary>
        public static readonly float PASS_RECEIVER_PRESSURE_PENALTY = 0.35f;

        /// <summary>
        /// Distance within which a defender is considered "tight marking" a receiver.
        /// Pass score is penalised when intended receiver has a defender within this range.
        /// 60 units = 6m.
        /// </summary>
        public static readonly float PASS_TIGHT_MARKING_RADIUS = 60f;

        /// <summary>
        /// Score bonus for a back-pass or sideways recycle when under heavy pressure.
        /// Applied when SenderPressure > PASS_SAFE_RECYCLE_PRESSURE_THRESHOLD.
        /// Prevents ball carriers from panicking and launching blind long balls.
        /// </summary>
        public static readonly float PASS_SAFE_RECYCLE_BONUS = 0.30f;

        /// <summary>
        /// Pressure threshold on the ball carrier above which safe-recycle bonus activates.
        /// Pressure = nearest defender distance (lower = more pressure).
        /// 50 units = 5m — a defender is "on top of you".
        /// </summary>
        public static readonly float PASS_SAFE_RECYCLE_PRESSURE_THRESHOLD = 50f;

        // =====================================================================
        // DRIBBLING — SCORING WEIGHTS
        // =====================================================================

        /// <summary>
        /// Maximum open space ahead (in movement direction) that makes dribbling attractive.
        /// 200 units = 20m. If more than this space is available, dribbling score is capped.
        /// </summary>
        public static readonly float DRIBBLE_MAX_SPACE_AHEAD = 200f;

        /// <summary>
        /// Minimum distance to nearest defender before dribbling becomes a viable option.
        /// Below this, dribbling score drops sharply (defender too close to beat).
        /// 30 units = 3m.
        /// </summary>
        public static readonly float DRIBBLE_DEFENDER_SAFE_DISTANCE = 30f;

        /// <summary>
        /// Maximum distance to nearest defender where dribbling is still scored.
        /// If defender is beyond this, dribble score is suppressed — just run with ball.
        /// 120 units = 12m.
        /// </summary>
        public static readonly float DRIBBLE_DEFENDER_RELEVANT_DISTANCE = 120f;

        // =====================================================================
        // CROSSING — POSITION AND RANGE
        // =====================================================================

        /// <summary>
        /// Minimum X distance from the centre line to be in a "crossing position".
        /// Only players within CROSS_WIDE_ZONE_X of the touchline are considered
        /// wide enough to cross. 150 units = 15m from the touchline (= X < 150 or X > 900).
        /// </summary>
        public static readonly float CROSS_WIDE_ZONE_X = 150f;

        /// <summary>
        /// Minimum Y progress (toward opponent goal) before a cross becomes viable.
        /// Crossing from deep positions is low value. 0.55 = past midfield + some.
        /// Normalised [0=own goal line, 1=opponent goal line].
        /// </summary>
        public static readonly float CROSS_MIN_Y_PROGRESS = 0.55f;

        /// <summary>
        /// Score bonus when at least MIN_CROSS_ATTACKERS are in the penalty box.
        /// Rewards teams that flood the box before crossing.
        /// </summary>
        public static readonly float CROSS_ATTACKERS_IN_BOX_BONUS = 0.30f;

        /// <summary>
        /// Minimum attackers in or near the penalty box for the cross bonus to apply.
        /// </summary>
        public static readonly int CROSS_MIN_ATTACKERS_IN_BOX = 2;

        // =====================================================================
        // SUPPORT POSITION — OFF-BALL MOVEMENT TARGETS
        // =====================================================================

        /// <summary>
        /// Ideal spacing between supporting players and the ball carrier.
        /// Off-ball players target a position at roughly this distance away from the ball.
        /// 150 units = 15m. Classic passing triangle spacing.
        /// </summary>
        public static readonly float SUPPORT_IDEAL_DISTANCE = 150f;

        /// <summary>
        /// Score penalty if two teammates are within this distance of each other.
        /// Prevents clustering — players spread out for better support angles.
        /// 80 units = 8m.
        /// </summary>
        public static readonly float SUPPORT_CROWDING_RADIUS = 80f;

        /// <summary>
        /// Penalty score subtracted when support target is too crowded with teammates.
        /// </summary>
        public static readonly float SUPPORT_CROWDING_PENALTY = 0.40f;

        // =====================================================================
        // DEFENSIVE BEHAVIOUR — MARKING AND TRACKING
        // =====================================================================

        /// <summary>
        /// Distance at which a defender switches from MarkingSpace to Tracking an opponent.
        /// When the nearest dangerous opponent enters this radius, the defender tracks them.
        /// 200 units = 20m.
        /// </summary>
        public static readonly float MARK_SWITCH_TO_TRACKING_RADIUS = 200f;

        /// <summary>
        /// Danger score threshold above which a player is considered a marking priority.
        /// Danger = proximity to goal × has ball or likely to receive.
        /// 0.5 = player must be moderately dangerous to be assigned a marker.
        /// </summary>
        public static readonly float MARK_DANGER_THRESHOLD = 0.50f;

        /// <summary>
        /// Distance ahead of a runner that a tracking defender tries to position.
        /// Defenders don't chase — they anticipate. 40 units = 4m ahead of runner.
        /// </summary>
        public static readonly float MARK_ANTICIPATION_OFFSET = 40f;

        // =====================================================================
        // LOOSE BALL — CLAIMING PRIORITY
        // =====================================================================

        /// <summary>
        /// Distance within which a player considers making a run for a loose ball.
        /// Beyond this, they return to shape instead of chasing.
        /// 300 units = 30m.
        /// </summary>
        public static readonly float LOOSE_BALL_CHASE_RADIUS = 300f;

        /// <summary>
        /// Score bonus for the closest player to a loose ball. Encourages the
        /// nearest player to claim rather than everyone converging.
        /// </summary>
        public static readonly float LOOSE_BALL_PROXIMITY_BONUS = 0.50f;

        // =====================================================================
        // FORMATION RETURN PRESSURE
        // =====================================================================

        /// <summary>
        /// Distance from FormationAnchor beyond which a player feels "out of position"
        /// and starts scoring WalkingToAnchor more highly than other off-ball actions.
        /// 200 units = 20m. Players > 20m from anchor feel pull back to shape.
        /// </summary>
        public static readonly float ANCHOR_PULL_RADIUS = 200f;

        /// <summary>
        /// Score weight for returning to anchor. Multiplied by how far beyond ANCHOR_PULL_RADIUS
        /// the player currently is. Higher = players snap back to shape more aggressively.
        /// </summary>
        public static readonly float ANCHOR_RETURN_WEIGHT = 0.60f;

        // =====================================================================
        // FREEDOM LEVEL — BLENDING ROLE vs INSTINCT
        // =====================================================================

        /// <summary>
        /// At FreedomLevel = 0.0 (Strict): role bias weight in scoring.
        /// effectiveBias = Lerp(roleBias, playerAttribute, FreedomLevel).
        /// At Strict: effectiveBias = roleBias (100% role).
        /// At Expressive (1.0): effectiveBias = playerAttribute (100% instinct).
        /// </summary>
        public static readonly float FREEDOM_ROLE_WEIGHT_AT_STRICT = 1.0f;
        public static readonly float FREEDOM_ROLE_WEIGHT_AT_EXPRESSIVE = 0.0f;

        // =====================================================================
        // ACTION LOCK — MINIMUM TICKS BEFORE AN ACTION CAN CHANGE
        // =====================================================================

        /// <summary>
        /// Minimum ticks a player must stay in Passing/Shooting/Crossing action
        /// after the ball is launched. Prevents immediate re-decision before
        /// BallSystem has processed the launch.
        /// 2 ticks = 0.2 seconds. Short but enough for physics to process.
        /// </summary>
        public static readonly int ACTION_LOCK_AFTER_KICK_TICKS = 2;

        // =====================================================================
        // BALL HOLD — MINIMUM TICKS BEFORE PASSING IS ALLOWED
        // =====================================================================

        /// Ticks a defender cannot attempt another tackle after a failed/foul tackle.
        /// 7 ticks = 0.7 seconds. A sliding miss puts the defender on the ground briefly.
        public static readonly int TACKLE_RECOVERY_TICKS_FAILED = 7;

        /// Ticks a defender cannot attempt another tackle after a successful tackle.
        /// 3 ticks = 0.3 seconds. Successful tackle still needs brief recovery.
        public static readonly int TACKLE_RECOVERY_TICKS_SUCCESS = 3;

        // =====================================================================
        // INTERCEPT — reaction window constant (replaces magic ×20, Bug 9 fix)
        // =====================================================================

        /// <summary>
        /// How many ticks ahead on the ball trajectory a defender can realistically
        /// intercept. maxInterceptRange = ballVel.Length() × DEFENDER_INTERCEPT_REACTION_TICKS.
        /// 12 ticks = 1.2 seconds. A defender can step into a passing lane within 1.2s.
        /// At typical pass speed of 25 units/tick: 25 × 12 = 300 units max reach (30m). Realistic.
        /// </summary>
        public static readonly int DEFENDER_INTERCEPT_REACTION_TICKS = 12;

        /// <summary>
        /// Minimum ticks a CB or BPD must hold the ball before passing.
        /// 10 ticks = 1.0 second. Centre backs are deliberate on the ball.
        /// </summary>
        public static readonly int HOLD_TICKS_DEFENDER = 10;

        /// <summary>
        /// Minimum ticks a CDM or DLP must hold before passing.
        /// 8 ticks = 0.8 seconds. Holding midfielders recycle patiently.
        /// </summary>
        public static readonly int HOLD_TICKS_DM = 8;

        /// <summary>
        /// Minimum ticks a CM, BBM, WB must hold before passing.
        /// 5 ticks = 0.5 seconds.
        /// </summary>
        public static readonly int HOLD_TICKS_MID = 5;

        /// <summary>
        /// Minimum ticks a winger, AM, CF, ST must hold before passing.
        /// 3 ticks = 0.3 seconds. Attackers play quickly.
        /// </summary>
        public static readonly int HOLD_TICKS_ATTACKER = 3;

        /// <summary>
        /// GK and SK hold longest — they survey the pitch before distributing.
        /// 12 ticks = 1.2 seconds.
        /// </summary>
        public static readonly int HOLD_TICKS_GK = 12;

        /// <summary>
        /// When true, long passes (dist > PASS_LONG_THRESHOLD) are completely
        /// disabled in DecisionSystem.ScorePass(). All passes must be short or medium.
        /// Set true during debugging to isolate ping-pong and interception bugs
        /// from long-ball fling issues.
        /// </summary>
        /// disabled in DecisionSystem.ScorePass(). Use during debugging to isolate
        /// ping-pong and interception bugs from long-ball fling issues.

        public static readonly bool DISABLE_LONG_PASS = false;

        /// <summary>
        /// Runtime override set by DebugTickRunner. Never true in production.
        /// </summary>
        public static bool DISABLE_LONG_PASS_OVERRIDE = false;

        /// <summary>
        /// Minimum ticks a player must remain in Celebrating before returning to normal.
        /// 15 ticks = 1.5 seconds of celebration before the next kickoff evaluation.
        /// </summary>
        public static readonly int ACTION_LOCK_CELEBRATE_TICKS = 15;

        // =====================================================================
        // GK SPECIAL BEHAVIOUR
        // =====================================================================

        /// <summary>
        /// Y distance from goal line within which the GK's anchor position is fixed.
        /// GK never targets a position further than this from their own goal line.
        /// 165 units = 16.5m = penalty area depth. SK may push a bit further.
        /// </summary>
        public static readonly float GK_MAX_FORWARD_DISTANCE = 165f;

        /// <summary>
        /// Minimum number of ticks ball must be LOOSE near goal before GK rushes.
        /// Prevents GK from immediately abandoning goal line for every loose ball.
        /// 5 ticks = 0.5 seconds. Ball must be loose for half a second.
        /// </summary>
        public static readonly int GK_LOOSE_BALL_RUSH_TICKS = 5;
    }
}