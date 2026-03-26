// =============================================================================
// Module:  PlayerAI.cs
// Path:    FootballSim/Engine/Systems/PlayerAI.cs
// Purpose:
//   Applies intelligence to all 22 players each tick. The only system that:
//     1. Calls DecisionSystem to get a scored ActionPlan
//     2. Writes the result to PlayerState (Action, TargetPosition, IsSprinting)
//     3. Calls BallSystem public methods (LaunchPass, LaunchShot, LaunchCross)
//        when a player executes a ball action
//     4. Manages DecisionCooldown — skips re-evaluation on cooldown ticks
//     5. Handles special cases: GK, Celebrating, TakingSetPiece, action lock
//
//   Execution order within TickSystem (MUST be respected):
//     1. MovementSystem.Tick(ctx)   ← updates positions
//     2. PlayerAI.Tick(ctx)         ← reads positions, decides, calls BallSystem launch
//     3. BallSystem.Tick(ctx)       ← moves ball, syncs flags
//
//   PlayerAI is the only place where DecisionSystem is called and where
//   BallSystem.LaunchPass / LaunchShot / LaunchCross are invoked.
//   CollisionSystem will also call BallSystem.AssignOwnership / SetLoose (Step 5).
//
// API:
//   PlayerAI.Tick(MatchContext ctx) → void
//     Main entry point. Iterates all 22 players. Skips inactive.
//     Manages cooldown, delegates decision, applies result.
//
//   PlayerAI.InitialiseMatch(MatchContext ctx, System.Random rng) → void
//     Called once at match start by MatchEngine.
//     Staggers decision cooldowns so players don't all re-evaluate at tick 0.
//
// Per-player flow inside Tick():
//   if (locked)          → skip entirely (action lock after kick, celebrating)
//   if (cooldown > 0)    → decrement cooldown, apply existing TargetPosition only
//   if (cooldown == 0)   → call decision, apply ActionPlan, reset cooldown
//   if (plan.Action == Passing/Shooting/Crossing) → call BallSystem launch method
//
// Dependencies:
//   Engine/Models/MatchContext.cs
//   Engine/Models/PlayerState.cs
//   Engine/Models/Enums.cs
//   Engine/Systems/DecisionSystem.cs  (ActionPlan, evaluators)
//   Engine/Systems/BallSystem.cs      (LaunchPass, LaunchShot, LaunchCross)
//   Engine/Systems/AIConstants.cs
//   Engine/Systems/PhysicsConstants.cs
//   Engine/Tactics/RoleRegistry.cs    (role lookup — read only)
//
// Notes:
//   • PlayerAI is the ONLY caller of BallSystem.LaunchPass / LaunchShot / LaunchCross.
//     No other system should call these during normal play.
//   • IsSprinting is SET here, not derived from Action in MovementSystem.
//     MovementSystem's fallback derivation is overridden by explicit assignment here.
//   • Debug: when DEBUG=true, logs every decision with tick, player, chosen action,
//     target position, and score. Filterable by DEBUG_PLAYER_ID.
//   • randomiser is seeded from MatchContext.RandomSeed for determinism.
//     The rng instance is per-match, not per-tick, ensuring reproducibility.
// =============================================================================

using System;
using FootballSim.Engine.Models;
using FootballSim.Engine.Systems;
using FootballSim.Engine;

namespace FootballSim.Engine.Systems
{
    public static class PlayerAI
    {
        // ── DEBUG ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Master debug toggle. Logs every decision made.
        /// Very verbose — combine with DEBUG_PLAYER_ID filter.
        /// </summary>
        public static bool DEBUG = false;

        /// <summary>
        /// Filter debug output to a single player. -1 = all players.
        /// Set to a specific PlayerId (0–21) to trace one player.
        /// </summary>
        public static int DEBUG_PLAYER_ID = -1;

        // ── Private state — one per match, not per tick ───────────────────────

        private static Random _rng = new Random(0);

        // ── Initialisation ────────────────────────────────────────────────────

        /// <summary>
        /// Called once by MatchEngine before the tick loop begins.
        /// Staggers decision cooldowns so all 22 players don't decide on tick 0.
        /// Seeded from MatchContext.RandomSeed for reproducible matches.
        /// </summary>
        public static void InitialiseMatch(MatchContext ctx)
        {
            _rng = new Random(ctx.RandomSeed);

            for (int i = 0; i < 22; i++)
            {
                // Random stagger: [0, DECISION_STAGGER_MAX] ticks
                ctx.Players[i].DecisionCooldown =
                    _rng.Next(0, AIConstants.DECISION_STAGGER_MAX + 1);
            }

            if (DEBUG)
                Console.WriteLine(
                    $"[PlayerAI] InitialiseMatch: seed={ctx.RandomSeed}, " +
                    $"stagger applied to all 22 players.");
        }

