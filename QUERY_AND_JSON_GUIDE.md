# ✅ Debug Query & JSON Export - Complete Guide

## Overview

The `DebugLogger.Query()` system provides:
1. **Fluent query builder** — Filter tick logs down to exactly what you need
2. **JSON export** — Save results as JSON for LLM analysis or external tools
3. **Table printing** — Human-readable console output for quick inspection

---

## Quick Start

### From C# (Recommended for Debug)

```csharp
// 1. Run a debug scenario (2v2 with Full logging for score details)
var ctx    = DebugMatchContext.TwoVTwo(seed: 42);
var runner = new DebugTickRunner(ctx, maxTicks: 600, DebugCaptureMode.Full);
runner.Run();

// 2. Query and export as JSON
string json = runner.Query()
                    .ForPlayer(0)           // Home striker (P0)
                    .HasBall()              // Only when they have the ball
                    .InOpponentBox()        // Only in opponent's box
                    .ToJson();              // Returns JSON string

// 3. Save to file or print to console
System.IO.File.WriteAllText("debug_striker.json", json);
Console.WriteLine(json);

// 4. Copy/paste JSON into LLM prompt
```

---

## Query Filter Methods

### Player-Level Filters

```csharp
query.ForPlayer(playerId)        // Only this player (0–21)
query.ForTeam(teamId)            // Only home (0) or away (1)
query.HasBall()                  // Player has the ball
query.NoBall()                   // Player does NOT have the ball
query.InOpponentBox()            // Inside opponent penalty box
query.InOwnBox()                 // Inside own penalty box
query.WasReEvaluated()           // Decision made (not cooldown tick)
query.Action(PlayerAction.Shoot) // Only this action type
```

### Range & Custom Filters

```csharp
query.TickRange(from, to)        // Only ticks in [from, to] (inclusive)
query.Where(predicate)           // Custom filter function
```

### Chainable

```csharp
// All methods return 'this' so you can chain:
query
  .ForPlayer(0)
  .HasBall()
  .InOpponentBox()
  .TickRange(100, 200)
  .Where(p => p.Scores?.ScoreShoot > 0.5f)
  .PrintTable();
```

---

## Terminal Methods (Execute Query)

### Export to JSON

```csharp
string json = query.ToJson();
// Returns formatted JSON string
// Also prints to Console.WriteLine()
```

**Output format**:
```json
[
  {
    "tick": 44,
    "id": 0,
    "role": "ST",
    "team": 0,
    "shirt": 9,
    "pos": {"x": 525.0, "y": 590.0},
    "has_ball": true,
    "in_opp_box": true,
    "action": "Shooting",
    "scores": {
      "shoot": 0.7845,
      "pass": 0.4310,
      "dribble": 0.0930
    },
    "shoot_detail": {
      "xg": 0.2412,
      "threshold": 0.6000,
      "dist_to_goal": 90.1
    }
  }
]
```

### Print to Console Table

```csharp
query.PrintTable();
// Outputs compact human-readable table
// Useful for quick inspection in Godot Output panel
```

**Output format**:
```
[DebugLogQuery] 18 entries:
─────┬──────────────────────────────────────────────────────────
T:0044 P00 ST   B (525,590) → (525,610)  *SHOOTING    stam=0.98 spd=12.3
           shoot=0.7845 pass=0.4310 drib=0.0930 xg=0.2412 thr=0.6000
T:0045 P00 ST   B (525,610) → (525,630)  *DRIBBLING   stam=0.97 spd=11.8
           shoot=0.6200 pass=0.3100 drib=0.8900 xg=0.1900 thr=0.5500
```

### Get Matching Entries as List

```csharp
var results = query.ToList();
// Returns List<(int Tick, PlayerTickLog Player)>
// Tick = simulation tick number
// Player = all player data for that tick
```

---

## Real-World Examples

### Example 1: "Why does the striker never shoot?"

```csharp
var json = runner.Query()
                 .ForPlayer(9)           // Home striker (usually shirt 9, player index 9)
                 .HasBall()
                 .InOpponentBox()
                 .ToJson();

// Copy json output, then ask LLM:
// "Here's every tick where my striker was in the box with the ball.
//  They never shoot - always pass or dribble. Looking at scores and xG,
//  why is shoot score always 0 or very low? [paste JSON here]"
```

### Example 2: "Show all tackles attempted"

```csharp
runner.Query()
      .Action(PlayerAction.Tackle)
      .PrintTable();
      
// Look at console output to see all tackle attempts with player data
```

### Example 3: "Export all CB decisions (out of possession)"

```csharp
string json = runner.Query()
                    .ForPlayer(5)         // Home CB (player 5)
                    .NoBall()             // Defending (no ball)
                    .ToJson();

System.IO.File.WriteAllText("cb_defense.json", json);
```

### Example 4: "Compare two players in same tick range"

```csharp
// Player 0 with ball
var attacker = runner.Query()
                     .ForPlayer(0)
                     .HasBall()
                     .TickRange(100, 200)
                     .ToList();

// Player 11 (defender) in same range
var defender = runner.Query()
                     .ForPlayer(11)
                     .NoBall()
                     .TickRange(100, 200)
                     .ToList();

// Analyze: How did defender respond to attacker's movement?
```

### Example 5: "Find all failed passes"

```csharp
runner.Query()
      .Action(PlayerAction.Pass)
      .Where(p => p.PassReceiverId != -1 && /* receiver lost ball quickly */)
      .PrintTable();
```

---

## JSON Export Use Cases

### 1. LLM Analysis

