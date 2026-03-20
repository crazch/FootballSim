# =============================================================================
# Module:  FormationGhostsOverlay.gd
# Path:    FootballSim/Scripts/Overlays/FormationGhostsOverlay.gd
# Purpose:
#   When the user pauses playback, draws translucent "ghost" dots at each
#   player's FormationAnchor position — the ideal shape position the engine
#   is pulling them toward.
#
#   This reveals the formation structure even while players drift from their
#   anchors in possession or defensive phases. The gap between a live player
#   dot and its ghost shows how far out of position that player is.
#
#   Ghost data comes from the initial frame (frame 0) at match start,
#   when all players are at their formation anchors.
#
# Public API:
#   load_anchors(first_frame: Dictionary,
#                home_color: Color, away_color: Color)
#     Call once after start_replay() with Bridge.GetFrame(0).
#
#   set_overlay_visible(v: bool)
#     Toggle visibility (called by ReplayPlayer when paused/resumed).
#
# Scene tree:
#   OverlayLayer (Node2D)
#     └── FormationGhostsOverlay (Node2D)  ← this script
#
# z_index = 1 (behind players, above grass)
# =============================================================================

extends Node2D

# ── Visual constants ───────────────────────────────────────────────────────────

const GHOST_RADIUS      : float = 8.0
const GHOST_ALPHA       : float = 0.30
const GHOST_RING_ALPHA  : float = 0.55
const GHOST_RING_WIDTH  : float = 1.5

# ── State ──────────────────────────────────────────────────────────────────────

# Each entry: { "pos": Vector2, "color": Color, "ring_color": Color }
var _anchors : Array[Dictionary] = []

# ── Lifecycle ─────────────────────────────────────────────────────────────────

func _ready() -> void:
	z_index  = 1
	visible  = false   # hidden by default; shown when paused


# ── Public API ────────────────────────────────────────────────────────────────

## Call once after simulation starts.
## Reads player positions from frame 0 (= formation anchors at kickoff).
func load_anchors(first_frame: Dictionary, home_color: Color, away_color: Color) -> void:
	_anchors.clear()

	var players : Array = first_frame.get("players", [])
	for i : int in range(players.size()):
		var p   : Dictionary = players[i]
		var pos : Vector2    = p.get("position", Vector2.ZERO)
		if pos == Vector2.ZERO:
			continue

		var team_id    : int   = 0 if i <= 10 else 1
		var base_color : Color = home_color if team_id == 0 else away_color

		var fill_color : Color = Color(
			base_color.r, base_color.g, base_color.b, GHOST_ALPHA
		)
		var ring_color : Color = Color(
			base_color.r, base_color.g, base_color.b, GHOST_RING_ALPHA
		)

		var entry : Dictionary = {
			"pos":        pos,
			"fill":       fill_color,
			"ring":       ring_color,
		}
		_anchors.append(entry)

	queue_redraw()


func set_overlay_visible(v: bool) -> void:
	visible = v
	if v:
		queue_redraw()

# ── Drawing ────────────────────────────────────────────────────────────────────

func _draw() -> void:
	for entry : Dictionary in _anchors:
		var pos        : Vector2 = entry.get("pos",  Vector2.ZERO)
		var fill_color : Color   = entry.get("fill", Color.TRANSPARENT)
		var ring_color : Color   = entry.get("ring", Color.TRANSPARENT)

		# Ghost dot fill
		draw_circle(pos, GHOST_RADIUS, fill_color)

		# Ghost ring outline
		draw_arc(pos, GHOST_RADIUS, 0.0, TAU, 16, ring_color, GHOST_RING_WIDTH)

		# Small cross at centre for precise position reading
		var arm : float = GHOST_RADIUS * 0.5
		draw_line(
			pos + Vector2(-arm, 0.0),
			pos + Vector2( arm, 0.0),
			ring_color,
			1.0
		)
		draw_line(
			pos + Vector2(0.0, -arm),
			pos + Vector2(0.0,  arm),
			ring_color,
			1.0
		)
