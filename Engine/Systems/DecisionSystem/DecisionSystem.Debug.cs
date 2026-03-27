// =============================================================================
// Module:  DecisionSystem.Debug.cs
// Path:    FootballSim/Engine/Systems/DecisionSystem/DecisionSystem.Debug.cs
// Purpose:
//   All verbose diagnostic logging for the DecisionSystem. Keeping debug output
//   in its own module prevents diagnostic noise from polluting the actual decision
//   logic and makes it easy to strip at compile time if needed.
//
// Methods defined here:
//   DebugLogWithBall(player, best, shot, pass, cross, drib, hold, tick)
//   DebugLogWithoutBall(player, best)
//   DebugLogOutOfPossession(player, best)
//
// Bug fixes applied (see DecisionSystemBugsAnalysis.md):
//   Bug 28 — DebugLogWithBall logs "Tick ?": ctx.Tick now passed through correctly
//             as an explicit int parameter so the log always shows the correct tick.
//
// Usage:
//   Set DecisionSystem.DEBUG = true to enable logging.
//   Set DecisionSystem.DEBUG_PLAYER_ID to a specific PlayerId to filter to one
//   player. Leave at -1 to log all players (WARNING: very expensive).
//
// Notes:
//   • All methods are private static — called only from within DecisionSystem.
//   • Early-return on DEBUG=false ensures zero overhead in production builds.
// =============================================================================

using System;
using FootballSim.Engine.Models;

namespace FootballSim.Engine.Systems
{
    public static partial class DecisionSystem
    {
        // =====================================================================
        // DEBUG LOGGING
        // =====================================================================

        private static void DebugLogWithBall(ref PlayerState player, ref ActionPlan best,
            ShotScore shot, PassCandidate pass, float cross, float drib, float hold,
            int tick) // Bug 28 fix: tick passed explicitly so log shows correct tick number
        {
            if (!DEBUG) return;
            if (DEBUG_PLAYER_ID >= 0 && DEBUG_PLAYER_ID != player.PlayerId) return;

            Console.WriteLine(
                $"[DecisionSystem] Tick {tick}  P{player.PlayerId} (Team{player.TeamId} {player.Role}) " +
                $"HAS BALL → chose {best.Action} score={best.Score:F3} | " +
                $"SHOOT={shot.Score:F3}(xG={shot.XG:F3}) " +
                $"PASS={pass.Score:F3}(→P{pass.ReceiverId}) " +
                $"CROSS={cross:F3} DRIB={drib:F3} HOLD={hold:F3}");
        }

        private static void DebugLogWithoutBall(ref PlayerState player, ref ActionPlan best)
        {
            if (!DEBUG) return;
            if (DEBUG_PLAYER_ID >= 0 && DEBUG_PLAYER_ID != player.PlayerId) return;

            Console.WriteLine(
                $"[DecisionSystem] P{player.PlayerId} (Team{player.TeamId} {player.Role}) " +
                $"NO BALL (team has) → chose {best.Action} score={best.Score:F3} " +
                $"target={best.TargetPosition}");
        }

        private static void DebugLogOutOfPossession(ref PlayerState player, ref ActionPlan best)
        {
            if (!DEBUG) return;
            if (DEBUG_PLAYER_ID >= 0 && DEBUG_PLAYER_ID != player.PlayerId) return;

            Console.WriteLine(
                $"[DecisionSystem] P{player.PlayerId} (Team{player.TeamId} {player.Role}) " +
                $"OUT OF POSS → chose {best.Action} score={best.Score:F3} " +
                $"target={best.TargetPosition}");
        }
    }
}
