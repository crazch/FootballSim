// =============================================================================
// Module:  MatchContext.cs
// Path:    FootballSim/Engine/Models/MatchContext.cs
// Purpose:
//   The single shared state object passed to every system on every tick.
//   Acts as the "world" — holds all mutable runtime data for the current tick.
//   Systems READ from it, WRITE to it, then TickSystem records a ReplayFrame.
//
//   This is NOT a snapshot. It is the live, mutating state.
//   ReplayFrame.cs captures an immutable copy of what matters for playback.
//
// API (fields — no methods, pure data):
//   Tick            int             Current tick index (0–54000)
//   MatchSecond     float           Tick × 0.1 seconds
//   MatchMinute     int             Floor(MatchSecond / 60)
//   Phase           MatchPhase      Current flow state (OpenPlay, SetPiece, etc.)
//   Ball            BallState       Live ball state — written by BallSystem
//   Players         PlayerState[22] All 22 players — written by movement/AI systems
//   HomeTeam        TeamData        Static — read only, set at match start
//   AwayTeam        TeamData        Static — read only, set at match start
//   EventsThisTick  List<MatchEvent> Events emitted this tick — cleared each tick
//   HomePossessionTicks  int        Running count — home team ticks with possession
//   AwayPossessionTicks  int        Running count — away team ticks with possession
//   HomeScore       int             Current score
//   AwayScore       int             Current score
//   RandomSeed      int             Seed used this tick for deterministic debug replay
//
// Dependencies:
//   Engine/Models/PlayerState.cs
//   Engine/Models/BallState.cs
//   Engine/Models/MatchEvent.cs
//   Engine/Models/TeamData.cs
//   Engine/Models/Enums.cs (MatchPhase)
//
// Notes:
//   • Players[0..10] = home team. Players[11..21] = away team.
//     Index = PlayerId. Always consistent — never shuffled.
//   • EventsThisTick is cleared by TickSystem at the start of each tick.
//     EventSystem appends to it. ReplayFrame copies it at end of tick.
//   • RandomSeed stored per tick allows exact deterministic replay for debugging:
//     re-run the same match with same seed → identical outcome.
//   • HomePossessionTicks / AwayPossessionTicks feed into TeamMatchStats.Possession%.
//     Incremented by BallSystem each tick based on BallState.OwnerId.
// =============================================================================

using System;
using System.Collections.Generic;
using FootballSim.Engine.Systems;

namespace FootballSim.Engine.Models
{
    /// <summary>
    /// Live shared state for the current tick. Passed to every system.
    /// Mutated in-place — not copied per system. Order of system execution matters.
    /// See TickSystem for execution order.
    /// </summary>
    public class MatchContext
    {
        // ── Time ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Current tick index. 0 = match start. Max ~54000 for 90 minutes.
        /// Incremented by TickSystem at end of each tick.
        /// </summary>
        public int Tick;

        /// <summary>
        /// Match time in seconds. = Tick × 0.1.
        /// Pre-calculated each tick to avoid repeated multiplication in systems.
        /// </summary>
        public float MatchSecond;

        /// <summary>
        /// Match minute for display. = (int)(MatchSecond / 60).
        /// Pre-calculated each tick.
        /// </summary>
        public int MatchMinute;

        // ── Flow ──────────────────────────────────────────────────────────────

        /// <summary>
        /// High-level match flow state. TickSystem checks this each tick
        /// to decide which systems to run (e.g. SetPiece pauses most systems).
        /// </summary>
        public MatchPhase Phase;

        // ── Live State ────────────────────────────────────────────────────────

        /// <summary>
        /// Live ball state. Written exclusively by BallSystem.
        /// All other systems are read-only consumers of BallState.
        /// </summary>
        public BallState Ball;

        /// <summary>
        /// All 22 player states. Index = PlayerId.
        /// Players[0..10] = home. Players[11..21] = away.
        /// Written by MovementSystem (positions), PlayerAI (actions/decisions),
        /// BallSystem (HasBall flag), CollisionSystem (tackle outcomes).
        /// </summary>
        public PlayerState[] Players;

        // ── Static Team Data (read-only during match) ─────────────────────────

        /// <summary>
        /// Home team static data (squad attributes, formation, tactics).
        /// Set at match start. Systems read from this — never write.
        /// </summary>
        public TeamData HomeTeam;

