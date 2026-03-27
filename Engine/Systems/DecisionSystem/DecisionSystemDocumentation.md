# DecisionSystem — Module Documentation

**Path:** `FootballSim/Engine/Systems/DecisionSystem/`  
**Architecture:** `public static partial class DecisionSystem` across 9 files  
**Pattern:** Pure utility scoring engine — no side effects, no state writes, no Random calls

---

## Overview

DecisionSystem is the AI brain for each player's action selection. Given a `PlayerState` and the full `MatchContext`, it produces a scored `ActionPlan` that `PlayerAI` then executes. The system is split into focused modules along natural domain seams: ball carrier, off-ball attacker, defender, goalkeeper, targeting math, shared helpers, types, and debug logging.

**Key invariants:**
- All methods are `static` and pure — same inputs always produce same outputs
- No writes to `MatchContext`. No calls to `BallSystem`. No `Random` inside scorers
- `PlayerAI` is the only caller of the top-level evaluators
- Bug 3 fix: `IsSprinting` is set via `ActionPlan.ShouldSprint`, not mutated inside the system

---

## Module Map

```
DecisionSystem/
├── DecisionSystem.cs            ← Public façade / orchestrator (entry points)
├── DecisionSystem.Types.cs      ← Result structs and output types
├── DecisionSystem.WithBall.cs   ← Ball-carrier scoring
├── DecisionSystem.OffBall.cs    ← In-possession off-ball scoring
├── DecisionSystem.Defense.cs    ← Out-of-possession defensive scoring
├── DecisionSystem.Goalkeeper.cs ← GK-specific helpers
├── DecisionSystem.Targeting.cs  ← Target-position generation
├── DecisionSystem.Helpers.cs    ← Shared math utilities
└── DecisionSystem.Debug.cs      ← Verbose decision logging
```

---

## Module Reference

---

### `DecisionSystem.cs` — Public Façade / Orchestrator

**Role:** The only file `PlayerAI` touches. Exposes the five top-level evaluators and holds the `DEBUG` / `DEBUG_PLAYER_ID` flags. Delegates everything else to the partial modules.

**Answers:** *"Which decision branch do we use?"*

| Method | Returns | Description |
|--------|---------|-------------|
| `EvaluateWithBall(player, ctx)` | `ActionPlan` | Scores Shoot / Pass / Dribble / Cross / Hold. Returns highest-scoring plan. |
| `EvaluateWithoutBall(player, ctx)` | `ActionPlan` | Scores MakeRun / Overlap / DropDeep / HoldWidth / SupportRun / ReturnToAnchor. |
| `EvaluateOutOfPossession(player, ctx)` | `ActionPlan` | Scores Press / Track / Recover / MarkSpace. Populates defensive context breakdown. |
| `EvaluateLooseBall(player, ctx)` | `ActionPlan` | Returns `Pressing` toward ball if close enough, else `WalkingToAnchor`. |
| `EvaluateGK(player, ctx)` | `ActionPlan` | GK distribution, loose-ball claim, or goal-line anchor. |

**Static flags:**

| Flag | Default | Purpose |
|------|---------|---------|
| `DEBUG` | `false` | Master toggle for verbose logging |
| `DEBUG_PLAYER_ID` | `-1` | Filter logging to one player. `-1` = log all (very expensive) |

---

### `DecisionSystem.Types.cs` — Result Structs

**Role:** Pure data contracts. No logic. Defines the interface between `DecisionSystem` and `PlayerAI`.

| Type | Used by | Description |
|------|---------|-------------|
| `ActionPlan` | `PlayerAI` | Complete scored plan for one player's next action. Carries optional breakdown fields (`HasScoreBreakdown`, `HasDefensiveContext`). |
| `PassCandidate` | `WithBall`, `Goalkeeper` | Score result for one pass receiver including speed, height, and breakdown. |
| `ShotScore` | `WithBall` | xG, final score, aim point, and blocker/distance context. |
| `PressScore` | `Defense` | Press target position, score, distance to carrier, and trigger radius. |
| `TrackScore` | `Defense` | Tracking target position, score, and target `PlayerId`. |

> **Note on `ShouldSprint`:** Lives in `ActionPlan` rather than mutating `PlayerState.IsSprinting`. `PlayerAI` must apply `plan.ShouldSprint` after accepting the plan (Bug 3 fix).

---

### `DecisionSystem.WithBall.cs` — Ball-Carrier Scorers

