# =============================================================================
# Module:  PitchRenderer.gd
# Path:    FootballSim/Scripts/Visualization/PitchRenderer.gd
# Purpose:
#   Draws the complete football pitch as a static Node2D using _draw().
#   Renders once on scene load — never redraws per tick.
#   All coordinates are in engine pitch units (1050 × 680 pixels = 105m × 68m).
#   Origin is top-left (0, 0). Home attacks toward bottom (Y = 680).
#
# What is drawn:
#   1. Grass base (dark green rectangle)
#   2. Alternating grass stripe bands (lighter green, 7 vertical stripes)
#   3. Outer touchline rectangle
#   4. Halfway line (horizontal)
#   5. Centre circle (radius 91.5 units = 9.15m)
#   6. Centre spot (small dot)
#   7. Home penalty area (top)
#   8. Away penalty area (bottom)
#   9. Home goal box (6-yard box, top)
#  10. Away goal box (6-yard box, bottom)
#  11. Home goal (behind goal line, top)
#  12. Away goal (behind goal line, bottom)
#  13. Penalty spots (home and away)
#  14. Corner arcs (4 corners)
#  15. Halfway line spot (centre spot)
#
# Scene tree: attach this script to a Node2D named "PitchRenderer".
# z_index = 0 (draws behind all players and ball).
# No exports needed — all constants are defined below.
#
# Pitch geometry (must match Engine/Systems/PhysicsConstants.cs exactly):
#   Pitch:    1050 × 680 units
#   Penalty area depth:     165 units (16.5m)
#   Penalty area half-width: 201.6 units (20.16m each side of centre)
#   Goal width:              73.2 units (7.32m), centred at X=525
#   Goal depth:              22 units (2.2m, drawn outside pitch boundary)
#   Goal box depth:          55 units (5.5m)
#   Goal box half-width:     100 units (10.0m each side)
#   Centre circle radius:    91.5 units (9.15m)
#   Corner arc radius:       10 units (1m)
#   Penalty spot distance:   110 units from goal line (11m)
# =============================================================================

extends Node2D

# ── Pitch geometry constants (match PhysicsConstants.cs exactly) ──────────────

const PITCH_W         : float = 1050.0
const PITCH_H         : float = 680.0
const CENTRE_X        : float = 525.0
const CENTRE_Y        : float = 340.0

const LINE_W          : float = 2.0    # touchline and marking line width

const PENALTY_DEPTH   : float = 165.0  # penalty area depth from goal line
const PENALTY_HALF_W  : float = 201.6  # penalty area half-width from centre X
const GOAL_HALF_W     : float = 36.6   # goal mouth half-width (7.32m / 2)
const GOAL_DEPTH      : float = 22.0   # goal net depth drawn outside pitch
const GOAL_BOX_DEPTH  : float = 55.0   # 6-yard box depth from goal line
const GOAL_BOX_HALF_W : float = 100.0  # 6-yard box half-width from centre X

const CENTRE_CIRCLE_R : float = 91.5   # 9.15m centre circle
const CORNER_ARC_R    : float = 10.0   # 1m corner arc
const PENALTY_SPOT_R  : float = 2.5    # penalty spot dot radius
const CENTRE_SPOT_R   : float = 2.5    # centre spot dot radius

# Penalty spot is 11m from goal line = 110 units
const PENALTY_SPOT_DIST : float = 110.0

# ── Colour palette ─────────────────────────────────────────────────────────────

const COLOR_GRASS_DARK    : Color = Color(0.173, 0.380, 0.173, 1.0)  # FM-style dark green
const COLOR_GRASS_LIGHT   : Color = Color(0.196, 0.420, 0.196, 1.0)  # alternating stripe
const COLOR_LINE          : Color = Color(0.95,  0.95,  0.95,  1.0)  # near-white lines
const COLOR_GOAL_POST     : Color = Color(1.0,   1.0,   1.0,  1.0)   # white goal frame
const COLOR_GOAL_NET      : Color = Color(0.85,  0.85,  0.85,  0.35) # translucent net fill
const COLOR_PENALTY_SPOT  : Color = Color(0.95,  0.95,  0.95,  1.0)

# Number of grass stripe bands
const STRIPE_COUNT : int = 8

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	z_index = 0        # always behind players and ball


# _draw() is called once when the node enters the scene tree.
# Call queue_redraw() if you ever need to force a repaint (not needed here).
func _draw() -> void:
	_draw_grass_base()
	_draw_grass_stripes()
	_draw_touchlines()
	_draw_halfway_line()
	_draw_centre_circle()
	_draw_centre_spot()
	_draw_penalty_area(true)    # home (top of pitch)
	_draw_penalty_area(false)   # away (bottom of pitch)
	_draw_goal_box(true)
	_draw_goal_box(false)
	_draw_goal(true)
	_draw_goal(false)
	_draw_penalty_spot(true)
	_draw_penalty_spot(false)
	_draw_corner_arcs()

# ── Drawing methods ────────────────────────────────────────────────────────────

func _draw_grass_base() -> void:
	draw_rect(Rect2(0.0, 0.0, PITCH_W, PITCH_H), COLOR_GRASS_DARK)


func _draw_grass_stripes() -> void:
	# Vertical alternating lighter stripes across full pitch height
	var stripe_w : float = PITCH_W / float(STRIPE_COUNT)
	for i : int in range(STRIPE_COUNT):
		if i % 2 == 0:
			continue   # even stripes stay as dark base
		var x : float = float(i) * stripe_w
		draw_rect(Rect2(x, 0.0, stripe_w, PITCH_H), COLOR_GRASS_LIGHT)


