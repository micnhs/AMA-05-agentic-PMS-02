# Cohors
## Architekturdesign und Implementierungskonzept für eine agentenbasierte PMS-Pilotplattform

**Azure Master Architect Program · Case Study 5**

**Stand:** 15. November 2026 · **Version:** 0.3 · **Status:** Zur Pilotimplementierung freigegeben; Umsetzung läuft, keine regulatorische Betriebsfreigabe.

**Ergänzung in Version 0.3:** Die nachgereichte Anforderungsmatrix ist vollständig eingelesen und anhand ihrer 118 Original-IDs dem genehmigten Pilotumfang zugeordnet. Der separate Einzelabgleich unterscheidet vorhandene Teilnachweise, offene Pilotabnahmen, Simulationen und nachgelagerte Zielbetriebsfunktionen. Die Matrix erweitert nicht stillschweigend den genehmigten Umfang.

**Beibehalten aus Version 0.2:** getrennte Agent Runtime, Flex-Codepakete statt produktiver Functions-Container und ein gesonderter Nachweis der EU-Verarbeitungsgrenze anhand der teils widersprüchlichen Microsoft-Dokumentation.

**Leitentscheidung:** KI-Agenten recherchieren, korrelieren, analysieren und formulieren begründete Vorschläge. Ein deterministischer Prozesskern verwaltet Fälle, Versionen, Freigaben, Fristen und Übermittlungsnachweise. Medizinisch-regulatorische Entscheidungen bleiben bei qualifizierten Menschen.

**Bestätigte Pilotgrenzen:** synthetische Falldaten; möglichst echte öffentliche Lesequellen; ausschließlich simulierte Behördenmeldungen; durchgängiger, schlanker PMS-Regelkreis; Azure-PaaS mit React/TypeScript, .NET, Container Apps, Durable Functions, Azure SQL, Blob Storage, AI Search und Microsoft Foundry.

**Bestätigtes Freigabemodell:** medizinischer Reviewer und Regulatory Affairs sind getrennte menschliche Rollen mit getrennten Benutzerkonten. Beide Freigaben sind für ein Meldungspaket erforderlich.

Dieses Dokument ist eine technische und organisatorische Planungsgrundlage, keine Rechtsberatung, Medizinproduktezulassung oder Compliance-Bescheinigung. Rechtsauslegung, medizinische Bewertungsmaßstäbe und Freigabe-SOPs benötigen vor einem Echtbetrieb die Bestätigung durch Regulatory Affairs, PRRC, medizinische Sicherheit, QM und Datenschutz.

## 1. Ergebnis und Zielbild

Cohors wird keine Sammlung voneinander unabhängiger Chatbots, sondern eine **zentrale, fallorientierte PMS-Plattform mit kontrollierten Agentenfähigkeiten**. Die Oberfläche zeigt den Zusammenhang zwischen ursprünglichem Hinweis, Evidenz, KI-Analyse, menschlicher Änderung, Entscheidung, Meldung und Nachverfolgung.

Der Pilot demonstriert:

1. Öffentliche Informationen und synthetische Beschwerden werden nachvollziehbar aufgenommen und Produktfamilien zugeordnet.
2. Agenten kombinieren diese Informationen mit versionierten technischen Spezifikationen, Prüfergebnissen, Risikodokumenten, klinischer Evidenz und bisherigen Fällen.
3. Ein Assessment-Agent erstellt eine ausführliche medizinisch-technische Bewertung einschließlich Unsicherheiten, Gegenhypothesen und begründeter Handlungsvorschläge.
4. Ein separat ausgeführter Assurance-Agent prüft Belegbarkeit, Vollständigkeit und Plausibilität. Er erstellt bei Bedarf gezielte Informationsanforderungen, aber keine eigenständigen Freigaben.
5. Medizinischer Reviewer und Regulatory Affairs treffen und dokumentieren die verbindlichen Entscheidungen. Ein Agent kann weder einen sicherheitsrelevanten Fall endgültig schließen noch einen Bericht freigeben.
6. Ein unabhängiges Fristenmanagement läuft auch während menschlicher Prüfung, offener Rückfragen, KI-Ausfällen und weiterer Untersuchungen weiter.
7. Freigegebene Meldungspakete werden ausschließlich an einen Simulator übertragen. Empfang, Ablehnung und unklarer Übertragungsstatus werden realistisch nachgebildet.
8. Erkenntnisse fließen in Trendanalysen, CAPA, Risikomanagement-Änderungsaufträge und einen PSUR-Entwurf zurück.

**Geschäftlicher Bezugsrahmen:** Die Case Study beschreibt einen irischen Hersteller kardiovaskulärer und orthopädischer Implantate, 250.000 Eingangssignale pro Jahr, ein PMS-Team von 90 Personen und Tätigkeiten in mehreren Ländern. Die Ziele „70 % kürzere Signal-to-Decision-Zeit“, „keine verspäteten Vigilanzmeldungen“ und „60 % weniger PSUR-Erstellungsaufwand“ werden als zu messende Geschäftshypothesen übernommen, nicht als bereits nachgewiesene Leistungsversprechen.

250.000 Eingangsinformationen sind nicht 250.000 bestätigte Sicherheitsfälle. Cohors unterscheidet deshalb **Rohinformation → Beobachtung → Fall/Signalkandidat → bewertetes Signal → Entscheidung/Maßnahme**.

## 2. Ausgewertete Unterlagen und wesentliche Präzisierungen

### 2.1 Quellenbasis und Repository-Ausgangslage

| Quelle | Ausgewerteter Umfang | Verwendung |
| --- | --- | --- |
| Fachkonzept „PMS-System für Klasse-III-Implantate“, Version 1.1 | Gesamter Text einschließlich zwölf Modulen, acht Prozessen und Anhängen | Fachlicher Referenzrahmen; Kapitel- und Anforderungsbezüge |
| Anforderungsmatrix „PMS_Anforderungsmatrix_MDR_KlasseIII_Implantate.xlsx“, Stand 15.11.2026 | Alle sechs Blätter; 118 eindeutige IDs, 104 Muss und 14 Soll | Einzelzuordnung zu Pilotumfang, Architektur, Nachweisen und Rest-/Zielbetriebsgrenzen |
| Case Study 5, bereitgestelltes Foto | Business Challenge, Agentenrollen, Autonomiestufen, Governance, Zielgrößen | Demonstrationsszenario und Architekturentscheidungen |
| „Rechtliche Bewertung: Anforderung medizinischer Informationen“ | Gesamtes Markdown-Dokument | Erforderlichkeit und Rechtsgrundlagen gezielter Nachforderungen |
| „Regulatorische Analyse PMS, Vigilanz, PSUR und EUDAMED“, 8.10.2026 | 17 Seiten einschließlich Architekturabbildung | Abgrenzung EU, Großbritannien und Nordirland |
| Cohors-Logo und Logoanimation | Statisches Logo als visuelle Grundlage; Animation als bereitgestelltes Markenasset | Branding; keine Neugestaltung des Logos |
| Aktuelle Primärquellen | EU-Kommission/MDCG, AI Act Service Desk, MHRA, FDA, NCBI und Microsoft Learn | Prüfung zeitabhängiger Aussagen und technischer Empfehlungen |

