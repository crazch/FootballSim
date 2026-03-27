// =============================================================================
// Module:  DecisionSystem.cs
// Path:    FootballSim/Engine/Systems/DecisionSystem/DecisionSystem.cs
// Purpose:
//   Public façade and top-level orchestrator. Exposes only the five entry-point
//   evaluators that PlayerAI calls each decision cycle. Delegates all scoring,
//   targeting, helper math, and debug logging to the other partial modules.
//
//   This file answers: "Which decision branch do we use?"
//   It does NOT answer: "How do we calculate every tiny thing?"
//
// Entry points (called by PlayerAI):
//   EvaluateWithBall(player, ctx)         → ActionPlan
//   EvaluateWithoutBall(player, ctx)      → ActionPlan
//   EvaluateOutOfPossession(player, ctx)  → ActionPlan
//   EvaluateLooseBall(player, ctx)        → ActionPlan
//   EvaluateGK(player, ctx)              → ActionPlan
//
// Architecture:
//   public static partial class DecisionSystem
//   Spread across:
//     DecisionSystem.cs           ← this file (façade / orchestrator)
//     DecisionSystem.Types.cs     ← result structs
//     DecisionSystem.WithBall.cs  ← ball-carrier scorers
//     DecisionSystem.OffBall.cs   ← in-possession off-ball scorers
//     DecisionSystem.Defense.cs   ← out-of-possession scorers
//     DecisionSystem.Goalkeeper.cs← GK-specific logic
//     DecisionSystem.Targeting.cs ← target-position generation
//     DecisionSystem.Helpers.cs   ← shared math utilities
//     DecisionSystem.Debug.cs     ← verbose decision logging
//
// Bug fixes applied (see DecisionSystemBugsAnalysis.md):
//   Bug 3  — EvaluateOutOfPossession mutates player.IsSprinting: all IsSprinting
//             assignments moved to ActionPlan.ShouldSprint. PlayerAI must apply it.
//   Bug 6  — EvaluateLooseBall uses WalkingToAnchor for chase: now uses Pressing
//             (sprint-eligible action).
//   Bug 7  — GK holds when no pass found: FindGKClearanceTarget provides a long-kick
//             fallback to the most advanced teammate.
//   Bug 25 — GK rushes loose ball only after LooseTicks delay: now rushes immediately
//             if within GK_CLAIM_RADIUS, regardless of LooseTicks.
//   Bug 28 — DebugLogWithBall logs "Tick ?": ctx.Tick now passed through correctly.
//
// Required additions outside this file:
//   AIConstants.cs    — add: XG_DISTANCE_DECAY = 2.5f
//                             DISABLE_LONG_PASS_OVERRIDE (already used in WithBall)
//   MatchContext.cs   — add: bool AttacksDownward(int teamId)
//   PlayerAI.cs       — after EvaluateOutOfPossession: player.IsSprinting = plan.ShouldSprint;
//
// Dependencies:
//   Engine/Models/MatchContext.cs
//   Engine/Models/PlayerState.cs
//   Engine/Models/BallState.cs
//   Engine/Models/Vec2.cs
//   Engine/Models/Enums.cs
//   Engine/Systems/AIConstants.cs
//   Engine/Systems/PhysicsConstants.cs
//   Engine/Tactics/RoleDefinition.cs
//   Engine/Tactics/RoleRegistry.cs
//   Engine/Tactics/TacticsInput.cs
//
// Notes:
//   • All methods are static and pure — same inputs always produce same outputs.
//   • No Random calls inside scoring methods. Randomness only in PlayerAI.
//   • DEBUG flag: when true, logs winning action + full score breakdown per player
//     per decision cycle. Very verbose — only enable for one player at a time via
//     DEBUG_PLAYER_ID filter.
// =============================================================================

using System;
using FootballSim.Engine.Models;
using FootballSim.Engine.Tactics;

namespace FootballSim.Engine.Systems
{
    public static partial class DecisionSystem
    {
        // ── DEBUG ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Master debug toggle. When true, enables verbose decision logging.
        /// WARNING: produces enormous output. Use DEBUG_PLAYER_ID to filter.
        /// </summary>
        public static bool DEBUG = false;

