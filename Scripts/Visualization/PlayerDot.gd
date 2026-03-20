# =============================================================================
# Module:  PlayerDot.gd
# Path:    FootballSim/Scripts/Visualization/PlayerDot.gd
# Purpose:
#   Visual representation of one player on the 2D pitch.
#   Draws a coloured circle (dot), shirt number label, stamina saturation ring,
#   and optional debug overlays. Reads only from ReplayPlayer-pushed data —
#   never reads from the bridge or engine directly.
#
# Node structure (all created in _ready, no .tscn needed):
#   PlayerDot (Node2D)           ← this script
#     └── dot (polygon / draw)   ← _draw() handles circle rendering
#     └── label (Label)          ← shirt number
#     └── debug_label (Label)    ← action name, only visible in DEBUG mode
#
# Public API (called by ReplayPlayer.gd each tick):
#   init(player_id, team_id, shirt_number, kit_primary, kit_secondary)
#   apply_snap(position, stamina, has_ball, action_int, is_active, is_sprinting)
#   set_debug(enabled)
#
# Colour encoding:
#   Dot fill      = team kit colour, saturation modulated by stamina
#                   Full stamina → vivid kit colour
#                   Zero stamina → 40% saturation (same as STAMINA_SPEED_MIN_MULTIPLIER)
#   Dot outline   = white when HasBall, invisible otherwise
#   Stamina ring  = thin arc around dot: arc length = stamina (full arc = 100%)
#                   Colour: green (>0.6) → yellow (0.3–0.6) → red (<0.3)
#
# Debug overlays (only drawn when DEBUG = true):
#   Action label  = string name of current PlayerAction enum value
#   Sprint shimmer= pulsing highlight tint when IsSprinting
#
# Smoothing:
#   Position is visually interpolated between ticks using lerp in _process().
#   The engine position is set as _target_pos; _draw_pos follows it smoothly.
#   This removes jitter at high playback speeds.
#
# Godot node configuration (attach this script to a Node2D):
#   The parent scene must call init() before the first apply_snap().
#   z_index: outfield players = 2, GK = 1, ball = 3 (handled by ReplayPlayer).
# =============================================================================

extends Node2D

# ── Constants ─────────────────────────────────────────────────────────────────

# PlayerAction enum values (must match C# Engine/Models/Enums.cs PlayerAction order)
const ACTION_NAMES := [
	"Dribbling", "Passing", "Shooting", "Crossing", "Holding",
	"SupportingRun", "MakingRun", "OverlapRun", "HoldingWidth",
	"DroppingDeep", "PositioningSupport",
	"Pressing", "Tracking", "Recovering", "Covering", "Blocking", "MarkingSpace",
	"Idle", "WalkingToAnchor", "TackleAttempt", "InterceptAttempt",
	"Heading", "Fouling", "Celebrating", "TakingSetPiece"
]

# Dot visual sizes (Godot pixels — pitch coords are already 1:1 with pixels)
const DOT_RADIUS        := 10.0   # outfield player dot radius
const GK_DOT_RADIUS     := 11.0   # GK slightly larger for visibility
const BALL_OWNER_RADIUS := 12.0   # dot grows when player has ball
const STAMINA_RING_RADIUS  := 14.0
const STAMINA_RING_WIDTH   := 2.5

# Stamina → colour thresholds
const STAMINA_HIGH  := 0.60
const STAMINA_LOW   := 0.30

# Minimum stamina saturation multiplier (mirrors PhysicsConstants.STAMINA_SPEED_MIN_MULTIPLIER)
const STAMINA_MIN_SAT := 0.40

# Position smoothing speed (higher = snappier, lower = smoother)
# At 1x playback speed, value of 12.0 gives a one-frame visual delay.
# At 4x speed, increase this or disable smoothing.
const SMOOTH_SPEED := 12.0

# ── Exports (editable in Inspector for prototyping) ───────────────────────────

@export var DEBUG := false

# ── State ─────────────────────────────────────────────────────────────────────

var _player_id    : int   = 0
var _team_id      : int   = 0
var _shirt_number : int   = 0
var _is_gk        : bool  = false

# Kit colours (packed RRGGBB → unpacked Color)
var _kit_color_primary   : Color = Color.BLUE
var _kit_color_secondary : Color = Color.WHITE

