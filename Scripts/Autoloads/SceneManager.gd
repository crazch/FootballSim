# =============================================================================
# Module:  SceneManager.gd
# Path:    FootballSim/Scripts/Autoloads/SceneManager.gd
# Purpose:
#   AutoLoad singleton that owns every EventBus navigation connection.
#   Because it is an AutoLoad it is NEVER freed during scene transitions,
#   so its signal connections remain active for the entire application lifetime.
#
#   This is the fix for the core navigation bug:
#     MainMenu connected EventBus signals in its _ready().
#     When change_scene_to_file(TacticsScreen) ran, MainMenu was freed.
#     Godot automatically disconnected all signals owned by freed objects.
#     When TacticsScreen emitted EventBus.go_to_match, nothing was listening.
#
#   SceneManager is an AutoLoad → it connects once → it is never freed →
#   signals always have a listener regardless of which scene is loaded.
#
# AutoLoad registration (Project Settings → AutoLoad):
#   Name: SceneManager
#   Path: res://Scripts/Autoloads/SceneManager.gd
#   Order: add AFTER EventBus (SceneManager connects to EventBus in _ready)
#
#   Final AutoLoad order:
#     1. Bridge        (MatchEngineBridge.cs)
#     2. GameState     (GameState.gd)
#     3. EventBus      (EventBus.gd)
#     4. SceneManager  (SceneManager.gd)  ← new
#
# MainMenu.gd change required:
#   Remove the four EventBus.connect() calls from MainMenu._ready().
#   MainMenu now only handles its own button signals — it does not own navigation.
# =============================================================================

extends Node

# ── Scene paths — the only place in the project these strings live ────────────

const SCENE_MENU      : String = "res://Scenes/MainMenu.tscn"
const SCENE_TACTICS   : String = "res://Scenes/TacticsScreen.tscn"
const SCENE_MATCH     : String = "res://Scenes/MatchView.tscn"
const SCENE_POSTMATCH : String = "res://Scenes/PostMatch.tscn"

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	# Connect all navigation signals once.
	# These connections persist for the entire application lifetime.
	EventBus.go_to_tactics.connect(_go_to_tactics)
	EventBus.go_to_match.connect(_go_to_match)
	EventBus.go_to_postmatch.connect(_go_to_postmatch)
	EventBus.go_to_main_menu.connect(_go_to_main_menu)

	print("[SceneManager] Navigation connections registered.")

# ── Navigation handlers ───────────────────────────────────────────────────────

func _go_to_tactics() -> void:
	print("[SceneManager] → TacticsScreen")
	get_tree().change_scene_to_file(SCENE_TACTICS)


func _go_to_match() -> void:
	print("[SceneManager] → MatchView")
	get_tree().change_scene_to_file(SCENE_MATCH)


func _go_to_postmatch() -> void:
	print("[SceneManager] → PostMatch")
	get_tree().change_scene_to_file(SCENE_POSTMATCH)


func _go_to_main_menu() -> void:
	print("[SceneManager] → MainMenu")
	get_tree().change_scene_to_file(SCENE_MENU)
