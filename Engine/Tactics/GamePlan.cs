// =============================================================================
// Module:  GamePlan.cs
// Path:    FootballSim/Engine/Tactics/GamePlan.cs
// Purpose:
//   Preset tactic bundles. Each GamePlan is a named TacticsInput configuration
//   that represents a well-known playing style. Used by TacticsScreen to let
//   the user pick a style and have all sliders set at once.
//
//   A GamePlan is ONLY a data preset — it fills TacticsInput fields.
//   It contains NO behaviour, NO logic, NO runtime state.
//   The user can still fine-tune individual sliders after applying a preset.
//
//   Presets defined here:
//     Gegenpress      High press, high tempo, high line, instant counter
//     Possession      Patient build, short passes, wide spread, low direct
//     WingPlay        Wide anchors, crossing focus, overlap runners
//     DirectCounter   Deep block, fast transition, long ball on turnover
//     FluidCounter    Mid block, high transition speed, runs on turnover
//     ParkTheBus      Very deep, very narrow, ultra-low press, no counter
//     HighPress       Full pitch press, narrow funnel, trap wide
//     TikiTaka        Extreme possession, very short, very high freedom
//     LongBall        Direct, physical, high ball, aerial focus
//     BalancedControl Moderate everything — a well-rounded default shape
//
//   Loaded from Data/GamePlans/game_plans.json by GamePlanLoader.cs.
//   Stored in GamePlanRegistry for lookup by Id.
//
// API:
//   GamePlan         class     { Id, DisplayName, Description, Tactics }
//   GamePlanRegistry class     { Register(plan), Get(id), All() }
//
// Dependencies:
//   Engine/Tactics/TacticsInput.cs
//
// Notes:
//   • Id is the stable string key (matches JSON). Never rename after definition.
//   • Tactics field is a TacticsInput struct — fully self-contained preset.
//   • TacticsScreen.gd copies Tactics into the live TeamData.Tactics when user
//     clicks "Apply Preset". No reference is kept to the GamePlan after that.
//   • All float values in Tactics follow TacticsInput 0.0–1.0 convention.
//   • GamePlanRegistry also holds hardcoded fallback presets (the ones defined
//     below as static properties) so the game works even before JSON is loaded.
// =============================================================================

namespace FootballSim.Engine.Tactics
{
    /// <summary>
    /// A named preset bundle of TacticsInput values. Pure data — no behaviour.
    /// Applied by TacticsScreen to set all sliders at once.
    /// </summary>
    public class GamePlan
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>
        /// Stable string key. Used in dropdowns, saved configs, and JSON.
        /// Examples: "gegenpress", "possession", "park_the_bus"
        /// Never change after initial definition — stored in user saved setups.
        /// </summary>
        public string Id;

        /// <summary>
        /// Human-readable name shown in TacticsScreen game plan picker.
        /// Examples: "Gegenpress", "Tiki-Taka", "Park the Bus"
        /// </summary>
        public string DisplayName;

        /// <summary>
        /// One-sentence description shown in TacticsScreen tooltip.
        /// Example: "Intense press after every loss of possession, high tempo,
        /// designed to win the ball back immediately in advanced positions."
        /// </summary>
        public string Description;

        /// <summary>
        /// The full TacticsInput preset. Copied into TeamData.Tactics on apply.
        /// All values normalised 0.0–1.0.
        /// </summary>
        public TacticsInput Tactics;

        // ── Hardcoded Fallback Presets ─────────────────────────────────────────
        // These are the canonical preset values. JSON files should match these.
        // If JSON fails to load, GamePlanRegistry uses these as fallback.
        // -----------------------------------------------------------------------

