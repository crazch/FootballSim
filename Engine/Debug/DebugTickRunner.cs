// =============================================================================
// Module:  DebugTickRunner.cs (v3)
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
// Changes from v2:
//   • Accepts DebugCaptureMode in constructor
//   • Calls DebugLogger.Capture() after EventSystem.Tick() each tick
//   • Exposes TickLogs property for DebugLogQuery
//   • DebugLogger.Query(runner.TickLogs) is the primary post-run API
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim.Engine.Core;
using FootballSim.Engine.Models;
using FootballSim.Engine.Stats;
using FootballSim.Engine.Systems;

namespace FootballSim.Engine.Debug
{
    // =========================================================================
    // TICK LOG ENTRY — lightweight, for assertions only
    // =========================================================================

    public struct DebugTickEntry
    {
        public int Tick;
        public int BallOwnerId;
        public BallPhase BallPhase;
        public Vec2 BallPosition;
        public int HomeScore;
        public int AwayScore;
        public int LastEventType;
        public int LastEventPrimary;
        public int LastEventSecondary;
        public string LastEventDesc;
    }

    // =========================================================================
    // DEBUG TICK RUNNER
    // =========================================================================

    public sealed class DebugTickRunner
    {
        private readonly MatchContext _ctx;
        private readonly int _maxTicks;
        private readonly List<DebugTickEntry> _log;
        private readonly List<ReplayFrame> _frames;
        private readonly List<TickLog> _tickLogs;
        private readonly DebugCaptureMode _captureMode;

        /// <summary>
        /// When true, captures ReplayFrame each tick for BuildReplay().
        /// Default: true. Set false if only running assertions (no visualization).
        /// </summary>
        public bool CaptureFrames { get; set; } = true;

        public DebugAssertions Assert { get; }

        /// <summary>
        /// Structured per-tick logs captured by DebugLogger.
        /// Available after Run(). Use with DebugLogger.Query() for filtering.
        ///
        /// Example:
        ///   runner.Run();
        ///   DebugLogger.Query(runner.TickLogs)
        ///              .ForPlayer(0).HasBall().InOpponentBox()
        ///              .PrintTable();
        /// </summary>
        public IReadOnlyList<TickLog> TickLogs => _tickLogs;

        // ── Constructor ───────────────────────────────────────────────────────

        /// <param name="ctx">The debug MatchContext (from DebugMatchContext).</param>
        /// <param name="maxTicks">Maximum ticks to run.</param>
        /// <param name="captureMode">
        ///   None  = no DebugLogger capture (fastest, only assertions work).
        ///   Light = positions and actions only (no score breakdowns).
        ///   Full  = everything including score breakdowns (default, best for LLM).
        /// </param>
        public DebugTickRunner(
            MatchContext ctx,
            int maxTicks = 600,
            DebugCaptureMode captureMode = DebugCaptureMode.Full)
        {
            _ctx = ctx;
            _maxTicks = maxTicks;
            _captureMode = captureMode;
            _log = new List<DebugTickEntry>(maxTicks);
            _frames = new List<ReplayFrame>(maxTicks);
            _tickLogs = captureMode != DebugCaptureMode.None
                           ? new List<TickLog>(maxTicks)
                           : new List<TickLog>(0);
            Assert = new DebugAssertions(_log, ctx);
        }

        // ── Run ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs the tick loop. After this completes:
        ///   runner.TickLogs  → query with DebugLogger.Query()
        ///   runner.Assert.*  → validate with assertions
        ///   runner.BuildReplay() → get MatchReplay for visualization
        /// </summary>
        public void Run(bool stopOnGoal = false)
        {
            PlayerAI.InitialiseMatch(_ctx);

            // Clear LastAppliedPlans if DebugMode is on
            if (_ctx.DebugMode)
                Array.Clear(_ctx.LastAppliedPlans, 0, 22);

            for (int t = 0; t < _maxTicks; t++)
            {
                ApplyDebugOverrides(_ctx);

                // ── Tick pipeline ──────────────────────────────────────────────
                _ctx.EventsThisTick.Clear();

                MovementSystem.Tick(_ctx);
                PlayerAI.Tick(_ctx);        // writes ctx.LastAppliedPlans when DebugMode=true
                BallSystem.Tick(_ctx);
                CollisionSystem.Tick(_ctx);
                EventSystem.Tick(_ctx);

                // ── Capture ReplayFrame for visualization ──────────────────────
                if (CaptureFrames)
                    _frames.Add(ReplayFrame.Capture(_ctx));

                // ── Capture structured TickLog for query/LLM ──────────────────
                if (_captureMode != DebugCaptureMode.None)
                {
                    ActionPlan?[]? plans = _ctx.DebugMode ? _ctx.LastAppliedPlans : null;
                    _tickLogs.Add(DebugLogger.Capture(_ctx, plans, _captureMode));
                }

                // ── Capture lightweight assertion log ──────────────────────────
                CaptureLogEntry(_ctx, t);

                _ctx.Tick++;

                // ── Early exit ─────────────────────────────────────────────────
                if (stopOnGoal && (_ctx.HomeScore > 0 || _ctx.AwayScore > 0)) break;
                if (_ctx.Phase == MatchPhase.FullTime ||
                    _ctx.Phase == MatchPhase.HalfTime) break;
            }
        }

