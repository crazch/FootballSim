# =============================================================================
# Module:  EventLogUI.gd
# Path:    FootballSim/Scripts/UI/EventLogUI.gd
# Purpose:
#   Scrolling panel on the right side of the screen that shows match events
#   as they occur during replay. Each event type has a distinct colour.
#   Only "notable" events are shown — passes and possession changes are
#   filtered out by default to keep the log readable.
#
#   Data flow: ReplayPlayer.event_fired → on_event_fired(event_dict)
#   No Bridge access. No direct engine access.
#
# What is shown (by default):
#   Goals (★ yellow), Saves (blue), Shots on target (green),
#   Shots off target (grey), Tackles (white), Fouls (orange),
#   Yellow cards (yellow), Red cards (red), Corners (teal),
#   Free kicks (teal), Penalties (orange), Half/Full time (cyan),
#   Key passes (lime), Interceptions (purple).
#   Passes, pressures, possession events: filtered out (too frequent).
#
# Scene tree:
#
#   EventLogUI (CanvasLayer)             ← this script, layer = 11
#     └── Panel (PanelContainer)         ← background panel, anchored right
#           └── VBox (VBoxContainer)
#                 ├── TitleLabel (Label)  "MATCH EVENTS"
#                 └── ScrollContainer
#                       └── LogVBox (VBoxContainer)  ← event labels added here
#
# Inspector setup:
#   @export var log_vbox : VBoxContainer ← drag LogVBox node here
#   @export var max_entries : int = 30   ← max visible entries before oldest removed
#
# How to wire in MatchView.gd _ready():
#   $ReplayPlayer.event_fired.connect($EventLogUI.on_event_fired)
#   $ReplayPlayer.playback_finished.connect($EventLogUI.on_playback_finished)
#
# Strict typing: no := on arrays / get / bridge results / math.
# =============================================================================

extends CanvasLayer

# ── MatchEventType int values (match C# enum order exactly) ─────────────────
const EV_GOAL            : int = 0
const EV_OWNGOL          : int = 1
const EV_SHOT_ON         : int = 2
const EV_SHOT_OFF        : int = 3
const EV_SHOT_BLOCKED    : int = 4
const EV_SAVE            : int = 5
const EV_CLAIMED_CROSS   : int = 6
const EV_PASS_COMPLETED  : int = 7
const EV_PASS_INTERCEPT  : int = 8
const EV_PASS_OUT        : int = 9
const EV_DRIBBLE_OK      : int = 10
const EV_DRIBBLE_FAIL    : int = 11
const EV_TACKLE_OK       : int = 12
const EV_TACKLE_FOUL     : int = 13
const EV_PRESS_OK        : int = 14
const EV_PRESS_FAIL      : int = 15
const EV_POSS_WON        : int = 16
const EV_POSS_LOST       : int = 17
const EV_KICKOFF         : int = 18
const EV_THROW_IN        : int = 19
const EV_GOAL_KICK       : int = 20
const EV_CORNER          : int = 21
const EV_FREE_KICK       : int = 22
const EV_PENALTY         : int = 23
const EV_FOUL            : int = 24
const EV_YELLOW          : int = 25
const EV_RED             : int = 26
const EV_OFFSIDE         : int = 27
const EV_HALFTIME        : int = 28
const EV_FULLTIME        : int = 29
const EV_KEY_PASS        : int = 30
const EV_ASSIST          : int = 31
const EV_CROSS           : int = 32
const EV_HEADER          : int = 33
const EV_LONG_BALL       : int = 34

# ── Event colours ─────────────────────────────────────────────────────────────
const COLOR_GOAL         : Color = Color(1.00, 0.92, 0.10, 1.0)  # gold
const COLOR_OWNGOL       : Color = Color(1.00, 0.50, 0.10, 1.0)  # orange-red
const COLOR_SHOT_ON      : Color = Color(0.40, 1.00, 0.40, 1.0)  # green
const COLOR_SHOT_OFF     : Color = Color(0.60, 0.60, 0.60, 1.0)  # grey
const COLOR_SAVE         : Color = Color(0.30, 0.70, 1.00, 1.0)  # sky blue
const COLOR_TACKLE       : Color = Color(0.90, 0.90, 0.90, 1.0)  # near-white
const COLOR_FOUL         : Color = Color(1.00, 0.60, 0.20, 1.0)  # orange
const COLOR_YELLOW_CARD  : Color = Color(1.00, 0.95, 0.10, 1.0)  # yellow
const COLOR_RED_CARD     : Color = Color(1.00, 0.15, 0.15, 1.0)  # red
const COLOR_CORNER       : Color = Color(0.20, 0.85, 0.75, 1.0)  # teal
const COLOR_PENALTY      : Color = Color(1.00, 0.65, 0.20, 1.0)  # amber
const COLOR_HALFTIME     : Color = Color(0.40, 0.90, 1.00, 1.0)  # cyan
const COLOR_FULLTIME     : Color = Color(0.50, 1.00, 0.50, 1.0)  # lime green
const COLOR_KEY_PASS     : Color = Color(0.70, 1.00, 0.30, 1.0)  # lime
const COLOR_INTERCEPT    : Color = Color(0.80, 0.40, 1.00, 1.0)  # purple
const COLOR_DEFAULT      : Color = Color(0.75, 0.75, 0.75, 0.80) # dim white