**Role:** Every scoring function for a player currently holding the ball.

**Answers:** *"What should the ball carrier do?"*

| Method | Returns | Key logic |
|--------|---------|-----------|
| `ScoreShoot(player, role, tactics, ctx)` | `ShotScore` | xG × role blend. Gates on range, xG threshold, and role type. Bug 5: correct goal per team. Bug 15: xG curve uses `SHOOT_MAX_RANGE`. Bug 16: multiplicative blocker decay. |
| `ScoreBestPass(player, role, tactics, ctx)` | `PassCandidate` | Iterates all teammates, returns highest `ScorePass` result. Bug 8: self-exclusion by `PlayerId`. |
| `ScorePass(player, receiver, role, tactics, ctx)` | `PassCandidate` | Distance modifier + progressive bonus + receiver pressure penalty + safe recycle bonus. Bug 9: sender pressure normalised to [0,1]. |
| `ScoreDribble(player, role, tactics, ctx)` | `float` | Peaks when defender is just outside safe distance (1v1 range). Bug 2: `contestT` inverted. |
| `ScoreCross(player, role, tactics, ctx)` | `float` | Requires wide zone + minimum Y progress + attackers in box bonus. |
| `ScoreHold(player, role, tactics, ctx)` | `float` | Peaks at medium pressure. Zero at max pressure and zero pressure. Bug 1: inverted normalisation. |

**Scoring formula (all options):**
```
raw    = base_attribute × role_bias_blended × tactic_modifier × situation_factor
blended = Lerp(role.Bias, player.AttributeEquivalent, FreedomLevel)
final  = Clamp01(raw) ± bonuses/penalties
```

---

### `DecisionSystem.OffBall.cs` — In-Possession Off-Ball Scorers

**Role:** Attacking movement for players whose team has the ball but who do not hold it.

**Answers:** *"How should an off-ball teammate support the attack?"*

| Method | Returns | Key logic |
|--------|---------|-----------|
| `ScoreSupportRun(player, role, tactics, ctx)` | `(Vec2, float)` | Distance to ideal support triangle position, minus crowding penalty. Bug 23: lateral offset capped to pitch bounds. |
| `ScoreMakeRun(player, role, tactics, ctx)` | `(Vec2, float)` | `RunInBehindTendency` × pace. Bug 11: pace proxy is `BaseSpeed / PLAYER_SPRINT_SPEED`, not `DribblingAbility`. |
| `ScoreOverlapRun(player, role, tactics, ctx)` | `(Vec2, float)` | `OverlapRunTendency` × pace. Bug 14: divide-by-zero guard on `PLAYER_SPRINT_SPEED`. |
| `ScoreDropDeep(player, role, tactics, ctx)` | `(Vec2, float)` | `DropDeepTendency` × passing ability. Rewards low `AttackingLine` tactic. |
| `ScoreHoldWidth(player, role, tactics, ctx)` | `float` | `WidthBias` blended with pace + passing ability. Bug 22: was hardcoded `0.5`. |
| `ScoreReturnToAnchor(player, role, tactics, ctx)` | `float` | Distance from anchor × `HoldPositionBias`. Shape discipline fallback. |

---

### `DecisionSystem.Defense.cs` — Out-of-Possession Scorers

**Role:** Defensive behaviour scoring when the player's team does not have the ball.

**Answers:** *"How should the team behave without the ball?"*

| Method | Returns | Key logic |
|--------|---------|-----------|
| `ScorePress(player, role, tactics, ctx)` | `PressScore` | Proximity × stamina × role press bias. Bug 18: loose ball within trigger zone now generates a press score. |
| `ScoreTrack(player, role, tactics, ctx)` | `TrackScore` | Finds most dangerous opponent runner within marking radius. Anticipates runner position. |
| `ScoreRecover(player, role, tactics, ctx)` | `float` | Urgency ∝ distance from defensive anchor. Bug 27: suppression threshold reduced from `0.5×` to `0.3×` to eliminate oscillation. |
| `ScoreMarkSpace(player, role, tactics, ctx)` | `float` | Zonal hold score. Low-urgency fallback when no press/track is triggered. |

---

### `DecisionSystem.Goalkeeper.cs` — GK Helpers

**Role:** GK-specific utilities. Goalkeeper behaviour is a special case with its own constraints and is kept isolated to prevent it infecting the outfield logic.

> `EvaluateGK` (the top-level evaluator) lives in `DecisionSystem.cs` because it is a `PlayerAI` entry point. The helpers it calls are defined here.

