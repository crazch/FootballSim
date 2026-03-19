// =============================================================================
// Module:  Enums.cs
// Path:    FootballSim/Engine/Models/Enums.cs
// Purpose:
//   All simulation enums in one file to keep imports clean.
//   Split into logical regions. No logic — pure enum declarations.
//
//   PlayerRole      → Tactical role of a player (drives spatial anchors + AI weights)
//   PlayerAction    → What a player is doing this tick (drives MovementSystem + BallSystem)
//   BallPhase       → State of the ball (OWNED / IN_FLIGHT / LOOSE)
//   MatchEventType  → All possible events emitted by EventSystem
//   MatchPhase      → High-level phase of the match flow (KICKOFF, OPEN_PLAY, SET_PIECE...)
//   TeamSide        → Home or Away (used in contexts where int TeamId is ambiguous)
//
// Dependencies: none
//
// Notes:
//   • PlayerRole values must align with roles.json and role_tendencies.json in Data/.
//   • MatchEventType covers all events StatAggregator and PostMatchUI will ever need.
//     Add new event types here first before implementing in EventSystem.
//   • BallPhase is the first thing every system checks before any ball logic.
//     The three-state model prevents "two owners at once" bugs.
// =============================================================================

namespace FootballSim.Engine.Models
{
    // =========================================================================
    // PLAYER ROLE
    // What tactical role this player fulfils. Controls:
    //   - FormationData anchor slot they are assigned
    //   - RoleDefinition spatial tendencies (where they move in/out of possession)
    //   - DecisionSystem scoring weights (IW drifts inside, DLP looks for switches)
    // =========================================================================
    public enum PlayerRole
    {
        // Goalkeepers
        GK,     // Goalkeeper — stays on line, distributes
        SK,     // Sweeper Keeper — pushes to edge of box, plays passes to beat press

        // Defenders
        CB,     // Central Defender — stays compact, aerial duels
        BPD,    // Ball-Playing Defender — drives out with ball, joins midfield
        WB,     // Wing Back — pushes high in possession, tracks back in defence
        RB,     // Right Back — standard full back
        LB,     // Left Back — standard full back

        // Defensive Midfielders
        CDM,    // Anchor CDM — stays between defence and ball, doesn't advance
        DLP,    // Deep-Lying Playmaker — drops to receive, long switches

        // Central Midfielders
        CM,     // Central Midfielder — balanced two-way role
        BBM,    // Box-to-Box Midfielder — covers ground both ways, joins attacks

        // Attacking Midfielders / Wide
        AM,     // Attacking Midfielder — sits between lines, creates chances
        IW,     // Inverted Winger — wide in defence, drifts inside in possession
        WF,     // Wide Forward — stays wide, targets space in behind, crosses

        // Forwards
        CF,     // False Nine — drops to midfield, pulls CB out of line
        ST,     // Striker — anchors on shoulder of last defender, direct
        PF,     // Pressing Forward — anchor high, very tight press trigger
    }

    // =========================================================================
    // PLAYER ACTION
    // What a player is doing this tick. Set by PlayerAI/DecisionSystem.
    // MovementSystem and BallSystem read this to determine what physics to apply.
    // =========================================================================
    public enum PlayerAction
    {
        // ── With ball ─────────────────────────────────────────────────────────
        Dribbling,          // Moving with ball attached, defender not close enough to tackle
        Passing,            // Ball release animation tick — BallSystem sets IN_FLIGHT this tick
        Shooting,           // Shot release tick — BallSystem sets IN_FLIGHT (IsShot=true)
        Crossing,           // Cross from wide area — similar to pass, height > 0
        Holding,            // Standing still with ball — buying time, waiting for run

        // ── Without ball, team has possession ────────────────────────────────
        SupportingRun,      // Moving to create passing angle for ball carrier
        MakingRun,          // Forward run into space to receive through ball
        OverlapRun,         // Wide run to overlap and receive ball wide
        HoldingWidth,       // Staying wide to stretch defensive block
        DroppingDeep,       // Dropping toward own half to receive (e.g. False Nine)
        PositioningSupport, // Moving to optimal passing triangle position

        // ── Without ball, team does not have possession ───────────────────────
        Pressing,           // Actively running at ball carrier or nearby opponent
        Tracking,           // Marking a specific opposition runner
        Recovering,         // Sprinting back to defensive shape after losing possession
        Covering,           // Covering space behind a pressing teammate
        Blocking,           // Getting in line of shot or pass
        MarkingSpace,       // Zone coverage — defending an area rather than a player

        // ── Neutral / Physical ────────────────────────────────────────────────
        Idle,               // Waiting — no active task (e.g. just completed action)
        WalkingToAnchor,    // Returning to formation anchor position
        TackleAttempt,      // Committed tackle this tick — CollisionSystem resolves
        InterceptAttempt,   // Reaching to intercept pass — CollisionSystem resolves
        Heading,            // Contesting aerial ball this tick
        Fouling,            // Illegal contact — EventSystem fires FOUL event
        Celebrating,        // Goal scored — short action lock after goal event
        TakingSetPiece,     // About to take corner / free kick / throw-in
    }

