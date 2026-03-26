// =============================================================================
// Module:  BallState.cs
// Path:    FootballSim/Engine/Models/BallState.cs
// Purpose:
//   Runtime state of the ball for one tick.
//   Holds physical position, velocity, ownership, and phase.
//   BallSystem is the ONLY writer. All other systems are read-only consumers.
//
// API (fields — no methods, pure data):
//   Position      Vec2        World position of the ball in engine coordinates
//   Velocity      Vec2        Movement vector applied each tick by BallSystem
//   OwnerId       int         PlayerId of ball owner. -1 when LOOSE or IN_FLIGHT
//   Phase         BallPhase   Enum: OWNED | IN_FLIGHT | LOOSE
//   PassTarget    int         PlayerId of intended pass receiver. -1 if not a pass
//   ShotOnTarget  bool        True when a shot is heading toward goal frame
//   Height        float       0.0 = ground, 1.0 = head height (for aerial contests)
//   LastTouchedBy int         PlayerId of the last player who touched the ball
//   LooseTicks    int         How many ticks ball has been LOOSE (for GK rush logic)
//
// Dependencies:
//   Engine/Models/Vec2.cs       (lightweight vector, no Godot)
//   Engine/Models/BallPhase.cs  (phase enum)
//
// Notes:
//   • OwnerId = -1 is the sentinel for "no owner". Always check Phase before OwnerId.
//   • Velocity decays each tick when LOOSE (friction). BallSystem owns the decay rate.
//   • PassTarget is set when Phase transitions to IN_FLIGHT for a pass.
//     CollisionSystem reads PassTarget to know who to test intercept against.
//   • Height > 0 means aerial — heading contests apply, not tackle contests.
//   • ShotOnTarget is evaluated by EventSystem to decide if GK save attempt fires.
// =============================================================================

namespace FootballSim.Engine.Models
{
    /// <summary>
    /// Runtime state of the ball. Written only by BallSystem. Pure data — no methods.
    /// </summary>
    public struct BallState
    {
        // ── Spatial ───────────────────────────────────────────────────────────

        /// <summary>
        /// World position of the ball in engine coordinates.
        /// (0,0) = top-left of pitch. Pitch is 1050 × 680 units.
        /// Updated every tick by BallSystem.
        /// </summary>
        public Vec2 Position;

        /// <summary>
        /// Movement vector applied to Position each tick.
        /// Magnitude represents speed. Direction represents travel direction.
        /// When Phase == OWNED: velocity mirrors owner's velocity + small offset.
        /// When Phase == IN_FLIGHT: set at kick moment, decays slightly per tick (air resistance).
        /// When Phase == LOOSE: decays per tick (ground friction) until magnitude near zero.
        /// </summary>
        public Vec2 Velocity;

        // ── Ownership ─────────────────────────────────────────────────────────

        /// <summary>
        /// PlayerId (0–21) of the player currently owning the ball.
        /// -1 when Phase is IN_FLIGHT or LOOSE — nobody owns it.
        /// Written exclusively by BallSystem on ownership transfer events.
        /// </summary>
        public int OwnerId;

        /// <summary>
        /// PlayerId of the last player who had possession or last touched the ball.
        /// Used by EventSystem for attribution: "tackle won by Player X from Player Y".
        /// Never reset to -1 during a match.
        /// </summary>
        public int LastTouchedBy;

        // ── Phase ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Current ball phase. The most important field — all systems check this first.
        ///   OWNED      → ball attached to OwnerId player, moves with them
        ///   IN_FLIGHT  → ball travelling through air (pass or shot in progress)
        ///   LOOSE      → no owner, ball rolling, anyone can contest
        /// </summary>
        public BallPhase Phase;

        // ── Pass / Shot Metadata ──────────────────────────────────────────────

        /// <summary>
        /// PlayerId of the intended pass recipient when Phase == IN_FLIGHT (pass).
        /// -1 for shots or when not a directed pass.
        /// CollisionSystem uses this to determine who gets priority interception test
        /// vs who gets a random intercept chance based on proximity.
        /// </summary>
        public int PassTargetId;

        /// <summary>
        /// True when Phase == IN_FLIGHT and trajectory is heading toward goal frame.
        /// Set by BallSystem at shot moment based on angle + power calculation.
        /// EventSystem reads this each tick to fire GK save attempt when ball
        /// reaches goal line proximity.
        /// </summary>
        public bool ShotOnTarget;

        /// <summary>
        /// True when this ball flight is a shot (vs pass).
        /// Affects how CollisionSystem handles intercepts:
        /// shots = blocking geometry, passes = interception geometry.
        /// </summary>
        public bool IsShot;

        /// <summary>
        /// Expected goals value computed at the moment of the shot by DecisionSystem/PlayerAI.
        /// Stored here so CollisionSystem can use the true xG for GK save probability
        /// rather than re-estimating from ball speed (which decays in flight).
        /// Set by BallSystem.LaunchShot(). Read by CollisionSystem.ResolveGoalCheck().
        /// 0.0 when ball is not a shot.
        /// </summary>
        public float ShotXG;

        /// <summary>
        /// True after CollisionSystem has resolved the GK save/goal contest for this shot.
        /// Prevents the save roll from firing again on subsequent ticks while the ball
        /// travels slowly through the trigger zone (Bug 6 fix).
        /// Reset to false by BallSystem when ball phase changes away from InFlight.
        /// </summary>
        public bool ShotContestResolved;



        // ── Physical Properties ───────────────────────────────────────────────

        /// <summary>
        /// Ball height above pitch surface.
        /// 0.0 = ground level (rolling or bouncing).
        /// 0.5 = waist height (driven pass).
        /// 1.0 = head height (lofted ball, cross).
        /// CollisionSystem uses this: ground ball → tackle/intercept contest,
        /// aerial ball → heading contest.
        /// </summary>
        public float Height;

        /// <summary>
        /// How many ticks the ball has been in LOOSE phase.
        /// GK rush logic: if ball is LOOSE near goal and LooseTicks > threshold,
        /// GK sprints to claim. EventSystem uses this for "GK claims cross" events.
        /// Reset to 0 when Phase changes away from LOOSE.
        /// </summary>
        public int LooseTicks;

        // ── Out of Play ───────────────────────────────────────────────────────

        /// <summary>
        /// True when ball has crossed a boundary line this tick.
        /// TickSystem pauses simulation and EventSystem fires the correct set-piece event.
        /// Reset to false after the restart is resolved.
        /// </summary>
        public bool IsOutOfPlay;

        /// <summary>
        /// The team ID (0 = home, 1 = away) that caused the ball to go out of play.
        /// -1 if IsOutOfPlay is false.
        /// Used by EventSystem to assign throw-in / goal-kick / corner ownership.
        /// </summary>
        public int OutOfPlayCausedBy;
    }
}