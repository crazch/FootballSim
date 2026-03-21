// =============================================================================
// Module:  MatchReplay.cs
// Path:    FootballSim/Engine/Core/MatchReplay.cs
// Purpose:
//   Container for the complete output of one simulated match.
//   Holds the ordered list of ReplayFrames, final match stats, and metadata.
//   MatchEngineBridge.cs receives a MatchReplay and exposes it to GDScript.
//
//   Also contains MatchEngine — the master orchestrator that runs the full
//   tick loop from seed to MatchReplay. This is the end-to-end entry point
//   that can be called from a console test runner without any Godot dependency.
//
// MatchReplay API (pure data):
//   Frames          List<ReplayFrame>     ordered frames, one per tick
//   HomeStats       TeamMatchStats        final team stats (home)
//   AwayStats       TeamMatchStats        final team stats (away)
//   PlayerStats     PlayerMatchStats[22]  final per-player stats
//   RandomSeed      int                   seed used — for exact deterministic replay
//   HomeTeamName    string
//   AwayTeamName    string
//   FinalHomeScore  int
//   FinalAwayScore  int
//   TotalTicks      int                   actual ticks simulated (may be < 54000 on error)
//
//   HighlightFrames → IEnumerable<ReplayFrame> where frame.Events != null
//
// MatchEngine API:
//   MatchEngine.Simulate(TeamData home, TeamData away, int seed) → MatchReplay
//     The single entry point. Runs the full match loop. Returns MatchReplay.
//     No Godot types. No renderer. Pure C#.
//     Can be called from Tests/ without any Godot dependency.
//
// Tick execution order (locked and enforced by MatchEngine):
//   1. ctx.EventsThisTick.Clear()
//   2. MovementSystem.Tick(ctx)
//   3. PlayerAI.Tick(ctx)
//   4. BallSystem.Tick(ctx)
//   5. CollisionSystem.Tick(ctx)
//   6. EventSystem.Tick(ctx)
//   7. aggregator.Consume(ctx)
//   8. ReplayFrame.Capture(ctx) → replay.AddFrame()
//   9. AdvanceTick(ctx)
//
// Determinism guarantee:
//   Given the same TeamData inputs and seed, Simulate() always produces
//   identical MatchReplay output. No System.DateTime, no Thread.Sleep,
//   no external state. ctx.Random drives all randomness.
//
// Dependencies:
//   Engine/Models/MatchContext.cs
//   Engine/Models/TeamData.cs
//   Engine/Models/Enums.cs
//   Engine/Systems/* (all systems)
//   Engine/Stats/StatAggregator.cs
//   Engine/Core/ReplayFrame.cs
//   Engine/Tactics/RoleRegistry.cs  (must be loaded before Simulate)
//   Engine/Tactics/FormationRegistry.cs
// =============================================================================

using System;
using System.Collections.Generic;
using FootballSim.Engine.Models;
using FootballSim.Engine.Systems;
using FootballSim.Engine.Stats;
using FootballSim.Engine.Tactics;

namespace FootballSim.Engine.Core
{
    // =========================================================================
    // MATCH REPLAY — the complete output of one simulated match
    // =========================================================================

    /// <summary>
    /// Complete, immutable output of one simulated match.
    /// Created by MatchEngine.Simulate() and handed to MatchEngineBridge.
    /// Pure data — no behaviour.
    /// </summary>
    public class MatchReplay
    {
        // ── Metadata ──────────────────────────────────────────────────────────

        /// <summary>The seed used to generate this replay. Identical seed → identical replay.</summary>
        public int RandomSeed;

        public string HomeTeamName;
        public string AwayTeamName;

        // ── Result ────────────────────────────────────────────────────────────

        public int FinalHomeScore;
        public int FinalAwayScore;

        /// <summary>Total ticks simulated. Should be 54000 for a 90-minute match.</summary>
        public int TotalTicks;

        // ── Frames ────────────────────────────────────────────────────────────