        // ── Main Tick Entry Point ─────────────────────────────────────────────

        /// <summary>
        /// Master update. Called once per tick by TickSystem, after MovementSystem.
        /// Processes all 22 players in order. Index order is stable each tick.
        /// </summary>
        public static void Tick(MatchContext ctx)
        {
            for (int i = 0; i < 22; i++)
            {
                ref PlayerState player = ref ctx.Players[i];

                if (!player.IsActive) continue;

                ProcessPlayer(ref player, ctx);
            }
        }

        // ── Per-Player Processing ─────────────────────────────────────────────

        private static void ProcessPlayer(ref PlayerState player, MatchContext ctx)
        {
            // ── 0. Ball hold tracking ──────────────────────────────────────────
            if (player.HasBall)
                player.BallHoldTicks++;
            else
                player.BallHoldTicks = 0;

            // ── 1. Action lock check ───────────────────────────────────────────
            // After kicking, the player is locked briefly.
            // After celebrating, they are locked until celebration ends.
            if (IsActionLocked(ref player, ctx)) return;

            // ── 2. Cooldown decrement ──────────────────────────────────────────
            if (player.DecisionCooldown > 0)
            {
                player.DecisionCooldown--;
                // Still applying existing TargetPosition — MovementSystem handles it.
                // Adjust IsSprinting based on current Action even mid-cooldown.
                ApplySprintFlag(ref player, ctx);
                return;
            }

            // ── 3. Full decision re-evaluation ─────────────────────────────────
            ActionPlan plan = EvaluatePlayer(ref player, ctx);

            // ── 4. Apply plan to PlayerState ────────────────────────────────────
            ApplyPlan(ref player, plan, ctx);

            // ── 5. Reset cooldown ───────────────────────────────────────────────
            player.DecisionCooldown = AIConstants.DECISION_INTERVAL_TICKS;
        }

        // ── Evaluate: routes to correct DecisionSystem evaluator ──────────────

        private static ActionPlan EvaluatePlayer(ref PlayerState player, MatchContext ctx)
        {
            // ── GK special path ────────────────────────────────────────────────
            if (player.Role == PlayerRole.GK || player.Role == PlayerRole.SK)
                return DecisionSystem.EvaluateGK(ref player, ctx);

            // ── Ball state determines which evaluator to call ──────────────────
            bool teamHasBall = TeamHasBall(player.TeamId, ctx);
            bool ballIsLoose = ctx.Ball.Phase == BallPhase.Loose;

            if (player.HasBall)
                return DecisionSystem.EvaluateWithBall(ref player, ctx);

            if (ballIsLoose)
                return DecisionSystem.EvaluateLooseBall(ref player, ctx);

            if (teamHasBall)
                return DecisionSystem.EvaluateWithoutBall(ref player, ctx);

            // Opponent has ball — defensive decision
            return DecisionSystem.EvaluateOutOfPossession(ref player, ctx);
        }

        // ── Apply Plan ────────────────────────────────────────────────────────