Der initiale Repository-Stand enthielt das Word-Fachkonzept unter `source\`, aber keinen Anwendungscode und keine Infrastrukturdefinition. Repository-Datei und angehängtes Fachkonzept sind inhaltsidentisch, nachgewiesen durch gleichen SHA-256-Hash. Inzwischen entsteht die freigegebene Pilotimplementierung; lokale Teilabläufe sind ausführbar, die Gesamtintegration ist noch nicht abgeschlossen.

**Nachgereichter Nachweis:** Die Matrix ist nun unverändert unter `source\PMS_Anforderungsmatrix_MDR_KlasseIII_Implantate.xlsx` verfügbar; SHA-256 `6a70b6ca1d287f340abc43097b8538ba9b0b14b676e2063a4aa70245831785ca`. Die Tabelle `Anforderungen` auf dem Blatt `Anforderungsmatrix` umfasst `A2:L120`. Alle 118 Originalstatus stehen auf „Offen“ und werden nicht überschrieben. `docs\requirements\coordinator-analysis.md` ordnet jede ID dem Pilotumfang, Architekturstellen, Prüfansätzen und verbleibenden Grenzen zu. Vollständige Zuordnung ist nicht gleich vollständige technische oder regulatorische Erfüllung. Die Zählung „109“ im Word-Konzept bleibt ein älterer Versionsstand.

### 2.2 Was übernommen, ergänzt oder präzisiert wird

| Punkt | Bewertung | Konsequenz für Cohors |
| --- | --- | --- |
| Zwölf Fachmodule A–L | Tragfähige fachliche Struktur, aber keine Vorgabe für zwölf Microservices | Fachliche Module innerhalb eines kleinen Satzes technischer Dienste |
| Autonomie L4/L3/L2 | Szenarioeigene Beschreibung, keine rechtliche Erlaubnis | Autonomie nur innerhalb freigegebener Quellen, Tools und Zustandsübergänge |
| Menschlicher Reviewer | Zwingender Sicherheits-Gate aus der Aufgabenstellung | Medizinische Freigabe; zusätzliche regulatorische Versandfreigabe nach SOP |
| „Neuer Fristlauf“ bei Reklassifizierung | Kein allgemeiner Reset. MDCG unterscheidet eine begründete ursprüngliche Nichtmeldepflicht von später eingehenden neuen Informationen | Getrennte Zeitpunkte und dokumentierte Ausnahme; keine Verschiebung wegen später Bearbeitung |
| 2/10/15 Tage | Höchstfristen, kein zulässiges Warten bis zum letzten Tag | Sofortige Bearbeitung, eigene frühere Safety-Due und getrennte rechtliche Fälligkeit |
| Wochenenden und Feiertage | MDCG 2023-3 Rev.2 berücksichtigt VO 1182/71 einschließlich Fristende und mindestens zwei Arbeitstagen | Versionierter Kalender und Rechenweg statt bloßem `AddDays` |
| EUDAMED | Vier Module sind seit 28.5.2026 verpflichtend; Vigilance/PMS ist weiterhin in Entwicklung | Keine erfundene produktive Vigilanz-API; nationaler Kanal bzw. BS-Weg als fachlicher Vertrag, im Pilot simuliert |
| MIR 7.3.1 | Aktuelles Formular und veröffentlichte XSD/XSL sind nutzbar; nicht jede ältere 7.3.1-Fassung ist zulässig | Formular-, XSD- und Build-Version separat pinnen; Export und Schema-Prüfung können echt sein |
| AI Act: 2.8.2028 | Im aktuell dargestellten konsolidierten Art. 113 für Art.-6(1)/Anhang-I-Systeme bestätigt | Datum nicht pauschal als Fehler der Case Study behandeln; Cohors-Einstufung separat prüfen |
| AI Act: „PMS für Klasse III = automatisch High Risk“ | Diese Gleichsetzung reicht nicht aus; Zweckbestimmung und Sicherheitskomponentenstatus sind entscheidend | Dokumentierte Einstufung von Cohors selbst, einschließlich Art. 6(1a)/(1b); Human Review allein ist keine Ausnahme |
| Alle Daten pauschal 15 Jahre | MDR-Dokumentationspflicht und Erforderlichkeit einzelner personenbezogener Rohdaten sind zu unterscheiden | Objektbezogene Aufbewahrung; keine ungeprüfte WORM-Ablage vollständiger Patientenakten |
| Hersteller darf Befunde anfordern | Kein allgemeiner Anspruch auf Herausgabe | Medizinische Notwendigkeit, Datenminimierung und Offenlegungsbefugnis getrennt prüfen |
| FDA/MAUDE als Datenquelle | Reale öffentliche Quelle, aber kein belastbarer Nenner und keine automatische Kausalität | Hinweis- und Vergleichsevidenz, keine daraus allein abgeleitete Inzidenz |
| GAMP 5 / ISO 13485 / 21 CFR Part 11 | Methoden bzw. Norm-/Rechtsbezüge sind nach Anwendbarkeit zu behandeln | Risikobasierte Validierung; keine Behauptung einer pauschalen EU-Pflicht oder erlangten Zertifizierung |

Quellen: Fachkonzept §§ 6.1, 6.5, 7.4, 9–12; bereitgestellte regulatorische Analyse; [R1], [R2], [R3], [R4], [R5], [R8], [R9], [R10].

### 2.3 Präzisierungen aus dem Einzelabgleich

Die Matrix ist eine fachliche Quelle auf Basis des MDR-Texts von 2017 mit gesonderten Leitlinienergänzungen. Aktuelle Übergangsregeln, verfügbare Behördenkanäle sowie Datenschutz- und KI-Pflichten werden nicht allein aus ihr abgeleitet.

| Matrixbezug | Für Cohors festzuhalten |
| --- | --- |
| A-05/A-06 | Produktbezogene PRRC-Verantwortung und Vertretung sind von technischen Rollen zu unterscheiden. Die zwei freigegebenen menschlichen Freigaben bleiben unverändert; weder dritte Pflichtsignatur noch PRRC-Übersteuerung werden eingeführt. |
| C-03 | Erwartete Nebenwirkung allein reicht nicht als Ausnahme: dokumentierte Produktinformation, Quantifizierung in der TD und Einbeziehung in die Trendmeldung gemeinsam nachvollziehen. |
| C-12 | Periodische Sammelmeldung ist nicht PSUR; passende Behördenvereinbarung zu Form, Inhalt und Häufigkeit sowie die einschlägigen Fallvoraussetzungen müssen nachgewiesen werden. |
| C-13/C-14/L-06 | Die bereits präzisierte Awareness-Regel aus §9 gilt weiter. Keine Fristverschiebung durch gewöhnliche Reklassifizierung, KI-Bearbeitung oder eine spätere Kausalitätsbewertung. |
| E-02 | Asservat/Zustand, vorherige Behördenunterrichtung und interne menschliche Prüffreigabe vor beeinträchtigender Untersuchung unterscheiden; keine pauschale behördliche Genehmigungspflicht erfinden. |
| F-08–F-16 | Stabile PSUR-Kennung/Leitprodukt, Gruppierungsregeln, lückenlose Perioden, Terminologie, Quellen und offene Maßnahmen brauchen konkrete Nachweise; allgemeine Berichtserstellung genügt nicht. |
| F-11/L-01 | PSUR-Ende, weitere PMS-Aufgaben und Aufbewahrung bleiben getrennt. Kein automatischer universeller 15-Jahre-Stop oder Löschlauf. |
| G/H/I/J/K | PMCF/SSCP/TD bleiben Evidenz und kontrollierte Change Requests; Studienmanagement, Patientenwebsite und reale Behördenverbindungen werden nicht stillschweigend Teil des Piloten. |

Die zwanzig übergreifenden Abnahmeszenarien werden durch die im Einzelabgleich genannten Prüfungen ergänzt. Ein Teiltest oder ein generischer Dokumenttyp darf nicht die vollständige Erfüllung einer mehrteiligen Anforderung vortäuschen.

## 3. Pilotumfang und Zielbetrieb

### 3.1 Fachlicher Umfang

| Modul | Im lauffähigen Pilot | Bewusst nicht als vollständiges Fachsystem |
| --- | --- | --- |
| A Governance/QMS | Produktakte, PMS-Plan-Referenz, Rollen, Freigaben, Eskalation | Vollständiges Unternehmens-QMS und Zertifizierungsverwaltung |
| B Datenerfassung | Synthetisches Beschwerdeformular und Dateiimport; öffentliche FDA-/Literaturadapter; Quellenprotokoll | Produktive CRM-, Klinik- und Registerverbindungen |
| C Vigilanz | Fallakte, Klassifikation, begründete Entscheidung, Erst-/Folge-/Abschlussmeldung, Fristen | Reale Behördenübermittlung |
| D Trend/Signal | Reproduzierbare Statistik, Exposition, Kandidaten, Review und Trendberichtsentwurf | Vollständige pharmakoepidemiologische Forschungsplattform |
| E Untersuchung/CAPA/FSCA | Untersuchungsaufgaben, CAPA mit Wirksamkeitsprüfung, FSCA-/FSN-Entwurf und Simulator | Reale Rückrufe, reale Kundenkommunikation |
| F PSUR | Periodensnapshot, Aggregation, Quellenbezug, Autorenagent, QA, Freigabe und simulierte BS-Antwort | Behördlich bzw. durch BS akzeptierter produktiver Bericht |
| G PMCF | Versionierte Pläne/Berichte als Evidenz; Aufgaben und Fälligkeiten | Studienmanagement, EDC und klinische Prüfungsdurchführung |
| H SSCP/Produktinformation | Versionierte Dokumente, Impact-Analyse und Änderungsauftrag | Eigenständiges Redaktions-, Übersetzungs- und Publikationssystem |
| I Risikomanagement/TD | Gefährdungszuordnung und versionierte Änderungsaufträge mit Freigabe | Vollständiger Ersatz einer bestehenden RM-/PLM-Lösung |
| J UDI/Rückverfolgbarkeit | Synthetischer Produkt-/Chargenstamm, Länder und Expositionsdaten | Produktive UDI-Registrierung und Patientenregister |
| K Integration | Echte Leseadapter, echter MIR-Schemaexport, vertraglich definierte Simulatoren | Ungeprüfte M2M-Zugänge oder Browser-RPA in Behördenportalen |
| L Datenhaltung/Audit | Zentrale Datenbank, nachvollziehbare Versionen, Audit-Export, Aufbewahrungsklassen | Behauptung eines bereits validierten Langzeitarchivs |

**Demonstrationsportfolio:** zwei ausdrücklich fiktive Produktfamilien, eine kardiovaskulär und eine orthopädisch. Öffentliche Datensätze realer Produkte sind Vergleichsevidenz und dürfen nicht als echte Vorkommnisse des fiktiven Herstellers umetikettiert werden.

**Jurisdiktionen:** EU-MDR mit konkreten Demonstrationswegen für Irland und Deutschland; Frankreich und Italien werden als weitere Empfänger-/Sprachprofile berücksichtigt. Großbritannien und Nordirland erhalten getrennte Regel- und Routingprofile, nicht einen gemeinsamen „UK“-Schalter. Der Pilot prüft diese Unterschiede im Simulator; der vollständige landesspezifische Rechts- und Sprachabgleich bleibt ein Echtbetriebs-Gate. [R7]

**Nicht im Pilot:** patientenspezifische Therapieentscheidungen, automatische Änderung technischer Freigabedokumente, automatische Meldepflichtentscheidung, produktive externe Gesundheitsdatenanforderungen, autonome FSCA, vollständige Zertifizierung und echter Behördenversand.

### 3.2 Demo-Modi ohne Täuschung

| Modus | Verhalten |
| --- | --- |
| Live-Pilot | Reale Azure-Modellaufrufe und öffentliche Lesezugriffe; Falldaten und Empfänger bleiben synthetisch |
| Reproduzierbares Replay | Versionierte, zuvor gespeicherte Antworten und Quelldatensnapshots; überall als Replay gekennzeichnet |
| Fehlerdemo | Gezielte Simulation von KI-Ausfall, unvollständigem Befund, abgelehntem Paket und unklarem Versandstatus |

Replay ist kein stiller Erfolgsersatz bei einem fehlgeschlagenen Live-Aufruf. Ausfälle bleiben sichtbar. Modell-, Quellen- und Simulatorstatus werden separat angezeigt.

## 4. Architekturentscheidungen

### 4.1 Wesentliche Entscheidungen

| ADR | Entscheidung | Begründung / Alternative |
| --- | --- | --- |
| ADR-01 | Deterministischer Fallworkflow mit agentischen Teilschritten | Freigaben und Fristen dürfen nicht von freier LLM-Planung abhängen; kein autonomer „Supervisor“, der Prozessregeln überschreibt |
| ADR-02 | Kleine PaaS-Anwendung statt zwölf Microservices | Geringerer Betriebsaufwand und konsistente Transaktionen; AKS ist für diesen Pilot nicht erforderlich |
| ADR-03 | Azure SQL als fachliches System of Record | Relationen, Constraints, Transaktionen, Concurrency und Audit sind zentral; Cosmos DB wird nicht zusätzlich eingeführt |
| ADR-04 | Blob Storage für Dokumente; AI Search als abgeleiteter Index | Ein Suchindex ist kein Archiv und kein führender Datenspeicher |
| ADR-05 | .NET 10 LTS / isolierte Azure Functions; React/TypeScript | Gemeinsame typsichere Backend-Domäne; keine neue In-Process-Functions-Lösung |
| ADR-06 | Microsoft Agent Framework im kontrollierten Runtime-Dienst, Foundry für Modelle | Fachliche Zustände bleiben unabhängig von Agenten-Threads; Foundry Agent Service ist eine spätere Alternative, nicht zweiter Prozessmaster |
| ADR-07 | SQL-Outbox plus Durable Functions; zunächst kein zusätzlicher Broker | Durable Storage stellt die technische Arbeitsverteilung; ein weiterer Service Bus wäre im Pilot eine zusätzliche Fehler- und Betriebsgrenze |
| ADR-08 | Getrennte Daten-, Wissens- und Übermittlungsberechtigungen | Ein kompromittierter Agent erhält weder Freigaberechte noch produktive Versandberechtigungen |
| ADR-09 | Live-Leseadapter plus Vertragssimulatoren | Reale technische Integration demonstrieren, ohne nicht verfügbare oder nicht autorisierte Behörden-APIs vorzutäuschen |
| ADR-10 | Begründete, versionierte Aussage statt versteckter Gedankenkette | Audit benötigt prüfbare Evidenz, Bewertungsgründe und menschliche Entscheidung, nicht Zugriff auf interne Modell-Reasoning-Spuren |

Microsoft empfiehlt für regulierte, nachvollziehbare Abläufe die Kombination aus deterministischer Orchestrierung und flexibel ausgeführten Agentenschritten. Direkter Datenbankzugriff aus KI-Code und unbegrenzte Agentenketten werden vermieden. [A1], [A2], [A3]

### 4.2 Technischer Zuschnitt

Vier klar abgegrenzte Laufzeitbereiche reichen aus:

1. **Cohors Web/API:** React-Oberfläche und ASP.NET-Core-Domänen-API in Container Apps. Die API ist die einzige Stelle für fachliche Commands, Autorisierung, Versionierung und Freigabetransaktionen.
2. **Workflow:** Durable Functions mit .NET Isolated Worker für Fall-, Informationsanforderungs- und Berichtsworkflows. Agenten- und externe Aufrufe laufen ausschließlich in Activities, nie im replaybaren Orchestratorcode.
3. **Agent Runtime:** interne Container App mit den sechs logischen Agentenrollen. Rollen sind keine sechs obligatorischen Cloud-Dienste. Jeder Auftrag erhält einen serverseitig geprüften Tool- und Fallkontext.
4. **Operations/Integration:** getrennte Function App für Quellenabrufe, SQL-Outbox-Pump, Fristen-Watchdog und Simulator. Sicherheitskritische Timer und Alarmierung hängen nicht an der KI-Runtime.

Die fachliche Zuständigkeit bleibt auch bei gemeinsam genutzten technischen Hosts getrennt. Wo mehrere Agenten dieselbe Managed Identity verwenden, ist deren Unterschied zunächst eine **Anwendungsberechtigung**, keine behauptete Azure-RBAC-Isolation. Für besonders schutzbedürftige Tools werden eigene Dienstidentitäten eingesetzt.

### 4.3 Azure-Komponenten und Konfiguration

| Komponente | Aufgabe | Pilotentscheidung und notwendige Absicherung |
| --- | --- | --- |
| Microsoft Entra ID | Anmeldung und Identität | Ein Herstellermandant; App-Rollen, Produktzugriff, MFA/Conditional Access nach Tenant-Möglichkeiten; keine frei wählbaren Rollen im Browser |
| Azure Container Apps | Web/API und interne Agent Runtime | Workload-Profiles-Umgebung, Health Probes, getrennte Identitäten; keine dauerhaften Daten im Container |
| Azure Functions Flex Consumption | Durable Workflows und getrennte Operations-App | .NET 10 Isolated, VNet-Integration; keine Linux-Legacy-Consumption-/In-Process-Neuentwicklung |
| Azure SQL Database | Fälle, Entscheidungen, Beziehungen, Fristen, Outbox | General Purpose als Pilot-Kandidat; Entra-only, Private Endpoint, TDE, PITR; Ledger für kritischen Audit-Trail |
| Azure Storage | Rohdatenquarantäne, Evidenz, freigegebene Pakete | Getrennte Speicherbereiche und Aufbewahrung; private Zugriffe; keine Account-Keys in Anwendungen |
| Durable Task Hub in Azure Storage | Orchestrierungszustand | Eigener Storage-Account; Blob/Queue/Table-Abhängigkeiten, DNS und Identitätsrechte explizit provisionieren |
| Azure AI Search | Hybrid Retrieval über zugelassene Evidenz | Eigene filterbare ACL-, Produkt-, Versions- und Gültigkeitsfelder; Index ausschließlich durch kontrollierte Pipeline aktualisieren |
| Microsoft Foundry Models | Extraktion, Assessment, Assurance und Berichtsentwurf | Nur freigegebene Modellversionen und geprüfte EU-Mitgliedstaaten-Verarbeitungsgeografie; DataZone/Global/Developer nicht automatisch freigegeben; keine unbemerkte Modellumleitung |
| Azure Key Vault | Schlüssel, externe API-Secrets, Signaturmaterial soweit erforderlich | Managed Identity/RBAC, Soft Delete, Purge Protection, Rotation; keine Secrets in Prompts |
| Azure Monitor / Application Insights | Betriebsmetriken, verteilte Traces, Alarmierung | OpenTelemetry, redigierte Logs, Alert-Herzschlag; Betriebslogs ersetzen nicht den fachlichen Audit-Trail |
| Azure Container Registry | Versionierte Containerartefakte | Immutable Image-Digests, Scan, kein Admin-Account; Private-Link-fähiger Tarif, falls private Registry gewählt wird |
| VNet, Private Endpoints, Private DNS | Daten- und Dienstisolation | Getrennte Subnetze für Container Apps, Functions und Endpoints; passende Delegation, keine mehrfach belegten delegierten Subnetze |
| Kontrollierter Zugang / Egress | Zugriff der Demo-Anwender und öffentliche Lesequellen | Private Zugänge bevorzugt; öffentlicher Demo-Eingang nur explizit über geschützten HTTPS-Einstieg; Egress zu genehmigten Quellen begrenzen |

**Keine Deployment-Zusage:** Region, konkrete SKUs, Modell-Quoten, Netzwerkzugang der Vorführenden und Kosten sind noch nicht gegen eine Azure-Subscription geprüft. West Europe bzw. North Europe sind EU-Kandidaten, keine bestätigte Kombination. Die Bereitstellung darf erst nach Kompatibilitäts-, Quota-, Kosten- und Zugangsprüfung erfolgen.

Die PaaS-Basis ist entschieden; die belastbare Ressourcenliste wird daraus vor Deployment generiert. Insbesondere Private Link, Search-Kapazität, Registry-Tarif, Functions-Storage und erforderliche Always-ready-Kapazität sind Kostenpositionen und dürfen nicht als kostenlos angenommen werden. [A4], [A5], [A6], [A7]

**Cloud-Auslieferungsvertrag:** Web/API und eine interne Agent Runtime sind getrennte Dienste mit eigenen Identitäten. Die beiden Flex-Function-Apps erhalten veröffentlichte Codepakete über das Flex-Paket-/OneDeploy-Verfahren. Custom-Container sind kein Flex-Deploymentformat; vorhandene Functions-Dockerimages dienen einer lokalen Hostabnahme und begründen keinen Wechsel auf einen anderen kostenpflichtigen Hostingplan. Eine lokale In-Process-Demo ersetzt nicht die getrennte Cloud-Runtime. [Microsoft Learn: Deploymenttechnologien](https://learn.microsoft.com/en-us/azure/azure-functions/functions-deployment-technologies)

### 4.4 Führende Zustände und technische Konsistenz

- **Azure SQL** enthält den fachlich verbindlichen Zustand. Durable hält den Ausführungsfortschritt; Search eine wiederherstellbare Projektion.
- Jeder fachliche Command schreibt Zustandsänderung, neue Dokument-/Assessment-Version, AuditEvent und OutboxEvent in **einer SQL-Transaktion**.
- Die Outbox-Pump liefert Ereignisse mindestens einmal aus. Empfänger deduplizieren anhand stabiler Event-IDs und fachlicher Command-IDs.
- Ein Durable-Event bedeutet nicht automatisch fachliche Freigabe. Vor Fortsetzung liest die Orchestrierung den autorisierten SQL-Zustand und prüft Version und zulässigen Übergang.
- Eine Blob-Ablage und eine SQL-Transaktion sind nicht gemeinsam atomar. Dateien werden erst unter einer neuen Objekt-ID geschrieben und gehasht; erst danach referenziert eine SQL-Transaktion das fertige Objekt. Nicht referenzierte Uploads werden nachvollziehbar bereinigt.
- Bei einem verlorenen Activity-Ergebnis wird eine vorhandene Ergebnisversion anhand des Idempotenzschlüssels wiederverwendet. Ein erneuter Modellaufruf ist sonst ein neuer AgentRun, kein identisches Replay.
- Suchindex und UI-Readmodels tragen ihren Verarbeitungsstand. Veraltete Projektionen werden angezeigt und dürfen keine kritische Entscheidung verdeckt beeinflussen.
- „Exactly once“ wird nicht für das verteilte Gesamtsystem behauptet. Fachliche Einmaligkeit entsteht durch Constraints, Idempotenz und explizite Abgleichprozesse.

## 5. Daten- und Evidenzmodell

### 5.1 Kernobjekte

Interne UUIDs sind Primärschlüssel. UDI und externe Referenzen sind fachliche Identifikatoren, aber kein Zwang, einen unvollständigen Ersthinweis abzuweisen. Unbekannte Werte sind explizit unbekannt, nicht automatisch „nein“.

| Objekt | Wesentliche Attribute und Beziehungen |
| --- | --- |
| Manufacturer | Interne ID, Name, Sitzstaat, SRN, verantwortliche Rollen; referenzierte produktbezogene PRRC-Zuständigkeit und Vertretung |
| DeviceFamily / DeviceModel / DeviceInstance | Familie → Modelle → Chargen/Serien; Zweckbestimmung, Klasse, Implantatstatus, Basic UDI-DI, UDI-DI, UDI-PI, Markt-/Legacy-Status, begründete Art.-18-Anwendbarkeit |
| Component / DeviceComponent | Material, Komponente, Lieferant und Gültigkeit; N:M-Verknüpfung für produktübergreifende Hypothesen |
| MarketAuthorization / Certificate | Produkt, Jurisdiktion, BS/Approved Body, Zertifikatsversion und Gültigkeit |
| SourcePolicy / IngestionRun | Quelle, zugelassene Suchstrategie, Lizenz, Zeitplan, Cursor, Ergebnis-/Fehlerstatus, letzte erfolgreiche Aktualisierung |
| SourceItem / SourceVersion | Externe ID, Herkunfts-URL, Publikations-/Eingangszeit, Content-Hash, Lizenz, Sprache, Privacy-Status; ein Item hat mehrere Versionen |
| Observation / EvidenceLink | Extrahierte Beobachtung, Textstelle/Seite, Quellenversion, Extraktionsmodell, Fakten-/Hypothesenstatus; N:M zu Fällen |
| DuplicateCluster | Ursprungsdatensätze, Matchmethode und Begründung, menschliche Auflösung unsicherer Zuordnungen; keine Löschung des Originals |
| SafetyCase / CaseVersion | Produktscope, Ereignisland, Vorfallbeschreibung, Folgen, Klassifikation, Owner, Disposition, RowVersion |
| AwarenessEvent | Art der Kenntnis, Quelle, erstes Organisationswissen, Erfassungszeit, Zeitzone, Begründung einer Korrektur, bestätigender Nutzer |
| SignalCandidate / SignalDecision | Hypothese, verknüpfte Fälle/Quellen, Validierungsstatus; bestätigtes Signal nur nach menschlicher Entscheidung |
| ExposureDataset / ExposureVersion | Zeitraum, Produkt, Land, Nennerart, Einheiten, Population, Herkunft, Qualität und Freigabe |
| TrendDefinition / TrendRun | Freigegebene Methode, Referenzrate, Fenster, Stratifizierung, Schwellen, Datenstand, Ergebnis, Rechencode-Version |
| AssessmentVersion | Fakten, technische Analyse, medizinische Analyse, Kausalität, Risiko, Reportability-Vorschlag, Alternativen, Unsicherheiten und Empfehlungen |
| AssessmentClaim | Aussage, unterstützende/widersprechende EvidenceLinks, Belegstatus, Reichweite und Autor |
| QualityReview / QualityFinding | Bezogene Assessment-Version, Prüfregeln, Befund, Kritikalität, Beleg, Abhilfe, Status und ggf. begründete menschliche Ausnahme |
| InformationRequest | Fragestellung, exakt benötigte Daten, medizinisch-technische Relevanz, Rechtsgrundlagen, Empfängerrolle, Freigabe und Rücklauf |
| HumanDecision / Approval | Person/Identität, Rolle, Entscheidungsart, Begründung, Zeitpunkt, signierter Scope und Versions-/Paket-Hash |
| RegulatoryObligation / DeadlineCalculation | Anlass, Jurisdiktion, LegalDue, SafetyDue, Auslöser, Regel-/Kalenderversion, vollständiger Rechenweg, Status |
| RegulatoryReport / ReportVersion | MIR/FSCA/FSN/Trend/PSR/PSUR; Initial/Follow-up/Final; Sprache, Formularversion und verknüpfte Freigaben |
| Submission / SubmissionAttempt / Receipt | Freigegebener Paket-Hash, Empfänger, Adapterversion, Idempotenz-ID, Versuch, technischer Status, fachliche Quittung, Simulator-Kennzeichen |
| Investigation / Action / CAPA | Fragestellung, Asservate und Zustand, Prüfauftrag, vorherige Behördenunterrichtung und interne Prüffreigabe, Aufgaben, Verantwortliche, Fristen, Maßnahmen, Wirksamkeitskriterium und menschlicher Abschluss |
| PMSPlan / PSURCycle / ReportSnapshot | Produktgruppe, Leitprodukt, BS, Berichtsfenster, Freigabe, eingefrorene Fall-/Nenner-/Dokumentversionen |
| ControlledDocument / DocumentVersion | Spezifikation, Test, Risk File, CER, PMCF, SSCP, IFU; fachliche Gültigkeit, Quelle, Status und Hash |
| ChangeRequest | Betroffene freigegebene Dokumente, Ursprung aus Fall/Signal/PSUR, vorgeschlagene Änderung, Freigabe und Umsetzungsnachweis |
| AgentRun / ToolCall | Auftrag, Rolle, Modell-/Prompt-/Tool-Version, erlaubter Scope, Eingabe-/Ausgabereferenz, Kosten, Fehler und Qualitätsstatus |
| AuditEvent / OutboxEvent / InboxReceipt | Wer/was/wann/warum, vorherige/neue Version, Korrelation, kanonischer Payload-Hash, Übermittlungs- und Deduplikationsstatus |

### 5.2 Beziehungen und Invarianten

- Ein SourceItem kann mehrere Fälle betreffen; ein Fall kann aus mehreren Quellen entstehen. Ein Artikel, der denselben Vorfall wie eine Beschwerde beschreibt, ist keine zweite unabhängige Fallzählung.
- Ein Fall kann mehrere Produkte und mehrere RegulatoryObligations haben. Gemeinsame Fallbearbeitung beseitigt keine landesspezifische Meldepflicht.
- Eine AssessmentVersion referenziert einen eingefrorenen Evidenzsatz und konkrete Versionen technischer und klinischer Dokumente.
- Entscheidungen und Freigaben sind append-only. Änderungen erzeugen neue Versionen; frühere Freigaben bleiben historisch sichtbar, gelten aber nicht für geänderten Inhalt.
- Jede freigegebene Empfehlung hat eine fachliche Begründung und verifizierbare EvidenceLinks oder eine explizite Kennzeichnung als unbewiesene Hypothese.
- Zahlwerte tragen Einheit, Bezugszeitraum und Herkunft. Fehlender Nenner verhindert eine als Inzidenz dargestellte Rate.
- Produktzuordnung enthält den Matchgrund: bestätigte UDI, Modell-/Chargenabgleich oder unsichere Textähnlichkeit. Fuzzy-Matches dürfen keine endgültige regulatorische Zuordnung vortäuschen.
- Dokumente tragen sowohl **fachliche Gültigkeit** als auch **Zeitpunkt der Erfassung**. Die heute gültige Spezifikation ist nicht automatisch die beim Ereignis gültige Version.
- Ein PSUR-Snapshot friert alle verwendeten Datenstände ein. Später eingegangene Informationen erzeugen eine kontrollierte Ergänzung oder Folgeversion, keine stille rückwirkende Änderung.

### 5.3 Evidenzkette

Die kleinste prüfbare Einheit ist nicht nur „Dokument X“, sondern **Aussage → Fundstelle → Quellenversion → Ursprung**.

Ein EvidenceLink enthält mindestens SourceVersion-ID, SHA-256, Seiten-/Abschnitts-/Textspanne, Zeitpunkte, Sprache, Lizenz, Verifizierungsstatus und Zugriffslabel. Übersetzungen referenzieren Original und Übersetzungsversion. Eine Sekundärquelle wird nicht als unabhängiger Primärbefund gewertet.

RAG wird über einen Knowledge-Endpunkt ausgeführt, der Produkt- und Dokumentberechtigungen serverseitig erzwingt. Der Agent darf seinen Autorisierungsfilter nicht selbst bestimmen. Die Retrieval-Ergebnisse, nicht nur der Name des Suchindexes, werden pro AgentRun gespeichert.

## 6. Agentenrollen und ihre Grenzen

| Agent | Erstellt / verarbeitet | Erlaubte Werkzeuge | Darf ausdrücklich nicht |
| --- | --- | --- | --- |
| Signal Ingestion Agent | Quellenabfragen innerhalb einer freigegebenen Suchstrategie; Observation- und Zuordnungsvorschläge | Freigegebene öffentliche Connectoren, Parser, Duplikatsuche, Produkt-Lookup | Quellen eigenmächtig freischalten, kritische Hinweise endgültig löschen, Patientendaten frei versenden |
| Trend & Signal Detection Agent | Interpretiert deterministisch berechnete TrendRuns; formuliert Signalkandidaten und Vergleichshypothesen | Statistikdienst, freigegebene Nenner, Source-/Case-Lookup | Rechenwerte erfinden, MAUDE-Zählungen als Inzidenz behandeln, Schwellen selbst ändern |
| Causality & Risk Assessment Agent | Medizinische und technische Beurteilung, Kausalitätshypothesen, Risikoeinordnung, Maßnahmenvorschlag | Knowledge Retrieval, Spezifikations-/Test-/Risk-File-Abgleich, Terminologiedienst | Endgültige Meldepflicht oder Risikoakzeptanz feststellen, Therapien verordnen, Dokumente freigeben |
| Evidence Assurance Agent | QualityReview, Widersprüche, fehlende Nachweise, begründete Informationsanforderung | Quellenprüfung, Pflichtfeld-/Belegvalidatoren, unabhängiges Retrieval | Sich selbst als menschlichen Reviewer ausgeben, QA-Pass mit Freigabe gleichsetzen, Untersuchung endlos wiederholen |
| Vigilance Reporting Agent | MIR-, FSCA-, FSN-, Trend- und Follow-up-Entwürfe | Freigegebene Assessment-Versionen, Formularmapping, Empfänger-Lookup, Fristen-Lesezugriff | Produktiv versenden, LegalDue ändern, Empfänger erfinden, alte Freigabe für neue Paketversion nutzen |
| PSUR Authoring Agent | Kapitel aus freigegebenen Analysen und eingefrorenen Aggregationen; offene Risiken sichtbar machen | PSUR-Snapshot, Statistikdienst, freigegebene PMCF-/RM-Nachweise, Vorlagen | Neue unfreigegebene Sicherheitskonklusionen als bestätigt darstellen, Zahlen aus Fließtext erraten |

Fristenrechner, Formatvalidator, Deduplikationsregeln, Versand-Gate und Statistikdienst sind **deterministische Komponenten**, keine zusätzlichen LLM-Agenten.

### 6.1 Laufzeitvertrag

Jeder AgentRun erhält eine Rolle, einen abgegrenzten Auftrag, Case-/Snapshot-ID, zulässige Tools, Dokumentrechte, Output-Schema und versionierte Ausführungsgrenzen. Strukturiertes JSON und ausführlicher fachlicher Text werden gemeinsam erzeugt; die API validiert beides.

Für den Pilot wird als überprüfbarer Startwert vorgeschlagen: höchstens zwei automatische Nachrecherche-/Überarbeitungsschleifen nach dem ersten Entwurf. Danach entsteht eine menschliche Arbeitsaufgabe. Begrenzungen für Tokens, Tools und Laufzeit werden als versionierte Policy festgelegt. **Ein Limit beendet niemals den Fall oder seine regulatorische Frist.**

Der Assurance-Agent arbeitet in einem separaten Lauf mit eigenem Prüfauftrag und unabhängig zusammengestellter Evidenzsicht. Ein anderes freigegebenes Modell kann später die Diversität erhöhen; es ist keine Voraussetzung und keine Garantie unabhängiger Fehler. Deterministische Prüfungen und qualifizierter Human Review bleiben erforderlich.

### 6.2 Mögliche QA-Befunde

| Befund | Beispiel | Konsequenz |
| --- | --- | --- |
| Kritischer Belegfehler | Quelle existiert nicht; Aussage widerspricht dem zitierten Befund | Entwurf nicht als abgeschlossen freigabefähig |
| Unvollständige Untersuchung | Explantatbericht fehlt, obwohl Materialversagen behauptet wird | Hypothese kennzeichnen, gezielte Nachfrage; vorläufige Meldung bleibt möglich |
| Unzulässige Statistik | Nenner aus anderer Region oder anderem Zeitraum | Rate sperren, Neuberechnung anfordern |
| Unangemessene Sicherheit | „Kausalität ausgeschlossen“ ohne tragfähige Alternativerklärung | Reviewer alarmieren; Textüberarbeitung verlangen |
| Datenschutzproblem | Vollständige Patientenakte ohne spezifischen Bedarf angefordert | Anfrage blockieren; Scope und Rechtsgrundlage prüfen |
| Versionsbruch | Assessment nach Freigabe verändert | Freigabe ungültig für neue Version; erneute Prüfung |
| Kritische Zeitlage | LegalDue/SafetyDue gefährdet | Parallel in den menschlichen Dringlichkeitsprozess, nicht weitere KI-Schleifen erzwingen |

## 7. Prozessablauf: Wer erstellt, verändert und genehmigt was?

Das separate Prozessdiagramm visualisiert den Hauptweg, die Nachforderungsschleife und den fristenkritischen Parallelweg. Die folgende Tabelle ist die verbindliche fachliche Ergänzung.

| Schritt / Auslöser | Wer handelt? | Was wird erstellt oder verändert? | Prüfung / Freigabe | Folgeschritt |
| --- | --- | --- | --- | --- |
| P00 Produkt wird für PMS eingerichtet | RA, PMS, RM, medizinische Sicherheit | Produktakte, verantwortliche Rollen, PMS-Plan, Quellenstrategie, Baselines und Regelprofil | Menschliche Freigabe der Ausgangsbasis | Geplante Quellenbeobachtung aktivieren |
| P01 Quelle liefert Information | Connector / Ingestion Agent; alternativ PMS-Mitarbeiter | SourceItem, Rohdatenreferenz, Eingangszeit, behauptetes Ereignisdatum; potenzieller Awareness-Hinweis | Technische Validierung und Quarantäne; nichts still verwerfen | P02 |
| P02 Intake und Zuordnung | Deterministische Pipeline, Ingestion Agent, ggf. PMS | Normalisierte Observation, Duplikatsbeziehung, Produkt-/Länderzuordnung; ggf. SafetyCase | Unsichere Identität bleibt offen; sicherheitsrelevante Hinweise sofort sichtbar | P03 und parallel Fristenüberwachung |
| P03 Frühe Triage | PMS-Verantwortlicher / medizinischer Reviewer, unterstützt durch Vorschläge | Seriousness-/Expectedness-/Reportability-Vorschlag, Owner, Awareness-Einordnung, RegulatoryObligations | Verbindliche Entscheidung menschlich; unklare Lage wird konservativ eskaliert | P04; bei Dringlichkeit direkt P08 mit vorläufigem Paket |
| P04 Evidenzkorrelation | Assessment Agent | EvidenceBundle: Spezifikation, Test, Risk File, Fallhistorie, Literatur, Material-/Chargenbeziehungen | Versions- und Zugriffskontrolle durch Knowledge-Service | P05 |
| P05 Erstbewertung | Assessment Agent | Neue AssessmentVersion mit Analyse, Gegenhypothesen, Risiko, Empfehlungen und Begründungen | Schema-, Zahlen-, Terminologie- und Quellenprüfung | P06 |
| P06 Unabhängige Qualitätssicht | Assurance Agent plus deterministische Validatoren | QualityReview, belegte QualityFindings, Informationsbedarf | Keine Freigabe durch den Agenten; Ergebnisse gehen transparent an Menschen | P07 bei Lücke, sonst P08 |
| P07 Gezielte Nachforderung | Agent entwirft; Reviewer und berechtigte RA-/Privacy-Rolle entscheiden | InformationRequest mit Notwendigkeit, Minimaldatensatz, Rechts-/Offenlegungsgrundlage; im Pilot simulierte Antwort | Externe Anfrage nur nach menschlicher Freigabe; im Pilot kein realer Versand | Neue Evidenz → P04/P05; Fristen laufen weiter |
| P08 Medizinische Entscheidung | Qualifizierter Medical Safety Reviewer; ggf. Technik/RM beteiligt | Überarbeitete AssessmentVersion, begründete Entscheidung: melden, überwachen, weiter untersuchen, Maßnahme empfehlen oder nicht meldepflichtig | Medizinische Freigabe an konkrete Version; Änderungen durch Reviewer bleiben sichtbar | P09 oder kontrollierter Beobachtungs-/Abschlussweg |
| P09 Regulatorische Prüfung | RA; PRRC als verantwortliche Eskalations-/Freigaberolle gemäß SOP | Jurisdiktion, Empfänger, Meldeart, Formular, Sprache, Pflichtfelder, regulatorische Begründung | Zweite, getrennte Versandfreigabe; RA darf medizinische Ablehnung nicht übersteuern | P10 |
| P10 Paket einfrieren und übertragen | Domänen-API und isolierter Submission-Simulator | Unveränderbares Paket, Approval-Bindung, SubmissionAttempt | Backend prüft Rechte, Version, Hash, Empfänger und doppelte Ausführung | Simulierte Annahme, Ablehnung oder Unknown |
| P11 Quittung und Follow-up | Integrationsdienst; PMS/RA für fachliche Rückfragen | Receipt, Behördenreferenz als Simulation, Follow-up-Aufgabe, neue Fälligkeit | Technischer Empfang ist nicht automatisch fachliche Akzeptanz | P12 oder erneuter Entwurf mit neuer Freigabe |
| P12 Untersuchung und Maßnahmen | PMS, QM, RM, medizinische Sicherheit; Agenten assistieren | Untersuchung, CAPA/FSCA-Vorschlag, Wirksamkeitskriterium, ChangeRequests an RM/CER/TD/SSCP | Maßnahmen und Abschluss menschlich freigeben; erforderliche Meldungen bleiben parallel | P13 und weitere Follow-ups |
| P13 Periodische Auswertung | Statistikdienst, Trend Agent, PMS/RM | TrendRun und SignalCandidate aus freigegebener Methodik; Risiko-/Maßnahmenreview | Trendinterpretation und Sicherheitsfolgerung menschlich | Bei neuem Risiko zurück P03/P04; Eingang in P14 |
| P14 PSUR-Zyklus | PSUR Agent, Assurance Agent, PMS/KB/RM, Reviewer und RA | Eingefrorener Snapshot, PSUR-Entwurf, QA, genehmigte Version, simulierte BS-Rückmeldung | Fachliche Schlussfolgerungen und finale Fassung menschlich; BS-Bewertung separat | Maßnahmen/ChangeRequests und nächster Zyklus |

### 7.1 Entscheidungswege des Reviewers

**Melden:** Der Reviewer bestätigt bzw. korrigiert den Sicherheitsbefund; RA konkretisiert Meldeart und Kanal. Bei unvollständiger Untersuchung wird ein als vorläufig gekennzeichneter Bericht freigegeben, statt die Erstmeldung zu verzögern.

**Weiter beobachten:** Begründete Disposition mit Review-Datum, konkreten Triggern und Owner. „Beobachten“ setzt keine bestehende Meldungspflicht außer Kraft.

**Weitere Untersuchung:** Offene Hypothesen und Fragen, Verantwortliche und Rückmeldefristen. Untersuchung und Meldung können gleichzeitig laufen.

**Nicht meldepflichtig:** Begründete menschliche Entscheidung mit Rechtsbezug und Evidenz; QA-Befunde werden erledigt oder mit dokumentierter menschlicher Begründung dispositioniert. Ein späterer neuer Befund kann den Fall wieder öffnen.

**CAPA/FSCA prüfen:** Separate Maßnahmenentscheidung. Eine FSCA ist weder automatisch mit jeder Meldung verbunden noch durch einen Agenten auslösbar.

### 7.2 Fristenkritischer Parallelweg

Der Standardprozess P04–P07 darf P08–P10 **nicht** blockieren, wenn eine Meldung zeitkritisch ist. Die Oberfläche bietet einen priorisierten Weg „Vorläufige Meldung vorbereiten“:

1. Vorhandene, belegte Fakten übernehmen und fehlende Informationen ausdrücklich aufführen.
2. Medical Safety Reviewer und RA sofort benachrichtigen; Stellvertretung und PRRC-Eskalation aktivieren.
3. Menschlich geprüften Mindestdatensatz gemäß geltendem Formular freigeben.
4. Im Pilot ausschließlich simuliert übertragen; danach Untersuchung und Follow-up fortsetzen.

**Fail-safe bedeutet:** keine unzulässige automatische Freigabe, aber auch kein künstliches KI-Gate vor einer notwendigen menschlich verantworteten Erstmeldung. Läuft die Frist trotzdem ab, wird die Verletzung unverändert protokolliert und eskaliert; Cohors erzeugt keine rückdatierten Nachweise.

### 7.3 RACI der kritischen Entscheidungen

R = operative Durchführung; A = letztverantwortliche Freigabe nach Organisations-SOP; C = einzubeziehen; I = informiert.

| Entscheidung / Artefakt | R | A | C / I |
| --- | --- | --- | --- |
| PMS-Plan, Quellen und Baselines | PMS / RM / Biostatistik | Benannte verantwortliche Fachrolle unter PRRC-Governance | RA, Medical Safety, QM |
| Medizinisch-technische Sicherheitsbewertung | Medical Safety Reviewer, Technik/RM | Qualifizierter Medical Safety Reviewer | PMS, RA |
| Reportability und Behördenweg | RA / PMS | Benannte RA-Freigaberolle | Medical Safety; PRRC bei Eskalation |
| Finale externe Paketfreigabe | RA | RA-Freigaberolle, nach gültiger medizinischer Freigabe | PRRC gemäß SOP |
| Informationsanforderung | PMS / Reviewer | Medizinische Freigabe plus berechtigte Privacy-/RA-Prüfung | Datenschutz bei Ausnahme oder neuer Datenkategorie |
| CAPA und Wirksamkeitsabschluss | QM / Maßnahmenowner | QM-Freigaberolle | Medical Safety, RM, RA |
| FSCA/FSN | RA / QM / PMS | Festgelegtes Sicherheitsgremium bzw. autorisierte Verantwortliche | PRRC, medizinische Sicherheit, betroffene Funktionen |
| PSUR-Schlussfolgerung und finale Fassung | PMS / KB / RM | Benannte medizinisch-regulatorische Freigaberollen | PRRC; BS bewertet anschließend separat |
| Modell-, Prompt-, Regel- und Tooländerung | IT / AI Engineering | Change-Control-Verantwortliche mit fachlicher Mitfreigabe | QM, Medical Safety, RA, Datenschutz je Auswirkung |

Die MDR legt Verantwortung fest, schreibt aber nicht für jedes Dokument exakt diese interne RACI oder eine PRRC-Einzelsignatur vor. Diese Ausgestaltung ist eine **Cohors-Governanceentscheidung**, die organisatorisch bestätigt werden muss.

## 8. Zustandsmodell und Freigabeschutz

### 8.1 Drei getrennte Zustandsachsen

| Achse | Beispielzustände | Wichtige Abgrenzung |
| --- | --- | --- |
| CaseWorkflow | Intake, Triage, Assessing, AwaitingInformation, AwaitingMedicalReview, Investigating, Monitoring, Closed | Beschreibt Bearbeitung, nicht Meldepflicht |
| SafetyDisposition | Undetermined, ProposedReportable, ReportableApproved, NonReportableApproved, MonitorApproved | Endgültige Zustände benötigen menschliche Entscheidung |
| ReportLifecycle | Draft, QARequired, MedicalApproved, RegulatoryApproved, Frozen, SubmissionPending, ReceiptPending, SimulatedAccepted, Rejected, TransmissionUnknown, Superseded | Ein angenommener Bericht beendet nicht automatisch Fall, CAPA oder PSUR-Pflichten |

Neue Evidenz kann eine genehmigte Assessment-Version fachlich überholen. Die alte Version wird nicht gelöscht; ein Reassessment wird erzeugt. Noch nicht übermittelte Pakete verlieren ihre Freigabefähigkeit, wenn ihr gebundener Inhalt betroffen ist.

### 8.2 Freigabe an Inhalt binden

Der Approval-Scope umfasst mindestens:

- AssessmentVersion und zugehörigen Evidenzsnapshot;
- ReportVersion, Formular-/Template-Version und Sprache;
- Meldeart, Jurisdiktion und Empfängerliste;
- relevante Regelversionen und aktuelle QA-Disposition;
- Hash des kanonisch serialisierten Pakets einschließlich Anlagenmanifest.

Die Freigabe speichert Entra-Identität, Rolle, Zeitstempel, Bedeutung der Signatur und Begründung. Eine spätere Änderung an Inhalt, Anlagen, Empfänger oder Sprache erzeugt eine neue Version und fordert neue Freigaben an.

Der Versand-Endpunkt akzeptiert nicht bloß `approved=true` vom Browser oder Agenten. Er prüft den gespeicherten Approval-Scope, die aktuellen Rollen, die erwartete RowVersion, den Paket-Hash und die freigegebene Adapterkonfiguration serverseitig.

Ein Benutzer darf im vorgesehenen Vier-Augen-Prozess nicht beide unabhängigen Freigaberollen für dasselbe Paket ersetzen. Der Pilot verwendet getrennte Testidentitäten; ein Rollenumschalter ohne echte Autorisierung wäre nur ein klar markierter UI-Prototyp.

### 8.3 Keine Umgehung durch QA oder Notfall

Ein QA-Pass ist ein Prüfergebnis, keine Freigabe. Medizinisch kritische, nicht auflösbare QA-Befunde erlauben kein automatisches „approved“. Für vorläufige Meldungen gibt es dagegen eine explizite, menschlich genehmigte Disposition offener Untersuchungsfragen; diese ist keine heimliche Abschaltung der Qualitätsprüfung.

Eine Dringlichkeitseskalation informiert berechtigte Stellvertreter. Sie verleiht keinem Agenten oder Administrator medizinische Rechte. Produktverändernde Untersuchungen und dringliche FSCA werden nach der einschlägigen SOP behandelt; erforderliche Behördenunterrichtung und Beweissicherung bleiben sichtbar.

## 9. Fristenmanagement

### 9.1 Regelkatalog

| Verpflichtung | Grundlage / fachlicher Zeitraum | Umsetzung |
| --- | --- | --- |
| Schwerwiegende Gefahr für öffentliche Gesundheit | Unverzüglich; höchstens 2 Tage nach maßgeblicher Kenntnis | Höchste Priorität; sofortige Eskalation, keine KI-Abhängigkeit |
| Tod / unvorhergesehene schwerwiegende Verschlechterung | Unverzüglich bei festgestellter/vermuteter Kausalität; höchstens 10 Tage | Gesonderte Regel; medizinische Klassifikation nachvollziehbar |
| Sonstiges schwerwiegendes Vorkommnis | Unverzüglich nach maßgeblicher Kausalitätseinordnung; höchstens 15 Tage | Kein Default „immer 15 Tage“ |
| FSCA | Grundsätzlich vor Durchführung, ohne ungebührliche Verzögerung; dringliche Ausnahme | Ereignis- und Gate-Regel, keine erfundene pauschale Tagesfrist |
| Trendmeldung | Art. 88; Methodik/Beobachtungsperiode und anwendbare Konkretisierung | Keine frei erfundene allgemeine MDR-Tagesfrist |
| Periodische Sammelmeldung | Nur vereinbarte Form, Inhalt und Frequenz | Behördenvereinbarung als Pflichtreferenz |
| PSUR Klasse III/IIb | Mindestens jährlich; planmäßige Erstellung/Einreichung mit BS abstimmen | Lückenlose Perioden, versionierte Gruppierung und eigener Issuance-Termin |
| PMCF-Bewertungsbericht / ggf. SSCP | Für Klasse III und Implantate PMCF Evaluation Report und, falls angezeigt, SSCP mindestens jährlich mit neuen Daten aktualisieren | Eigenständige Pflichtobjekte; nicht pauschal jeder PMCF-Plan jährlich neu |
| Behörden-/BS-Rückfrage | Konkrete Anfrage bzw. jeweiliges Recht; GB hat gesonderte Vorgaben | Eigenständiger Rule Pack und Kalender, nicht EU-Fristen kopieren |
| CAPA / Informationsanforderung | Fachlich festgelegte interne Termine | Als SOP-/Arbeitsfrist gekennzeichnet, nicht als Gesetz ausgeben |

Rechtsgrundlagen: MDR Art. 61, 86–89; MDCG 2023-3 Rev.2 Fragen 13–15; MDCG 2022-21; MHRA-Vigilanzleitlinie. [R1], [R2], [R3], [R7]

### 9.2 Kenntnis und neue Informationen

Es werden mindestens vier Zeitbegriffe unterschieden:

1. **occurredAt:** Zeitpunkt des Ereignisses, soweit bekannt.
2. **firstOrganizationalAwarenessAt:** erstes bekanntes Wissen eines Mitarbeiters oder Vertreters einschließlich ausgelagerter Beschwerdebearbeitung.
3. **systemReceivedAt:** tatsächlicher Eingang in Cohors; darf früheres Organisationswissen nicht ersetzen.
4. **reportabilityAwarenessAt:** in einer belegten Ausnahmesituation Eingang der neuen Information, die eine zuvor begründet nicht meldepflichtige Situation ändert.

Korrekturen werden als neue AwarenessEvents mit Quelle und Begründung ergänzt. Originalwerte bleiben erhalten. Bei unsicherem Awareness-Zeitpunkt wird eine konservative interne Frist geführt und die Klärung eskaliert.

**Wichtig:** MDCG erlaubt unter bestimmten Bedingungen einen späteren Auslöser durch tatsächlich neue Informationen. Das ist nicht gleichbedeutend mit „bei jedem Reviewer-Wechsel, jeder Reklassifizierung oder verspäteter Bearbeitung neu starten“. Bei einer Verschärfung werden alle einschlägigen Verpflichtungen erneut berechnet und die früheste noch maßgebliche Fälligkeit geschützt.

### 9.3 LegalDue und SafetyDue getrennt berechnen

Die EU-Regeln sind genauer als „Kenntniszeitpunkt plus 48/240/360 Stunden“:

- Awareness-Tag ist grundsätzlich Tag 0; die Tageszählung beginnt am Folgetag.
- Kalendertage umfassen zunächst Wochenenden und Feiertage.
- MDCG 2023-3 Rev.2 berücksichtigt für das Fristende die VO 1182/71: ein Ende an Samstag, Sonntag oder Feiertag wird auf den folgenden Arbeitstag verschoben, Art. 3(4); mindestens zwei Arbeitstage bei Perioden ab zwei Tagen, Art. 3(5). Die Absatzangaben sind in Fußnote 25 der Guidance vertauscht; maßgeblich ist der Verordnungstext. [R15]
- Die Pflicht zur unverzüglichen Meldung bleibt unabhängig von dieser Höchstfrist bestehen.
- Zeitzone, Feiertagskalender und seine Zuständigkeit müssen je Regelprofil durch RA festgelegt werden. Die Browserzeitzone ist keine Rechtsgrundlage.

**LegalDue:** nach freigegebenem Rechts- und Kalenderprofil berechnete Höchstfrist, einschließlich vollständig protokollierter Zwischenschritte.

**SafetyDue:** strengere interne Bearbeitungs-/Versandfrist ohne automatische Ausnutzung einer Wochenendverlängerung, ergänzt um Sofortalarm bei dringlichen Anlässen. Sie ist als interne SOP-Frist gekennzeichnet.

**Rechenbeispiel für ein freigegebenes EU-Testprofil ohne zusätzlichen Feiertag:** Kenntnis am Donnerstag, 12.11.2026, 10:00 Uhr Europe/Dublin, 2-Tage-Regel. Kalendertagsziel: Samstag, 14.11., Tagesende. Unter der beschriebenen EU-Fristendregel: LegalDue Montag, 16.11., Tagesende; Freitag und Montag sind zwei Arbeitstage. Interne SafetyDue wird nicht über das ursprüngliche Kalendertagsziel hinausgeschoben; die Dringlichkeitsbenachrichtigung erfolgt sofort am Kenntnistag. Das Beispiel ersetzt keine landesspezifische rechtliche Freigabe.

GB- und NI-Profile werden separat geprüft. Eine EU-Wochenendregel wird nicht ohne Rechtsprüfung auf GB übertragen.

### 9.4 Unabhängiger Betrieb

- Durable Timer unterstützen den Ablauf; ein separater Watchdog prüft persistierte offene Fristen unabhängig von einem wartenden AgentRun.
- Der Watchdog protokolliert sein letztes erfolgreiches Intervall. Ein externer Monitor alarmiert bei fehlendem Herzschlag: „keine Alerts“ darf nicht mit „keine Fristprobleme“ verwechselt werden.
- Vorgeschlagenes Pilot-Testprofil: Prüfung mindestens jede Minute; fehlender Herzschlag nach drei ausgebliebenen Intervallen alarmieren; spätestens fünf Minuten nach Überschreiten einer gespeicherten Warn-/Fälligkeitsgrenze eine nachvollziehbare In-App-Eskalation erzeugen. Dies sind technische Abnahmekriterien, keine Verlängerung rechtlicher Fristen oder zugesagte Produktions-SLAs.
- Eskalation erfolgt an Fallowner, Stellvertretung und PRRC/RA-Bereitschaft; Zustellung und Bestätigung sind nachvollziehbar.
- Beim Neustart werden versäumte Prüfintervalle aufgeholt. Zeit, Benutzerwechsel, Retry, Reindexierung und Deployment setzen eine Frist nicht zurück.
- Informationsanforderungen, ausstehende Befunde, QA und Modellkontingente pausieren keine regulatorische Uhr.
- Für die Demo existiert eine isolierte virtuelle Uhr. Simulationszeit und echte Auditzeit sind unterschiedliche Felder; produktive Zeitstempel werden niemals manipuliert.

## 10. Medizinisch-technische Analysen und Handlungsempfehlungen

### 10.1 Verbindliche Struktur eines Assessments

Ein Assessment muss fachlich lesbar sein und parallel strukturierte Daten liefern. Ein kurzer Chat-Absatz oder ein unbegründeter Score reicht nicht.

| Abschnitt | Erforderlicher Inhalt |
| --- | --- |
| Auftrag und Fallkontext | Produkt, Charge soweit bekannt, Zweckbestimmung, Ereignis, Märkte, Untersuchungsfrage, Kenntnisstand |
| Gesicherte Fakten | Chronologie und Befunde; jede tragende Aussage mit Fundstelle |
| Technische Analyse | Fehlerbild, spezifizierte Funktion, Testabdeckung, Material-/Komponentenaspekte, Abweichungen und Grenzen der Übertragbarkeit |
| Medizinische Analyse | Gesundheitliche Folgen, Schweregrad, zeitlicher Zusammenhang, klinisch plausible Mechanismen und alternative Ursachen |
| Kausalitätsbewertung | Für und gegen Produktbeitrag sprechende Evidenz, Confounder, Gegenhypothesen, fehlende Daten; keine Scheingenauigkeit |
| Risikobewertung | Gefährdung, Gefährdungssituation, möglicher Schaden, Schwere, Wahrscheinlichkeit mit Datenbasis, Unsicherheit und vorhandene Kontrollen |
| Nutzen-Risiko-Auswirkung | Fallbezogener Befund versus Aussage über die gesamte Produktfamilie klar trennen |
| Regulatorische Einordnung | Vorschlag zur Seriousness/Expectedness/Reportability, Rechts-/Regelbezug und aktuelle Frist; als Vorschlag kenntlich |
| Handlungsoptionen | Je Option Zweck, Evidenz, erwartete Risikominderung, Grenzen, Verantwortliche, Dringlichkeit und Prüfbedarf |
| Informationsbedarf | Genau bezeichnete Frage, benötigter Befund, Relevanz, datensparsame Anfrage und Auswirkung auf die Unsicherheit |
| Schlussfolgerung | Begründete Empfehlung, Gegenargumente und Bedingungen, unter denen sie geändert werden müsste |
| Prüf- und Freigabevermerk | KI-Ersteller, Versionen, QA, menschliche Änderungen, finale Entscheidung und Signaturbedeutung |

Die Ausführlichkeit bemisst sich an der medizinisch-technischen Fragestellung, nicht an einer künstlichen Mindestwortzahl. Zahlen und Terminologie werden maschinell validiert; fachliche Schlüssigkeit wird unabhängig geprüft.

**Unsicherheit wird differenziert:** Datenvollständigkeit, Quellenqualität, technische Plausibilität, klinische Kausalität und regulatorische Einordnung sind verschiedene Dimensionen. Ein LLM-eigener „Confidence 93 %“-Wert ist keine kalibrierte klinische Wahrscheinlichkeit.

Die Risikomethodik wird ISO-14971-orientiert aus dem freigegebenen Hersteller-Risikomanagement übernommen: Gefährdung, Gefährdungssituation, Schaden, Schwere, begründete Wahrscheinlichkeit, Kontrollen und Restrisiko bleiben unterscheidbar. Cohors erfindet weder eine universelle Risikoakzeptanzmatrix noch einen regulatorisch verbindlichen RPN. Nicht schätzbare Wahrscheinlichkeiten bleiben offen und führen zur definierten menschlichen Bewertung, nicht zu einem rechnerischen Nullrisiko.

### 10.2 Beispiel: ausschließlich synthetischer Demonstrationsfall

**Kennzeichnung:** Das folgende Beispiel beschreibt keine reale Person, kein reales Produkt und keine therapeutische Empfehlung.

**Fiktiver Kontext:** Bei einer synthetischen modularen Hüftendoprothese wird eine vorzeitige Revision nach Schmerzen und radiologisch beschriebener Lockerung gemeldet. Ein zweiter Eingang beschreibt denselben Vorfall. Zuordnung zu Modell und Charge ist teilweise möglich; ein Explantatbefund und eine mikrobiologische Abklärung liegen noch nicht vor.

**Demo-Evidenz:** E1 = synthetische Beschwerde, Version 1; E2 = fiktive technische Spezifikation, Version 3; E3 = synthetischer Prüfbericht mit beschriebenen Randbedingungen; E4 = freigegebene fiktive Risikodatei; E5 = simulierte Verlaufszusammenfassung. Diese IDs müssen im Pilot auf tatsächlich angelegte Demo-Dokumente und konkrete Fundstellen zeigen.

**Medizinisch-technische Bewertung:** Die in E1 und E5 beschriebene Revision und Funktionsbeeinträchtigung begründen eine priorisierte Prüfung der klinischen Schwere und des möglichen Produktbeitrags. Die zeitliche Abfolge ist mit einem produktbezogenen Problem vereinbar, beweist dessen Ursache jedoch nicht. Die beiden Eingangsmeldungen beschreiben nach den verfügbaren Identifikatoren denselben Vorgang und dürfen daher nicht als zwei unabhängige Ereignisse in die Häufigkeitsberechnung eingehen.

Als technische Hypothesen sind eine mechanische Instabilität sowie, sofern die in E2 beschriebene Verbindung dies plausibel macht, eine material- oder verbindungsbezogene Schädigung zu untersuchen. Ein bestandener Prüfversuch in E3 schließt einen Feldfehler nicht aus: Zu prüfen ist, ob Belastungskollektiv, Produktkonfiguration, Umgebungsbedingungen und Dauer des Tests die konkrete Anwendung abdecken. Ohne Untersuchung des Explantats ist eine Behauptung von Materialversagen nicht ausreichend belegt.

Differenzialursächlich sind unter anderem eine infektiöse Ursache sowie anwendungs- oder patientenbezogene Faktoren zu berücksichtigen. Das Fehlen eines mikrobiologischen Befunds erlaubt weder die Diagnose noch den Ausschluss einer Infektion. Ebenso darf aus einer unvollständigen Dokumentation kein Anwendungsfehler abgeleitet werden. Die Kausalität bleibt deshalb vorläufig offen; vorhandene Daten reichen nicht für einen belastbaren Ausschluss eines Produktbeitrags.

**Begründete Handlungsempfehlung:** Der Fall sollte unverzüglich dem qualifizierten Medical Safety Reviewer zur Seriousness- und Reportability-Entscheidung vorgelegt werden. Wenn die Kriterien für eine fristgebundene Erstmeldung erfüllt oder nicht rechtzeitig sicher auszuschließen sind, ist ein vorläufiger Bericht mit explizit dokumentierten Datenlücken vorzubereiten. Die ausstehende Ursachenklärung ist kein Grund, diese Prüfung oder eine erforderliche Meldung zu verschieben.

Für die technische Untersuchung sollte ein datierter, nachvollziehbarer Plan erstellt werden. Er muss den Zustand und die Herkunft eines verfügbaren Explantats, nichtdestruktive Erstuntersuchungen und die Abgrenzung weiterer materialverändernder Prüfungen enthalten. Erforderliche Behördenunterrichtung vor beweisverändernden Untersuchungen bleibt ein separater Prozessschritt. Die Empfehlung folgt aus der derzeit fehlenden direkten Evidenz zum vermuteten Versagensmechanismus, nicht aus der Annahme, dass dieser bereits nachgewiesen sei.

Für die klinische Einordnung sollte gezielt nach dem relevanten Revisionsbefund, den für die Lockerungsbeurteilung erforderlichen Bildbefunden und dem Ergebnis einer bereits durchgeführten mikrobiologischen Untersuchung gefragt werden. Jede Anfrage muss ihre konkrete Fragestellung nennen und auf den erforderlichen Ausschnitt begrenzt bleiben; die vollständige Patientenakte wird nicht pauschal angefordert. Im Pilot werden Anfrage, Genehmigung und Antwort ausschließlich simuliert.

Eine produktfamilienweite FSCA oder eine Aussage über eine erhöhte Inzidenz lässt sich aus diesem Einzelbefund nicht rechtfertigen. Dafür sind weitere bestätigte Fälle, geeignete Expositionsdaten, Vergleichszeiträume und eine fachlich freigegebene Trendbewertung notwendig. Die vorgeschlagene Maßnahme ist daher eine priorisierte Untersuchung mit fristwahrender regulatorischer Prüfung und einem definierten Wiederbewertungspunkt, keine bereits beschlossene Feldmaßnahme.

### 10.3 Informationsanforderung als eigener kontrollierter Prozess

Eine Anfrage enthält: Hypothese, benötigte Information, warum diese den Unsicherheitsgrad verändert, Empfängerrolle, minimale Datenfelder, zulässigen Übertragungsweg, Aufbewahrungszweck, Verantwortlichen und Rückmeldefrist.

Vor realer Nutzung werden die Rechtsgrundlage der Verarbeitung beim Hersteller und die eigenständige Offenlegungsbefugnis der Klinik/des Arztes geprüft. Einwilligung ist weder pauschal immer erforderlich noch pauschal entbehrlich. Pseudonymisierung ersetzt keine Rechtsgrundlage. In Deutschland kann zusätzlich § 203 StGB relevant sein. [R11], [R12]

Antworten und ausbleibende Antworten werden dokumentiert. „Keine Antwort“ ist keine negative medizinische Evidenz. Der Assurance-Agent benennt die verbleibende Unsicherheit und ihre Konsequenz für die Bewertung.

## 11. Korrelation, Trendanalyse und PSUR

### 11.1 Korrelation mit internen Nachweisen

Die Korrelation erfolgt gestuft: bestätigte Produktidentität, zeitlich passende Dokumentversion, ähnliche Fehlerbilder/IMDRF-Terminologie, gemeinsame Komponenten/Materialien, klinische Merkmale und Quellenunabhängigkeit.

Eine technische Spezifikation beschreibt den Sollzustand; ein Prüfergebnis belegt nur die tatsächlich geprüften Randbedingungen. Beides wird nicht mit einem Nachweis gleichgesetzt, dass ein gemeldeter Fehler im Feld unmöglich sei. Der Agent muss diese Reichweite in seiner Begründung benennen.

Gemeinsamkeiten zwischen Produkten werden als Hypothese mit Matchgrund und Evidenz ausgegeben. Embedding-Ähnlichkeit allein rechtfertigt weder eine Chargenbetroffenheit noch eine Sicherheitsmaßnahme.

### 11.2 Deterministische Statistik

- Referenzraten, Ereignisdefinition, Beobachtungsfenster, Stratifizierung und Alarmgrenzen stammen aus einem freigegebenen PMS-Plan.
- Der Pilot implementiert ein enges, dokumentiertes Verfahren für Ereigniszahlen mit geeignetem Expositionsnenner, beispielsweise einen exakt spezifizierten Poisson-Ratenvergleich, sofern dessen Annahmen erfüllt sind.
- Überdispersion, kleine Fallzahlen, wiederholte Tests, verändertes Meldeverhalten und Confounder werden erkannt bzw. als Einschränkung ausgewiesen. Ein unpassendes Verfahren wird nicht allein deshalb eingesetzt, weil eine Bibliothek es anbietet.
- Ergebniswerte entstehen durch getesteten Rechencode. Ein LLM interpretiert das Ergebnis, berechnet aber nicht die rechtlich relevante Statistik.
- Rohzählungen öffentlicher MAUDE-Meldungen sind keine belastbare Inzidenz. Sie werden getrennt von internen Rate-/Expositionsanalysen dargestellt. [R10]
- Statistische Auffälligkeit, klinische Relevanz und regulatorische Trendmeldepflicht sind getrennte Entscheidungen.
- Ein bestätigter Trend erzeugt einen Review-/Maßnahmenauftrag; nicht automatisch eine unkontrollierte Änderung der Risikodatei.

### 11.3 PSUR-Erstellung

Ein PSURCycle definiert Produktgruppe, Leitprodukt, BS, Datenerhebungsfenster, Erstellungs-/Einreichungstermin und Vergleichsperioden. Die Gruppierung ist begründet; ein Wechsel des Leitprodukts wird nicht als harmlose Stammdatenänderung behandelt.

MDCG 2022-21 beschreibt für Klasse III/IIb vier Zwölfmonatsscheiben mit den Bezeichnungen **N, N2, N3, N4**. Das Datenmodell speichert explizite Start-/Enddaten statt mehrdeutiger Kürzel. Regionale Auswertungen EWR/Türkei/Nordirland und weltweit werden nicht addiert, da die weltweite Menge diese Teilmenge bereits umfasst. [R3]

Der Snapshot enthält auch offene Untersuchungen und ungelöste Risiken. „Nur freigegebene Schlussfolgerungen verwenden“ bedeutet nicht, offene sicherheitsrelevante Fälle aus dem Bericht auszublenden.

Der Agent erzeugt Deckblatt, Zusammenfassung, Produkt-/Gruppierungsabschnitt, Absatz/Exposition, Vorkommnisse, Trends, FSCA/CAPA, weitere PMS-/PMCF-Daten und begründete Nutzen-Risiko-Schlussfolgerungen. Zahlen kommen aus strukturierten Snapshots; Aussagen verweisen auf Assessment- und Evidence-Versionen.

QA prüft Zahlenkonsistenz, Nenner, Periodenlücken, Quellen, Todesfallangaben, Maßnahmenstatus und Abgleich mit PMCF, CER, Risk File und SSCP. Reviewer und RA genehmigen die finale Fassung; der Simulator liefert getrennt davon eine BS-Rückmeldung.

**Laufzeit und Archivierung:** Das Ende der PSUR-Erstellung und die Aufbewahrung bereits erstellter Unterlagen sind unterschiedliche Regeln. MDCG 2022-21 § 3.2 enthält eine Laufzeit-/Retention-Abgrenzung; MDR Art. 10(8) eine Mindestaufbewahrung. Cohors leitet daraus weder ein pauschales Löschdatum noch einen automatischen Stopp sämtlicher PMS-Aktivitäten ab. Die produktbezogene Regel wird fachlich und rechtlich freigegeben.

## 12. Schnittstellen: echt, bedingt möglich oder simuliert?

| Satellit / Quelle | Pilotmodus | Was tatsächlich implementiert wird | Grenze / Voraussetzung |
| --- | --- | --- | --- |
| FDA openFDA Device Event / MAUDE | Echt lesend | REST-Adapter, Suchstrategie, Pagination, Cursor, Backoff, Original-ID, Snapshot und Update-Status | Öffentliche Meldungen sind unvollständig/biased; keine automatische Kausalität oder Inzidenz |
| PubMed / NCBI E-utilities | Echt lesend | Such- und Metadatenadapter, PMID/DOI, Suchprotokoll, zulässige Abstract-/Volltextnutzung | Rate Limits, Registrierung/API-Key nach Bedarf; PubMed-Eintrag bedeutet nicht freie Volltextlizenz |
| HPRA/BfArM öffentliche Sicherheitsinformationen | Bedingt echt lesend | Definierter Import öffentlich zugänglicher Veröffentlichungen, bevorzugt stabiler Feed/Download | Kein allgemeines Behörden-API versprechen; Kanal und Nutzungsbedingungen zuvor prüfen |
| Internes Beschwerde-/CRM-System | Simulierter Lieferant, echte Importlogik | JSON/CSV bzw. Formular; gleiche Validierung und Fehlerbehandlung wie spätere API | Keine produktiven E-Mail-Postfächer oder CRM-Zugänge im Pilot |
| PLM/QMS/LIMS/ERP | Simulierte Quelldaten, echte Dokument-/Datenpipeline | Spezifikationen, Testberichte, RM/CER/PMCF/SSCP, Chargen und Exposition | Schnittstellenverträge und fachliche Owner; keine Direktänderung fremder Freigabedokumente |
| Implantateregister / Kliniken | Simuliert | Aggregat-/Befundimport und kontrollierte Informationsanforderung | Registerzugang, Verträge und Offenlegungsbefugnis sind nicht durch öffentliche Existenz gegeben |
| Social Media | Synthetische Hinweise | Unbestätigte Quelle, Verifikation, Deduplikation, niedrige Evidenzstufe | Keine pauschale Scraping-Erlaubnis und keine automatische medizinische Schlussfolgerung |
| EUDAMED Actor/UDI/Certificates | Vertrag/Fixtures; ggf. öffentlicher Link/zugelassener Export | Referenzdatenmapping, SRN/UDI-Validierung, nachvollziehbare Importfixtures | Öffentliche Oberfläche ist keine zugesagte allgemeine REST-API |
| EUDAMED M2M / Playground | Als spätere Integrationsoption dokumentiert | Adaptervertrag und Schema-Versionierung; separates Onboarding-Gate | Modul-/Rollenberechtigung, SRN, Token/Zertifikate und passender Access Point erforderlich; Playground ist nicht Produktion |
| EUDAMED Vigilance/PMS | Simuliert | Zukünftiger Versandvertrag mit realistischen Status-/Fehlerfällen | Laut Kommission noch in Entwicklung; kein produktiver Endpoint wird erfunden |
| EU MIR-Export | Echt im Format, simuliert im Versand | Versioniertes Mapping gegen veröffentlichte MIR-XSD; maschinenlesbares Paket und lesbare Demo-Vorschau | XSD-valid ist nicht automatisch fachlich angenommen; geschütztes Original-PDF nicht umgehen |
| HPRA / deutsche Behörde | Simuliert | Nationalen Meldeweg, Formular und Empfänger im Paket abbilden | Reale Einreichung nach aktuellem Behördenverfahren außerhalb des Piloten |
| MHRA MORE: GB / NI | Simuliert, getrennte Profile | GB-Form versus EU-Form für NI, Statusmodell und Nachweisvertrag | Kein Portal-RPA und keine ungeprüfte API-Annahme; MORE-Zugang später autorisieren |
| Benannte Stelle | Simuliert | PSUR-Paket, Rückfrage, Bewertung und Maßnahmenantwort | Tatsächlicher Übergangsweg muss mit konkreter BS vereinbart sein |

Quellen: [R4]–[R7], [R10], [R13], [R14].

### 12.1 Schnittstellenverträge

Ein Leseadapter liefert SourceEnvelope mit externer ID, Versions-/Änderungskennung, Herkunft, Abrufzeit, Nutzungsrechten und unverändertem Rohdatenbezug. Er meldet Fehler, unvollständige Seiten und Rate Limits ausdrücklich; ein Fehler ist keine leere Trefferliste.

Ein SubmissionAdapter verarbeitet ausschließlich ein eingefrorenes, freigegebenes Paket. Der Vertrag unterscheidet `technicalReceipt`, `businessAccepted`, `businessRejected`, `pending` und `unknown`. Jede Antwort bindet Paket-Hash, externe Referenz und Versuch.

**TransmissionUnknown:** Bei Timeout nach möglichem Versand wird zuerst anhand einer stabilen Korrelations-ID abgeglichen. Ohne zuverlässige Statusabfrage ist menschliche Klärung erforderlich. Kein blindes erneutes Senden, insbesondere bei E-Mail-basierten Übergangswegen.

**Pilot-Sperre:** Produktive Behörden-Domains und -Credentials sind im Pilot nicht vorhanden bzw. nicht freigeschaltet. Ein konfigurierbarer Simulator wird nicht durch einen einfachen UI-Schalter zum Produktivadapter. Jeder simulierte Beleg trägt `simulated=true` und einen sichtbaren Demo-Hinweis.

### 12.2 MIR-Besonderheiten

Die Kommission stellt MIR 7.3.1 sowie XSD/XSL bereit. Akzeptierte PDF-Builds und Schema-Builds werden getrennt dokumentiert; die veröffentlichte Seite nennt etwa PDF SB 11154 und XSD/XSL SB 11252. Versionen vor der Veröffentlichung Dezember 2025 sind laut Seite nicht mehr zulässig. [R5]

Der Pilot nutzt die freigegebenen Schemaartefakte für Mapping- und Kontrakttests. Das passwortgeschützte Originalformular wird nicht technisch umgangen. Eine optisch ähnliche Demo-Vorschau wird nicht als ausgefülltes, behördlich akzeptiertes Originalformular ausgegeben.

## 13. Oberflächen, Dashboards und Branding

| Oberfläche | Benutzer / Zweck | Zentrale Inhalte und Aktionen |
| --- | --- | --- |
| PMS Cockpit | PRRC, PMS-Leitung | Offene Sicherheitsfälle, LegalDue/SafetyDue, Überfälligkeit, Reviewer-Last, Quellenfrische, Modell-/Workflow-Ausfälle |
| Signal Inbox | PMS, Data Steward | Quellen, Dubletten, Produktzuordnung, Quarantäne, ungeprüfte Kandidaten; begründete manuelle Korrektur |
| Case Workbench | Medical Safety, Technik, RA | Chronologie, ausführliche Analyse, Originale und Fundstellen, QA-Befunde, offene Fragen, Human-Edits und Versionsvergleich |
| Review Queue | Reviewer / Stellvertretung | Priorität und Dringlichkeit, Entscheidung mit Begründung; Freigabe, Rückgabe, Beobachtung, weitere Untersuchung |
| Regulatory Desk | RA | Meldepflicht, Land, Empfänger, Version, Mindestfelder, Freigaben, Paketvorschau, simulierte Quittungen/Fehler |
| Fristenkalender | PMS / PRRC / RA | Gesetzliche, vertragliche und interne Fristen unterscheidbar; Auslöser, Kalender, Rechenweg, Owner und Eskalation |
| Trend & Product Safety | PMS, RM, Biostatistik | Zähler/Nenner, Baselines, Strata, statistische Ergebnisse, Verzerrungen, Signalentscheidungen |
| PSUR Workspace | PMS, KB, RM, RA | Zyklus, Gruppierung, Snapshot, Kapitelentwurf, Quellen, Konsistenzprüfungen, Freigabe und simulierte BS-Bewertung |
| Actions & Change Requests | QM / RM / Technik | Untersuchung, CAPA/FSCA, Verantwortliche, Wirksamkeit, Rückkopplung in kontrollierte Dokumente |
| Audit Explorer | QM, Auditor | Wer hat was warum auf welcher Evidenz geändert/freigegeben; exportierbare, prüfbare Kette |
| Quellen- und Policy-Verwaltung | Autorisierte Fachadministration | Versionierte Quellen, Formulare, Kalender, Baselines und Agentenpolicies mit Change Control |

**Case Workbench:** links die nachvollziehbare Zeitachse, zentral die medizinisch-technische Bewertung, rechts die Evidenz-/QA-Sicht. Empfehlungen sind an den Quellen direkt prüfbar. Kritische Datenlücken und abweichende Reviewer-Ansichten werden nicht hinter einem grünen Gesamtscore verborgen.

**Cohors-Branding:** vorhandenes Logo unverändert verwenden; Navy/Orange als visuelle Grundrichtung, helle gut lesbare Arbeitsflächen. Orange als Markenfarbe wird nicht alleiniger Alarmindikator. Status benötigt zusätzlich Text/Icon; keine reine Rot-Grün-Codierung.

Die Logoanimation ist für einen optionalen Einstieg bzw. die Vorführung vorgesehen, nicht für dauernde Bewegung in der klinischen Arbeitsfläche. Statisches Logo bei `prefers-reduced-motion`, abschaltbare/überspringbare Animation, kein erzwungener Ton. Animation und Bild bleiben bereitgestellte Originalassets.

UI und Vorlagen werden lokalisierbar aufgebaut. Deutsches Pilot-UI und englische Demonstrationsberichte sind ein umsetzbarer Start; landesspezifisch verpflichtende Sprachfassungen werden vor realer Nutzung fachlich geprüft und separat freigegeben. Übersetzung ändert nicht verdeckt die Aussage einer genehmigten Fassung.

Barrierearme Bedienung umfasst Tastatursteuerung, sichtbaren Fokus, ausreichende Kontraste, beschriftete Diagramme und verständliche Bestätigungsdialoge. Die Freigabe zeigt ausdrücklich **welche Version und welche Handlung** bestätigt wird.

## 14. Sicherheit, Auditierbarkeit, GDPR und AI Act

### 14.1 Sicherheitsgrenzen

| Risiko | Technische / organisatorische Kontrolle |
| --- | --- |
| Prompt Injection aus PDF, Web oder Beschwerde | Fremdinhalte strikt als Daten; erlaubte Tools serverseitig begrenzen; keine Codeausführung aus Dokumenten; adversariale Tests |
| Unberechtigtes Retrieval | Tenant-/Produkt-/Rollenfilter im Knowledge-Service; kein allein vom LLM erzeugter Filter; Zugriff erneut am Dokument prüfen |
| Privilegienausweitung | Agent-Identität getrennt von Approver/Submission-Identität; keine Freigabe-Tools im Agentenkatalog |
| Datenabfluss | Private Datenendpunkte, kontrollierter Egress, keine freien Upload-/HTTP-Tools, API-Scopes und Secret-Trennung |
| Manipulierte Freigabe | Content-Hash, stabile IDs, RowVersion, signierter Approval-Scope, serverseitige Gate-Prüfung |
| Veraltete Wissensbasis | Gültigkeitsfilter und versionierter Snapshot; Ingestion-/Index-Frische im UI; Reassessment bei Änderungen |
| Halluzinierte Begründung | Claim-Evidence-Prüfung, Quellenvalidierung, Assertion-/Zahlenchecks, Assurance und Human Review |
| Fehlende Auditspur | Fachliche Transaktion und Audit atomar; nicht gesampelte Sicherheitsereignisse; Ledger-/Digest-Prüfung |
| Modellwechsel | Versionierte Release-Baseline, Regression, fachliche Freigabe; kein stiller Ersatz durch „latest“ |

### 14.2 Auditmodell und reproduzierbare Nachvollziehbarkeit

AuditEvent enthält Actor-ID, tatsächliche Rolle, Aktion, Objekt-/Versions-ID, Zeit, Grund, Vorher-/Nachher-Referenz, Korrelation und Hash. Personen, Systemkomponenten und Agenten werden unterscheidbar protokolliert.

Für kritische AuditEvents wird eine append-only Ledger-Tabelle in Azure SQL vorgesehen. Datenbank-Digests und freigegebene Artefakte werden mit getrennter Berechtigung in unveränderbarem Blob Storage abgelegt und regelmäßig verifiziert. SQL-Auditing ergänzt privilegierte Datenbankoperationen. **Temporal Tables allein sind kein manipulationsnachweisendes Audit.** [A5], [A8], [A9]

Der Infrastruktur-Spike prüft für Digest-Publishing ausdrücklich unterstützte Storage-Redundanz, geschützte Append-Writes, Locked Policy, SQL-Identität und Erreichbarkeit im privaten Netz. Ein nicht unterstützter automatischer Upload wird nicht durch pauschal offene Storage-Firewalls kaschiert. Falls erforderlich, publiziert ein dedizierter, überwachter Worker den von SQL erzeugten Digest über autorisierte private Verbindungen; dessen Verfügbarkeit und Berechtigungstrennung sind dann zusätzlich nachzuweisen. Ein Ledger für AuditEvents schützt nicht automatisch jede andere Tabelle; fachliche Versionen und eingefrorene Belege werden deshalb mit ihren Hashes referenziert und beim Zugriff/Export abgeglichen.

Eine Audit-Datei enthält ein Manifest mit Hashes, Versionen, Approval-Scope, Quellenreferenzen und Prüfbericht. Ein Hash ohne geschützten externen Bezugspunkt reicht nicht als Nachweis gegen nachträgliche Änderung durch hoch privilegierte Akteure.

Gespeichert werden tatsächliche Modellantworten, fachliche Begründungen, aufgerufene Tools, bereitgestellte Evidenz, Modell-/Prompt-/Regel-/Template-Version und menschliche Änderungen. Nicht gespeichert werden vermeintlich vollständig zugängliche interne Gedankengänge des Modells.

Ein identischer Prompt garantiert keine bitweise identische Regeneration. Nachvollziehbarkeit bedeutet deshalb: den **damaligen tatsächlichen Output** und seine Eingaben nachweisen sowie kontrolliert neue Bewertungen vergleichen. Sie bedeutet nicht, eine stillgelegte Modellversion für immer neu ausführen zu können.

### 14.3 Datenschutz und Aufbewahrung

| Bereich | Vorgabe für Pilot und Zielbetrieb |
| --- | --- |
| Zweck und Rollen | Zweckgebundene Produktsicherheitsverarbeitung; Verantwortlicher, Auftragsverarbeiter und ggf. weitere Rollen dokumentieren |
| Rechtsgrundlagen | Art. 6 DSGVO und für Gesundheitsdaten zusätzlich Art. 9 prüfen; nationale Schutz-/Offenlegungsvorschriften berücksichtigen |
| Datenminimierung | Synthetische Fälle im Pilot; reale öffentliche Inhalte können trotzdem personenbezogene Daten enthalten und werden geprüft/redigiert |
| Pseudonymisierung | Reidentifizierungsinformationen im Zielbetrieb getrennt, streng autorisiert; pseudonymisierte Daten bleiben personenbezogen |
| DPIA / Verarbeitungsverzeichnis | Vor Verarbeitung realer Gesundheitsdaten Datenschutz-Folgenabschätzung und Verzeichnis prüfen/erstellen; keine pauschale Freistellung durch Human Review |
| Transparenz / Rechte | Informationen, Berichtigung, Auskunft, Einschränkung und Löschprüfung mit dokumentierten gesetzlichen Ausnahmen |
| Quarantäne | Nicht geprüfte Rohdaten mit eng begrenzter, freigegebener Aufbewahrung; nicht automatisch in das Langzeit-WORM-Archiv |
| Regulatorische Evidenz | Erforderliche Nachweise objektbezogen aufbewahren; MDR-Mindestfrist für Implantatdokumentation ab letztem Inverkehrbringen berücksichtigen |
| Telemetrie | Keine Gesundheitsdaten in URLs, Ressourcennamen oder Standardlogs; kontrollierte Inhalte getrennt schützen |
| Löschung | Abhängigkeiten in Blob, SQL, Search, Caches, Agentenspeichern, Exporten und Backups berücksichtigen; Audit der Löschentscheidung erhalten |
| Drittlandtransfer | Nicht allein die Azure-Region betrachten: Modell-Deploymenttyp, Support, Subprozessoren, Connectoren und Backups prüfen |

Ein Locked-WORM-Container kann vor Ablauf nicht wie ein normaler Datenspeicher gelöscht werden. Deshalb werden Aufbewahrung und Datenminimierung **vor** dem Schreiben umgesetzt. Objektbezogene Retention wird aus Rechtsgrundlage, Dokumentklasse, Produktlebenszyklus und Legal Hold ermittelt; „15 Jahre ab Upload“ ist nicht gleich „15 Jahre ab letztem Inverkehrbringen“.

Der Pilot sperrt synthetische Demonstrationsbelege nur für eine kurze explizite Demo-Retention, statt echte Langzeitbindungen blind zu aktivieren. Eine spätere produktive WORM-Sperre ist eine gesondert zu bestätigende, schwer rückgängig zu machende Maßnahme.

Auch ein synthetischer Pilot verarbeitet bei echter Entra-Anmeldung personenbezogene Nutzungs-/Identitätsdaten. „Keine echten Patientenfälle“ ist daher nicht gleich „DSGVO nicht anwendbar“. [R11], [R12]

### 14.4 Modellverarbeitung in Azure

Nur geprüfte, von Azure angebotene Modelle und ein freigegebener Deploymenttyp werden eingesetzt. Microsoft unterscheidet Standard-/Geography-Verarbeitung, Data Zone und Global. **Ein Ressourcenstandort in der EU beweist weder bei Global noch bei EU Data Zone eine ausschließliche Verarbeitung in EU-Mitgliedstaaten.** [A10]

Die am 15. November 2026 abgerufene Deploymenttyp-Dokumentation ordnet die EU Data Zone der Azure EU Data Boundary zu und nennt mögliche EFTA-Verarbeitung, etwa in Norwegen und der Schweiz. Die gleichzeitig abgerufene Datenschutzbeschreibung verwendet noch „EU Member Nation“. Diese widersprüchlichen Aussagen werden nicht zugunsten einer ungeprüften EU-only-Zusage aufgelöst.

Für die bestätigte EU-Mitgliedstaaten-Grenze erhalten DataZone-, Global- und Developer-Deployments keine automatische Freigabe. Geeignete Standard-/Geography-Bereitstellungen sind vor Live-Nutzung anhand von Deployment-SKU, tatsächlicher Azure-Geografie und aktuellen Vertragsbedingungen zu prüfen; Standard ist kein Versprechen einer einzelnen Region. `EUDataZone` oder `EU` allein ist kein Nachweis. Eine EFTA-Verarbeitung wird dadurch nicht pauschal als rechtswidrig bewertet.

Konkrete Modellverfügbarkeit wird vor Deployment geprüft. Stateful Responses-/Agentenfunktionen und Missbrauchsmonitoring können zusätzliche Speicherung bzw. autorisierte Prüfungen verursachen. „Kein Training mit Kundendaten“ ist nicht gleich „keine Speicherung“.

Der bevorzugte Runtime-Vertrag minimiert providerseitig gespeicherten Gesprächszustand. Die fachliche Fallhistorie bleibt in Cohors. Echte Gesundheitsdaten erfordern eine gesonderte Prüfung der verwendeten Features, Verträge und Aufbewahrungseinstellungen.

### 14.5 AI-Act-Einstufung und Controls

Cohors benötigt eine eigene dokumentierte Zweckbestimmung. Ein PMS-Werkzeug eines Medizinprodukteherstellers ist nicht allein wegen der überwachten Implantate automatisch selbst ein Klasse-III-Medizinprodukt oder ein Anhang-I-High-Risk-System.

Gleichzeitig wäre „nur interne Qualitätssoftware“ keine ausreichende Entwarnung. Der aktuelle Art. 6 unterscheidet nicht-sicherheitsbezogene Unterstützung von Systemen, deren Ausfall Gesundheit und Sicherheit gefährdet; außerdem sind der Produkt-/Sicherheitskomponentenbezug und die erforderliche Drittprüfung zu prüfen. Medizinischer Human Review allein beseitigt diese Prüfung nicht. [R8]

Die Einstufungsakte behandelt: Zweckbestimmung, Systemgrenzen, Einfluss auf Sicherheitsentscheidungen, Anbieter-/Betreiberrolle des Herstellers und Softwarelieferanten, Anhang I/III, mögliche Änderungen der Zweckbestimmung und anwendbare Pflichten.

Die aktuelle Darstellung des konsolidierten Art. 113 nennt **2. August 2028** für Art.-6(1)/Anhang-I-Systeme sowie **2. Dezember 2027** für Art.-6(2)/Anhang-III-Systeme. Diese Daten ersetzen nicht bereits anwendbare Pflichten oder die individuelle Einstufung. Der frühere pauschale Termin 2.8.2027 wird nicht unverändert aus älteren Zusammenfassungen übernommen. [R9]

Unabhängig von der abschließenden Einstufung werden folgende Kontrollen als Sicherheitsbaseline eingeplant: AI-Literacy/Schulung, dokumentierte Grenzen, Human Oversight, Daten-/Evidenzgovernance, Qualitäts- und Risikomanagement, Robustheits-/Sicherheitsprüfung, Versionierung und Änderungsfreigabe, Vorfallbehandlung und laufende Leistungskontrolle.

Fragen nach verpflichtender Konformitätsbewertung, Registrierung, Grundrechte-Folgenabschätzung oder Anbieterpflichten werden in der Einstufungsakte beantwortet, nicht pauschal als für jeden Pilot zwingend behauptet. Das MDR-PMS der Implantate und die Überwachung des KI-Systems selbst sind zwei unterschiedliche Regelkreise.

### 14.6 Validierung und elektronische Freigaben

Risikobasierte Systemvalidierung nach den anwendbaren QMS-Verfahren und GAMP-5-orientierter Methodik umfasst Anforderungen, Design, Risikobewertung, Tests, Abweichungen und Freigaben. Normtexte werden nicht reproduziert und nicht als durch Azure automatisch erfüllt ausgegeben.

Entra-Anmeldung plus protokollierter Freigabeklick ist nicht automatisch eine qualifizierte elektronische Signatur. Signaturbedeutung, Authentifizierung, Identitätsbindung und nachträgliche Unveränderbarkeit sind gezielt zu validieren. 21 CFR Part 11 wird nur nach tatsächlicher US-Anwendbarkeit beurteilt; die Case Study rechtfertigt keine pauschale EU-Gleichsetzung.

## 15. Azure Well-Architected Framework

| Säule | Konkrete Cohors-Maßnahmen | Nachweis im Pilot | Zusätzlicher Zielbetriebsbedarf |
| --- | --- | --- | --- |
| Reliability | Persistierte Workflows, SQL-Outbox, Idempotenz, unabhängiger Watchdog, Retry/Backoff, sichtbare Fehler | Neustart im Human-Wait; KI-Ausfall ohne Fristenverlust; Unknown-Versand ohne Doppelmeldung | BIA, genehmigte RTO/RPO/SLO, zonenredundante Komponenten, DR-Übung und unabhängiger Notbetrieb |
| Security | Entra, minimale Rollen, Private Endpoints, Tool-Grenzen, sichere Freigabebindung, Datenschutz | Negative Berechtigungstests, Prompt-Injection-Fälle, Blockierung veränderter Pakete, Audit-Prüfung | Threat Modeling, Penetrationstest, DPA/DPIA, Zugriffsrezertifizierung, Produktionsfreigabe |
| Cost Optimization | Wenige Dienste, keine AKS-/Cosmos-/Broker-Dopplung, deduplizierte Verarbeitung, begrenzte Agentenschleifen | Kosten pro AgentRun/Fall, Budgets, sichtbare Quota-/Budgetabbrüche | Lastabhängige SKU-Optimierung, Budgetowner und Betriebskostenentscheidung |
| Operational Excellence | IaC, versionierte Releases, CI/CD mit OIDC, Runbooks, Fach-/Betriebstelemetrie getrennt | Reproduzierbare Bereitstellung nach Freigabe, Audit-Export, Rollback und Fehlerdemo | Change Control, 24/7-Verantwortung für kritische Fristen, Trainings- und Incidentprozesse |
| Performance Efficiency | Asynchrone Bearbeitung, begrenzte Parallelität, separater interaktiver API-Pfad, Indexprojektionen | Definierter Lasttest mit Batchimport und gleichzeitigem Review | Nachgewiesene Peaklast, Kapazitäts-/Modellquota-Planung, Isolation von PSUR-Batches |

**Pilot ist nicht Hochverfügbarkeit:** Eine einzelne kostengünstige Umgebung mit einer API-Replik erfüllt nicht automatisch produktive Zuverlässigkeitsziele. Für den Zielbetrieb werden zonenfähige Container-App-Umgebung, angemessene Replikazahl, SQL-/Storage-Redundanz und Search-Verfügbarkeit gemeinsam betrachtet. Modell-Failover erfolgt nur auf vorab fachlich freigegebene Versionen und zulässige Verarbeitungsregionen.

**Notbetrieb:** Bei Cloud-/Regionalausfall muss eine freigegebene organisatorische SOP mit aktuellem Fristen-/Fallregister und nationalem Meldeweg existieren. Ein zweiter Agent im gleichen ausgefallenen Dienst ist kein Notfallkonzept.

**Kostenmodell:** monatliche Basiskosten für Datenbank, Search, Netz, Registry, Monitoring und Mindestkapazität plus tatsächliche Modell-, Speicher- und Ausführungskosten. Vor Deployment werden konkrete SKUs und Tokenbudgets kalkuliert. Es werden keine unbelegten Euro-Beträge oder pauschalen Einsparungen behauptet.

**Startwerte zur Abnahme, keine zugesagten Produktions-SLAs:** 1.000 synthetische Rohinformationen, mindestens zwölf fachlich kuratierte Fallszenarien, zwei Produktfamilien und zehn gleichzeitige Testnutzer. Nicht-KI-Leseoperationen sollen im definierten Testprofil p95 unter zwei Sekunden liegen. Für LLM-Aufgaben werden End-to-End-Latenz, Wartezeit, Fehlerrate und Kosten getrennt gemessen; Fristenalarmierung erhält eine separat zu prüfende maximale technische Erkennungs-/Zustelllatenz.

## 16. Implementierungskonzept

### 16.1 Geplante Repository-Struktur

Die folgende Struktur wird erst nach Umsetzungsfreigabe angelegt. Das vorhandene Quellkonzept bleibt unverändert.

```text
source\
  Konzept_PMS-System_Klasse-III-Implantate_MDR.docx