        /// <summary>
        /// Gegenpress — Intense press after every loss of possession.
        /// Designed to win the ball back immediately in advanced positions.
        /// High stamina cost. Press collapses if stamina is low.
        /// </summary>
        public static GamePlan Gegenpress() => new GamePlan
        {
            Id          = "gegenpress",
            DisplayName = "Gegenpress",
            Description = "Intense press after every loss of possession, high tempo, " +
                          "designed to win the ball back immediately in advanced positions.",
            Tactics = new TacticsInput
            {
                PressingIntensity    = 0.95f,
                PressingTrigger      = 0.90f,
                PressCompactness     = 0.75f,
                DefensiveLine        = 0.80f,
                DefensiveWidth       = 0.55f,
                DefensiveAggression  = 0.70f,
                PossessionFocus      = 0.55f,
                BuildUpSpeed         = 0.85f,
                PassingDirectness    = 0.55f,
                AttackingWidth       = 0.60f,
                AttackingLine        = 0.80f,
                TransitionSpeed      = 0.95f,
                CrossingFrequency    = 0.45f,
                ShootingThreshold    = 0.40f,
                Tempo                = 0.90f,
                OutOfPossessionShape = 0.80f,
                InPossessionSpread   = 0.60f,
                FreedomLevel         = 0.55f,
                CounterAttackFocus   = 0.70f,
                OffsideTrapFrequency = 0.60f,
                PhysicalityBias      = 0.55f,
                SetPieceFocus        = 0.50f,
            }
        };

        /// <summary>
        /// Possession — Patient build-up, always have a recycling option.
        /// Team positions create constant short-pass triangles.
        /// Low stamina cost but vulnerable to high press.
        /// </summary>
        public static GamePlan Possession() => new GamePlan
        {
            Id          = "possession",
            DisplayName = "Possession",
            Description = "Patient build-up through short passes. Team shape creates " +
                          "constant triangles. Recycle before going forward.",
            Tactics = new TacticsInput
            {
                PressingIntensity    = 0.50f,
                PressingTrigger      = 0.45f,
                PressCompactness     = 0.50f,
                DefensiveLine        = 0.55f,
                DefensiveWidth       = 0.60f,
                DefensiveAggression  = 0.35f,
                PossessionFocus      = 0.90f,
                BuildUpSpeed         = 0.30f,
                PassingDirectness    = 0.15f,
                AttackingWidth       = 0.75f,
                AttackingLine        = 0.55f,
                TransitionSpeed      = 0.40f,
                CrossingFrequency    = 0.35f,
                ShootingThreshold    = 0.60f,
                Tempo                = 0.35f,
                OutOfPossessionShape = 0.70f,
                InPossessionSpread   = 0.80f,
                FreedomLevel         = 0.45f,
                CounterAttackFocus   = 0.20f,
                OffsideTrapFrequency = 0.30f,
                PhysicalityBias      = 0.25f,
                SetPieceFocus        = 0.55f,
            }
        };

        /// <summary>
        /// Wing Play — Wide anchors, wingbacks push high, crosses prioritised.
        /// Stretches the opponent with width before delivering into the box.
        /// </summary>
        public static GamePlan WingPlay() => new GamePlan
        {
            Id          = "wing_play",
            DisplayName = "Wing Play",
            Description = "Stretch the opponent wide. Wingbacks push high and deliver " +
                          "crosses into the box. Forwards attack near post and far post.",
            Tactics = new TacticsInput
            {
                PressingIntensity    = 0.50f,
                PressingTrigger      = 0.50f,
                PressCompactness     = 0.30f,
                DefensiveLine        = 0.50f,
                DefensiveWidth       = 0.75f,
                DefensiveAggression  = 0.50f,
                PossessionFocus      = 0.50f,
                BuildUpSpeed         = 0.55f,
                PassingDirectness    = 0.45f,
                AttackingWidth       = 0.95f,
                AttackingLine        = 0.65f,
                TransitionSpeed      = 0.55f,
                CrossingFrequency    = 0.90f,
                ShootingThreshold    = 0.45f,
                Tempo                = 0.60f,
                OutOfPossessionShape = 0.55f,
                InPossessionSpread   = 0.90f,
                FreedomLevel         = 0.50f,
                CounterAttackFocus   = 0.40f,
                OffsideTrapFrequency = 0.25f,
                PhysicalityBias      = 0.60f,
                SetPieceFocus        = 0.60f,
            }
        };

