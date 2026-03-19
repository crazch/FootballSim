// =============================================================================
// Module:  PlayerMatchStats.cs
// Path:    FootballSim/Engine/Stats/PlayerMatchStats.cs
// Purpose:
//   Accumulator for one player's statistics over the course of one match.
//   StatAggregator writes to this each tick by consuming ctx.EventsThisTick.
//   PostMatch UI reads from this to render the player stats table.
//   HypothesisResult reads from this to compute intent-vs-actual gaps.
//
//   Pure data container. No methods except Reset(). No logic.
//   All float stats that represent percentages are stored as raw counts here —
//   the percentage is computed at read time in PostMatch UI.
//   e.g. PassAccuracy% = PassesCompleted / (float)PassesAttempted
//
// Fields match real football statistics as used by Opta / StatsBomb:
//   Goals, Assists, Shots (on target, off target, blocked), xG, xA
//   Passes (attempted, completed, progressive, key), Pass accuracy
//   Dribbles (attempted, successful), Tackles (attempted, won, fouls),
//   Pressures (attempted, successful), Interceptions
//   Distance covered, Sprints, Fouls committed / drawn
//   Yellow cards, Red cards
//   Crosses (attempted, successful), Headers (won, lost)
//
// Dependencies: none
// =============================================================================

namespace FootballSim.Engine.Stats
{
    /// <summary>
    /// One player's accumulated match statistics.
    /// Written by StatAggregator.Consume(). Read by PostMatch UI.
    /// All counts are raw integers — percentages are computed at read time.
    /// </summary>
    public class PlayerMatchStats
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>Global player index 0–21. Matches MatchContext.Players[PlayerId].</summary>
        public int PlayerId;

        /// <summary>0 = home team, 1 = away team.</summary>
        public int TeamId;

        /// <summary>Shirt number for display. Set at match start from PlayerData.</summary>
        public int ShirtNumber;

        /// <summary>Player display name. Set at match start from PlayerData.</summary>
        public string Name;

        // ── Goals and Attacking ───────────────────────────────────────────────

        /// <summary>Goals scored (not own goals).</summary>
        public int Goals;

        /// <summary>Own goals conceded by this player.</summary>
        public int OwnGoals;

        /// <summary>Assists (pass leading directly to a goal).</summary>
        public int Assists;

        /// <summary>
        /// Expected goals accumulated across all shots taken this match.
        /// Sum of xG values from every ShotOnTarget/ShotOffTarget/Goal event.
        /// </summary>
        public float XG;

        /// <summary>
        /// Expected assists accumulated. Sum of xG from shots that followed a key pass by this player.
        /// </summary>
        public float XA;

        // ── Shots ─────────────────────────────────────────────────────────────

        /// <summary>Total shots attempted (on target + off target + blocked).</summary>
        public int ShotsAttempted;

        /// <summary>Shots on target (saved or scored). Includes goals.</summary>
        public int ShotsOnTarget;

        /// <summary>Shots that missed the target (wide or over).</summary>
        public int ShotsOffTarget;

        /// <summary>Shots blocked by an outfield player before reaching GK.</summary>
        public int ShotsBlocked;

        // ── Passes ────────────────────────────────────────────────────────────

        /// <summary>All pass attempts (completed + intercepted + out of play).</summary>
        public int PassesAttempted;

        /// <summary>Passes successfully received by a teammate.</summary>
        public int PassesCompleted;

        /// <summary>
        /// Passes that advance the ball at least 10 units toward opponent goal.
        /// Opta definition: a pass that moves the ball forward significantly.
        /// </summary>
        public int ProgressivePasses;

        /// <summary>Passes that directly create a shot opportunity (xG ≥ KEY_PASS_XG_THRESHOLD).</summary>
        public int KeyPasses;

        /// <summary>Passes intercepted or that went out of play.</summary>
        public int PassesIntercepted;

        /// <summary>Passes that went directly out of play (touch-line, byline).</summary>
        public int PassesOutOfPlay;

        /// <summary>Long balls attempted (distance ≥ LONG_BALL_DISTANCE threshold).</summary>
        public int LongBallsAttempted;

        /// <summary>Long balls that reached a teammate.</summary>
        public int LongBallsCompleted;

        // ── Dribbles ──────────────────────────────────────────────────────────

        /// <summary>Dribble attempts — player tried to beat a defender.</summary>
        public int DribblesAttempted;

        /// <summary>Successful dribbles — player beat the defender.</summary>
        public int DribblesSuccessful;

        /// <summary>Times player lost the ball while dribbling (dispossessed).</summary>
        public int TimesDispossessed;

