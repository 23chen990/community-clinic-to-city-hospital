using Game.Simulation.Demo;

if (args.Any(argument => argument == "--vertical"))
{
    HospitalVerticalSliceSession.Run(Console.In, Console.Out);
}
else
{
    ClinicSandboxSession.Run(Console.In, Console.Out, args.Any(argument => argument == "--queue"));
}
