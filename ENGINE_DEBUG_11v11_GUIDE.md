# Running 11v11 Production Match with Debug Enabled

## Overview

This guide explains how to run a **full 11v11 production match with debug logging** enabled, using `DebugTickRunner` instead of `MatchEngine.Simulate()`. The critical difference is that `DebugTickRunner` captures detailed logs during the simulation for post-analysis, while avoiding bottlenecks that would slow down large matches.

## Quick Start

### From MatchView.gd (Godot)

```gdscript
var match_view = preload("res://Scenes/MatchView.tscn").instantiate()
match_view.debug_mode_enabled = true      # Enable debug capture
match_view.debug_capture_mode = "Light"   # or "Full" for smaller runs
add_child(match_view)
```

### From C# (Direct API)

```csharp
var homeTeam = /* TeamData loaded from disk */;
var awayTeam = /* TeamData loaded from disk */;
int seed = 12345;

var ctx = MatchContext.CreateMatch(homeTeam, awayTeam, seed);
var runner = new DebugTickRunner(ctx, maxTicks: 54000, DebugCaptureMode.Light);
runner.Run();

// Query the logs
DebugLogger.Query(runner.TickLogs)
           .ForPlayer(0)
           .HasBall()
           .InOpponentBox()
           .PrintTable();
```

---

## Architecture Comparison

### Production Flow (MatchEngine.Simulate)
```
TeamData (11v11) → MatchEngine.Simulate() 
  → runs MatchEngine.RunFullMatch() 
    → 54,000 ticks via MatchEngine.Tick() 
    → calls StatAggregator each tick 
  → returns MatchReplay (no debug logs)
```

### Debug Flow (DebugTickRunner)
```
TeamData (11v11) → DebugTickRunner.Run()
  → runs tick loop 54,000 times
    → Step 1: MovementSystem.Tick(ctx)
    → Step 2: PlayerAI.Tick(ctx)
    → Step 3: BallSystem.Tick(ctx)
    → Step 4: CollisionSystem.Tick(ctx)
    → Step 5: EventSystem.Tick(ctx)
    → Step 6: ReplayFrame.Capture(ctx)        [for visualization]
    → Step 7: DebugLogger.Capture(ctx, mode) [for analysis]
  → returns MatchReplay + TickLogs
```

### Key Differences

| Aspect | Production | Debug |
|--------|-----------|-------|
| **Entry Point** | `MatchEngine.Simulate(home, away, seed)` | `new DebugTickRunner(ctx, maxTicks, mode)` |
| **Stats Captured** | Yes (StatAggregator) | No (faster) |
| **Debug Logs** | None | Captured each tick (mode-dependent) |
| **MatchReplay** | Yes | Yes (same format) |
| **Tick Order** | 6 systems + StatAggregator | 6 systems only |
| **Memory for 54k ticks** | ~100 MB (with stats) | 16–43 MB (Light/Full) |
| **Playback** | ReplayPlayer.gd (unchanged) | ReplayPlayer.gd (unchanged) |
| **Assertions** | Not available | Available via DebugAssertions |

---

## Debug Capture Modes

### Mode: `None`
- **Memory per tick**: 0 bytes (fastest)
- **Data captured**: Nothing from DebugLogger
- **Use case**: Run full 90-min match, only use assertions
- **TickLogs size**: Empty array (DebugLogger.Capture not called)

```csharp
var runner = new DebugTickRunner(ctx, maxTicks: 54000, DebugCaptureMode.None);
runner.Run();
// runner.TickLogs will be empty
// But runner.Assert.ShotAttempted() still works on lightweight log
```

### Mode: `Light`
- **Memory per tick**: ~300 bytes
- **Data captured**: 
  - Tick number, match second, match minute
  - Ball position, ball phase, ball owner
  - All player positions, actions, role
  - Match events (goals, passes, tackles, fouls, etc.)
  - Score
- **Use case**: Full 11v11 match, full 90 minutes, quick overview
- **TickLogs size**: ~16 MB for 54,000 ticks

