using Godot;
using FootballSim.Engine.Debug;
using FootballSim.Engine.Models;

public partial class DebugScenarioPlayer : Node2D
{
	public enum DebugScenarioType
	{
		OneVOne,
		TwoVTwo,
		ThreeVThree
	}

	[Export]
	public DebugScenarioType Scenario = DebugScenarioType.TwoVTwo;

	private DebugTickRunner runner;
	private MatchContext ctx;
	private bool finished = false;

	public override void _Ready()
	{
		GD.Print("=== DEBUG SCENARIO START ===");

		ctx = Scenario switch
		{
			DebugScenarioType.OneVOne => DebugMatchContext.OneVOne(),
			DebugScenarioType.TwoVTwo => DebugMatchContext.TwoVTwo(),
			DebugScenarioType.ThreeVThree => DebugMatchContext.ThreeVThreeShortPass(),
			_ => DebugMatchContext.TwoVTwo()
		};

		runner = new DebugTickRunner(ctx, maxTicks: 400);
	}

	public override void _Process(double delta)
	{
		if (runner == null || finished)
			return;

		var entry = runner.Step();

		GD.Print(
			$"Tick {entry.Tick} " +
			$"Owner={entry.BallOwnerId} " +
			$"Phase={entry.BallPhase}"
		);

		if (entry.Tick >= 400)
		{
			finished = true;
			GD.Print("=== DEBUG SCENARIO END ===");
		}
	}
}
