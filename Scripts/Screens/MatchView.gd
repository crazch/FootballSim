# =============================================================================
# Module:  MatchView.gd
# Path:    FootballSim/Scripts/Screens/MatchView.gd
# Purpose:
#   Orchestration layer for the match screen. The single contact point between
#   the Bridge (engine output) and all visualization/UI nodes.
#
#   MatchView is a state machine with three states:
#     SIMULATING  → Bridge.SimulateMatch() running in a Thread
#     PLAYING     → ReplayPlayer advancing, signals flowing to UI
#     FINISHED    → replay reached end, waiting for user to go to PostMatch
#
#   Strict rules (mirrors the brief):
#     • MatchView calls Bridge. No other node calls Bridge.
#     • MatchView calls ReplayPlayer.start_replay(). Nothing else starts replay.
#     • MatchView connects all signals. Nodes do not cross-connect to each other.
#     • MatchView never computes positions, scores, stamina, or events.
#     • MatchView never implements a tick loop.
#     • MatchView reads team data from GameState (set by TacticsScreen).
#       If GameState is empty it falls back to hardcoded test teams so the
#       scene is runnable in isolation during development.
#
# Owned connections (all wired in _wire_signals()):
#   ReplayPlayer.score_changed     → HUD.on_score_changed
#   ReplayPlayer.tick_advanced     → HUD.on_tick_advanced
#   ReplayPlayer.phase_changed     → HUD.on_phase_changed
#   ReplayPlayer.event_fired       → HUD.on_event_fired
#   ReplayPlayer.event_fired       → EventLogUI.on_event_fired
#   ReplayPlayer.tick_advanced     → _on_tick_advanced       (overlay routing)
#   ReplayPlayer.phase_changed     → _on_phase_changed       (pause/resume overlays)
#   ReplayPlayer.playback_finished → _on_playback_finished   (state transition)
#   ReplayPlayer.playback_finished → EventLogUI.on_playback_finished
#   ReplayPlayer.tick_advanced     → SpeedControls.on_tick_advanced
#   ReplayPlayer.playback_finished → SpeedControls.on_playback_finished
#   ReplayPlayer.phase_changed     → SpeedControls.on_phase_changed
#
# Scene tree — MUST match this exactly (see also the .tscn comment below):
#
#   MatchView (Node2D)                     ← this script
#     ├── PitchRenderer (Node2D)
#     ├── OverlayLayer (Node2D)
#     │     ├── FormationGhostsOverlay (Node2D)
#     │     ├── DefensiveLineOverlay (Node2D)
#     │     └── PressingLinesOverlay (Node2D)
#     ├── PlayersLayer (Node2D)
#     ├── BallDot (Node2D)
#     ├── ReplayPlayer (Node)
#     ├── HUD (CanvasLayer)
#     ├── EventLogUI (CanvasLayer)
#     ├── SpeedControls (CanvasLayer)
#     └── LoadingOverlay (CanvasLayer)      ← shown during simulation
#           └── LoadingPanel (Panel)
#                 └── LoadingLabel (Label)
#
# Inspector @export slots on this script:
#   replay_player, overlay_layer, hud, event_log_ui, speed_controls,
#   loading_label, loading_overlay
#   (players_layer and ball_dot are set on ReplayPlayer, not here)
#
# GameState AutoLoad provides:
#   GameState.home_team : Dictionary
#   GameState.away_team : Dictionary
#   GameState.match_seed : int
#
# Strict typing: no := on arrays / lerp / get / bridge results / math.
# =============================================================================

extends Node2D

# ── State machine ─────────────────────────────────────────────────────────────

enum MatchState {
	IDLE, # scene just loaded, nothing started
	SIMULATING, # Bridge.SimulateMatch() running in Thread
	PLAYING, # ReplayPlayer running
	FINISHED, # replay reached end
}

