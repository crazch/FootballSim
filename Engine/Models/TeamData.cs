// =============================================================================
// Module:  TeamData.cs
// Path:    FootballSim/Engine/Models/TeamData.cs
// Purpose:
//   Static data describing one team loaded at match start.
//   Holds the squad (11 players with attributes), the chosen formation name,
//   and the tactics input (all sliders). Does NOT change during a match.
//   MatchEngine reads this once to initialise 11 PlayerState structs per team.
//
// API (fields — no methods, pure data):
//   TeamId         int              0 = home, 1 = away
//   Name           string           Display name (e.g. "Home FC")
//   FormationName  string           Key into FormationData lookup (e.g. "4-3-3")
//   Players        PlayerData[11]   Ordered by formation slot 0–10
//   Tactics        TacticsInput     All slider values for this team
//
// PlayerData (nested struct):
//   PlayerId       int              Global index (0–10 home, 11–21 away)
//   ShirtNumber    int
//   Name           string
//   Role           PlayerRole
//   BaseSpeed      float
//   PassingAbility float
//   ShootingAbility float
//   DribblingAbility float
//   DefendingAbility float
//   Reactions      float
//   Ego            float
//   StaminaAttribute float
//
// Dependencies:
//   Engine/Models/PlayerRole.cs   (role enum)
//   Engine/Tactics/TacticsInput.cs
//
// Notes:
//   • PlayerData index in Players[] matches formation slot (slot 0 = GK, slot 1 = RB/CB etc.)
//     FormationData maps slot index to anchor Vector2 — order must match.
//   • All float attributes are 0.0–1.0 normalised. 0.5 = average professional.
//   • TeamData is the only place player names are stored. PlayerState uses PlayerId only.
//   • TacticsInput is copied into TeamData by TacticsScreen before match start.
//     Engine does NOT read sliders from UI — it reads from this struct only.
// =============================================================================

using FootballSim.Engine.Tactics;

namespace FootballSim.Engine.Models
{
    /// <summary>
    /// Static attributes of one player. Loaded at match start into PlayerState.
    /// Pure data — no methods.
    /// </summary>
    public struct PlayerData
    {
        /// <summary>
        /// Global player index 0–10 for home, 11–21 for away.
        /// Used as the PlayerId in PlayerState and all event attribution.
        /// </summary>
        public int PlayerId;

        /// <summary>Shirt number 1–11. Display only.</summary>
        public int ShirtNumber;

        /// <summary>Player display name. Used in MatchEvent.Description and PostMatch UI.</summary>
        public string Name;

        /// <summary>
        /// Tactical role. Determines formation slot behaviour and spatial tendencies.
        /// Must match the slot this player occupies in the formation.
        /// </summary>
        public PlayerRole Role;

        // ── Physical Attributes (0.0 – 1.0) ──────────────────────────────────

        /// <summary>
        /// Base movement speed in pitch units per tick at full stamina.
        /// Applied in MovementSystem before stamina curve scaling.
        /// Typical range: 0.35 (slow CB) – 0.60 (fast winger).
        /// </summary>
        public float BaseSpeed;

        /// <summary>
        /// Stamina capacity 0.0–1.0. High = slower decay, faster recovery.
        /// Midfielders typically 0.7–0.9. Low-stamina attackers 0.4–0.6.
        /// </summary>
        public float StaminaAttribute;

        // ── Technical Attributes (0.0 – 1.0) ─────────────────────────────────

        /// <summary>
        /// Passing quality. Affects pass success probability in DecisionSystem.
        /// Also affects DecisionSystem pass_score weighting (better passer = more pass attempts).
        /// </summary>
        public float PassingAbility;

        /// <summary>
        /// Shooting quality. Feeds into xG calculation: base_xG × (0.5 + ShootingAbility × 0.5).
        /// High ego striker with high shooting will take more shots from range.
        /// </summary>
        public float ShootingAbility;

        /// <summary>
        /// Dribbling quality. Affects dribble_score in DecisionSystem and
        /// success probability in CollisionSystem tackle contest.
        /// </summary>
        public float DribblingAbility;

        // ── Defensive Attributes (0.0 – 1.0) ─────────────────────────────────

        /// <summary>
        /// Defending quality. Used in CollisionSystem tackle contest:
        /// defender roll uses this, attacker roll uses DribblingAbility.
        /// </summary>
        public float DefendingAbility;

        /// <summary>
        /// Reaction speed. Affects intercept contest in CollisionSystem.
        /// High reactions = higher chance of reacting to loose ball or deflection.
        /// </summary>
        public float Reactions;

        // ── Personality ───────────────────────────────────────────────────────

        /// <summary>
        /// Ego 0.0–1.0. Biases DecisionSystem toward SHOOT over PASS.
        /// Visible effect: high-ego striker shoots from xG 0.08 positions,
        /// low-ego striker lays off to better-positioned teammate.
        /// </summary>
        public float Ego;
    }

    /// <summary>
    /// Static team definition. Loaded once before match start.
    /// Consumed by MatchEngine to build initial PlayerState[] array.
    /// Pure data — no methods.
    /// </summary>
    public class TeamData
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>0 = home, 1 = away. Must be unique per match.</summary>
        public int TeamId;

        /// <summary>Display name. Used in PostMatch UI and EventLog.</summary>
        public string Name;

        /// <summary>
        /// Kit primary colour as packed RGB integer (0xRRGGBB).
        /// Visualisation: PlayerDot.gd reads this to set dot colour.
        /// Example: 0xFF0000 = red home kit.
        /// </summary>
        public int KitColorPrimary;

        /// <summary>
        /// Kit secondary colour. Used for shirt number text contrast on PlayerDot.
        /// </summary>
        public int KitColorSecondary;

        // ── Formation ─────────────────────────────────────────────────────────

        /// <summary>
        /// Formation key used to look up anchor positions in FormationData.
        /// Must exactly match a key in FormationData.Formations dictionary.
        /// Examples: "4-3-3", "4-2-3-1", "3-5-2".
        /// </summary>
        public string FormationName;

        // ── Squad ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Exactly 11 players ordered by formation slot index.
        /// Slot 0 = GK, slots 1–4 = defenders (left to right),
        /// slots 5–N = midfield, remaining = forwards.
        /// The slot index must match FormationData anchor array order.
        /// </summary>
        public PlayerData[] Players;

        // ── Tactics ───────────────────────────────────────────────────────────

        /// <summary>
        /// All tactical slider values for this team.
        /// Copied from TacticsScreen before match start.
        /// Engine reads from here only — never from UI directly.
        /// </summary>
        public TacticsInput Tactics;

        // Currently AttacksDownward not intialized!
        public bool AttacksDownward;
    }
}