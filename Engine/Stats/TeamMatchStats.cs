// =============================================================================
// Module:  TeamMatchStats.cs
// Path:    FootballSim/Engine/Stats/TeamMatchStats.cs
// Purpose:
//   Team-level accumulated statistics for one match, plus the HypothesisResult
//   that compares tactical intent (TacticsInput) against what actually happened.
//
//   StatAggregator writes to this each tick and at match end.
//   PostMatch UI reads TeamMatchStats for the team summary panel.
//   HypothesisResult is the signature feature — it explains the gap between
//   what the user intended tactically and what the simulation produced.
//
//   Two separate structs:
//     TeamMatchStats    — raw stat counters, aggregated from events
//     HypothesisResult  — post-match tactical analysis, computed at match end
//
// HypothesisResult design (from design doc):
//   "Pressing intent: 85% → Actual press success: 61%
//    Gap reason: CM stamina dropped below effective threshold at minute 58.
//    Possession intent: 70% → Actual possession: 52%
//    Gap reason: Away team tempo was too high for your recycling pattern."
//
//   Each dimension has:
//     IntentScore    float 0–1   (from TacticsInput slider)
//     ActualScore    float 0–1   (computed from match events/stats)
//     ExecutionGap   float       (IntentScore - ActualScore)
//     GapReason      string      (pre-built explanation, one sentence)
//
// Dependencies:
//   Engine/Tactics/TacticsInput.cs   (IntentScore fields sourced from here)
// =============================================================================

using FootballSim.Engine.Tactics;

namespace FootballSim.Engine.Stats
{
    // =========================================================================
    // HYPOTHESIS RESULT — tactical intent vs actual execution
    // =========================================================================

    /// <summary>
    /// One dimension of tactical hypothesis analysis.
    /// IntentScore = what the user set (slider). ActualScore = what happened.
    /// </summary>
    public struct HypothesisDimension
    {
        /// <summary>Label shown in PostMatch UI. E.g. "Pressing", "Possession", "High Line".</summary>
        public string Label;

        /// <summary>
        /// What the user intended (from TacticsInput slider, 0–1).
        /// Displayed as percentage: "Pressing intent: 85%"
        /// </summary>
        public float IntentScore;

        /// <summary>
        /// What actually happened in the match (0–1).
        /// Computed from match events and stats.
        /// Displayed as percentage: "Actual press success: 61%"
        /// </summary>
        public float ActualScore;

        /// <summary>
        /// IntentScore - ActualScore. Positive = underperformed vs intent.
        /// Used to sort dimensions by biggest gap for PostMatch focus.
        /// </summary>
        public float ExecutionGap => IntentScore - ActualScore;

        /// <summary>
        /// Pre-built one-sentence explanation of the gap.
        /// Built by StatAggregator.FinaliseHypothesis() at match end.
        /// E.g. "CM stamina dropped below effective threshold at minute 58."
        /// </summary>
        public string GapReason;

        /// <summary>Overall execution score 0–1 for this dimension. 1.0 = perfect execution.</summary>
        public float ExecutionScore => 1f - System.MathF.Abs(ExecutionGap);
    }

    /// <summary>
    /// Complete post-match hypothesis analysis for one team.
    /// Computed once at match end by StatAggregator.FinaliseHypothesis().
    /// </summary>
    public class HypothesisResult
    {
        /// <summary>The TacticsInput snapshot captured at match start (intent).</summary>
        public TacticsInput Intent;

        // ── Dimensions (one per major tactical parameter) ────────────────────

        public HypothesisDimension Pressing;
        public HypothesisDimension Possession;
        public HypothesisDimension HighLine;
        public HypothesisDimension PressCollapse;   // when press broke down
        public HypothesisDimension PassingStyle;    // short vs long intent
        public HypothesisDimension AttackingWidth;
        public HypothesisDimension Tempo;
        public HypothesisDimension ShapeHeld;        // % of match in formation

        // ── Headline summary ──────────────────────────────────────────────────

        /// <summary>
        /// 0–100 overall execution score. How well the squad ran the system.
        /// Average of ExecutionScore across all dimensions.
        /// "Overall execution: 73/100"
        /// </summary>
        public float OverallExecutionScore;

        /// <summary>
        /// Pre-built headline paragraph for PostMatch UI.
        /// E.g. "You set a high press. Your squad achieved 61% of press target.
        ///       Press collapsed at minute 58 due to CM stamina. Possession fell 18%
        ///       below intent as a result."
        /// </summary>
        public string HeadlineSummary;

        // ── Press collapse tracking ───────────────────────────────────────────

        /// <summary>Match minute when press success rate dropped below 50% of intent. -1 = never collapsed.</summary>
        public int PressCollapseMinute;

        /// <summary>PlayerId of the player whose stamina was lowest when press collapsed. -1 = not tracked.</summary>
        public int PressCollapsePlayerId;
    }

    // =========================================================================
    // TEAM MATCH STATS
    // =========================================================================

    /// <summary>
    /// Team-level accumulated statistics for one match.
    /// Raw counts — percentages computed at read time in PostMatch UI.
    /// </summary>
    public class TeamMatchStats
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>0 = home, 1 = away.</summary>
        public int TeamId;

