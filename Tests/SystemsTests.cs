// =============================================================================
// Module:  SystemsTests.cs
// Path:    FootballSim/Tests/SystemsTests.cs
// Purpose:
//   Unit tests for BallSystem and MovementSystem.
//   Tests run entirely in C# with no Godot dependency.
//   Each test constructs a minimal MatchContext with only the fields the
//   system under test requires — nothing else is populated.
//
//   Run these before wiring any Godot scene. If these pass, the physics
//   layer is stable and future systems (DecisionSystem, CollisionSystem)
//   can rely on BallSystem and MovementSystem producing correct state.
//
//   Tests are written as self-contained static methods that throw on failure.
//   No test framework required — call RunAll() from a standalone C# console
//   project or from MatchEngineBridge in DEBUG mode at startup.
//
// Test coverage:
//
//   BallSystem:
//     01 Ball advances by velocity each tick when InFlight
//     02 Ball applies air resistance each tick when InFlight
//     03 Ball applies ground friction each tick when Loose
//     04 Ball stops (velocity → zero) when Loose speed < STOP_THRESHOLD
//     05 Ball goes Loose when InFlight velocity decays below stop threshold
//     06 Ball transfers ownership when receiver arrives within ARRIVAL_THRESHOLD
//     07 Ball does NOT transfer ownership when receiver is outside threshold
//     08 Ball IsOutOfPlay set when ball crosses right touchline
//     09 Ball IsOutOfPlay set when ball crosses top byline (Y < 0)
//     10 Ball position clamped to boundary when out of play
//     11 Owned ball position mirrors owner position + dribble offset
//     12 LooseTicks increments each tick when Loose
//     13 LooseTicks resets to 0 when ownership transferred
//     14 HomePossessionTicks increments when home player owns ball
//     15 AwayPossessionTicks increments when away player owns ball
//     16 HasBall flag set on owner, cleared on all others
//     17 LaunchPass sets correct velocity direction toward receiver
//     18 LaunchShot ShotOnTarget=true when aimed at goal mouth
//     19 LaunchShot ShotOnTarget=false when aimed wide
//     20 Height decays each tick for aerial ball
//     21 Corrupt Owned state (invalid OwnerId) forces ball Loose
//
//   MovementSystem:
//     22 Player advances toward TargetPosition each tick
//     23 Player stops at TargetPosition (doesn't overshoot)
//     24 Player speed limited by effective speed (BaseSpeed × StaminaCurve)
//     25 Player at low stamina moves slower than player at full stamina
//     26 Stamina decays when IsSprinting=true
//     27 Stamina recovers when Idle (not sprinting, not jogging)
//     28 High StaminaAttribute decays stamina slower than low attribute
//     29 ComputeStaminaCurve returns 1.0 at Stamina=1.0
//     30 ComputeStaminaCurve returns STAMINA_SPEED_MIN_MULTIPLIER at Stamina=0
//     31 Player position clamped when TargetPosition is outside pitch
//     32 Inactive player (IsActive=false) not updated
//     33 Player velocity smoothly accelerates (doesn't jump to max speed)
//     34 Same input → same output (determinism check)
//
// Dependencies:
//   Engine/Models/*
//   Engine/Systems/BallSystem.cs
//   Engine/Systems/MovementSystem.cs
//   Engine/Systems/PhysicsConstants.cs
//   Engine/Tactics/TacticsInput.cs
// =============================================================================

using System;
using FootballSim.Engine.Models;
using FootballSim.Engine.Systems;
using FootballSim.Engine.Tactics;

namespace FootballSim.Tests
{
    public static class SystemsTests
    {
        // ── Test Runner ───────────────────────────────────────────────────────

