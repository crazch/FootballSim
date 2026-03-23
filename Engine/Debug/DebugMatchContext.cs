// =============================================================================
// Module:  DebugMatchContext.cs
// Path:    FootballSim/Engine/Debug/DebugMatchContext.cs
//
// Purpose:
//   Builds a MatchContext directly — bypassing FormationLoader, FormationRegistry,
//   and the 11-slot validation — for small debug scenarios (1v1, 2v2, 3v3).
//
//   PRODUCTION CODE IS UNTOUCHED.
//   This file only adds a new construction path. MatchEngine.Simulate() still
//   requires full 11v11 TeamData. These helpers are called only from
//   ScenarioRunner.cs and Tests/.
//
//   Why it is safe:
//   • All 22 PlayerState slots exist (Players[22] is always allocated by MatchContext).
//     Unused slots have IsActive = false — every system already skips inactive players.
//   • BallState, Random, scores, phase are initialised identically to a real match.
//   • Godot rendering: inactive player dots already hide themselves (IsActive == false
//     check in PlayerDot.gd). No Godot changes needed.
//
//   Key design decisions:
//   • Players are placed at absolute pitch coordinates (no normalised anchors).
//     FormationData.anchorY flip for away team is applied manually per slot.
//   • TacticsInput uses DebugTactics.Balanced() — neutral sliders so no single
//     tactic dominates and hides the bug you are looking for.
//   • ctx.DebugMode = true suppresses the 11-player assertion in any system that
//     checks it (currently only FormationLoader — the engine itself does not care).
//
// Usage:
//   var ctx = DebugMatchContext.TwoVTwo();
//   for (int t = 0; t < 300; t++) DebugTickRunner.Tick(ctx);
//   DebugAssert.ShotWasAttempted(ctx);
//
// =============================================================================

using System;
using FootballSim.Engine.Models;
using FootballSim.Engine.Systems;
using FootballSim.Engine.Tactics;

namespace FootballSim.Engine.Debug
{
    /// <summary>
    /// Builds pre-configured MatchContext objects for debug scenarios.
    /// Never called from production paths. Use ScenarioRunner or Tests/ only.
    /// </summary>
    public static class DebugMatchContext
    {
        // =====================================================================
        // SCENARIO: 1 v 1 — Attacker at penalty spot vs lone GK
        // Tests: ScoreShoot, LaunchShot, ShotOnTarget, GK save pipeline
        // =====================================================================

        /// <summary>
        /// Home attacker (ST) at the penalty spot.
        /// Away GK on goal line.
        /// Ball owned by the attacker.
        /// No defenders — only tests: does the attacker decide to shoot?
        /// Does the GK save correctly? Is the goal credited to the right team?
        /// </summary>
        public static MatchContext OneVOne(int seed = 42)
        {
            var ctx = BuildEmpty(seed);

            // ── Home team: 1 striker ──────────────────────────────────────────
            // Penalty spot = X:525, Y:515 (110 units from away goal line at Y=680)
            SetPlayer(ctx, index: 0, teamId: 0,
                role:     PlayerRole.ST,
                shirt:    9,
                pos:      new Vec2(525f, 515f),
                anchor:   new Vec2(525f, 515f),
                speed:    0.75f, shooting: 0.78f, passing: 0.55f,
                dribbling: 0.65f, defending: 0.20f,
                reactions: 0.70f, stamina: 1.0f, ego: 0.55f);

            // ── Away team: 1 GK ───────────────────────────────────────────────
            SetPlayer(ctx, index: 11, teamId: 1,
                role:     PlayerRole.GK,
                shirt:    1,
                pos:      new Vec2(525f, 660f),
                anchor:   new Vec2(525f, 660f),
                speed:    0.55f, shooting: 0.10f, passing: 0.50f,
                dribbling: 0.20f, defending: 0.70f,
                reactions: 0.78f, stamina: 1.0f, ego: 0.10f);

            // ── Ball: owned by the striker ────────────────────────────────────
            ctx.Ball.Phase   = BallPhase.Owned;
            ctx.Ball.OwnerId = 0;
            ctx.Ball.Position = new Vec2(525f, 515f);
            ctx.Players[0].HasBall = true;

            // ── Tactics: neutral ──────────────────────────────────────────────
            ctx.HomeTeam = BuildDebugTeamData(teamId: 0, "Debug Home 1v1",
                DebugTactics.Attacking());
            ctx.AwayTeam  = BuildDebugTeamData(teamId: 1, "Debug Away 1v1",
                DebugTactics.Balanced());

            ctx.DebugMode             = true;
            ctx.HomeActiveCount       = 1;
            ctx.AwayActiveCount       = 1;
            ctx.KickoffTeam           = 0;
            ctx.Phase                 = MatchPhase.OpenPlay;

            return ctx;
        }