| Method | Returns | Description |
|--------|---------|-------------|
| `FindGKClearanceTarget(gk, ctx)` | `int` (PlayerId) | Most advanced teammate for a long clearance punt. Bug 7: prevents indefinite holding under pressure. |
| `FindOpponentGK(attackingTeam, ctx)` | `int` (PlayerId) | Opponent GK lookup. Also used by `ComputeGoalAimPoint` in `Targeting.cs`. |
| `ComputeGKAnchor(player, ctx)` | `Vec2` | Goal-line centre position. SK variant pushes further off the line. |

---

### `DecisionSystem.Targeting.cs` — Target-Position Generation

**Role:** Computes *where* a player should move. Conceptually distinct from scoring (which asks *how good* an option is).

> **Boundary:** Scoring → `float` or scored struct. Targeting → `Vec2`.

| Method | Returns | Description |
|--------|---------|-------------|
| `ComputeDribbleTarget(player, ctx)` | `Vec2` | Forward with role-based lateral bias (IW cuts in, WF stays wide). |
| `ComputeWidthTarget(player, ctx)` | `Vec2` | Touchline X, Y blended 40% toward ball. Bug 26: was fixed `FormationAnchor.Y`. |
| `ComputeSupportTarget(player, carrierPos, ctx)` | `Vec2` | Lateral offset 30–80 units from carrier based on `AttackingWidth` tactic. Bug 23: was raw `SUPPORT_IDEAL_DISTANCE` as X offset (off-pitch). |
| `ComputeRunInBehindTarget(player, ctx)` | `Vec2` | Targets gap between opponent CBs. Y = just behind offside line. Bug 13: lone CB → run the channel, not at the CB. |
| `ComputeOverlapTarget(player, ctx)` | `Vec2` | Wide position 150 units ahead of current position in attacking direction. |
| `ComputeDropDeepTarget(player, ctx)` | `Vec2` | Midfield retreat at `PITCH_HEIGHT × 0.45 / 0.55`. |
| `ComputeDefensiveAnchor(player, tactics, ctx)` | `Vec2` | Delegates to `BlockShiftSystem.ComputeShiftedTarget`. Shape maintained via pre-computed offsets. |
| `ComputeMarkSpaceTarget(player, tactics, ctx)` | `Vec2` | Shifted slot with compactness tightening toward shape centre (not pitch centre). |
| `ComputeGoalAimPoint(player, goalCentre, ctx)` | `Vec2` | Post opposite to GK lean. Bug 17: correct half-width per attacking direction. |

---

### `DecisionSystem.Helpers.cs` — Shared Math Utilities

**Role:** The toolbox. Pure math functions consumed by multiple scorers. All methods are `private static`.

| Method | Returns | Description |
|--------|---------|-------------|
| `BlendRoleWithInstinct(roleBias, instinct, freedomLevel)` | `float` | `Lerp(roleBias, instinct, freedomLevel)`. At `FreedomLevel=0`: full role control. At `1`: full player instinct. |
| `ComputeXG(player, goalCentre, distToGoal, ctx)` | `float` | Distance decay × angle factor × ability modifier × blocker decay. Bug 15: uses `SHOOT_MAX_RANGE`. Bug 16: multiplicative per-blocker decay. |
| `ComputeEffectiveXGThreshold(player, tactics)` | `float` | Base threshold from tactic, reduced by `player.Ego`. |
| `CountBlockersInShootingLane(player, goalCentre, team, ctx)` | `int` | Counts defenders within `DEFENDER_BLOCK_LANE_WIDTH` lateral of the shooting line. |
| `FindNearestOpponentDistance(player, ctx)` | `float` | Minimum distance to any active opponent. Returns `9999` if none found. |
| `ComputeReceiverPressure(receiver, receiverTeam, ctx)` | `float [0,1]` | Normalised: `0` = far from defenders, `1` = within tight-marking radius. |
| `ComputeSenderPressure(sender, ctx)` | `float [0,1]` | Same scale as `ComputeReceiverPressure`. Bug 9: was raw distance, now `[0,1]`. |
| `SelectPassSpeed(dist, directness, longPassBias)` | `float` | Soft for short, hard for long, lerped for medium. |
| `ComputeCrowdingPenalty(player, target, ctx)` | `float` | Accumulates penalty per teammate within `SUPPORT_CROWDING_RADIUS` of the target. |
| `ComputeOpponentDanger(opp, defensiveTeam, ctx)` | `float` | Proximity to defended goal + sprint bonus. Bug 21: uses `AttacksDownward` (half-time flip safe). |
| `CountAttackersInBox(attackingTeam, ctx)` | `int` | Counts team players inside opponent penalty area bounds. |

