// =============================================================================
// Module:  DebugTickRunner.cs
// Path:    FootballSim/Engine/Debug/DebugTickRunner.cs
//
// Purpose:
//   Runs the tick pipeline for a debug MatchContext.
//   Identical tick order to MatchEngine but without StatAggregator.
//
//   KEY ADDITION vs previous version:
//   BuildReplay() captures ReplayFrame objects during Run() and packages
//   them as a MatchReplay. MatchEngineBridge.LoadExternalReplay() then
//   accepts this replay — ReplayPlayer.gd reads it with zero changes.
//
// Tick order (mirrors MatchEngine exactly):
//   1. ctx.EventsThisTick.Clear()
//   2. MovementSystem.Tick(ctx)
//   3. PlayerAI.Tick(ctx)
//   4. BallSystem.Tick(ctx)
//   5. CollisionSystem.Tick(ctx)
//   6. EventSystem.Tick(ctx)
//   7. ReplayFrame.Capture(ctx)   ← added for visualization path
//   8. DebugTickEntry.Capture()   ← lightweight log for assertions
//   9. ctx.Tick++
//
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using FootballSim.Engine.Models;
using FootballSim.Engine.Systems;
using FootballSim.Engine.Core;
using FootballSim.Engine.Stats;

namespace FootballSim.Engine.Debug
{
    // =========================================================================
    // TICK LOG ENTRY — one per tick, lightweight, for assertions
    // =========================================================================

    public struct DebugTickEntry
    {
        public int       Tick;
        public int       BallOwnerId;
        public BallPhase BallPhase;
        public Vec2      BallPosition;
        public int       HomeScore;
        public int       AwayScore;
        public int       LastEventType;      // cast of MatchEventType, -1 = none
        public int       LastEventPrimary;
        public int       LastEventSecondary;
        public string    LastEventDesc;
    }

    // =========================================================================
    // DEBUG TICK RUNNER
    // =========================================================================

    public sealed class DebugTickRunner
    {
        private readonly MatchContext            _ctx;
        private readonly int                     _maxTicks;
        private readonly List<DebugTickEntry>    _log;

        // Frames captured for BuildReplay() — only allocated when CaptureFrames=true
        private readonly List<ReplayFrame>       _frames;

        /// <summary>
        /// When true, captures a ReplayFrame each tick so BuildReplay() works.
        /// Costs ~400 bytes × maxTicks. For 600 ticks: ~240KB — negligible.
        /// Default: true. Set false if you only need assertions (console-only runs).
        /// </summary>
        public bool CaptureFrames { get; set; } = true;

        public DebugAssertions Assert { get; }

        public DebugTickRunner(MatchContext ctx, int maxTicks = 600)
        {
            _ctx      = ctx;
            _maxTicks = maxTicks;
            _log      = new List<DebugTickEntry>(maxTicks);
            _frames   = new List<ReplayFrame>(maxTicks);
            Assert    = new DebugAssertions(_log, ctx);
        }

        // ── Run ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs the tick loop up to maxTicks.
        /// Stops early if stopOnGoal=true and a goal is scored.
        /// After Run(), call BuildReplay() to get a MatchReplay for visualization.
        /// </summary>
        public void Run(bool stopOnGoal = false)
        {
            PlayerAI.InitialiseMatch(_ctx);

            for (int t = 0; t < _maxTicks; t++)
            {
                ApplyDebugOverrides(_ctx);

                // ── Tick pipeline — identical to MatchEngine ──────────────────
                _ctx.EventsThisTick.Clear();

                MovementSystem.Tick(_ctx);
                PlayerAI.Tick(_ctx);
                BallSystem.Tick(_ctx);
                CollisionSystem.Tick(_ctx);
                EventSystem.Tick(_ctx);

                // ── Capture ReplayFrame for visualization ─────────────────────
                if (CaptureFrames)
                    _frames.Add(ReplayFrame.Capture(_ctx));

                // ── Capture lightweight log entry for assertions ───────────────
                CaptureLogEntry(_ctx, t);

                _ctx.Tick++;

                // ── Early exit ────────────────────────────────────────────────
                if (stopOnGoal &&
                    (_ctx.HomeScore > 0 || _ctx.AwayScore > 0))
                    break;

                if (_ctx.Phase == MatchPhase.FullTime ||
                    _ctx.Phase == MatchPhase.HalfTime)
                    break;
            }
        }

        /// <summary>
        /// Steps a single tick. Returns the log entry for that tick.
        /// Useful for Godot step-through debug scene (_process driven).
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

            if (CaptureFrames)
                _frames.Add(ReplayFrame.Capture(_ctx));

            CaptureLogEntry(_ctx, _ctx.Tick);
            _ctx.Tick++;

