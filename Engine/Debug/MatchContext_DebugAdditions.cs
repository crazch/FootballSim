// =============================================================================
// PATCH FILE: MatchContext_DebugAdditions.cs
// Path:    FootballSim/Engine/Models/MatchContext_DebugAdditions.cs
//
// Purpose:
//   Documents the TWO fields that must be added to the existing MatchContext.cs.
//   This is a patch file — copy these two fields into MatchContext.cs in the
//   section after HomeScore/AwayScore.
//
//   Do NOT add any logic to MatchContext.cs itself. These are plain fields.
//   All debug-mode behaviour lives in DebugMatchContext.cs and DebugTickRunner.cs.
//
// ─────────────────────────────────────────────────────────────────────────────
// ADD TO MatchContext.cs (in the fields region, after AwayPossessionTicks):
// ─────────────────────────────────────────────────────────────────────────────
//
//   // ── Debug mode fields ─────────────────────────────────────────────────
//   // These are ONLY set by DebugMatchContext. Normal MatchEngine never touches them.
//   // All 22 player slots are always allocated — unused slots have IsActive = false.
//   // Systems skip inactive players already, so no system needs to check DebugMode.
//   // The only code that reads DebugMode is FormationLoader (skip 11-slot assertion)
//   // and AIConstants (DISABLE_LONG_PASS_OVERRIDE).
//
//   /// <summary>
//   /// When true, FormationLoader 11-slot validation is suppressed.
//   /// Debug scenarios have fewer than 11 active players per team.
//   /// Systems never read this — they skip inactive players (IsActive=false) already.
//   /// </summary>
//   public bool DebugMode = false;
//
//   /// <summary>
//   /// Number of active home players set by DebugMatchContext.
//   /// For documentation/logging only — no system uses this for iteration bounds.
//   /// Systems always iterate 0..21 and skip IsActive=false.
//   /// </summary>
//   public int HomeActiveCount = 11;
//
//   /// <summary>Number of active away players. See HomeActiveCount.</summary>
//   public int AwayActiveCount = 11;
//
//   /// <summary>
//   /// When true, forces AIConstants.DISABLE_LONG_PASS = true for this context
//   /// regardless of the constant's value. Applied by DebugTickRunner before each tick.
//   /// Allows per-scenario long-pass control without permanently changing AIConstants.
//   /// </summary>
//   public bool ForceLongPassDisabled = false;
//
// ─────────────────────────────────────────────────────────────────────────────
// ADD TO AIConstants.cs (anywhere in the class body):
// ─────────────────────────────────────────────────────────────────────────────
//
//   /// <summary>
//   /// Runtime override for DISABLE_LONG_PASS. Set by DebugTickRunner per tick
//   /// when ctx.ForceLongPassDisabled = true. Not a const — must be settable.
//   /// In production this is always false. Only DebugTickRunner writes this.
//   /// </summary>
//   public static bool DISABLE_LONG_PASS_OVERRIDE = false;
//
// ─────────────────────────────────────────────────────────────────────────────
// CHANGE IN DecisionSystem.cs — ScoreBestPass method, long-pass guard:
// ─────────────────────────────────────────────────────────────────────────────
//
//   // BEFORE:
//   if (AIConstants.DISABLE_LONG_PASS && dist > AIConstants.PASS_LONG_THRESHOLD)
//       return new PassCandidate { ReceiverId = -1, Score = 0f };
//
//   // AFTER (reads override first, falls back to constant):
//   bool longPassDisabled = AIConstants.DISABLE_LONG_PASS_OVERRIDE
//                        || AIConstants.DISABLE_LONG_PASS;
//   if (longPassDisabled && dist > AIConstants.PASS_LONG_THRESHOLD)
//       return new PassCandidate { ReceiverId = -1, Score = 0f };
//
// ─────────────────────────────────────────────────────────────────────────────
// CHANGE IN FormationLoader.cs — LoadAll or LoadFromJson validation:
// ─────────────────────────────────────────────────────────────────────────────
//
//   // BEFORE (throws for any non-11 slot count):
//   if (slots.Count != 11)
//       throw new InvalidOperationException(
//           $"Formation '{id}' has {slots.Count} slots — expected exactly 11.");
//
//   // AFTER (only throws in non-debug formation files):
//   // FormationLoader is called at startup by MatchEngineBridge, NOT per-scenario.
//   // Debug scenarios bypass FormationLoader entirely — they never call LoadAll.
//   // So this change is NOT needed in FormationLoader.
//   //
//   // The 11-slot validation in FormationLoader stays unchanged.
//   // Debug scenarios simply never invoke FormationLoader — DebugMatchContext
//   // sets PlayerState.FormationAnchor directly and sets TeamData.Players = empty.
//   //
//   // No change needed to FormationLoader.cs.
//
// ─────────────────────────────────────────────────────────────────────────────
// WHY NO CHANGE IS NEEDED TO FormationLoader.cs:
// ─────────────────────────────────────────────────────────────────────────────
//
//   FormationLoader.LoadAll() is called once at startup by MatchEngineBridge.
//   It populates FormationRegistry with production formations.
//
//   Debug scenarios created by DebugMatchContext:
//     1. Never call FormationLoader.LoadAll()
//     2. Never call FormationRegistry.Get()
//     3. Set ctx.Players[i].FormationAnchor directly (absolute coordinates)
//     4. Set ctx.HomeTeam.FormationName = "debug" (never looked up in registry)
//
//   The 11-slot validation in FormationLoader is therefore NEVER triggered
//   by debug scenarios. FormationLoader.cs does not need to change.
//
// =============================================================================

// This file is documentation only — no compilable code.
// All actual code changes are described above.
// The only files that need editing are:
//   1. FootballSim/Engine/Models/MatchContext.cs   — add 4 fields
//   2. FootballSim/Engine/Systems/AIConstants.cs   — add 1 field
//   3. FootballSim/Engine/Systems/DecisionSystem.cs — change 1 guard condition
// All other production files are untouched.
