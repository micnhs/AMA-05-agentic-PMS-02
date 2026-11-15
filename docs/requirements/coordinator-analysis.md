# Cohors – Einzelabgleich der Anforderungsmatrix

**Stand:** 15. November 2026 · **Version:** 0.1

**Ergebnis:** Alle 118 Original-IDs sind dem genehmigten Pilotumfang, Architekturstellen und einem Nachweis-/Restarbeitsbedarf zugeordnet. Das ist eine vollständige Anforderungszuordnung, **keine Bestätigung von 118 erfüllten Anforderungen**. Die technische Gesamtintegration und die fachlich-regulatorische Abnahme sind weiterhin offen.

## Quelle und Nachweisgrenze

Quelle ist die vom Nutzer bereitgestellte **PMS_Anforderungsmatrix_MDR_KlasseIII_Implantate.xlsx**, Stand 15.11.2026, mit den Ergänzungen der Version 1.1. SHA-256:

`6a70b6ca1d287f340abc43097b8538ba9b0b14b676e2063a4aa70245831785ca`

Gelesen wurden alle sechs Blätter. Die Tabelle `Anforderungen` umfasst `A2:L120`: 118 eindeutige Anforderungen, 104 Muss und 14 Soll. Keine doppelten oder ungültigen IDs, keine leeren Pflichtspalten und keine unaufgelösten ID-Verweise in den Begleitblättern wurden gefunden. Die 62 gespeicherten Formeln besitzen Ergebniswerte ohne gespeicherte Excel-Fehler. Das Original wurde nicht verändert oder neu berechnet.

**Alle Originalstatus stehen auf „Offen“.** Diese Quellwerte bleiben erhalten. Ein bestandener Teiltest wird nicht nachträglich als originale Herstellerfreigabe oder als Erfüllung einer gesamten, mehrteiligen MDR-Anforderung ausgegeben.

Die Matrix nennt als Rechtsbasis die MDR-Fassung von 2017 und ergänzt F-08 bis F-16 durch MDCG 2022-21. Aktuelle Übergangsregeln, Schnittstellenverfügbarkeit, DSGVO und die Qualifikation von Cohors nach dem AI Act bleiben eigenständige Prüfgegenstände des Architekturkonzepts. Die ursprünglichen Muss-/Soll-Einstufungen werden übernommen, nicht als neue Rechtsauslegung ausgegeben.

| Modul | Anforderungen | Muss | Soll |
| --- | --- | --- | --- |
| A Governance & QMS | 10 | 10 | 0 |
| B Datenerfassung | 12 | 12 | 0 |
| C Vigilanz-Fallmanagement | 15 | 15 | 0 |
| D Trend & Signal | 7 | 6 | 1 |
| E Untersuchung/CAPA/FSCA | 12 | 12 | 0 |
| F PSUR | 16 | 6 | 10 |
| G PMCF | 13 | 12 | 1 |
| H SSCP & Produktinformation | 5 | 5 | 0 |
| I Risikomanagement & TD | 8 | 8 | 0 |
| J UDI & Rückverfolgbarkeit | 6 | 5 | 1 |
| K Schnittstellen | 7 | 7 | 0 |
| L Datenhaltung & Audit | 7 | 6 | 1 |

## Leseschlüssel

**P** bedeutet eine Funktion innerhalb des genehmigten synthetischen Piloten, nicht „bereits umgesetzt“. **S** bedeutet ausschließlich simulierte externe Kommunikation. **D** bedeutet versionierte Evidenz, Aufgaben und kontrollierte Änderungsaufträge anstelle eines vollständigen Fachsystems. **Z** bezeichnet eine bedingte oder nachgelagerte Zielbetriebsfunktion außerhalb des genehmigten Piloten. Kombinationen machen beide Grenzen ausdrücklich sichtbar.

Die Architekturbezüge verweisen auf **Cohors – Architekturdesign und Implementierungskonzept, Version 0.3**. P00–P14 sind dessen Prozessschritte. T01–T20 sind zugeordnete Abnahmeszenarien, keine pauschalen Bestehensvermerke. Wo diese Szenarien nicht ausreichen, ist ein matrixspezifischer Nachweis genannt.

Die folgenden Nachweiskürzel bezeichnen nur bereits beobachtete **Teilnachweise**. Grundlage sind der frühere geprüfte Zwischenstand und der inzwischen eingesehene Lauf mit 47 erfolgreichen Backendtests, beendet am 15.11.2026 um 15:22 Uhr MEZ, sowie drei frühere Browserfälle. Der TRX-Nachweis hat SHA-256 `51ffd9beed7d27548bf7c6928c5f882ae7f06fa03b7ae0f291eaa1aa870766e8`. Tatsächliche Hostausführung, Infrastruktur und ein späterer Integrationsstand sind damit nicht automatisch geprüft. Die Nachweise müssen gegen den abschließenden integrierten Stand wiederholt werden.