        /// <summary>Steps a single tick. Useful for _process-driven debug scenes.</summary>
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

            if (_captureMode != DebugCaptureMode.None)
            {
                ActionPlan?[]? plans = _ctx.DebugMode ? _ctx.LastAppliedPlans : null;
                _tickLogs.Add(DebugLogger.Capture(_ctx, plans, _captureMode));
            }

            CaptureLogEntry(_ctx, _ctx.Tick);
            _ctx.Tick++;

            return _log.Count > 0 ? _log[^1] : default;
        }

        // ── BuildReplay ───────────────────────────────────────────────────────

        /// <summary>Packages captured ReplayFrames into a MatchReplay for bridge/Godot.</summary>
        public MatchReplay? BuildReplay()
        {
            if (!CaptureFrames || _frames.Count == 0) return null;

            var stubPlayerStats = new PlayerMatchStats[22];
            for (int i = 0; i < 22; i++)
                stubPlayerStats[i] = new PlayerMatchStats { PlayerId = i };

            return new MatchReplay
            {
                Frames = new List<ReplayFrame>(_frames),
                HomeStats = new TeamMatchStats { TeamId = 0 },
                AwayStats = new TeamMatchStats { TeamId = 1 },
                PlayerStats = stubPlayerStats,
                RandomSeed = _ctx.RandomSeed,
                HomeTeamName = _ctx.HomeTeam?.Name ?? "Debug Home",
                AwayTeamName = _ctx.AwayTeam?.Name ?? "Debug Away",
                FinalHomeScore = _ctx.HomeScore,
                FinalAwayScore = _ctx.AwayScore,
                TotalTicks = _frames.Count,
            };
        }

        // ── Query shortcut ────────────────────────────────────────────────────

        /// <summary>
        /// Shortcut for DebugLogger.Query(runner.TickLogs).
        /// Equivalent — use whichever reads better at the call site.
        /// </summary>
        public DebugLogQuery Query() => DebugLogger.Query(_tickLogs);

        // ── Print helpers ─────────────────────────────────────────────────────

        public void PrintSummary()
        {
            Console.WriteLine("==============================================");
            Console.WriteLine($"[DebugTickRunner] {_log.Count} ticks | " +
                              $"Score: {_ctx.HomeScore}–{_ctx.AwayScore} | " +
                              $"Frames: {_frames.Count} | TickLogs: {_tickLogs.Count}");
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

        public IReadOnlyList<DebugTickEntry> Log => _log;
        public IReadOnlyList<ReplayFrame> Frames => _frames;

        // ── Private helpers ───────────────────────────────────────────────────

        private static void ApplyDebugOverrides(MatchContext ctx)
        {
            AIConstants.DISABLE_LONG_PASS_OVERRIDE = ctx.ForceLongPassDisabled;
        }

        private void CaptureLogEntry(MatchContext ctx, int tick)
        {
            var entry = new DebugTickEntry
            {
                Tick = tick,
                BallOwnerId = ctx.Ball.OwnerId,
                BallPhase = ctx.Ball.Phase,
                BallPosition = ctx.Ball.Position,
                HomeScore = ctx.HomeScore,
                AwayScore = ctx.AwayScore,
                LastEventType = -1,
                LastEventPrimary = -1,
                LastEventSecondary = -1,
                LastEventDesc = null,
            };

            if (ctx.EventsThisTick.Count > 0)
            {
                var ev = ctx.EventsThisTick[^1];
                entry.LastEventType = (int)ev.Type;
                entry.LastEventPrimary = ev.PrimaryPlayerId;
                entry.LastEventSecondary = ev.SecondaryPlayerId;
                entry.LastEventDesc = ev.Description;
            }

            _log.Add(entry);
        }
    }

    // =========================================================================
    // ASSERTIONS (unchanged from v2)
    // =========================================================================

