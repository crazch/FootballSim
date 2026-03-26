// =============================================================================
// Module:  BallSystem.cs
// Path:    FootballSim/Engine/Systems/BallSystem.cs
// Purpose:
//   The ONLY system permitted to write to MatchContext.Ball (BallState).
//   Every other system is read-only on ball data.
//
//   Responsibilities this step (Step 3 — pure spatial physics):
//     1. Tick ball position: apply velocity to position each tick
//     2. Apply friction/air-resistance to velocity based on phase
//     3. Handle aerial height decay
//     4. Detect and flag out-of-play boundary crossings
//     5. Sync ball position to owner when phase is Owned (dribble offset)
//     6. Transfer ownership when PassTargetId player arrives at IN_FLIGHT ball
//     7. Increment LooseTicks when phase is Loose
//     8. Update MatchContext possession counters (HomePossessionTicks / AwayPossessionTicks)
//     9. Sync PlayerState.HasBall flags to match BallState.OwnerId
//
//   Responsibilities NOT in this step (will be added later):
//     • Deciding to kick the ball (PlayerAI / DecisionSystem)
//     • Interception geometry (CollisionSystem)
//     • Event emission on goal / out-of-play (EventSystem)
//     • Set piece restarts (TickSystem / EventSystem)
//
// API:
//   BallSystem.Tick(MatchContext ctx)  → void
//     Called once per tick by TickSystem, AFTER MovementSystem has updated
//     player positions. Mutates ctx.Ball and ctx.Players[n].HasBall only.
//
//   BallSystem.LaunchPass(MatchContext ctx, int senderId, int receiverId,
//                          float speed, float height) → void
//     Called by DecisionSystem (Step 4) when a player decides to pass.
//     Sets ball phase to InFlight, velocity toward receiver, PassTargetId.
//
//   BallSystem.LaunchShot(MatchContext ctx, int shooterId,
//                          Vec2 targetPosition, float speed) → void
//     Called by DecisionSystem when a player decides to shoot.
//     Sets ball phase to InFlight, IsShot=true, ShotOnTarget based on angle.
//
//   BallSystem.LaunchCross(MatchContext ctx, int crosserId,
//                           Vec2 deliveryPoint, float speed) → void
//     Called by DecisionSystem for crossing actions. Height = 0.8 (aerial).
//
//   BallSystem.AssignOwnership(MatchContext ctx, int newOwnerId) → void
//     Called by CollisionSystem after a tackle/intercept is resolved.
//     Transfers ball ownership cleanly — single point of write authority.
//
//   BallSystem.SetLoose(MatchContext ctx, Vec2 looseVelocity) → void
//     Called by CollisionSystem to knock ball loose from current owner.
//
// Dependencies:
//   Engine/Models/MatchContext.cs
//   Engine/Models/BallState.cs
//   Engine/Models/PlayerState.cs
//   Engine/Models/Vec2.cs
//   Engine/Models/Enums.cs
//   Engine/Systems/PhysicsConstants.cs
//
// Notes:
//   • Tick() must be called AFTER MovementSystem.Tick() each frame so that
//     ball position in Owned phase mirrors the updated player position.
//   • BallSystem never reads RoleRegistry, FormationRegistry, JSON, or Godot.
//   • All public mutating methods are the only sanctioned write paths to BallState.
//     If you find ball state being written outside this file, that is a bug.
//   • DEBUG flag: when true logs every phase transition with tick, player IDs,
//     ball position, and velocity at the transition moment.
// =============================================================================

using System;
using FootballSim.Engine.Models;

namespace FootballSim.Engine.Systems
{
    /// <summary>
    /// Sole owner of BallState mutation. Called once per tick by TickSystem.
    /// No other system may write to MatchContext.Ball directly.
    /// </summary>
    public static class BallSystem
    {
        // ── DEBUG ─────────────────────────────────────────────────────────────

        /// <summary>
        /// When true, logs every ball phase transition, ownership change,
        /// and out-of-play event to console with tick and position info.
        /// Set false for release / production simulation runs.
        /// </summary>
        public static bool DEBUG = false;

