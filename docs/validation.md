# Nachweisstand und Abnahmegrenzen

**Arbeitsstand, kein Abschluss der Gesamt- oder Fachabnahme.** Dieser Nachweis wird mit den tatsaechlich ausgefuehrten Paketen fortgeschrieben.

## Bereits ausgefuehrte Nachweise

- Aktueller vollstaendiger Windows-.NET-Lauf: **68 bestanden, 0 fehlgeschlagen, 0 uebersprungen** gegen echten isolierten Loopback-SQL; `.local\test-results\acceptance.trx`. Er ersetzt fruehere Zwischenlaeufe mit einzelnen Testfehlern.
- Der neue SQL-MIR-Pfad prueft Initial-Unknown/Abgleich, offenen festen Sonntagstermin ohne Verschiebung, Watchdog-Eskalation, idempotenten alten Abgleich, FollowUp mit neuer Zusage und Final mit exakt gebundener Jurisdiktionsabdeckung.
- Fuenf SQL-/Service-PSUR-Provenienztests pruefen Kenntnis/Nachtrag, acht/fuenf Worldwide-/Teilregionsbelege, historischen geschlossenen N2-Fall, gesperrtes Altformat sowie Dokumentwirksamkeit vor/ab Folgeversion bei unveraendertem frueheren Snapshot.
- Vier weitere Terminologietests pruefen die echte FDA-Referenz, menschliche Kodierung und Freigabeinvalidierung, eindeutige Gruppen-/F02-Quellbelege, getrennte historische Kodes, blockierte widerspruechliche Hierarchien sowie selbst konsistente, aber vom SQL-Original abweichende Snapshot-Kodierung.
- API und React wurden gebaut; SQL-Migration und synthetische Baseline liefen gegen einen echten lokalen SQL-Server.
- Ein Browserlauf hat Beschwerde, Duplikat, unbekannte UDI, zwoelfteilige Replay-Analyse, QA, getrennte Medical-/RA-Freigaben, eingefrorenes Paket, `Unknown`, Abgleich ohne zweiten Versand und Auditmanifest erfolgreich durchlaufen.
- Auth-/CSRF- und mobiles Login-Layout wurden im Browser geprueft.
- Ein eigener Browser-Regressionsfall verwendet `Europe/Berlin` und prueft Dublin-Fristen einschliesslich Datumskarte, Workbench und Fristenliste. Die gespeicherten Instants bleiben identisch.
- Sieben erste .NET-Tests prueften unabhhaengige Kalender-/DST-/Ratenreferenzen und Poisson-Referenzwerte.
- Die exakten oeffentlichen FDA-/PubMed-Queryvertraege wurden lesend erreicht. Die Integration im Domaenenpfad wird gesondert getestet.

Die fruehen Windows-Browserlaeufe betreffen den v2-Preview. Der unten bezeichnete
isolierte Linux-Lauf prueft dagegen den neuen MIR-Editor, authentifizierten
XML-Download und die Folgepflicht-Anzeige mit getrennten echten Medical-/RA-
Browsersitzungen. Die laufende Windows-Demo wurde dabei weder migriert noch
ersetzt. Frontend-Build und .NET-Static-Web-Assets-Build duerfen nicht gleichzeitig
denselben `wwwroot`-Ausgabeordner veraendern.

## Tatsaechliche isolierte lokale Linux-Abnahme

`linux-79c71c096289` lief in einem eigenen Linux-Container mit frischer
Konfiguration, eigener Loopback-SQL-Instanz auf Port 14348, eigener API auf 5198,
.NET 10.0.404, Node 24.22.3, PowerShell 7.5.0 und Chromium. Ergebnis:
**64/64 SQL-/Anwendungstests und 3/3 Browsertests**, keine Skips, beide
API-/AgentHost-Images gebaut, beide Flex-Codepakete samt erforderlichen
Trigger-Metadaten geprueft. Credential-/Ergebnisfilter liefen vor der Freigabe
der Berichte. Eigene SQL-Instanz/Volume und generierte Zugangsdaten wurden
anschliessend entfernt; die Original-Demokonfiguration blieb hashgleich.

