extends Node2D

var home_team_dict = {}
var away_team_dict = {}

func _ready():
	var replay_player = $ReplayPlayer

	replay_player.score_changed.connect($HUD.on_score_changed)
	replay_player.event_fired.connect($EventLogUI.on_event)
	replay_player.playback_finished.connect(_on_match_over)


func on_simulation_done():

	$ReplayPlayer.home_kit_primary = home_team_dict.get("kit_primary", 0x2266FF)
	$ReplayPlayer.away_kit_primary = away_team_dict.get("kit_primary", 0xFF3333)

	$ReplayPlayer.start_replay()


func _on_match_over():
	print("Match finished")
