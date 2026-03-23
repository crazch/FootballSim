// =============================================================================
// Module:  EngineDebugLogger_ScenarioExtensions.cs
// Path:    FootballSim/Engine/Debug/EngineDebugLogger_ScenarioExtensions.cs
//
// Purpose:
//   Extends the existing EngineDebugLogger.cs (currently a stub) with
//   per-tick logging hooks useful during scenario debugging.
//
//   This is a SEPARATE file, not a modification of EngineDebugLogger.cs.
//   It adds static methods that DebugTickRunner can call optionally.
//
//   Enable per-system debug flags when a scenario is isolating a specific bug:
//     CollisionSystem.DEBUG = true   → logs every tackle/intercept probability
//     PlayerAI.DEBUG = true          → logs every decision with full score breakdown
//     BallSystem.DEBUG = true        → logs every phase transition
//     DecisionSystem.DEBUG = true    → logs winning action + all scores per player
//     BlockShiftSystem.DEBUG = true  → logs shape shift X values
//
//   These flags already exist in each system. This file provides a convenience
//   method to enable/disable them all at once and to filter by player ID.
//
// Usage:
//   // Enable all logging for player 0 (home striker in 2v2):
//   DebugLogger.EnableAll(playerId: 0);
//   var runner = new DebugTickRunner(ctx);
//   runner.Run();
//   DebugLogger.DisableAll();
//
//   // Enable only collision logging (no AI noise):
//   DebugLogger.EnableCollisionOnly();
//   runner.Run();
//   DebugLogger.DisableAll();
//
// =============================================================================

using FootballSim.Engine.Systems;

namespace FootballSim.Engine.Debug
{
    /// <summary>
    /// Static convenience wrapper for all per-system DEBUG flags.
    /// Allows targeted logging without editing individual system files.
    /// </summary>
    public static class DebugLogger
    {
        // ── Enable profiles ───────────────────────────────────────────────────

        /// <summary>
        /// Enables verbose logging in ALL systems for one specific player.
        /// Very noisy — use only when you know exactly which player to watch.
        /// -1 = log all players (extremely verbose in 11v11 — use only in 2v2).
        /// </summary>
        public static void EnableAll(int playerId = -1)
        {
            SetPlayerId(playerId);
            CollisionSystem.DEBUG    = true;
            PlayerAI.DEBUG           = true;
            BallSystem.DEBUG         = true;
            DecisionSystem.DEBUG     = true;
            MovementSystem.DEBUG     = true;
            BlockShiftSystem.DEBUG   = true;
        }

        /// <summary>
        /// Enables only CollisionSystem logging.
        /// Best for debugging: tackle probability, intercept probability, save probability.
        /// Filter by playerId to reduce noise to the one player you care about.
        /// </summary>
        public static void EnableCollisionOnly(int playerId = -1)
        {
            DisableAll();
            SetPlayerId(playerId);
            CollisionSystem.DEBUG = true;
        }

        /// <summary>
        /// Enables only DecisionSystem logging.
        /// Best for debugging: why is a player choosing to hold/pass/shoot/dribble?
        /// Shows full score breakdown for every option considered.
        /// </summary>
        public static void EnableDecisionOnly(int playerId = -1)
        {
            DisableAll();
            SetPlayerId(playerId);
            DecisionSystem.DEBUG = true;
        }

        /// <summary>
        /// Enables only PlayerAI logging.
        /// Best for debugging: what action was chosen and at what target position?
        /// Shows tick, player, action, target, score for every executed plan.
        /// </summary>
        public static void EnablePlayerAIOnly(int playerId = -1)
        {
            DisableAll();
            SetPlayerId(playerId);
            PlayerAI.DEBUG = true;
        }

        /// <summary>
        /// Enables only BallSystem logging.
        /// Best for debugging: phase transitions, pass arrivals, out-of-play events.
        /// Not filterable by player — ball events are not player-specific.
        /// </summary>
        public static void EnableBallOnly()
        {
            DisableAll();
            BallSystem.DEBUG = true;
        }

        /// <summary>
        /// Disables all system debug logging. Call after a targeted debug run.
        /// </summary>
        public static void DisableAll()
        {
            CollisionSystem.DEBUG  = false;
            PlayerAI.DEBUG         = false;
            BallSystem.DEBUG       = false;
            DecisionSystem.DEBUG   = false;
            MovementSystem.DEBUG   = false;
            BlockShiftSystem.DEBUG = false;

            // Reset player ID filters to "log all" (non-intrusive default)
            CollisionSystem.DEBUG_PLAYER_ID  = -1;
            PlayerAI.DEBUG_PLAYER_ID         = -1;
            DecisionSystem.DEBUG_PLAYER_ID   = -1;
        }

        // ── Recommended debug profiles per scenario ───────────────────────────

        /// <summary>
        /// Profile for 1v1 shot debugging.
        /// Logs: DecisionSystem (why does the attacker hold?),
        ///       CollisionSystem (does the GK save roll fire?),
        ///       BallSystem (is ShotOnTarget set correctly?)
        /// Filtered to home attacker (index 0) to reduce noise.
        /// </summary>
        public static void ProfileOneVOne()
        {
            DisableAll();
            SetPlayerId(0);  // home striker
            DecisionSystem.DEBUG  = true;   // see shoot score vs hold score
            CollisionSystem.DEBUG = true;   // see GK save probability
            BallSystem.DEBUG      = true;   // see ShotOnTarget + phase transitions
        }

        /// <summary>
        /// Profile for 2v2 dribble/tackle debugging.
        /// Logs: DecisionSystem for the attacker (dribble vs pass decision),
        ///       CollisionSystem (tackle probability),
        ///       PlayerAI for the defender (TackleAttempt cooldown).
        /// Filtered to home striker (index 0) and away CB (index 11).
        /// </summary>
        public static void ProfileTwoVTwo()
        {
            DisableAll();
            // Can only set one player ID filter — use the attacker (most informative)
            SetPlayerId(0);
            DecisionSystem.DEBUG  = true;   // see dribble score: is it 0 in the dead zone?
            CollisionSystem.DEBUG = true;   // see tackle probability and cooldown
            PlayerAI.DEBUG        = true;   // see action chosen + sprint flag
        }

        /// <summary>
        /// Profile for 3v3 passing triangle debugging.
        /// Logs: DecisionSystem for all players (pass vs hold scores),
        ///       BallSystem (does the ball arrive at the receiver?).
        /// Not filtered by player — need to see all 6 decision logs.
        /// Warning: verbose. Run for only 60 ticks when using this profile.
        /// </summary>
        public static void ProfileThreeVThree()
        {
            DisableAll();
            // No player filter — need to see all 6 players
            DecisionSystem.DEBUG_PLAYER_ID = -1;
            DecisionSystem.DEBUG           = true;
            BallSystem.DEBUG               = true;
        }

        // ── Private helper ────────────────────────────────────────────────────

        private static void SetPlayerId(int id)
        {
            CollisionSystem.DEBUG_PLAYER_ID  = id;
            PlayerAI.DEBUG_PLAYER_ID         = id;
            DecisionSystem.DEBUG_PLAYER_ID   = id;
            // MovementSystem and BallSystem don't have player ID filters
        }
    }
}
