// =============================================================================
// Module:  CollisionSystem.cs (post-bug fix)
// Path:    FootballSim/Engine/Systems/CollisionSystem.cs
// Purpose:
//   Resolves all physical contests that require a probability draw:
//     • Tackle contest     — defender in TackleAttempt range of ball carrier
//     • Intercept contest  — defender in InFlight ball trajectory
//     • Loose ball claim   — any player within BALL_CONTEST_RADIUS of Loose ball
//     • GK save contest    — goalkeeper vs shot on target
//     • Header contest     — aerial ball with two opponents both Heading
//
//   CollisionSystem is the ONLY caller of:
//     BallSystem.AssignOwnership(ctx, winnerId)
//     BallSystem.SetLoose(ctx, deflectionVelocity)
//   PlayerAI.Tick() runs first and sets player.Action = TackleAttempt on eligible
//   defenders. CollisionSystem reads that flag, runs the contest, then mutates.
//
//   Output — CollisionSystem does NOT emit events. After each contest it sets
//   ctx.LastCollisionResult so EventSystem can inspect it and emit the right event.
//
// Tick order (locked):
//   1. MovementSystem  — positions updated
//   2. PlayerAI        — actions + TargetPositions set
//   3. BallSystem      — ball physics, flight, loose
//   4. CollisionSystem ← HERE: resolve contests, call BallSystem.Assign/SetLoose
//   5. EventSystem     — reads ctx.LastCollisionResult, emits MatchEvent
//
// Determinism:
//   All random draws use ctx.Random (a System.Random seeded from ctx.RandomSeed).
//   ctx.Random is advanced monotonically — one NextDouble() per contest per tick.
//   Never new Random() inside this file.
//
// API:
//   CollisionSystem.Tick(MatchContext ctx) → void
//     Entry point called by TickSystem. Runs all contest checks in order:
//       1. Tackle contests  (only when ball.Phase == Owned)
//       2. Intercept contests (only when ball.Phase == InFlight)
//       3. Loose ball claims  (only when ball.Phase == Loose)
//       4. GK save contests   (only when ball.IsShot && near goal)
//
//   CollisionResult (struct) — written to ctx.LastCollisionResult after each contest.
//     Consumed by EventSystem in its own Tick.
//
// Probability formulas (pre-bug-fix reference; bugs 1–23 now fixed):
//   Tackle success:
//     p = Lerp(0.2, 0.85, defender.DefendingAbility)
//       × Lerp(0.5, 1.0,  1.0 - attacker.DribblingAbility)
//       × staminaFactor(defender)
//     Bug 3: aggression now only raises FOUL probability, not tackle success
//     if random < p → tackle success
//     else          → foul check (random < foulProbability)
//     Bug 23: defender enters cooldown after attempt
//
//   Intercept success:
//     distToTrajectory = perpendicular distance from player to ball flight line
//     p = Lerp(0.9, 0.0, distToTrajectory / INTERCEPT_LANE_WIDTH)
//       × Lerp(0.2, 0.9, interceptor.Reactions)
//     Bug 8: lane width scaled inversely by ball speed
//     Bug 9: max range = ballSpeed × DEFENDER_INTERCEPT_REACTION_TICKS (no magic ×20)
//     if random < p → intercept (assign ownership, or set loose if aerial)
//
//   Loose ball claim:
//     Nearest player within BALL_CONTEST_RADIUS auto-claims (no roll needed).
//     If two players within radius: weighted random by Reactions + Stamina + Proximity
//     Bug 12: three-factor weighting, not just Reactions
//
//   GK save:
//     p = Lerp(0.2, 0.95, gk.Reactions)
//       × Lerp(0.5, 1.0,  1.0 - shot_xG)   // harder shots harder to save
//       × angleBonus (how centred the GK is on the shot)
//     Bug 4: uses ctx.Ball.ShotXG set at shot moment by PlayerAI (not speed proxy)
//     Bug 6: ShotContestResolved flag prevents re-firing each tick
//     if random < p → save (loose ball near goal)
//     else          → goal
//
// Bug fixes applied (see CollisionSystemBugsAnalysis.md for full details):
//   Bug 1  — Single-tackle monopoly: collect all candidates, sort nearest first.
//   Bug 2  — Collapsed Lerp: correct multiplicative formula pBase × dribblingResist.
//   Bug 3  — aggrFactor > 1.0: removed from success formula; drives foul risk only.
//   Bug 4  — Proxy xG: reads ctx.Ball.ShotXG set at shot moment by PlayerAI.
//   Bug 5  — Wrong goal X: goal mouth X now selected per attacking team.
//   Bug 6  — Save fires every tick: BallState.ShotContestResolved flag.
//   Bug 7  — Off-target vanishes: sets ball.IsOutOfPlay = true.
//   Bug 8  — Lane width not normalised: scales inversely with ball speed.
//   Bug 9  — Magic ×20: replaced with AIConstants.DEFENDER_INTERCEPT_REACTION_TICKS.
//   Bug 10 — Shot enters intercept: Tick() dispatcher routes explicitly.
//   Bug 12 — Loose ball ignores proximity/stamina: three-factor weighting.
//   Bug 14 — Header deterministic: probabilistic draw added.
//   Bug 20 — Own goal uses index bands: uses ctx.Players[id].TeamId.
//   Bug 21 — Duplicate xG calc: shotXG computed once, passed to RecordGoal.
//   Bug 23 — No tackle cooldown: sets TackleCooldownTicks after each attempt.
//
// Deferred (noted in code with comments):
//   Bug 11, 13, 15, 16, 17, 18, 19, 22
//
// Dependencies:
//   Engine/Models/MatchContext.cs
//   Engine/Models/PlayerState.cs
//   Engine/Models/BallState.cs
//   Engine/Models/Vec2.cs
//   Engine/Models/Enums.cs
//   Engine/Systems/BallSystem.cs      (AssignOwnership, SetLoose — only callers)
//   Engine/Systems/PhysicsConstants.cs
//   Engine/Systems/AIConstants.cs
//   Engine/MathUtil.cs                (Lerp, Clamp01 — never MathF.Lerp)
//
// Notes:
//   • All MathF.Lerp uses are replaced with MathUtil.Lerp. See MathUtil.cs.
//   • ctx.LastCollisionResult is reset to None at the start of Tick().
//     If multiple contests happen the same tick, only the highest-impact result
//     is stored (Goal > Tackle > Intercept > LooseClaim). EventSystem reads once.
//   • Header contest now uses probabilistic draw (Bug 14 fix).
//     Full aerial contest physics (trajectory, timing window) comes in a later pass.
// =============================================================================