            return _log.Count > 0 ? _log[^1] : default;
        }

        // ── BuildReplay ───────────────────────────────────────────────────────

        /// <summary>
        /// Packages the captured ReplayFrames into a MatchReplay that
        /// MatchEngineBridge.LoadExternalReplay() can accept.
        ///
        /// Call AFTER Run(). Returns null if CaptureFrames was false.
        ///
        /// The returned MatchReplay is structurally identical to one produced by
        /// MatchEngine.Simulate() — ReplayPlayer.gd reads it with zero changes.
        ///
        /// Stats (HomeStats, AwayStats, PlayerStats) are stubbed with zeroes
        /// because StatAggregator is not wired in debug scenarios. The visualization
        /// (dots, ball, events) works fully. PostMatch stats screen would show zeroes
        /// but DebugMatchView.tscn does not show a PostMatch screen.
        /// </summary>
        public MatchReplay? BuildReplay()
        {
            if (!CaptureFrames || _frames.Count == 0)
                return null;

            // Stub stats — debug scenarios do not run StatAggregator
            var stubPlayerStats = new PlayerMatchStats[22];
            for (int i = 0; i < 22; i++)
                stubPlayerStats[i] = new PlayerMatchStats { PlayerId = i };

            var replay = new MatchReplay
            {
                Frames         = new List<ReplayFrame>(_frames),
                HomeStats      = new TeamMatchStats { TeamId = 0 },
                AwayStats      = new TeamMatchStats { TeamId = 1 },
                PlayerStats    = stubPlayerStats,
                RandomSeed     = _ctx.RandomSeed,
                HomeTeamName   = _ctx.HomeTeam?.Name ?? "Debug Home",
                AwayTeamName   = _ctx.AwayTeam?.Name  ?? "Debug Away",
                FinalHomeScore = _ctx.HomeScore,
                FinalAwayScore = _ctx.AwayScore,
                TotalTicks     = _frames.Count,
            };

            return replay;
        }

        // ── Diagnostics ───────────────────────────────────────────────────────

        public void PrintSummary()
        {
            Console.WriteLine("==============================================");
            Console.WriteLine($"[DebugTickRunner] {_log.Count} ticks | " +
                              $"Score: {_ctx.HomeScore}–{_ctx.AwayScore} | " +
                              $"Frames captured: {_frames.Count}");
            Console.WriteLine("  Events:");

            int count = 0;
            foreach (var e in _log.Where(e => e.LastEventDesc != null))
            {
                Console.WriteLine($"    Tick {e.Tick:D4}: {e.LastEventDesc}");
                if (++count >= 50) { Console.WriteLine("    ..."); break; }
            }
            if (count == 0) Console.WriteLine("    (none)");
            Console.WriteLine("==============================================");
        }

        public IReadOnlyList<DebugTickEntry> Log    => _log;
        public IReadOnlyList<ReplayFrame>    Frames => _frames;

        // ── Private helpers ───────────────────────────────────────────────────

        private static void ApplyDebugOverrides(MatchContext ctx)
        {
            AIConstants.DISABLE_LONG_PASS_OVERRIDE = ctx.ForceLongPassDisabled;
        }

