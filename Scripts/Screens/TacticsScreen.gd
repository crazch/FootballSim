# =============================================================================
# Module:  TacticsScreen.gd
# Path:    FootballSim/Scripts/Screens/TacticsScreen.gd
# Purpose:
#   Pre-match setup screen. Each team column now has two tabs:
#     Tab 0 "Tactics"  — formation, style preset, 22 tactical sliders
#     Tab 1 "Squad"    — editable table of 11 players (name, role, attributes)
#
# Squad editor (new):
#   One row per player. Each row has:
#     [Shirt#] [Name LineEdit] [Role OptionButton]
#     [Spd] [Stam] [Pas] [Sht] [Dri] [Def] [Rea] [Ego]  ← 8 SpinBoxes 0.00–1.00
#   Formation dropdown change rebuilds the squad rows from the matching template.
#   Individual edits override the template values in the live squad array.
#   Kick Off assembles final dict from live squad, not from the template directly.
#
# Strict typing: no := on arrays / get / math / bridge results.
# =============================================================================

extends Control

# ── Formations ────────────────────────────────────────────────────────────────
const FORMATIONS : Array[String] = ["4-3-3", "4-2-3-1", "4-4-2", "3-5-2", "5-4-1"]

# ── All valid role strings (must match C# PlayerRole enum names exactly) ──────
const ROLES : Array[String] = [
	"GK","SK",
	"CB","BPD","WB","RB","LB",
	"CDM","DLP",
	"CM","BBM",
	"AM","IW","WF",
	"CF","ST","PF"
]

# ── Slider defs: [dict_key, display_label, default_value] ────────────────────
const SLIDER_DEFS : Array = [
	["pressing_intensity",     "Pressing Intensity",   0.50],
	["pressing_trigger",       "Press Trigger",        0.50],
	["press_compactness",      "Press Compactness",    0.50],
	["defensive_line",         "Defensive Line",       0.50],
	["defensive_width",        "Defensive Width",      0.50],
	["defensive_aggression",   "Def Aggression",       0.50],
	["possession_focus",       "Possession Focus",     0.50],
	["build_up_speed",         "Build-Up Speed",       0.50],
	["passing_directness",     "Pass Directness",      0.50],
	["attacking_width",        "Attack Width",         0.50],
	["attacking_line",         "Attack Line",          0.50],
	["transition_speed",       "Transition Speed",     0.50],
	["crossing_frequency",     "Crossing",             0.50],
	["shooting_threshold",     "Shoot Threshold",      0.50],
	["tempo",                  "Tempo",                0.50],
	["out_of_possession_shape","OOP Shape",            0.50],
	["in_possession_spread",   "IP Spread",            0.50],
	["freedom_level",          "Freedom Level",        0.50],
	["counter_attack_focus",   "Counter Focus",        0.50],
	["offside_trap_frequency", "Offside Trap",         0.20],
	["physicality_bias",       "Physicality",          0.50],
	["set_piece_focus",        "Set Pieces",           0.50],
]

# ── Attribute columns for squad editor: [dict_key, short_label] ──────────────
const ATTR_COLS : Array = [
	["base_speed", "Spd"],
	["stamina",    "Sta"],
	["passing",    "Pas"],
	["shooting",   "Sht"],
	["dribbling",  "Dri"],
	["defending",  "Def"],
	["reactions",  "Rea"],
	["ego",        "Ego"],
]

