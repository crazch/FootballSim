// =============================================================================
// Module:  DecisionSystem.Defense.cs
// Path:    FootballSim/Engine/Systems/DecisionSystem/DecisionSystem.Defense.cs
// Purpose:
//   Out-of-possession defensive behaviour scoring. Covers all situations where
//   the player's team does not have the ball.
//   This module answers: "How should the team behave without the ball?"
//
// Scoring methods defined here:
//   ScorePress(player, role, tactics, ctx)     → PressScore { targetPos, score }
//   ScoreTrack(player, role, tactics, ctx)     → TrackScore { targetPlayerId, targetPos, score }
//   ScoreRecover(player, role, tactics, ctx)   → float [0,1]
//   ScoreMarkSpace(player, role, tactics, ctx) → float [0,1]
//
// Bug fixes applied (see DecisionSystemBugsAnalysis.md):
//   Bug 18 — ScorePress returns 0 on loose ball: loose ball within triggerDist now
//             generates a press score targeting ball position.
//   Bug 21 — ComputeOpponentDanger uses wrong goal: comment clarified; logic already
//             correct via ctx.AttacksDownward which flips at half-time.
//   Bug 27 — ScoreRecover oscillation: threshold reduced from 0.5× to 0.3× radius.
//
// Notes:
//   • All methods are static and pure — no side effects.
//   • Called by EvaluateOutOfPossession in DecisionSystem.cs (the façade).
//   • Target positions are computed by helpers in DecisionSystem.Targeting.cs.
// =============================================================================

using System;
using FootballSim.Engine.Models;
using FootballSim.Engine.Tactics;

namespace FootballSim.Engine.Systems
{
    public static partial class DecisionSystem
    {
        // =====================================================================
        // OUT-OF-POSSESSION SCORERS
        // =====================================================================

        /// <summary>
        /// Scores pressing the ball carrier or a nearby opponent.
        /// Accounts for press trigger distance, stamina, role press bias, tactics.
        /// </summary>
        public static PressScore ScorePress(
            ref PlayerState player, RoleDefinition role, TacticsInput tactics, MatchContext ctx)
        {
            // Compute press trigger distance from tactics
            float triggerDist = MathUtil.Lerp(
                AIConstants.PRESS_TRIGGER_DIST_MIN,
                AIConstants.PRESS_TRIGGER_DIST_MAX,
                tactics.PressingIntensity
            );

            // Bug 18 fix: also handle loose ball — high-press teams should chase loose balls,
            // not collapse to formation anchors. A loose ball within triggerDist is a press target.
            if (ctx.Ball.Phase == BallPhase.Loose)
            {
                float distToBall = player.Position.DistanceTo(ctx.Ball.Position);
                if (distToBall > triggerDist)
                    return new PressScore { TargetPosition = player.FormationAnchor, Score = 0f, DistToCarrier = distToBall, PressTriggerDist = triggerDist };

                float staminaFactorLoose = player.Stamina >= AIConstants.PRESS_STAMINA_THRESHOLD
                    ? 1.0f : player.Stamina / AIConstants.PRESS_STAMINA_THRESHOLD;
                float proximityFactorLoose = 1f - (distToBall / triggerDist);
                float roleBiasLoose = role.PressBias;
                float blendedLoose  = BlendRoleWithInstinct(roleBiasLoose, player.Reactions, tactics.FreedomLevel);
                float scoreLoose    = MathF.Min(1f, blendedLoose * proximityFactorLoose * staminaFactorLoose
                                               + tactics.PressingIntensity * 0.2f);
                return new PressScore { TargetPosition = ctx.Ball.Position, Score = scoreLoose, DistToCarrier = distToBall, PressTriggerDist = triggerDist };
            }

            // Find press target: the ball carrier (owned ball)
            if (ctx.Ball.OwnerId < 0 || ctx.Ball.Phase != BallPhase.Owned)
                return new PressScore { TargetPosition = player.FormationAnchor, Score = 0f, DistToCarrier = 0f, PressTriggerDist = triggerDist };

            ref PlayerState carrier = ref ctx.Players[ctx.Ball.OwnerId];
            if (carrier.TeamId == player.TeamId)
                return new PressScore { TargetPosition = player.FormationAnchor, Score = 0f, DistToCarrier = 0f, PressTriggerDist = triggerDist }; // don't press your own team

            float distToCarrier = player.Position.DistanceTo(carrier.Position);

            // Outside trigger zone — don't press
            if (distToCarrier > triggerDist)
                return new PressScore { TargetPosition = player.FormationAnchor, Score = 0f, DistToCarrier = distToCarrier, PressTriggerDist = triggerDist };

            // Stamina gate: press willingness decays below collapse threshold
            float staminaFactor = player.Stamina >= AIConstants.PRESS_STAMINA_THRESHOLD
                ? 1.0f
                : player.Stamina / AIConstants.PRESS_STAMINA_THRESHOLD;

            // Proximity factor: closer = higher urgency
            float proximityFactor = 1f - (distToCarrier / triggerDist);

            float roleBias = role.PressBias;
            float instinct = player.Reactions; // reactions = pressing speed/willingness
            float blended = BlendRoleWithInstinct(roleBias, instinct, tactics.FreedomLevel);

            float tacticBonus = tactics.PressingIntensity * 0.2f;

            float score = MathF.Min(1f,
                blended * proximityFactor * staminaFactor + tacticBonus);

            if (DEBUG && (DEBUG_PLAYER_ID < 0 || DEBUG_PLAYER_ID == player.PlayerId))
                Console.WriteLine($"[DecisionSystem] Tick {ctx.Tick}: P{player.PlayerId} " +
                    $"PRESS score={score:F3} dist={distToCarrier:F0} trigger={triggerDist:F0} " +
                    $"stamina={player.Stamina:F2} factor={staminaFactor:F2}");

            return new PressScore
            {
                TargetPosition = carrier.Position,
                Score = score,
                DistToCarrier = distToCarrier,
                PressTriggerDist = triggerDist,
            };
        }

