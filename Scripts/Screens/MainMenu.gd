# =============================================================================
# Module:  MainMenu.gd
# Path:    FootballSim/Scripts/Screens/MainMenu.gd
# Purpose:
#   The root scene and the single owner of all scene-transition logic.
#   MainMenu.gd owns every EventBus navigation connection so no other scene
#   needs to know another scene's path. The full navigation flow is:
#
#   MainMenu ──start──▶ TacticsScreen ──kickoff──▶ MatchView ──finish──▶ PostMatch
#      ▲                                                                       │
#      └──────────────────── main menu ────────────────────────────────────────┘
#      ▲                                                                       │
#      └──────────────────── play again → TacticsScreen ──────────────────────┘
#
#   MainMenu connects to ALL EventBus navigation signals once in _ready().
#   When a signal fires it calls change_scene_to_file() with the appropriate path.
#   No other scene performs scene transitions directly.
#
# Scene stays resident:
#   MainMenu is the persistent root. Other screens are loaded on top via
#   change_scene_to_file(), which replaces the current scene in the SceneTree.
#   This means MainMenu itself is unloaded when TacticsScreen loads — but its
#   AutoLoad connections via EventBus persist because EventBus is an AutoLoad,
#   not a node inside MainMenu's tree.
#
#   The correct architecture here is therefore:
#     • MainMenu connects EventBus signals in _ready() using static functions
#       so the callbacks are not tied to this node's lifetime.
#     • All callbacks simply call get_tree().change_scene_to_file().
#     • This works because EventBus (AutoLoad) outlives any scene.
#
# @export slots (Inspector):
#   start_button         — "Start" / "Kick Off" button
#   quit_button          — "Quit" button
#   version_label        — optional version string label
#   data_error_label     — shown if GameState.data_loaded is false
#
# Strict typing: no := on arrays / get / bridge / math results.
# =============================================================================

extends Control

# ── Version string displayed in corner ───────────────────────────────────────
const VERSION_STRING : String = "v0.1.0-dev"

# ── @exports ──────────────────────────────────────────────────────────────────

@export var start_button      : Button = null
@export var quit_button       : Button = null
@export var version_label     : Label  = null
@export var data_error_label  : Label  = null

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	# Navigation connections are owned by SceneManager (AutoLoad) — not here.
	# MainMenu only connects its own button signals.

	# ── 1. Connect button signals ─────────────────────────────────────────────
	if start_button != null:
		start_button.pressed.connect(_on_start_pressed)
	if quit_button != null:
		quit_button.pressed.connect(_on_quit_pressed)

	# ── 2. Version label ──────────────────────────────────────────────────────
	if version_label != null:
		version_label.text = VERSION_STRING

	# ── 3. Data load check ────────────────────────────────────────────────────
	# GameState._ready() runs before any scene node — data is already loaded.
	if data_error_label != null:
		if not GameState.data_loaded:
			data_error_label.text    = "⚠  Engine data failed to load. Check Data/ folder."
			data_error_label.visible = true
			if start_button != null:
				start_button.disabled = true
		else:
			data_error_label.visible = false

# ── Button handlers ───────────────────────────────────────────────────────────

func _on_start_pressed() -> void:
	# Emit go_to_tactics so the EventBus handler below fires.
	# This keeps the pattern consistent — the button never calls
	# change_scene_to_file() directly.
	EventBus.go_to_tactics.emit()


func _on_quit_pressed() -> void:
	get_tree().quit()
