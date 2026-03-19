// =============================================================================
// Module:  FormationLoader.cs
// Path:    FootballSim/Engine/Tactics/FormationLoader.cs
// Purpose:
//   Reads all formation JSON files from Data/Formations/ and populates
//   FormationRegistry with FormationData instances. Pure JSON → struct conversion.
//   Does NOT modify engine state, does NOT touch MatchContext, does NOT
//   interact with any system. Called once at application startup.
//
// API:
//   FormationLoader.LoadAll(string dataFolderPath) → void
//     Reads every *.json in dataFolderPath/Formations/, parses each,
//     validates slot count and slot indices, registers in FormationRegistry.
//     Throws on any validation error with descriptive message.
//
//   FormationLoader.LoadFromJson(string json, string sourceLabel) → FormationData
//     Parses a single JSON string into FormationData.
//     sourceLabel is used in error messages (e.g. the filename).
//     Returns the parsed FormationData. Does NOT register — caller registers.
//
// JSON format (one file per formation, e.g. 433.json):
// {
//   "id":          "4-3-3",
//   "displayName": "4-3-3 (Attack)",
//   "shapeLabel":  "4-3-3",
//   "slots": [
//     { "slotIndex": 0, "role": "GK",  "anchorX": 0.50, "anchorY": 0.05 },
//     { "slotIndex": 1, "role": "RB",  "anchorX": 0.80, "anchorY": 0.22 },
//     { "slotIndex": 2, "role": "CB",  "anchorX": 0.62, "anchorY": 0.18 },
//     { "slotIndex": 3, "role": "CB",  "anchorX": 0.38, "anchorY": 0.18 },
//     { "slotIndex": 4, "role": "LB",  "anchorX": 0.20, "anchorY": 0.22 },
//     { "slotIndex": 5, "role": "CM",  "anchorX": 0.65, "anchorY": 0.42 },
//     { "slotIndex": 6, "role": "CDM", "anchorX": 0.50, "anchorY": 0.35 },
//     { "slotIndex": 7, "role": "CM",  "anchorX": 0.35, "anchorY": 0.42 },
//     { "slotIndex": 8, "role": "IW",  "anchorX": 0.82, "anchorY": 0.62 },
//     { "slotIndex": 9, "role": "ST",  "anchorX": 0.50, "anchorY": 0.75 },
//     { "slotIndex": 10,"role": "IW",  "anchorX": 0.18, "anchorY": 0.62 }
//   ]
// }
//
// Validation rules (throws on failure):
//   • Exactly 11 slots per formation
//   • SlotIndex values must be unique and cover 0–10 exactly
//   • Slot 0 must have role GK or SK
//   • anchorX and anchorY must be in [0.0, 1.0]
//   • role string must parse to a valid PlayerRole enum name
//
// Dependencies:
//   Engine/Models/Enums.cs          (PlayerRole, for enum parsing)
//   Engine/Models/Vec2.cs           (anchor position)
//   Engine/Tactics/FormationData.cs (FormationData, FormationSlot, FormationRegistry)
//   System.Text.Json                (JSON parsing — no Godot dependency)
//
// Notes:
//   • Uses System.Text.Json (built into .NET 6+). No third-party libs required.
//   • DEBUG flag: when true, logs each loaded formation name and slot count.
//   • Engine has zero Godot dependency — file path is passed in from bridge/startup,
//     not read from Godot ProjectSettings. On Godot, MatchEngineBridge.cs resolves
//     the path with ProjectSettings.GlobalizePath("res://Data/Formations/") and
//     passes the absolute path string to LoadAll().
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FootballSim.Engine.Models;

namespace FootballSim.Engine.Tactics
{
    /// <summary>
    /// Reads formation JSON files and populates FormationRegistry.
    /// Pure JSON → data struct conversion. No engine state modification.
    /// </summary>
    public static class FormationLoader
    {
        // ── DEBUG ─────────────────────────────────────────────────────────────