using System;
using System.Collections.Generic;
using FootballSim.Engine.Models;
using FootballSim.Engine.Tactics;

namespace FootballSim.Engine.Systems
{
    // =========================================================================
    // COLLISION RESULT
    // =========================================================================

    public enum CollisionResultType
    {
        None,
        TackleSuccess,      // defender wins ball cleanly
        TaclkeFoul,         // defender fouls attacker (typo preserved — Bug 19 deferred)
        TackleFailed,       // tackle missed — attacker keeps ball
        InterceptSuccess,   // defender cuts out pass, gains possession
        InterceptDeflection,// defender touches ball but can't hold — goes Loose
        LooseBallClaimed,   // player claims loose ball
        GKSave,             // goalkeeper saves shot — ball goes Loose
        GKCatch,            // goalkeeper catches (clean) — owns ball
        Goal,               // ball enters goal — EventSystem handles score
        OwnGoal,            // ball enters own goal
        HeaderWon,          // attacker wins aerial contest
        HeaderLost,         // defender wins aerial contest
    }

    /// <summary>
    /// Output of one contest resolved this tick.
    /// CollisionSystem writes this; EventSystem reads it to emit the right MatchEvent.
    /// </summary>
    public struct CollisionResult
    {
        public CollisionResultType Type;
        public int PrimaryPlayerId;    // winner / tackler / interceptor / GK
        public int SecondaryPlayerId;  // loser / attacker / passer (-1 if none)
        public Vec2 Position;           // where the contest happened
        public float Probability;        // p used in the winning roll (for ExtraFloat)
        public float FoulSeverity;       // 0–1, only set when TaclkeFoul
        public bool IsClean;            // true when tackle was clean (no foul risk)
        public float ShotXG;             // populated on GKSave / Goal (Bug 4 + 21)
        public int DefendingTeamId;    // which team benefits from the result
    }

    // =========================================================================
    // COLLISION CONSTANTS
    // =========================================================================

    // =========================================================================
    // COLLISION CONSTANTS — all tunable values in one place
    // =========================================================================

    public static class CollisionConstants
    {
        // ── Tackle ────────────────────────────────────────────────────────────

        /// <summary>
        /// Maximum distance for a TackleAttempt to connect.
        /// 25 units = 2.5m. Real sliding tackle reach.
        /// </summary>
        public static readonly float TACKLE_RANGE = 25f;

        /// <summary>
        /// Base tackle success probability for an average defender vs average attacker.
        /// Bug 2 fix: Lerp floor for dribbling resistance. Spec: Lerp(0.5, 1.0, 1-drib).
        /// </summary>
        public static readonly float TACKLE_P_MIN = 0.20f;
        public static readonly float TACKLE_P_MAX = 0.85f;

        /// <summary>Bug 2 fix: Lerp floor for dribbling resistance.</summary>
        public static readonly float DRIBBLE_RESIST_FLOOR = 0.50f;

        /// <summary>
        /// Base probability of a tackle attempt resulting in a foul
        /// when the tackle fails or is marginal.
        /// 0.30 = 30% chance a missed/marginal tackle is a foul.
        /// </summary>
        public static readonly float FOUL_BASE_PROBABILITY = 0.30f;

        /// <summary>
        /// Bug 3 fix: aggression bonus applied to FOUL probability only, not tackle success.
        /// High defensive aggression increases foul risk.
        /// At DefensiveAggression=1.0: foul probability += FOUL_AGGRESSION_BONUS.
        /// </summary>
        public static readonly float FOUL_AGGRESSION_BONUS = 0.35f;

        /// <summary>Yellow card threshold on foul severity. Above this = yellow.</summary>
        public static readonly float YELLOW_CARD_THRESHOLD = 0.65f;

        /// <summary>Red card threshold on foul severity (straight red). Very rare.</summary>
        public static readonly float RED_CARD_THRESHOLD = 0.92f;

        // ── Intercept ─────────────────────────────────────────────────────────