        // =====================================================================
        // SCENARIO: 2 v 2 — Attacker + GK vs Defender + GK
        // Tests: ScoreDribble, tackle pipeline, ScorePass (to GK),
        //        movement steering, stamina drain on tackle
        // =====================================================================

        /// <summary>
        /// Home ST at centre-circle edge, ball at their feet.
        /// Home GK on their own goal line.
        /// Away CB 40 units ahead of the attacker (in their path).
        /// Away GK on their goal line.
        ///
        /// Primary question: does the attacker dribble past the CB or pass to the GK?
        /// Secondary: does the CB attempt a tackle with correct probability and cooldown?
        /// </summary>
        public static MatchContext TwoVTwo(int seed = 42)
        {
            var ctx = BuildEmpty(seed);

            // ── Home: Striker + GK ───────────────────────────────────────────
            // Striker: centre of pitch, ball at feet
            SetPlayer(ctx, index: 0, teamId: 0,
                role:     PlayerRole.ST,
                shirt:    9,
                pos:      new Vec2(525f, 340f),
                anchor:   new Vec2(525f, 340f),
                speed:    0.78f, shooting: 0.75f, passing: 0.60f,
                dribbling: 0.72f, defending: 0.25f,
                reactions: 0.70f, stamina: 1.0f, ego: 0.60f);

            // Home GK: own goal line (home defends top: Y=0)
            SetPlayer(ctx, index: 1, teamId: 0,
                role:     PlayerRole.GK,
                shirt:    1,
                pos:      new Vec2(525f, 20f),
                anchor:   new Vec2(525f, 20f),
                speed:    0.55f, shooting: 0.10f, passing: 0.55f,
                dribbling: 0.20f, defending: 0.72f,
                reactions: 0.76f, stamina: 1.0f, ego: 0.10f);

            // ── Away: CB + GK ─────────────────────────────────────────────────
            // CB: 40 units ahead of the striker (toward away goal at Y=680)
            SetPlayer(ctx, index: 11, teamId: 1,
                role:     PlayerRole.CB,
                shirt:    5,
                pos:      new Vec2(525f, 380f),    // 40 units south of striker
                anchor:   new Vec2(525f, 460f),    // anchor deeper in defensive half
                speed:    0.68f, shooting: 0.15f, passing: 0.55f,
                dribbling: 0.30f, defending: 0.78f,
                reactions: 0.68f, stamina: 1.0f, ego: 0.20f);

            // Away GK: away goal line (away defends bottom: Y=680)
            SetPlayer(ctx, index: 12, teamId: 1,
                role:     PlayerRole.GK,
                shirt:    1,
                pos:      new Vec2(525f, 660f),
                anchor:   new Vec2(525f, 660f),
                speed:    0.55f, shooting: 0.10f, passing: 0.50f,
                dribbling: 0.20f, defending: 0.70f,
                reactions: 0.78f, stamina: 1.0f, ego: 0.10f);

            // ── Ball: owned by home striker ───────────────────────────────────
            ctx.Ball.Phase    = BallPhase.Owned;
            ctx.Ball.OwnerId  = 0;
            ctx.Ball.Position = new Vec2(525f, 340f);
            ctx.Players[0].HasBall = true;

            // ── Tactics ───────────────────────────────────────────────────────
            ctx.HomeTeam = BuildDebugTeamData(teamId: 0, "Debug Home 2v2",
                DebugTactics.Attacking());
            ctx.AwayTeam  = BuildDebugTeamData(teamId: 1, "Debug Away 2v2",
                DebugTactics.Defending());

            ctx.DebugMode       = true;
            ctx.HomeActiveCount = 2;
            ctx.AwayActiveCount = 2;
            ctx.KickoffTeam     = 0;
            ctx.Phase           = MatchPhase.OpenPlay;

            return ctx;
        }