```csharp
var runner = new DebugTickRunner(ctx, maxTicks: 54000, DebugCaptureMode.Light);
runner.Run();

DebugLogger.Query(runner.TickLogs)
           .ForPlayer(0)
           .HasBall()
           .PrintTable();  // See where player 0 is with the ball
```

### Mode: `Full`
- **Memory per tick**: ~800 bytes
- **Data captured**: Light + 
  - Score breakdowns (pass score, dribble score, shoot score, etc.)
  - Defensive context (nearest defender, pressure level)
  - Decision reasoning data (from ActionPlan)
- **Use case**: Debugging AI decisions in small scenarios (2v2, 3v3)
- **TickLogs size**: ~43 MB for 54,000 ticks (still reasonable)
- **Warning**: Not recommended for full 90-min with all 22 players due to memory

```csharp
// Run a smaller scenario with Full logging
var ctx = DebugMatchContext.TwoVTwo(seed: 42);
var runner = new DebugTickRunner(ctx, maxTicks: 600, DebugCaptureMode.Full);
runner.Run();

DebugLogger.Query(runner.TickLogs)
           .ForPlayer(0)
           .HasBall()
           .ToJson()  // Export for LLM analysis
           .PrintFormat();
```

---

## What NO Longer Works for 11v11 Debug (Compared to Production)

### Stat Aggregation
- **Production**: `StatAggregator` runs each tick, builds `PlayerMatchStats` and `TeamMatchStats`
- **Debug**: Skipped (too slow for debug runs)
- **Workaround**: Export a production replay and convert stats separately, OR run production first to validate stats

### What IS Still Captured
✓ ReplayFrame (positions, events) — used by ReplayPlayer.gd for visualization  
✓ TickLog (structural logs) — used by DebugLogQuery for analysis  
✓ DebugTickEntry (lightweight assertions) — used by DebugAssertions  

---

## What BYPASSES Exist for 2v2 (vs 11v11)

When running **2v2 scenarios** with `DebugMatchContext.TwoVTwo()`:

### 1. **Formation Bypass**
```csharp
// DebugMatchContext manually places 2 players, does NOT use FormationLoader
var ctx = DebugMatchContext.TwoVTwo(seed);
// Inside: SetPlayer(ctx, index: 0, pos: Vec2(...), ...)
//         SetPlayer(ctx, index: 11, pos: Vec2(...), ...)
// FormationLoader is never called
```

**For 11v11**: Formation data IS loaded and applied by `MatchContext.CreateMatch()` or via `MatchEngineBridge.SimulateMatch()`.

### 2. **Player Count Check Bypass**
```csharp
// In DebugMatchContext:
ctx.DebugMode = true;           // Flag that bypasses the 11-player assertion
ctx.HomeActiveCount = 2;        // Only 2 players active
ctx.AwayActiveCount = 2;        // Only 2 players active
```

**For 11v11**: All 22 slots are populated, `ctx.DebugMode = false`.

### 3. **Role Bypass**
```csharp
// 2v2 assigns one ST and one GK per team
// No full 11-player role chart used

// 11v11 uses full role assignments from formation
```

### 4. **Tactical Bypass**
```csharp
// 2v2 uses DebugTactics.Balanced() — neutral slider defaults
// No formation-based tactical adjustments

// 11v11 uses real TacticsInput with full slider ranges
```

### What Systems DO NOT Bypass (Same for 2v2 and 11v11)
```
✓ MovementSystem       — steering, acceleration, fatigue
✓ PlayerAI             — decision-making
✓ BallSystem           — physics, possession logic
✓ CollisionSystem      — contact, tackling
✓ EventSystem          — goal, foul, pass events
✓ DebugLogger.Capture  — structural logging
```

---

## Recommended Usage Patterns

### Pattern 1: Quick 11v11 Overview
**Goal**: See what happens in a full match without detailed analysis.

```csharp
// From tests or C#
var homeTeam = /* load from disk */;
var awayTeam = /* load from disk */;

var ctx = MatchContext.CreateMatch(homeTeam, awayTeam, seed: 12345);
var runner = new DebugTickRunner(ctx, maxTicks: 54000, DebugCaptureMode.Light);
runner.Run();

// Visualize in Godot via ReplayPlayer
var replay = runner.BuildReplay();
bridge.LoadExternalReplay(replay);

// Print text summary
runner.PrintSummary();
```