        private void CaptureLogEntry(MatchContext ctx, int tick)
        {
            var entry = new DebugTickEntry
            {
                Tick               = tick,
                BallOwnerId        = ctx.Ball.OwnerId,
                BallPhase          = ctx.Ball.Phase,
                BallPosition       = ctx.Ball.Position,
                HomeScore          = ctx.HomeScore,
                AwayScore          = ctx.AwayScore,
                LastEventType      = -1,
                LastEventPrimary   = -1,
                LastEventSecondary = -1,
                LastEventDesc      = null,
            };

            if (ctx.EventsThisTick.Count > 0)
            {
                var ev = ctx.EventsThisTick[^1];
                entry.LastEventType      = (int)ev.Type;
                entry.LastEventPrimary   = ev.PrimaryPlayerId;
                entry.LastEventSecondary = ev.SecondaryPlayerId;
                entry.LastEventDesc      = ev.Description;
            }

            _log.Add(entry);
        }
    }

    // =========================================================================
    // DEBUG ASSERTIONS
    // =========================================================================

    public sealed class DebugAssertions
    {
        private readonly IReadOnlyList<DebugTickEntry> _log;
        private readonly MatchContext                  _ctx;

        public DebugAssertions(IReadOnlyList<DebugTickEntry> log, MatchContext ctx)
        {
            _log = log;
            _ctx = ctx;
        }

        public void ShotAttempted(string? context = null)
        {
            if (!_log.Any(e =>
                e.LastEventType == (int)MatchEventType.ShotOnTarget  ||
                e.LastEventType == (int)MatchEventType.ShotOffTarget ||
                e.LastEventType == (int)MatchEventType.Goal))
                Fail("ShotAttempted",
                    "No shot event in run. Check ScoreShoot xG threshold and " +
                    "ScoreHold inversion (DecisionSystem Bug 1).", context);
        }

        public void ShotWithinTicks(int maxTicks, string? context = null)
        {
            int start = _log.FirstOrDefault(e => e.BallOwnerId >= 0).Tick;
            if (!_log.Any(e =>
                e.Tick <= start + maxTicks &&
                (e.LastEventType == (int)MatchEventType.ShotOnTarget ||
                 e.LastEventType == (int)MatchEventType.Goal)))
                Fail("ShotWithinTicks",
                    $"No shot on target within {maxTicks} ticks of possession (start={start}). " +
                    "Check ScoreHold inversion and attacksDown direction.", context);
        }

        public void SaveAttempted(string? context = null)
        {
            if (!_log.Any(e => e.LastEventType == (int)MatchEventType.Save))
                Fail("SaveAttempted",
                    "No Save event. ShotOnTarget may be false — check LaunchShot " +
                    "attacksDown direction (BallSystem Bug 3+4).", context);
        }

        public void PassCompleted(string? context = null)
        {
            if (!_log.Any(e => e.LastEventType == (int)MatchEventType.PassCompleted))
                Fail("PassCompleted",
                    "No PassCompleted event. Check ScoreBestPass scoring and " +
                    "LaunchPass static aim (BallSystem Bug 1).", context);
        }

        public void PassesCompleted(int minCount, string? context = null)
        {
            int count = _log.Count(e =>
                e.LastEventType == (int)MatchEventType.PassCompleted);
            if (count < minCount)
                Fail("PassesCompleted",
                    $"Only {count} passes completed (expected >= {minCount}). " +
                    "Check ScoreHold inversion and SUPPORT_CROWDING_PENALTY.", context);
        }

        public void LongPassRatioBelow(float maxRatio, string? context = null)
        {
            int total = _log.Count(e =>
                e.LastEventType == (int)MatchEventType.PassCompleted ||
                e.LastEventType == (int)MatchEventType.LongBallAttempt);
            int longs = _log.Count(e =>
                e.LastEventType == (int)MatchEventType.LongBallAttempt);
            if (total == 0) return;
            float ratio = (float)longs / total;
            if (ratio > maxRatio)
                Fail("LongPassRatioBelow",
                    $"Long ratio={ratio:P0} > {maxRatio:P0} ({longs}/{total}). " +
                    "Check ForceLongPassDisabled and PASS_PROGRESSIVE_BONUS.", context);
        }

        public void TackleAttempted(string? context = null)
        {
            if (!_log.Any(e =>
                e.LastEventType == (int)MatchEventType.TackleSuccess ||
                e.LastEventType == (int)MatchEventType.Foul))
                Fail("TackleAttempted",
                    "No tackle event. Check ChasingBall sprint (PlayerAI Bug 5) " +
                    "and TACKLE_RANGE vs initial gap.", context);
        }

        public void GoalScoredByTeam(int teamId, string? context = null)
        {
            bool found = teamId == 0 ? _ctx.HomeScore > 0 : _ctx.AwayScore > 0;
            if (!found)
                Fail("GoalScoredByTeam",
                    $"No goal by team {teamId}. Score: {_ctx.HomeScore}–{_ctx.AwayScore}. " +
                    "Check EmitGoal index-band (EventSystem Bug 3).", context);
        }

        public void NoGoalScored(string? context = null)
        {
            if (_ctx.HomeScore > 0 || _ctx.AwayScore > 0)
                Fail("NoGoalScored",
                    $"Unexpected goal: {_ctx.HomeScore}–{_ctx.AwayScore}. " +
                    "Check GK save probability.", context);
        }

        public void BallOwnedByPlayer(int playerId, string? context = null)
        {
            if (!_log.Any(e => e.BallOwnerId == playerId))
                Fail("BallOwnedByPlayer",
                    $"Player {playerId} never owned ball. " +
                    "Check BALL_ARRIVAL_THRESHOLD vs BALL_DRIBBLE_OFFSET " +
                    "(PhysicsConstants Bug 5).", context);
        }

        private static void Fail(string name, string msg, string? ctx)
        {
            string full = $"[DebugAssert:{name}]" +
                          (ctx != null ? $" ({ctx})" : "") +
                          $"\n  {msg}";
            throw new DebugAssertionException(full);
        }
    }

    public sealed class DebugAssertionException : Exception
    {
        public DebugAssertionException(string message) : base(message) { }
    }
}