        // =====================================================================
        // SCENARIO: 3 v 3 — Striker + Midfielder + GK vs CB + CM + GK
        // Long pass DISABLED to test short-pass triangle realism
        // Tests: passing triangles, support runs, press vs mark-space decision,
        //        ScoreHold (should hold briefly when support run is developing)
        // =====================================================================

        /// <summary>
        /// Home: ST (ball) + CM (support) + GK
        /// Away: CB (marks ST) + CM (covers passing lane) + GK
        ///
        /// AIConstants.DISABLE_LONG_PASS is temporarily forced true for this
        /// scenario regardless of its value in AIConstants — set via ctx.ForceLongPassDisabled.
        ///
        /// Primary questions:
        ///   1. Does the ST pass to the CM (support run) rather than holding indefinitely?
        ///   2. Does the CM receive the ball and look to advance?
        ///   3. Does the away CM cover the passing lane or press the carrier?
        ///   4. Is 70%+ of play short/medium passes (< 350 units)?
        /// </summary>
        public static MatchContext ThreeVThreeShortPass(int seed = 42)
        {
            var ctx = BuildEmpty(seed);

            // ── Home team ─────────────────────────────────────────────────────
            // ST: centre-half, ball at feet
            SetPlayer(ctx, index: 0, teamId: 0,
                role:     PlayerRole.ST,
                shirt:    9,
                pos:      new Vec2(525f, 390f),
                anchor:   new Vec2(525f, 460f),
                speed:    0.75f, shooting: 0.72f, passing: 0.60f,
                dribbling: 0.65f, defending: 0.22f,
                reactions: 0.68f, stamina: 1.0f, ego: 0.55f);

            // CM: support position — left channel, slightly behind ST
            SetPlayer(ctx, index: 1, teamId: 0,
                role:     PlayerRole.CM,
                shirt:    8,
                pos:      new Vec2(390f, 420f),
                anchor:   new Vec2(390f, 380f),
                speed:    0.72f, shooting: 0.55f, passing: 0.74f,
                dribbling: 0.58f, defending: 0.55f,
                reactions: 0.70f, stamina: 1.0f, ego: 0.35f);

            // Home GK
            SetPlayer(ctx, index: 2, teamId: 0,
                role:     PlayerRole.GK,
                shirt:    1,
                pos:      new Vec2(525f, 20f),
                anchor:   new Vec2(525f, 20f),
                speed:    0.55f, shooting: 0.10f, passing: 0.55f,
                dribbling: 0.20f, defending: 0.72f,
                reactions: 0.76f, stamina: 1.0f, ego: 0.10f);

            // ── Away team ─────────────────────────────────────────────────────
            // CB: directly marking the ST, 35 units behind
            SetPlayer(ctx, index: 11, teamId: 1,
                role:     PlayerRole.CB,
                shirt:    4,
                pos:      new Vec2(525f, 425f),
                anchor:   new Vec2(525f, 480f),
                speed:    0.67f, shooting: 0.15f, passing: 0.55f,
                dribbling: 0.28f, defending: 0.80f,
                reactions: 0.66f, stamina: 1.0f, ego: 0.18f);

            // Away CM: covering the home CM's likely passing lane
            SetPlayer(ctx, index: 12, teamId: 1,
                role:     PlayerRole.CM,
                shirt:    6,
                pos:      new Vec2(400f, 450f),
                anchor:   new Vec2(400f, 420f),
                speed:    0.70f, shooting: 0.50f, passing: 0.65f,
                dribbling: 0.52f, defending: 0.62f,
                reactions: 0.68f, stamina: 1.0f, ego: 0.30f);

            // Away GK
            SetPlayer(ctx, index: 13, teamId: 1,
                role:     PlayerRole.GK,
                shirt:    1,
                pos:      new Vec2(525f, 660f),
                anchor:   new Vec2(525f, 660f),
                speed:    0.55f, shooting: 0.10f, passing: 0.50f,
                dribbling: 0.20f, defending: 0.70f,
                reactions: 0.78f, stamina: 1.0f, ego: 0.10f);

            // ── Ball: owned by home ST ────────────────────────────────────────
            ctx.Ball.Phase    = BallPhase.Owned;
            ctx.Ball.OwnerId  = 0;
            ctx.Ball.Position = new Vec2(525f, 390f);
            ctx.Players[0].HasBall = true;

            // ── Tactics ───────────────────────────────────────────────────────
            ctx.HomeTeam = BuildDebugTeamData(teamId: 0, "Debug Home 3v3",
                DebugTactics.ShortPassPossession());
            ctx.AwayTeam  = BuildDebugTeamData(teamId: 1, "Debug Away 3v3",
                DebugTactics.Balanced());

            ctx.DebugMode              = true;
            ctx.HomeActiveCount        = 3;
            ctx.AwayActiveCount        = 3;
            ctx.KickoffTeam            = 0;
            ctx.Phase                  = MatchPhase.OpenPlay;
            ctx.ForceLongPassDisabled  = true;  // isolate short-pass behaviour

            return ctx;
        }

