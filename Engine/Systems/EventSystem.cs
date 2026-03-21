// =============================================================================
// Module:  EventSystem.cs
// Path:    FootballSim/Engine/Systems/EventSystem.cs
// Purpose:
//   Detects match conditions from the current MatchContext state and emits
//   MatchEvent records into ctx.EventsThisTick. This is the only system that
//   creates MatchEvent objects.
//
//   EventSystem is PURELY an observer and emitter. Hard rules:
//     ✓ MAY read ctx.Ball, ctx.Players, ctx.LastCollisionResult, ctx.Tick
//     ✓ MAY write to ctx.EventsThisTick (append only)
//     ✓ MAY write ctx.HomeScore / ctx.AwayScore on goal events
//     ✓ MAY write ctx.Phase (e.g. → GoalScored, → HalfTime, → FullTime)
//     ✓ MAY write player.IsActive = false on RedCard
//     ✗ MUST NOT call BallSystem (no physics modification)
//     ✗ MUST NOT call PlayerAI or DecisionSystem
//     ✗ MUST NOT move any player Position or Velocity
//     ✗ MUST NOT modify BallState directly
//
//   Detection responsibilities:
//     From ctx.LastCollisionResult:
//       Goal          → emit Goal event, update score, set ctx.Phase = GoalScored
//       OwnGoal       → same as Goal but ownership inverted
//       TackleSuccess → emit TackleSuccess event
//       TaclkeFoul    → emit Foul + optional YellowCard/RedCard
//       InterceptSuccess / InterceptDeflection → emit PassIntercepted
//       LooseBallClaimed → emit PossessionWon
//       GKSave / GKCatch → emit Save event
//
//     From ball state (independent of collision result):
//       Ball IsOutOfPlay  → emit ThrowIn / GoalKick / CornerKick
//       Ball IsShot (was shot last tick, now resolved) → emit ShotOnTarget/ShotOffTarget
//       InFlight → PassCompleted when receiver gains ownership this tick
//
//     From timing:
//       ctx.Tick == HALF_TIME_TICK → emit HalfTime, set Phase
//       ctx.Tick == FULL_TIME_TICK → emit FullTime, set Phase
//
// API:
//   EventSystem.Tick(MatchContext ctx) → void
//     Called once per tick by TickSystem after CollisionSystem.
//     Reads ctx and appends to ctx.EventsThisTick. Single entry point.
//
//   EventSystem.BuildEvent(ctx, type, primary, secondary, teamId, pos, ef, eb)
//     → MatchEvent
//     Factory helper — fills timing fields and builds Description string.
//     Used internally; public for tests.
//
// Timing constants:
//   HALF_TIME_TICK = 27000  (45 min × 600 ticks/min = 27000)
//   FULL_TIME_TICK = 54000  (90 min × 600 ticks/min = 54000)
//   These are defined in EventSystem, not PhysicsConstants, because they
//   are match-rules constants, not physics constants.
//
// Description format (pre-built so GDScript never does string building):
//   Goal:         "45' GOAL — P9 [Home] assisted by P7 | xG: 0.34"
//   Tackle:       "32' TACKLE — P6 [Away] wins ball from P10 [Home]"
//   Foul:         "58' FOUL — P4 [Home] on P11 [Away] | severity: 0.71 → YELLOW"
//   Pass:         "12' PASS — P7 [Home] → P3 | dist: 142 | progressive"
//   Intercept:    "19' INTERCEPT — P14 [Away] cuts out pass from P3 [Home]"
//   Save:         "67' SAVE — GK P11 [Away] catches shot by P9 [Home] | xG: 0.41"
//   OutOfPlay:    "24' CORNER — Home concedes corner"
//
// Dependencies:
//   Engine/Models/MatchContext.cs
//   Engine/Models/MatchEvent.cs
//   Engine/Models/PlayerState.cs
//   Engine/Models/BallState.cs
//   Engine/Models/Enums.cs
//   Engine/Systems/CollisionSystem.cs  (CollisionResult, CollisionResultType)
//   Engine/Systems/PhysicsConstants.cs
//   Engine/MathUtil.cs
// =============================================================================

using System;
using FootballSim.Engine.Models;

namespace FootballSim.Engine.Systems
{
    public static class EventSystem
    {
        // ── DEBUG ─────────────────────────────────────────────────────────────

        /// <summary>
        /// When true, logs every event emitted to console with tick, type, and description.
        /// </summary>
        public static bool DEBUG = false;

