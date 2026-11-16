# Nachweisstand und Abnahmegrenzen

**Arbeitsstand, kein Abschluss der Gesamt- oder Fachabnahme.** Dieser Nachweis wird mit den tatsaechlich ausgefuehrten Paketen fortgeschrieben.

## Qualifizierter lokaler Preview-Schnitt

Die aktuell veroeffentlichte **5187-Vorschau** verwendet das unveraenderte Paket
`.local\app-replay-prefill-d166658` aus dem sauberen Quellcheckpoint
**`d16665832fd1066be60c7f15db41b2c3d1233cc7`**. Sie laeuft mit
**`Retention:PhysicalMode=Disabled`**, `Seed=false`, `AutoMigrate=false`,
`LocalOperations=true` und `AgentTransport=Local`. Weitere Quellarbeit aendert
dieses bereits veroeffentlichte Paket nicht. Der Vorbefuellungsschnitt ist mit
neuem Build, **46/46 gezielten Domain-/SQL-Faellen und 1/1 ReportPrefill-Fall**
geprueft, nicht mit einer neuen Vollsuite. Der unmittelbare Rueckfall bleibt
das unveraenderte UI-Paket `27672ae` mit eigenem 21/21-/2/2-Nachweis.
Die unten aufgefuehrten Belege
binden auch die tatsaechlich laufende API, ihre HTTP-Assets und eine eigene
rein lesende Edge-Ansicht der erhaltenen Demo.

### Historischer 2a-Schnitt und unveraenderter Rueckfall

Das fruehere Paket `.local\app-replay-2a29196` aus
`2a291966e551f5f69ac7ceed2cf1d5aed1c0c13f` bleibt unveraendert erhalten.
Die folgenden 122/6-Ergebnisse gelten nur fuer diesen damaligen Stand,
nicht fuer spaetere Audit-/Retention-/UI-Quellen.

| Historischer Nachweis | Ergebnis / Quellenbindung |
| --- | --- |
| `.local\test-results\application-2a29196.trx` | **122/122, keine Skips**; SHA256 `3c6ee59e89f003d8087ced1ff028ac6d23479e3bdf5ff67636b4d5a536ef7dfb` |
| `.local\ui-replay-2a29196\browser-results.json` | **6/6 Preview-Szenarien, keine Skips**; `sourceTreeDirty=false`; SHA256 `9231b8048919830ff2cef9ae96aba7777e09b51cc48ac34908f76d545dccedff` |
| `.local\ui-replay-2a29196\e2e-results.xml` | SHA256 `788ef5fd843674f014965607aafb223925b5d72ca7391aeb6292b56f5c2aa6d1` |
| `.local\preview-handoff-2a29196.json` | Quell-, Paket-, Browser- und Bestandsbindung; SHA256 `ee94426a1473afa664af1458c1df554a7c5860705c7b2d1e4aa5850183131653` |

Der Anwendungsfilter `FullyQualifiedName!~Cohors.Tests.RecoveryTests` trennt
die eigene Docker-Recovery-Suite ausdruecklich ab. Die 122 Faelle sind daher
**keine neue Recovery-Gesamtabnahme**. Alle **16 Paketdateihashes**, darunter
fuenf Anwendungsassemblies und die Browserassets, wurden mit dem danach auf
5187 gestarteten Paket verglichen. Der Browserlauf verwendet eigene SQL-/API-
Ressourcen auf 5197; diese wurden terminal entfernt.

Die sechs Browserfaelle pruefen den bisherigen PMS-/MIR-/Scope-/Export-Schnitt
und zusaetzlich den simulierten BS-Rueckfrage-/Antwort-/Massnahmenzyklus samt
historischer PSUR-Snapshotbindung. Der authentifiziert heruntergeladene
`cohors-psur-bs-snapshot.pdf` hat 16 Seiten; BS-Marker, Stichtags-/aktuelle
Statusangaben und der Korrespondenzanhang wurden gelesen, die letzte Seite
gerendert und auf abgeschnittene Inhalte geprueft. PDF-SHA256:
`f52630251e5b225bd0f8d7476bbf55f9db6aa2ff913cfd3d2deb88ec114044fc`.
Dies ist keine unabhaengige erneute Einzelpruefung jedes darin genannten
Quellenhashs und keine echte BS-Akzeptanz.

Die Aktualisierung der bestehenden Demo bewahrte **66 Fachaggregate, 1821
vorhandene Historien-/Audit-/Command-/Outbox-/Inbox-Bindungen, 17 persistente
Fristen und 48 Objekte**. Einstellungen und der vorhandene gruppierte
63.805-Zeichen-Entwurf blieben unveraendert; neue betriebliche Heartbeats sind
getrennt erfasst. `.local\preview-2a29196-browser.json` belegt anschliessend
echte getrennte PMS-/RA-UI-Anmeldungen auf 5187, das Disabled-Profil und den
erhaltenen Entwurf ohne neue Fachkommandos. Die bereits geteilte Browserseite
wurde aktualisiert; nach Sitzungsablauf zeigt sie regulaer die Anmeldung.

**Qualifizierte Teiluebergabe, kein Pilotabschluss:** Die physische Loeschung
bleibt in dieser Vorschau gesperrt. Die historische I/O-Ursache, aktuelle
T18/T20-Gesamtintegration, aktuelle getrennte Functions-/Agent-Hosts, Last,
Linux-Gesamtintegration, Azure/Entra/Foundry sowie Organisations- und
Fachfreigaben bleiben getrennt offen. T12 Lauf09 bleibt an `245b3b0` gebunden.

## Begrenzter L03-/Diagnostikblock neben der Vorschau

Der neue produktbezogene Auditexport und die native I/O-Diagnostik wurden
**getrennt von der laufenden 2a-Vorschau** gebaut und ausgefuehrt. Alle Belege
liegen unter `.local\audit-diagnostics-01`. Der getestete Anwendungsstand war
die explizite Arbeitsfassung auf 2a29196, nicht deren unveraendertes Paket:
`source-03\source.json` bindet 232 Quelldateihashes und die geaenderten Dateien,
SHA256 `d7800dc0606ecbb0bcafb2d8a816d65fd9467df469e01539d1b23d18525bd336`.
Die spaetere Browserquelle `source-04\source.json`,
SHA256 `3e92785065a1e9139ed36a286acc995a458bfcfa496f8a262483fda29b358883`,
aendert daran ausschliesslich den Playwright-Profilfilter. Die fuenf
Anwendungsassemblies des eigenen `application`-Pakets blieben bytegleich.

| Getrennter Lauf | Ergebnis und SHA256 |
| --- | --- |
| `l03-diagnostics-02.trx` | **17/17** gezielte `ProductAuditTests`/`RetentionTests`, keine Skips; `e8154d28d7eebba16f53992a9456c57dbf7f07031634bc8ee948a325312774db` |
| `ui-audit-02\browser-results.json` | **1/1 AuditExport**, Disabled, keine Skips; `126df1ececcc65407929b922b8e5df5047f505ca9ea66e368864998a05bb53a5` |
| `ui-physical-01\browser-results.json` | **1/1 PhysicalRetention**, isoliertes SyntheticLocal, keine Skips; `3457e073c645da15783c32ef95e6a8ffc2dc79aeb591893a580cf50f2ad24fcd` |
| `ui-physical-01\cohors-retention-io.json` | Wirklicher kontrollierter Fehler und anschliessender Abgleich; `0e4351e141d327d85b12b25688cb853a505de17409361cfb157bfae9432cefe4` |
| `terminal-cleanup.json` | Terminaler eigener Ressourcenabschluss und Bestandsvergleich; `0976f26b39f1be9c384c00af0a27b86d10ff17d71d6f2613ca4e11b1201a69aa` |