    // =========================================================================
    // BALL PHASE
    // State machine for the ball. Systems check this FIRST before any ball logic.
    // Prevents two-owner bugs and clarifies which physics model applies.
    // =========================================================================
    public enum BallPhase
    {
        /// <summary>
        /// Ball is attached to a player (OwnerId is valid).
        /// Ball position mirrors player position + small offset.
        /// No velocity applied by BallSystem — ball moves with player.
        /// </summary>
        Owned,

        /// <summary>
        /// Ball is travelling through air or rolling toward a target.
        /// Velocity is active. OwnerId = -1.
        /// CollisionSystem checks intercept each tick.
        /// Phase → Owned when intended receiver reaches ball.
        /// Phase → Loose when ball overshoots or is deflected.
        /// </summary>
        InFlight,

        /// <summary>
        /// Ball has no owner and is decelerating to a stop.
        /// OwnerId = -1. Velocity decays each tick (friction).
        /// Any player within possession range can contest to own it.
        /// GK rushes if LOOSE near goal and LooseTicks > threshold.
        /// </summary>
        Loose,
    }

    // =========================================================================
    // MATCH EVENT TYPE
    // All events EventSystem can emit. Add here before implementing in EventSystem.
    // StatAggregator switches on this to increment the correct stat counter.
    // =========================================================================
    public enum MatchEventType
    {
        // ── Goals & Shots ─────────────────────────────────────────────────────
        Goal,               // Ball crosses goal line — ExtraFloat = xG, ExtraBool = true
        OwnGoal,            // Defensive error — ball crosses own line
        ShotOnTarget,       // Shot saved or post — ExtraFloat = xG
        ShotOffTarget,      // Shot wide or over — ExtraFloat = xG
        ShotBlocked,        // Outfield player blocks shot before GK

        // ── Saves ─────────────────────────────────────────────────────────────
        Save,               // GK parries or catches — ExtraFloat = xG of shot saved
        ClaimedCross,       // GK catches cross from air

        // ── Passes ───────────────────────────────────────────────────────────
        PassCompleted,      // Successful pass — ExtraFloat = distance, ExtraBool = progressive
        PassIntercepted,    // Pass cut out by opponent — SecondaryPlayerId = interceptor
        PassOutOfPlay,      // Pass goes out — gives throw-in / goal-kick

        // ── Dribbles ──────────────────────────────────────────────────────────
        DribbleSuccess,     // Player beats defender — ExtraFloat = space gained
        DribbleFailed,      // Player loses ball to tackle or running into defender

        // ── Tackles & Pressures ───────────────────────────────────────────────
        TackleSuccess,      // Clean tackle, possession won — ExtraBool = true (clean)
        TaclkeFoul,         // Tackle attempt results in foul
        PressureSuccess,    // Press forces rushed clearance or bad pass
        PressureFailed,     // Press unsuccessful, ball carrier keeps ball

        // ── Possession Changes ────────────────────────────────────────────────
        PossessionWon,      // Generic possession gain (loose ball claimed, etc.)
        PossessionLost,     // Generic possession loss

        // ── Set Pieces ────────────────────────────────────────────────────────
        Kickoff,            // Match start or restart after goal
        ThrowIn,            // Ball out on touchline
        GoalKick,           // Ball out over byline (attacking team last touch)
        CornerKick,         // Ball out over byline (defending team last touch)
        FreeKick,           // Foul outside penalty area
        PenaltyAwarded,     // Foul inside penalty area

        // ── Fouls & Discipline ────────────────────────────────────────────────
        Foul,               // Illegal contact — ExtraFloat = severity
        YellowCard,         // Caution — ExtraFloat = foul severity that triggered it
        RedCard,            // Dismissal — player.IsActive = false after this event
        Offside,            // Attacker in offside position at pass moment

        // ── Phase Changes ─────────────────────────────────────────────────────
        HalfTime,           // End of first half
        FullTime,           // End of match

        // ── Special / Role-Based ──────────────────────────────────────────────
        KeyPass,            // Pass directly creating a goal scoring chance (xG ≥ threshold)
        Assist,             // Pass leading directly to a goal — added retroactively by StatAggregator
        CrossAttempt,       // Cross from wide area
        HeaderAttempt,      // Headed attempt on goal
        LongBallAttempt,    // Pass > long_ball_distance_threshold
    }

    // =========================================================================
    // MATCH PHASE
    // High-level flow state. TickSystem reads this to decide what the loop does.
    // =========================================================================
    public enum MatchPhase
    {
        PreKickoff,         // Before match starts — players walking to positions
        Kickoff,            // Kickoff moment — ball placed, PlayerAI knows who starts
        OpenPlay,           // Normal play — all systems running
        SetPiece,           // Set piece being prepared — most movement paused
        GoalScored,         // Brief pause — celebration ticks, then Kickoff
        HalfTime,           // 45-minute pause — stamina partially recovers
        FullTime,           // Match over — simulation ends, ReplayFrame recording stops
    }

    // =========================================================================
    // TEAM SIDE
    // Semantic wrapper for TeamId when the int 0/1 would be ambiguous.
    // Used in MatchContext and EventSystem where both teams are discussed together.
    // =========================================================================
    public enum TeamSide
    {
        Home = 0,
        Away = 1,
    }
}