# ── Presets ───────────────────────────────────────────────────────────────────
const PRESETS : Array = [
	["— Custom —",        {}],
	["Gegenpress",        {"pressing_intensity":0.95,"pressing_trigger":0.90,"press_compactness":0.75,
						   "defensive_line":0.80,"defensive_aggression":0.70,"possession_focus":0.55,
						   "build_up_speed":0.85,"passing_directness":0.55,"attacking_width":0.60,
						   "attacking_line":0.80,"transition_speed":0.95,"tempo":0.90,
						   "out_of_possession_shape":0.80,"freedom_level":0.55,"counter_attack_focus":0.70}],
	["Possession",        {"pressing_intensity":0.40,"pressing_trigger":0.40,"possession_focus":0.90,
						   "build_up_speed":0.30,"passing_directness":0.15,"attacking_width":0.80,
						   "tempo":0.40,"freedom_level":0.65,"in_possession_spread":0.80,
						   "defensive_line":0.55,"out_of_possession_shape":0.65}],
	["Wing Play",         {"pressing_intensity":0.55,"attacking_width":0.95,"crossing_frequency":0.90,
						   "in_possession_spread":0.90,"attacking_line":0.65,"transition_speed":0.70,
						   "tempo":0.65,"freedom_level":0.60}],
	["Direct Counter",    {"pressing_intensity":0.20,"defensive_line":0.15,"out_of_possession_shape":0.85,
						   "passing_directness":0.85,"transition_speed":0.95,"counter_attack_focus":0.90,
						   "tempo":0.75,"defensive_aggression":0.55}],
	["Park the Bus",      {"pressing_intensity":0.05,"defensive_line":0.05,"defensive_width":0.15,
						   "out_of_possession_shape":0.95,"tempo":0.20,"possession_focus":0.30,
						   "counter_attack_focus":0.20,"freedom_level":0.25}],
	["High Press",        {"pressing_intensity":0.90,"pressing_trigger":0.85,"press_compactness":0.85,
						   "defensive_line":0.75,"defensive_aggression":0.75,"tempo":0.85,
						   "out_of_possession_shape":0.80,"transition_speed":0.80}],
	["Tiki-Taka",         {"pressing_intensity":0.60,"possession_focus":0.95,"build_up_speed":0.25,
						   "passing_directness":0.05,"freedom_level":0.85,"tempo":0.50,
						   "in_possession_spread":0.70,"attacking_width":0.65}],
	["Long Ball",         {"passing_directness":0.90,"physicality_bias":0.90,"attacking_line":0.85,
						   "transition_speed":0.80,"tempo":0.70,"build_up_speed":0.80,
						   "crossing_frequency":0.60,"attacking_width":0.55}],
	["Balanced",          {"pressing_intensity":0.50,"defensive_line":0.50,"possession_focus":0.50,
						   "tempo":0.50,"freedom_level":0.50,"attacking_width":0.55,
						   "passing_directness":0.45}],
]

