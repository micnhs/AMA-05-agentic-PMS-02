# Nachweisstand und Abnahmegrenzen

**Arbeitsstand, kein Abschluss der Gesamt- oder Fachabnahme.** Dieser Nachweis wird mit den tatsaechlich ausgefuehrten Paketen fortgeschrieben.

## Qualifizierter lokaler Preview-Schnitt

Die aktuell veroeffentlichte 5187-Vorschau ist an den sauberen lokalen
Checkpoint `ee13e91fd6909dba61b3e70fe53f644566368067` gebunden.
Der isolierte Windows-Browserlauf `.local\ui-replay-ee13e91` besteht mit
**5/5 Preview-Szenarien, ohne Skips**, bei
**`Retention:PhysicalMode=Disabled`**. Er prueft den durchgaengigen PMS-/MIR-Pfad,
getrennte menschliche Konten, CSRF, Dublin-Fristen, gruppierte PSUR-Berichte
einschliesslich aller Mitgliedsscope-Pruefungen und authentifizierter
JSON-/PDF-/ZIP-Exporte, menschlicher Revision und neuer zweifacher Freigabe.
Der Standard-Retention-Fall prueft auch die serverseitige Ablehnung eines
gefaelschten Client-Profilwechsels und das getrennte Lifecycle-Fristbeispiel.
Der eigene API-Prozess und die eigene SQL-Testdatenbank wurden danach beendet
beziehungsweise entfernt; die bestehende Demo blieb im isolierten Test
unveraendert. Ihre anschliessende additive Aktualisierung bewahrte die
vorhandenen Aggregate, Historien, Fristen, Audit-/Outbox-Daten und
urspruenglichen Objekte; keine Neuerzeugung der bestehenden Faelle.
`browser-results.json`: SHA256
`3270b242ddc63e5a54e7ab9a167985b62bdecbb2521848b570c2c1e69077362b`,
`sourceTreeDirty=false`.

UI01 brach beim begrenzten Startup-Request ab. UI02 blieb **3/5**: zwei Tests
suchten faelschlich ein explizites `type=submit`-Attribut an einem regulaeren
impliziten Submit-Button. UI03 verwendet die tatsaechlichen zugaenglichen
Buttonnamen und verlangt weiterhin erfolgreichen Dialogabschluss und die
fachlichen Assertions. Die fehlgeschlagenen Laeufe werden nicht umetikettiert.

`browser-results.json` bindet die ausgefuehrten Test-/Runnerdateien, fuenf
Anwendungsassemblies und alle veroeffentlichten Browserassets per SHA256.
Der vorangehende UI03-Lauf stammt noch aus dem Arbeitsbaum auf `b9a94b9`,
ausdruecklich `sourceTreeDirty=true`. Er bleibt vom spaeteren sauberen
ee13e91-Lauf getrennt.

Dies ist eine **Teilabnahme der lokalen Replay-Vorschau**. Der separat
deklarierte `PhysicalRetention`-Browserfall wurde hier nicht ausgefuehrt und
ist dadurch nicht freigegeben. Lokale Outbox-/Watchdog-Emulation ist kein aktueller
getrennter Functions-Hostlauf. Retention-I/O-Ursache, erneute Recovery auf
abschliessenden Quellen, aktuelles T12, Lastprofil, Linux-Gesamtintegration,
Cloud und Live-KI bleiben eigenstaendige offene Gates.

## Nachfolgende BS-/Recovery-Integration

- **13/13 Windows-Recovery, keine Skips**, auf sauberem **ee13e91**:
  `.local\recovery-ee13e91-current\recovery-results.json`,
  SHA256 `118cb0525cae037def6e4094041e86d7127197e52135ce00e50ba2fec3c9ae14`.
  Alle fuenf ausgefuehrten Anwendungsassemblies sind bytegleich mit dem
  genannten sauberen Preview-Paket. Echte getrennte SQL-/Objektwiederherstellung,
  aktuelle/historische Belege, beibehaltene Rohdaten, exaktes Loeschjournal,
  abgewiesene alte Cuts, eingefrorenes MIR, persistente Fristen,
  Unknown-/Outbox-/Receipt-Stand und neu aufgebaute lokale Search-Projektion
  wurden geprueft. Die exakte Eigentuemerlabel-Nachkontrolle ergab
  null eigene Container/Volumes. Keine Linux-SQL-Recovery oder Cloud-DR.
