using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

// CURRENT §6.6, SIM-002: compare the authoritative replay hash at every
// simulation Tick while the presentation clock uses different frame rhythms,
// speed controls, and a real pause/resume interval.
public sealed class FullShiftReplayTests
{
    private const long ArrangeNextDayCommandId = 100;
    private const long RiskPriorityCommandId = 200;
    private const int DispatchCommandTick = M1PlayScenarios.MainPressureWindowEndTick;

    [Fact]
    public void DifferentFramePacingAndSpeedControlsMatchEveryTickAcrossNextDay()
    {
        var config = M1PlayScenarios.FirstWhiteShiftConfig;

        var traces = new[]
        {
            RunReplay(
                seed: M1PlayScenarios.Seed,
                config,
                frameMilliseconds: [1, 99, 16, 33, 250, 7, 401],
                controls: new ReplayControls(
                    controlIdBase: 10_000,
                    pauseTick: 3_000,
                    speedChanges:
                    [
                        new(0, SimulationSpeed.Half),
                        new(1_200, SimulationSpeed.Normal),
                        new(2_400, SimulationSpeed.Double),
                        new(3_800, SimulationSpeed.Half),
                    ])),
            RunReplay(
                seed: M1PlayScenarios.Seed,
                config,
                frameMilliseconds: [100, 100, 100, 17, 1_000, 3, 47],
                controls: new ReplayControls(
                    controlIdBase: 20_000,
                    pauseTick: 2_100,
                    speedChanges:
                    [
                        new(0, SimulationSpeed.Double),
                        new(900, SimulationSpeed.Half),
                        new(2_700, SimulationSpeed.Normal),
                    ])),
            RunReplay(
                seed: M1PlayScenarios.Seed,
                config,
                frameMilliseconds: [3, 17, 41, 89, 233, 377, 610, 997],
                controls: new ReplayControls(
                    controlIdBase: 30_000,
                    pauseTick: 1_500,
                    speedChanges:
                    [
                        new(0, SimulationSpeed.Normal),
                        new(600, SimulationSpeed.Double),
                        new(3_300, SimulationSpeed.Half),
                    ])),
        };

        var expectedTicks = traces[0].Hashes.Keys.OrderBy(tick => tick).ToArray();
        Assert.NotEmpty(expectedTicks);
        foreach (var trace in traces)
        {
            Assert.Equal(expectedTicks, trace.Hashes.Keys.OrderBy(tick => tick));
            foreach (var tick in expectedTicks)
            {
                Assert.Equal(traces[0].Hashes[tick], trace.Hashes[tick]);
            }

            Assert.Equal(2, trace.Simulation.Day);
            Assert.Equal(ShiftPhase.Operating, trace.Simulation.Phase);
            Assert.Single(trace.Simulation.CompletedShifts);
            Assert.True(trace.Simulation.CompletedShifts[0].CompletedTick >=
                config.ShiftLengthTicks + config.ClosingDurationTicks);
            Assert.Contains(trace.Simulation.Events, entry =>
                entry.EventType == SimulationEventType.ShiftClosingStarted &&
                entry.Tick == config.ShiftLengthTicks);
            Assert.Contains(trace.Simulation.Events, entry =>
                entry.EventType == SimulationEventType.PatientEntered &&
                entry.PatientId == "患者007");

            var businessCommands = trace.Simulation.CommandLog
                .Where(command => command.Type is SimulationCommandType.DispatchLab or
                    SimulationCommandType.ArrangeNextDay)
                .ToArray();
            Assert.Equal(2, businessCommands.Length);
            var dispatchCommand = Assert.Single(businessCommands,
                command => command.Type == SimulationCommandType.DispatchLab);
            Assert.Equal(RiskPriorityCommandId, dispatchCommand.CommandId);
            Assert.Equal(DispatchCommandTick, dispatchCommand.SubmittedTick);
            var arrangeCommand = Assert.Single(businessCommands,
                command => command.Type == SimulationCommandType.ArrangeNextDay);
            Assert.Equal(ArrangeNextDayCommandId, arrangeCommand.CommandId);
            Assert.DoesNotContain(
                trace.Simulation.CommandLog,
                command => (command.Type is SimulationCommandType.Pause or SimulationCommandType.Resume or
                    SimulationCommandType.SetSpeed) &&
                    (command.CommandId == ArrangeNextDayCommandId ||
                     command.CommandId == RiskPriorityCommandId));

            Assert.Equal(CommandResultStatus.Accepted,
                trace.Simulation.GetCommandResult(RiskPriorityCommandId).Status);
        }

        var arrangeTicks = traces
            .Select(trace => trace.Simulation.CommandLog
                .Single(command => command.Type == SimulationCommandType.ArrangeNextDay)
                .SubmittedTick)
            .ToArray();
        Assert.All(arrangeTicks, tick => Assert.Equal(arrangeTicks[0], tick));

        var dispatchTicks = traces
            .Select(trace => trace.Simulation.CommandLog
                .Single(command => command.Type == SimulationCommandType.DispatchLab)
                .SubmittedTick)
            .ToArray();
        Assert.All(dispatchTicks, tick => Assert.Equal(DispatchCommandTick, tick));
    }