        private static void ApplyPlan(ref PlayerState player, ActionPlan plan, MatchContext ctx)
        {
            player.Action = plan.Action;
            player.TargetPosition = plan.TargetPosition;

            ApplySprintFlag(ref player, ctx);

            // ── Execute ball actions: these are the ONLY calls to BallSystem launch ──
            switch (plan.Action)
            {
                case PlayerAction.Passing:
                    if (player.HasBall && plan.PassReceiverId >= 0 &&
                        plan.PassReceiverId <= 21)
                    {
                        // Enforce minimum ball-hold time before passing is allowed.
                        int minHold = GetMinHoldTicks(player.Role);
                        if (player.BallHoldTicks < minHold)
                        {
                            // Not held long enough — force Hold action instead.
                            player.Action = PlayerAction.Holding;
                            break;
                        }

                        BallSystem.LaunchPass(ctx, player.PlayerId,
                            plan.PassReceiverId, plan.PassSpeed, plan.PassHeight);
                        EventSystem.NotifyPassLaunched(player.PlayerId, player.TeamId);
                        player.DecisionCooldown = AIConstants.ACTION_LOCK_AFTER_KICK_TICKS;
                        player.Action = PlayerAction.Passing;

                        if (DEBUG && (DEBUG_PLAYER_ID < 0 ||
                                      DEBUG_PLAYER_ID == player.PlayerId))
                            Console.WriteLine(
                                $"[PlayerAI] Tick {ctx.Tick}: P{player.PlayerId} " +
                                $"PASSES to P{plan.PassReceiverId} " +
                                $"held={player.BallHoldTicks} minHold={minHold} " +
                                $"speed={plan.PassSpeed:F1} score={plan.Score:F3}");
                    }
                    break;
                case PlayerAction.Shooting:
                    if (player.HasBall)
                    {
                        // Choose shot type: power vs placed based on distance and ability
                        float distToGoal = player.Position.DistanceTo(plan.ShotTargetPos);
                        float shotSpeed = SelectShotSpeed(ref player, distToGoal);

                        BallSystem.LaunchShot(ctx, player.PlayerId,
                            plan.ShotTargetPos, shotSpeed);

                        // Set the xG value for collision resolution
                        ctx.Ball.ShotXG = plan.XG;
                        ctx.Ball.ShotContestResolved = false;

                        // Notify EventSystem so it can track xG for save/goal attribution
                        EventSystem.NotifyShotLaunched(plan.XG);

                        player.DecisionCooldown = AIConstants.ACTION_LOCK_AFTER_KICK_TICKS;
                        player.Action = PlayerAction.Shooting;

                        if (DEBUG && (DEBUG_PLAYER_ID < 0 ||
                                      DEBUG_PLAYER_ID == player.PlayerId))
                            Console.WriteLine(
                                $"[PlayerAI] Tick {ctx.Tick}: P{player.PlayerId} " +
                                $"SHOOTS at {plan.ShotTargetPos} " +
                                $"speed={shotSpeed:F1} xG={plan.XG:F3} " +
                                $"score={plan.Score:F3}");
                    }
                    break;

                case PlayerAction.Crossing:
                    if (player.HasBall)
                    {
                        Vec2 deliveryPoint = ComputeCrossDeliveryPoint(ref player, ctx);
                        BallSystem.LaunchCross(ctx, player.PlayerId,
                            deliveryPoint, PhysicsConstants.BALL_CROSS_SPEED);

                        player.DecisionCooldown = AIConstants.ACTION_LOCK_AFTER_KICK_TICKS;
                        player.Action = PlayerAction.Crossing;

                        if (DEBUG && (DEBUG_PLAYER_ID < 0 ||
                                      DEBUG_PLAYER_ID == player.PlayerId))
                            Console.WriteLine(
                                $"[PlayerAI] Tick {ctx.Tick}: P{player.PlayerId} " +
                                $"CROSSES to {deliveryPoint} " +
                                $"score={plan.Score:F3}");
                    }
                    break;

                case PlayerAction.Celebrating:
                    // Lock for full celebration duration
                    player.DecisionCooldown = AIConstants.ACTION_LOCK_CELEBRATE_TICKS;
                    player.IsSprinting = false;
                    break;
            }

            if (DEBUG && plan.Action != PlayerAction.Passing &&
                plan.Action != PlayerAction.Shooting &&
                plan.Action != PlayerAction.Crossing &&
                (DEBUG_PLAYER_ID < 0 || DEBUG_PLAYER_ID == player.PlayerId))
            {
                Console.WriteLine(
                    $"[PlayerAI] Tick {ctx.Tick}: P{player.PlayerId} " +
                    $"(Team{player.TeamId} {player.Role}) → {plan.Action} " +
                    $"target={plan.TargetPosition} score={plan.Score:F3}");
            }

            // Store plan for DebugLogger if debugging
            if (ctx.DebugMode)
                ctx.LastAppliedPlans[player.PlayerId] = plan;
        }

        // ── Sprint Flag ───────────────────────────────────────────────────────

        /// <summary>
        /// Sets IsSprinting explicitly from the player's current Action.
        /// Overrides MovementSystem's fallback derivation — this is the authoritative source.
        /// </summary>
        private static void ApplySprintFlag(ref PlayerState player, MatchContext ctx)
        {
            switch (player.Action)
            {
                // High-intensity sprint actions
                case PlayerAction.Pressing:
                case PlayerAction.Recovering:
                case PlayerAction.MakingRun:
                case PlayerAction.OverlapRun:
                case PlayerAction.Tracking:
                case PlayerAction.TackleAttempt:
                case PlayerAction.InterceptAttempt:
                    player.IsSprinting = player.Stamina > 0.05f; // tiny stamina reserve check
                    break;

                // Medium intensity — jog
                case PlayerAction.SupportingRun:
                case PlayerAction.PositioningSupport:
                case PlayerAction.Dribbling:
                case PlayerAction.Covering:
                case PlayerAction.DroppingDeep:
                    player.IsSprinting = false;
                    break;

                // Walking / standing
                default:
                    player.IsSprinting = false;
                    break;
            }
        }

