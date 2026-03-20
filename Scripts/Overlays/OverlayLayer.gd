# =============================================================================
# Module:  OverlayLayer.gd
# Path:    FootballSim/Scripts/Overlays/OverlayLayer.gd
# Purpose:
#   Master coordinator for all tactical overlay visualizations.
#   Owns child overlay nodes and routes per-tick frame data to each of them.
#   Acts as the single contact point between ReplayPlayer and the overlay system.
#   ReplayPlayer calls OverlayLayer.update_frame() once per tick.
#   OverlayLayer routes to whichever child overlays are currently enabled.
#
# Child overlays managed:
#   DefensiveLineOverlay  — horizontal dashed lines showing back-four height
#   PressingLinesOverlay  — orange/purple lines from pressers to ball carrier
#   FormationGhostsOverlay — ghost dots at formation anchors (shown when paused)
#
# Public API (called by MatchView.gd and ReplayPlayer.gd):
#   initialise(first_frame, home_kit, away_kit, home_color, away_color)
#     Call once after Bridge.SimulateMatch() finishes.
#     Passes frame 0 to FormationGhostsOverlay for anchor data.
#
#   update_frame(frame: Dictionary)
#     Called every tick by ReplayPlayer. Routes data to active overlays.
#
#   on_paused()
#     Shows FormationGhosts when playback is paused.
#
#   on_resumed()
#     Hides FormationGhosts when playback resumes.
#
#   set_overlay(overlay_name: String, enabled: bool)
#     Toggle individual overlays at runtime.
#     overlay_name values: "defensive_line", "pressing", "ghosts"
#
# Scene tree — IMPORTANT — set up exactly like this in the Godot editor:
#
#   MatchView (Node2D)                          ← MatchView.gd
#     ├── PitchRenderer (Node2D)                ← PitchRenderer.gd, z_index=0
#     ├── OverlayLayer (Node2D)                 ← THIS SCRIPT, z_index=1
#     │     ├── FormationGhostsOverlay (Node2D) ← FormationGhostsOverlay.gd, z_index=1
#     │     ├── DefensiveLineOverlay (Node2D)   ← DefensiveLineOverlay.gd,   z_index=4
#     │     └── PressingLinesOverlay (Node2D)   ← PressingLinesOverlay.gd,   z_index=5
#     ├── PlayersLayer (Node2D)                 ← no script, z_index=2
#     ├── BallDot (Node2D)                      ← BallDot.gd,                z_index=3
#     └── ReplayPlayer (Node)                   ← ReplayPlayer.gd
#
# IMPORTANT — Inspector assignments in OverlayLayer:
#   @export var defensive_line_node  ← drag DefensiveLineOverlay node here
#   @export var pressing_node        ← drag PressingLinesOverlay node here
#   @export var ghosts_node          ← drag FormationGhostsOverlay node here
#
# How to connect OverlayLayer to ReplayPlayer (in MatchView.gd _ready):
#   $ReplayPlayer.connect("tick_advanced", _on_tick_advanced)
#   $ReplayPlayer.connect("playback_finished", $OverlayLayer.on_paused)
#   func _on_tick_advanced(tick: int, _minute: int) -> void:
#       var frame : Dictionary = Bridge.GetFrame(tick)
#       $OverlayLayer.update_frame(frame)
#
# Notes:
#   • OverlayLayer never calls Bridge directly — all data comes via update_frame().
#   • OverlayLayer does NOT draw anything itself — it only routes to children.
#   • All overlay visibility defaults: defensive_line ON, pressing ON, ghosts OFF.
# =============================================================================

extends Node2D

# ── Exports (assign in Inspector) ─────────────────────────────────────────────

@export var defensive_line_node : Node2D = null
@export var pressing_node       : Node2D = null
@export var ghosts_node         : Node2D = null

# ── Overlay enable flags ───────────────────────────────────────────────────────

var _show_defensive_line : bool = true
var _show_pressing       : bool = true
var _show_ghosts         : bool = false   # only shown when paused

# ── Team colours (set during initialise) ──────────────────────────────────────

var _home_color : Color = Color(0.3, 0.6, 1.0, 1.0)
var _away_color : Color = Color(1.0, 0.4, 0.4, 1.0)

# Which team's pressing lines to show: 0=home, 1=away, -1=both
var _pressing_team : int = -1

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	z_index = 1
	# Validate that child nodes were assigned in Inspector
	if defensive_line_node == null:
		push_warning("[OverlayLayer] defensive_line_node not assigned in Inspector.")
	if pressing_node == null:
		push_warning("[OverlayLayer] pressing_node not assigned in Inspector.")
	if ghosts_node == null:
		push_warning("[OverlayLayer] ghosts_node not assigned in Inspector.")

# ── Public API ────────────────────────────────────────────────────────────────

## Call once after replay is ready. Passes frame 0 for anchor positions.
## home_kit / away_kit are packed 0xRRGGBB integers.
func initialise(
	first_frame  : Dictionary,
	home_kit     : int,
	away_kit     : int
) -> void:
	_home_color = _unpack_color(home_kit)
	_away_color = _unpack_color(away_kit)

	if ghosts_node != null:
		ghosts_node.load_anchors(first_frame, _home_color, _away_color)


## Called every tick by MatchView's connection to ReplayPlayer.tick_advanced.
## Routes live frame data to whichever overlays are active.
func update_frame(frame: Dictionary) -> void:
	if _show_defensive_line and defensive_line_node != null:
		defensive_line_node.update_frame(frame, _home_color, _away_color)
		defensive_line_node.set_visible_home(_show_defensive_line)
		defensive_line_node.set_visible_away(_show_defensive_line)

	if _show_pressing and pressing_node != null:
		pressing_node.update_frame(frame, _pressing_team)


## Show ghost anchors and pause-state overlays.
func on_paused() -> void:
	if ghosts_node != null and _show_ghosts:
		ghosts_node.set_visible(true)


## Hide ghost anchors and resume live overlays.
func on_resumed() -> void:
	if ghosts_node != null:
		ghosts_node.set_visible(false)


## Toggle individual overlays by name at runtime.
## Called from MatchView UI toggle buttons (future HUD step).
## overlay_name: "defensive_line" | "pressing" | "ghosts"
func set_overlay(overlay_name: String, enabled: bool) -> void:
	match overlay_name:
		"defensive_line":
			_show_defensive_line = enabled
			if defensive_line_node != null:
				defensive_line_node.set_visible_home(enabled)
				defensive_line_node.set_visible_away(enabled)

		"pressing":
			_show_pressing = enabled
			if pressing_node != null:
				pressing_node.visible = enabled

		"ghosts":
			_show_ghosts = enabled
			if ghosts_node != null and not enabled:
				ghosts_node.set_visible(false)

		_:
			push_warning("[OverlayLayer] Unknown overlay name: " + overlay_name)


## Set which team's pressing lines to show. -1 = both.
func set_pressing_team(team_id: int) -> void:
	_pressing_team = team_id

# ── Private helpers ────────────────────────────────────────────────────────────

func _unpack_color(packed: int) -> Color:
	var r : float = float((packed >> 16) & 0xFF) / 255.0
	var g : float = float((packed >>  8) & 0xFF) / 255.0
	var b : float = float( packed        & 0xFF)  / 255.0
	return Color(r, g, b, 1.0)