# ── MatchPhase mirror (must match C# MatchPhase enum order) ──────────────────
const PHASE_GOALSCORED: int = 4
const PHASE_HALFTIME: int = 5
const PHASE_FULLTIME: int = 6

# ── PostMatch scene path ──────────────────────────────────────────────────────
const POSTMATCH_SCENE: String = "res://Scenes/PostMatch.tscn"

# ── Simulation finish delay before auto-transitioning to PostMatch (seconds) ──
# 0 = user must press a button. >0 = auto-advance after N seconds.
const AUTO_POSTMATCH_DELAY: float = 0.0

# ── Exports — assign every slot in the Godot Inspector ───────────────────────

## The ReplayPlayer node.
@export var replay_player: Node = null

## The OverlayLayer node.
@export var overlay_layer: Node2D = null

## The HUD CanvasLayer node.
@export var hud: Node = null

## The EventLogUI CanvasLayer node.
@export var event_log_ui: Node = null

## The SpeedControls CanvasLayer node.
@export var speed_controls: Node = null

## The loading overlay CanvasLayer (shown while simulating).
@export var loading_overlay: Node = null

## The label inside the loading overlay.
@export var loading_label: Label = null

## Enable verbose logging for this scene.
@export var DEBUG: bool = false

## Enable debug mode for 11v11 match simulation (captures detailed logs).
## When true, DebugTickRunner is used instead of MatchEngine.Simulate().
@export var debug_mode_enabled: bool = false

## Debug capture mode: "None" (no logs, fastest), "Light" (positions/actions,
## ~16 MB for 54k ticks), or "Full" (with scores, ~43 MB for 54k ticks).
## "Light" is recommended for 11v11; "Full" for 2v2/3v3 scenarios.
@export var debug_capture_mode: String = "Light"

## Debug capture range (ticks). Use these to export a small window
## of tick logs (e.g., 0..600) to keep JSON small for LLM analysis.
@export var debug_capture_start_tick: int = 0
@export var debug_capture_end_tick: int = 600

## Default export filename (will be prefixed with timestamp when saving)
@export var debug_export_basename: String = "match_debug_export.json"

# ── Internal state ────────────────────────────────────────────────────────────

var _state: MatchState = MatchState.IDLE
var _sim_thread: Thread = null
var _home_team: Dictionary = {}
var _away_team: Dictionary = {}
var _match_seed: int = 0
var _signals_wired: bool = false

# Timer for auto-PostMatch transition after playback_finished
var _finish_timer: float = 0.0
var _finish_waiting: bool = false

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	_set_loading_visible(false)

	# Load team data from GameState AutoLoad.
	# Falls back to hardcoded test teams if GameState is empty —
	# this lets you run MatchView.tscn directly in the editor.
	_load_team_data()

	# Wire all signals before simulation starts so nothing is missed.
	_wire_signals()

	# Begin simulation immediately.
	_begin_simulation()


func _process(delta: float) -> void:
	# Poll thread completion each frame — Thread.wait_to_finish() blocks;
	# is_alive() lets us check without blocking.
	if _state == MatchState.SIMULATING and _sim_thread != null:
		if not _sim_thread.is_alive():
			_sim_thread.wait_to_finish()
			_sim_thread = null
			_on_simulation_complete()

	# Auto-transition to PostMatch after delay
	if _finish_waiting and AUTO_POSTMATCH_DELAY > 0.0:
		_finish_timer -= delta
		if _finish_timer <= 0.0:
			_finish_waiting = false
			EventBus.go_to_postmatch.emit()

# ── Signal wiring (all connections owned here) ────────────────────────────────