        /// <summary>
        /// Ordered list of ReplayFrames. One per tick.
        /// Index 0 = tick 0 (match start). Index 53999 = tick 53999 (full time).
        /// ReplayPlayer.gd reads from this list during playback.
        /// </summary>
        public List<ReplayFrame> Frames;

        // ── Stats ─────────────────────────────────────────────────────────────

        /// <summary>Final home team statistics. Valid after match ends.</summary>
        public TeamMatchStats HomeStats;

        /// <summary>Final away team statistics. Valid after match ends.</summary>
        public TeamMatchStats AwayStats;

        /// <summary>Per-player final statistics. Index = PlayerId (0–21).</summary>
        public PlayerMatchStats[] PlayerStats;

        // ── Convenience ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns only frames that contain at least one event.
        /// Used by highlights mode — filter to ~500 frames instead of 54,000.
        /// </summary>
        public IEnumerable<ReplayFrame> HighlightFrames
        {
            get
            {
                foreach (var frame in Frames)
                    if (frame.Events != null && frame.Events.Count > 0)
                        yield return frame;
            }
        }

        /// <summary>
        /// All MatchEvent records from the entire match, in tick order.
        /// Flattened from all frames. Useful for PostMatch analysis.
        /// </summary>
        public List<MatchEvent> AllEvents
        {
            get
            {
                var result = new List<MatchEvent>(256);
                foreach (var frame in Frames)
                    if (frame.Events != null)
                        result.AddRange(frame.Events);
                return result;
            }
        }

        // ── Internal: frame recording ─────────────────────────────────────────

        internal void AddFrame(ReplayFrame frame) => Frames.Add(frame);
    }

    // =========================================================================
    // MATCH ENGINE — runs the full tick loop, produces MatchReplay
    // =========================================================================

    /// <summary>
    /// Orchestrates the complete match simulation from seed to MatchReplay.
    /// No Godot dependency. Can run in a console test without any renderer.
    /// </summary>
    public static class MatchEngine
    {
        // ── DEBUG ─────────────────────────────────────────────────────────────

        /// <summary>
        /// When true, logs tick milestones (every 1000 ticks), score changes,
        /// and system errors to console. Useful for integration testing.
        /// </summary>
        public static bool DEBUG = false;

        // ── Simulation entry point ────────────────────────────────────────────

