// =============================================================================
// Module:  DecisionSystem.Helpers.cs
// Path:    FootballSim/Engine/Systems/DecisionSystem/DecisionSystem.Helpers.cs
// Purpose:
//   Shared math and utility heuristics used by multiple scorers across the
//   DecisionSystem. This is the "toolbox" that all other modules draw from.
//   If anything is split further in future, this file is the first candidate.
//
// Methods defined here:
//   BlendRoleWithInstinct(roleBias, instinct, freedomLevel) → float
//   ComputeXG(player, goalCentre, distToGoal, ctx)          → float
//   ComputeEffectiveXGThreshold(player, tactics)            → float
//   CountBlockersInShootingLane(player, goalCentre, team, ctx) → int
//   FindNearestOpponentDistance(player, ctx)                → float
//   ComputeReceiverPressure(receiver, receiverTeam, ctx)    → float [0,1]
//   ComputeSenderPressure(sender, ctx)                      → float [0,1]
//   SelectPassSpeed(dist, directness, longPassBias)         → float
//   ComputeCrowdingPenalty(player, target, ctx)             → float
//   ComputeOpponentDanger(opp, defensiveTeam, ctx)          → float
//   CountAttackersInBox(attackingTeam, ctx)                 → int
//
// Bug fixes applied (see DecisionSystemBugsAnalysis.md):
//   Bug 9  — ComputeSenderPressure returns raw distance: now normalised to [0,1]
//             matching ComputeReceiverPressure.
//   Bug 15 — xG normalised to OPTIMAL_RANGE: ComputeXG now uses SHOOT_MAX_RANGE.
//   Bug 16 — Block penalty additive collapse: ComputeXG now uses multiplicative
//             Pow decay per blocker.
//   Bug 21 — ComputeOpponentDanger uses wrong goal: comment clarified; logic
//             already correct via ctx.AttacksDownward which flips at half-time.
//
// Notes:
//   • All methods are private static — internal utilities only.
//   • All methods are pure — same inputs always produce same outputs.
//   • No Random calls, no MatchContext writes.
// =============================================================================

using System;
using FootballSim.Engine.Models;
using FootballSim.Engine.Tactics;

namespace FootballSim.Engine.Systems
{
    public static partial class DecisionSystem
    {
        // =====================================================================
        // PRIVATE HELPERS — pure math, no side effects
        // =====================================================================

        /// <summary>
        /// Blends a role bias [0,1] with a player instinct value [0,1] using FreedomLevel.
        /// FreedomLevel=0 (Strict):      returns roleBias.
        /// FreedomLevel=1 (Expressive):  returns instinct.
        /// FreedomLevel=0.5:             returns average.
        /// </summary>
        private static float BlendRoleWithInstinct(float roleBias, float instinct,
                                                    float freedomLevel)
        {
            return roleBias + (instinct - roleBias) * freedomLevel;
        }

        /// <summary>
        /// Computes expected goals (xG) for a shot from the given position.
        /// Based on distance, angle to goal, and number of blocking defenders.
        /// Range: ~0.01 (long range, poor angle) to ~0.85 (open goal, close range).
        /// </summary>
        private static float ComputeXG(ref PlayerState player, Vec2 goalCentre,
                                        float distToGoal, MatchContext ctx)
        {
            // Bug 15 fix: normalise by SHOOT_MAX_RANGE, not SHOOT_OPTIMAL_RANGE.
            // Using OPTIMAL_RANGE as denominator compressed all shots beyond ~10m to near-zero xG,
            // making long-range efforts impossible and crowding attackers in the six-yard box.
            // With MAX_RANGE as denominator, the xG curve is distributed across the full
            // shooting range — shots from 25m (typical edge-of-box) get realistic ~0.05-0.10 xG.
            float distNorm = distToGoal / AIConstants.SHOOT_MAX_RANGE;
            float baseXG   = MathF.Exp(-distNorm * AIConstants.XG_DISTANCE_DECAY);

            // Angle factor: shots from central positions are better (0.5–1.0 range)
            float pitchCentreX = PhysicsConstants.PITCH_WIDTH * 0.5f;
            float angleOffset  = MathF.Abs(player.Position.X - pitchCentreX);
            float maxAngleOff  = PhysicsConstants.PITCH_WIDTH * 0.5f;
            float angleFactor  = 1f - (angleOffset / maxAngleOff) * 0.5f;

            // Shooting ability modifier (0.5–1.0 range)
            float abilityMod = 0.5f + player.ShootingAbility * 0.5f;

            float xG = baseXG * angleFactor * abilityMod;

            // Bug 16 fix: multiplicative decay per blocker (was additive, could go negative).
            // Old: xG -= blockers * penalty * xG → with 4 blockers at 0.25 each: xG = 0.
            // New: each blocker multiplies xG by (1 - penalty) → asymptotically approaches 0,
            // never reaches it. A penalty kick with 4 defenders in the lane still has xG > 0.
            int blockersInLane = CountBlockersInShootingLane(ref player, goalCentre,
                                                               player.TeamId, ctx);
            float blockMultiplier = MathF.Pow(1f - AIConstants.XG_DEFENDER_BLOCK_PENALTY,
                                               blockersInLane);
            xG *= blockMultiplier;

            return MathF.Max(0f, MathF.Min(1f, xG));
        }