        // ── Match Timing Constants ────────────────────────────────────────────

        /// <summary>
        /// Distance ball must move from centre to finish kickoff
        /// </summary>
        private const float KICKOFF_OPEN_PLAY_THRESHOLD = 80f;

        /// <summary>
        /// Tick at which first half ends. 45 min × 60 s/min × 10 ticks/s = 27000.
        /// </summary>
        public const int HALF_TIME_TICK = 27000;

        /// <summary>
        /// Tick at which full time is called. 90 min × 60 × 10 = 54000.
        /// </summary>
        public const int FULL_TIME_TICK = 54000;

        /// <summary>
        /// Minimum xG for a shot event to be labelled a "key pass" on the assist.
        /// 0.15 = moderate chance — avoids spamming key-pass events on every shot.
        /// </summary>
        private const float KEY_PASS_XG_THRESHOLD = 0.15f;

        /// <summary>
        /// Minimum pass distance (pitch units) to count as a "long ball" in the event log.
        /// 350 units = 35m.
        /// </summary>
        private const float LONG_BALL_DISTANCE = 350f;

        // ── Tick state — carry one field between sub-methods ──────────────────

        // Last known passer — used to attribute assists retroactively on the next goal
        private static int _lastPasserId = -1;
        private static int _lastPassTeam = -1;
        private static float _lastShotXG = 0f;
        private static bool _shotWasInFlight = false; // true on the tick a shot was launched

        // ── Main Tick ─────────────────────────────────────────────────────────

        /// <summary>
        /// Main entry point. Called once per tick by TickSystem, after CollisionSystem.
        /// </summary>
        public static void Tick(MatchContext ctx)
        {
            // ── 1. Collision-driven events (highest priority) ──────────────────
            ProcessCollisionResult(ctx);

            // ── 2. Ball out of play ────────────────────────────────────────────
            if (ctx.Ball.IsOutOfPlay)
                ProcessOutOfPlay(ctx);

            // ── 3. Shot resolved this tick (not goal — that's from collision) ──
            if (ctx.Ball.IsShot && ctx.Ball.Phase == BallPhase.InFlight)
                ProcessShotInFlight(ctx);

            // ── 4. Pass completed this tick ────────────────────────────────────
            ProcessPassCompletion(ctx);

            // ── 5. Match timing ────────────────────────────────────────────────
            ProcessTiming(ctx);
        }

        // =====================================================================
        // COLLISION-DRIVEN EVENTS
        // =====================================================================

        private static void ProcessCollisionResult(MatchContext ctx)
        {
            CollisionResult cr = ctx.LastCollisionResult;

            switch (cr.Type)
            {
                case CollisionResultType.None:
                    break;

                case CollisionResultType.Goal:
                    EmitGoal(ctx, cr, isOwn: false);
                    break;

                case CollisionResultType.OwnGoal:
                    EmitGoal(ctx, cr, isOwn: true);
                    break;

                case CollisionResultType.TackleSuccess:
                    EmitTackle(ctx, cr);
                    break;

                case CollisionResultType.TaclkeFoul:
                    EmitFoul(ctx, cr);
                    break;

                case CollisionResultType.TackleFailed:
                    // No event — tackle missed, play continues silently
                    break;

                case CollisionResultType.InterceptSuccess:
                case CollisionResultType.InterceptDeflection:
                    EmitIntercept(ctx, cr);
                    break;

                case CollisionResultType.LooseBallClaimed:
                    EmitPossessionWon(ctx, cr);
                    break;

                case CollisionResultType.GKSave:
                case CollisionResultType.GKCatch:
                    EmitSave(ctx, cr);
                    break;

                case CollisionResultType.HeaderWon:
                case CollisionResultType.HeaderLost:
                    EmitHeader(ctx, cr);
                    break;
            }
        }

        // ── Goal ──────────────────────────────────────────────────────────────