        /// <summary>
        /// Runs a complete match simulation. Returns MatchReplay.
        /// Preconditions:
        ///   • FormationRegistry.LoadAll() completed
        ///   • RoleRegistry.LoadAll() completed
        ///   • GamePlanRegistry.SeedDefaults() called
        ///   • homeTeam.Players and awayTeam.Players arrays populated (11 each)
        /// </summary>
        /// <param name="homeTeam">Home team data including squad and tactics.</param>
        /// <param name="awayTeam">Away team data including squad and tactics.</param>
        /// <param name="seed">Random seed. Same seed always produces identical output.</param>
        public static MatchReplay Simulate(TeamData homeTeam, TeamData awayTeam, int seed = 0)
        {
            if (DEBUG)
                Console.WriteLine(
                    $"[MatchEngine] Starting simulation. Seed={seed} " +
                    $"Home={homeTeam.Name} ({homeTeam.FormationName}) vs " +
                    $"Away={awayTeam.Name} ({awayTeam.FormationName})");

            // ── 1. Build MatchContext ──────────────────────────────────────────
            var ctx = new MatchContext(homeTeam, awayTeam);
            ctx.RandomSeed = seed;
            ctx.Random = new Random(seed);
            ctx.Phase = MatchPhase.Kickoff;

            // ── 2. Initialise all players from TeamData + FormationData ────────
            InitialisePlayers(ctx);

            // ── 3. Place ball at kickoff ───────────────────────────────────────
            SetupKickoffPositions(ctx, 0);

            // ── 4. Stagger player decision cooldowns ──────────────────────────
            PlayerAI.InitialiseMatch(ctx);

            // ── 5. Initialise stat aggregator ─────────────────────────────────
            var aggregator = new StatAggregator();
            aggregator.Initialise(ctx);

            // ── 6. Allocate replay ────────────────────────────────────────────
            var replay = new MatchReplay
            {
                RandomSeed = seed,
                HomeTeamName = homeTeam.Name,
                AwayTeamName = awayTeam.Name,
                Frames = new List<ReplayFrame>(EventSystem.FULL_TIME_TICK),
            };

            // ── 7. Main tick loop ─────────────────────────────────────────────
            while (ctx.Phase != MatchPhase.FullTime)
            {
                // Safety guard: never exceed FULL_TIME_TICK
                if (ctx.Tick > EventSystem.FULL_TIME_TICK + 100)
                {
                    if (DEBUG)
                        Console.WriteLine(
                            $"[MatchEngine] SAFETY STOP at tick {ctx.Tick} — FullTime not fired.");
                    break;
                }

                RunTick(ctx, aggregator);
                replay.AddFrame(ReplayFrame.Capture(ctx));
                AdvanceTick(ctx);
            }

            // ── 8. Finalise stats ──────────────────────────────────────────────
            aggregator.Finalise(ctx);

            replay.TotalTicks = ctx.Tick;
            replay.FinalHomeScore = ctx.HomeScore;
            replay.FinalAwayScore = ctx.AwayScore;
            replay.HomeStats = aggregator.HomeStats;
            replay.AwayStats = aggregator.AwayStats;
            replay.PlayerStats = aggregator.PlayerStats;

            if (DEBUG)
                Console.WriteLine(
                    $"[MatchEngine] Simulation complete. " +
                    $"{homeTeam.Name} {ctx.HomeScore}–{ctx.AwayScore} {awayTeam.Name} " +
                    $"| Ticks={ctx.Tick} Frames={replay.Frames.Count} " +
                    $"| Home xG={replay.HomeStats.XG:F2} Away xG={replay.AwayStats.XG:F2}");

            return replay;
        }

        // ── Single tick execution ─────────────────────────────────────────────

        /// <summary>
        /// Executes one tick in the locked system order:
        /// Clear → Move → AI → Ball → Collision → Event → Stats
        /// </summary>
        private static void RunTick(MatchContext ctx, StatAggregator aggregator)
        {
            // 0. Clear events from last tick
            ctx.EventsThisTick.Clear();

            // Skip all simulation systems during non-play phases
            if (ctx.Phase == MatchPhase.HalfTime)
            {
                HandleHalfTime(ctx);
                return;
            }

            if (ctx.Phase == MatchPhase.GoalScored)
            {
                HandleGoalScoredPause(ctx);
                return;
            }

            // 1. Movement — positions updated, stamina decayed/recovered
            MovementSystem.Tick(ctx);

            // 2. AI — decisions evaluated, ball launch methods called
            PlayerAI.Tick(ctx);

            // 3. Ball — physics, flight, ownership transfers, flag sync
            BallSystem.Tick(ctx);

            // 4. Collision — tackle/intercept/save contests → LastCollisionResult
            CollisionSystem.Tick(ctx);

            // 5. Event — reads LastCollisionResult → emits MatchEvents
            EventSystem.Tick(ctx);

            // 6. Stats — consumes EventsThisTick, updates counters
            aggregator.Consume(ctx);

            if (DEBUG && ctx.Tick % 1000 == 0)
                Console.WriteLine(
                    $"[MatchEngine] Tick {ctx.Tick} ({ctx.MatchMinute}') " +
                    $"Score: {ctx.HomeScore}–{ctx.AwayScore} " +
                    $"Phase={ctx.Phase} Ball={ctx.Ball.Phase}");
        }

        // ── Tick advance ──────────────────────────────────────────────────────

        private static void AdvanceTick(MatchContext ctx)
        {
            ctx.Tick++;
            ctx.MatchSecond = ctx.Tick * 0.1f;
            ctx.MatchMinute = (int)(ctx.MatchSecond / 60f);
        }