# ── Default squad templates: [shirt, name, role, spd, sta, pas, sht, dri, def, rea, ego]
const SQUAD_433 : Array = [
	[1,"GK","GK",    0.45,0.70, 0.55,0.10,0.20,0.75,0.80,0.20],
	[2,"RB","RB",    0.55,0.72, 0.60,0.20,0.45,0.65,0.60,0.30],
	[5,"CBL","CB",   0.48,0.68, 0.55,0.10,0.25,0.80,0.65,0.25],
	[4,"CBR","CB",   0.48,0.68, 0.55,0.10,0.25,0.78,0.62,0.25],
	[3,"LB","LB",    0.55,0.72, 0.60,0.20,0.45,0.65,0.60,0.30],
	[8,"CMR","CM",   0.55,0.78, 0.70,0.40,0.55,0.55,0.65,0.45],
	[6,"CDM","CDM",  0.50,0.80, 0.65,0.25,0.40,0.72,0.68,0.30],
	[10,"CML","CM",  0.55,0.76, 0.72,0.45,0.60,0.50,0.62,0.50],
	[7,"RW","IW",    0.65,0.70, 0.65,0.68,0.78,0.30,0.70,0.65],
	[9,"ST","ST",    0.60,0.65, 0.50,0.82,0.62,0.20,0.72,0.75],
	[11,"LW","IW",   0.63,0.70, 0.68,0.65,0.75,0.30,0.68,0.60],
]
const SQUAD_4231 : Array = [
	[1,"GK","GK",    0.45,0.70, 0.55,0.10,0.20,0.75,0.78,0.20],
	[2,"RB","RB",    0.55,0.70, 0.58,0.20,0.42,0.64,0.58,0.28],
	[5,"CB1","CB",   0.48,0.68, 0.53,0.10,0.22,0.79,0.63,0.22],
	[4,"CB2","CB",   0.48,0.68, 0.53,0.10,0.22,0.77,0.60,0.22],
	[3,"LB","LB",    0.55,0.70, 0.58,0.20,0.42,0.64,0.58,0.28],
	[6,"CDM1","CDM", 0.52,0.80, 0.65,0.22,0.38,0.70,0.66,0.28],
	[8,"CDM2","CDM", 0.52,0.78, 0.63,0.22,0.38,0.68,0.64,0.28],
	[7,"RWF","WF",   0.63,0.68, 0.62,0.58,0.72,0.28,0.68,0.58],
	[10,"AM","AM",   0.58,0.72, 0.72,0.60,0.65,0.35,0.68,0.55],
	[11,"LWF","WF",  0.63,0.68, 0.62,0.55,0.70,0.28,0.65,0.55],
	[9,"ST","ST",    0.62,0.65, 0.50,0.80,0.60,0.20,0.70,0.72],
]
const SQUAD_442 : Array = [
	[1,"GK","GK",    0.45,0.70, 0.55,0.10,0.20,0.75,0.78,0.20],
	[2,"RB","RB",    0.55,0.72, 0.60,0.20,0.45,0.65,0.60,0.28],
	[5,"CB1","CB",   0.48,0.68, 0.55,0.10,0.25,0.80,0.65,0.25],
	[4,"CB2","CB",   0.48,0.68, 0.55,0.10,0.25,0.78,0.62,0.25],
	[3,"LB","LB",    0.55,0.72, 0.60,0.20,0.45,0.65,0.60,0.28],
	[7,"RM","WF",    0.62,0.70, 0.62,0.50,0.65,0.35,0.65,0.52],
	[8,"CMR","CM",   0.55,0.78, 0.68,0.38,0.52,0.55,0.63,0.45],
	[6,"CML","CM",   0.55,0.76, 0.68,0.38,0.52,0.55,0.63,0.45],
	[11,"LM","WF",   0.62,0.70, 0.62,0.50,0.65,0.35,0.65,0.52],
	[9,"ST1","ST",   0.60,0.65, 0.52,0.80,0.60,0.22,0.70,0.72],
	[10,"ST2","ST",  0.60,0.65, 0.52,0.78,0.60,0.22,0.70,0.70],
]
const SQUAD_352 : Array = [
	[1,"GK","GK",    0.45,0.70, 0.55,0.10,0.20,0.75,0.78,0.20],
	[5,"CBL","CB",   0.48,0.68, 0.55,0.10,0.25,0.80,0.65,0.25],
	[4,"CBC","BPD",  0.50,0.68, 0.60,0.15,0.35,0.78,0.65,0.30],
	[3,"CBR","CB",   0.48,0.68, 0.55,0.10,0.25,0.80,0.65,0.25],
	[2,"RWB","WB",   0.60,0.78, 0.62,0.25,0.55,0.62,0.65,0.38],
	[6,"CDM","CDM",  0.52,0.80, 0.65,0.22,0.38,0.72,0.68,0.30],
	[8,"CMR","CM",   0.55,0.76, 0.70,0.40,0.55,0.52,0.64,0.45],
	[10,"CML","CM",  0.55,0.76, 0.70,0.42,0.58,0.50,0.64,0.48],
	[11,"LWB","WB",  0.60,0.78, 0.62,0.25,0.55,0.62,0.65,0.38],
	[9,"ST1","ST",   0.60,0.65, 0.52,0.80,0.60,0.22,0.70,0.72],
	[7,"ST2","CF",   0.58,0.65, 0.60,0.72,0.65,0.22,0.68,0.65],
]
const SQUAD_541 : Array = [
	[1,"GK","GK",    0.45,0.70, 0.55,0.10,0.20,0.75,0.78,0.20],
	[2,"RWB","WB",   0.60,0.78, 0.62,0.25,0.55,0.62,0.65,0.38],
	[5,"CBR","CB",   0.48,0.68, 0.55,0.10,0.25,0.80,0.65,0.25],
	[4,"CBC","CB",   0.48,0.68, 0.55,0.10,0.25,0.80,0.65,0.25],
	[3,"CBL","CB",   0.48,0.68, 0.55,0.10,0.25,0.78,0.62,0.25],
	[11,"LWB","WB",  0.60,0.78, 0.62,0.25,0.55,0.62,0.65,0.38],
	[7,"MR","WF",    0.60,0.72, 0.60,0.45,0.62,0.38,0.63,0.50],
	[6,"CDM","CDM",  0.52,0.80, 0.65,0.22,0.38,0.72,0.68,0.30],
	[8,"ML","WF",    0.60,0.72, 0.60,0.45,0.62,0.38,0.63,0.50],
	[10,"AM","AM",   0.56,0.72, 0.70,0.55,0.60,0.35,0.66,0.55],
	[9,"ST","ST",    0.60,0.65, 0.50,0.80,0.60,0.20,0.70,0.72],
]
const SQUAD_BY_FORMATION : Dictionary = {
	"4-3-3":   SQUAD_433,
	"4-2-3-1": SQUAD_4231,
	"4-4-2":   SQUAD_442,
	"3-5-2":   SQUAD_352,
	"5-4-1":   SQUAD_541,
}

