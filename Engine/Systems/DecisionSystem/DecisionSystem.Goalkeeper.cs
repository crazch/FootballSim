// =============================================================================
// Module:  DecisionSystem.Goalkeeper.cs
// Path:    FootballSim/Engine/Systems/DecisionSystem/DecisionSystem.Goalkeeper.cs
// Purpose:
//   Goalkeeper-specific logic: positional anchoring, loose-ball claiming,
//   distribution decisions, and clearance fallback.
//   GK logic is kept separate because goalkeeper behaviour is a special case
//   with its own constraints, priorities, and decision flow that does not fit
//   cleanly into the general outfield scoring pipeline.
//
// Methods defined here:
//   FindGKClearanceTarget(gk, ctx)   → int (PlayerId of clearance target, -1 if none)
//   FindOpponentGK(attackingTeam, ctx) → int (PlayerId of opponent GK, -1 if none)
//   ComputeGKAnchor(player, ctx)     → Vec2 (goal-line anchor position)
//
// Bug fixes applied (see DecisionSystemBugsAnalysis.md):
//   Bug 7  — GK holds when no pass found: FindGKClearanceTarget provides a long-kick
//             fallback to the most advanced teammate.
//   Bug 17 — ComputeGoalAimPoint uses HOME half-width always: fixed per attacksDown.
//             (aim point uses FindOpponentGK from this module)
//   Bug 25 — GK rushes loose ball only after LooseTicks delay: now rushes immediately
//             if within GK_CLAIM_RADIUS, regardless of LooseTicks.
//             (rush logic lives in EvaluateGK in DecisionSystem.cs)
//
// Notes:
//   • All methods are static and pure — no side effects.
//   • EvaluateGK (the GK entry point) lives in DecisionSystem.cs (the façade)
//     because it is a top-level evaluator called directly by PlayerAI.
//   • FindOpponentGK is also consumed by ComputeGoalAimPoint in Targeting.cs.
// =============================================================================

using FootballSim.Engine.Models;
using FootballSim.Engine.Tactics;

namespace FootballSim.Engine.Systems
{
    public static partial class DecisionSystem
    {
        // =====================================================================
        // GOALKEEPER HELPERS
        // =====================================================================

        /// <summary>
        /// Bug 7 fix: finds the most advanced teammate for a GK clearance kick.
        /// "Most advanced" = furthest from the GK's own goal in the attacking direction.
        /// Excludes the GK themselves. Returns -1 if no teammate found.
        /// </summary>
        private static int FindGKClearanceTarget(ref PlayerState gk, MatchContext ctx)
        {
            int teamStart = gk.TeamId == 0 ? 0 : 11;
            int teamEnd   = gk.TeamId == 0 ? 11 : 22;
            bool attacksDown = ctx.AttacksDownward(gk.TeamId);

            int   bestId   = -1;
            float bestY    = attacksDown ? float.MaxValue : float.MinValue;

            for (int i = teamStart; i < teamEnd; i++)
            {
                if (!ctx.Players[i].IsActive) continue;
                if (ctx.Players[i].PlayerId == gk.PlayerId) continue;
                if (ctx.Players[i].Role == PlayerRole.GK ||
                    ctx.Players[i].Role == PlayerRole.SK) continue;

                float y = ctx.Players[i].Position.Y;
                if (attacksDown ? y < bestY : y > bestY)
                {
                    bestY = y;
                    bestId = ctx.Players[i].PlayerId;
                }
            }
            return bestId;
        }

        /// <summary>Finds the PlayerId of the opponent GK. Returns -1 if not found.</summary>
        private static int FindOpponentGK(int attackingTeam, MatchContext ctx)
        {
            int gkStart = attackingTeam == 0 ? 11 : 0;
            int gkEnd = attackingTeam == 0 ? 22 : 11;

            for (int i = gkStart; i < gkEnd; i++)
            {
                if (!ctx.Players[i].IsActive) continue;
                if (ctx.Players[i].Role == PlayerRole.GK ||
                    ctx.Players[i].Role == PlayerRole.SK)
                    return i;
            }
            return -1;
        }

        /// <summary>GK anchor position: on goal line, centred, slightly off line for SK.</summary>
        private static Vec2 ComputeGKAnchor(ref PlayerState player, MatchContext ctx)
        {
            float centreX = PhysicsConstants.PITCH_WIDTH * 0.5f;
            bool isHome = player.TeamId == 0;
            float lineY = isHome ? PhysicsConstants.HOME_GOAL_LINE_Y + 20f
                                   : PhysicsConstants.AWAY_GOAL_LINE_Y - 20f;

            // SK pushes further from goal line
            float skOffset = player.Role == PlayerRole.SK ? 60f : 0f;
            lineY += isHome ? skOffset : -skOffset;

            return new Vec2(centreX, lineY);
        }
    }
}
