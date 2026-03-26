// =============================================================================
// Module:  CollisionSystem.cs (pre-bugs fix)
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
// Probability formulas:
//   Tackle success:
//     p = Lerp(0.2, 0.85, defender.DefendingAbility)
//       × Lerp(0.5, 1.0,  1.0 - attacker.DribblingAbility)
//       × staminaFactor(defender)
//       × aggression(tactics.DefensiveAggression)
//     if random < p → tackle success
//     else          → foul check (random < foulProbability)
//
//   Intercept success:
//     distToTrajectory = perpendicular distance from player to ball flight line
//     p = Lerp(0.9, 0.0, distToTrajectory / INTERCEPT_LANE_WIDTH)
//       × Lerp(0.2, 0.9, interceptor.Reactions)
//     if random < p → intercept (assign ownership, or set loose if aerial)
//
//   Loose ball claim:
//     Nearest player within BALL_CONTEST_RADIUS auto-claims (no roll needed).
//     If two players within radius: weighted random by Reactions attribute.
//
//   GK save:
//     p = Lerp(0.2, 0.95, gk.Reactions)
//       × Lerp(0.5, 1.0,  1.0 - shot_xG)   // harder shots harder to save
//       × angleBonus (how centred the GK is on the shot)
//     if random < p → save (loose ball near goal)
//     else          → goal
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
//   • Header contest is simplified: attacker with higher AerialDuelTendency wins.
//     Full aerial contest physics (trajectory, timing window) comes in a later pass.
// =============================================================================

using System;
using FootballSim.Engine.Models;

namespace FootballSim.Engine.Systems
{
    // =========================================================================
    // COLLISION RESULT — written by CollisionSystem, read by EventSystem
    // =========================================================================

    public enum CollisionResultType
    {
        None,
        TackleSuccess,      // defender wins ball cleanly
        TaclkeFoul,         // defender fouls attacker
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
        public float ShotXG;             // populated on GKSave / Goal
        public int DefendingTeamId;    // which team benefits from the result
    }

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

        /// <summary>Base tackle success probability for an average defender vs average attacker.</summary>
        public static readonly float TACKLE_P_MIN = 0.20f;
        public static readonly float TACKLE_P_MAX = 0.85f;

        /// <summary>
        /// Base probability of a tackle attempt resulting in a foul
        /// when the tackle fails or is marginal.
        /// 0.30 = 30% chance a missed/marginal tackle is a foul.
        /// </summary>
        public static readonly float FOUL_BASE_PROBABILITY = 0.30f;

        /// <summary>
        /// High defensive aggression increases foul risk.
        /// At DefensiveAggression=1.0: foul probability += FOUL_AGGRESSION_BONUS.
        /// </summary>
        public static readonly float FOUL_AGGRESSION_BONUS = 0.25f;

        /// <summary>Yellow card threshold on foul severity. Above this = yellow.</summary>
        public static readonly float YELLOW_CARD_THRESHOLD = 0.65f;

        /// <summary>Red card threshold on foul severity (straight red). Very rare.</summary>
        public static readonly float RED_CARD_THRESHOLD = 0.92f;

        // ── Intercept ─────────────────────────────────────────────────────────

        /// <summary>
        /// Half-width of the interception lane around the ball's flight path.
        /// A defender within this perpendicular distance can attempt an interception.
        /// 35 units = 3.5m. A player can reach about 3.5m to either side while running.
        /// </summary>
        public static readonly float INTERCEPT_LANE_WIDTH = 35f;

        /// <summary>Min/max intercept success probability.</summary>
        public static readonly float INTERCEPT_P_MIN = 0.05f;
        public static readonly float INTERCEPT_P_MAX = 0.88f;

        /// <summary>
        /// Probability that an interception results in clean possession vs deflection.
        /// 0.65 = 65% chance of clean claim; 35% chance ball just goes Loose.
        /// </summary>
        public static readonly float INTERCEPT_CLEAN_CLAIM_P = 0.65f;

        // ── Loose Ball ────────────────────────────────────────────────────────

