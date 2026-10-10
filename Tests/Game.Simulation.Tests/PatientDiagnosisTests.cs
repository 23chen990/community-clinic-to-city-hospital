using Game.Simulation;
using Xunit;

namespace Game.Simulation.Tests;

public sealed class PatientDiagnosisTests
{
    [Fact]
    public void EvidenceSupportUsesNonNegativeSumsAndFlooredConfidence()
    {
        var candidates = new[]
        {
            new DiagnosisCandidate("A", 4_000),
            new DiagnosisCandidate("B", 3_000),
            new DiagnosisCandidate("C", 3_000),
        };
        var evidence = new[]
        {
            new DiagnosisEvidence("exam-1", new[]
            {
                new DiagnosisEvidenceWeight("A", 1_500),
                new DiagnosisEvidenceWeight("B", -4_000),
            }),
            new DiagnosisEvidence("exam-2", new[]
            {
                new DiagnosisEvidenceWeight("A", -250),
                new DiagnosisEvidenceWeight("C", 500),
            }),
        };

        var assessment = DiagnosisConfidenceCalculator.Calculate(candidates, evidence);

        Assert.Equal("A", assessment.TopCandidateId);
        Assert.Equal(5_250, assessment.Supports.Single(support => support.CandidateId == "A").SupportBasisPoints);
        Assert.Equal(0, assessment.Supports.Single(support => support.CandidateId == "B").SupportBasisPoints);
        Assert.Equal(3_500, assessment.Supports.Single(support => support.CandidateId == "C").SupportBasisPoints);
        Assert.Equal(6_000, assessment.ConfidenceBasisPoints);
    }

    [Fact]
    public void EqualTopSupportUsesCatalogOrderForStableWinner()
    {
        var candidates = new[]
        {
            new DiagnosisCandidate("FIRST", 5_000),
            new DiagnosisCandidate("SECOND", 5_000),
        };

        var assessment = DiagnosisConfidenceCalculator.Calculate(candidates, Array.Empty<DiagnosisEvidence>());

        Assert.Equal("FIRST", assessment.TopCandidateId);
        Assert.Equal(5_000, assessment.ConfidenceBasisPoints);
        Assert.Equal(new[] { "FIRST", "SECOND" }, assessment.Supports.Select(support => support.CandidateId));
    }

    [Fact]
    public void M1CatalogHasSixCasesAndAtLeastThreeLabCases()
    {
        Assert.Equal(6, M1CaseCatalog.All.Count);
        Assert.True(M1CaseCatalog.All.Count(definition => definition.RequiresLab) >= 3);
        Assert.Contains(M1CaseCatalog.All, definition => definition.RequiresLab && definition.LabEvidence.Count > 0);
    }

    [Fact]
    public void PublishedLabEvidenceRaisesConfidenceBeforeFollowUp()
    {
        var simulation = HospitalSimulation.CreateBaseline(seed: 7);

        Assert.True(simulation.AdvanceUntil(
            snapshot => snapshot.Lab.ResultBacklog > 0,
            2_000));

        var pending = simulation.Snapshot.Patients
            .Where(patient => patient.Stage == PatientStage.ResultPending)
            .OrderBy(patient => patient.PatientId, StringComparer.Ordinal)
            .First();
        var confidenceBefore = pending.DiagnosisConfidenceBasisPoints;

        Assert.True(simulation.AdvanceUntil(
            _ => simulation.Events.Any(entry =>
                entry.EventType == SimulationEventType.ResultPublished && entry.PatientId == pending.PatientId),
            2_000));

        var afterPublish = simulation.Snapshot.Patients.Single(patient => patient.PatientId == pending.PatientId);
        Assert.True(afterPublish.DiagnosisConfidenceBasisPoints > confidenceBefore);
        Assert.True(afterPublish.Stage is PatientStage.WaitingFollowUp or PatientStage.InFollowUp);
    }

    [Fact]
    public void OutsourcedResultAppliesEvidenceBeforeReturningToFollowUp()
    {
        var simulation = HospitalSimulation.CreateScenario(
            seed: 29,
            arrivals:
            [
                new PatientArrival(1, 1, "URTI"),
                new PatientArrival(2, 1, "URTI"),
            ],
            config: new M1SimulationConfig
            {
                LabAnalysisServiceTicks = 100,
                NormalPublishIntervalTicks = 5_000,
                RiskIncreasePerWaitingTick = 0,
                PatienceLossPerWaitingTick = 0,
            });

        Assert.True(simulation.AdvanceUntil(snapshot => snapshot.Lab.AnalysisQueue > 0, 2_000));
        var outsourced = simulation.Snapshot.Patients
            .Where(patient => patient.Stage == PatientStage.WaitingLabAnalysis)
            .OrderBy(patient => patient.PatientId, StringComparer.Ordinal)
            .First();
        var confidenceBefore = outsourced.DiagnosisConfidenceBasisPoints;

        simulation.Submit(SimulationCommand.DispatchLab(1, simulation.Tick, LabDispatchPlan.OutsourceSamples));
        Assert.True(simulation.AdvanceUntil(
            _ => simulation.Events.Any(entry =>
                entry.EventType == SimulationEventType.OutsourcedResultReturned && entry.PatientId == outsourced.PatientId),
            2_000));

        var afterReturn = simulation.Snapshot.Patients.Single(patient => patient.PatientId == outsourced.PatientId);
        Assert.True(afterReturn.DiagnosisConfidenceBasisPoints > confidenceBefore);
        Assert.True(afterReturn.Stage is PatientStage.WaitingFollowUp or PatientStage.InFollowUp);
    }
}
