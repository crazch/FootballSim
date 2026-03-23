// =============================================================================
// Module:  ScenarioRunner.cs
// Path:    FootballSim/Engine/Debug/ScenarioRunner.cs
//
// Purpose:
//   Runs all debug scenarios in order and reports pass/fail.
//   The single entry point for debug-mode validation.
//
//   Called from:
//     • Console: dotnet run --project FootballSim -- debug
//     • Godot bridge: MatchEngineBridge.RunDebugScenarios() (optional)
//     • Tests/: ScenarioRunnerTests.cs (NUnit/xUnit)
//
//   Each scenario validates a specific system layer:
//     Scenario 1: 1v1   — shot pipeline, GK save, goal attribution
//     Scenario 2: 2v2   — dribble decision, tackle, movement steering
//     Scenario 3: 3v3 S — short-pass triangles, support runs, hold scoring
//     Scenario 4: 3v3 L — long-pass prediction, progressive play
//
//   The scenarios are ordered so that each layer builds on the previous.
//   If Scenario 1 fails, stop — Scenario 2 depends on the shot pipeline working.
//
//   Output format:
//     [PASS] Scenario 1 — 1v1 Shot and GK Save (tick 47)
//     [PASS] Scenario 2 — 2v2 Dribble and Tackle (tick 83)
//     [FAIL] Scenario 3 — 3v3 Short-Pass Triangle
//            DebugAssert:PassesCompleted
//            Only 1 pass completed (expected >= 3). The ball may not be circulating.
//            Check ScoreHold (inverted — holds when no pressure), ...
//
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using FootballSim.Engine.Systems;

namespace FootballSim.Engine.Debug
{
    // =========================================================================
    // SCENARIO RESULT — one per scenario
    // =========================================================================

    public sealed class ScenarioResult
    {
        public string  Name       { get; init; } = "";
        public bool    Passed     { get; init; }
        public int     TicksRun   { get; init; }
        public string? FailReason { get; init; }
        public long    ElapsedMs  { get; init; }
    }

    // =========================================================================
    // SCENARIO RUNNER
    // =========================================================================

    public static class ScenarioRunner
    {
        // ── Entry Point ───────────────────────────────────────────────────────