Dies ist **kein GitHub-Actions-Lauf**. Die Actions-only-Hostpruefung wurde nicht
umgangen. Auch ein Imagebuild beziehungsweise `functions.metadata` belegt
keinen gestarteten Functions-Host oder OneDeploy. Der Lauf bindet den
Quellenmanifest SHA256
`818616ff33a80ba07ad1ae6c8290097dca4e7b7d629bc50de595149c524301ca`.
Er liegt **vor** den vier neuen Terminologie-Regressionsfaellen; deren Nachweis
ist zunaechst der aktuelle Windows-Lauf. Vor Gesamtabschluss ist der dann
aktuelle Integrationsstand erneut abzugleichen.

| Artefakt | Identitaet |
| --- | --- |
| API-Image | `sha256:bd3605cb812f8f1095a14234fcdcbcf7321c0506629899b36f6e76d9d615cdbe` |
| AgentHost-Image | `sha256:de3ee8c568da41556a700b4f9f67cc2327f0fa5507bcbe599e8d387a7ebd4ac8` |
| Workflows-Codepaket | `337526f8267fb8571a634cd29273147cd63bf646b0dd7e47685f89c827301110` |
| Operations-Codepaket | `c92d0acea0d52c5a1830a7f37e2827e01ffdab25eecf66e0031b1431f9e15e1e` |

Gefilterte Berichte, Screenshots, Quellenmanifest und Buildmetadaten liegen in
`.local/linux-runs/linux-79c71c096289/evidence/verified`. Der Erstversuch hatte
einen realen PowerShell-IDictionary-/ConnectionString-Setterfehler im
Credential-Inventar aufgedeckt und korrekt abgebrochen; keine Rohlogs wurden
veroeffentlicht. Der korrigierte Produktions-CI-Helper ist mit dem dritten
Infra-Commit integriert: **3/3 echte PowerShell-Regressionen in Linux ohne
Netzwerk**, **28/28 Offline-Infratests unter Windows**. Diese Tests ersetzen
keinen echten Actions-Lauf. Der separat dokumentierte Checkov-Sicherheitsstand
bleibt BLOCKED; keine Findings wurden still unterdrueckt.

## T01-T20

| Szenario | Umsetzung / Nachweis |
| --- | --- |
| T01 Duplikat | Ein Vorfall, getrennte Originalquellen; Browser bestanden, erweiterter SQL-Test angelegt |
| T02 Unbekannte UDI | Intake ohne erfundenen UDI; Browser bestanden |
| T03 Fehlender klinischer Befund | QA, zweifach genehmigte simulierte RFI, unveraenderte Frist; SQL-Abnahme erweitert |
| T04 Unbelegte Kausalitaet | Kritische Quellen-/Claim-/Abschnittsbefunde blockieren; gezielte Negativtests angelegt |
| T05 Modell-/Search-Ausfall | Expliziter Fehler, unabhaengiger menschlicher Mindestbericht; SQL-Abnahme angelegt |
| T06 Parallelversion | SQL-Version/Audit/Outbox-Regression angelegt |
| T07 Aenderung nach Freigabe | Gebundener Scope einschliesslich Verpflichtungsreferenzen; Negativtests angelegt |
| T08 Unberechtigte Identitaet | Browser-Auth/CSRF bestanden; Rollen-/Produktscope-SQL-Tests angelegt |
| T09 Prompt Injection | Keine DB-/HTTP-/Approve-/Submit-Tools fuer das Modell; Negativtest angelegt |
| T10 Deadline-Grenzen | Unabhaengige Kalender-/DST-Werte und abweichende Browserzeitzone bestanden |
| T11 Neue Informationen | Getrennte Ausnahme, keine spaete Verschiebung, zwei menschliche Nichtmeldepflichtpruefungen; SQL-Abnahme erweitert |
| T12 Neustart/Timer | SQL-Persistenz und fehlender Herzschlag; SQL-Abnahme angelegt |
| T13 Unknown | Erster Browser-Abgleich bestanden; Hashwechsel-/Coverage-Regression angelegt |
| T14 MIR | Original-7.3.1-Mapping/XSD, fehlende Pflichtdaten, Signaturbindung, Folge-/Finaltypen und feste Folgepflichtkette SQL-geprueft; MIR-Browserbedienung und XML-Download im bezeichneten Linux-Lauf bestanden |
| T15 Statistik | Unabhaengige normalisierte Raten, ungueltige Nenner und Poisson-Werte bestanden |
| T16 PSUR | Kenntnis-/Regions-/Vergleichsperioden-Provenienz, gesperrtes Altformat, Dokumentwirksamkeit und A/C/D/F-/Todesfallkodierung SQL-geprueft; uebrige Serien-/Status-/Exportintegration noch offen |
| T17 CAPA | Unabhaengiger Wirksamkeitsnachweis, negatives Ergebnis, Nacharbeit und erneute Pruefung im aktuellen SQL-Lauf bestanden |
| T18 Audit/Retention | Ledger und Historien-/Current-State-Manipulationspruefung angelegt; Retention/Recovery noch nicht vollstaendig nachgewiesen |
| T19 Simulatorgrenze | Keine echten Melde-/Klinikadapter; weitere Export-/Endpoint-Pruefung offen |
| T20 Wiederherstellung | Vollstaendige SQL-/Objekt-/Outbox-/Search-Wiederherstellung noch offen |

