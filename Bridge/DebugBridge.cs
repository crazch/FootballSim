using FootballSim.Engine.Debug;
using FootballSim.Engine.Models;
using System.Collections.Generic;
using Godot;

namespace FootballSim.Bridge
{
    public static class DebugBridge
    {
        private static List<MatchContext> _frames = new List<MatchContext>();
        private static bool _ready = false;
        private static string _error = "";


        public static void SimulateTwoVTwo(int seed)
        {
            GD.Print("=== DebugBridge SimulateTwoVTwo ===");

            _frames.Clear();
            _ready = false;
            _error = "";

            try
            {
                var ctx = DebugMatchContext.TwoVTwo(seed);

                int maxTicks = 400;

                var runner = new DebugTickRunner(ctx, maxTicks);

                for (int i = 0; i < maxTicks; i++)
                {
                    runner.Step();

                    // store context snapshot
                    _frames.Add(ctx);
                }

                MatchEngineBridge.Debug_SetReplayFrames(_frames);

                _ready = true;
            }
            catch (System.Exception ex)
            {
                _error = ex.ToString();
                GD.PrintErr(_error);
            }
        }

        public static bool IsReady()
        {
            return _ready;
        }


        public static string GetError()
        {
            return _error;
        }
    }
}