        /// <summary>
        /// When DEBUG=true, only log decisions for this PlayerId.
        /// -1 = log all players (very expensive). Set to a specific player index.
        /// </summary>
        public static int DEBUG_PLAYER_ID = -1;

        // =====================================================================
        // TOP-LEVEL EVALUATORS — entry points called by PlayerAI
        // =====================================================================

        /// <summary>
        /// Full evaluation for a player who currently has the ball.
        /// Scores: Shoot, Pass (all receivers), Dribble, Cross, Hold.
        /// Returns the highest-scoring ActionPlan.
        /// </summary>
        public static ActionPlan EvaluateWithBall(ref PlayerState player,
                                                   MatchContext ctx)
        {
            RoleDefinition role = RoleRegistry.Get(player.Role);
            TacticsInput tactics = player.TeamId == 0
                ? ctx.HomeTeam.Tactics : ctx.AwayTeam.Tactics;

            ActionPlan best = default;
            best.PassReceiverId = -1;
            best.Score = -1f;

            // Keep references to all scores for breakdown storage
            ShotScore shot = default;
            PassCandidate bestPass = default;
            float crossScore = -1f;
            float dribScore = -1f;
            float holdScore = -1f;

            // ── Score each option ──────────────────────────────────────────────

            // 1. SHOOT
            shot = ScoreShoot(ref player, role, tactics, ctx);
            if (shot.Score > best.Score)
            {
                best.Score = shot.Score;
                best.Action = PlayerAction.Shooting;
                best.TargetPosition = player.Position; // shooter stays put
                best.ShotTargetPos = shot.TargetPosition;
                best.XG = shot.XG;
                best.PassReceiverId = -1;
            }

            // 2. PASS (best receiver)
            bestPass = ScoreBestPass(ref player, role, tactics, ctx);
            if (bestPass.Score > best.Score && bestPass.ReceiverId >= 0)
            {
                best.Score = bestPass.Score;
                best.Action = PlayerAction.Passing;
                best.TargetPosition = player.Position; // passer stays put on kick tick
                best.PassReceiverId = bestPass.ReceiverId;
                best.PassSpeed = bestPass.PassSpeed;
                best.PassHeight = bestPass.PassHeight;
            }

            // 3. CROSS (if wide enough)
            crossScore = ScoreCross(ref player, role, tactics, ctx);
            if (crossScore > best.Score)
            {
                best.Score = crossScore;
                best.Action = PlayerAction.Crossing;
                best.TargetPosition = player.Position;
                best.PassReceiverId = -1;
            }

            // 4. DRIBBLE
            dribScore = ScoreDribble(ref player, role, tactics, ctx);
            if (dribScore > best.Score)
            {
                best.Score = dribScore;
                best.Action = PlayerAction.Dribbling;
                best.TargetPosition = ComputeDribbleTarget(ref player, ctx);
                best.PassReceiverId = -1;
            }

            // 5. HOLD
            holdScore = ScoreHold(ref player, role, tactics, ctx);
            if (holdScore > best.Score)
            {
                best.Score = holdScore;
                best.Action = PlayerAction.Holding;
                best.TargetPosition = player.Position;
                best.PassReceiverId = -1;
            }

            // Fallback: if somehow all scores are negative, hold the ball
            if (best.Score < 0f)
            {
                best.Action = PlayerAction.Holding;
                best.TargetPosition = player.Position;
                best.Score = 0f;
            }

            // ── Populate with-ball score breakdown for ActionPlan ──────────────
            best.HasScoreBreakdown = true;
            best.ScoreShoot = shot.Score;
            best.ScorePass = bestPass.Score;
            best.ScoreDribble = dribScore;
            best.ScoreCross = crossScore;
            best.ScoreHold = holdScore;

            // Shoot detail (already computed in ScoreShoot):
            best.ShootXG = shot.XG;
            best.ShootEffectiveThreshold = shot.EffectiveThreshold;
            best.ShootBlockersInLane = shot.BlockersInLane;
            best.ShootDistToGoal = shot.DistToGoal;

            // Pass detail:
            best.PassIsProgressive = bestPass.IsProgressive;
            best.PassDistance = bestPass.Distance;
            best.PassReceiverPressure = bestPass.ReceiverPressure;

            DebugLogWithBall(ref player, ref best, shot, bestPass, crossScore, dribScore, holdScore, ctx.Tick); // Bug 28 fix
            return best;
        }

