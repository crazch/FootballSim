// =============================================================================
// PATCH:  ActionPlan_ScoreFields.cs
// Path:   FootballSim/Engine/Debug/ActionPlan_ScoreFields.cs
//
// Purpose:
//   Documents the fields to add to the existing ActionPlan struct in
//   FootballSim/Engine/Systems/DecisionSystem.cs.
//
//   DecisionSystem already computes all these values — they are currently
//   stored in local variables and discarded. Adding them to ActionPlan
//   costs zero extra computation. PlayerAI receives ActionPlan and can
//   store the last applied plans in ctx.LastAppliedPlans for DebugLogger.
//
// =============================================================================
//
// ── STEP 1: Add fields to ActionPlan struct in DecisionSystem.cs ─────────────
//
//   Find the ActionPlan struct definition and add these fields at the bottom:
//
//   public struct ActionPlan
//   {
//       // ── EXISTING FIELDS (unchanged) ──────────────────────────────────────
//       public PlayerAction Action;
//       public Vec2 TargetPosition;
//       public int  PassReceiverId;
//       public Vec2 ShotTargetPos;
//       public float PassSpeed;
//       public float PassHeight;
//       public float Score;
//       public float XG;
//
//       // ── NEW: with-ball score breakdown ────────────────────────────────────
//       // Populated by EvaluateWithBall. Zero when player does not have ball.
//
//       /// <summary>True when score breakdown fields were populated this evaluation.</summary>
//       public bool HasScoreBreakdown;
//
//       public float ScoreShoot;
//       public float ScorePass;
//       public float ScoreDribble;
//       public float ScoreCross;
//       public float ScoreHold;
//
//       // Shoot detail — why was xG what it was?
//       public float ShootXG;                   // computed xG at this position
//       public float ShootEffectiveThreshold;   // xG must exceed this to shoot
//       public int   ShootBlockersInLane;        // defenders in shooting lane
//       public float ShootDistToGoal;            // for quick reference
//
//       // Pass detail — best receiver context
//       public bool  PassIsProgressive;          // receiver is further forward
//       public float PassDistance;               // distance to best receiver
//       public float PassReceiverPressure;       // pressure on best receiver [0-1]
//
//       // ── NEW: out-of-possession score breakdown ────────────────────────────
//       // Populated by EvaluateOutOfPossession. Zero when player has ball.
//
//       /// <summary>True when defensive context fields were populated.</summary>
//       public bool HasDefensiveContext;
//
//       public float ScorePress;
//       public float ScoreTrack;
//       public float ScoreRecover;
//       public float ScoreMarkSpace;
//       public int   TrackTargetPlayerId;        // -1 if not tracking
//       public float DistToCarrier;              // distance to ball carrier
//       public float PressTriggerDist;           // press trigger radius this tick
//   }
//
// ── STEP 2: Populate the fields in EvaluateWithBall ──────────────────────────
//
//   In DecisionSystem.EvaluateWithBall(), after the best option is selected,
//   set the breakdown fields on the returned plan:
//
//   // At the end of EvaluateWithBall, before return:
//   best.HasScoreBreakdown       = true;
//   best.ScoreShoot              = shot.Score;
//   best.ScorePass               = bestPass.Score;
//   best.ScoreDribble            = dribScore;
//   best.ScoreCross              = crossScore;
//   best.ScoreHold               = holdScore;
//
//   // Shoot detail (already computed in ScoreShoot):
//   best.ShootXG                 = shot.XG;
//   best.ShootEffectiveThreshold = /* the effectiveThreshold local variable */;
//   best.ShootBlockersInLane     = /* blockersInLane local variable */;
//   best.ShootDistToGoal         = distToGoal;   // local var already exists
//
//   // Pass detail:
//   best.PassIsProgressive       = /* isProgressive local var in ScorePass */;
//   best.PassDistance            = /* dist local var in ScorePass */;
//
// ── STEP 3: Populate fields in EvaluateOutOfPossession ───────────────────────
//
//   In DecisionSystem.EvaluateOutOfPossession(), after best is selected:
//
//   best.HasDefensiveContext     = true;
//   best.ScorePress              = press.Score;
//   best.ScoreTrack              = track.Score;
//   best.ScoreRecover            = recoverScore;
//   best.ScoreMarkSpace          = markScore;
//   best.TrackTargetPlayerId     = track.TargetPlayerId;  // if exposed
//   best.DistToCarrier           = /* distToCarrier local var in ScorePress */;
//   best.PressTriggerDist        = triggerDist;           // local var in ScorePress
//
// ── STEP 4: Store LastAppliedPlans in MatchContext ────────────────────────────
//
//   Add to MatchContext.cs:
//
//   /// <summary>
//   /// The ActionPlan PlayerAI applied to each player this tick.
//   /// Written by PlayerAI.ApplyPlan, read by DebugLogger.Capture.
//   /// Index = player array index (0–21). Null entries = inactive or cooldown tick.
//   /// Only populated when ctx.DebugMode = true (avoids allocation in production).
//   /// </summary>
//   public ActionPlan?[] LastAppliedPlans = new ActionPlan?[22];
//
//   Add to PlayerAI.ApplyPlan():
//
//   // After plan is applied, store for DebugLogger:
//   if (ctx.DebugMode)
//       ctx.LastAppliedPlans[player.PlayerId] = plan;
//
// ── STEP 5: Call DebugLogger.Capture in DebugTickRunner ──────────────────────
//
//   In DebugTickRunner.Run() and DebugTickRunner.Step(), after EventSystem.Tick():
//
//   // After:  EventSystem.Tick(_ctx);
//   // Add:
//   if (_captureMode != DebugCaptureMode.None)
//   {
//       var tickLog = DebugLogger.Capture(
//           _ctx,
//           _ctx.LastAppliedPlans,
//           _captureMode);
//       _tickLogs.Add(tickLog);
//   }
//
//   // And in DebugTickRunner constructor, add:
//   private readonly List<TickLog> _tickLogs;
//   private readonly DebugCaptureMode _captureMode;
//
//   public DebugTickRunner(MatchContext ctx, int maxTicks = 600,
//                          DebugCaptureMode captureMode = DebugCaptureMode.Full)
//   {
//       _captureMode = captureMode;
//       _tickLogs    = captureMode != DebugCaptureMode.None
//                      ? new List<TickLog>(maxTicks) : null;
//       // ... rest of constructor
//   }
//
//   public IReadOnlyList<TickLog> TickLogs => _tickLogs ?? (IReadOnlyList<TickLog>)Array.Empty<TickLog>();
//
// =============================================================================
//
// SUMMARY OF ALL FILES CHANGED:
//
//   FootballSim/Engine/Systems/DecisionSystem.cs
//     • Add score fields to ActionPlan struct (Step 1)
//     • Assign them in EvaluateWithBall (Step 2)
//     • Assign them in EvaluateOutOfPossession (Step 3)
//
//   FootballSim/Engine/Models/MatchContext.cs
//     • Add LastAppliedPlans field (Step 4)
//
//   FootballSim/Engine/Systems/PlayerAI.cs (ApplyPlan method)
//     • Store plan to ctx.LastAppliedPlans when DebugMode=true (Step 4)
//
//   FootballSim/Engine/Debug/DebugTickRunner.cs
//     • Add _tickLogs list and _captureMode field (Step 5)
//     • Call DebugLogger.Capture after EventSystem.Tick (Step 5)
//     • Expose TickLogs property (Step 5)
//
// =============================================================================