# ── @exports ──────────────────────────────────────────────────────────────────

@export var home_name_edit           : LineEdit          = null
@export var away_name_edit           : LineEdit          = null
@export var home_formation_dropdown  : OptionButton      = null
@export var away_formation_dropdown  : OptionButton      = null
@export var home_preset_dropdown     : OptionButton      = null
@export var away_preset_dropdown     : OptionButton      = null
@export var home_kit_primary_picker  : ColorPickerButton = null
@export var away_kit_primary_picker  : ColorPickerButton = null
@export var home_kit_sec_picker      : ColorPickerButton = null
@export var away_kit_sec_picker      : ColorPickerButton = null
@export var home_slider_grid         : GridContainer     = null
@export var away_slider_grid         : GridContainer     = null
@export var home_squad_grid          : GridContainer     = null   # ← new
@export var away_squad_grid          : GridContainer     = null   # ← new
@export var kick_off_button          : Button            = null
@export var error_label              : Label             = null

# ── Live state ────────────────────────────────────────────────────────────────

var _home_tactics : Dictionary = {}
var _away_tactics : Dictionary = {}

# Live squad data: Array of 11 Dictionaries per team, edited in place
# _squads[0] = home squad, _squads[1] = away squad
var _squads : Array = [[], []]

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	_init_tactics_defaults()
	_populate_formation_dropdowns()
	_populate_preset_dropdowns()
	_build_slider_grids()
	_init_squads_from_formations()
	_build_squad_grids()
	_connect_signals()

	if error_label != null:
		error_label.visible = false

# ── Init ──────────────────────────────────────────────────────────────────────

func _init_tactics_defaults() -> void:
	for def in SLIDER_DEFS:
		var key : String = def[0]
		var val : float  = def[2]
		_home_tactics[key] = val
		_away_tactics[key] = val
	_away_tactics["pressing_intensity"]      = 0.30
	_away_tactics["defensive_line"]          = 0.30
	_away_tactics["out_of_possession_shape"] = 0.75
	_away_tactics["counter_attack_focus"]    = 0.65


func _init_squads_from_formations() -> void:
	var home_form : String = _selected_formation(home_formation_dropdown)
	var away_form : String = _selected_formation(away_formation_dropdown)
	_squads[0] = _template_to_squad(home_form)
	_squads[1] = _template_to_squad(away_form)


func _template_to_squad(formation: String) -> Array:
	var template : Array = SQUAD_BY_FORMATION.get(formation, SQUAD_433)
	var squad    : Array = []
	for row : Array in template:
		squad.append({
			"shirt_number": row[0],
			"name":         row[1],
			"role":         row[2],
			"base_speed":   row[3],
			"stamina":      row[4],
			"passing":      row[5],
			"shooting":     row[6],
			"dribbling":    row[7],
			"defending":    row[8],
			"reactions":    row[9],
			"ego":          row[10],
		})
	return squad

# ── Dropdowns ─────────────────────────────────────────────────────────────────