        // ── Phase handlers ────────────────────────────────────────────────────

        /// <summary>
        /// Handles the half-time interval. Restores stamina, transitions to second half.
        /// Half-time spans a fixed number of ticks (halftime pause = 10 ticks = 1 second sim).
        /// </summary>
        private static void HandleHalfTime(MatchContext ctx)
        {
            // Give a brief pause before resuming (10 ticks)
            if (!_halftimePauseStarted)
            {
                _halftimePauseStarted = true;
                _halftimePauseTick = ctx.Tick;
                if (DEBUG) Console.WriteLine($"[MatchEngine] Half-time at tick {ctx.Tick}.");
            }

            if (ctx.Tick - _halftimePauseTick >= HALFTIME_PAUSE_TICKS)
            {
                // Restore partial stamina
                for (int i = 0; i < 22; i++)
                {
                    if (!ctx.Players[i].IsActive) continue;
                    ctx.Players[i].Stamina = MathUtil.Clamp01(
                        ctx.Players[i].Stamina + PhysicsConstants.STAMINA_HALFTIME_RECOVERY);
                }

                // Flip attacking directions (home and away swap ends)
                FlipAttackingDirections(ctx);

                ctx.Phase = MatchPhase.Kickoff;
                _halftimePauseStarted = false;

                if (DEBUG) Console.WriteLine($"[MatchEngine] Second half starts at tick {ctx.Tick}.");
            }
        }

        // Pause state — module-level state for halftime (reset between matches)
        private static bool _halftimePauseStarted = false;
        private static int _halftimePauseTick = 0;
        private const int HALFTIME_PAUSE_TICKS = 10;

        /// <summary>
        /// Handles the brief pause after a goal before kickoff restarts.
        /// </summary>
        private static void HandleGoalScoredPause(MatchContext ctx)
        {
            if (!_goalPauseStarted)
            {
                _goalPauseStarted = true;
                _goalPauseTick = ctx.Tick;
                // Set all outfield players to Celebrating
                for (int i = 0; i < 22; i++)
                    if (ctx.Players[i].IsActive)
                        ctx.Players[i].Action = PlayerAction.Celebrating;
            }

            if (ctx.Tick - _goalPauseTick >= GOAL_PAUSE_TICKS)
            {
                // Reset to kickoff positions
                SetupKickoffPositions(ctx, ctx.KickoffTeam);
                ctx.Phase = MatchPhase.Kickoff;
                _goalPauseStarted = false;
            }
        }

        private static bool _goalPauseStarted = false;
        private static int _goalPauseTick = 0;
        private const int GOAL_PAUSE_TICKS = 20; // 2 seconds celebration

        // ── Initialisation helpers ────────────────────────────────────────────

        /// <summary>
        /// Populates all 22 PlayerState entries from TeamData and FormationData.
        /// Converts normalised formation anchors (0–1) to world coordinates.
        /// </summary>
        private static void InitialisePlayers(MatchContext ctx)
        {
            InitialiseTeam(ctx, ctx.HomeTeam, teamId: 0, globalOffset: 0, flipY: false);
            InitialiseTeam(ctx, ctx.AwayTeam, teamId: 1, globalOffset: 11, flipY: true);
        }

        private static void InitialiseTeam(MatchContext ctx, TeamData team,
                                            int teamId, int globalOffset, bool flipY)
        {
            FormationData formation;
            try
            {
                formation = FormationRegistry.Get(team.FormationName);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"[MatchEngine] Cannot load formation '{team.FormationName}' " +
                    $"for team '{team.Name}': {ex.Message}");
            }

            float pitchW = PhysicsConstants.PITCH_WIDTH;
            float pitchH = PhysicsConstants.PITCH_HEIGHT;

