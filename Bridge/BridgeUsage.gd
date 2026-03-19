# =============================================================================
# Module:  BridgeUsage.gd
# Path:    FootballSim/Bridge/BridgeUsage.gd
# Purpose:
#   Canonical GDScript usage examples for every MatchEngineBridge method.
#   This is NOT a runnable script — it is reference documentation.
#   Copy the patterns you need into TacticsScreen.gd, MatchView.gd, etc.
#
# CRITICAL GODOT C# RULES — read before touching the bridge:
#
#   RULE 1 — Never type-annotate a C# object reference:
#     WRONG:  var bridge: MatchEngineBridge = MatchEngineBridge.new()
#     CORRECT: var bridge = MatchEngineBridge.new()
#     Reason: Godot parses type annotations at load time. If MatchEngineBridge
#             hasn't finished registering in ClassDB yet, the scene crashes.
#
#   RULE 2 — Never keep C# object in a typed variable:
#     WRONG:  @onready var bridge: MatchEngineBridge = $Bridge
#     CORRECT: @onready var bridge = $Bridge
#
#   RULE 3 — Dictionary keys are always lowercase_snake_case strings.
#     The bridge uses string literal keys. GDScript reads them the same way.
#     frame["ball_pos"] not frame["BallPos"] not frame["ballPos"]
#
#   RULE 4 — Run SimulateMatch in a Thread for non-blocking UI:
#     Simulation takes ~100ms for 90 minutes. Runs synchronously.
#     Wrap in a Thread to keep the TacticsScreen responsive.
#
#   RULE 5 — Check IsReplayReady() before calling GetFrame():
#     GetFrame() returns an empty Dictionary if called before simulation.
#     Always guard with IsReplayReady().
#
#   RULE 6 — LoadData() must complete before SimulateMatch():
#     Call LoadData once at app startup (e.g. in Main.gd _ready()).
#     Not per-match. Formation and role data loads from disk once.
# =============================================================================

# =============================================================================
# STEP 0 — Startup: bridge as AutoLoad (recommended)
# In Project Settings → AutoLoad, add MatchEngineBridge.cs as "Bridge"
# Then access it anywhere as:
#   Bridge.LoadData(...)
#   Bridge.SimulateMatch(...)
# =============================================================================

# =============================================================================
# STEP 1 — Load data (call once in Main._ready or GameState._ready)
# =============================================================================

func _ready_example():
	# ProjectSettings.globalize_path converts "res://" to absolute path
	var data_path = ProjectSettings.globalize_path("res://Data")
	Bridge.LoadData(data_path)

	if Bridge.GetLastError() != "":
		push_error("Data load failed: " + Bridge.GetLastError())
		return

	print("Engine data loaded OK")

# =============================================================================
# STEP 2 — Build team dictionaries (from TacticsScreen.gd)
# =============================================================================