        /// <summary>
        /// Away team static data. Set at match start. Read-only.
        /// </summary>
        public TeamData AwayTeam;

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>
        /// Events emitted during the current tick.
        /// Cleared by TickSystem at start of each tick.
        /// EventSystem appends to this list.
        /// ReplayFrame captures a copy at end of tick.
        /// </summary>
        public List<MatchEvent> EventsThisTick;

        // ── Score ─────────────────────────────────────────────────────────────

        /// <summary>Home team goals. Incremented by EventSystem on Goal event (TeamId == 0).</summary>
        public int HomeScore;

        /// <summary>Away team goals. Incremented by EventSystem on Goal event (TeamId == 1).</summary>
        public int AwayScore;

        /// <summary>
        /// Which team kicks off next. 0 = home, 1 = away.
        /// After goal, conceding team kicks off.
        /// </summary>
        public int KickoffTeam = 0;

        // ── Possession Counters ───────────────────────────────────────────────

        /// <summary>
        /// Running count of ticks where home team had possession (BallState.OwnerId 0–10).
        /// Divided by total ticks at match end → possession percentage.
        /// </summary>
        public int HomePossessionTicks;

        /// <summary>Same for away team.</summary>
        public int AwayPossessionTicks;

        // ── Determinism ───────────────────────────────────────────────────────

        /// <summary>
        /// Random seed used for probability rolls this tick.
        /// Stored so that a specific tick can be replayed identically for debugging.
        /// EngineDebugLogger can log: "Tick 7205 seed 48291 — Player 4 PASS decision"
        /// </summary>
        public int RandomSeed;

        /// <summary>
        /// Seeded random instance. Initialised from RandomSeed in MatchContext constructor.
        /// CollisionSystem is the sole consumer — all probability draws go through
        /// CollisionSystem.NextDouble(ctx) which calls ctx.Random.NextDouble().
        /// NEVER construct a new Random() inside any system — always use this instance.
        /// Same seed → identical match replay.
        /// </summary>
        public Random Random;

        // ── Collision result bus ──────────────────────────────────────────────

        /// <summary>
        /// Written by CollisionSystem after each contest resolution.
        /// Read by EventSystem in the same tick to emit the correct MatchEvent.
        /// Reset to CollisionResultType.None at the start of CollisionSystem.Tick().
        /// Only the highest-impact result per tick is stored
        /// (Goal > Tackle > Intercept > LooseClaim).
        /// </summary>
        public CollisionResult LastCollisionResult;

        // ── Constructor ───────────────────────────────────────────────────────

        /// <summary>
        /// Initialise MatchContext at match start.
        /// Called once by MatchEngine before the tick loop begins.
        /// </summary>
        public MatchContext(TeamData homeTeam, TeamData awayTeam)
        {
            HomeTeam = homeTeam;
            AwayTeam = awayTeam;
            Players = new PlayerState[22];
            EventsThisTick = new List<MatchEvent>(8); // typical: 0–3 events per tick
            Phase = MatchPhase.PreKickoff;
            Tick = 0;
            MatchSecond = 0f;
            MatchMinute = 0;
            HomeScore = 0;
            AwayScore = 0;
            HomePossessionTicks = 0;
            AwayPossessionTicks = 0;
            Random = new Random(RandomSeed); // seeded for deterministic replay
        }

        // ── Helpers (minimal — context is mostly data) ────────────────────────

        /// <summary>
        /// Returns the TeamData for the team currently in possession.
        /// Returns HomeTeam if nobody has possession (ball LOOSE or IN_FLIGHT).
        /// </summary>
        public TeamData GetPossessingTeam()
        {
            if (Ball.OwnerId >= 0 && Ball.OwnerId <= 10) return HomeTeam;
            if (Ball.OwnerId >= 11 && Ball.OwnerId <= 21) return AwayTeam;
            return HomeTeam; // default — caller should check Ball.Phase first
        }

        /// <summary>
        /// Returns all 11 PlayerState entries for one team side.
        /// Home: indices 0–10. Away: indices 11–21.
        /// Allocates a small array — use sparingly in the tick loop.
        /// </summary>
        public PlayerState[] GetTeamPlayers(int teamId)
        {
            int start = teamId == 0 ? 0 : 11;
            var result = new PlayerState[11];
            for (int i = 0; i < 11; i++)
                result[i] = Players[start + i];
            return result;
        }
    }
}