        private static void EmitGoal(MatchContext ctx, CollisionResult cr, bool isOwn)
        {
            // Scoring team
            int scorerTeam;
            if (isOwn)
                scorerTeam = 1 - (cr.PrimaryPlayerId <= 10 ? 0 : 1);
            else
                scorerTeam = cr.PrimaryPlayerId <= 10 ? 0 : 1;

            // Update score
            if (scorerTeam == 0) ctx.HomeScore++;
            else ctx.AwayScore++;

            // Conceding team kicks off next
            ctx.KickoffTeam = scorerTeam == 0 ? 1 : 0;

            // Build assist info
            int assisterId = -1;
            if (!isOwn && _lastPasserId >= 0 &&
                _lastPassTeam == scorerTeam &&
                ctx.Players[_lastPasserId].IsActive)
            {
                assisterId = _lastPasserId;
            }

            string teamLabel = scorerTeam == 0 ? "Home" : "Away";
            string assistPart = assisterId >= 0
                ? $" | assisted by P{ctx.Players[assisterId].ShirtNumber}"
                : "";
            string prefix = isOwn ? "OWN GOAL" : "GOAL";

            string desc = $"{ctx.MatchMinute}' {prefix} — " +
                          $"P{ctx.Players[cr.PrimaryPlayerId].ShirtNumber} [{teamLabel}]" +
                          $"{assistPart} | xG: {cr.ShotXG:F2} | " +
                          $"Score: {ctx.HomeScore}–{ctx.AwayScore}";

            var ev = BuildEvent(ctx,
                type: isOwn ? MatchEventType.OwnGoal : MatchEventType.Goal,
                primary: cr.PrimaryPlayerId,
                secondary: assisterId,
                teamId: scorerTeam,
                pos: cr.Position,
                ef: cr.ShotXG,
                eb: true,
                desc: desc);

            ctx.EventsThisTick.Add(ev);
            ctx.Phase = MatchPhase.GoalScored;

            // Also emit Assist as a separate event if applicable
            if (assisterId >= 0)
            {
                string assistDesc = $"{ctx.MatchMinute}' ASSIST — " +
                                    $"P{ctx.Players[assisterId].ShirtNumber} [{teamLabel}]";
                ctx.EventsThisTick.Add(BuildEvent(ctx,
                    type: MatchEventType.Assist,
                    primary: assisterId,
                    secondary: cr.PrimaryPlayerId,
                    teamId: scorerTeam,
                    pos: cr.Position,
                    ef: cr.ShotXG,
                    eb: false,
                    desc: assistDesc));
            }

            // Reset passer state after goal
            _lastPasserId = -1;
            _lastPassTeam = -1;

            LogDebug(ctx, desc);
        }

        // ── Tackle ────────────────────────────────────────────────────────────

        private static void EmitTackle(MatchContext ctx, CollisionResult cr)
        {
            string tacklerTeam = cr.PrimaryPlayerId <= 10 ? "Home" : "Away";
            string fouledTeam = cr.SecondaryPlayerId <= 10 ? "Home" : "Away";

            string desc = $"{ctx.MatchMinute}' TACKLE — " +
                          $"P{ctx.Players[cr.PrimaryPlayerId].ShirtNumber} [{tacklerTeam}] " +
                          $"wins ball from " +
                          $"P{ctx.Players[cr.SecondaryPlayerId].ShirtNumber} [{fouledTeam}]";

            ctx.EventsThisTick.Add(BuildEvent(ctx,
                type: MatchEventType.TackleSuccess,
                primary: cr.PrimaryPlayerId,
                secondary: cr.SecondaryPlayerId,
                teamId: cr.DefendingTeamId,
                pos: cr.Position,
                ef: cr.Probability,
                eb: cr.IsClean,
                desc: desc));

            LogDebug(ctx, desc);
        }

        // ── Foul ──────────────────────────────────────────────────────────────