.github\
  workflows\
src\
  Cohors.Domain\
  Cohors.Application\
  Cohors.Infrastructure\
  Cohors.Api\
  Cohors.Workflows\
  Cohors.Operations\
  Cohors.Agents\
  Cohors.Web\
contracts\
  api\
  agent-outputs\
  integrations\
  regulatory-schemas\
policies\
  jurisdiction\
  calendars\
  sources\
  agent-releases\
demo\
  devices\
  evidence\
  cases\
  exposure\
  simulator\
tests\
  Domain\
  Workflows\
  Integration\
  E2E\
  Evaluation\
infra\
  main.bicep
  modules\
docs\
  architecture\
  process\
  validation\
  operations\
```

Die Projekte entsprechen Verantwortungsgrenzen, nicht automatisch unabhängigen Deployments. Frontend, API-Verträge und Tests verwenden dieselben Versionen des Zustands- und Berechtigungsmodells.

### 16.2 Fachliche API-Commands

Commands sind fachlich benannt, nicht ein universelles „Status ändern“:

| Command | Wesentliche Prüfung |
| --- | --- |
| IntakeSource / RegisterComplaint | Pflichtmetadaten, Quarantäne, Idempotenz; unvollständige medizinische Daten trotzdem annehmen |
| LinkEvidence / ResolveDuplicate | Produktzugriff, Matchgrund, Version; Originale erhalten |
| RecordAwarenessCorrection | Berechtigung, neue Quelle und Begründung; Fristwirkung explizit |
| RequestAssessment / RecordAssessmentDraft | Eingefrorener Kontext, Output-Schema, zulässige Agentenrolle |
| RecordQualityReview / DispositionFinding | Bezogene Version, unabhängiger Lauf, menschliche Ausnahmebefugnis |
| RequestAdditionalInformation | Minimaldatensatz, Notwendigkeit, Privacy-/Freigabestatus |
| SubmitMedicalDecision | Qualifikation/Rolle, Assessment-Version, Gründe und QA-Disposition |
| ApproveRegulatoryPackage | Gültige medizinische Freigabe, getrennte Identität, Empfänger-/Schema-/Versionskontrolle |
| SubmitToSimulator | Unverändertes freigegebenes Paket; ausschließlich Simulationsadapter |
| ReconcileSubmission | Korrelation, Paket-Hash, plausibler Receipt-Status; kein pauschales Retry |
| OpenCAPA / VerifyEffectiveness | Verantwortung, Maßnahmenkriterium, unabhängige Abschlussprüfung |
| FreezePSURSnapshot / ApprovePSUR | Gruppierung, Perioden, Nenner, offene Fälle, Freigabebindung |
| ReopenCase / CloseCase | Neue Evidenz bzw. begründeter Abschluss, offene Verpflichtungen und Maßnahmen prüfen |

Für schreibende Commands werden Idempotency-Key und erwartete Version geprüft. Konflikte geben einen verständlichen Fehler und den aktuellen Stand zurück; keine stille Überschreibung.

### 16.3 Arbeitspakete und Abhängigkeiten

| Paket | Inhalt / Ergebnis | Abhängigkeit |
| --- | --- | --- |
| W01 Fachliche Baseline | Fehlende Anforderungsmatrix abgleichen, Zweckbestimmung, Rollen, Demo-Szenarien, Regel-/Kalenderprofile und Datenklassen festlegen | Architekturentscheidung |
| W02 Grundgerüst | React/.NET, Entra, Domain/API, SQL-Migrationen, Dokumentversionen, Audit/Outbox, Branding | W01 |
| W03 Prozesskern und Fristen | Zustandsübergänge, getrennte Human Gates, LegalDue/SafetyDue, Watchdog, Dringlichkeitsweg | W02 |
| W04 Intake und Evidenz | Synthetischer Import, reale öffentliche Leseadapter, Deduplikation, Produktzuordnung, Source-Versionen, AI-Search-Projektion | W02 |
| W05 Agentische Analyse | Sechs Rollen schrittweise, strukturierte Assessments, Assurance, begrenzte Recherche und Informationsanforderung | W03, W04 |
| W06 Reporting und Simulator | MIR-Mapping/XSD-Prüfung, eingefrorene Pakete, Versandfreigabe, Quittungen, Unknown/Reconcile und Follow-up | W03, W05 |
| W07 Regelkreis | Deterministische Trends, CAPA/Wirksamkeit, RM-/TD-Änderungsaufträge, PSUR-Snapshot und Autorenprozess | W04, W05, W06 |
| W08 Arbeitsoberflächen | Cockpit, Inbox, Workbench, Review, Regulatory Desk, Fristen, Trend, PSUR, Audit | W02–W07; inkrementell pro vertikalem Schnitt |
| W09 Azure-Bereitstellung | Bicep, private Datenpfade, Identitäten, Monitoring, sichere CI/CD, Kosten-/Quota-Prüfung | W02; vollständige Freigabe vor Deployment |
| W10 Abnahme und Demo | Fachliche Evaluation, Rechte-/Fristen-/Recovery-Tests, Auditpaket, Runbook und vorführbarer End-to-End-Ablauf | W03–W09 |

Zuerst wird ein schmaler **vertikaler Schnitt** fertiggestellt: synthetische Beschwerde → persistierter Fall → Frist → zunächst einfacher Entwurf → menschliche Freigaben → simulierte Meldung → Auditnachweis. Erst danach werden Recherche, QA, Trends und PSUR vertieft. So existiert früh ein überprüfbarer Prozess statt zuerst sechs unverbundener Agentendemos.

Die Umsetzung beginnt in einer zusammenhängenden Coding-Session, weil Datenmodell, Prozesszustände und API-Verträge eng gekoppelt sind. Unabhängige Konnektoren oder UI-Bereiche können später auf stabilen Verträgen getrennt bearbeitet werden; gleichzeitige Änderungen derselben Kernverträge werden vermieden.

### 16.4 Release- und Änderungsmodell

Ein ReleaseManifest bindet Anwendungsversion, Datenbankschema, Workflowversion, Modellbereitstellung/-version, Prompt-Hash, Toolkatalog, Output-Schemas, Regel-/Kalenderprofile, Formularversionen und Test-/Evaluationsnachweise.

Laufende Fälle bleiben ihrer Baseline zugeordnet. Eine Änderung erzeugt eine Impact-Analyse: Welche offenen Fälle, freigegebenen Pakete und PSUR-Snapshots sind betroffen? Sicherheitskorrekturen dürfen nicht bis zum nächsten PSUR-Zyklus aufgeschoben werden; ihre kontrollierte Migration und Neubewertung werden dokumentiert.

Schemaänderungen sind rückwärtskompatibel auszurollen, solange alte Workflows aktiv sind. Eine bloße Rückkehr zum alten Containerimage ist kein vollständiger Datenbankrollback. Es gibt keine automatische Modell-/Prompt-Selbstoptimierung im produktiven Sicherheitsprozess.

## 17. Abnahme und messbare Nachweise

### 17.1 Verpflichtende Testszenarien

| Test | Erwarteter Nachweis |
| --- | --- |
| T01 Doppelter Eingang | Ein Vorfall wird nicht doppelt gezählt; beide Quellen und Kenntniszeitpunkte bleiben erhalten |
| T02 Unbekannte UDI | Intake funktioniert; Identitätslücke ist sichtbar; kein erfundener UDI-Wert |
| T03 Fehlender klinischer Befund | QA erstellt begründete Nachforderung; Unsicherheit bleibt sichtbar; Frist läuft weiter |
| T04 Unbelegte Kausalität | Quellenvalidator/QA bzw. Reviewer verhindert eine unberechtigte endgültige Schlussfolgerung |
| T05 KI-/Search-Ausfall | Manueller Review-/Vorläufigkeitsweg und Fristenüberwachung funktionieren ohne KI |
| T06 Parallele Reviewer-Änderung | Veraltete Version wird abgewiesen; keine stille Überschreibung oder Freigabe falscher Inhalte |
| T07 Änderung nach Freigabe | Neuer Inhalt/Empfänger/Anhang invalidiert die Versandberechtigung für dieses Paket |
| T08 Unberechtigter Agent oder Nutzer | Kein direkter DB-/Dokumentzugriff außerhalb des Scopes; kein Approve-/Submit-Bypass |
| T09 Prompt Injection | Fremddokument kann keine Quellenrechte erweitern, keine Secrets abfragen und keine Meldung auslösen |
| T10 Deadline-Grenzen | 2/10/15 Tage, Tag 0, Zeitzone, DST, Jahreswechsel, Feiertage, EU-Arbeitstagsregel und getrennte GB-/NI-Regeln |
| T11 Neue Informationen | Belegte Ausnahme zu reportabilityAwarenessAt wird getrennt abgebildet; verspätete Bearbeitung setzt keinen neuen Beginn |
| T12 Restart und Timer | Persistierte Freigabe und Frist überleben Neustart; verpasste Watchdog-Prüfung wird erkannt |
| T13 Timeout nach möglichem Versand | TransmissionUnknown bleibt offen; Reconcile verhindert blinde Doppelmeldung |
| T14 MIR-Kontrakt | Export validiert gegen gepinnte XSD; fehlende Pflichtdaten werden nicht erfunden |
| T15 Statistische Referenz | Berechnung stimmt mit unabhängig kuratierter Referenz überein; fehlender/unpassender Nenner sperrt Inzidenzaussage |
| T16 PSUR-Konsistenz | Zähler, Nenner, Regionen und Perioden stimmen; offene Fälle sichtbar; jede Konklusion nachvollziehbar |
| T17 CAPA-Abschluss | Abschluss erst nach dokumentierter Wirksamkeitsbewertung durch berechtigte Person |
| T18 Audit / Retention | Manifest-/Digestprüfung erkennt Manipulation; Aufbewahrungs- und Löschregeln wirken über relevante Speicher hinweg |
| T19 Simulator-Schutz | Kein produktiver Behördenendpunkt erreichbar; alle Quittungen und Exporte sichtbar als Demo markiert |
| T20 Wiederherstellung | SQL-/Dokumentstände und Verknüpfungen wiederhergestellt; Fristen/Outbox abgeglichen; Search neu aufbaubar |

**Matrixspezifische Ergänzungen:** Nebenwirkungsausnahme C-03, Sammelvereinbarungen C-12, Beweissicherung E-02, FSCA-/FSN-Gates, PRRC-Zuständigkeiten, PSUR-Gruppierung und Produktmetadaten brauchen gezielte Nachweise. Vollständiger ID-Bezug und Restgrenzen stehen im separaten Einzelabgleich.

### 17.2 AI-Evaluation

Ein fachlich kuratierter Evaluationssatz enthält bestätigte und verworfene Fälle, fehlende Daten, widersprüchliche Quellen, ähnliche Produktnamen, unterschiedliche Sprachen, ungewöhnliche technische Ursachen und zeitkritische Szenarien. Erwartete Entscheidungen werden unabhängig von den Prompts festgelegt.

Gemessen werden: Retrieval-Relevanz, Claim-Belegbarkeit, korrekte Quellenreferenzen, Pflichtfeldvollständigkeit, Fakt-/Hypothesentrennung, medizinische/technische Plausibilität, erkannte Datenlücken, kritische Auslassungen, unzulässige Handlungsvorschläge und menschlicher Überarbeitungsbedarf.

Strukturelle Sicherheitsinvarianten haben im Testsatz **null tolerierte Verstöße**: keine Freigabe ohne berechtigte Menschen, keine echte Submission, keine stille Fristverschiebung, keine Erfolgsquittung bei Unknown. Ein null Fehler aufweisender kleiner Demo-Satz beweist jedoch keine klinische Sensitivität im Echtbetrieb.

Fachliche Qualitätsgrenzen und ein angemessener unabhängiger Holdout müssen Medical Safety/QM vor Abnahme genehmigen. Trainings-/Prompt-Tuning-Beispiele und Abnahmefälle werden getrennt gehalten. Modellbewertung durch ein anderes LLM ergänzt, ersetzt aber nicht die menschliche Fachbewertung.

### 17.3 Demonstrationsdramaturgie

1. Cohors öffnet mit dem bereitgestellten Branding; Simulator-/Live-/Replay-Modus ist eindeutig sichtbar.
2. Eine synthetische Beschwerde und ein Duplikat treffen ein. Produktzuordnung, Awareness und Frist entstehen nachvollziehbar.
3. Die Analyse verknüpft eine konkrete Test-/Spezifikationsversion und findet fehlende Evidenz.
4. Assurance formuliert eine begründete Nachforderung; ein Reviewer prüft und bearbeitet den Entwurf.
5. Der Fristenalarm demonstriert, warum eine vorläufige Meldung unabhängig von der Nachforderung vorbereitet wird.
6. Medical Safety und RA geben ein bestimmtes Paket frei; eine Änderung danach blockiert den ersten Versandversuch.
7. Der Simulator zeigt erst einen unklaren Status und anschließend einen abgeglichenen Empfang.
8. Der Fall fließt in CAPA/Trend und den eingefrorenen PSUR-Snapshot ein. Der Audit Explorer erklärt die komplette Kette.

### 17.4 Messung der Case-Study-Ziele

| Ziel | Messdefinition | Grenze des Piloten |
| --- | --- | --- |
| 70 % weniger Signal-to-Decision-Zeit | Gleiche kuratierte Falltypen manuell und KI-unterstützt vergleichen; Kenntnis-/Eingangszeit, Start/Ende aktiver Bearbeitung, finale menschliche Entscheidung und Wartezeiten separat messen | Ohne unabhängigen manuellen Vergleich keine belegte Prozentverbesserung; Erlernen eines bereits bekannten Demo-Falls verzerrt die Messung |
| Keine verspätete Meldung | Alle fälligen RegulatoryObligations gegen nachgewiesenen, zum Kanal passenden Übermittlungs-/Annahmenachweis prüfen; Unknown ist kein Erfolg | Eine nachgewiesene Fristtreue im Simulator ist keine Garantie für reale Behördenkanäle |
| 60 % weniger PSUR-Aufwand | Aktive menschliche Bearbeitungszeit für denselben Datenstand und Qualitätsmaßstab vergleichen, einschließlich Review/Korrektur | Reine LLM-Generierungszeit ist nicht der PSUR-Gesamtaufwand |
| Trending-Feststellung geschlossen | Freigegebene Methodik, nachvollziehbare Nenner, reproduzierbare Auswertung, dokumentierte Signalentscheidung und Maßnahmennachweis | Nur die zuständige BS kann eine reale Feststellung als geschlossen bewerten |

## 18. Offene Entscheidungen und Freigabe-Gates

| Gate | Vor Pilotentwicklung / vor Echtbetrieb | Verantwortlich |
| --- | --- | --- |
| G01 Einzelanforderungen | Matrix und Zuordnung aller 118 IDs vorhanden. Konkrete Pilotnachweise vervollständigen und Rest-/Zielbetriebsgrenzen fachlich bestätigen; keine pauschale Erfüllungsbehauptung | Auftraggeber, RA, QM |
| G02 Fachliche Regeln | Reportability-Kriterien, Risikobewertung, Kalender, Zuständigkeiten, Sprachen und SOP-Fristen bestätigen | Medical Safety, RA, PRRC |
| G03 Datenschutz | Echtbetrieb erst nach Rechtsgrundlagen-/DPIA-/Vertrags-/Retention-Prüfung; Nachforderungen separat beurteilen | Datenschutz, Legal, RA |
| G04 AI-Act-/Produktqualifikation | Zweckbestimmung und tatsächlichen Einfluss von Cohors beurteilen; Anbieter-/Betreiberpflichten bestimmen | Legal, RA, QM |
| G05 Azure-Machbarkeit | Subscription, EU-Regionen, Modell-/SKU-Verfügbarkeit, Quota, Netzwerkzugriff, Kosten und Monitoring bestätigen | Architektur, Plattformbetrieb, Budgetowner |
| G06 Reale Schnittstellen | Autorisierte Zugänge, unterstützte Verträge, Schema-/Kanalversionen und externe Annahmetests | Integration, RA, jeweiliger Systemowner |
| G07 Validierung | Fachliche Qualität, Sicherheitsinvarianten, Recovery, Audit und Betriebs-SOPs abnehmen | QM, Medical Safety, RA, IT |
| G08 Produktion | Eigenständige Freigabe nach G02–G07; echte externe Kommunikation bleibt bis dahin gesperrt | Verantwortlicher Hersteller |

**Ergebnis dieses Entwurfs:** Der Pilot ist fachlich und technisch abgegrenzt, einschließlich aller 118 Matrixbezüge. Lokale Teile sind implementiert und geprüft; vollständige Pilotabnahme und Azure-Integration stehen noch aus. Keine Azure-Bereitstellung und keine Freigabe für echte Patientenfälle oder Behördenmeldungen.

## 19. Quellenverzeichnis

Die bereitgestellten Unterlagen sind die fachliche Ausgangsbasis. Zeitabhängige Aussagen wurden für diesen Entwurf gegen die unten genannten Primärquellen geprüft. Die Quellenliste ist keine pauschale Behauptung, sämtliche nationalen Rechts- oder Sprachvorgaben abschließend geprüft zu haben.

### Regulatorik und öffentliche Schnittstellen

- **R1:** Verordnung (EU) 2017/745, insbesondere Art. 10, 15, 61, 83–92, Anhänge III und XIV. [Amtliche MDR-Fundstelle](https://eur-lex.europa.eu/eli/reg/2017/745/oj); jeweils geltende konsolidierte Fassung im Rechtsregister führen. Der direkte EUR-Lex-Abruf war eingeschränkt; Detailabgleich hier über die bereitgestellte Rechtsanalyse und die genannten aktuellen MDCG-Primärquellen.
- **R2:** MDCG 2023-3 Rev.2, insbesondere Fragen 13–15, Seiten 17–20: unverzügliche Meldung, Kalendertage, VO 1182/71, Awareness und neue Informationen. [Volltext](https://health.ec.europa.eu/document/download/af1433fd-ed64-4c53-abc7-612a7f16f976_en?filename=mdcg_2023-3_en.pdf).
- **R3:** MDCG 2022-21, insbesondere §§ 3.2, 4, 5 und Anhänge I–V: PSUR-Laufzeit, Gruppierung, Zeitplanung und Darstellung. [Volltext](https://health.ec.europa.eu/document/download/a7df24c3-d4a3-4218-a8e0-726febfa01c2_en?filename=mdcg_2022-21_en.pdf).
- **R4:** Europäische Kommission: [EUDAMED Overview](https://health.ec.europa.eu/medical-devices-eudamed/overview_en), Pflichtmodule und Status Vigilance/PMS.
- **R5:** Europäische Kommission: [PMSV reporting forms](https://health.ec.europa.eu/medical-devices-sector/new-regulations/guidance-mdcg-endorsed-documents-and-other-guidance/pmsv-reporting-forms_en), MIR 7.3.1, akzeptierte Builds und XSD/XSL.
- **R6:** Europäische Kommission: [EUDAMED M2M Technical Documentation](https://webgate.ec.europa.eu/eudamed-help/en/data-exchange/machine-to-machine/support/technical-documentation.html) und [Playground-Dokumentation](https://webgate.ec.europa.eu/eudamed-play-help/en/documentation/technical-documentation.html). Dort veröffentlichte Vigilanz-Datenwörterbücher sind kein Nachweis einer produktiv zugänglichen Vigilanz-Schnittstelle.
- **R7:** MHRA: [Vigilance reporting requirements](https://www.gov.uk/government/publications/medical-devices-post-market-surveillance-requirements/vigilance-reporting-requirements) und [PMS requirements](https://www.gov.uk/government/publications/medical-devices-post-market-surveillance-requirements), GB/NI und MORE.
- **R8:** AI Act Service Desk der EU-Kommission: [konsolidierter Art. 6](https://ai-act-service-desk.ec.europa.eu/en/ai-act/article-6), einschließlich Sicherheitskomponenten-/Qualitätskontrollabgrenzung.
- **R9:** AI Act Service Desk: [konsolidierter Art. 113](https://ai-act-service-desk.ec.europa.eu/en/ai-act/article-113); ergänzend [Kommissionsübersicht AI Act](https://digital-strategy.ec.europa.eu/en/policies/regulatory-framework-ai). Der Service Desk verweist auf die konsolidierte Fassung vom 27.7.2026.
- **R10:** FDA: [MAUDE limitations and data](https://open.fda.gov/data/maude/) und [Device Event API](https://open.fda.gov/apis/device/event/).
- **R11:** Verordnung (EU) 2016/679: [DSGVO](https://eur-lex.europa.eu/eli/reg/2016/679/oj), insbesondere Art. 5, 6, 9, 17, 25, 28, 30, 32, 35 und Kapitel V; ergänzt durch bereitgestellte rechtliche Bewertung.
- **R12:** Deutschland: [§ 203 StGB](https://www.gesetze-im-internet.de/stgb/__203.html), bei relevanten Offenlegungen zusätzlich zu prüfen.
- **R13:** NCBI: [A General Introduction to the E-utilities](https://preview.ncbi.nlm.nih.gov/books/NBK25497/), Nutzung und Rate Limits.
- **R14:** Nationale Verfahren gemäß bereitgestelltem Fachkonzept und regulatorischer Analyse, Kapitel zu HPRA/BfArM. Direkte Seitenabrufe waren technisch eingeschränkt; konkrete URLs, Formulare, Empfänger, aktuelle Nutzungsbedingungen und Sprachen sind ein explizites Adapter-Freigabe-Gate, kein bereits abgenommener Live-Anschluss.
- **R15:** Verordnung (EEC, Euratom) Nr. 1182/71, Art. 3(1)–(5). [Amtliche EU-Fundstelle](https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX:31971R1182); Artikeltext zusätzlich auf [legislation.gov.uk](https://www.legislation.gov.uk/eur/1971/1182/article/3) abgeglichen. Dortige spätere britische Ergänzungen, insbesondere Abs. 6, werden nicht als EU-Recht übernommen. Für die aktuelle Anwendung auf MDR-Meldungen siehe R2.

### Azure und technische Architektur

- **A1:** Microsoft Learn: [Application design for AI workloads](https://learn.microsoft.com/azure/well-architected/ai/application-design).
- **A2:** Microsoft Learn: [Workflow-oriented multi-agent patterns](https://learn.microsoft.com/agents/architecture/multi-agent-workflow-oriented).
- **A3:** Microsoft Learn: [Durable human interaction](https://learn.microsoft.com/azure/durable-task/common/durable-task-human-interaction), [External events](https://learn.microsoft.com/azure/durable-task/common/durable-task-external-events).
- **A4:** Microsoft Learn: [Well-Architected Container Apps](https://learn.microsoft.com/azure/well-architected/service-guides/azure-container-apps).
- **A5:** Microsoft Learn: [Well-Architected Azure SQL Database](https://learn.microsoft.com/en-us/azure/well-architected/service-guides/azure-sql-database).
- **A6:** Microsoft Learn: [Azure Functions execution-model comparison](https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-in-process-differences), insbesondere .NET 10, Isolated Worker und Flex Consumption.
- **A7:** Microsoft Learn: [AI Search security and shared responsibility](https://learn.microsoft.com/en-us/azure/search/search-security-built-in).
- **A8:** Microsoft Learn: [Immutable Blob Storage](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-storage-overview).
- **A9:** Microsoft Learn: [SQL Ledger overview](https://learn.microsoft.com/sql/relational-databases/security/ledger/ledger-overview), [Digest management](https://learn.microsoft.com/sql/relational-databases/security/ledger/ledger-digest-management?view=sql-server-ver17) und [Ledger limitations](https://learn.microsoft.com/sql/relational-databases/security/ledger/ledger-limits?view=sql-server-ver17); Digest-/Netzwerkkonfiguration im technischen Spike prüfen.
- **A10:** Microsoft Learn: [Data, privacy and security for Foundry Models sold by Azure](https://learn.microsoft.com/en-us/azure/foundry/responsible-ai/openai/data-privacy).

**Implementierungspräzisierung zum EU-Gate (Konzept v0.2):** Die [Deploymenttypen](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/deployment-types) beschreiben EU Data Zone als EU Data Boundary mit möglichen EFTA-Ländern, während A10 noch EU Member Nations nennt. Cohors löst diesen Widerspruch nicht als EU-only-Zusage auf. Die regionale SKU ist nur eine notwendige Vorbedingung; der konkrete Deploymentnachweis muss Geography und mögliche Verarbeitungsländer prüfen. Das ist eine geografische Pilotgrenze, keine pauschale Aussage zur DSGVO-Rechtmäßigkeit von EFTA-Verarbeitung. Technische Konfigurationsbindung und verbleibendes Azure-/Fachgate stehen in `docs/runtime-contract.md`.

[R1]: https://eur-lex.europa.eu/eli/reg/2017/745/oj
[R2]: https://health.ec.europa.eu/document/download/af1433fd-ed64-4c53-abc7-612a7f16f976_en?filename=mdcg_2023-3_en.pdf
[R3]: https://health.ec.europa.eu/document/download/a7df24c3-d4a3-4218-a8e0-726febfa01c2_en?filename=mdcg_2022-21_en.pdf
[R4]: https://health.ec.europa.eu/medical-devices-eudamed/overview_en
[R5]: https://health.ec.europa.eu/medical-devices-sector/new-regulations/guidance-mdcg-endorsed-documents-and-other-guidance/pmsv-reporting-forms_en
[R6]: https://webgate.ec.europa.eu/eudamed-help/en/data-exchange/machine-to-machine/support/technical-documentation.html
[R7]: https://www.gov.uk/government/publications/medical-devices-post-market-surveillance-requirements/vigilance-reporting-requirements
[R8]: https://ai-act-service-desk.ec.europa.eu/en/ai-act/article-6
[R9]: https://ai-act-service-desk.ec.europa.eu/en/ai-act/article-113
[R10]: https://open.fda.gov/data/maude/
[R11]: https://eur-lex.europa.eu/eli/reg/2016/679/oj
[R12]: https://www.gesetze-im-internet.de/stgb/__203.html
[R13]: https://preview.ncbi.nlm.nih.gov/books/NBK25497/
[R14]: #19-quellenverzeichnis
[R15]: https://www.legislation.gov.uk/eur/1971/1182/article/3
[A1]: https://learn.microsoft.com/azure/well-architected/ai/application-design
[A2]: https://learn.microsoft.com/agents/architecture/multi-agent-workflow-oriented
[A3]: https://learn.microsoft.com/azure/durable-task/common/durable-task-human-interaction
[A4]: https://learn.microsoft.com/azure/well-architected/service-guides/azure-container-apps
[A5]: https://learn.microsoft.com/en-us/azure/well-architected/service-guides/azure-sql-database
[A6]: https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-in-process-differences
[A7]: https://learn.microsoft.com/en-us/azure/search/search-security-built-in
[A8]: https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-storage-overview
[A9]: https://learn.microsoft.com/sql/relational-databases/security/ledger/ledger-overview
[A10]: https://learn.microsoft.com/en-us/azure/foundry/responsible-ai/openai/data-privacy
