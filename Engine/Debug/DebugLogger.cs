// =============================================================================
// Module:  DebugLogger.cs
// Path:    FootballSim/Engine/Debug/DebugLogger.cs
//
// Purpose:
//   Captures one TickLog per tick after all systems have settled.
//   Called by DebugTickRunner at the end of each tick (step 6 in tick order).
//   Never writes to MatchContext. Pure observer.
//
//   Two capture modes:
//     Light  — position, action, ball state, events only (Layer 1)
//     Full   — Light + score breakdowns + defensive context (Layer 2)
//
//   Score breakdowns require ActionPlan to carry score fields (see patch notes).
//   If ActionPlan does not yet have score fields, Full mode gracefully
//   captures what it can and leaves Scores = null.
//
//   Usage in DebugTickRunner (after EventSystem.Tick):
//     DebugLogger.Capture(ctx, _lastPlans, mode: DebugCaptureMode.Full);
//
//   After Run():
//     var query = DebugLogger.Query(logs);
//     query.ForPlayer(0).HasBall().InPenaltyBox().PrintTable();
//     query.ForPlayer(0).HasBall().InPenaltyBox().ToJson();
// =============================================================================

using System;
using System.Collections.Generic;
using FootballSim.Engine.Models;
using FootballSim.Engine.Systems;

namespace FootballSim.Engine.Debug
{
    // =========================================================================
    // CAPTURE MODE
    // =========================================================================

    public enum DebugCaptureMode
    {
        /// <summary>
        /// No capture. Fastest. Use for runs that only care about assertions.
        /// </summary>
        None,

        /// <summary>
        /// Position, action, ball state, events only.
        /// Cheap. Good for 11v11 overview or long runs.
        /// Scores will be null in all PlayerTickLog entries.
        /// </summary>
        Light,

        /// <summary>
        /// Light + score breakdowns + defensive context.
        /// More memory per tick. Best for 2v2/3v3 debugging.
        /// Requires ActionPlan to carry score fields (see ActionPlan patch).
        /// </summary>
        Full,
    }

    // =========================================================================
    // DEBUG LOGGER
    // =========================================================================

    public static partial class DebugLogger
    {
        // ── Capture ───────────────────────────────────────────────────────────

        /// <summary>
        /// Captures one TickLog from the current MatchContext state.
        /// Call AFTER EventSystem.Tick() — reads the fully settled state.
        ///
        /// lastPlans: the ActionPlan array that PlayerAI applied this tick.
        ///   PlayerAI should write its plans to ctx.LastAppliedPlans[22] each tick
        ///   OR pass them directly here. See patch notes for the cleanest approach.
        ///   Pass null if score breakdowns are not yet available.
        /// </summary>
        public static TickLog Capture(
            MatchContext ctx,
            ActionPlan?[]? lastPlans = null,
            DebugCaptureMode mode = DebugCaptureMode.Full)
        {
            var log = new TickLog
            {
                Tick = ctx.Tick,
                MatchSecond = ctx.Tick * 0.1f,
                MatchMinute = (int)(ctx.Tick * 0.1f / 60f),
                HomeScore = ctx.HomeScore,
                AwayScore = ctx.AwayScore,
                Phase = ctx.Phase,
            };

            // ── Ball ──────────────────────────────────────────────────────────
            log.Ball = CaptureBall(ctx);

            // ── Players ───────────────────────────────────────────────────────
            for (int i = 0; i < 22; i++)
            {
                ref PlayerState p = ref ctx.Players[i];
                if (!p.IsActive) continue;

                var plan = lastPlans != null ? lastPlans[i] : (ActionPlan?)null;
                log.Players.Add(CapturePlayer(ref p, plan, ctx, mode));
            }

            // ── Events ────────────────────────────────────────────────────────
            foreach (var ev in ctx.EventsThisTick)
            {
                log.Events.Add(new EventTickLog
                {
                    Type = ev.Type,
                    PrimaryPlayerId = ev.PrimaryPlayerId,
                    SecondaryPlayerId = ev.SecondaryPlayerId,
                    TeamId = ev.TeamId,
                    Position = ev.Position,
                    ExtraFloat = ev.ExtraFloat,
                    ExtraBool = ev.ExtraBool,
                    Description = ev.Description,
                });
            }

            return log;
        }

