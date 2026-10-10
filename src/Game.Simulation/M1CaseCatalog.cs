namespace Game.Simulation;

public sealed record DiagnosisCandidate(
    string CandidateId,
    int PriorBasisPoints);

public readonly record struct DiagnosisEvidenceWeight(
    string CandidateId,
    int WeightBasisPoints);

public sealed record DiagnosisEvidence(
    string EvidenceId,
    IReadOnlyList<DiagnosisEvidenceWeight> Weights);

public readonly record struct DiagnosisSupport(
    string CandidateId,
    int SupportBasisPoints);

public sealed record DiagnosisAssessment(
    int ConfidenceBasisPoints,
    string TopCandidateId,
    IReadOnlyList<DiagnosisSupport> Supports);

public static class DiagnosisConfidenceCalculator
{
    public static DiagnosisAssessment Calculate(
        IReadOnlyList<DiagnosisCandidate> candidates,
        IReadOnlyList<DiagnosisEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(evidence);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("至少需要一个诊断候选", nameof(candidates));
        }

        var candidateIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var supportValues = new long[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (string.IsNullOrWhiteSpace(candidate.CandidateId))
            {
                throw new ArgumentException("诊断候选必须有稳定 ID", nameof(candidates));
            }

            if (candidate.PriorBasisPoints < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(candidates), "诊断先验不能为负数");
            }

            if (!candidateIndexes.TryAdd(candidate.CandidateId, index))
            {
                throw new ArgumentException($"诊断候选 ID 重复：{candidate.CandidateId}", nameof(candidates));
            }

            supportValues[index] = candidate.PriorBasisPoints;
        }

        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            if (string.IsNullOrWhiteSpace(item.EvidenceId))
            {
                throw new ArgumentException("诊断证据必须有稳定 ID", nameof(evidence));
            }

            if (!evidenceIds.Add(item.EvidenceId))
            {
                throw new ArgumentException($"诊断证据 ID 重复：{item.EvidenceId}", nameof(evidence));
            }

            var weightIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var weight in item.Weights)
            {
                if (!candidateIndexes.TryGetValue(weight.CandidateId, out var candidateIndex))
                {
                    throw new ArgumentException(
                        $"证据 {item.EvidenceId} 引用了未知候选：{weight.CandidateId}",
                        nameof(evidence));
                }

                if (!weightIds.Add(weight.CandidateId))
                {
                    throw new ArgumentException(
                        $"证据 {item.EvidenceId} 重复提供候选权重：{weight.CandidateId}",
                        nameof(evidence));
                }

                supportValues[candidateIndex] += weight.WeightBasisPoints;
            }
        }

        var supports = new DiagnosisSupport[candidates.Count];
        var totalSupport = 0L;
        var topSupport = 0L;
        var topCandidateIndex = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            var support = Math.Max(0L, supportValues[index]);
            if (support > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(evidence), "诊断支持度超出整数范围");
            }

            supports[index] = new DiagnosisSupport(candidates[index].CandidateId, (int)support);
            totalSupport += support;
            if (support > topSupport)
            {
                topSupport = support;
                topCandidateIndex = index;
            }
        }

        var confidence = totalSupport == 0
            ? 0
            : (int)Math.Min(10_000L, 10_000L * topSupport / Math.Max(1L, totalSupport));
        return new DiagnosisAssessment(
            confidence,
            candidates[topCandidateIndex].CandidateId,
            Array.AsReadOnly(supports));
    }
}

public sealed record M1CaseDefinition(
    string CaseId,
    bool RequiresLab,
    bool RequiresTreatment,
    int BaseRiskBasisPoints,
    int InitialDiagnosisConfidenceBasisPoints,
    long RevenueMinorUnits,
    IReadOnlyList<DiagnosisCandidate> Candidates,
    IReadOnlyList<DiagnosisEvidence> LabEvidence);

