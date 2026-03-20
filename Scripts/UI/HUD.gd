# =============================================================================
# Module:  HUD.gd
# Path:    FootballSim/Scripts/UI/HUD.gd
# Purpose:
#   In-match heads-up display drawn as a CanvasLayer above the pitch.
#   Shows live score, match clock, xG accumulated totals, possession bar,
#   and a stamina collapse warning when average stamina drops below threshold.
#
#   The HUD NEVER reads from Bridge directly.
#   All data arrives through signal connections from ReplayPlayer:
#     ReplayPlayer.score_changed  → on_score_changed(home, away)
#     ReplayPlayer.tick_advanced  → on_tick_advanced(tick, minute)
#     ReplayPlayer.phase_changed  → on_phase_changed(phase_int)
#     ReplayPlayer.event_fired    → on_event_fired(event_dict)
#
#   The HUD reads live xG and possession from Bridge.GetTeamStats() once
#   per second (every 600 ticks) to avoid calling it every tick.
#
# Scene tree (CanvasLayer so it renders above all 2D pitch content):
#
#   HUD (CanvasLayer)                    ← this script, layer = 10
#     ├── TopBar (PanelContainer)        ← semi-transparent dark bar across top
#     │     └── TopBarHBox (HBoxContainer)
#     │           ├── HomeScoreLabel (Label)
#     │           ├── ScoreSeparator (Label)  "–"
#     │           ├── AwayScoreLabel (Label)
#     │           ├── VSpacer (Control)
#     │           ├── ClockLabel (Label)
#     │           ├── VSpacer2 (Control)
#     │           ├── PhaseLabel (Label)
#     │           └── SpeedLabel (Label)
#     ├── StatsBar (PanelContainer)      ← narrow bar just below top bar
#     │     └── StatsHBox (HBoxContainer)
#     │           ├── HomeXGLabel (Label)
#     │           ├── XGBar (Control)    ← custom drawn bar (xG ratio)
#     │           ├── AwayXGLabel (Label)
#     │           ├── Spacer (Control)
#     │           ├── HomePossLabel (Label)
#     │           ├── PossBar (Control)  ← custom drawn bar (possession %)
#     │           └── AwayPossLabel (Label)
#     └── StaminaWarning (PanelContainer)  ← shown only when press collapses
#           └── StaminaLabel (Label)
#
# How to wire in MatchView.gd _ready():
#   $ReplayPlayer.score_changed.connect($HUD.on_score_changed)
#   $ReplayPlayer.tick_advanced.connect($HUD.on_tick_advanced)
#   $ReplayPlayer.phase_changed.connect($HUD.on_phase_changed)
#   $ReplayPlayer.event_fired.connect($HUD.on_event_fired)
#   $HUD.initialise(home_team_name, away_team_name,
#                   home_kit_primary, away_kit_primary)
#
# Strict typing: no := on any array / lerp / get / bridge call / math result.
# =============================================================================

extends CanvasLayer

# ── MatchPhase enum values (mirrors C# MatchPhase order) ─────────────────────
const PHASE_PREKICKOFF  : int = 0
const PHASE_KICKOFF     : int = 1
const PHASE_OPENPLAY    : int = 2
const PHASE_SETPIECE    : int = 3
const PHASE_GOALSCORED  : int = 4
const PHASE_HALFTIME    : int = 5
const PHASE_FULLTIME    : int = 6

# ── MatchEventType values that affect HUD display ─────────────────────────────
const EVENT_GOAL        : int = 0
const EVENT_OWNGOL      : int = 1
const EVENT_HALFTIME    : int = 28
const EVENT_FULLTIME    : int = 29

# ── Ticks between live stat polls (600 ticks = 1 match minute) ───────────────
const STAT_POLL_INTERVAL : int = 600

# ── Stamina collapse threshold (mirrors AIConstants.PRESS_STAMINA_THRESHOLD) ─
const STAMINA_WARN_THRESHOLD : float = 0.45