        /// <summary>
        /// If two players contest a Loose ball within BALL_CONTEST_RADIUS:
        /// winner is chosen by Reactions-weighted random.
        /// </summary>
        public static readonly float LOOSE_BALL_REACTIONS_WEIGHT = 0.70f;

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

            switch (ctx.Ball.Phase)
            {
                case BallPhase.Owned:
                    ResolveTackleContests(ctx);
                    break;

                case BallPhase.InFlight:
                    ResolveInterceptContests(ctx);
                    ResolveGoalCheck(ctx);       // shot could be in flight
                    break;

                case BallPhase.Loose:
                    ResolveLooseBallClaim(ctx);
                    break;
            }
        }

        // =====================================================================
        // TACKLE CONTESTS
        // =====================================================================

        private static void ResolveTackleContests(MatchContext ctx)
        {
            int ownerId = ctx.Ball.OwnerId;
            if (ownerId < 0 || ownerId > 21) return;

            ref PlayerState carrier = ref ctx.Players[ownerId];
            if (!carrier.IsActive) return;

            // Find any opponent in TackleAttempt action within TACKLE_RANGE
            int oppStart = carrier.TeamId == 0 ? 11 : 0;
            int oppEnd = carrier.TeamId == 0 ? 22 : 11;

            for (int i = oppStart; i < oppEnd; i++)
            {
                ref PlayerState defender = ref ctx.Players[i];
                if (!defender.IsActive) continue;
                if (defender.Action != PlayerAction.TackleAttempt) continue;

                float dist = defender.Position.DistanceTo(carrier.Position);
                if (dist > CollisionConstants.TACKLE_RANGE) continue;

                // Run the contest — only the first eligible tackle per tick
                RunTackleContest(ref defender, ref carrier, dist, ctx);
                return;
            }
        }

        private static void RunTackleContest(ref PlayerState defender,
                                              ref PlayerState carrier,
                                              float dist,
                                              MatchContext ctx)
        {
            var tactics = defender.TeamId == 0
                ? ctx.HomeTeam.Tactics : ctx.AwayTeam.Tactics;

            // ── Success probability ────────────────────────────────────────────
            float defScore = MathUtil.Lerp(0f, 1f, defender.DefendingAbility);
            float attScore = MathUtil.Lerp(0f, 1f, 1f - carrier.DribblingAbility);

            float staminaFactor = MathUtil.Lerp(0.6f, 1.0f, defender.Stamina);
            float aggrFactor = MathUtil.Lerp(0.8f, 1.15f, tactics.DefensiveAggression);
            float distFactor = MathUtil.Lerp(1.0f, 0.7f,
                                    dist / CollisionConstants.TACKLE_RANGE);

            float p = MathUtil.Lerp(
                CollisionConstants.TACKLE_P_MIN,
                CollisionConstants.TACKLE_P_MAX,
                defScore * attScore
            ) * staminaFactor * aggrFactor * distFactor;

            p = MathUtil.Clamp01(p);

            double roll = NextDouble(ctx);

            if (DEBUG && (DEBUG_PLAYER_ID < 0 ||
                          DEBUG_PLAYER_ID == defender.PlayerId ||
                          DEBUG_PLAYER_ID == carrier.PlayerId))
                Console.WriteLine(
                    $"[CollisionSystem] Tick {ctx.Tick}: TACKLE P{defender.PlayerId} " +
                    $"vs P{carrier.PlayerId} p={p:F3} roll={roll:F3} dist={dist:F1}");

            if (roll < p)
            {
                // Tackle success — clean
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
            }
            else
            {
                // Tackle failed — check for foul
                float foulP = ComputeFoulProbability(ref defender, tactics.DefensiveAggression,
                                                     dist);
                double foulRoll = NextDouble(ctx);

                if (foulRoll < foulP)
                {
                    float severity = ComputeFoulSeverity(ref defender, dist,
                                                          tactics.DefensiveAggression, ctx);

                    ctx.LastCollisionResult = new CollisionResult
                    {
                        Type = CollisionResultType.TaclkeFoul,
                        PrimaryPlayerId = defender.PlayerId,
                        SecondaryPlayerId = carrier.PlayerId,
                        Position = defender.Position,
                        Probability = foulP,
                        FoulSeverity = severity,
                        IsClean = false,
                        DefendingTeamId = carrier.TeamId, // fouled team benefits
                    };
                    // Ball stays with carrier (foul = free kick)
                }
                else
                {
                    // Clean miss — defender slides past, ball stays with carrier
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
        // INTERCEPT CONTESTS
        // =====================================================================

        private static void ResolveInterceptContests(MatchContext ctx)
        {
            // Only relevant if a pass is in flight (not a shot)
            if (ctx.Ball.IsShot) return;

            Vec2 ballPos = ctx.Ball.Position;
            Vec2 ballVel = ctx.Ball.Velocity;

            if (ballVel.LengthSquared() < 0.01f) return;

            Vec2 ballDir = ballVel.Normalized();

            // Determine which team is defending (team that did NOT send the ball)
            int lastTouched = ctx.Ball.LastTouchedBy;
            if (lastTouched < 0 || lastTouched > 21) return;

            int attackingTeam = ctx.Players[lastTouched].TeamId;
            int defStart = attackingTeam == 0 ? 11 : 0;
            int defEnd = attackingTeam == 0 ? 22 : 11;

            // Don't test the intended pass receiver (BallSystem handles that arrival)
            int passTarget = ctx.Ball.PassTargetId;

            float bestP = -1f;
            int bestId = -1;

            for (int i = defStart; i < defEnd; i++)
            {
                ref PlayerState def = ref ctx.Players[i];
                if (!def.IsActive) continue;
                if (i == passTarget) continue; // receiver claimed by BallSystem

                // Project defender onto ball trajectory line
                Vec2 toPlayer = def.Position - ballPos;
                float along = toPlayer.Dot(ballDir);     // signed, must be > 0

                // Only intercept if ball is still approaching (ahead on trajectory)
                if (along < 0f) continue;

                float maxInterceptRange = ballVel.Length() * 20f; // example scaling factor
                if (along > maxInterceptRange) continue;

                float lateral = MathF.Sqrt(MathF.Max(0f,
                    toPlayer.LengthSquared() - along * along));

                if (lateral > CollisionConstants.INTERCEPT_LANE_WIDTH) continue;

                // Intercept probability scales with closeness to trajectory and Reactions
                float laneT = MathUtil.Lerp(1f, 0f,
                    lateral / CollisionConstants.INTERCEPT_LANE_WIDTH);

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
                    $"[CollisionSystem] Tick {ctx.Tick}: INTERCEPT candidate P{bestId} " +
                    $"p={bestP:F3} roll={roll:F3}");

            if (roll < bestP)
            {
                // Check: clean claim or deflection
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
                    // Deflection: ball goes Loose in a randomised direction
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
        // LOOSE BALL CLAIMS
        // =====================================================================

        private static void ResolveLooseBallClaim(MatchContext ctx)
        {
            float radius = PhysicsConstants.BALL_CONTEST_RADIUS;
            float radiusSq = radius * radius;

            // Collect all players within contest radius
            int contesterA = -1;
            int contesterB = -1;
            float distA = float.MaxValue, distB = float.MaxValue;

            for (int i = 0; i < 22; i++)
            {
                ref PlayerState p = ref ctx.Players[i];
                if (!p.IsActive) continue;

                // GK: if in own penalty box and ball is loose near goal, check wider
                float claimRadius = IsGK(p.Role) && IsInOwnPenaltyBox(ref p, ctx)
                    ? PhysicsConstants.GK_CLAIM_RADIUS
                    : radius;

                float dSq = p.Position.DistanceSquaredTo(ctx.Ball.Position);
                if (dSq > claimRadius * claimRadius) continue;

                float d = MathF.Sqrt(dSq);
                if (d < distA)
                {
                    distB = distA; contesterB = contesterA;
                    distA = d; contesterA = i;
                }
                else if (d < distB)
                {
                    distB = d; contesterB = i;
                }
            }

            if (contesterA < 0) return; // nobody close enough

            int winner;
            if (contesterB < 0)
            {
                // Only one player nearby — auto-claim
                winner = contesterA;
            }
            else
            {
                // Two players contesting — weighted random by Reactions
                ref PlayerState pA = ref ctx.Players[contesterA];
                ref PlayerState pB = ref ctx.Players[contesterB];

                float wA = MathUtil.Lerp(
                    1f - CollisionConstants.LOOSE_BALL_REACTIONS_WEIGHT,
                    1f,
                    pA.Reactions);
                float wB = MathUtil.Lerp(
                    1f - CollisionConstants.LOOSE_BALL_REACTIONS_WEIGHT,
                    1f,
                    pB.Reactions);

                // Normalise and pick
                float total = wA + wB;
                double roll = NextDouble(ctx);
                winner = roll < (wA / total) ? contesterA : contesterB;

                if (DEBUG && (DEBUG_PLAYER_ID < 0 ||
                              DEBUG_PLAYER_ID == contesterA ||
                              DEBUG_PLAYER_ID == contesterB))
                    Console.WriteLine(
                        $"[CollisionSystem] Tick {ctx.Tick}: LOOSE BALL contest " +
                        $"P{contesterA}(wA={wA:F2}) vs P{contesterB}(wB={wB:F2}) " +
                        $"roll={roll:F3} winner=P{winner}");
            }

            BallSystem.AssignOwnership(ctx, winner);

            ctx.LastCollisionResult = new CollisionResult
            {
                Type = CollisionResultType.LooseBallClaimed,
                PrimaryPlayerId = winner,
                SecondaryPlayerId = -1,
                Position = ctx.Ball.Position,
                Probability = 1f,
                IsClean = true,
                DefendingTeamId = ctx.Players[winner].TeamId,
            };
        }

        // =====================================================================
        // GK SAVE / GOAL CHECK
        // =====================================================================

        private static void ResolveGoalCheck(MatchContext ctx)
        {
            if (!ctx.Ball.IsShot) return;

            // Determine which goal the ball might be approaching
            // Home team (TeamId=0) attacks toward Y=680 (away goal).
            int attackingTeam;
            if (ctx.Ball.LastTouchedBy >= 0 && ctx.Ball.LastTouchedBy <= 10)
                attackingTeam = 0;
            else if (ctx.Ball.LastTouchedBy >= 11 && ctx.Ball.LastTouchedBy <= 21)
                attackingTeam = 1;
            else return;

            float goalLineY = attackingTeam == 0
                ? PhysicsConstants.AWAY_GOAL_LINE_Y
                : PhysicsConstants.HOME_GOAL_LINE_Y;

            float goalLeftX = PhysicsConstants.AWAY_GOAL_LEFT_X;
            float goalRightX = PhysicsConstants.AWAY_GOAL_RIGHT_X;

            bool ballApproachingGoal = ctx.AttacksDownward(attackingTeam)
                ? ctx.Ball.Velocity.Y > 0f  // home attacks down
                : ctx.Ball.Velocity.Y < 0f; // away attacks up

            if (!ballApproachingGoal) return;

            float distToGoalLine = MathF.Abs(ctx.Ball.Position.Y - goalLineY);
            if (distToGoalLine > CollisionConstants.GK_SAVE_TRIGGER_DIST) return;

            // Only fire save check once — prevent repeated triggers as ball travels
            // Guard: if a GKSave or Goal result is already set this tick, skip
            if (ctx.LastCollisionResult.Type == CollisionResultType.GKSave ||
                ctx.LastCollisionResult.Type == CollisionResultType.GKCatch ||
                ctx.LastCollisionResult.Type == CollisionResultType.Goal)
                return;

            // Only trigger when shot is actually heading toward goal mouth
            if (!ctx.Ball.ShotOnTarget)
            {
                // Off-target: no save needed, ball goes out or wide
                return;
            }

            // Find opponent GK
            int gkTeam = attackingTeam == 0 ? 1 : 0;
            int gkStart = gkTeam == 0 ? 0 : 11;
            int gkEnd = gkTeam == 0 ? 11 : 22;
            int gkId = -1;

            for (int i = gkStart; i < gkEnd; i++)
            {
                if (!ctx.Players[i].IsActive) continue;
                if (IsGK(ctx.Players[i].Role))
                {
                    gkId = i;
                    break;
                }
            }

            if (gkId < 0)
            {
                // No GK — goal (empty net)
                RecordGoal(attackingTeam, ctx);
                return;
            }

            ref PlayerState gk = ref ctx.Players[gkId];

            // Estimate xG from ball velocity magnitude as proxy
            // Real xG was calculated at shot moment; we approximate here from speed
            float ballSpeed = ctx.Ball.Velocity.Length();
            float speedNorm = MathUtil.Clamp01(ballSpeed / PhysicsConstants.BALL_SHOT_SPEED_POWER);
            float shotXG = MathUtil.Lerp(0.05f, 0.85f, speedNorm);

            // Angle bonus: how centred is GK on the ball's X trajectory
            float ballX = ctx.Ball.Position.X;
            float gkOffsetX = MathF.Abs(gk.Position.X - ballX);
            float maxOffset = (goalRightX - goalLeftX) * 0.5f;
            float angleMult = MathUtil.Lerp(1.0f, 0.5f, gkOffsetX / maxOffset);

            float savePBase = MathUtil.Lerp(
                CollisionConstants.SAVE_P_MIN,
                CollisionConstants.SAVE_P_MAX,
                gk.Reactions
            );

            // Harder shot (higher xG) = lower save probability
            float saveDiffMult = MathUtil.Lerp(1.0f, 0.4f, shotXG);
            float saveP = MathUtil.Clamp01(savePBase * saveDiffMult * angleMult);

            double roll = NextDouble(ctx);

            if (DEBUG)
                Console.WriteLine(
                    $"[CollisionSystem] Tick {ctx.Tick}: GK SAVE check P{gkId} " +
                    $"saveP={saveP:F3} shotXG={shotXG:F3} roll={roll:F3}");

            if (roll < saveP)
            {
                // Save — catch or parry?
                double catchRoll = NextDouble(ctx);
                bool catches = catchRoll < CollisionConstants.SAVE_CATCH_P;

                if (catches)
                {
                    BallSystem.AssignOwnership(ctx, gkId);

                    ctx.LastCollisionResult = new CollisionResult
                    {
                        Type = CollisionResultType.GKCatch,
                        PrimaryPlayerId = gkId,
                        SecondaryPlayerId = ctx.Ball.LastTouchedBy,
                        Position = gk.Position,
                        Probability = saveP,
                        ShotXG = shotXG,
                        DefendingTeamId = gkTeam,
                    };
                }
                else
                {
                    // Parry: ball goes Loose near goal
                    Vec2 parryVel = ComputeParryVelocity(ref gk, ctx.Ball.Velocity, ctx);
                    BallSystem.SetLoose(ctx, parryVel);

                    ctx.LastCollisionResult = new CollisionResult
                    {
                        Type = CollisionResultType.GKSave,
                        PrimaryPlayerId = gkId,
                        SecondaryPlayerId = ctx.Ball.LastTouchedBy,
                        Position = gk.Position,
                        Probability = saveP,
                        ShotXG = shotXG,
                        DefendingTeamId = gkTeam,
                    };
                }
            }
            else
            {
                // No save — goal!
                // Verify ball is actually within goal mouth X range
                float xAtGoal = ctx.Ball.Position.X;
                bool ownGoal = IsOwnGoal(ctx.Ball.LastTouchedBy, attackingTeam);

                if (xAtGoal >= goalLeftX && xAtGoal <= goalRightX)
                {
                    if (ownGoal)
                    {
                        ctx.LastCollisionResult = new CollisionResult
                        {
                            Type = CollisionResultType.OwnGoal,
                            PrimaryPlayerId = ctx.Ball.LastTouchedBy,
                            SecondaryPlayerId = -1,
                            Position = ctx.Ball.Position,
                            ShotXG = shotXG,
                            DefendingTeamId = 1 - attackingTeam,
                        };
                    }
                    else
                    {
                        RecordGoal(attackingTeam, ctx);
                    }
                }
            }
        }

        // =====================================================================
        // PRIVATE HELPERS
        // =====================================================================

        private static void RecordGoal(int scoringTeam, MatchContext ctx)
        {
            float ballSpeed = ctx.Ball.Velocity.Length();
            float speedNorm = MathUtil.Clamp01(ballSpeed / PhysicsConstants.BALL_SHOT_SPEED_POWER);
            float shotXG = MathUtil.Lerp(0.05f, 0.85f, speedNorm);

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

        private static float ComputeFoulProbability(ref PlayerState defender,
                                                     float aggressionSlider,
                                                     float dist)
        {
            float aggrBonus = MathUtil.Lerp(0f, CollisionConstants.FOUL_AGGRESSION_BONUS,
                                               aggressionSlider);
            float distBonus = MathUtil.Lerp(0.1f, 0f, dist / CollisionConstants.TACKLE_RANGE);
            return MathUtil.Clamp01(CollisionConstants.FOUL_BASE_PROBABILITY
                                    + aggrBonus + distBonus);
        }

        private static float ComputeFoulSeverity(ref PlayerState defender,
                                                   float dist,
                                                   float aggressionSlider,
                                                   MatchContext ctx)
        {
            float baseS = MathUtil.Lerp(0.1f, 0.6f, aggressionSlider);
            float distAdj = MathUtil.Lerp(0.2f, 0f, dist / CollisionConstants.TACKLE_RANGE);
            float jitter = (float)(NextDouble(ctx) * 0.15); // small random variance
            return MathUtil.Clamp01(baseS + distAdj + jitter);
        }

        private static Vec2 ComputeDeflectionVelocity(Vec2 originalVel, MatchContext ctx)
        {
            // Deflect at a random angle variation from the original direction
            // Use seeded random to stay deterministic
            double angleShift = (NextDouble(ctx) - 0.5) * 2.0; // -1 to 1 radians
            Vec2 dir = originalVel.Normalized();
            float cos = MathF.Cos((float)angleShift);
            float sin = MathF.Sin((float)angleShift);
            Vec2 deflDir = new Vec2(dir.X * cos - dir.Y * sin,
                                     dir.X * sin + dir.Y * cos);
            return deflDir * CollisionConstants.DEFLECTION_SPEED;
        }

        private static Vec2 ComputeParryVelocity(ref PlayerState gk, Vec2 shotVel,
                                                   MatchContext ctx)
        {
            // Parry pushes ball roughly away from GK in a random direction
            Vec2 reverseDir = new Vec2(-shotVel.X, -shotVel.Y).Normalized();
            double splay = (NextDouble(ctx) - 0.5) * 1.2; // random spread
            float cos = MathF.Cos((float)splay);
            float sin = MathF.Sin((float)splay);
            Vec2 parryDir = new Vec2(
                reverseDir.X * cos - reverseDir.Y * sin,
                reverseDir.X * sin + reverseDir.Y * cos
            );
            return parryDir * (CollisionConstants.DEFLECTION_SPEED * 1.5f);
        }

        private static bool IsGK(PlayerRole role)
            => role == PlayerRole.GK || role == PlayerRole.SK;

        private static bool IsInOwnPenaltyBox(ref PlayerState player, MatchContext ctx)
        {
            // Home GK is at top (Y close to 0), away GK at bottom (Y close to 680)
            float halfW = PhysicsConstants.PENALTY_AREA_HALF_WIDTH;
            float centreX = PhysicsConstants.PITCH_WIDTH * 0.5f;

            bool xInBox = player.Position.X >= centreX - halfW &&
                          player.Position.X <= centreX + halfW;

            if (player.TeamId == 0) // home GK
                return xInBox && player.Position.Y <= PhysicsConstants.PENALTY_AREA_DEPTH;
            else // away GK
                return xInBox && player.Position.Y >=
                       PhysicsConstants.PITCH_HEIGHT - PhysicsConstants.PENALTY_AREA_DEPTH;
        }

        private static bool IsOwnGoal(int lastTouchedById, int attackingTeam)
        {
            if (lastTouchedById < 0 || lastTouchedById > 21) return false;
            // Own goal when the last touch was by the defending team
            int lastTeam = lastTouchedById <= 10 ? 0 : 1;
            return lastTeam != attackingTeam;
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