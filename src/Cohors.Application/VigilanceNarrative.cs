using Cohors.Domain;

namespace Cohors.Application;

public static class VigilanceNarrative
{
    public static string FromAssessment(SafetyCase c, string language)
    {
        Guard.Against(language is not ("de" or "en"), "report_language",
            "Der Pilot unterstuetzt begruendete Berichte nur auf Deutsch/Englisch, keine umetikettierten Uebersetzungen.");
        var excerpts = string.Join("\n\n", c.CurrentAssessment!.Content.Sections
            .Where(s => s.Key is "facts" or "causality" or "conclusion").Select(s => s.Title + "\n" + s.Text));
        return language == "de"
            ? $"""
                SYNTHETISCHE DEMONSTRATION - NICHT ZUR REALEN BEHOERDENUEBERMITTLUNG.

                Fall {c.Reference}; Produktfamilie {c.ProductId}.
                Unveraenderte, begruendete Auszuege aus der aktuellen Assessment-Version:

                {excerpts}

                Kausalitaet und Vollstaendigkeit bleiben vorlaeufig. Fehlende Wahrscheinlichkeiten sind kein Nullrisiko.
                Gezielte Untersuchungen und Fristen laufen unabhaengig weiter. Medical Safety und Regulatory Affairs
                muessen diesen konkreten Bericht getrennt pruefen und freigeben.
                """
            : $"""
                SYNTHETIC DEMONSTRATION - NOT FOR REAL AUTHORITY SUBMISSION.

                Case {c.Reference}; product family {c.ProductId}.
                This preliminary report preserves the documented observations and the current medical/technical assessment.
                A complaint alone does not establish device causality. Competing technical and clinical mechanisms have not
                been excluded. Missing probability data must not be interpreted as zero risk.

                ORIGINAL-LANGUAGE ASSESSMENT EXCERPTS (verbatim; not a certified translation):
                {excerpts}
                END OF ORIGINAL-LANGUAGE EXCERPTS.

                Targeted follow-up is required to distinguish competing explanations. Unknown findings remain explicitly
                unresolved; investigation and vigilance deadlines continue independently. No final benefit-risk acceptance
                is expressed. Separate medical and regulatory reviewers must assess the exact package before simulated
                transmission. A regulator-ready translation of the original-language excerpts requires human review.
                """;
    }
}