        private static void EmitFoul(MatchContext ctx, CollisionResult cr)
        {
            // Which area? Determines free kick vs penalty
            bool inPenaltyBox = IsInPenaltyBox(cr.Position, cr.DefendingTeamId);
            string teamLabel = cr.PrimaryPlayerId <= 10 ? "Home" : "Away";
            string victimLabel = cr.SecondaryPlayerId >= 0
                ? (cr.SecondaryPlayerId <= 10 ? "Home" : "Away") : "";

            // Card check
            string cardSuffix = "";
            MatchEventType foulType = inPenaltyBox
                ? MatchEventType.PenaltyAwarded
                : MatchEventType.FreeKick;

            string desc = $"{ctx.MatchMinute}' FOUL — " +
                          $"P{ctx.Players[cr.PrimaryPlayerId].ShirtNumber} [{teamLabel}]";

            if (cr.SecondaryPlayerId >= 0)
                desc += $" on P{ctx.Players[cr.SecondaryPlayerId].ShirtNumber} [{victimLabel}]";

            desc += $" | severity: {cr.FoulSeverity:F2}";

            // Check for card
            if (cr.FoulSeverity >= CollisionConstants.RED_CARD_THRESHOLD)
            {
                cardSuffix = " → RED CARD";
                // Issue red card event separately
                ctx.EventsThisTick.Add(BuildEvent(ctx,
                    type: MatchEventType.RedCard,
                    primary: cr.PrimaryPlayerId,
                    secondary: cr.SecondaryPlayerId,
                    teamId: cr.PrimaryPlayerId <= 10 ? 0 : 1,
                    pos: cr.Position,
                    ef: cr.FoulSeverity,
                    eb: false,
                    desc: $"{ctx.MatchMinute}' RED CARD — " +
                          $"P{ctx.Players[cr.PrimaryPlayerId].ShirtNumber} [{teamLabel}]"));

                // Deactivate the player
                if (cr.PrimaryPlayerId >= 0 && cr.PrimaryPlayerId <= 21)
                    ctx.Players[cr.PrimaryPlayerId].IsActive = false;
            }
            else if (cr.FoulSeverity >= CollisionConstants.YELLOW_CARD_THRESHOLD)
            {
                cardSuffix = " → YELLOW CARD";
                ctx.EventsThisTick.Add(BuildEvent(ctx,
                    type: MatchEventType.YellowCard,
                    primary: cr.PrimaryPlayerId,
                    secondary: cr.SecondaryPlayerId,
                    teamId: cr.PrimaryPlayerId <= 10 ? 0 : 1,
                    pos: cr.Position,
                    ef: cr.FoulSeverity,
                    eb: false,
                    desc: $"{ctx.MatchMinute}' YELLOW CARD — " +
                          $"P{ctx.Players[cr.PrimaryPlayerId].ShirtNumber} [{teamLabel}]"));
            }

            desc += cardSuffix;

            // Emit foul event
            ctx.EventsThisTick.Add(BuildEvent(ctx,
                type: MatchEventType.Foul,
                primary: cr.PrimaryPlayerId,
                secondary: cr.SecondaryPlayerId,
                teamId: cr.DefendingTeamId,
                pos: cr.Position,
                ef: cr.FoulSeverity,
                eb: inPenaltyBox,
                desc: desc));

            // Emit free kick or penalty
            ctx.EventsThisTick.Add(BuildEvent(ctx,
                type: foulType,
                primary: cr.SecondaryPlayerId >= 0 ? cr.SecondaryPlayerId : cr.PrimaryPlayerId,
                secondary: -1,
                teamId: cr.DefendingTeamId,
                pos: cr.Position,
                ef: 0f,
                eb: false,
                desc: inPenaltyBox
                    ? $"{ctx.MatchMinute}' PENALTY — " +
                      $"{(cr.DefendingTeamId == 0 ? "Home" : "Away")} awarded"
                    : $"{ctx.MatchMinute}' FREE KICK — " +
                      $"{(cr.DefendingTeamId == 0 ? "Home" : "Away")}"));

            LogDebug(ctx, desc);
        }

        // ── Intercept ─────────────────────────────────────────────────────────

        private static void EmitIntercept(MatchContext ctx, CollisionResult cr)
        {
            string intTeam = cr.PrimaryPlayerId <= 10 ? "Home" : "Away";
            string passTeam = cr.SecondaryPlayerId >= 0
                ? (cr.SecondaryPlayerId <= 10 ? "Home" : "Away") : "?";

            bool clean = cr.Type == CollisionResultType.InterceptSuccess;
            string kind = clean ? "INTERCEPT" : "INTERCEPTION (deflection)";

            string desc = $"{ctx.MatchMinute}' {kind} — " +
                          $"P{ctx.Players[cr.PrimaryPlayerId].ShirtNumber} [{intTeam}]";

            if (cr.SecondaryPlayerId >= 0)
                desc += $" cuts out pass from " +
                        $"P{ctx.Players[cr.SecondaryPlayerId].ShirtNumber} [{passTeam}]";

            ctx.EventsThisTick.Add(BuildEvent(ctx,
                type: MatchEventType.PassIntercepted,
                primary: cr.PrimaryPlayerId,
                secondary: cr.SecondaryPlayerId,
                teamId: cr.DefendingTeamId,
                pos: cr.Position,
                ef: cr.Probability,
                eb: clean,
                desc: desc));

            LogDebug(ctx, desc);
        }

        // ── Possession Won (loose ball) ────────────────────────────────────────

