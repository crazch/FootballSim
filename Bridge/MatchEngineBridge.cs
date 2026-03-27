// =============================================================================
// Module:  MatchEngineBridge.cs
// Path:    FootballSim/Bridge/MatchEngineBridge.cs
//
// Purpose:
//   The ONE AND ONLY file in the entire project that contains "using Godot;".
//   It is an adapter between Godot's type system (Vector2, Dictionary, Array,
//   String) and the engine's type system (Vec2, TeamData, MatchReplay).
//
//   Responsibilities — strictly conversion and orchestration:
//     1. Accept Godot.Collections.Dictionary input from GDScript
//     2. Convert it to engine types (TeamData, PlayerData, TacticsInput)
//     3. Call MatchEngine.Simulate(homeTeam, awayTeam, seed) — the ONLY engine call
//     4. Store the returned MatchReplay as a private field
//     5. Expose read-only access to ReplayFrame data via typed Godot methods
//     6. Convert engine output types back to Godot primitives for GDScript
//     7. Bootstrap loaders (FormationLoader, RoleLoader, GamePlanRegistry) once
//
//   What this file must NEVER do:
//     ✗ Implement gameplay logic
//     ✗ Modify MatchContext, PlayerState, or BallState
//     ✗ Call any System inside Engine/Systems/ (BallSystem, MovementSystem, etc.)
//     ✗ Use MathF.Lerp — use MathUtil.Lerp only
//     ✗ Create new System.Random — seed passes through to MatchEngine unchanged
//     ✗ Re-run the simulation per frame — _replay is computed once and read only
//
// Godot integration notes:
//   • [GlobalClass] + extends Node → registered in ClassDB, instantiable from GDScript
//   • GDScript type annotation trap: NEVER type-annotate a variable holding a
//     C# object in GDScript. Use untyped `var`:
//       var bridge = MatchEngineBridge.new()   ← correct
//       var bridge: MatchEngineBridge = ...    ← causes parse-time crash
//   • Godot.Collections.Dictionary uses StringName keys internally — always use
//     string literals when building or reading dictionaries across the boundary.
//   • MatchEngineBridge is added as an AutoLoad OR instantiated manually in
//     MatchView.gd — never both. Scene tree owns it; don't new() in GDScript.
//
// Public API exposed to GDScript:
//   void       LoadData(string dataFolderPath)
//   void       SimulateMatch(Dictionary homeTeam, Dictionary awayTeam, int seed)
//   bool       IsReplayReady()
//   int        GetTotalFrames()
//   int        GetFinalHomeScore()
//   int        GetFinalAwayScore()
//   Dictionary GetFrame(int tick)
//   Array      GetFrameEvents(int tick)
//   Array      GetHighlightTicks()
//   Dictionary GetPlayerStats(int playerId)
//   Dictionary GetTeamStats(int teamId)
//   Dictionary GetHypothesis(int teamId)
//   string     GetLastError()
//   void       SetDebugFlags(bool enabled, int filterPlayerId = -1)
//   void       SetDebugCaptureRange(int startTick, int endTick, string captureMode = "Light")
//   void       ClearDebugCaptureRange()
//   string     ExportCapturedDebugJSON(int[] playerIds = null)
//   string     ExportDebugJSONRange(Dictionary homeTeam, Dictionary awayTeam, int seed, int startTick, int endTick, string captureMode = "Light", Array playerIds = null)
//   void       LoadExternalReplay(MatchReplay replay)
//   void       SimulateDebugScenario(string scenarioName, int seed = 42, int maxTicks = 600)
//   void       DumpTickRange(int fromTick, int toTick, int filterPlayerId = -1)
//
// GDScript usage example:
//   var bridge = MatchEngineBridge.new()
//   bridge.LoadData("res://Data")
//   bridge.SimulateMatch(home_dict, away_dict, 12345)
//   var frame = bridge.GetFrame(7200)   # minute 12
//   var ball_pos = frame["ball_pos"]    # Vector2
//
// Dependencies:
//   Engine/Core/MatchReplay.cs        (MatchEngine.Simulate, MatchReplay)
//   Engine/Core/ReplayFrame.cs        (ReplayFrame, PlayerSnap, BallSnap)
//   Engine/Models/*                   (TeamData, PlayerData, Vec2, Enums)
//   Engine/Tactics/*                  (FormationLoader, RoleLoader, GamePlanRegistry)
//   Engine/Stats/*                    (PlayerMatchStats, TeamMatchStats)
//   Engine/MathUtil.cs                (the only Lerp/Clamp used in this file)
//
// =============================================================================

using Godot;
using System;
using System.Collections.Generic;
using FootballSim.Engine;
using FootballSim.Engine.Core;
using FootballSim.Engine.Models;
using FootballSim.Engine.Stats;
using FootballSim.Engine.Systems;
using FootballSim.Engine.Tactics;
using FootballSim.Engine.Debug;

namespace FootballSim.Bridge
{
	/// <summary>
	/// Godot Node bridge between GDScript and the simulation engine.
	/// The only file in the project with "using Godot;".
	/// Extends Node so it can be added to the scene tree or used as AutoLoad.
	/// [GlobalClass] registers it in ClassDB so GDScript can instantiate it.
	/// </summary>
	[GlobalClass]
	public partial class MatchEngineBridge : Node
	{
		// ── DEBUG ─────────────────────────────────────────────────────────────

		/// <summary>
		/// When true, logs every bridge call — SimulateMatch, GetFrame, LoadData —
		/// to GD.Print so they appear in the Godot Output panel.
		/// Set false for release. Toggle via Project Settings or a GDScript call.
		/// </summary>
		[Export] public bool DEBUG = false;

		// SEARCHTAG: DEBUG FLAGS CONTROL
		/// <summary>
		/// Enables debug logging for selected engine systems.
		/// Optional: filter output to a specific player ID (-1 = all players)
		/// </summary>
		public void SetDebugFlags(bool enabled, int filterPlayerId = -1)
		{
			CollisionSystem.DEBUG = enabled;
			CollisionSystem.DEBUG_PLAYER_ID = filterPlayerId;
			BallSystem.DEBUG = enabled;
			PlayerAI.DEBUG = enabled;
			PlayerAI.DEBUG_PLAYER_ID = filterPlayerId;
			EventSystem.DEBUG = enabled;

			if (DEBUG)
				GD.Print($"[MatchEngineBridge] Debug flags set: enabled={enabled}, filterPlayerId={filterPlayerId}");
		}