        /// <summary>
        /// Direct Counter — Very deep defensive line, instant long ball on turnover.
        /// Fast forwards in behind. Exposes high attacking lines.
        /// </summary>
        public static GamePlan DirectCounter() => new GamePlan
        {
            Id          = "direct_counter",
            DisplayName = "Direct Counter",
            Description = "Deep defensive block. Win the ball and launch long immediately. " +
                          "Fast forwards chase in behind. Exposes teams with high lines.",
            Tactics = new TacticsInput
            {
                PressingIntensity    = 0.25f,
                PressingTrigger      = 0.20f,
                PressCompactness     = 0.70f,
                DefensiveLine        = 0.15f,
                DefensiveWidth       = 0.45f,
                DefensiveAggression  = 0.55f,
                PossessionFocus      = 0.15f,
                BuildUpSpeed         = 0.90f,
                PassingDirectness    = 0.90f,
                AttackingWidth       = 0.50f,
                AttackingLine        = 0.85f,
                TransitionSpeed      = 0.95f,
                CrossingFrequency    = 0.35f,
                ShootingThreshold    = 0.30f,
                Tempo                = 0.70f,
                OutOfPossessionShape = 0.85f,
                InPossessionSpread   = 0.45f,
                FreedomLevel         = 0.40f,
                CounterAttackFocus   = 0.95f,
                OffsideTrapFrequency = 0.10f,
                PhysicalityBias      = 0.70f,
                SetPieceFocus        = 0.55f,
            }
        };

        /// <summary>
        /// Fluid Counter — Mid-block, high transition speed, runners on turnover.
        /// More controlled than Direct Counter — builds slightly before releasing.
        /// </summary>
        public static GamePlan FluidCounter() => new GamePlan
        {
            Id          = "fluid_counter",
            DisplayName = "Fluid Counter",
            Description = "Compact mid-block, fast transition. Forwards make runs on turnover. " +
                          "More controlled than direct counter — plays through midfield when possible.",
            Tactics = new TacticsInput
            {
                PressingIntensity    = 0.40f,
                PressingTrigger      = 0.40f,
                PressCompactness     = 0.65f,
                DefensiveLine        = 0.30f,
                DefensiveWidth       = 0.50f,
                DefensiveAggression  = 0.50f,
                PossessionFocus      = 0.30f,
                BuildUpSpeed         = 0.80f,
                PassingDirectness    = 0.65f,
                AttackingWidth       = 0.55f,
                AttackingLine        = 0.75f,
                TransitionSpeed      = 0.85f,
                CrossingFrequency    = 0.40f,
                ShootingThreshold    = 0.35f,
                Tempo                = 0.75f,
                OutOfPossessionShape = 0.75f,
                InPossessionSpread   = 0.55f,
                FreedomLevel         = 0.50f,
                CounterAttackFocus   = 0.80f,
                OffsideTrapFrequency = 0.15f,
                PhysicalityBias      = 0.55f,
                SetPieceFocus        = 0.50f,
            }
        };

        /// <summary>
        /// Park the Bus — Ultra-deep, ultra-narrow, no press, no counter.
        /// Designed to absorb pressure and grind out results. Very low xG conceded.
        /// </summary>
        public static GamePlan ParkTheBus() => new GamePlan
        {
            Id          = "park_the_bus",
            DisplayName = "Park the Bus",
            Description = "Two banks of four very deep and narrow. Press only in own box. " +
                          "No counter — protect what you have. Hard to break down.",
            Tactics = new TacticsInput
            {
                PressingIntensity    = 0.05f,
                PressingTrigger      = 0.05f,
                PressCompactness     = 0.95f,
                DefensiveLine        = 0.05f,
                DefensiveWidth       = 0.15f,
                DefensiveAggression  = 0.40f,
                PossessionFocus      = 0.20f,
                BuildUpSpeed         = 0.50f,
                PassingDirectness    = 0.60f,
                AttackingWidth       = 0.30f,
                AttackingLine        = 0.30f,
                TransitionSpeed      = 0.40f,
                CrossingFrequency    = 0.20f,
                ShootingThreshold    = 0.30f,
                Tempo                = 0.20f,
                OutOfPossessionShape = 0.98f,
                InPossessionSpread   = 0.30f,
                FreedomLevel         = 0.20f,
                CounterAttackFocus   = 0.20f,
                OffsideTrapFrequency = 0.02f,
                PhysicalityBias      = 0.65f,
                SetPieceFocus        = 0.70f,
            }
        };