        // ── Main Tick Entry Point ─────────────────────────────────────────────

        /// <summary>
        /// Master update called once per tick by TickSystem.
        /// Must be called AFTER MovementSystem.Tick() so player positions are current.
        /// Execution order within this method is documented and must not be changed.
        /// </summary>
        public static void Tick(MatchContext ctx)
        {
            // Step order matters — do NOT reorder without updating this comment.
            // 1. Sync ball-to-owner when Owned (position follows player)
            // 2. Apply velocity to position when InFlight or Loose
            // 3. Apply deceleration (friction / air resistance)
            // 4. Decay height for aerial balls
            // 5. Check arrival — did PassTargetId player reach the ball?
            // 6. Check boundary — did ball leave the pitch?
            // 7. Increment LooseTicks if Loose
            // 8. Update possession counters
            // 9. Sync HasBall flags on all PlayerState entries

            switch (ctx.Ball.Phase)
            {
                case BallPhase.Owned:
                    TickOwned(ctx);
                    break;

                case BallPhase.InFlight:
                    TickInFlight(ctx);
                    break;

                case BallPhase.Loose:
                    TickLoose(ctx);
                    break;
            }

            UpdatePossessionCounters(ctx);
            SyncHasBallFlags(ctx);
        }

        // ── Phase: Owned ──────────────────────────────────────────────────────

        private static void TickOwned(MatchContext ctx)
        {
            int ownerId = ctx.Ball.OwnerId;

            // Safety: if OwnerId is invalid while phase claims Owned, go Loose.
            if (ownerId < 0 || ownerId > 21 || !ctx.Players[ownerId].IsActive)
            {
                if (DEBUG)
                    Console.WriteLine(
                        $"[BallSystem] Tick {ctx.Tick}: Owned phase but OwnerId={ownerId} invalid. " +
                        $"Forcing to Loose at {ctx.Ball.Position}");

                ForceLoose(ctx, Vec2.Zero);
                return;
            }

            ref PlayerState owner = ref ctx.Players[ownerId];

            // Ball position: ahead of the player in their movement direction.
            // When holding (standing still), ball is at feet (smaller offset).
            Vec2 ballOffset;
            float ownerSpeed = owner.Velocity.Length();

            if (ownerSpeed < 0.1f || owner.Action == PlayerAction.Holding)
            {
                // Stationary: ball sits directly in front of player
                // Use a fixed tiny forward offset so ball is visible separately
                ballOffset = new Vec2(0f, PhysicsConstants.BALL_HOLD_OFFSET);
            }
            else
            {
                // Moving: ball leads in direction of travel
                Vec2 dir = owner.Velocity.Normalized();
                ballOffset = dir * PhysicsConstants.BALL_DRIBBLE_OFFSET;
            }

            ctx.Ball.Position = owner.Position + ballOffset;

            // Ball velocity mirrors owner velocity (for visual smoothness in replay)
            ctx.Ball.Velocity = owner.Velocity;

            // No friction, no height decay when Owned
            // Height stays at 0 when dribbling (ground ball)
            ctx.Ball.Height = 0f;
        }

        // ── Phase: InFlight ───────────────────────────────────────────────────