        /// <summary>
        /// Bug 8: base lane width — scaled by speed ratio at runtime.
        /// Half-width of the interception lane around the ball's flight path.
        /// A defender within this perpendicular distance can attempt an interception.
        /// 35 units = 3.5m. A player can reach about 3.5m to either side while running.
        /// </summary>
        public static readonly float INTERCEPT_LANE_WIDTH = 35f;

        /// <summary>Bug 8: average pass speed used as normalisation baseline.</summary>
        public static readonly float INTERCEPT_LANE_AVG_SPEED = 20f;

        /// <summary>Min/max intercept success probability.</summary>
        public static readonly float INTERCEPT_P_MIN = 0.05f;
        public static readonly float INTERCEPT_P_MAX = 0.88f;

        /// <summary>
        /// Probability that an interception results in clean possession vs deflection.
        /// 0.65 = 65% chance of clean claim; 35% chance ball just goes Loose.
        /// </summary>
        public static readonly float INTERCEPT_CLEAN_CLAIM_P = 0.65f;

        // ── Loose Ball — Bug 12 fix: three separate weights sum to 1.0 ────────

        /// <summary>
        /// If two players contest a Loose ball within BALL_CONTEST_RADIUS:
        /// winner is chosen by three-factor weighting (Bug 12 fix).
        /// </summary>
        public static readonly float LOOSE_BALL_REACTIONS_WEIGHT = 0.50f;
        public static readonly float LOOSE_BALL_STAMINA_WEIGHT = 0.25f;
        public static readonly float LOOSE_BALL_PROXIMITY_WEIGHT = 0.25f;

        // ── GK Save ───────────────────────────────────────────────────────────

        /// <summary>Min/max save probability, scaled by xG and GK Reactions.</summary>
        public static readonly float SAVE_P_MIN = 0.20f;
        public static readonly float SAVE_P_MAX = 0.95f;

        /// <summary>
        /// Distance from goal line at which GK save contest is triggered.
        /// Ball must be within this Y distance of goal line for the GK check to fire.
        /// 80 units = 8m. Shot travelling fast will cross this in ~2 ticks.
        /// </summary>
        public static readonly float GK_SAVE_TRIGGER_DIST = 80f;

        /// <summary>
        /// Probability that a saved shot results in a caught ball (GK owns it)
        /// vs a parried rebound (ball goes Loose near goal).
        /// </summary>
        public static readonly float SAVE_CATCH_P = 0.55f;

        // ── Deflection velocity ───────────────────────────────────────────────

        /// <summary>
        /// Speed of ball deflection when knocked loose by a tackle or block.
        /// 12 units/tick = 1.2 m/s — a soft knock, not a full clearance.
        /// </summary>
        public static readonly float DEFLECTION_SPEED = 12f;

        // ── Header — Bug 14 fix: probabilistic range ──────────────────────────

        /// <summary>Bug 14 fix: probabilistic range for header contests.</summary>
        public static readonly float HEADER_P_MIN = 0.30f;
        public static readonly float HEADER_P_MAX = 0.90f;
    }

    // =========================================================================
    // COLLISION SYSTEM
    // =========================================================================

    public static class CollisionSystem
    {
        // ── DEBUG ─────────────────────────────────────────────────────────────

        /// <summary>
        /// When true, logs every contest resolved this tick with probability,
        /// roll, and outcome. Filter by DEBUG_PLAYER_ID for a single player.
        /// </summary>
        public static bool DEBUG = false;

        /// <summary>-1 = log all contests. Set to specific PlayerId to filter.</summary>
        public static int DEBUG_PLAYER_ID = -1;

        // Reusable list — avoids per-tick heap allocation for tackle candidate collection
        private static readonly List<(int idx, float dist)> _tackleCandidates
            = new List<(int, float)>(6);

        // ── Main Tick ─────────────────────────────────────────────────────────

        /// <summary>
        /// Called once per tick by TickSystem after BallSystem.Tick().
        /// Runs all contest checks, writes ctx.LastCollisionResult.
        /// May call BallSystem.AssignOwnership or BallSystem.SetLoose.
        /// Never calls PlayerAI or DecisionSystem.
        /// </summary>
        public static void Tick(MatchContext ctx)
        {
            // Reset last result
            ctx.LastCollisionResult = new CollisionResult
            {
                Type = CollisionResultType.None,
                PrimaryPlayerId = -1,
                SecondaryPlayerId = -1,
            };

            // Skip contests when match is not in live-play states
            if (ctx.Phase == MatchPhase.HalfTime ||
                ctx.Phase == MatchPhase.FullTime ||
                ctx.Phase == MatchPhase.GoalScored)
                return;

            // Bug 10 fix: explicit routing — shots and passes never share a path
            if (ctx.Ball.Phase == BallPhase.InFlight)
            {
                if (ctx.Ball.IsShot)
                    ResolveGoalCheck(ctx);
                else
                    ResolveInterceptContests(ctx);
            }
            else if (ctx.Ball.Phase == BallPhase.Owned)
            {
                if (ctx.Ball.Height >= 0.5f)
                    ResolveHeaderContest(ctx);
                else
                    ResolveTackleContests(ctx);
            }
            else if (ctx.Ball.Phase == BallPhase.Loose)
            {
                ResolveLooseBallClaim(ctx);
            }
        }

        // =====================================================================
        // TACKLE CONTESTS — Bug 1, 2, 3, 23 fixed
        // =====================================================================