        // ── Private: capture player ───────────────────────────────────────────

        private static PlayerTickLog CapturePlayer(
            ref PlayerState p,
            ActionPlan? plan,
            MatchContext ctx,
            DebugCaptureMode mode)
        {
            // ── Geometry helpers ──────────────────────────────────────────────
            bool attacksDown = p.TeamId == 0
                ? (ctx.HomeTeam?.AttacksDownward ?? true)
                : (ctx.AwayTeam?.AttacksDownward ?? false);

            float goalY = attacksDown
                ? PhysicsConstants.AWAY_GOAL_LINE_Y
                : PhysicsConstants.HOME_GOAL_LINE_Y;

            Vec2 goalCentre = new Vec2(
                (PhysicsConstants.HOME_GOAL_LEFT_X + PhysicsConstants.HOME_GOAL_RIGHT_X) * 0.5f,
                goalY);

            float distToGoal = p.Position.DistanceTo(goalCentre);
            float distToBall = p.Position.DistanceTo(ctx.Ball.Position);
            float distToNearest = FindNearestOpponentDist(ref p, ctx);

            bool inOppBox = IsInOpponentPenaltyBox(ref p, attacksDown);
            bool inOwnBox = IsInOwnPenaltyBox(ref p, attacksDown);

            var entry = new PlayerTickLog
            {
                PlayerId = p.PlayerId,
                TeamId = p.TeamId,
                ShirtNumber = p.ShirtNumber,
                Role = p.Role,
                Position = p.Position,
                TargetPosition = p.TargetPosition,
                Velocity = p.Velocity,
                Speed = p.Speed,
                IsSprinting = p.IsSprinting,
                Stamina = p.Stamina,
                HasBall = p.HasBall,
                IsInOpponentPenaltyBox = inOppBox,
                IsInOwnPenaltyBox = inOwnBox,
                DistToOpponentGoal = distToGoal,
                DistToBall = distToBall,
                DistToNearestOpponent = distToNearest,
                ActionChosen = p.Action,
                PassReceiverId = plan?.PassReceiverId ?? -1,
                ShotTargetPos = plan?.ShotTargetPos ?? Vec2.Zero,
                DecisionCooldown = p.DecisionCooldown,
                WasReEvaluatedThisTick = p.DecisionCooldown == AIConstants.DECISION_INTERVAL_TICKS,
            };

            // ── Score breakdown (Full mode only, requires ActionPlan scores) ──
            if (mode == DebugCaptureMode.Full && plan.HasValue)
            {
                var ap = plan.Value;

                // Only populate scores if the plan carries them
                // (ActionPlan.ScoreShoot etc. exist after the patch)
                if (ap.HasScoreBreakdown)
                {
                    entry.Scores = new ScoreBreakdown
                    {
                        ScoreShoot = ap.ScoreShoot,
                        ScorePass = ap.ScorePass,
                        ScoreDribble = ap.ScoreDribble,
                        ScoreCross = ap.ScoreCross,
                        ScoreHold = ap.ScoreHold,
                        WinningScore = ap.Score,
                        Shoot = new ShootDetail
                        {
                            XG = ap.ShootXG,
                            EffectiveThreshold = ap.ShootEffectiveThreshold,
                            DistToGoal = distToGoal,
                            BlockersInLane = ap.ShootBlockersInLane,
                            ThresholdBlocked = ap.ShootXG < ap.ShootEffectiveThreshold,
                            RangeBlocked = distToGoal > AIConstants.SHOOT_MAX_RANGE,
                        },
                        Pass = new PassDetail
                        {
                            BestReceiverId = ap.PassReceiverId,
                            BestReceiverScore = ap.ScorePass,
                            IsProgressive = ap.PassIsProgressive,
                            PassDistance = ap.PassDistance,
                        },
                    };
                }

                // Defensive context for out-of-possession players
                if (ap.HasDefensiveContext)
                {
                    entry.Defensive = new DefensiveContext
                    {
                        ScorePress = ap.ScorePress,
                        ScoreTrack = ap.ScoreTrack,
                        ScoreRecover = ap.ScoreRecover,
                        ScoreMarkSpace = ap.ScoreMarkSpace,
                        TrackTargetPlayerId = ap.TrackTargetPlayerId,
                        DistToCarrier = ap.DistToCarrier,
                        PressTriggerDist = ap.PressTriggerDist,
                    };
                }
            }

            return entry;
        }