func _draw_touchlines() -> void:
	# Outer rectangle: left, right, top, bottom touchlines
	draw_rect(Rect2(0.0, 0.0, PITCH_W, PITCH_H), COLOR_LINE, false, LINE_W)


func _draw_halfway_line() -> void:
	var p1 : Vector2 = Vector2(0.0,    CENTRE_Y)
	var p2 : Vector2 = Vector2(PITCH_W, CENTRE_Y)
	draw_line(p1, p2, COLOR_LINE, LINE_W)


func _draw_centre_circle() -> void:
	# Filled arc segments to approximate circle (Godot draw_circle fills solid)
	# Draw empty circle (outline only) using draw_arc
	draw_arc(
		Vector2(CENTRE_X, CENTRE_Y),
		CENTRE_CIRCLE_R,
		0.0,
		TAU,
		64,
		COLOR_LINE,
		LINE_W
	)


func _draw_centre_spot() -> void:
	draw_circle(Vector2(CENTRE_X, CENTRE_Y), CENTRE_SPOT_R, COLOR_LINE)


func _draw_penalty_area(is_home: bool) -> void:
	# Home penalty area: top of pitch (Y from 0 to PENALTY_DEPTH)
	# Away penalty area: bottom of pitch (Y from PITCH_H - PENALTY_DEPTH to PITCH_H)
	var left   : float = CENTRE_X - PENALTY_HALF_W
	var right  : float = CENTRE_X + PENALTY_HALF_W
	var top    : float
	var bottom : float

	if is_home:
		top    = 0.0
		bottom = PENALTY_DEPTH
	else:
		top    = PITCH_H - PENALTY_DEPTH
		bottom = PITCH_H

	var rect : Rect2 = Rect2(left, top, right - left, bottom - top)
	draw_rect(rect, COLOR_LINE, false, LINE_W)


func _draw_goal_box(is_home: bool) -> void:
	# 6-yard box: smaller rectangle inside penalty area near goal line
	var left  : float = CENTRE_X - GOAL_BOX_HALF_W
	var right : float = CENTRE_X + GOAL_BOX_HALF_W
	var top   : float
	var bottom: float

	if is_home:
		top    = 0.0
		bottom = GOAL_BOX_DEPTH
	else:
		top    = PITCH_H - GOAL_BOX_DEPTH
		bottom = PITCH_H

	var rect : Rect2 = Rect2(left, top, right - left, bottom - top)
	draw_rect(rect, COLOR_LINE, false, LINE_W)


func _draw_goal(is_home: bool) -> void:
	var left  : float = CENTRE_X - GOAL_HALF_W
	var right : float = CENTRE_X + GOAL_HALF_W

	var post_top    : float
	var post_bottom : float
	var net_top     : float
	var net_bottom  : float

	if is_home:
		# Goal drawn above the pitch (negative Y = outside boundary)
		post_top    = -GOAL_DEPTH
		post_bottom = 0.0
		net_top     = post_top
		net_bottom  = post_bottom
	else:
		# Goal drawn below the pitch
		post_top    = PITCH_H
		post_bottom = PITCH_H + GOAL_DEPTH
		net_top     = post_top
		net_bottom  = post_bottom

	# Net fill (translucent rectangle)
	var net_rect : Rect2 = Rect2(left, net_top, right - left, net_bottom - net_top)
	draw_rect(net_rect, COLOR_GOAL_NET)

	# Left post (vertical line)
	draw_line(
		Vector2(left, post_top),
		Vector2(left, post_bottom),
		COLOR_GOAL_POST,
		LINE_W + 1.0
	)
	# Right post
	draw_line(
		Vector2(right, post_top),
		Vector2(right, post_bottom),
		COLOR_GOAL_POST,
		LINE_W + 1.0
	)
	# Crossbar (back line — depth end)
	if is_home:
		draw_line(
			Vector2(left,  post_top),
			Vector2(right, post_top),
			COLOR_GOAL_POST,
			LINE_W + 1.0
		)
	else:
		draw_line(
			Vector2(left,  post_bottom),
			Vector2(right, post_bottom),
			COLOR_GOAL_POST,
			LINE_W + 1.0
		)


func _draw_penalty_spot(is_home: bool) -> void:
	var y : float
	if is_home:
		y = PENALTY_SPOT_DIST
	else:
		y = PITCH_H - PENALTY_SPOT_DIST

	draw_circle(Vector2(CENTRE_X, y), PENALTY_SPOT_R, COLOR_PENALTY_SPOT)


func _draw_corner_arcs() -> void:
	# Four quarter-circle arcs at each corner of the pitch
	# Top-left corner: arc from 0 to PI/2 (sweeping right and down)
	draw_arc(Vector2(0.0,    0.0),    CORNER_ARC_R, 0.0,       PI * 0.5, 8, COLOR_LINE, LINE_W)
	# Top-right corner
	draw_arc(Vector2(PITCH_W, 0.0),   CORNER_ARC_R, PI * 0.5,  PI,       8, COLOR_LINE, LINE_W)
	# Bottom-right corner
	draw_arc(Vector2(PITCH_W, PITCH_H), CORNER_ARC_R, PI,     PI * 1.5, 8, COLOR_LINE, LINE_W)
	# Bottom-left corner
	draw_arc(Vector2(0.0, PITCH_H),   CORNER_ARC_R, PI * 1.5,  TAU,      8, COLOR_LINE, LINE_W)