		/// <summary>
		/// Configure a capture range for production simulation. When enabled,
		/// MatchEngine will only print system DEBUG output and capture TickLogs
		/// for ticks in [startTick..endTick). Call this BEFORE starting a
		/// simulation (Bridge.SimulateMatch()).
		/// </summary>
		public void SetDebugCaptureRange(int startTick, int endTick, string captureMode = "Light")
		{
			MatchEngine.DebugCaptureEnabled = true;
			MatchEngine.DebugCaptureStart = Math.Max(0, startTick);
			MatchEngine.DebugCaptureEnd = Math.Max(MatchEngine.DebugCaptureStart + 1, endTick);
			switch (captureMode.ToLower())
			{
				case "full":
					MatchEngine.DebugCaptureMode = FootballSim.Engine.Debug.DebugCaptureMode.Full;
					break;
				default:
					MatchEngine.DebugCaptureMode = FootballSim.Engine.Debug.DebugCaptureMode.Light;
					break;
			}
			if (DEBUG)
				GD.Print($"[MatchEngineBridge] Debug capture range set: {MatchEngine.DebugCaptureStart}..{MatchEngine.DebugCaptureEnd} mode={MatchEngine.DebugCaptureMode}");
		}

		/// <summary>
		/// Disable capture range and stop collecting TickLogs in MatchEngine.
		/// </summary>
		public void ClearDebugCaptureRange()
		{
			MatchEngine.DebugCaptureEnabled = false;
			MatchEngine.DebugCaptureStart = 0;
			MatchEngine.DebugCaptureEnd = 0;
			if (DEBUG) GD.Print("[MatchEngineBridge] Debug capture range cleared");
		}

		/// <summary>
		/// Returns JSON for TickLogs captured during the last production simulation
		/// (the capture range must have been enabled prior to SimulateMatch()).
		/// Pass null or empty array to export all players.
		/// </summary>
		public string ExportCapturedDebugJSON(int[] playerIds = null!)
		{
			try
			{
				var logs = MatchEngine.GetCapturedTickLogs();
				if (logs == null || logs.Count == 0)
				{
					if (DEBUG) GD.Print("[MatchEngineBridge] ExportCapturedDebugJSON: no captured logs");
					return "[]";
				}

				var query = FootballSim.Engine.Debug.DebugLogger.Query(logs);
				if (playerIds != null && playerIds.Length > 0)
					return query.Where(p => System.Array.IndexOf(playerIds, p.PlayerId) >= 0).ToJson();
				return query.ToJson();
			}
			catch (Exception ex)
			{
				_lastError = $"ExportCapturedDebugJSON failed: {ex.Message}";
				GD.PrintErr($"[MatchEngineBridge] {_lastError}");
				return "{}";
			}
		}

		/// <summary>
		/// Run a short DebugTickRunner for ticks [0..endTick) and export JSON for
		/// the requested sub-range [startTick..endTick]. This does NOT replace
		/// production `SimulateMatch()` — it's a separate short-run used only
		/// for structured debug exports. Call from GDScript after a normal
		/// `Bridge.SimulateMatch()` if you want a JSON snapshot for LLM analysis.
		/// </summary>
		public string ExportDebugJSONRange(Godot.Collections.Dictionary homeTeamDict,
									 Godot.Collections.Dictionary awayTeamDict,
									 int seed,
									 int startTick,
									 int endTick,
									 string captureMode = "Light",
									 Godot.Collections.Array playerIds = null!)
		{
			_lastError = string.Empty;
			try
			{
				if (endTick <= 0 || endTick <= startTick)
				{
					_lastError = "ExportDebugJSONRange: invalid tick range";
					GD.PrintErr($"[MatchEngineBridge] {_lastError}");
					return "{}";
				}

				// Parse capture mode
				var debugMode = captureMode.ToLower() switch
				{
					"light" => FootballSim.Engine.Debug.DebugCaptureMode.Light,
					"full" => FootballSim.Engine.Debug.DebugCaptureMode.Full,
					_ => FootballSim.Engine.Debug.DebugCaptureMode.Light
				};

				// Parse team data into engine types
				TeamData homeTeam = ParseTeamData(homeTeamDict, teamId: 0);
				TeamData awayTeam = ParseTeamData(awayTeamDict, teamId: 1);

				// Create a production MatchContext seeded identically to the main run.
				var ctx = new FootballSim.Engine.Models.MatchContext(homeTeam, awayTeam);
				ctx.RandomSeed = seed;
				ctx.Random = new System.Random(seed);
				ctx.DebugMode = true;

				// Run DebugTickRunner for the requested endTick. CaptureFrames=false
				// since we only need TickLogs for JSON export.
				var runner = new FootballSim.Engine.Debug.DebugTickRunner(ctx, maxTicks: endTick, captureMode: debugMode)
				{
					CaptureFrames = false
				};

				runner.Run();

				var allLogs = runner.TickLogs;
				if (allLogs == null || allLogs.Count == 0)
				{
					if (DEBUG) GD.Print("[MatchEngineBridge] ExportDebugJSONRange: no tick logs captured");
					return "[]";
				}

				// Build sub-range list
				int start = Math.Max(0, startTick);
				int end = Math.Min(endTick, allLogs.Count);
				var slice = allLogs.Skip(start).Take(end - start).ToList();

				// Convert playerIds if provided
				int[]? pids = null;
				if (playerIds != null && playerIds.Count > 0)
				{
					pids = new int[playerIds.Count];
					for (int i = 0; i < playerIds.Count; i++)
						pids[i] = System.Convert.ToInt32(playerIds[i]);
				}

				var query = FootballSim.Engine.Debug.DebugLogger.Query(slice);
				if (pids != null && pids.Length > 0)
				{
					var json = query.Where(p => System.Array.IndexOf(pids, p.PlayerId) >= 0).ToJson();
					return json;
				}

				return query.ToJson();
			}
			catch (Exception ex)
			{
				_lastError = $"ExportDebugJSONRange failed: {ex.Message}";
				GD.PrintErr($"[MatchEngineBridge] {_lastError}\n{ex.StackTrace}");
				return "{}";
			}
		}

		// ── Private state ─────────────────────────────────────────────────────

		/// <summary>
		/// The result of the last SimulateMatch() call.
		/// Null until simulation completes. Read-only after that.
		/// Never re-computed per frame.
		/// </summary>
		private MatchReplay? _replay = null;

