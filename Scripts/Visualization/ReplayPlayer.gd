# =============================================================================
# Module:  ReplayPlayer.gd
# Path:    FootballSim/Scripts/Visualization/ReplayPlayer.gd
# Purpose:
#   Master driver for all 2D pitch visualization.
#   Reads bridge frames tick-by-tick and pushes data to PlayerDot and BallDot.
#   Owns the playback state (current tick, speed, pause).
#   Emits signals for HUD, EventLog, and SpeedControls to listen to.
#
# Responsibilities:
#   • Manage playback state: current_tick, playback_speed, paused
#   • Advance ticks each _process() frame according to speed
#   • Call Bridge.GetFrame(tick) to get position data
#   • Call Bridge.GetFrameEvents(tick) and emit signal for each event
#   • Push apply_snap() to all 22 PlayerDot nodes and BallDot
#   • Emit score/phase change signals for HUD
#   • Support seek, highlights, pause, resume from external scripts
#
# Scene tree expectation (MatchView.tscn):
#   MatchView (Node2D)
#     ├── PitchRenderer (Node2D)         ← draws pitch lines, goals
#     ├── PlayersLayer (Node2D)          ← ReplayPlayer spawns PlayerDots here
#     ├── BallDot (Node2D)               ← ball dot (single instance)
#     └── ReplayPlayer (Node)            ← this script
#
# Signals (listened to by HUD.gd, EventLogUI.gd, SpeedControls.gd):
#   score_changed(home_score, away_score)
#   phase_changed(phase_int)
#   event_fired(event_dict)        — one signal per event per tick
#   playback_finished()
#   tick_advanced(tick, match_minute)
#
# Public API (called by MatchView.gd and SpeedControls.gd):
#   start_replay()            — call after SimulateMatch finishes
#   pause()
#   resume()
#   seek(tick)                — jump to specific tick
#   seek_to_highlight(index)  — jump to nth highlight tick
#   set_speed(multiplier)     — 0.5, 1.0, 2.0, 4.0, 8.0, 16.0
#
# Bridge access:
#   ReplayPlayer talks to the Bridge AutoLoad directly.
#   It never accesses MatchEngine, MatchReplay, or any C# type directly.
#   All data comes through Bridge.GetFrame() and Bridge.GetFrameEvents().
#
# CRITICAL: Never type-annotate C# bridge variables (see BridgeUsage.gd Rule 1).
# =============================================================================

extends Node

# ── Signals ───────────────────────────────────────────────────────────────────

signal score_changed(home_score: int, away_score: int)
signal phase_changed(phase_int: int)
signal event_fired(event_dict: Dictionary)
signal playback_finished()
signal tick_advanced(tick: int, match_minute: int)

# ── Speed constants ────────────────────────────────────────────────────────────

const SPEED_OPTIONS := [0.5, 1.0, 2.0, 4.0, 8.0, 16.0]

# Ticks advanced per display frame at each speed multiplier.
# At 60fps and 1x: 1 tick/frame = 6 ticks/second = 0.6 match-seconds/display-second
# This is intentionally slower than real-time (real-time would be 10 ticks/s).
# Adjust TICKS_PER_FRAME_BASE to taste.
const TICKS_PER_FRAME_BASE := 1

# ── Exports ────────────────────────────────────────────────────────────────────

@export var DEBUG := false

## Kit colours for home and away teams.
## Set these before calling start_replay(), or they default to blue/red.
@export var home_kit_primary   : int = 0x2266FF
@export var home_kit_secondary : int = 0xFFFFFF
@export var away_kit_primary   : int = 0xFF3333
@export var away_kit_secondary : int = 0xFFFFFF

# ── Node references ────────────────────────────────────────────────────────────

## The Node2D that PlayerDot nodes will be spawned into.
## Assign in MatchView._ready() before calling start_replay().
@export var players_layer : Node2D = null