        /// <summary>
        /// Runs all debug scenarios in order. Returns a list of results.
        /// Prints a formatted report to Console.
        /// Returns true if all scenarios passed.
        /// </summary>
        public static bool RunAll(bool stopOnFirstFailure = false)
        {
            Console.WriteLine();
            Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║          FOOTBALL SIM — DEBUG SCENARIO RUNNER            ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            var results = new List<ScenarioResult>();

            // ── Scenario 1: 1v1 ──────────────────────────────────────────────
            var s1 = Run("1v1 — Shot Pipeline and GK Save", seed: 42, () =>
            {
                var ctx    = DebugMatchContext.OneVOne(seed: 42);
                var runner = new DebugTickRunner(ctx, maxTicks: 200);
                runner.Run(stopOnGoal: false);

                // Assertions: ordered from most fundamental to most specific
                runner.Assert.ShotWithinTicks(
                    maxTicks: 30,
                    context: "Attacker at penalty spot should shoot quickly. " +
                             "If this fails: check ScoreShoot xG threshold vs " +
                             "distance (515 units from Y=0 goal = ~51.5m but " +
                             "AWAY goal is at Y=680 so distance ≈ 165 units = 16.5m). " +
                             "Also check ScoreHold inversion — attacker must NOT hold " +
                             "when under zero pressure.");

                runner.Assert.SaveAttempted(
                    context: "GK must attempt a save. If ShotAttempted passed but " +
                             "SaveAttempted fails: ShotOnTarget=false. Check LaunchShot " +
                             "attacksDown direction — home attacks Y=680 in first half.");

                runner.PrintSummary();
                return runner.Log.Count;
            });
            results.Add(s1);
            PrintResult(s1);

            if (stopOnFirstFailure && !s1.Passed)
            {
                PrintFinalReport(results);
                return false;
            }

            // ── Scenario 2: 2v2 ──────────────────────────────────────────────
            var s2 = Run("2v2 — Dribble Decision and Tackle", seed: 42, () =>
            {
                var ctx    = DebugMatchContext.TwoVTwo(seed: 42);
                var runner = new DebugTickRunner(ctx, maxTicks: 400);
                runner.Run(stopOnGoal: false);

                runner.Assert.TackleAttempted(
                    context: "CB at 380 Y, attacker at 340 Y = 40 units apart. " +
                             "TACKLE_RANGE = 25f. CB must close the distance (sprint) " +
                             "and attempt a tackle within 3-4 ticks. " +
                             "If no tackle fires: check ChasingBall not in sprint cases " +
                             "(PlayerAI Bug 5 + MovementSystem Bug 13).");

                // Either a goal or a save should happen — something should resolve
                bool goalOrSave = ctx.HomeScore > 0 || ctx.AwayScore > 0 ||
                    runner.Log.Count > 0 && runner.Log[^1].LastEventType ==
                        (int)MatchEventType.Save;
                if (!goalOrSave)
                    runner.Assert.ShotAttempted(
                        context: "After CB challenge is resolved (tackled or beaten), " +
                                 "a shot should follow. If dribble bug exists, " +
                                 "attacker never beats the CB and never gets a clear shot.");

                runner.PrintSummary();
                return runner.Log.Count;
            });
            results.Add(s2);
            PrintResult(s2);

            if (stopOnFirstFailure && !s2.Passed)
            {
                PrintFinalReport(results);
                return false;
            }

            // ── Scenario 3: 3v3 Short-Pass ────────────────────────────────────
            var s3 = Run("3v3 Short-Pass — Passing Triangle Realism", seed: 42, () =>
            {
                var ctx    = DebugMatchContext.ThreeVThreeShortPass(seed: 42);
                var runner = new DebugTickRunner(ctx, maxTicks: 600);
                runner.Run(stopOnGoal: false);

                runner.Assert.PassesCompleted(
                    minCount: 3,
                    context: "A 3v3 with possession tactics should complete at least 3 " +
                             "passes in 600 ticks (60 seconds). " +
                             "If failing: ScoreHold inversion causes permanent ball-hold, " +
                             "OR SUPPORT_CROWDING_PENALTY collapses support run scores " +
                             "(AIConstants Bug 14: radius 80f catches all nearby teammates), " +
                             "OR LaunchPass aims at static receiver (BallSystem Bug 1).");

                runner.Assert.LongPassRatioBelow(
                    maxRatio: 0.20f,
                    context: "With ForceLongPassDisabled=true and possession tactics, " +
                             "fewer than 20% of passes should be long balls. " +
                             "If this fails with long pass disabled: check that " +
                             "ctx.ForceLongPassDisabled is being read by ApplyDebugOverrides.");

                runner.PrintSummary();
                return runner.Log.Count;
            });
            results.Add(s3);
            PrintResult(s3);

            if (stopOnFirstFailure && !s3.Passed)
            {
                PrintFinalReport(results);
                return false;
            }

            // ── Scenario 4: 3v3 Long-Pass ─────────────────────────────────────
            var s4 = Run("3v3 Long-Pass — Progressive Play and Lead Pass", seed: 42, () =>
            {
                var ctx    = DebugMatchContext.ThreeVThreeLongPass(seed: 42);
                var runner = new DebugTickRunner(ctx, maxTicks: 600);
                runner.Run(stopOnGoal: false);

                // At least some passes should complete
                runner.Assert.PassesCompleted(
                    minCount: 2,
                    context: "Long-pass enabled scenario should still complete passes. " +
                             "The DLP (index 1) has high passing ability — they should find " +
                             "the ST who is running in behind. " +
                             "If passes fail: LaunchPass lead-prediction may be off, " +
                             "or BALL_AIR_RESISTANCE too low (ball stops mid-flight).");

                // Ball should reach the ST at some point (ownership transfer test)
                runner.Assert.BallOwnedByPlayer(
                    playerId: 0,
                    context: "The home ST (index 0) should receive at least one pass. " +
                             "If failing: BallSystem.BALL_ARRIVAL_THRESHOLD may be too small " +
                             "vs BALL_DRIBBLE_OFFSET (PhysicsConstants Bug 5), " +
                             "or the ball decelerates below BALL_STOP_THRESHOLD before arrival " +
                             "(BallSystem Bug 2 — air resistance too low causes premature stop check).");

                runner.PrintSummary();
                return runner.Log.Count;
            });
            results.Add(s4);
            PrintResult(s4);

            PrintFinalReport(results);
            return results.TrueForAll(r => r.Passed);
        }

