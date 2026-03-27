// =============================================================================
// Module:  DecisionSystem.Targeting.cs
// Path:    FootballSim/Engine/Systems/DecisionSystem/DecisionSystem.Targeting.cs
// Purpose:
//   All target-position generation methods. These methods compute WHERE a player
//   should move — they are conceptually distinct from scoring methods which ask
//   HOW GOOD an option is.
//
//   Scoring asks:  "How good is this option?"   → float or scored struct
//   Targeting asks: "Where exactly should the player move?" → Vec2
//
//   This boundary is intentional and clean. Scorers call targeting methods to
//   obtain the Vec2 that goes into the returned ActionPlan.
//
// Methods defined here:
//   ComputeDribbleTarget(player, ctx)                    → Vec2
//   ComputeWidthTarget(player, ctx)                      → Vec2
//   ComputeSupportTarget(player, carrierPos, ctx)        → Vec2
//   ComputeRunInBehindTarget(player, ctx)                → Vec2
//   ComputeOverlapTarget(player, ctx)                    → Vec2
//   ComputeDropDeepTarget(player, ctx)                   → Vec2
//   ComputeDefensiveAnchor(player, tactics, ctx)         → Vec2
//   ComputeMarkSpaceTarget(player, tactics, ctx)         → Vec2
//
// Bug fixes applied (see DecisionSystemBugsAnalysis.md):
//   Bug 13 — cbCount==1 targets CB directly: now targets channel beside lone CB.
//   Bug 17 — ComputeGoalAimPoint uses HOME half-width always: fixed per attacksDown.
//   Bug 23 — ComputeSupportTarget SUPPORT_IDEAL_DISTANCE as X offset: now uses
//             scaled lateral offset (30-80 units) from AttackingWidth tactic.
//   Bug 26 — ComputeWidthTarget uses fixed FormationAnchor.Y: now blends 40% toward
//             ball Y so wide channel is held in the correct vertical zone.
//
// Notes:
//   • All methods are static and pure — no side effects.
//   • ComputeDefensiveAnchor and ComputeMarkSpaceTarget delegate to BlockShiftSystem.
//   • ComputeGoalAimPoint (used by ScoreShoot) also lives here as it is
//     a position-generation utility, not a scorer.
// =============================================================================

using System;
using FootballSim.Engine.Models;
using FootballSim.Engine.Tactics;

namespace FootballSim.Engine.Systems
{
    public static partial class DecisionSystem
    {
        // =====================================================================
        // TARGET POSITION GENERATORS
        // =====================================================================

        /// <summary>Computes an ideal dribble target: forward in movement direction.</summary>
        private static Vec2 ComputeDribbleTarget(ref PlayerState player, MatchContext ctx)
        {
            bool attacksDown = ctx.AttacksDownward(player.TeamId);
            Vec2 forward = attacksDown ? new Vec2(0f, 1f) : new Vec2(0f, -1f);

            // Slightly bias toward centre for IW, slightly outward for WF
            float xBias = (player.Role == PlayerRole.IW) ? -0.3f :
                           (player.Role == PlayerRole.WF) ? 0.3f : 0f;
            Vec2 dir = new Vec2(xBias, attacksDown ? 1f : -1f).Normalized();

            return player.Position + dir * AIConstants.DRIBBLE_MAX_SPACE_AHEAD * 0.5f;
        }

        /// <summary>Computes a holding-width target at the touchline for wide players.</summary>
        private static Vec2 ComputeWidthTarget(ref PlayerState player, MatchContext ctx)
        {
            bool isRightSide = player.FormationAnchor.X > PhysicsConstants.PITCH_WIDTH * 0.5f;
            float targetX = isRightSide
                ? PhysicsConstants.PITCH_WIDTH - 50f
                : 50f;

            // Bug 26 fix: FormationAnchor.Y is fixed pre-match — wide players held width
            // at their defensive Y even during attacks deep in opposition territory.
            // Blend 40% toward ball Y so the wide channel is maintained in the correct
            // vertical zone. Clamped to avoid extreme forward/backward positions.
            float ballY   = ctx.Ball.Position.Y;
            float anchorY = player.FormationAnchor.Y;
            float widthY  = MathUtil.Lerp(anchorY, ballY, 0.4f);
            widthY = Math.Clamp(widthY,
                PhysicsConstants.PITCH_TOP    + 50f,
                PhysicsConstants.PITCH_BOTTOM - 50f);

            return new Vec2(targetX, widthY);
        }