        // =====================================================================
        // SCENARIO: 3 v 3 — Same as above but long pass ENABLED
        // Tests: progressive passing, DLP switching, lead-pass prediction
        // Only run after ThreeVThreeShortPass validates short-pass system
        // =====================================================================

        /// <summary>
        /// Same setup as ThreeVThreeShortPass but long passes are enabled.
        /// Home CM has high LongPassBias to encourage switches of play.
        /// </summary>
        public static MatchContext ThreeVThreeLongPass(int seed = 42)
        {
            var ctx = ThreeVThreeShortPass(seed);

            // Upgrade home CM to a DLP with high long-pass tendency
            SetPlayer(ctx, index: 1, teamId: 0,
                role:     PlayerRole.DLP,   // Deep-Lying Playmaker
                shirt:    8,
                pos:      new Vec2(390f, 420f),
                anchor:   new Vec2(390f, 380f),
                speed:    0.65f, shooting: 0.45f, passing: 0.88f,
                dribbling: 0.50f, defending: 0.58f,
                reactions: 0.72f, stamina: 1.0f, ego: 0.30f);

            ctx.ForceLongPassDisabled = false;   // enable long passing
            ctx.DebugMode             = true;

            return ctx;
        }

        // =====================================================================
        // PRIVATE BUILDERS
        // =====================================================================

        /// <summary>
        /// Creates an empty MatchContext with all 22 player slots inactive.
        /// Ball is centred, loose. Random seeded deterministically.
        /// </summary>
        private static MatchContext BuildEmpty(int seed)
        {
            // MatchContext must be constructible without TeamData for debug use.
            // We rely on MatchContext having a parameterless constructor or
            // a debug-friendly factory. Adjust to match your actual constructor.
            var ctx = new MatchContext
            {
                RandomSeed = seed,
                Tick       = 0,
                Phase      = MatchPhase.OpenPlay,
                HomeScore  = 0,
                AwayScore  = 0,
            };

            // Seed the shared random — same as MatchEngine does
            ctx.Random = new System.Random(seed);

            // Deactivate all 22 player slots — active slots are set below
            for (int i = 0; i < 22; i++)
            {
                ctx.Players[i] = new PlayerState
                {
                    PlayerId  = i,
                    TeamId    = i < 11 ? 0 : 1,
                    IsActive  = false,
                    Position  = new Vec2(PhysicsConstants.PITCH_WIDTH * 0.5f,
                                         PhysicsConstants.PITCH_HEIGHT * 0.5f),
                    TargetPosition = new Vec2(PhysicsConstants.PITCH_WIDTH * 0.5f,
                                              PhysicsConstants.PITCH_HEIGHT * 0.5f),
                };
            }

            // Ball: loose at centre, no owner
            ctx.Ball = new BallState
            {
                Phase        = BallPhase.Loose,
                OwnerId      = -1,
                LastTouchedBy = -1,
                Position     = new Vec2(PhysicsConstants.PITCH_WIDTH  * 0.5f,
                                         PhysicsConstants.PITCH_HEIGHT * 0.5f),
                Velocity     = Vec2.Zero,
                Height       = 0f,
            };

            return ctx;
        }

