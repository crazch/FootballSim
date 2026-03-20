# =============================================================================
# Module:  PostMatch.gd
# Path:    FootballSim/Scripts/Screens/PostMatch.gd
# Purpose:
#   Post-match analysis screen. Reads all pre-computed stats from Bridge and
#   displays them in three tabbed panels:
#     Tab 0 — Result        : scoreline, team stats side-by-side, xG, possession
#     Tab 1 — Players       : per-player stat table for both squads
#     Tab 2 — Hypothesis    : the signature feature — tactical intent vs execution
#
#   PostMatch is READ-ONLY. It never calls SimulateMatch or modifies any state.
#   It calls Bridge.GetTeamStats(), Bridge.GetPlayerStats(), Bridge.GetHypothesis().
#   When the user is done it calls GameState.clear_match() then either:
#     "Play Again" → EventBus.go_to_tactics.emit()
#     "Main Menu"  → EventBus.go_to_main_menu.emit()
#
# Scene tree overview (see PostMatch.tscn for exact hierarchy):
#
#   PostMatch (Control)                             ← this script
#     └── Root (MarginContainer)
#           └── MainVBox (VBoxContainer)
#                 ├── ResultHeader (PanelContainer)  scoreline banner
#                 ├── TabContainer
#                 │     ├── ResultTab (ScrollContainer)
#                 │     ├── PlayersTab (ScrollContainer)
#                 │     └── HypothesisTab (ScrollContainer)
#                 └── ButtonRow (HBoxContainer)
#                       ├── PlayAgainButton
#                       └── MainMenuButton
#
# @export slots (assign in Inspector):
#   result_home_label, result_away_label, result_score_label
#   tab_container
#   result_scroll, players_scroll, hypothesis_scroll
#   play_again_button, main_menu_button
#
# All content inside each tab is built programmatically in _ready()
# so no additional node assignments are needed beyond the scroll containers.
#
# Strict typing: no := on arrays / get / bridge / math results.
# =============================================================================

extends Control

# ── Hypothesis dimension prefixes (must match Bridge.GetHypothesis keys) ─────
const HYPO_DIMS : Array[String] = [
	"pressing_", "possession_", "high_line_",
	"passing_style_", "attacking_width_", "tempo_", "shape_held_"
]

const HYPO_LABELS : Array[String] = [
	"Pressing", "Possession", "Defensive Line",
	"Passing Style", "Attacking Width", "Tempo", "Shape Discipline"
]

# ── Colours ───────────────────────────────────────────────────────────────────
const COLOR_HOME         : Color = Color(0.30, 0.60, 1.00, 1.0)
const COLOR_AWAY         : Color = Color(1.00, 0.40, 0.40, 1.0)
const COLOR_GOOD         : Color = Color(0.30, 0.90, 0.40, 1.0)  # execution ≥ 0.80
const COLOR_MID          : Color = Color(1.00, 0.85, 0.25, 1.0)  # execution 0.60–0.80
const COLOR_BAD          : Color = Color(1.00, 0.35, 0.35, 1.0)  # execution < 0.60
const COLOR_HEADER_BG    : Color = Color(0.08, 0.08, 0.10, 1.0)
const COLOR_ROW_EVEN     : Color = Color(0.10, 0.10, 0.12, 0.85)
const COLOR_ROW_ODD      : Color = Color(0.07, 0.07, 0.09, 0.85)
const COLOR_SECTION_HEAD : Color = Color(0.55, 0.55, 0.60, 1.0)

# ── @exports ──────────────────────────────────────────────────────────────────

@export var result_home_label  : Label          = null
@export var result_away_label  : Label          = null
@export var result_score_label : Label          = null
@export var tab_container      : TabContainer   = null
@export var result_scroll      : ScrollContainer = null
@export var players_scroll     : ScrollContainer = null
@export var hypothesis_scroll  : ScrollContainer = null
@export var play_again_button  : Button         = null
@export var main_menu_button   : Button         = null

# ── State ─────────────────────────────────────────────────────────────────────

var _home_stats  : Dictionary = {}
var _away_stats  : Dictionary = {}
var _home_hypo   : Dictionary = {}
var _away_hypo   : Dictionary = {}
var _home_kit    : Color      = COLOR_HOME
var _away_kit    : Color      = COLOR_AWAY

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	_load_data()
	_build_result_header()
	_build_result_tab()
	_build_players_tab()
	_build_hypothesis_tab()
	_connect_buttons()