        // ── Crosses ───────────────────────────────────────────────────────────

        /// <summary>Cross deliveries attempted from wide areas.</summary>
        public int CrossesAttempted;

        /// <summary>Crosses that reached a teammate in a dangerous position.</summary>
        public int CrossesSuccessful;

        // ── Aerial Duels ──────────────────────────────────────────────────────

        /// <summary>Total aerial duels contested (headers).</summary>
        public int AerialDuelsTotal;

        /// <summary>Aerial duels won.</summary>
        public int AerialDuelsWon;

        // ── Defending ─────────────────────────────────────────────────────────

        /// <summary>Tackle attempts initiated by this player.</summary>
        public int TacklesAttempted;

        /// <summary>Tackles that successfully won possession.</summary>
        public int TacklesWon;

        /// <summary>Interceptions — passes cut out by this player.</summary>
        public int Interceptions;

        /// <summary>
        /// Pressures applied (player actively pressed an opponent).
        /// Press = running at opponent within press trigger distance.
        /// </summary>
        public int PressuresAttempted;

        /// <summary>
        /// Successful pressures — press forced a turnover, rushed clearance, or bad pass.
        /// PPDA (Passes Per Defensive Action) uses this field.
        /// </summary>
        public int PressuresSuccessful;

        /// <summary>Blocks — player got in line of a shot or pass.</summary>
        public int Blocks;

        /// <summary>Clearances — ball cleared from own half / danger zone.</summary>
        public int Clearances;

        // ── Fouls and Discipline ──────────────────────────────────────────────

        /// <summary>Fouls committed by this player.</summary>
        public int FoulsCommitted;

        /// <summary>Fouls drawn (opponent fouled this player).</summary>
        public int FoulsDrawn;

        /// <summary>Yellow cards received.</summary>
        public int YellowCards;

        /// <summary>Red cards received (IsActive set to false).</summary>
        public int RedCards;

        /// <summary>Offsides caught.</summary>
        public int Offsides;

        // ── GK-Specific ───────────────────────────────────────────────────────

        /// <summary>Shots faced (GK only). = ShotsOnTarget from opponents.</summary>
        public int ShotsFaced;

        /// <summary>Saves made (GK parries + catches).</summary>
        public int Saves;

        /// <summary>
        /// Post-Save Expected Goals On Target conceded.
        /// Sum of xG of all shots saved. Measures GK quality.
        /// </summary>
        public float XGSaved;

        /// <summary>Goals conceded while this player was GK.</summary>
        public int GoalsConceded;

        // ── Physical ──────────────────────────────────────────────────────────

        /// <summary>
        /// Total distance covered in pitch units. Accumulated per tick from
        /// player velocity magnitude. Divide by 10 to get approximate metres.
        /// </summary>
        public float DistanceCovered;

        /// <summary>
        /// Distance covered while IsSprinting=true.
        /// Ratio SprintDistance/DistanceCovered indicates physical intensity.
        /// </summary>
        public float SprintDistance;

        /// <summary>Number of discrete sprint bursts (IsSprinting transitions false→true).</summary>
        public int SprintCount;

        /// <summary>Final stamina value at match end. 0.0 = exhausted, 1.0 = fully fresh.</summary>
        public float FinalStamina;

        // ── Reset ─────────────────────────────────────────────────────────────

        /// <summary>Resets all counters to zero. Called at match start by StatAggregator.</summary>
        public void Reset()
        {
            Goals = OwnGoals = Assists = 0;
            XG = XA = 0f;
            ShotsAttempted = ShotsOnTarget = ShotsOffTarget = ShotsBlocked = 0;
            PassesAttempted = PassesCompleted = ProgressivePasses = KeyPasses = 0;
            PassesIntercepted = PassesOutOfPlay = 0;
            LongBallsAttempted = LongBallsCompleted = 0;
            DribblesAttempted = DribblesSuccessful = TimesDispossessed = 0;
            CrossesAttempted = CrossesSuccessful = 0;
            AerialDuelsTotal = AerialDuelsWon = 0;
            TacklesAttempted = TacklesWon = Interceptions = 0;
            PressuresAttempted = PressuresSuccessful = Blocks = Clearances = 0;
            FoulsCommitted = FoulsDrawn = YellowCards = RedCards = Offsides = 0;
            ShotsFaced = Saves = GoalsConceded = 0;
            XGSaved = 0f;
            DistanceCovered = SprintDistance = 0f;
            SprintCount = 0;
            FinalStamina = 1f;
        }
    }
}