        /// <summary>
        /// Computes an ideal passing triangle support position relative to ball carrier.
        /// Offset is lateral and slightly behind the carrier for easy ball receipt.
        /// </summary>
        private static Vec2 ComputeSupportTarget(ref PlayerState player,
                                                   Vec2 carrierPos,
                                                   MatchContext ctx)
        {
            // Bug 23 fix: was using SUPPORT_IDEAL_DISTANCE (~80-120 units) as a raw X offset.
            // This placed support positions off the pitch edge for any central carrier.
            // Now uses a scaled lateral offset based on attacking width tactic (30-80 units).
            TacticsInput tactics = player.TeamId == 0 ? ctx.HomeTeam.Tactics : ctx.AwayTeam.Tactics;
            float lateralOffset = MathUtil.Lerp(30f, 80f, tactics.AttackingWidth);
            float xSide = player.FormationAnchor.X > PhysicsConstants.PITCH_WIDTH * 0.5f
                ? lateralOffset : -lateralOffset;

            bool attacksDown = ctx.AttacksDownward(player.TeamId);
            // Y offset: slightly behind carrier (toward own half) — easier ball receipt angle
            float yOffset = attacksDown ? -lateralOffset * 0.3f : lateralOffset * 0.3f;

            return new Vec2(
                Math.Clamp(carrierPos.X + xSide, PhysicsConstants.PITCH_LEFT + 30f,
                            PhysicsConstants.PITCH_RIGHT - 30f),
                Math.Clamp(carrierPos.Y + yOffset, PhysicsConstants.PITCH_TOP,
                            PhysicsConstants.PITCH_BOTTOM)
            );
        }

        /// <summary>Run-in-behind target: channel behind last defender line.</summary>
        /// <summary>
        /// Run-in-behind target. For striker/forward roles, targets the GAP BETWEEN
        /// the two opponent CBs rather than a fixed X anchor. This prevents the
        /// "striker merged with GK" problem by placing attackers between defenders,
        /// not deeper than the offside line.
        ///
        /// The offside line Y is read from BlockShiftSystem.ComputeOffsideLineY()
        /// so the target stays just behind (onside side of) the last defender.
        ///
        /// For non-forward roles (CM, AM): uses formation anchor X as before.
        /// </summary>
        private static Vec2 ComputeRunInBehindTarget(ref PlayerState player,
                                                       MatchContext ctx)
        {
            bool attacksDown = ctx.AttacksDownward(player.TeamId);
            int opponentTeam = player.TeamId == 0 ? 1 : 0;

            bool isForwardRole = player.Role == PlayerRole.ST ||
                                 player.Role == PlayerRole.CF ||
                                 player.Role == PlayerRole.PF ||
                                 player.Role == PlayerRole.IW ||
                                 player.Role == PlayerRole.WF ||
                                 player.Role == PlayerRole.AM;

            if (!isForwardRole)
            {
                // Non-forward: target just past midfield in own attacking direction
                float targetY = attacksDown
                    ? PhysicsConstants.PITCH_HEIGHT * 0.65f
                    : PhysicsConstants.PITCH_HEIGHT * 0.35f;
                return new Vec2(player.FormationAnchor.X, targetY);
            }

            // ── Forward: target the gap between opponent CBs ──────────────────
            // Step 1: find opponent CB positions
            int opStart = opponentTeam == 0 ? 0 : 11;
            int opEnd = opponentTeam == 0 ? 11 : 22;
            Vec2 cb1Pos = Vec2.Zero;
            Vec2 cb2Pos = Vec2.Zero;
            int cbCount = 0;

            for (int i = opStart; i < opEnd; i++)
            {
                if (!ctx.Players[i].IsActive) continue;
                PlayerRole r = ctx.Players[i].Role;
                if (r == PlayerRole.CB || r == PlayerRole.BPD)
                {
                    if (cbCount == 0) { cb1Pos = ctx.Players[i].Position; cbCount++; }
                    else if (cbCount == 1) { cb2Pos = ctx.Players[i].Position; cbCount++; }
                }
            }

            // Step 2: compute gap X between the two CBs (or use formation anchor if no CBs found)
            float targetX;
            if (cbCount == 2)
            {
                // Target the midpoint between the two CBs + slight offset toward own anchor side
                float gapX = (cb1Pos.X + cb2Pos.X) * 0.5f;
                float anchorX = player.FormationAnchor.X;
                // Blend toward anchor so wide forwards don't all collapse to centre
                targetX = MathUtil.Lerp(gapX, anchorX, 0.35f);
            }
            else if (cbCount == 1)
            {
                // Bug 13 fix: running straight at the lone CB is the worst possible run.
                // Target the channel to the side closer to this player's formation anchor.
                float channelOffset = player.FormationAnchor.X > cb1Pos.X ? 60f : -60f;
                targetX = cb1Pos.X + channelOffset;
            }
            else
            {
                targetX = player.FormationAnchor.X; // fallback
            }

            // Step 3: Y = just behind the offside line (on the onside side)
            // "Just behind" means slightly toward own half from the last defender.
            float offsideLineY = BlockShiftSystem.ComputeOffsideLineY(opponentTeam, ctx);
            float onsideOffset = attacksDown ? -15f : 15f; // 15 units = 1.5m behind line
            float runTargetY = offsideLineY + onsideOffset;

            // Clamp to reasonable attacking zone — don't run back too deep
            float minAttackY = attacksDown
                ? PhysicsConstants.PITCH_HEIGHT * 0.45f
                : 0f;
            float maxAttackY = attacksDown
                ? PhysicsConstants.PITCH_HEIGHT
                : PhysicsConstants.PITCH_HEIGHT * 0.55f;
            runTargetY = Math.Clamp(runTargetY, minAttackY, maxAttackY);
            targetX = Math.Clamp(targetX, 40f, PhysicsConstants.PITCH_WIDTH - 40f);

            return new Vec2(targetX, runTargetY);
        }

