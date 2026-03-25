# DebugLogger — Usage Guide and LLM Query Patterns

## Setup in one call

```csharp
// Run 2v2, capture full score breakdowns
var ctx    = DebugMatchContext.TwoVTwo(seed: 42);
var runner = new DebugTickRunner(ctx, maxTicks: 600,
                                 captureMode: DebugCaptureMode.Full);
runner.Run();
```

After `Run()`, `runner.TickLogs` contains one `TickLog` per tick.
Each `TickLog` contains one `PlayerTickLog` per active player,
including the full score breakdown for every decision made.

---

## Query patterns

### "Why does the striker never shoot from inside the box?"

```csharp
runner.Query()
      .ForPlayer(0)
      .HasBall()
      .InOpponentBox()
      .PrintSummary();
```

Output:
```
[Query Summary] 18 entries
  actions: Pass×9  Hold×5  Dribble×3  Shoot×1
  avg scores: shoot=0.004  pass=0.381  drib=0.124  hold=0.218
  xG range: 0.018–0.071  threshold: 0.100
  → threshold-blocked: 17/18  range-blocked: 0/18
```

**LLM diagnosis from this:** `ScoreShoot ≈ 0` because `xG ≈ 0.04 < threshold=0.10`.
The xG is too low for the distance. Either the distance calculation is wrong
(check `SHOOT_OPTIMAL_RANGE` normalisation — DecisionSystem Bug 15) or the
threshold is miscalibrated (AIConstants `XG_THRESHOLD_LOW = 0.02` vs actual
`effectiveThreshold = 0.10` from ego calculation).

---

### "Show me every decision tick for player 0 in the box (for LLM)"

```csharp
string json = runner.Query()
                    .ForPlayer(0)
                    .HasBall()
                    .InOpponentBox()
                    .WasReEvaluated()     // only actual decision ticks, not cooldown
                    .ToJson();
// Paste json into your LLM prompt
```

Sample JSON output (paste to LLM):
```json
[
  {
    "tick": 44, "id": 0, "role": "ST", "team": 0, "shirt": 9,
    "pos": {"x": 525.0, "y": 590.0},
    "has_ball": true, "in_opp_box": true,
    "dist_to_goal": 90.1, "dist_to_nearest_opp": 41.3,
    "action": "Passing", "pass_to": 1,
    "was_reevaluated": true, "cooldown": 3,
    "scores": {
      "shoot": 0.0000, "pass": 0.4310, "dribble": 0.0930,
      "cross": 0.0000, "hold": 0.2180, "winning": 0.4310
    },
    "shoot_detail": {
      "xg": 0.0412, "threshold": 0.1000,
      "dist_to_goal": 90.1, "blockers_in_lane": 0,
      "blocked_by_threshold": true, "blocked_by_range": false
    },
    "pass_detail": {
      "best_receiver": 1, "receiver_score": 0.4310,
      "is_progressive": false, "distance": 142.0
    }
  }
]
```

**LLM prompt template:**
```
Here are the ticks where our striker (P0, ST) was inside the opponent penalty box
with the ball in a 2v2 simulation.

The striker never shoots — they always pass to P1 (the GK behind them).

Looking at the data:
- shoot score is always 0.0000
- xG ranges from 0.018 to 0.071
- threshold is always 0.1000
- blocked_by_threshold is always true

Question: Why is xG so low from 90 units distance? 
The penalty area depth is 165 units and penalty spot is 110 units from goal.
At 90 units the striker is inside the box at a central position with 0 blockers.
Real xG from this position should be ~0.25–0.40.

Relevant code: ComputeXG in DecisionSystem.cs normalises distance by
SHOOT_OPTIMAL_RANGE = 120f, not SHOOT_MAX_RANGE.
```

---

### "Show me all decisions by the CB defender"

```csharp
runner.Query()
      .ForPlayer(11)              // away CB
      .NoBall()
      .WasReEvaluated()
      .PrintTable();
```

Output:
```
[DebugLogQuery] 12 entries:
─────┬────────────────────────────────────────────────────────────────
T:0000 P11 CB   . (525,380) ---  *RECOVER          stam=1.00 spd=0.0
            press=0.00 track=0.00 recov=0.42 mark=0.29 dist_carrier=40.0
T:0003 P11 CB   . (523,393) ---  *RECOVER          stam=0.99 spd=5.2
            press=0.00 track=0.00 recov=0.38 mark=0.31 dist_carrier=15.3
T:0006 P11 CB   . (524,367) ---  *PRESS            stam=0.97 spd=6.8
            press=0.61 track=0.12 recov=0.09 mark=0.22 dist_carrier=12.1
...
```

