# DebugMatchView.gd

extends Node2D

@export var replay_player : Node

var bridge


func _ready():

	print("=== DebugMatchView READY ===")

	bridge = MatchEngineBridge.new()

	print("Calling DebugSimulateTwoVTwo")

	bridge.DebugSimulateTwoVTwo(42)

	print("Starting replay")

	replay_player.start_replay()

	print("Replay started")
