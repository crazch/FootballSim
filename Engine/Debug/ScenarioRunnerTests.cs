// =============================================================================
// Module:  ScenarioRunnerTests.cs
// Path:    FootballSim/Tests/ScenarioRunnerTests.cs
//
// Purpose:
//   NUnit test file that runs each debug scenario as a named test.
//   Sits alongside the existing BallSystemTests.cs, CollisionSystemTests.cs etc.
//   No Godot dependency — pure C# NUnit tests.
//
//   These tests serve two purposes:
//   1. Regression guard — if a fix for Bug X is applied and later reverted,
//      the relevant scenario test catches it immediately.
//   2. Bug isolation guide — each test method has a comment pointing to the
//      exact bugs it will expose if failing.
//
//   Run with:
//     dotnet test FootballSim/Tests/
//
//   Filter to debug scenarios only:
//     dotnet test --filter "Category=Debug"
//
// =============================================================================

using NUnit.Framework;
using FootballSim.Engine.Debug;
using FootballSim.Engine.Systems;
using FootballSim.Engine.Models;

namespace FootballSim.Tests
{
    [TestFixture]
    [Category("Debug")]
    public class ScenarioRunnerTests
    {
        // Disable all system debug logging before each test — prevents console spam
        [SetUp]
        public void SetUp()
        {
            DebugLogger.DisableAll();
            // Reset the long-pass override so each test starts clean
            AIConstants.DISABLE_LONG_PASS_OVERRIDE = false;
        }

        [TearDown]
        public void TearDown()
        {
            DebugLogger.DisableAll();
            AIConstants.DISABLE_LONG_PASS_OVERRIDE = false;
        }

        // =====================================================================
        // SCENARIO 1 — 1v1 Shot Pipeline
        // =====================================================================

        [Test]
        [Description(
            "Attacker at penalty spot (165 units from away goal) must shoot " +
            "within 30 ticks and the GK must attempt a save. " +
            "Exposes: ScoreHold inversion (Decision Bug 1), " +
            "attacksDown direction hardcode (Decision Bug 4), " +
            "LaunchShot ShotOnTarget wrong for away goal (BallSystem Bug 3+4).")]
        public void OneVOne_AttackerShoots_GKAttemptsSave()
        {
            var ctx    = DebugMatchContext.OneVOne(seed: 42);
            var runner = new DebugTickRunner(ctx, maxTicks: 200);

            runner.Run(stopOnGoal: false);

            // The attacker must shoot — if this fails, ScoreHold is dominating
            // or ScoreShoot xG threshold is wrong for this distance
            Assert.DoesNotThrow(
                () => runner.Assert.ShotWithinTicks(30),
                "Attacker should shoot within 30 ticks from the penalty spot. " +
                "Enable DebugLogger.ProfileOneVOne() to see score breakdowns.");

            // The GK must save or a goal must be scored
            bool saveOrGoal = ctx.HomeScore > 0 || ctx.AwayScore > 0;
            if (!saveOrGoal)
            {
                Assert.DoesNotThrow(
                    () => runner.Assert.SaveAttempted(),
                    "GK save must fire. If shot was attempted but no save fired, " +
                    "ShotOnTarget=false. Check attacksDown direction in LaunchShot.");
            }
        }

        [Test]
        [Description(
            "Goal scored by home team (index 0) must be credited to home, not away. " +
            "Exposes: EmitGoal index-band bug (EventSystem Bug 3) — " +
            "uses playerId <= 10 ? 0 : 1 instead of PlayerState.TeamId.")]
        public void OneVOne_GoalCreditedToCorrectTeam()
        {
            // Use a seed where a goal is statistically likely in 600 ticks
            var ctx    = DebugMatchContext.OneVOne(seed: 99);
            var runner = new DebugTickRunner(ctx, maxTicks: 600);
            runner.Run(stopOnGoal: false);

            // If any goal was scored, it must be for the home team (index 0 = ST)
            if (ctx.HomeScore > 0 || ctx.AwayScore > 0)
            {
                Assert.AreEqual(0, ctx.AwayScore,
                    "Away score should be 0 — home striker scored. " +
                    "If AwayScore > 0: EventSystem.EmitGoal uses index band " +
                    "(playerId <= 10 ? 0 : 1) to determine scorerTeam. " +
                    "The ST is at index 0 which is <= 10, so this should return teamId=0. " +
                    "If still wrong: check that ctx.Players[0].TeamId == 0.");
            }
        }

        // =====================================================================
        // SCENARIO 2 — 2v2 Dribble and Tackle
        // =====================================================================

        [Test]
        [Description(
            "CB 40 units from attacker must attempt a tackle within 200 ticks. " +
            "Exposes: ChasingBall not in sprint cases (PlayerAI Bug 5 + MovementSystem Bug 13), " +
            "TACKLE_RANGE=25 too small for initial gap (CB must close first), " +
            "ScoreDribble dead zone near 0 in 1v1 range (DecisionSystem Bug 2).")]
        public void TwoVTwo_CBAttemptsTackle()
        {
            var ctx    = DebugMatchContext.TwoVTwo(seed: 42);
            var runner = new DebugTickRunner(ctx, maxTicks: 400);

            runner.Run();

            Assert.DoesNotThrow(
                () => runner.Assert.TackleAttempted(),
                "CB at 380Y (40 units from attacker at 340Y) should close gap and tackle. " +
                "If failing: CB may not be sprinting toward ball (ChasingBall sprint bug). " +
                "Enable DebugLogger.ProfileTwoVTwo() to see PlayerAI decisions.");
        }