## The BallDot node (single instance, already in scene).
## Assign in MatchView._ready().
@export var ball_dot : Node2D = null

# ── Playback state ─────────────────────────────────────────────────────────────

var current_tick   : int   = 0
var total_ticks    : int   = 0
var paused         : bool  = true
var playback_speed : float = 1.0

# Fractional tick accumulator (for sub-1-tick speed like 0.5x)
var _tick_accumulator : float = 0.0

# ── Internal state ─────────────────────────────────────────────────────────────

# 22 PlayerDot instances. Index = PlayerId.
var _player_dots : Array = []   # Array of PlayerDot (untyped — C#-adjacent)

# Highlight tick list (from bridge, populated on start_replay)
var _highlight_ticks : Array = []

# Score tracking to detect changes (only emit signal when score changes)
var _last_home_score : int = -1
var _last_away_score : int = -1
var _last_phase      : int = -1

# PlayerDot scene path. PlayerDot.gd is attached to a plain Node2D in your scene.
# We create them dynamically via script, not via scene instancing.
# If you prefer scene-based instantiation, replace this with preload().
const PLAYER_DOT_SCRIPT := preload("res://Scripts/Visualization/PlayerDot.gd")
const BALL_DOT_SCRIPT   := preload("res://Scripts/Visualization/BallDot.gd")

# ── Lifecycle ──────────────────────────────────────────────────────────────────

func _ready() -> void:
	set_process(false)   # don't process until start_replay() is called
	if DEBUG:
		print("[ReplayPlayer] Ready. Waiting for start_replay().")

func _process(_delta: float) -> void:
	if paused or not Bridge.IsReplayReady():
		return

	# Accumulate fractional ticks for slow speeds (0.5x = advance every other frame)
	_tick_accumulator += playback_speed * TICKS_PER_FRAME_BASE

	var ticks_this_frame := int(_tick_accumulator)
	_tick_accumulator    -= ticks_this_frame

	for _i in range(max(1, ticks_this_frame)):
		if current_tick >= total_ticks:
			_on_playback_finished()
			return
		_advance_one_tick()

# ── Advance one tick ───────────────────────────────────────────────────────────

func _advance_one_tick() -> void:
	var frame : Dictionary = Bridge.GetFrame(current_tick)
	if frame.is_empty():
		current_tick += 1
		return

	# Push data to all visual nodes
	_apply_frame(frame)

	# Emit events for this tick
	_emit_frame_events(current_tick, frame)

	# Emit tick signal for HUD clock
	emit_signal("tick_advanced", current_tick, int(frame.get("match_minute", 0)))

	current_tick += 1

func _apply_frame(frame: Dictionary) -> void:
	# ── Ball ──────────────────────────────────────────────────────────────────
	if ball_dot != null:
		var ball_pos   : Vector2 = frame.get("ball_pos",   Vector2.ZERO)
		var ball_phase : int     = frame.get("ball_phase", 0)
		var ball_height: float   = frame.get("ball_height",0.0)
		var ball_owner : int     = frame.get("ball_owner", -1)
		var ball_shot  : bool    = frame.get("ball_is_shot", false)
		ball_dot.apply_snap(ball_pos, ball_phase, ball_height, ball_owner, ball_shot)

	# ── Players ───────────────────────────────────────────────────────────────
	var players_array : Array = frame.get("players", [])
	for i in range(min(players_array.size(), _player_dots.size())):
		var p    : Dictionary = players_array[i]
		var dot  = _player_dots[i]
		if dot == null: continue

		dot.apply_snap(
			p.get("position",    Vector2.ZERO),
			p.get("stamina",     1.0),
			p.get("has_ball",    false),
			p.get("action",      0),
			p.get("is_active",   true),
			p.get("is_sprinting",false)
		)

	# ── Score (only emit signal when changed) ─────────────────────────────────
	var home_score : int = frame.get("home_score", 0)
	var away_score : int = frame.get("away_score", 0)
	if home_score != _last_home_score or away_score != _last_away_score:
		_last_home_score = home_score
		_last_away_score = away_score
		emit_signal("score_changed", home_score, away_score)

	# ── Phase (only emit signal when changed) ─────────────────────────────────
	var phase_int : int = frame.get("phase", 0)
	if phase_int != _last_phase:
		_last_phase = phase_int
		emit_signal("phase_changed", phase_int)

