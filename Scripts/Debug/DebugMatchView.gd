# =============================================================================
# Script: DebugMatchView.gd
# Path:   FootballSim/Scripts/Screens/DebugMatchView.gd
#
# Purpose:
#   Root script for DebugMatchView.tscn.
#   The ONLY new GDScript in the entire debug visualization system.
#
#   Calls bridge.SimulateDebugScenario() instead of bridge.SimulateMatch().
#   Then calls replay_player.start_replay() exactly as MatchView.gd does.
#   Everything else — PitchRenderer, PlayerDot, BallDot, ReplayPlayer,
#   HUD, EventLogUI, SpeedControls — is reused verbatim from MatchView.tscn.
#
# Scene tree (DebugMatchView.tscn):
#   DebugMatchView (Node2D)            ← this script attached here
#   ├── PitchRenderer (Node2D)         ← reused unchanged
#   ├── PlayersLayer (Node2D)          ← ReplayPlayer spawns PlayerDots here
#   ├── BallDot (Node2D)               ← reused unchanged
#   ├── ReplayPlayer (Node)            ← reused COMPLETELY unchanged
#   ├── HUD (CanvasLayer)              ← reused unchanged (shows 0-0 for debug)
#   ├── EventLogUI (CanvasLayer)       ← reused unchanged (shows debug events)
#   ├── SpeedControls (CanvasLayer)    ← reused unchanged (pause/seek/speed)
#   └── DebugPanel (CanvasLayer)       ← new: scenario picker (see below)
#
# HOW TO CREATE DebugMatchView.tscn:
#   1. Duplicate MatchView.tscn → rename to DebugMatchView.tscn
#   2. Detach MatchView.gd from the root node
#   3. Attach this script (DebugMatchView.gd) to the root node
#   4. Add a DebugPanel CanvasLayer with a VBoxContainer:
#        Label:        "Debug Scenario"
#        OptionButton: id=scenario_picker (items: 1v1, 2v2, 3v3 Short, 3v3 Long)
#        SpinBox:      id=seed_input (min=0, max=999999, value=42)
#        Button:       id=run_button, text="Run Scenario"
#   That is the entire scene setup. No other node changes.
#
# CRITICAL GDScript rule (from MatchEngineBridge docs):
#   NEVER type-annotate the bridge variable:
#     var bridge = MatchEngineBridge.new()    ← correct
#     var bridge: MatchEngineBridge = ...     ← parse-time crash
# =============================================================================

extends Node2D

# ── Node references ────────────────────────────────────────────────────────────
# Assigned in _ready(). Using untyped var — C# object rule from bridge docs.
var _bridge          # MatchEngineBridge — never type-annotate
@onready var _replay_player  = $ReplayPlayer
@onready var _scenario_picker = $DebugPanel/VBox/ScenarioPicker
@onready var _seed_input      = $DebugPanel/VBox/SeedInput
@onready var _run_button      = $DebugPanel/VBox/RunButton
@onready var _status_label    = $DebugPanel/VBox/StatusLabel

# Scenario name strings — must match SimulateDebugScenario() switch cases exactly
const SCENARIO_NAMES = ["1v1", "2v2", "3v3_short", "3v3_long"]
const SCENARIO_LABELS = ["1v1 — Shot Pipeline", "2v2 — Dribble & Tackle",
						 "3v3 Short Pass", "3v3 Long Pass"]

# ── Lifecycle ──────────────────────────────────────────────────────────────────

func _ready() -> void:
	# Get the bridge — same AutoLoad as MatchView.gd uses
	# If MatchEngineBridge is an AutoLoad named "Bridge", use get_node("/root/Bridge")
	# If it is instantiated manually (like in MatchView.gd), adapt accordingly.
	# The pattern here assumes it is an AutoLoad — adjust to match your project.
	_bridge = get_node("/root/Bridge")
	
	_replay_player.players_layer = $PlayersLayer
	_replay_player.ball_dot = $BallDot

	_replay_player.tick_advanced.connect($HUD.on_tick_advanced)
	_replay_player.score_changed.connect($HUD.on_score_changed)
	_replay_player.phase_changed.connect($HUD.on_phase_changed)
	_replay_player.event_fired.connect($HUD.on_event_fired)
	
	$HUD.initialise("Home", "Away", 0x4C99FF, 0xFF6666)
	# Populate scenario picker
	_scenario_picker.clear()
	for label in SCENARIO_LABELS:
		_scenario_picker.add_item(label)

	# Wire run button
	_run_button.pressed.connect(_on_run_pressed)

	# Default status
	_set_status("Pick a scenario and press Run.")

# ── Button handler ─────────────────────────────────────────────────────────────

func _on_run_pressed() -> void:
	var scenario_idx : int  = _scenario_picker.selected
	var seed         : int  = int(_seed_input.value)
	var scenario_name: String = SCENARIO_NAMES[scenario_idx]

	_set_status("Running %s (seed %d)..." % [scenario_name, seed])
	_run_button.disabled = true

	# Defer to next frame so the status label updates before the simulation blocks
	call_deferred("_run_scenario", scenario_name, seed)

func _run_scenario(scenario_name: String, seed: int) -> void:
	# This is the ONLY bridge call that differs from MatchView.gd.
	# SimulateDebugScenario runs the scenario, builds the replay,
	# and loads it into the bridge — all in one call.
	# After this, _bridge.IsReplayReady() returns true and
	# _bridge.GetFrame(tick) works exactly as in production.
	_bridge.SimulateDebugScenario(scenario_name, seed, 1200)

	if not _bridge.IsReplayReady():
		_set_status("ERROR: " + _bridge.GetLastError())
		_run_button.disabled = false
		return

	var total_frames : int = _bridge.GetTotalFrames()
	var home_score   : int = _bridge.GetFinalHomeScore()
	var away_score   : int = _bridge.GetFinalAwayScore()

	_set_status(
		"%s complete — %d ticks | Score: %d–%d" % [
			scenario_name, total_frames, home_score, away_score
		]
	)

	# Start replay — identical to how MatchView.gd starts it.
	# ReplayPlayer.gd reads bridge.GetFrame() the same way as always.
	_replay_player.start_replay()

	_run_button.disabled = false

# ── Helpers ────────────────────────────────────────────────────────────────────

func _set_status(msg: String) -> void:
	if _status_label:
		_status_label.text = msg