        private static void TickInFlight(MatchContext ctx)
        {
            ref BallState ball = ref ctx.Ball;

            // 1. Advance position
            ball.Position = ball.Position + ball.Velocity;

            // 2. Apply air resistance
            float airResistance = PhysicsConstants.BALL_AIR_RESISTANCE;

            // Lofted balls (Height > 0.5) decay faster
            if (ball.Height > 0.5f)
                airResistance *= PhysicsConstants.BALL_LOFT_EXTRA_DECELERATION;

            ball.Velocity = ball.Velocity * airResistance;

            // 3. Decay height toward ground
            if (ball.Height > 0f)
            {
                ball.Height -= PhysicsConstants.BALL_HEIGHT_DECAY_PER_TICK;
                if (ball.Height < 0f) ball.Height = 0f;
            }

            // 4. Check if PassTargetId player has arrived at ball position
            if (!ball.IsShot && ball.PassTargetId >= 0 && ball.PassTargetId <= 21)
            {
                ref PlayerState receiver = ref ctx.Players[ball.PassTargetId];

                if (receiver.IsActive)
                {
                    float distSq = receiver.Position.DistanceSquaredTo(ball.Position);
                    float threshold = PhysicsConstants.BALL_ARRIVAL_THRESHOLD;

                    if (distSq <= threshold * threshold)
                    {
                        if (DEBUG)
                            Console.WriteLine(
                                $"[BallSystem] Tick {ctx.Tick}: Pass arrived. " +
                                $"Receiver={ball.PassTargetId} at {receiver.Position}, " +
                                $"Ball at {ball.Position}, distSq={distSq:F1}");

                        TransferOwnership(ctx, ball.PassTargetId);
                        return; // Don't check boundary after a successful reception
                    }
                }
            }

            // 5. Check if ball has stopped (velocity near zero while InFlight → go Loose)
            if (ball.Velocity.LengthSquared() < PhysicsConstants.BALL_STOP_THRESHOLD *
                                                PhysicsConstants.BALL_STOP_THRESHOLD)
            {
                if (DEBUG)
                    Console.WriteLine(
                        $"[BallSystem] Tick {ctx.Tick}: InFlight ball stopped. " +
                        $"Going Loose at {ball.Position}");

                GoLoose(ctx);
                return;
            }

            // 6. Boundary check
            CheckBoundary(ctx);
        }

        // ── Phase: Loose ──────────────────────────────────────────────────────

        private static void TickLoose(MatchContext ctx)
        {
            ref BallState ball = ref ctx.Ball;

            // 1. Advance position
            ball.Position = ball.Position + ball.Velocity;

            // 2. Apply ground friction
            ball.Velocity = ball.Velocity * PhysicsConstants.BALL_GROUND_FRICTION;

            // 3. Stop ball when velocity is negligible
            if (ball.Velocity.LengthSquared() < PhysicsConstants.BALL_STOP_THRESHOLD *
                                                PhysicsConstants.BALL_STOP_THRESHOLD)
            {
                ball.Velocity = Vec2.Zero;
                // Ball remains Loose at rest — CollisionSystem / PlayerAI must claim it
            }

            // 4. Decay height if ball was aerial and is now Loose
            if (ball.Height > 0f)
            {
                ball.Height -= PhysicsConstants.BALL_HEIGHT_DECAY_PER_TICK;
                if (ball.Height < 0f) ball.Height = 0f;
            }

            // 5. Increment loose ticks counter
            ball.LooseTicks++;

            // 6. Boundary check
            CheckBoundary(ctx);
        }

        // ── Boundary Detection ────────────────────────────────────────────────

        private static void CheckBoundary(MatchContext ctx)
        {
            ref BallState ball = ref ctx.Ball;

            if (ball.IsOutOfPlay) return; // Already flagged this tick

            bool outLeft = ball.Position.X < PhysicsConstants.PITCH_LEFT;
            bool outRight = ball.Position.X > PhysicsConstants.PITCH_RIGHT;
            bool outTop = ball.Position.Y < PhysicsConstants.PITCH_TOP;
            bool outBottom = ball.Position.Y > PhysicsConstants.PITCH_BOTTOM;

            if (!outLeft && !outRight && !outTop && !outBottom) return;

            // Clamp ball position to pitch boundary to prevent it from drifting further.
            // EventSystem will handle the restart in its own Tick.
            ball.Position = new Vec2(
                Math.Clamp(ball.Position.X, PhysicsConstants.PITCH_LEFT, PhysicsConstants.PITCH_RIGHT),
                Math.Clamp(ball.Position.Y, PhysicsConstants.PITCH_TOP, PhysicsConstants.PITCH_BOTTOM)
            );
            ball.Velocity = Vec2.Zero;
            ball.IsOutOfPlay = true;

            // Attribute out-of-play to the last player who touched the ball
            ball.OutOfPlayCausedBy = ball.LastTouchedBy >= 0 && ball.LastTouchedBy <= 10
                ? 0   // home player last touched → home caused it
                : 1;  // away player last touched → away caused it

            if (DEBUG)
                Console.WriteLine(
                    $"[BallSystem] Tick {ctx.Tick}: Ball out of play at {ball.Position}. " +
                    $"LastTouchedBy=Player {ball.LastTouchedBy}, CausedBy team {ball.OutOfPlayCausedBy}");
        }