# Frame state (set by apply_snap, read by _draw and _process)
var _stamina     : float = 1.0
var _has_ball    : bool  = false
var _is_active   : bool  = true
var _is_sprinting: bool  = false
var _action_int  : int   = 0        # raw enum int from bridge

# Visual position state
var _target_pos  : Vector2 = Vector2.ZERO   # engine position (from frame)
var _draw_pos    : Vector2 = Vector2.ZERO   # smoothed display position

# Sprint pulse timer (for visual shimmer)
var _sprint_pulse_timer : float = 0.0

# Child nodes created in _ready
var _label       : Label = null
var _debug_label : Label = null

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	# Shirt number label — centred in dot
	_label = Label.new()
	_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_label.vertical_alignment   = VERTICAL_ALIGNMENT_CENTER
	_label.add_theme_font_size_override("font_size", 7)
	_label.modulate = _kit_color_secondary
	_label.position = Vector2(-8, -6)
	_label.size     = Vector2(16, 12)
	_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(_label)

	# Debug action label — displayed above dot
	_debug_label = Label.new()
	_debug_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_debug_label.add_theme_font_size_override("font_size", 6)
	_debug_label.modulate = Color(1.0, 1.0, 0.4, 0.9)
	_debug_label.position = Vector2(-30, -24)
	_debug_label.size     = Vector2(60, 12)
	_debug_label.visible  = DEBUG
	_debug_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(_debug_label)

	visible = _is_active

func _process(delta: float) -> void:
	if not _is_active:
		visible = false
		return

	visible = true

	# Smooth position toward engine target
	_draw_pos = _draw_pos.lerp(_target_pos, clamp(SMOOTH_SPEED * delta, 0.0, 1.0))
	position  = _draw_pos

	# Sprint pulse animation
	if _is_sprinting:
		_sprint_pulse_timer += delta * 6.0
	else:
		_sprint_pulse_timer = 0.0

	queue_redraw()

func _draw() -> void:
	if not _is_active:
		return

	# ── Dot fill colour: kit colour with stamina-modulated saturation ──────────
	var fill_color = _stamina_modulate(_kit_color_primary, _stamina)

	# ── Sprint shimmer: overlay a pulsing bright tint when sprinting ───────────
	if _is_sprinting:
		var pulse = (sin(_sprint_pulse_timer) * 0.5 + 0.5) * 0.25
		fill_color = fill_color.lightened(pulse)

	# ── Dot radius: larger when has ball ──────────────────────────────────────
	var base_radius := GK_DOT_RADIUS if _is_gk else DOT_RADIUS
	var radius := BALL_OWNER_RADIUS if _has_ball else base_radius

	# ── Shadow (subtle depth, drawn first) ───────────────────────────────────
	draw_circle(Vector2(1.5, 1.5), radius, Color(0, 0, 0, 0.35))

	# ── Main dot fill ─────────────────────────────────────────────────────────
	draw_circle(Vector2.ZERO, radius, fill_color)

	# ── Ball-owner ring: white outline ────────────────────────────────────────
	if _has_ball:
		draw_arc(Vector2.ZERO, radius + 2.0, 0.0, TAU, 20, Color.WHITE, 2.0)

	# ── Stamina arc ring ──────────────────────────────────────────────────────
	_draw_stamina_ring()

	# ── Pressing indicator: arrow toward ball carrier ─────────────────────────
	if DEBUG and _action_int == _action_index("Pressing"):
		_draw_action_indicator(Color(1.0, 0.4, 0.1, 0.8))

	# ── Recovering indicator ──────────────────────────────────────────────────
	if DEBUG and _action_int == _action_index("Recovering"):
		_draw_action_indicator(Color(0.4, 0.7, 1.0, 0.6))

func _draw_stamina_ring() -> void:
	# Full arc = TAU (360°). Arc length proportional to stamina.
	var arc_end   : float = lerp(0.0, TAU, _stamina)
	var ring_color := _stamina_ring_color(_stamina)

	# Background ring (dark, full circle)
	draw_arc(Vector2.ZERO, STAMINA_RING_RADIUS, 0.0, TAU, 32,
			 Color(0.1, 0.1, 0.1, 0.5), STAMINA_RING_WIDTH)

	# Foreground arc (stamina level)
	if _stamina > 0.01:
		draw_arc(Vector2.ZERO, STAMINA_RING_RADIUS, -PI * 0.5, -PI * 0.5 + arc_end,
				 max(6, int(32 * _stamina)), ring_color, STAMINA_RING_WIDTH)