func _populate_formation_dropdowns() -> void:
	for dropdown in [home_formation_dropdown, away_formation_dropdown]:
		if dropdown == null:
			continue
		dropdown.clear()
		for f : String in FORMATIONS:
			dropdown.add_item(f)
	if away_formation_dropdown != null and FORMATIONS.size() > 1:
		away_formation_dropdown.selected = 1


func _populate_preset_dropdowns() -> void:
	for dropdown in [home_preset_dropdown, away_preset_dropdown]:
		if dropdown == null:
			continue
		dropdown.clear()
		for preset in PRESETS:
			dropdown.add_item(preset[0])

# ── Tactics slider grid ───────────────────────────────────────────────────────

func _build_slider_grids() -> void:
	_build_slider_grid_for(home_slider_grid, _home_tactics, 0)
	_build_slider_grid_for(away_slider_grid, _away_tactics, 1)


func _build_slider_grid_for(grid: GridContainer, tactics: Dictionary,
							 team_idx: int) -> void:
	if grid == null:
		return
	for child in grid.get_children():
		child.queue_free()

	for i : int in range(SLIDER_DEFS.size()):
		var def     : Array  = SLIDER_DEFS[i]
		var key     : String = def[0]
		var label   : String = def[1]
		var default : float  = def[2]
		var current : float  = tactics.get(key, default)

		var name_lbl : Label = Label.new()
		name_lbl.text = label
		name_lbl.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		name_lbl.add_theme_font_size_override("font_size", 11)
		grid.add_child(name_lbl)

		var slider : HSlider = HSlider.new()
		slider.min_value             = 0.0
		slider.max_value             = 1.0
		slider.step                  = 0.01
		slider.value                 = current
		slider.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		slider.custom_minimum_size   = Vector2(100.0, 20.0)

		var cap_key    : String  = key
		var cap_team   : int     = team_idx
		var cap_slider : HSlider = slider
		slider.value_changed.connect(
			func(val: float) -> void:
				_on_slider_changed(cap_team, cap_key, val, cap_slider)
		)
		grid.add_child(slider)

		var val_lbl : Label = Label.new()
		val_lbl.text                  = "%.2f" % current
		val_lbl.custom_minimum_size   = Vector2(34.0, 0.0)
		val_lbl.horizontal_alignment  = HORIZONTAL_ALIGNMENT_RIGHT
		val_lbl.add_theme_font_size_override("font_size", 11)
		val_lbl.name = "Val_%s_%d" % [key, team_idx]
		grid.add_child(val_lbl)

# ── Squad editor grid ─────────────────────────────────────────────────────────

func _build_squad_grids() -> void:
	_build_squad_grid_for(home_squad_grid, 0)
	_build_squad_grid_for(away_squad_grid, 1)