Die vom Auftraggeber uebergebenen unabhhaengigen Referenzen liegen unveraendert in `tests\Cohors.Tests\Fixtures\cohors-acceptance-references.json`. Deren ursprüngliches `applicationVerified: false` dokumentiert die Herkunft des Referenzsatzes; nur ausgefuehrte Anwendungstests liefern den zusaetzlichen Nachweis.

## MIR-Herkunft, nicht Behoerdenabnahme

Der unabhaengige Koordinatornachweis vom 15.11.2026 ist unveraendert unter
[`contracts/regulatory-schemas/mir/provenance.json`](../contracts/regulatory-schemas/mir/provenance.json)
integriert. Er belegt den Link von der offiziellen EC-PMSV-Seite zum MIR-7.3.1-
SB11252-Archiv (49.788 Byte, SHA256
`7f230641de8df5dae7af64dcd780e081ee7ccfe86a37a11cbcf59dacdc80c839`),
alle acht vendorten XSD/XSL-Dateien und die drei aktiven Runtime-Pins. In dieser
Integration wurden alle acht lokalen Dateihashes nochmals gegen diesen Nachweis
verglichen; kein erneuter Download und keine Uebermittlung.

`incident-PDFgeneric-v7.3.1.xsd` ist ein Schema, kein Nachweis ueber die separate
geschuetzte PDF-Datei oder deren Build. Herkunft und XSD-Konformitaet sind von
Mapping-Vollstaendigkeit, IMDRF-Semantik, klinischer Plausibilitaet, Browserbedienung
und tatsaechlicher Behoerdenannahme getrennt.

## Nicht ausgefuehrt / keine Behauptung

Keine Azure-Bereitstellung, keine Entra-Live-Freigabe, kein Foundry-Live-Modellergebnis, keine private Azure-Netzwerkabnahme, kein extern geschuetztes Ledger-Digestpublishing, keine produktive Retention, keine echte Behoerden-/BS-Akzeptanz.

Die unveraenderte 118er-Matrix und der abgeschlossene Einzelabgleich sind integriert. Alle Originalstatus bleiben Offen; zugeordnete Pilot- und Zielbetriebsnachweise sind nicht pauschal erfuellt. Klinische/medizinische Plausibilitaetsabnahme, vollstaendige Laenderkalender, SOP-/Sprachfreigaben, DSGVO/DPIA und ein unabhaengiger fachlicher Holdout bleiben separat. Weder behauptete Zeitersparnisprozente noch klinische Sensitivitaet lassen sich aus diesen kleinen technischen Demotests ableiten.