        // ── Action Lock ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns true when a player should be skipped entirely this tick.
        /// Covers: Celebrating (locked for duration), TakingSetPiece (TickSystem handles).
        /// Does NOT cover cooldown — cooldown is managed separately.
        /// </summary>
        private static bool IsActionLocked(ref PlayerState player, MatchContext ctx)
        {
            // Celebrating: locked for celebration duration
            if (player.Action == PlayerAction.Celebrating &&
                player.DecisionCooldown > 0)
            {
                player.DecisionCooldown--;
                return true;
            }

            // Set piece taker: TickSystem drives set piece logic separately
            if (player.Action == PlayerAction.TakingSetPiece)
                return true;

            return false;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>True if the given team currently owns the ball.</summary>
        private static bool TeamHasBall(int teamId, MatchContext ctx)
        {
            if (ctx.Ball.Phase != BallPhase.Owned) return false;
            int ownerId = ctx.Ball.OwnerId;
            if (ownerId < 0 || ownerId > 21) return false;
            return ctx.Players[ownerId].TeamId == teamId;
        }

        /// <summary>
        /// Selects shot speed: power shot or placed shot based on distance and ability.
        /// Close range, high ability → prefer placed (more accurate).
        /// Long range or low ability → power (needs pace to beat GK).
        /// </summary>
        private static float SelectShotSpeed(ref PlayerState player, float distToGoal)
        {
            bool closeRange = distToGoal < AIConstants.SHOOT_OPTIMAL_RANGE;
            bool highAbility = player.ShootingAbility > 0.65f;

            if (closeRange && highAbility)
            {
                // Placed shot — GK has less time but needs accuracy
                return MathUtil.Lerp(PhysicsConstants.BALL_SHOT_SPEED_PLACED,
                                   PhysicsConstants.BALL_SHOT_SPEED_POWER,
                                   player.ShootingAbility);
            }

            // Power shot — need pace at range
            return PhysicsConstants.BALL_SHOT_SPEED_POWER;
        }

        /// <summary>
        /// Computes a realistic cross delivery point inside the penalty area.
        /// Targets the area between the penalty spot and the near post,
        /// adjusted for which side the cross is coming from.
        /// </summary>
        private static Vec2 ComputeCrossDeliveryPoint(ref PlayerState player, MatchContext ctx)
        {
            bool attacksDown = ctx.AttacksDownward(player.TeamId);
            bool isRightSide = player.Position.X > PhysicsConstants.PITCH_WIDTH * 0.5f;

            // Aim for the far post area when crossing from the right, near post from left
            float targetX = isRightSide
                ? PhysicsConstants.HOME_GOAL_LEFT_X + 30f  // far post side
                : PhysicsConstants.HOME_GOAL_RIGHT_X - 30f; // near post side

            float targetY = attacksDown
                ? PhysicsConstants.PITCH_HEIGHT - PhysicsConstants.PENALTY_AREA_DEPTH * 0.4f
                : PhysicsConstants.PENALTY_AREA_DEPTH * 0.4f;

            return new Vec2(targetX, targetY);
        }

        /// <summary>
        /// Returns the minimum number of ticks a player of this role must hold the ball
        /// before passing is allowed. Enforces realistic decision time per position.
        /// </summary>
        private static int GetMinHoldTicks(PlayerRole role)
        {
            switch (role)
            {
                case PlayerRole.GK:
                case PlayerRole.SK:
                    return AIConstants.HOLD_TICKS_GK;

                case PlayerRole.CB:
                case PlayerRole.BPD:
                    return AIConstants.HOLD_TICKS_DEFENDER;

                case PlayerRole.CDM:
                case PlayerRole.DLP:
                    return AIConstants.HOLD_TICKS_DM;

                case PlayerRole.WB:
                case PlayerRole.RB:
                case PlayerRole.LB:
                case PlayerRole.CM:
                case PlayerRole.BBM:
                    return AIConstants.HOLD_TICKS_MID;

                case PlayerRole.AM:
                case PlayerRole.IW:
                case PlayerRole.WF:
                case PlayerRole.CF:
                case PlayerRole.ST:
                case PlayerRole.PF:
                    return AIConstants.HOLD_TICKS_ATTACKER;

                default:
                    return AIConstants.HOLD_TICKS_MID;
            }
        }
    }
}