| Kürzel | Vorliegender Teilnachweis | Ausdrückliche Grenze |
| --- | --- | --- |
| N-INTAKE | Synthetischer Intake, Duplikatbeziehung und sichtbare UDI-Lücke; SQL-/Browserfälle T01/T02 | Keine produktiven CRM-, Klinik- oder Registerimporte |
| N-FREIGABE | Versionskonflikte, zwei unterschiedliche menschliche Identitäten und Invalidierung geänderter Pakete; CaseSafetyTests und Browserfall | Keine Qualifikationsprüfung der Personen oder formale Organisationsfreigabe |
| N-EVIDENZ | Leere tragende Aussagen und ungültige Quellenbezüge werden abgewiesen; historische Evidenzversion bleibt erhalten, explizites Relinking invalidiert die Versandberechtigung; AssuranceTests und QualityAndEvidenceTests | Kein Nachweis klinischer Wahrheit, vollständiger Dokumentvorlagen oder produktiver Search-Integration |
| N-FRIST | Unabhängige Kalender-/DST-Referenzen, getrennte Awareness-Behandlung und Berlin-/Dublin-Anzeige; IndependentReferencesTests, CaseSafetyTests und Browserfall | Vollständige nationale Kalender, laufende Cloud-Hosts und externe Alarmzustellung nicht abgenommen |
| N-SENDUNG | Unknown-Sperre auch bei neuem Paket-Hash, Reconcile und explizite Verpflichtungsbindung; CaseSafetyTests und Browserfall | Simulierte Übermittlung; nicht alle Meldearten und kein realer Behördenkanal abgenommen |
| N-RATE | Unabhängige Raten-/Statistikreferenzen und Nennerprüfungen; IndependentReferencesTests | Keine freigegebene medizinische Trendmethodik oder komplette PSUR-Integration |
| N-CAPA | Unabhängige, berechtigte Wirksamkeitsbewertung; fehlende Evidenz, Selbstprüfung und negative Wirksamkeit verhindern einen positiven Abschluss; QualityAndEvidenceTests | Backend-Regel, nicht reale klinische Wirksamkeit oder vollständige UI-/FSCA-Abnahme |
| N-AUDIT | Manipulation historischer und aktueller Aggregate wird erkannt; SQL-Manifestprüfung | Extern geschützte Ledger-Digests, storeübergreifende Retention und Recovery noch offen |
| N-SCOPE | Lokale Rollen-/Produktscope- und Nicht-Mensch-Freigabeprüfungen; Auth-/CSRF-Browserfall | Kein Nachweis live eingerichteter Entra-/Azure-RBAC-Grenzen |
| N-QUELLEN | Gemeldeter API-/SQL-Smoke-Test mit je fünf FDA-/PubMed-Metadaten und unveränderter Herstellerfallzahl; zusätzlich acht eingesehene erfolgreiche Real-Reader/Fake-HTTP-/SQL-Fälle: fehlerhaftes Schema oder leerer Pflichttitel persistiert Failed/source_contract, gleicher Schlüssel liest nicht erneut | Metadaten und konkret geprüfte Fehlformen; keine Volltext-/Registerabnahme oder vollständige Quellenabdeckung |
| N-PSUR | Eingesehene erfolgreiche AnalyticsTests: eindeutige Fallversionen, passender Nenner, eingefrorene offene Fälle, getrennte versionsgebundene Freigaben, Revisionsinvalidierung und simulierte BS-Quittung; Validator erkennt falsche Zahlen/Referenzen als kritische Befunde; Regionen-/Periodenreferenz geprüft | Noch kein Nachweis sämtlicher F-01–F-16-Felder, Stichtagsrekonstruktion, Gruppierungsregeln, PDF-Export oder vollständiger UI |
| N-BETRIEB | Eingesehene erfolgreiche OutboxTests zu parallelen Claims, abgelaufenen Leases, Backoff/Poison und auditierter QM-Wiederaufnahme; FoundryBoundaryTests weisen pauschale EU-/DataZone-Labels, ungeeignete SKUs, EFTA-Länder und veraltete Konfigurationsbindung ab | Keine echte Functions-Neustart-/Linux-Abnahme, externe Alarmzustellung oder Bestätigung tatsächlicher Azure-Verarbeitungsländer |

## Fachliche Präzisierungen für die Umsetzung

| Kennung | Betroffene Anforderungen | Verbindliche Einordnung innerhalb des genehmigten Piloten |
| --- | --- | --- |
| M01 | C-06 bis C-08, C-13, C-14, L-06 | Die Höchstfristen beziehen sich auf die maßgebliche Kenntnis. Ein späterer KI-/Kausalitätsbefund und eine bloße Reklassifizierung starten keine neue Frist. Eine begründete neue Reportability-Awareness nach zuvor gerechtfertigter Nichtmeldepflicht braucht tatsächlich neue Information. Die bereits spezifizierte Regel aus Architektur §9 bleibt maßgeblich. |
| M02 | C-03, D-01 bis D-04 | „Erwartete Nebenwirkung“ allein belegt keine Ausnahme: Dokumentation in Produktinformationen, Quantifizierung in der TD und Einbeziehung in die Trendmeldung müssen zusammen nachvollziehbar sein. Kein automatisches Nichtmelden durch ein einzelnes Expectedness-Flag oder eine KI-Vermutung. |
| M03 | C-12, K-01 | Periodische Sammelmeldungen sind nicht PSUR. Vor einer simulierten Nutzung müssen die einschlägigen Voraussetzungen und eine dokumentierte, passende Behördenvereinbarung zu Form, Inhalt und Häufigkeit vorliegen. Gruppieren allein genügt nicht. |
| M04 | E-02 | Vor einer die Ursachenbewertung beeinträchtigenden Prüfung sind Asservat/Zustand und die vorherige Behördenunterrichtung nachzuweisen. Die interne menschliche Prüffreigabe ist von einer gesetzlich nicht pauschal vorauszusetzenden behördlichen Genehmigung zu unterscheiden. Im Pilot nur ein synthetischer Vorgang, keine reale Materialprüfung. |
| M05 | F-11, L-01 | Ende der PSUR-Erstellung, Ende anderer PMS-Aufgaben und Datenlöschung sind getrennte Entscheidungen. Die in F-11 selbst gekennzeichnete Leitlinien-/Mindestfristfrage wird nicht als universeller 15-Jahre-Stop oder Löschautomat implementiert. |
| M06 | A-05, A-06, L-02 | PRRC-Verantwortung und Produkt-/Vertretungszuständigkeit müssen organisatorisch erkennbar sein. Eine technische RA-Berechtigung allein belegt keine PRRC-Bestellung. Die genehmigten zwei menschlichen Freigaben bleiben bestehen; keine dritte Pflichtsignatur und keine automatische PRRC-Übersteuerung werden daraus abgeleitet. |
| M07 | C-05, F-06, F-16, H-02, K-01 bis K-03, K-07 | Die Matrix beschreibt Zielpflichten, nicht die heutige Verfügbarkeit sämtlicher EUDAMED-Module. Reale Formatvalidierung und Export sind von simuliertem Versand und späterem autorisiertem Produktivzugang zu trennen. Keine erfundene API oder Behördenportal-RPA. |
| M08 | F-08 bis F-16 | Diese neun Ergänzungen sind Soll-Anforderungen der Quelle. Sie konkretisieren den bereits vereinbarten PSUR-Piloten: stabile Kennung/Leitprodukt, zusammenhängende Perioden, N/N2/N3/N4, regions- und nennerkonsistente Daten, versionierte Terminologie sowie offene Maßnahmen. Die Soll-Kennzeichnung macht zugesagte Nachvollziehbarkeit oder unveränderliche Zeitstempel nicht optional. |
| M09 | G-01 bis G-13, H-01 bis H-05, I-01 bis I-08, J-03, K-07, L-05 | Kein stiller Ausbau zu Studien-, Patienten-, QMS-/PLM- oder Publikationssystemen. PMCF/SSCP/TD bleiben Evidenz und Change Requests. Datenschutz und KI-Governance gehen über die MDR-Matrix hinaus; synthetische Falldaten und das separate EU-Verarbeitungsgate bleiben bestehen. |

## A – Governance & QMS

