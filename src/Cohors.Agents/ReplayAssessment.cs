using Cohors.Domain;

namespace Cohors.Agents;

public static class ReplayAssessment
{
    public static AssessmentContent Create(SafetyCase c, Evidence[] evidence)
    {
        var complaint = evidence.First(e => e.Kind == "Complaint");
        var spec = evidence.FirstOrDefault(e => e.Kind == "Specification")
            ?? throw new DomainException("specification_missing", "Technische Spezifikation fehlt; keine Beschwerde als Ersatzbeleg verwenden.");
        var test = evidence.FirstOrDefault(e => e.Kind == "BenchTest")
            ?? throw new DomainException("test_evidence_missing", "Pruefevidenz fehlt; menschliche Erstbewertung erforderlich.");
        var risk = evidence.FirstOrDefault(e => e.Kind == "RiskFile")
            ?? throw new DomainException("risk_evidence_missing", "Risikobaseline fehlt; menschliche Erstbewertung erforderlich.");
        var clinical = evidence.FirstOrDefault(e => e.Kind == "ClinicalFollowUp");
        Citation Cite(Evidence e) => new(e.Id, e.Hash, e.Locator, e.Content);
        Claim[] claims =
        [
            new("C1", "Der synthetische Eingang beschreibt das folgende Ereignis: " + complaint.Content, "Observed", [Cite(complaint)]),
            new("C2", "Die spezifizierte Konstruktion und Zweckbestimmung bilden den technischen Vergleichsrahmen.", "Observed", [Cite(spec)]),
            new("C3", "Pruefergebnisse gelten ausschliesslich fuer die dokumentierten Randbedingungen.", "Inference", [Cite(test)]),
            new("C4", "Ein Produktbeitrag ist eine zu untersuchende Hypothese; alternative Ursachen sind nicht ausgeschlossen.", "Hypothesis", [Cite(complaint), Cite(risk)])
        ];
        var ortho = c.ProductId == "ortho";
        var mechanism = ortho
            ? "mechanische Instabilitaet, Grenzflaechenmikrobewegung sowie moegliche material- oder verbindungsbezogene Schaedigungen"
            : "eine Beeintraechtigung der Klappenfunktion, strukturelle Veraenderungen oder ein thrombotischer Mechanismus";
        var differential = ortho
            ? "Infektion, Implantations-/Positionierungsbedingungen und individuelle Knochen- bzw. Belastungsfaktoren"
            : "Infektion, haematologische bzw. haemodynamische Einfluesse und konkurrierende kardiale Erkrankungen";
        var gap = clinical is null
            ? "Direkter Revisions-/Explantatbefund und gezielte klinische Abklaerung fehlen. Aus Nichtvorliegen folgt weder negativer Befund noch Kausalitaetsausschluss."
            : "Ein simulierter gezielter Befund liegt vor: " + clinical.Content +
                " Sein konkreter Inhalt ist im Quellenpaneel zu pruefen; er ersetzt keine unabhaengige klinische Gesamtbewertung.";
        if (clinical is not null)
            claims = [.. claims, new("C5", "Der gezielte synthetische Befundausschnitt wurde als neue Evidenz aufgenommen.", "Observed", [Cite(clinical)])];
        AnalysisSection S(string key, string title, string text, params string[] ids) => new(key, title, text, ids);
        return new(
        [
            S("context", "01 / Auftrag und Fallkontext",
                $"SYNTHETISCHER REPLAY-ENTWURF. Bewertet wird {c.Reference}: {c.Title}. Produktscope: {c.ProductId}; Charge: {c.Batch ?? "unbekannt"}; UDI: {c.Udi ?? "unbekannt"}. " +
                "Ziel ist eine nachvollziehbare Produktsicherheitsbewertung, keine patientenspezifische Diagnose oder Therapieanweisung. " +
                "Fiktive Herstellerprodukte und echte oeffentliche Vergleichsinformationen sind getrennte Evidenzklassen. " +
                "Die verbindliche medizinische und regulatorische Entscheidung steht unabhaengig von diesem Entwurf aus.", "C1", "C2"),
            S("facts", "02 / Gesicherte Fakten und Chronologie",
                $"Der originale synthetische Eingang lautet: {c.Description} " +
                $"Erstes Organisationswissen: {c.FirstAwarenessAt:O}; Systemeingang: {c.SystemReceivedAt:O}. " +
                $"Es liegen {c.Sources.Length} Eingangsquellen fuer diesen einen Vorfall vor. Eine zweite Beschreibung desselben Ereignisses ist keine zweite unabhaengige Fallbeobachtung. " +
                "Die Uebereinstimmung der Vorfallreferenz begruendet die Deduplikation, nicht eine aus Textaehnlichkeit abgeleitete klinische Gleichheit. " +
                "Nicht erhobene Werte werden ausdruecklich als unbekannt behandelt; zeitliche Naehe allein beweist keine Ursache.", "C1"),
            S("technical", "03 / Technische Analyse",
                $"Als technische Arbeitshypothesen sind {mechanism} zu beurteilen, soweit die gebundene Spezifikation sie plausibel macht. " +
                "Die technische Spezifikation definiert einen Sollzustand. Der gebundene Pruefbericht ist nur fuer sein konkretes Belastungskollektiv, " +
                "die getestete Konfiguration, Umgebungsbedingungen und Versuchsdauer aussagekraeftig. Ein bestandener Labortest schliesst einen Feldfehler nicht aus. " +
                "Die Analyse muss deshalb Testabdeckung und Einsatzbedingungen abgleichen, bevor ein materialbedingter Mechanismus als bestaetigt gelten kann. " +
                "Ohne Untersuchung des betroffenen Produkts waere die Feststellung eines Materialversagens nicht hinreichend belegt.", "C2", "C3", "C4"),
            S("medical", "04 / Medizinische Analyse",
                "Die im Eingang geschilderten gesundheitlichen Folgen sind durch den qualifizierten Medical Safety Reviewer im Hinblick auf Schwere, " +
                "Dauerhaftigkeit und den Anlass einer Intervention zu beurteilen. Die vorliegende Beschreibung ist keine vollstaendige Krankenakte. " +
                $"Differenzialursachlich kommen {differential} in Betracht. Fehlende Dokumentation erlaubt weder die Diagnose noch den Ausschluss einer dieser Ursachen. " +
                "Eine unvollstaendige Anwendungschronologie darf nicht pauschal als Anwendungsfehler interpretiert werden. Es werden keine Behandlungsempfehlungen fuer eine reale Person abgegeben.", "C1", "C4"),
            S("causality", "05 / Kausalitaet und Gegenhypothesen",
                "Fuer die Untersuchung eines Produktbeitrags sprechen die berichtete Funktionseinschraenkung und ein plausibler technischer Zusammenhang mit der Zweckbestimmung. " +
                "Gegen eine bereits abschliessende kausale Zuschreibung sprechen die fehlende direkte Untersuchung und nicht abgegrenzte konkurrierende Erklaerungen. " +
                "Weder der zeitliche Zusammenhang noch die konstruktive Plausibilitaet ist ein isolierter Kausalitaetsbeweis. " +
                "Die vorlaeufige Schlussfolgerung lautet deshalb: Produktbeitrag nicht ausreichend ausgeschlossen, Kausalitaet offen. " +
                "Eine spaetere Bewertung muss neue Befunde und Gegenbelege gleichrangig beruecksichtigen.", "C1", "C3", "C4"),
            S("risk", "06 / Risiko, Kontrollen und Unsicherheit",
                "Gefaehrdung, konkrete Gefaehrdungssituation und moeglicher Gesundheitsschaden sind getrennt zu dokumentieren. " +
                "Die gebundene fiktive Risikodatei ist der methodische Bezug; dieser Pilot erfindet keine universelle Risikoakzeptanzmatrix und keinen regulatorischen RPN. " +
                "Die Schwerebewertung muss den berichteten Folgen folgen. Eine belastbare Eintrittswahrscheinlichkeit laesst sich aus einem unvollstaendigen Einzelhinweis nicht berechnen. " +
                "Unbekannte Wahrscheinlichkeit bedeutet nicht Nullrisiko. Bestehende technische und klinische Kontrollen werden auf ihre Wirksamkeit im konkreten Fehlerbild untersucht, " +
                "nicht allein wegen ihrer Dokumentation als ausreichend angenommen. Restrisikoakzeptanz bleibt ausschliesslich eine begruendete menschliche Entscheidung.", "C1", "C4"),
            S("benefitRisk", "07 / Nutzen-Risiko-Auswirkung",
                "Ein fallbezogener Sicherheitsbefund und eine produktfamilienweite Nutzen-Risiko-Aussage sind unterschiedliche Schlussfolgerungsebenen. " +
                "Der vorliegende Vorfall kann eine dringende Untersuchung und eine regulatorische Erstmeldung begruenden, erlaubt aber ohne weitere Daten keine allgemeine Aussage " +
                "ueber die Inzidenz oder die Aufhebung des klinischen Nutzens der gesamten Produktfamilie. Expositionsdaten, bestaetigte unabhaengige Faelle, " +
                "klinische Vergleichsevidenz und die gueltige CER-/PMCF-Baseline sind fuer die uebergreifende Bewertung gesondert heranzuziehen.", "C1", "C4"),
            S("regulatory", "08 / Regulatorischer Vorschlag und Frist",
                $"Der gespeicherte Triagevorschlag ist {c.Seriousness} im Profil {c.Jurisdiction}. Rechts-/Regelbezug: MDR Art. 87 und versioniertes Pilot-Rule-Pack; " +
                "GB ist ein eigenes, noch fachlich abzunehmendes Profil. Der Vorschlag ist keine endgueltige Meldepflichtentscheidung. " +
                "Die aktuelle LegalDue und die fruehere interne SafetyDue sind im Fristenpaneel mit Rechenweg sichtbar. Der urspruengliche Wissenseingang wird nicht " +
                "durch den spaeteren Systemeingang ersetzt. Modellfehler, Assurance und Rueckfragen pausieren keine Frist. Eine notwendige Erstmeldung darf nicht " +
                "bis zum Abschluss der Ursachenuntersuchung warten; der menschliche vorlaeufige Meldeweg bleibt offen.", "C1"),
            S("options", "09 / Handlungsoptionen",
                "Prioritaet hat die unverzuegliche gemeinsame Pruefung von klinischer Schwere und regulatorischem Handlungsbedarf durch Medical Safety und RA. " +
                "Eine gezielte technische Untersuchung soll Probenzustand, Herkunft und nichtdestruktive Erstpruefung dokumentieren. Beweisveraendernde Untersuchungen " +
                "und ggf. notwendige Behoerdenunterrichtung sind separat zu genehmigen. Zusaetzlich ist eine datensparsame Befundnachforderung sinnvoll, " +
                "weil sie konkrete Gegenhypothesen aufklaeren kann. Keine dieser Optionen ist bereits eine freigegebene FSCA oder Kundenkommunikation.", "C1", "C3", "C4"),
            S("information", "10 / Gezielter Informationsbedarf",
                gap + " Gefragt werden sollte nur nach dem relevanten Revisions-/Eingriffsbefund, den zur Funktionsbeurteilung erforderlichen Bildbefunden " +
                "und den Ergebnissen bereits durchgefuehrter ursachenbezogener Diagnostik. Die vollstaendige Patientenakte wird nicht angefordert. " +
                "Jedes Datenfeld braucht eine benannte Fragestellung, medizinisch-technische Relevanz und einen Aufbewahrungszweck. Herstellerseitige Verarbeitung " +
                "und eigenstaendige Offenlegungsbefugnis der abgebenden Stelle werden getrennt geprueft. Anfrage und Antwort bleiben ausschliesslich simuliert.",
                clinical is null ? ["C1", "C4"] : ["C1", "C4", "C5"]),
            S("conclusion", "11 / Begruendete Empfehlung",
                "Empfohlen wird die priorisierte menschliche Sicherheitsbewertung mit dokumentierter Untersuchung und, wenn erforderlich, fristwahrender vorlaeufiger Meldung. " +
                "Diese Empfehlung folgt aus der berichteten klinischen Folge, der nicht ausgeschlossenen technischen Plausibilitaet und den noch offenen Gegenhypothesen. " +
                "Sie behauptet weder einen bewiesenen Produktdefekt noch eine erhoehte Inzidenz. Sie muss geaendert werden, wenn neue verifizierte Befunde " +
                "eine andere Ursache belegen, eine weitergehende Gefaehrdung erkennen lassen oder die Produktzuordnung korrigieren. Die zeitkritische regulatorische Entscheidung " +
                "und die laenger dauernde Ursachenuntersuchung werden parallel, nicht nacheinander, bearbeitet.", "C1", "C3", "C4"),
            S("review", "12 / Pruef- und Freigabevermerk",
                "Ersteller: reproduzierbarer Cohors-Demo-/Replay-Modus, keine Live-Modellantwort. Alle tragenden Aussagen sind an den eingeblendeten Evidenzsnapshot gebunden. " +
                "Die separate Assurance und die deterministischen Quellenpruefungen sind keine menschliche Freigabe. Medical Safety und Regulatory Affairs " +
                "bestaetigen getrennt die konkrete Version, den Berichtsinhalt, das Formular, die Sprache, die Jurisdiktion, den Empfaenger und den Paket-Hash. " +
                "Inhaltsaenderungen verlangen neue Freigaben. Der Pilot bietet keine regulatorische Zertifizierung oder klinische Betriebsfreigabe.", "C1")
        ], claims,
        [
            new("Unverzuegliche Sicherheits- und Meldepruefung", "Berichtete gesundheitliche Folgen und offener Produktbeitrag erfordern eine qualifizierte Entscheidung vor Ablauf der unveraenderten Frist.",
                "Medical Safety + RA", "Sofort", "Keine automatische Feststellung der Meldepflicht; nur simulierte Uebermittlung.", ["C1", "C4"]),
            new("Beweiserhaltende technische Untersuchung", "Die Diskrepanz zwischen berichteter Feldfunktion und begrenzter Pruefabdeckung muss am konkreten Produkt untersucht werden.",
                "Technik / QM", "Priorisiert", "Keine beweisveraendernde Pruefung ohne erforderliche Genehmigung; kein bestaetigter Materialdefekt.", ["C2", "C3", "C4"]),
            new("Gezielte Befundnachforderung", "Relevante Befundausschnitte helfen, konkurrierende technische und klinische Ursachen abzugrenzen und Unsicherheit zu reduzieren.",
                "Medical Safety / RA", "Parallel zur Meldung", "Hersteller-Rechtsgrundlage und Offenlegungsbefugnis getrennt; keine Vollakte; ausschliesslich simuliert.", ["C1", "C4"])
        ], [gap, "Kausalitaet vorlaeufig offen; keine kalibrierte klinische Wahrscheinlichkeit.",
            "Oeffentliche MAUDE-Zahlen ohne geeigneten Nenner sind keine Inzidenz."], null,
            "Nicht schaetzbar aus dem vorliegenden Einzelhinweis; fehlender Nenner und nicht abgeschlossene Ursachenuntersuchung.");
    }
}