func _load_data() -> void:
	_home_stats = Bridge.GetTeamStats(0)
	_away_stats = Bridge.GetTeamStats(1)
	_home_hypo  = Bridge.GetHypothesis(0)
	_away_hypo  = Bridge.GetHypothesis(1)

	# Unpack kit colours from GameState for tinted headers
	var home_kit_int : int = GameState.home_team.get("kit_primary", 0x2266FF)
	var away_kit_int : int = GameState.away_team.get("kit_primary", 0xFF3333)
	_home_kit = _unpack_color(home_kit_int)
	_away_kit = _unpack_color(away_kit_int)

# ── Result header ─────────────────────────────────────────────────────────────

func _build_result_header() -> void:
	var home_name  : String = _home_stats.get("name", "Home")
	var away_name  : String = _away_stats.get("name", "Away")
	var home_goals : int    = _home_stats.get("goals_scored", 0)
	var away_goals : int    = _away_stats.get("goals_scored", 0)

	if result_home_label != null:
		result_home_label.text     = home_name
		result_home_label.modulate = _home_kit

	if result_away_label != null:
		result_away_label.text     = away_name
		result_away_label.modulate = _away_kit

	if result_score_label != null:
		result_score_label.text = "%d  –  %d" % [home_goals, away_goals]

# =============================================================================
# TAB 0 — RESULT
# Two-column team stat comparison.
# =============================================================================

func _build_result_tab() -> void:
	if result_scroll == null:
		return
	var vbox : VBoxContainer = VBoxContainer.new()
	vbox.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	vbox.add_theme_constant_override("separation", 4)
	result_scroll.add_child(vbox)

	# ── Team stat rows ────────────────────────────────────────────────────────
	_add_result_section(vbox, "POSSESSION & SHOTS")
	_add_stat_row(vbox, "Possession",
		"%.0f%%" % (_home_stats.get("possession_percent", 0.5) * 100.0),
		"%.0f%%" % (_away_stats.get("possession_percent", 0.5) * 100.0))
	_add_stat_row(vbox, "Shots",
		str(_home_stats.get("shots_total", 0)),
		str(_away_stats.get("shots_total", 0)))
	_add_stat_row(vbox, "Shots on Target",
		str(_home_stats.get("shots_on_target", 0)),
		str(_away_stats.get("shots_on_target", 0)))
	_add_stat_row(vbox, "xG",
		"%.2f" % _home_stats.get("xg", 0.0),
		"%.2f" % _away_stats.get("xg", 0.0))
	_add_stat_row(vbox, "xGA",
		"%.2f" % _home_stats.get("xga", 0.0),
		"%.2f" % _away_stats.get("xga", 0.0))

	_add_result_section(vbox, "PASSING")
	_add_stat_row(vbox, "Passes",
		"%d / %d" % [_home_stats.get("passes_completed", 0), _home_stats.get("passes_attempted", 0)],
		"%d / %d" % [_away_stats.get("passes_completed", 0), _away_stats.get("passes_attempted", 0)])
	_add_stat_row(vbox, "Pass Accuracy",
		"%.0f%%" % (_home_stats.get("pass_accuracy", 0.0) * 100.0),
		"%.0f%%" % (_away_stats.get("pass_accuracy", 0.0) * 100.0))
	_add_stat_row(vbox, "Progressive Passes",
		str(_home_stats.get("progressive_passes", 0)),
		str(_away_stats.get("progressive_passes", 0)))
	_add_stat_row(vbox, "Key Passes",
		str(_home_stats.get("key_passes", 0)),
		str(_away_stats.get("key_passes", 0)))

	_add_result_section(vbox, "PRESSING")
	_add_stat_row(vbox, "PPDA",
		"%.1f" % _home_stats.get("ppda", 99.0),
		"%.1f" % _away_stats.get("ppda", 99.0))
	_add_stat_row(vbox, "Pressures",
		"%d / %d" % [_home_stats.get("pressures_successful", 0), _home_stats.get("pressures_attempted", 0)],
		"%d / %d" % [_away_stats.get("pressures_successful", 0), _away_stats.get("pressures_attempted", 0)])
	_add_stat_row(vbox, "Tackles Won",
		"%d / %d" % [_home_stats.get("tackles_won", 0), _home_stats.get("tackles_attempted", 0)],
		"%d / %d" % [_away_stats.get("tackles_won", 0), _away_stats.get("tackles_attempted", 0)])
	_add_stat_row(vbox, "Interceptions",
		str(_home_stats.get("interceptions", 0)),
		str(_away_stats.get("interceptions", 0)))

	_add_result_section(vbox, "DISCIPLINE")
	_add_stat_row(vbox, "Fouls",
		str(_home_stats.get("fouls_committed", 0)),
		str(_away_stats.get("fouls_committed", 0)))
	_add_stat_row(vbox, "Yellow Cards",
		str(_home_stats.get("yellow_cards", 0)),
		str(_away_stats.get("yellow_cards", 0)))
	_add_stat_row(vbox, "Red Cards",
		str(_home_stats.get("red_cards", 0)),
		str(_away_stats.get("red_cards", 0)))
	_add_stat_row(vbox, "Corners",
		str(_home_stats.get("corners_won", 0)),
		str(_away_stats.get("corners_won", 0)))

	_add_result_section(vbox, "PHYSICAL")
	_add_stat_row(vbox, "Distance (km)",
		"%.1f" % (_home_stats.get("total_distance", 0.0) / 100.0),
		"%.1f" % (_away_stats.get("total_distance", 0.0) / 100.0))
	_add_stat_row(vbox, "Sprint Distance (km)",
		"%.1f" % (_home_stats.get("total_sprint_distance", 0.0) / 100.0),
		"%.1f" % (_away_stats.get("total_sprint_distance", 0.0) / 100.0))