# ── Colours ───────────────────────────────────────────────────────────────────
const COLOR_PANEL_BG       : Color = Color(0.05, 0.05, 0.08, 0.82)
const COLOR_CLOCK_NORMAL   : Color = Color(0.92, 0.92, 0.92, 1.0)
const COLOR_CLOCK_INJURY   : Color = Color(1.0,  0.85, 0.20, 1.0)
const COLOR_PHASE_NORMAL   : Color = Color(0.60, 0.60, 0.60, 1.0)
const COLOR_PHASE_GOAL     : Color = Color(1.0,  0.90, 0.20, 1.0)
const COLOR_PHASE_HT       : Color = Color(0.40, 0.85, 1.0,  1.0)
const COLOR_PHASE_FT       : Color = Color(0.50, 1.0,  0.50, 1.0)
const COLOR_SCORE_NORMAL   : Color = Color(1.0,  1.0,  1.0,  1.0)
const COLOR_SCORE_FLASH    : Color = Color(1.0,  0.95, 0.20, 1.0)
const COLOR_STAMINA_WARN   : Color = Color(1.0,  0.35, 0.15, 0.90)
const COLOR_XG_BAR_BG      : Color = Color(0.20, 0.20, 0.20, 0.80)

# ── Score flash duration (seconds) ────────────────────────────────────────────
const SCORE_FLASH_DURATION : float = 1.8

# ── Exports (assign in Inspector) ─────────────────────────────────────────────

@export var home_score_label    : Label        = null
@export var away_score_label    : Label        = null
@export var clock_label         : Label        = null
@export var phase_label         : Label        = null
@export var speed_label         : Label        = null
@export var home_xg_label       : Label        = null
@export var away_xg_label       : Label        = null
@export var home_poss_label     : Label        = null
@export var away_poss_label     : Label        = null
@export var xg_bar              : Control      = null
@export var poss_bar            : Control      = null
@export var stamina_warning     : Control      = null
@export var stamina_label       : Label        = null

# ── State ─────────────────────────────────────────────────────────────────────

var _home_name     : String = "Home"
var _away_name     : String = "Away"
var _home_color    : Color  = Color(0.3,  0.6,  1.0,  1.0)
var _away_color    : Color  = Color(1.0,  0.4,  0.4,  1.0)

var _home_score    : int    = 0
var _away_score    : int    = 0
var _current_tick  : int    = 0
var _current_minute: int    = 0
var _phase_int     : int    = PHASE_PREKICKOFF

# xG and possession (updated once per STAT_POLL_INTERVAL ticks)
var _home_xg       : float  = 0.0
var _away_xg       : float  = 0.0
var _home_poss     : float  = 0.5
var _last_stat_tick: int    = -STAT_POLL_INTERVAL

# Score flash timer
var _score_flash_timer : float = 0.0
var _score_flashing    : bool  = false

# Stamina warning state
var _stamina_warn_active  : bool   = false
var _stamina_warn_team    : int    = -1
var _stamina_warn_minute  : int    = -1

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	layer = 10
	if stamina_warning != null:
		stamina_warning.visible = false
	_refresh_score_display()
	_refresh_clock_display()

func _process(delta: float) -> void:
	# Score flash decay
	if _score_flashing:
		_score_flash_timer -= delta
		if _score_flash_timer <= 0.0:
			_score_flashing = false
			_set_score_color(COLOR_SCORE_NORMAL)

# ── Public API ────────────────────────────────────────────────────────────────

## Call once after simulation is ready, before start_replay().
func initialise(
	home_name     : String,
	away_name     : String,
	home_kit_int  : int,
	away_kit_int  : int
) -> void:
	_home_name  = home_name
	_away_name  = away_name
	_home_color = _unpack_color(home_kit_int)
	_away_color = _unpack_color(away_kit_int)
	_refresh_score_display()
	_refresh_xg_display()
	_refresh_poss_display()


## Connected to ReplayPlayer.score_changed
func on_score_changed(home_score: int, away_score: int) -> void:
	_home_score = home_score
	_away_score = away_score
	_refresh_score_display()
	_start_score_flash()


## Connected to ReplayPlayer.tick_advanced
func on_tick_advanced(tick: int, match_minute: int) -> void:
	_current_tick   = tick
	_current_minute = match_minute
	_refresh_clock_display()

	# Poll live stats once per match minute
	if tick - _last_stat_tick >= STAT_POLL_INTERVAL:
		_poll_live_stats()
		_last_stat_tick = tick


