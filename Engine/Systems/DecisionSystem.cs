// =============================================================================
// Module:  DecisionSystem.cs
// Path:    FootballSim/Engine/Systems/DecisionSystem.cs
// Purpose:
//   Pure utility scoring engine. Given a player and the full MatchContext,
//   produces a scored ActionPlan that PlayerAI then executes.
//   DecisionSystem has NO side effects — it reads state and returns scores.
//   It never writes to MatchContext. It never calls BallSystem methods.
//   It never calls RoleRegistry or any loader at runtime (registry already loaded).
//
//   What it scores (with ball):
//     ScorePass(player, receiver, ctx)     → float [0,1]
//     ScoreShoot(player, ctx)              → ShotScore { xG, score, targetPos }
//     ScoreDribble(player, ctx)            → float [0,1]
//     ScoreCross(player, ctx)              → float [0,1]
//     ScoreHold(player, ctx)              → float [0,1]
//
//   What it scores (without ball, team in possession):
//     ScoreSupportRun(player, ctx)         → SupportScore { targetPos, score }
//     ScoreMakeRun(player, ctx)            → RunScore { targetPos, score }
//     ScoreOverlapRun(player, ctx)         → RunScore { targetPos, score }
//     ScoreDropDeep(player, ctx)           → RunScore { targetPos, score }
//     ScoreHoldWidth(player, ctx)          → float [0,1]
//
//   What it scores (without ball, team out of possession):
//     ScorePress(player, ctx)              → PressScore { targetPos, score }
//     ScoreTrack(player, ctx)             → TrackScore { targetPlayerId, targetPos, score }
//     ScoreRecover(player, ctx)           → float [0,1] + FormationAnchor target
//     ScoreMarkSpace(player, ctx)         → float [0,1] + zoneTarget
//
//   High-level selectors (called by PlayerAI):
//     EvaluateWithBall(player, ctx)        → ActionPlan
//     EvaluateWithoutBall(player, ctx)     → ActionPlan
//     EvaluateLooseBall(player, ctx)       → ActionPlan
//     EvaluateGK(player, ctx)             → ActionPlan
//
// Output type:
//   ActionPlan { Action, TargetPosition, PassReceiverId, ShotTargetPos, Score }
//   PlayerAI reads this and applies it — one clean handoff.
//
// Scoring formula (all options):
//   raw = base_attribute × role_bias_blended × tactic_modifier × situation_factor
//   role_bias_blended = Lerp(role.Bias, player.AttributeEquivalent, FreedomLevel)
//   situation_factor  = distance/proximity terms, stamina penalty, etc.
//   final = Clamp01(raw) ± bonuses/penalties
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
    // =========================================================================
    // RESULT TYPES — returned by scoring methods to PlayerAI
    // =========================================================================

    /// <summary>Complete scored plan for one player's next action this decision cycle.</summary>
    public struct ActionPlan
    {
        public PlayerAction Action;
        public Vec2 TargetPosition;   // Where MovementSystem should steer
        public int PassReceiverId;   // -1 if not a pass
        public Vec2 ShotTargetPos;    // goal aim point if shooting
        public float PassSpeed;        // units/tick for BallSystem.LaunchPass
        public float PassHeight;       // 0=ground, 0.5=driven, 1.0=lofted
        public float Score;            // winning utility score [0,1]
        public float XG;              // populated when Action==Shooting
    }

    /// <summary>Score result for one specific pass receiver candidate.</summary>
    public struct PassCandidate
    {
        public int ReceiverId;
        public float Score;
        public float PassSpeed;
        public float PassHeight;
    }

    /// <summary>Score result for a shot attempt.</summary>
    public struct ShotScore
    {
        public float Score;
        public float XG;
        public Vec2 TargetPosition;  // aim point inside goal
    }

    // =========================================================================
    // DECISION SYSTEM
    // =========================================================================

    public static class DecisionSystem
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

            // ── Score each option ──────────────────────────────────────────────

            // 1. SHOOT
            ShotScore shot = ScoreShoot(ref player, role, tactics, ctx);
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
            PassCandidate bestPass = ScoreBestPass(ref player, role, tactics, ctx);
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
            float crossScore = ScoreCross(ref player, role, tactics, ctx);
            if (crossScore > best.Score)
            {
                best.Score = crossScore;
                best.Action = PlayerAction.Crossing;
                best.TargetPosition = player.Position;
                best.PassReceiverId = -1;
            }

            // 4. DRIBBLE
            float dribScore = ScoreDribble(ref player, role, tactics, ctx);
            if (dribScore > best.Score)
            {
                best.Score = dribScore;
                best.Action = PlayerAction.Dribbling;
                best.TargetPosition = ComputeDribbleTarget(ref player, ctx);
                best.PassReceiverId = -1;
            }

            // 5. HOLD
            float holdScore = ScoreHold(ref player, role, tactics, ctx);
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

            DebugLogWithBall(ref player, ref best, shot, bestPass, crossScore, dribScore, holdScore);
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

            // 1. PRESS
            var press = ScorePress(ref player, role, tactics, ctx);
            if (press.Score > best.Score)
            {
                best.Score = press.Score;
                best.Action = PlayerAction.Pressing;
                best.TargetPosition = press.TargetPosition;
                player.IsSprinting = true;
            }

            // 2. TRACK runner
            var track = ScoreTrack(ref player, role, tactics, ctx);
            if (track.Score > best.Score)
            {
                best.Score = track.Score;
                best.Action = PlayerAction.Tracking;
                best.TargetPosition = track.TargetPosition;
                player.IsSprinting = track.Score > 0.6f; // sprint if urgent
            }

            // 3. RECOVER to shape
            float recoverScore = ScoreRecover(ref player, role, tactics, ctx);
            if (recoverScore > best.Score)
            {
                best.Score = recoverScore;
                best.Action = PlayerAction.Recovering;
                best.TargetPosition = ComputeDefensiveAnchor(ref player, tactics, ctx);
                player.IsSprinting = true;
            }

            // 4. MARK SPACE (zone cover)
            float markScore = ScoreMarkSpace(ref player, role, tactics, ctx);
            Vec2 markTarget = ComputeMarkSpaceTarget(ref player, tactics, ctx);
            if (markScore > best.Score)
            {
                best.Score = markScore;
                best.Action = PlayerAction.MarkingSpace;
                best.TargetPosition = markTarget;
                player.IsSprinting = false;
            }

            // Fallback: return to defensive anchor
            if (best.Score < 0f)
            {
                best.Action = PlayerAction.Recovering;
                best.TargetPosition = ComputeDefensiveAnchor(ref player, tactics, ctx);
                best.Score = 0f;
            }

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

            return new ActionPlan
            {
                Action = PlayerAction.WalkingToAnchor, // MovementSystem drives toward ball
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

                return new ActionPlan
                {
                    Action = PlayerAction.Holding,
                    TargetPosition = player.Position,
                    Score = 0.5f,
                };
            }

            // Ball is LOOSE near goal — rush to claim
            if (ctx.Ball.Phase == BallPhase.Loose &&
                ctx.Ball.LooseTicks >= AIConstants.GK_LOOSE_BALL_RUSH_TICKS)
            {
                float distToBall = player.Position.DistanceTo(ctx.Ball.Position);
                if (distToBall < PhysicsConstants.GK_CLAIM_RADIUS * 3f)
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

        // =====================================================================
        // WITH-BALL SCORERS
        // =====================================================================

        /// <summary>
        /// Scores a shot attempt. Returns ShotScore with xG and final score.
        /// Score = 0 if beyond max range, below xG threshold, or GK/defender role.
        /// </summary>
        public static ShotScore ScoreShoot(ref PlayerState player,
                                            RoleDefinition role,
                                            TacticsInput tactics,
                                            MatchContext ctx)
        {
            // Goalkeepers and pure defenders almost never shoot
            if (player.Role == PlayerRole.GK || player.Role == PlayerRole.SK)
                return default;

            // Determine attacking direction
            bool attacksDown = player.TeamId == 0; // home attacks toward Y=680
            float goalY = attacksDown ? PhysicsConstants.AWAY_GOAL_LINE_Y
                                        : PhysicsConstants.HOME_GOAL_LINE_Y;
            float goalCentreX = (PhysicsConstants.HOME_GOAL_LEFT_X +
                                  PhysicsConstants.HOME_GOAL_RIGHT_X) * 0.5f;
            Vec2 goalCentre = new Vec2(goalCentreX, goalY);

            float distToGoal = player.Position.DistanceTo(goalCentre);

            // Beyond max shooting range — skip
            if (distToGoal > AIConstants.SHOOT_MAX_RANGE) return default;

            // Calculate base xG from distance and angle
            float xG = ComputeXG(ref player, goalCentre, distToGoal, ctx);

            // Apply ego modifier to threshold
            float effectiveThreshold = ComputeEffectiveXGThreshold(ref player, tactics);

            // Below threshold — don't shoot
            if (xG < effectiveThreshold) return default;

            // Blend role ShootBias with player ShootingAbility via FreedomLevel
            float roleBias = role.ShootBias;
            float instinct = player.ShootingAbility;
            float blended = BlendRoleWithInstinct(roleBias, instinct, tactics.FreedomLevel);

            // Final score: xG × role/instinct blend
            float finalScore = MathF.Min(1f, xG * 1.5f * blended);

            // Find best aim point (slight offset from GK to increase chance of scoring)
            Vec2 aimPoint = ComputeGoalAimPoint(ref player, goalCentre, ctx);

            if (DEBUG && (DEBUG_PLAYER_ID < 0 || DEBUG_PLAYER_ID == player.PlayerId))
                Console.WriteLine($"[DecisionSystem] Tick {ctx.Tick}: P{player.PlayerId} " +
                    $"SHOOT score={finalScore:F3} xG={xG:F3} dist={distToGoal:F0} " +
                    $"threshold={effectiveThreshold:F3} blend={blended:F3}");

            return new ShotScore
            {
                Score = finalScore,
                XG = xG,
                TargetPosition = aimPoint,
            };
        }

        /// <summary>
        /// Scores ALL possible pass receivers, returns the single best PassCandidate.
        /// Only considers teammates on the same team as the passer.
        /// </summary>
        public static PassCandidate ScoreBestPass(ref PlayerState player,
                                                   RoleDefinition role,
                                                   TacticsInput tactics,
                                                   MatchContext ctx)
        {
            int teamStart = player.TeamId == 0 ? 0 : 11;
            int teamEnd = player.TeamId == 0 ? 11 : 22;

            PassCandidate best = default;
            best.ReceiverId = -1;
            best.Score = -1f;

            for (int i = teamStart; i < teamEnd; i++)
            {
                if (i == player.PlayerId) continue;
                if (!ctx.Players[i].IsActive) continue;

                PassCandidate candidate = ScorePass(ref player, ref ctx.Players[i],
                                                     role, tactics, ctx);
                if (candidate.Score > best.Score)
                    best = candidate;
            }

            return best;
        }

        /// <summary>
        /// Scores a pass to one specific receiver.
        /// </summary>
        public static PassCandidate ScorePass(ref PlayerState player,
                                               ref PlayerState receiver,
                                               RoleDefinition role,
                                               TacticsInput tactics,
                                               MatchContext ctx)
        {
            float dist = player.Position.DistanceTo(receiver.Position);
            if (dist > AIConstants.PASS_MAX_DISTANCE)
                return new PassCandidate { ReceiverId = -1, Score = 0f };

            // Base score from passing ability
            float abilityFactor = player.PassingAbility;

            // Role blend
            float roleBias = role.PassBias;
            float blended = BlendRoleWithInstinct(roleBias, abilityFactor, tactics.FreedomLevel);

            // ── Distance modifier ─────────────────────────────────────────────
            float distScore;
            bool isLong = dist > AIConstants.PASS_LONG_THRESHOLD;
            bool isShort = dist < AIConstants.PASS_SHORT_THRESHOLD;

            if (isShort)
            {
                // Short passes: possession_focus rewards these heavily
                distScore = 0.5f + tactics.PossessionFocus * 0.3f;
            }
            else if (isLong)
            {
                // Long passes: directness rewards these, ability must be high
                distScore = (tactics.PassingDirectness * 0.5f + role.LongPassBias * 0.5f)
                            * player.PassingAbility;
            }
            else
            {
                // Medium range: flat moderate score
                distScore = 0.5f;
            }

            // ── Progressive bonus ─────────────────────────────────────────────
            bool attacksDown = player.TeamId == 0;
            bool isProgressive = attacksDown
                ? receiver.Position.Y > player.Position.Y + 20f
                : receiver.Position.Y < player.Position.Y - 20f;

            float progressiveBonus = isProgressive
                ? AIConstants.PASS_PROGRESSIVE_BONUS * (1f - tactics.PossessionFocus * 0.5f)
                : 0f;

            // ── Receiver pressure penalty ─────────────────────────────────────
            float pressure = ComputeReceiverPressure(ref receiver, player.TeamId, ctx);
            float pressurePenalty = pressure * AIConstants.PASS_RECEIVER_PRESSURE_PENALTY;

            // ── Safe recycle bonus ────────────────────────────────────────────
            float senderPressure = ComputeSenderPressure(ref player, ctx);
            float safeRecycleBonus = 0f;
            if (senderPressure > AIConstants.PASS_SAFE_RECYCLE_PRESSURE_THRESHOLD && isShort)
                safeRecycleBonus = AIConstants.PASS_SAFE_RECYCLE_BONUS;

            // ── Final score ───────────────────────────────────────────────────
            float raw = blended * distScore + progressiveBonus + safeRecycleBonus - pressurePenalty;
            float score = MathF.Max(0f, MathF.Min(1f, raw));

            // ── Pass speed selection ──────────────────────────────────────────
            float speed = SelectPassSpeed(dist, tactics.PassingDirectness, role.LongPassBias);
            float height = isLong && role.LongPassBias > 0.5f ? 0.7f : 0f;

            return new PassCandidate
            {
                ReceiverId = receiver.PlayerId,
                Score = score,
                PassSpeed = speed,
                PassHeight = height,
            };
        }

        /// <summary>
        /// Scores dribbling based on space ahead, nearest defender distance, and role bias.
        /// </summary>
        public static float ScoreDribble(ref PlayerState player,
                                          RoleDefinition role,
                                          TacticsInput tactics,
                                          MatchContext ctx)
        {
            if (player.Role == PlayerRole.GK || player.Role == PlayerRole.SK)
                return 0f;

            float nearestDefenderDist = FindNearestOpponentDistance(ref player, ctx);

            // Too close — defender will immediately tackle
            if (nearestDefenderDist < AIConstants.DRIBBLE_DEFENDER_SAFE_DISTANCE)
                return 0f;

            // Very open — just run with ball, no contest
            if (nearestDefenderDist > AIConstants.DRIBBLE_DEFENDER_RELEVANT_DISTANCE)
                return 0.1f; // low score — just walk forward, no "dribble" needed

            // Normalise defender distance to [0,1] in the relevant range
            float defScore = (nearestDefenderDist - AIConstants.DRIBBLE_DEFENDER_SAFE_DISTANCE)
                           / (AIConstants.DRIBBLE_DEFENDER_RELEVANT_DISTANCE
                              - AIConstants.DRIBBLE_DEFENDER_SAFE_DISTANCE);

            float roleBias = role.DribbleBias;
            float instinct = player.DribblingAbility;
            float blended = BlendRoleWithInstinct(roleBias, instinct, tactics.FreedomLevel);

            return MathF.Min(1f, blended * defScore);
        }

        /// <summary>
        /// Scores crossing from a wide position into the penalty area.
        /// Returns 0 if player is not in a crossing position.
        /// </summary>
        public static float ScoreCross(ref PlayerState player,
                                        RoleDefinition role,
                                        TacticsInput tactics,
                                        MatchContext ctx)
        {
            if (player.Role == PlayerRole.GK || player.Role == PlayerRole.SK ||
                player.Role == PlayerRole.CB || player.Role == PlayerRole.CDM)
                return 0f;

            // Must be in wide zone
            float pitchEdgeDist = MathF.Min(player.Position.X,
                                             PhysicsConstants.PITCH_WIDTH - player.Position.X);
            if (pitchEdgeDist > AIConstants.CROSS_WIDE_ZONE_X) return 0f;

            // Must be advanced enough
            float yProgress = player.TeamId == 0
                ? player.Position.Y / PhysicsConstants.PITCH_HEIGHT
                : 1f - player.Position.Y / PhysicsConstants.PITCH_HEIGHT;

            if (yProgress < AIConstants.CROSS_MIN_Y_PROGRESS) return 0f;

            // Count attackers in or near penalty box
            int attackersInBox = CountAttackersInBox(player.TeamId, ctx);
            float boxBonus = attackersInBox >= AIConstants.CROSS_MIN_ATTACKERS_IN_BOX
                ? AIConstants.CROSS_ATTACKERS_IN_BOX_BONUS : 0f;

            float roleBias = role.CrossBias;
            float instinct = player.PassingAbility; // crossing = passing quality
            float blended = BlendRoleWithInstinct(roleBias, instinct, tactics.FreedomLevel);
            float tacticBonus = tactics.CrossingFrequency * 0.3f;

            return MathF.Min(1f, blended + boxBonus + tacticBonus);
        }

        /// <summary>
        /// Scores holding the ball (Holding action) — waiting for a run to develop.
        /// Higher when under low pressure and SupportBias is high on the passer's role.
        /// </summary>
        public static float ScoreHold(ref PlayerState player,
                                       RoleDefinition role,
                                       TacticsInput tactics,
                                       MatchContext ctx)
        {
            float senderPressure = ComputeSenderPressure(ref player, ctx);
            // Only hold if not immediately under pressure
            if (senderPressure < AIConstants.PASS_SAFE_RECYCLE_PRESSURE_THRESHOLD * 0.5f)
                return 0f; // defender too close — must act

            // Normalise pressure to [0,1] — higher pressure = lower hold score
            float pressureNorm = MathF.Min(1f, senderPressure / AIConstants.PASS_MAX_DISTANCE);
            float holdScore = pressureNorm * (1f - tactics.BuildUpSpeed) * 0.5f;

            return MathF.Max(0f, MathF.Min(1f, holdScore));
        }

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
            float instinct = player.DribblingAbility; // pace / ability to run
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
            float instinct = player.BaseSpeed / PhysicsConstants.PLAYER_SPRINT_SPEED; // pace matters
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
            float blended = BlendRoleWithInstinct(roleBias, 0.5f, tactics.FreedomLevel);
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

        // =====================================================================
        // OUT-OF-POSSESSION SCORERS
        // =====================================================================

        /// <summary>
        /// Scores pressing the ball carrier or a nearby opponent.
        /// Accounts for press trigger distance, stamina, role press bias, tactics.
        /// </summary>
        public static (Vec2 TargetPosition, float Score) ScorePress(
            ref PlayerState player, RoleDefinition role, TacticsInput tactics, MatchContext ctx)
        {
            // Compute press trigger distance from tactics
            float triggerDist = MathUtil.Lerp(
                AIConstants.PRESS_TRIGGER_DIST_MIN,
                AIConstants.PRESS_TRIGGER_DIST_MAX,
                tactics.PressingIntensity
            );

            // Find press target: the ball carrier
            if (ctx.Ball.OwnerId < 0 || ctx.Ball.Phase != BallPhase.Owned)
                return (player.FormationAnchor, 0f);

            ref PlayerState carrier = ref ctx.Players[ctx.Ball.OwnerId];
            if (carrier.TeamId == player.TeamId)
                return (player.FormationAnchor, 0f); // don't press your own team

            float distToCarrier = player.Position.DistanceTo(carrier.Position);

            // Outside trigger zone — don't press
            if (distToCarrier > triggerDist) return (player.FormationAnchor, 0f);

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

            return (carrier.Position, score);
        }

        /// <summary>
        /// Scores tracking the most dangerous opposing runner.
        /// Returns the target player ID and intercept position.
        /// </summary>
        public static (Vec2 TargetPosition, float Score) ScoreTrack(
            ref PlayerState player, RoleDefinition role, TacticsInput tactics, MatchContext ctx)
        {
            int opponentStart = player.TeamId == 0 ? 11 : 0;
            int opponentEnd = player.TeamId == 0 ? 22 : 11;

            float bestDanger = 0f;
            Vec2 bestTarget = player.FormationAnchor;

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
                    // Anticipate: target is ahead of runner, not on them
                    Vec2 runDir = opp.Velocity.LengthSquared() > 0.01f
                        ? opp.Velocity.Normalized() : Vec2.Zero;
                    bestTarget = opp.Position + runDir * AIConstants.MARK_ANTICIPATION_OFFSET;
                }
            }

            if (bestDanger <= AIConstants.MARK_DANGER_THRESHOLD)
                return (player.FormationAnchor, 0f);

            float roleBias = role.TackleCommitTendency;
            float blended = BlendRoleWithInstinct(roleBias, player.DefendingAbility,
                                                    tactics.FreedomLevel);

            return (bestTarget, MathF.Min(1f, blended * bestDanger));
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

            // Not out of position — no urgency
            if (distFromAnchor < AIConstants.ANCHOR_PULL_RADIUS * 0.5f) return 0f;

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
            // Base xG from distance: exponential falloff
            // At 0m: ~1.0. At 12m (120 units): ~0.35. At 35m (350 units): ~0.03
            float distNorm = distToGoal / AIConstants.SHOOT_OPTIMAL_RANGE;
            float baseXG = MathF.Exp(-distNorm * 1.2f);

            // Angle factor: shots from central positions are better
            float pitchCentreX = PhysicsConstants.PITCH_WIDTH * 0.5f;
            float angleOffset = MathF.Abs(player.Position.X - pitchCentreX);
            float maxAngleOff = PhysicsConstants.PITCH_WIDTH * 0.5f;
            float angleFactor = 1f - (angleOffset / maxAngleOff) * 0.5f; // 0.5–1.0

            // Shooting ability modifier
            float abilityMod = 0.5f + player.ShootingAbility * 0.5f; // 0.5–1.0

            float xG = baseXG * angleFactor * abilityMod;

            // Block penalty: count defenders in shooting lane
            int blockersInLane = CountBlockersInShootingLane(ref player, goalCentre,
                                                               player.TeamId, ctx);
            xG -= blockersInLane * AIConstants.XG_DEFENDER_BLOCK_PENALTY * xG;

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
        /// Selects an aim point inside the goal, slightly offset from GK position.
        /// Simplified: aim at the corner opposite to the GK's lean.
        /// </summary>
        private static Vec2 ComputeGoalAimPoint(ref PlayerState player,
                                                  Vec2 goalCentre, MatchContext ctx)
        {
            // Find opponent GK
            int gkId = FindOpponentGK(player.TeamId, ctx);
            float goalHalfWidth = (PhysicsConstants.HOME_GOAL_RIGHT_X -
                                    PhysicsConstants.HOME_GOAL_LEFT_X) * 0.5f;

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

        /// <summary>Nearest opponent distance to the ball carrier (sender pressure).</summary>
        private static float ComputeSenderPressure(ref PlayerState sender, MatchContext ctx)
        {
            return FindNearestOpponentDistance(ref sender, ctx);
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

        /// <summary>Computes an ideal dribble target: forward in movement direction.</summary>
        private static Vec2 ComputeDribbleTarget(ref PlayerState player, MatchContext ctx)
        {
            bool attacksDown = player.TeamId == 0;
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
            return new Vec2(targetX, player.FormationAnchor.Y);
        }

        /// <summary>
        /// Computes an ideal passing triangle support position relative to ball carrier.
        /// Offset is lateral and slightly behind the carrier for easy ball receipt.
        /// </summary>
        private static Vec2 ComputeSupportTarget(ref PlayerState player,
                                                   Vec2 carrierPos,
                                                   MatchContext ctx)
        {
            // Side bias: player stays on their formation anchor side
            float xSide = player.FormationAnchor.X > PhysicsConstants.PITCH_WIDTH * 0.5f
                ? AIConstants.SUPPORT_IDEAL_DISTANCE : -AIConstants.SUPPORT_IDEAL_DISTANCE;

            bool attacksDown = player.TeamId == 0;
            float yOffset = attacksDown ? -AIConstants.SUPPORT_IDEAL_DISTANCE * 0.3f
                                           : AIConstants.SUPPORT_IDEAL_DISTANCE * 0.3f;

            return new Vec2(
                Math.Clamp(carrierPos.X + xSide, PhysicsConstants.PITCH_LEFT,
                            PhysicsConstants.PITCH_RIGHT),
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
            bool attacksDown = player.TeamId == 0;
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
                targetX = cb1Pos.X; // just target the one CB
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
            bool attacksDown = player.TeamId == 0;
            float targetY = attacksDown
                ? player.Position.Y + 150f
                : player.Position.Y - 150f;

            return new Vec2(targetX,
                Math.Clamp(targetY, PhysicsConstants.PITCH_TOP, PhysicsConstants.PITCH_BOTTOM));
        }

        /// <summary>Drop-deep target: position between ball carrier and own half.</summary>
        private static Vec2 ComputeDropDeepTarget(ref PlayerState player, MatchContext ctx)
        {
            bool attacksDown = player.TeamId == 0;
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
            bool defAttacksDown = defensiveTeam == 0;
            float oppGoalY = defAttacksDown
                ? PhysicsConstants.PITCH_TOP    // defending team's goal is at top
                : PhysicsConstants.PITCH_BOTTOM;

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
            bool attacksDown = attackingTeam == 0;
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

        // =====================================================================
        // DEBUG LOGGING
        // =====================================================================

        private static void DebugLogWithBall(ref PlayerState player, ref ActionPlan best,
            ShotScore shot, PassCandidate pass, float cross, float drib, float hold)
        {
            if (!DEBUG) return;
            if (DEBUG_PLAYER_ID >= 0 && DEBUG_PLAYER_ID != player.PlayerId) return;

            Console.WriteLine(
                $"[DecisionSystem] Tick ?  P{player.PlayerId} (Team{player.TeamId} {player.Role}) " +
                $"HAS BALL → chose {best.Action} score={best.Score:F3} | " +
                $"SHOOT={shot.Score:F3}(xG={shot.XG:F3}) " +
                $"PASS={pass.Score:F3}(→P{pass.ReceiverId}) " +
                $"CROSS={cross:F3} DRIB={drib:F3} HOLD={hold:F3}");
        }

        private static void DebugLogWithoutBall(ref PlayerState player, ref ActionPlan best)
        {
            if (!DEBUG) return;
            if (DEBUG_PLAYER_ID >= 0 && DEBUG_PLAYER_ID != player.PlayerId) return;

            Console.WriteLine(
                $"[DecisionSystem] P{player.PlayerId} (Team{player.TeamId} {player.Role}) " +
                $"NO BALL (team has) → chose {best.Action} score={best.Score:F3} " +
                $"target={best.TargetPosition}");
        }

        private static void DebugLogOutOfPossession(ref PlayerState player, ref ActionPlan best)
        {
            if (!DEBUG) return;
            if (DEBUG_PLAYER_ID >= 0 && DEBUG_PLAYER_ID != player.PlayerId) return;

            Console.WriteLine(
                $"[DecisionSystem] P{player.PlayerId} (Team{player.TeamId} {player.Role}) " +
                $"OUT OF POSS → chose {best.Action} score={best.Score:F3} " +
                $"target={best.TargetPosition}");
        }
    }
}