Der reale Auditor-Download prueft alle ZIP-Dateihashes, Originalversionen,
Objektbytes, Mehrfamilienscope und die Ablehnung des Einzelfamilienkontos.
`cohors-product-audit.png` und `cohors-retention-io-failed.png` wurden visuell
gelesen. Der physische Fall provoziert in seinem eigenen Testverzeichnis
einen echten Journal-Datei/Verzeichnis-Konflikt:
`PrepareJournal / UnauthorizedAccessException / 0x80070005`.
Die Rohbytes bleiben erhalten, der fehlgeschlagene SQL-Zustand und die
gefilterte Anzeige sind sichtbar; erst ein weiterer ausdruecklicher
menschlicher Abgleich mit derselben Autorisierung beendet den Vorgang.
Das erklaert **nicht** die historische I/O-Ursache und aktiviert den Pfad
nicht auf 5187.

Die negativen Erstbefunde bleiben erhalten: Der erste C#-Build scheiterte an
`CS8631` in einer neuen xUnit-Arrayassertion; die unveraenderte Einzelprodukt-
Anforderung wird nun mit einer exakten Einzelwertassertion geprueft.
`l03-diagnostics-01.trx` blieb **12/16**, SHA256
`9b6ed2205f582c9a6f97cefcd46daf5e71d9c8ae6db746ba06d2e8991f69980c`.
Die vier Auditfehler entstanden bei einer legitimen leeren Inbox:
Eine skalare `FOR JSON`-Unterabfrage lieferte nachweislich SQL NULL.
Die explizite leere JSON-Menge wurde korrigiert; ein neuer Negativfall
weist einen behaupteten Outbox-Abschluss ohne erforderliche Inbox weiter
ab. Der erste Browseraufruf fand wegen `^L03` im vollqualifizierten
Playwright-Titel **keine Tests** (JUnit
`15babb5d5331896d526476dc682c9ede02ac8501aff237138786e0da75c1dafc`).
Nach reiner Profilkorrektur wurden beide begrenzten Profile vorab eindeutig
aufgelistet. Der spaeter verzoegert beendete Audit-Worker endete regulaer;
kein Prozess wurde zwangsweise beendet.

Die Abschlusspruefung bestaetigt **0 Testdatenbanken, 0 neue
Fixtureverzeichnisse, 0 private Browserreste und 0 Listener auf 5197**.
Drei bereits vorher vorhandene Verzeichnisse blieben ausdruecklich erhalten.
Kein Dockerressourcenstart. Die alte 5187-API mit PID 72076 blieb GET 200;
66 Fachaggregate, je 107 Fachhistorien-/Audit-/Outbox-/Inbox-/Command-Zeilen
(typisierte Operations ausgenommen), 17 Fristen, 48 Objekte, beide
Konfigurationsdateien und alle 16 Paketdateihashes blieben exakt unveraendert.
Das ist **keine neue Vollsuite, kein 7/7-Previewlauf, keine Recovery-,
separate Host-, Last-, Cloud- oder medizinische Abnahme**.

## Historischer Compilerblock fuer Recovery-Fixture und Sammelbericht

Der freigegebene reine Compilerblock baute ausschliesslich
`tests\Cohors.Tests\Cohors.Tests.csproj` mit privatem SDK 10.0.404/CLI-Home,
beiden First-use-Flags `false`, `--no-restore`, deaktivierten Buildservern
und einem MSBuild-Knoten. Basis ist
`75bee999fff3c1bb723274a16486283aa8883892` **plus explizit schmutziger
Arbeitsfassung**, nicht ein unveraendert getesteter Commit. Enthalten sind
die integrierten c906641/d0e864dc-Fixtures und die neuen Sammelbericht-C#-
Quellen. `source-02.json` bindet alle C#-Quellhashes und die sechs
aufgeloesten Projekt-Dependencygraphen; keine Eingabe aenderte sich im Build.

| Beleg unter `.local\recovery-typecheck-01` | Tatsaechliches Ergebnis / SHA256 |
| --- | --- |
| `build-01.log` | **Exit 1**, zwei `xUnit2031` in `RecoveryTests.cs:271/272`; `d0794a78c5dc904c83c376453b52dd70913d1d06cd026d4eaed365813ca318da` |
| `source-02.json` | `ea29c9fdc7ed4b50d0590b22d0d207a403a023ce35adc7bf1a884e3325364f51` |
| `build-02.log` | **Exit 0, 0 Warnungen, 0 Fehler**, 42,61 s; `5a4f7686737e0dbef4cded47061155cb14e6b02070bbfbc5f42ade0ca085b9a9` |
| `handoff.json` | Beide Ausgaenge, sechs Ausgabeassemblyhashes und terminaler eigener Ressourcenabschluss; `281bddf277de1a76c4a01ed92733a8b65696d9120fb04de1e475c050f46cbaa4` |

Einzige Korrektur zwischen den Builds: `Assert.Single(collection, predicate)`
statt `Assert.Single(collection.Where(predicate))` an den zwei belegten
Stellen; Rollen und exakt-eins-Anforderung unveraendert. Die nach Wrapperende
geprueften eigenen Konsolenprozesse waren beendet; das zuvor exakt
inspizierte private CLI-Home wurde entfernt. Kein Prozessabbruch, keine
Abhaengigkeitswiederherstellung oder globale SDK-/Profilaenderung.

**Null Laufzeittests:** 14 Recoveryfaelle/11 Fixturechecks bleiben erwartet.
Der Sammelbericht-Schnitt fuer **A-04/C-12/K-01** enthaelt Vereinbarung,
eigenstaendiges Paket, getrennte Freigaben, Simulator, Export und UI sowie
vier vorbereitete SQL-Faelle/einen Browserfall; deren Ausfuehrung fehlte in
diesem Compilerblock.
Anwendungs- und E2E-TypeScript wurden separat nur statisch geprueft.
Das weiterentwickelte Preview-Profil erwartet jetzt acht Faelle, **kein
8/8-Nachweis**. Der FSCA-basierte Zulassungszweig ist noch nicht implementiert.
Die 2a-Vorschau wurde weder promoviert noch neu gestartet, ihre Demo-SQL
nicht angesprochen. Keine neue SQL-/Recovery-/Browser-/Docker-/Host- oder
Lastabnahme; der schwere Platz ist nach diesem begrenzten Block zurueckgegeben.

**Nach diesem unveraendert archivierten Compilerblock** wurde die
Sammelvereinbarungsgrenze eng korrigiert: eine volle vereinbarte Periode
plus ein noch gueltiger Folgetag, gegebenenfalls erst ab dem naechsten
Intervallanker nach dem Vereinbarungsdatum. Ablaufdatum, Einzelkenntnis und
LegalDue/SafetyDue werden nicht umgedeutet. Fuenf vorbereitete SQL-freie
Regressionsfaelle decken 1/7/366 Tage, spaete Vereinbarung sowie das am
Ablauftag endende Intervall ab. Der vorhandene SQL-Fall ergaenzt die
Ablehnungen; der Browserfall prueft die Ablehnung ohne Speicherung und
die minimale nutzbare Grenze.
Diese spaeteren C#-Aenderungen und Regressionen waren **zu diesem Zeitpunkt
nicht vom obigen Build oder einem Laufnachweis abgedeckt**. `.local\summary-boundary-01`
enthaelt erfolgreiche C#-/PowerShell-Syntax- und Anwendungs-/E2E-TypeScript-
Pruefungen, keine neue C#-Typpruefung. `post-compiler-csharp.diff`, SHA256
`29fcce54cb41266ce50b42d1c79350afb74c831eaa7b77ac42acb98b7935f9bb`,
grenzt die zwei geaenderten C#-Dateien exakt ab; Rueckanwendung im Speicher
reproduziert deren urspruengliche Build-Quellhashes, 95 weitere erfasste
Eingaben blieben unveraendert. Die gesicherte zweizeilige
Recovery-Analyserkorrektur und ihre Dokumentation stehen separat in
`71c9835199ee87c11a2135b833cdfe50ffb85862`.