## Connected to ReplayPlayer.phase_changed
func on_phase_changed(phase_int: int) -> void:
	_phase_int = phase_int
	_refresh_phase_label()


## Connected to ReplayPlayer.event_fired
## Checks for stamina-relevant events to trigger collapse warning.
func on_event_fired(event_dict: Dictionary) -> void:
	var ev_type : int = event_dict.get("type", -1)

	# Flash on goal
	if ev_type == EVENT_GOAL or ev_type == EVENT_OWNGOL:
		_start_score_flash()

	# Nothing else in HUD is driven by events —
	# EventLogUI handles the scrolling log.


## Update speed label (called by SpeedControls when speed changes).
func set_speed_display(speed_label_text: String) -> void:
	if speed_label != null:
		speed_label.text = speed_label_text


## Show stamina collapse warning.
## Called by MatchView after checking hypothesis data.
func show_stamina_warning(team_name: String, minute: int) -> void:
	_stamina_warn_active = true
	_stamina_warn_minute = minute

	if stamina_warning != null:
		stamina_warning.visible = true
	if stamina_label != null:
		stamina_label.text = "%s press collapsed at min %d (stamina)" % [team_name, minute]
		stamina_label.modulate = COLOR_STAMINA_WARN


## Hide stamina warning.
func hide_stamina_warning() -> void:
	_stamina_warn_active = false
	if stamina_warning != null:
		stamina_warning.visible = false

# ── Private: display refreshers ───────────────────────────────────────────────

func _refresh_score_display() -> void:
	if home_score_label != null:
		home_score_label.text = str(_home_score)
	if away_score_label != null:
		away_score_label.text = str(_away_score)


func _refresh_clock_display() -> void:
	if clock_label == null:
		return

	var minute : int = _current_minute
	var second : int = (_current_tick % 600) / 10   # seconds within the current minute

	# Show "45+'" or "90+'" during injury time if tick exceeds half/full time marks
	var display_minute : int = minute
	var suffix         : String = ""

	if _current_tick > 27000 and _current_tick < 32000 and _phase_int != PHASE_HALFTIME:
		# Extra time at end of first half
		var extra : int = maxi(0, minute - 45)
		display_minute = 45
		suffix = "+%d'" % extra
	elif _current_tick > 54000:
		var extra : int = maxi(0, minute - 90)
		display_minute = 90
		suffix = "+%d'" % extra

	if suffix.is_empty():
		clock_label.text = "%d:%02d" % [display_minute, second]
	else:
		clock_label.text = "%d%s" % [display_minute, suffix]

	# Clock colour: yellow during injury time
	var is_injury_time : bool = not suffix.is_empty()
	clock_label.modulate = COLOR_CLOCK_INJURY if is_injury_time else COLOR_CLOCK_NORMAL


func _refresh_phase_label() -> void:
	if phase_label == null:
		return

	var text  : String = ""
	var color : Color  = COLOR_PHASE_NORMAL

	match _phase_int:
		PHASE_PREKICKOFF: text = "Pre-Match";  color = COLOR_PHASE_NORMAL
		PHASE_KICKOFF:    text = "Kick Off";   color = COLOR_PHASE_NORMAL
		PHASE_OPENPLAY:   text = ""            # silent during open play
		PHASE_SETPIECE:   text = "Set Piece";  color = COLOR_PHASE_NORMAL
		PHASE_GOALSCORED: text = "GOAL!";      color = COLOR_PHASE_GOAL
		PHASE_HALFTIME:   text = "Half Time";  color = COLOR_PHASE_HT
		PHASE_FULLTIME:   text = "Full Time";  color = COLOR_PHASE_FT

	phase_label.text     = text
	phase_label.modulate = color


func _refresh_xg_display() -> void:
	if home_xg_label != null:
		home_xg_label.text = "xG %.2f" % _home_xg
	if away_xg_label != null:
		away_xg_label.text = "xG %.2f" % _away_xg
	if xg_bar != null:
		xg_bar.queue_redraw()


