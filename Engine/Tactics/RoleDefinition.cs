// =============================================================================
// Module:  RoleDefinition.cs
// Path:    FootballSim/Engine/Tactics/RoleDefinition.cs
// Purpose:
//   Describes the spatial and decision-making tendencies of a PlayerRole using
//   normalised weight/bias values (0.0–1.0). These weights are multiplied into
//   DecisionSystem scoring and MovementSystem anchor offset calculations.
//
//   There is exactly ONE RoleDefinition per PlayerRole enum value.
//   Loaded from Data/Roles/roles.json by RoleLoader.cs.
//   Stored in RoleRegistry for lookup by PlayerRole.
//
//   A RoleDefinition answers two questions:
//     1. WHERE does this role move on the pitch?
//        (InPossessionDrift, OutOfPossessionReturnDepth, WidthBias, DepthBias)
//     2. WHAT does this role prefer to do when it has a decision?
//        (PassBias, ShootBias, DribbleBias, PressBias, SupportBias, etc.)
//
//   These are STATIC DEFAULTS. TacticsInput sliders and FreedomLevel modify
//   how strongly these defaults apply at runtime — that modification happens
//   in DecisionSystem and MovementSystem, not here.
//
// API:
//   RoleDefinition  class    { Role, DisplayName, all tendency floats }
//   RoleRegistry    class    { Load(list), Get(role), All() }
//
// Tendency field definitions (all float 0.0–1.0):
//
//   ── MOVEMENT TENDENCIES ────────────────────────────────────────────────────
//   InPossessionDriftX     How far this role drifts horizontally in possession.
//                          0.0 = stays exactly on anchor X
//                          1.0 = drifts maximally (e.g. IW drifts to centre from wide)
//                          Direction of drift is role-specific (see DriftDirectionInward)
//
//   InPossessionDriftY     How far this role moves forward in possession.
//                          0.0 = stays on anchor Y (e.g. CDM holds position)
//                          1.0 = pushes maximally forward (e.g. WB overlaps to byline)
//
//   DriftDirectionInward   True if horizontal drift is toward the centre (IW, CF).
//                          False if drift is toward the touchline (WB, WF).
//                          Used by MovementSystem to determine drift direction.
//
//   OutOfPossessionReturnY How deep this role drops when team loses possession.
//                          0.0 = stays high (PF, ST press from front)
//                          1.0 = drops very deep (CDM, CB return toward own goal)
//
//   WidthBias              Preference for wide positions vs central.
//                          0.0 = always central (CDM, AM, CF)
//                          1.0 = always wide (WB, WF, IW starting position)
//
//   RunInBehindTendency    How often this role makes runs in behind the defensive line.
//                          0.0 = never makes runs behind (CB, CDM)
//                          1.0 = constantly makes runs behind (ST, PF, IW)
//
//   DropDeepTendency       How often this role drops into deeper areas to receive.
//                          0.0 = never drops (pure ST)
//                          1.0 = always drops (CF, DLP)
//
//   OverlapRunTendency     How often this role makes overlap runs past teammates.
//                          0.0 = never overlaps
//                          1.0 = always looks to overlap (WB, BBM)
//
//   ── DECISION WEIGHTS ──────────────────────────────────────────────────────
//   PassBias               Tendency to choose pass over other options when has ball.
//                          0.0 = rarely passes (high-ego ST)
//                          1.0 = almost always passes (DLP, CDM)
//
//   ShootBias              Tendency to shoot when in range.
//                          0.0 = rarely shoots (CB, CDM, WB)
//                          1.0 = shoots whenever in range (ST, AM, IW from inside)
//
//   DribbleBias            Tendency to attempt dribble when under light pressure.
//                          0.0 = never dribbles (GK, CB in deep positions)
//                          1.0 = always tries to dribble (IW, ST on edge of box)
//
//   CrossBias              Tendency to cross when reaching wide area with ball.
//                          0.0 = never crosses (CDM, CB)
//                          1.0 = always crosses (WF, WB in crossing positions)
//
//   LongPassBias           Tendency to play long passes vs short recycling.
//                          0.0 = short only (CDM recycling, CB playing out)
//                          1.0 = long ball preference (DLP switches, BPD diagonals)
//
//   PressBias              Willingness to press opponents off the ball.
//                          0.0 = rarely presses (SK, DLP save energy)
//                          1.0 = presses aggressively whenever trigger met (PF, BBM)
//                          Multiplied by TacticsInput.PressingIntensity at runtime.
//
//   SupportBias            Priority placed on finding a support position for teammate.
//                          0.0 = focuses on own threat (ST waits for ball)
//                          1.0 = constant movement to create passing options (CM, BBM)
//
//   HoldPositionBias       Tendency to hold shape over free movement.
//                          0.0 = free roaming (Expressive roles like CF, IW)
//                          1.0 = strict position-holder (CDM anchor, CB compact)
//                          Multiplied by (1.0 - TacticsInput.FreedomLevel) at runtime.
//
//   ── PHYSICALITY ────────────────────────────────────────────────────────────
//   AerialDuelTendency     Preference for contesting aerial balls.
//                          0.0 = avoids aerial contests (technical wide players)
//                          1.0 = seeks aerial duels (target ST, aerial CB)
//
//   TackleCommitTendency   Willingness to commit to a tackle vs. shadow/jockey.
//                          0.0 = always jockeys, delays, stays on feet
//                          1.0 = commits to tackle immediately in range
//
//   ── STAMINA USAGE ─────────────────────────────────────────────────────────
//   StaminaExpenditureBias How hard this role pushes physically vs. conserves energy.
//                          0.0 = very energy-efficient (SK, DLP save stamina)
//                          1.0 = high stamina consumer (PF, BBM sprint constantly)
//                          Applied as a multiplier to stamina drain in MovementSystem.
//
//   ── SET PIECES ─────────────────────────────────────────────────────────────
//   SetPieceAttackRole     Tendency to attack set pieces (corners, free kicks).
//                          0.0 = stays back for balance
//                          1.0 = always attacks set pieces (target ST, aerial CB)
//
//   SetPieceDeliveryBias   Preference for delivering set pieces when assigned.
//                          0.0 = not a set piece taker
//                          1.0 = primary set piece deliverer for this role type
//
// Dependencies:
//   Engine/Models/Enums.cs  (PlayerRole)
//
// Notes:
//   • These are DEFAULTS. FreedomLevel in TacticsInput scales how much the engine
//     respects these weights vs. pure player-attribute-driven decisions.
//     At FreedomLevel=1.0 (Expressive), role weights are nearly ignored.
//     At FreedomLevel=0.0 (Strict), role weights dominate every decision.
//   • No logic here. DecisionSystem and MovementSystem are the consumers.
//   • RoleRegistry is populated once at startup. No writes during a match.
// =============================================================================

