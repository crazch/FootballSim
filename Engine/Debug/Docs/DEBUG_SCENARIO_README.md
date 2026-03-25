# FootballSim — Debug Scenario System

## What This Is

A minimal debug harness that lets you run **1v1, 2v2, and 3v3** scenarios to
isolate engine bugs **without touching any production code**.

Every system runs identically to production. The only difference is:
- Fewer active players (unused slots have `IsActive = false`)
- No `FormationLoader` involved (anchors set directly)
- No `StatAggregator` or `ReplayFrame` capture
- Optional `ForceLongPassDisabled` per scenario
- Assertion helpers that fail with diagnostic messages pointing to the relevant bug

---

## Files Generated

| File | Destination | Purpose |
|---|---|---|
| `DebugMatchContext.cs` | `FootballSim/Engine/Debug/` | Builds 1v1, 2v2, 3v3 MatchContext |
| `DebugTickRunner.cs` | `FootballSim/Engine/Debug/` | Runs ticks, collects log, assertions |
| `ScenarioRunner.cs` | `FootballSim/Engine/Debug/` | Runs all scenarios, prints report |
| `EngineDebugLogger_ScenarioExtensions.cs` | `FootballSim/Engine/Debug/` | Logging profile helpers |
| `ScenarioRunnerTests.cs` | `FootballSim/Tests/` | NUnit tests, one per scenario |
| `MatchContext_DebugAdditions.cs` | `FootballSim/Engine/Debug/` | Documents the 6 lines to add to existing files |

---

## Existing Files to Change (Minimal)

Only **3 existing files** need small additions. No existing logic is changed.

### 1. `FootballSim/Engine/Models/MatchContext.cs`

Add these 4 fields after `AwayPossessionTicks`:

```csharp
// ── Debug mode fields (set only by DebugMatchContext, ignored by production) ──
public bool DebugMode              = false;
public int  HomeActiveCount        = 11;
public int  AwayActiveCount        = 11;
public bool ForceLongPassDisabled  = false;
```

### 2. `FootballSim/Engine/Systems/AIConstants.cs`

Add this 1 field anywhere in the class body:

```csharp
/// <summary>
/// Runtime override set by DebugTickRunner. Never true in production.
/// </summary>
public static bool DISABLE_LONG_PASS_OVERRIDE = false;
```

### 3. `FootballSim/Engine/Systems/DecisionSystem.cs`

Find the long-pass guard in `ScorePass` or `ScoreBestPass` and change:

```csharp
// BEFORE:
if (AIConstants.DISABLE_LONG_PASS && dist > AIConstants.PASS_LONG_THRESHOLD)
    return new PassCandidate { ReceiverId = -1, Score = 0f };

// AFTER:
bool longPassDisabled = AIConstants.DISABLE_LONG_PASS_OVERRIDE
                     || AIConstants.DISABLE_LONG_PASS;
if (longPassDisabled && dist > AIConstants.PASS_LONG_THRESHOLD)
    return new PassCandidate { ReceiverId = -1, Score = 0f };
```

### FormationLoader.cs — NO CHANGE NEEDED

Debug scenarios bypass `FormationLoader` entirely. The 11-slot validation
never fires because `DebugMatchContext` never calls `FormationRegistry.Get()`.

### Godot — NO CHANGE NEEDED

Inactive player dots (`IsActive = false`) already hide themselves in `PlayerDot.gd`
because the existing render loop skips inactive players. A 2v2 will show 4 dots on a
full-size pitch — exactly what you want.

---

## How to Run

### Option A: From a console test runner

```csharp
// In your entry point or a debug scene:
FootballSim.Engine.Debug.ScenarioRunner.RunAll();
```

### Option B: Run a single scenario

```csharp
// Quick 2v2 test:
var result = ScenarioRunner.RunTwoVTwo(seed: 42, maxTicks: 400);
Console.WriteLine(result.Passed ? "PASS" : $"FAIL: {result.FailReason}");
```

### Option C: Step-through (useful in Godot debug scene)

```csharp
var ctx    = DebugMatchContext.TwoVTwo();
var runner = new DebugTickRunner(ctx, maxTicks: 400);

// In _Process() or a timer:
var entry = runner.Step();
Console.WriteLine($"Tick {entry.Tick}: Ball owner={entry.BallOwnerId} phase={entry.BallPhase}");
```

### Option D: NUnit

```bash
dotnet test FootballSim/Tests/ --filter "Category=Debug"
```

---

## Enabling Verbose Logging

Use `DebugLogger` profiles to enable system-specific logging before a run:

```csharp
// See why the attacker won't shoot:
DebugLogger.ProfileOneVOne();
var runner = new DebugTickRunner(DebugMatchContext.OneVOne());
runner.Run();
DebugLogger.DisableAll();

// See tackle probability in 2v2:
DebugLogger.ProfileTwoVTwo();
var runner = new DebugTickRunner(DebugMatchContext.TwoVTwo());
runner.Run();
DebugLogger.DisableAll();

// See all pass scores in 3v3 (verbose — run for only 60 ticks):
DebugLogger.ProfileThreeVThree();
var runner = new DebugTickRunner(DebugMatchContext.ThreeVThreeShortPass(), maxTicks: 60);
runner.Run();
DebugLogger.DisableAll();
```