| ID | Priorität | Pilotzuschnitt und Ergebnis | Architektur / Prozess | Prüfansatz / Teilnachweis | Rest / Grenze |
| --- | --- | --- | --- | --- | --- |
| A-01 | Muss | P: versionierte Produkt- und PMS-Systemakte für beide fiktiven Familien | §3.1, §5.1; P00 | T02/T07/T08; N-INTAKE, N-EVIDENZ | Vollständige Produktattribute und PMS-Plan-Zuordnung gezielt abnehmen; keine reale Herstellerakte |
| A-02 | Muss | D: versionierte QMS-/SOP-Referenzen und Verknüpfung zu Change/CAPA | §5.1, §7, §14.6; P00/P12 | T07/T17; N-EVIDENZ, N-CAPA | Prozessreferenzen und Änderungsnachverfolgung prüfen; Unternehmens-QMS und SOP-Freigabe bleiben extern |
| A-03 | Muss | P/S: Meldung, CAPA, Kennzahlen und protokollierte Kommunikation | §7, §11, §12; P09–P14 | T13/T15/T17/T19; N-SENDUNG, N-RATE, N-CAPA | Durchgängige Kommunikations-/Maßnahmenintegration und Organisationsverfahren offen |
| A-04 | Muss | P/S: Fallregister mit getrennten Einzel-, Sammel- und Trendmeldungen | §5.1, §8, §12; P03/P10/P13 | T13/T14/T19; N-SENDUNG | Erstmeldung allein belegt nicht alle Meldearten; Sammelvereinbarung nach M03 nachweisen |
| A-05 | Muss | P: PRRC-Verantwortung sichtbar; zwei getrennte medizinische/RA-Freigaben | §7.3, §8, §14.1; P00/P08/P09 | T06/T07/T08; N-FREIGABE, N-SCOPE | PRRC-Zuordnung und Eskalationsadressierung gezielt prüfen; Bestellung/Qualifikation Organisationsgate, M06 |
| A-06 | Muss | P/D: schriftliche Zuständigkeitsreferenz je Produkt und Vertretung | §5.1, §7.3; P00 | T08 plus matrixspezifische Zuständigkeitsprüfung; N-SCOPE nur teilweise | Produktberechtigung ist keine Aufgabenverteilung mehrerer PRRC; M06 |
| A-07 | Muss | P/S: Rückruf, Rücknahme und sonstige Maßnahmen mit Empfängern | §5.1, §7.3, §12; P12 | T07/T17/T19; N-FREIGABE, N-CAPA | Maßnahmenarten, Empfänger und simulierte Benachrichtigungen einzeln nachweisen; kein realer Rückruf |
| A-08 | Muss | P/S: Behördenanfrage mit Frist, Antwort und Dossierpaket | §5.1, §7, §12; P07/P11 | T10/T14/T19; N-FRIST nur Fristenkern | Anfrage-/Antwortkette und Paketvollständigkeit offen; keine tatsächliche Behördenkooperation behaupten |
| A-09 | Muss | D: PMS-bedingter TD-Änderungsauftrag mit Ursprung und Freigabe | §5.1, §8, §11.1; P12 | T07; N-EVIDENZ | Change Request bis zum Umsetzungsbeleg nachweisen; kein direktes Überschreiben fremder TD |
| A-10 | Muss | P/D: klar gegliederte, versionierte und auffindbare PMS-Dokumente | §5.3, §13, §14.2 | T07/T08/T20; N-EVIDENZ | Volltextsuche, Berechtigungsfilter und Search-Wiederaufbau integriert noch nicht abgenommen |

## B – Datenerfassung

| ID | Priorität | Pilotzuschnitt und Ergebnis | Architektur / Prozess | Prüfansatz / Teilnachweis | Rest / Grenze |
| --- | --- | --- | --- | --- | --- |
| B-01 | Muss | P: zentraler Quellen-/Falldatenbestand mit Provenienz und Zeitachse | §5, §7; P01–P04 | T01/T02/T04; N-INTAKE, N-EVIDENZ | Geplante Wiederholung, Cursor und Lifecycle-Bezug integriert prüfen; keine lebenslange Vollständigkeitsbehauptung |
| B-02 | Muss | P/S: Vorkommnisse und FSCA als nachvollziehbare PSUR-Quellen | §5.1, §11.3; P12/P14 | T16/T19; N-SENDUNG nur Teilpfad | Durchgängigen Einbezug in den PSUR und Quellenherkunft nachweisen |
| B-03 | Muss | P: nicht schwerwiegende Vorkommnisse und Nebenwirkungen separat klassifizieren | §5.1, §7.1, §11.2; P03/P13 | T04/T15; N-EVIDENZ, N-RATE nur Teilregeln | Nichtmelden begründen, Trends weiterführen; Ausnahmebedingungen nach M02 |
| B-04 | Muss | P/S: Trendentscheidungen und Trendmeldungen rückführen | §11.2–11.3; P13/P14 | T15/T16/T19; N-RATE | Verknüpfte Trendmeldung und PSUR-Übernahme noch gezielt abnehmen |
| B-05 | Muss | P/S: PubMed-Metadaten real, Registerdaten synthetisch; menschliche Relevanzbewertung | §5.3, §12; P01/P04 | T04/T08/T09/T16; N-QUELLEN, N-EVIDENZ | Quellenfehler, Literaturprotokoll, Nutzungsrechte und Registerattribute prüfen; kein Volltextrecht ableiten |
| B-06 | Muss | P/S: synthetische Beschwerde-/Rückmeldekanäle mit Herkunft | §3.1, §12; P01/P02 | T01/T02/T08; N-INTAKE, N-SCOPE | Dateiimport/Kanalherkunft integriert prüfen; keine produktiven E-Mail-, CRM- oder Händlerzugänge |
| B-07 | Muss | P: öffentliche Vergleichsmetadaten getrennt von Herstellerfällen | §11.1, §12; P01/P04 | T01/T04/T15; N-QUELLEN | Vergleichbarkeit und Auswahl begründen; öffentliche Rohzahlen sind weder eigene Fälle noch Inzidenzen |
| B-08 | Muss | P: produktbezogene Raten und qualifizierter Vergleich | §5.1, §11.1–11.2; P13 | T15; N-RATE | Gleiche Ereignisdefinition, Exposition und Vergleichsgruppe prüfen; keine unbelegten Benchmarks |
| B-09 | Muss | P: Kodierung, Priorisierung, Assessment, Assurance und Human Review | §6–§10; P03–P09 | T03/T04/T06/T07; N-EVIDENZ, N-FREIGABE | Klinisch-technische Qualitätsbewertung und vollständige Kodierlisten nicht durch Schema-Tests ersetzt |
| B-10 | Muss | P/D: Untersuchungsaufgaben und technische Evidenz nach Land/Charge | §5.1, §10, §11.1; P04/P12 | T04/T17; N-EVIDENZ, N-CAPA | Rückläufer-/Untersuchungsworkflow und Stratifizierung gezielt prüfen; nur synthetische Befunde |
| B-11 | Muss | P/S: Behördenweiterleitung als Ursprung mit korrekter Kenntnis | §7.1, §9.2, §12; P01/P03/P11 | T01/T10/T11; N-FRIST | Weiterleitung, Begründung und neue Information als zusammenhängenden Fall prüfen; kein pauschaler Fristreset |
| B-12 | Muss | P/D: Anwendungsfehler/Informationsmangel mit IFU-/Usability-Rückkopplung | §5.1, §10, §11.1; P03/P12 | T04/T07; N-EVIDENZ | Klassifikationswerte und zugehörigen Änderungsauftrag gezielt nachweisen |