		/// <summary>
		/// True after LoadData() completed successfully.
		/// SimulateMatch() refuses to run until this is true.
		/// </summary>
		private bool _dataLoaded = false;

		/// <summary>
		/// Stores the last error message from any failed operation.
		/// GDScript reads this via GetLastError() to show error UI.
		/// </summary>
		private string _lastError = string.Empty;

		// ── Godot lifecycle ───────────────────────────────────────────────────

		public override void _Ready()
		{
			if (DEBUG) GD.Print("[MatchEngineBridge] _Ready() — bridge node active.");
		}

		// =====================================================================
		// STEP 1 — LOAD DATA
		// Must be called once before SimulateMatch.
		// Loads formation JSON, roles JSON, seeds game plan defaults.
		// =====================================================================

		/// <summary>
		/// Loads all engine data from disk. Call once at application startup,
		/// before any match simulation.
		/// </summary>
		/// <param name="dataFolderPath">
		/// Absolute path to the Data/ folder.
		/// From GDScript: ProjectSettings.GlobalizePath("res://Data")
		/// </param>
		//[Export]
		public void LoadData(string dataFolderPath)
		{
			_lastError = string.Empty;
			try
			{
				// Seed hardcoded game plan presets first (works even with no JSON)
				GamePlanRegistry.SeedDefaults();

				// Load formation JSON files (throws if folder missing)
				FormationLoader.DEBUG = DEBUG;
				FormationLoader.LoadAll(dataFolderPath);

				// Load role definitions JSON (throws if file missing or incomplete)
				RoleLoader.DEBUG = DEBUG;
				RoleLoader.LoadAll(dataFolderPath);

				// Load optional game plan JSON overrides
				GamePlanLoader.DEBUG = DEBUG;
				GamePlanLoader.LoadAll(dataFolderPath);

				_dataLoaded = true;

				if (DEBUG) GD.Print($"[MatchEngineBridge] LoadData() OK. Path='{dataFolderPath}'");
			}
			catch (Exception ex)
			{
				_lastError = $"LoadData failed: {ex.Message}";
				_dataLoaded = false;
				GD.PrintErr($"[MatchEngineBridge] {_lastError}");
			}
		}

		// =====================================================================
		// STEP 2 — SIMULATE MATCH
		// Converts GDScript dictionaries → engine types → runs engine → stores replay.
		// =====================================================================

		/// <summary>
		/// Runs a full match simulation. Blocks until complete (~100ms typical).
		/// Call from a Thread in GDScript if you need the UI to remain responsive.
		///
		/// homeTeam / awayTeam Dictionary keys (all required unless marked optional):
		///   "name"             String     Team display name
		///   "formation"        String     Formation ID e.g. "4-3-3"
		///   "kit_primary"      int        Packed RGB colour 0xRRGGBB
		///   "kit_secondary"    int        Packed RGB colour
		///   "players"          Array      11 Dictionaries, each with player fields
		///   "tactics"          Dictionary Slider values (all 0.0–1.0 floats)
		///
		/// player Dictionary keys:
		///   "shirt_number"     int
		///   "name"             String
		///   "role"             String     PlayerRole enum name e.g. "ST", "GK", "IW"
		///   "base_speed"       float      0.0–1.0  (0.5 = average)
		///   "stamina"          float      0.0–1.0
		///   "passing"          float      0.0–1.0
		///   "shooting"         float      0.0–1.0
		///   "dribbling"        float      0.0–1.0
		///   "defending"        float      0.0–1.0
		///   "reactions"        float      0.0–1.0
		///   "ego"              float      0.0–1.0
		///
		/// tactics Dictionary keys (all optional — missing keys default to 0.5):
		///   "pressing_intensity"      float
		///   "pressing_trigger"        float
		///   "press_compactness"       float
		///   "defensive_line"          float
		///   "defensive_width"         float
		///   "defensive_aggression"    float
		///   "possession_focus"        float
		///   "build_up_speed"          float
		///   "passing_directness"      float
		///   "attacking_width"         float
		///   "attacking_line"          float
		///   "transition_speed"        float
		///   "crossing_frequency"      float
		///   "shooting_threshold"      float
		///   "tempo"                   float
		///   "out_of_possession_shape" float
		///   "in_possession_spread"    float
		///   "freedom_level"           float
		///   "counter_attack_focus"    float
		///   "offside_trap_frequency"  float
		///   "physicality_bias"        float
		///   "set_piece_focus"         float
		/// </summary>
		/// <param name="homeTeamDict">Home team Godot Dictionary (see above).</param>
		/// <param name="awayTeamDict">Away team Godot Dictionary (see above).</param>
		/// <param name="seed">Random seed. Same seed always produces identical output.</param>
		public void SimulateMatch(Godot.Collections.Dictionary homeTeamDict,
								  Godot.Collections.Dictionary awayTeamDict,
								  int seed = 0)
		{
			_lastError = string.Empty;
			_replay = null!;

			if (!_dataLoaded)
			{
				_lastError = "SimulateMatch called before LoadData(). " +
							 "Call LoadData(dataFolderPath) first.";
				GD.PrintErr($"[MatchEngineBridge] {_lastError}");
				return;
			}

			try
			{
				if (DEBUG)
					GD.Print($"[MatchEngineBridge] SimulateMatch() seed={seed}");

				TeamData homeTeam = ParseTeamData(homeTeamDict, teamId: 0);
				TeamData awayTeam = ParseTeamData(awayTeamDict, teamId: 1);

				// Enable engine debug output if bridge debug is on
				MatchEngine.DEBUG = DEBUG;

				_replay = MatchEngine.Simulate(homeTeam, awayTeam, seed);

				if (DEBUG)
					GD.Print($"[MatchEngineBridge] Simulation done. " +
							 $"Score: {_replay.FinalHomeScore}–{_replay.FinalAwayScore} " +
							 $"Frames: {_replay.TotalTicks}");
			}
			catch (Exception ex)
			{
				_lastError = $"SimulateMatch failed: {ex.Message}";
				GD.PrintErr($"[MatchEngineBridge] {_lastError}\n{ex.StackTrace}");
			}
		}

