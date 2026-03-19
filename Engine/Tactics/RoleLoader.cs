// =============================================================================
// Module:  RoleLoader.cs
// Path:    FootballSim/Engine/Tactics/RoleLoader.cs
// Purpose:
//   Reads Data/Roles/roles.json and populates RoleRegistry with one
//   RoleDefinition per PlayerRole enum value. Pure JSON → struct conversion.
//   Does NOT modify engine state. Called once at startup.
//
// API:
//   RoleLoader.LoadAll(string dataFolderPath) → void
//     Reads dataFolderPath/Roles/roles.json, parses all role entries,
//     validates each, registers in RoleRegistry.
//     Also validates that ALL PlayerRole enum values have a definition —
//     missing roles are a hard error (would cause NullRef at runtime).
//
//   RoleLoader.LoadFromJson(string json, string sourceLabel) → List<RoleDefinition>
//     Parses a JSON array of role definitions. Returns list. Does NOT register.
//
// JSON format (roles.json — single file with all roles as an array):
// [
//   {
//     "role":                   "IW",
//     "displayName":            "Inverted Winger",
//     "description":            "Drifts inside from wide, looks to shoot or play the killer pass.",
//     "inPossessionDriftX":     0.80,
//     "inPossessionDriftY":     0.50,
//     "driftDirectionInward":   true,
//     "outOfPossessionReturnY": 0.40,
//     "widthBias":              0.75,
//     "runInBehindTendency":    0.65,
//     "dropDeepTendency":       0.15,
//     "overlapRunTendency":     0.10,
//     "passBias":               0.55,
//     "shootBias":              0.70,
//     "dribbleBias":            0.75,
//     "crossBias":              0.10,
//     "longPassBias":           0.20,
//     "pressBias":              0.50,
//     "supportBias":            0.55,
//     "holdPositionBias":       0.25,
//     "aerialDuelTendency":     0.30,
//     "tackleCommitTendency":   0.35,
//     "staminaExpenditureBias": 0.65,
//     "setPieceAttackRole":     0.55,
//     "setPieceDeliveryBias":   0.10
//   },
//   ... (one entry per PlayerRole enum value)
// ]
//
// Validation rules:
//   • "role" must parse to valid PlayerRole enum name
//   • All float fields must be present and in [0.0, 1.0]
//   • "driftDirectionInward" must be a JSON bool
//   • After loading, every PlayerRole enum value must have exactly one entry
//
// Dependencies:
//   Engine/Models/Enums.cs            (PlayerRole)
//   Engine/Tactics/RoleDefinition.cs  (RoleDefinition, RoleRegistry)
//   System.Text.Json
//
// Notes:
//   • Unlike formations (one file per formation), roles are all in one file.
//   • All-in-one file makes it easier to compare roles side-by-side when tuning.
//   • DEBUG flag logs each loaded role name and key bias values.
//   • Missing PlayerRole enum entry is a hard startup crash — intentional.
//     Every role must be defined or DecisionSystem will throw at runtime.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FootballSim.Engine.Models;

namespace FootballSim.Engine.Tactics
{
    /// <summary>
    /// Reads roles.json and populates RoleRegistry.
    /// Pure JSON → data conversion. No engine state modification.
    /// </summary>
    public static class RoleLoader
    {
        // ── DEBUG ─────────────────────────────────────────────────────────────

        /// <summary>
        /// When true, logs each loaded role with key tendency values.
        /// Set false for release builds.
        /// </summary>
        public static bool DEBUG = false;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Loads Data/Roles/roles.json and registers all role definitions.
        /// Validates that every PlayerRole enum value has exactly one definition.
        /// Throws descriptive exception on any error.
        /// </summary>
        /// <param name="dataFolderPath">Absolute path to Data/ folder.</param>
        public static void LoadAll(string dataFolderPath)
        {
            string rolesPath = Path.Combine(dataFolderPath, "Roles", "roles.json");

            if (!File.Exists(rolesPath))
                throw new Exception(
                    $"[RoleLoader] roles.json not found at: '{rolesPath}'. " +
                    $"Create Data/Roles/roles.json with one entry per PlayerRole enum value.");

            string json = File.ReadAllText(rolesPath);

            if (DEBUG)
                Console.WriteLine($"[RoleLoader] Loading roles from '{rolesPath}'");

            List<RoleDefinition> definitions = LoadFromJson(json, "roles.json");

            foreach (var def in definitions)
            {
                RoleRegistry.Register(def);
                if (DEBUG)
                    Console.WriteLine(
                        $"[RoleLoader] Loaded role '{def.Role}' ({def.DisplayName}) " +
                        $"| press={def.PressBias:F2} shoot={def.ShootBias:F2} " +
                        $"pass={def.PassBias:F2} support={def.SupportBias:F2}");
            }

            // ── Validate completeness — every enum value must have a definition ──
            var missingRoles = new List<string>();
            foreach (PlayerRole role in Enum.GetValues(typeof(PlayerRole)))
            {
                try { RoleRegistry.Get(role); }
                catch { missingRoles.Add(role.ToString()); }
            }

            if (missingRoles.Count > 0)
                throw new Exception(
                    $"[RoleLoader] Missing role definitions in roles.json for: " +
                    $"{string.Join(", ", missingRoles)}. " +
                    $"Every PlayerRole enum value must have exactly one entry.");

            RoleRegistry.MarkLoaded();

            if (DEBUG)
                Console.WriteLine(
                    $"[RoleLoader] All {definitions.Count} roles loaded and validated.");
        }

