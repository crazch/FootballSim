// =============================================================================
// Module:  StatAggregator.cs
// Path:    FootballSim/Engine/Stats/StatAggregator.cs
// Purpose:
//   Consumes ctx.EventsThisTick each tick and accumulates statistics into
//   PlayerMatchStats[22] and TeamMatchStats[2]. Also tracks per-tick distance
//   from player velocity magnitudes (physical stats that don't come from events).
//
//   Hard rules — StatAggregator NEVER:
//     ✗ Modifies ctx.Ball, ctx.Players positions/velocity
//     ✗ Calls BallSystem, PlayerAI, CollisionSystem, EventSystem
//     ✗ Uses any randomness (System.Random or otherwise)
//     ✗ Uses MathF.Lerp — always MathUtil.Lerp / MathUtil.Clamp01
//
//   StatAggregator ONLY:
//     ✓ Reads ctx.EventsThisTick (append-only list, cleared after this call)
//     ✓ Reads ctx.Players[i].Velocity.Length() for distance tracking
//     ✓ Reads ctx.Players[i].Stamina, IsSprinting for physical tracking
//     ✓ Writes to PlayerMatchStats[i] and TeamMatchStats[teamId]
//     ✓ At match end: calls FinaliseHypothesis() to compute HypothesisResult
//
// API:
//   StatAggregator.Initialise(MatchContext ctx) → void
//     Called once at match start. Allocates and resets all stat arrays.
//     Snapshots TacticsInput for both teams for HypothesisResult comparison.
//
//   StatAggregator.Consume(MatchContext ctx) → void
//     Called every tick after EventSystem.Tick() and before ReplayFrame record.
//     Processes EventsThisTick and updates physical stats.
//
//   StatAggregator.Finalise(MatchContext ctx) → void
//     Called once at match end (FullTime event detected or ctx.Phase==FullTime).
//     Computes PPDA, HypothesisResult, FinalStamina per player.
//
//   Properties:
//     PlayerStats   PlayerMatchStats[22]   — indexed by PlayerId
//     HomeStats     TeamMatchStats          — home team
//     AwayStats     TeamMatchStats          — away team
//
// Determinism guarantee:
//   No Random. No floating-point-order-dependent loops. Same EventsThisTick
//   list in same order always produces identical stat output. ✓
//
// Dependencies:
//   Engine/Models/MatchContext.cs
//   Engine/Models/MatchEvent.cs
//   Engine/Models/Enums.cs   (MatchEventType, MatchPhase)
//   Engine/Stats/PlayerMatchStats.cs
//   Engine/Stats/TeamMatchStats.cs
//   Engine/Systems/AIConstants.cs  (press collapse threshold)
//   Engine/MathUtil.cs
// =============================================================================

using System;
using System.Collections.Generic;
using FootballSim.Engine.Models;
using FootballSim.Engine.Systems;

namespace FootballSim.Engine.Stats
{
    public class StatAggregator
    {
        // ── DEBUG ─────────────────────────────────────────────────────────────

        /// <summary>
        /// When true, logs each event type processed with the player it attributed to.
        /// Very verbose — intended for single-match debugging only.
        /// </summary>
        public static bool DEBUG = false;

        // ── Public accessors ──────────────────────────────────────────────────

        /// <summary>
        /// Per-player match stats. Index = PlayerId (0–21).
        /// Valid after Initialise() is called.
        /// </summary>
        public PlayerMatchStats[] PlayerStats { get; private set; }

        /// <summary>Home team (TeamId=0) accumulated stats.</summary>
        public TeamMatchStats HomeStats { get; private set; }

        /// <summary>Away team (TeamId=1) accumulated stats.</summary>
        public TeamMatchStats AwayStats { get; private set; }

        // ── Private per-tick tracking ─────────────────────────────────────────

        // Minute-by-minute press attempt and success counts for HypothesisResult
        private int[] _homePressAttemptByMinute;   // length 90
        private int[] _homePressSuccessByMinute;
        private int[] _awayPressAttemptByMinute;
        private int[] _awayPressSuccessByMinute;

        // Snapshot of sprint flag from last tick per player — for SprintCount
        private bool[] _wasSprintingLastTick;

        // Intent snapshots captured at match start
        private float _homePressingIntent;
        private float _homePossessionIntent;
        private float _homeLineIntent;
        private float _homeTempoIntent;
        private float _homePassingDirectnessIntent;
        private float _homeAttackingWidthIntent;