        /// <summary>
        /// Runs all tests. Prints PASS/FAIL per test.
        /// Returns total failure count. 0 = all passed.
        /// Call from MatchEngineBridge when DEBUG=true, or from a console test runner.
        /// </summary>
        public static int RunAll()
        {
            int failures = 0;
            failures += RunTest("01 Ball advances by velocity InFlight",            Test_01_BallAdvancesInFlight);
            failures += RunTest("02 Ball air resistance applied InFlight",          Test_02_BallAirResistance);
            failures += RunTest("03 Ball ground friction applied Loose",            Test_03_BallGroundFriction);
            failures += RunTest("04 Ball stops when Loose speed below threshold",   Test_04_BallStopsWhenSlow);
            failures += RunTest("05 Ball goes Loose when InFlight velocity zero",   Test_05_InFlightGoesLooseWhenStopped);
            failures += RunTest("06 Ownership transferred on arrival",              Test_06_OwnershipOnArrival);
            failures += RunTest("07 No transfer when receiver outside threshold",   Test_07_NoTransferOutsideThreshold);
            failures += RunTest("08 OutOfPlay right touchline",                     Test_08_OutOfPlayRightTouchline);
            failures += RunTest("09 OutOfPlay top byline",                          Test_09_OutOfPlayTopByline);
            failures += RunTest("10 Position clamped on out of play",               Test_10_PositionClampedOutOfPlay);
            failures += RunTest("11 Owned ball mirrors owner position",             Test_11_OwnedBallMirrorsOwner);
            failures += RunTest("12 LooseTicks increments",                         Test_12_LooseTicksIncrements);
            failures += RunTest("13 LooseTicks resets on ownership transfer",       Test_13_LooseTicksResetOnTransfer);
            failures += RunTest("14 HomePossessionTicks increments",                Test_14_HomePossessionTicks);
            failures += RunTest("15 AwayPossessionTicks increments",                Test_15_AwayPossessionTicks);
            failures += RunTest("16 HasBall flag sync",                             Test_16_HasBallFlagSync);
            failures += RunTest("17 LaunchPass velocity toward receiver",           Test_17_LaunchPassVelocity);
            failures += RunTest("18 LaunchShot ShotOnTarget true",                  Test_18_ShotOnTarget);
            failures += RunTest("19 LaunchShot ShotOnTarget false when wide",       Test_19_ShotWide);
            failures += RunTest("20 Aerial height decays each tick",                Test_20_HeightDecay);
            failures += RunTest("21 Corrupt Owned state forces Loose",              Test_21_CorruptOwnedForcesLoose);
            failures += RunTest("22 Player moves toward target",                    Test_22_PlayerMovesTowardTarget);
            failures += RunTest("23 Player stops at target, no overshoot",          Test_23_PlayerStopsAtTarget);
            failures += RunTest("24 Player speed limited by effective speed",       Test_24_SpeedLimited);
            failures += RunTest("25 Low stamina player moves slower",               Test_25_LowStaminaSlower);
            failures += RunTest("26 Stamina decays while sprinting",                Test_26_StaminaDecaysSprinting);
            failures += RunTest("27 Stamina recovers while idle",                   Test_27_StaminaRecoversIdle);
            failures += RunTest("28 High StaminaAttribute decays slower",           Test_28_HighAttributeSlowerDecay);
            failures += RunTest("29 StaminaCurve = 1.0 at full stamina",            Test_29_StaminaCurveFull);
            failures += RunTest("30 StaminaCurve = min at zero stamina",            Test_30_StaminaCurveZero);
            failures += RunTest("31 Position clamped outside pitch",                Test_31_PositionClamped);
            failures += RunTest("32 Inactive player not updated",                   Test_32_InactiveNotUpdated);
            failures += RunTest("33 Velocity accelerates smoothly",                 Test_33_VelocityAccelerates);
            failures += RunTest("34 Determinism: same input same output",           Test_34_Determinism);

            Console.WriteLine($"\n=== SystemsTests: {(failures == 0 ? "ALL PASSED" : $"{failures} FAILED")} ===");
            return failures;
        }

        private static int RunTest(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine($"  PASS  {name}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine($"        {ex.Message}");
                return 1;
            }
        }

        // ── Test Helpers ──────────────────────────────────────────────────────