        /// <summary>
        /// Computes the effective xG threshold below which a player won't shoot.
        /// Applies ego modifier — high-ego players accept lower-quality positions.
        /// </summary>
        private static float ComputeEffectiveXGThreshold(ref PlayerState player,
                                                           TacticsInput tactics)
        {
            float base_threshold = MathUtil.Lerp(AIConstants.XG_THRESHOLD_LOW,
                                               AIConstants.XG_THRESHOLD_HIGH,
                                               tactics.ShootingThreshold);
            float egoReduction = player.Ego * AIConstants.EGO_XG_THRESHOLD_REDUCTION
                                 * base_threshold;
            return MathF.Max(AIConstants.XG_THRESHOLD_LOW,
                              base_threshold - egoReduction);
        }

        /// <summary>
        /// Counts defenders between shooter and goal within DEFENDER_BLOCK_LANE_WIDTH.
        /// </summary>
        private static int CountBlockersInShootingLane(ref PlayerState player,
                                                         Vec2 goalCentre,
                                                         int attackingTeam,
                                                         MatchContext ctx)
        {
            int defStart = attackingTeam == 0 ? 11 : 0;
            int defEnd = attackingTeam == 0 ? 22 : 11;
            int count = 0;

            Vec2 laneDir = (goalCentre - player.Position).Normalized();

            for (int i = defStart; i < defEnd; i++)
            {
                if (!ctx.Players[i].IsActive) continue;
                Vec2 toOpp = ctx.Players[i].Position - player.Position;
                // Only opponents between shooter and goal (positive dot)
                float along = toOpp.Dot(laneDir);
                if (along < 0f) continue;
                // Lateral distance from shooting lane
                float lateral = MathF.Sqrt(MathF.Max(0f,
                    toOpp.LengthSquared() - along * along));
                if (lateral < AIConstants.DEFENDER_BLOCK_LANE_WIDTH)
                    count++;
            }
            return count;
        }

        /// <summary>Nearest opponent distance to this player.</summary>
        private static float FindNearestOpponentDistance(ref PlayerState player,
                                                          MatchContext ctx)
        {
            int oppStart = player.TeamId == 0 ? 11 : 0;
            int oppEnd = player.TeamId == 0 ? 22 : 11;
            float minDist = float.MaxValue;

            for (int i = oppStart; i < oppEnd; i++)
            {
                if (!ctx.Players[i].IsActive) continue;
                float d = player.Position.DistanceSquaredTo(ctx.Players[i].Position);
                if (d < minDist) minDist = d;
            }
            return minDist < float.MaxValue ? MathF.Sqrt(minDist) : 9999f;
        }

        /// <summary>Nearest opponent distance to a receiver — used for pressure scoring.</summary>
        private static float ComputeReceiverPressure(ref PlayerState receiver,
                                                      int receiverTeam,
                                                      MatchContext ctx)
        {
            int oppStart = receiverTeam == 0 ? 11 : 0;
            int oppEnd = receiverTeam == 0 ? 22 : 11;
            float minDist = float.MaxValue;

            for (int i = oppStart; i < oppEnd; i++)
            {
                if (!ctx.Players[i].IsActive) continue;
                float dSq = receiver.Position.DistanceSquaredTo(ctx.Players[i].Position);
                if (dSq < minDist) minDist = dSq;
            }
            float nearest = minDist < float.MaxValue ? MathF.Sqrt(minDist) : 9999f;

            // Pressure = 0 when far, 1 when within PASS_TIGHT_MARKING_RADIUS
            return MathF.Max(0f, 1f - nearest / AIConstants.PASS_TIGHT_MARKING_RADIUS);
        }

        /// <summary>
        /// Nearest opponent PRESSURE on the ball carrier, normalised to [0,1].
        /// Bug 9 fix: previously returned raw distance (0-9999). Now returns 0=no
        /// pressure (far away) to 1=maximum pressure (within tight-marking radius).
        /// Matches the same scale as ComputeReceiverPressure so both can be used
        /// together in ScorePass comparisons without unit mismatch.
        /// </summary>
        private static float ComputeSenderPressure(ref PlayerState sender, MatchContext ctx)
        {
            float dist = FindNearestOpponentDistance(ref sender, ctx);
            return MathF.Max(0f, 1f - dist / AIConstants.PASS_TIGHT_MARKING_RADIUS);
        }