## Begrenzter Sammelbericht-Nachweis einschliesslich Datumsgrenze

Der anschliessend freigegebene Block pruefte
**`fe6f01415ed8fc988d189cb62570deb177346c7d` plus genau zwei
Browser-Profilanpassungen** in `scripts\Test-Ui.ps1` und
`src\Cohors.Web\playwright.config.ts`. `-Profile SummaryReport` waehlt
das Projekt `summary-report` mit genau dem C12-Browserfall. Unfertige
B-10/E-02-Quellen waren gesichert ausserhalb der Build-Eingaben geparkt;
alle erfassten Quelldateien blieben bis zum Ressourcenabschluss unveraendert.

| Beleg unter `.local\summary-acceptance-01` | Tatsaechliches Ergebnis / SHA256 |
| --- | --- |
| `source-01.json` | Vollstaendige Quellhashbindung; `f26f32ae62a82142702dad7be89a4d5486dc5c6c1a7ec5b1411565a36e5aa558` |
| `dotnet-build-01.log` | Neuer Testprojekt-Build: **Exit 0, 0 Warnungen, 0 Fehler**, 77,31 s; `d5210002cd2df8f1b752acb2ddf3ae508740c4071c82d6839051f36127cdbf5a` |
| `summary-01.trx` | **9/9, keine Skips**: fuenf `SummaryAgreementRuleTests` und vier `SummaryReportingTests`; `77d6de4f3868a8f62647516de77198c96eb362afa93975d03c63c202afbf4d61` |
| `browser-01\e2e-results.xml` | **1/1 SummaryReport, keine Skips**; `0f23991826cad0a32b2200b829317483bdd41914b82fbb6b7590999404e212ef` |
| `browser-01\browser-results.json` | Isolierter eigener SQL-/API-Pruefhost auf 5197, `PhysicalMode=Disabled`; `863032fe5135b5a39cd5bf4a882f94ad5c2c35c523caa7c5f7e00432e9cc4528` |
| `browser-01\cohors-periodic-summary.json` | Tatsaechlich authentifiziert heruntergeladenes Simulatorpaket; `b0edb2a1141b877319d3b52462b80106b6d1f66356c7ada63b2c7e99cf195961` |
| `browser-01\cohors-periodic-summary.png` | Gepruefter echter Browserscreenshot; `3dc730a3b964db4d4a2e988cb3459abeabb01158092b8df761c77d1ee7d835cd` |

Die Regelfaelle decken 1/7/366 Tage, spaete Vereinbarung und das am
Ablauftag endende Intervall ab. SQL und Browser pruefen die abgewiesene
unbrauchbare Vereinbarung; der Browser fuehrt die minimale nutzbare
Geltung mit gueltigem Folgetag durch, einschliesslich zwei verschiedener
Medical-/RA-Identitaeten, Rollen-/Produktscope-Abweisung, Simulation,
FrozenJson-Hash und unveraenderter Einzelakte/offener Fristen.

Vite wurde vor dem C#-Build erfolgreich ausgefuehrt; seine
Chunkgroessenwarnung bleibt sichtbar. Privates SDK 10.0.404 und CLI-Home,
beide First-use-Flags `false`, keine Wiederherstellung oder globalen
Aenderungen. Die sechs Testassemblyhashes stehen in `build-01.json`;
die fuenf API-Assemblies im eigenen Pruefpaket stimmen damit ueberein.
`publish-verified-01.json` bindet alle 16 Paketdateien, einschliesslich
sechs komprimierter Webdateien, deren dekomprimierte Bytes mit den
aktuellen Vite-Ausgaben verglichen wurden.

Zwei **nachgelagerte Pruefskriptfehler**, keine fehlgeschlagenen Fachtests,
sind erhalten: der erste Paketvergleich erwartete faelschlich eine
Vite-Quelldatei `index.html.br`; die erste Ressourcenpruefung hatte
`Invoke-RecoverySql` nur im untergeordneten Skriptscope geladen. Die
engen korrigierten Verifikationen sind getrennt dokumentiert; kein
erneuter Build, SQL-Testlauf, Browserlauf oder Publish war erforderlich.

`terminal-cleanup.json` bindet den eigenen terminalen Abschluss: keine
Testdatenbanken, keine neuen Fixtureverzeichnisse, kein 5197-Listener,
keine erfassten eigenen Restprozesse; private Zugangsdaten, Objekte,
Browser-Runtime und CLI-Home entfernt. Die drei vorher bestehenden
Fixtureverzeichnisse blieben erhalten. Vorher-/Nachhervergleich:
**66 Demoaggregate samt fachlichen SQL-Historien/Transportdaten,
48 Objekte, 16 veroeffentlichte Paketdateien und Konfiguration unveraendert**;
5187 bleibt bei PID 72076 bereit (HTTP 200). Nur betriebliche
Operations-Zeilen sind wie zuvor vom Gleichheitsvergleich ausgenommen.

Dies schliesst den geprueften Sammelbericht-Baustein, **nicht** den
FSCA-basierten Zulassungszweig oder den Gesamtpiloten. Kein 8/8-Preview,
keine Promotion von 5187, keine Recovery-, separate Host-, Last- oder
Cloud-Abnahme. **14 Recoveryfaelle/11 Fixturechecks bleiben unausgefuehrt.**

## Historischer Preview-Schnitt ee13e91

Die zuvor veroeffentlichte 5187-Vorschau war an den sauberen lokalen
Checkpoint `ee13e91fd6909dba61b3e70fe53f644566368067` gebunden.
Der isolierte Windows-Browserlauf `.local\ui-replay-ee13e91` bestand mit
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

## Historische BS-/Recovery-Integration vor 2a29196

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
  Erzeugung und Bearbeitung. Die unmittelbar zuvor genannten Arbeitsbaum-
  Laeufe mit 122/122 und 6/6 liegen vor dieser engen Haertung. Die oben
  gesondert dokumentierte saubere 2a29196-Wiederholung enthaelt sie und
  ersetzt ee13e91 als veroeffentlichtes Preview-Paket, nicht als Recovery-
  oder PhysicalRetention-Nachweis.

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
| T18 Audit/Retention | Begrenzter L03-/Diagnostikblock: 17/17 gezielte SQL-Faelle sowie getrennt 1/1 AuditExport und 1/1 isolierter PhysicalRetention-Browserfall; keine Vollsuite. Historische I/O-Ursache und aktuelle Recovery offen; 5187 bleibt Disabled |
| T19 Simulatorgrenze | Keine echten Melde-/Klinikadapter; weitere Export-/Endpoint-Pruefung offen |
| T20 Wiederherstellung | Historischer 13er-Windows-Lauf mit echten SQL-/Objekt-/Journal-Cuts bestanden. d0e864dc-Fixture als c906641 integriert und nach zwei identitaetserhaltenden xUnit-Analyserkorrekturen typgeprueft; neue 14er-Selektion/11 Abschlusschecks einschliesslich PSUR/BS weiterhin ohne Lauf. Aktuelle Wiederholung sowie Linux-/Cloud-DR offen |