- Der danach ergaenzte BS-Zyklus bestand getrennt mit **6/6 SQL-Faellen**
  (`psur-review-cycle.trx`) und **6/6 Preview-Browserfaellen**
  (`.local\ui-bs-cycle-01`), keine Skips. Zwei gebundene Quellen,
  medizinische Revision mit Freigabeinvalidierung, getrennte Konten,
  verweigerter Abschluss einer offenen CAPA, spaeterer Wirksamkeitsnachweis,
  explizite Bewertung, authentifizierter JSON-Export und unveraenderte
  Original-PSUR-Bytes wurden tatsaechlich durchlaufen.
  Browserbericht-SHA256:
  `d8e765ded01b490fc1ae4cd95175e95ea3c3e1cbe81254a67bf5d22dccc00142`.
- Der ausdruecklich getrennte **PhysicalRetention-Browserlauf 1/1**
  (`.local\ui-physical-bs-cycle-01`) bestand ohne Skips im isolierten
  `SyntheticLocal`-Profil. Der Bericht
  `3e91a9f6b3f6757952a53f7d9582b72b9f367f72f73463ad0162369941b3f3e9`
  bindet exakt dieselben fuenf Assemblies wie der 6/6-Preview-Lauf.
  Der Runner entfernte seine API/DB; Port 5197 wurde danach ohne Listener
  bestaetigt. Das schaltet die veroeffentlichte Vorschau nicht frei und
  erklaert den frueheren Retention-I/O-Fehler nicht.
- Diese beiden neuen Browserberichte sind ausdruecklich
  `sourceTreeDirty=true` auf ee13e91; sie pruefen **nicht** den danach
  ergaenzten F08/F14-BS-Snapshotschnitt. Dessen erster SQL-Lauf
  `psur-bs-snapshot.trx` blieb **8/9**: im neuen Folgeperiodentest fehlte
  die Pflichtbegruendung der neuen Fassung. Nach Ergaenzung nur dieses
  Testeingangs bestand `psur-bs-cutoff-corrected.trx` mit **1/1**.
  Keine Produktionsregel wurde gelockert und der 8/9-Lauf bleibt erhalten.
  Keine dieser Pruefungen erteilt eine Gesamtfreigabe der urspruenglichen
  Pilotaufgabe.
- Der anschliessende Anwendungslauf bestand mit **122/122, ohne Skips**:
  `.local\test-results\current-bs-application.trx`,
  SHA256 `827339d14f976b59af614f89ebe60c5c34af86494e746db2b15972b76c11f59d`.
  Der Filter `FullyQualifiedName!~Cohors.Tests.RecoveryTests` trennt die
  eigene Docker-Recovery-Suite ausdruecklich ab; er enthaelt weiterhin die
  nicht-Docker-abhaengigen Recovery-Vertrags-/Pfadtests. Kein neuer
  vollstaendiger Recovery- oder separater Hostnachweis.
- Der BS-Snapshot-Browsererstlauf `.local\ui-bs-snapshot-01` blieb
  **5/6, ohne Skips**. Im neuen Refreeze-Schritt war der Standard-Submitname
  statt des sichtbaren individuellen Buttonnamens angegeben. Nur dieser
  Locator wurde korrigiert. Die komplette Wiederholung
  `.local\ui-bs-snapshot-02` bestand im identischen Anwendungspaket mit
  **6/6, ohne Skips**, weiterhin `Disabled` und `sourceTreeDirty=true`.
  Browserbericht-SHA256
  `e3c31c60c0c09e5a80806d1fcbf454eed8bb04959017b3cd32b10b1b34204252`,
  JUnit-SHA256
  `2c48ddafc519ae476a9b07b4213602f68e568b5d16325ad1ab59d6799dcfad58`.
  Die echte neue Berichtsfassung bewahrt den Original-PSUR, bindet den
  nachtraeglichen BS-Vorgang als `PostCutoffAddendum` und trennt
  `NotRecordedAtCutoff` von `QuestionClosed` beim Freeze. JSON und
  fehlender Mitgliedsscope wurden geprueft; beide BS-Screenshots wurden
  eingesehen. Der Runner entfernte seine API/DB, Port 5197 war danach frei.