func _refresh_poss_display() -> void:
	var home_pct : int = int(_home_poss * 100.0)
	var away_pct : int = 100 - home_pct

	if home_poss_label != null:
		home_poss_label.text = "%d%%" % home_pct
	if away_poss_label != null:
		away_poss_label.text = "%d%%" % away_pct
	if poss_bar != null:
		poss_bar.queue_redraw()

# ── Private: live stat poll ───────────────────────────────────────────────────

func _poll_live_stats() -> void:
	# Read cumulative stats from bridge once per minute.
	# GetTeamStats works at any point during replay (stats are pre-computed).
	var home_stats : Dictionary = Bridge.GetTeamStats(0)
	var away_stats : Dictionary = Bridge.GetTeamStats(1)

	if not home_stats.is_empty():
		_home_xg   = home_stats.get("xg",                   0.0)
		_home_poss = home_stats.get("possession_percent",    0.5)

	if not away_stats.is_empty():
		_away_xg   = away_stats.get("xg", 0.0)

	_refresh_xg_display()
	_refresh_poss_display()

# ── Private: score flash ──────────────────────────────────────────────────────

func _start_score_flash() -> void:
	_score_flashing    = true
	_score_flash_timer = SCORE_FLASH_DURATION
	_set_score_color(COLOR_SCORE_FLASH)


func _set_score_color(color: Color) -> void:
	if home_score_label != null:
		home_score_label.modulate = color
	if away_score_label != null:
		away_score_label.modulate = color

# ── Private: helpers ──────────────────────────────────────────────────────────

func _unpack_color(packed: int) -> Color:
	var r : float = float((packed >> 16) & 0xFF) / 255.0
	var g : float = float((packed >>  8) & 0xFF) / 255.0
	var b : float = float( packed        & 0xFF)  / 255.0
	return Color(r, g, b, 1.0)

# =============================================================================
# XGBar — inner Control that draws the xG ratio bar
# Attach this script to the xg_bar Control node, OR handle _draw inline below.
# =============================================================================

# If you want XGBar and PossBar as separate scripts, use these draw functions.
# Alternatively paste _draw_xg_bar() directly into a minimal Control script.

## Draws a split bar: left = home xG fill, right = away xG fill.
## Call this from the xg_bar Control node's _draw().
func draw_xg_bar_onto(control: Control) -> void:
	if control == null:
		return
	var w : float = control.size.x
	var h : float = control.size.y

	# Background
	control.draw_rect(Rect2(0.0, 0.0, w, h), COLOR_XG_BAR_BG)

	var total : float = _home_xg + _away_xg
	if total < 0.001:
		return

	var home_frac  : float = _home_xg / total
	var home_width : float = w * home_frac

	# Home fill (left side)
	control.draw_rect(
		Rect2(0.0, 0.0, home_width, h),
		Color(_home_color.r, _home_color.g, _home_color.b, 0.75)
	)
	# Away fill (right side)
	control.draw_rect(
		Rect2(home_width, 0.0, w - home_width, h),
		Color(_away_color.r, _away_color.g, _away_color.b, 0.75)
	)
	# Centre divider
	control.draw_line(
		Vector2(home_width, 0.0),
		Vector2(home_width, h),
		Color(1.0, 1.0, 1.0, 0.5),
		1.0
	)


## Draws a split bar showing possession %: left = home, right = away.
func draw_poss_bar_onto(control: Control) -> void:
	if control == null:
		return
	var w : float = control.size.x
	var h : float = control.size.y

	control.draw_rect(Rect2(0.0, 0.0, w, h), COLOR_XG_BAR_BG)

	var home_width : float = w * clampf(_home_poss, 0.0, 1.0)

	control.draw_rect(
		Rect2(0.0, 0.0, home_width, h),
		Color(_home_color.r, _home_color.g, _home_color.b, 0.70)
	)
	control.draw_rect(
		Rect2(home_width, 0.0, w - home_width, h),
		Color(_away_color.r, _away_color.g, _away_color.b, 0.70)
	)
	control.draw_line(
		Vector2(home_width, 0.0),
		Vector2(home_width, h),
		Color(1.0, 1.0, 1.0, 0.5),
		1.0
	)