func _build_squad_grid_for(grid: GridContainer, team_idx: int) -> void:
	if grid == null:
		return
	for child in grid.get_children():
		child.queue_free()

	var squad : Array = _squads[team_idx]

	# ── Header row ────────────────────────────────────────────────────────────
	_squad_grid_add_label(grid, "#",    28.0)
	_squad_grid_add_label(grid, "Name", 90.0)
	_squad_grid_add_label(grid, "Role", 54.0)
	for col : Array in ATTR_COLS:
		_squad_grid_add_label(grid, col[1], 38.0)

	# ── Player rows ───────────────────────────────────────────────────────────
	for slot : int in range(squad.size()):
		var p : Dictionary = squad[slot]

		# Shirt number label (read-only, from template)
		var shirt_lbl : Label = Label.new()
		shirt_lbl.text                 = str(p.get("shirt_number", slot + 1))
		shirt_lbl.custom_minimum_size  = Vector2(28.0, 0.0)
		shirt_lbl.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
		shirt_lbl.add_theme_font_size_override("font_size", 11)
		grid.add_child(shirt_lbl)

		# Name LineEdit
		var name_edit : LineEdit = LineEdit.new()
		name_edit.text               = p.get("name", "Player")
		name_edit.custom_minimum_size = Vector2(90.0, 24.0)
		name_edit.add_theme_font_size_override("font_size", 11)
		var cap_slot_n : int = slot
		var cap_team_n : int = team_idx
		name_edit.text_changed.connect(
			func(val: String) -> void:
				_squads[cap_team_n][cap_slot_n]["name"] = val
		)
		grid.add_child(name_edit)

		# Role OptionButton
		var role_btn : OptionButton = OptionButton.new()
		role_btn.custom_minimum_size = Vector2(54.0, 24.0)
		role_btn.add_theme_font_size_override("font_size", 10)
		var current_role : String = p.get("role", "CM")
		var current_role_idx : int = 0
		for r_i : int in range(ROLES.size()):
			role_btn.add_item(ROLES[r_i])
			if ROLES[r_i] == current_role:
				current_role_idx = r_i
		role_btn.selected = current_role_idx
		var cap_slot_r : int = slot
		var cap_team_r : int = team_idx
		role_btn.item_selected.connect(
			func(idx: int) -> void:
				_squads[cap_team_r][cap_slot_r]["role"] = ROLES[idx]
		)
		grid.add_child(role_btn)

		# Attribute SpinBoxes
		for col : Array in ATTR_COLS:
			var attr_key : String = col[0]
			var spin : SpinBox = SpinBox.new()
			spin.min_value           = 0.00
			spin.max_value           = 1.00
			spin.step                = 0.01
			spin.value               = p.get(attr_key, 0.50)
			spin.custom_minimum_size = Vector2(38.0, 24.0)
			spin.add_theme_font_size_override("font_size", 10)
			# Hide the arrows — too cramped; user types or scrolls
			spin.suffix = ""
			var cap_slot_a : int    = slot
			var cap_team_a : int    = team_idx
			var cap_key_a  : String = attr_key
			spin.value_changed.connect(
				func(val: float) -> void:
					_squads[cap_team_a][cap_slot_a][cap_key_a] = val
			)
			grid.add_child(spin)


func _squad_grid_add_label(grid: GridContainer, text: String, min_w: float) -> void:
	var lbl : Label = Label.new()
	lbl.text                 = text
	lbl.custom_minimum_size  = Vector2(min_w, 0.0)
	lbl.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	lbl.modulate             = Color(0.60, 0.60, 0.60, 1.0)
	lbl.add_theme_font_size_override("font_size", 10)
	grid.add_child(lbl)

# ── Signal connections ─────────────────────────────────────────────────────────

func _connect_signals() -> void:
	if kick_off_button != null:
		kick_off_button.pressed.connect(_on_kick_off_pressed)

	if home_preset_dropdown != null:
		home_preset_dropdown.item_selected.connect(
			func(idx: int) -> void: _on_preset_selected(0, idx)
		)
	if away_preset_dropdown != null:
		away_preset_dropdown.item_selected.connect(
			func(idx: int) -> void: _on_preset_selected(1, idx)
		)

	# Formation change → rebuild squad grid from new template
	if home_formation_dropdown != null:
		home_formation_dropdown.item_selected.connect(
			func(_idx: int) -> void: _on_formation_changed(0)
		)
	if away_formation_dropdown != null:
		away_formation_dropdown.item_selected.connect(
			func(_idx: int) -> void: _on_formation_changed(1)
		)

# ── Handlers ──────────────────────────────────────────────────────────────────

func _on_formation_changed(team_idx: int) -> void:
	# Rebuild squad from the new formation template, discarding edits.
	var dropdown : OptionButton = home_formation_dropdown if team_idx == 0 \
								  else away_formation_dropdown
	var form : String = _selected_formation(dropdown)
	_squads[team_idx] = _template_to_squad(form)
	var grid : GridContainer = home_squad_grid if team_idx == 0 else away_squad_grid
	_build_squad_grid_for(grid, team_idx)


func _on_slider_changed(team_idx: int, key: String, val: float,
						_slider: HSlider) -> void:
	if team_idx == 0:
		_home_tactics[key] = val
	else:
		_away_tactics[key] = val
	var grid : GridContainer = home_slider_grid if team_idx == 0 else away_slider_grid
	if grid == null:
		return
	var lbl_name : String = "Val_%s_%d" % [key, team_idx]
	var lbl_node : Node   = grid.find_child(lbl_name, true, false)
	if lbl_node != null and lbl_node is Label:
		(lbl_node as Label).text = "%.2f" % val