Die vom Auftraggeber uebergebenen unabhhaengigen Referenzen liegen unveraendert in `tests\Cohors.Tests\Fixtures\cohors-acceptance-references.json`. Deren ursprüngliches `applicationVerified: false` dokumentiert die Herkunft des Referenzsatzes; nur ausgefuehrte Anwendungstests liefern den zusaetzlichen Nachweis.

## Prozessleiste, Dokumentdialoge und Kritikalitaet: geprueft und auf 5187 aktiv

Die fallversionsgebundene Leseprojektion unterscheidet erneute/fehlgeschlagene
Analyse, Assurance, aktuelle Paketfreigaben, Unknown, Nichtmeldeweg und
Nachverfolgung. Sie erteilt keine Freigabe und fuehrt keine Meldung aus.
Ein gemeinsamer Dialog liest die ausgewaehlte Fall- oder Bibliotheksrevision
autorisiert aus dem Objektbestand. SQL-Text, Referenzhash und gelesene Bytes
muessen zusammenpassen; gespeicherte Texte werden nicht als komplette PDF- oder
Word-Originale ausgegeben.

Fristenklasse und expliziter Patientenausgang sind getrennt sichtbar. Aktuelle
menschliche F-Kodierung und medizinisch freigegebene strukturierte MIR-Angaben
bleiben von historischen, unbelegten oder veralteten Angaben unterscheidbar.
Die gemeinsame Fristenklasse Tod/schwere Verschlechterung, Freitext und
KI-Verdacht erzeugen keinen Todesfallnachweis. Quellen, Bibliothek und Dialog
zeigen dieselben verstaendlichen Kategorien aus dem gespeicherten `Evidence.kind`.

Auf **27672ae62** bestanden siebzehn SQL-freie Prozess-/Ausgangsfaelle und
vier gezielte SQL-Faelle gemeinsam **21/21**, ohne Skips. Zwei
`CaseDocuments`-Browserfaelle bestanden **2/2** fuer Prozessnavigation,
Fehlerstatus, Claimbezug, historische Revision, schmale/lange Ansicht, Fokus,
reale Scope-Verweigerung und verspaetete Antwort.
Der bisherige unveraenderte private fe6-Paketstand dient nur als Vorheransicht;
er wurde nicht als neuer UI-Erfolg ausgegeben. B10-Arbeit ist geparkt;
Meldungsvorbefuellung wurde danach separat integriert und geprueft (siehe unten).
Strukturierte Outcome-Quellen werden nebeneinander mit ihrem jeweiligen
Pruefstand gezeigt: fehlendes F02 verdeckt kein medizinisch freigegebenes
MIR-Death, und historische/veraltete F02-Angaben ersetzen nicht den Pruefstand
eines neueren MIR. Mehrere F-Folgen gelten nicht automatisch als Widerspruch.
Auch ein neuer Entwurf ohne MIR laesst die explizite Angabe einer frueheren
MIR-Fassung sichtbar, jedoch als historisch und nicht aktuell freigegeben.

Alle folgenden Dateien liegen unter `.local\case-documents-01`:

| Beleg | Tatsaechliches Ergebnis / SHA256 |
| --- | --- |
| `build-02-result.json` | TypeScript/Vite, C# und Publish Exit 0; C# 0 Warnungen/Fehler; `c964f66462b3f1fa6bb47dd85ecf3ffb33100109eedaef8fdc4a757d2bc8584f` |
| `sql-02.trx` | 21/21; `736de64ab3a50a448c291080cbbbe099710ae2a89c37ac3baad66931fd9a70b8` |
| `after-03\browser-results.json` | 2/2, sauberer gleicher Quellstand; `5b2afdf323ca7396bca954d757c7ea173913969df4a882dc4660a7bc51c229c4` |
| `after-03\e2e-results.xml` | `c25796a1da02c548cb4c162daf8765ef8045e90a4a3d2ee1b3bdf22d8097512a` |
| `promotion-result.json` | Laufendes neues Paket, Prozessendpunkt, HTTP-Assets und Erhaltungspruefung; `e8adfd3dc580d628303751ebecba23b5f82c63e28ae64c41a8eb7bedda7d5eb2` |
| `preview-live-browser.json` | Eigener echter Edge-Kontext, vier Screenshots, keine Fachkommandos; `ab48a55d9d46094ec976049dbbe1340226120300ae08cde6c2ab9390337899f0` |
| `test-resources-terminal.json` | 0 Testdatenbanken/aufgezeichnete Testprozesse/5197-Listener, private Test-Runtimes und CLI-Home entfernt; `db7fab4515116a87e7b6fb7159a7b9420c985c49f8d82aa7d0e93bf42fa31fe3` |

Die Promotion verwendete genau ein neues finales Paketverzeichnis und keine
Migration, Seeds oder Bestandsruecksetzung. Schema 2, Einstellungen, Konten,
vorhandene Schluessel und Objektbytes blieben erhalten. Drei schon vorher
vorhandene Fixtureverzeichnisse und die geparkte B10-Quelle blieben unberuehrt.
Nur der neue gewollt weiterlaufende Vorschauprozess samt privatem Start-CLI-Home
bleibt aktiv. Der Nutzerbrowser wurde weder navigiert noch neu geladen.
Spaetere Nutzereingaben sind kein Grund fuer eine Datenruecksetzung.

Erstbefunde bleiben getrennt erhalten: `before-01` scheiterte erwartungsgemaess
0/2 an den im alten Paket fehlenden UI-Funktionen. Beim ersten neuen Build waren
alle nativen Exits 0; allein der Nachpruefer behandelte die erzeugte
`tsconfig.tsbuildinfo` falsch als veraenderte Fachquelle. Seine Korrektur und der
spaetere neue Build sind getrennt gebunden. `after-01` bestand 1/2: das volle
Zitat kam berechtigt sowohl im Claim als auch im Zitatblock vor; der Selektor
wurde auf Zitatblock plus exakte Fundstelle praezisiert, nicht der Inhalt gekuerzt.

Zwei Umschaltversuche blieben ebenfalls negativ erhalten: die unmittelbare
Portfreipruefung brach vor einem neuen API-Start ab; spaeter forderte ein
PowerShell-Nachpruefer wegen einer verschachtelten REST-Collection versehentlich
17 IDs als einen Fall an (404). Beide Male wurde nur das unveraenderte alte
Paket wieder gestartet, niemals SQL oder Nutzeraktionen zurueckgerollt.
Begrenztes, eigentuemergebundenes Listener-Warten und eine vorab per GET
gepruefte einzelne Fall-ID korrigieren diese Betriebspruefer. Auch die
unpassenden Rollen-Selektoren der separaten Live-Screenshotpruefung bleiben
erhalten; die sichtbare Tabellenueberschrift wird nun als exakter `th`-Text
geprueft. Das benoetigte keine Aenderung der geprueften Anwendung.

Dies ist keine neue 122er-/10er-Gesamtsuite, kein Lauf der 14 Recoveryfaelle,
kein getrennter Host-/Last-/Cloudnachweis und keine medizinische oder
regulatorische Betriebsfreigabe.

## Agentengestuetzte Meldungsvorbefuellung: eigener gepruefter Stand