        /// <summary>
        /// Full evaluation for a player without the ball while team has possession.
        /// Scores: SupportRun, MakeRun, OverlapRun, DropDeep, HoldWidth, WalkToAnchor.
        /// Returns the highest-scoring ActionPlan.
        /// </summary>
        public static ActionPlan EvaluateWithoutBall(ref PlayerState player,
                                                      MatchContext ctx)
        {
            RoleDefinition role = RoleRegistry.Get(player.Role);
            TacticsInput tactics = player.TeamId == 0
                ? ctx.HomeTeam.Tactics : ctx.AwayTeam.Tactics;

            ActionPlan best = default;
            best.PassReceiverId = -1;
            best.Score = -1f;

            // ── In-possession off-ball options ────────────────────────────────

            // 1. MAKE RUN (in behind)
            var makeRun = ScoreMakeRun(ref player, role, tactics, ctx);
            if (makeRun.Score > best.Score)
            {
                best.Score = makeRun.Score;
                best.Action = PlayerAction.MakingRun;
                best.TargetPosition = makeRun.TargetPosition;
            }

            // 2. OVERLAP RUN
            var overlapRun = ScoreOverlapRun(ref player, role, tactics, ctx);
            if (overlapRun.Score > best.Score)
            {
                best.Score = overlapRun.Score;
                best.Action = PlayerAction.OverlapRun;
                best.TargetPosition = overlapRun.TargetPosition;
            }

            // 3. DROP DEEP
            var dropDeep = ScoreDropDeep(ref player, role, tactics, ctx);
            if (dropDeep.Score > best.Score)
            {
                best.Score = dropDeep.Score;
                best.Action = PlayerAction.DroppingDeep;
                best.TargetPosition = dropDeep.TargetPosition;
            }

            // 4. HOLD WIDTH
            float holdWidthScore = ScoreHoldWidth(ref player, role, tactics, ctx);
            Vec2 widthTarget = ComputeWidthTarget(ref player, ctx);
            if (holdWidthScore > best.Score)
            {
                best.Score = holdWidthScore;
                best.Action = PlayerAction.HoldingWidth;
                best.TargetPosition = widthTarget;
            }

            // 5. SUPPORT RUN (passing triangle)
            var supportRun = ScoreSupportRun(ref player, role, tactics, ctx);
            if (supportRun.Score > best.Score)
            {
                best.Score = supportRun.Score;
                best.Action = PlayerAction.PositioningSupport;
                best.TargetPosition = supportRun.TargetPosition;
            }

            // 6. RETURN TO ANCHOR (shape discipline)
            float anchorScore = ScoreReturnToAnchor(ref player, role, tactics, ctx);
            if (anchorScore > best.Score)
            {
                best.Score = anchorScore;
                best.Action = PlayerAction.WalkingToAnchor;
                best.TargetPosition = player.FormationAnchor;
            }

            // Fallback
            if (best.Score < 0f)
            {
                best.Action = PlayerAction.WalkingToAnchor;
                best.TargetPosition = player.FormationAnchor;
                best.Score = 0f;
            }

            DebugLogWithoutBall(ref player, ref best);
            return best;
        }