        /// <summary>
        /// Selects pass speed based on distance and directness preference.
        /// </summary>
        private static float SelectPassSpeed(float dist, float directness, float longPassBias)
        {
            if (dist < AIConstants.PASS_SHORT_THRESHOLD)
                return PhysicsConstants.BALL_PASS_SPEED_SOFT;

            if (dist > AIConstants.PASS_LONG_THRESHOLD)
            {
                return MathUtil.Lerp(PhysicsConstants.BALL_PASS_SPEED_MED,
                                   PhysicsConstants.BALL_PASS_SPEED_HARD,
                                   (directness + longPassBias) * 0.5f);
            }

            return MathUtil.Lerp(PhysicsConstants.BALL_PASS_SPEED_SOFT,
                               PhysicsConstants.BALL_PASS_SPEED_HARD,
                               directness);
        }

        /// <summary>Counts crowding penalty at a target support position.</summary>
        private static float ComputeCrowdingPenalty(ref PlayerState player,
                                                      Vec2 target,
                                                      MatchContext ctx)
        {
            int teamStart = player.TeamId == 0 ? 0 : 11;
            int teamEnd = player.TeamId == 0 ? 11 : 22;
            float penalty = 0f;

            for (int i = teamStart; i < teamEnd; i++)
            {
                if (i == player.PlayerId) continue;
                if (!ctx.Players[i].IsActive) continue;
                float dSq = target.DistanceSquaredTo(ctx.Players[i].Position);
                float radius = AIConstants.SUPPORT_CROWDING_RADIUS;
                if (dSq < radius * radius)
                    penalty += AIConstants.SUPPORT_CROWDING_PENALTY;
            }
            return MathF.Min(1f, penalty);
        }

        /// <summary>
        /// Danger score for an opponent player. Used by ScoreTrack to find marking target.
        /// High danger = close to goal + making a run.
        /// </summary>
        private static float ComputeOpponentDanger(ref PlayerState opp,
                                                    int defensiveTeam,
                                                    MatchContext ctx)
        {
            // Bug 21 fix: danger should measure proximity to the GOAL BEING DEFENDED
            // (i.e., the defending team's goal, which the opponent is attacking toward).
            // defAttacksDown=true → home defends top (Y=0) when home attacks down.
            // So the defended goal is at PITCH_TOP when defAttacksDown=true.
            // This was already geometrically correct for first half — the fix ensures
            // it also works in the second half when AttacksDownward has been flipped.
            bool defAttacksDown = ctx.AttacksDownward(defensiveTeam);
            float oppGoalY = defAttacksDown
                ? PhysicsConstants.PITCH_TOP    // defending team's goal at top while they attack down
                : PhysicsConstants.PITCH_BOTTOM; // defending team's goal at bottom while they attack up

            float distToGoal = opp.Position.DistanceTo(
                new Vec2(PhysicsConstants.PITCH_WIDTH * 0.5f, oppGoalY));
            float maxDist = PhysicsConstants.PITCH_HEIGHT;

            float proximity = 1f - MathF.Min(1f, distToGoal / maxDist);
            float isRunning = opp.IsSprinting ? 0.3f : 0f;

            return MathF.Min(1f, proximity + isRunning);
        }

        /// <summary>Counts attackers from teamId inside or near the opponent penalty box.</summary>
        private static int CountAttackersInBox(int attackingTeam, MatchContext ctx)
        {
            int teamStart = attackingTeam == 0 ? 0 : 11;
            int teamEnd = attackingTeam == 0 ? 11 : 22;
            bool attacksDown = ctx.AttacksDownward(attackingTeam);
            int count = 0;

            float boxTopY = attacksDown
                ? PhysicsConstants.PITCH_HEIGHT - PhysicsConstants.PENALTY_AREA_DEPTH
                : 0f;
            float boxBottomY = attacksDown
                ? PhysicsConstants.PITCH_HEIGHT
                : PhysicsConstants.PENALTY_AREA_DEPTH;
            float boxLeftX = PhysicsConstants.PITCH_WIDTH * 0.5f
                               - PhysicsConstants.PENALTY_AREA_HALF_WIDTH;
            float boxRightX = PhysicsConstants.PITCH_WIDTH * 0.5f
                               + PhysicsConstants.PENALTY_AREA_HALF_WIDTH;

            for (int i = teamStart; i < teamEnd; i++)
            {
                if (!ctx.Players[i].IsActive) continue;
                Vec2 pos = ctx.Players[i].Position;
                if (pos.X >= boxLeftX && pos.X <= boxRightX &&
                    pos.Y >= boxTopY && pos.Y <= boxBottomY)
                    count++;
            }
            return count;
        }
    }
}