        private static void ResolveTackleContests(MatchContext ctx)
        {
            int ownerId = ctx.Ball.OwnerId;
            if (ownerId < 0 || ownerId > 21) return;

            ref PlayerState carrier = ref ctx.Players[ownerId];
            if (!carrier.IsActive) return;

            int oppStart = carrier.TeamId == 0 ? 11 : 0;
            int oppEnd = carrier.TeamId == 0 ? 22 : 11;

            // Bug 1: collect ALL eligible tacklers
            _tackleCandidates.Clear();
            for (int i = oppStart; i < oppEnd; i++)
            {
                ref PlayerState def = ref ctx.Players[i];
                if (!def.IsActive) continue;
                if (def.Action != PlayerAction.TackleAttempt) continue;
                if (def.TackleCooldownTicks > 0) continue; // Bug 23: skip cooling-down defenders

                float dist = def.Position.DistanceTo(carrier.Position);
                if (dist > CollisionConstants.TACKLE_RANGE) continue;

                _tackleCandidates.Add((i, dist));
            }

            if (_tackleCandidates.Count == 0) return;

            // Sort nearest first — nearest defender gets first attempt
            _tackleCandidates.Sort((a, b) => a.dist.CompareTo(b.dist));

            foreach (var (idx, dist) in _tackleCandidates)
            {
                ref PlayerState defender = ref ctx.Players[idx];
                RunTackleContest(ref defender, ref carrier, dist, ctx);

                // Stop on decisive outcome; clean misses allow next candidate
                if (ctx.LastCollisionResult.Type == CollisionResultType.TackleSuccess ||
                    ctx.LastCollisionResult.Type == CollisionResultType.TaclkeFoul)
                    break;
            }
        }

        private static void RunTackleContest(ref PlayerState defender,
                                              ref PlayerState carrier,
                                              float dist,
                                              MatchContext ctx)
        {
            TacticsInput tactics = defender.TeamId == 0
                ? ctx.HomeTeam.Tactics : ctx.AwayTeam.Tactics;

            // Bug 2 fix: correct formula — multiplicative not collapsed Lerp
            // pBase = defender ability mapped to [TACKLE_P_MIN, TACKLE_P_MAX]
            float pBase = MathUtil.Lerp(
                CollisionConstants.TACKLE_P_MIN,
                CollisionConstants.TACKLE_P_MAX,
                defender.DefendingAbility
            );
            // dribblingResist = Lerp(0.5, 1.0, 1 - dribbling) as per spec
            float dribblingResist = MathUtil.Lerp(
                CollisionConstants.DRIBBLE_RESIST_FLOOR,
                1.0f,
                1.0f - carrier.DribblingAbility
            );
            float staminaFactor = MathUtil.Lerp(0.6f, 1.0f, defender.Stamina);
            float distFactor = MathUtil.Lerp(1.0f, 0.7f,
                                      dist / CollisionConstants.TACKLE_RANGE);

            // Bug 3 fix: aggression NOT in success formula — only affects foul risk below
            float p = MathUtil.Clamp01(pBase * dribblingResist * staminaFactor * distFactor);

            double roll = NextDouble(ctx);

            if (DEBUG && (DEBUG_PLAYER_ID < 0 ||
                          DEBUG_PLAYER_ID == defender.PlayerId ||
                          DEBUG_PLAYER_ID == carrier.PlayerId))
                Console.WriteLine(
                    $"[CollisionSystem] Tick {ctx.Tick}: TACKLE P{defender.PlayerId}" +
                    $" vs P{carrier.PlayerId} p={p:F3} roll={roll:F3} dist={dist:F1}");

            if (roll < p)
            {
                BallSystem.AssignOwnership(ctx, defender.PlayerId);

                ctx.LastCollisionResult = new CollisionResult
                {
                    Type = CollisionResultType.TackleSuccess,
                    PrimaryPlayerId = defender.PlayerId,
                    SecondaryPlayerId = carrier.PlayerId,
                    Position = defender.Position,
                    Probability = p,
                    IsClean = true,
                    DefendingTeamId = defender.TeamId,
                };

                // Bug 23: brief cooldown on success
                defender.TackleCooldownTicks = AIConstants.TACKLE_RECOVERY_TICKS_SUCCESS;
            }
            else
            {
                // Bug 3: aggression raises foul probability, not tackle probability
                float foulP = ComputeFoulProbability(tactics.DefensiveAggression, dist);
                double foulRoll = NextDouble(ctx);

                // Bug 23: recovery on miss/foul
                defender.TackleCooldownTicks = AIConstants.TACKLE_RECOVERY_TICKS_FAILED;

                if (foulRoll < foulP)
                {
                    float severity = ComputeFoulSeverity(dist, tactics.DefensiveAggression, ctx);
                    ctx.LastCollisionResult = new CollisionResult
                    {
                        Type = CollisionResultType.TaclkeFoul,
                        PrimaryPlayerId = defender.PlayerId,
                        SecondaryPlayerId = carrier.PlayerId,
                        Position = defender.Position,
                        Probability = foulP,
                        FoulSeverity = severity,
                        IsClean = false,
                        DefendingTeamId = carrier.TeamId,
                    };
                }
                else
                {
                    ctx.LastCollisionResult = new CollisionResult
                    {
                        Type = CollisionResultType.TackleFailed,
                        PrimaryPlayerId = defender.PlayerId,
                        SecondaryPlayerId = carrier.PlayerId,
                        Position = defender.Position,
                        Probability = p,
                        IsClean = true,
                        DefendingTeamId = carrier.TeamId,
                    };
                }
            }
        }

