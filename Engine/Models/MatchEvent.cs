// =============================================================================
// Module:  MatchEvent.cs
// Path:    FootballSim/Engine/Models/MatchEvent.cs
// Purpose:
//   Immutable record of a single meaningful event that occurred during simulation.
//   Emitted by EventSystem when conditions are met (goal, tackle, pass, shot...).
//   Stored in ReplayFrame.Event (nullable) and consumed by:
//     • StatAggregator  → builds per-player and team stats
//     • ReplayPlayer.gd → fires EventBus signal for EventLogUI
//     • HypothesisResult → analyses tactical intent vs outcome
//
// API (fields — no methods, pure data):
//   Tick          int           Tick number when event occurred (0–54000)
//   MatchSecond   float         Tick × 0.1 = seconds into match
//   Type          MatchEventType  Enum covering all possible events
//   PrimaryPlayerId   int       The player who caused/performed the event
//   SecondaryPlayerId int       The other player involved (-1 if none)
//   TeamId        int           0 = home, 1 = away (team that "owns" this event)
//   Position      Vec2          Pitch position where event occurred
//   ExtraFloat    float         General-purpose value (xG for shot, pass length, etc.)
//   ExtraBool     bool          General-purpose flag (e.g. ShotOnTarget for SHOT events)
//   Description   string        Human-readable log line for EventLogUI
//
// Dependencies:
//   Engine/Models/Vec2.cs             (lightweight vector, no Godot)
//   Engine/Models/MatchEventType.cs   (event type enum)
//
// Notes:
//   • MatchEvent is a CLASS (not struct) because it is nullable in ReplayFrame.
//     (ReplayFrame.Event = null means no event this tick — the common case.)
//   • Description is pre-built by EventSystem using player names + context,
//     so Godot visualization only reads it — no string building in GDScript.
//   • ExtraFloat stores different meanings per event type:
//       SHOT      → xG value (0.0–1.0)
//       PASS      → pass distance in units
//       TACKLE    → tackle success probability at contest moment
//       FOUL      → foul severity 0.0–1.0 (affects card decision)
//       DRIBBLE   → space gained in units
//   • MatchSecond is redundant with Tick but kept for human-readable debug output.
// =============================================================================

namespace FootballSim.Engine.Models
{
    /// <summary>
    /// Immutable record of one simulation event. Written by EventSystem,
    /// consumed by StatAggregator, ReplayPlayer, and HypothesisResult.
    /// Class (not struct) so it can be null in ReplayFrame.
    /// </summary>
    public class MatchEvent
    {
        // ── Timing ────────────────────────────────────────────────────────────

        /// <summary>
        /// Tick index when this event occurred. Range 0–54000 for a 90-minute match.
        /// Primary key for ordering events.
        /// </summary>
        public int Tick;

        /// <summary>
        /// Match time in seconds. = Tick × 0.1.
        /// Stored pre-calculated for readable debug output and EventLogUI display.
        /// "Second 720.5" → Tick 7205.
        /// </summary>
        public float MatchSecond;

        /// <summary>
        /// Match minute for display (MatchSecond / 60, floor).
        /// Used in EventLogUI: "45' - GOAL - Player 9".
        /// </summary>
        public int MatchMinute;

        // ── Classification ────────────────────────────────────────────────────

        /// <summary>
        /// The type of event. Drives how StatAggregator categorises it and
        /// how EventLogUI renders the description.
        /// </summary>
        public MatchEventType Type;

        // ── Players Involved ──────────────────────────────────────────────────

        /// <summary>
        /// PlayerId (0–21) of the primary actor in this event.
        /// GOAL → scorer. TACKLE → tackler. PASS → passer. FOUL → fouler.
        /// SAVE → goalkeeper. YELLOW_CARD / RED_CARD → player carded.
        /// </summary>
        public int PrimaryPlayerId;

        /// <summary>
        /// PlayerId (0–21) of the secondary participant. -1 if event involves only one player.
        /// GOAL → assister (-1 if no assist). TACKLE → player tackled.
        /// PASS → receiver. FOUL → player fouled. INTERCEPT → intercepting player.
        /// </summary>
        public int SecondaryPlayerId;

        // ── Team ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Team ID that "owns" this event. 0 = home, 1 = away.
        /// For contested events (tackle, intercept): the team that won the contest.
        /// Used by StatAggregator to attribute possession events correctly.
        /// </summary>
        public int TeamId;

        // ── Spatial ───────────────────────────────────────────────────────────

        /// <summary>
        /// Pitch position where the event occurred.
        /// SHOT → shot origin (used for xG calculation display).
        /// TACKLE → tackle location (used for heatmap pressure zones).
        /// GOAL → always near goal line.
        /// PASS → ball position at moment of kick.
        /// </summary>
        public Vec2 Position;

        // ── Payload ───────────────────────────────────────────────────────────

        /// <summary>
        /// General-purpose float payload. Meaning varies by event type:
        ///   SHOT            → xG value 0.0–1.0
        ///   PASS            → pass distance in pitch units
        ///   TACKLE          → tackle contest probability at moment of contact
        ///   FOUL            → severity 0.0–1.0 (used for card decision)
        ///   DRIBBLE_SUCCESS → space gained in pitch units
        ///   PRESS           → distance pressed from (how aggressive the press was)
        ///   Default         → 0.0
        /// </summary>
        public float ExtraFloat;

        /// <summary>
        /// General-purpose bool payload. Meaning varies by event type:
        ///   SHOT    → true if on target
        ///   PASS    → true if progressive pass (moves ball 10+ units toward opponent goal)
        ///   TACKLE  → true if clean tackle (no foul)
        ///   PRESS   → true if press was successful (forced turnover or rushed clearance)
        ///   Default → false
        /// </summary>
        public bool ExtraBool;

        // ── Display ───────────────────────────────────────────────────────────

        /// <summary>
        /// Pre-built human-readable description for EventLogUI scrolling feed.
        /// Built by EventSystem at event creation time.
        /// Example: "45' GOAL — Player 9 (Home) | xG: 0.34 | Assist: Player 7"
        /// Example: "32' TACKLE — Player 6 (Away) wins ball from Player 10 (Home)"
        /// Example: "58' YELLOW CARD — Player 4 (Home) | Foul on Player 11"
        /// </summary>
        public string Description;
    }
}