		/// <summary>
		/// Accepts a pre-built MatchReplay from DebugTickRunner.BuildReplay().
		/// After this call, GetFrame(), GetFrameEvents(), IsReplayReady(), etc.
		/// all work exactly as they do after SimulateMatch() — ReplayPlayer.gd
		/// reads debug replays with zero code changes.
		///
		/// Called by SimulateDebugScenario() and optionally from C# test code.
		/// GDScript should call SimulateDebugScenario() instead.
		/// </summary>
		public void LoadExternalReplay(MatchReplay replay)
		{
			if (replay == null)
			{
				_lastError = "LoadExternalReplay: replay was null. " +
							 "Did DebugTickRunner.Run() complete before BuildReplay()?";
				GD.PrintErr(_lastError);
				return;
			}

			_replay = replay;
			_lastError = "";

			if (OS.IsDebugBuild())
				GD.Print($"[Bridge] Debug replay loaded: {replay.TotalTicks} ticks, " +
						 $"score {replay.FinalHomeScore}–{replay.FinalAwayScore}");
		}

		/// <summary>
		/// Builds a debug scenario MatchContext, runs it, and loads the result
		/// as a replay that ReplayPlayer.gd can read immediately.
		///
		/// Called from DebugMatchView.gd instead of SimulateMatch().
		/// ReplayPlayer.gd, GetFrame(), GetFrameEvents() are untouched.
		///
		/// Supported scenarioName values (must match DebugMatchContext method names):
		///   "1v1"              → OneVOne
		///   "2v2"              → TwoVTwo
		///   "3v3_short"        → ThreeVThreeShortPass
		///   "3v3_long"         → ThreeVThreeLongPass
		///
		/// maxTicks: how many ticks to simulate. 600 = 60 seconds of match time.
		/// </summary>
		public void SimulateDebugScenario(string scenarioName, int seed = 42,
										   int maxTicks = 600)
		{
			_replay = null!;
			_lastError = "";

			try
			{
				// ── 1. Build the scenario context ──────────────────────────────────
				FootballSim.Engine.Models.MatchContext ctx = scenarioName switch
				{
					"1v1" => DebugMatchContext.OneVOne(seed),
					"2v2" => DebugMatchContext.TwoVTwo(seed),
					"3v3_short" => DebugMatchContext.ThreeVThreeShortPass(seed),
					"3v3_long" => DebugMatchContext.ThreeVThreeLongPass(seed),
					_ => throw new ArgumentException(
										 $"Unknown debug scenario: '{scenarioName}'. " +
										 "Valid values: 1v1, 2v2, 3v3_short, 3v3_long")
				};

				// ── 2. Force Debug Mode on the Context ─────────────────────────────
				// Most AI Systems check 'if (ctx.DebugMode)' before printing.
				// If this is false, ProfileTwoVTwo() flags are ignored.
				ctx.DebugMode = true;

				// ── 3. Activate Specific System Flags ──────────────────────────────
				if (scenarioName == "2v2")
				{
					FootballSim.Engine.Debug.DebugLogger.ProfileTwoVTwo();
					GD.Print("[Bridge] Internal Profiler engaged for 2v2 (ST vs CB).");
				}
				else if (scenarioName == "1v1")
				{
					// Basic debug logs for 1v1
					ctx.DebugMode = true;
				}

				// ── 4. Run the simulation ──────────────────────────────────────────
				var runner = new DebugTickRunner(ctx, maxTicks)
				{
					CaptureFrames = true
				};

				// Perform the simulation
				runner.Run();

				// ── 5. Convert to MatchReplay and load ─────────────────────────────
				var replay = runner.BuildReplay();
				if (replay == null)
				{
					_lastError = $"SimulateDebugScenario('{scenarioName}'): " +
								 "BuildReplay() returned null — no frames captured.";
					GD.PrintErr(_lastError);
					return;
				}

				LoadExternalReplay(replay);

				// ── 6. Final Output ────────────────────────────────────────────────
				if (OS.IsDebugBuild())
				{
					GD.Print($"[Bridge] SimulateDebugScenario '{scenarioName}' complete.");

					// This prints the per-tick summary if the runner captured it
					runner.PrintSummary();

					// If your engine uses an extension to flush logs to Godot, call it:
					// FootballSim.Engine.Debug.EngineDebugLogger_ScenarioExtensions.FlushToGodot();
				}
			}
			catch (Exception ex)
			{
				_lastError = $"SimulateDebugScenario('{scenarioName}') exception: {ex.Message}";
				GD.PrintErr(_lastError);
				GD.PrintErr(ex.StackTrace);
			}
		}

		// =====================================================================
		// STEP 3 — READ REPLAY (called by ReplayPlayer.gd each frame)
		// =====================================================================

		/// <summary>True after SimulateMatch() completed without error.</summary>
		public bool IsReplayReady() => _replay != null;

		/// <summary>Total frames recorded. Should be 54000 for a 90-minute match.</summary>
		public int GetTotalFrames() => _replay?.TotalTicks ?? 0;

		/// <summary>Final home team score.</summary>
		public int GetFinalHomeScore() => _replay?.FinalHomeScore ?? 0;

		/// <summary>Final away team score.</summary>
		public int GetFinalAwayScore() => _replay?.FinalAwayScore ?? 0;

		/// <summary>Last error message from LoadData or SimulateMatch. Empty if no error.</summary>
		public string GetLastError() => _lastError;

