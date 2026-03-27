// =============================================================================
// Module:  DecisionSystem.OffBall.cs
// Path:    FootballSim/Engine/Systems/DecisionSystem/DecisionSystem.OffBall.cs
// Purpose:
//   Attacking movement scoring for players whose team has possession but who
//   do not currently hold the ball themselves.
//   This module answers: "How should an off-ball teammate support the attack?"
//
// Scoring methods defined here:
//   ScoreSupportRun(player, role, tactics, ctx)   → (Vec2 TargetPosition, float Score)
//   ScoreMakeRun(player, role, tactics, ctx)       → (Vec2 TargetPosition, float Score)
//   ScoreOverlapRun(player, role, tactics, ctx)    → (Vec2 TargetPosition, float Score)
//   ScoreDropDeep(player, role, tactics, ctx)      → (Vec2 TargetPosition, float Score)
//   ScoreHoldWidth(player, role, tactics, ctx)     → float [0,1]
//   ScoreReturnToAnchor(player, role, tactics, ctx)→ float [0,1]
//
// Bug fixes applied (see DecisionSystemBugsAnalysis.md):
//   Bug 11 — ScoreMakeRun uses DribblingAbility as pace proxy: now uses BaseSpeed
//             normalised by PLAYER_SPRINT_SPEED.
//   Bug 14 — ScoreOverlapRun divide by zero: guarded with Clamp01.
//   Bug 22 — ScoreHoldWidth hardcoded 0.5: now blends pace + passing ability.
//   Bug 23 — ComputeSupportTarget SUPPORT_IDEAL_DISTANCE as X offset: now uses
//             scaled lateral offset (30-80 units) from AttackingWidth tactic.
//
// Notes:
//   • All methods are static and pure — no side effects.
//   • Called by EvaluateWithoutBall in DecisionSystem.cs (the façade).
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
        // WITHOUT-BALL, IN-POSSESSION SCORERS
        // =====================================================================

        /// <summary>
        /// Scores moving to a passing triangle support position relative to the ball carrier.
        /// Rewards spacing, penalises crowding teammates.
        /// </summary>
        public static (Vec2 TargetPosition, float Score) ScoreSupportRun(
            ref PlayerState player, RoleDefinition role, TacticsInput tactics, MatchContext ctx)
        {
            if (ctx.Ball.OwnerId < 0) return (player.FormationAnchor, 0f);

            ref PlayerState carrier = ref ctx.Players[ctx.Ball.OwnerId];
            Vec2 carrierPos = carrier.Position;

            // Compute ideal support position: at SUPPORT_IDEAL_DISTANCE from carrier,
            // slightly backward from carrier's forward direction, on this player's side
            Vec2 supportTarget = ComputeSupportTarget(ref player, carrierPos, ctx);

            float distToIdeal = player.Position.DistanceTo(supportTarget);
            float distScore = 1f - MathF.Min(1f, distToIdeal / AIConstants.SUPPORT_IDEAL_DISTANCE);

            // Penalise if crowded with teammates
            float crowdPenalty = ComputeCrowdingPenalty(ref player, supportTarget, ctx);

            float roleBias = role.SupportBias;
            float instinct = player.PassingAbility;
            float blended = BlendRoleWithInstinct(roleBias, instinct, tactics.FreedomLevel);
            float tacticBonus = tactics.PossessionFocus * 0.2f;

            float score = MathF.Max(0f,
                MathF.Min(1f, blended * distScore + tacticBonus - crowdPenalty));

            return (supportTarget, score);
        }

        /// <summary>
        /// Scores making a run in behind the defensive line.
        /// Rewards roles with high RunInBehindTendency (ST, IW, PF).
        /// </summary>
        public static (Vec2 TargetPosition, float Score) ScoreMakeRun(
            ref PlayerState player, RoleDefinition role, TacticsInput tactics, MatchContext ctx)
        {
            float roleBias = role.RunInBehindTendency;
            // Bug 11 fix: use normalised BaseSpeed as proxy for pace, not DribblingAbility.
            // DribblingAbility measures ball-control; pace/running ability governs run-in-behind.
            // A target man with high BaseSpeed but average dribbling should score high here.
            float instinct = PhysicsConstants.PLAYER_SPRINT_SPEED > 0f
                ? MathF.Min(1f, player.BaseSpeed / PhysicsConstants.PLAYER_SPRINT_SPEED)
                : 0.5f;
            float blended = BlendRoleWithInstinct(roleBias, instinct, tactics.FreedomLevel);

            // Only viable if team has the ball and there's a runner role
            if (blended < 0.2f) return (player.FormationAnchor, 0f);

            Vec2 runTarget = ComputeRunInBehindTarget(ref player, ctx);
            float tacticBonus = tactics.AttackingLine * 0.25f;

            return (runTarget, MathF.Min(1f, blended + tacticBonus));
        }

        /// <summary>
        /// Scores an overlap run past the wide player.
        /// Rewards WB, RB, LB, BBM roles.
        /// </summary>
        public static (Vec2 TargetPosition, float Score) ScoreOverlapRun(
            ref PlayerState player, RoleDefinition role, TacticsInput tactics, MatchContext ctx)
        {
            float roleBias = role.OverlapRunTendency;
            // Bug 14 fix: guard against PLAYER_SPRINT_SPEED==0 and clamp instinct to [0,1]
            float instinct = PhysicsConstants.PLAYER_SPRINT_SPEED > 0f
                ? MathF.Min(1f, player.BaseSpeed / PhysicsConstants.PLAYER_SPRINT_SPEED)
                : 0.5f;
            float blended = BlendRoleWithInstinct(roleBias, instinct, tactics.FreedomLevel);

            if (blended < 0.2f) return (player.FormationAnchor, 0f);

            Vec2 overlapTarget = ComputeOverlapTarget(ref player, ctx);
            float tacticBonus = tactics.AttackingWidth * 0.2f;

            return (overlapTarget, MathF.Min(1f, blended + tacticBonus));
        }

        /// <summary>
        /// Scores dropping deep to receive (False Nine, DLP, CF behaviour).
        /// </summary>
        public static (Vec2 TargetPosition, float Score) ScoreDropDeep(
            ref PlayerState player, RoleDefinition role, TacticsInput tactics, MatchContext ctx)
        {
            float roleBias = role.DropDeepTendency;
            float instinct = player.PassingAbility;
            float blended = BlendRoleWithInstinct(roleBias, instinct, tactics.FreedomLevel);

            if (blended < 0.15f) return (player.FormationAnchor, 0f);

            Vec2 deepTarget = ComputeDropDeepTarget(ref player, ctx);
            float tacticBonus = (1f - tactics.AttackingLine) * 0.2f;

            return (deepTarget, MathF.Min(1f, blended + tacticBonus));
        }

        /// <summary>
        /// Scores holding width when team is in possession.
        /// Rewards WB, WF, IW (before drifting), RB, LB roles.
        /// </summary>
        public static float ScoreHoldWidth(ref PlayerState player,
                                            RoleDefinition role,
                                            TacticsInput tactics,
                                            MatchContext ctx)
        {
            float roleBias = role.WidthBias;
            // Bug 22 fix: instinct was hardcoded 0.5 — player ability had zero influence.
            // Now blends pace (for covering ground) with passing ability (for crossing threat).
            float instinct = PhysicsConstants.PLAYER_SPRINT_SPEED > 0f
                ? MathUtil.Lerp(MathF.Min(1f, player.BaseSpeed / PhysicsConstants.PLAYER_SPRINT_SPEED),
                                player.PassingAbility, 0.5f)
                : 0.5f;
            float blended = BlendRoleWithInstinct(roleBias, instinct, tactics.FreedomLevel);
            float tacticBonus = tactics.AttackingWidth * 0.25f;
            return MathF.Min(1f, blended * 0.6f + tacticBonus);
        }

        /// <summary>
        /// Scores returning to formation anchor. Stronger when player is far from anchor.
        /// Controlled by HoldPositionBias and FreedomLevel.
        /// </summary>
        public static float ScoreReturnToAnchor(ref PlayerState player,
                                                  RoleDefinition role,
                                                  TacticsInput tactics,
                                                  MatchContext ctx)
        {
            float distFromAnchor = player.Position.DistanceTo(player.FormationAnchor);

            // Always give a baseline for returning to anchor (shape discipline)
            float distFactor = MathF.Min(1f,
                distFromAnchor / AIConstants.ANCHOR_PULL_RADIUS);

            float roleBias = role.HoldPositionBias;
            // At FreedomLevel=0 (Strict): role bias fully applied.
            // At FreedomLevel=1 (Expressive): role bias has less pull.
            float discipline = BlendRoleWithInstinct(roleBias, 0.2f, tactics.FreedomLevel);

            return MathF.Min(1f,
                discipline * distFactor * AIConstants.ANCHOR_RETURN_WEIGHT);
        }
    }
}
