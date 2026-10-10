using Game.Simulation.Demo;
using Game.Simulation;

if (args.Any(argument => argument == "--play"))
{
    M1SimulationSession.Run(Console.In, Console.Out, M1PlayScenarios.CreateFirstWhiteShift());
}
else if (args.Any(argument => argument == "--vertical"))
{
    M1SimulationSession.Run(Console.In, Console.Out);
}
else
{
    ClinicSandboxSession.Run(Console.In, Console.Out, args.Any(argument => argument == "--queue"));
}