		/// <summary>
		/// Returns one replay frame as a Godot Dictionary.
		/// Called every display frame by ReplayPlayer.gd.
		///
		/// Returned Dictionary keys:
		///   "tick"          int       Simulation tick index
		///   "match_second"  float     Match time in seconds
		///   "match_minute"  int       Match minute
		///   "home_score"    int
		///   "away_score"    int
		///   "phase"         int       MatchPhase enum value (cast to int)
		///   "ball_pos"      Vector2   Ball world position
		///   "ball_phase"    int       BallPhase enum value (0=Owned,1=InFlight,2=Loose)
		///   "ball_height"   float     0.0 = ground, 1.0 = head height
		///   "ball_owner"    int       PlayerId or -1
		///   "ball_is_shot"  bool
		///   "players"       Array     22 Dictionaries (see below)
		///
		/// Each player Dictionary:
		///   "position"      Vector2
		///   "stamina"       float     0.0–1.0
		///   "has_ball"      bool
		///   "action"        int       PlayerAction enum value
		///   "is_active"     bool
		///   "is_sprinting"  bool
		/// </summary>
		public Godot.Collections.Dictionary GetFrame(int tick)
		{
			if (_replay == null || tick < 0 || tick >= _replay.Frames.Count)
				return new Godot.Collections.Dictionary();

			ReplayFrame frame = _replay.Frames[tick];
			var dict = new Godot.Collections.Dictionary();

			// Timing
			dict["tick"] = frame.Tick;
			dict["match_second"] = frame.MatchSecond;
			dict["match_minute"] = frame.MatchMinute;
			dict["home_score"] = frame.HomeScore;
			dict["away_score"] = frame.AwayScore;
			dict["phase"] = (int)frame.Phase;

			// Ball — Vec2 → Vector2
			dict["ball_pos"] = ToVector2(frame.Ball.Position);
			dict["ball_phase"] = (int)frame.Ball.Phase;
			dict["ball_height"] = frame.Ball.Height;
			dict["ball_owner"] = frame.Ball.OwnerId;
			dict["ball_is_shot"] = frame.Ball.IsShot;

			// Offside lines — computed by BlockShiftSystem, captured per frame
			dict["home_offside_y"] = frame.HomeOffsideY;
			dict["away_offside_y"] = frame.AwayOffsideY;

			// Players — 22 player snapshots
			var playerArray = new Godot.Collections.Array();
			for (int i = 0; i < 22; i++)
			{
				PlayerSnap snap = frame.Players[i];
				var p = new Godot.Collections.Dictionary();
				p["position"] = ToVector2(snap.Position);
				p["stamina"] = snap.Stamina;
				p["has_ball"] = snap.HasBall;
				p["action"] = (int)snap.Action;
				p["is_active"] = snap.IsActive;
				p["is_sprinting"] = snap.IsSprinting;
				playerArray.Add(p);
			}
			dict["players"] = playerArray;

			return dict;
		}

		/// <summary>
		/// Returns all events for a given tick as a Godot Array of Dictionaries.
		/// Returns an empty Array if there are no events on that tick (most ticks).
		///
		/// Each event Dictionary:
		///   "tick"          int
		///   "match_minute"  int
		///   "type"          int     MatchEventType enum value
		///   "type_name"     String  Enum name e.g. "Goal", "TackleSuccess"
		///   "primary"       int     PrimaryPlayerId (or -1)
		///   "secondary"     int     SecondaryPlayerId (or -1)
		///   "team_id"       int     0=home, 1=away
		///   "position"      Vector2 World position
		///   "extra_float"   float   e.g. xG for shots
		///   "extra_bool"    bool    e.g. progressive pass flag
		///   "description"   String  Pre-built human-readable log line
		/// </summary>
		public Godot.Collections.Array GetFrameEvents(int tick)
		{
			var result = new Godot.Collections.Array();

			if (_replay == null || tick < 0 || tick >= _replay.Frames.Count)
				return result;

			List<MatchEvent> events = _replay.Frames[tick].Events;
			if (events == null || events.Count == 0) return result;

			foreach (MatchEvent ev in events)
			{
				var d = new Godot.Collections.Dictionary();
				d["tick"] = ev.Tick;
				d["match_minute"] = ev.MatchMinute;
				d["type"] = (int)ev.Type;
				d["type_name"] = ev.Type.ToString();
				d["primary"] = ev.PrimaryPlayerId;
				d["secondary"] = ev.SecondaryPlayerId;
				d["team_id"] = ev.TeamId;
				d["position"] = ToVector2(ev.Position);
				d["extra_float"] = ev.ExtraFloat;
				d["extra_bool"] = ev.ExtraBool;
				d["description"] = ev.Description ?? string.Empty;
				result.Add(d);
			}

			return result;
		}

		/// <summary>
		/// Returns all highlight tick indices — ticks that contain at least one event.
		/// ReplayPlayer.gd uses this to jump between highlights.
		/// Returns Array of ints.
		/// </summary>
		public Godot.Collections.Array GetHighlightTicks()
		{
			var result = new Godot.Collections.Array();
			if (_replay == null) return result;

			foreach (ReplayFrame frame in _replay.HighlightFrames)
				result.Add(frame.Tick);

			return result;
		}

		// =====================================================================
		// DEBUG HELPERS
		// =====================================================================

		/// <summary>
		/// Dumps replay frames for a specific tick range to Godot Output.
		/// Use after simulation completes to inspect specific moments.
		/// </summary>
		public void DumpTickRange(int fromTick, int toTick, int filterPlayerId = -1)
		{
			if (_replay == null)
			{
				GD.Print("[Bridge] No replay loaded.");
				return;
			}

			for (int t = fromTick; t <= toTick && t < _replay.Frames.Count; t++)
			{
				ReplayFrame f = _replay.Frames[t];
				GD.Print($"[TICK {t}] phase={f.Phase} score={f.HomeScore}-{f.AwayScore} " +
						 $"ball_pos={f.Ball.Position} ball_phase={f.Ball.Phase} " +
						 $"ball_owner={f.Ball.OwnerId} ball_height={f.Ball.Height:F2} " +
						 $"is_shot={f.Ball.IsShot}");

				// Player positions (filtered)
				for (int i = 0; i < 22; i++)
				{
					if (filterPlayerId >= 0 && i != filterPlayerId) continue; // <-- only this player

					var p = f.Players[i];
					if (!p.IsActive) continue;

					string side = i <= 10 ? "H" : "A";
					GD.Print($"  P{i}[{side}] pos={p.Position} action={p.Action} " +
							 $"hasBall={p.HasBall} stamina={p.Stamina:F2}");
				}

				// Print events (optional, still shows all)
				if (f.Events != null)
					foreach (var ev in f.Events)
						GD.Print($"  >> EVENT {ev.Type}: {ev.Description}");
			}
		}

		// =====================================================================
		// STATS ACCESS (called by PostMatch.gd)
		// =====================================================================