        private static void EmitPossessionWon(MatchContext ctx, CollisionResult cr)
        {
            string teamLabel = cr.PrimaryPlayerId <= 10 ? "Home" : "Away";
            string desc = $"{ctx.MatchMinute}' POSSESSION — " +
                          $"P{ctx.Players[cr.PrimaryPlayerId].ShirtNumber} [{teamLabel}] " +
                          $"claims loose ball";

            ctx.EventsThisTick.Add(BuildEvent(ctx,
                type: MatchEventType.PossessionWon,
                primary: cr.PrimaryPlayerId,
                secondary: -1,
                teamId: cr.DefendingTeamId,
                pos: cr.Position,
                ef: 0f,
                eb: false,
                desc: desc));

            LogDebug(ctx, desc);
        }

        // ── Save ──────────────────────────────────────────────────────────────

        private static void EmitSave(MatchContext ctx, CollisionResult cr)
        {
            string gkTeam = cr.PrimaryPlayerId <= 10 ? "Home" : "Away";
            string shooterTeam = cr.SecondaryPlayerId >= 0
                ? (cr.SecondaryPlayerId <= 10 ? "Home" : "Away") : "?";
            bool caught = cr.Type == CollisionResultType.GKCatch;
            string action = caught ? "catches" : "saves";

            string desc = $"{ctx.MatchMinute}' SAVE — GK " +
                          $"P{ctx.Players[cr.PrimaryPlayerId].ShirtNumber} [{gkTeam}] " +
                          $"{action} shot";

            if (cr.SecondaryPlayerId >= 0)
                desc += $" by P{ctx.Players[cr.SecondaryPlayerId].ShirtNumber} [{shooterTeam}]";

            desc += $" | xG: {cr.ShotXG:F2}";

            ctx.EventsThisTick.Add(BuildEvent(ctx,
                type: MatchEventType.Save,
                primary: cr.PrimaryPlayerId,
                secondary: cr.SecondaryPlayerId,
                teamId: cr.DefendingTeamId,
                pos: cr.Position,
                ef: cr.ShotXG,
                eb: caught,
                desc: desc));

            // Also emit ShotOnTarget (the shot that was saved)
            if (cr.SecondaryPlayerId >= 0)
            {
                string shooterLabel = cr.SecondaryPlayerId <= 10 ? "Home" : "Away";
                ctx.EventsThisTick.Add(BuildEvent(ctx,
                    type: MatchEventType.ShotOnTarget,
                    primary: cr.SecondaryPlayerId,
                    secondary: cr.PrimaryPlayerId,
                    teamId: cr.SecondaryPlayerId <= 10 ? 0 : 1,
                    pos: ctx.Players[cr.SecondaryPlayerId].Position,
                    ef: cr.ShotXG,
                    eb: true,
                    desc: $"{ctx.MatchMinute}' SHOT ON TARGET — " +
                               $"P{ctx.Players[cr.SecondaryPlayerId].ShirtNumber} " +
                               $"[{shooterLabel}] | xG: {cr.ShotXG:F2}"));
            }

            LogDebug(ctx, desc);
        }

        // ── Header ────────────────────────────────────────────────────────────

        private static void EmitHeader(MatchContext ctx, CollisionResult cr)
        {
            bool won = cr.Type == CollisionResultType.HeaderWon;
            string teamLabel = cr.PrimaryPlayerId <= 10 ? "Home" : "Away";
            string desc = $"{ctx.MatchMinute}' HEADER — " +
                          $"P{ctx.Players[cr.PrimaryPlayerId].ShirtNumber} [{teamLabel}] " +
                          (won ? "wins" : "loses") + " aerial contest";

            ctx.EventsThisTick.Add(BuildEvent(ctx,
                type: MatchEventType.HeaderAttempt,
                primary: cr.PrimaryPlayerId,
                secondary: cr.SecondaryPlayerId,
                teamId: cr.DefendingTeamId,
                pos: cr.Position,
                ef: 0f,
                eb: won,
                desc: desc));
        }

        // =====================================================================
        // BALL STATE EVENTS
        // =====================================================================