        /// <summary>
        /// Full evaluation for a player when their team does NOT have the ball.
        /// Scores: Press, Track, Recover, MarkSpace, Block.
        /// </summary>
        public static ActionPlan EvaluateOutOfPossession(ref PlayerState player,
                                                          MatchContext ctx)
        {
            RoleDefinition role = RoleRegistry.Get(player.Role);
            TacticsInput tactics = player.TeamId == 0
                ? ctx.HomeTeam.Tactics : ctx.AwayTeam.Tactics;

            ActionPlan best = default;
            best.PassReceiverId = -1;
            best.Score = -1f;

            // Keep track of scores for breakdown storage
            PressScore press = default;
            TrackScore track = default;
            float recoverScore = -1f;
            float markScore = -1f;

            // 1. PRESS
            press = ScorePress(ref player, role, tactics, ctx);
            if (press.Score > best.Score)
            {
                best.Score = press.Score;
                best.Action = PlayerAction.Pressing;
                best.TargetPosition = press.TargetPosition;
                best.ShouldSprint = true; // Bug 3 fix: was player.IsSprinting = true
            }

            // 2. TRACK runner
            track = ScoreTrack(ref player, role, tactics, ctx);
            if (track.Score > best.Score)
            {
                best.Score = track.Score;
                best.Action = PlayerAction.Tracking;
                best.TargetPosition = track.TargetPosition;
                best.ShouldSprint = track.Score > 0.6f; // Bug 3 fix: was player.IsSprinting
            }

            // 3. RECOVER to shape
            recoverScore = ScoreRecover(ref player, role, tactics, ctx);
            if (recoverScore > best.Score)
            {
                best.Score = recoverScore;
                best.Action = PlayerAction.Recovering;
                best.TargetPosition = ComputeDefensiveAnchor(ref player, tactics, ctx);
                best.ShouldSprint = true; // Bug 3 fix: was player.IsSprinting = true
            }

            // 4. MARK SPACE (zone cover)
            markScore = ScoreMarkSpace(ref player, role, tactics, ctx);
            Vec2 markTarget = ComputeMarkSpaceTarget(ref player, tactics, ctx);
            if (markScore > best.Score)
            {
                best.Score = markScore;
                best.Action = PlayerAction.MarkingSpace;
                best.TargetPosition = markTarget;
                best.ShouldSprint = false; // Bug 3 fix: was player.IsSprinting = false
            }

            // Fallback: return to defensive anchor
            if (best.Score < 0f)
            {
                best.Action = PlayerAction.Recovering;
                best.TargetPosition = ComputeDefensiveAnchor(ref player, tactics, ctx);
                best.Score = 0f;
            }

            // ── Populate defensive context breakdown for ActionPlan ────────────
            best.HasDefensiveContext = true;
            best.ScorePress = press.Score;
            best.ScoreTrack = track.Score;
            best.ScoreRecover = recoverScore;
            best.ScoreMarkSpace = markScore;
            best.TrackTargetPlayerId = track.TargetPlayerId;
            best.DistToCarrier = press.DistToCarrier;
            best.PressTriggerDist = press.PressTriggerDist;

            DebugLogOutOfPossession(ref player, ref best);
            return best;
        }

        /// <summary>
        /// Evaluation for a player when the ball is LOOSE.
        /// Any player near the loose ball may attempt to claim it.
        /// Returns WalkingToAnchor if too far away.
        /// </summary>
        public static ActionPlan EvaluateLooseBall(ref PlayerState player,
                                                    MatchContext ctx)
        {
            float distToBall = player.Position.DistanceTo(ctx.Ball.Position);

            if (distToBall > AIConstants.LOOSE_BALL_CHASE_RADIUS)
            {
                // Too far — return to shape
                return new ActionPlan
                {
                    Action = PlayerAction.WalkingToAnchor,
                    TargetPosition = player.FormationAnchor,
                    PassReceiverId = -1,
                    Score = 0.1f,
                };
            }

            // Chase the loose ball — score inversely by distance
            // Nearest player gets highest score
            float proximityScore = 1.0f - (distToBall / AIConstants.LOOSE_BALL_CHASE_RADIUS);
            proximityScore += AIConstants.LOOSE_BALL_PROXIMITY_BONUS
                             * (distToBall < PhysicsConstants.BALL_CONTEST_RADIUS ? 1f : 0f);

            // Bug 6 fix: use Pressing action (sprint-eligible) not WalkingToAnchor.
            // WalkingToAnchor is walk-speed; a player chasing a loose ball should sprint.
            // MovementSystem and PlayerAI check Action==Pressing to allow sprint speed.
            return new ActionPlan
            {
                Action = PlayerAction.Pressing, // Bug 6 fix: was WalkingToAnchor — now sprint-eligible
                TargetPosition = ctx.Ball.Position,
                PassReceiverId = -1,
                Score = MathF.Min(1f, proximityScore),
            };
        }