func _wire_signals() -> void:
	if _signals_wired:
		return
	_signals_wired = true

	if replay_player == null:
		push_error("[MatchView] replay_player is null — assign it in the Inspector.")
		return

	# HUD
	if hud != null:
		replay_player.score_changed.connect(hud.on_score_changed)
		replay_player.tick_advanced.connect(hud.on_tick_advanced)
		replay_player.phase_changed.connect(hud.on_phase_changed)
		replay_player.event_fired.connect(hud.on_event_fired)

	# EventLogUI
	if event_log_ui != null:
		replay_player.event_fired.connect(event_log_ui.on_event_fired)
		replay_player.playback_finished.connect(event_log_ui.on_playback_finished)

	# SpeedControls
	if speed_controls != null:
		replay_player.tick_advanced.connect(speed_controls.on_tick_advanced)
		replay_player.playback_finished.connect(speed_controls.on_playback_finished)
		replay_player.phase_changed.connect(speed_controls.on_phase_changed)

	# OverlayLayer — routed via MatchView handlers so frame data is fetched here
	replay_player.tick_advanced.connect(_on_tick_advanced)
	replay_player.phase_changed.connect(_on_phase_changed)

	# Playback finished
	replay_player.playback_finished.connect(_on_playback_finished)

	if DEBUG:
		print("[MatchView] All signals wired.")

# ── Team data ─────────────────────────────────────────────────────────────────

func _load_team_data() -> void:
	# Read from GameState AutoLoad if it exists and has data.
	if Engine.has_singleton("GameState"):
		var gs = Engine.get_singleton("GameState")
		var home: Dictionary = gs.home_team
		var away: Dictionary = gs.away_team
		var match_seed: int = gs.match_seed

		if not home.is_empty() and not away.is_empty():
			_home_team = home
			_away_team = away
			_match_seed = match_seed
			if DEBUG:
				print("[MatchView] Loaded teams from GameState.")
			return

	# Fallback: hardcoded test teams for running the scene directly.
	if DEBUG:
		print("[MatchView] GameState empty — using test teams.")
	_home_team = _build_test_team_home()
	_away_team = _build_test_team_away()
	_match_seed = 41

# ── Simulation ────────────────────────────────────────────────────────────────

func _begin_simulation() -> void:
	# Set engine debug flags based on debug_mode_enabled
	# This enables logging WITHIN MatchEngine.Simulate(), not replacing it
	Bridge.SetDebugFlags(debug_mode_enabled, -1)

	# Configure MatchEngine capture range so Inspector controls both
	# console printing and captured TickLogs for JSON.
	if debug_mode_enabled:
		Bridge.SetDebugCaptureRange(debug_capture_start_tick, debug_capture_end_tick, debug_capture_mode)
	else:
		Bridge.ClearDebugCaptureRange()
	
	_state = MatchState.SIMULATING
	_set_loading_visible(true)

	if loading_label != null:
		loading_label.text = "Simulating match…"

	if DEBUG:
		print("[MatchView] Starting simulation thread. Seed=%d Debug=%s Mode=%s" % [
			_match_seed, debug_mode_enabled, debug_capture_mode
		])

	# Run simulation in a background thread to avoid freezing the UI.
	# Thread captures the team dictionaries by value at this point.
	_sim_thread = Thread.new()
	_sim_thread.start(_run_simulation_thread)


func _run_simulation_thread() -> void:
	# This runs on a background thread.
	# Bridge.SimulateMatch() is the ONLY call on this thread.
	# No Godot node access allowed from here.
	# Debug mode is enabled via SetDebugFlags() in _begin_simulation().
	Bridge.SimulateMatch(_home_team, _away_team, _match_seed)


func _on_simulation_complete() -> void:
	# Disabled unless need quick search
	# Bridge.DumpTickRange(32300, 32340, 8)
	# Back on the main thread.
	var err: String = Bridge.GetLastError()
	if err != "":
		push_error("[MatchView] Simulation failed: " + err)
		if loading_label != null:
			loading_label.text = "Error: " + err
		return
	if DEBUG:
		print("[MatchView] Simulation complete. Frames=%d Score=%d-%d" % [
			Bridge.GetTotalFrames(),
			Bridge.GetFinalHomeScore(),
			Bridge.GetFinalAwayScore()
		])

	_set_loading_visible(false)
	_begin_replay()

