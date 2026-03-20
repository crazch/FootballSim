# =============================================================================
# Module:  PressingLinesOverlay.gd
# Path:    FootballSim/Scripts/Overlays/PressingLinesOverlay.gd
# Purpose:
#   Draws directional lines from each pressing player toward the ball carrier.
#   Only active when at least one player has action = Pressing (index 11).
#
#   Visual:
#     A faint orange line from presser position → ball carrier position.
#     Line alpha = presser stamina (exhausted pressers show faint/invisible line).
#     This makes the stamina-driven press collapse visually legible —
#     as minute 58 approaches, the orange press lines fade and disappear.
#
#   Additionally draws a small "press arc" — a partial arc around the ball
#   carrier showing the radius of press coverage from all active pressers.
#
# Public API:
#   update_frame(frame: Dictionary, pressing_team: int)
#     pressing_team: 0 = show home pressing lines, 1 = away, -1 = both
#
# Scene tree:
#   OverlayLayer (Node2D)
#     └── PressingLinesOverlay (Node2D)  ← this script
#
# z_index = 5
# =============================================================================

extends Node2D

# ── PlayerAction Pressing index (must match Engine/Models/Enums.cs order) ─────
const ACTION_PRESSING : int = 11

# ── Visual constants ───────────────────────────────────────────────────────────

const LINE_COLOR_HOME : Color = Color(1.0, 0.60, 0.15, 1.0)  # orange
const LINE_COLOR_AWAY : Color = Color(0.80, 0.20, 0.80, 1.0)  # purple
const LINE_WIDTH      : float = 1.5
const ARROWHEAD_SIZE  : float = 7.0
const PRESS_ARC_R     : float = 30.0  # arc drawn around ball carrier

# ── State ──────────────────────────────────────────────────────────────────────

# Presser entries: Array of Dictionaries with keys "from", "to", "alpha", "team"
var _press_lines : Array[Dictionary] = []
var _ball_pos    : Vector2           = Vector2.ZERO
var _arc_alpha   : float             = 0.0   # average alpha of all pressers this tick

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	z_index = 5

# ── Public API ────────────────────────────────────────────────────────────────

func update_frame(frame: Dictionary, pressing_team: int) -> void:
	_press_lines.clear()
	_arc_alpha = 0.0

	var players   : Array  = frame.get("players",   [])
	var ball_owner : int   = frame.get("ball_owner", -1)
	_ball_pos               = frame.get("ball_pos",  Vector2.ZERO)

	if ball_owner < 0 or ball_owner >= players.size():
		queue_redraw()
		return

	var carrier_pos : Vector2 = Vector2.ZERO
	var carrier_p   : Dictionary = players[ball_owner]
	carrier_pos = carrier_p.get("position", Vector2.ZERO)

	var alpha_sum   : float = 0.0
	var press_count : int   = 0

	for i : int in range(players.size()):
		var p : Dictionary = players[i]
		if not p.get("is_active", true):
			continue

		var action : int = p.get("action", 0)
		if action != ACTION_PRESSING:
			continue

		var team_id : int = 0 if i <= 10 else 1
		if pressing_team != -1 and team_id != pressing_team:
			continue

		var stamina   : float   = p.get("stamina", 1.0)
		var from_pos  : Vector2 = p.get("position", Vector2.ZERO)
		var line_alpha : float  = clampf(stamina * 0.9, 0.05, 0.90)

		var entry : Dictionary = {
			"from":  from_pos,
			"to":    carrier_pos,
			"alpha": line_alpha,
			"team":  team_id,
		}
		_press_lines.append(entry)
		alpha_sum   += line_alpha
		press_count += 1

	if press_count > 0:
		_arc_alpha = alpha_sum / float(press_count)

	queue_redraw()

# ── Drawing ────────────────────────────────────────────────────────────────────

func _draw() -> void:
	for entry : Dictionary in _press_lines:
		var from_pos  : Vector2 = entry.get("from",  Vector2.ZERO)
		var to_pos    : Vector2 = entry.get("to",    Vector2.ZERO)
		var alpha     : float   = entry.get("alpha", 0.5)
		var team_id   : int     = entry.get("team",  0)

		var base_color : Color = LINE_COLOR_HOME if team_id == 0 else LINE_COLOR_AWAY
		var line_color : Color = Color(base_color.r, base_color.g, base_color.b, alpha)

		draw_line(from_pos, to_pos, line_color, LINE_WIDTH)
		_draw_arrowhead(from_pos, to_pos, line_color)

	# Press arc around ball carrier
	if _press_lines.size() > 0 and _arc_alpha > 0.05:
		var arc_color : Color = Color(1.0, 0.65, 0.0, _arc_alpha * 0.4)
		draw_arc(_ball_pos, PRESS_ARC_R, 0.0, TAU, 24, arc_color, 2.0)


func _draw_arrowhead(from_pos: Vector2, to_pos: Vector2, color: Color) -> void:
	var dir : Vector2 = (to_pos - from_pos)
	if dir.length_squared() < 1.0:
		return
	dir = dir.normalized()

	# Place arrowhead partway along the line (not at carrier, to avoid clutter)
	var mid : Vector2 = from_pos.lerp(to_pos, 0.65)

	var perp   : Vector2 = Vector2(-dir.y, dir.x)
	var tip    : Vector2 = mid + dir * ARROWHEAD_SIZE
	var left   : Vector2 = mid - perp * (ARROWHEAD_SIZE * 0.4)
	var right  : Vector2 = mid + perp * (ARROWHEAD_SIZE * 0.4)

	draw_line(left,  tip, color, LINE_WIDTH)
	draw_line(right, tip, color, LINE_WIDTH)
