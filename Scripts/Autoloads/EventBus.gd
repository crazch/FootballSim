# =============================================================================
# Module:  EventBus.gd
# Path:    FootballSim/Scripts/Autoloads/EventBus.gd
# Purpose:
#   Global signal hub. Any node can emit or connect to these signals without
#   needing a direct reference to the emitter.
#   This decouples scenes that don't share a common parent in the tree.
#
# AutoLoad registration (Project Settings → AutoLoad):
#   Name: EventBus
#   Path: res://Scripts/Autoloads/EventBus.gd
#   Order does not matter relative to Bridge or GameState.
#
# Usage pattern:
#   EMIT:   EventBus.match_event_fired.emit(event_dict)
#   LISTEN: EventBus.match_event_fired.connect(_on_match_event)
#
# Signal catalogue:
#
#   ── Navigation ────────────────────────────────────────────────────────────
#   go_to_tactics()
#     Emitted by MainMenu "Start" button and PostMatch "Play Again" button.
#     Listener: whoever owns the scene tree (Main.gd or SceneManager).
#
#   go_to_match()
#     Emitted by TacticsScreen "Kick Off" button after set_match() is called.
#     Listener: whoever owns scene transitions.
#
#   go_to_postmatch()
#     Emitted by MatchView when playback finishes and user clicks "View Stats".
#     Listener: whoever owns scene transitions.
#
#   go_to_main_menu()
#     Emitted by PostMatch "Main Menu" button.
#
#   ── Match events (re-broadcast from ReplayPlayer for cross-scene consumers) ─
#   match_event_fired(event_dict: Dictionary)
#     Emitted by MatchView for each event_fired signal from ReplayPlayer.
#     Any future overlay, sound system, or achievement tracker can listen here
#     without being parented under MatchView.
#
#   match_score_changed(home_score: int, away_score: int)
#     Emitted by MatchView when score changes during replay.
#
#   match_phase_changed(phase_int: int)
#     Emitted by MatchView on phase transitions.
#
#   match_finished()
#     Emitted by MatchView when replay reaches the last tick.
#
#   ── Debug ─────────────────────────────────────────────────────────────────
#   debug_toggle(enabled: bool)
#     Emitted by any debug UI toggle. Listened to by ReplayPlayer.set_debug_all()
#     and OverlayLayer overlays to enable/disable debug rendering globally.
#
# Strict typing: all signals use typed parameters.
# =============================================================================

extends Node

# ── Navigation signals ────────────────────────────────────────────────────────

## Any scene requesting navigation to TacticsScreen.
signal go_to_tactics()

## TacticsScreen is ready — navigate to MatchView.
signal go_to_match()

## MatchView finished — navigate to PostMatch.
signal go_to_postmatch()

## PostMatch requesting return to main menu.
signal go_to_main_menu()

# ── Match event broadcast signals ─────────────────────────────────────────────

## One match event fired during replay. Payload mirrors Bridge.GetFrameEvents() dict.
## Keys: tick, match_minute, type, type_name, primary, secondary, team_id,
##       position, extra_float, extra_bool, description.
signal match_event_fired(event_dict: Dictionary)

## Score changed during replay.
signal match_score_changed(home_score: int, away_score: int)

## MatchPhase changed during replay.
signal match_phase_changed(phase_int: int)

## Replay reached the last tick.
signal match_finished()

# ── Debug ─────────────────────────────────────────────────────────────────────

## Global debug toggle. Broadcast to all visualization nodes.
signal debug_toggle(enabled: bool)