public static class M1CaseCatalog
{
    private static readonly M1CaseDefinition[] Definitions =
    [
        new(
            "URTI",
            true,
            true,
            2_200,
            4_500,
            120,
            Candidates(
                ("viral_upper_airway", 4_500),
                ("bacterial_throat", 3_200),
                ("allergic_airway", 2_300)),
            Evidence(
                ("basic_airway_panel", ("viral_upper_airway", 3_000), ("bacterial_throat", -1_500), ("allergic_airway", -1_000)))),
        new(
            "FLU_LIKE",
            true,
            true,
            4_200,
            4_500,
            130,
            Candidates(
                ("viral_systemic", 4_500),
                ("dehydration_pattern", 3_000),
                ("bacterial_systemic", 2_500)),
            Evidence(
                ("basic_systemic_panel", ("viral_systemic", 5_000), ("dehydration_pattern", -1_000), ("bacterial_systemic", -1_000)))),
        new(
            "THROAT_INFECTION",
            true,
            true,
            5_200,
            4_500,
            140,
            Candidates(
                ("bacterial_throat", 4_500),
                ("viral_throat", 3_000),
                ("irritation_pattern", 2_500)),
            Evidence(
                ("basic_throat_panel", ("bacterial_throat", 5_500), ("viral_throat", -1_000), ("irritation_pattern", -500)))),
        new(
            "ALLERGY",
            true,
            true,
            3_500,
            4_500,
            125,
            Candidates(
                ("allergic_reaction", 4_500),
                ("viral_upper_airway", 3_500),
                ("irritant_pattern", 2_000)),
            Evidence(
                ("basic_reaction_panel", ("allergic_reaction", 5_000), ("viral_upper_airway", -1_200), ("irritant_pattern", -800)))),
        new(
            "DEHYDRATION",
            false,
            true,
            1_800,
            8_500,
            90,
            Candidates(
                ("dehydration_pattern", 8_500),
                ("viral_systemic", 1_000),
                ("other_fluid_loss", 500)),
            Array.Empty<DiagnosisEvidence>()),
        new(
            "HIGH_RISK_ABDOMINAL",
            true,
            true,
            7_600,
            4_200,
            180,
            Candidates(
                ("urgent_abdominal", 4_200),
                ("benign_abdominal", 3_500),
                ("other_abdominal", 2_300)),
            Evidence(
                ("basic_abdominal_panel", ("urgent_abdominal", 6_500), ("benign_abdominal", -2_000), ("other_abdominal", -1_500)))),
    ];

    private static readonly IReadOnlyDictionary<string, M1CaseDefinition> ById =
        Definitions.ToDictionary(definition => definition.CaseId, StringComparer.Ordinal);

    public static IReadOnlyList<M1CaseDefinition> All { get; } = Array.AsReadOnly(Definitions);

    public static bool TryGet(string caseId, out M1CaseDefinition definition) =>
        ById.TryGetValue(caseId, out definition!);

    private static IReadOnlyList<DiagnosisCandidate> Candidates(
        params (string CandidateId, int PriorBasisPoints)[] values) =>
        Array.AsReadOnly(values
            .Select(value => new DiagnosisCandidate(value.CandidateId, value.PriorBasisPoints))
            .ToArray());

    private static IReadOnlyList<DiagnosisEvidence> Evidence(
        (string EvidenceId, (string CandidateId, int WeightBasisPoints) First,
            (string CandidateId, int WeightBasisPoints) Second,
            (string CandidateId, int WeightBasisPoints) Third) value) =>
        Array.AsReadOnly(new[]
        {
            new DiagnosisEvidence(
                value.EvidenceId,
                Array.AsReadOnly(new[]
                {
                    new DiagnosisEvidenceWeight(value.First.CandidateId, value.First.WeightBasisPoints),
                    new DiagnosisEvidenceWeight(value.Second.CandidateId, value.Second.WeightBasisPoints),
                    new DiagnosisEvidenceWeight(value.Third.CandidateId, value.Third.WeightBasisPoints),
                })),
        });
}