# ── Filtering: event types that are NOT shown in the log by default ───────────
# Passes and possession churn on every tick — too noisy for readability.
const FILTERED_EVENTS : Array[int] = [
	EV_PASS_COMPLETED, EV_PASS_OUT, EV_PRESS_OK, EV_PRESS_FAIL,
	EV_POSS_WON, EV_POSS_LOST, EV_THROW_IN, EV_GOAL_KICK,
	EV_KICKOFF, EV_DRIBBLE_OK, EV_DRIBBLE_FAIL, EV_CROSS,
	EV_LONG_BALL, EV_HEADER, EV_OFFSIDE, EV_ASSIST
]

# ── Panel background ──────────────────────────────────────────────────────────
const COLOR_PANEL_BG     : Color = Color(0.04, 0.04, 0.06, 0.86)
const FONT_SIZE_ENTRY    : int   = 11
const FONT_SIZE_TITLE    : int   = 12

# ── Exports ────────────────────────────────────────────────────────────────────

## The VBoxContainer inside the ScrollContainer where event labels are added.
@export var log_vbox    : VBoxContainer = null

## Maximum number of entries to keep. Oldest entries are removed when exceeded.
@export var max_entries : int = 35

# ── State ─────────────────────────────────────────────────────────────────────

var _entry_count : int = 0

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	layer = 11

# ── Public API ────────────────────────────────────────────────────────────────

## Connected to ReplayPlayer.event_fired
func on_event_fired(event_dict: Dictionary) -> void:
	var ev_type : int = event_dict.get("type", -1)

	# Filter out noisy events
	if FILTERED_EVENTS.has(ev_type):
		return

	var description : String = event_dict.get("description", "")
	if description.is_empty():
		return

	_add_entry(description, _event_color(ev_type), ev_type)


## Connected to ReplayPlayer.playback_finished
## Adds a separator line at the end of the log.
func on_playback_finished() -> void:
	_add_entry("── Match Over ──", COLOR_FULLTIME, EV_FULLTIME)


## Clear all entries (call between matches).
func clear() -> void:
	if log_vbox == null:
		return
	for child in log_vbox.get_children():
		child.queue_free()
	_entry_count = 0

# ── Private ───────────────────────────────────────────────────────────────────

func _add_entry(text: String, color: Color, ev_type: int) -> void:
	if log_vbox == null:
		return

	# Remove oldest entry if at capacity
	if _entry_count >= max_entries:
		var children : Array = log_vbox.get_children()
		if children.size() > 0:
			children[0].queue_free()
		else:
			_entry_count = 0

	var label : Label = Label.new()
	label.text = text
	label.modulate = color
	label.add_theme_font_size_override("font_size", FONT_SIZE_ENTRY)
	label.autowrap_mode = TextServer.AUTOWRAP_WORD
	label.mouse_filter = Control.MOUSE_FILTER_IGNORE

	# Goal entries get a visual separator above them
	if ev_type == EV_GOAL or ev_type == EV_OWNGOL:
		var sep : HSeparator = HSeparator.new()
		sep.modulate = Color(1.0, 0.90, 0.10, 0.60)
		log_vbox.add_child(sep)
		_entry_count += 1

	# Half/Full time entries get separator below
	var is_milestone : bool = (ev_type == EV_HALFTIME or ev_type == EV_FULLTIME)

	log_vbox.add_child(label)
	_entry_count += 1

	if is_milestone:
		var sep2 : HSeparator = HSeparator.new()
		sep2.modulate = Color(0.40, 0.90, 1.00, 0.50)
		log_vbox.add_child(sep2)
		_entry_count += 1

	# Scroll to bottom — find the ScrollContainer parent
	_scroll_to_bottom()


func _scroll_to_bottom() -> void:
	if log_vbox == null:
		return
	var parent : Node = log_vbox.get_parent()
	if parent is ScrollContainer:
		var sc : ScrollContainer = parent as ScrollContainer
		# Deferred so layout is computed first
		sc.call_deferred("set_v_scroll", sc.get_v_scroll_bar().max_value as int)


func _event_color(ev_type: int) -> Color:
	match ev_type:
		EV_GOAL:         return COLOR_GOAL
		EV_OWNGOL:       return COLOR_OWNGOL
		EV_SHOT_ON:      return COLOR_SHOT_ON
		EV_SHOT_OFF:     return COLOR_SHOT_OFF
		EV_SHOT_BLOCKED: return COLOR_SHOT_OFF
		EV_SAVE:         return COLOR_SAVE
		EV_CLAIMED_CROSS:return COLOR_SAVE
		EV_TACKLE_OK:    return COLOR_TACKLE
		EV_TACKLE_FOUL:  return COLOR_FOUL
		EV_FOUL:         return COLOR_FOUL
		EV_YELLOW:       return COLOR_YELLOW_CARD
		EV_RED:          return COLOR_RED_CARD
		EV_CORNER:       return COLOR_CORNER
		EV_FREE_KICK:    return COLOR_CORNER
		EV_PENALTY:      return COLOR_PENALTY
		EV_HALFTIME:     return COLOR_HALFTIME
		EV_FULLTIME:     return COLOR_FULLTIME
		EV_KEY_PASS:     return COLOR_KEY_PASS
		EV_PASS_INTERCEPT: return COLOR_INTERCEPT
		_:               return COLOR_DEFAULT