        /// <summary>
        /// High Press — Full pitch press from the front, narrow central funnel.
        /// Forces play through the sides into a trap. Different from Gegenpress:
        /// Gegenpress is reactive (after losing ball), High Press is proactive (always on).
        /// </summary>
        public static GamePlan HighPress() => new GamePlan
        {
            Id          = "high_press",
            DisplayName = "High Press",
            Description = "Proactive full-pitch press — always on, not just after losing ball. " +
                          "Central funnel forces play wide. Very high stamina cost.",
            Tactics = new TacticsInput
            {
                PressingIntensity    = 0.90f,
                PressingTrigger      = 1.00f,
                PressCompactness     = 0.85f,
                DefensiveLine        = 0.85f,
                DefensiveWidth       = 0.45f,
                DefensiveAggression  = 0.65f,
                PossessionFocus      = 0.45f,
                BuildUpSpeed         = 0.75f,
                PassingDirectness    = 0.50f,
                AttackingWidth       = 0.55f,
                AttackingLine        = 0.85f,
                TransitionSpeed      = 0.90f,
                CrossingFrequency    = 0.40f,
                ShootingThreshold    = 0.35f,
                Tempo                = 0.95f,
                OutOfPossessionShape = 0.70f,
                InPossessionSpread   = 0.55f,
                FreedomLevel         = 0.50f,
                CounterAttackFocus   = 0.65f,
                OffsideTrapFrequency = 0.70f,
                PhysicalityBias      = 0.50f,
                SetPieceFocus        = 0.45f,
            }
        };

        /// <summary>
        /// Tiki-Taka — Extreme possession, ultra-short passes, constant triangles.
        /// High freedom for players to find and express passing solutions.
        /// Very technical — weak against intense press if players have low passing ability.
        /// </summary>
        public static GamePlan TikiTaka() => new GamePlan
        {
            Id          = "tiki_taka",
            DisplayName = "Tiki-Taka",
            Description = "Extreme possession through ultra-short passes. Constant triangles. " +
                          "High player freedom. Requires high passing ability across the squad.",
            Tactics = new TacticsInput
            {
                PressingIntensity    = 0.60f,
                PressingTrigger      = 0.70f,
                PressCompactness     = 0.60f,
                DefensiveLine        = 0.65f,
                DefensiveWidth       = 0.65f,
                DefensiveAggression  = 0.30f,
                PossessionFocus      = 1.00f,
                BuildUpSpeed         = 0.25f,
                PassingDirectness    = 0.05f,
                AttackingWidth       = 0.70f,
                AttackingLine        = 0.50f,
                TransitionSpeed      = 0.35f,
                CrossingFrequency    = 0.15f,
                ShootingThreshold    = 0.70f,
                Tempo                = 0.40f,
                OutOfPossessionShape = 0.65f,
                InPossessionSpread   = 0.75f,
                FreedomLevel         = 0.80f,
                CounterAttackFocus   = 0.10f,
                OffsideTrapFrequency = 0.35f,
                PhysicalityBias      = 0.10f,
                SetPieceFocus        = 0.40f,
            }
        };