## C – Vigilanz-Fallmanagement

| ID | Priorität | Pilotzuschnitt und Ergebnis | Architektur / Prozess | Prüfansatz / Teilnachweis | Rest / Grenze |
| --- | --- | --- | --- | --- | --- |
| C-01 | Muss | P: kontrollierte Vorkommnis-, Problem- und Folgenklassifikation | §5.1, §10.1; P03 | T02/T04; N-INTAKE, N-EVIDENZ | Wertelisten/Terminologierelease und fehlende Pflichtdaten gezielt prüfen |
| C-02 | Muss | P: begründete menschliche Seriousness-Entscheidung mit Fristfolge | §7.1, §9, §10; P03/P08 | T04/T10; N-EVIDENZ, N-FRIST | Beinaheereignisse und potenzielle Folgen fachlich abnehmen; keine autonome Schlussentscheidung |
| C-03 | Muss | P/D: eng begründete Ausnahme für erwartete Nebenwirkungen | §7.1, §10, §11.2; P03/P08/P13 | T04/T15 plus Ausnahmeprüfung nach M02 | Noch kein gezielter Nachweis aller drei Voraussetzungen; Expectedness allein darf nicht genügen |
| C-04 | Muss | P/S: FSCA mit Drittland-/EU-Marktbezug | §5.1, §7.3, §12; P12 | T07/T19 plus matrixspezifischer Drittlandfall | Länder-/Produktbetroffenheit und simulierte Pflichtentstehung nachweisen |
| C-05 | Muss | P/S: validierbares Meldungspaket und simulierte elektronische Einreichung | §12.1–12.2; P09–P11 | T13/T14/T19; N-SENDUNG | MIR-Gesamtabnahme und weitere Formate offen; Produktivzugang nach M07 separat |
| C-06 | Muss | P: 15-Tage-Höchstfrist plus unverzügliche Bearbeitung | §9; P03/P09 | T10/T11/T12; N-FRIST | Kalenderfreigabe, tatsächlicher Watchdog und Eskalation noch abnehmen; M01 |
| C-07 | Muss | P: 2-Tage-Höchstfrist, priorisierte Bearbeitung und Eskalation | §7.2, §9; P03/P09 | T10/T12; N-FRIST | Bereitschafts-/Vertretungsweg und Alarmzustellung fehlen als Gesamtnachweis; M01 |
| C-08 | Muss | P: 10-Tage-Höchstfrist bei zutreffender Klassifikation | §9; P03/P09 | T10/T11/T12; N-FRIST | Fachliche Einordnung und vollständige Alarmkette zusätzlich abnehmen; M01 |
| C-09 | Muss | P/S: getrennte Initial-, Follow-up- und Abschlussversionen | §5.1, §7.1, §8; P09–P11 | T05/T07/T13/T14; N-FREIGABE, N-SENDUNG | Vorläufigkeitsweg, offene Follow-ups und XSD je Meldeart integriert prüfen |
| C-10 | Muss | P/S: Unsicherheit sichtbar, fristgerechter menschlicher Vorläufigkeitsweg | §7.2, §9, §10; P06/P08/P09 | T03/T04/T05/T10; N-EVIDENZ, N-FRIST | Direkter Weg ohne verfügbare KI muss aktuell integriert vorführbar sein |
| C-11 | Muss | P/S: FSCA-Melde-Gate vor Durchführung, begründete Dringlichkeit | §7.3, §8, §12; P12 | T07/T19 plus matrixspezifischer FSCA-Gate-Test | Reihenfolge und Ausnahme fehlen als gezielter Nachweis; keine reale Durchführung |
| C-12 | Muss | P/S: eigenständige Sammelmeldung mit passender Behördenvereinbarung | §5.1, §12; P09/P10 | T07/T19 plus Vereinbarungsprüfung nach M03 | Nicht durch Einzelmeldung oder PSUR abgedeckt; Form/Inhalt/Häufigkeit und Gültigkeit testen |
| C-13 | Muss | P/S: Nichtmeldebegründung, Behördenreaktion und kontrollierte Neubewertung | §7.1, §9.2; P08/P11 | T11; N-FRIST | Behördenkorrespondenz ergänzen; bloße Reklassifizierung startet keine neue Frist, M01 |
| C-14 | Muss | P: Kenntnis, Kausalitätsbewertung und neue Information getrennt erfassen | §9.2, §10.1; P03/P05/P08 | T10/T11; N-FRIST | Quellenformulierung nicht als Freigabe eines verzögerten Fristbeginns verwenden, M01 |
| C-15 | Muss | P/S: Ereignisland und korrekter behördlicher Empfängerscope | §5.1, §9.1, §12; P09/P10 | T07/T10/T13/T19; N-SENDUNG | Empfängermatrix je Land abnehmen; EU, GB und NI getrennt, kein stiller Default |

## D – Trend & Signal

| ID | Priorität | Pilotzuschnitt und Ergebnis | Architektur / Prozess | Prüfansatz / Teilnachweis | Rest / Grenze |
| --- | --- | --- | --- | --- | --- |
| D-01 | Muss | P/S: statistischer Kandidat, menschliche Relevanz-/Meldeentscheidung, Trendpaket | §11.2, §12; P13 | T15/T16/T19; N-RATE | Signifikanz ist nicht allein Meldepflicht; Workflow/Export und fachliche Methodik offen |
| D-02 | Muss | P/D: freigegebene Referenzraten mit TD-/IFU-Version | §5.1, §11.2; P00/P13 | T07/T15; N-RATE, N-EVIDENZ | Provenienz und Gültigkeit der konkreten Baseline im Trendlauf abnehmen |
| D-03 | Muss | P/D: versionierte Methodik, Fenster und Schwellen im PMS-Plan | §11.2, §16.4; P00/P13 | T07/T15; N-RATE | Keine frei erfundene universelle Signifikanzgrenze; Planfreigabe und Export nachweisen |
| D-04 | Muss | P: Indikatoren, Review-Alarm und RM-Änderungsauftrag | §11.2, §7.3; P13/P12 | T15/T17; N-RATE, N-CAPA nur Teilpfade | Auslösung bis Maßnahmenreview integriert prüfen; keine automatische Risikoakzeptanz |
| D-05 | Muss | P/S: Behördenantwort zu einem Trend mit Maßnahmenverfolgung | §7, §12; P11/P12/P13 | T17/T19; N-CAPA nur Teilregel | Simulierte Trendkorrespondenz und verknüpfte Maßnahmen gezielt nachweisen |
| D-06 | Muss | P/S: externes Sicherheitssignal löst verpflichtenden Maßnahmenreview aus | §5.1, §7; P01/P03/P12 | T01/T04/T17/T19; N-INTAKE, N-CAPA | CAPA-Anstoß und menschliche Entscheidung prüfen; kein autonomer realer Maßnahmenvollzug |
| D-07 | Soll | P: vergleichbare Expositionsraten mit dokumentiertem Nenner | §11.2–11.3; P13/P14 | T15/T16; N-RATE | Fehlende, unpassende oder nicht freigegebene Nenner sperren Raten; PSUR-Integration offen |