**BcHG auf d16665832:** Neuer TypeScript-/Vite-Build, neuer C#-Build des
kleinsten bestehenden Testprojekts und Publish bestanden. Gemeinsam liefen
**46/46 ohne Skips**: 37 SQL-freie Regel-/Prozessfaelle und neun SQL-Faelle
fuer Vorbefuellung, MIR und Autoren-Idempotenz. Enthalten sind die gemischten
Annex-F-/medizinischen MIR-Ausgaenge, alle fuenf unterstuetzten EU-/NI-Ziele,
Initial/FollowUp/FinalRep, bedingter Final-Riskreview, Similar-Incident-
Zeitraeume/Nenner und die tatsaechlich zusammengesetzten Feldlaengengrenzen.
GB wurde gesondert nur als GB-SIM-Narrativ geprueft, nicht als GB-MIR.

Der echte isolierte `V01`-Browserfall bestand **1/1**: PMS beauftragt den
vorausgefuellten Entwurf, liest Behoerde/Simulator, Pflichtluecken,
Feldherkunft und verifizierten Originalbeleg. RA hat keine verpflichtende
Datenerfassung und gibt konkrete Luecken zurueck. PMS korrigiert belegte
synthetische Fakten und bestaetigt den Folgetermin; die Agententexte bleiben
unveraendert. Danach pruefen Medical und RA mit genau zwei verschiedenen
Identitaeten. Das Paket wird eingefroren, aber nicht automatisch gesendet.
LegalDue/SafetyDue bleiben unveraendert; Desktop und 390-Pixel-Ansicht wurden
als Dateien erfasst und gelesen. Das ist keine neue 11/11-Preview-Suite.

Fuehrend uebernommen werden vorhandene Fallreferenz/-beschreibung/-land,
Organisationskenntnis, bekannte Ereigniszeit, Produktmodell, Basic-UDI,
bekannte UDI/Charge und freigegebene Herstellerstammdaten. Kausalitaets- und
Untersuchungstexte sind gekennzeichnete, an Claims/Fundstellen gebundene
Agentenvorschlaege, keine bestaetigte Ursache oder ausgefuehrte Massnahme.
Nicht belegte Ereignisangaben, Kontakte, Kodierungen und menschliche
Terminzusagen bleiben offen; sie werden nicht durch Platzhalter erfunden.
Eine neue Version uebernimmt keinen alten zugesagten Folgetermin.

Alle folgenden Dateien liegen unter `.local\report-prefill-01`:

| Beleg | Tatsaechliches Ergebnis / SHA256 |
| --- | --- |
| `build-03-verified.json` | Native Web-/C#-/Publish-Exits 0, Assemblies und entpackte Assets gebunden; `b4f21a566d2bfc9717b7f7865fdbd9f2939702bae0f7cc0a77ac2cac5ecee005` |
| `tests-02.trx` | 46/46; `a1101e130fa62b65cacc2d5b2b239ff3a1dd7d5edb5b95854092db35bae97ce5` |
| `browser-02\browser-results.json` | 1/1, eigener SQL-/API-Lauf; `545b7575edb8974310e71fdafba316ff8cca7945383d14736523fd838365e6dc` |
| `browser-02\e2e-results.xml` | `1f45c7be37bff54d7cbcebeac1b97c3774deb7b6cc605fa19db2fca2c73c4890` |
| `compatibility-current-verified.json` | Derselbe lesend erfasste Bestand mit alten/neuen Typen; `5304f6bfaddaff0d40e8a9d14abe49080d37c21b32f8676189b90bdb1c5455c0` |
| `promotion-result.json` | Tatsaechlich laufendes Paket, Prozess/HTTP-Assets und Erhaltung; `e32a7b7e2334279c03ab12886f78dae28ce57b68eec409dfcb55b2310b3df8cf` |
| `preview-live-browser.json` | Eigener echter Edge, zwei Screenshots, nur lesende Fachzugriffe; `2f8410866abf1cb63fddfbb807ac44fa8cc6b382120550ecff12fb362838c721` |
| `test-resources-terminal.json` | Exakter eigener Ressourcenabschluss; `2ddf071ea202b5bb5e8b3f52125f3f2d80bc6cd76f9e467a01c22e69b7727771` |

Der Browser-Rohbeleg behaelt sein `sourceTreeDirty=true`: Der Build hatte die
getrackte `tsconfig.tsbuildinfo` erzeugt. Die 178 fachlichen/Pruef-Quelldateien
des sauberen Buildinputs, des Browserinputs und des wieder sauberen
Quellcheckpoints wurden danach hashgleich nachgewiesen. Das ist kein
nachtraegliches Umschreiben des Browserergebnisses.

Erstfehler bleiben erhalten: `tests-01` bestand 35/36; der neue
Unknown-Referenz-Test hatte die obligatorischen FollowUp-Aktions-/Planfelder
nicht geliefert. Die Fixture wurde vervollstaendigt und eine eigene
Pflichtlueckenregression ergaenzt. `build-02` scheiterte an xUnit2030 in
einer neuen Assertion; die exakte Praedikatsassertion ersetzt den Where-Aufruf.
Bei `build-03` waren alle nativen Exits 0, aber sechs alte, gehashte Vite-
Dateien lagen noch im unpromoteten Kandidaten. Nur diese nachgewiesenen
Altreste wurden entfernt und alle aktuellen/komprimierten Assets erneut
gegen den Build geprueft. `build-03-result.json` bleibt deshalb negativ;
erst `build-03-verified.json` bestaetigt das bereinigte Paket.

Die lesende Promotionsvorpruefung stoppte zunaechst an einer optionalen
Null-MIR-Metadatenprojektion und danach an einem historischen Scope.
Der zweite Befund wurde nicht durch Datenreparatur umgangen: Auf demselben
Bestand ergeben `27672ae` und `d166658` fuer **17 Faelle und drei
Berichtsversionen** identische typisierte Daten, Scope-Berechnungen und
eingefrorene Inhalte. Diese drei Bestandsberichte enthalten kein MIR-Artefakt;
MIR-Altvertraege mit/ohne optionale Metadaten sind nur durch die gezielten
Regeltests abgedeckt, nicht durch einen vorhandenen Demo-XML-Bestand.
Zwei schon vorher nicht zum aktuellen
Lesemodell passende historische Scope-/Frozen-Serialisierungsstaende bleiben
offen; weder Signaturen noch Original-Payloads wurden normalisiert oder
neu freigegeben. Die Vorpruefung bindet diese Alt/Neu-Gleichheit ausdruecklich.

Erst danach wurde der frisch identifizierte eigene 5187-Prozess ersetzt.
Genau ein finales, unveraenderliches Prefill-Verzeichnis wurde durch
Verschieben des geprueften Kandidaten angelegt. Schema 2, Demo-SQL,
Einstellungen, Konten, sieben vorhandene Schluessel und 48 vorhandene
Objektdateien blieben erhalten. Kein Seed, keine Migration, kein
SQL-/Nutzerdatenrollback und keine gemeinsamen Docker-Eingriffe.
Die vier eigenen Testdatenbanken und vier aufgezeichneten Test-PIDs wurden
terminal als abwesend geprueft; 5197 ist frei. Beide Test-Runtimes, das
Test-CLI-Home und die privaten lesenden Kompatibilitaetssnapshots sind
entfernt; die bereinigten Belege und Erstfehler bleiben erhalten.
Nur die gewollte Vorschau **PID 90260**, ihr privater Startprozess und
`promotion-cli-home` laufen weiter. PID-Angaben sind Zeitpunktnachweise,
keine Erlaubnis fuer einen spaeteren blinden Stop.