        /// <summary>
        /// Long Ball — Direct, physical, aerial focus. Skip midfield, aim at target man.
        /// Effective against deep-lying possession teams. Predictable against high pressing.
        /// </summary>
        public static GamePlan LongBall() => new GamePlan
        {
            Id          = "long_ball",
            DisplayName = "Long Ball",
            Description = "Skip midfield. Long diagonals to a target striker. " +
                          "Aerial battles and second balls. Physical and direct.",
            Tactics = new TacticsInput
            {
                PressingIntensity    = 0.40f,
                PressingTrigger      = 0.35f,
                PressCompactness     = 0.60f,
                DefensiveLine        = 0.30f,
                DefensiveWidth       = 0.50f,
                DefensiveAggression  = 0.60f,
                PossessionFocus      = 0.10f,
                BuildUpSpeed         = 0.95f,
                PassingDirectness    = 1.00f,
                AttackingWidth       = 0.40f,
                AttackingLine        = 0.90f,
                TransitionSpeed      = 0.85f,
                CrossingFrequency    = 0.50f,
                ShootingThreshold    = 0.25f,
                Tempo                = 0.65f,
                OutOfPossessionShape = 0.70f,
                InPossessionSpread   = 0.40f,
                FreedomLevel         = 0.35f,
                CounterAttackFocus   = 0.80f,
                OffsideTrapFrequency = 0.10f,
                PhysicalityBias      = 0.90f,
                SetPieceFocus        = 0.65f,
            }
        };

        /// <summary>
        /// Balanced Control — Moderate everything. A well-rounded starting point
        /// that performs competently without excelling or struggling in any area.
        /// Recommended starting preset before experimenting.
        /// </summary>
        public static GamePlan BalancedControl() => new GamePlan
        {
            Id          = "balanced_control",
            DisplayName = "Balanced Control",
            Description = "A well-rounded shape. Moderate press, moderate possession, " +
                          "moderate line. A reliable starting point before specialising.",
            Tactics = TacticsInput.Default() // All 0.5 — exact neutral midpoint
        };
    }

    // =========================================================================
    // GAME PLAN REGISTRY
    // =========================================================================

    /// <summary>
    /// Static registry of all GamePlan presets. Pre-seeded with hardcoded fallbacks.
    /// JSON loader can override individual entries or add custom plans.
    /// </summary>
    public static class GamePlanRegistry
    {
        private static readonly System.Collections.Generic.Dictionary<string, GamePlan>
            _plans = new System.Collections.Generic.Dictionary<string, GamePlan>();

        private static bool _seeded = false;

        /// <summary>
        /// Seeds the registry with all hardcoded fallback presets.
        /// Call once at startup before loading JSON (JSON can override).
        /// Calling a second time is a no-op.
        /// </summary>
        public static void SeedDefaults()
        {
            if (_seeded) return;
            Register(GamePlan.Gegenpress());
            Register(GamePlan.Possession());
            Register(GamePlan.WingPlay());
            Register(GamePlan.DirectCounter());
            Register(GamePlan.FluidCounter());
            Register(GamePlan.ParkTheBus());
            Register(GamePlan.HighPress());
            Register(GamePlan.TikiTaka());
            Register(GamePlan.LongBall());
            Register(GamePlan.BalancedControl());
            _seeded = true;
        }

        /// <summary>
        /// Register or overwrite a GamePlan entry. JSON loader calls this to
        /// override defaults with authored values.
        /// </summary>
        public static void Register(GamePlan plan)
        {
            _plans[plan.Id] = plan;
        }

        /// <summary>
        /// Returns the GamePlan for the given Id.
        /// Throws descriptive exception if not found.
        /// </summary>
        public static GamePlan Get(string id)
        {
            if (!_plans.TryGetValue(id, out var plan))
                throw new System.Exception(
                    $"[GamePlanRegistry] Game plan '{id}' not found. " +
                    $"Available: {string.Join(", ", _plans.Keys)}");
            return plan;
        }

        /// <summary>Returns all registered game plans. Used by TacticsScreen preset picker.</summary>
        public static System.Collections.Generic.IEnumerable<GamePlan> All() => _plans.Values;
    }
}