        /// <summary>Overlap target: wide position ahead of current wide player.</summary>
        private static Vec2 ComputeOverlapTarget(ref PlayerState player, MatchContext ctx)
        {
            bool isRight = player.FormationAnchor.X > PhysicsConstants.PITCH_WIDTH * 0.5f;
            float targetX = isRight
                ? PhysicsConstants.PITCH_WIDTH - 30f
                : 30f;
            bool attacksDown = ctx.AttacksDownward(player.TeamId);
            float targetY = attacksDown
                ? player.Position.Y + 150f
                : player.Position.Y - 150f;

            return new Vec2(targetX,
                Math.Clamp(targetY, PhysicsConstants.PITCH_TOP, PhysicsConstants.PITCH_BOTTOM));
        }

        /// <summary>Drop-deep target: position between ball carrier and own half.</summary>
        private static Vec2 ComputeDropDeepTarget(ref PlayerState player, MatchContext ctx)
        {
            bool attacksDown = ctx.AttacksDownward(player.TeamId);
            float deepY = attacksDown
                ? PhysicsConstants.PITCH_HEIGHT * 0.45f
                : PhysicsConstants.PITCH_HEIGHT * 0.55f;
            return new Vec2(player.FormationAnchor.X, deepY);
        }

        /// <summary>
        /// Computes defensive anchor adjusted for TacticsInput.DefensiveLine.
        /// Deep line = anchor near own goal. High line = anchor well up the pitch.
        /// </summary>
        /// <summary>
        /// Computes defensive anchor using the block shift template.
        /// Reads pre-computed shift offsets from ctx (written by BlockShiftSystem).
        /// X: shape slides toward ball, slot offsets preserved (shape maintained).
        /// Y: defensive line height from DefensiveLine tactic + role vertical offset.
        /// GK/SK: returns goal-line anchor unchanged.
        /// </summary>
        private static Vec2 ComputeDefensiveAnchor(ref PlayerState player,
                                                     TacticsInput tactics,
                                                     MatchContext ctx)
        {
            return BlockShiftSystem.ComputeShiftedTarget(ref player, ctx);
        }

        /// <summary>
        /// Zone mark target using the shifted defensive slot position.
        /// Compactness tightens slots toward the SHIFTED shape centre (not pitch centre).
        /// </summary>
        private static Vec2 ComputeMarkSpaceTarget(ref PlayerState player,
                                                    TacticsInput tactics,
                                                    MatchContext ctx)
        {
            Vec2 shiftedSlot = BlockShiftSystem.ComputeShiftedTarget(ref player, ctx);
            float shiftX = player.TeamId == 0 ? ctx.HomeShiftX : ctx.AwayShiftX;
            float shiftedCentreX = PhysicsConstants.PITCH_WIDTH * 0.5f + shiftX;
            float compactness = tactics.OutOfPossessionShape;
            float finalX = MathUtil.Lerp(shiftedSlot.X, shiftedCentreX, compactness * 0.25f);
            return new Vec2(finalX, shiftedSlot.Y);
        }

        /// <summary>
        /// Selects an aim point inside the goal, slightly offset from GK position.
        /// Simplified: aim at the corner opposite to the GK's lean.
        /// </summary>
        private static Vec2 ComputeGoalAimPoint(ref PlayerState player,
                                                  Vec2 goalCentre, MatchContext ctx)
        {
            // Find opponent GK
            int gkId = FindOpponentGK(player.TeamId, ctx);
            // Bug 17 fix: use correct goal half-width per attacking direction (was always HOME)
            bool aimAttacksDown = ctx.AttacksDownward(player.TeamId);
            float goalHalfWidth = aimAttacksDown
                ? (PhysicsConstants.AWAY_GOAL_RIGHT_X - PhysicsConstants.AWAY_GOAL_LEFT_X) * 0.5f
                : (PhysicsConstants.HOME_GOAL_RIGHT_X - PhysicsConstants.HOME_GOAL_LEFT_X) * 0.5f;

            if (gkId < 0)
                return goalCentre; // No GK found — aim centre

            float gkX = ctx.Players[gkId].Position.X;
            float centreX = goalCentre.X;

            // Aim at the post opposite to GK lean
            float offsetX = gkX > centreX
                ? -goalHalfWidth * 0.7f   // GK leans right → aim left post area
                : +goalHalfWidth * 0.7f;  // GK leans left  → aim right post area

            return new Vec2(goalCentre.X + offsetX, goalCentre.Y);
        }
    }
}
