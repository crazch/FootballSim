// =============================================================================
// Module:  GamePlanLoader.cs
// Path:    FootballSim/Engine/Tactics/GamePlanLoader.cs
// Purpose:
//   Reads Data/GamePlans/game_plans.json and registers parsed GamePlan entries
//   into GamePlanRegistry, overriding the hardcoded fallback defaults.
//   Pure JSON → data struct conversion. No behaviour, no engine state.
//   Called once at startup, AFTER GamePlanRegistry.SeedDefaults().
//
//   This separation means:
//     1. Game works immediately with hardcoded defaults if JSON is missing.
//     2. JSON file can override any preset with designer-tuned values.
//     3. Designers can add custom plan IDs that don't exist in code.
//
// API:
//   GamePlanLoader.LoadAll(string dataFolderPath) → void
//     Reads dataFolderPath/GamePlans/game_plans.json.
//     If file is missing, logs a warning and returns (fallbacks still active).
//     Parses each plan, calls GamePlanRegistry.Register() (overwrite allowed).
//
//   GamePlanLoader.LoadFromJson(string json, string sourceLabel) → List<GamePlan>
//     Parses a JSON array into GamePlan list. Does NOT register. Caller registers.
//
// JSON format (game_plans.json — array of plan objects):
// [
//   {
//     "id":          "gegenpress",
//     "displayName": "Gegenpress",
//     "description": "Intense press after every loss of possession...",
//     "tactics": {
//       "pressingIntensity":    0.95,
//       "pressingTrigger":      0.90,
//       "pressCompactness":     0.75,
//       "defensiveLine":        0.80,
//       "defensiveWidth":       0.55,
//       "defensiveAggression":  0.70,
//       "possessionFocus":      0.55,
//       "buildUpSpeed":         0.85,
//       "passingDirectness":    0.55,
//       "attackingWidth":       0.60,
//       "attackingLine":        0.80,
//       "transitionSpeed":      0.95,
//       "crossingFrequency":    0.45,
//       "shootingThreshold":    0.40,
//       "tempo":                0.90,
//       "outOfPossessionShape": 0.80,
//       "inPossessionSpread":   0.60,
//       "freedomLevel":         0.55,
//       "counterAttackFocus":   0.70,
//       "offsideTrapFrequency": 0.60,
//       "physicalityBias":      0.55,
//       "setPieceFocus":        0.50
//     }
//   }
// ]
//
// Validation rules:
//   • "id", "displayName" are required strings
//   • "tactics" is required and must contain ALL TacticsInput fields
//   • All tactics float values must be in [0.0, 1.0]
//   • Unknown tactic keys are IGNORED with a debug warning (future-proofing)
//
// Dependencies:
//   Engine/Tactics/TacticsInput.cs  (TacticsInput struct — all field names)
//   Engine/Tactics/GamePlan.cs      (GamePlan class, GamePlanRegistry)
//   System.Text.Json
//
// Notes:
//   • JSON field names use camelCase to match C# property convention.
//   • Missing JSON file is a warning, not a crash — hardcoded fallbacks cover it.
//   • Missing individual tactics field in JSON IS a warning + uses 0.5 default —
//     allows partial overrides in JSON without listing every slider.
//   • DEBUG flag logs each plan loaded with a summary of key slider values.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FootballSim.Engine.Tactics
{
    /// <summary>
    /// Reads game_plans.json and registers parsed plans into GamePlanRegistry.
    /// Overrides hardcoded defaults with JSON-authored values.
    /// Gracefully skips if file is missing (fallbacks remain active).
    /// </summary>
    public static class GamePlanLoader
    {
        // ── DEBUG ─────────────────────────────────────────────────────────────

        /// <summary>
        /// When true, logs each loaded game plan with key slider values.
        /// Set false for release builds.
        /// </summary>
        public static bool DEBUG = false;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Loads Data/GamePlans/game_plans.json and overrides GamePlanRegistry.
        /// If the file is missing, emits a debug warning and returns gracefully —
        /// hardcoded fallback presets (seeded by GamePlanRegistry.SeedDefaults()) remain.
        /// Throws only on malformed JSON or validation errors.
        /// </summary>
        /// <param name="dataFolderPath">Absolute path to Data/ folder.</param>
        public static void LoadAll(string dataFolderPath)
        {
            string plansPath = Path.Combine(dataFolderPath, "GamePlans", "game_plans.json");

            if (!File.Exists(plansPath))
            {
                if (DEBUG)
                    Console.WriteLine(
                        $"[GamePlanLoader] game_plans.json not found at '{plansPath}'. " +
                        $"Using hardcoded fallback presets. This is fine for development.");
                return;
            }

            string json = File.ReadAllText(plansPath);

            if (DEBUG)
                Console.WriteLine($"[GamePlanLoader] Loading game plans from '{plansPath}'");

            List<GamePlan> plans = LoadFromJson(json, "game_plans.json");

            foreach (var plan in plans)
            {
                GamePlanRegistry.Register(plan); // Overwrites fallback if same Id
                if (DEBUG)
                    Console.WriteLine(
                        $"[GamePlanLoader] Loaded '{plan.Id}' ({plan.DisplayName}) " +
                        $"| press={plan.Tactics.PressingIntensity:F2} " +
                        $"poss={plan.Tactics.PossessionFocus:F2} " +
                        $"tempo={plan.Tactics.Tempo:F2} " +
                        $"line={plan.Tactics.DefensiveLine:F2}");
            }

            if (DEBUG)
                Console.WriteLine($"[GamePlanLoader] {plans.Count} game plan(s) loaded from JSON.");
        }

        /// <summary>
        /// Parses a JSON array of game plan objects into a list of GamePlan.
        /// Does NOT register — caller handles registration.
        /// Missing float fields in "tactics" object use 0.5 as default (not an error).
        /// </summary>
        public static List<GamePlan> LoadFromJson(string json, string sourceLabel)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new Exception(
                    $"[GamePlanLoader] Invalid JSON in '{sourceLabel}': {ex.Message}");
            }

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new Exception(
                    $"[GamePlanLoader] '{sourceLabel}': Root must be a JSON array of game plan objects.");

            var result = new List<GamePlan>();
            int index  = 0;

            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                string src = $"{sourceLabel}[{index}]";

                string id          = ReadRequiredString(el, "id",          src);
                string displayName = ReadRequiredString(el, "displayName", src);
                string description = ReadOptionalString(el, "description", "");

                if (!el.TryGetProperty("tactics", out JsonElement tacticsEl) ||
                    tacticsEl.ValueKind != JsonValueKind.Object)
                    throw new Exception(
                        $"[GamePlanLoader] '{src}': Missing or non-object 'tactics' field.");

                TacticsInput tactics = ParseTacticsInput(tacticsEl, $"{src}.tactics");

                result.Add(new GamePlan
                {
                    Id          = id,
                    DisplayName = displayName,
                    Description = description,
                    Tactics     = tactics,
                });

                index++;
            }

            return result;
        }

        // ── Private: TacticsInput parser ──────────────────────────────────────
        // Each field is optional in JSON — missing fields default to 0.5.
        // This allows partial overrides: only specify sliders you want to change.
        // Unknown fields are silently ignored (forward-compatibility).

        private static TacticsInput ParseTacticsInput(JsonElement el, string source)
        {
            return new TacticsInput
            {
                PressingIntensity    = ReadOptionalFloat01(el, "pressingIntensity",    0.5f, source),
                PressingTrigger      = ReadOptionalFloat01(el, "pressingTrigger",      0.5f, source),
                PressCompactness     = ReadOptionalFloat01(el, "pressCompactness",     0.5f, source),
                DefensiveLine        = ReadOptionalFloat01(el, "defensiveLine",        0.5f, source),
                DefensiveWidth       = ReadOptionalFloat01(el, "defensiveWidth",       0.5f, source),
                DefensiveAggression  = ReadOptionalFloat01(el, "defensiveAggression",  0.5f, source),
                PossessionFocus      = ReadOptionalFloat01(el, "possessionFocus",      0.5f, source),
                BuildUpSpeed         = ReadOptionalFloat01(el, "buildUpSpeed",         0.5f, source),
                PassingDirectness    = ReadOptionalFloat01(el, "passingDirectness",    0.5f, source),
                AttackingWidth       = ReadOptionalFloat01(el, "attackingWidth",       0.5f, source),
                AttackingLine        = ReadOptionalFloat01(el, "attackingLine",        0.5f, source),
                TransitionSpeed      = ReadOptionalFloat01(el, "transitionSpeed",      0.5f, source),
                CrossingFrequency    = ReadOptionalFloat01(el, "crossingFrequency",    0.5f, source),
                ShootingThreshold    = ReadOptionalFloat01(el, "shootingThreshold",    0.5f, source),
                Tempo                = ReadOptionalFloat01(el, "tempo",                0.5f, source),
                OutOfPossessionShape = ReadOptionalFloat01(el, "outOfPossessionShape", 0.5f, source),
                InPossessionSpread   = ReadOptionalFloat01(el, "inPossessionSpread",   0.5f, source),
                FreedomLevel         = ReadOptionalFloat01(el, "freedomLevel",         0.5f, source),
                CounterAttackFocus   = ReadOptionalFloat01(el, "counterAttackFocus",   0.5f, source),
                OffsideTrapFrequency = ReadOptionalFloat01(el, "offsideTrapFrequency", 0.2f, source),
                PhysicalityBias      = ReadOptionalFloat01(el, "physicalityBias",      0.5f, source),
                SetPieceFocus        = ReadOptionalFloat01(el, "setPieceFocus",        0.5f, source),
            };
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static string ReadRequiredString(JsonElement el, string key, string source)
        {
            if (!el.TryGetProperty(key, out JsonElement val) ||
                val.ValueKind != JsonValueKind.String)
                throw new Exception(
                    $"[GamePlanLoader] '{source}': Missing or non-string field '{key}'.");
            return val.GetString() ?? string.Empty;
        }

        private static string ReadOptionalString(JsonElement el, string key, string fallback)
        {
            if (el.TryGetProperty(key, out JsonElement val) &&
                val.ValueKind == JsonValueKind.String)
                return val.GetString() ?? fallback;
            return fallback;
        }

        private static float ReadOptionalFloat01(
            JsonElement el, string key, float defaultValue, string source)
        {
            if (!el.TryGetProperty(key, out JsonElement val))
            {
                if (DEBUG)
                    Console.WriteLine(
                        $"[GamePlanLoader] '{source}': Optional field '{key}' not found. " +
                        $"Using default {defaultValue}.");
                return defaultValue;
            }

            if (val.ValueKind != JsonValueKind.Number)
                throw new Exception(
                    $"[GamePlanLoader] '{source}': Field '{key}' must be a number.");

            float f = val.GetSingle();

            if (f < 0f || f > 1f)
                throw new Exception(
                    $"[GamePlanLoader] '{source}': Field '{key}' value {f} is out of [0.0, 1.0]. " +
                    $"All tactics values must be normalised 0–1.");

            return f;
        }
    }
}