func _on_preset_selected(team_idx: int, preset_idx: int) -> void:
	if preset_idx <= 0 or preset_idx >= PRESETS.size():
		return
	var preset_tactics : Dictionary = PRESETS[preset_idx][1]
	if preset_tactics.is_empty():
		return
	var live : Dictionary = _home_tactics if team_idx == 0 else _away_tactics
	for key : String in preset_tactics.keys():
		live[key] = preset_tactics[key]
	var grid : GridContainer = home_slider_grid if team_idx == 0 else away_slider_grid
	_refresh_slider_grid(grid, live, team_idx)


func _refresh_slider_grid(grid: GridContainer, tactics: Dictionary,
						   team_idx: int) -> void:
	if grid == null:
		return
	var children : Array = grid.get_children()
	for i : int in range(SLIDER_DEFS.size()):
		var key        : String = SLIDER_DEFS[i][0]
		var slider_idx : int    = i * 3 + 1
		var vallbl_idx : int    = i * 3 + 2
		if slider_idx >= children.size():
			break
		var slider : Node = children[slider_idx]
		if slider is HSlider:
			(slider as HSlider).set_value_no_signal(tactics.get(key, 0.5))
		if vallbl_idx < children.size():
			var lbl : Node = children[vallbl_idx]
			if lbl is Label:
				(lbl as Label).text = "%.2f" % tactics.get(key, 0.5)


func _on_kick_off_pressed() -> void:
	if error_label != null:
		error_label.visible = false

	var home_name : String = "Home FC"
	var away_name : String = "Away FC"
	if home_name_edit != null and not home_name_edit.text.strip_edges().is_empty():
		home_name = home_name_edit.text.strip_edges()
	if away_name_edit != null and not away_name_edit.text.strip_edges().is_empty():
		away_name = away_name_edit.text.strip_edges()

	var home_form : String = _selected_formation(home_formation_dropdown)
	var away_form : String = _selected_formation(away_formation_dropdown)

	var home_kit_col : Color = Color(0.13, 0.40, 1.0)
	var away_kit_col : Color = Color(1.0,  0.20, 0.20)
	var home_kit_sec : Color = Color.WHITE
	var away_kit_sec : Color = Color.WHITE
	if home_kit_primary_picker != null: home_kit_col = home_kit_primary_picker.color
	if away_kit_primary_picker != null: away_kit_col = away_kit_primary_picker.color
	if home_kit_sec_picker     != null: home_kit_sec = home_kit_sec_picker.color
	if away_kit_sec_picker     != null: away_kit_sec = away_kit_sec_picker.color

	var home_dict : Dictionary = {
		"name":          home_name,
		"formation":     home_form,
		"kit_primary":   _color_to_int(home_kit_col),
		"kit_secondary": _color_to_int(home_kit_sec),
		"tactics":       _home_tactics.duplicate(),
		"players":       _squads[0].duplicate(true),
	}
	var away_dict : Dictionary = {
		"name":          away_name,
		"formation":     away_form,
		"kit_primary":   _color_to_int(away_kit_col),
		"kit_secondary": _color_to_int(away_kit_sec),
		"tactics":       _away_tactics.duplicate(),
		"players":       _squads[1].duplicate(true),
	}

	GameState.set_match(home_dict, away_dict)
	EventBus.go_to_match.emit()

# ── Helpers ───────────────────────────────────────────────────────────────────

func _selected_formation(dropdown: OptionButton) -> String:
	if dropdown == null or dropdown.selected < 0:
		return "4-3-3"
	var idx : int = dropdown.selected
	return FORMATIONS[idx] if idx < FORMATIONS.size() else "4-3-3"


func _color_to_int(c: Color) -> int:
	var r : int = int(clampf(c.r, 0.0, 1.0) * 255.0)
	var g : int = int(clampf(c.g, 0.0, 1.0) * 255.0)
	var b : int = int(clampf(c.b, 0.0, 1.0) * 255.0)
	return (r << 16) | (g << 8) | b