        // =====================================================================
        // INTERCEPT CONTESTS — Bug 8, 9, 10, 20 fixed
        // =====================================================================

        private static void ResolveInterceptContests(MatchContext ctx)
        {
            if (ctx.Ball.IsShot) return; // Bug 10: defensive guard (dispatcher handles this)

            Vec2 ballPos = ctx.Ball.Position;
            Vec2 ballVel = ctx.Ball.Velocity;
            if (ballVel.LengthSquared() < 0.01f) return;

            float ballSpeed = ballVel.Length();
            Vec2 ballDir = ballVel.Normalized();

            int lastTouched = ctx.Ball.LastTouchedBy;
            if (lastTouched < 0 || lastTouched > 21) return;

            // Bug 20: use TeamId
            int attackingTeam = ctx.Players[lastTouched].TeamId;
            int defStart = attackingTeam == 0 ? 11 : 0;
            int defEnd = attackingTeam == 0 ? 22 : 11;
            int passTarget = ctx.Ball.PassTargetId;

            // Bug 8: narrow lane for fast balls, wider for slow balls
            float speedRatio = MathUtil.Clamp01(CollisionConstants.INTERCEPT_LANE_AVG_SPEED
                                                       / MathF.Max(1f, ballSpeed));
            float effectiveLaneW = CollisionConstants.INTERCEPT_LANE_WIDTH
                                     * MathUtil.Lerp(0.5f, 1.5f, speedRatio);

            // Bug 9: physically-grounded range limit
            float maxInterceptRange = ballSpeed * AIConstants.DEFENDER_INTERCEPT_REACTION_TICKS;

            float bestP = -1f;
            int bestId = -1;

            for (int i = defStart; i < defEnd; i++)
            {
                ref PlayerState def = ref ctx.Players[i];
                if (!def.IsActive) continue;
                if (i == passTarget) continue;

                Vec2 toPlayer = def.Position - ballPos;
                float along = toPlayer.Dot(ballDir);
                if (along < 0f || along > maxInterceptRange) continue;

                float lateral = MathF.Sqrt(MathF.Max(0f,
                    toPlayer.LengthSquared() - along * along));
                if (lateral > effectiveLaneW) continue;

                float laneT = MathUtil.Lerp(1f, 0f, lateral / effectiveLaneW);
                float reactionT = MathUtil.Lerp(0f, 1f, def.Reactions);
                float p = MathUtil.Lerp(
                    CollisionConstants.INTERCEPT_P_MIN,
                    CollisionConstants.INTERCEPT_P_MAX,
                    laneT * reactionT
                );

                if (p > bestP) { bestP = p; bestId = i; }
            }

            if (bestId < 0 || bestP <= 0f) return;

            double roll = NextDouble(ctx);
            ref PlayerState interceptor = ref ctx.Players[bestId];

            if (DEBUG && (DEBUG_PLAYER_ID < 0 || DEBUG_PLAYER_ID == bestId))
                Console.WriteLine(
                    $"[CollisionSystem] Tick {ctx.Tick}: INTERCEPT P{bestId}" +
                    $" p={bestP:F3} roll={roll:F3} laneW={effectiveLaneW:F1}");

            if (roll < bestP)
            {
                double claimRoll = NextDouble(ctx);
                bool cleanClaim = claimRoll < CollisionConstants.INTERCEPT_CLEAN_CLAIM_P;

                if (cleanClaim || ctx.Ball.Height < 0.3f)
                {
                    BallSystem.AssignOwnership(ctx, bestId);
                    ctx.LastCollisionResult = new CollisionResult
                    {
                        Type = CollisionResultType.InterceptSuccess,
                        PrimaryPlayerId = bestId,
                        SecondaryPlayerId = lastTouched,
                        Position = interceptor.Position,
                        Probability = bestP,
                        IsClean = true,
                        DefendingTeamId = interceptor.TeamId,
                    };
                }
                else
                {
                    Vec2 deflection = ComputeDeflectionVelocity(ctx.Ball.Velocity, ctx);
                    BallSystem.SetLoose(ctx, deflection);
                    ctx.LastCollisionResult = new CollisionResult
                    {
                        Type = CollisionResultType.InterceptDeflection,
                        PrimaryPlayerId = bestId,
                        SecondaryPlayerId = lastTouched,
                        Position = interceptor.Position,
                        Probability = bestP,
                        IsClean = false,
                        DefendingTeamId = interceptor.TeamId,
                    };
                }
            }
        }

        // =====================================================================
        // LOOSE BALL — Bug 12 fixed
        // =====================================================================