        private static BallTickLog CaptureBall(MatchContext ctx)
        {
            return new BallTickLog
            {
                Position = ctx.Ball.Position,
                Velocity = ctx.Ball.Velocity,
                Phase = ctx.Ball.Phase,
                Height = ctx.Ball.Height,
                OwnerId = ctx.Ball.OwnerId,
                PassTargetId = ctx.Ball.PassTargetId,
                IsShot = ctx.Ball.IsShot,
                ShotOnTarget = ctx.Ball.ShotOnTarget,
                Speed = ctx.Ball.Velocity.Length(),
                LastTouchedBy = ctx.Ball.LastTouchedBy,
                LooseTicks = ctx.Ball.LooseTicks,
            };
        }

        // ── Geometry helpers ──────────────────────────────────────────────────

        private static float FindNearestOpponentDist(ref PlayerState p, MatchContext ctx)
        {
            int oppStart = p.TeamId == 0 ? 11 : 0;
            int oppEnd = p.TeamId == 0 ? 22 : 11;
            float minSq = float.MaxValue;

            for (int i = oppStart; i < oppEnd; i++)
            {
                if (!ctx.Players[i].IsActive) continue;
                float dSq = p.Position.DistanceSquaredTo(ctx.Players[i].Position);
                if (dSq < minSq) minSq = dSq;
            }
            return minSq < float.MaxValue ? MathF.Sqrt(minSq) : 9999f;
        }

        private static bool IsInOpponentPenaltyBox(ref PlayerState p, bool attacksDown)
        {
            float centreX = PhysicsConstants.PITCH_WIDTH * 0.5f;
            float hw = PhysicsConstants.PENALTY_AREA_HALF_WIDTH;
            bool xIn = p.Position.X >= centreX - hw && p.Position.X <= centreX + hw;
            if (!xIn) return false;

            return attacksDown
                ? p.Position.Y >= PhysicsConstants.PITCH_HEIGHT - PhysicsConstants.PENALTY_AREA_DEPTH
                : p.Position.Y <= PhysicsConstants.PENALTY_AREA_DEPTH;
        }

        private static bool IsInOwnPenaltyBox(ref PlayerState p, bool attacksDown)
        {
            float centreX = PhysicsConstants.PITCH_WIDTH * 0.5f;
            float hw = PhysicsConstants.PENALTY_AREA_HALF_WIDTH;
            bool xIn = p.Position.X >= centreX - hw && p.Position.X <= centreX + hw;
            if (!xIn) return false;

            return attacksDown
                ? p.Position.Y <= PhysicsConstants.PENALTY_AREA_DEPTH
                : p.Position.Y >= PhysicsConstants.PITCH_HEIGHT - PhysicsConstants.PENALTY_AREA_DEPTH;
        }

        // ── Query factory ─────────────────────────────────────────────────────

        /// <summary>
        /// Creates a query builder over a list of TickLogs.
        /// Call after DebugTickRunner.Run().
        ///
        /// Example:
        ///   var q = DebugLogger.Query(runner.TickLogs);
        ///   q.ForPlayer(0).HasBall().InOpponentBox().PrintTable();
        ///   q.ForPlayer(0).HasBall().InOpponentBox().ToJson();
        /// </summary>
        public static DebugLogQuery Query(IReadOnlyList<TickLog> logs)
            => new DebugLogQuery(logs);
    }
}