        /// <summary>Team display name. Set at match start.</summary>
        public string Name;

        // ── Score ─────────────────────────────────────────────────────────────

        /// <summary>Goals scored this match (not own goals).</summary>
        public int GoalsScored;

        /// <summary>Goals conceded (including own goals by this team).</summary>
        public int GoalsConceded;

        // ── Possession ────────────────────────────────────────────────────────

        /// <summary>
        /// Ticks this team had possession (ball.OwnerId belongs to this team).
        /// Divide by total ticks played to get possession%.
        /// Mirrors MatchContext.HomePossessionTicks / AwayPossessionTicks.
        /// </summary>
        public int PossessionTicks;

        /// <summary>Total ticks played (increases every tick of OpenPlay).</summary>
        public int TotalTicksPlayed;

        /// <summary>Computed at read time: PossessionTicks / (float)TotalTicksPlayed.</summary>
        public float PossessionPercent =>
            TotalTicksPlayed > 0 ? PossessionTicks / (float)TotalTicksPlayed : 0f;

        // ── Shots ─────────────────────────────────────────────────────────────

        public int ShotsTotal;
        public int ShotsOnTarget;
        public int ShotsOffTarget;
        public int ShotsBlocked;

        /// <summary>Total expected goals accumulated from all shots.</summary>
        public float XG;

        /// <summary>Total expected goals conceded (from opponent shots).</summary>
        public float XGA;

        // ── Passes ────────────────────────────────────────────────────────────

        public int PassesAttempted;
        public int PassesCompleted;
        public int ProgressivePasses;
        public int KeyPasses;
        public int LongBallsAttempted;
        public int LongBallsCompleted;

        /// <summary>Computed at read time: PassesCompleted / (float)PassesAttempted.</summary>
        public float PassAccuracy =>
            PassesAttempted > 0 ? PassesCompleted / (float)PassesAttempted : 0f;

        // ── Pressing ──────────────────────────────────────────────────────────

        /// <summary>
        /// PPDA — Passes Per Defensive Action.
        /// Lower = more intense press. Typical Gegenpress: 5–7. Low block: 12+.
        /// Formula: OpponentPassesCompleted / (TacklesWon + Interceptions + PressuresSuccessful)
        /// </summary>
        public float PPDA;

        public int PressuresAttempted;
        public int PressuresSuccessful;

        // ── Defending ─────────────────────────────────────────────────────────

        public int TacklesAttempted;
        public int TacklesWon;
        public int Interceptions;
        public int Clearances;
        public int Blocks;

        // ── Set Pieces ────────────────────────────────────────────────────────

        public int CornersWon;
        public int CornersAgainst;
        public int FreeKicksWon;
        public int FreeKicksAgainst;
        public int PenaltiesAwarded;
        public int PenaltiesAgainst;

        // ── Fouls and Discipline ──────────────────────────────────────────────

        public int FoulsCommitted;
        public int FoulsDrawn;
        public int YellowCards;
        public int RedCards;

        // ── Physical ──────────────────────────────────────────────────────────

        /// <summary>Total distance covered by all players combined. Pitch units.</summary>
        public float TotalDistanceCovered;

        /// <summary>Total sprint distance across all players.</summary>
        public float TotalSprintDistance;

        // ── Press collapse tracking ───────────────────────────────────────────

        /// <summary>
        /// Minute-by-minute press success rate (index = minute, value = pressSuccessful/pressAttempted).
        /// Used by HypothesisResult to identify when press collapsed.
        /// Length = 90 (one entry per match minute).
        /// </summary>
        public float[] PressSuccessRateByMinute;

        // ── Hypothesis ────────────────────────────────────────────────────────

        /// <summary>
        /// Post-match tactical analysis. Computed by StatAggregator.FinaliseHypothesis().
        /// Null until match completes.
        /// </summary>
        public HypothesisResult Hypothesis;

        // ── Reset ─────────────────────────────────────────────────────────────

        /// <summary>Resets all counters. Called at match start by StatAggregator.</summary>
        public void Reset()
        {
            GoalsScored = GoalsConceded = 0;
            PossessionTicks = TotalTicksPlayed = 0;
            ShotsTotal = ShotsOnTarget = ShotsOffTarget = ShotsBlocked = 0;
            XG = XGA = 0f;
            PassesAttempted = PassesCompleted = ProgressivePasses = KeyPasses = 0;
            LongBallsAttempted = LongBallsCompleted = 0;
            PPDA = 0f;
            PressuresAttempted = PressuresSuccessful = 0;
            TacklesAttempted = TacklesWon = Interceptions = Clearances = Blocks = 0;
            CornersWon = CornersAgainst = FreeKicksWon = FreeKicksAgainst = 0;
            PenaltiesAwarded = PenaltiesAgainst = 0;
            FoulsCommitted = FoulsDrawn = YellowCards = RedCards = 0;
            TotalDistanceCovered = TotalSprintDistance = 0f;
            PressSuccessRateByMinute = new float[90];
            Hypothesis = null;
        }
    }
}