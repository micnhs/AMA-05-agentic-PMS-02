using System.Globalization;
using Cohors.Domain;

namespace Cohors.Application;

public sealed record AgentEnvelope(string InputHash, string ProductId, AgentInput Input);
public sealed record AgentResultEnvelope(string InputHash, AgentOutput Output);

public static class AgentInstructions
{
    public static string For(AgentRole role) => role switch
    {
        AgentRole.Ingestion => "Produktzuordnung und Quellenunabhaengigkeit pruefen.",
        AgentRole.CausalityRisk => "Begruendeten medizinisch-technischen Entwurf erstellen.",
        AgentRole.EvidenceAssurance => "Unabhaengig Belege, Gegenhypothesen und Datenluecken pruefen.",
        AgentRole.VigilanceAuthoring => "Bericht und strukturierte Vigilance-Texte aus bestehendem Assessment mit exakten Claim-Referenzen; keine neuen Sicherheitsbehauptungen, Fakten oder Termine.",
        AgentRole.TrendSignal => "Erlaeutere die deterministischen Werte, Nennerpassung, ausgeschlossene unbekannte Daten, Annahmen und Unsicherheit. Keine automatische Art.-88-Entscheidung.",
        AgentRole.PsurAuthoring => "Deutscher PSUR-Entwurf mit N/N2/N3/N4, getrennten Regionen und unbekannten Daten. Alle Kennzahlen mit [metric:Periode:Region:events|exposure=Wert] (unknown wenn unbekannt), heutige F02-Kodierungen mit [metric:Periode:Region:deathCoded=Wert], historische nicht releasevalidierte F02-Angaben separat mit [metric:Periode:Region:historicalDeathClaimed=Wert]. Bei einer gebundenen Gruppe alle Marker zusaetzlich mit Produkt qualifizieren: [metric:Produkt:Periode:Region:Kennzahl=Wert]. Jede Basic-UDI einzeln auswerten; keine produktuebergreifende Ratenaddition. Historische Originalangabe und heute vorgenommene retrospektive Kodierung strikt trennen; weder alte Release-Gueltigkeit noch alte Hierarchie aus heutigen Metadaten erfinden. Unvollstaendige Kodierung ist kein Null-Todesfallnachweis. Fallbelege [case:ID@Version], Dokumentbelege [evidence:ID@Version]. Jeden relevanten offenen/aktualisierten/nachtraeglichen BS-Vorgang mit [psurReview:ID@Version] belegen; Stichtagsstatus, Freeze-Status und Antwortzeitpunkt getrennt benennen. Gruppenweite BS-Historie nicht als Einzelfamilieninformation darstellen; fruehere Serienpflichten nicht schliessen. Medizinisch-technisch begruenden, keine autonome Risikoakzeptanz.",
        _ => throw new DomainException("agent_role", "Nicht freigegebene Agentenrolle.")
    };
}

public static class AgentBoundary
{
    public const int MaximumNarrativeLength = 200000;

    public static string Validate(AgentInput input)
    {
        Guard.Against(!Enum.IsDefined(input.Role) || !Enum.IsDefined(input.Mode) || input.Revision is < 0 or > 2,
            "agent_contract", "Agentenrolle, Modus oder Recherchebudget ungueltig.");
        Guard.Against(new object?[] { input.Case, input.Trend, input.Snapshot }.Count(x => x is not null) != 1,
            "agent_context", "Genau ein gebundener Fall-/Trend-/PSUR-Kontext ist erforderlich.");
        var product = input.Case?.ProductId ?? input.Trend?.ProductId ?? input.Snapshot!.ProductId;
        Guard.Against(input.Case is { Synthetic: false }, "synthetic_only", "Nur synthetische Agentenkontexte.");
        Guard.Against(input.Role == AgentRole.TrendSignal ? input.Trend is null :
            input.Role == AgentRole.PsurAuthoring ? input.Snapshot is null : input.Case is null,
            "agent_context", "Die Rolle passt nicht zum Auftragskontext.");
        var products = input.Snapshot is { } snapshot ? PsurGroupRules.Products(snapshot) : [product];
        Guard.Against(input.Snapshot is { Group: not null } grouped && PsurGroupRules.Check(grouped).Length != 0,
            "agent_group_integrity", "Gruppierter Agentenkontext ist nicht konsistent gebunden.", 403);
        var contextEvidence = input.Case?.Evidence ?? (input.Snapshot is null ? [] : PsurGroupRules.Documents(input.Snapshot));
        Guard.Against(input.Evidence is null || input.Evidence.Length > 100 ||
            input.Evidence.Concat(contextEvidence).Any(e => !e.Approved || !products.Contains(e.ProductId)),
            "retrieval_scope", "Nicht freigegebener oder zu grosser Evidenzkontext.", 403);
        foreach (var evidence in input.Evidence!.Concat(contextEvidence)) EvidenceRules.RequireIntegrity(evidence);
        Guard.Text(input.Instruction, "Agentenauftrag", 1, 4000);
        return product;
    }

    public static void RequireProducts(AgentInput input, string[] allowed)
    {
        var lead = Validate(input);
        var products = input.Snapshot is null ? [lead] : PsurGroupRules.Products(input.Snapshot);
        Guard.Against(products.Any(p => !allowed.Contains(p)), "agent_product",
            "Der Runtime-Scope muss alle eingefrorenen Gruppenmitglieder erlauben.", 403);
    }

