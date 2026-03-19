// =============================================================================
// Module:  TacticsInput.cs
// Path:    FootballSim/Engine/Tactics/TacticsInput.cs
// Purpose:
//   Pure data container for all tactical slider values set by the user in
//   TacticsScreen. Stored inside TeamData.Tactics and read every tick by AI
//   systems. Contains ONLY normalised float values (0.0–1.0). No behaviour,
//   no logic, no runtime state.
//
//   Systems that READ this (never write):
//     DecisionSystem   → pass/shoot/press scoring weights
//     MovementSystem   → anchor offset magnitudes, sprint thresholds
//     PlayerAI         → press trigger distance, tempo pacing
//     CollisionSystem  → tackle aggression modifier
//     EventSystem      → foul threshold, offside line height
//     StatAggregator   → stores a copy for HypothesisResult comparison
//
// API (fields — all float 0.0–1.0 unless noted):
//
//   ── PRESSING ──────────────────────────────────────────────────────────────
//   PressingIntensity     How aggressively players press opponents with the ball.
//                         0.0 = only press deep in own half / never really press
//                         1.0 = Gegenpress — press immediately anywhere on pitch
//                         Maps to: press trigger distance threshold in PlayerAI
//
//   PressingTrigger       Which situations trigger the press shape.
//                         0.0 = only trigger when opponent GK has ball
//                         0.5 = trigger on any defender in own half
//                         1.0 = trigger on any opponent touch anywhere
//
//   PressCompactness      How narrow the press shape is.
//                         0.0 = spread wide when pressing (cut off wide outlets)
//                         1.0 = condense centrally (force play down the wings)
//
//   ── DEFENSIVE LINE ────────────────────────────────────────────────────────
//   DefensiveLine         Vertical position of back-four anchor line.
//                         0.0 = very deep — back four anchor at 80% of pitch depth
//                         1.0 = very high line — back four anchor at 30% of pitch depth
//                         Maps to: defender FormationAnchor Y override in MovementSystem
//
//   DefensiveWidth        Horizontal spread of defensive block.
//                         0.0 = very narrow — compact central block, concede wide space
//                         1.0 = very wide — full width coverage, exposed centrally
//
//   DefensiveAggression   Willingness to attempt tackles vs. hold shape.
//                         0.0 = jockey / delay — stay on feet, hold position
//                         1.0 = all-in — commit to tackles immediately
//
//   ── POSSESSION / BUILD-UP ─────────────────────────────────────────────────
//   PossessionFocus       Priority given to keeping the ball vs. going direct.
//                         0.0 = direct — always favour forward options first
//                         1.0 = patient — always recycle before going forward
//                         Maps to: pass_score recycling bonus in DecisionSystem
//
//   BuildUpSpeed          How quickly the team moves the ball forward.
//                         0.0 = slow and methodical — wait for shape before passing
//                         1.0 = fast — play immediately on first touch
//
//   PassingDirectness     Preference for short vs. long passes.
//                         0.0 = short — always prefer nearest available teammate
//                         1.0 = direct — prefer longest progressive option available
//
//   ── ATTACKING ─────────────────────────────────────────────────────────────
//   AttackingWidth        How wide attacking players position in possession.
//                         0.0 = narrow — pack the box, overload central channels
//                         1.0 = wide — stretch defence, create wide crossing opportunities
//
//   AttackingLine         How high forwards push their attacking anchor.
//                         0.0 = deep — strikers drop into midfield pockets
//                         1.0 = very high — strikers pin last defender, chase in behind
//
//   TransitionSpeed       How quickly team transitions from defence to attack on turnover.
//                         0.0 = patient — regroup into shape before advancing
//                         1.0 = instant — sprint forward immediately on possession won
//
//   CrossingFrequency     How often wide players look for crossing opportunities vs. cutting inside.
//                         0.0 = never cross — always cut inside or play back
//                         1.0 = always cross — wide players prioritise delivery
//
//   ShootingThreshold     Minimum positional quality before a player attempts a shot.
//                         0.0 = shoot from anywhere — low xG shots attempted freely
//                         1.0 = only shoot in high-value positions (xG ≥ 0.25)
//                         Modified by individual player Ego attribute in DecisionSystem
//
//   ── TEMPO ─────────────────────────────────────────────────────────────────
//   Tempo                 Pacing of play — how quickly players move to next position.
//                         0.0 = slow — hold position, wait, patient movement
//                         1.0 = high — immediate transition to next role position
//                         High tempo increases stamina drain (MovementSystem)
//
//   ── SHAPE / COMPACTNESS ───────────────────────────────────────────────────
//   OutOfPossessionShape  How tight the team defends as a unit.
//                         0.0 = very open — man-marking, players chase individually
//                         1.0 = very compact — block shape, short distances between lines
//
//   InPossessionSpread    How spread out the team gets when in possession.
//                         0.0 = clustered — overload zones, short triangles
//                         1.0 = maximally spread — use full pitch width and depth
//
//   ── FREEDOM / DISCIPLINE ──────────────────────────────────────────────────
//   FreedomLevel          How much players deviate from tactical instructions.
//                         0.0 = Strict — player always returns to anchor, follows
//                                role definition exactly, ignores instinct
//                         0.5 = Balanced — follows shape but allows role expression
//                         1.0 = Expressive — player weight heavily toward personal
//                                instinct and ability; shape is a suggestion
//                         Maps to: PlayerAI instinct_weight vs tactic_weight split
//
//   ── PRESSING / COUNTER ────────────────────────────────────────────────────
//   CounterAttackFocus    Priority on counter-attacking after winning possession.
//                         0.0 = consolidate — keep ball, re-establish shape
//                         1.0 = counter — forward players sprint into channels
//                                immediately on turnover regardless of shape
//
//   OffsideTrapFrequency  How often defenders attempt to spring the offside trap.
//                         0.0 = never — stay deep, avoid risk
//                         1.0 = always — defensive line steps up as a unit on pass
//
//   ── PHYSICAL ──────────────────────────────────────────────────────────────
//   PhysicalityBias       Tendency to favour physical duels vs technical play.
//                         0.0 = technical — avoid aerial/50-50s, play around pressure
//                         1.0 = physical — actively seek headers, long balls, duels
//
//   SetPieceFocus         Time and organisation invested in set piece routines.
//                         0.0 = no set piece rehearsal — generic delivery and runs
//                         1.0 = high set piece focus — organised routines, priority
//
// Dependencies: none
//
// Notes:
//   • ALL values are 0.0–1.0. Never store raw distances, ticks, or pixels here.
//     Systems multiply these against their own constants to get real values.
//     Example: press distance = Lerp(MIN_PRESS_DIST, MAX_PRESS_DIST, PressingIntensity)
//   • GamePlan.cs creates preset TacticsInput instances (Gegenpress, ParkTheBus etc.)
//   • TacticsScreen UI maps each slider directly to one field here.
//   • StatAggregator captures a copy of this at match start for HypothesisResult.
//   • Default() returns a balanced 0.5 midpoint — a neutral starting point.
// =============================================================================

