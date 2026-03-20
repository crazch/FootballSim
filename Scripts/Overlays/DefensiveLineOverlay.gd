# =============================================================================
# Module:  DefensiveLineOverlay.gd
# Path:    FootballSim/Scripts/Overlays/DefensiveLineOverlay.gd
# Purpose:
#   Draws two horizontal dashed lines — one per team — showing the current
#   average Y position of each team's defensive line (back four).
#
#   Home defensive line: average Y of home defenders (players 0–10, roles CB/RB/LB/WB)
#   Away defensive line: average Y of away defenders (players 11–21)
#
#   The line colour fades when the team is in possession (not actively defending).
#   A small label at the right end of each line shows the team name abbreviation.
#
# How it works:
#   ReplayPlayer calls update_frame(frame_dict) each tick.
#   update_frame reads player positions and computes average defender Y.
#   queue_redraw() is called and _draw() renders the lines.
#
# Public API:
#   update_frame(frame: Dictionary, home_color: Color, away_color: Color)
#   set_visible_home(v: bool)
#   set_visible_away(v: bool)
#
# Scene tree:
#   OverlayLayer (Node2D)
#     └── DefensiveLineOverlay (Node2D)  ← this script
#
# z_index = 4 (above players, below debug labels)
# =============================================================================

extends Node2D

# ── Constants ─────────────────────────────────────────────────────────────────

const PITCH_W        : float = 1050.0
const PITCH_H        : float = 680.0
const LINE_WIDTH     : float = 1.5
const LINE_ALPHA_DEF : float = 0.80   # alpha when actively defending
const LINE_ALPHA_ATK : float = 0.30   # alpha when in possession (not defending)
const DASH_LENGTH    : float = 18.0
const DASH_GAP       : float = 10.0

# Defender role indices (match PlayerAction enum — these are team-relative slot checks)
# Home players 0–10: slot 0 = GK, slots 1–4 = defenders
# Away players 11–21: slot 11 = GK, slots 12–15 = defenders
const HOME_DEFENDER_INDICES : Array[int] = [1, 2, 3, 4]
const AWAY_DEFENDER_INDICES : Array[int] = [12, 13, 14, 15]

# ── State ─────────────────────────────────────────────────────────────────────

var _home_line_y   : float = 170.0   # default: penalty area depth
var _away_line_y   : float = 510.0
var _home_color    : Color = Color(0.3, 0.6, 1.0, LINE_ALPHA_DEF)
var _away_color    : Color = Color(1.0, 0.4, 0.4, LINE_ALPHA_DEF)
var _show_home     : bool  = true
var _show_away     : bool  = true
var _home_in_poss  : bool  = false   # home team has the ball this tick
var _away_in_poss  : bool  = false

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	z_index = 4

# ── Public API ────────────────────────────────────────────────────────────────

func update_frame(frame: Dictionary, home_color: Color, away_color: Color) -> void:
	_home_color = home_color
	_away_color = away_color

	var players : Array = frame.get("players", [])
	if players.size() < 22:
		return

	# Ball owner determines which team is in possession
	var ball_owner : int = frame.get("ball_owner", -1)
	_home_in_poss = ball_owner >= 0 and ball_owner <= 10
	_away_in_poss = ball_owner >= 11 and ball_owner <= 21

	# Average defender Y for home team
	var home_total : float = 0.0
	var home_count : int   = 0
	for idx : int in HOME_DEFENDER_INDICES:
		if idx >= players.size():
			continue
		var p : Dictionary = players[idx]
		if not p.get("is_active", false):
			continue
		var pos : Vector2 = p.get("position", Vector2.ZERO)
		home_total += pos.y
		home_count += 1
	if home_count > 0:
		_home_line_y = home_total / float(home_count)

	# Average defender Y for away team
	var away_total : float = 0.0
	var away_count : int   = 0
	for idx : int in AWAY_DEFENDER_INDICES:
		if idx >= players.size():
			continue
		var p : Dictionary = players[idx]
		if not p.get("is_active", false):
			continue
		var pos : Vector2 = p.get("position", Vector2.ZERO)
		away_total += pos.y
		away_count += 1
	if away_count > 0:
		_away_line_y = away_total / float(away_count)

	queue_redraw()


func set_visible_home(v: bool) -> void:
	_show_home = v
	queue_redraw()


func set_visible_away(v: bool) -> void:
	_show_away = v
	queue_redraw()

# ── Drawing ────────────────────────────────────────────────────────────────────

func _draw() -> void:
	if _show_home:
		var alpha : float = LINE_ALPHA_ATK if _home_in_poss else LINE_ALPHA_DEF
		var c : Color = Color(_home_color.r, _home_color.g, _home_color.b, alpha)
		_draw_dashed_line(0.0, PITCH_W, _home_line_y, c)

	if _show_away:
		var alpha : float = LINE_ALPHA_ATK if _away_in_poss else LINE_ALPHA_DEF
		var c : Color = Color(_away_color.r, _away_color.g, _away_color.b, alpha)
		_draw_dashed_line(0.0, PITCH_W, _away_line_y, c)


func _draw_dashed_line(x_start: float, x_end: float, y: float, color: Color) -> void:
	var x : float = x_start
	var drawing : bool = true   # alternate draw/gap
	while x < x_end:
		var next_x : float = minf(x + (DASH_LENGTH if drawing else DASH_GAP), x_end)
		if drawing:
			draw_line(
				Vector2(x, y),
				Vector2(next_x, y),
				color,
				LINE_WIDTH
			)
		x = next_x
		drawing = not drawing
