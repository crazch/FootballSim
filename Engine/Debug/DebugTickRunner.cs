// =============================================================================
// Module:  DebugTickRunner.cs
// Path:    FootballSim/Engine/Debug/DebugTickRunner.cs
//
// Purpose:
//   Runs the tick pipeline for a debug MatchContext.
//   Identical tick order to MatchEngine but:
//     • No ReplayFrame capture (saves memory in tight test loops)
//     • No StatAggregator (stats are irrelevant for system debugging)
//     • ctx.ForceLongPassDisabled respected per-tick
//     • Collects a lightweight DebugLog per tick for assertion in ScenarioRunner
//
//   PRODUCTION CODE IS UNTOUCHED.
//   MatchEngine.Simulate() is not called here. This runner calls each system
//   directly in the correct order — it is deliberately a thin wrapper so the
//   same system code is exercised exactly as in production.
//
// Tick order (mirrors MatchEngine exactly):
//   1. ctx.EventsThisTick.Clear()
//   2. MovementSystem.Tick(ctx)
//   3. PlayerAI.Tick(ctx)          ← respects ctx.ForceLongPassDisabled
//   4. BallSystem.Tick(ctx)
//   5. CollisionSystem.Tick(ctx)
//   6. EventSystem.Tick(ctx)
//   7. DebugLog.Capture(ctx)       ← lightweight, debug only
//
// Usage:
//   var ctx    = DebugMatchContext.TwoVTwo();
//   var runner = new DebugTickRunner(ctx, maxTicks: 300);
//   runner.Run();
//   runner.Assert.ShotAttempted();
//   runner.Assert.TackleAttempted();
//   runner.PrintSummary();
//
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim.Engine.Models;
using FootballSim.Engine.Systems;

namespace FootballSim.Engine.Debug
{
    // =========================================================================
    // TICK LOG — one entry per tick, lightweight
    // =========================================================================

    /// <summary>
    /// Snapshot of one tick's important state for post-run assertion.
    /// Only captures fields useful for debugging — much smaller than ReplayFrame.
    /// </summary>
    public struct DebugTickEntry
    {
        public int   Tick;
        public int   BallOwnerId;         // -1 = no owner
        public BallPhase BallPhase;
        public Vec2  BallPosition;
        public int   HomeScore;
        public int   AwayScore;
        public int   LastEventType;       // cast of MatchEventType, -1 = no event
        public int   LastEventPrimary;    // PrimaryPlayerId of last event
        public int   LastEventSecondary;
        public string LastEventDesc;      // Description string, null if no event
    }

    // =========================================================================
    // DEBUG TICK RUNNER
    // =========================================================================

    /// <summary>
    /// Runs debug scenarios tick-by-tick. Collects DebugTickEntry log.
    /// Provides assertion helpers for test validation after Run() completes.
    /// </summary>
    public sealed class DebugTickRunner
    {
        private readonly MatchContext            _ctx;
        private readonly int                     _maxTicks;
        private readonly List<DebugTickEntry>    _log;

        /// <summary>Access assertions after Run() completes.</summary>
        public DebugAssertions Assert { get; }

        public DebugTickRunner(MatchContext ctx, int maxTicks = 600)
        {
            _ctx      = ctx;
            _maxTicks = maxTicks;
            _log      = new List<DebugTickEntry>(maxTicks);
            Assert    = new DebugAssertions(_log, ctx);
        }

        /// <summary>
        /// Runs the tick loop for up to maxTicks ticks.
        /// Stops early if Phase reaches FullTime or GoalScored (configurable).
        /// </summary>
        public void Run(bool stopOnGoal = false)
        {
            // Initialise AI cooldown stagger — same as MatchEngine does
            PlayerAI.InitialiseMatch(_ctx);

            for (int t = 0; t < _maxTicks; t++)
            {
                // ── Apply per-scenario overrides before each tick ──────────────
                ApplyDebugOverrides(_ctx);

                // ── Tick pipeline (matches MatchEngine order exactly) ──────────
                _ctx.EventsThisTick.Clear();

                MovementSystem.Tick(_ctx);
                PlayerAI.Tick(_ctx);
                BallSystem.Tick(_ctx);
                CollisionSystem.Tick(_ctx);
                EventSystem.Tick(_ctx);

                // ── Capture lightweight log entry ──────────────────────────────
                CaptureLogEntry(_ctx, t);

                // ── Advance tick counter ───────────────────────────────────────
                _ctx.Tick++;

                // ── Early exit conditions ──────────────────────────────────────
                if (stopOnGoal &&
                    (_ctx.Phase == MatchPhase.GoalScored ||
                     _ctx.HomeScore > 0 || _ctx.AwayScore > 0))
                    break;

                if (_ctx.Phase == MatchPhase.FullTime ||
                    _ctx.Phase == MatchPhase.HalfTime)
                    break;
            }
        }