- Ein danach ergaenzter Grenzfall reproduzierte im Langberichtstest eine
  bislang ungetestete Luecke: die getrimmte Laengenpruefung liess durch
  angehaengten Leerraum tatsaechlich mehr als 200.000 Zeichen zu.
  `psur-literal-limit-before.trx` bleibt **0/1**
  (`19ed145c37bfeebb0bffb48dd89da4549caf97922791ef7fa2203fa321fc9899`).
  Die gemeinsame Pruefung zaehlt jetzt vor dem Trimmen, ohne den
  gespeicherten Text zu veraendern. Der korrigierte echte Langberichtsfall
  besteht mit **1/1** in `psur-literal-limit-corrected.trx`
  (`9aaf6f534371956066e02d34200d675df8e2c9608ed3acfb7a9288068e52b84b`):
  medizinische Revision, Freigabeinvalidierung, unveraenderter Snapshot,
  exakte Grenze und abgewiesener Ueberlauf einschliesslich Leerraum in
  Erzeugung und Bearbeitung. Die zuvor genannten 122/122 und 6/6 liegen
  vor dieser engen Haertung; die 5187-Vorschau bleibt bis zur
  gesonderten quellengebundenen Aktualisierung auf ee13e91.

## Bereits ausgefuehrte Nachweise

- Vollstaendiger Windows-.NET-Lauf am Checkpoint `a5c7545`: **68 bestanden, 0 fehlgeschlagen, 0 uebersprungen** gegen echten isolierten Loopback-SQL; `.local\test-results\acceptance.trx`. Dies ist nicht die Vollsuite des spaeter erweiterten Arbeitsstands.
- Der neue SQL-MIR-Pfad prueft Initial-Unknown/Abgleich, offenen festen Sonntagstermin ohne Verschiebung, Watchdog-Eskalation, idempotenten alten Abgleich, FollowUp mit neuer Zusage und Final mit exakt gebundener Jurisdiktionsabdeckung.
- Fuenf SQL-/Service-PSUR-Provenienztests pruefen Kenntnis/Nachtrag, acht/fuenf Worldwide-/Teilregionsbelege, historischen geschlossenen N2-Fall, gesperrtes Altformat sowie Dokumentwirksamkeit vor/ab Folgeversion bei unveraendertem frueheren Snapshot.
- Vier weitere Terminologietests pruefen die echte FDA-Referenz, menschliche Kodierung und Freigabeinvalidierung, eindeutige Gruppen-/F02-Quellbelege, getrennte historische Kodes, blockierte widerspruechliche Hierarchien sowie selbst konsistente, aber vom SQL-Original abweichende Snapshot-Kodierung.
- Nach dem 68er-Checkpoint: **20/20 fokussierte Windows-SQL-/PSUR-Tests**
  in `.local/test-results/psur-terminology-history.trx`. Dazu gehoeren drei
  neue Serien-/Parallelitaets-/Zeitplanfaelle und drei neue historische
  Kodierungsfaelle: New-2026 als 2025, Added-2024 als 2023 sowie heutige
  retrospektive Kodierung versus wortgetreu belegte Originalangabe.
  Fehlende/falsche Belegversionen und Zitate, Freigabeinvalidierung bei
  Herkunftsaenderung und unveraenderte eingefrorene Berichte sind geprueft.
  Dies ist noch kein neuer vollstaendiger Suite-/Linux-Lauf.
- PSUR-Erweiterungen wurden danach in **31/31 echten SQL-Tests, ohne Skips**
  geprueft: `.local\test-results\psur-group-integration-resumed.trx`.
  Darin enthalten sind gruppenweite Service-/Exportberechtigungen,
  unabhaengige Basic-UDI-Zaehler, Profil-/Mitgliedschaftsversionen, BS/
  Leitprodukt/Zeitplangrenzen, zulaessige nichtfuehrende Mitverwendung,
  unveraenderte Altberichte sowie die bisherigen Serien-, Quellen-,
  Terminologie- und Exportregressionen. Der vorherige gleichnamige Erstlauf
  ohne `-resumed` scheiterte mit 31 Verbindungsfehlern am nicht erreichbaren
  SQL-Host; er ist keine fachliche Reproduktion.