## E – Untersuchung, CAPA und FSCA

| ID | Priorität | Pilotzuschnitt und Ergebnis | Architektur / Prozess | Prüfansatz / Teilnachweis | Rest / Grenze |
| --- | --- | --- | --- | --- | --- |
| E-01 | Muss | P/D: fallbezogene Untersuchung und begründete Risikobewertung | §10, §11.1; P04–P08/P12 | T03/T04/T17; N-EVIDENZ, N-CAPA | Untersuchung läuft parallel zur fristgerechten Meldung; keine klinische Validierung durch Demo |
| E-02 | Muss | P/S: Asservatenbezug, vorherige Behördenunterrichtung und interne Prüffreigabe | §5.1, §7.3; P12 | Matrixspezifischer Beweissicherungs-/Gate-Test; M04 | Noch kein gezielter Nachweis; keine reale destruktive Prüfung oder erfundene Behördenzustimmung |
| E-03 | Muss | P/S: Abschlussbericht mit Untersuchung, Schlussfolgerung und Maßnahmen | §7, §10, §12.2; P11/P12 | T04/T07/T14/T19; N-EVIDENZ, N-FREIGABE | Finales Mapping und Vollständigkeit gegen richtige Schemaart prüfen |
| E-04 | Muss | P/S: Behördeninteraktionen und Untersuchungsauflagen protokollieren | §5.1, §7; P11/P12 | T07/T18/T19; N-AUDIT nur Grundmechanismus | Auflagen, Verantwortliche und Nachweis der Bearbeitung gezielt abnehmen |
| E-05 | Muss | P/S: CAPA-Plan, Termine und unabhängige Wirksamkeitsentscheidung | §5.1, §7.3; P12 | T17; N-CAPA | UI, Benachrichtigungen und verknüpfte Fall-/PSUR-Sicht noch integrieren |
| E-06 | Muss | P: synthetische Betroffenheitsliste über Produkt/Charge/Markt | §5.1, §11.1; P12 | T02/T08 plus matrixspezifische Betroffenheitsprüfung | Keine Betroffenheit allein aus Textähnlichkeit; Distributionsimport und Umfang prüfen |
| E-07 | Muss | P/S: versionierter FSN-Entwurf, Behördenreview und Sprach-/Empfängerscope | §7.3, §8.2, §12; P09/P12 | T07/T19; N-FREIGABE nur allgemeiner Schutz | Konkreten FSN-Review, Dringlichkeitsausnahme und simulierte Zustellung nachweisen |
| E-08 | Muss | P: FSN-Pflichtfelder mit klarer Risiko- und Anwendermaßnahmenbeschreibung | §5.1, §10, §12; P09/P12 | Matrixspezifischer FSN-Pflichtfeldtest; N-EVIDENZ nur Claim-Schutz | UDI/SRN, Fehlfunktion, Risiken und Maßnahmen gezielt prüfen; fehlende Fakten nicht erfinden |
| E-09 | Muss | S: FSN-Einstellung mit eindeutig simuliertem Übermittlungsbeleg | §12.1; P10/P11 | T13/T19; N-SENDUNG nur Grundvertrag | FSN-spezifischen Beleg nachweisen; keine öffentliche Veröffentlichung, M07 |
| E-10 | Muss | P/S: koordinierter Vorgang mit Länderfällen und koordinierender Behörde | §5.1–5.2, §12; P09/P11/P12 | T07/T13/T19; N-SENDUNG nur Verpflichtungsbindung | Mehrländerfall und Korrespondenz prüfen; Pflichten einzelner Länder nicht zusammenfallen lassen |
| E-11 | Muss | P/S: nachvollziehbares Untersuchungs-/Risikodossier | §5.3, §10, §14.2; P11/P12 | T04/T07/T18/T19; N-EVIDENZ, N-AUDIT | Exportvollständigkeit und Berechtigungen des konkreten Pakets nachweisen |
| E-12 | Muss | P/S: Rückruf, Rücknahme und sonstige FSCA mit Umfang und Status | §5.1, §7.3; P12 | T07/T17/T19; N-CAPA nur Teilregel | Eigenständige Maßnahmentypen und Statusfolge prüfen; kein realer Rückruf |

## F – PSUR