---

## Scenario Descriptions and What They Test

### 1v1 — Shot and GK Save
```
Home ST (index 0) at penalty spot (Y=515, 165 units from away goal)
Away GK (index 11) at goal line (Y=660)
Ball owned by ST
```
**What it tests:**
- Does `ScoreShoot` produce a non-zero score from 165 units?
- Does `ScoreHold` correctly return 0 when under no pressure? (Bug: it returns high when no pressure)
- Does `LaunchShot` set `ShotOnTarget=true` for a shot aimed at Y=680?
- Does `CollisionSystem.ResolveGoalCheck` fire within `GK_SAVE_TRIGGER_DIST`?
- Is the goal credited to home team (index 0, TeamId=0)?

**Expected behaviour:** ST shoots within 30 ticks. GK save fires. Goal or save event emitted.

---

### 2v2 — Dribble and Tackle
```
Home ST (index 0) at Y=340, ball at feet
Home GK (index 1) at Y=20 (own goal, safe outlet)
Away CB (index 11) at Y=380 (40 units from ST, closing)
Away GK (index 12) at Y=660
```
**What it tests:**
- Does `ScoreDribble` return > 0 when the CB is 40 units away? (Bug: dead zone returns ~0)
- Does the CB sprint toward the ball? (`ChasingBall` sprint bug)
- Does `CollisionSystem.ResolveTackleContests` fire when gap closes to < 25 units?
- Does a failed tackle have a cooldown? (PlayerAI Bug 23)
- Does the attacker eventually shoot or pass to GK?

**Expected behaviour:** CB closes gap and attempts tackle within ~10 ticks. Either tackle succeeds (ball loose) or attacker beats CB and shoots.

---

### 3v3 Short-Pass (long pass disabled)
```
Home ST (index 0) at Y=390, ball at feet
Home CM (index 1) at Y=420, X=390 (support position left of ST)
Home GK (index 2) at Y=20
Away CB (index 11) at Y=425 (marking ST)
Away CM (index 12) at Y=450, X=400 (covering CM's lane)
Away GK (index 13) at Y=660
```
**What it tests:**
- Does `ScoreSupportRun` produce a non-zero score for the CM? (`SUPPORT_CROWDING_PENALTY` bug)
- Does the ST pass to the CM rather than holding indefinitely? (`ScoreHold` inversion bug)
- Does the ball arrive at the CM's future position? (Lead-pass prediction)
- Is long-pass ratio < 20%? (`ForceLongPassDisabled` working)

**Expected behaviour:** ST passes to CM within 10-15 ticks. CM advances or passes back. At least 3 completed passes in 600 ticks.

---

### 3v3 Long-Pass (long pass enabled)
```
Same positions as above but home CM replaced with DLP (high passing ability)
ForceLongPassDisabled = false
```
**What it tests:**
- Does `LaunchPass` lead the ST's future position correctly? (BallSystem Bug 1)
- Does the ball survive the full distance without stopping? (Air resistance calibration)
- Does the ST receive the ball? (BALL_ARRIVAL_THRESHOLD > BALL_DRIBBLE_OFFSET)

**Expected behaviour:** DLP finds and plays the ST at least once in 600 ticks.

---

## Bug Reference — Which Scenario Catches Which Bug

| Bug (from analysis) | Scenario | Assertion |
|---|---|---|
| DecisionSystem Bug 1 — ScoreHold inverted | 1v1, 3v3 | `ShotWithinTicks`, `PassesCompleted` |
| DecisionSystem Bug 2 — ScoreDribble dead zone | 2v2 | `TackleAttempted` (indirect) |
| DecisionSystem Bug 4 — attacksDown hardcoded | 1v1 | `SaveAttempted` (ShotOnTarget wrong) |
| BallSystem Bug 1 — pass aims at static position | 3v3 L | `BallOwnedByPlayer(0)` |
| BallSystem Bug 2 — air resistance too low | 3v3 L | `BallOwnedByPlayer(0)` |
| BallSystem Bug 3+4 — LaunchShot wrong direction/goal | 1v1 | `SaveAttempted` |
| PhysicsConstants Bug 5 — arrival threshold ≤ dribble offset | 3v3 L | `BallOwnedByPlayer(0)` |
| PlayerAI Bug 5 — ChasingBall not sprinting | 2v2 | `TackleAttempted` |
| MovementSystem Bug 13 — ChasingBall not in sprint | 2v2 | `TackleAttempted` |
| AIConstants Bug 13 — progressive bonus too high | 3v3 S | `LongPassRatioBelow` |
| AIConstants Bug 14 — crowding penalty too high | 3v3 S | `PassesCompleted` |
| EventSystem Bug 3 — goal team attribution | 1v1 | `GoalCreditedToCorrectTeam` |