- Der aus diesem SQL-Lauf erzeugte Gruppen-PDF wurde unabhaengig mit
  PyMuPDF gelesen: **13 Seiten, 2 Basic-UDIs, alle 42 Quellhashes und
  beide Paket-/Scope-Hashes**, keine horizontale Textabschneidung.
  Der vorangehende Einzelfamilienexport umfasste sieben Seiten, alle
  21 Quellhashes sowie deutsche und griechische Zeichen.
  Das ersetzt keine klinische, semantische oder behoerdliche Abnahme.
- Die anschliessenden globalen Basic-UDI-Identifier-/Perioden- und
  nichtfuehrenden Quellmanipulationsregressionen liefen mit den
  PSUR-/Terminologietests in `current-pms-retention-sql.trx`: alle
  **29 PSUR-/Terminologiefaelle bestanden**. Der Gesamtlauf bleibt **31/34**;
  drei Retention-Faelle scheiterten an doppelt serialisierten
  Autorisierungs-Hashfeldern (`id`/`Id`). Das explizite Feld `actorId`
  korrigiert diesen Fehler; `retention-sql-corrected.trx` bestand danach
  mit **5/5**, ohne Skips. Dies sind getrennte Laeufe.
- Der PSUR-Generierungs-/Revisionsvertrag wurde auf die gemeinsame
  endliche Grenze von 200.000 Zeichen vereinheitlicht. Ein echter
  gruppierter Langentwurf mit menschlicher Revision, Freigabeinvalidierung,
  unveraenderten Snapshotbytes und expliziter Ueberlimit-Ablehnung ist als
  SQL-Regressionsfall tatsaechlich im genannten 29er-Teil bestanden.
  Die enge Langbericht-Regressionsfeststellung ist damit lokal geschlossen,
  nicht die vollstaendige fachliche T16-Abnahme.
- Im aktuellen Retention-Arbeitsblock liefen **17/17 nicht-SQL-abhaengige
  Tests ohne Skips**: die neun Byteintegritaetsregressionen, vier
  unabhaengige T18-Regel-/Datumsfaelle und vier wirkliche lokale
  Rohobjektfaelle (Entfernung/Replay, fehlende Bytes, Shared-Hash-Kopie,
  fremde Autorisierung und Wiederauftauchen).
  `.local\test-results\retention-local-current.trx`.
  Die Erweiterung um drei wirkliche PowerShell-Recovery-Vertragstests
  bestand danach mit **20/20** in
  `.local\test-results\retention-recovery-integration-offline.trx`.
- Der integrierte T18/T20-Runner bestand mit **13/13**, ohne Skips:
  `.local\t18-t20-integrated-current\recovery-results.json`.
  Echte getrennte SQL-/Objektbereiche, nativer Vor-/Nachloeschungs-Cut,
  aktiver Rohdatensatz, exaktes Loeschjournal und gesperrte alte Cuts wurden
  geprueft. Vier Implementierungs- und fuenf Assemblyhashes binden den Lauf.
  Die unabhaengige anschliessende Eigentuemerlabel-Pruefung ergab null
  eigene Container/Volumes. Spaetere Diagnostikaenderungen sind damit
  nicht automatisch erneut geprueft.
- Der vollstaendige Anwendungslauf **ohne Recovery** in
  `current-application-excluding-recovery.trx` bleibt **109/110**:
  `T18_AnInFlightDeletionCannotAcquireANewReferenceFromAnotherSqlWriter`
  meldete `retention_incomplete (retention_object_io)` nach 0,790 Sekunden.
  Die damalige Ursache, HResult und der genaue physische Schritt wurden
  nicht aufgezeichnet. Kein belegter SQL-Timeout, Referenzverlust,
  Host-/Schutzsoftwarefehler oder nachgewiesener Flake.
- Der Fehlerwrapper erhaelt inzwischen die InnerException. Der gezielte
  Diagnoselauf bestand mit **5/5**, der vollstaendige getrennte Wiederholungslauf
  `current-application-diagnostics.trx` mit **110/110**, ohne Skips.
  Beides erklaert den frueheren I/O-Fehler nicht. Anschliessend ergaenzte,
  kompilierte Testdiagnostik protokolliert bei erneutem echtem I/O-Fehler
  nur Typ, HResult, `DeleteRaw` und Repository-Quellzeile; keine verdeckte
  Wiederholung, Ersatzbytes oder Aenderung von Schutzkontrollen.