    public static void ValidateNarrative(string? text, string field, int minimumLength)
    {
        Guard.Against(text is { Length: > MaximumNarrativeLength }, "invalid_field",
            $"{field}: hoechstens {MaximumNarrativeLength} Zeichen einschliesslich Leerraum; kein automatisches Abschneiden.");
        Guard.Text(text, field, minimumLength, MaximumNarrativeLength);
    }

    public static void ValidateResult(AgentInput input, AgentOutput output)
    {
        if (input.Mode == AgentMode.Live)
        {
            var provenance = output.ModelProvenance;
            Guard.Against(provenance is null || provenance.Role != input.Role ||
                output.Model != provenance.Deployment + "@" + provenance.ModelVersion ||
                provenance.EvidenceSha256 != Canonical.Hash(input.Evidence) ||
                string.IsNullOrWhiteSpace(provenance.Deployment) || string.IsNullOrWhiteSpace(provenance.ModelVersion) ||
                provenance.WorkloadTenantId == Guid.Empty || provenance.WorkloadClientId == Guid.Empty ||
                provenance.WorkloadPrincipalId == Guid.Empty ||
                new[] { provenance.BindingSha256, provenance.ReviewSha256, provenance.QualifiedReviewSha256,
                    provenance.PromptSha256, provenance.PipelineConfigurationSha256 }
                    .Any(hash => hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)),
                "model_output_provenance", "Live-Ergebnis ohne passende Rollen-, Modell-, Review- und Evidenzbindung.", 409);
            AgentModelIdentity.RequireObservedModel(provenance!);
            Guard.Against(input.ExpectedActivation is not null &&
                !AgentModelIdentity.SameQualifiedBinding(provenance!, input.ExpectedActivation),
                "model_activation_mismatch", "API und AgentHost bestaetigen nicht dieselbe gepruefte Aktivierung.", 409);
        }
        ValidateNarrative(output.Narrative, "Agentenentwurf", 1);
        Guard.Against(output.Findings is null || output.Tools is null || output.Findings.Length > 100 ||
            output.Findings.Any(f => f.Severity is not ("Critical" or "Major" or "Minor")),
            "agent_output", "Qualitaetsbefunde sind strukturell ungueltig.");
        Guard.Against(input.Role == AgentRole.CausalityRisk && output.Assessment is null,
            "agent_output", "Tragender Assessment-Entwurf fehlt.");
        if (input.Role == AgentRole.VigilanceAuthoring)
        {
            Guard.Against(output.Vigilance is null, "vigilance_fields", "Pruefbare strukturierte Meldungstexte fehlen; kein Ersatz durch Blindvorbefuellung.");
            foreach (var field in new[] { output.Vigilance!.PreliminaryAnalysis, output.Vigilance.FurtherInvestigation })
            {
                Guard.Against(field is null || field.ClaimIds is not { Length: > 0 },
                    "vigilance_sources", "Meldungstext braucht konkrete Claim-Referenzen.");
                ValidateNarrative(field!.Text, "Begruendeter Meldungstext", 40);
                Guard.Against(field.ClaimIds.Any(id => !input.Case!.CurrentAssessment!.Content.Claims.Any(claim =>
                    claim.Id == id && claim.Citations.Length > 0 &&
                    claim.Citations.All(citation => EvidenceRules.CitationMatches(citation, input.Evidence)))),
                    "vigilance_sources", "Meldungstext verweist auf fehlende oder nicht verifizierbare Assessment-Fundstellen.");
            }
        }

    }
}

public static class AgentModelIdentity
{
    public const string MetadataSource = "AzureOpenAI.ChatCompletion.Model.DatedSnapshot";

    public static string RequireDatedSnapshotVersion(string? modelId, string code)
    {
        if (modelId is not { Length: > 11 and <= 200 } || modelId[^11] != '-' ||
            !char.IsAsciiLetterOrDigit(modelId[0]) || !char.IsAsciiLetterOrDigit(modelId[^12]) ||
            modelId[..^11].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) ||
            !DateOnly.TryParseExact(modelId[^10..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new DomainException(code,
                "Keine eindeutige datierte Provider-Snapshotkennung; Alias oder unbekanntes Versionsformat ist nicht aktivierbar.", 409);
        return modelId[^10..];
    }

    public static AgentModelObservation RequireObservedModel(AgentModelProvenance provenance)
    {
        var observed = provenance.ObservedModel ??
            throw new DomainException("model_response_identity_missing", "Die tatsaechliche Provider-Antwortidentitaet fehlt.", 409);
        Guard.Against(observed.Source != MetadataSource || string.IsNullOrWhiteSpace(observed.ResponseId) ||
            observed.ResponseId.Length > 200 || observed.ResponseId.Any(char.IsControl),
            "model_response_identity_insufficient", "Providerquelle oder eindeutige Antwortreferenz fehlt.", 409);
        var version = RequireDatedSnapshotVersion(observed.ModelId, "model_response_identity_insufficient");
        Guard.Against(observed.ModelVersion != version, "model_response_identity_ambiguous",
            "Beobachtete Modellkennung und Version sind widerspruechlich.", 409);
        Guard.Against(observed.ModelId != provenance.ExpectedResponseModelId || version != provenance.ModelVersion,
            "model_response_identity_mismatch", "Das tatsaechliche Antwortmodell entspricht nicht dem qualifizierten Modellstand.", 409);
        return observed;
    }

    public static bool SameQualifiedBinding(AgentModelProvenance actual, AgentModelProvenance expected) =>
        expected.ObservedModel is null && Canonical.Hash(actual with { ObservedModel = null }) == Canonical.Hash(expected);
}