        private static void ResolveLooseBallClaim(MatchContext ctx)
        {
            float radius = PhysicsConstants.BALL_CONTEST_RADIUS;

            int contesterA = -1, contesterB = -1;
            float distA = float.MaxValue, distB = float.MaxValue;

            for (int i = 0; i < 22; i++)
            {
                ref PlayerState p = ref ctx.Players[i];
                if (!p.IsActive) continue;

                float claimRadius = IsGK(p.Role) && IsInOwnPenaltyBox(ref p, ctx)
                    ? PhysicsConstants.GK_CLAIM_RADIUS : radius;

                float dSq = p.Position.DistanceSquaredTo(ctx.Ball.Position);
                if (dSq > claimRadius * claimRadius) continue;

                float d = MathF.Sqrt(dSq);
                if (d < distA) { distB = distA; contesterB = contesterA; distA = d; contesterA = i; }
                else if (d < distB) { distB = d; contesterB = i; }
            }

            if (contesterA < 0) return;

            int winner;
            if (contesterB < 0)
            {
                winner = contesterA;
            }
            else
            {
                ref PlayerState pA = ref ctx.Players[contesterA];
                ref PlayerState pB = ref ctx.Players[contesterB];

                // Bug 12 fix: three-factor weighting — reactions + stamina + proximity
                float rA = MathUtil.Lerp(0.3f, 1.0f, pA.Reactions)
                              * CollisionConstants.LOOSE_BALL_REACTIONS_WEIGHT;
                float rB = MathUtil.Lerp(0.3f, 1.0f, pB.Reactions)
                              * CollisionConstants.LOOSE_BALL_REACTIONS_WEIGHT;
                float sA = MathUtil.Lerp(0.7f, 1.0f, pA.Stamina)
                              * CollisionConstants.LOOSE_BALL_STAMINA_WEIGHT;
                float sB = MathUtil.Lerp(0.7f, 1.0f, pB.Stamina)
                              * CollisionConstants.LOOSE_BALL_STAMINA_WEIGHT;
                float proxA = MathUtil.Lerp(1.0f, 0.5f, distA / radius)
                              * CollisionConstants.LOOSE_BALL_PROXIMITY_WEIGHT;
                float proxB = MathUtil.Lerp(1.0f, 0.5f, distB / radius)
                              * CollisionConstants.LOOSE_BALL_PROXIMITY_WEIGHT;

                float wA = rA + sA + proxA;
                float wB = rB + sB + proxB;
                float total = wA + wB;

                double roll = NextDouble(ctx);
                winner = roll < (wA / total) ? contesterA : contesterB;
            }

            BallSystem.AssignOwnership(ctx, winner);
            ctx.LastCollisionResult = new CollisionResult
            {
                Type = CollisionResultType.LooseBallClaimed,
                PrimaryPlayerId = winner,
                Position = ctx.Ball.Position,
                Probability = 1f,
                IsClean = true,
                DefendingTeamId = ctx.Players[winner].TeamId,
            };
        }

        // =====================================================================
        // GK SAVE / GOAL CHECK — Bug 4, 5, 6, 7, 21 fixed
        // =====================================================================

