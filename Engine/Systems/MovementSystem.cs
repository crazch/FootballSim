// =============================================================================
// Module:  MovementSystem.cs
// Path:    FootballSim/Engine/Systems/MovementSystem.cs
// Purpose:
//   Moves all 22 active players toward their TargetPosition each tick.
//   Applies stamina decay/recovery, speed calculation from stamina curve,
//   tempo modifier from TacticsInput, and smooth acceleration/deceleration.
//   Does NOT decide where players should go — that is PlayerAI / DecisionSystem.
//   Does NOT move the ball — that is BallSystem.
//
//   Responsibilities this step (Step 3 — pure spatial physics):
//     1. Per player: calculate effective speed from BaseSpeed × StaminaCurve(Stamina)
//     2. Per player: apply tempo modifier from team TacticsInput.Tempo
//     3. Per player: smooth velocity using acceleration/deceleration limits
//     4. Per player: apply turn-rate limit so direction changes are gradual
//     5. Per player: advance Position by Velocity
//     6. Per player: clamp Position to pitch boundaries (players can't leave pitch)
//     7. Per player: decay or recover Stamina based on IsSprinting / movement speed
//     8. Per player: snap to TargetPosition when within arrival threshold
//     9. Per player: set IsSprinting flag based on current action
//
//   NOT in this step:
//     • Choosing TargetPosition (PlayerAI)
//     • Setting IsSprinting (currently derived here from Action for now — will
//       be overridden by PlayerAI in Step 4)
//     • Collision detection with other players (CollisionSystem)
//     • Formation anchor adjustments from DefensiveLine slider (MovementSystem
//       will read DefensiveLine in Step 4 when PlayerAI sets TargetPosition)
//
// API:
//   MovementSystem.Tick(MatchContext ctx) → void
//     Called once per tick by TickSystem, BEFORE BallSystem.Tick().
//     Updates all 22 Players[i].Position, .Velocity, .Speed, .Stamina.
//
//   MovementSystem.ComputeEffectiveSpeed(PlayerState player,
//                                         TacticsInput tactics) → float
//     Public helper used by tests and debug tools to read the speed
//     a given player would have at their current stamina + team tactics.
//
//   MovementSystem.ComputeStaminaCurve(float stamina) → float
//     Returns speed multiplier for the given stamina level.
//     1.0 at full stamina, STAMINA_SPEED_MIN_MULTIPLIER at zero stamina.
//     Non-linear (quadratic) to match real football fatigue behaviour.
//
// Dependencies:
//   Engine/Models/MatchContext.cs
//   Engine/Models/PlayerState.cs
//   Engine/Models/Enums.cs (PlayerAction, PlayerRole)
//   Engine/Systems/PhysicsConstants.cs
//   Engine/Tactics/TacticsInput.cs
//
// Notes:
//   • MovementSystem must run BEFORE BallSystem each tick.
//     BallSystem.TickOwned() mirrors ball to owner.Position — needs the
//     player at their updated position for this tick.
//   • All math is deterministic: no Random calls, no Time.deltaTime.
//     Same input state → same output state always.
//   • DEBUG flag: when true, logs any player whose stamina drops below
//     STAMINA_PRESS_COLLAPSE_THRESHOLD (the visible "press collapse" event).
//   • IsSprinting is currently derived from Action enum in this step.
//     PlayerAI (Step 4) will override IsSprinting directly. This derivation
//     acts as a safe fallback if AI hasn't assigned an action yet.
// =============================================================================

using System;
using FootballSim.Engine.Models;
using FootballSim.Engine.Tactics;

namespace FootballSim.Engine.Systems
{
    /// <summary>
    /// Pure movement system. Steers 22 players toward their TargetPosition each tick.
    /// Applies stamina, speed curves, tempo modifiers, acceleration limits.
    /// Makes no decisions — only moves.
    /// </summary>
    public static class MovementSystem
    {
        // ── DEBUG ─────────────────────────────────────────────────────────────

        /// <summary>
        /// When true:
        ///   • Logs when any player's stamina crosses STAMINA_PRESS_COLLAPSE_THRESHOLD.
        ///   • Logs players who are stuck (velocity near zero despite non-zero target delta).
        /// Set false for release / production simulation runs.
        /// </summary>
        public static bool DEBUG = false;

        // ── Main Tick Entry Point ─────────────────────────────────────────────

        /// <summary>
        /// Master update called once per tick by TickSystem.
        /// Must run BEFORE BallSystem.Tick() each tick.
        /// Iterates all 22 players, skips inactive (IsActive=false).
        /// </summary>
        public static void Tick(MatchContext ctx)
        {
            for (int i = 0; i < 22; i++)
            {
                if (!ctx.Players[i].IsActive) continue;

                // Pass team tactics matching this player's team
                TacticsInput tactics = ctx.Players[i].TeamId == 0
                    ? ctx.HomeTeam.Tactics
                    : ctx.AwayTeam.Tactics;

                UpdatePlayer(ctx, i, tactics);
            }
        }

        // ── Per-Player Update ─────────────────────────────────────────────────