        // ── Possession Counters ───────────────────────────────────────────────

        private static void UpdatePossessionCounters(MatchContext ctx)
        {
            if (ctx.Ball.Phase != BallPhase.Owned) return;

            int ownerId = ctx.Ball.OwnerId;
            if (ownerId < 0 || ownerId > 21) return;

            if (ownerId <= 10)
                ctx.HomePossessionTicks++;
            else
                ctx.AwayPossessionTicks++;
        }

        // ── HasBall Flag Sync ─────────────────────────────────────────────────

        private static void SyncHasBallFlags(MatchContext ctx)
        {
            // Clear all HasBall flags first
            for (int i = 0; i < 22; i++)
                ctx.Players[i].HasBall = false;

            // Set only the current owner's flag
            if (ctx.Ball.Phase == BallPhase.Owned &&
                ctx.Ball.OwnerId >= 0 && ctx.Ball.OwnerId <= 21)
            {
                ctx.Players[ctx.Ball.OwnerId].HasBall = true;
            }
        }

        // ── Internal Phase Transitions ────────────────────────────────────────

        /// <summary>
        /// Internal: transfer ball ownership to a player. Phase → Owned.
        /// Used when pass receiver arrives or on claiming a Loose ball.
        /// </summary>
        private static void TransferOwnership(MatchContext ctx, int newOwnerId)
        {
            ref BallState ball = ref ctx.Ball;

            int previousOwner = ball.OwnerId;

            ball.Phase = BallPhase.Owned;
            ball.OwnerId = newOwnerId;
            ball.LastTouchedBy = newOwnerId;
            ball.Velocity = Vec2.Zero;
            ball.PassTargetId = -1;
            ball.IsShot = false;
            ball.ShotOnTarget = false;
            ball.LooseTicks = 0;
            ball.Height = 0f;

            if (DEBUG)
                Console.WriteLine(
                    $"[BallSystem] Tick {ctx.Tick}: Ownership transferred. " +
                    $"Previous={previousOwner} → New=Player {newOwnerId} " +
                    $"at {ctx.Players[newOwnerId].Position}");
        }

        /// <summary>
        /// Internal: transition ball to Loose phase with given velocity.
        /// OwnerId becomes -1.
        /// </summary>
        private static void GoLoose(MatchContext ctx)
        {
            ref BallState ball = ref ctx.Ball;

            if (DEBUG)
                Console.WriteLine(
                    $"[BallSystem] Tick {ctx.Tick}: Ball goes LOOSE at {ball.Position} " +
                    $"vel={ball.Velocity} (was Phase={ball.Phase}, Owner={ball.OwnerId})");

            ball.Phase = BallPhase.Loose;
            ball.OwnerId = -1;
            ball.PassTargetId = -1;
            ball.IsShot = false;
            ball.ShotOnTarget = false;
            ball.LooseTicks = 0;
            // Velocity kept — ball rolls with whatever speed it had
        }

        // ── Public Mutation API (called by DecisionSystem / CollisionSystem) ──