        /// <summary>
        /// Sets one player slot with all required fields.
        /// Reusable for any scenario. All attributes are 0.0–1.0.
        /// </summary>
        private static void SetPlayer(
            MatchContext ctx,
            int          index,
            int          teamId,
            PlayerRole   role,
            int          shirt,
            Vec2         pos,
            Vec2         anchor,
            float        speed,
            float        shooting,
            float        passing,
            float        dribbling,
            float        defending,
            float        reactions,
            float        stamina,
            float        ego,
            float        staminaAttr = 0.70f)
        {
            ctx.Players[index] = new PlayerState
            {
                PlayerId         = index,
                TeamId           = teamId,
                ShirtNumber      = shirt,
                Role             = role,
                IsActive         = true,
                Position         = pos,
                TargetPosition   = pos,
                FormationAnchor  = anchor,
                Velocity         = Vec2.Zero,
                Speed            = 0f,
                Stamina          = stamina,
                StaminaAttribute = staminaAttr,
                IsSprinting      = false,
                HasBall          = false,
                Action           = PlayerAction.WalkingToAnchor,
                DecisionCooldown = 0,
                BaseSpeed        = speed     * PhysicsConstants.PLAYER_SPRINT_SPEED,
                ShootingAbility  = shooting,
                PassingAbility   = passing,
                DribblingAbility = dribbling,
                DefendingAbility = defending,
                Reactions        = reactions,
                Ego              = ego,
            };
        }

        /// <summary>
        /// Builds a minimal TeamData for debug scenarios.
        /// Players[] array is empty — the engine reads PlayerState directly
        /// from ctx.Players, not from TeamData.Players, during a live tick.
        /// TeamData.Players is only used at match initialisation (MatchEngine.Simulate),
        /// which we bypass for debug scenarios.
        /// </summary>
        private static TeamData BuildDebugTeamData(int teamId, string name,
                                                     TacticsInput tactics)
        {
            return new TeamData
            {
                TeamId        = teamId,
                Name          = name,
                FormationName = "debug",     // not used — no FormationRegistry lookup
                Tactics       = tactics,
                Players       = Array.Empty<PlayerData>(),  // not used during live tick
            };
        }
    }

    // =========================================================================
    // DEBUG TACTICS — preset TacticsInput configurations for debug scenarios
    // =========================================================================

    /// <summary>
    /// Pre-built TacticsInput configurations for debug scenarios.
    /// Named by their purpose, not by a football style, so the intent is clear
    /// when reading ScenarioFactory code.
    /// </summary>
    public static class DebugTactics
    {
        /// <summary>
        /// All sliders at 0.5 — nothing dominates, good neutral baseline.
        /// Use when you want to observe raw system behaviour without tactical bias.
        /// </summary>
        public static TacticsInput Balanced() => new TacticsInput
        {
            PressingIntensity    = 0.5f,
            PressingTrigger      = 0.5f,
            PressCompactness     = 0.5f,
            DefensiveLine        = 0.5f,
            DefensiveWidth       = 0.5f,
            DefensiveAggression  = 0.5f,
            PossessionFocus      = 0.5f,
            BuildUpSpeed         = 0.5f,
            PassingDirectness    = 0.5f,
            AttackingWidth       = 0.5f,
            AttackingLine        = 0.5f,
            TransitionSpeed      = 0.5f,
            CrossingFrequency    = 0.5f,
            ShootingThreshold    = 0.5f,
            Tempo                = 0.5f,
            OutOfPossessionShape = 0.5f,
            InPossessionSpread   = 0.5f,
            FreedomLevel         = 0.5f,
            CounterAttackFocus   = 0.5f,
            OffsideTrapFrequency = 0.5f,
            PhysicalityBias      = 0.5f,
            SetPieceFocus        = 0.5f,
        };