func _draw_action_indicator(color: Color) -> void:
	# Small upward-pointing arrow above dot, indicating high-intensity action
	var tip    := Vector2(0, -DOT_RADIUS - 8)
	var left   := Vector2(-4, -DOT_RADIUS - 3)
	var right  := Vector2( 4, -DOT_RADIUS - 3)
	draw_line(left, tip, color, 1.5)
	draw_line(right, tip, color, 1.5)
	draw_line(left, right, color, 1.5)

# ── Public API ────────────────────────────────────────────────────────────────

## Called once by ReplayPlayer.gd after instantiation.
## Sets identity and colours. Must be called before first apply_snap().
func init(player_id: int, team_id: int, shirt_number: int,
		  kit_primary: int, kit_secondary: int, is_gk: bool) -> void:
	_player_id    = player_id
	_team_id      = team_id
	_shirt_number = shirt_number
	_is_gk        = is_gk

	# Unpack packed integer colours (0xRRGGBB)
	_kit_color_primary   = _unpack_color(kit_primary)
	_kit_color_secondary = _unpack_color(kit_secondary)

	if _label != null:
		_label.text    = str(shirt_number)
		_label.modulate = _kit_color_secondary

	# Set z-index: GK behind outfield players
	z_index = 1 if is_gk else 2

	name = "PlayerDot_%d" % player_id


## Called every tick by ReplayPlayer.gd with the current frame snapshot.
## All display state updates happen here.
func apply_snap(pos: Vector2, stamina: float, has_ball: bool,
				action_int: int, is_active: bool, is_sprinting: bool) -> void:
	_target_pos   = pos
	_stamina      = clamp(stamina, 0.0, 1.0)
	_has_ball     = has_ball
	_action_int   = action_int
	_is_active    = is_active
	_is_sprinting = is_sprinting

	# First frame: snap immediately (no interpolation on first placement)
	if _draw_pos == Vector2.ZERO and pos != Vector2.ZERO:
		_draw_pos = pos
		position  = pos

	# Debug label
	if DEBUG and _debug_label != null:
		_debug_label.visible = true
		_debug_label.text    = _action_name(action_int)
	elif _debug_label != null:
		_debug_label.visible = false


## Enables or disables debug overlay labels and action indicators.
func set_debug(enabled: bool) -> void:
	DEBUG = enabled
	if _debug_label != null:
		_debug_label.visible = enabled


## Returns the current smoothed draw position (used by BallDot to follow owner).
func get_draw_pos() -> Vector2:
	return _draw_pos

# ── Private helpers ───────────────────────────────────────────────────────────

func _stamina_modulate(base_color: Color, stamina: float) -> Color:
	# Lerp saturation from STAMINA_MIN_SAT (exhausted) to 1.0 (full)
	var sat_mult : float = lerp(STAMINA_MIN_SAT, 1.0, stamina)
	var h := base_color.h
	var s : float = clamp(base_color.s * sat_mult, 0.0, 1.0)
	var v := base_color.v
	return Color.from_hsv(h, s, v, 1.0)

func _stamina_ring_color(stamina: float) -> Color:
	if stamina >= STAMINA_HIGH:
		return Color(0.2, 0.9, 0.3, 0.9)   # green
	elif stamina >= STAMINA_LOW:
		# lerp green → yellow in mid range
		var t := (stamina - STAMINA_LOW) / (STAMINA_HIGH - STAMINA_LOW)
		return Color(lerp(1.0, 0.2, t), lerp(0.8, 0.9, t), 0.2, 0.9)
	else:
		return Color(0.95, 0.2, 0.2, 0.9)  # red

func _unpack_color(packed: int) -> Color:
	var r := ((packed >> 16) & 0xFF) / 255.0
	var g := ((packed >>  8) & 0xFF) / 255.0
	var b := ( packed        & 0xFF) / 255.0
	return Color(r, g, b, 1.0)

func _action_name(action_int: int) -> String:
	if action_int >= 0 and action_int < ACTION_NAMES.size():
		return ACTION_NAMES[action_int]
	return "?"

func _action_index(name: String) -> int:
	return ACTION_NAMES.find(name)