using FootballSim.Engine.Models;

namespace FootballSim.Engine.Tactics
{
    /// <summary>
    /// All tendency weights for one PlayerRole. Pure data — no methods, no
    /// runtime state. Written once from JSON at startup, read every tick by
    /// DecisionSystem and MovementSystem.
    /// </summary>
    public class RoleDefinition
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>
        /// The PlayerRole this definition belongs to.
        /// Used as the registry lookup key. Must be unique across all definitions.
        /// </summary>
        public PlayerRole Role;

        /// <summary>
        /// Human-readable name for TacticsScreen role picker.
        /// Examples: "Inverted Winger", "Box-to-Box Midfielder", "Sweeper Keeper"
        /// </summary>
        public string DisplayName;

        /// <summary>
        /// Short description shown in TacticsScreen tooltip.
        /// Example: "Drifts inside from wide, looks to shoot or play the killer pass."
        /// </summary>
        public string Description;

        // ── Movement Tendencies ───────────────────────────────────────────────

        /// <summary>How far this role drifts horizontally from anchor in possession. 0=stays, 1=max drift.</summary>
        public float InPossessionDriftX;

        /// <summary>How far this role pushes forward from anchor in possession. 0=holds, 1=max push.</summary>
        public float InPossessionDriftY;

        /// <summary>True = drift toward centre (IW, CF). False = drift toward touchline (WB, WF).</summary>
        public bool DriftDirectionInward;

        /// <summary>How deep the role drops when team loses possession. 0=stays high, 1=very deep.</summary>
        public float OutOfPossessionReturnY;

        /// <summary>Preference for wide vs central positions. 0=central, 1=always wide.</summary>
        public float WidthBias;

        /// <summary>Frequency of runs in behind defensive line. 0=never, 1=constantly.</summary>
        public float RunInBehindTendency;