        /// <summary>
        /// Runs a single tick and returns the log entry. Useful for step-through
        /// debugging: call once per frame in a Godot debug scene.
        /// </summary>
        public DebugTickEntry Step()
        {
            ApplyDebugOverrides(_ctx);

            _ctx.EventsThisTick.Clear();
            MovementSystem.Tick(_ctx);
            PlayerAI.Tick(_ctx);
            BallSystem.Tick(_ctx);
            CollisionSystem.Tick(_ctx);
            EventSystem.Tick(_ctx);

            CaptureLogEntry(_ctx, _ctx.Tick);
            _ctx.Tick++;

            return _log.Count > 0 ? _log[^1] : default;
        }

        /// <summary>
        /// Prints a human-readable summary of key events from the run.
        /// Call after Run() to see what happened without a debugger.
        /// </summary>
        public void PrintSummary()
        {
            Console.WriteLine("==============================================");
            Console.WriteLine($"[DebugTickRunner] Summary — {_log.Count} ticks simulated");
            Console.WriteLine($"  Final score: Home {_ctx.HomeScore} – Away {_ctx.AwayScore}");
            Console.WriteLine($"  Final phase: {_ctx.Phase}");
            Console.WriteLine($"  Ball owner at end: P{_ctx.Ball.OwnerId} | Phase: {_ctx.Ball.Phase}");
            Console.WriteLine("----------------------------------------------");
            Console.WriteLine("  Events:");

            int eventCount = 0;
            foreach (var entry in _log.Where(e => e.LastEventDesc != null))
            {
                Console.WriteLine($"    Tick {entry.Tick:D5}: {entry.LastEventDesc}");
                eventCount++;
                if (eventCount >= 50)
                {
                    Console.WriteLine("    ... (truncated — more than 50 events)");
                    break;
                }
            }

            if (eventCount == 0)
                Console.WriteLine("    (no events emitted)");

            Console.WriteLine("==============================================");
        }

        /// <summary>Exposes raw log for custom inspection after Run().</summary>
        public IReadOnlyList<DebugTickEntry> Log => _log;

        // ── Private helpers ───────────────────────────────────────────────────

        private static void ApplyDebugOverrides(MatchContext ctx)
        {
            // Respect ctx.ForceLongPassDisabled even if AIConstants says otherwise.
            // DecisionSystem reads AIConstants.DISABLE_LONG_PASS directly — we
            // temporarily patch it here. This is debug-only; not thread-safe.
            if (ctx.ForceLongPassDisabled)
                AIConstants.DISABLE_LONG_PASS_OVERRIDE = true;
            else
                AIConstants.DISABLE_LONG_PASS_OVERRIDE = false;
        }

        private void CaptureLogEntry(MatchContext ctx, int tick)
        {
            var entry = new DebugTickEntry
            {
                Tick          = tick,
                BallOwnerId   = ctx.Ball.OwnerId,
                BallPhase     = ctx.Ball.Phase,
                BallPosition  = ctx.Ball.Position,
                HomeScore     = ctx.HomeScore,
                AwayScore     = ctx.AwayScore,
                LastEventType = -1,
                LastEventPrimary   = -1,
                LastEventSecondary = -1,
                LastEventDesc      = null,
            };

            if (ctx.EventsThisTick.Count > 0)
            {
                var ev = ctx.EventsThisTick[^1]; // last event this tick = highest priority
                entry.LastEventType      = (int)ev.Type;
                entry.LastEventPrimary   = ev.PrimaryPlayerId;
                entry.LastEventSecondary = ev.SecondaryPlayerId;
                entry.LastEventDesc      = ev.Description;
            }

            _log.Add(entry);
        }
    }

    // =========================================================================
    // DEBUG ASSERTIONS — post-run validation helpers
    // =========================================================================

    /// <summary>
    /// Assertion helpers that scan the DebugTickEntry log after a run.
    /// All methods throw DebugAssertionException on failure with a descriptive message.
    /// Call after DebugTickRunner.Run() completes.
    /// </summary>
    public sealed class DebugAssertions
    {
        private readonly IReadOnlyList<DebugTickEntry> _log;
        private readonly MatchContext                  _ctx;