func _emit_frame_events(tick: int, _frame: Dictionary) -> void:
	var events : Array = Bridge.GetFrameEvents(tick)
	for ev in events:
		emit_signal("event_fired", ev)
		if DEBUG:
			print("[ReplayPlayer] Tick %d EVENT: %s" % [tick, ev.get("description", "?")])

# ── Public API ────────────────────────────────────────────────────────────────

## Call after Bridge.SimulateMatch() completes.
## Spawns all 22 PlayerDot nodes and positions ball_dot.
## players_layer and ball_dot must be assigned first.
func start_replay() -> void:
	if not Bridge.IsReplayReady():
		push_error("[ReplayPlayer] start_replay() called but bridge not ready. " +
				   "Call Bridge.SimulateMatch() first.")
		return

	total_ticks      = Bridge.GetTotalFrames()
	current_tick     = 0
	_tick_accumulator = 0.0
	_last_home_score  = -1
	_last_away_score  = -1
	_last_phase       = -1

	_spawn_player_dots()
	_load_highlight_ticks()

	# Snap to frame 0 immediately so pitch is populated before first _process
	var first_frame : Dictionary = Bridge.GetFrame(0)
	if not first_frame.is_empty():
		_apply_frame(first_frame)

	paused = false
	set_process(true)

	if DEBUG:
		print("[ReplayPlayer] Replay started. Total ticks: %d  Highlights: %d" % [
			total_ticks, _highlight_ticks.size()
		])

## Pause playback. Dots stay in current position.
func pause() -> void:
	paused = true

## Resume from paused state.
func resume() -> void:
	if not Bridge.IsReplayReady(): return
	paused = false

## Jump to a specific tick. Snaps dots immediately (no smoothing on seek).
func seek(tick: int) -> void:
	current_tick      = clamp(tick, 0, total_ticks - 1)
	_tick_accumulator = 0.0

	var frame : Dictionary = Bridge.GetFrame(current_tick)
	if not frame.is_empty():
		_apply_frame_instant(frame)

	_emit_frame_events(current_tick, frame)

## Jump to the Nth highlight event (index into highlight_ticks array).
func seek_to_highlight(index: int) -> void:
	if _highlight_ticks.is_empty(): return
	var clamped : float = clamp(index, 0, _highlight_ticks.size() - 1)
	seek(int(_highlight_ticks[clamped]))

## Set playback speed multiplier. Must be one of SPEED_OPTIONS.
## Passing 0.0 pauses. Passing a value not in SPEED_OPTIONS clamps to nearest.
func set_speed(multiplier: float) -> void:
	if multiplier <= 0.0:
		pause()
		return
	playback_speed = multiplier
	paused         = false

## Returns the current tick index.
func get_current_tick() -> int:
	return current_tick

## Returns the total highlight count (number of frames with events).
func get_highlight_count() -> int:
	return _highlight_ticks.size()

## Returns the tick index of the Nth highlight.
func get_highlight_tick(index: int) -> int:
	if index < 0 or index >= _highlight_ticks.size(): return 0
	return int(_highlight_ticks[index])

## Enables or disables debug overlays on all dots.
func set_debug_all(enabled: bool) -> void:
	DEBUG = enabled
	for dot in _player_dots:
		if dot != null:
			dot.set_debug(enabled)
	if ball_dot != null:
		ball_dot.set_debug(enabled)

# ── Private: spawn dots ────────────────────────────────────────────────────────