        private float _awayPressingIntent;
        private float _awayPossessionIntent;
        private float _awayLineIntent;
        private float _awayTempoIntent;
        private float _awayPassingDirectnessIntent;
        private float _awayAttackingWidthIntent;

        // ── Initialise ────────────────────────────────────────────────────────

        /// <summary>
        /// Called once at match start. Allocates all arrays, resets counters,
        /// snapshots tactical intent for HypothesisResult.
        /// </summary>
        public void Initialise(MatchContext ctx)
        {
            // Allocate player stats
            PlayerStats = new PlayerMatchStats[22];
            for (int i = 0; i < 22; i++)
            {
                PlayerStats[i] = new PlayerMatchStats();
                PlayerStats[i].PlayerId    = ctx.Players[i].PlayerId;
                PlayerStats[i].TeamId      = ctx.Players[i].TeamId;
                PlayerStats[i].ShirtNumber = ctx.Players[i].ShirtNumber;

                // Name from TeamData.Players
                int slot = i <= 10 ? i : i - 11;
                var team = i <= 10 ? ctx.HomeTeam : ctx.AwayTeam;
                if (team.Players != null && slot < team.Players.Length)
                    PlayerStats[i].Name = team.Players[slot].Name ?? $"P{ctx.Players[i].ShirtNumber}";
                else
                    PlayerStats[i].Name = $"P{ctx.Players[i].ShirtNumber}";

                PlayerStats[i].Reset();
                PlayerStats[i].FinalStamina = ctx.Players[i].Stamina;
            }

            // Allocate team stats
            HomeStats = new TeamMatchStats { TeamId = 0, Name = ctx.HomeTeam.Name };
            AwayStats = new TeamMatchStats { TeamId = 1, Name = ctx.AwayTeam.Name };
            HomeStats.Reset();
            AwayStats.Reset();

            // Per-minute press tracking
            _homePressAttemptByMinute  = new int[90];
            _homePressSuccessByMinute  = new int[90];
            _awayPressAttemptByMinute  = new int[90];
            _awayPressSuccessByMinute  = new int[90];

            _wasSprintingLastTick = new bool[22];

            // Snapshot tactical intent for HypothesisResult
            _homePressingIntent          = ctx.HomeTeam.Tactics.PressingIntensity;
            _homePossessionIntent        = ctx.HomeTeam.Tactics.PossessionFocus;
            _homeLineIntent              = ctx.HomeTeam.Tactics.DefensiveLine;
            _homeTempoIntent             = ctx.HomeTeam.Tactics.Tempo;
            _homePassingDirectnessIntent = ctx.HomeTeam.Tactics.PassingDirectness;
            _homeAttackingWidthIntent    = ctx.HomeTeam.Tactics.AttackingWidth;

            _awayPressingIntent          = ctx.AwayTeam.Tactics.PressingIntensity;
            _awayPossessionIntent        = ctx.AwayTeam.Tactics.PossessionFocus;
            _awayLineIntent              = ctx.AwayTeam.Tactics.DefensiveLine;
            _awayTempoIntent             = ctx.AwayTeam.Tactics.Tempo;
            _awayPassingDirectnessIntent = ctx.AwayTeam.Tactics.PassingDirectness;
            _awayAttackingWidthIntent    = ctx.AwayTeam.Tactics.AttackingWidth;

            if (DEBUG)
                Console.WriteLine("[StatAggregator] Initialised. " +
                    $"Home intent: press={_homePressingIntent:F2} poss={_homePossessionIntent:F2} | " +
                    $"Away intent: press={_awayPressingIntent:F2} poss={_awayPossessionIntent:F2}");
        }

        // ── Per-Tick Consumption ──────────────────────────────────────────────

        /// <summary>
        /// Called every tick after EventSystem.Tick().
        /// Processes ctx.EventsThisTick and updates physical movement stats.
        /// Does NOT clear EventsThisTick — TickSystem does that.
        /// </summary>
        public void Consume(MatchContext ctx)
        {
            // ── 1. Process all events emitted this tick ────────────────────────
            foreach (MatchEvent ev in ctx.EventsThisTick)
                ProcessEvent(ev, ctx);

            // ── 2. Update physical stats from live player state ────────────────
            UpdatePhysicalStats(ctx);

            // ── 3. Track total ticks played ────────────────────────────────────
            if (ctx.Phase == MatchPhase.OpenPlay ||
                ctx.Phase == MatchPhase.Kickoff)
            {
                HomeStats.TotalTicksPlayed++;
                AwayStats.TotalTicksPlayed++;
            }

            // ── 4. Accumulate possession ticks ─────────────────────────────────
            // Mirrors BallSystem.UpdatePossessionCounters but tracked here for stats
            if (ctx.Ball.Phase == BallPhase.Owned && ctx.Ball.OwnerId >= 0)
            {
                int possTeam = ctx.Ball.OwnerId <= 10 ? 0 : 1;
                if (possTeam == 0) HomeStats.PossessionTicks++;
                else               AwayStats.PossessionTicks++;
            }

            // ── 5. Per-minute press success rate ───────────────────────────────
            UpdatePressRateByMinute(ctx);
        }