- API und React wurden gebaut; SQL-Migration und synthetische Baseline liefen gegen einen echten lokalen SQL-Server.
- Ein Browserlauf hat Beschwerde, Duplikat, unbekannte UDI, zwoelfteilige Replay-Analyse, QA, getrennte Medical-/RA-Freigaben, eingefrorenes Paket, `Unknown`, Abgleich ohne zweiten Versand und Auditmanifest erfolgreich durchlaufen.
- Auth-/CSRF- und mobiles Login-Layout wurden im Browser geprueft.
- Ein eigener Browser-Regressionsfall verwendet `Europe/Berlin` und prueft Dublin-Fristen einschliesslich Datumskarte, Workbench und Fristenliste. Die gespeicherten Instants bleiben identisch.
- Sieben erste .NET-Tests prueften unabhhaengige Kalender-/DST-/Ratenreferenzen und Poisson-Referenzwerte.
- Die exakten oeffentlichen FDA-/PubMed-Queryvertraege wurden lesend erreicht. Die Integration im Domaenenpfad wird gesondert getestet.

Die fruehen Windows-Browserlaeufe betreffen den v2-Preview; der oben getrennt
bezeichnete UI03-Lauf prueft den aktuellen erweiterten Arbeitsstand. Der unten bezeichnete
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

Der separate T12-Handoff `2cba8ce6` ist als `947386f1` integriert,
der Folgecommit `1253b884` als `b9a94b9492c431a19adbc6750aee340665085f2f`.
Hier wurden **38/38 Offline-Infratests** und die Node-Treibersyntax
erfolgreich geprueft. Kein zweiter paralleler T12-Hostlauf wurde gestartet.
Der von Infra gelieferte Lauf09 besteht auf der App-Baseline `245b3b0`;
der Harness bindet weiterhin ausdruecklich diese Quelle. Das ist kein
Nachweis der neueren Fach-/Retention-/PSUR-Implementierung. Die aktuelle
quellengebundene Hostwiederholung, Linux-, Actions- und Cloud-Abnahme
bleiben getrennt.

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
| T12 Neustart/Timer | Separater Host-Harness integriert, 38 Offline-Infratests bestanden; Infra-Lauf09 besteht nur auf 245b3b0, aktuelle Quellenwiederholung offen |
| T13 Unknown | Erster Browser-Abgleich bestanden; Hashwechsel-/Coverage-Regression angelegt |
| T14 MIR | Original-7.3.1-Mapping/XSD, fehlende Pflichtdaten, Signaturbindung, Folge-/Finaltypen und feste Folgepflichtkette SQL-geprueft; MIR-Browserbedienung und XML-Download im bezeichneten Linux-Lauf bestanden |
| T15 Statistik | Unabhaengige normalisierte Raten, ungueltige Nenner und Poisson-Werte bestanden |
| T16 PSUR | Quellen, historische Kodierung, Serien/Gruppen, globale Identifier-/Periodengrenzen und >60000-Zeichen-Revision SQL-geprueft; UI03 prueft all-member-Exporte, Revision und getrennte Freigaben; fachliche Gesamtfreigabe offen |
| T17 CAPA | Unabhaengiger Wirksamkeitsnachweis, negatives Ergebnis, Nacharbeit und erneute Pruefung im aktuellen SQL-Lauf bestanden |
| T18 Audit/Retention | Getrennte 20er-Offline-, 5er-SQL- und 13er-Recovery-Nachweise; frueherer I/O-Fehler ungeklaert trotz gruenem Wiederholungslauf. Preview prueft nur Disabled-Profil, physischer Pfad nicht freigegeben |
| T19 Simulatorgrenze | Keine echten Melde-/Klinikadapter; weitere Export-/Endpoint-Pruefung offen |
| T20 Wiederherstellung | Integrierter 13er-Windows-Lauf mit echten SQL-/Objekt-/Journal-Cuts bestanden; erneut auf finalen Quellen sowie Linux-/Cloud-DR offen |

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