func _spawn_player_dots() -> void:
	# Remove any existing dots (e.g. from a previous match)
	for dot in _player_dots:
		if dot != null and is_instance_valid(dot):
			dot.queue_free()
	_player_dots.clear()

	if players_layer == null:
		push_error("[ReplayPlayer] players_layer is null. Assign it before start_replay().")
		return

	# Read initial frame to get player identity data
	# We need kit colours and shirt numbers from first frame.
	# Kit colours are set as exports on ReplayPlayer (pushed by MatchView from bridge dict).
	var first_frame : Dictionary = Bridge.GetFrame(0)
	var players_arr : Array      = first_frame.get("players", [])

	for i in range(22):
		var dot_node := Node2D.new()
		dot_node.set_script(PLAYER_DOT_SCRIPT)
		players_layer.add_child(dot_node)

		var team_id     : int  = 0 if i <= 10 else 1
		var kit_primary : int  = home_kit_primary   if team_id == 0 else away_kit_primary
		var kit_sec     : int  = home_kit_secondary if team_id == 0 else away_kit_secondary

		# Determine shirt number from first frame player data
		var shirt : int = i + 1   # fallback: just use index+1
		if i < players_arr.size():
			# Shirt numbers aren't in the frame dict directly —
			# they come from Bridge.GetPlayerStats(i)
			var stats : Dictionary = Bridge.GetPlayerStats(i)
			if not stats.is_empty():
				shirt = stats.get("shirt_number", shirt)

		# Is this player a GK? Check role from stats.
		var is_gk := false
		var stats2 : Dictionary = Bridge.GetPlayerStats(i)
		if not stats2.is_empty():
			# role is not in frame dict; we check first-frame player position
			# GK: home GK = index 0, away GK = index 11
			is_gk = (i == 0 or i == 11)

		dot_node.init(i, team_id, shirt, kit_primary, kit_sec, is_gk)
		dot_node.set_debug(DEBUG)

		_player_dots.append(dot_node)

	# Give BallDot references to all player dots for smooth owner-follow
	if ball_dot != null:
		var dot_dict := {}
		for i in range(_player_dots.size()):
			dot_dict[i] = _player_dots[i]
		ball_dot.register_player_dots(dot_dict)
		ball_dot.set_debug(DEBUG)

	if DEBUG:
		print("[ReplayPlayer] Spawned %d PlayerDots." % _player_dots.size())

func _load_highlight_ticks() -> void:
	_highlight_ticks = Bridge.GetHighlightTicks()
	if DEBUG:
		print("[ReplayPlayer] Loaded %d highlight ticks." % _highlight_ticks.size())

# ── Instant seek (no smoothing) ───────────────────────────────────────────────

# Used when seek() is called — we want an immediate snap, not a lerp.
# Temporarily bypasses the smooth lerp in PlayerDot by forcing _draw_pos = _target_pos.
func _apply_frame_instant(frame: Dictionary) -> void:
	_apply_frame(frame)

	# Force all dots to snap immediately by pushing position twice
	# PlayerDot._draw_pos is updated to _target_pos on the next _process —
	# the easiest approach without exposing an internal API is to set position directly.
	var players_array : Array = frame.get("players", [])
	for i in range(min(players_array.size(), _player_dots.size())):
		var p   : Dictionary = players_array[i]
		var dot = _player_dots[i]
		if dot == null: continue
		var pos : Vector2 = p.get("position", Vector2.ZERO)
		dot.position = pos

	if ball_dot != null:
		var ball_pos : Vector2 = frame.get("ball_pos", Vector2.ZERO)
		ball_dot.position = ball_pos

# ── Playback finished ─────────────────────────────────────────────────────────

func _on_playback_finished() -> void:
	paused = true
	set_process(false)
	emit_signal("playback_finished")
	if DEBUG:
		print("[ReplayPlayer] Playback finished at tick %d." % current_tick)