        private static void ProcessOutOfPlay(MatchContext ctx)
        {
            // Determine set piece type from ball position and last touch
            int lastToucher = ctx.Ball.LastTouchedBy;
            int lastTeam = lastToucher >= 0 && lastToucher <= 10 ? 0 : 1;

            Vec2 ballPos = ctx.Ball.Position;

            // Left or right touchline → throw-in
            bool sideline = ballPos.X <= PhysicsConstants.PITCH_LEFT ||
                            ballPos.X >= PhysicsConstants.PITCH_RIGHT;

            if (sideline)
            {
                string throwTeam = (1 - lastTeam) == 0 ? "Home" : "Away";
                string desc = $"{ctx.MatchMinute}' THROW-IN — {throwTeam}";

                ctx.EventsThisTick.Add(BuildEvent(ctx,
                    type: MatchEventType.ThrowIn,
                    primary: lastToucher,
                    secondary: -1,
                    teamId: 1 - lastTeam,   // team that gets the throw
                    pos: ballPos,
                    ef: 0f, eb: false, desc: desc));

                LogDebug(ctx, desc);
                ctx.Ball.IsOutOfPlay = false;   // FIX: consume flag
                return;
            }

            // Top or bottom byline
            bool topByline = ballPos.Y <= PhysicsConstants.PITCH_TOP;
            bool bottomByline = ballPos.Y >= PhysicsConstants.PITCH_BOTTOM;

            if (!topByline && !bottomByline) return;

            // Determine attacking direction
            // Home attacks toward bottom (Y=680), away toward top (Y=0)
            bool homeAttacksDown = true;
            bool lastTeamWasAttacking;
            if (topByline)
                lastTeamWasAttacking = lastTeam == 1; // away attacks top
            else
                lastTeamWasAttacking = lastTeam == 0; // home attacks bottom

            if (lastTeamWasAttacking)
            {
                // Attacking team last touched → goal kick for defenders
                int defendingTeam = 1 - lastTeam;
                string defLabel = defendingTeam == 0 ? "Home" : "Away";
                string desc = $"{ctx.MatchMinute}' GOAL KICK — {defLabel}";

                ctx.EventsThisTick.Add(BuildEvent(ctx,
                    type: MatchEventType.GoalKick,
                    primary: lastToucher,
                    secondary: -1,
                    teamId: defendingTeam,
                    pos: ballPos,
                    ef: 0f, eb: false, desc: desc));

                LogDebug(ctx, desc);
                ctx.Ball.IsOutOfPlay = false;   // FIX
            }
            else
            {
                // Defending team last touched → corner for attackers
                int attackingTeam = 1 - lastTeam;
                string atkLabel = attackingTeam == 0 ? "Home" : "Away";
                string desc = $"{ctx.MatchMinute}' CORNER — {atkLabel}";

                ctx.EventsThisTick.Add(BuildEvent(ctx,
                    type: MatchEventType.CornerKick,
                    primary: lastToucher,
                    secondary: -1,
                    teamId: attackingTeam,
                    pos: ballPos,
                    ef: 0f, eb: false, desc: desc));

                LogDebug(ctx, desc);
                ctx.Ball.IsOutOfPlay = false;   // FIX
            }
        }

        // ── Shot in flight (not resolved yet — still tracking) ────────────────

        private static void ProcessShotInFlight(MatchContext ctx)
        {
            // We track that a shot is live; the goal/save is resolved by CollisionSystem.
            // If the shot is off target (ShotOnTarget=false) we can emit ShotOffTarget now
            // only if the ball left the pitch this tick.
            if (ctx.Ball.IsOutOfPlay && !ctx.Ball.ShotOnTarget &&
                ctx.Ball.LastTouchedBy >= 0)
            {
                int shooterId = ctx.Ball.LastTouchedBy;
                int shooterTeam = shooterId <= 10 ? 0 : 1;
                string teamLabel = shooterTeam == 0 ? "Home" : "Away";

                // Approximate xG from speed — same proxy used in CollisionSystem
                float spd = ctx.Ball.Velocity.Length();
                float xG = MathUtil.Lerp(0.02f, 0.30f,
                                MathUtil.Clamp01(spd / PhysicsConstants.BALL_SHOT_SPEED_POWER));

                string desc = $"{ctx.MatchMinute}' SHOT OFF TARGET — " +
                              $"P{ctx.Players[shooterId].ShirtNumber} [{teamLabel}] " +
                              $"| xG: {xG:F2}";

                ctx.EventsThisTick.Add(BuildEvent(ctx,
                    type: MatchEventType.ShotOffTarget,
                    primary: shooterId,
                    secondary: -1,
                    teamId: shooterTeam,
                    pos: ctx.Players[shooterId].Position,
                    ef: xG,
                    eb: false,
                    desc: desc));

                LogDebug(ctx, desc);
            }
        }

        // ── Pass completion ───────────────────────────────────────────────────