        public DebugAssertions(IReadOnlyList<DebugTickEntry> log, MatchContext ctx)
        {
            _log = log;
            _ctx = ctx;
        }

        // ── Shot assertions ────────────────────────────────────────────────────

        /// <summary>
        /// Asserts that at least one ShotOnTarget or ShotOffTarget event was emitted.
        /// Fails if the attacker never decided to shoot in the given tick window.
        /// </summary>
        public void ShotAttempted(string? context = null)
        {
            bool found = _log.Any(e =>
                e.LastEventType == (int)MatchEventType.ShotOnTarget ||
                e.LastEventType == (int)MatchEventType.ShotOffTarget ||
                e.LastEventType == (int)MatchEventType.Goal);

            if (!found)
                Fail("ShotAttempted",
                    "No shot event (ShotOnTarget / ShotOffTarget / Goal) was emitted " +
                    $"in {_log.Count} ticks. " +
                    "Check ScoreShoot — attacker may be holding due to ScoreHold inversion " +
                    "or the xG threshold may be too high for this distance.",
                    context);
        }

        /// <summary>
        /// Asserts that the attacker shot within the first N ticks of possessing the ball.
        /// </summary>
        public void ShotWithinTicks(int maxTicks, string? context = null)
        {
            // Find the tick when the ball was first owned
            int possessionStartTick = _log
                .FirstOrDefault(e => e.BallOwnerId >= 0).Tick;

            bool found = _log.Any(e =>
                e.Tick <= possessionStartTick + maxTicks &&
                (e.LastEventType == (int)MatchEventType.ShotOnTarget ||
                 e.LastEventType == (int)MatchEventType.Goal));

            if (!found)
                Fail("ShotWithinTicks",
                    $"No shot on target within {maxTicks} ticks of possession start " +
                    $"(possession started at tick {possessionStartTick}). " +
                    "The attacker is either holding the ball (ScoreHold bug) or " +
                    "dribbling away from goal (attacksDown direction bug).",
                    context);
        }

        /// <summary>
        /// Asserts that a GKSave or GKCatch event was emitted.
        /// </summary>
        public void SaveAttempted(string? context = null)
        {
            bool found = _log.Any(e =>
                e.LastEventType == (int)MatchEventType.Save);

            if (!found)
                Fail("SaveAttempted",
                    "No Save event was emitted. Either no shot was on target " +
                    "(check ShotOnTarget in BallSystem.LaunchShot — attacksDown direction) " +
                    "or CollisionSystem.ResolveGoalCheck never fired " +
                    "(check GK_SAVE_TRIGGER_DIST vs ball speed).",
                    context);
        }

        // ── Passing assertions ─────────────────────────────────────────────────

        /// <summary>
        /// Asserts that at least one PassCompleted event was emitted.
        /// </summary>
        public void PassCompleted(string? context = null)
        {
            bool found = _log.Any(e =>
                e.LastEventType == (int)MatchEventType.PassCompleted);

            if (!found)
                Fail("PassCompleted",
                    "No PassCompleted event was emitted. " +
                    "Either the carrier always shoots/dribbles (check ScoreBestPass scoring), " +
                    "or LaunchPass aims at static position and the pass goes Loose before arrival " +
                    "(BallSystem Bug 1 — no lead-pass prediction).",
                    context);
        }

        /// <summary>
        /// Asserts that at least N passes completed.
        /// Use n >= 3 for the 3v3 passing triangle validation.
        /// </summary>
        public void PassesCompleted(int minCount, string? context = null)
        {
            int count = _log.Count(e =>
                e.LastEventType == (int)MatchEventType.PassCompleted);

            if (count < minCount)
                Fail("PassesCompleted",
                    $"Only {count} passes completed (expected >= {minCount}). " +
                    "The ball may not be circulating. " +
                    "Check ScoreHold (inverted — holds when no pressure), " +
                    "SUPPORT_CROWDING_PENALTY (collapses support run scores in compact shape), " +
                    "and LaunchPass lead-pass prediction.",
                    context);
        }

