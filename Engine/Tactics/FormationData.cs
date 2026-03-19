// =============================================================================
// Module:  FormationData.cs
// Path:    FootballSim/Engine/Tactics/FormationData.cs
// Purpose:
//   Stores exactly 11 normalised anchor positions (Vec2 in 0.0–1.0 range) for
//   each formation. Positions are pitch-size independent — MovementSystem
//   multiplies by actual pitch dimensions (1050 × 680) to get world coordinates.
//
//   A FormationSlot pairs one anchor position with the PlayerRole assigned
//   to that slot. MatchEngine reads the slot list at match start to initialise
//   each PlayerState.FormationAnchor and PlayerState.Role.
//
//   Loaded from Data/Formations/*.json by FormationLoader.cs.
//   Stored in a static registry (FormationRegistry) for lookup by ID string.
//
// API:
//   FormationSlot   struct    { SlotIndex, Role, Anchor(Vec2 0-1) }
//   FormationData   class     { Id, DisplayName, ShapeLabel, Slots[11] }
//   FormationRegistry class   { Load(json[]), Get(id), All() }
//
// Anchor coordinate system:
//   X: 0.0 = left touchline, 1.0 = right touchline
//   Y: 0.0 = team's own goal line, 1.0 = opponent's goal line
//   Centre of pitch = Vec2(0.5, 0.5)
//   ALL anchors are described as if the team attacks toward Y=1.0.
//   MovementSystem flips Y for the away team: anchorY_away = 1.0 - anchorY_home
//
// Slot ordering contract (must match TeamData.Players[] order):
//   Slot 0       = GK
//   Slots 1–4    = Defenders (right to left: slot 1 = rightmost)
//   Slots 5–N    = Midfielders (right to left or as formation dictates)
//   Remaining    = Forwards (right to left)
//   This ordering is documented in each formation's JSON and enforced here.
//
// Dependencies:
//   Engine/Models/Vec2.cs       (anchor positions)
//   Engine/Models/Enums.cs      (PlayerRole)
//
// Notes:
//   • Anchors are reference positions only. In possession, roles drift from
//     the anchor per RoleDefinition tendencies. Out of possession, defenders
//     compress toward a compact block — the anchor Y shifts based on
//     TacticsInput.DefensiveLine (handled in MovementSystem, not here).
//   • FormationRegistry is populated once at startup by FormationLoader.
//     It is a static dictionary — no instance needed.
//   • Id is the stable key used everywhere (TeamData.FormationName, UI dropdowns).
//     DisplayName is display only and can be localised later.
// =============================================================================

using System.Collections.Generic;
using FootballSim.Engine.Models;

namespace FootballSim.Engine.Tactics
{
    /// <summary>
    /// One slot in a formation: a position index, the assigned role, and the
    /// normalised anchor Vec2. Pure data — no methods.
    /// </summary>
    public struct FormationSlot
    {
        /// <summary>
        /// Index 0–10. Must match the corresponding PlayerData index in TeamData.Players[].
        /// Slot 0 = GK. Enforced by MatchEngine at match start validation.
        /// </summary>
        public int SlotIndex;

        /// <summary>
        /// The tactical role this slot represents. Loaded from JSON, must be a valid
        /// PlayerRole enum name (case-sensitive match in FormationLoader).
        /// </summary>
        public PlayerRole Role;

        /// <summary>
        /// Normalised anchor position. X and Y in [0.0, 1.0].
        /// X: 0.0 = left touchline, 1.0 = right touchline.
        /// Y: 0.0 = own goal line, 1.0 = opponent's goal line.
        /// Away team Y is flipped by MovementSystem at runtime.
        /// </summary>
        public Vec2 Anchor;
    }

    /// <summary>
    /// Complete formation definition: 11 slots with normalised anchors and roles.
    /// Loaded from JSON, stored in FormationRegistry. Pure data — no methods.
    /// </summary>
    public class FormationData
    {
        /// <summary>
        /// Stable string key used in TeamData.FormationName and all lookups.
        /// Must exactly match the JSON filename stem and the registry key.
        /// Examples: "4-3-3", "4-2-3-1", "3-5-2", "5-4-1"
        /// Never change this after initial definition — it is stored in saved matches.
        /// </summary>
        public string Id;

        /// <summary>
        /// Human-readable name shown in TacticsScreen formation picker.
        /// Can differ from Id for display purposes. Localisable.
        /// Examples: "4-3-3 (Attack)", "4-2-3-1 (Wide)"
        /// </summary>
        public string DisplayName;

        /// <summary>
        /// Short shape descriptor for UI badge display.
        /// Examples: "4-3-3", "4-2-3-1", "3-5-2"
        /// </summary>
        public string ShapeLabel;

        /// <summary>
        /// Exactly 11 slots. Index 0 = GK. Ordered by SlotIndex ascending.
        /// FormationLoader validates count == 11 and SlotIndex unique 0–10.
        /// </summary>
        public FormationSlot[] Slots;
    }

    /// <summary>
    /// Static registry of all loaded formations. Populated once at startup
    /// by FormationLoader. Thread-safe reads after load (no writes during match).
    /// </summary>
    public static class FormationRegistry
    {
        private static readonly System.Collections.Generic.Dictionary<string, FormationData>
            _formations = new System.Collections.Generic.Dictionary<string, FormationData>();

        private static bool _loaded = false;

        /// <summary>
        /// Register a formation. Called only by FormationLoader during startup.
        /// Throws if duplicate Id detected.
        /// </summary>
        public static void Register(FormationData formation)
        {
            if (_formations.ContainsKey(formation.Id))
                throw new System.Exception(
                    $"[FormationRegistry] Duplicate formation Id: '{formation.Id}'. " +
                    $"Check Data/Formations/ for duplicate JSON files.");
            _formations[formation.Id] = formation;
        }

        /// <summary>
        /// Mark registry as fully loaded. Called by FormationLoader after all files parsed.
        /// </summary>
        public static void MarkLoaded() => _loaded = true;

        /// <summary>
        /// Returns the FormationData for the given Id.
        /// Throws a descriptive exception if not found — surfaces JSON naming errors early.
        /// </summary>
        public static FormationData Get(string id)
        {
            if (!_loaded)
                throw new System.Exception(
                    "[FormationRegistry] Registry not loaded. Call FormationLoader.LoadAll() at startup.");
            if (!_formations.TryGetValue(id, out var formation))
                throw new System.Exception(
                    $"[FormationRegistry] Formation '{id}' not found. " +
                    $"Available: {string.Join(", ", _formations.Keys)}");
            return formation;
        }

        /// <summary>
        /// Returns all registered formations. Used to populate TacticsScreen dropdown.
        /// </summary>
        public static IEnumerable<FormationData> All() => _formations.Values;

        /// <summary>
        /// True after FormationLoader.LoadAll() has completed successfully.
        /// </summary>
        public static bool IsLoaded => _loaded;
    }
}