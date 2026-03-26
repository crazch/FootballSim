# Quick Reference: Query & JSON

## One-Liner Examples

```csharp
// Save striker decisions to JSON
System.IO.File.WriteAllText("striker.json", 
    runner.Query().ForPlayer(9).HasBall().InOpponentBox().ToJson());

// Print all tackles to console
runner.Query().Action(PlayerAction.Tackle).PrintTable();

// Export defender decisions (no ball) as JSON
System.IO.File.WriteAllText("defender.json",
    runner.Query().ForPlayer(5).NoBall().ToJson());

// Get all decisions in specific time window
runner.Query().TickRange(100, 200).PrintTable();

// Complex filter: shots where xG > 0.3
runner.Query()
      .Action(PlayerAction.Shoot)
      .Where(p => p.Scores?.Shoot?.XG > 0.3f)
      .PrintTable();
```

## Query Methods Cheat Sheet

### Filtering
```
ForPlayer(id)           Filter to one player
ForTeam(0 or 1)         Filter to home or away
HasBall()               Player has possession
NoBall()                Player defending/off-ball
InOpponentBox()         Inside opponent penalty area
InOwnBox()              Inside own penalty area
WasReEvaluated()        Decision made (not on cooldown)
Action(action)          Specific action type
TickRange(from, to)     Tick range
Where(predicate)        Custom filter function
```

### Exporting
```
.ToJson()               Export as JSON string (for LLM/files)
.PrintTable()           Print human-readable table (console)
.ToList()               Get List<(tick, player)> (programmatic)
```

## File Save Pattern

```csharp
string json = runner.Query()
                    .ForPlayer(playerId)
                    .HasBall()
                    .ToJson();

System.IO.File.WriteAllText("output.json", json);
```

## LLM Workflow

```
1. Query specific situation
   var json = runner.Query().ForPlayer(0).HasBall().InOpponentBox().ToJson();

2. Copy from console

3. Paste into LLM:
   "Why is this player always passing instead of shooting? Here's the data: [paste JSON]"

4. LLM analyzes scores, thresholds, distances → identifies bug
```

## Capture Mode for JSON

```
Full mode = Rich data (shoot_detail, pass_detail, defensive context)
Light mode = Positions/actions only (no score breakdowns)
None mode = No data (assertions only)

→ Always use Full for JSON export (small scenarios only)
```

That's it! Query + JSON export is ready to use. 🎯
