# =============================================================================
# Module:  SpeedControls.gd
# Path:    FootballSim/Scripts/UI/SpeedControls.gd
# Purpose:
#   Playback control bar at the bottom of the screen.
#   Contains: Pause/Play button, speed step buttons (½× 1× 2× 4× 8× 16×),
#   an Instant button (seek to last tick), and a progress scrub bar.
#
#   Data flow:
#     User presses button → SpeedControls calls ReplayPlayer method
#     ReplayPlayer.tick_advanced → SpeedControls.on_tick_advanced updates scrub bar
#     ReplayPlayer.playback_finished → SpeedControls shows finished state
#
#   SpeedControls does NOT call Bridge. All state comes from ReplayPlayer signals.
#
# Scene tree:
#
#   SpeedControls (CanvasLayer)          ← this script, layer = 12
#     └── BottomBar (PanelContainer)     ← anchored bottom, full width
#           └── VBox (VBoxContainer)
#                 ├── ScrubBar (HSlider)          ← tick position slider
#                 └── ButtonRow (HBoxContainer)
#                       ├── PauseButton (Button)   ← "⏸" / "▶"
#                       ├── HalfButton (Button)    ← "½×"
#                       ├── OneButton (Button)     ← "1×"
#                       ├── TwoButton (Button)     ← "2×"
#                       ├── FourButton (Button)    ← "4×"
#                       ├── EightButton (Button)   ← "8×"
#                       ├── SixteenButton (Button) ← "16×"
#                       ├── VSpacer (Control)
#                       ├── PrevHighlight (Button) ← "◀ Event"
#                       ├── NextHighlight (Button) ← "Event ▶"
#                       └── InstantButton (Button) ← "End ⏭"
#
# Inspector assignments:
#   @export var replay_player       ← drag ReplayPlayer node here
#   @export var scrub_bar           ← drag ScrubBar HSlider here
#   @export var pause_button        ← drag PauseButton here
#   @export var speed_buttons       ← Array[Button]: drag all speed buttons here
#   @export var hud                 ← drag HUD node here (to update speed label)
#
# How to wire signals in MatchView.gd _ready():
#   $ReplayPlayer.tick_advanced.connect($SpeedControls.on_tick_advanced)
#   $ReplayPlayer.playback_finished.connect($SpeedControls.on_playback_finished)
#   $ReplayPlayer.phase_changed.connect($SpeedControls.on_phase_changed)
#   (Button signals are connected in _ready() of this script via @export refs)
#
# Strict typing: no := on arrays / math / get / bridge calls.
# =============================================================================

extends CanvasLayer

# ── Speed steps available (matches ReplayPlayer.SPEED_OPTIONS) ───────────────
const SPEED_STEPS    : Array[float] = [0.5, 1.0, 2.0, 4.0, 8.0, 16.0]
const SPEED_LABELS   : Array[String] = ["½×", "1×", "2×", "4×", "8×", "16×"]

# ── Button text ───────────────────────────────────────────────────────────────
const TEXT_PAUSE  : String = "⏸"
const TEXT_PLAY   : String = "▶"
const TEXT_END    : String = "⏭ End"
const TEXT_PREV   : String = "◀ Event"
const TEXT_NEXT   : String = "Event ▶"

# ── Colours ───────────────────────────────────────────────────────────────────
const COLOR_BUTTON_ACTIVE   : Color = Color(0.90, 0.90, 0.90, 1.0)
const COLOR_BUTTON_INACTIVE : Color = Color(0.50, 0.50, 0.50, 1.0)
const COLOR_BUTTON_SELECTED : Color = Color(0.30, 0.80, 0.40, 1.0)  # green = active speed

# ── Exports (assign in Inspector) ─────────────────────────────────────────────

## The ReplayPlayer node. Assign in Inspector.
@export var replay_player   : Node       = null

## The HSlider that shows/controls playback position.
@export var scrub_bar       : HSlider    = null

## The Pause/Play toggle button.
@export var pause_button    : Button     = null

## The 6 speed step buttons in order: ½× 1× 2× 4× 8× 16×
## Drag them into this array in the Inspector, in order.
@export var speed_buttons   : Array[Button] = []

## Previous highlight button.
@export var prev_highlight  : Button    = null

## Next highlight button.
@export var next_highlight  : Button    = null

## Instant/end button.
@export var instant_button  : Button    = null

## HUD node (to update speed label display).
@export var hud             : Node      = null

# ── State ─────────────────────────────────────────────────────────────────────

var _paused             : bool  = true
var _current_speed      : float = 1.0
var _total_ticks        : int   = 0
var _current_tick       : int   = 0
var _highlight_index    : int   = 0
var _scrub_dragging     : bool  = false   # true while user is dragging the scrub bar

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	layer = 12

	# Connect button signals
	if pause_button != null:
		pause_button.pressed.connect(_on_pause_pressed)
		pause_button.text = TEXT_PAUSE

	if instant_button != null:
		instant_button.pressed.connect(_on_instant_pressed)
		instant_button.text = TEXT_END

	if prev_highlight != null:
		prev_highlight.pressed.connect(_on_prev_highlight_pressed)
		prev_highlight.text = TEXT_PREV

	if next_highlight != null:
		next_highlight.pressed.connect(_on_next_highlight_pressed)
		next_highlight.text = TEXT_NEXT

	# Connect speed buttons
	for i : int in range(speed_buttons.size()):
		if speed_buttons[i] == null:
			continue
		var idx : int = i   # capture for lambda
		speed_buttons[i].pressed.connect(func() -> void: _on_speed_pressed(idx))
		speed_buttons[i].text = SPEED_LABELS[i] if i < SPEED_LABELS.size() else "?"

	# Connect scrub bar
	if scrub_bar != null:
		scrub_bar.drag_started.connect(_on_scrub_drag_started)
		scrub_bar.drag_ended.connect(_on_scrub_drag_ended)
		scrub_bar.value_changed.connect(_on_scrub_value_changed)
		scrub_bar.min_value = 0.0
		scrub_bar.step      = 1.0

	_refresh_button_states()

