// =============================================================================
// Module:  BlockShiftSystem.cs
// Path:    FootballSim/Engine/Systems/BlockShiftSystem.cs
// Purpose:
//   Computes and stores the per-team defensive shape shift offset each tick.
//   Called once per tick by MatchEngine BEFORE DecisionSystem evaluates targets.
//   Writes results into ctx.HomeShiftX, ctx.AwayShiftX, ctx.HomeDefLineY,
//   ctx.AwayDefLineY — four floats that DecisionSystem reads when computing
//   ComputeDefensiveAnchor() and ComputeMarkSpaceTarget().
//
//   Main fixes in this revision:
//   - Uses last touched team to stop both teams from collapsing toward the ball
//   - Separates "defending team tracks ball" vs "recover to shape"
//   - Ball carrier is excluded from block shift
//   - GK/SK return current position instead of static formation anchor
//   - Offside helper uses TargetPosition (more current than Position)
//   - Role Y offsets are smaller and scaled by tactics
//   - Smoothing is time-based instead of a hardcoded per-tick lerp factor
//   - Defensive line and block shift are clamped and stabilized
//
//   CONCEPT — The Rigid Template Model:
//     The team's defensive shape is a rigid template (11 slot positions relative
//     to each other). The template slides along the X axis toward the ball.
//     Each player's TargetPosition = their formation slot position inside the
//     shifted template. The shape is PRESERVED — only its centre of gravity moves.
//
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

        public static bool DEBUG = false;
        private const int SHIFT_DEBUG_INTERVAL = 300;

        // ── Tick timing / smoothing ───────────────────────────────────────────
        // If your engine tick rate changes, update this constant or move it into ctx.
        private const float ASSUMED_TICK_SECONDS = 0.10f; // 10 ticks/sec

        /// <summary>
        /// Time constant for team-shape movement toward a tracked ball-side target.
        /// Smaller = snappier, larger = more inertia.
        /// </summary>
        private const float SHIFT_TRACK_TIME_CONSTANT = 1.15f;

        /// <summary>
        /// Time constant for returning toward neutral shape when not tracking.
        /// Usually a little faster than tracking, so teams recover shape cleanly.
        /// </summary>
        private const float SHIFT_RECOVER_TIME_CONSTANT = 0.85f;

        // ── Shift magnitude limits per behaviour tier ─────────────────────────

        private const float MAX_SHIFT_PARK_BUS = 60f;
        private const float MAX_SHIFT_MID_BLOCK = 130f;
        private const float MAX_SHIFT_HIGH_PRESS = 200f;
        private const float SHIFT_ABSOLUTE_MAX = 220f;

        // ── Defensive line Y constants ────────────────────────────────────────

        private const float HOME_DEF_LINE_DEEP = 0.75f;
        private const float HOME_DEF_LINE_HIGH = 0.38f;

        private const float AWAY_DEF_LINE_DEEP = 0.25f;
        private const float AWAY_DEF_LINE_HIGH = 0.62f;

        // ── Pitch margins ────────────────────────────────────────────────────

        private const float X_MARGIN = 40f;
        private const float Y_MARGIN = 10f;

        // =====================================================================
        // MAIN TICK
        // =====================================================================

        /// <summary>
        /// Main update. Called once per tick by MatchEngine, after MovementSystem,
        /// before PlayerAI. Updates all four shift fields in ctx.
        /// </summary>
        public static void Tick(MatchContext ctx)
        {
            Vec2 ballPos = ctx.Ball.Position;
            float pitchCentreX = PhysicsConstants.PITCH_WIDTH * 0.5f;

            bool homeHasBall = ctx.Ball.OwnerId >= 0 && ctx.Ball.OwnerId <= 10;
            bool awayHasBall = ctx.Ball.OwnerId >= 11 && ctx.Ball.OwnerId <= 21;

            int lastTouchedTeam = GetLastTouchedTeam(ctx);

            // Each team decides independently:
            // - if they have the ball, they recover toward neutral
            // - if they are the team defending the current ball state, they track
            // - otherwise they recover instead of also collapsing toward the ball
            bool homeTracksBall = ShouldTrackBall(teamId: 0, ctx, lastTouchedTeam, homeHasBall);
            bool awayTracksBall = ShouldTrackBall(teamId: 1, ctx, lastTouchedTeam, awayHasBall);

            ctx.HomeShiftX = UpdateTeamShift(
                currentShift: ctx.HomeShiftX,
                teamId: 0,
                hasBall: homeHasBall,
                tracksBall: homeTracksBall,
                ballPos: ballPos,
                tactics: ctx.HomeTeam.Tactics,
                pitchCentreX: pitchCentreX);

            ctx.AwayShiftX = UpdateTeamShift(
                currentShift: ctx.AwayShiftX,
                teamId: 1,
                hasBall: awayHasBall,
                tracksBall: awayTracksBall,
                ballPos: ballPos,
                tactics: ctx.AwayTeam.Tactics,
                pitchCentreX: pitchCentreX);

            ctx.HomeDefLineY = ComputeDefLineY(ctx.HomeTeam.Tactics, isHome: true);
            ctx.AwayDefLineY = ComputeDefLineY(ctx.AwayTeam.Tactics, isHome: false);

            if (DEBUG && ctx.Tick % SHIFT_DEBUG_INTERVAL == 0)
            {
                Console.WriteLine(
                    $"[BlockShiftSystem] Tick {ctx.Tick} " +
                    $"Ball=({ballPos.X:F0},{ballPos.Y:F0}) " +
                    $"HomeShiftX={ctx.HomeShiftX:F1} AwayShiftX={ctx.AwayShiftX:F1} " +
                    $"HomeDefY={ctx.HomeDefLineY:F0} AwayDefY={ctx.AwayDefLineY:F0} " +
                    $"LastTouchedTeam={lastTouchedTeam}");
            }
        }

        // =====================================================================
        // PUBLIC — used by DecisionSystem and overlay
        // =====================================================================

        /// <summary>
        /// Computes the shifted defensive slot position for one player.
        /// Called by DecisionSystem.ComputeDefensiveAnchor() and
        /// DecisionSystem.ComputeMarkSpaceTarget() instead of raw FormationAnchor.
        ///
        /// GK/SK and ball carriers are excluded from the shift.
        /// </summary>
        public static Vec2 ComputeShiftedTarget(ref PlayerState player, MatchContext ctx)
        {
            // Ball carrier should be handled by separate ball-control logic.
            if (player.HasBall)
                return player.Position;

            // GK/SK stay on their own line and should not be warped by block shift.
            if (player.Role == PlayerRole.GK || player.Role == PlayerRole.SK)
                return player.Position;

            float pitchCentreX = PhysicsConstants.PITCH_WIDTH * 0.5f;
            float shiftX = player.TeamId == 0 ? ctx.HomeShiftX : ctx.AwayShiftX;
            float defLineY = player.TeamId == 0 ? ctx.HomeDefLineY : ctx.AwayDefLineY;
            TacticsInput tactics = player.TeamId == 0 ? ctx.HomeTeam.Tactics : ctx.AwayTeam.Tactics;

            float slotOffsetX = player.FormationAnchor.X - pitchCentreX;
            float shiftedX = pitchCentreX + shiftX + slotOffsetX;
            shiftedX = Math.Clamp(shiftedX, X_MARGIN, PhysicsConstants.PITCH_WIDTH - X_MARGIN);

            float roleYOffset = ComputeRoleYOffset(player.Role, attacksDown: player.TeamId == 0, tactics);
            float shiftedY = defLineY + roleYOffset;
            shiftedY = Math.Clamp(shiftedY,
                PhysicsConstants.PITCH_TOP + Y_MARGIN,
                PhysicsConstants.PITCH_BOTTOM - Y_MARGIN);

            return new Vec2(shiftedX, shiftedY);
        }

        /// <summary>
        /// Returns the Y coordinate of the offside line for a team.
        /// Uses TargetPosition rather than Position so the line follows tactical intent
        /// instead of lagging behind movement.
        /// </summary>
        public static float ComputeOffsideLineY(int teamId, MatchContext ctx)
        {
            int start = teamId == 0 ? 0 : 11;
            int end = teamId == 0 ? 11 : 22;
            bool attacksDown = teamId == 0;

            float offsideY = attacksDown ? float.MaxValue : float.MinValue;
            bool found = false;

            for (int i = start; i < end; i++)
            {
                ref PlayerState p = ref ctx.Players[i];
                if (!p.IsActive) continue;
                if (p.Role == PlayerRole.GK || p.Role == PlayerRole.SK) continue;
                if (p.HasBall) continue;
                if (!IsDefenderRole(p.Role)) continue;

                // Use TargetPosition so the overlay and striker logic see the intended line.
                float y = p.TargetPosition.Y;

                if (attacksDown)
                {
                    if (y < offsideY)
                    {
                        offsideY = y;
                        found = true;
                    }
                }
                else
                {
                    if (y > offsideY)
                    {
                        offsideY = y;
                        found = true;
                    }
                }
            }

            if (!found)
                return PhysicsConstants.PITCH_HEIGHT * 0.5f;

            return offsideY;
        }

        // =====================================================================
        // PRIVATE — shift computation helpers
        // =====================================================================

        private static int GetLastTouchedTeam(MatchContext ctx)
        {
            int lastTouch = ctx.Ball.LastTouchedBy;
            if (lastTouch < 0 || lastTouch > 21)
                return -1;

            return lastTouch <= 10 ? 0 : 1;
        }

        private static bool ShouldTrackBall(int teamId, MatchContext ctx, int lastTouchedTeam, bool teamHasBall)
        {
            if (teamHasBall)
                return false;

            // If the ball is out of play or loose, the team that did NOT last touch it
            // is the one that should be shaping to defend the restart / contest.
            if (ctx.Ball.IsOutOfPlay || ctx.Ball.OwnerId < 0)
                return lastTouchedTeam >= 0 && lastTouchedTeam != teamId;

            return lastTouchedTeam >= 0 && lastTouchedTeam != teamId;
        }

        private static float UpdateTeamShift(
            float currentShift,
            int teamId,
            bool hasBall,
            bool tracksBall,
            Vec2 ballPos,
            TacticsInput tactics,
            float pitchCentreX)
        {
            float targetShift;

            if (hasBall)
            {
                targetShift = 0f;
                return SmoothTowards(currentShift, targetShift, SHIFT_RECOVER_TIME_CONSTANT);
            }

            if (tracksBall)
            {
                targetShift = ComputeTargetShiftX(ballPos, tactics, pitchCentreX);

                // If ball is out of play, be a bit less aggressive so the shape
                // doesn't whip unnaturally to the boundary.
                if (ballPos.X <= PhysicsConstants.PITCH_LEFT ||
                    ballPos.X >= PhysicsConstants.PITCH_RIGHT ||
                    ballPos.Y <= PhysicsConstants.PITCH_TOP ||
                    ballPos.Y >= PhysicsConstants.PITCH_BOTTOM)
                {
                    targetShift *= 0.90f;
                }

                return SmoothTowards(currentShift, targetShift, SHIFT_TRACK_TIME_CONSTANT);
            }

            // Not the defending team right now: recover toward neutral instead of also chasing.
            return SmoothTowards(currentShift, 0f, SHIFT_RECOVER_TIME_CONSTANT);
        }

        /// <summary>
        /// Time-based smoothing so the behavior stays stable if tick rate changes.
        /// </summary>
        private static float SmoothTowards(float current, float target, float timeConstantSeconds)
        {
            if (timeConstantSeconds <= 0f)
                return target;

            float alpha = 1f - MathF.Exp(-ASSUMED_TICK_SECONDS / timeConstantSeconds);
            alpha = Math.Clamp(alpha, 0f, 1f);

            float next = MathUtil.Lerp(current, target, alpha);
            return Math.Clamp(next, -SHIFT_ABSOLUTE_MAX, SHIFT_ABSOLUTE_MAX);
        }

        /// <summary>
        /// Computes the target X shift for the defensive shape based on ball position
        /// and tactical settings. Returns signed offset from pitch centre (positive = right).
        /// </summary>
        private static float ComputeTargetShiftX(Vec2 ballPos, TacticsInput tactics, float pitchCentreX)
        {
            float ballOffsetX = ballPos.X - pitchCentreX;
            float maxShift = ComputeMaxShiftForTactics(tactics);
            float shiftIntensity = ComputeShiftIntensity(tactics);

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
        /// </summary>
        private static float ComputeShiftIntensity(TacticsInput tactics)
        {
            float pressPull = tactics.PressingIntensity * 0.5f;      // 0–0.5
            float compactResist = tactics.OutOfPossessionShape * 0.3f; // 0–0.3
            float intensity = pressPull - compactResist + 0.15f;
            return Math.Clamp(intensity, 0.10f, 0.80f);
        }

        /// <summary>
        /// Computes the defensive line Y coordinate from the DefensiveLine tactic slider.
        /// </summary>
        private static float ComputeDefLineY(TacticsInput tactics, bool isHome)
        {
            float pitchH = PhysicsConstants.PITCH_HEIGHT;
            float t = Math.Clamp(tactics.DefensiveLine, 0f, 1f);

            float lineYNorm = isHome
                ? MathUtil.Lerp(HOME_DEF_LINE_DEEP, HOME_DEF_LINE_HIGH, t)
                : MathUtil.Lerp(AWAY_DEF_LINE_DEEP, AWAY_DEF_LINE_HIGH, t);

            return lineYNorm * pitchH;
        }

        /// <summary>
        /// Returns a Y offset from the defensive line anchor for each role.
        /// Smaller values than before to avoid exaggerated vertical jumps.
        /// Positive = further from own goal (more advanced).
        /// </summary>
        private static float ComputeRoleYOffset(PlayerRole role, bool attacksDown, TacticsInput tactics)
        {
            float baseAdvanceAmount;

            switch (role)
            {
                case PlayerRole.CB:
                case PlayerRole.BPD:
                case PlayerRole.RB:
                case PlayerRole.LB:
                case PlayerRole.WB:
                    baseAdvanceAmount = 0f;
                    break;

                case PlayerRole.CDM:
                case PlayerRole.DLP:
                    baseAdvanceAmount = 22f;
                    break;

                case PlayerRole.CM:
                case PlayerRole.BBM:
                    baseAdvanceAmount = 40f;
                    break;

                case PlayerRole.AM:
                case PlayerRole.IW:
                case PlayerRole.WF:
                    baseAdvanceAmount = 58f;
                    break;

                case PlayerRole.CF:
                case PlayerRole.ST:
                case PlayerRole.PF:
                    baseAdvanceAmount = 78f;
                    break;

                default:
                    baseAdvanceAmount = 30f;
                    break;
            }

            float compactness = Math.Clamp(tactics.OutOfPossessionShape, 0f, 1f);
            float lineAggression = Math.Clamp(tactics.DefensiveLine, 0f, 1f);

            // More compact teams keep the vertical block tighter.
            // Higher defensive lines allow a little more vertical spread.
            float spreadScale = MathUtil.Lerp(1.00f, 0.78f, compactness);
            float lineScale = MathUtil.Lerp(0.90f, 1.15f, lineAggression);

            float offset = baseAdvanceAmount * spreadScale * lineScale;

            // Home attacks down -> more advanced means smaller Y.
            // Away attacks up   -> more advanced means larger Y.
            return attacksDown ? -offset : offset;
        }

        /// <summary>
        /// Returns true if this role is part of the defensive unit that sets the line.
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
                case PlayerRole.CDM:
                case PlayerRole.DLP:
                    return true;
                default:
                    return false;
            }
        }
    }
}