        private static void UpdatePlayer(MatchContext ctx, int playerIndex, TacticsInput tactics)
        {
            ref PlayerState p = ref ctx.Players[playerIndex];

            // ── 1. Derive IsSprinting from current Action ──────────────────────
            // PlayerAI (Step 4) will override this. This is the fallback behaviour
            // so that movement is correct even before AI is wired in.
            p.IsSprinting = IsSprintingAction(p.Action);

            // ── 2. Compute effective speed for this tick ───────────────────────
            p.Speed = ComputeEffectiveSpeed(p, tactics);

            // ── 3. Compute desired direction toward TargetPosition ─────────────
            Vec2 toTarget = p.TargetPosition - p.Position;
            float distToTarget = toTarget.Length();

            // Snap: if close enough, just place at target and stop
            if (distToTarget < PhysicsConstants.PLAYER_ARRIVAL_THRESHOLD)
            {
                p.Position = p.TargetPosition;
                p.Velocity = Vec2.Zero;
                // Stamina still updates even when standing still
                UpdateStamina(ref p, ctx.Tick);
                return;
            }

            Vec2 desiredDir = toTarget.Normalized();

            // ── 4. Apply turn-rate limit ───────────────────────────────────────
            // Prevent instant 180° direction changes mid-stride.
            Vec2 currentDir = p.Velocity.LengthSquared() > 0.001f
                ? p.Velocity.Normalized()
                : desiredDir; // If standing still, face desired direction immediately

            Vec2 steerDir = SteerToward(currentDir, desiredDir,
                                         PhysicsConstants.PLAYER_TURN_RATE);

            // ── 5. Compute desired velocity magnitude for this tick ────────────
            float desiredSpeed = MathF.Min(p.Speed, distToTarget);

            // ── 6. Apply acceleration / deceleration limits ────────────────────
            float currentSpeed = p.Velocity.Length();
            float targetSpeed = desiredSpeed;
            float newSpeed;

            if (targetSpeed > currentSpeed)
            {
                // Accelerating
                newSpeed = MathF.Min(
                    currentSpeed + PhysicsConstants.PLAYER_ACCELERATION,
                    targetSpeed
                );
            }
            else
            {
                // Decelerating
                newSpeed = MathF.Max(
                    currentSpeed - PhysicsConstants.PLAYER_DECELERATION,
                    targetSpeed
                );
            }

            // ── 7. Apply final velocity ────────────────────────────────────────
            p.Velocity = steerDir * newSpeed;

            // ── 8. Advance position ────────────────────────────────────────────
            p.Position = p.Position + p.Velocity;

            // ── 9. Clamp to pitch boundaries ──────────────────────────────────
            // Players can't leave the pitch even if TargetPosition is off-pitch.
            p.Position = new Vec2(
                Math.Clamp(p.Position.X, PhysicsConstants.PITCH_LEFT, PhysicsConstants.PITCH_RIGHT),
                Math.Clamp(p.Position.Y, PhysicsConstants.PITCH_TOP, PhysicsConstants.PITCH_BOTTOM)
            );

            // ── 10. Update stamina ─────────────────────────────────────────────
            UpdateStamina(ref p, ctx.Tick);

            // ── 11. Decrement tackle cooldown ──────────────────────────────────
            if (p.TackleCooldownTicks > 0)
                p.TackleCooldownTicks--;
        }

        // ── Stamina Update ────────────────────────────────────────────────────

        private static void UpdateStamina(ref PlayerState p, int tick)
        {
            float prevStamina = p.Stamina;

            if (p.IsSprinting)
            {
                // Sprint drain, mitigated by StaminaAttribute
                // High StaminaAttribute = slower drain (multiply drain by inverse)
                float drainScale = 1.0f - p.StaminaAttribute * 0.5f;
                float drain = PhysicsConstants.STAMINA_SPRINT_DRAIN_PER_TICK * drainScale;
                p.Stamina = MathF.Max(0f, p.Stamina - drain);
            }
            else if (p.Velocity.LengthSquared() > 1f)
            {
                // Jogging — tiny drain
                p.Stamina = MathF.Max(0f,
                    p.Stamina - PhysicsConstants.STAMINA_JOG_DRAIN_PER_TICK);
            }
            else
            {
                // Walking or idle — recover stamina
                // StaminaAttribute scales recovery speed too
                float recoveryScale = 0.5f + p.StaminaAttribute * 0.5f;
                float recovery = PhysicsConstants.STAMINA_RECOVERY_WALK_PER_TICK
                                      * recoveryScale;
                p.Stamina = MathF.Min(1f, p.Stamina + recovery);
            }

            // DEBUG: Log press collapse threshold crossing (downward only)
            if (DEBUG)
            {
                float threshold = PhysicsConstants.STAMINA_PRESS_COLLAPSE_THRESHOLD;
                if (prevStamina >= threshold && p.Stamina < threshold)
                {
                    Console.WriteLine(
                        $"[MovementSystem] Tick {tick}: Player {p.PlayerId} " +
                        $"(Team {p.TeamId}, Role {p.Role}) stamina crossed press-collapse " +
                        $"threshold {threshold:F2}. Stamina now={p.Stamina:F3}. " +
                        $"Press willingness will now decay.");
                }
            }
        }