        /// <summary>
        /// High shooting threshold, high freedom, fast build-up.
        /// Forces the attacker to take shots when in range — good for 1v1 and dribble tests.
        /// </summary>
        public static TacticsInput Attacking() => new TacticsInput
        {
            PressingIntensity    = 0.3f,
            PressingTrigger      = 0.3f,
            PressCompactness     = 0.4f,
            DefensiveLine        = 0.7f,
            DefensiveWidth       = 0.6f,
            DefensiveAggression  = 0.3f,
            PossessionFocus      = 0.3f,   // don't recycle — go forward
            BuildUpSpeed         = 0.8f,   // fast
            PassingDirectness    = 0.7f,   // direct
            AttackingWidth       = 0.7f,
            AttackingLine        = 0.8f,   // high line — encourage runs behind
            TransitionSpeed      = 0.8f,
            CrossingFrequency    = 0.5f,
            ShootingThreshold    = 0.2f,   // low threshold — shoot more often
            Tempo                = 0.75f,
            OutOfPossessionShape = 0.4f,
            InPossessionSpread   = 0.7f,
            FreedomLevel         = 0.7f,   // high freedom — instinct over role
            CounterAttackFocus   = 0.6f,
            OffsideTrapFrequency = 0.4f,
            PhysicalityBias      = 0.4f,
            SetPieceFocus        = 0.4f,
        };

        /// <summary>
        /// Low defensive line, high compactness, moderate press.
        /// Forces the CB to stay deep and defend — good for tackle tests.
        /// </summary>
        public static TacticsInput Defending() => new TacticsInput
        {
            PressingIntensity    = 0.4f,
            PressingTrigger      = 0.4f,
            PressCompactness     = 0.7f,
            DefensiveLine        = 0.2f,   // deep line
            DefensiveWidth       = 0.5f,
            DefensiveAggression  = 0.65f,  // commit to tackles
            PossessionFocus      = 0.6f,
            BuildUpSpeed         = 0.3f,
            PassingDirectness    = 0.4f,
            AttackingWidth       = 0.4f,
            AttackingLine        = 0.3f,
            TransitionSpeed      = 0.5f,
            CrossingFrequency    = 0.3f,
            ShootingThreshold    = 0.6f,
            Tempo                = 0.4f,
            OutOfPossessionShape = 0.75f,  // compact block
            InPossessionSpread   = 0.4f,
            FreedomLevel         = 0.3f,   // strict — role discipline over instinct
            CounterAttackFocus   = 0.3f,
            OffsideTrapFrequency = 0.2f,
            PhysicalityBias      = 0.6f,
            SetPieceFocus        = 0.5f,
        };

        /// <summary>
        /// High possession focus, short passing, low directness, moderate freedom.
        /// Forces tiki-taka style — good for testing passing triangles in 3v3.
        /// ForceLongPassDisabled on ctx should also be set true for this scenario.
        /// </summary>
        public static TacticsInput ShortPassPossession() => new TacticsInput
        {
            PressingIntensity    = 0.6f,
            PressingTrigger      = 0.6f,
            PressCompactness     = 0.55f,
            DefensiveLine        = 0.55f,
            DefensiveWidth       = 0.5f,
            DefensiveAggression  = 0.4f,
            PossessionFocus      = 0.85f,  // very high — recycle first
            BuildUpSpeed         = 0.4f,   // patient
            PassingDirectness    = 0.15f,  // short passes preferred
            AttackingWidth       = 0.6f,
            AttackingLine        = 0.5f,
            TransitionSpeed      = 0.45f,
            CrossingFrequency    = 0.3f,
            ShootingThreshold    = 0.55f,
            Tempo                = 0.5f,
            OutOfPossessionShape = 0.65f,
            InPossessionSpread   = 0.65f,  // spread out for passing options
            FreedomLevel         = 0.55f,
            CounterAttackFocus   = 0.3f,
            OffsideTrapFrequency = 0.35f,
            PhysicalityBias      = 0.35f,
            SetPieceFocus        = 0.4f,
        };
    }
}