            for (int slot = 0; slot < 11; slot++)
            {
                int globalIndex = globalOffset + slot;
                FormationSlot fSlot = formation.Slots[slot];

                // Convert normalised anchor to world coordinates
                float anchorX = fSlot.Anchor.X * pitchW;
                float anchorY = flipY
                    ? (1f - fSlot.Anchor.Y) * pitchH  // away team flips Y
                    : fSlot.Anchor.Y * pitchH;

                var anchor = new Vec2(anchorX, anchorY);

                // Copy player attributes from TeamData
                PlayerData pd = team.Players[slot];

                ctx.Players[globalIndex] = new PlayerState
                {
                    PlayerId = globalIndex,
                    TeamId = teamId,
                    ShirtNumber = pd.ShirtNumber,
                    Role = fSlot.Role,
                    Position = anchor,
                    TargetPosition = anchor,
                    FormationAnchor = anchor,
                    Velocity = Vec2.Zero,
                    BaseSpeed = pd.BaseSpeed,
                    Stamina = 1.0f,
                    StaminaAttribute = pd.StaminaAttribute,
                    PassingAbility = pd.PassingAbility,
                    ShootingAbility = pd.ShootingAbility,
                    DribblingAbility = pd.DribblingAbility,
                    DefendingAbility = pd.DefendingAbility,
                    Reactions = pd.Reactions,
                    Ego = pd.Ego,
                    IsActive = true,
                    Action = PlayerAction.WalkingToAnchor,
                    DecisionCooldown = 0,
                    HasBall = false,
                    IsSprinting = false,
                    Speed = pd.BaseSpeed,
                };
            }
        }

        /// <summary>
        /// Places ball at centre spot, owned by home team's striker (slot 9 or 10).
        /// </summary>
        private static void InitialiseBall(MatchContext ctx)
        {
            float centreX = PhysicsConstants.PITCH_WIDTH * 0.5f;
            float centreY = PhysicsConstants.PITCH_HEIGHT * 0.5f;

            // Find the home striker (last slot, typically ST/PF)
            int kickerIndex = 9; // default slot 9 (first forward)
            for (int i = 0; i <= 10; i++)
            {
                var role = ctx.Players[i].Role;
                if (role == PlayerRole.ST || role == PlayerRole.PF || role == PlayerRole.CF)
                {
                    kickerIndex = i;
                    break;
                }
            }

            ctx.Ball = new BallState
            {
                Position = new Vec2(centreX, centreY),
                Velocity = Vec2.Zero,
                Phase = BallPhase.Owned,
                OwnerId = kickerIndex,
                LastTouchedBy = kickerIndex,
                PassTargetId = -1,
                IsShot = false,
                ShotOnTarget = false,
                Height = 0f,
                LooseTicks = 0,
                IsOutOfPlay = false,
                OutOfPlayCausedBy = -1,
            };

            ctx.Players[kickerIndex].HasBall = true;
        }