`preview-live-prefill-review.png` zeigt einen unveraenderten historischen
Demobericht mit ehrlichem Altstandhinweis. Es wurde dafuer kein neuer
Demobericht erzeugt. Die `browser-02\cohors-prefill-*`-Ansichten zeigen dagegen
die tatsaechliche neue Vorbefuellung/Rueckgabe/Freigabe im isolierten Test.
Der Nutzerbrowser blieb unangetastet; nach eigenem Neuladen aktiviert eine
neue Berichtsversion die Vorbefuellung.

XSD-Konformitaet, implementierte Pilot-Pflichtpruefungen und nationale
Versandbereitschaft bleiben getrennt. ANSM verlangt PDF **und** XML;
dieser Pilot weist kein solches nationales Versandpaket nach. GB-SIM-1
ersetzt nicht GB 7.2.1GB, NI-MORE-Import ist nicht nachgewiesen, nationale
Kanal-/Sprachdetails bleiben teilweise offen. Es gab keine Live-KI,
echte Meldung, Recovery-/Last-/Native-Parser-/separate-Host-/Azure-Abnahme
oder neue medizinische/regulatorische Betriebsfreigabe. B10 war in diesem Pruefstand geparkt.

## B-10/E-02: gesicherter Folge-Quellcheckpoint, nicht ausgefuehrt

Die geparkten Untersuchungsquellen wurden auf dem ausgelieferten Prefill-
Quellstand getrennt weitergefuehrt: Asservatenherkunft/-zustand, interne
Medical-/RA-Prueffreigabe, eigenstaendige vorherige simulierte
Behoerdenunterrichtung bei moeglicher Beeintraechtigung, synthetischer Befund,
Verzicht und historische Originalexporte. Die Unterrichtung ist ausdruecklich
keine Behoerdenzustimmung; eine nichtbeeintraechtigende Methode bekommt keine
pauschale Behoerdengenehmigungspflicht.

Vorbereitet sind sieben SQL-freie `SpecimenInvestigationRuleTests` und drei
`SpecimenInvestigationTests` mit SQL-Fixture. Sie adressieren Reihenfolge,
getrennte Identitaeten, Scope-/Originalbelegmanipulation, geaenderte
Fallgrundlage, keinen Scheinabschluss, Versions-/Idempotenzbindung,
unveraenderte Fristen, Variantenhistorie, den gespeicherten Audit-Quellhash
einer aelteren JSON-Form sowie falsche Objektbytes und Scope-Verweigerung.
Das waren in diesem Quellblock **Testquellen, keine zehn bestandenen Faelle**. Es gab in diesem
Folgeblock keinen SDK-/Build-/SQL-/Docker-/Browser-/Lastlauf und keine
Promotion. Ein eigener B10-Workbench-/Browserfall bleibt nach der neuen
Cockpit-Priorisierung offen. Die spaeter tatsaechlich ausgefuehrten zehn
Backendfaelle sind unten dem eigenen Cockpit-Pruefstand zugeordnet.

Die laufende d166658-Demo, 27672ae/2a-Rueckfaelle, Konten, Daten, Schluessel
und Nutzerbrowser bleiben unveraendert. Die beiden historischen
Scope-/Frozen-Abweichungen wurden nicht normalisiert oder neu signiert.
Der gesonderte Infra-Commit 09e166f wurde konfliktfrei als 3774627 integriert:
nur 52 Dokumentationszeilen in `azure-infrastructure.md`, Produktionsziele
statt Istnachweis; keine Runtime-/Deploymentaenderung.

## Cockpit/Kalender: eigener Quellcheckpoint und gezielte Pruefserie

Neue Quellen: berechtigungsgebundene SQL-Leseprojektionen, gemeinsame
Prozessableitung, Fallzugangsdiagramm, exakte Risiko-/Pruefstand-/Prozesszahlen,
KPI-Drilldowns und Monatskalender mit Agenda/Quellnavigation. Es werden keine
fehlenden Risikogruppen oder Termine erfunden und keine Fristen neu berechnet.

Tests04 umfasst neun SQL-freie `WorkspaceReadRuleTests` (einschliesslich
beider DST-Tageslaengen) und drei `WorkspaceReadSqlTests`. Sie pruefen
Fallzugang statt Kenntnis/Ereignis, Umfang/Produktberechtigung, unbekannte
Risikogruppen, menschlichen Override und veraltete Analyse, aktuellen
Medical-/RA-Stand, Original-Nichtmeldeweg, genau einen Pflichtzaehler trotz
zweier Marker, echte Monats-/UTC-/DST-Grenzen, reine PSUR-Kalendertage,
historische Fassungen, BS-Runden sowie paginierte Quellen ohne Snapshottext.
Ein legitimer Prozessfilter bleibt auch nach dem Wechsel des letzten
Falles, bei null Medical-Treffern und bei leerem berechtigten Bestand
gueltig; nicht definierte Schluessel werden weiter abgelehnt.

Das neue isolierte Browserprofil `CockpitCalendar` umfasst exakt vier Faelle:
`D01`, `D02` und die beiden eng betroffenen bestehenden Regressionsfaelle
SQL-backed complaint und `T10`. Die neuen Faelle pruefen Zahlen gegen
gespeicherte Fallauswahl und gemeinsame Prozesssicht,
serverseitige Scopeverweigerung, Grafik-/KPI-/Fallnavigation ohne
zusaetzlichen Vollabruf, echte PSUR-/BS-Quelltermine und UTC-Monatswechsel
bei abweichender Browserzeitzone, konkrete Serien-/Rundenauswahl, keine
Fachmutation durch die Oberflaeche und exakt 31 Zellen im Januar 0001 ohne
die implizite 1900-Korrektur von `Date.UTC`. Die Regressionen pruefen den
realen Freigabe-/Quittungsweg mit offener Folgepflicht und den Dublin-Tag
als Original neben seiner ausdruecklichen Berliner Kalenderanzeige.
Das Profil verwendet eine eigene SQL-Fixture;
wegen seiner ausdruecklich angelegten Leitserie ist es vom unveraenderten
elfteiligen `Preview`-Profil getrennt, nicht dessen Ersatz.

### Historischer Zwischenstand vom 16.11.2026 vor Browser05

**Zu diesem Zwischenstand noch keine neue Bereitstellung.** Die folgenden
Negativbelege bleiben unveraendert; der spaetere Gesamtpass und die
Bereitstellung sind im naechsten Abschnitt getrennt dokumentiert. App-Quelle
`4b0442680e3c20836908fbfd8c9c8062eaa53e34` ist mit Build04 gebaut und mit
**44/44 gezielten Domain-/SQL-Faellen, ohne Skips**, geprueft. Enthalten
sind die zwoelf Workspace-Faelle, sieben B10-Regel-/drei B10-SQL-Faelle,
17 Prozessfaelle, vier Sicherheits-/Freigaberegressionen und ein
Follow-up-Fall. Das ist keine neue Vollsuite und keine B10-Workbench-Abnahme.

Die spaetere Browserquelle
`3a1f0153ccd82d11a4e0bc6780e142b97121e507` aendert ausschliesslich elf
Select-Lokatoren zu exakten Combobox-Rollen/Namen. App-/Build-/C#-Eingaben,
sechs Testassemblies und die 16 protokollierten Paketdateien wurden vor
Wiederverwendung hashgleich geprueft. Build04 und die 44 Ergebnisse sind
**weiter passende fruehere Belege, keine erneut ausgefuehrten Laeufe**.
Die Wiederverwendung ist jeweils in `browser-03-binding.json` bzw.
`browser-04-binding.json` getrennt an App- und Browserquelle gebunden.