---

### `DecisionSystem.Debug.cs` — Diagnostic Logging

**Role:** All verbose decision logging. Isolated so diagnostic noise stays out of decision code and can be trivially stripped.

| Method | Logs |
|--------|------|
| `DebugLogWithBall(player, best, shot, pass, cross, drib, hold, tick)` | Chosen action, all five option scores, xG, best receiver. Bug 28: tick parameter explicit. |
| `DebugLogWithoutBall(player, best)` | Chosen action, score, target position. |
| `DebugLogOutOfPossession(player, best)` | Chosen action, score, target position. |

All three methods short-circuit immediately when `DEBUG = false`.

---

## Bug Fix Summary

| Bug | Module | Fix |
|-----|--------|-----|
| Bug 1 | WithBall | `ScoreHold` inverted: peaks at medium pressure, not zero pressure |
| Bug 2 | WithBall | `ScoreDribble` dead zone: `contestT` peaks at 1v1 range |
| Bug 3 | Types / cs | `IsSprinting` moved to `ActionPlan.ShouldSprint` — no mutation in scorers |
| Bug 5 | WithBall | `ScoreShoot` correct goal centre per attacking direction |
| Bug 6 | cs | `EvaluateLooseBall` uses `Pressing` (sprint-eligible), not `WalkingToAnchor` |
| Bug 7 | Goalkeeper | `FindGKClearanceTarget` prevents indefinite holding |
| Bug 8 | WithBall | `ScoreBestPass` self-exclusion by `PlayerId` not array index |
| Bug 9 | Helpers | `ComputeSenderPressure` normalised to `[0,1]` |
| Bug 11 | OffBall | `ScoreMakeRun` pace proxy: `BaseSpeed / PLAYER_SPRINT_SPEED` |
| Bug 13 | Targeting | Lone CB: target channel beside CB, not the CB directly |
| Bug 14 | OffBall | `ScoreOverlapRun` divide-by-zero guard |
| Bug 15 | Helpers | `ComputeXG` normalised by `SHOOT_MAX_RANGE` |
| Bug 16 | Helpers | `ComputeXG` multiplicative per-blocker decay |
| Bug 17 | Targeting | `ComputeGoalAimPoint` correct half-width per attacking direction |
| Bug 18 | Defense | `ScorePress` generates score for loose ball within trigger distance |
| Bug 21 | Helpers | `ComputeOpponentDanger` half-time flip safe via `AttacksDownward` |
| Bug 22 | OffBall | `ScoreHoldWidth` instinct blends pace + passing (was hardcoded `0.5`) |
| Bug 23 | Targeting | `ComputeSupportTarget` lateral offset bounded to pitch |
| Bug 25 | cs | `EvaluateGK` rushes loose ball immediately within `GK_CLAIM_RADIUS` |
| Bug 26 | Targeting | `ComputeWidthTarget` Y blended 40% toward ball |
| Bug 27 | Defense | `ScoreRecover` suppression threshold reduced from `0.5×` to `0.3×` |
| Bug 28 | Debug | `DebugLogWithBall` tick passed explicitly |

---

## External Requirements

These additions are required outside this module (unchanged from original `DecisionSystem.cs`):

| File | Addition |
|------|----------|
| `AIConstants.cs` | `XG_DISTANCE_DECAY = 2.5f`, `DISABLE_LONG_PASS_OVERRIDE` |
| `MatchContext.cs` | `bool AttacksDownward(int teamId)` — flipped at half-time |
| `PlayerAI.cs` | After `EvaluateOutOfPossession`: `player.IsSprinting = plan.ShouldSprint;` |

---

## Dependencies

```
Engine/Models/MatchContext.cs
Engine/Models/PlayerState.cs
Engine/Models/BallState.cs
Engine/Models/Vec2.cs
Engine/Models/Enums.cs
Engine/Systems/AIConstants.cs
Engine/Systems/PhysicsConstants.cs
Engine/Systems/BlockShiftSystem.cs
Engine/Tactics/RoleDefinition.cs
Engine/Tactics/RoleRegistry.cs
Engine/Tactics/TacticsInput.cs
```