        // ── Individual scenario runners ────────────────────────────────────────

        /// <summary>
        /// Convenience: run only the 2v2 scenario. Good for focused dribble/tackle debugging.
        /// </summary>
        public static ScenarioResult RunTwoVTwo(int seed = 42, int maxTicks = 400)
        {
            return Run("2v2 — Dribble and Tackle (standalone)", seed, () =>
            {
                var ctx    = DebugMatchContext.TwoVTwo(seed);
                var runner = new DebugTickRunner(ctx, maxTicks);
                runner.Run();
                runner.Assert.TackleAttempted();
                runner.PrintSummary();
                return runner.Log.Count;
            });
        }

        /// <summary>
        /// Convenience: run only the 1v1 scenario. Good for shot pipeline debugging.
        /// </summary>
        public static ScenarioResult RunOneVOne(int seed = 42, int maxTicks = 200)
        {
            return Run("1v1 — Shot and Save (standalone)", seed, () =>
            {
                var ctx    = DebugMatchContext.OneVOne(seed);
                var runner = new DebugTickRunner(ctx, maxTicks);
                runner.Run();
                runner.Assert.ShotAttempted();
                runner.PrintSummary();
                return runner.Log.Count;
            });
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static ScenarioResult Run(string name, int seed,
                                           Func<int> scenarioBody)
        {
            Console.WriteLine($"► Running: {name}");
            var sw = Stopwatch.StartNew();

            try
            {
                int ticks = scenarioBody();
                sw.Stop();
                return new ScenarioResult
                {
                    Name      = name,
                    Passed    = true,
                    TicksRun  = ticks,
                    ElapsedMs = sw.ElapsedMilliseconds,
                };
            }
            catch (DebugAssertionException ex)
            {
                sw.Stop();
                return new ScenarioResult
                {
                    Name       = name,
                    Passed     = false,
                    TicksRun   = 0,
                    FailReason = ex.Message,
                    ElapsedMs  = sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ScenarioResult
                {
                    Name       = name,
                    Passed     = false,
                    TicksRun   = 0,
                    FailReason = $"[EXCEPTION] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}",
                    ElapsedMs  = sw.ElapsedMilliseconds,
                };
            }
        }

        private static void PrintResult(ScenarioResult r)
        {
            if (r.Passed)
            {
                Console.WriteLine($"  [PASS] {r.Name} ({r.TicksRun} ticks, {r.ElapsedMs}ms)");
            }
            else
            {
                Console.WriteLine($"  [FAIL] {r.Name} ({r.ElapsedMs}ms)");
                if (r.FailReason != null)
                {
                    foreach (var line in r.FailReason.Split('\n'))
                        Console.WriteLine($"         {line}");
                }
            }
            Console.WriteLine();
        }

        private static void PrintFinalReport(List<ScenarioResult> results)
        {
            int passed = results.Count(r => r.Passed);
            int total  = results.Count;

            Console.WriteLine("══════════════════════════════════════════════════════════");
            Console.WriteLine($"  Result: {passed}/{total} scenarios passed");

            if (passed == total)
                Console.WriteLine("  All scenarios passed. Core systems are working correctly.");
            else
                Console.WriteLine("  Fix failing scenarios before debugging 11v11 behaviour.");

            Console.WriteLine("══════════════════════════════════════════════════════════");
            Console.WriteLine();
        }
    }
}