        /// <summary>
        /// Parses a JSON array string into a list of RoleDefinition.
        /// Does NOT register — caller handles registration.
        /// Validates all required fields and bounds.
        /// </summary>
        public static List<RoleDefinition> LoadFromJson(string json, string sourceLabel)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new Exception(
                    $"[RoleLoader] Invalid JSON in '{sourceLabel}': {ex.Message}");
            }

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new Exception(
                    $"[RoleLoader] '{sourceLabel}': Root element must be a JSON array of role objects.");

            var result = new List<RoleDefinition>();
            int index  = 0;

            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                string src = $"{sourceLabel}[{index}]";

                string roleStr     = ReadRequiredString(el, "role",        src);
                string displayName = ReadRequiredString(el, "displayName", src);
                string description = ReadOptionalString(el, "description", "");

                if (!Enum.TryParse<PlayerRole>(roleStr, out PlayerRole role))
                    throw new Exception(
                        $"[RoleLoader] '{src}': Unknown role '{roleStr}'. " +
                        $"Must match a PlayerRole enum value exactly.");

                var def = new RoleDefinition
                {
                    Role        = role,
                    DisplayName = displayName,
                    Description = description,

                    // Movement tendencies
                    InPossessionDriftX       = ReadRequiredFloat01(el, "inPossessionDriftX",       src),
                    InPossessionDriftY       = ReadRequiredFloat01(el, "inPossessionDriftY",       src),
                    DriftDirectionInward     = ReadRequiredBool(el,   "driftDirectionInward",      src),
                    OutOfPossessionReturnY   = ReadRequiredFloat01(el, "outOfPossessionReturnY",   src),
                    WidthBias                = ReadRequiredFloat01(el, "widthBias",                src),
                    RunInBehindTendency      = ReadRequiredFloat01(el, "runInBehindTendency",      src),
                    DropDeepTendency         = ReadRequiredFloat01(el, "dropDeepTendency",         src),
                    OverlapRunTendency       = ReadRequiredFloat01(el, "overlapRunTendency",       src),

                    // Decision weights
                    PassBias                 = ReadRequiredFloat01(el, "passBias",                 src),
                    ShootBias                = ReadRequiredFloat01(el, "shootBias",                src),
                    DribbleBias              = ReadRequiredFloat01(el, "dribbleBias",              src),
                    CrossBias                = ReadRequiredFloat01(el, "crossBias",                src),
                    LongPassBias             = ReadRequiredFloat01(el, "longPassBias",             src),
                    PressBias                = ReadRequiredFloat01(el, "pressBias",                src),
                    SupportBias              = ReadRequiredFloat01(el, "supportBias",              src),
                    HoldPositionBias         = ReadRequiredFloat01(el, "holdPositionBias",         src),

                    // Physicality
                    AerialDuelTendency       = ReadRequiredFloat01(el, "aerialDuelTendency",       src),
                    TackleCommitTendency     = ReadRequiredFloat01(el, "tackleCommitTendency",     src),

                    // Stamina
                    StaminaExpenditureBias   = ReadRequiredFloat01(el, "staminaExpenditureBias",   src),

                    // Set pieces
                    SetPieceAttackRole       = ReadRequiredFloat01(el, "setPieceAttackRole",       src),
                    SetPieceDeliveryBias     = ReadRequiredFloat01(el, "setPieceDeliveryBias",     src),
                };

                result.Add(def);
                index++;
            }

            return result;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static string ReadRequiredString(JsonElement el, string key, string source)
        {
            if (!el.TryGetProperty(key, out JsonElement val) ||
                val.ValueKind != JsonValueKind.String)
                throw new Exception(
                    $"[RoleLoader] '{source}': Missing or non-string field '{key}'.");
            return val.GetString() ?? string.Empty;
        }

        private static string ReadOptionalString(JsonElement el, string key, string fallback)
        {
            if (el.TryGetProperty(key, out JsonElement val) &&
                val.ValueKind == JsonValueKind.String)
                return val.GetString() ?? fallback;
            return fallback;
        }

        private static bool ReadRequiredBool(JsonElement el, string key, string source)
        {
            if (!el.TryGetProperty(key, out JsonElement val))
                throw new Exception(
                    $"[RoleLoader] '{source}': Missing boolean field '{key}'.");
            if (val.ValueKind == JsonValueKind.True)  return true;
            if (val.ValueKind == JsonValueKind.False) return false;
            throw new Exception(
                $"[RoleLoader] '{source}': Field '{key}' must be a JSON boolean (true/false).");
        }

        private static float ReadRequiredFloat01(JsonElement el, string key, string source)
        {
            if (!el.TryGetProperty(key, out JsonElement val) ||
                val.ValueKind != JsonValueKind.Number)
                throw new Exception(
                    $"[RoleLoader] '{source}': Missing or non-numeric field '{key}'.");
            float f = val.GetSingle();
            if (f < 0f || f > 1f)
                throw new Exception(
                    $"[RoleLoader] '{source}': Field '{key}' value {f} is out of [0.0, 1.0]. " +
                    $"All role tendency values must be normalised 0–1.");
            return f;
        }
    }
}