        /// <summary>Frequency of dropping deep to receive. 0=never drops, 1=always drops.</summary>
        public float DropDeepTendency;

        /// <summary>Frequency of overlap runs past teammates. 0=never, 1=always overlaps.</summary>
        public float OverlapRunTendency;

        // ── Decision Weights ──────────────────────────────────────────────────

        /// <summary>Tendency to pass when has ball. 0=rarely, 1=almost always.</summary>
        public float PassBias;

        /// <summary>Tendency to shoot when in range. 0=rarely, 1=always shoots in range.</summary>
        public float ShootBias;

        /// <summary>Tendency to dribble under light pressure. 0=never, 1=always tries.</summary>
        public float DribbleBias;

        /// <summary>Tendency to cross from wide area. 0=never, 1=always crosses.</summary>
        public float CrossBias;

        /// <summary>Preference for long passes vs short. 0=short only, 1=long ball preference.</summary>
        public float LongPassBias;

        /// <summary>Willingness to press when trigger met. 0=passive, 1=aggressive presser.</summary>
        public float PressBias;

        /// <summary>Priority on finding support positions. 0=waits, 1=constant movement.</summary>
        public float SupportBias;

        /// <summary>Tendency to hold shape vs free roam. 0=free roam, 1=strict holder.</summary>
        public float HoldPositionBias;

        // ── Physicality ───────────────────────────────────────────────────────

        /// <summary>Preference for contesting aerial balls. 0=avoids, 1=seeks headers.</summary>
        public float AerialDuelTendency;

        /// <summary>Willingness to commit to tackles. 0=always jockeys, 1=commits immediately.</summary>
        public float TackleCommitTendency;

        // ── Stamina ───────────────────────────────────────────────────────────

        /// <summary>How hard this role pushes physically. 0=conserves energy, 1=sprints constantly.</summary>
        public float StaminaExpenditureBias;

        // ── Set Pieces ────────────────────────────────────────────────────────

        /// <summary>Tendency to attack set pieces. 0=stays back, 1=always attacks.</summary>
        public float SetPieceAttackRole;

        /// <summary>Preference for delivering set pieces. 0=not a taker, 1=primary taker type.</summary>
        public float SetPieceDeliveryBias;
    }

    /// <summary>
    /// Static registry of all RoleDefinition instances. One entry per PlayerRole.
    /// Populated once at startup by RoleLoader. No writes during match.
    /// </summary>
    public static class RoleRegistry
    {
        private static readonly System.Collections.Generic.Dictionary<PlayerRole, RoleDefinition>
            _roles = new System.Collections.Generic.Dictionary<PlayerRole, RoleDefinition>();

        private static bool _loaded = false;

        /// <summary>
        /// Register a role definition. Called only by RoleLoader during startup.
        /// Throws on duplicate Role.
        /// </summary>
        public static void Register(RoleDefinition definition)
        {
            if (_roles.ContainsKey(definition.Role))
                throw new System.Exception(
                    $"[RoleRegistry] Duplicate RoleDefinition for role: '{definition.Role}'. " +
                    $"Check Data/Roles/roles.json for duplicate entries.");
            _roles[definition.Role] = definition;
        }

        /// <summary>Mark registry fully loaded after all roles parsed.</summary>
        public static void MarkLoaded() => _loaded = true;

        /// <summary>
        /// Returns the RoleDefinition for the given PlayerRole.
        /// Throws if not found — surfaces missing role definitions early.
        /// </summary>
        public static RoleDefinition Get(PlayerRole role)
        {
            if (!_loaded)
                throw new System.Exception(
                    "[RoleRegistry] Registry not loaded. Call RoleLoader.LoadAll() at startup.");
            if (!_roles.TryGetValue(role, out var def))
                throw new System.Exception(
                    $"[RoleRegistry] No RoleDefinition found for role '{role}'. " +
                    $"Add an entry for this role in Data/Roles/roles.json.");
            return def;
        }

        /// <summary>Returns all registered role definitions. Used by TacticsScreen role picker.</summary>
        public static System.Collections.Generic.IEnumerable<RoleDefinition> All() => _roles.Values;

        /// <summary>True after RoleLoader.LoadAll() completed successfully.</summary>
        public static bool IsLoaded => _loaded;
    }
}