		/// <summary>
		/// Returns per-player match statistics as a Godot Dictionary.
		/// playerId = 0–21 (home 0–10, away 11–21).
		///
		/// Returned keys — all Opta/StatsBomb standard stat names:
		///   Identity:
		///     "player_id"            int
		///     "team_id"              int
		///     "shirt_number"         int
		///     "name"                 String
		///   Goals/Attacking:
		///     "goals"                int
		///     "own_goals"            int
		///     "assists"              int
		///     "xg"                   float
		///     "xa"                   float
		///   Shots:
		///     "shots_attempted"      int
		///     "shots_on_target"      int
		///     "shots_off_target"     int
		///     "shots_blocked"        int
		///   Passes:
		///     "passes_attempted"     int
		///     "passes_completed"     int
		///     "pass_accuracy"        float  (0.0–1.0, computed)
		///     "progressive_passes"   int
		///     "key_passes"           int
		///     "long_balls_attempted" int
		///     "long_balls_completed" int
		///   Dribbles:
		///     "dribbles_attempted"   int
		///     "dribbles_successful"  int
		///     "times_dispossessed"   int
		///   Crosses:
		///     "crosses_attempted"    int
		///     "crosses_successful"   int
		///   Aerial:
		///     "aerial_duels_total"   int
		///     "aerial_duels_won"     int
		///   Defending:
		///     "tackles_attempted"    int
		///     "tackles_won"          int
		///     "interceptions"        int
		///     "pressures_attempted"  int
		///     "pressures_successful" int
		///     "blocks"               int
		///     "clearances"           int
		///   Discipline:
		///     "fouls_committed"      int
		///     "fouls_drawn"          int
		///     "yellow_cards"         int
		///     "red_cards"            int
		///     "offsides"             int
		///   GK:
		///     "shots_faced"          int
		///     "saves"                int
		///     "xg_saved"             float
		///     "goals_conceded"       int
		///   Physical:
		///     "distance_covered"     float  (pitch units — divide by 10 for metres)
		///     "sprint_distance"      float
		///     "sprint_count"         int
		///     "final_stamina"        float
		/// </summary>
		public Godot.Collections.Dictionary GetPlayerStats(int playerId)
		{
			var dict = new Godot.Collections.Dictionary();
			if (_replay?.PlayerStats == null) return dict;
			if (playerId < 0 || playerId >= _replay.PlayerStats.Length) return dict;

			PlayerMatchStats s = _replay.PlayerStats[playerId];

			dict["player_id"] = s.PlayerId;
			dict["team_id"] = s.TeamId;
			dict["shirt_number"] = s.ShirtNumber;
			dict["name"] = s.Name ?? string.Empty;

			dict["goals"] = s.Goals;
			dict["own_goals"] = s.OwnGoals;
			dict["assists"] = s.Assists;
			dict["xg"] = s.XG;
			dict["xa"] = s.XA;

			dict["shots_attempted"] = s.ShotsAttempted;
			dict["shots_on_target"] = s.ShotsOnTarget;
			dict["shots_off_target"] = s.ShotsOffTarget;
			dict["shots_blocked"] = s.ShotsBlocked;

			dict["passes_attempted"] = s.PassesAttempted;
			dict["passes_completed"] = s.PassesCompleted;
			dict["pass_accuracy"] = s.PassesAttempted > 0
										   ? (float)s.PassesCompleted / s.PassesAttempted
										   : 0f;
			dict["progressive_passes"] = s.ProgressivePasses;
			dict["key_passes"] = s.KeyPasses;
			dict["long_balls_attempted"] = s.LongBallsAttempted;
			dict["long_balls_completed"] = s.LongBallsCompleted;

			dict["dribbles_attempted"] = s.DribblesAttempted;
			dict["dribbles_successful"] = s.DribblesSuccessful;
			dict["times_dispossessed"] = s.TimesDispossessed;

			dict["crosses_attempted"] = s.CrossesAttempted;
			dict["crosses_successful"] = s.CrossesSuccessful;

			dict["aerial_duels_total"] = s.AerialDuelsTotal;
			dict["aerial_duels_won"] = s.AerialDuelsWon;

			dict["tackles_attempted"] = s.TacklesAttempted;
			dict["tackles_won"] = s.TacklesWon;
			dict["interceptions"] = s.Interceptions;
			dict["pressures_attempted"] = s.PressuresAttempted;
			dict["pressures_successful"] = s.PressuresSuccessful;
			dict["blocks"] = s.Blocks;
			dict["clearances"] = s.Clearances;

			dict["fouls_committed"] = s.FoulsCommitted;
			dict["fouls_drawn"] = s.FoulsDrawn;
			dict["yellow_cards"] = s.YellowCards;
			dict["red_cards"] = s.RedCards;
			dict["offsides"] = s.Offsides;

			dict["shots_faced"] = s.ShotsFaced;
			dict["saves"] = s.Saves;
			dict["xg_saved"] = s.XGSaved;
			dict["goals_conceded"] = s.GoalsConceded;

			dict["distance_covered"] = s.DistanceCovered;
			dict["sprint_distance"] = s.SprintDistance;
			dict["sprint_count"] = s.SprintCount;
			dict["final_stamina"] = s.FinalStamina;

			return dict;
		}

		/// <summary>
		/// Returns team-level match statistics as a Godot Dictionary.
		/// teamId = 0 (home) or 1 (away).
		///
		/// Returned keys:
		///   "team_id"               int
		///   "name"                  String
		///   "goals_scored"          int
		///   "goals_conceded"        int
		///   "possession_percent"    float  (0.0–1.0)
		///   "shots_total"           int
		///   "shots_on_target"       int
		///   "shots_off_target"      int
		///   "shots_blocked"         int
		///   "xg"                    float
		///   "xga"                   float
		///   "passes_attempted"      int
		///   "passes_completed"      int
		///   "pass_accuracy"         float
		///   "progressive_passes"    int
		///   "key_passes"            int
		///   "long_balls_attempted"  int
		///   "long_balls_completed"  int
		///   "ppda"                  float  (lower = more intense press)
		///   "pressures_attempted"   int
		///   "pressures_successful"  int
		///   "tackles_attempted"     int
		///   "tackles_won"           int
		///   "interceptions"         int
		///   "clearances"            int
		///   "blocks"                int
		///   "corners_won"           int
		///   "free_kicks_won"        int
		///   "penalties_awarded"     int
		///   "fouls_committed"       int
		///   "fouls_drawn"           int
		///   "yellow_cards"          int
		///   "red_cards"             int
		///   "total_distance"        float
		///   "total_sprint_distance" float
		/// </summary>
		public Godot.Collections.Dictionary GetTeamStats(int teamId)
		{
			var dict = new Godot.Collections.Dictionary();
			if (_replay == null) return dict;

			TeamMatchStats s = teamId == 0 ? _replay.HomeStats : _replay.AwayStats;
			if (s == null) return dict;

			dict["team_id"] = s.TeamId;
			dict["name"] = s.Name ?? string.Empty;
			dict["goals_scored"] = s.GoalsScored;
			dict["goals_conceded"] = s.GoalsConceded;
			dict["possession_percent"] = s.PossessionPercent;
			dict["shots_total"] = s.ShotsTotal;
			dict["shots_on_target"] = s.ShotsOnTarget;
			dict["shots_off_target"] = s.ShotsOffTarget;
			dict["shots_blocked"] = s.ShotsBlocked;
			dict["xg"] = s.XG;
			dict["xga"] = s.XGA;
			dict["passes_attempted"] = s.PassesAttempted;
			dict["passes_completed"] = s.PassesCompleted;
			dict["pass_accuracy"] = s.PassAccuracy;
			dict["progressive_passes"] = s.ProgressivePasses;
			dict["key_passes"] = s.KeyPasses;
			dict["long_balls_attempted"] = s.LongBallsAttempted;
			dict["long_balls_completed"] = s.LongBallsCompleted;
			dict["ppda"] = s.PPDA;
			dict["pressures_attempted"] = s.PressuresAttempted;
			dict["pressures_successful"] = s.PressuresSuccessful;
			dict["tackles_attempted"] = s.TacklesAttempted;
			dict["tackles_won"] = s.TacklesWon;
			dict["interceptions"] = s.Interceptions;
			dict["clearances"] = s.Clearances;
			dict["blocks"] = s.Blocks;
			dict["corners_won"] = s.CornersWon;
			dict["free_kicks_won"] = s.FreeKicksWon;
			dict["penalties_awarded"] = s.PenaltiesAwarded;
			dict["fouls_committed"] = s.FoulsCommitted;
			dict["fouls_drawn"] = s.FoulsDrawn;
			dict["yellow_cards"] = s.YellowCards;
			dict["red_cards"] = s.RedCards;
			dict["total_distance"] = s.TotalDistanceCovered;
			dict["total_sprint_distance"] = s.TotalSprintDistance;

			return dict;
		}