**Workflow**:
1. Query specific situation → `.ToJson()`
2. Copy JSON output from console
3. Paste into LLM prompt with context question

**Example prompt**:
```
I'm debugging a football AI. Here are 20 ticks where my striker was in the 
opponent's penalty box with the ball. In ALL cases, the xG (expected goals) 
is below 0.1 even though they're inside the box with clear shooting lanes.

Analysis of xG calculation:
- Distance to goal: 85–110 units (inside box)
- Blockers in lane: 0–1 (mostly clear)
- Threshold: varies 0.45–0.80
- But xG is always 0.02–0.08

Why is xG so low? Is the distance normalization wrong?

[JSON data here]
```

**LLM can then**:
- Identify patterns in scoring
- Spot normalization issues
- Suggest fixes to DecisionSystem

### 2. External Analysis Tools

```csharp
// Export full match for statistical analysis
string json = runner.Query()
                    .ForTeam(0)  // All home team players
                    .ToJson();

// Save and analyze in Python/R/Excel:
// - Plot decision distribution by position
// - Identify outliers
// - Compare to real player data
```

### 3. Automated Testing

```csharp
// In unit tests - verify AI makes correct decisions
var json = runner.Query()
                 .ForPlayer(0)
                 .InOpponentBox()
                 .ToJson();

// Parse JSON, assert properties:
// Assert.IsTrue(json.Contains("\"action\": \"Shooting\""));
// Assert.IsTrue(json.Contains("\"shoot\": ") && score > 0.5);
```

---

## Capture Mode Impact on JSON

| Mode | Score Details | Memory | JSON Size |
|------|---------------|--------|-----------|
| **None** | ❌ No | 0 B/tick | Empty |
| **Light** | ❌ No | ~300 B/tick | Minimal |
| **Full** | ✅ Yes | ~800 B/tick | Rich data |

**For JSON export, always use `Full` for small scenarios**:

```csharp
// Good: Full data for analysis
var runner = new DebugTickRunner(ctx, maxTicks: 600, DebugCaptureMode.Full);
runner.Run();
string json = runner.Query().ForPlayer(0).ToJson();

// Not useful: No score breakdowns in JSON
var runner = new DebugTickRunner(ctx, maxTicks: 54000, DebugCaptureMode.Light);
runner.Run();
string json = runner.Query().ForPlayer(0).ToJson();  // Missing shoot_detail, pass_detail
```

---

## Saving to File (C#)

```csharp
// Method 1: Direct write
string json = runner.Query().ForPlayer(0).HasBall().ToJson();
System.IO.File.WriteAllText("debug_query.json", json);

// Method 2: Pretty print (add formatting)
string formatted = System.Text.Json.JsonSerializer.Serialize(
    System.Text.Json.JsonDocument.Parse(json),
    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
);
System.IO.File.WriteAllText("debug_query_pretty.json", formatted);

// Method 3: Append multiple queries to one file
var sb = new System.Text.StringBuilder();
sb.AppendLine("{");
sb.AppendLine($"  \"striker\": {runner.Query().ForPlayer(9).ToJson()},");
sb.AppendLine($"  \"defender\": {runner.Query().ForPlayer(5).ToJson()}");
sb.AppendLine("}");
System.IO.File.WriteAllText("debug_analysis.json", sb.ToString());
```

---

## Console Output Redirection (Godot)

JSON and tables are printed to `Console.WriteLine()`, which appears in:
- **Godot Editor**: Output panel → "Output" tab
- **Terminal**: Stdout

**To capture from Godot**:
```csharp
// This prints to Output panel:
Console.WriteLine(json);

// Copy from Godot Output panel:
// 1. Open Output panel (bottom right)
// 2. Click "Output" tab
// 3. Select all, copy
// 4. Paste into file or LLM
```

---

## Complete Workflow Example

```csharp
// 1. Set up
var ctx    = DebugMatchContext.TwoVTwo(seed: 999);
var runner = new DebugTickRunner(ctx, maxTicks: 600, DebugCaptureMode.Full);
runner.Run();

// 2. Query striker with ball in box
var strikerJson = runner.Query()
                        .ForPlayer(0)
                        .HasBall()
                        .InOpponentBox()
                        .ToJson();

// 3. Save to file
System.IO.File.WriteAllText("striker_decisions.json", strikerJson);

// 4. Print for inspection
runner.Query()
      .ForPlayer(0)
      .HasBall()
      .InOpponentBox()
      .PrintTable();

// 5. Query defender without ball
var defenderJson = runner.Query()
                         .ForPlayer(11)
                         .NoBall()
                         .TickRange(0, 100)  // First 100 ticks
                         .ToJson();

System.IO.File.WriteAllText("defender_early_game.json", defenderJson);

// 6. Export everything for LLM
var fullJson = runner.Query().ForTeam(0).ToJson();
System.IO.File.WriteAllText("full_analysis_home_team.json", fullJson);

Console.WriteLine("✓ Exported 3 JSON files for analysis");
```

---

## Summary

| Feature | Method | Output |
|---------|--------|--------|
| **Query filter** | `.ForPlayer()`, `.HasBall()`, etc. | Filtered entries |
| **Export JSON** | `.ToJson()` | JSON string (file-safe) |
| **Print table** | `.PrintTable()` | Console table |
| **Get list** | `.ToList()` | `List<(int, PlayerTickLog)>` |
| **Save file** | `File.WriteAllText()` | `.json` file on disk |

**Yes, you can save to JSON and use the powerful query system!** 🚀