        /// <summary>
        /// When true, logs each loaded formation to console with slot details.
        /// Set to true during development. Set to false for release builds.
        /// Controlled globally by EngineDebugLogger.DEBUG or set directly.
        /// </summary>
        public static bool DEBUG = false;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Loads all *.json files from dataFolderPath/Formations/ and registers
        /// them into FormationRegistry. Throws on any validation error.
        /// Call once at startup (from MatchEngineBridge.cs).
        /// </summary>
        /// <param name="dataFolderPath">
        /// Absolute path to the Data/ folder. On Godot:
        /// ProjectSettings.GlobalizePath("res://Data/")
        /// </param>
        public static void LoadAll(string dataFolderPath)
        {
            string formationsPath = Path.Combine(dataFolderPath, "Formations");

            if (!Directory.Exists(formationsPath))
                throw new Exception(
                    $"[FormationLoader] Formations folder not found at: '{formationsPath}'. " +
                    $"Create Data/Formations/ and add formation JSON files.");

            string[] files = Directory.GetFiles(formationsPath, "*.json");

            if (files.Length == 0)
                throw new Exception(
                    $"[FormationLoader] No JSON files found in '{formationsPath}'. " +
                    $"Add at least one formation JSON (e.g. 433.json).");

            if (DEBUG)
                Console.WriteLine($"[FormationLoader] Loading {files.Length} formation file(s) from '{formationsPath}'");

            foreach (string filePath in files)
            {
                string json = File.ReadAllText(filePath);
                string label = Path.GetFileName(filePath);

                FormationData formation = LoadFromJson(json, label);
                FormationRegistry.Register(formation);

                if (DEBUG)
                    Console.WriteLine(
                        $"[FormationLoader] Loaded '{formation.Id}' " +
                        $"({formation.DisplayName}) — {formation.Slots.Length} slots");
            }

            FormationRegistry.MarkLoaded();

            if (DEBUG)
                Console.WriteLine($"[FormationLoader] Registry loaded. Total formations: {files.Length}");
        }

        /// <summary>
        /// Parses a single JSON string into FormationData. Validates all fields.
        /// Does NOT register into FormationRegistry — caller handles registration.
        /// Throws descriptive exceptions on any validation failure.
        /// </summary>
        /// <param name="json">Raw JSON string content of one formation file.</param>
        /// <param name="sourceLabel">Filename or identifier used in error messages.</param>
        public static FormationData LoadFromJson(string json, string sourceLabel)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new Exception(
                    $"[FormationLoader] Invalid JSON in '{sourceLabel}': {ex.Message}");
            }

            JsonElement root = doc.RootElement;

            // ── Required top-level fields ─────────────────────────────────────
            string id          = ReadRequiredString(root, "id",          sourceLabel);
            string displayName = ReadRequiredString(root, "displayName", sourceLabel);
            string shapeLabel  = ReadRequiredString(root, "shapeLabel",  sourceLabel);

            if (!root.TryGetProperty("slots", out JsonElement slotsElement) ||
                slotsElement.ValueKind != JsonValueKind.Array)
                throw new Exception(
                    $"[FormationLoader] '{sourceLabel}': Missing or invalid 'slots' array.");

            // ── Parse slots ───────────────────────────────────────────────────
            var slotList = new List<FormationSlot>();
            int slotArrayIndex = 0;