| ID | Priorität | Pilotzuschnitt und Ergebnis | Architektur / Prozess | Prüfansatz / Teilnachweis | Rest / Grenze |
| --- | --- | --- | --- | --- | --- |
| F-01 | Muss | P: eingefrorener produkt-/gruppenbezogener PSUR-Entwurf | §5.1, §11.3; P14 | T16; N-PSUR, N-EVIDENZ | Vollständigen Snapshotinhalt aus Fällen, Trends, CAPA, PMCF und Literatur noch abnehmen |
| F-02 | Muss | P/D: begründete Nutzen-Risiko-Schlussfolgerung mit aktuellem Evidenzscope | §10, §11.3; P14 | T04/T16; N-EVIDENZ | Fachliche Schlussfolgerungen menschlich bestätigen; Risikoakzeptanz nicht aus Modelltext ableiten |
| F-03 | Muss | P/D: richtige PMCF-Berichtsversion und Hauptergebnisse im PSUR | §5.3, §11.3; P14 | T07/T16; N-EVIDENZ | Fachliche Gültigkeit und konkrete Ergebnisübernahme prüfen; kein PMCF-Fachsystem |
| F-04 | Muss | P/S: synthetische Absatz-/Expositionsdaten samt Population und Einheit | §5.1, §11.2–11.3; P14 | T15/T16; N-RATE | Absatz, Implantate und Personen nicht gleichsetzen; vollständige PSUR-Ausweisung prüfen |
| F-05 | Muss | P/D: Jahreszyklus, Fälligkeit, Berichtsversion und TD-Bezug | §9.1, §11.3; P14 | T10/T12/T16; N-FRIST nur Rechenkern | Jährlichen PSUR-Zyklus und Wiederaufnahme gezielt abnehmen |
| F-06 | Muss | P/S: getrennte Einreichung, BS-Rückfrage und BS-Bewertung | §11.3, §12; P14/P11 | T07/T13/T16/T19; N-PSUR, N-SENDUNG | Grundquittung geprüft; BS-Rückfrage-/Maßnahmenzyklus und UI noch offen, keine echte BS-Akzeptanz, M07 |
| F-07 | Soll | P: jede PSUR-Aussage auf Fall-/Daten-/Evidenzversion zurückführen | §5.3, §11.3; P14 | T04/T16; N-PSUR, N-EVIDENZ | Zahlen-/Referenzvalidator geprüft; vollständige fachliche Claim-Provenienz weiter abnehmen |
| F-08 | Soll | P: stabile PSUR-Referenz, fortlaufende Version und Revisionshistorie | §5.1, §11.3; P14 | T07/T16 plus Kennungs-/Historienprüfung | Leitproduktbindung sowie Vorversionen und frühere BS-Maßnahmen gezielt prüfen |
| F-09 | Soll | P: begründete Gruppe mit gesperrtem Leitprodukt und passender BS | §5.1, §11.3; P14 | T16 plus Gruppierungs-/Leitproduktregression | Höchste Risikoklasse, gleiche BS, Periodeneindeutigkeit und Produktwechsel nachweisen |
| F-10 | Soll | P: lückenlose Berichtsperioden und gesonderter vereinbarter Einreichungstermin | §9.1, §11.3; P14 | T10/T16; N-FRIST nur Teilregel | Keine erfundene allgemeine Tagesfrist nach Periodenende; Zeitplan und Abweichungen prüfen |
| F-11 | Soll | P/D: kontrollierter Produktlebenszyklus und geprüfte Regel zum PSUR-Ende | §11.3, §14.3; P14 | T16/T18 plus Lifecycle-Prüfung; M05 | Kein automatischer 15-Jahre-Stop/Löschlauf; konkrete Regel mit RA/BS abstimmen |
| F-12 | Soll | P: Basic-UDI-, regions-, perioden- und nennerkonsistente Tabellen | §11.2–11.3; P14 | T15/T16; N-RATE, N-PSUR | N/N2/N3/N4 und nicht additive Welt-/Teilregion geprüft; alle Produkt-/Populationsmerkmale noch abnehmen |
| F-13 | Soll | P: versionierte IMDRF-Kodes, Ebenen, Todesfälle und historische Kennzeichnung | §10.1, §11.3; P14 | T14/T16 plus Terminologie-/Todesfallprüfung | Anhänge A/C/D/F, zulässige Ebenen und Legacy-Ausnahme explizit abnehmen |
| F-14 | Soll | P: sicherheitsrelevante und offene CAPA/FSCA mit Stichtagsstatus | §5.2, §11.3; P14 | T16/T17; N-CAPA | Stichtag statt heutiger Status, offene Altmaßnahmen und Art.-83(4)-Bezug nachweisen |
| F-15 | Soll | P/D: vollständiges Quellenverzeichnis einschließlich begründeter Ausschlüsse | §5.3, §11.3, §12; P14 | T04/T16; N-QUELLEN, N-EVIDENZ nur Teilpfade | Registername/-typ, Beschwerdeauswahl und nicht genutzte Datensätze im PSUR prüfen |
| F-16 | Soll | P/S: Bericht, Pflichtaussage und vollständiger simuliert einreichbarer Datensatz | §11.3, §12; P14 | T16/T19 plus Export-/Webformularprüfung | PDF/Metadaten, Zertifikate und Legacy-/Klassenfilter abnehmen; kein reales EUDAMED-Webformular, M07 |

## G – PMCF

| ID | Priorität | Pilotzuschnitt und Ergebnis | Architektur / Prozess | Prüfansatz / Teilnachweis | Rest / Grenze |
| --- | --- | --- | --- | --- | --- |
| G-01 | Muss | D: PMCF-Akte als versionierte Evidenz mit CER-/QMS-Bezug | §3.1, §5.1; P00/P04 | T07/T16; N-EVIDENZ | Kein eigenständiges klinisches Studien-/QMS-System, M09 |
| G-02 | Muss | D/S: synthetische klinische/Registry-Evidenz und reale zulässige Literaturmetadaten | §3.1, §12; P01/P04 | T04/T08/T16; N-EVIDENZ, N-QUELLEN | Keine realen Patientendaten, Registerberechtigung oder Lebenszeit-Wirksamkeitsbehauptung |
| G-03 | Muss | D: freigegebene PMCF-Planversion mit PMS-Plan-Verweis | §5.1–5.3; P00/P04 | T07; N-EVIDENZ | Pflichtabschnitte und Nichtanwendbarkeitsbegründung dokumentfachlich prüfen |
| G-04 | Muss | D: Ziele und neue Risiken im Plan-/Ergebnisdokument nachvollziehen | §10, §11.1; P04/P12 | T04/T16; N-EVIDENZ nur Quellenprüfung | Kein vollständiger PMCF-Ziel-/Studienanalysebau; fachlicher Zielkatalog bleibt kontrollierte Evidenz |
| G-05 | Muss | D: vollständige Planvorlage und referenzierte Aufgaben/Fälligkeiten | §5.1, §9.1; P00/P12 | T07/T10 plus dokumentfachliche Vollständigkeitsprüfung | Planabschnitte a–h und Zeitplan nachweisen; kein EDC/Studienmanagement |
| G-06 | Muss | D: versionierter PMCF-Bericht als CER-/TD-/PSUR-Evidenz | §3.1, §5.3, §11.3; P04/P14 | T07/T16; N-EVIDENZ | Vollständiger PMCF-Berichtsgenerator ausdrücklich außerhalb des Piloten |
| G-07 | Muss | D/P: PMCF-Befund erzeugt kontrollierten CER-/RM-Change oder CAPA | §5.1, §7; P12 | T07/T17; N-EVIDENZ, N-CAPA | Durchgängige Verknüpfung und Umsetzungsbeleg prüfen; keine automatische Dokumentfreigabe |
| G-08 | Muss | D/P: jährliche Review-Aufgabe für PMCF-Bericht und gegebenenfalls SSCP | §3.1, §9.1; P12/P14 | T10/T12 plus Jahreszyklusprüfung | Fachliche Jahresversion und tatsächliche Wiederholung noch nachweisen |
| G-09 | Muss | D: PMS-/PMCF-getriggerter Änderungsauftrag an die klinische Bewertung | §5.1, §11.1; P12 | T04/T07; N-EVIDENZ | CER-Fachsystem und klinische Freigabe bleiben extern |
| G-10 | Muss | Z: Anzeige und Management invasiver/belastender PMCF-Studien | §3.1, §12 | Zielbetriebsnachweis; kein Pilot-Abnahmetest als Erfüllung | Studienmanagement ausgeschlossen; Art.-74-Frist/Unterlagen und Art.-73-Zugang bei gesondertem Ausbau |
| G-11 | Muss | D/S: erforderliche PMCF-Studie und BS-Rückmeldung als Plan-/Auflagenreferenz | §3.1, §5.1, §12; P12 | T07/T19 plus Auflagenreferenzprüfung | Reale Studienplanung/-durchführung und BS-Akzeptanz bleiben Zielbetrieb |
| G-12 | Muss | D/S: Bescheinigungsauflagen mit Nachweisreferenz und Aufgabe | §5.1, §9.1, §12; P12 | T07/T10/T19; N-FRIST nur Grundfunktion | Vollständige Zertifizierungsverwaltung ausgeschlossen; konkrete Auflagenkette nachweisen |
| G-13 | Soll | D/S: synthetische Registerdaten mit Herkunft und Produktbezug | §5.3, §12; P01/P04 | T02/T04/T08; N-EVIDENZ nur Provenienzgrundlage | Kein produktiver Registerzugang oder automatischer Personenabgleich |