        private static void ProcessPassCompletion(MatchContext ctx)
        {
            // A pass is "completed" this tick when:
            //   - Ball was InFlight last tick (IsShot=false, PassTargetId >= 0)
            //   - Ball is now Owned by the PassTargetId player
            // We detect this by: ball.Phase==Owned && ball.LastTouchedBy == PassTargetId
            // and the PassTargetId was just set to -1 (cleared by BallSystem on arrival).
            // Simpler reliable check: if ball is Owned this tick, and OwnerId ≠ LastTouchedBy
            // from the prior tick... the ownership just changed without collision.
            //
            // Limitation: we emit PassCompleted conservatively only when no collision
            // event already covered it (avoid double-emitting on intercepts).

            if (ctx.Ball.Phase != BallPhase.Owned) return;
            if (ctx.LastCollisionResult.Type != CollisionResultType.None) return;
            if (ctx.Ball.IsShot) return;

            int ownerId = ctx.Ball.OwnerId;
            if (ownerId < 0 || ownerId > 21) return;
            if (!ctx.Players[ownerId].IsActive) return;

            // Only emit if we have a known recent passer to attribute
            if (_lastPasserId < 0) return;
            if (_lastPassTeam != ctx.Players[ownerId].TeamId) return; // different team got it

            // Guard: don't re-emit if we already emitted this possession cycle
            // We use _lastPasserId reset as the flag — clear it after emitting
            int passerId = _lastPasserId;
            int passerTeam = _lastPassTeam;
            _lastPasserId = -1;
            _lastPassTeam = -1;

            if (passerId == ownerId) return; // player passed to themselves (can't happen, but guard)

            float passDist = ctx.Players[passerId].Position.DistanceTo(
                             ctx.Players[ownerId].Position);

            bool attacksDown = passerTeam == 0;
            bool progressive = attacksDown
                ? ctx.Players[ownerId].Position.Y > ctx.Players[passerId].Position.Y + 20f
                : ctx.Players[ownerId].Position.Y < ctx.Players[passerId].Position.Y - 20f;

            bool isLongBall = passDist >= LONG_BALL_DISTANCE;

            string teamLabel = passerTeam == 0 ? "Home" : "Away";
            string progressLabel = progressive ? " | progressive" : "";
            string longLabel = isLongBall ? " | long ball" : "";

            string desc = $"{ctx.MatchMinute}' PASS — " +
                          $"P{ctx.Players[passerId].ShirtNumber} [{teamLabel}] → " +
                          $"P{ctx.Players[ownerId].ShirtNumber} " +
                          $"| dist: {passDist:F0}{progressLabel}{longLabel}";

            ctx.EventsThisTick.Add(BuildEvent(ctx,
                type: MatchEventType.PassCompleted,
                primary: passerId,
                secondary: ownerId,
                teamId: passerTeam,
                pos: ctx.Players[passerId].Position,
                ef: passDist,
                eb: progressive,
                desc: desc));

            // Emit long ball sub-event
            if (isLongBall)
                ctx.EventsThisTick.Add(BuildEvent(ctx,
                    type: MatchEventType.LongBallAttempt,
                    primary: passerId,
                    secondary: ownerId,
                    teamId: passerTeam,
                    pos: ctx.Players[passerId].Position,
                    ef: passDist,
                    eb: progressive,
                    desc: $"{ctx.MatchMinute}' LONG BALL — " +
                               $"P{ctx.Players[passerId].ShirtNumber} [{teamLabel}] " +
                               $"| dist: {passDist:F0}"));

            // If this pass created a significant chance, emit KeyPass
            if (_lastShotXG >= KEY_PASS_XG_THRESHOLD)
                ctx.EventsThisTick.Add(BuildEvent(ctx,
                    type: MatchEventType.KeyPass,
                    primary: passerId,
                    secondary: ownerId,
                    teamId: passerTeam,
                    pos: ctx.Players[passerId].Position,
                    ef: _lastShotXG,
                    eb: true,
                    desc: $"{ctx.MatchMinute}' KEY PASS — " +
                               $"P{ctx.Players[passerId].ShirtNumber} [{teamLabel}]" +
                               $" | xG created: {_lastShotXG:F2}"));

            LogDebug(ctx, desc);

            // Track the current owner as passer for the next pass chain
            _lastPasserId = ownerId;
            _lastPassTeam = passerTeam;
        }

        // ── Match timing ──────────────────────────────────────────────────────