        /// <summary>
        /// Overrides player positions for legal kickoff.
        /// All players are clamped to their own half.
        /// The kickoff team's best forward is placed at the centre spot with the ball.
        /// A second player from the kickoff team is placed just behind the kicker.
        /// kickoffTeam: 0 = home kicks off, 1 = away kicks off.
        /// </summary>
        private static void SetupKickoffPositions(MatchContext ctx, int kickoffTeam)
        {
            float pitchW = PhysicsConstants.PITCH_WIDTH;
            float pitchH = PhysicsConstants.PITCH_HEIGHT;
            float centreX = pitchW * 0.5f;
            float centreY = pitchH * 0.5f;

            // ── 1. Clamp every player to their own half ───────────────────────────
            // Home attacks toward Y=680 (bottom), so home own half = top (Y < halfY).
            // Away attacks toward Y=0   (top),    so away own half = bottom (Y > halfY).
            for (int i = 0; i < 22; i++)
            {
                ref PlayerState p = ref ctx.Players[i];   // ref — writes go to actual array
                if (!p.IsActive) continue;

                float x = p.FormationAnchor.X;
                float y = p.FormationAnchor.Y;

                if (p.TeamId == 0)
                    y = Math.Min(y, centreY - 10f);   // home: clamp to Y <= 330
                else
                    y = Math.Max(y, centreY + 10f);   // away: clamp to Y >= 350

                var pos = new Vec2(x, y);
                p.Position = pos;
                p.TargetPosition = pos;
                p.Velocity = Vec2.Zero;
                p.HasBall = false;
            }

            // ── 2. Choose kicker from kickoff team ───────────────────────────────
            // Priority: ST → CF → PF → AM → CM → BBM → slot 9 fallback
            int start = kickoffTeam == 0 ? 0 : 11;
            int end = start + 11;
            int kicker = start + 9; // fallback

            PlayerRole[] kickerPriority =
            {
        PlayerRole.ST, PlayerRole.CF, PlayerRole.PF,
        PlayerRole.AM, PlayerRole.CM, PlayerRole.BBM
    };

            foreach (PlayerRole preferred in kickerPriority)
            {
                bool found = false;
                for (int i = start; i < end; i++)
                {
                    if (ctx.Players[i].Role == preferred && ctx.Players[i].IsActive)
                    {
                        kicker = i;
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }

            // ── 3. Choose second player — next best forward/mid, not the kicker ──
            int second = -1;
            foreach (PlayerRole preferred in kickerPriority)
            {
                for (int i = start; i < end; i++)
                {
                    if (i == kicker) continue;
                    if (ctx.Players[i].Role == preferred && ctx.Players[i].IsActive)
                    {
                        second = i;
                        break;
                    }
                }
                if (second >= 0) break;
            }

            // ── 4. Place kicker at exact centre spot ─────────────────────────────
            {
                ref PlayerState k = ref ctx.Players[kicker];
                var centre = new Vec2(centreX, centreY);
                k.Position = centre;
                k.TargetPosition = centre;
                k.Velocity = Vec2.Zero;
                k.HasBall = true;
            }

            // ── 5. Place second player just behind kicker (toward own goal) ───────
            if (second >= 0)
            {
                ref PlayerState s = ref ctx.Players[second];
                // Home attacks down → "behind" = lower Y (toward home goal at top)
                // Away attacks up   → "behind" = higher Y (toward away goal at bottom)
                float behindY = kickoffTeam == 0
                    ? centreY - 20f   // home: slightly above centre
                    : centreY + 20f;  // away: slightly below centre

                var behindPos = new Vec2(centreX, behindY);
                s.Position = behindPos;
                s.TargetPosition = behindPos;
                s.Velocity = Vec2.Zero;
                s.HasBall = false;
            }

            // ── 6. Place ball at centre spot owned by kicker ─────────────────────
            ctx.Ball = new BallState
            {
                Position = new Vec2(centreX, centreY),
                Velocity = Vec2.Zero,
                Phase = BallPhase.Owned,
                OwnerId = kicker,
                LastTouchedBy = kicker,
                PassTargetId = -1,
                IsShot = false,
                ShotOnTarget = false,
                Height = 0f,
                LooseTicks = 0,
                IsOutOfPlay = false,
                OutOfPlayCausedBy = -1,
            };
        }
        /// <summary>
        /// Flips FormationAnchor Y positions at half-time so each team defends the other end.
        /// Also flips current Position and TargetPosition so transition is smooth.
        /// </summary>
        private static void FlipAttackingDirections(MatchContext ctx)
        {
            float pitchH = PhysicsConstants.PITCH_HEIGHT;
            for (int i = 0; i < 22; i++)
            {
                if (!ctx.Players[i].IsActive) continue;

                // Flip anchor
                ctx.Players[i].FormationAnchor = new Vec2(
                    ctx.Players[i].FormationAnchor.X,
                    pitchH - ctx.Players[i].FormationAnchor.Y
                );

                // Teleport position and target to flipped anchor for clean half-time reset
                ctx.Players[i].Position = ctx.Players[i].FormationAnchor;
                ctx.Players[i].TargetPosition = ctx.Players[i].FormationAnchor;
                ctx.Players[i].Velocity = Vec2.Zero;
            }
        }
    }
}