func _add_result_section(parent: VBoxContainer, title: String) -> void:
	var lbl : Label = Label.new()
	lbl.text     = title
	lbl.modulate = COLOR_SECTION_HEAD
	lbl.add_theme_font_size_override("font_size", 11)
	parent.add_child(lbl)


func _add_stat_row(parent: VBoxContainer, label: String,
				   home_val: String, away_val: String) -> void:
	var row : HBoxContainer = HBoxContainer.new()
	row.add_theme_constant_override("separation", 4)

	var home_lbl : Label = Label.new()
	home_lbl.text                  = home_val
	home_lbl.custom_minimum_size   = Vector2(90.0, 0.0)
	home_lbl.horizontal_alignment  = HORIZONTAL_ALIGNMENT_RIGHT
	home_lbl.modulate              = _home_kit.lightened(0.3)
	home_lbl.add_theme_font_size_override("font_size", 12)

	var mid_lbl : Label = Label.new()
	mid_lbl.text                  = label
	mid_lbl.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	mid_lbl.horizontal_alignment  = HORIZONTAL_ALIGNMENT_CENTER
	mid_lbl.add_theme_font_size_override("font_size", 11)
	mid_lbl.modulate              = Color(0.75, 0.75, 0.75, 1.0)

	var away_lbl : Label = Label.new()
	away_lbl.text                 = away_val
	away_lbl.custom_minimum_size  = Vector2(90.0, 0.0)
	away_lbl.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	away_lbl.modulate             = _away_kit.lightened(0.3)
	away_lbl.add_theme_font_size_override("font_size", 12)

	row.add_child(home_lbl)
	row.add_child(mid_lbl)
	row.add_child(away_lbl)
	parent.add_child(row)

# =============================================================================
# TAB 1 — PLAYERS
# Per-player stat table: both squads shown, home first.
# Columns: #, Name, Goals, Ast, xG, Shots, Passes, Acc%, Tackles, Intercept, Dist
# =============================================================================

func _build_players_tab() -> void:
	if players_scroll == null:
		return
	var vbox : VBoxContainer = VBoxContainer.new()
	vbox.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	vbox.add_theme_constant_override("separation", 2)
	players_scroll.add_child(vbox)

	_add_players_section(vbox, "HOME SQUAD", 0,  11, _home_kit)
	_add_players_section(vbox, "AWAY SQUAD", 11, 22, _away_kit)