# ── Replay startup ────────────────────────────────────────────────────────────

func _begin_replay() -> void:
	if not Bridge.IsReplayReady():
		push_error("[MatchView] _begin_replay called but bridge not ready.")
		return

	_state = MatchState.PLAYING

	# Unpack kit colours for nodes that need them
	var home_kit: int = _home_team.get("kit_primary", 0x2266FF)
	var away_kit: int = _away_team.get("kit_primary", 0xFF3333)
	var home_name: String = _home_team.get("name", "Home")
	var away_name: String = _away_team.get("name", "Away")

	# Configure ReplayPlayer kit colours before spawning dots
	if replay_player != null:
		replay_player.home_kit_primary = home_kit
		replay_player.away_kit_primary = away_kit
		replay_player.home_kit_secondary = _home_team.get("kit_secondary", 0xFFFFFF)
		replay_player.away_kit_secondary = _away_team.get("kit_secondary", 0xFFFFFF)

	# Initialise HUD with team names and colours
	if hud != null and hud.has_method("initialise"):
		hud.initialise(home_name, away_name, home_kit, away_kit)

	# Initialise SpeedControls with total tick count
	if speed_controls != null and speed_controls.has_method("initialise"):
		speed_controls.initialise(Bridge.GetTotalFrames())

	# Initialise OverlayLayer with frame-0 anchor positions
	var first_frame: Dictionary = Bridge.GetFrame(0)
	if overlay_layer != null and overlay_layer.has_method("initialise"):
		overlay_layer.initialise(first_frame, home_kit, away_kit)

	# Check hypothesis for stamina collapse warning (show if collapse detected)
	_check_stamina_warning()

	# Start replay — this enables ReplayPlayer._process() and begins tick loop
	if replay_player != null:
		replay_player.start_replay()

	if DEBUG:
		print("[MatchView] Replay started.")

# ── Signal handlers ───────────────────────────────────────────────────────────

## Routes live frame data to OverlayLayer each tick.
## MatchView fetches the frame here so overlay nodes never call Bridge.
func _on_tick_advanced(tick: int, _match_minute: int) -> void:
	if overlay_layer == null:
		return
	var frame: Dictionary = Bridge.GetFrame(tick)
	if frame.is_empty():
		return
	if overlay_layer.has_method("update_frame"):
		overlay_layer.update_frame(frame)


## Routes pause/resume state to OverlayLayer (ghost anchors).
func _on_phase_changed(phase_int: int) -> void:
	if overlay_layer == null:
		return

	# Show ghost anchors on pause-like phases
	if phase_int == PHASE_HALFTIME or phase_int == PHASE_GOALSCORED:
		if overlay_layer.has_method("on_paused"):
			overlay_layer.on_paused()
	else:
		if overlay_layer.has_method("on_resumed"):
			overlay_layer.on_resumed()


## Called when ReplayPlayer reaches the last tick.
func _on_playback_finished() -> void:
	_state = MatchState.FINISHED

	if DEBUG:
		print("[MatchView] Playback finished.")

	if AUTO_POSTMATCH_DELAY > 0.0:
		# Auto-advance after a delay (handled in _process)
		_finish_waiting = true
		_finish_timer = AUTO_POSTMATCH_DELAY
	else:
		# Emit immediately — SceneManager listener transitions to PostMatch.
		EventBus.go_to_postmatch.emit()


# ── Overlay toggle API (called by future toggle buttons in HUD) ───────────────

## Toggle an overlay on/off. Delegates to OverlayLayer.
## overlay_name: "defensive_line" | "pressing" | "ghosts"
func toggle_overlay(overlay_name: String, enabled: bool) -> void:
	if overlay_layer != null and overlay_layer.has_method("set_overlay"):
		overlay_layer.set_overlay(overlay_name, enabled)


## Go to PostMatch scene. Called by a "View Stats" button or auto-advance.
func go_to_postmatch() -> void:
	_go_to_postmatch()