## H – SSCP & Produktinformation

| ID | Priorität | Pilotzuschnitt und Ergebnis | Architektur / Prozess | Prüfansatz / Teilnachweis | Rest / Grenze |
| --- | --- | --- | --- | --- | --- |
| H-01 | Muss | D: versioniertes SSCP-Dokument mit Sprache und Produktbezug | §3.1, §5.1; P04 | T07; N-EVIDENZ | Keine vollständige Redaktion oder öffentliche Veröffentlichung |
| H-02 | Muss | D/S: BS-Validierungsnachweis und öffentliche Fundstelle als getrennte Metadaten | §5.1, §12; P12 | T07/T19 plus Dokumentstatusprüfung | Keine echte BS-Validierung oder EUDAMED-Veröffentlichung, M07 |
| H-03 | Muss | D: SSCP-Pflichtabschnitte als kontrollierte Dokumentvorlage/Checkliste | §3.1, §5.1, §10 | Dokumentfachliche Prüfung; N-EVIDENZ nur allgemeine Belegprüfung | Vollständiger SSCP-Generator und laienverständliche Fachfreigabe nicht Teil des Piloten |
| H-04 | Muss | D/P/S: Änderungsauftrag, Jahresreview und simulierte Revalidierung | §5.1, §9.1; P12 | T07/T10/T19; N-EVIDENZ | Änderungs-/Review-Kette gezielt prüfen; keine eigenständige Publikationsplattform |
| H-05 | Muss | D: belegter IFU-/Labeling-/Design-Änderungsauftrag | §5.1, §11.1; P12 | T04/T07; N-EVIDENZ | Umsetzungsbeleg nachverfolgen; keine automatische Änderung von Design-/Freigabedokumenten |

## I – Risikomanagement & technische Dokumentation

| ID | Priorität | Pilotzuschnitt und Ergebnis | Architektur / Prozess | Prüfansatz / Teilnachweis | Rest / Grenze |
| --- | --- | --- | --- | --- | --- |
| I-01 | Muss | D/P: belegte Gefährdungszuordnung und menschlicher RM-Änderungsauftrag | §5.1, §10, §11.1; P04/P12 | T04/T07/T15; N-EVIDENZ, N-RATE | Kein vollständiger RM-Ersatz, keine ungeprüfte Risikoakzeptanz |
| I-02 | Muss | D/P: signal-/trend-/PSUR-getriggerter RM-Review mit Historie | §5.1, §7, §11; P12–P14 | T07/T16/T17; N-EVIDENZ, N-CAPA | Gesamten Review-/Änderungszyklus nachweisen; fachliche RM-Methodik extern freigeben |
| I-03 | Muss | D: versionsgebundener Daten-/Änderungsbezug zur klinischen Bewertung | §5.1, §11.1; P12 | T04/T07; N-EVIDENZ | CER-Aktualisierung und klinische Abnahme liegen beim Fachowner |
| I-04 | Muss | D/P: begründeter Verbesserungsvorschlag mit bewertetem Change Request | §10.1, §5.1; P05/P08/P12 | T04/T07; N-EVIDENZ | Verbesserungsvorschlag bis zur dispositionierten Maßnahme verfolgen |
| I-05 | Muss | P/D: produktübergreifende Hypothese mit Komponenten-/Materialbezug | §5.1, §11.1; P04/P13 | T02/T04/T08; N-EVIDENZ, N-SCOPE nur Teilregeln | Konkrete Familienkorrelation und Rechte nachweisen; Ähnlichkeit ist keine bestätigte Betroffenheit |
| I-06 | Muss | D: versionierter TD-Index mit PMS-/PSUR-/PMCF-Beziehungen | §5.1, §11.3; P00/P14 | T07/T16; N-EVIDENZ | Vollständigen Index und gültige Versionsreferenzen prüfen |
| I-07 | Muss | D/P/S: PMS-Prozessreferenzen und nachvollziehbare Kommunikationsprotokolle | §7, §12.1; P00/P07/P11 | T13/T18/T19; N-SENDUNG, N-AUDIT | Alle relevanten Korrespondenzarten integrieren; keine realen Aussendungen |
| I-08 | Muss | D: TD-Verknüpfung zu CER/Plan und PMCF/Plan/Bericht | §5.1–5.3; P00/P04 | T07; N-EVIDENZ | Vollständigkeit und fachliche Gültigkeit der konkreten Dokumentstruktur prüfen |

## J – UDI & Rückverfolgbarkeit

| ID | Priorität | Pilotzuschnitt und Ergebnis | Architektur / Prozess | Prüfansatz / Teilnachweis | Rest / Grenze |
| --- | --- | --- | --- | --- | --- |
| J-01 | Muss | P: synthetischer Basic-UDI-/UDI-DI-/UDI-PI-Stamm mit Referenzen | §5.1–5.2; P00/P02 | T02/T14/T16; N-INTAKE | Vollständige Stammdaten und konsistente Berichtsnutzung prüfen; keine reale Registrierung |
| J-02 | Soll | P/S: synthetische Distributions-/Implantationsdaten als Betroffenheits-/Expositionsquelle | §5.1, §11.2, §12; P02/P12/P14 | T02/T15/T16; N-RATE nur Rechenkern | Herkunft, Mehrfachimplantate und Importvalidierung nachweisen; keine Patientenregisteranbindung |
| J-03 | Muss | D/Z: versionierte Patienten-/Implantatinformation und Änderungsauftrag | §3.1, §5.1; P12 | T07; N-EVIDENZ nur Versionsgrundlage | Herstellerwebsite, echte Patientenausweise und Publikation bleiben außerhalb des Piloten |
| J-04 | Muss | P/D: begründetes Produktmerkmal zur Art.-18-Anwendbarkeit | §5.1; P00 | Matrixspezifische Stammdatenprüfung | Merkmal/Begründung noch gezielt nachweisen; keine pauschale Ausnahme nur wegen Bauteilnamen |
| J-05 | Muss | P/S: konsistente UDI-Bezüge in Meldungen und Berichtspaketen | §5.2, §12; P09/P14 | T02/T14/T16; N-INTAKE | MIR/FSN/PSUR/SSCP-Quervergleich offen; keine zugesagte produktive UDI-API |
| J-06 | Muss | P: synthetischer Herstellerstamm mit SRN, Sitzstaat und Rollenbezug | §5.1, §12; P00/P09 | T14/T16 plus Stammdatenprüfung | SRN und koordinierende Behörde konkret prüfen; fiktive Kennungen sichtbar kennzeichnen |