func _add_players_section(parent: VBoxContainer, title: String,
						   start_id: int, end_id: int, kit_color: Color) -> void:
	# Section header
	var header_lbl : Label = Label.new()
	header_lbl.text     = title
	header_lbl.modulate = kit_color
	header_lbl.add_theme_font_size_override("font_size", 13)
	parent.add_child(header_lbl)

	# Column header row
	_add_player_row(parent,
		"#", "Name", "G", "A", "xG", "Sh", "Pass", "Acc", "Tkl", "Int", "Dist",
		Color(0.6, 0.6, 0.6, 1.0), true)

	# Player rows
	for i : int in range(start_id, end_id):
		var ps : Dictionary = Bridge.GetPlayerStats(i)
		if ps.is_empty():
			continue

		var shirt   : String = str(ps.get("shirt_number", i + 1))
		var name    : String = ps.get("name", "?")
		var goals   : String = str(ps.get("goals", 0))
		var assists : String = str(ps.get("assists", 0))
		var xg      : String = "%.2f" % ps.get("xg", 0.0)
		var shots   : String = "%d/%d" % [ps.get("shots_on_target", 0), ps.get("shots_attempted", 0)]
		var passes  : String = "%d/%d" % [ps.get("passes_completed", 0), ps.get("passes_attempted", 0)]
		var acc     : String = "%.0f%%" % (ps.get("pass_accuracy", 0.0) * 100.0)
		var tackles : String = "%d/%d" % [ps.get("tackles_won", 0), ps.get("tackles_attempted", 0)]
		var interc  : String = str(ps.get("interceptions", 0))
		var dist    : String = "%.1f" % (ps.get("distance_covered", 0.0) / 100.0)

		var row_color : Color = kit_color.darkened(0.4)
		_add_player_row(parent,
			shirt, name, goals, assists, xg, shots, passes, acc, tackles, interc, dist,
			row_color, false)

	# Spacer between squads
	var spacer : Control = Control.new()
	spacer.custom_minimum_size = Vector2(0.0, 10.0)
	parent.add_child(spacer)


func _add_player_row(parent: VBoxContainer,
					  shirt: String, name: String, goals: String, assists: String,
					  xg: String, shots: String, passes: String, acc: String,
					  tackles: String, interc: String, dist: String,
					  bg_color: Color, is_header: bool) -> void:
	var row : HBoxContainer = HBoxContainer.new()
	row.add_theme_constant_override("separation", 2)

	# Background panel
	var panel : PanelContainer = PanelContainer.new()
	panel.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	panel.modulate              = Color(bg_color.r, bg_color.g, bg_color.b,
									   0.60 if is_header else 0.45)

	var inner : HBoxContainer = HBoxContainer.new()
	inner.add_theme_constant_override("separation", 4)

	var font_size : int = 11 if not is_header else 10

	# Column widths: shirt, name, G, A, xG, Sh, Pass, Acc, Tkl, Int, Dist
	var widths : Array[float] = [22.0, 90.0, 22.0, 22.0, 40.0, 55.0, 65.0, 40.0, 55.0, 30.0, 45.0]
	var values : Array[String] = [shirt, name, goals, assists, xg, shots, passes, acc, tackles, interc, dist]
	var alignments : Array[int] = [
		HORIZONTAL_ALIGNMENT_CENTER,
		HORIZONTAL_ALIGNMENT_LEFT,
		HORIZONTAL_ALIGNMENT_CENTER,
		HORIZONTAL_ALIGNMENT_CENTER,
		HORIZONTAL_ALIGNMENT_CENTER,
		HORIZONTAL_ALIGNMENT_CENTER,
		HORIZONTAL_ALIGNMENT_CENTER,
		HORIZONTAL_ALIGNMENT_CENTER,
		HORIZONTAL_ALIGNMENT_CENTER,
		HORIZONTAL_ALIGNMENT_CENTER,
		HORIZONTAL_ALIGNMENT_RIGHT,
	]

	for j : int in range(values.size()):
		var lbl : Label = Label.new()
		lbl.text                  = values[j]
		lbl.custom_minimum_size   = Vector2(widths[j], 0.0)
		lbl.horizontal_alignment  = alignments[j]
		lbl.add_theme_font_size_override("font_size", font_size)
		if is_header:
			lbl.modulate = Color(0.7, 0.7, 0.7, 1.0)
		inner.add_child(lbl)

	panel.add_child(inner)
	parent.add_child(panel)