        // ── Event Switch ──────────────────────────────────────────────────────

        private void ProcessEvent(MatchEvent ev, MatchContext ctx)
        {
            if (DEBUG)
                Console.WriteLine($"[StatAggregator] Tick {ctx.Tick}: " +
                    $"Processing {ev.Type} P{ev.PrimaryPlayerId} team={ev.TeamId}");

            // Guard out-of-range player IDs before array access
            bool primaryValid   = ev.PrimaryPlayerId   >= 0 && ev.PrimaryPlayerId   < 22;
            bool secondaryValid = ev.SecondaryPlayerId >= 0 && ev.SecondaryPlayerId < 22;

            TeamMatchStats teamStats = ev.TeamId == 0 ? HomeStats : AwayStats;
            TeamMatchStats oppStats  = ev.TeamId == 0 ? AwayStats : HomeStats;

            switch (ev.Type)
            {
                // ── Goals & Shots ─────────────────────────────────────────────

                case MatchEventType.Goal:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].Goals++;
                        PlayerStats[ev.PrimaryPlayerId].ShotsOnTarget++;
                        PlayerStats[ev.PrimaryPlayerId].ShotsAttempted++;
                        PlayerStats[ev.PrimaryPlayerId].XG += ev.ExtraFloat;
                    }
                    teamStats.GoalsScored++;
                    teamStats.ShotsTotal++;
                    teamStats.ShotsOnTarget++;
                    teamStats.XG += ev.ExtraFloat;
                    oppStats.GoalsConceded++;
                    oppStats.XGA += ev.ExtraFloat;
                    break;

                case MatchEventType.OwnGoal:
                    if (primaryValid) PlayerStats[ev.PrimaryPlayerId].OwnGoals++;
                    // Own goal counts as goal for opponent
                    oppStats.GoalsScored++;
                    teamStats.GoalsConceded++;
                    break;