        private static void ResolveGoalCheck(MatchContext ctx)
        {
            if (!ctx.Ball.IsShot) return;

            // Bug 6: only resolve once per shot
            if (ctx.Ball.ShotContestResolved) return;

            int lastTouchedBy = ctx.Ball.LastTouchedBy;
            if (lastTouchedBy < 0 || lastTouchedBy > 21) return;

            // Bug 20: TeamId not index band
            int attackingTeam = ctx.Players[lastTouchedBy].TeamId;

            float goalLineY = attackingTeam == 0
                ? PhysicsConstants.AWAY_GOAL_LINE_Y
                : PhysicsConstants.HOME_GOAL_LINE_Y;

            // Bug 5: correct goal X per team
            float goalLeftX = attackingTeam == 0
                ? PhysicsConstants.AWAY_GOAL_LEFT_X
                : PhysicsConstants.HOME_GOAL_LEFT_X;
            float goalRightX = attackingTeam == 0
                ? PhysicsConstants.AWAY_GOAL_RIGHT_X
                : PhysicsConstants.HOME_GOAL_RIGHT_X;

            // Only process when ball is travelling toward goal line
            bool attacksDown = attackingTeam == 0;
            bool approaching = attacksDown ? ctx.Ball.Velocity.Y > 0f
                                           : ctx.Ball.Velocity.Y < 0f;
            if (!approaching) return;

            float distToGoalLine = MathF.Abs(ctx.Ball.Position.Y - goalLineY);
            if (distToGoalLine > CollisionConstants.GK_SAVE_TRIGGER_DIST) return;

            // Bug 7: off-target shots set IsOutOfPlay instead of silently vanishing
            if (!ctx.Ball.ShotOnTarget)
            {
                ctx.Ball.IsOutOfPlay = true;
                ctx.Ball.ShotContestResolved = true;
                return;
            }

            // Bug 4 + Bug 21: use ShotXG stored at shot moment; fallback gracefully
            float shotXG = ctx.Ball.ShotXG > 0f
                ? ctx.Ball.ShotXG
                : EstimateShotXGFromSpeed(ctx.Ball.Velocity.Length());

            // Find defending GK
            int gkTeam = 1 - attackingTeam;
            int gkStart = gkTeam == 0 ? 0 : 11;
            int gkEnd = gkTeam == 0 ? 11 : 22;
            int gkId = -1;

            for (int i = gkStart; i < gkEnd; i++)
            {
                if (!ctx.Players[i].IsActive) continue;
                if (IsGK(ctx.Players[i].Role)) { gkId = i; break; }
            }

            if (gkId < 0)
            {
                ctx.Ball.ShotContestResolved = true;
                RecordGoal(attackingTeam, shotXG, ctx);
                return;
            }

            ref PlayerState gk = ref ctx.Players[gkId];

            float gkOffsetX = MathF.Abs(gk.Position.X - ctx.Ball.Position.X);
            float maxOffset = (goalRightX - goalLeftX) * 0.5f;
            float angleMult = MathUtil.Lerp(1.0f, 0.5f,
                MathUtil.Clamp01(gkOffsetX / maxOffset));

            float savePBase = MathUtil.Lerp(CollisionConstants.SAVE_P_MIN,
                                                CollisionConstants.SAVE_P_MAX,
                                                gk.Reactions);
            float saveDiffMult = MathUtil.Lerp(1.0f, 0.4f, shotXG);
            float saveP = MathUtil.Clamp01(savePBase * saveDiffMult * angleMult);

            double roll = NextDouble(ctx);

            if (DEBUG)
                Console.WriteLine(
                    $"[CollisionSystem] Tick {ctx.Tick}: GK SAVE P{gkId}" +
                    $" saveP={saveP:F3} shotXG={shotXG:F3} roll={roll:F3}");

            // Bug 6: mark resolved regardless of outcome
            ctx.Ball.ShotContestResolved = true;

            if (roll < saveP)
            {
                double catchRoll = NextDouble(ctx);
                bool catches = catchRoll < CollisionConstants.SAVE_CATCH_P;

                if (catches)
                {
                    BallSystem.AssignOwnership(ctx, gkId);
                    ctx.LastCollisionResult = new CollisionResult
                    {
                        Type = CollisionResultType.GKCatch,
                        PrimaryPlayerId = gkId,
                        SecondaryPlayerId = lastTouchedBy,
                        Position = gk.Position,
                        Probability = saveP,
                        ShotXG = shotXG,
                        DefendingTeamId = gkTeam,
                    };
                }
                else
                {
                    Vec2 parryVel = ComputeParryVelocity(ref gk, ctx.Ball.Velocity, ctx);
                    BallSystem.SetLoose(ctx, parryVel);
                    ctx.LastCollisionResult = new CollisionResult
                    {
                        Type = CollisionResultType.GKSave,
                        PrimaryPlayerId = gkId,
                        SecondaryPlayerId = lastTouchedBy,
                        Position = gk.Position,
                        Probability = saveP,
                        ShotXG = shotXG,
                        DefendingTeamId = gkTeam,
                    };
                }
            }
            else
            {
                float xAtGoal = ctx.Ball.Position.X;
                if (xAtGoal >= goalLeftX && xAtGoal <= goalRightX)
                {
                    // Bug 20: use TeamId for own goal
                    bool ownGoal = IsOwnGoalByTeamId(lastTouchedBy, attackingTeam, ctx);
                    if (ownGoal)
                    {
                        ctx.LastCollisionResult = new CollisionResult
                        {
                            Type = CollisionResultType.OwnGoal,
                            PrimaryPlayerId = lastTouchedBy,
                            SecondaryPlayerId = -1,
                            Position = ctx.Ball.Position,
                            ShotXG = shotXG,
                            DefendingTeamId = 1 - attackingTeam,
                        };
                    }
                    else
                    {
                        RecordGoal(attackingTeam, shotXG, ctx); // Bug 21: pass shotXG
                    }
                }
            }
        }

        // =====================================================================
        // HEADER CONTEST — Bug 14 fixed
        // =====================================================================

        private static void ResolveHeaderContest(MatchContext ctx)
        {
            if (ctx.Ball.Height < 0.5f) return;

            int ownerId = ctx.Ball.OwnerId;
            if (ownerId < 0 || ownerId > 21) return;

            ref PlayerState owner = ref ctx.Players[ownerId];
            if (!owner.IsActive || owner.Action != PlayerAction.Heading) return;

            int oppStart = owner.TeamId == 0 ? 11 : 0;
            int oppEnd = owner.TeamId == 0 ? 22 : 11;

            int contesterId = -1;
            float contestDist = float.MaxValue;

            for (int i = oppStart; i < oppEnd; i++)
            {
                ref PlayerState opp = ref ctx.Players[i];
                if (!opp.IsActive || opp.Action != PlayerAction.Heading) continue;
                float d = opp.Position.DistanceTo(owner.Position);
                if (d < contestDist) { contestDist = d; contesterId = i; }
            }

            if (contesterId < 0) return;

            ref PlayerState contester = ref ctx.Players[contesterId];

            // Bug 14: probabilistic draw — previously deterministic higher-attr wins
            float pOwner = MathUtil.Lerp(CollisionConstants.HEADER_P_MIN,
                                              CollisionConstants.HEADER_P_MAX,
                                              owner.Reactions);
            float pContester = MathUtil.Lerp(CollisionConstants.HEADER_P_MIN,
                                              CollisionConstants.HEADER_P_MAX,
                                              contester.Reactions);

            double roll = NextDouble(ctx);
            bool ownerWins = roll < (pOwner / (pOwner + pContester));

            if (ownerWins)
            {
                ctx.LastCollisionResult = new CollisionResult
                {
                    Type = CollisionResultType.HeaderWon,
                    PrimaryPlayerId = ownerId,
                    SecondaryPlayerId = contesterId,
                    Position = owner.Position,
                    Probability = (float)(pOwner / (pOwner + pContester)),
                    DefendingTeamId = contester.TeamId,
                };
            }
            else
            {
                Vec2 clearance = ComputeDeflectionVelocity(ctx.Ball.Velocity, ctx);
                BallSystem.SetLoose(ctx, clearance);
                ctx.LastCollisionResult = new CollisionResult
                {
                    Type = CollisionResultType.HeaderLost,
                    PrimaryPlayerId = contesterId,
                    SecondaryPlayerId = ownerId,
                    Position = contester.Position,
                    Probability = (float)(pContester / (pOwner + pContester)),
                    DefendingTeamId = contester.TeamId,
                };
            }
        }

