// =============================================================================
// Module:  DecisionSystem.WithBall.cs
// Path:    FootballSim/Engine/Systems/DecisionSystem/DecisionSystem.WithBall.cs
// Purpose:
//   Every scoring function for players who currently have the ball.
//   This module answers: "What should the ball carrier do?"
//
// Scoring methods defined here:
//   ScoreShoot(player, role, tactics, ctx)                → ShotScore
//   ScoreBestPass(player, role, tactics, ctx)             → PassCandidate
//   ScorePass(player, receiver, role, tactics, ctx)       → PassCandidate
//   ScoreDribble(player, role, tactics, ctx)              → float [0,1]
//   ScoreCross(player, role, tactics, ctx)                → float [0,1]
//   ScoreHold(player, role, tactics, ctx)                 → float [0,1]
//
// Bug fixes applied (see DecisionSystemBugsAnalysis.md):
//   Bug 1  — ScoreHold inverted: gate and normalisation both fixed so hold score
//             peaks at medium pressure, 0 at max pressure and 0 with no pressure.
//   Bug 2  — ScoreDribble dead zone: contestT now peaks at the 1v1 range (just
//             outside safe distance) instead of rising from zero there.
//   Bug 5  — ScoreShoot uses HOME goalCentreX for both teams: fixed per attacksDown.
//   Bug 8  — ScoreBestPass self-exclusion: now compares PlayerId not array index.
//   Bug 9  — ComputeSenderPressure returns raw distance: now normalised to [0,1]
//             matching ComputeReceiverPressure. ScorePass recycle check updated.
//   Bug 13 — cbCount==1 targets CB directly: now targets channel beside lone CB.
//   Bug 15 — xG normalised to OPTIMAL_RANGE: now uses SHOOT_MAX_RANGE so the full
//             shooting range has meaningful xG values.
//   Bug 16 — Block penalty additive collapse: now multiplicative Pow decay per blocker.
//   Bug 17 — ComputeGoalAimPoint uses HOME half-width always: fixed per attacksDown.
//   Bug 22 — ScoreHoldWidth hardcoded 0.5: now blends pace + passing ability.
//
// Notes:
//   • All methods are static and pure — no side effects.
//   • Called by EvaluateWithBall in DecisionSystem.cs (the façade).
// =============================================================================

using System;
using FootballSim.Engine.Models;
using FootballSim.Engine.Tactics;

namespace FootballSim.Engine.Systems
{
    public static partial class DecisionSystem
    {
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
            bool attacksDown = ctx.AttacksDownward(player.TeamId);
            float goalY = attacksDown ? PhysicsConstants.AWAY_GOAL_LINE_Y
                                        : PhysicsConstants.HOME_GOAL_LINE_Y;
            // Bug 5 fix: goalCentreX selects correct goal per team (was always HOME)
            float goalCentreX = attacksDown
                ? (PhysicsConstants.AWAY_GOAL_LEFT_X + PhysicsConstants.AWAY_GOAL_RIGHT_X) * 0.5f
                : (PhysicsConstants.HOME_GOAL_LEFT_X + PhysicsConstants.HOME_GOAL_RIGHT_X) * 0.5f;
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

            // Count blockers in lane for debug context
            int blockersInLane = CountBlockersInShootingLane(ref player, goalCentre, player.TeamId, ctx);

            if (DEBUG && (DEBUG_PLAYER_ID < 0 || DEBUG_PLAYER_ID == player.PlayerId))
                Console.WriteLine($"[DecisionSystem] Tick {ctx.Tick}: P{player.PlayerId} " +
                    $"SHOOT score={finalScore:F3} xG={xG:F3} dist={distToGoal:F0} " +
                    $"threshold={effectiveThreshold:F3} blend={blended:F3}");

            return new ShotScore
            {
                Score = finalScore,
                XG = xG,
                TargetPosition = aimPoint,
                EffectiveThreshold = effectiveThreshold,
                BlockersInLane = blockersInLane,
                DistToGoal = distToGoal,
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
                // Bug 8 fix: compare array index i against player's array index (PlayerId==index
                // is guaranteed by engine contract: Players[0..10]=home, [11..21]=away, sequential).
                // Using ctx.Players[i].PlayerId == player.PlayerId is the safe form if that
                // contract were ever relaxed, but both are equivalent under current engine rules.
                if (ctx.Players[i].PlayerId == player.PlayerId) continue;
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
            bool longPassDisabled = AIConstants.DISABLE_LONG_PASS_OVERRIDE
                                 || AIConstants.DISABLE_LONG_PASS;
            if (longPassDisabled && dist > AIConstants.PASS_LONG_THRESHOLD)
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
            bool attacksDown = ctx.AttacksDownward(player.TeamId);
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
            // Bug 9 fix: senderPressure now [0,1]. High value = defender close.
            // Recycle bonus triggers when pressure is HIGH (defender near) and pass is short.
            float senderPressure = ComputeSenderPressure(ref player, ctx); // [0,1]
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
                IsProgressive = isProgressive,
                Distance = dist,
                ReceiverPressure = pressure,
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

            // Bug 2 fix: score peaks when defender is JUST outside safe distance (1v1 range),
            // falls as defender becomes irrelevant (too far to contest).
            // Old code: defScore rises from 0→1 as defender moves away → 0 at the 1v1 zone.
            // New code: contestT falls from 1→0 as defender moves away → peak at the 1v1 zone.
            float contestT = 1f - (nearestDefenderDist - AIConstants.DRIBBLE_DEFENDER_SAFE_DISTANCE)
                                 / (AIConstants.DRIBBLE_DEFENDER_RELEVANT_DISTANCE
                                    - AIConstants.DRIBBLE_DEFENDER_SAFE_DISTANCE);
            contestT = MathF.Max(0f, contestT);

            float roleBias = role.DribbleBias;
            float instinct = player.DribblingAbility;
            float blended = BlendRoleWithInstinct(roleBias, instinct, tactics.FreedomLevel);

            // Base floor of 0.3 so a small dribble option always exists; peak at 1v1 range
            return MathF.Min(1f, blended * (0.3f + contestT * 0.7f));
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
            bool attacksDown = ctx.AttacksDownward(player.TeamId);
            float yProgress = attacksDown
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
            // Bug 1 fix: ComputeSenderPressure now returns normalised [0,1] (Bug 9 fix).
            // High pressureLevel (near 1) = defender is close = do NOT hold.
            // Low pressureLevel (near 0) = open space = modest hold opportunity.
            float pressureLevel = ComputeSenderPressure(ref player, ctx); // [0,1], 1=maxPressure

            // Gate: under immediate pressure — must act, do not hold
            if (pressureLevel > AIConstants.PASS_SAFE_RECYCLE_PRESSURE_THRESHOLD * 0.5f)
                return 0f; // defender too close — must act

            // Bug 1 fix: inverted normalisation — low pressure → small hold score (no reason to
            // sit idle), medium pressure → peak hold score (shield ball, wait for run).
            // pressureLevel=0 (nobody near) → holdScore=0 (just pass freely)
            // pressureLevel=medium → holdScore peaks (worth shielding ball briefly)
            float holdScore = pressureLevel * (1f - tactics.BuildUpSpeed) * 0.5f;

            return MathF.Max(0f, MathF.Min(1f, holdScore));
        }
    }
}