| Beleg unter `.local\cockpit-calendar-01` | Ergebnis / SHA256 |
| --- | --- |
| `build-04-result.json` | Web/TypeScript und C# erfolgreich; `3279069f90794bd35c9082b38dfadc830911abfc220f203a24f0ab37a2f4d916` |
| `build-04-source.json` | App-/Build-/Testeingaben; `c1269662d09a2df9eff4eae7e1ef2630e6183bfa48af4564a6bfe8e768548415` |
| `tests-04.trx` | 44/44, keine Skips; `744f9dd9ec9db9d34decb5b16ec0d8def983d2416ce1f4836e978c0e880b5fc8` |
| `browser-03\e2e-results.xml` | 3 bestanden, T10 fehlgeschlagen, 0 Skips; `2555e66d259f895182e89361ee3542244b872649949827b6e95b0086275bc78e` |
| `browser-04-bounded-result.json` | Fehlgeschlagen, 600-s-Aussengrenze erreicht, Streams noch offen; `19b3c49f21ba3afb71f242a695b616afe67e96829b1e8498d55166129d4bf8e7` |
| `browser-04\private-browser.log` | D01 negativ, nur D02/SQL-Workflow/T10 reporter-ok; keine abgeschlossene JUnit; `438a894c791fd551c82009f03148c15935a10d49e0a47c9fb180488ad72871e5` |
| `browser-04-independent-cleanup-result.json` | Gesonderter begrenzter Cleanup erfolgreich; kein nachtraeglicher Browserpass; `7c0b987ee4f40ffe8be612250e98bc5b260ae5a875ffdaa894ed16b56fc75ee7` |
| `browser-04-bounded-cleanup.json` | Erfasste PIDs, genaue Testdatenbank, Zugangsdaten, Objekte und beide privaten Browserprofile abwesend; `adabcb271903f3978f73ab8d101bc4309db61460f61bac55172ef5578d0a18b1` |

Die vorherigen negativen Belege bleiben erhalten: Browser01 fand wegen
des am vollqualifizierten Titel falsch verankerten Profilfilters keine
Tests. Browser02 fuehrte alle vier Faelle aus, scheiterte aber an exakten
Label-Abfragen verschachtelter Selects. Die Korrekturen veraenderten
weder Fachassertionen noch Falltimeouts. Die abgebrochenen Build03-/Verify05-
Anlaeufe sind separat protokolliert und nicht als erfolgreiche Laeufe gezaehlt.

Browser03 loeste in T10 nach 90,010 s bereits bei `context.newPage()` vor
Navigation aus; der gesamte Ergebnis-/Browserabbau dauerte 24,7 Minuten.
Browser04 pruefte T10 danach erfolgreich in 6 s, hatte jedoch einen eigenen
D01-Negativbefund und erreichte beim Browserabbau die neue Aussengrenze.
**Erfolge verschiedener Versuche werden nicht zu 4/4 zusammengesetzt.**
Der konkrete D01-Fehlertext wurde vor dem abgebrochenen Suiteabschluss
nicht persistiert; daraus wird kein vermuteter App-Fehler abgeleitet.
Eine zwischenzeitliche Aussage, alle vier Faelle seien reporter-ok,
war falsch und wurde anhand des vollstaendigen Logs ausdruecklich korrigiert.

Der erste Cleanup04 blockierte sicher an einer noch vorhandenen erfassten
Node-PID. Nach deren eigenstaendigem Ende bestaetigte ein unabhaengiger,
auf 60 s begrenzter Cleanup alle sechs erfassten PIDs und beide Profile als
abwesend und pruefte die SQL-Abwesenheit der genau zugeordneten Testdatenbank.
Es gab keinen Prozessabschuss auf ungeklaerte Identitaet, keine globale
Browser-/Richtlinienaenderung und keinen Eingriff in den Nutzerbrowser.
Der private Port 5197 wurde danach als frei geprueft; 5187 blieb auf d166658.

Der urspruengliche gebundene Supervisor blieb unveraendert. Eine getrennte
Helferversion fuehrt Cleanup auch bei einer Ausnahme des Ausfuehrungsabschlusses
in einem unabhaengigen `finally` aus und erhaelt beide Fehler getrennt.
`browser-boundary-check-02.json` belegt den echten eigenen Sleep-Prozessstopp
und drei rein synthetische Fehlerpfade, nicht einen neuen Browserlauf.
Auch die verstaerkte Bestandssicherung ist nur offline geprueft:
`preserved-gate-offline-01.json` enthaelt acht bestandene synthetische Faelle.
Jede aktuelle Zeile muss ihrer eigenen Kind/Id/Version-Historienkopie
entsprechen, gleiche Versionen zusaetzlich dem Capture; legitime neuere
Versionen bleiben erlaubt. Alt/Neu-Vergleich ist fuer getrennte frische
PowerShell-Prozesse mit tatsaechlich geladener Domain-Location/SHA und
identischem Helferhash vorbereitet, **noch nicht am Livebestand ausgefuehrt**.

Vor einem weiteren Browserlauf sind eine neue ausdrueckliche Laufzuteilung,
persistente Fehlerdetails beim Testende und die begrenzte Abbau-/Cleanup-
Strategie erforderlich. Ein separat gebundener Chromium-Gegenversuch
waere eine Umgebungspruefung, kein Beweis einer Edge-Ursache; das von
Playwright 1.56.1 erwartete Chromium-1194-Binary fehlt am abgefragten
Standardpfad. Zu diesem Zwischenstand wurde kein Chromium gestartet oder installiert.
Erst nach erfolgreicher gebundener Browserabnahme darf der frische lesende
Alt/Neu-Abgleich heutige Faelle, PSUR-/BS-Fassungen und Historien fuer eine
Promotion pruefen. Die offene B10-Workbench sowie Pflichtfeld-
Vervollstaendigung, Informationsnachforderungs-/Antwortzuordnung und
Reviewhistorie/Neubewertung sind damit nicht erledigt.

### Browser05: vollstaendiger Chromium-Gesamtpass und kontrollierte Bereitstellung

Am 16.11.2026 wurde der ausdruecklich zugeteilte Gegenversuch mit
`COHORS_BROWSER=chromium` durchgefuehrt: Playwright **1.56.1**, zugehoeriges
Chromium **1194 / 141.0.7390.37**, session-privater Cache. Die tatsaechliche
Startzeile, der ausfuehrbare Pfad, PE-Version und SHA256 sind gebunden.
`chrome.exe` hat SHA256
`153106834fb06976c587c39aaef0e8c0d1b1339a575e8d7b87f187058663a094`.
Keine globale Browser-/Richtlinienaenderung, kein Playwright-/Lockfile-Upgrade
und keine nachtraegliche Edge-Abnahme oder bewiesene Edge-Ursache.