# =============================================================================
# TAB 2 — HYPOTHESIS
# Tactical intent vs execution for both teams.
# The signature feature of the project.
# =============================================================================

func _build_hypothesis_tab() -> void:
	if hypothesis_scroll == null:
		return
	var vbox : VBoxContainer = VBoxContainer.new()
	vbox.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	vbox.add_theme_constant_override("separation", 10)
	hypothesis_scroll.add_child(vbox)

	_build_hypothesis_team(vbox, _home_hypo, _home_stats.get("name", "Home"),
						   _home_kit, 0)

	# Divider
	var sep : HSeparator = HSeparator.new()
	vbox.add_child(sep)

	_build_hypothesis_team(vbox, _away_hypo, _away_stats.get("name", "Away"),
						   _away_kit, 1)


func _build_hypothesis_team(parent: VBoxContainer, hypo: Dictionary,
							 team_name: String, kit_color: Color,
							 _team_id: int) -> void:
	if hypo.is_empty():
		var empty_lbl : Label = Label.new()
		empty_lbl.text = "%s — No hypothesis data." % team_name
		parent.add_child(empty_lbl)
		return

	# Team header
	var team_lbl : Label = Label.new()
	team_lbl.text     = team_name.to_upper()
	team_lbl.modulate = kit_color
	team_lbl.add_theme_font_size_override("font_size", 16)
	parent.add_child(team_lbl)

	# Overall execution score
	var overall : float  = hypo.get("overall_execution", 0.0)
	var exec_lbl : Label = Label.new()
	exec_lbl.text     = "Overall Execution: %.0f / 100" % overall
	exec_lbl.modulate = _execution_color(overall / 100.0)
	exec_lbl.add_theme_font_size_override("font_size", 14)
	parent.add_child(exec_lbl)

	# Press collapse banner (if applicable)
	var collapse_min : int = hypo.get("press_collapse_minute", -1)
	if collapse_min >= 0:
		var collapse_lbl : Label = Label.new()
		collapse_lbl.text     = "⚠ Press collapsed at minute %d" % collapse_min
		collapse_lbl.modulate = COLOR_BAD
		collapse_lbl.add_theme_font_size_override("font_size", 12)
		parent.add_child(collapse_lbl)

	# Headline summary
	var headline : String = hypo.get("headline_summary", "")
	if not headline.is_empty():
		var headline_lbl : Label = Label.new()
		headline_lbl.text         = headline
		headline_lbl.autowrap_mode = TextServer.AUTOWRAP_WORD
		headline_lbl.modulate     = Color(0.80, 0.80, 0.80, 1.0)
		headline_lbl.add_theme_font_size_override("font_size", 11)
		parent.add_child(headline_lbl)

	# Per-dimension bars
	var dims_label : Label = Label.new()
	dims_label.text     = "TACTICAL DIMENSIONS"
	dims_label.modulate = COLOR_SECTION_HEAD
	dims_label.add_theme_font_size_override("font_size", 11)
	parent.add_child(dims_label)

	for d : int in range(HYPO_DIMS.size()):
		var prefix    : String = HYPO_DIMS[d]
		var dim_label : String = HYPO_LABELS[d]
		var intent    : float  = hypo.get(prefix + "intent",    0.5)
		var actual    : float  = hypo.get(prefix + "actual",    0.5)
		var execution : float  = hypo.get(prefix + "execution", 0.5)
		var reason    : String = hypo.get(prefix + "reason",    "")

		_add_dimension_row(parent, dim_label, intent, actual, execution, reason, kit_color)


