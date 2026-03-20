# =============================================================================
# Module:  GameState.gd
# Path:    FootballSim/Scripts/Autoloads/GameState.gd
# Purpose:
#   Persistent singleton that lives for the entire application lifetime.
#   Stores the two team dictionaries built by TacticsScreen and the match seed
#   so they survive the scene transition from TacticsScreen → MatchView.
#   Also calls Bridge.LoadData() exactly once at startup so every subsequent
#   scene can assume engine data is ready.
#
# AutoLoad registration (Project Settings → AutoLoad):
#   Name: GameState
#   Path: res://Scripts/Autoloads/GameState.gd
#   Add ABOVE Bridge in the AutoLoad list so Bridge exists when _ready() runs.
#
# Fields accessed by other scripts:
#   GameState.home_team   : Dictionary   — home team dict (empty until set)
#   GameState.away_team   : Dictionary   — away team dict (empty until set)
#   GameState.match_seed  : int          — random seed for this match
#   GameState.data_loaded : bool         — true after Bridge.LoadData() succeeds
#
# Public API:
#   GameState.set_match(home, away, seed)   — called by TacticsScreen
#   GameState.clear_match()                 — called after PostMatch to reset
#
# Reading pattern used by MatchView (Engine.has_singleton not needed for AutoLoads
# in Godot 4 — but MatchView uses it for defensive access; this works fine):
#   var gs = GameState          # direct AutoLoad access
#   if not gs.home_team.is_empty():
#       ...
#
# Strict typing: explicit types on all variables.
# =============================================================================

extends Node

# ── Match data ────────────────────────────────────────────────────────────────

## Home team dictionary built by TacticsScreen.
## Empty until set_match() is called.
var home_team  : Dictionary = {}

## Away team dictionary built by TacticsScreen.
var away_team  : Dictionary = {}

## Random seed for the next match.
## TacticsScreen can set this explicitly or leave it for GameState to randomise.
var match_seed : int = 0

# ── Engine load state ─────────────────────────────────────────────────────────

## True after Bridge.LoadData() completed without error.
## MatchView checks this before SimulateMatch to guard against use before init.
var data_loaded : bool = false

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	# Load engine data once at application startup.
	# All scenes after this can assume formations, roles, and game plans are ready.
	_load_engine_data()


func _load_engine_data() -> void:
	# Bridge is an AutoLoad (MatchEngineBridge.cs registered as "Bridge").
	# It must appear in the AutoLoad list BEFORE GameState, or use a deferred call.
	var data_path : String = ProjectSettings.globalize_path("res://Data")

	Bridge.LoadData(data_path)

	var err : String = Bridge.GetLastError()
	if err != "":
		push_error("[GameState] Bridge.LoadData failed: " + err)
		data_loaded = false
		return

	data_loaded = true
	print("[GameState] Engine data loaded from: " + data_path)

# ── Public API ────────────────────────────────────────────────────────────────

## Called by TacticsScreen before transitioning to MatchView.
## seed = 0 means GameState will generate a random seed automatically.
func set_match(home: Dictionary, away: Dictionary, seed: int = 0) -> void:
	home_team  = home
	away_team  = away
	match_seed = seed if seed != 0 else randi()

	print("[GameState] Match set: %s vs %s  seed=%d" % [
		home.get("name", "?"),
		away.get("name", "?"),
		match_seed
	])


## Called by PostMatch after the user is done viewing results.
## Clears match data so the next TacticsScreen starts fresh.
func clear_match() -> void:
	home_team  = {}
	away_team  = {}
	match_seed = 0