Die fehlende Binary wurde zuerst als echter Abhaengigkeitsfehler erfasst.
Zwei gepinnte CLI-Bereitstellungen endeten unvollstaendig; ihre Belege bleiben
negativ. Die native Entpackung genau des protokollierten offiziellen ZIPs
hinterliess alle 249 Dateien, ihr kombinierter Entpack-/Pruefschritt erreichte
jedoch seine 60-s-Grenze. **Erst der anschliessende, getrennt gebundene rein
lesende Vergleich aller 249 Dateien** bestaetigte Laenge und SHA256 gegen
jeden ZIP-Stream, einschliesslich der vollstaendigen `chrome.dll`.
Es wurde kein Installer-Erfolgsmarker erfunden. Der nachgewiesen fehlende
`winldd-1007` wurde bytegleich aus seinem bereits bestehenden Cache in den
privaten Cache kopiert, ohne das Original zu aendern. Das ist eine explizite
native Abhaengigkeitsbereitstellung, kein behaupteter CLI-Erfolg.

App **4b0442680e3c20836908fbfd8c9c8062eaa53e34 / Build04** und die
historischen **44/44** Ergebnisse wurden nach erneuter Hashpruefung
wiederverwendet, **nicht erneut gebaut oder als C#/SQL-Suite ausgefuehrt**.
Browser-Testquelle bleibt **3a1f0153ccd82d11a4e0bc6780e142b97121e507**.
Der tatsaechlich ausgefuehrte Workspace war
**4ceffc7951f9d752430771cab94ae5e9775c91c6** mit ausschliesslich
`docs/validation.md` als weiterem Unterschied. Diese drei Identitaeten
sind ausdruecklich getrennt; alle Fallassertionen und Falltimeouts blieben gleich.

| Unveraendert ausgewaehlter, tatsaechlich bestandener Testtitel | Dauer |
| --- | --- |
| `D01: authorized cockpit counts, actual process drilldown, unavailable risk groups and read-only navigation` | 44,465 s |
| `D02: stored PSUR date-only and BS UTC month-boundary calendar, exact source navigation and whole-group authorization` | 51,422 s |
| `SQL-backed complaint → grounded assessment → distinct approvals → unknown/reconcile → verified audit` | 56,826 s |
| `T10: Dublin legal day remains Dublin legal day in a Berlin browser` | 4,574 s |

**4 bestanden, 0 Fehler, 0 Skips, 0 Retries** in einem einzigen Lauf:
vollstaendige JUnit und unabhaengig persistierte Test-Ende-/Suite-Ende-Ereignisse.
Der korrigierte `finally`-Supervisor hielt die unveraenderten Grenzen
600 s Ausfuehrung, getrennt 60 s Cleanup und 10 s Stop ein. Beide Phasen
endeten mit Exit 0, geschlossenen Streams und ohne Grenzueberschreitung.
Eigene API, aufgezeichnete Browser-/Worker-PIDs, genaue Testdatenbank,
private Zugangsdaten/Objekte und Browserprofil sind terminal bereinigt.
Der Nutzerbrowser `cohors-tryout` wurde nicht verwendet.

| Beleg unter `.local\cockpit-calendar-01` | SHA256 |
| --- | --- |
| `chromium-runtime-01.json` | `7e82a2c7fac53f9f78dd0c34caaf188887b815c42b355b5f53220a9e6bf2bbf7` |
| `browser-05-binding.json` | `16999c83bc5b8b0206cf2e6846fda3a938b6bab5f4dee0f63876462db1a80462` |
| `browser-05\e2e-results.xml` | `1a281f3dbceb6a9488c726d266fc54801cd9030cd13c796d230f4a1d7a71cc2a` |
| `browser-05\test-events.jsonl` | `ea9c042863e1824d1cea8649772d0501f61b96cbdcd5ab973f8734ae1e6bca44` |
| `browser-05-bounded-result.json` | `ea6d4f356c5ecb20b08ebc34f9f9a1652408c77451379208fb5a6f71e3c70a34` |
| `browser-05-bounded-cleanup.json` | `a275208e86c35c2181986e706b9268e604149c027b535bd7a3937e81e57038f2` |
| `compatibility-01-verified.json` | `632cb4b80d6a9f3bb86c9ac74d64f19b7646001c18883c9a9a3ed5deeb4d4f14` |
| `compatibility-01-preserved.json` | `30636de84ae294da12c17bf9d6623a78d91f1874c9266799a6ea553f8a5e2af3` |
| `promotion-preflight.json` | `007ec7b7828ed0e3192a17dcf6ad2e7479833dfc893a039ec4d25d2c03cad9a0` |
| `promotion-result.json` | `f98001d2e83b755a134acafb4f57e8de6c8264a69425b7c42ff2bc9caaf0834b` |

Danach wurde der echte aktuelle Bestand rein lesend erfasst:
**249 aktuelle/historische Zeilen**, darunter 17 aktuelle Faelle und zwei
PSURs, Schema 2, 30.415.374 gespeicherte JSON-Bytes. Alt und Neu verwendeten
denselben Capture und denselben Vergleichshelfer
`f04cc44490f5ad5fdc18d4584108706774fbdee5b9277b31ca53aa0d0a4451ff`,
aber getrennte frische PowerShell-Prozesse (107592/114528).
Die tatsaechlich geladenen Domain-Locations zeigten auf das ausgewaehlte
alte d166-Paket bzw. Build04; ihre Hashes waren
`abf7cc96c4abc4ab40da7029b1b61731d3a11d10b4802e47c5ceefaa37249546`
und `1b2aad26a77288fa45c65b8d2040f271bd589166f5c9dae61d2e3134d0c41f05`.
Es gab keinen zusaetzlichen typisierten oder Freigabescopedrift; die beiden
bereits im alten Paket bestehenden Warnungen blieben erhalten.

Unmittelbar vor der Umschaltung wurde derselbe Bestand erneut auf Aenderungen
geprueft. Anschliessend bestaetigte das verstaerkte Preserved-Gate jede
aktuelle Zeile gegen ihre eigene Kind/Id/Version-Historienkopie und jede
unveraenderte Version gegen den Capture. Neuere korrekt historisierte
Nutzerversionen bleiben zulaessig; nichts wurde normalisiert oder zurueckgesetzt.
Die Eigentuemerkontrolle ist unabhaengig auf 10 s plus 10 s eigene Stopgrenze
begrenzt. Nach einem erhaltenen negativen Preflight verwendet sie nun native
IPv4-/IPv6-Portzuordnung, Prozessabbild/Startzeit, Token-Owner-SID und exakte
Befehlszeile, ohne CIM/GetOwner, Elevation oder AccessDenied-Fallback.
Die native Variante wurde zuerst am eigenen Prozess und eigenen kurzlebigen
IPv4-/IPv6-Sockets nachgewiesen, danach vor und nach dem echten Start.

**Bereitgestellt unter `http://localhost:5187`:**
`.local\app-replay-cockpit-4b04426`, eigener PID 100836.
Alte API und alter Supervisor sind beendet; nur die neue Demo bleibt
absichtlich aktiv. Health, autorisierte neue Projektionen sowie die
tatsaechlich ausgelieferten HTML-/JS-/CSS-Bytes wurden bestaetigt
(17 Faelle, 34 Kalendereintraege zum Beobachtungszeitpunkt).
Alle Nutzerstaende/Historien, Accounts, Schluessel und Originalobjekte,
das originale Build04-Paket und die drei Rueckfallpakete sind erhalten.
Kein Seed, keine Migration, Datenkorrektur, Wiederherstellung oder echte Sendung.

Dieser Nachweis erledigt den KPI-/Kalenderschnitt, nicht die offene
B10-Workbench, Pflichtfeld-Vervollstaendigung, Nachforderungs-/Antwortzuordnung
oder Reviewhistorie/Neubewertung. Die fachliche Folgequeue bleibt sequenziell;
Load, Recovery und Cloud sind hierdurch nicht freigegeben.

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