		/// <summary>
		/// Returns the HypothesisResult for one team as a Godot Dictionary.
		/// teamId = 0 (home) or 1 (away).
		///
		/// Returned keys:
		///   "overall_execution"          float  0–100
		///   "headline_summary"           String
		///   "press_collapse_minute"      int    (-1 if no collapse)
		///   "press_collapse_player_id"   int    (-1 if no collapse)
		///
		///   Per dimension (prefix = "pressing_", "possession_", "high_line_",
		///                  "passing_style_", "attacking_width_", "tempo_", "shape_held_"):
		///   "{prefix}intent"             float  0.0–1.0
		///   "{prefix}actual"             float  0.0–1.0
		///   "{prefix}gap"                float  intent - actual
		///   "{prefix}execution"          float  1 - abs(gap)  [0=worst, 1=perfect]
		///   "{prefix}reason"             String one-sentence explanation
		/// </summary>
		public Godot.Collections.Dictionary GetHypothesis(int teamId)
		{
			var dict = new Godot.Collections.Dictionary();
			if (_replay == null) return dict;

			TeamMatchStats ts = teamId == 0 ? _replay.HomeStats : _replay.AwayStats;
			if (ts?.Hypothesis == null) return dict;

			var h = ts.Hypothesis;
			dict["overall_execution"] = h.OverallExecutionScore;
			dict["headline_summary"] = h.HeadlineSummary ?? string.Empty;
			dict["press_collapse_minute"] = h.PressCollapseMinute;
			dict["press_collapse_player_id"] = h.PressCollapsePlayerId;

			AppendDimension(dict, "pressing_", h.Pressing);
			AppendDimension(dict, "possession_", h.Possession);
			AppendDimension(dict, "high_line_", h.HighLine);
			AppendDimension(dict, "passing_style_", h.PassingStyle);
			AppendDimension(dict, "attacking_width_", h.AttackingWidth);
			AppendDimension(dict, "tempo_", h.Tempo);
			AppendDimension(dict, "shape_held_", h.ShapeHeld);

			return dict;
		}

		// =====================================================================
		// PRIVATE — TYPE CONVERSION HELPERS
		// All Vec2 ↔ Vector2 conversions live here. Nowhere else.
		// =====================================================================

		/// <summary>
		/// Converts engine Vec2 to Godot Vector2.
		/// Engine X→Godot X, Engine Y→Godot Y. No scaling needed.
		/// Pitch coordinates are already in the pixel-unit range Godot expects.
		/// </summary>
		private static Vector2 ToVector2(Vec2 v)
			=> new Vector2(v.X, v.Y);

		/// <summary>
		/// Converts Godot Vector2 to engine Vec2.
		/// Used when reading player position input from GDScript debug tools.
		/// </summary>
		private static Vec2 ToVec2(Vector2 v)
			=> new Vec2(v.X, v.Y);

		// =====================================================================
		// PRIVATE — INPUT PARSING
		// Converts Godot Dictionaries → engine data types.
		// All parsing is defensive — missing keys fall back to safe defaults.
		// =====================================================================

		/// <summary>
		/// Parses a Godot Dictionary representing one team into a TeamData instance.
		/// Missing keys emit a debug warning and use safe defaults.
		/// Throws only on structural errors (wrong player count, invalid role name).
		/// </summary>
		private TeamData ParseTeamData(Godot.Collections.Dictionary d, int teamId)
		{
			var team = new TeamData
			{
				TeamId = teamId,
				Name = ReadString(d, "name", $"Team {teamId}"),
				FormationName = ReadString(d, "formation", "4-3-3"),
				KitColorPrimary = ReadInt(d, "kit_primary", teamId == 0 ? 0x2266FF : 0xFF3333),
				KitColorSecondary = ReadInt(d, "kit_secondary", 0xFFFFFF),
			};

			// Parse tactics
			if (d.ContainsKey("tactics") && d["tactics"].VariantType == Variant.Type.Dictionary)
				team.Tactics = ParseTactics(d["tactics"].AsGodotDictionary());
			else
				team.Tactics = TacticsInput.Default();

			// Parse players
			if (!d.ContainsKey("players"))
				throw new Exception($"[Bridge] Team '{team.Name}': missing 'players' array.");

			var playersVariant = d["players"];
			if (playersVariant.VariantType != Variant.Type.Array)
				throw new Exception($"[Bridge] Team '{team.Name}': 'players' must be an Array.");

			Godot.Collections.Array playersArr = playersVariant.AsGodotArray();
			if (playersArr.Count != 11)
				throw new Exception(
					$"[Bridge] Team '{team.Name}': 'players' must have exactly 11 entries. " +
					$"Got {playersArr.Count}.");

			team.Players = new PlayerData[11];
			int globalOffset = teamId == 0 ? 0 : 11;

			for (int slot = 0; slot < 11; slot++)
			{
				if (playersArr[slot].VariantType != Variant.Type.Dictionary)
					throw new Exception(
						$"[Bridge] Team '{team.Name}': players[{slot}] must be a Dictionary.");

				team.Players[slot] = ParsePlayerData(
					playersArr[slot].AsGodotDictionary(),
					playerId: globalOffset + slot,
					slot: slot,
					teamName: team.Name
				);
			}

			return team;
		}