        /// <summary>
        /// Called by DecisionSystem when a player decides to pass.
        /// Calculates velocity vector toward receiver and launches ball.
        /// </summary>
        /// <param name="senderId">PlayerId of the passer.</param>
        /// <param name="receiverId">PlayerId of the intended receiver.</param>
        /// <param name="speed">Initial ball speed in units/tick. Use PhysicsConstants.BALL_PASS_SPEED_*</param>
        /// <param name="height">0=ground pass, 0.5=driven, 1.0=lofted.</param>
        public static void LaunchPass(MatchContext ctx, int senderId, int receiverId,
                                      float speed, float height)
        {
            ref BallState ball = ref ctx.Ball;
            ref PlayerState sender = ref ctx.Players[senderId];
            ref PlayerState receiver = ref ctx.Players[receiverId];

            Vec2 direction = (receiver.Position - sender.Position).Normalized();

            ball.Phase = BallPhase.InFlight;
            ball.OwnerId = -1;
            ball.PassTargetId = receiverId;
            ball.Velocity = direction * speed;
            ball.Position = sender.Position; // starts at sender's feet
            ball.Height = height;
            ball.IsShot = false;
            ball.ShotOnTarget = false;
            ball.LastTouchedBy = senderId;
            ball.LooseTicks = 0;

            if (DEBUG)
                Console.WriteLine(
                    $"[BallSystem] Tick {ctx.Tick}: PASS launched. " +
                    $"Sender=Player {senderId} at {sender.Position}, " +
                    $"Receiver=Player {receiverId} at {receiver.Position}, " +
                    $"Speed={speed:F1} Height={height:F2} Dir={direction}");
        }

        /// <summary>
        /// Called by DecisionSystem when a player decides to shoot.
        /// Calculates ShotOnTarget based on whether trajectory crosses goal mouth.
        /// </summary>
        /// <param name="shooterId">PlayerId of the shooter.</param>
        /// <param name="targetPosition">The aimed point in world space (goal area).</param>
        /// <param name="speed">Initial ball speed. Use PhysicsConstants.BALL_SHOT_SPEED_*</param>
        public static void LaunchShot(MatchContext ctx, int shooterId,
                                      Vec2 targetPosition, float speed)
        {
            ref BallState ball = ref ctx.Ball;
            ref PlayerState shooter = ref ctx.Players[shooterId];

            Vec2 direction = (targetPosition - shooter.Position).Normalized();

            // Determine which goal line to check (shooter attacks toward high Y or low Y)
            bool attackingDown = ctx.AttacksDownward(shooter.TeamId);
            float goalY = attackingDown ? PhysicsConstants.AWAY_GOAL_LINE_Y
                                             : PhysicsConstants.HOME_GOAL_LINE_Y;
            float goalLeftX = PhysicsConstants.AWAY_GOAL_LEFT_X;  // same X for both goals
            float goalRightX = PhysicsConstants.AWAY_GOAL_RIGHT_X;

            // Project where ball trajectory crosses the goal line
            // Parametric: position = shooter.Position + direction * t
            // At goal line: shooter.Position.Y + direction.Y * t = goalY
            bool shotOnTarget = false;
            if (Math.Abs(direction.Y) > 0.001f)
            {
                float t = (goalY - shooter.Position.Y) / direction.Y;
                if (t > 0f) // Must be forward direction
                {
                    float xAtGoalLine = shooter.Position.X + direction.X * t;
                    shotOnTarget = xAtGoalLine >= goalLeftX && xAtGoalLine <= goalRightX;
                }
            }

            ball.Phase = BallPhase.InFlight;
            ball.OwnerId = -1;
            ball.PassTargetId = -1;
            ball.Velocity = direction * speed;
            ball.Position = shooter.Position;
            ball.Height = 0.2f; // slightly off ground (driven shot)
            ball.IsShot = true;
            ball.ShotOnTarget = shotOnTarget;
            ball.LastTouchedBy = shooterId;
            ball.LooseTicks = 0;

            if (DEBUG)
                Console.WriteLine(
                    $"[BallSystem] Tick {ctx.Tick}: SHOT launched. " +
                    $"Shooter=Player {shooterId} at {shooter.Position}, " +
                    $"Target={targetPosition}, Speed={speed:F1}, " +
                    $"OnTarget={shotOnTarget}");
        }

