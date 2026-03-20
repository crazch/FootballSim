# =============================================================================
# Module:  BallDot.gd
# Path:    FootballSim/Scripts/Visualization/BallDot.gd
# Purpose:
#   Visual representation of the ball on the 2D pitch.
#   Renders differently for each BallPhase:
#     Owned     → white dot attached near owning player, small size
#     InFlight  → white dot with fading trajectory trail
#     Loose     → slightly grey dot, no trail, slow drift effect
#
#   Shot behaviour:
#     When IsShot=true: bright yellow flash on the dot for 3 frames,
#     larger size, faster trail fade.
#
#   Height encoding:
#     Ball height 0→1 modulates dot size: ground=6px, head-height=9px.
#     A cross at height 0.8 looks visibly elevated vs a ground pass.
#
# Node structure (all created in code, no .tscn):
#   BallDot (Node2D) ← this script
#     └── trail drawn in _draw() from _trail_points history
#
# Public API (called by ReplayPlayer.gd):
#   apply_snap(position, phase_int, height, owner_id, is_shot)
#   set_debug(enabled)
#
# BallPhase enum integers (must match C# BallPhase order):
#   0 = Owned
#   1 = InFlight
#   2 = Loose
#
# No bridge calls from here. All data is pushed by ReplayPlayer.
# =============================================================================

extends Node2D

# ── BallPhase constants (mirrors C# BallPhase enum order) ─────────────────────
const PHASE_OWNED    := 0
const PHASE_INFLIGHT := 1
const PHASE_LOOSE    := 2

# ── Visual constants ───────────────────────────────────────────────────────────
const BALL_RADIUS_GROUND    := 6.0    # radius at height = 0
const BALL_RADIUS_AERIAL    := 9.0    # radius at height = 1
const BALL_COLOR_OWNED      := Color(0.95, 0.95, 0.95, 1.0)   # near-white
const BALL_COLOR_INFLIGHT   := Color(1.0,  1.0,  1.0,  1.0)   # pure white
const BALL_COLOR_LOOSE      := Color(0.75, 0.75, 0.75, 1.0)   # light grey
const BALL_COLOR_SHOT_FLASH := Color(1.0,  0.95, 0.3,  1.0)   # yellow flash

# Trail settings
const TRAIL_MAX_POINTS := 18   # how many past positions to keep
const TRAIL_MIN_POINTS := 4    # draw trail only if at least this many points
const TRAIL_WIDTH_START := 4.5 # trail width at the newest end
const TRAIL_WIDTH_END   := 0.5 # trail width at oldest end

# Shot flash duration (in calls to apply_snap — roughly frames at 1x speed)
const SHOT_FLASH_TICKS := 5

# Smoothing
const SMOOTH_SPEED := 14.0     # slightly faster than players

# ── Export ────────────────────────────────────────────────────────────────────
@export var DEBUG := false

# ── State ─────────────────────────────────────────────────────────────────────
var _phase       : int    = PHASE_OWNED
var _height      : float  = 0.0
var _owner_id    : int    = -1
var _is_shot     : bool   = false

var _target_pos  : Vector2 = Vector2.ZERO
var _draw_pos    : Vector2 = Vector2.ZERO

# Trail: ring buffer of past draw positions, newest at front
var _trail_points : Array[Vector2] = []

# Shot flash counter: counts down from SHOT_FLASH_TICKS to 0
var _shot_flash_counter : int = 0

# Reference to PlayerDot nodes pushed by ReplayPlayer (for owner offset)
# Key: player_id (int), Value: PlayerDot node
var _player_dots : Dictionary = {}

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	z_index = 3   # above all players

func _process(delta: float) -> void:
	# If ball is owned, follow the owner's visual draw position (not engine position)
	# This keeps the ball glued to the dot even during smoothing.
	if _phase == PHASE_OWNED and _owner_id >= 0 and _owner_id in _player_dots:
		var dot = _player_dots[_owner_id]
		var owner_draw_pos : Vector2 = dot.get_draw_pos()
		# Offset ball slightly ahead-right of player dot for visual separation
		_target_pos = owner_draw_pos + Vector2(4.0, -4.0)

	# Smooth position toward target
	_draw_pos = _draw_pos.lerp(_target_pos, clamp(SMOOTH_SPEED * delta, 0.0, 1.0))
	position  = _draw_pos

	# Append to trail when in flight
	if _phase == PHASE_INFLIGHT:
		_trail_points.push_front(_draw_pos)
		if _trail_points.size() > TRAIL_MAX_POINTS:
			_trail_points.resize(TRAIL_MAX_POINTS)
	else:
		# Fade trail: remove one point per frame when not in flight
		if _trail_points.size() > 0:
			_trail_points.pop_back()

	queue_redraw()