        /// <summary>
        /// Asserts that fewer than X% of passes were long (>= LONG_BALL_DISTANCE).
        /// Use for 3v3 short-pass scenario: expect < 20% long balls.
        /// </summary>
        public void LongPassRatioBelow(float maxRatio, string? context = null)
        {
            int totalPasses = _log.Count(e =>
                e.LastEventType == (int)MatchEventType.PassCompleted ||
                e.LastEventType == (int)MatchEventType.LongBallAttempt);
            int longPasses = _log.Count(e =>
                e.LastEventType == (int)MatchEventType.LongBallAttempt);

            if (totalPasses == 0)
                return; // no passes — other assertion will catch that

            float ratio = (float)longPasses / totalPasses;
            if (ratio > maxRatio)
                Fail("LongPassRatioBelow",
                    $"Long pass ratio = {ratio:P0} (expected < {maxRatio:P0}). " +
                    $"Long passes: {longPasses} / Total: {totalPasses}. " +
                    "Check PASS_PROGRESSIVE_BONUS — it may be overscoring forward long balls " +
                    "(AIConstants Bug 13). Also verify ForceLongPassDisabled = true on ctx.",
                    context);
        }

        // ── Tackle / dribble assertions ────────────────────────────────────────

        /// <summary>
        /// Asserts that at least one TackleSuccess or TaclkeFoul event was emitted.
        /// </summary>
        public void TackleAttempted(string? context = null)
        {
            bool found = _log.Any(e =>
                e.LastEventType == (int)MatchEventType.TackleSuccess ||
                e.LastEventType == (int)MatchEventType.Foul);

            if (!found)
                Fail("TackleAttempted",
                    "No tackle event (TackleSuccess / Foul) was emitted. " +
                    "Either the CB never set Action=TackleAttempt (check TACKLE_RANGE vs CB-attacker distance), " +
                    "or CollisionSystem.ResolveTackleContests returned early " +
                    "(check the TACKLE_RANGE = 25f constant vs actual positions in this scenario).",
                    context);
        }

        // ── Score assertions ───────────────────────────────────────────────────

        /// <summary>
        /// Asserts that a goal was scored by the correct team.
        /// </summary>
        public void GoalScoredByTeam(int expectedTeamId, string? context = null)
        {
            bool found = _log.Any(e =>
                e.LastEventType == (int)MatchEventType.Goal &&
                ((expectedTeamId == 0 && e.HomeScore > 0) ||
                 (expectedTeamId == 1 && e.AwayScore > 0)));

            if (!found)
                Fail("GoalScoredByTeam",
                    $"No goal was scored by team {expectedTeamId}. " +
                    $"Final score: Home {_ctx.HomeScore} – Away {_ctx.AwayScore}. " +
                    "If a goal event fired but with wrong teamId, check EmitGoal index-band bug " +
                    "(EventSystem uses playerId <= 10 ? 0 : 1 instead of PlayerState.TeamId).",
                    context);
        }

        /// <summary>
        /// Asserts that no goal was scored for either team.
        /// Useful for asserting the GK save worked.
        /// </summary>
        public void NoGoalScored(string? context = null)
        {
            if (_ctx.HomeScore > 0 || _ctx.AwayScore > 0)
                Fail("NoGoalScored",
                    $"A goal was scored (Home {_ctx.HomeScore} – Away {_ctx.AwayScore}) " +
                    "when none was expected. " +
                    "If GKSave is expected but a goal fired, check saveP calculation in " +
                    "CollisionSystem.ResolveGoalCheck and ShotOnTarget in BallSystem.LaunchShot.",
                    context);
        }

        // ── State assertions ───────────────────────────────────────────────────

        /// <summary>
        /// Asserts that the ball was owned by the given player at some point during the run.
        /// </summary>
        public void BallOwnedByPlayer(int playerId, string? context = null)
        {
            bool found = _log.Any(e => e.BallOwnerId == playerId);
            if (!found)
                Fail("BallOwnedByPlayer",
                    $"Player {playerId} never owned the ball in {_log.Count} ticks. " +
                    "Check that PassTargetId is set correctly and BallSystem " +
                    "arrival detection uses BALL_ARRIVAL_THRESHOLD >= BALL_DRIBBLE_OFFSET.",
                    context);
        }

        // ── Private helpers ────────────────────────────────────────────────────

        private static void Fail(string assertName, string message, string? context)
        {
            string full = $"[DebugAssert:{assertName}]" +
                           (context != null ? $" ({context})" : "") +
                           $"\n  {message}";
            throw new DebugAssertionException(full);
        }
    }

    /// <summary>
    /// Thrown by DebugAssertions when a post-run assertion fails.
    /// Caught by ScenarioRunner to format the failure report.
    /// </summary>
    public sealed class DebugAssertionException : Exception
    {
        public DebugAssertionException(string message) : base(message) { }
    }
}