        /// <summary>
        /// Special evaluation path for GK (GK, SK).
        /// GK logic: position on goal line, claim loose balls in box, distribute.
        /// </summary>
        public static ActionPlan EvaluateGK(ref PlayerState player,
                                             MatchContext ctx)
        {
            bool teamHasBall = ctx.Ball.OwnerId >= 0 &&
                               ctx.Players[ctx.Ball.OwnerId].TeamId == player.TeamId;

            // GK has ball — distribute
            if (player.HasBall)
            {
                RoleDefinition role = RoleRegistry.Get(player.Role);
                TacticsInput tactics = player.TeamId == 0
                    ? ctx.HomeTeam.Tactics : ctx.AwayTeam.Tactics;
                PassCandidate bestPass = ScoreBestPass(ref player, role, tactics, ctx);

                if (bestPass.ReceiverId >= 0)
                {
                    return new ActionPlan
                    {
                        Action = PlayerAction.Passing,
                        TargetPosition = player.Position,
                        PassReceiverId = bestPass.ReceiverId,
                        PassSpeed = bestPass.PassSpeed,
                        PassHeight = bestPass.PassHeight,
                        Score = bestPass.Score,
                    };
                }

                // Bug 7 fix: GK should not hold indefinitely when pressed.
                // Find the most advanced teammate for a long clearance kick.
                // If nobody is found at all, hold — but this should be very rare.
                int clearanceTarget = FindGKClearanceTarget(ref player, ctx);
                if (clearanceTarget >= 0)
                {
                    return new ActionPlan
                    {
                        Action = PlayerAction.Passing,
                        TargetPosition = player.Position,
                        PassReceiverId = clearanceTarget,
                        PassSpeed = PhysicsConstants.BALL_PASS_SPEED_HARD,
                        PassHeight = 0.9f, // high ball — long clearance punt
                        Score = 0.35f,
                    };
                }

                return new ActionPlan
                {
                    Action = PlayerAction.Holding,
                    TargetPosition = player.Position,
                    Score = 0.5f,
                };
            }

            // Ball is LOOSE near goal — rush to claim
            // Bug 25 fix: was gated entirely on LooseTicks delay — GK ignored a ball
            // at their feet for N ticks, letting attackers claim it.
            // New logic: rush immediately if very close (within GK_CLAIM_RADIUS),
            // OR wait LooseTicks delay for balls at medium distance.
            if (ctx.Ball.Phase == BallPhase.Loose)
            {
                float distToBall = player.Position.DistanceTo(ctx.Ball.Position);
                bool isVeryClose = distToBall < PhysicsConstants.GK_CLAIM_RADIUS;
                bool waitedLongEnough = ctx.Ball.LooseTicks >= AIConstants.GK_LOOSE_BALL_RUSH_TICKS;

                if ((isVeryClose || waitedLongEnough) && distToBall < PhysicsConstants.GK_CLAIM_RADIUS * 3f)
                {
                    return new ActionPlan
                    {
                        Action = PlayerAction.Pressing, // sprint to ball
                        TargetPosition = ctx.Ball.Position,
                        PassReceiverId = -1,
                        Score = 0.9f,
                    };
                }
            }

            // Default: hold goal line position
            Vec2 goalLineAnchor = ComputeGKAnchor(ref player, ctx);
            return new ActionPlan
            {
                Action = PlayerAction.WalkingToAnchor,
                TargetPosition = goalLineAnchor,
                PassReceiverId = -1,
                Score = 0.5f,
            };
        }
    }
}