**What to look for:**
- `press=0.00` when CB is 40 units from carrier? Press trigger distance is `AIConstants.PRESS_TRIGGER_DIST_MIN = 50f` — the CB is outside the press trigger. Normal.
- `press=0.00` when CB is 15 units from carrier? That is a bug — should be pressing.
- `recov=0.42` from anchor every tick for 10 ticks? Recovery urgency is too low, CB is creeping back slowly.

---

### "Show me every tick where a shot was attempted (for context)"

```csharp
runner.Query()
      .Events()
      .OfType(MatchEventType.ShotOnTarget)
      .PrintTable();
```

Output:
```
[Events]
  T:0089 [ShotOnTarget        ] P00→P12 (525,590) xtra=0.041  45' SHOT ON TARGET — P9 [Home] | xG: 0.04
```

---

### "Show me ball phase transitions — when did it go InFlight?"

```csharp
runner.Query()
      .Ball()
      .Where(b => b.Phase == BallPhase.InFlight && b.IsShot)
      .PrintTable();
```

Output:
```
[Ball states]
  T:0089 InFlight   (525,590) spd=55.0 h=0.20 owner=-1 shot=True on_tgt=True
  T:0090 InFlight   (524,607) spd=54.2 h=0.16 owner=-1 shot=True on_tgt=True
  T:0091 InFlight   (523,624) spd=53.5 h=0.12 owner=-1 shot=True on_tgt=True
  T:0092 InFlight   (523,641) spd=52.8 h=0.08 owner=-1 shot=True on_tgt=True
  T:0093 InFlight   (522,658) spd=52.1 h=0.04 owner=-1 shot=True on_tgt=True
```

**What to check:** Ball travels from Y=590 to Y=658 in 4 ticks at ~52 units/tick.
Goal line is Y=680. Ball should arrive in ~5 ticks. Does the GK save fire?
If `on_tgt=True` but no Save event follows, check `GK_SAVE_TRIGGER_DIST` vs
ball approach speed.

---

### "Show all ticks for all players at tick 47 specifically"

```csharp
runner.Query()
      .TickRange(47, 47)
      .PrintTable();
```

---

### "Export everything for ticks 40–50 as JSON (full LLM context)"

```csharp
string json = runner.Query()
                    .TickRange(40, 50)
                    .ToJson();
// json contains all active players for all 11 ticks
// Paste to LLM with question about the 10-tick window
```

---

## Capture mode selection

| Mode | Memory/tick | Score breakdown | Best for |
|---|---|---|---|
| `DebugCaptureMode.None` | 0 | No | Fast batch, assertions only |
| `DebugCaptureMode.Light` | ~300B | No | Position/action overview |
| `DebugCaptureMode.Full` | ~800B | Yes | LLM debugging, score analysis |

For 600-tick 2v2: Full mode = ~480KB total. Negligible.
For 54000-tick 11v11: Full mode = ~43MB. Use Light mode for full matches.

```csharp
// Console testing: Full (see all scores)
var runner = new DebugTickRunner(ctx, maxTicks: 600,
                                 captureMode: DebugCaptureMode.Full);

// Long runs or production monitoring: Light
var runner = new DebugTickRunner(ctx, maxTicks: 54000,
                                 captureMode: DebugCaptureMode.Light);

// Assertions only, no query needed
var runner = new DebugTickRunner(ctx, maxTicks: 600,
                                 captureMode: DebugCaptureMode.None);
```

---

## Files changed summary

| File | Change |
|---|---|
| `Engine/Debug/TickLog.cs` | New — data structures |
| `Engine/Debug/DebugLogger.cs` | New — Capture() factory |
| `Engine/Debug/DebugLogQuery.cs` | New — fluent query + formatters |
| `Engine/Debug/DebugTickRunner.cs` | Updated — add TickLogs, CaptureMode, Logger call |
| `Engine/Systems/DecisionSystem.cs` | Add score fields to ActionPlan struct |
| `Engine/Models/MatchContext.cs` | Add LastAppliedPlans field |
| `Engine/Systems/PlayerAI.cs` | Store plan to LastAppliedPlans in ApplyPlan |

Production pipeline (MatchEngine, MatchReplay, Bridge, Godot) — untouched.