namespace FootballSim.Engine.Tactics
{
    /// <summary>
    /// All tactical slider values for one team. Pure data, normalised 0.0–1.0.
    /// Stored in TeamData.Tactics. Read every tick by AI systems. Never mutated
    /// during a match.
    /// </summary>
    public struct TacticsInput
    {
        // ── Pressing ──────────────────────────────────────────────────────────

        /// <summary>How aggressively the team presses. 0=passive, 1=Gegenpress.</summary>
        public float PressingIntensity;

        /// <summary>What situations trigger the press shape. 0=GK only, 1=any touch.</summary>
        public float PressingTrigger;

        /// <summary>How narrow/wide the press shape is. 0=wide trap, 1=central funnel.</summary>
        public float PressCompactness;

        // ── Defensive Line ────────────────────────────────────────────────────

        /// <summary>Vertical height of back-four anchor. 0=deep block, 1=high line.</summary>
        public float DefensiveLine;

        /// <summary>Horizontal spread of defensive block. 0=narrow, 1=full width.</summary>
        public float DefensiveWidth;

        /// <summary>Willingness to commit to tackles. 0=jockey, 1=all-in.</summary>
        public float DefensiveAggression;

        // ── Possession / Build-Up ─────────────────────────────────────────────

        /// <summary>Preference for keeping ball vs going direct. 0=direct, 1=patient.</summary>
        public float PossessionFocus;