func build_home_team() -> Dictionary:
	return {
		"name": "Home FC",
		"formation": "4-3-3",
		"kit_primary":   0x2266FF,   # packed RGB
		"kit_secondary": 0xFFFFFF,

		"tactics": {
			"pressing_intensity":    0.85,
			"pressing_trigger":      0.80,
			"press_compactness":     0.60,
			"defensive_line":        0.70,
			"defensive_width":       0.55,
			"defensive_aggression":  0.60,
			"possession_focus":      0.55,
			"build_up_speed":        0.75,
			"passing_directness":    0.50,
			"attacking_width":       0.65,
			"attacking_line":        0.75,
			"transition_speed":      0.90,
			"crossing_frequency":    0.40,
			"shooting_threshold":    0.45,
			"tempo":                 0.80,
			"out_of_possession_shape": 0.70,
			"in_possession_spread":  0.60,
			"freedom_level":         0.55,
			"counter_attack_focus":  0.65,
			"offside_trap_frequency": 0.40,
			"physicality_bias":      0.50,
			"set_piece_focus":       0.55,
		},

		"players": [
			# Slot 0 — GK (must match formation slot 0)
			{ "shirt_number": 1,  "name": "Keeper",    "role": "GK",
			  "base_speed": 0.45, "stamina": 0.70,
			  "passing": 0.55, "shooting": 0.10, "dribbling": 0.20,
			  "defending": 0.75, "reactions": 0.80, "ego": 0.20 },
			# Slot 1 — RB
			{ "shirt_number": 2,  "name": "Right Back", "role": "RB",
			  "base_speed": 0.55, "stamina": 0.72,
			  "passing": 0.60, "shooting": 0.20, "dribbling": 0.45,
			  "defending": 0.65, "reactions": 0.60, "ego": 0.30 },
			# Slot 2 — CB
			{ "shirt_number": 5,  "name": "Centre Back L", "role": "CB",
			  "base_speed": 0.48, "stamina": 0.68,
			  "passing": 0.55, "shooting": 0.10, "dribbling": 0.25,
			  "defending": 0.80, "reactions": 0.65, "ego": 0.25 },
			# Slot 3 — CB
			{ "shirt_number": 4,  "name": "Centre Back R", "role": "CB",
			  "base_speed": 0.48, "stamina": 0.68,
			  "passing": 0.55, "shooting": 0.10, "dribbling": 0.25,
			  "defending": 0.78, "reactions": 0.62, "ego": 0.25 },
			# Slot 4 — LB
			{ "shirt_number": 3,  "name": "Left Back", "role": "LB",
			  "base_speed": 0.55, "stamina": 0.72,
			  "passing": 0.60, "shooting": 0.20, "dribbling": 0.45,
			  "defending": 0.65, "reactions": 0.60, "ego": 0.30 },
			# Slot 5 — CM
			{ "shirt_number": 8,  "name": "CM Right", "role": "CM",
			  "base_speed": 0.55, "stamina": 0.78,
			  "passing": 0.70, "shooting": 0.40, "dribbling": 0.55,
			  "defending": 0.55, "reactions": 0.65, "ego": 0.45 },
			# Slot 6 — CDM
			{ "shirt_number": 6,  "name": "Anchor Mid", "role": "CDM",
			  "base_speed": 0.50, "stamina": 0.80,
			  "passing": 0.65, "shooting": 0.25, "dribbling": 0.40,
			  "defending": 0.72, "reactions": 0.68, "ego": 0.30 },
			# Slot 7 — CM
			{ "shirt_number": 10, "name": "CM Left", "role": "CM",
			  "base_speed": 0.55, "stamina": 0.76,
			  "passing": 0.72, "shooting": 0.45, "dribbling": 0.60,
			  "defending": 0.50, "reactions": 0.62, "ego": 0.50 },
			# Slot 8 — IW (right inverted winger)
			{ "shirt_number": 7,  "name": "Right IW", "role": "IW",
			  "base_speed": 0.65, "stamina": 0.70,
			  "passing": 0.65, "shooting": 0.68, "dribbling": 0.78,
			  "defending": 0.30, "reactions": 0.70, "ego": 0.65 },
			# Slot 9 — ST
			{ "shirt_number": 9,  "name": "Striker", "role": "ST",
			  "base_speed": 0.60, "stamina": 0.65,
			  "passing": 0.50, "shooting": 0.82, "dribbling": 0.62,
			  "defending": 0.20, "reactions": 0.72, "ego": 0.75 },
			# Slot 10 — IW (left inverted winger)
			{ "shirt_number": 11, "name": "Left IW", "role": "IW",
			  "base_speed": 0.63, "stamina": 0.70,
			  "passing": 0.68, "shooting": 0.65, "dribbling": 0.75,
			  "defending": 0.30, "reactions": 0.68, "ego": 0.60 },
		]
	}

# =============================================================================
# STEP 3 — Run simulation (typically in MatchView.gd or a loading screen)
# =============================================================================

func run_simulation_example():
	var home = build_home_team()
	var away = build_away_team()  # same structure as home
	var seed = randi()             # or a fixed seed for reproducible matches

	# Option A: blocking (freezes UI for ~100ms — fine for background loading)
	Bridge.SimulateMatch(home, away, seed)

	# Option B: in a Thread (keeps UI responsive)
	# var thread = Thread.new()
	# thread.start(func(): Bridge.SimulateMatch(home, away, seed))
	# await thread.wait_to_finish()

	if Bridge.GetLastError() != "":
		push_error("Simulation failed: " + Bridge.GetLastError())
		return

	print("Match complete: %d-%d in %d frames" % [
		Bridge.GetFinalHomeScore(),
		Bridge.GetFinalAwayScore(),
		Bridge.GetTotalFrames()
	])

# =============================================================================
# STEP 4 — ReplayPlayer.gd: read frames at playback speed
# =============================================================================

