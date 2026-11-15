using Cohors.Domain;

namespace Cohors.Application;

public static class ManualAssessment
{
    public static AssessmentContent Create(SafetyCase c, ManualAssessmentCommand input)
    {
        Guard.Text(input.Facts, "Dokumentierte Fakten", 30, 4000);
        Guard.Text(input.Analysis, "Medizinisch-technische Erstbewertung", 40, 4000);
        Guard.Text(input.Conclusion, "Begruendete vorlaeufige Empfehlung", 40, 4000);
        var source = c.Evidence.First(e => e.Kind == "Complaint");
        var citation = new Citation(source.Id, source.Hash, source.Locator, source.Content);
        AnalysisSection Section(string key, string title, string text) => new(key, title, text, ["M1"]);
        return new([
            Section("context", "Fallkontext", $"Menschlicher vorlaeufiger Mindestbericht zu {c.Reference}, Produkt {c.ProductId}. Keine KI- oder Search-Nutzung."),
            Section("facts", "Dokumentierte Fakten", input.Facts),
            Section("technical", "Technischer Untersuchungsstand", "Technische Ursachen sind nicht abschliessend belegt. Originalbefunde und Produktzuordnung muessen unabhaengig geprueft werden."),
            Section("medical", "Medizinischer Untersuchungsstand", "Die menschliche Erstbewertung ist vorlaeufig. Fehlende gezielte klinische Befunde bleiben als offene Untersuchung dokumentiert."),
            Section("causality", "Erstbewertung und Gegenhypothesen", input.Analysis + "\nKonkurrierende technische und klinische Ursachen sind nicht ausgeschlossen. Ein Beschwerdehinweis allein beweist keine Produktkausalitaet."),
            Section("risk", "Risiko und Unsicherheit", "Eine Eintrittswahrscheinlichkeit ist aus diesem unvollstaendigen Einzelfall nicht bestimmbar. Fehlende Information bedeutet nicht Nullrisiko."),
            Section("benefitRisk", "Nutzen-Risiko-Grenze", "Dieser vorlaeufige Mindestbericht enthaelt keine abschliessende Nutzen-Risiko-Akzeptanz oder Produktfamilienfreigabe."),
            Section("regulatory", "Regulatorisches Vorgehen", "Die unabhhaengige Frist laeuft weiter. Unverzuegliche vorlaeufige Bearbeitung erfolgt ausschliesslich nach medizinischer und getrennter RA-Freigabe."),
            Section("options", "Handlungsoptionen", "Prioritaet hat die fristgerechte Mindestinformation bei unveraenderten menschlichen Gates. Gezielte Untersuchung und spaetere Folgemeldung laufen parallel."),
            Section("information", "Gezielter Informationsbedarf", "Nur fuer die Ursachenabgrenzung erforderliche Befundausschnitte nach zweifacher menschlicher Genehmigung simuliert anfordern; keine vollstaendige Patientenakte."),
            Section("conclusion", "Menschliche Schlussfolgerung", input.Conclusion),
            Section("review", "Freigabebedarf", "Dieser vom Menschen erstellte Entwurf ist noch keine Fall- oder Meldefreigabe. Beide getrennten berechtigten Personen pruefen das konkrete Paket.")
        ], [
            new("M1", "Die synthetische Beschwerde liegt als versionierter Originalhinweis vor.", "Observed", [citation])
        ], [
            new("Vorlaeufiges menschliches Review", input.Conclusion, Roles.Medical,
                "Unverzueglich", "Keine endgueltige Kausalitaets- oder Sicherheitsfeststellung.", ["M1"])
        ], ["Ursachen und gezielte klinische/technische Befunde sind noch offen."], null,
            "Keine belastbare Wahrscheinlichkeitsbasis; nicht als Nullrisiko interpretieren.");
    }
}