# ── Scene transition ──────────────────────────────────────────────────────────

func _go_to_postmatch() -> void:
	if DEBUG:
		print("[MatchView] Transitioning to PostMatch.")
	get_tree().change_scene_to_file(POSTMATCH_SCENE)

# ── Loading overlay ───────────────────────────────────────────────────────────

func _set_loading_visible(visible_flag: bool) -> void:
	if loading_overlay != null:
		loading_overlay.visible = visible_flag

# ── Stamina collapse warning ──────────────────────────────────────────────────

func _check_stamina_warning() -> void:
	# Read hypothesis for home team — if press collapse was detected, warn HUD.
	if hud == null or not hud.has_method("show_stamina_warning"):
		return

	var home_hyp: Dictionary = Bridge.GetHypothesis(0)
	var away_hyp: Dictionary = Bridge.GetHypothesis(1)

	var home_collapse: int = home_hyp.get("press_collapse_minute", -1)
	var away_collapse: int = away_hyp.get("press_collapse_minute", -1)

	# Show warning for whichever team has the earlier collapse
	if home_collapse >= 0 and (away_collapse < 0 or home_collapse <= away_collapse):
		var home_name: String = _home_team.get("name", "Home")
		hud.show_stamina_warning(home_name, home_collapse)
	elif away_collapse >= 0:
		var away_name: String = _away_team.get("name", "Away")
		hud.show_stamina_warning(away_name, away_collapse)


## -------------------- Debug JSON range export -------------------------
## Export a JSON snapshot for ticks in [debug_capture_start_tick..debug_capture_end_tick).
## Returns JSON string or "{}"/"[]" on error/empty.
func export_debug_range(playerIds: Array = []) -> String:
	if not Bridge.IsReplayReady():
		push_error("[MatchView] export_debug_range: Replay not ready. Run simulation first.")
		return "{}"

	if not debug_mode_enabled:
		push_error("[MatchView] export_debug_range: Debug mode not enabled. Set debug_mode_enabled=true before simulation.")
		return "{}"

	var json: String
	if playerIds.size() > 0:
		json = Bridge.ExportDebugJSONRange(_home_team, _away_team, _match_seed, debug_capture_start_tick, debug_capture_end_tick, debug_capture_mode, playerIds)
	else:
		json = Bridge.ExportDebugJSONRange(_home_team, _away_team, _match_seed, debug_capture_start_tick, debug_capture_end_tick, debug_capture_mode, null)

	return json


## Saves the debug JSON to user:// and returns the saved path, or empty string on error.
func save_debug_range_to_file(playerIds: Array = []) -> String:
	var json = export_debug_range(playerIds)
	if json == "{}" or json == "[]":
		push_error("[MatchView] save_debug_range_to_file: No data to export")
		return ""

	var path = "user://" + debug_export_basename
	var file = FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		push_error("[MatchView] save_debug_range_to_file: Failed to open file: " + path)
		return ""

	file.store_string(json)
	file.close()

	if DEBUG:
		print("[MatchView] ✓ Saved debug JSON to: " + path)

	return path

# ── Test team builders (fallback when GameState is empty) ─────────────────────
# These allow running MatchView.tscn directly from the editor without going
# through TacticsScreen first. All attribute values are 0.0–1.0.