        // =====================================================================
        // PRIVATE HELPERS
        // =====================================================================

        /// <summary>Bug 21: receives pre-computed shotXG instead of recalculating.</summary>
        private static void RecordGoal(int scoringTeam, float shotXG, MatchContext ctx)
        {
            ctx.LastCollisionResult = new CollisionResult
            {
                Type = CollisionResultType.Goal,
                PrimaryPlayerId = ctx.Ball.LastTouchedBy,
                SecondaryPlayerId = -1,
                Position = ctx.Ball.Position,
                ShotXG = shotXG,
                DefendingTeamId = 1 - scoringTeam,
            };
        }

        /// <summary>Fallback xG estimate when BallState.ShotXG was not set (legacy path).</summary>
        private static float EstimateShotXGFromSpeed(float ballSpeed)
        {
            float speedNorm = MathUtil.Clamp01(ballSpeed / PhysicsConstants.BALL_SHOT_SPEED_POWER);
            return MathUtil.Lerp(0.05f, 0.85f, speedNorm);
        }

        /// <summary>Bug 3 fix: aggression only increases foul risk, not tackle success.</summary>
        private static float ComputeFoulProbability(float aggressionSlider, float dist)
        {
            float aggrBonus = MathUtil.Lerp(0f, CollisionConstants.FOUL_AGGRESSION_BONUS,
                                               aggressionSlider);
            float distBonus = MathUtil.Lerp(0.1f, 0f,
                                               dist / CollisionConstants.TACKLE_RANGE);
            return MathUtil.Clamp01(CollisionConstants.FOUL_BASE_PROBABILITY
                                    + aggrBonus + distBonus);
        }

        private static float ComputeFoulSeverity(float dist, float aggressionSlider,
                                                   MatchContext ctx)
        {
            float baseS = MathUtil.Lerp(0.1f, 0.6f, aggressionSlider);
            float distAdj = MathUtil.Lerp(0.2f, 0f, dist / CollisionConstants.TACKLE_RANGE);
            float jitter = (float)(NextDouble(ctx) * 0.15);
            return MathUtil.Clamp01(baseS + distAdj + jitter);
            // Note Bug 15 deferred: severity ceiling rarely reaches RED_CARD_THRESHOLD.
        }

        private static Vec2 ComputeDeflectionVelocity(Vec2 originalVel, MatchContext ctx)
        {
            // Note Bug 16 deferred: ±1 radian (~57°) is too narrow; expand to full circle later.
            double angleShift = (NextDouble(ctx) - 0.5) * 2.0;
            Vec2 dir = originalVel.Normalized();
            float cos = MathF.Cos((float)angleShift);
            float sin = MathF.Sin((float)angleShift);
            return new Vec2(dir.X * cos - dir.Y * sin,
                            dir.X * sin + dir.Y * cos)
                   * CollisionConstants.DEFLECTION_SPEED;
        }

        private static Vec2 ComputeParryVelocity(ref PlayerState gk, Vec2 shotVel,
                                                   MatchContext ctx)
        {
            // Note Bug 17 deferred: should weight sideways not directly backward.
            Vec2 reverseDir = new Vec2(-shotVel.X, -shotVel.Y).Normalized();
            double splay = (NextDouble(ctx) - 0.5) * 1.2;
            float cos = MathF.Cos((float)splay);
            float sin = MathF.Sin((float)splay);
            return new Vec2(reverseDir.X * cos - reverseDir.Y * sin,
                            reverseDir.X * sin + reverseDir.Y * cos)
                   * (CollisionConstants.DEFLECTION_SPEED * 1.5f);
        }

        private static bool IsGK(PlayerRole role)
            => role == PlayerRole.GK || role == PlayerRole.SK;

        private static bool IsInOwnPenaltyBox(ref PlayerState player, MatchContext ctx)
        {
            float halfW = PhysicsConstants.PENALTY_AREA_HALF_WIDTH;
            float centreX = PhysicsConstants.PITCH_WIDTH * 0.5f;
            bool xInBox = player.Position.X >= centreX - halfW &&
                            player.Position.X <= centreX + halfW;
            if (player.TeamId == 0)
                return xInBox && player.Position.Y <= PhysicsConstants.PENALTY_AREA_DEPTH;
            else
                return xInBox && player.Position.Y >=
                       PhysicsConstants.PITCH_HEIGHT - PhysicsConstants.PENALTY_AREA_DEPTH;
        }

        /// <summary>Bug 20 fix: own goal determined by TeamId, not raw index band.</summary>
        private static bool IsOwnGoalByTeamId(int lastTouchedById, int attackingTeam,
                                               MatchContext ctx)
        {
            if (lastTouchedById < 0 || lastTouchedById > 21) return false;
            return ctx.Players[lastTouchedById].TeamId != attackingTeam;
        }

        /// <summary>
        /// Single point of access for all random draws.
        /// Uses ctx.Random — seeded at match start from ctx.RandomSeed.
        /// NEVER call new Random() inside CollisionSystem.
        /// </summary>
        private static double NextDouble(MatchContext ctx)
            => ctx.Random.NextDouble();
    }
}