        /// <summary>
        /// Scores tracking the most dangerous opposing runner.
        /// Returns the target player ID and intercept position.
        /// </summary>
        public static TrackScore ScoreTrack(
            ref PlayerState player, RoleDefinition role, TacticsInput tactics, MatchContext ctx)
        {
            int opponentStart = player.TeamId == 0 ? 11 : 0;
            int opponentEnd = player.TeamId == 0 ? 22 : 11;

            float bestDanger = 0f;
            Vec2 bestTarget = player.FormationAnchor;
            int bestTargetPlayerId = -1;

            for (int i = opponentStart; i < opponentEnd; i++)
            {
                if (!ctx.Players[i].IsActive) continue;
                if (ctx.Players[i].HasBall) continue; // ball carrier is handled by Press

                ref PlayerState opp = ref ctx.Players[i];
                float dist = player.Position.DistanceTo(opp.Position);

                if (dist > AIConstants.MARK_SWITCH_TO_TRACKING_RADIUS) continue;

                float danger = ComputeOpponentDanger(ref opp, player.TeamId, ctx);
                if (danger > bestDanger && danger > AIConstants.MARK_DANGER_THRESHOLD)
                {
                    bestDanger = danger;
                    bestTargetPlayerId = opp.PlayerId;
                    // Anticipate: target is ahead of runner, not on them
                    Vec2 runDir = opp.Velocity.LengthSquared() > 0.01f
                        ? opp.Velocity.Normalized() : Vec2.Zero;
                    bestTarget = opp.Position + runDir * AIConstants.MARK_ANTICIPATION_OFFSET;
                }
            }

            if (bestDanger <= AIConstants.MARK_DANGER_THRESHOLD)
                return new TrackScore { TargetPosition = player.FormationAnchor, Score = 0f, TargetPlayerId = -1 };

            float roleBias = role.TackleCommitTendency;
            float blended = BlendRoleWithInstinct(roleBias, player.DefendingAbility,
                                                    tactics.FreedomLevel);

            return new TrackScore
            {
                TargetPosition = bestTarget,
                Score = MathF.Min(1f, blended * bestDanger),
                TargetPlayerId = bestTargetPlayerId,
            };
        }

        /// <summary>
        /// Scores recovering back to defensive shape after losing possession.
        /// Urgency is proportional to how far ball has penetrated.
        /// </summary>
        public static float ScoreRecover(ref PlayerState player,
                                          RoleDefinition role,
                                          TacticsInput tactics,
                                          MatchContext ctx)
        {
            Vec2 defensiveAnchor = ComputeDefensiveAnchor(ref player, tactics, ctx);
            float distFromAnchor = player.Position.DistanceTo(defensiveAnchor);

            // Bug 27 fix: was 0.5× threshold, causing oscillation as player drifted
            // just above/below it on alternate ticks (sprint back → arrive → mark → drift → sprint).
            // Reduced to 0.3× threshold so ScoreRecover suppresses only when clearly in position,
            // reducing the chattering window. A full hysteresis state flag on PlayerState would
            // be the complete fix but is deferred; this is a reliable 80% solution.
            if (distFromAnchor < AIConstants.ANCHOR_PULL_RADIUS * 0.3f) return 0f;

            float urgency = MathF.Min(1f, distFromAnchor / (AIConstants.ANCHOR_PULL_RADIUS * 2f));
            float roleBias = role.OutOfPossessionReturnY;
            float blended = BlendRoleWithInstinct(roleBias, player.Reactions, tactics.FreedomLevel);

            return MathF.Min(1f, blended * urgency);
        }

        /// <summary>
        /// Scores holding a defensive zone (zonal marking).
        /// Low-urgency fallback when no pressing or tracking is triggered.
        /// </summary>
        public static float ScoreMarkSpace(ref PlayerState player,
                                            RoleDefinition role,
                                            TacticsInput tactics,
                                            MatchContext ctx)
        {
            // Higher with OutOfPossessionShape (compact shape)
            float shapeScore = tactics.OutOfPossessionShape * 0.6f;
            float roleBias = role.HoldPositionBias;
            float blended = BlendRoleWithInstinct(roleBias, 0.4f, tactics.FreedomLevel);

            return MathF.Min(1f, blended * 0.5f + shapeScore * 0.5f);
        }
    }
}