**Output**:
```
[DebugTickRunner] 54000 ticks | Score: 2–1 | Frames: 54000 | TickLogs: 54000
  Events:
    Tick 0001: Goal by Home (ST #9)
    Tick 0800: Goal by Away (IW #11)
    ...
```

### Pattern 2: Debug Specific Player's Decisions
**Goal**: Analyze why a player made (or didn't make) a decision.

```csharp
var runner = /* run match as above */;

// Find all ticks where player 0 has the ball in opponent box
var query = DebugLogger.Query(runner.TickLogs)
                       .ForPlayer(0)
                       .HasBall()
                       .InOpponentBox();

query.PrintTable();
// Tick | Action | Pos | Nearest Def | Shoot Score | Pass Score | ...
// 456  | Pass   | ... | 5.2 units   | 0.62        | 0.71       | ...
// 457  | Pass   | ... | 4.1 units   | 0.65        | 0.58       | ...
// 458  | Shoot  | ... | 6.0 units   | 0.78        | 0.45       | ...
```

### Pattern 3: Validate Full Match in Full Mode (Smaller Test)
**Goal**: Full debugging data for a 2v2 test case.

```csharp
var ctx = DebugMatchContext.TwoVTwo(seed: 99);
var runner = new DebugTickRunner(ctx, maxTicks: 600, DebugCaptureMode.Full);
runner.Run();

// Export for LLM
var json = DebugLogger.Query(runner.TickLogs)
                     .ForPlayer(0)
                     .HasBall()
                     .ToJson();
// Paste JSON into your AI prompt for code analysis
```

---

## Workflow: Run 11v11 with Debug for the First Time

### Step 1: Verify Production Still Works
```csharp
// Ensure production baseline is solid
var homeTeam = /* load from disk */;
var awayTeam = /* load from disk */;
var replay = MatchEngine.Simulate(homeTeam, awayTeam, seed: 12345);
Console.WriteLine($"Production score: {replay.FinalHomeScore}–{replay.FinalAwayScore}");
```

### Step 2: Run Same Match with DebugTickRunner (Light)
```csharp
var ctx = MatchContext.CreateMatch(homeTeam, awayTeam, seed: 12345);
var runner = new DebugTickRunner(ctx, maxTicks: 54000, DebugCaptureMode.Light);
runner.Run();

var debugReplay = runner.BuildReplay();
Console.WriteLine($"Debug score: {debugReplay.FinalHomeScore}–{debugReplay.FinalAwayScore}");

// Should match production
```

### Step 3: Visualize in Godot
```gdscript
# MatchView.gd or any scene:
bridge.LoadExternalReplay(debug_replay)  # Bridge has LoadExternalReplay() method
replay_player.start_replay()             # ReplayPlayer reads it unchanged
```

### Step 4: Query for Insights
```csharp
// If score doesn't match, diagnose
var query = DebugLogger.Query(runner.TickLogs);

// Which player scored?
query.Where(log => log.HomeScore > 0).PrintTable();

// Where was the striker when they shot?
query.ForPlayer(9).Where(log => log.Action == "Shoot").PrintTable();
```

---

## Potential Issues & Solutions

### Issue: "Error: below 11 players" in 11v11 with Debug

**Symptom**:
```
System.Exception: "Cannot process 9 players; minimum is 11 (DebugMode=false)"
```

**Cause**: You created a regular `MatchContext` instead of using `DebugMatchContext`.

**Fix**: Use the production path:
```csharp
// WRONG:
var ctx = DebugMatchContext.TwoVTwo(seed);  // Only 2 players!
var runner = new DebugTickRunner(ctx, 54000, Light);  // Will fail for 11v11

// RIGHT:
var ctx = MatchContext.CreateMatch(homeTeam, awayTeam, seed);  // 11v11
var runner = new DebugTickRunner(ctx, 54000, Light);           // OK
```

### Issue: Debug Replay Doesn't Match Production Score

**Symptom**:
```
Production: 2–1
Debug:      1–2
```

**Cause**: Random seed mismatch or a system behaves differently under debug.

**Check**:
```csharp
// Ensure same seed
var prod = MatchEngine.Simulate(homeTeam, awayTeam, seed: 12345);
var ctx = MatchContext.CreateMatch(homeTeam, awayTeam, seed: 12345);
var debug = new DebugTickRunner(ctx, 54000, Light).Run().BuildReplay();

Console.WriteLine($"Random seed: {ctx.RandomSeed}");  // Should be 12345
```

### Issue: TickLogs is Empty

**Symptom**:
```csharp
Console.WriteLine(runner.TickLogs.Count);  // 0
```

**Cause**: Capture mode was `None`.

**Fix**:
```csharp
// WRONG:
var runner = new DebugTickRunner(ctx, 54000, DebugCaptureMode.None);
runner.Run();
Console.WriteLine(runner.TickLogs.Count);  // 0

// RIGHT:
var runner = new DebugTickRunner(ctx, 54000, DebugCaptureMode.Light);
runner.Run();
Console.WriteLine(runner.TickLogs.Count);  // 54000
```

### Issue: Out of Memory on 11v11 Full Mode

**Symptom**:
```
OutOfMemoryException after 30,000 ticks
```

**Cause**: Full mode captures ~800 bytes/tick × 22 players × 54,000 ticks = ~950 MB.

**Fix**: Use Light mode for 11v11:
```csharp
// WRONG:
var runner = new DebugTickRunner(ctx, 54000, DebugCaptureMode.Full);

// RIGHT:
var runner = new DebugTickRunner(ctx, 54000, DebugCaptureMode.Light);
```

Or use Full mode for smaller scenarios:
```csharp
var ctx = DebugMatchContext.TwoVTwo(seed);  // Only 4 players
var runner = new DebugTickRunner(ctx, 600, DebugCaptureMode.Full);  // ~500 KB
```

---

## File Locations Reference

| File | Purpose |
|------|---------|
| [Engine/Debug/DebugTickRunner.cs](Engine/Debug/DebugTickRunner.cs) | Main tick loop for debug runs |
| [Engine/Debug/DebugLogger.cs](Engine/Debug/DebugLogger.cs) | Capture modes and log structures |
| [Engine/Debug/DebugMatchContext.cs](Engine/Debug/DebugMatchContext.cs) | Build 2v2/3v3 scenarios |
| [Engine/Debug/DebugLogQuery.cs](Engine/Debug/DebugLogQuery.cs) | Query API for analysis |
| [Bridge/MatchEngineBridge.cs](Bridge/MatchEngineBridge.cs#L320) | Bridge.SimulateDebugScenario() and LoadExternalReplay() |
| [Scripts/Screens/MatchView.gd](Scripts/Screens/MatchView.gd) | GDScript orchestration (debug_mode_enabled export) |
| [Engine/Core/MatchReplay.cs](Engine/Core/MatchReplay.cs) | MatchReplay structure (shared) |
| [Engine/Core/ReplayFrame.cs](Engine/Core/ReplayFrame.cs) | Per-tick frame data (shared) |

---

## Next Steps

1. **First Run**: Execute the "Quick 11v11 Overview" pattern with Light mode ✓
2. **Verify**: Compare debug replay score with production score ✓
3. **Visualize**: Load debug replay in Godot via ReplayPlayer.gd ✓
4. **Debug**: Run DebugLogQuery on specific situations ✓
5. **Optimize**: If needed, use Full mode on smaller scenarios ✓

---

## Summary Table: When to Use Each Mode

| Scenario | Mode | Ticks | Players | Example |
|----------|------|-------|---------|---------|
| Full 90-min match overview | Light | 54,000 | 22 | Check if match plays out correctly |
| Diagnose full match + detailed logs | Light + assertions | 54,000 | 22 | "Why didn't the striker shoot?" |
| Deep dive on single decision | Full | 600–1,200 | 2–4 | "Why did the AI choose pass over shoot?" |
| Batch runs, no analysis | None | 54,000 | 22 | Performance testing, simulation validation |
| Production stats + replay | N/A (use MatchEngine.Simulate) | 54,000 | 22 | Official match result |

---

Good luck with your 11v11 debug runs! 🚀