		private PlayerData ParsePlayerData(Godot.Collections.Dictionary d,
											int playerId, int slot, string teamName)
		{
			string roleStr = ReadString(d, "role", "CM");

			if (!Enum.TryParse<PlayerRole>(roleStr, out PlayerRole role))
			{
				if (DEBUG)
					GD.PushWarning($"[Bridge] {teamName} slot {slot}: " +
								   $"unknown role '{roleStr}', defaulting to CM.");
				role = PlayerRole.CM;
			}

			return new PlayerData
			{
				PlayerId = playerId,
				ShirtNumber = ReadInt(d, "shirt_number", slot + 1),
				Name = ReadString(d, "name", $"P{slot + 1}"),
				Role = role,
				BaseSpeed = ReadFloat(d, "base_speed", 0.5f),
				StaminaAttribute = ReadFloat(d, "stamina", 0.7f),
				PassingAbility = ReadFloat(d, "passing", 0.5f),
				ShootingAbility = ReadFloat(d, "shooting", 0.5f),
				DribblingAbility = ReadFloat(d, "dribbling", 0.5f),
				DefendingAbility = ReadFloat(d, "defending", 0.5f),
				Reactions = ReadFloat(d, "reactions", 0.5f),
				Ego = ReadFloat(d, "ego", 0.5f),
			};
		}

		private TacticsInput ParseTactics(Godot.Collections.Dictionary d)
		{
			// All fields optional — missing keys default to 0.5 (neutral).
			// MathUtil.Clamp01 ensures rogue GDScript values can't break the engine.
			return new TacticsInput
			{
				PressingIntensity = Clamp01(ReadFloat(d, "pressing_intensity", 0.5f)),
				PressingTrigger = Clamp01(ReadFloat(d, "pressing_trigger", 0.5f)),
				PressCompactness = Clamp01(ReadFloat(d, "press_compactness", 0.5f)),
				DefensiveLine = Clamp01(ReadFloat(d, "defensive_line", 0.5f)),
				DefensiveWidth = Clamp01(ReadFloat(d, "defensive_width", 0.5f)),
				DefensiveAggression = Clamp01(ReadFloat(d, "defensive_aggression", 0.5f)),
				PossessionFocus = Clamp01(ReadFloat(d, "possession_focus", 0.5f)),
				BuildUpSpeed = Clamp01(ReadFloat(d, "build_up_speed", 0.5f)),
				PassingDirectness = Clamp01(ReadFloat(d, "passing_directness", 0.5f)),
				AttackingWidth = Clamp01(ReadFloat(d, "attacking_width", 0.5f)),
				AttackingLine = Clamp01(ReadFloat(d, "attacking_line", 0.5f)),
				TransitionSpeed = Clamp01(ReadFloat(d, "transition_speed", 0.5f)),
				CrossingFrequency = Clamp01(ReadFloat(d, "crossing_frequency", 0.5f)),
				ShootingThreshold = Clamp01(ReadFloat(d, "shooting_threshold", 0.5f)),
				Tempo = Clamp01(ReadFloat(d, "tempo", 0.5f)),
				OutOfPossessionShape = Clamp01(ReadFloat(d, "out_of_possession_shape", 0.5f)),
				InPossessionSpread = Clamp01(ReadFloat(d, "in_possession_spread", 0.5f)),
				FreedomLevel = Clamp01(ReadFloat(d, "freedom_level", 0.5f)),
				CounterAttackFocus = Clamp01(ReadFloat(d, "counter_attack_focus", 0.5f)),
				OffsideTrapFrequency = Clamp01(ReadFloat(d, "offside_trap_frequency", 0.2f)),
				PhysicalityBias = Clamp01(ReadFloat(d, "physicality_bias", 0.5f)),
				SetPieceFocus = Clamp01(ReadFloat(d, "set_piece_focus", 0.5f)),
			};
		}

		// =====================================================================
		// PRIVATE — DICTIONARY READ HELPERS
		// All key access is defensive — missing key returns a safe default.
		// Uses the engine's MathUtil.Clamp01 for any float clamping.
		// =====================================================================

		private static string ReadString(Godot.Collections.Dictionary d,
										  string key, string fallback)
		{
			if (!d.ContainsKey(key)) return fallback;
			var v = d[key];
			if (v.VariantType == Variant.Type.String) return v.AsString();
			return fallback;
		}

		private static int ReadInt(Godot.Collections.Dictionary d,
									string key, int fallback)
		{
			if (!d.ContainsKey(key)) return fallback;
			var v = d[key];
			if (v.VariantType == Variant.Type.Int) return v.AsInt32();
			if (v.VariantType == Variant.Type.Float) return (int)v.AsSingle();
			return fallback;
		}

		private static float ReadFloat(Godot.Collections.Dictionary d,
										string key, float fallback)
		{
			if (!d.ContainsKey(key)) return fallback;
			var v = d[key];
			if (v.VariantType == Variant.Type.Float) return v.AsSingle();
			if (v.VariantType == Variant.Type.Int) return (float)v.AsInt32();
			return fallback;
		}

		// =====================================================================
		// PRIVATE — MATH HELPERS
		// The ONLY math allowed here. Delegates to MathUtil — no MathF.Lerp.
		// =====================================================================

		/// <summary>
		/// Clamps float to [0, 1]. Delegates to MathUtil.Clamp01.
		/// Bridge must not implement its own clamp logic.
		/// </summary>
		private static float Clamp01(float v) => MathUtil.Clamp01(v);

		// =====================================================================
		// PRIVATE — HYPOTHESIS DIMENSION HELPER
		// =====================================================================

		private static void AppendDimension(Godot.Collections.Dictionary dict,
											 string prefix,
											 HypothesisDimension dim)
		{
			dict[prefix + "intent"] = dim.IntentScore;
			dict[prefix + "actual"] = dim.ActualScore;
			dict[prefix + "gap"] = dim.ExecutionGap;
			dict[prefix + "execution"] = dim.ExecutionScore;
			dict[prefix + "reason"] = dim.GapReason ?? string.Empty;
		}
	}
}