        /// <summary>Minimal MatchContext with two teams, basic tactics, all players inactive.</summary>
        private static MatchContext MakeContext()
        {
            var homeTeam = new TeamData
            {
                TeamId = 0,
                Name   = "Home",
                FormationName = "4-3-3",
                Players = new PlayerData[11],
                Tactics = TacticsInput.Default(),
            };
            var awayTeam = new TeamData
            {
                TeamId = 1,
                Name   = "Away",
                FormationName = "4-3-3",
                Players = new PlayerData[11],
                Tactics = TacticsInput.Default(),
            };

            var ctx = new MatchContext(homeTeam, awayTeam);
            ctx.Phase = MatchPhase.OpenPlay;

            // Default all players: inactive so tests can activate only what they need
            for (int i = 0; i < 22; i++)
            {
                ctx.Players[i] = new PlayerState
                {
                    PlayerId         = i,
                    TeamId           = i <= 10 ? 0 : 1,
                    IsActive         = false,
                    BaseSpeed        = 8.0f,
                    Stamina          = 1.0f,
                    StaminaAttribute = 0.7f,
                    Position         = new Vec2(525f, 340f), // pitch centre
                    TargetPosition   = new Vec2(525f, 340f),
                    Action           = PlayerAction.Idle,
                };
            }

            // Ball at centre
            ctx.Ball = new BallState
            {
                Position       = new Vec2(525f, 340f),
                Velocity       = Vec2.Zero,
                Phase          = BallPhase.Loose,
                OwnerId        = -1,
                PassTargetId   = -1,
                LastTouchedBy  = 0,
                IsOutOfPlay    = false,
                OutOfPlayCausedBy = -1,
            };

            return ctx;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void AssertApprox(float actual, float expected, float tolerance, string message)
        {
            if (MathF.Abs(actual - expected) > tolerance)
                throw new Exception($"{message} Expected≈{expected:F4} Got={actual:F4} Tol={tolerance}");
        }

        // =====================================================================
        // BALL SYSTEM TESTS
        // =====================================================================

        private static void Test_01_BallAdvancesInFlight()
        {
            var ctx = MakeContext();
            ctx.Ball.Phase    = BallPhase.InFlight;
            ctx.Ball.Position = new Vec2(100f, 100f);
            ctx.Ball.Velocity = new Vec2(10f, 5f);
            ctx.Ball.PassTargetId = -1;

            BallSystem.Tick(ctx);

            // Position should be approximately (110, 105) after one tick
            // (exact values affected by air resistance applied to velocity)
            Assert(ctx.Ball.Position.X > 109f, "Ball should have moved right");
            Assert(ctx.Ball.Position.Y > 104f, "Ball should have moved down");
        }

        private static void Test_02_BallAirResistance()
        {
            var ctx = MakeContext();
            ctx.Ball.Phase    = BallPhase.InFlight;
            ctx.Ball.Position = new Vec2(200f, 200f);
            ctx.Ball.Velocity = new Vec2(20f, 0f);
            ctx.Ball.PassTargetId = -1;

            BallSystem.Tick(ctx);

            float expectedVelX = 20f * PhysicsConstants.BALL_AIR_RESISTANCE;
            AssertApprox(ctx.Ball.Velocity.X, expectedVelX, 0.01f,
                "Air resistance not applied correctly to InFlight ball X velocity");
        }

        private static void Test_03_BallGroundFriction()
        {
            var ctx = MakeContext();
            ctx.Ball.Phase    = BallPhase.Loose;
            ctx.Ball.Position = new Vec2(200f, 200f);
            ctx.Ball.Velocity = new Vec2(15f, 0f);

            BallSystem.Tick(ctx);

            float expectedVelX = 15f * PhysicsConstants.BALL_GROUND_FRICTION;
            AssertApprox(ctx.Ball.Velocity.X, expectedVelX, 0.01f,
                "Ground friction not applied correctly to Loose ball");
        }

        private static void Test_04_BallStopsWhenSlow()
        {
            var ctx = MakeContext();
            ctx.Ball.Phase    = BallPhase.Loose;
            ctx.Ball.Position = new Vec2(300f, 300f);
            ctx.Ball.Velocity = new Vec2(0.3f, 0f); // below STOP_THRESHOLD after friction

            // Run enough ticks for ball to stop
            for (int i = 0; i < 10; i++)
                BallSystem.Tick(ctx);

            AssertApprox(ctx.Ball.Velocity.X, 0f, 0.001f, "Ball should have stopped");
            AssertApprox(ctx.Ball.Velocity.Y, 0f, 0.001f, "Ball velocity Y should be zero");
        }

        private static void Test_05_InFlightGoesLooseWhenStopped()
        {
            var ctx = MakeContext();
            ctx.Ball.Phase        = BallPhase.InFlight;
            ctx.Ball.Position     = new Vec2(300f, 300f);
            ctx.Ball.Velocity     = new Vec2(0.001f, 0f); // extremely slow — will drop below threshold
            ctx.Ball.PassTargetId = -1;

            BallSystem.Tick(ctx);

            Assert(ctx.Ball.Phase == BallPhase.Loose,
                $"Ball should go Loose when InFlight velocity near zero. Got {ctx.Ball.Phase}");
        }

        private static void Test_06_OwnershipOnArrival()
        {
            var ctx = MakeContext();
            ctx.Players[5].IsActive   = true;
            ctx.Players[5].Position   = new Vec2(300f, 300f);

            ctx.Ball.Phase        = BallPhase.InFlight;
            ctx.Ball.Position     = new Vec2(300f, 300f); // ball right at receiver
            ctx.Ball.Velocity     = new Vec2(5f, 0f);
            ctx.Ball.PassTargetId = 5;

            BallSystem.Tick(ctx);

            Assert(ctx.Ball.Phase == BallPhase.Owned,
                $"Ball should be Owned after receiver arrives. Got {ctx.Ball.Phase}");
            Assert(ctx.Ball.OwnerId == 5, $"OwnerId should be 5. Got {ctx.Ball.OwnerId}");
        }

        private static void Test_07_NoTransferOutsideThreshold()
        {
            var ctx = MakeContext();
            ctx.Players[5].IsActive  = true;
            ctx.Players[5].Position  = new Vec2(500f, 300f); // far from ball

            ctx.Ball.Phase        = BallPhase.InFlight;
            ctx.Ball.Position     = new Vec2(300f, 300f);
            ctx.Ball.Velocity     = new Vec2(5f, 0f);
            ctx.Ball.PassTargetId = 5;

            BallSystem.Tick(ctx);

            Assert(ctx.Ball.Phase == BallPhase.InFlight,
                $"Ball should remain InFlight when receiver is far. Got {ctx.Ball.Phase}");
        }

        private static void Test_08_OutOfPlayRightTouchline()
        {
            var ctx = MakeContext();
            ctx.Ball.Phase    = BallPhase.InFlight;
            ctx.Ball.Position = new Vec2(1060f, 300f); // beyond right touchline
            ctx.Ball.Velocity = new Vec2(10f, 0f);
            ctx.Ball.PassTargetId = -1;

            BallSystem.Tick(ctx);

            Assert(ctx.Ball.IsOutOfPlay, "Ball should be out of play crossing right touchline");
        }

        private static void Test_09_OutOfPlayTopByline()
        {
            var ctx = MakeContext();
            ctx.Ball.Phase    = BallPhase.InFlight;
            ctx.Ball.Position = new Vec2(525f, -10f); // beyond top byline
            ctx.Ball.Velocity = new Vec2(0f, -5f);
            ctx.Ball.PassTargetId = -1;

            BallSystem.Tick(ctx);

            Assert(ctx.Ball.IsOutOfPlay, "Ball should be out of play crossing top byline");
        }

        private static void Test_10_PositionClampedOutOfPlay()
        {
            var ctx = MakeContext();
            ctx.Ball.Phase    = BallPhase.Loose;
            ctx.Ball.Position = new Vec2(2000f, 2000f); // way outside pitch
            ctx.Ball.Velocity = new Vec2(0f, 0f);

            BallSystem.Tick(ctx);

            Assert(ctx.Ball.Position.X <= PhysicsConstants.PITCH_RIGHT,
                $"Ball X should be clamped to pitch right. Got {ctx.Ball.Position.X}");
            Assert(ctx.Ball.Position.Y <= PhysicsConstants.PITCH_BOTTOM,
                $"Ball Y should be clamped to pitch bottom. Got {ctx.Ball.Position.Y}");
        }

        private static void Test_11_OwnedBallMirrorsOwner()
        {
            var ctx = MakeContext();
            ctx.Players[3].IsActive  = true;
            ctx.Players[3].Position  = new Vec2(400f, 300f);
            ctx.Players[3].Velocity  = Vec2.Zero;
            ctx.Players[3].Action    = PlayerAction.Holding;

            ctx.Ball.Phase   = BallPhase.Owned;
            ctx.Ball.OwnerId = 3;

            BallSystem.Tick(ctx);

            // Ball should be near player 3's position (within hold offset)
            float dist = ctx.Ball.Position.DistanceTo(ctx.Players[3].Position);
            Assert(dist <= PhysicsConstants.BALL_HOLD_OFFSET + 1f,
                $"Owned ball should be near owner. Distance={dist:F2}, " +
                $"MaxExpected={PhysicsConstants.BALL_HOLD_OFFSET + 1f}");
        }

        private static void Test_12_LooseTicksIncrements()
        {
            var ctx = MakeContext();
            ctx.Ball.Phase      = BallPhase.Loose;
            ctx.Ball.Velocity   = Vec2.Zero;
            ctx.Ball.LooseTicks = 0;

            BallSystem.Tick(ctx);
            Assert(ctx.Ball.LooseTicks == 1, $"LooseTicks should be 1 after 1 Loose tick. Got {ctx.Ball.LooseTicks}");

            BallSystem.Tick(ctx);
            Assert(ctx.Ball.LooseTicks == 2, $"LooseTicks should be 2 after 2 Loose ticks. Got {ctx.Ball.LooseTicks}");
        }

        private static void Test_13_LooseTicksResetOnTransfer()
        {
            var ctx = MakeContext();
            ctx.Players[2].IsActive  = true;
            ctx.Players[2].Position  = new Vec2(525f, 340f);

            ctx.Ball.Phase        = BallPhase.InFlight;
            ctx.Ball.Position     = new Vec2(525f, 340f);
            ctx.Ball.Velocity     = new Vec2(1f, 0f);
            ctx.Ball.PassTargetId = 2;
            ctx.Ball.LooseTicks   = 99; // artificially high

            BallSystem.Tick(ctx);

            Assert(ctx.Ball.LooseTicks == 0,
                $"LooseTicks should reset to 0 after ownership transfer. Got {ctx.Ball.LooseTicks}");
        }

        private static void Test_14_HomePossessionTicks()
        {
            var ctx = MakeContext();
            ctx.Players[7].IsActive  = true;
            ctx.Players[7].Position  = new Vec2(525f, 340f);
            ctx.Players[7].Velocity  = Vec2.Zero;
            ctx.Players[7].Action    = PlayerAction.Holding;

            ctx.Ball.Phase   = BallPhase.Owned;
            ctx.Ball.OwnerId = 7; // home player (index ≤ 10)

            int before = ctx.HomePossessionTicks;
            BallSystem.Tick(ctx);

            Assert(ctx.HomePossessionTicks == before + 1,
                $"HomePossessionTicks should increment. Before={before} After={ctx.HomePossessionTicks}");
            Assert(ctx.AwayPossessionTicks == 0, "AwayPossessionTicks should not change");
        }

        private static void Test_15_AwayPossessionTicks()
        {
            var ctx = MakeContext();
            ctx.Players[15].IsActive  = true;
            ctx.Players[15].Position  = new Vec2(525f, 340f);
            ctx.Players[15].Velocity  = Vec2.Zero;
            ctx.Players[15].Action    = PlayerAction.Holding;

            ctx.Ball.Phase   = BallPhase.Owned;
            ctx.Ball.OwnerId = 15; // away player (index > 10)

            BallSystem.Tick(ctx);

            Assert(ctx.AwayPossessionTicks == 1, "AwayPossessionTicks should be 1");
            Assert(ctx.HomePossessionTicks == 0, "HomePossessionTicks should not change");
        }

        private static void Test_16_HasBallFlagSync()
        {
            var ctx = MakeContext();
            ctx.Players[4].IsActive  = true;
            ctx.Players[4].Position  = new Vec2(525f, 340f);
            ctx.Players[4].Velocity  = Vec2.Zero;
            ctx.Players[4].Action    = PlayerAction.Holding;

            ctx.Ball.Phase   = BallPhase.Owned;
            ctx.Ball.OwnerId = 4;

            BallSystem.Tick(ctx);

            Assert(ctx.Players[4].HasBall, "Player 4 should have HasBall=true");
            for (int i = 0; i < 22; i++)
                if (i != 4) Assert(!ctx.Players[i].HasBall, $"Player {i} should not have HasBall");
        }

        private static void Test_17_LaunchPassVelocity()
        {
            var ctx = MakeContext();
            ctx.Players[0].IsActive  = true;
            ctx.Players[0].Position  = new Vec2(100f, 340f);
            ctx.Players[1].IsActive  = true;
            ctx.Players[1].Position  = new Vec2(500f, 340f); // directly to the right

            ctx.Ball.Phase   = BallPhase.Owned;
            ctx.Ball.OwnerId = 0;

            BallSystem.LaunchPass(ctx, 0, 1, PhysicsConstants.BALL_PASS_SPEED_MED, 0f);

            Assert(ctx.Ball.Phase == BallPhase.InFlight, "Ball should be InFlight after LaunchPass");
            Assert(ctx.Ball.PassTargetId == 1, $"PassTargetId should be 1. Got {ctx.Ball.PassTargetId}");
            // Direction should be purely right (positive X, near-zero Y)
            Assert(ctx.Ball.Velocity.X > 0f, "Pass velocity X should be positive (rightward)");
            AssertApprox(ctx.Ball.Velocity.Y, 0f, 0.5f, "Pass velocity Y should be near zero (straight pass)");
        }

        private static void Test_18_ShotOnTarget()
        {
            var ctx = MakeContext();
            ctx.Players[9].IsActive = true;
            ctx.Players[9].TeamId   = 0;  // home player attacks toward Y = 680
            ctx.Players[9].Position = new Vec2(525f, 400f); // inside attacking half

            ctx.Ball.Phase   = BallPhase.Owned;
            ctx.Ball.OwnerId = 9;

            // Aim at centre of away goal
            Vec2 goalCentre = new Vec2(525f, PhysicsConstants.AWAY_GOAL_LINE_Y);
            BallSystem.LaunchShot(ctx, 9, goalCentre, PhysicsConstants.BALL_SHOT_SPEED_POWER);

            Assert(ctx.Ball.ShotOnTarget, "Shot aimed at goal centre should be OnTarget");
        }

        private static void Test_19_ShotWide()
        {
            var ctx = MakeContext();
            ctx.Players[9].IsActive = true;
            ctx.Players[9].TeamId   = 0;
            ctx.Players[9].Position = new Vec2(525f, 400f);

            ctx.Ball.Phase   = BallPhase.Owned;
            ctx.Ball.OwnerId = 9;

            // Aim far wide of goal
            Vec2 wideTarget = new Vec2(0f, PhysicsConstants.AWAY_GOAL_LINE_Y);
            BallSystem.LaunchShot(ctx, 9, wideTarget, PhysicsConstants.BALL_SHOT_SPEED_POWER);

            Assert(!ctx.Ball.ShotOnTarget, "Shot aimed wide should not be OnTarget");
        }

        private static void Test_20_HeightDecay()
        {
            var ctx = MakeContext();
            ctx.Ball.Phase     = BallPhase.InFlight;
            ctx.Ball.Position  = new Vec2(300f, 300f);
            ctx.Ball.Velocity  = new Vec2(10f, 0f);
            ctx.Ball.Height    = 1.0f;
            ctx.Ball.PassTargetId = -1;

            BallSystem.Tick(ctx);

            float expectedHeight = 1.0f - PhysicsConstants.BALL_HEIGHT_DECAY_PER_TICK;
            AssertApprox(ctx.Ball.Height, expectedHeight, 0.001f,
                "Ball height should decay by BALL_HEIGHT_DECAY_PER_TICK each tick");
        }

        private static void Test_21_CorruptOwnedForcesLoose()
        {
            var ctx = MakeContext();
            ctx.Ball.Phase   = BallPhase.Owned;
            ctx.Ball.OwnerId = -99; // invalid

            BallSystem.Tick(ctx);

            Assert(ctx.Ball.Phase == BallPhase.Loose,
                $"Corrupt OwnerId should force ball Loose. Got Phase={ctx.Ball.Phase}");
        }

        // =====================================================================
        // MOVEMENT SYSTEM TESTS
        // =====================================================================

        private static void Test_22_PlayerMovesTowardTarget()
        {
            var ctx = MakeContext();
            ctx.Players[5].IsActive        = true;
            ctx.Players[5].Position        = new Vec2(200f, 200f);
            ctx.Players[5].TargetPosition  = new Vec2(600f, 200f); // move right
            ctx.Players[5].BaseSpeed       = 8f;
            ctx.Players[5].Stamina         = 1.0f;
            ctx.Players[5].Action          = PlayerAction.WalkingToAnchor;

            MovementSystem.Tick(ctx);

            Assert(ctx.Players[5].Position.X > 200f,
                $"Player should have moved right. X={ctx.Players[5].Position.X}");
        }

        private static void Test_23_PlayerStopsAtTarget()
        {
            var ctx = MakeContext();
            ctx.Players[5].IsActive        = true;
            ctx.Players[5].Position        = new Vec2(200f, 200f);
            ctx.Players[5].TargetPosition  = new Vec2(200f + 1f, 200f); // just 1 unit away
            ctx.Players[5].BaseSpeed       = 8f;
            ctx.Players[5].Stamina         = 1.0f;
            ctx.Players[5].Action          = PlayerAction.WalkingToAnchor;

            MovementSystem.Tick(ctx);

            // Should snap to target — within arrival threshold
            AssertApprox(ctx.Players[5].Position.X, 200f + 1f, 0.1f,
                "Player should snap to target when within arrival threshold");
        }

        private static void Test_24_SpeedLimited()
        {
            var ctx = MakeContext();
            ctx.Players[5].IsActive        = true;
            ctx.Players[5].Position        = new Vec2(100f, 340f);
            ctx.Players[5].TargetPosition  = new Vec2(900f, 340f); // far away
            ctx.Players[5].BaseSpeed       = 8f;
            ctx.Players[5].Stamina         = 1.0f;
            ctx.Players[5].Action          = PlayerAction.Pressing; // sprint
            ctx.Players[5].IsSprinting     = true;

            MovementSystem.Tick(ctx);

            float distMoved = ctx.Players[5].Position.X - 100f;
            // Should not exceed effective speed (roughly 8 × 1.0 × tempo_scale for 1 tick)
            // With acceleration, should be less than PLAYER_ACCELERATION for first tick from rest
            Assert(distMoved <= PhysicsConstants.PLAYER_ACCELERATION + 0.5f,
                $"Player moved too far in first tick: {distMoved:F2} (acceleration limit={PhysicsConstants.PLAYER_ACCELERATION})");
        }

        private static void Test_25_LowStaminaSlower()
        {
            // Run two players — one at full stamina, one at low stamina
            var ctx1 = MakeContext();
            ctx1.Players[5].IsActive       = true;
            ctx1.Players[5].Position       = new Vec2(100f, 340f);
            ctx1.Players[5].TargetPosition = new Vec2(900f, 340f);
            ctx1.Players[5].BaseSpeed      = 8f;
            ctx1.Players[5].Stamina        = 1.0f;
            ctx1.Players[5].Action         = PlayerAction.Pressing;
            ctx1.Players[5].IsSprinting    = true;

            var ctx2 = MakeContext();
            ctx2.Players[5].IsActive       = true;
            ctx2.Players[5].Position       = new Vec2(100f, 340f);
            ctx2.Players[5].TargetPosition = new Vec2(900f, 340f);
            ctx2.Players[5].BaseSpeed      = 8f;
            ctx2.Players[5].Stamina        = 0.1f; // nearly exhausted
            ctx2.Players[5].Action         = PlayerAction.Pressing;
            ctx2.Players[5].IsSprinting    = true;

            // Run several ticks so acceleration plays out
            for (int i = 0; i < 20; i++)
            {
                MovementSystem.Tick(ctx1);
                MovementSystem.Tick(ctx2);
            }

            float dist1 = ctx1.Players[5].Position.X - 100f;
            float dist2 = ctx2.Players[5].Position.X - 100f;

            Assert(dist1 > dist2, $"Full stamina player should cover more ground. " +
                                   $"Full={dist1:F1} Low={dist2:F1}");
        }

        private static void Test_26_StaminaDecaysSprinting()
        {
            var ctx = MakeContext();
            ctx.Players[5].IsActive        = true;
            ctx.Players[5].Position        = new Vec2(100f, 340f);
            ctx.Players[5].TargetPosition  = new Vec2(900f, 340f);
            ctx.Players[5].BaseSpeed       = 8f;
            ctx.Players[5].Stamina         = 1.0f;
            ctx.Players[5].StaminaAttribute = 0.5f;
            ctx.Players[5].Action          = PlayerAction.Pressing;
            ctx.Players[5].IsSprinting     = true;

            float before = ctx.Players[5].Stamina;
            MovementSystem.Tick(ctx);

            Assert(ctx.Players[5].Stamina < before,
                "Stamina should decrease when sprinting");
        }

        private static void Test_27_StaminaRecoversIdle()
        {
            var ctx = MakeContext();
            ctx.Players[5].IsActive        = true;
            ctx.Players[5].Position        = new Vec2(100f, 340f);
            ctx.Players[5].TargetPosition  = new Vec2(100f, 340f); // already at target → snap + no movement
            ctx.Players[5].BaseSpeed       = 8f;
            ctx.Players[5].Stamina         = 0.5f; // below full
            ctx.Players[5].StaminaAttribute = 0.7f;
            ctx.Players[5].Action          = PlayerAction.Idle;
            ctx.Players[5].IsSprinting     = false;

            float before = ctx.Players[5].Stamina;
            MovementSystem.Tick(ctx);

            Assert(ctx.Players[5].Stamina > before,
                $"Stamina should recover when idle/stopped. Before={before} After={ctx.Players[5].Stamina}");
        }

        private static void Test_28_HighAttributeSlowerDecay()
        {
            var ctx1 = MakeContext();
            ctx1.Players[5].IsActive        = true;
            ctx1.Players[5].Position        = new Vec2(100f, 340f);
            ctx1.Players[5].TargetPosition  = new Vec2(900f, 340f);
            ctx1.Players[5].BaseSpeed       = 8f;
            ctx1.Players[5].Stamina         = 1.0f;
            ctx1.Players[5].StaminaAttribute = 0.9f; // high attribute
            ctx1.Players[5].Action          = PlayerAction.Pressing;

            var ctx2 = MakeContext();
            ctx2.Players[5].IsActive        = true;
            ctx2.Players[5].Position        = new Vec2(100f, 340f);
            ctx2.Players[5].TargetPosition  = new Vec2(900f, 340f);
            ctx2.Players[5].BaseSpeed       = 8f;
            ctx2.Players[5].Stamina         = 1.0f;
            ctx2.Players[5].StaminaAttribute = 0.1f; // low attribute
            ctx2.Players[5].Action          = PlayerAction.Pressing;

            for (int i = 0; i < 100; i++)
            {
                MovementSystem.Tick(ctx1);
                MovementSystem.Tick(ctx2);
            }

            Assert(ctx1.Players[5].Stamina > ctx2.Players[5].Stamina,
                $"High StaminaAttribute should decay slower. " +
                $"High={ctx1.Players[5].Stamina:F3} Low={ctx2.Players[5].Stamina:F3}");
        }

        private static void Test_29_StaminaCurveFull()
        {
            float curve = MovementSystem.ComputeStaminaCurve(1.0f);
            AssertApprox(curve, 1.0f, 0.001f, "StaminaCurve at 1.0 should return 1.0");
        }

        private static void Test_30_StaminaCurveZero()
        {
            float curve = MovementSystem.ComputeStaminaCurve(0f);
            AssertApprox(curve, PhysicsConstants.STAMINA_SPEED_MIN_MULTIPLIER, 0.001f,
                "StaminaCurve at 0.0 should return STAMINA_SPEED_MIN_MULTIPLIER");
        }

        private static void Test_31_PositionClamped()
        {
            var ctx = MakeContext();
            ctx.Players[5].IsActive        = true;
            ctx.Players[5].Position        = new Vec2(1040f, 340f);
            ctx.Players[5].TargetPosition  = new Vec2(2000f, 340f); // way off pitch
            ctx.Players[5].BaseSpeed       = 8f;
            ctx.Players[5].Stamina         = 1.0f;
            ctx.Players[5].Action          = PlayerAction.Pressing;

            for (int i = 0; i < 20; i++)
                MovementSystem.Tick(ctx);

            Assert(ctx.Players[5].Position.X <= PhysicsConstants.PITCH_RIGHT,
                $"Player X should be clamped to pitch right. Got {ctx.Players[5].Position.X}");
        }

        private static void Test_32_InactiveNotUpdated()
        {
            var ctx = MakeContext();
            ctx.Players[5].IsActive        = false;
            ctx.Players[5].Position        = new Vec2(200f, 200f);
            ctx.Players[5].TargetPosition  = new Vec2(600f, 200f);
            ctx.Players[5].Stamina         = 1.0f;

            MovementSystem.Tick(ctx);

            AssertApprox(ctx.Players[5].Position.X, 200f, 0.001f,
                "Inactive player should not move");
            AssertApprox(ctx.Players[5].Stamina, 1.0f, 0.001f,
                "Inactive player stamina should not change");
        }

        private static void Test_33_VelocityAccelerates()
        {
            var ctx = MakeContext();
            ctx.Players[5].IsActive        = true;
            ctx.Players[5].Position        = new Vec2(100f, 340f);
            ctx.Players[5].TargetPosition  = new Vec2(900f, 340f);
            ctx.Players[5].BaseSpeed       = 8f;
            ctx.Players[5].Stamina         = 1.0f;
            ctx.Players[5].Velocity        = Vec2.Zero;
            ctx.Players[5].Action          = PlayerAction.Pressing;

            float prevSpeed = 0f;
            bool foundAcceleration = false;

            for (int i = 0; i < 10; i++)
            {
                MovementSystem.Tick(ctx);
                float speed = ctx.Players[5].Velocity.Length();
                if (speed > prevSpeed + 0.01f)
                {
                    foundAcceleration = true;
                    break;
                }
                prevSpeed = speed;
            }

            Assert(foundAcceleration, "Player velocity should increase gradually (acceleration), not instantly max");
        }

        private static void Test_34_Determinism()
        {
            // Run identical context twice, verify identical results
            var ctx1 = MakeContext();
            ctx1.Players[5].IsActive       = true;
            ctx1.Players[5].Position       = new Vec2(200f, 200f);
            ctx1.Players[5].TargetPosition = new Vec2(700f, 500f);
            ctx1.Players[5].BaseSpeed      = 7f;
            ctx1.Players[5].Stamina        = 0.8f;
            ctx1.Players[5].Action         = PlayerAction.Pressing;
            ctx1.Ball.Phase                = BallPhase.InFlight;
            ctx1.Ball.Position             = new Vec2(300f, 300f);
            ctx1.Ball.Velocity             = new Vec2(12f, 8f);
            ctx1.Ball.PassTargetId         = -1;

            var ctx2 = MakeContext();
            ctx2.Players[5].IsActive       = true;
            ctx2.Players[5].Position       = new Vec2(200f, 200f);
            ctx2.Players[5].TargetPosition = new Vec2(700f, 500f);
            ctx2.Players[5].BaseSpeed      = 7f;
            ctx2.Players[5].Stamina        = 0.8f;
            ctx2.Players[5].Action         = PlayerAction.Pressing;
            ctx2.Ball.Phase                = BallPhase.InFlight;
            ctx2.Ball.Position             = new Vec2(300f, 300f);
            ctx2.Ball.Velocity             = new Vec2(12f, 8f);
            ctx2.Ball.PassTargetId         = -1;

            for (int i = 0; i < 50; i++)
            {
                MovementSystem.Tick(ctx1);
                BallSystem.Tick(ctx1);
                MovementSystem.Tick(ctx2);
                BallSystem.Tick(ctx2);
            }

            AssertApprox(ctx1.Players[5].Position.X, ctx2.Players[5].Position.X, 0.001f,
                "Determinism: Player X should match");
            AssertApprox(ctx1.Players[5].Position.Y, ctx2.Players[5].Position.Y, 0.001f,
                "Determinism: Player Y should match");
            AssertApprox(ctx1.Ball.Position.X, ctx2.Ball.Position.X, 0.001f,
                "Determinism: Ball X should match");
            AssertApprox(ctx1.Players[5].Stamina, ctx2.Players[5].Stamina, 0.001f,
                "Determinism: Stamina should match");
        }
    }
}