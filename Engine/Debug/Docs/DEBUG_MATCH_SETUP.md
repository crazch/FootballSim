# Debug Match Setup (11v11 Production)

## Quick Start

To run an **11v11 production match with debug logging**, use MatchView.gd with debug mode enabled.

### In the Godot Inspector

1. Open **MatchView.tscn** (or go to a scene with MatchView.gd)
2. In the Inspector, find the MatchView script properties:
   - **Debug Mode Enabled**: `true` (toggle to enable debug capture)
   - **Debug Capture Mode**: `"Light"` or `"Full"`

3. Press Play → the simulation runs with debug logging enabled

### Programmatically (from GDScript)

```gdscript
# From TacticsScreen or any scene that loads MatchView:
var match_view = preload("res://Scenes/MatchView.tscn").instantiate()
match_view.debug_mode_enabled = true
match_view.debug_capture_mode = "Full"  # or "Light"
add_child(match_view)
```

Or set it before transitioning:

```gdscript
GameState.home_team = my_home_dict
GameState.away_team = my_away_dict
GameState.match_seed = 12345

# Create a MatchView manually or override properties on the scene
var scene = load("res://Scenes/MatchView.tscn").instantiate()
scene.debug_mode_enabled = true
scene.debug_capture_mode = "Light"
get_tree().root.add_child(scene)
```

## Capture Modes

| Mode | Memory/tick | Data Captured | Best For |
|------|-------------|--------------|----------|
| `"Light"` | ~300 B | Positions, actions, events | Quick debugging, full 90-min overview |
| `"Full"` | ~800 B | Light + score breakdowns, defensive context | Score analysis, AI decision trees (2v2/3v3 recommended) |
| `"None"` | 0 B | Nothing | Assertions only, no debug output |

**For 54,000 ticks (90 minutes):**
- Light = ~16 MB memory
- Full = ~43 MB memory

### Recommended Usage

- **First run**: `"Light"` — see what's happening position-wise
- **Investigating score bugs**: `"Full"` (but only for 2v2/3v3 scenarios to keep memory reasonable)
- **Large batch runs**: `"None"` for speed

## What Gets Captured

### DebugTickRunner + DebugLogger Flow

When `debug_mode_enabled = true`:

1. MatchView calls `Bridge.SimulateDebugMatch()` instead of `Bridge.SimulateMatch()`
2. Bridge creates a full 11v11 `MatchContext` from team dictionaries
3. `DebugTickRunner` runs the 54,000-tick simulation
4. Each tick, `DebugLogger.Capture()` is called (in the specified mode)
5. `TickLog` objects are stored (one per tick)
6. After simulation, `DebugTickRunner.BuildReplay()` converts to `MatchReplay`
7. ReplayPlayer.gd plays back **without any changes** — same UI layer

### Available Debug Output

After simulation completes, you can query the logs from C# using the patterns in [USAGE_GUIDE.md](Engine/Debug/Docs/USAGE_GUIDE.md):

```csharp
// In C# test code or console scripts:
var query = DebugLogger.Query(runner.TickLogs);

// Why didn't the striker shoot?
query.ForPlayer(0)
     .HasBall()
     .InOpponentBox()
     .PrintSummary();

// Export JSON for LLM analysis
string json = query.ForPlayer(0).HasBall().InOpponentBox().ToJson();
```

**But from Godot/MatchView**, the replay plays back normally — the debug logs are captured internally in `DebugTickRunner.TickLogs` and available for C# analysis post-simulation.

## Godot Console Output

When `Bridge.DEBUG = true` (in MatchEngineBridge Inspector):

```
[Bridge] 11v11 debug match complete: 2-1 (54000 ticks, mode=Light)
[DebugTickRunner] Run complete. Ticks: 54000, Frames captured: 54000
```

Check the Godot Output panel (Output tab) for simulation progress.

## Files Modified

- **Bridge/MatchEngineBridge.cs**: Added `SimulateDebugMatch()` method
- **Scripts/Screens/MatchView.gd**: Added `debug_mode_enabled` and `debug_capture_mode` exports

## Next Steps

After running a debug match:

1. **For LLM-assisted debugging**: Export a small 2v2 scenario in `Full` mode, then use the patterns in [USAGE_GUIDE.md](Engine/Debug/Docs/USAGE_GUIDE.md) to generate JSON queries for pasting into your LLM prompt

2. **For assertions in C#**: Write test code using `DebugTickRunner` directly:
   ```csharp
   var ctx = MatchContext.CreateMatch(homeTeam, awayTeam, seed);
   var runner = new DebugTickRunner(ctx, maxTicks: 54000, captureMode: DebugCaptureMode.Light);
   runner.Run();
   runner.Query().ForPlayer(0).HasBall().PrintTable();
   ```

3. **For production**: Set `debug_mode_enabled = false` — simulation switches back to production mode (no debug overhead)