func _add_dimension_row(parent: VBoxContainer, label: String,
						intent: float, actual: float, execution: float,
						reason: String, kit_color: Color) -> void:
	var container : VBoxContainer = VBoxContainer.new()
	container.add_theme_constant_override("separation", 2)

	# Row header: label + execution score
	var header_row : HBoxContainer = HBoxContainer.new()

	var name_lbl : Label = Label.new()
	name_lbl.text                  = label
	name_lbl.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	name_lbl.add_theme_font_size_override("font_size", 12)

	var exec_lbl : Label = Label.new()
	exec_lbl.text     = "%.0f%%" % (execution * 100.0)
	exec_lbl.modulate = _execution_color(execution)
	exec_lbl.add_theme_font_size_override("font_size", 12)
	exec_lbl.custom_minimum_size = Vector2(40.0, 0.0)
	exec_lbl.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT

	header_row.add_child(name_lbl)
	header_row.add_child(exec_lbl)
	container.add_child(header_row)

	# Double progress bar: intent (ghost) and actual (solid)
	var bar_bg : Panel = Panel.new()
	bar_bg.custom_minimum_size = Vector2(0.0, 12.0)
	bar_bg.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	# We draw inside using a script approach — use SubViewport alternative:
	# Simpler: use two nested ColorRect bars
	var bar_container : Control = Control.new()
	bar_container.custom_minimum_size = Vector2(0.0, 12.0)
	bar_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL

	# Background
	var bg_rect : ColorRect = ColorRect.new()
	bg_rect.set_anchors_preset(Control.PRESET_FULL_RECT)
	bg_rect.color = Color(0.15, 0.15, 0.17, 1.0)
	bar_container.add_child(bg_rect)

	# Intent ghost bar
	var intent_rect : ColorRect = ColorRect.new()
	intent_rect.anchor_left   = 0.0
	intent_rect.anchor_right  = clampf(intent, 0.0, 1.0)
	intent_rect.anchor_top    = 0.0
	intent_rect.anchor_bottom = 1.0
	intent_rect.color = Color(kit_color.r, kit_color.g, kit_color.b, 0.25)
	bar_container.add_child(intent_rect)

	# Actual solid bar
	var actual_rect : ColorRect = ColorRect.new()
	actual_rect.anchor_left   = 0.0
	actual_rect.anchor_right  = clampf(actual, 0.0, 1.0)
	actual_rect.anchor_top    = 0.0
	actual_rect.anchor_bottom = 1.0
	actual_rect.color = Color(kit_color.r, kit_color.g, kit_color.b,
							  0.80 * _execution_alpha(execution))
	bar_container.add_child(actual_rect)

	container.add_child(bar_container)

	# Intent vs actual numbers + reason
	var numbers_lbl : Label = Label.new()
	numbers_lbl.text     = "Intent %.0f%%  →  Actual %.0f%%  (gap %+.0f%%)" % [
		intent * 100.0, actual * 100.0, (actual - intent) * 100.0
	]
	numbers_lbl.modulate = Color(0.65, 0.65, 0.65, 1.0)
	numbers_lbl.add_theme_font_size_override("font_size", 10)
	container.add_child(numbers_lbl)

	if not reason.is_empty():
		var reason_lbl : Label = Label.new()
		reason_lbl.text          = reason
		reason_lbl.autowrap_mode = TextServer.AUTOWRAP_WORD
		reason_lbl.modulate      = Color(0.55, 0.55, 0.55, 1.0)
		reason_lbl.add_theme_font_size_override("font_size", 10)
		container.add_child(reason_lbl)

	parent.add_child(container)

# ── Buttons ───────────────────────────────────────────────────────────────────

func _connect_buttons() -> void:
	if play_again_button != null:
		play_again_button.pressed.connect(_on_play_again)
	if main_menu_button != null:
		main_menu_button.pressed.connect(_on_main_menu)


func _on_play_again() -> void:
	GameState.clear_match()
	EventBus.go_to_tactics.emit()


func _on_main_menu() -> void:
	GameState.clear_match()
	EventBus.go_to_main_menu.emit()

# ── Helpers ───────────────────────────────────────────────────────────────────

func _execution_color(execution: float) -> Color:
	if execution >= 0.80:
		return COLOR_GOOD
	elif execution >= 0.60:
		return COLOR_MID
	else:
		return COLOR_BAD


func _execution_alpha(execution: float) -> float:
	return clampf(0.4 + execution * 0.6, 0.4, 1.0)


func _unpack_color(packed: int) -> Color:
	var r : float = float((packed >> 16) & 0xFF) / 255.0
	var g : float = float((packed >>  8) & 0xFF) / 255.0
	var b : float = float( packed        & 0xFF)  / 255.0
	return Color(r, g, b, 1.0)