        /// <summary>
        /// Called by DecisionSystem for crossing actions from wide areas.
        /// </summary>
        /// <param name="crosserId">PlayerId of the crosser.</param>
        /// <param name="deliveryPoint">Target delivery point in the penalty area.</param>
        /// <param name="speed">Cross speed. Use PhysicsConstants.BALL_CROSS_SPEED.</param>
        public static void LaunchCross(MatchContext ctx, int crosserId,
                                       Vec2 deliveryPoint, float speed)
        {
            ref BallState ball = ref ctx.Ball;
            ref PlayerState crosser = ref ctx.Players[crosserId];

            Vec2 direction = (deliveryPoint - crosser.Position).Normalized();

            ball.Phase = BallPhase.InFlight;
            ball.OwnerId = -1;
            ball.PassTargetId = -1;  // No single target — anyone can attack the cross
            ball.Velocity = direction * speed;
            ball.Position = crosser.Position;
            ball.Height = 0.8f; // aerial delivery
            ball.IsShot = false;
            ball.ShotOnTarget = false;
            ball.LastTouchedBy = crosserId;
            ball.LooseTicks = 0;

            if (DEBUG)
                Console.WriteLine(
                    $"[BallSystem] Tick {ctx.Tick}: CROSS launched. " +
                    $"Crosser=Player {crosserId} at {crosser.Position}, " +
                    $"Delivery={deliveryPoint}, Speed={speed:F1}");
        }

        /// <summary>
        /// Called by CollisionSystem after a tackle or interception resolves ownership.
        /// The single sanctioned write path for CollisionSystem → ball ownership.
        /// </summary>
        /// <param name="newOwnerId">PlayerId who won the ball.</param>
        public static void AssignOwnership(MatchContext ctx, int newOwnerId)
        {
            if (DEBUG)
                Console.WriteLine(
                    $"[BallSystem] Tick {ctx.Tick}: AssignOwnership called. " +
                    $"Winner=Player {newOwnerId}");

            TransferOwnership(ctx, newOwnerId);
        }

        /// <summary>
        /// Called by CollisionSystem when a tackle knocks the ball loose.
        /// Ball leaves current owner with given loose velocity.
        /// </summary>
        /// <param name="looseVelocity">Direction and speed ball deflects when knocked loose.</param>
        public static void SetLoose(MatchContext ctx, Vec2 looseVelocity)
        {
            ref BallState ball = ref ctx.Ball;

            int previousOwner = ball.OwnerId;
            ball.Velocity = looseVelocity;

            GoLoose(ctx);

            if (DEBUG)
                Console.WriteLine(
                    $"[BallSystem] Tick {ctx.Tick}: Ball knocked LOOSE from Player {previousOwner}. " +
                    $"LooseVel={looseVelocity}");
        }

        /// <summary>
        /// Called by TickSystem / EventSystem to reset ball to a set-piece position.
        /// Used for kickoffs, free kicks, corners, throw-ins.
        /// Ball is placed at the given position, owned by the given player, phase Owned.
        /// </summary>
        public static void PlaceForSetPiece(MatchContext ctx, Vec2 position, int newOwnerId)
        {
            ref BallState ball = ref ctx.Ball;

            ball.Position = position;
            ball.Velocity = Vec2.Zero;
            ball.OwnerId = newOwnerId;
            ball.Phase = BallPhase.Owned;
            ball.PassTargetId = -1;
            ball.IsShot = false;
            ball.ShotOnTarget = false;
            ball.Height = 0f;
            ball.LooseTicks = 0;
            ball.IsOutOfPlay = false;
            ball.OutOfPlayCausedBy = -1;

            if (newOwnerId >= 0 && newOwnerId <= 21)
                ball.LastTouchedBy = newOwnerId;

            if (DEBUG)
                Console.WriteLine(
                    $"[BallSystem] Tick {ctx.Tick}: Set piece placement. " +
                    $"Position={position}, Owner=Player {newOwnerId}");
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Force ball to Loose phase with given velocity.
        /// Used internally when a corrupt Owned state is detected.
        /// </summary>
        private static void ForceLoose(MatchContext ctx, Vec2 velocity)
        {
            ctx.Ball.Velocity = velocity;
            GoLoose(ctx);
        }
    }
}