        // ── Public Helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Returns the effective speed this player would move at given their current
        /// stamina level and team tactics tempo. Public for tests and debug tools.
        /// Formula: BaseSpeed × StaminaCurve(Stamina) × TempoScale(tactics.Tempo)
        /// </summary>
        public static float ComputeEffectiveSpeed(PlayerState player, TacticsInput tactics)
        {
            float staminaMult = ComputeStaminaCurve(player.Stamina);
            float tempoScale = ComputeTempoScale(tactics.Tempo);

            // Sprint vs jog base speed
            float baseSpeed = player.IsSprinting
                ? player.BaseSpeed
                : player.BaseSpeed * 0.65f; // jogging = 65% of sprint base

            return baseSpeed * staminaMult * tempoScale;
        }

        /// <summary>
        /// Stamina → speed multiplier. Non-linear (quadratic) curve.
        /// At Stamina=1.0: returns 1.0 (full speed).
        /// At Stamina=0.0: returns STAMINA_SPEED_MIN_MULTIPLIER (e.g. 0.40).
        /// Quadratic means fatigue accelerates as stamina drops — realistic.
        ///
        /// Formula: min + (1 - min) × stamina²
        ///   At stamina=1.0: min + (1-min) × 1.0 = 1.0
        ///   At stamina=0.5: min + (1-min) × 0.25 = min + 0.25×(1-min)
        ///   At stamina=0.0: min + 0 = min
        /// </summary>
        public static float ComputeStaminaCurve(float stamina)
        {
            float min = PhysicsConstants.STAMINA_SPEED_MIN_MULTIPLIER;
            float t = MathF.Max(0f, MathF.Min(1f, stamina)); // clamp to [0,1]
            return min + (1f - min) * (t * t);
        }

        // ── Private Helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Maps TacticsInput.Tempo [0,1] to a speed scale multiplier.
        /// Tempo=0 → TEMPO_MIN_SPEED_SCALE (0.70).
        /// Tempo=1 → TEMPO_MAX_SPEED_SCALE (1.20).
        /// Linear interpolation between the two constants.
        /// </summary>
        private static float ComputeTempoScale(float tempo)
        {
            return PhysicsConstants.TEMPO_MIN_SPEED_SCALE
                   + tempo * (PhysicsConstants.TEMPO_MAX_SPEED_SCALE
                              - PhysicsConstants.TEMPO_MIN_SPEED_SCALE);
        }

        /// <summary>
        /// Smoothly steers currentDir toward desiredDir by at most maxTurnRate per tick.
        /// maxTurnRate is the maximum change in the normalised direction vector per tick.
        ///
        /// Implementation: linearly interpolate current→desired by a clamped t,
        /// then renormalise. Avoids trig functions for performance.
        ///
        /// When currentDir == Vec2.Zero (standing still), returns desiredDir immediately.
        /// </summary>
        private static Vec2 SteerToward(Vec2 currentDir, Vec2 desiredDir, float maxTurnRate)
        {
            if (currentDir == Vec2.Zero) return desiredDir;
            if (desiredDir == Vec2.Zero) return currentDir;

            // Dot product: 1.0 = same direction, -1.0 = opposite
            float dot = currentDir.Dot(desiredDir);

            // Already aligned (within floating point tolerance)
            if (dot >= 0.9999f) return desiredDir;

            // Clamp t so we don't overshoot.
            // t=1 would snap to desiredDir immediately.
            // We want to move by at most maxTurnRate per tick.
            // Approximate: use maxTurnRate as a direct blend weight.
            float t = MathF.Min(1f, maxTurnRate);

            Vec2 blended = new Vec2(
                currentDir.X + (desiredDir.X - currentDir.X) * t,
                currentDir.Y + (desiredDir.Y - currentDir.Y) * t
            );

            return blended.Normalized();
        }

        /// <summary>
        /// Derives whether a player should be considered sprinting based on their
        /// current action. PlayerAI (Step 4) will override IsSprinting directly.
        /// This is the fallback used in Step 3 before AI is wired in.
        ///
        /// Sprint actions: Pressing, Recovering, MakingRun, OverlapRun,
        ///                 Tracking (hard), TackleAttempt, InterceptAttempt.
        /// Jog actions: SupportingRun, PositioningSupport, Dribbling, Covering.
        /// Walk actions: WalkingToAnchor, Idle, Holding, HoldingWidth, MarkingSpace.
        /// </summary>
        private static bool IsSprintingAction(PlayerAction action)
        {
            switch (action)
            {
                case PlayerAction.Pressing:
                case PlayerAction.Recovering:
                case PlayerAction.MakingRun:
                case PlayerAction.OverlapRun:
                case PlayerAction.Tracking:
                case PlayerAction.TackleAttempt:
                case PlayerAction.InterceptAttempt:
                    return true;

                default:
                    return false;
            }
        }
    }
}