        /// <summary>How quickly ball is moved forward. 0=slow methodical, 1=first touch.</summary>
        public float BuildUpSpeed;

        /// <summary>Short vs long passing preference. 0=short, 1=direct long ball.</summary>
        public float PassingDirectness;

        // ── Attacking ─────────────────────────────────────────────────────────

        /// <summary>Width of attacking positions. 0=narrow overload, 1=full stretch.</summary>
        public float AttackingWidth;

        /// <summary>Height of forward anchor positions. 0=drop deep, 1=pin last defender.</summary>
        public float AttackingLine;

        /// <summary>Speed of transition attack after turnover. 0=regroup, 1=instant sprint.</summary>
        public float TransitionSpeed;

        /// <summary>Wide player crossing tendency. 0=cut inside always, 1=always cross.</summary>
        public float CrossingFrequency;

        /// <summary>Minimum positional quality before shooting. 0=shoot anywhere, 1=high xG only.</summary>
        public float ShootingThreshold;

        // ── Tempo ─────────────────────────────────────────────────────────────

        /// <summary>Speed of positional movement and decisions. 0=slow patient, 1=high intensity.</summary>
        public float Tempo;

        // ── Shape / Compactness ───────────────────────────────────────────────

        /// <summary>Tightness of out-of-possession block. 0=open man-mark, 1=compact shape.</summary>
        public float OutOfPossessionShape;

        /// <summary>Spread in possession. 0=clustered triangles, 1=full pitch spread.</summary>
        public float InPossessionSpread;

        // ── Freedom / Discipline ──────────────────────────────────────────────

        /// <summary>Player autonomy. 0=strict shape, 0.5=balanced, 1=expressive instinct.</summary>
        public float FreedomLevel;

        // ── Counter / Transition ──────────────────────────────────────────────

        /// <summary>Priority on counter-attack after turnover. 0=consolidate, 1=instant counter.</summary>
        public float CounterAttackFocus;

        /// <summary>Frequency of offside trap attempts. 0=never, 1=always step up.</summary>
        public float OffsideTrapFrequency;

        // ── Physical ──────────────────────────────────────────────────────────

        /// <summary>Tendency toward physical play. 0=technical, 1=aerial/50-50 preference.</summary>
        public float PhysicalityBias;

        /// <summary>Set piece organisation investment. 0=generic, 1=rehearsed routines.</summary>
        public float SetPieceFocus;

        // ── Factory ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a balanced neutral starting point — all sliders at 0.5.
        /// Used as the base before TacticsScreen or GamePlan modifies values.
        /// </summary>
        public static TacticsInput Default() => new TacticsInput
        {
            PressingIntensity     = 0.5f,
            PressingTrigger       = 0.5f,
            PressCompactness      = 0.5f,
            DefensiveLine         = 0.5f,
            DefensiveWidth        = 0.5f,
            DefensiveAggression   = 0.5f,
            PossessionFocus       = 0.5f,
            BuildUpSpeed          = 0.5f,
            PassingDirectness     = 0.5f,
            AttackingWidth        = 0.5f,
            AttackingLine         = 0.5f,
            TransitionSpeed       = 0.5f,
            CrossingFrequency     = 0.5f,
            ShootingThreshold     = 0.5f,
            Tempo                 = 0.5f,
            OutOfPossessionShape  = 0.5f,
            InPossessionSpread    = 0.5f,
            FreedomLevel          = 0.5f,
            CounterAttackFocus    = 0.5f,
            OffsideTrapFrequency  = 0.2f,  // default: rarely trap — safer
            PhysicalityBias       = 0.5f,
            SetPieceFocus         = 0.5f,
        };
    }
}