func _draw() -> void:
	# ── Trail (drawn first so dot renders on top) ──────────────────────────────
	if _trail_points.size() >= TRAIL_MIN_POINTS:
		_draw_trail()

	# ── Ball dot ───────────────────────────────────────────────────────────────
	var radius: float = lerp(BALL_RADIUS_GROUND, BALL_RADIUS_AERIAL, _height)
	var color  := _ball_color()

	# Shot flash: larger dot
	if _shot_flash_counter > 0:
		radius *= 1.4
		color   = BALL_COLOR_SHOT_FLASH

	# Shadow
	draw_circle(Vector2(1.0, 1.0), radius, Color(0, 0, 0, 0.40))

	# Main ball dot
	draw_circle(Vector2.ZERO, radius, color)

	# Outline: slightly darker
	draw_arc(Vector2.ZERO, radius, 0.0, TAU, 20,
			 Color(color.r * 0.7, color.g * 0.7, color.b * 0.7, 0.8), 1.0)

	# Aerial highlight: small bright spot at top-left for balls in the air
	if _height > 0.2:
		var highlight_pos := Vector2(-radius * 0.3, -radius * 0.3)
		var highlight_r   := radius * 0.2 * _height
		draw_circle(highlight_pos, highlight_r, Color(1.0, 1.0, 1.0, 0.7 * _height))

	# DEBUG: phase label below ball
	if DEBUG:
		_draw_debug_label()

func _draw_trail() -> void:
	# Draw trail as series of line segments with decreasing width and alpha
	var count := _trail_points.size()
	for i in range(min(count - 1, TRAIL_MAX_POINTS - 1)):
		# i=0 is the newest point (closest to ball), i=count-1 is oldest
		var t_near := 1.0 - (float(i)     / float(TRAIL_MAX_POINTS))
		var t_far  := 1.0 - (float(i + 1) / float(TRAIL_MAX_POINTS))

		var alpha_near := pow(t_near, 1.5) * 0.7
		var alpha_far  := pow(t_far,  1.5) * 0.7
		var width_near : float = lerp(TRAIL_WIDTH_END, TRAIL_WIDTH_START, t_near)

		# Convert local trail positions to local draw space
		# Trail points are stored as global positions — offset to local
		var p_near : Vector2 = _trail_points[i]     - _draw_pos
		var p_far  : Vector2 = _trail_points[i + 1] - _draw_pos

		# Colour: shot trail is yellow, otherwise white
		var trail_color := Color(1.0, 0.9, 0.3, alpha_near) if _is_shot \
						else Color(1.0, 1.0, 1.0, alpha_near)

		draw_line(p_near, p_far, trail_color, width_near)

func _draw_debug_label() -> void:
	var phase_names := ["Owned", "InFlight", "Loose"]
	var label       : String = phase_names[_phase] if _phase < phase_names.size() else "?"
	if _is_shot:
		label += " [SHOT]"
	# Draw via draw_string requires a font reference — use a simple outline rect instead
	draw_rect(Rect2(-20, 10, 40, 12), Color(0, 0, 0, 0.5))

# ── Public API ────────────────────────────────────────────────────────────────

## Called by ReplayPlayer.gd every tick with bridge frame data.
func apply_snap(pos: Vector2, phase_int: int, height: float,
				owner_id: int, is_shot: bool) -> void:
	_phase    = phase_int
	_height   = clamp(height, 0.0, 1.0)
	_owner_id = owner_id

	# Detect shot start: fire flash counter
	if is_shot and not _is_shot:
		_shot_flash_counter = SHOT_FLASH_TICKS

	_is_shot = is_shot

	if _shot_flash_counter > 0:
		_shot_flash_counter -= 1

	# Only update target pos from engine when not owned
	# (when owned, _process() overrides this using player dot position)
	if _phase != PHASE_OWNED:
		_target_pos = pos

	# First placement snap
	if _draw_pos == Vector2.ZERO and pos != Vector2.ZERO:
		_draw_pos = pos
		position  = pos
		_trail_points.clear()

## Called by ReplayPlayer after dots are spawned, so ball can follow owner smoothly.
func register_player_dots(dots: Dictionary) -> void:
	_player_dots = dots

## Enables/disables debug label.
func set_debug(enabled: bool) -> void:
	DEBUG = enabled

# ── Private helpers ───────────────────────────────────────────────────────────

func _ball_color() -> Color:
	match _phase:
		PHASE_OWNED:    return BALL_COLOR_OWNED
		PHASE_INFLIGHT: return BALL_COLOR_INFLIGHT
		PHASE_LOOSE:    return BALL_COLOR_LOOSE
	return BALL_COLOR_OWNED