# ── Public API ────────────────────────────────────────────────────────────────

## Call after start_replay() so scrub bar knows total range.
func initialise(total_ticks: int) -> void:
	_total_ticks = total_ticks
	_paused      = false

	if scrub_bar != null:
		scrub_bar.max_value = float(maxf(1.0, float(total_ticks - 1)))
		scrub_bar.value     = 0.0

	_refresh_button_states()


## Connected to ReplayPlayer.tick_advanced
func on_tick_advanced(tick: int, _match_minute: int) -> void:
	_current_tick = tick

	# Update scrub bar position while not being dragged
	if scrub_bar != null and not _scrub_dragging:
		scrub_bar.set_value_no_signal(float(tick))


## Connected to ReplayPlayer.playback_finished
func on_playback_finished() -> void:
	_paused = true
	if pause_button != null:
		pause_button.text = TEXT_PLAY
	_refresh_button_states()


## Connected to ReplayPlayer.phase_changed
## Pause controls during goal celebration / half-time.
func on_phase_changed(phase_int: int) -> void:
	# Phase 4 = GoalScored, Phase 5 = HalfTime
	# These are handled by the engine; speed controls stay responsive.
	pass

# ── Button handlers ───────────────────────────────────────────────────────────

func _on_pause_pressed() -> void:
	if replay_player == null:
		return

	if _paused:
		replay_player.resume()
		_paused = false
		if pause_button != null:
			pause_button.text = TEXT_PAUSE
	else:
		replay_player.pause()
		_paused = true
		if pause_button != null:
			pause_button.text = TEXT_PLAY

	_refresh_button_states()


func _on_speed_pressed(index: int) -> void:
	if replay_player == null:
		return
	if index < 0 or index >= SPEED_STEPS.size():
		return

	var speed : float = SPEED_STEPS[index]
	_current_speed = speed
	replay_player.set_speed(speed)
	_paused = false

	if pause_button != null:
		pause_button.text = TEXT_PAUSE

	_refresh_button_states()

	# Update HUD speed label
	if hud != null and hud.has_method("set_speed_display"):
		var label_text : String = SPEED_LABELS[index] if index < SPEED_LABELS.size() else "?"
		hud.set_speed_display(label_text)


func _on_instant_pressed() -> void:
	if replay_player == null:
		return
	# Seek to last tick
	var last_tick : int = maxi(0, _total_ticks - 1)
	replay_player.seek(last_tick)
	_paused = true
	replay_player.pause()
	if pause_button != null:
		pause_button.text = TEXT_PLAY
	_refresh_button_states()


func _on_prev_highlight_pressed() -> void:
	if replay_player == null:
		return
	_highlight_index = maxi(0, _highlight_index - 1)
	replay_player.seek_to_highlight(_highlight_index)
	_paused = true
	replay_player.pause()
	if pause_button != null:
		pause_button.text = TEXT_PLAY


func _on_next_highlight_pressed() -> void:
	if replay_player == null:
		return
	var max_idx : int = replay_player.get_highlight_count() - 1
	_highlight_index = mini(_highlight_index + 1, maxi(0, max_idx))
	replay_player.seek_to_highlight(_highlight_index)
	_paused = true
	replay_player.pause()
	if pause_button != null:
		pause_button.text = TEXT_PLAY

# ── Scrub bar handlers ────────────────────────────────────────────────────────

func _on_scrub_drag_started() -> void:
	_scrub_dragging = true
	# Pause while scrubbing for a clean seek experience
	if replay_player != null:
		replay_player.pause()


func _on_scrub_drag_ended(_value_changed: bool) -> void:
	_scrub_dragging = false
	if scrub_bar == null or replay_player == null:
		return

	var target_tick : int = int(scrub_bar.value)
	replay_player.seek(target_tick)

	# Resume at previous speed after seek if wasn't paused before
	if not _paused:
		replay_player.resume()


func _on_scrub_value_changed(value: float) -> void:
	# Only act on user drag — programmatic changes are ignored while not dragging
	if not _scrub_dragging:
		return
	if replay_player == null:
		return
	# Live preview: seek without resuming
	replay_player.seek(int(value))

# ── Private: update button visual state ──────────────────────────────────────

func _refresh_button_states() -> void:
	# Pause button
	if pause_button != null:
		pause_button.modulate = COLOR_BUTTON_ACTIVE

	# Speed buttons: highlight the currently active speed
	for i : int in range(speed_buttons.size()):
		if speed_buttons[i] == null:
			continue
		var is_selected : bool = (
			not _paused and
			i < SPEED_STEPS.size() and
			absf(SPEED_STEPS[i] - _current_speed) < 0.01
		)
		speed_buttons[i].modulate = COLOR_BUTTON_SELECTED if is_selected else COLOR_BUTTON_INACTIVE

	# Highlight navigation: dim if no highlights exist
	var has_highlights : bool = (
		replay_player != null and
		replay_player.get_highlight_count() > 0
	)
	if prev_highlight != null:
		prev_highlight.modulate = COLOR_BUTTON_ACTIVE if has_highlights else COLOR_BUTTON_INACTIVE
	if next_highlight != null:
		next_highlight.modulate = COLOR_BUTTON_ACTIVE if has_highlights else COLOR_BUTTON_INACTIVE

	# Instant button: always active while replay exists
	if instant_button != null:
		instant_button.modulate = COLOR_BUTTON_ACTIVE