        private static void ProcessTiming(MatchContext ctx)
        {
            if (ctx.Phase == MatchPhase.FullTime ||
                ctx.Phase == MatchPhase.HalfTime) return;

            // Kickoff → OpenPlay transition
            if (ctx.Phase == MatchPhase.Kickoff)
            {
                Vec2 centre = new Vec2(
                    PhysicsConstants.PITCH_WIDTH * 0.5f,
                    PhysicsConstants.PITCH_HEIGHT * 0.5f
                );

                float distFromCentre = ctx.Ball.Position.DistanceTo(centre);

                if (distFromCentre > KICKOFF_OPEN_PLAY_THRESHOLD)
                    ctx.Phase = MatchPhase.OpenPlay;

                return;
            }

            if (ctx.Tick == HALF_TIME_TICK)
            {
                ctx.Phase = MatchPhase.HalfTime;
                string desc = $"45' HALF TIME — {ctx.HomeScore}–{ctx.AwayScore}";
                ctx.EventsThisTick.Add(BuildEvent(ctx,
                    type: MatchEventType.HalfTime,
                    primary: -1, secondary: -1, teamId: -1,
                    pos: Vec2.Zero, ef: 0f, eb: false, desc: desc));
                LogDebug(ctx, desc);
            }
            else if (ctx.Tick >= FULL_TIME_TICK)
            {
                ctx.Phase = MatchPhase.FullTime;
                string desc = $"90' FULL TIME — {ctx.HomeScore}–{ctx.AwayScore}";
                ctx.EventsThisTick.Add(BuildEvent(ctx,
                    type: MatchEventType.FullTime,
                    primary: -1, secondary: -1, teamId: -1,
                    pos: Vec2.Zero, ef: 0f, eb: false, desc: desc));
                LogDebug(ctx, desc);
            }
        }

        // =====================================================================
        // PUBLIC HELPERS
        // =====================================================================

        /// <summary>
        /// Records that a pass was launched this tick so EventSystem can emit
        /// PassCompleted (or attribution on goal) on the receiving tick.
        /// Called by PlayerAI right after BallSystem.LaunchPass().
        /// </summary>
        public static void NotifyPassLaunched(int passerId, int passerTeam)
        {
            _lastPasserId = passerId;
            _lastPassTeam = passerTeam;
            _lastShotXG = 0f; // reset — no shot pending
        }

        /// <summary>
        /// Records that a shot was launched this tick and its xG value.
        /// Called by PlayerAI right after BallSystem.LaunchShot().
        /// </summary>
        public static void NotifyShotLaunched(float xG)
        {
            _lastShotXG = xG;
            _shotWasInFlight = true;
        }

        /// <summary>
        /// Factory: creates a MatchEvent with all timing fields pre-filled.
        /// Public for tests. Internally used by all emit methods.
        /// </summary>
        public static MatchEvent BuildEvent(MatchContext ctx,
                                             MatchEventType type,
                                             int primary,
                                             int secondary,
                                             int teamId,
                                             Vec2 pos,
                                             float ef,
                                             bool eb,
                                             string desc)
        {
            return new MatchEvent
            {
                Tick = ctx.Tick,
                MatchSecond = ctx.MatchSecond,
                MatchMinute = ctx.MatchMinute,
                Type = type,
                PrimaryPlayerId = primary,
                SecondaryPlayerId = secondary,
                TeamId = teamId,
                Position = pos,
                ExtraFloat = ef,
                ExtraBool = eb,
                Description = desc,
            };
        }

        // =====================================================================
        // PRIVATE HELPERS
        // =====================================================================

        private static bool IsInPenaltyBox(Vec2 pos, int defendingTeam)
        {
            float centreX = PhysicsConstants.PITCH_WIDTH * 0.5f;
            float halfW = PhysicsConstants.PENALTY_AREA_HALF_WIDTH;

            bool xInBox = pos.X >= centreX - halfW && pos.X <= centreX + halfW;
            if (!xInBox) return false;

            // Defending team 0 (home) defends top penalty box (Y <= PENALTY_AREA_DEPTH)
            // Defending team 1 (away) defends bottom penalty box (Y >= PITCH_HEIGHT - DEPTH)
            if (defendingTeam == 0)
                return pos.Y <= PhysicsConstants.PENALTY_AREA_DEPTH;
            else
                return pos.Y >= PhysicsConstants.PITCH_HEIGHT - PhysicsConstants.PENALTY_AREA_DEPTH;
        }

        private static void LogDebug(MatchContext ctx, string desc)
        {
            if (!DEBUG) return;
            Console.WriteLine($"[EventSystem] Tick {ctx.Tick}: {desc}");
        }
    }
}