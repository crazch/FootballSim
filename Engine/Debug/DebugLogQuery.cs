// =============================================================================
// Module:  DebugLogQuery.cs
// Path:    FootballSim/Engine/Debug/DebugLogQuery.cs
//
// Purpose:
//   Fluent query builder over a list of TickLogs.
//   Filters down to the exact PlayerTickLog entries you care about,
//   then exports as formatted table (human readable) or JSON (LLM input).
//
//   Design principle:
//     Every method returns 'this' so calls can be chained.
//     Terminal methods (PrintTable, ToJson, ToList) execute the query.
//
//   Example patterns:
//
//   // "Why does player 0 never shoot from inside the box?"
//   query.ForPlayer(0)
//        .HasBall()
//        .InOpponentBox()
//        .PrintTable();
//
//   // "Show me every tick where pass was chosen over a higher shoot score"
//   query.ForPlayer(0)
//        .HasBall()
//        .Where(e => e.Scores != null &&
//                    e.Scores.ScorePass > e.Scores.ScoreShoot &&
//                    e.Scores.ScoreShoot > 0)
//        .ToJson();
//
//   // "Show me all events in the run"
//   query.Events().PrintTable();
//
//   // "Show all ticks where the ball was in flight toward goal"
//   query.Ball()
//        .Where(b => b.IsShot && b.ShotOnTarget)
//        .PrintTable();
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FootballSim.Engine.Debug
{
    // =========================================================================
    // PLAYER QUERY — filters PlayerTickLog entries across all ticks
    // =========================================================================

    public sealed class DebugLogQuery
    {
        private readonly IReadOnlyList<TickLog> _logs;

        // Accumulated player-level filters
        private int?   _filterPlayerId;
        private int?   _filterTeamId;
        private bool?  _filterHasBall;
        private bool?  _filterInOppBox;
        private bool?  _filterInOwnBox;
        private bool?  _filterWasReEvaluated;
        private Models.PlayerAction? _filterAction;
        private Func<PlayerTickLog, bool>? _customFilter;

        // Tick range
        private int _tickFrom = 0;
        private int _tickTo   = int.MaxValue;

        internal DebugLogQuery(IReadOnlyList<TickLog> logs)
        {
            _logs = logs;
        }

        // ── Filter methods ────────────────────────────────────────────────────

        /// <summary>Only entries for this player index (0–21).</summary>
        public DebugLogQuery ForPlayer(int playerId)
        { _filterPlayerId = playerId; return this; }

        /// <summary>Only entries for this team (0=home, 1=away).</summary>
        public DebugLogQuery ForTeam(int teamId)
        { _filterTeamId = teamId; return this; }

        /// <summary>Only entries where the player has the ball.</summary>
        public DebugLogQuery HasBall()
        { _filterHasBall = true; return this; }

        /// <summary>Only entries where the player does NOT have the ball.</summary>
        public DebugLogQuery NoBall()
        { _filterHasBall = false; return this; }

        /// <summary>Only entries where the player is in the opponent's penalty box.</summary>
        public DebugLogQuery InOpponentBox()
        { _filterInOppBox = true; return this; }

        /// <summary>Only entries where the player is in their own penalty box.</summary>
        public DebugLogQuery InOwnBox()
        { _filterInOwnBox = true; return this; }

        /// <summary>Only entries where the player was re-evaluated this tick (not cooldown).</summary>
        public DebugLogQuery WasReEvaluated()
        { _filterWasReEvaluated = true; return this; }

        /// <summary>Only entries where the player chose this action.</summary>
        public DebugLogQuery Action(Models.PlayerAction action)
        { _filterAction = action; return this; }

        /// <summary>Only ticks in this inclusive range.</summary>
        public DebugLogQuery TickRange(int from, int to)
        { _tickFrom = from; _tickTo = to; return this; }

        /// <summary>Custom predicate filter on PlayerTickLog.</summary>
        public DebugLogQuery Where(Func<PlayerTickLog, bool> predicate)
        { _customFilter = predicate; return this; }

        // ── Terminal: list ────────────────────────────────────────────────────

        /// <summary>
        /// Executes all filters and returns matching (tick, PlayerTickLog) pairs.
        /// </summary>
        public List<(int Tick, PlayerTickLog Player)> ToList()
        {
            var results = new List<(int, PlayerTickLog)>();

            foreach (var log in _logs)
            {
                if (log.Tick < _tickFrom || log.Tick > _tickTo) continue;

                foreach (var p in log.Players)
                {
                    if (_filterPlayerId.HasValue && p.PlayerId != _filterPlayerId.Value) continue;
                    if (_filterTeamId.HasValue   && p.TeamId   != _filterTeamId.Value)   continue;
                    if (_filterHasBall.HasValue   && p.HasBall  != _filterHasBall.Value)   continue;
                    if (_filterInOppBox.HasValue  && p.IsInOpponentPenaltyBox != _filterInOppBox.Value) continue;
                    if (_filterInOwnBox.HasValue  && p.IsInOwnPenaltyBox      != _filterInOwnBox.Value) continue;
                    if (_filterWasReEvaluated.HasValue && p.WasReEvaluatedThisTick != _filterWasReEvaluated.Value) continue;
                    if (_filterAction.HasValue    && p.ActionChosen != _filterAction.Value) continue;
                    if (_customFilter != null     && !_customFilter(p)) continue;

                    results.Add((log.Tick, p));
                }
            }

            return results;
        }

        // ── Terminal: print table ─────────────────────────────────────────────

        /// <summary>
        /// Prints a compact human-readable table to Console.
        /// Best for quick inspection in Godot output panel or terminal.
        ///
        /// Format (with-ball player):
        /// T:0047 P00 ST  H (525,390) BOX  PASS→P01 | shoot=0.00 pass=0.41 drib=0.12 hold=0.08
        ///                                             xG=0.04 thr=0.10 dist=290 blk=0 [THR_BLOCK]
        ///
        /// Format (without-ball player):
        /// T:0047 P11 CB  . (525,425) ---  TRACKING | press=0.00 track=0.55 recov=0.21 mark=0.32
        /// </summary>
        public void PrintTable()
        {
            var rows = ToList();
            if (rows.Count == 0)
            {
                Console.WriteLine("[DebugLogQuery] No matching entries.");
                return;
            }

            Console.WriteLine($"[DebugLogQuery] {rows.Count} entries:");
            Console.WriteLine(
                "─────┬───────────────────────────────────────────────────────────────");

            foreach (var (tick, p) in rows)
            {
                // Header line
                string ballMark  = p.HasBall ? "H" : ".";
                string boxMark   = p.IsInOpponentPenaltyBox ? "BOX"
                                 : p.IsInOwnPenaltyBox      ? "OWN" : "---";
                string evalMark  = p.WasReEvaluatedThisTick ? "*" : " ";

                string actionStr = FormatAction(p);

                Console.WriteLine(
                    $"T:{tick:D4} P{p.PlayerId:D2} {p.Role,-4} {ballMark} " +
                    $"({p.Position.X:F0},{p.Position.Y:F0}) {boxMark} " +
                    $"{evalMark}{actionStr,-16} " +
                    $"stam={p.Stamina:F2} spd={p.Speed:F1}");

                // Score breakdown line (if available)
                if (p.Scores != null)
                {
                    Console.WriteLine(
                        $"            shoot={p.Scores.ScoreShoot:F2} " +
                        $"pass={p.Scores.ScorePass:F2} " +
                        $"drib={p.Scores.ScoreDribble:F2} " +
                        $"hold={p.Scores.ScoreHold:F2}");

                    if (p.Scores.Shoot != null)
                    {
                        var s = p.Scores.Shoot;
                        string why = s.RangeBlocked     ? "[OUT_OF_RANGE]"
                                   : s.ThresholdBlocked ? "[THR_BLOCK xG<thr]"
                                   : "[SHOOT_OK]";
                        Console.WriteLine(
                            $"            xG={s.XG:F3} thr={s.EffectiveThreshold:F3} " +
                            $"dist={s.DistToGoal:F0} blk={s.BlockersInLane} {why}");
                    }
                }

                // Defensive context line (if available)
                if (p.Defensive != null)
                {
                    var d = p.Defensive;
                    Console.WriteLine(
                        $"            press={d.ScorePress:F2} " +
                        $"track={d.ScoreTrack:F2} " +
                        $"recov={d.ScoreRecover:F2} " +
                        $"mark={d.ScoreMarkSpace:F2} " +
                        $"dist_carrier={d.DistToCarrier:F0}");
                }
            }

            Console.WriteLine(
                "─────┴───────────────────────────────────────────────────────────────");
        }

        // ── Terminal: JSON ────────────────────────────────────────────────────

        /// <summary>
        /// Returns a JSON string of matching entries suitable for LLM input.
        /// Paste directly into your LLM prompt:
        ///   "Here are the ticks where the striker was in the box with the ball.
        ///    Why is ScoreShoot always 0?"
        ///
        /// Outputs an array of tick objects. Each object has player data + scores.
        /// Also printed to Console so you can copy from the Godot output panel.
        /// </summary>
        public string ToJson()
        {
            var rows = ToList();

            if (rows.Count == 0)
            {
                string empty = "[]";
                Console.WriteLine("[DebugLogQuery.ToJson] No matching entries.");
                return empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine("[");

            for (int i = 0; i < rows.Count; i++)
            {
                var (tick, p) = rows[i];
                bool last = i == rows.Count - 1;

                sb.AppendLine("  {");
                sb.AppendLine($"    \"tick\": {tick},");
                sb.AppendLine($"    \"id\": {p.PlayerId},");
                sb.AppendLine($"    \"role\": \"{p.Role}\",");
                sb.AppendLine($"    \"team\": {p.TeamId},");
                sb.AppendLine($"    \"shirt\": {p.ShirtNumber},");
                sb.AppendLine($"    \"pos\": {{\"x\": {p.Position.X:F1}, \"y\": {p.Position.Y:F1}}},");
                sb.AppendLine($"    \"target\": {{\"x\": {p.TargetPosition.X:F1}, \"y\": {p.TargetPosition.Y:F1}}},");
                sb.AppendLine($"    \"velocity\": {{\"x\": {p.Velocity.X:F2}, \"y\": {p.Velocity.Y:F2}}},");
                sb.AppendLine($"    \"speed\": {p.Speed:F2},");
                sb.AppendLine($"    \"stamina\": {p.Stamina:F3},");
                sb.AppendLine($"    \"is_sprinting\": {p.IsSprinting.ToString().ToLower()},");
                sb.AppendLine($"    \"has_ball\": {p.HasBall.ToString().ToLower()},");
                sb.AppendLine($"    \"in_opp_box\": {p.IsInOpponentPenaltyBox.ToString().ToLower()},");
                sb.AppendLine($"    \"in_own_box\": {p.IsInOwnPenaltyBox.ToString().ToLower()},");
                sb.AppendLine($"    \"dist_to_goal\": {p.DistToOpponentGoal:F1},");
                sb.AppendLine($"    \"dist_to_ball\": {p.DistToBall:F1},");
                sb.AppendLine($"    \"dist_to_nearest_opp\": {p.DistToNearestOpponent:F1},");
                sb.AppendLine($"    \"action\": \"{p.ActionChosen}\",");
                sb.AppendLine($"    \"pass_to\": {p.PassReceiverId},");
                sb.AppendLine($"    \"was_reevaluated\": {p.WasReEvaluatedThisTick.ToString().ToLower()},");
                sb.AppendLine($"    \"cooldown\": {p.DecisionCooldown},");

                // Scores (with-ball)
                if (p.Scores != null)
                {
                    sb.AppendLine("    \"scores\": {");
                    sb.AppendLine($"      \"shoot\": {p.Scores.ScoreShoot:F4},");
                    sb.AppendLine($"      \"pass\": {p.Scores.ScorePass:F4},");
                    sb.AppendLine($"      \"dribble\": {p.Scores.ScoreDribble:F4},");
                    sb.AppendLine($"      \"cross\": {p.Scores.ScoreCross:F4},");
                    sb.AppendLine($"      \"hold\": {p.Scores.ScoreHold:F4},");
                    sb.AppendLine($"      \"winning\": {p.Scores.WinningScore:F4}");
                    sb.AppendLine("    },");

                    if (p.Scores.Shoot != null)
                    {
                        var s = p.Scores.Shoot;
                        sb.AppendLine("    \"shoot_detail\": {");
                        sb.AppendLine($"      \"xg\": {s.XG:F4},");
                        sb.AppendLine($"      \"threshold\": {s.EffectiveThreshold:F4},");
                        sb.AppendLine($"      \"dist_to_goal\": {s.DistToGoal:F1},");
                        sb.AppendLine($"      \"blockers_in_lane\": {s.BlockersInLane},");
                        sb.AppendLine($"      \"blocked_by_threshold\": {s.ThresholdBlocked.ToString().ToLower()},");
                        sb.AppendLine($"      \"blocked_by_range\": {s.RangeBlocked.ToString().ToLower()}");
                        sb.AppendLine("    },");
                    }

                    if (p.Scores.Pass != null)
                    {
                        var ps = p.Scores.Pass;
                        sb.AppendLine("    \"pass_detail\": {");
                        sb.AppendLine($"      \"best_receiver\": {ps.BestReceiverId},");
                        sb.AppendLine($"      \"receiver_score\": {ps.BestReceiverScore:F4},");
                        sb.AppendLine($"      \"is_progressive\": {ps.IsProgressive.ToString().ToLower()},");
                        sb.AppendLine($"      \"distance\": {ps.PassDistance:F1}");
                        sb.AppendLine("    },");
                    }
                }

                // Defensive context (out-of-possession)
                if (p.Defensive != null)
                {
                    var d = p.Defensive;
                    sb.AppendLine("    \"defensive\": {");
                    sb.AppendLine($"      \"score_press\": {d.ScorePress:F4},");
                    sb.AppendLine($"      \"score_track\": {d.ScoreTrack:F4},");
                    sb.AppendLine($"      \"score_recover\": {d.ScoreRecover:F4},");
                    sb.AppendLine($"      \"score_mark_space\": {d.ScoreMarkSpace:F4},");
                    sb.AppendLine($"      \"track_target\": {d.TrackTargetPlayerId},");
                    sb.AppendLine($"      \"dist_to_carrier\": {d.DistToCarrier:F1},");
                    sb.AppendLine($"      \"press_trigger_dist\": {d.PressTriggerDist:F1}");
                    sb.AppendLine("    },");
                }

                // Remove trailing comma from last property if needed
                // (simplified: JSON viewers tolerate trailing commas in objects)
                sb.Append(last ? "  }" : "  },");
                sb.AppendLine();
            }

            sb.AppendLine("]");

            string json = sb.ToString();
            Console.WriteLine("[DebugLogQuery.ToJson]");
            Console.WriteLine(json);
            return json;
        }

        // ── Terminal: summary stat ────────────────────────────────────────────

        /// <summary>
        /// Prints a one-line stat summary of the matching entries.
        /// Useful for quick sanity checks without reading every row.
        ///
        /// Example output:
        ///   [Query Summary] 18 entries | actions: Pass×9 Hold×5 Dribble×3 Shoot×1
        ///   avg shoot score: 0.04  avg pass score: 0.38
        ///   xG range: 0.01–0.07   threshold: 0.10  → shoot blocked in 17/18 entries
        /// </summary>
        public void PrintSummary()
        {
            var rows = ToList();
            if (rows.Count == 0)
            {
                Console.WriteLine("[DebugLogQuery] No matching entries.");
                return;
            }

            // Action counts
            var actionCounts = rows
                .GroupBy(r => r.Player.ActionChosen)
                .ToDictionary(g => g.Key, g => g.Count());

            Console.WriteLine($"[Query Summary] {rows.Count} entries");
            Console.Write("  actions: ");
            foreach (var kv in actionCounts.OrderByDescending(x => x.Value))
                Console.Write($"{kv.Key}×{kv.Value}  ");
            Console.WriteLine();

            // Score averages (only where scores exist)
            var withScores = rows.Where(r => r.Player.Scores != null).ToList();
            if (withScores.Count > 0)
            {
                float avgShoot = withScores.Average(r => r.Player.Scores!.ScoreShoot);
                float avgPass  = withScores.Average(r => r.Player.Scores!.ScorePass);
                float avgDrib  = withScores.Average(r => r.Player.Scores!.ScoreDribble);
                float avgHold  = withScores.Average(r => r.Player.Scores!.ScoreHold);
                Console.WriteLine(
                    $"  avg scores: shoot={avgShoot:F3} pass={avgPass:F3} " +
                    $"drib={avgDrib:F3} hold={avgHold:F3}");
            }

            // xG analysis
            var withShoot = rows.Where(r => r.Player.Scores?.Shoot != null).ToList();
            if (withShoot.Count > 0)
            {
                float minXG  = withShoot.Min(r => r.Player.Scores!.Shoot!.XG);
                float maxXG  = withShoot.Max(r => r.Player.Scores!.Shoot!.XG);
                float thr    = withShoot[0].Player.Scores!.Shoot!.EffectiveThreshold;
                int   thrBlk = withShoot.Count(r => r.Player.Scores!.Shoot!.ThresholdBlocked);
                int   rngBlk = withShoot.Count(r => r.Player.Scores!.Shoot!.RangeBlocked);
                Console.WriteLine(
                    $"  xG range: {minXG:F3}–{maxXG:F3}  threshold: {thr:F3}  " +
                    $"→ threshold-blocked: {thrBlk}/{withShoot.Count}  " +
                    $"range-blocked: {rngBlk}/{withShoot.Count}");
            }
        }

        // ── Event query ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns an event query builder over all events in the log.
        /// </summary>
        public EventQuery Events() => new EventQuery(_logs);

        /// <summary>Returns a ball query builder.</summary>
        public BallQuery Ball() => new BallQuery(_logs);

        // ── Private helpers ───────────────────────────────────────────────────

        private static string FormatAction(PlayerTickLog p)
        {
            return p.ActionChosen switch
            {
                Models.PlayerAction.Passing    => $"PASS→P{p.PassReceiverId:D2}",
                Models.PlayerAction.Shooting   => $"SHOOT({p.ShotTargetPos.X:F0},{p.ShotTargetPos.Y:F0})",
                Models.PlayerAction.Dribbling  => "DRIBBLE",
                Models.PlayerAction.Holding    => "HOLD",
                Models.PlayerAction.Crossing   => "CROSS",
                Models.PlayerAction.Pressing   => "PRESS",
                Models.PlayerAction.Recovering => "RECOVER",
                Models.PlayerAction.Tracking   => $"TRACK→P{p.PassReceiverId:D2}",
                _                              => p.ActionChosen.ToString()
            };
        }
    }

    // =========================================================================
    // EVENT QUERY
    // =========================================================================

    public sealed class EventQuery
    {
        private readonly IReadOnlyList<TickLog> _logs;
        private Models.MatchEventType? _filterType;

        internal EventQuery(IReadOnlyList<TickLog> logs) { _logs = logs; }

        public EventQuery OfType(Models.MatchEventType type)
        { _filterType = type; return this; }

        public void PrintTable()
        {
            Console.WriteLine("[Events]");
            foreach (var log in _logs)
            foreach (var ev in log.Events)
            {
                if (_filterType.HasValue && ev.Type != _filterType.Value) continue;
                Console.WriteLine(
                    $"  T:{log.Tick:D4} [{ev.Type,-20}] " +
                    $"P{ev.PrimaryPlayerId:D2}→P{ev.SecondaryPlayerId:D2} " +
                    $"({ev.Position.X:F0},{ev.Position.Y:F0}) " +
                    $"xtra={ev.ExtraFloat:F3}  {ev.Description}");
            }
        }

        public List<(int Tick, EventTickLog Event)> ToList()
        {
            var results = new List<(int, EventTickLog)>();
            foreach (var log in _logs)
            foreach (var ev in log.Events)
            {
                if (_filterType.HasValue && ev.Type != _filterType.Value) continue;
                results.Add((log.Tick, ev));
            }
            return results;
        }
    }

    // =========================================================================
    // BALL QUERY
    // =========================================================================

    public sealed class BallQuery
    {
        private readonly IReadOnlyList<TickLog> _logs;
        private Func<BallTickLog, bool>? _filter;

        internal BallQuery(IReadOnlyList<TickLog> logs) { _logs = logs; }

        public BallQuery Where(Func<BallTickLog, bool> predicate)
        { _filter = predicate; return this; }

        public void PrintTable()
        {
            Console.WriteLine("[Ball states]");
            foreach (var log in _logs)
            {
                var b = log.Ball;
                if (_filter != null && !_filter(b)) continue;
                Console.WriteLine(
                    $"  T:{log.Tick:D4} {b.Phase,-10} " +
                    $"({b.Position.X:F0},{b.Position.Y:F0}) " +
                    $"spd={b.Speed:F1} h={b.Height:F2} " +
                    $"owner={b.OwnerId:D2} shot={b.IsShot} " +
                    $"on_tgt={b.ShotOnTarget}");
            }
        }
    }
}