            foreach (JsonElement slotEl in slotsElement.EnumerateArray())
            {
                string slotSource = $"{sourceLabel}:slots[{slotArrayIndex}]";

                int slotIndex = ReadRequiredInt(slotEl, "slotIndex", slotSource);
                string roleStr = ReadRequiredString(slotEl, "role", slotSource);
                float anchorX  = ReadRequiredFloat(slotEl, "anchorX", slotSource);
                float anchorY  = ReadRequiredFloat(slotEl, "anchorY", slotSource);

                // Validate role string → enum
                if (!Enum.TryParse<PlayerRole>(roleStr, out PlayerRole role))
                    throw new Exception(
                        $"[FormationLoader] '{slotSource}': Unknown role '{roleStr}'. " +
                        $"Must be a valid PlayerRole enum name (e.g. 'GK', 'CB', 'IW').");

                // Validate anchor bounds
                if (anchorX < 0f || anchorX > 1f)
                    throw new Exception(
                        $"[FormationLoader] '{slotSource}': anchorX {anchorX} is out of [0,1] range.");
                if (anchorY < 0f || anchorY > 1f)
                    throw new Exception(
                        $"[FormationLoader] '{slotSource}': anchorY {anchorY} is out of [0,1] range.");

                slotList.Add(new FormationSlot
                {
                    SlotIndex = slotIndex,
                    Role      = role,
                    Anchor    = new Vec2(anchorX, anchorY),
                });

                slotArrayIndex++;
            }

            // ── Validate slot count ───────────────────────────────────────────
            if (slotList.Count != 11)
                throw new Exception(
                    $"[FormationLoader] '{sourceLabel}': Expected 11 slots but found {slotList.Count}. " +
                    $"Every formation must have exactly 11 player slots.");

            // ── Validate slot indices are 0–10 with no duplicates ─────────────
            var seenIndices = new HashSet<int>();
            foreach (var slot in slotList)
            {
                if (slot.SlotIndex < 0 || slot.SlotIndex > 10)
                    throw new Exception(
                        $"[FormationLoader] '{sourceLabel}': slotIndex {slot.SlotIndex} is out of [0,10] range.");
                if (!seenIndices.Add(slot.SlotIndex))
                    throw new Exception(
                        $"[FormationLoader] '{sourceLabel}': Duplicate slotIndex {slot.SlotIndex}. " +
                        $"Every slot must have a unique index 0–10.");
            }

            // ── Validate slot 0 is goalkeeper ─────────────────────────────────
            FormationSlot slot0 = slotList.Find(s => s.SlotIndex == 0);
            if (slot0.Role != PlayerRole.GK && slot0.Role != PlayerRole.SK)
                throw new Exception(
                    $"[FormationLoader] '{sourceLabel}': Slot 0 must be GK or SK. " +
                    $"Found role '{slot0.Role}'. Slot 0 is always the goalkeeper slot.");

            // ── Sort by slot index for consistent access ───────────────────────
            slotList.Sort((a, b) => a.SlotIndex.CompareTo(b.SlotIndex));

            return new FormationData
            {
                Id          = id,
                DisplayName = displayName,
                ShapeLabel  = shapeLabel,
                Slots       = slotList.ToArray(),
            };
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static string ReadRequiredString(JsonElement el, string key, string source)
        {
            if (!el.TryGetProperty(key, out JsonElement val) ||
                val.ValueKind != JsonValueKind.String)
                throw new Exception(
                    $"[FormationLoader] '{source}': Missing or non-string field '{key}'.");
            string result = val.GetString();
            if (string.IsNullOrWhiteSpace(result))
                throw new Exception(
                    $"[FormationLoader] '{source}': Field '{key}' must not be empty.");
            return result;
        }

        private static int ReadRequiredInt(JsonElement el, string key, string source)
        {
            if (!el.TryGetProperty(key, out JsonElement val) ||
                val.ValueKind != JsonValueKind.Number)
                throw new Exception(
                    $"[FormationLoader] '{source}': Missing or non-numeric field '{key}'.");
            return val.GetInt32();
        }

        private static float ReadRequiredFloat(JsonElement el, string key, string source)
        {
            if (!el.TryGetProperty(key, out JsonElement val) ||
                val.ValueKind != JsonValueKind.Number)
                throw new Exception(
                    $"[FormationLoader] '{source}': Missing or non-numeric field '{key}'.");
            return val.GetSingle();
        }
    }
}