func _build_test_team_home() -> Dictionary:
	return {
		"name": "Home FC",
		"formation": "4-3-3",
		"kit_primary": 0x2266FF,
		"kit_secondary": 0xFFFFFF,
		"tactics": {
			"pressing_intensity": 0.85,
			"pressing_trigger": 0.80,
			"press_compactness": 0.65,
			"defensive_line": 0.72,
			"defensive_width": 0.55,
			"defensive_aggression": 0.62,
			"possession_focus": 0.55,
			"build_up_speed": 0.75,
			"passing_directness": 0.50,
			"attacking_width": 0.65,
			"attacking_line": 0.75,
			"transition_speed": 0.90,
			"crossing_frequency": 0.40,
			"shooting_threshold": 0.45,
			"tempo": 0.80,
			"out_of_possession_shape": 0.70,
			"in_possession_spread": 0.60,
			"freedom_level": 0.55,
			"counter_attack_focus": 0.65,
			"offside_trap_frequency": 0.40,
			"physicality_bias": 0.50,
			"set_piece_focus": 0.55,
		},
		"players": [
			{"shirt_number": 1, "name": "Keeper", "role": "GK",
			 "base_speed": 0.45, "stamina": 0.70, "passing": 0.55,
			 "shooting": 0.10, "dribbling": 0.20, "defending": 0.75, "reactions": 0.80, "ego": 0.20},
			{"shirt_number": 2, "name": "Right Back", "role": "RB",
			 "base_speed": 0.55, "stamina": 0.72, "passing": 0.60,
			 "shooting": 0.20, "dribbling": 0.45, "defending": 0.65, "reactions": 0.60, "ego": 0.30},
			{"shirt_number": 5, "name": "CB Left", "role": "CB",
			 "base_speed": 0.48, "stamina": 0.68, "passing": 0.55,
			 "shooting": 0.10, "dribbling": 0.25, "defending": 0.80, "reactions": 0.65, "ego": 0.25},
			{"shirt_number": 4, "name": "CB Right", "role": "CB",
			 "base_speed": 0.48, "stamina": 0.68, "passing": 0.55,
			 "shooting": 0.10, "dribbling": 0.25, "defending": 0.78, "reactions": 0.62, "ego": 0.25},
			{"shirt_number": 3, "name": "Left Back", "role": "LB",
			 "base_speed": 0.55, "stamina": 0.72, "passing": 0.60,
			 "shooting": 0.20, "dribbling": 0.45, "defending": 0.65, "reactions": 0.60, "ego": 0.30},
			{"shirt_number": 8, "name": "CM Right", "role": "CM",
			 "base_speed": 0.55, "stamina": 0.78, "passing": 0.70,
			 "shooting": 0.40, "dribbling": 0.55, "defending": 0.55, "reactions": 0.65, "ego": 0.45},
			{"shirt_number": 6, "name": "Anchor", "role": "CDM",
			 "base_speed": 0.50, "stamina": 0.80, "passing": 0.65,
			 "shooting": 0.25, "dribbling": 0.40, "defending": 0.72, "reactions": 0.68, "ego": 0.30},
			{"shirt_number": 10, "name": "CM Left", "role": "CM",
			 "base_speed": 0.55, "stamina": 0.76, "passing": 0.72,
			 "shooting": 0.45, "dribbling": 0.60, "defending": 0.50, "reactions": 0.62, "ego": 0.50},
			{"shirt_number": 7, "name": "Right IW", "role": "IW",
			 "base_speed": 0.65, "stamina": 0.70, "passing": 0.65,
			 "shooting": 0.68, "dribbling": 0.78, "defending": 0.30, "reactions": 0.70, "ego": 0.65},
			{"shirt_number": 9, "name": "Striker", "role": "ST",
			 "base_speed": 0.60, "stamina": 0.65, "passing": 0.50,
			 "shooting": 0.82, "dribbling": 0.62, "defending": 0.20, "reactions": 0.72, "ego": 0.75},
			{"shirt_number": 11, "name": "Left IW", "role": "IW",
			 "base_speed": 0.63, "stamina": 0.70, "passing": 0.68,
			 "shooting": 0.65, "dribbling": 0.75, "defending": 0.30, "reactions": 0.68, "ego": 0.60},
		]
	}