    public sealed class DebugAssertions
    {
        private readonly IReadOnlyList<DebugTickEntry> _log;
        private readonly MatchContext _ctx;

        public DebugAssertions(IReadOnlyList<DebugTickEntry> log, MatchContext ctx)
        { _log = log; _ctx = ctx; }

        public void ShotAttempted(string? context = null)
        {
            if (!_log.Any(e =>
                e.LastEventType == (int)MatchEventType.ShotOnTarget ||
                e.LastEventType == (int)MatchEventType.ShotOffTarget ||
                e.LastEventType == (int)MatchEventType.Goal))
                Fail("ShotAttempted",
                    "No shot event. Check ScoreShoot xG threshold and ScoreHold inversion.",
                    context);
        }

        public void ShotWithinTicks(int maxTicks, string? context = null)
        {
            int start = _log.FirstOrDefault(e => e.BallOwnerId >= 0).Tick;
            if (!_log.Any(e =>
                e.Tick <= start + maxTicks &&
                (e.LastEventType == (int)MatchEventType.ShotOnTarget ||
                 e.LastEventType == (int)MatchEventType.Goal)))
                Fail("ShotWithinTicks",
                    $"No shot on target within {maxTicks} ticks (possession start={start}).",
                    context);
        }

        public void SaveAttempted(string? context = null)
        {
            if (!_log.Any(e => e.LastEventType == (int)MatchEventType.Save))
                Fail("SaveAttempted",
                    "No Save event. Check LaunchShot ShotOnTarget direction.",
                    context);
        }

        public void PassCompleted(string? context = null)
        {
            if (!_log.Any(e => e.LastEventType == (int)MatchEventType.PassCompleted))
                Fail("PassCompleted",
                    "No PassCompleted. Check ScoreBestPass and LaunchPass static aim.",
                    context);
        }

        public void PassesCompleted(int minCount, string? context = null)
        {
            int n = _log.Count(e => e.LastEventType == (int)MatchEventType.PassCompleted);
            if (n < minCount)
                Fail("PassesCompleted",
                    $"Only {n} passes (expected >= {minCount}). " +
                    "Check ScoreHold inversion and SUPPORT_CROWDING_PENALTY.", context);
        }

        public void LongPassRatioBelow(float maxRatio, string? context = null)
        {
            int total = _log.Count(e =>
                e.LastEventType == (int)MatchEventType.PassCompleted ||
                e.LastEventType == (int)MatchEventType.LongBallAttempt);
            int longs = _log.Count(e => e.LastEventType == (int)MatchEventType.LongBallAttempt);
            if (total == 0) return;
            float ratio = (float)longs / total;
            if (ratio > maxRatio)
                Fail("LongPassRatioBelow",
                    $"Long ratio={ratio:P0} > {maxRatio:P0}. Check PASS_PROGRESSIVE_BONUS.", context);
        }

        public void TackleAttempted(string? context = null)
        {
            if (!_log.Any(e =>
                e.LastEventType == (int)MatchEventType.TackleSuccess ||
                e.LastEventType == (int)MatchEventType.Foul))
                Fail("TackleAttempted",
                    "No tackle. Check ChasingBall sprint and TACKLE_RANGE.", context);
        }

        public void GoalScoredByTeam(int teamId, string? context = null)
        {
            bool ok = teamId == 0 ? _ctx.HomeScore > 0 : _ctx.AwayScore > 0;
            if (!ok)
                Fail("GoalScoredByTeam",
                    $"No goal by team {teamId}. Score: {_ctx.HomeScore}–{_ctx.AwayScore}.",
                    context);
        }

        public void NoGoalScored(string? context = null)
        {
            if (_ctx.HomeScore > 0 || _ctx.AwayScore > 0)
                Fail("NoGoalScored",
                    $"Unexpected goal: {_ctx.HomeScore}–{_ctx.AwayScore}.", context);
        }

        public void BallOwnedByPlayer(int playerId, string? context = null)
        {
            if (!_log.Any(e => e.BallOwnerId == playerId))
                Fail("BallOwnedByPlayer",
                    $"Player {playerId} never owned ball. " +
                    "Check BALL_ARRIVAL_THRESHOLD vs BALL_DRIBBLE_OFFSET.", context);
        }

        private static void Fail(string name, string msg, string? ctx)
            => throw new DebugAssertionException(
                $"[DebugAssert:{name}]" + (ctx != null ? $" ({ctx})" : "") + $"\n  {msg}");
    }

    public sealed class DebugAssertionException : Exception
    {
        public DebugAssertionException(string message) : base(message) { }
    }
}