        [Test]
        [Description(
            "After the tackle is resolved, something must happen (goal or save). " +
            "Tests that the full pipeline completes: decision → tackle/dribble → shot → save/goal. " +
            "If this passes but OneVOne_AttackerShoots fails, the bug is in tackle-then-shoot " +
            "sequencing, not in the shot pipeline itself.")]
        public void TwoVTwo_FullPipelineCompletes()
        {
            var ctx    = DebugMatchContext.TwoVTwo(seed: 42);
            var runner = new DebugTickRunner(ctx, maxTicks: 600);

            runner.Run(stopOnGoal: false);

            // After 600 ticks (60 seconds), either:
            // - A goal was scored (attacker beat CB and finished), OR
            // - A save was made (attacker beat CB but GK saved), OR
            // - At least a shot was attempted
            bool anyResolution = ctx.HomeScore > 0 || ctx.AwayScore > 0 ||
                runner.Log.Count > 0 &&
                runner.Log[^1].LastEventType == (int)MatchEventType.Save;

            if (!anyResolution)
            {
                Assert.DoesNotThrow(
                    () => runner.Assert.ShotAttempted(),
                    "In 600 ticks, the attacker should have shot at least once. " +
                    "If tackle fires but no shot follows, the attacker may be " +
                    "failing to regain possession after a failed tackle (BallSystem.SetLoose " +
                    "deflection direction) or ScoreDribble dead zone prevents re-engagement.");
            }
        }

        // =====================================================================
        // SCENARIO 3 — 3v3 Short-Pass Triangle
        // =====================================================================

        [Test]
        [Description(
            "3v3 with possession tactics must complete >= 3 passes in 600 ticks. " +
            "Exposes: ScoreHold inversion (Decision Bug 1) — permanent ball hold, " +
            "SUPPORT_CROWDING_PENALTY too high (AIConstants Bug 14) — suppresses all support runs, " +
            "LaunchPass static aim (BallSystem Bug 1) — pass goes Loose behind receiver.")]
        public void ThreeVThree_ShortPass_MinThreePassesComplete()
        {
            var ctx    = DebugMatchContext.ThreeVThreeShortPass(seed: 42);
            var runner = new DebugTickRunner(ctx, maxTicks: 600);

            runner.Run();

            Assert.DoesNotThrow(
                () => runner.Assert.PassesCompleted(3),
                "At least 3 completed passes expected in 600 ticks with possession tactics. " +
                "Enable DebugLogger.ProfileThreeVThree() to see pass scores per tick.");
        }

        [Test]
        [Description(
            "With ForceLongPassDisabled=true and possession tactics, " +
            "fewer than 20% of passes should be long balls. " +
            "Exposes: ctx.ForceLongPassDisabled not being applied, " +
            "PASS_PROGRESSIVE_BONUS overscoring forward long balls (AIConstants Bug 13).")]
        public void ThreeVThree_ShortPass_LongPassRatioBelow20Percent()
        {
            var ctx    = DebugMatchContext.ThreeVThreeShortPass(seed: 42);
            var runner = new DebugTickRunner(ctx, maxTicks: 600);

            runner.Run();

            Assert.DoesNotThrow(
                () => runner.Assert.LongPassRatioBelow(0.20f),
                "With ForceLongPassDisabled=true, long pass ratio must be < 20%. " +
                "If failing: verify DebugTickRunner.ApplyDebugOverrides sets " +
                "AIConstants.DISABLE_LONG_PASS_OVERRIDE correctly.");
        }

        // =====================================================================
        // SCENARIO 4 — 3v3 Long-Pass
        // =====================================================================

        [Test]
        [Description(
            "DLP (high passing ability) should complete passes to running ST. " +
            "Exposes: BallSystem Bug 1 (static aim — ball misses running ST), " +
            "BallSystem Bug 2 (air resistance too low — ball stops mid-flight), " +
            "PhysicsConstants Bug 5 (BALL_ARRIVAL_THRESHOLD <= BALL_DRIBBLE_OFFSET).")]
        public void ThreeVThree_LongPass_STReceivesBall()
        {
            var ctx    = DebugMatchContext.ThreeVThreeLongPass(seed: 42);
            var runner = new DebugTickRunner(ctx, maxTicks: 600);

            runner.Run();

            Assert.DoesNotThrow(
                () => runner.Assert.BallOwnedByPlayer(0),
                "Home ST (index 0) should receive at least one pass. " +
                "If failing with BallSystem Bug 1 still present: " +
                "the ball arrives where the ST was, not where they are. " +
                "Enable BallSystem.DEBUG=true to see PassTargetId arrival distances.");
        }

        // =====================================================================
        // FULL SUITE — runs all four scenarios via ScenarioRunner
        // =====================================================================

        [Test]
        [Description("Runs all 4 scenarios via ScenarioRunner and fails if any fail.")]
        public void AllScenarios_Pass()
        {
            bool allPassed = ScenarioRunner.RunAll(stopOnFirstFailure: false);

            Assert.IsTrue(allPassed,
                "One or more debug scenarios failed. " +
                "Run ScenarioRunner.RunAll() directly to see the full output with " +
                "diagnostic messages per failing assertion.");
        }
    }
}