# In ReplayPlayer.gd:
#   var current_tick: int = 0
#   var playback_speed: float = 1.0   # 1x, 4x, 16x etc.
#   var ticks_per_display_frame: int = 1
#
# func _process(delta: float) -> void:
#     if not Bridge.IsReplayReady(): return
#     if current_tick >= Bridge.GetTotalFrames(): return
#
#     # At 60fps, 1x speed = 1 tick/frame (0.1s per tick = 0.1s per tick = fine)
#     # At 4x speed = 4 ticks/frame
#     var advance = int(playback_speed)
#     for i in range(advance):
#         var frame = Bridge.GetFrame(current_tick)
#         if frame.is_empty(): break
#         apply_frame(frame)
#         current_tick += 1

func apply_frame_example(frame: Dictionary) -> void:
	# Ball
	var ball_pos: Vector2 = frame["ball_pos"]
	var ball_phase: int   = frame["ball_phase"]  # 0=Owned 1=InFlight 2=Loose
	var ball_height: float = frame["ball_height"]
	# $BallDot.position = ball_pos
	# $BallDot.set_phase(ball_phase, ball_height)

	# Players (Array of 22 Dictionaries, index = PlayerId)
	var players: Array = frame["players"]
	for i in range(22):
		var p = players[i]
		var pos: Vector2   = p["position"]
		var stamina: float = p["stamina"]
		var has_ball: bool = p["has_ball"]
		var action: int    = p["action"]
		var active: bool   = p["is_active"]
		# player_dots[i].position = pos
		# player_dots[i].set_stamina_color(stamina)
		# player_dots[i].set_has_ball(has_ball)

	# Score / HUD
	var home_score: int = frame["home_score"]
	var away_score: int = frame["away_score"]
	# $HUD.update_score(home_score, away_score)

	# Events this tick (null/empty most ticks)
	var events: Array = Bridge.GetFrameEvents(frame["tick"])
	for ev in events:
		var desc: String = ev["description"]
		var ev_type: int = ev["type"]
		# EventBus.emit_signal("match_event_fired", ev)
		# $EventLog.add_line(desc)

# =============================================================================
# STEP 5 — Highlights mode: jump between events only
# =============================================================================

func load_highlights_example() -> void:
	var highlight_ticks: Array = Bridge.GetHighlightTicks()
	print("Match has %d highlight moments" % highlight_ticks.size())
	# store and iterate:
	# for tick in highlight_ticks:
	#     apply_frame(Bridge.GetFrame(tick))

# =============================================================================
# STEP 6 — PostMatch.gd: read stats and hypothesis
# =============================================================================

func show_post_match_example() -> void:
	# Team stats
	var home_stats: Dictionary = Bridge.GetTeamStats(0)
	var away_stats: Dictionary = Bridge.GetTeamStats(1)

	print("Home xG: %.2f  Away xG: %.2f" % [home_stats["xg"], away_stats["xg"]])
	print("Home PPDA: %.1f  Away PPDA: %.1f" % [home_stats["ppda"], away_stats["ppda"]])
	print("Home possession: %.0f%%" % (home_stats["possession_percent"] * 100))

	# Per-player stats (all 22)
	for i in range(22):
		var ps: Dictionary = Bridge.GetPlayerStats(i)
		if ps.is_empty(): continue
		print("P%d %s — Goals:%d Assists:%d xG:%.2f Passes:%d/%d" % [
			ps["shirt_number"], ps["name"],
			ps["goals"], ps["assists"], ps["xg"],
			ps["passes_completed"], ps["passes_attempted"]
		])

	# Hypothesis (the signature feature)
	var hyp: Dictionary = Bridge.GetHypothesis(0)  # home team
	print("\n=== HYPOTHESIS RESULT (Home) ===")
	print(hyp["headline_summary"])
	print("Overall execution: %.0f/100" % hyp["overall_execution"])
	print("Pressing  intent=%.0f%%  actual=%.0f%%  gap=%.0f%%" % [
		hyp["pressing_intent"]  * 100,
		hyp["pressing_actual"]  * 100,
		hyp["pressing_gap"]     * 100
	])
	print("  Reason: " + hyp["pressing_reason"])
	print("Possession intent=%.0f%%  actual=%.0f%%" % [
		hyp["possession_intent"] * 100,
		hyp["possession_actual"] * 100
	])
	if hyp["press_collapse_minute"] >= 0:
		print("Press collapsed at minute %d (Player %d)" % [
			hyp["press_collapse_minute"],
			hyp["press_collapse_player_id"]
		])