    private static ReplayTrace RunReplay(
        int seed,
        M1SimulationConfig config,
        IReadOnlyList<int> frameMilliseconds,
        ReplayControls controls)
    {
        var simulation = M1PlayScenarios.CreateFirstWhiteShift(seed, config);
        var clock = new SimulationFrameClock();
        var hashes = new SortedDictionary<int, string>
        {
            [simulation.Tick] = simulation.StateHash,
        };
        var arrangeSubmitted = false;
        var frameIndex = 0;

        var dispatchSubmitted = false;
        while ((!HasDayTwoPatientWindow(simulation) || simulation.Day < 2) && frameIndex < 100_000)
        {
            if (simulation.Paused)
            {
                controls.Schedule(simulation);
                var pausedTicks = clock.AdvanceMilliseconds(
                    frameMilliseconds[frameIndex % frameMilliseconds.Count],
                    paused: true,
                    speed: simulation.Speed);
                Assert.Equal(0, pausedTicks);

                // Resume is a control command submitted at the paused
                // authoritative Tick. AdvanceTick applies it and then moves
                // exactly one Tick; no presentation time is consumed here.
                if (simulation.Paused)
                {
                    AdvanceAndRecord(simulation, hashes);
                }

                frameIndex++;
                continue;
            }

            SubmitBusinessCommandsIfDue(simulation, ref dispatchSubmitted, ref arrangeSubmitted);
            controls.Schedule(simulation);
            var ticks = clock.AdvanceMilliseconds(
                frameMilliseconds[frameIndex % frameMilliseconds.Count],
                paused: false,
                speed: simulation.Speed);

            for (var index = 0; index < ticks; index++)
            {
                if (simulation.Paused)
                {
                    break;
                }

                SubmitBusinessCommandsIfDue(simulation, ref dispatchSubmitted, ref arrangeSubmitted);
                controls.Schedule(simulation);
                AdvanceAndRecord(simulation, hashes);

                // A pause command is allowed to consume this presentation
                // frame without consuming another authoritative Tick.
                if (simulation.Paused)
                {
                    break;
                }

                if (simulation.Day >= 2)
                {
                    break;
                }
            }

            frameIndex++;
        }

        Assert.True(simulation.Day >= 2, "replay did not reach the next day within the frame budget");
        Assert.True(HasDayTwoPatientWindow(simulation),
            "replay did not reach the first next-day patient window within the frame budget");
        Assert.True(dispatchSubmitted);
        Assert.True(arrangeSubmitted);
        return new ReplayTrace(simulation, hashes);
    }

    private static void SubmitBusinessCommandsIfDue(
        HospitalSimulation simulation,
        ref bool dispatchSubmitted,
        ref bool arrangeSubmitted)
    {
        if (!dispatchSubmitted && simulation.Tick == DispatchCommandTick)
        {
            var dispatchResult = simulation.Submit(new SimulationCommand(
                RiskPriorityCommandId,
                simulation.Tick,
                SimulationCommandType.DispatchLab,
                DispatchPlan: LabDispatchPlan.RiskPriority));
            Assert.Equal(CommandResultStatus.Deferred, dispatchResult.Status);
            dispatchSubmitted = true;
        }

        if (arrangeSubmitted || simulation.Phase != ShiftPhase.Settled)
        {
            return;
        }

        var result = simulation.Submit(new SimulationCommand(
            ArrangeNextDayCommandId,
            simulation.Tick,
            SimulationCommandType.ArrangeNextDay));
        Assert.Equal(CommandResultStatus.Deferred, result.Status);
        arrangeSubmitted = true;
    }

    private static bool HasDayTwoPatientWindow(HospitalSimulation simulation) =>
        simulation.Day >= 2 && simulation.Events.Any(entry =>
            entry.EventType == SimulationEventType.PatientEntered &&
            entry.PatientId == "患者007");

    private static void AdvanceAndRecord(
        HospitalSimulation simulation,
        IDictionary<int, string> hashes)
    {
        var previousTick = simulation.Tick;
        simulation.AdvanceTick();
        if (simulation.Tick == previousTick)
        {
            return;
        }

        Assert.Equal(previousTick + 1, simulation.Tick);
        Assert.True(hashes.TryAdd(simulation.Tick, simulation.StateHash));
    }

    private sealed record ReplayTrace(
        HospitalSimulation Simulation,
        IReadOnlyDictionary<int, string> Hashes);

    private sealed class ReplayControls
    {
        private readonly int pauseTick;
        private readonly IReadOnlyList<SpeedChange> speedChanges;
        private readonly HashSet<int> submittedSpeedTicks = [];
        private bool pauseSubmitted;
        private bool resumeSubmitted;
        private long nextControlId;

        public ReplayControls(
            long controlIdBase,
            int pauseTick,
            IReadOnlyList<SpeedChange> speedChanges)
        {
            this.pauseTick = pauseTick;
            this.speedChanges = speedChanges;
            nextControlId = controlIdBase;
        }

        public void Schedule(HospitalSimulation simulation)
        {
            foreach (var change in speedChanges.Where(change => change.Tick == simulation.Tick))
            {
                if (!submittedSpeedTicks.Add(change.Tick))
                {
                    continue;
                }

                Assert.NotEqual(ArrangeNextDayCommandId, nextControlId);
                simulation.Submit(SimulationCommand.SetSpeed(
                    nextControlId++,
                    simulation.Tick,
                    change.Speed));
            }

            if (!pauseSubmitted && !simulation.Paused && simulation.Tick == pauseTick)
            {
                Assert.NotEqual(ArrangeNextDayCommandId, nextControlId);
                simulation.Submit(SimulationCommand.Pause(nextControlId++, simulation.Tick));
                pauseSubmitted = true;
                return;
            }

            if (pauseSubmitted && !resumeSubmitted && simulation.Paused)
            {
                Assert.NotEqual(ArrangeNextDayCommandId, nextControlId);
                simulation.Submit(SimulationCommand.Resume(nextControlId++, simulation.Tick));
                resumeSubmitted = true;
            }
        }
    }

    private readonly record struct SpeedChange(int Tick, SimulationSpeed Speed);
}