func _build_test_team_away() -> Dictionary:
	return {
		"name": "Away FC",
		"formation": "4-2-3-1",
		"kit_primary": 0xFF3333,
		"kit_secondary": 0xFFFFFF,
		"tactics": {
			"pressing_intensity": 0.40,
			"pressing_trigger": 0.40,
			"press_compactness": 0.65,
			"defensive_line": 0.30,
			"defensive_width": 0.50,
			"defensive_aggression": 0.50,
			"possession_focus": 0.60,
			"build_up_speed": 0.55,
			"passing_directness": 0.45,
			"attacking_width": 0.60,
			"attacking_line": 0.55,
			"transition_speed": 0.70,
			"crossing_frequency": 0.45,
			"shooting_threshold": 0.50,
			"tempo": 0.55,
			"out_of_possession_shape": 0.75,
			"in_possession_spread": 0.55,
			"freedom_level": 0.50,
			"counter_attack_focus": 0.60,
			"offside_trap_frequency": 0.20,
			"physicality_bias": 0.50,
			"set_piece_focus": 0.50,
		},
		"players": [
			{"shirt_number": 1, "name": "GK", "role": "GK",
			 "base_speed": 0.45, "stamina": 0.70, "passing": 0.55,
			 "shooting": 0.10, "dribbling": 0.20, "defending": 0.75, "reactions": 0.78, "ego": 0.20},
			{"shirt_number": 2, "name": "RB", "role": "RB",
			 "base_speed": 0.55, "stamina": 0.70, "passing": 0.58,
			 "shooting": 0.20, "dribbling": 0.42, "defending": 0.64, "reactions": 0.58, "ego": 0.28},
			{"shirt_number": 5, "name": "CB1", "role": "CB",
			 "base_speed": 0.48, "stamina": 0.68, "passing": 0.53,
			 "shooting": 0.10, "dribbling": 0.22, "defending": 0.79, "reactions": 0.63, "ego": 0.22},
			{"shirt_number": 4, "name": "CB2", "role": "CB",
			 "base_speed": 0.48, "stamina": 0.68, "passing": 0.53,
			 "shooting": 0.10, "dribbling": 0.22, "defending": 0.77, "reactions": 0.60, "ego": 0.22},
			{"shirt_number": 3, "name": "LB", "role": "LB",
			 "base_speed": 0.55, "stamina": 0.70, "passing": 0.58,
			 "shooting": 0.20, "dribbling": 0.42, "defending": 0.64, "reactions": 0.58, "ego": 0.28},
			{"shirt_number": 6, "name": "CDM1", "role": "CDM",
			 "base_speed": 0.52, "stamina": 0.80, "passing": 0.65,
			 "shooting": 0.22, "dribbling": 0.38, "defending": 0.70, "reactions": 0.66, "ego": 0.28},
			{"shirt_number": 8, "name": "CDM2", "role": "CDM",
			 "base_speed": 0.52, "stamina": 0.78, "passing": 0.63,
			 "shooting": 0.22, "dribbling": 0.38, "defending": 0.68, "reactions": 0.64, "ego": 0.28},
			{"shirt_number": 7, "name": "WF R", "role": "WF",
			 "base_speed": 0.63, "stamina": 0.68, "passing": 0.62,
			 "shooting": 0.58, "dribbling": 0.72, "defending": 0.28, "reactions": 0.68, "ego": 0.58},
			{"shirt_number": 10, "name": "AM", "role": "AM",
			 "base_speed": 0.58, "stamina": 0.72, "passing": 0.72,
			 "shooting": 0.60, "dribbling": 0.65, "defending": 0.35, "reactions": 0.68, "ego": 0.55},
			{"shirt_number": 11, "name": "WF L", "role": "WF",
			 "base_speed": 0.63, "stamina": 0.68, "passing": 0.62,
			 "shooting": 0.55, "dribbling": 0.70, "defending": 0.28, "reactions": 0.65, "ego": 0.55},
			{"shirt_number": 9, "name": "ST", "role": "ST",
			 "base_speed": 0.62, "stamina": 0.65, "passing": 0.50,
			 "shooting": 0.80, "dribbling": 0.60, "defending": 0.20, "reactions": 0.70, "ego": 0.72},
		]
	}