# =============================================================================
# STEP 7 — Speed controls (SpeedControls.gd)
# =============================================================================

# Speed multipliers map to ticks advanced per display frame:
#   1x  → advance 1 tick per _process()  (0.1 match-second per display frame)
#   4x  → advance 4 ticks per _process()
#   16x → advance 16 ticks per _process()
#   Instant → seek to Bridge.GetTotalFrames()-1 immediately

# =============================================================================
# HELPER — build_away_team (minimal example)
# =============================================================================

func build_away_team() -> Dictionary:
	return {
		"name": "Away FC",
		"formation": "4-2-3-1",
		"kit_primary":   0xFF3333,
		"kit_secondary": 0xFFFFFF,
		"tactics": {
			"pressing_intensity": 0.40,
			"defensive_line":     0.35,
			"possession_focus":   0.60,
			"tempo":              0.55,
			"freedom_level":      0.50,
		},
		"players": [
			{ "shirt_number": 1,  "name": "GK",   "role": "GK",
			  "base_speed": 0.45, "stamina": 0.70, "passing": 0.55,
			  "shooting": 0.10, "dribbling": 0.20, "defending": 0.75,
			  "reactions": 0.78, "ego": 0.20 },
			{ "shirt_number": 2,  "name": "RB",   "role": "RB",
			  "base_speed": 0.55, "stamina": 0.70, "passing": 0.58,
			  "shooting": 0.20, "dribbling": 0.42, "defending": 0.64,
			  "reactions": 0.58, "ego": 0.28 },
			{ "shirt_number": 5,  "name": "CB1",  "role": "CB",
			  "base_speed": 0.48, "stamina": 0.68, "passing": 0.53,
			  "shooting": 0.10, "dribbling": 0.22, "defending": 0.79,
			  "reactions": 0.63, "ego": 0.22 },
			{ "shirt_number": 4,  "name": "CB2",  "role": "CB",
			  "base_speed": 0.48, "stamina": 0.68, "passing": 0.53,
			  "shooting": 0.10, "dribbling": 0.22, "defending": 0.77,
			  "reactions": 0.60, "ego": 0.22 },
			{ "shirt_number": 3,  "name": "LB",   "role": "LB",
			  "base_speed": 0.55, "stamina": 0.70, "passing": 0.58,
			  "shooting": 0.20, "dribbling": 0.42, "defending": 0.64,
			  "reactions": 0.58, "ego": 0.28 },
			{ "shirt_number": 6,  "name": "CDM1", "role": "CDM",
			  "base_speed": 0.52, "stamina": 0.80, "passing": 0.65,
			  "shooting": 0.22, "dribbling": 0.38, "defending": 0.70,
			  "reactions": 0.66, "ego": 0.28 },
			{ "shirt_number": 8,  "name": "CDM2", "role": "CDM",
			  "base_speed": 0.52, "stamina": 0.78, "passing": 0.63,
			  "shooting": 0.22, "dribbling": 0.38, "defending": 0.68,
			  "reactions": 0.64, "ego": 0.28 },
			{ "shirt_number": 7,  "name": "WF R", "role": "WF",
			  "base_speed": 0.63, "stamina": 0.68, "passing": 0.62,
			  "shooting": 0.58, "dribbling": 0.72, "defending": 0.28,
			  "reactions": 0.68, "ego": 0.58 },
			{ "shirt_number": 10, "name": "AM",   "role": "AM",
			  "base_speed": 0.58, "stamina": 0.72, "passing": 0.72,
			  "shooting": 0.60, "dribbling": 0.65, "defending": 0.35,
			  "reactions": 0.68, "ego": 0.55 },
			{ "shirt_number": 11, "name": "WF L", "role": "WF",
			  "base_speed": 0.63, "stamina": 0.68, "passing": 0.62,
			  "shooting": 0.55, "dribbling": 0.70, "defending": 0.28,
			  "reactions": 0.65, "ego": 0.55 },
			{ "shirt_number": 9,  "name": "ST",   "role": "ST",
			  "base_speed": 0.62, "stamina": 0.65, "passing": 0.50,
			  "shooting": 0.80, "dribbling": 0.60, "defending": 0.20,
			  "reactions": 0.70, "ego": 0.72 },
		]
	}