                case MatchEventType.Assist:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].Assists++;
                        PlayerStats[ev.PrimaryPlayerId].XA += ev.ExtraFloat;
                    }
                    break;

                case MatchEventType.ShotOnTarget:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].ShotsOnTarget++;
                        PlayerStats[ev.PrimaryPlayerId].ShotsAttempted++;
                        PlayerStats[ev.PrimaryPlayerId].XG += ev.ExtraFloat;
                    }
                    teamStats.ShotsTotal++;
                    teamStats.ShotsOnTarget++;
                    teamStats.XG += ev.ExtraFloat;
                    oppStats.XGA += ev.ExtraFloat;
                    // GK faced a shot
                    if (secondaryValid && IsGK(ctx.Players[ev.SecondaryPlayerId].Role))
                    {
                        PlayerStats[ev.SecondaryPlayerId].ShotsFaced++;
                        PlayerStats[ev.SecondaryPlayerId].XGSaved += ev.ExtraFloat;
                    }
                    break;

                case MatchEventType.ShotOffTarget:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].ShotsOffTarget++;
                        PlayerStats[ev.PrimaryPlayerId].ShotsAttempted++;
                        PlayerStats[ev.PrimaryPlayerId].XG += ev.ExtraFloat;
                    }
                    teamStats.ShotsTotal++;
                    teamStats.ShotsOffTarget++;
                    teamStats.XG += ev.ExtraFloat;
                    break;

                case MatchEventType.ShotBlocked:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].ShotsBlocked++;
                        PlayerStats[ev.PrimaryPlayerId].ShotsAttempted++;
                        PlayerStats[ev.PrimaryPlayerId].XG += ev.ExtraFloat;
                    }
                    if (secondaryValid) PlayerStats[ev.SecondaryPlayerId].Blocks++;
                    teamStats.ShotsTotal++;
                    teamStats.ShotsBlocked++;
                    teamStats.XG += ev.ExtraFloat;
                    oppStats.Blocks++;
                    break;

                // ── Saves ─────────────────────────────────────────────────────

                case MatchEventType.Save:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].Saves++;
                        PlayerStats[ev.PrimaryPlayerId].ShotsFaced++;
                        PlayerStats[ev.PrimaryPlayerId].XGSaved += ev.ExtraFloat;
                        // ExtraBool = true → caught (no rebound), false → parried
                    }
                    break;

                case MatchEventType.ClaimedCross:
                    if (primaryValid) PlayerStats[ev.PrimaryPlayerId].Saves++;
                    break;

                // ── Passes ────────────────────────────────────────────────────

                case MatchEventType.PassCompleted:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].PassesAttempted++;
                        PlayerStats[ev.PrimaryPlayerId].PassesCompleted++;
                        if (ev.ExtraBool) // progressive
                            PlayerStats[ev.PrimaryPlayerId].ProgressivePasses++;
                    }
                    teamStats.PassesAttempted++;
                    teamStats.PassesCompleted++;
                    if (ev.ExtraBool) teamStats.ProgressivePasses++;
                    break;

                case MatchEventType.PassIntercepted:
                    if (primaryValid)
                    {
                        // primary = interceptor
                        PlayerStats[ev.PrimaryPlayerId].Interceptions++;
                    }
                    if (secondaryValid)
                    {
                        // secondary = original passer
                        PlayerStats[ev.SecondaryPlayerId].PassesAttempted++;
                        PlayerStats[ev.SecondaryPlayerId].PassesIntercepted++;
                    }
                    // teamStats = defending team (ev.TeamId = interceptor's team)
                    teamStats.Interceptions++;
                    oppStats.PassesAttempted++;
                    break;

                case MatchEventType.PassOutOfPlay:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].PassesAttempted++;
                        PlayerStats[ev.PrimaryPlayerId].PassesOutOfPlay++;
                    }
                    teamStats.PassesAttempted++;
                    break;

                case MatchEventType.KeyPass:
                    if (primaryValid) PlayerStats[ev.PrimaryPlayerId].KeyPasses++;
                    teamStats.KeyPasses++;
                    break;

                case MatchEventType.LongBallAttempt:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].LongBallsAttempted++;
                        // ExtraBool = true if it reached a teammate
                        if (ev.ExtraBool) PlayerStats[ev.PrimaryPlayerId].LongBallsCompleted++;
                    }
                    teamStats.LongBallsAttempted++;
                    if (ev.ExtraBool) teamStats.LongBallsCompleted++;
                    break;

                // ── Dribbles ──────────────────────────────────────────────────

                case MatchEventType.DribbleSuccess:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].DribblesAttempted++;
                        PlayerStats[ev.PrimaryPlayerId].DribblesSuccessful++;
                    }
                    break;

                case MatchEventType.DribbleFailed:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].DribblesAttempted++;
                        PlayerStats[ev.PrimaryPlayerId].TimesDispossessed++;
                    }
                    break;

                // ── Tackles ───────────────────────────────────────────────────

                case MatchEventType.TackleSuccess:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].TacklesAttempted++;
                        PlayerStats[ev.PrimaryPlayerId].TacklesWon++;
                    }
                    teamStats.TacklesAttempted++;
                    teamStats.TacklesWon++;
                    break;

                case MatchEventType.TaclkeFoul:
                    if (primaryValid) PlayerStats[ev.PrimaryPlayerId].TacklesAttempted++;
                    if (secondaryValid) PlayerStats[ev.SecondaryPlayerId].FoulsDrawn++;
                    teamStats.TacklesAttempted++;
                    break;

                // ── Pressures ─────────────────────────────────────────────────

                case MatchEventType.PressureSuccess:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].PressuresAttempted++;
                        PlayerStats[ev.PrimaryPlayerId].PressuresSuccessful++;
                    }
                    teamStats.PressuresAttempted++;
                    teamStats.PressuresSuccessful++;
                    // Per-minute press tracking
                    IncrementPressSuccess(ev.TeamId, ev.MatchMinute);
                    break;

                case MatchEventType.PressureFailed:
                    if (primaryValid) PlayerStats[ev.PrimaryPlayerId].PressuresAttempted++;
                    teamStats.PressuresAttempted++;
                    IncrementPressAttempt(ev.TeamId, ev.MatchMinute);
                    break;

                // ── Set Pieces ────────────────────────────────────────────────

                case MatchEventType.CornerKick:
                    teamStats.CornersWon++;
                    oppStats.CornersAgainst++;
                    break;

                case MatchEventType.FreeKick:
                    teamStats.FreeKicksWon++;
                    oppStats.FreeKicksAgainst++;
                    break;

                case MatchEventType.PenaltyAwarded:
                    teamStats.PenaltiesAwarded++;
                    oppStats.PenaltiesAgainst++;
                    break;

                // ── Fouls and Discipline ──────────────────────────────────────

                case MatchEventType.Foul:
                    if (primaryValid) PlayerStats[ev.PrimaryPlayerId].FoulsCommitted++;
                    if (secondaryValid) PlayerStats[ev.SecondaryPlayerId].FoulsDrawn++;
                    // teamStats = team benefiting (fouled team)
                    teamStats.FoulsDrawn++;
                    oppStats.FoulsCommitted++;
                    break;

                case MatchEventType.YellowCard:
                    if (primaryValid) PlayerStats[ev.PrimaryPlayerId].YellowCards++;
                    // team of the carded player
                    int ycTeam = primaryValid ? ctx.Players[ev.PrimaryPlayerId].TeamId : ev.TeamId;
                    (ycTeam == 0 ? HomeStats : AwayStats).YellowCards++;
                    break;

                case MatchEventType.RedCard:
                    if (primaryValid) PlayerStats[ev.PrimaryPlayerId].RedCards++;
                    int rcTeam = primaryValid ? ctx.Players[ev.PrimaryPlayerId].TeamId : ev.TeamId;
                    (rcTeam == 0 ? HomeStats : AwayStats).RedCards++;
                    break;

                case MatchEventType.Offside:
                    if (primaryValid) PlayerStats[ev.PrimaryPlayerId].Offsides++;
                    break;

                // ── Crosses ───────────────────────────────────────────────────

                case MatchEventType.CrossAttempt:
                    if (primaryValid) PlayerStats[ev.PrimaryPlayerId].CrossesAttempted++;
                    teamStats.PassesAttempted++; // crosses count as pass attempts
                    break;

                // ── Headers ───────────────────────────────────────────────────

                case MatchEventType.HeaderAttempt:
                    if (primaryValid)
                    {
                        PlayerStats[ev.PrimaryPlayerId].AerialDuelsTotal++;
                        if (ev.ExtraBool) PlayerStats[ev.PrimaryPlayerId].AerialDuelsWon++;
                    }
                    if (secondaryValid)
                    {
                        PlayerStats[ev.SecondaryPlayerId].AerialDuelsTotal++;
                        if (!ev.ExtraBool) PlayerStats[ev.SecondaryPlayerId].AerialDuelsWon++;
                    }
                    break;

                // ── Timing events (no stats to accumulate) ────────────────────

                case MatchEventType.HalfTime:
                case MatchEventType.FullTime:
                case MatchEventType.Kickoff:
                case MatchEventType.ThrowIn:
                case MatchEventType.GoalKick:
                case MatchEventType.PossessionWon:
                case MatchEventType.PossessionLost:
                    break; // no per-player stat; possession% from tick counters

                // Default: unknown or future event type — silently ignore
                default:
                    break;
            }
        }

        // ── Physical Stats Update ─────────────────────────────────────────────

        private void UpdatePhysicalStats(MatchContext ctx)
        {
            for (int i = 0; i < 22; i++)
            {
                if (!ctx.Players[i].IsActive) continue;

                float dist = ctx.Players[i].Velocity.Length(); // units per tick

                PlayerStats[i].DistanceCovered += dist;

                TeamMatchStats ts = ctx.Players[i].TeamId == 0 ? HomeStats : AwayStats;
                ts.TotalDistanceCovered += dist;

                bool sprinting = ctx.Players[i].IsSprinting;
                if (sprinting)
                {
                    PlayerStats[i].SprintDistance += dist;
                    ts.TotalSprintDistance += dist;

                    // Count sprint burst (false → true transition)
                    if (!_wasSprintingLastTick[i])
                        PlayerStats[i].SprintCount++;
                }

                _wasSprintingLastTick[i] = sprinting;

                // Always keep FinalStamina up to date
                PlayerStats[i].FinalStamina = ctx.Players[i].Stamina;
            }
        }

        // ── Press Rate Tracking ───────────────────────────────────────────────

        private void UpdatePressRateByMinute(MatchContext ctx)
        {
            int minute = (int)MathUtil.Clamp(ctx.MatchMinute, 0, 89);

            // Snapshot press attempt counts into the minute bucket
            // (PressureAttempted events are logged in ProcessEvent — this just computes rate)
            if (HomeStats.PressuresAttempted > 0)
            {
                HomeStats.PressSuccessRateByMinute[minute] =
                    _homePressSuccessByMinute[minute] /
                    (float)MathF.Max(1f, _homePressAttemptByMinute[minute]);
            }
            if (AwayStats.PressuresAttempted > 0)
            {
                AwayStats.PressSuccessRateByMinute[minute] =
                    _awayPressSuccessByMinute[minute] /
                    (float)MathF.Max(1f, _awayPressAttemptByMinute[minute]);
            }
        }

        private void IncrementPressAttempt(int teamId, int minute)
        {
            int m = (int)MathUtil.Clamp(minute, 0, 89);
            if (teamId == 0) _homePressAttemptByMinute[m]++;
            else             _awayPressAttemptByMinute[m]++;
        }

        private void IncrementPressSuccess(int teamId, int minute)
        {
            int m = (int)MathUtil.Clamp(minute, 0, 89);
            if (teamId == 0) { _homePressAttemptByMinute[m]++; _homePressSuccessByMinute[m]++; }
            else             { _awayPressAttemptByMinute[m]++; _awayPressSuccessByMinute[m]++; }
        }

        // ── Finalise ──────────────────────────────────────────────────────────

        /// <summary>
        /// Called once at match end. Computes derived stats (PPDA, pass accuracy ratios)
        /// and builds HypothesisResult for both teams.
        /// Does not mutate MatchContext.
        /// </summary>
        public void Finalise(MatchContext ctx)
        {
            // PPDA = opponent passes completed / (tackles + interceptions + pressures)
            // Home PPDA: how many away passes per defensive action by home team
            int homeDefActions = HomeStats.TacklesWon + HomeStats.Interceptions +
                                 HomeStats.PressuresSuccessful;
            HomeStats.PPDA = homeDefActions > 0
                ? AwayStats.PassesCompleted / (float)homeDefActions
                : 99f;

            int awayDefActions = AwayStats.TacklesWon + AwayStats.Interceptions +
                                 AwayStats.PressuresSuccessful;
            AwayStats.PPDA = awayDefActions > 0
                ? HomeStats.PassesCompleted / (float)awayDefActions
                : 99f;

            // GK goals conceded
            for (int i = 0; i < 22; i++)
            {
                if (!IsGK(ctx.Players[i].Role)) continue;
                int gkTeam = ctx.Players[i].TeamId;
                PlayerStats[i].GoalsConceded =
                    gkTeam == 0 ? HomeStats.GoalsConceded : AwayStats.GoalsConceded;
            }

            // Build HypothesisResult
            HomeStats.Hypothesis = FinaliseHypothesis(
                ctx, teamId: 0,
                pressingIntent:  _homePressingIntent,
                possessionIntent: _homePossessionIntent,
                lineIntent:      _homeLineIntent,
                tempoIntent:     _homeTempoIntent,
                passingIntent:   _homePassingDirectnessIntent,
                widthIntent:     _homeAttackingWidthIntent,
                myStats:         HomeStats,
                oppStats:        AwayStats
            );

            AwayStats.Hypothesis = FinaliseHypothesis(
                ctx, teamId: 1,
                pressingIntent:  _awayPressingIntent,
                possessionIntent: _awayPossessionIntent,
                lineIntent:      _awayLineIntent,
                tempoIntent:     _awayTempoIntent,
                passingIntent:   _awayPassingDirectnessIntent,
                widthIntent:     _awayAttackingWidthIntent,
                myStats:         AwayStats,
                oppStats:        HomeStats
            );

            if (DEBUG)
                Console.WriteLine(
                    $"[StatAggregator] Finalised. Home xG={HomeStats.XG:F2} xGA={HomeStats.XGA:F2} " +
                    $"PPDA={HomeStats.PPDA:F1} | Away xG={AwayStats.XG:F2} xGA={AwayStats.XGA:F2} " +
                    $"PPDA={AwayStats.PPDA:F1}");
        }

        // ── Hypothesis Builder ────────────────────────────────────────────────

        private HypothesisResult FinaliseHypothesis(
            MatchContext ctx,
            int teamId,
            float pressingIntent,
            float possessionIntent,
            float lineIntent,
            float tempoIntent,
            float passingIntent,
            float widthIntent,
            TeamMatchStats myStats,
            TeamMatchStats oppStats)
        {
            var h = new HypothesisResult();
            h.Intent = teamId == 0 ? ctx.HomeTeam.Tactics : ctx.AwayTeam.Tactics;

            // ── Pressing dimension ────────────────────────────────────────────
            // Actual = pressures_successful / pressures_attempted (clamped)
            float actualPressSuccess = myStats.PressuresAttempted > 0
                ? MathUtil.Clamp01(myStats.PressuresSuccessful / (float)myStats.PressuresAttempted)
                : 0f;

            // Find press collapse minute
            int collapseMinute = -1;
            int collapsePlayer = -1;
            float collapseThreshold = pressingIntent * 0.5f; // if rate drops below 50% of intent

            for (int m = 0; m < 90; m++)
            {
                if (myStats.PressSuccessRateByMinute[m] < collapseThreshold &&
                    m > 10) // ignore early match variance
                {
                    collapseMinute = m;
                    break;
                }
            }

            // Find lowest-stamina midfielder at collapse minute (approximate)
            if (collapseMinute >= 0)
            {
                int collapseTickApprox = collapseMinute * 600;
                float lowestStamina = float.MaxValue;
                for (int i = 0; i < 22; i++)
                {
                    if (ctx.Players[i].TeamId != teamId) continue;
                    if (!IsMidfielder(ctx.Players[i].Role)) continue;
                    if (PlayerStats[i].FinalStamina < lowestStamina)
                    {
                        lowestStamina  = PlayerStats[i].FinalStamina;
                        collapsePlayer = i;
                    }
                }
            }

            string pressCollapseReason = collapseMinute >= 0 && collapsePlayer >= 0
                ? $"Press collapsed at minute {collapseMinute} — " +
                  $"P{ctx.Players[collapsePlayer].ShirtNumber} " +
                  $"(stamina {PlayerStats[collapsePlayer].FinalStamina:F2}) " +
                  $"stopped tracking runners."
                : actualPressSuccess >= pressingIntent * 0.85f
                    ? "Press held throughout the match."
                    : "Press was underperformed — squad lacked the stamina to sustain intensity.";

            h.Pressing = new HypothesisDimension
            {
                Label       = "Pressing",
                IntentScore = pressingIntent,
                ActualScore = actualPressSuccess,
                GapReason   = pressCollapseReason,
            };

            h.PressCollapseMinute   = collapseMinute;
            h.PressCollapsePlayerId = collapsePlayer;

            // ── Possession dimension ──────────────────────────────────────────
            float actualPossession = MathUtil.Clamp01(myStats.PossessionPercent);

            string possReason;
            if (actualPossession < possessionIntent * 0.85f)
            {
                float oppTempo = (teamId == 0 ? ctx.AwayTeam : ctx.HomeTeam).Tactics.Tempo;
                possReason = oppTempo > 0.7f
                    ? $"Opponent's high tempo ({oppTempo:F0}×) disrupted recycling pattern."
                    : $"Possession intent {possessionIntent:P0} underperformed — " +
                      $"actual {actualPossession:P0}. " +
                      $"Pass accuracy was {myStats.PassAccuracy:P0}.";
            }
            else
            {
                possReason = "Possession target achieved.";
            }

            h.Possession = new HypothesisDimension
            {
                Label       = "Possession",
                IntentScore = possessionIntent,
                ActualScore = actualPossession,
                GapReason   = possReason,
            };

            // ── High Line dimension ────────────────────────────────────────────
            // Approximate actual line height by average defender Y position vs intent
            float avgDefY = ComputeAvgDefenderY(ctx, teamId);
            // Normalise to [0,1]: 0 = very deep, 1 = very high
            float pitchH = Systems.PhysicsConstants.PITCH_HEIGHT;
            float actualLine = teamId == 0
                ? MathUtil.Clamp01(1f - avgDefY / pitchH)
                : MathUtil.Clamp01(avgDefY / pitchH);

            h.HighLine = new HypothesisDimension
            {
                Label       = "Defensive Line",
                IntentScore = lineIntent,
                ActualScore = actualLine,
                GapReason   = MathUtil.Approximately(actualLine, lineIntent, 0.15f)
                    ? "Defensive line held as intended."
                    : $"Line intent {lineIntent:F2} vs actual {actualLine:F2}. " +
                      "Defenders may have been dragged deeper by pacy opponents.",
            };

            // ── Passing style dimension ────────────────────────────────────────
            float actualDirectness = myStats.LongBallsAttempted > 0 && myStats.PassesAttempted > 0
                ? MathUtil.Clamp01(myStats.LongBallsAttempted / (float)myStats.PassesAttempted * 5f)
                : 0f;  // scale: 20% long balls = score of 1.0

            h.PassingStyle = new HypothesisDimension
            {
                Label       = "Passing Directness",
                IntentScore = passingIntent,
                ActualScore = actualDirectness,
                GapReason   = "Passing directness derived from long ball ratio.",
            };

            // ── Attacking width dimension ──────────────────────────────────────
            // Approximate from cross attempts
            float actualWidth = myStats.PassesAttempted > 0
                ? MathUtil.Clamp01(myStats.TotalDistanceCovered > 0
                    ? (myStats.CornersWon + (float)myStats.ShotsTotal) /
                      MathF.Max(1f, myStats.PassesAttempted) * 10f
                    : 0f)
                : 0f;

            h.AttackingWidth = new HypothesisDimension
            {
                Label       = "Attacking Width",
                IntentScore = widthIntent,
                ActualScore = actualWidth,
                GapReason   = "Width measured from wide actions (crosses, wide shots).",
            };

            // ── Tempo dimension ────────────────────────────────────────────────
            // Approximate from pass tempo: passes per possession tick
            float actualTempo = myStats.PossessionTicks > 0
                ? MathUtil.Clamp01(myStats.PassesAttempted /
                                   (float)myStats.PossessionTicks * 15f)
                : 0f;  // scale: 1 pass per 15 possession ticks = score of 1.0

            h.Tempo = new HypothesisDimension
            {
                Label       = "Tempo",
                IntentScore = tempoIntent,
                ActualScore = actualTempo,
                GapReason   = "Tempo measured from pass rate during possession.",
            };

            // ── Shape held dimension ───────────────────────────────────────────
            // Simplified: high discipline (low FreedomLevel) → shape held well
            float freedomLevel = (teamId == 0 ? ctx.HomeTeam : ctx.AwayTeam).Tactics.FreedomLevel;
            float shapeScore   = MathUtil.Clamp01(1f - freedomLevel * 0.5f);

            h.ShapeHeld = new HypothesisDimension
            {
                Label       = "Shape Discipline",
                IntentScore = 1f - freedomLevel,
                ActualScore = shapeScore,
                GapReason   = "Shape discipline estimated from FreedomLevel setting.",
            };

            // ── Overall execution ──────────────────────────────────────────────
            float totalExec =
                h.Pressing.ExecutionScore +
                h.Possession.ExecutionScore +
                h.HighLine.ExecutionScore +
                h.PassingStyle.ExecutionScore +
                h.AttackingWidth.ExecutionScore +
                h.Tempo.ExecutionScore +
                h.ShapeHeld.ExecutionScore;

            h.OverallExecutionScore = MathUtil.Clamp01(totalExec / 7f) * 100f;

            // ── Headline summary ───────────────────────────────────────────────
            h.HeadlineSummary = BuildHeadline(h, teamId == 0 ? "Home" : "Away");

            return h;
        }

        // ── Hypothesis helper methods ─────────────────────────────────────────

        private float ComputeAvgDefenderY(MatchContext ctx, int teamId)
        {
            float totalY = 0f;
            int count = 0;
            int start = teamId == 0 ? 0 : 11;
            int end   = teamId == 0 ? 11 : 22;

            for (int i = start; i < end; i++)
            {
                var role = ctx.Players[i].Role;
                if (role == PlayerRole.CB || role == PlayerRole.BPD ||
                    role == PlayerRole.RB || role == PlayerRole.LB)
                {
                    totalY += ctx.Players[i].Position.Y;
                    count++;
                }
            }
            return count > 0 ? totalY / count : Systems.PhysicsConstants.PITCH_HEIGHT * 0.5f;
        }

        private static string BuildHeadline(HypothesisResult h, string teamName)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{teamName}] Overall execution: {h.OverallExecutionScore:F0}/100");

            if (MathF.Abs(h.Pressing.ExecutionGap) > 0.15f)
                sb.AppendLine($"• Pressing: intent {h.Pressing.IntentScore:P0} → actual {h.Pressing.ActualScore:P0}. {h.Pressing.GapReason}");

            if (MathF.Abs(h.Possession.ExecutionGap) > 0.10f)
                sb.AppendLine($"• Possession: intent {h.Possession.IntentScore:P0} → actual {h.Possession.ActualScore:P0}. {h.Possession.GapReason}");

            if (h.PressCollapseMinute >= 0)
                sb.AppendLine($"• Press collapse detected at minute {h.PressCollapseMinute}.");

            return sb.ToString().TrimEnd();
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static bool IsGK(PlayerRole role)
            => role == PlayerRole.GK || role == PlayerRole.SK;

        private static bool IsMidfielder(PlayerRole role)
            => role == PlayerRole.CM  || role == PlayerRole.CDM ||
               role == PlayerRole.BBM || role == PlayerRole.DLP ||
               role == PlayerRole.AM;

        private static int MathUtil_Clamp(int v, int lo, int hi)
            => v < lo ? lo : v > hi ? hi : v;
    }
}