## K – Schnittstellen

| ID | Priorität | Pilotzuschnitt und Ergebnis | Architektur / Prozess | Prüfansatz / Teilnachweis | Rest / Grenze |
| --- | --- | --- | --- | --- | --- |
| K-01 | Muss | P/S: getrennte Meldearten, Exportverträge und Simulatorstatus | §5.1, §12; P09–P14 | T13/T14/T16/T19; N-SENDUNG | Einzelmeldung belegt nicht FSCA, Sammelmeldung, Trend, PSUR und FSN; M03/M07 |
| K-02 | Muss | P/S: Empfänger-/Jurisdiktionsmapping je Meldeart | §9.1, §12; P09/P10 | T07/T13/T19; N-SENDUNG | Weiterleitung des Behördensystems nicht als selbst implementierte Echtfunktion ausgeben |
| K-03 | Muss | P/S: produkt-/periodenbezogenes BS-/Auditpaket mit Zugriffsscope | §12, §14.1–14.2; P11/P14 | T08/T16/T18/T19; N-SCOPE, N-AUDIT | Paketinhalt und berechtigtes Retrieval prüfen; kein realer BS-Zugang |
| K-04 | Muss | P/S: fallbezogene Korrespondenz mit Empfänger, Vorlage, Frist und Status | §5.1, §7, §12; P07/P11/P12 | T10/T13/T19; N-FRIST, N-SENDUNG nur Grundfunktionen | RFI-, Behörden-, BS- und Maßnahmenkorrespondenz vollständig integrieren |
| K-05 | Muss | P/S: synthetische Wirtschaftsakteur-Verteiler und Benachrichtigungsbelege | §5.1, §12; P12 | T07/T19 | Noch kein gezielter Verteiler-/Zustellnachweis; keine realen Händler/Importeure kontaktieren |
| K-06 | Muss | P/S: validierter Import synthetischer Absatz-/Lieferdaten | §5.1, §11.2, §12; P02/P12/P14 | T02/T15/T16; N-RATE nur Folgefunktion | Import-, Chargen-, Länder- und Expositionskonsistenz eigens nachweisen |
| K-07 | Muss | Z: Art.-73-/Art.-74-Studienanzeige als späterer Schnittstellenvertrag | §3.1, §12 | Zielbetriebsnachweis; keine Pilot-Erfüllungsbehauptung | Kein klinisches Prüfungs-/EDC-System; gesonderte Studien- und Zugangsgenehmigung erforderlich |

## L – Datenhaltung & Audit

| ID | Priorität | Pilotzuschnitt und Ergebnis | Architektur / Prozess | Prüfansatz / Teilnachweis | Rest / Grenze |
| --- | --- | --- | --- | --- | --- |
| L-01 | Muss | P: objektbezogene Retention, Legal Hold und Lesbarkeits-/Recovery-Nachweise | §14.3, §15; P00 | T18/T20; N-AUDIT nur Integrität | Mindestens 15 Jahre ab letztem Inverkehrbringen für einschlägige Unterlagen; kein pauschales Rohdaten-WORM, M05 |
| L-02 | Muss | Z/D: bedingter Bevollmächtigtenzugang über freigegebene Dokumente/Exportscope | §14.1, §12 | T08 bei Anwendbarkeit; N-SCOPE nur allgemeine Rollenprüfung | Fiktiver Hersteller sitzt in Irland; Nicht-EU-Herstellerfall und konkrete Bevollmächtigtenrechte separat |
| L-03 | Muss | P: nachvollziehbarer Audit-Trail und produktbezogenes Auditpaket | §14.2, §15, §17; P11/P14 | T18/T20; N-AUDIT | Keine tatsächliche BS-Prüfung; Retention, Recovery und externe Digestprüfung offen |
| L-04 | Muss | D/P: versioniertes QMS-Antragspaket aus PMS-Beschreibung und Verfahren | §5.1, §14.6, §16 | T07/T18; N-EVIDENZ, N-AUDIT nur Bausteine | Paketvollständigkeit prüfen; realer QMS-Antrag und Zertifizierung außerhalb des Piloten |
| L-05 | Muss | P: Rollen/Produktscope, Verschlüsselung, minimierte Daten und getrennte Datenklassen | §14.1–14.5, §15 | T08/T09/T18/T20; N-SCOPE, N-BETRIEB | Konfigurationsgate geprüft, tatsächlicher EU-Review und Entra-/Azure-/Privacy-/Retention-Abnahme offen |
| L-06 | Soll | P: unveränderbare Ereigniszeiten und auditierte Korrekturen statt Überschreiben | §9.2, §14.2 | T10/T11/T18; N-FRIST, N-AUDIT | Eingabe des tatsächlichen Kenntnisdatums mit Provenienz zulässig; keine heimliche Änderung/Neustartregel, M01 |
| L-07 | Muss | P/D/S: Sprach-/Länderversionen im freigegebenen Paketscope | §8.2, §12, §13 | T07/T14/T19; N-FREIGABE nur allgemeine Bindung | Sprach-/Templatewechsel gezielt prüfen; keine validierte Vollübersetzung oder vollständige nationale Sprachabnahme |

## Umsetzung und Abschlusskriterien

Cohors-Pilot bleibt für Anwendung, Quelldateiübernahme, Tests und anwendungsnahe Dokumentation verantwortlich. Cohors Azure-Infrastruktur bleibt ausschließlich im vereinbarten Infrastruktur-/CI-Bereich; besonders L-01, L-03 und L-05 betreffen gemeinsam Code und Azure-Konfiguration. Die Matrix ändert weder diese Dateizuständigkeiten noch Option 2 mit separater Agent Runtime und Flex-Codepaketen.

Die Original-Exceldatei ist unverändert als Quellnachweis zu übernehmen; die Zuordnung wird daneben gepflegt. Ein Statuswechsel einer einzelnen Anforderung setzt deren konkrete Pilotabnahme und eine sichtbare Rest-/Zielbetriebsgrenze voraus. Die allgemeinen T01–T20 ersetzen insbesondere nicht die hier benannten Prüfungen zu C-03, C-12, E-02, FSCA/FSN, PRRC-Zuständigkeit, PSUR-Gruppierung und Produktmetadaten.

Der Beschaffungs-/Leseblocker der Matrix ist geschlossen. Die Vollständigkeit der Zuordnung ist vom technischen Erfüllungsnachweis, der medizinischen Bewertung, dem Azure-Deployment und der Betriebsfreigabe getrennt. Es entstehen durch diesen Abgleich keine Erlaubnis zu realen Meldungen, realen Gesundheitsdaten, kostenpflichtiger Bereitstellung oder zusätzlichen Studien-/Publikationssystemen.
