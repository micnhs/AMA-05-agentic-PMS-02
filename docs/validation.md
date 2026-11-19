# Nachweisstand und Abnahmegrenzen

**Arbeitsstand, kein Abschluss der Gesamt- oder Fachabnahme.** Dieser Nachweis wird mit den tatsaechlich ausgefuehrten Paketen fortgeschrieben.

## Natuerlicher Diagnoselauf 3c1dff: Originalergebnis

Der einmalige normale Push von
`3c1dff925af474c8e9bd67123c813e9c7267c7a7` erzeugte genau den
[Cohors-CI-Lauf 44512005639](https://github.com/micnhs/AMA-05-agentic-PMS-02/actions/runs/44512005639),
Attempt 1. **395/395 .NET und 39/39 synthetische Bootstrap-Faelle**
bestanden. Alle elf Browserprofile fuehrten ihre 32 Faelle aus:
**26 bestanden, sechs fehlgeschlagen, keine Skips oder JUnit-Errors**.
Appjob `135518337923`, Infrajob `135518338326` und Gesamtworkflow
sind negativ. Infra: 56 positive Offline-Vertraege, Checkov
186 bestanden / 77 fehlgeschlagen; kein Security-Gesamtpass.

L03 scheiterte in Preview und AuditExport sowie E04/E05 in
EudamedDossiers jeweils mit HTTP 409, `audit_command` und
`Auditereignis hat keinen passenden persistenten Command-Receipt.`.
Das belegt den zweiten Command-Receipt-Guard, nicht den dort zuerst
geprueften 1:1-Zaehler. Die konkret betroffene SQL-Zeile steht nicht
im Originalbericht. C12 erreichte in Preview und SummaryReport nach
dem korrigierten Empfaenger den separaten Fehler
HTTP 422 / `response_envelope`. Die lokale vollstaendige
Ruecklauffixture unten war nicht Bestandteil dieses Laufs.

Artefakt `14525805288` hat 1177508 Bytes und SHA256
`79078f3bfdd6d597e739d0d59d848f10ec7a3c4817296c5968fc27293a748304`.
Registry, Uploadlog, ZIP und alle 27 allowlistgebundenen Einzeldateien
stimmen ueberein. Alle 22 Originalreferenzen des ausdruecklich
abgeleiteten Aggregats, 32 Fallidentitaeten und Originalfehlertexte
sind erhalten. Elf Original-Terminalberichte bestaetigen ihre sieben
Ownership-/Cleanupflags und jeweils null Cleanupfehler. Private
Intent-/Journalinhalte und die unabhaengige Parent-/Container-
Ressourceninventur liegen im Artefakt nicht vor: keine v8/v14-Closure.
Die zusaetzliche Markerpruefung aller 25 Textdateien fand lediglich
zwei exakt quellengebundene synthetische Bootstrap-Negativtestnamen;
das private Credentialinventar des urspruenglichen Harness wurde
nicht nachtraeglich abgerufen. Originalbytes und Credentialguard
wurden nicht bereinigt oder veraendert.

Die getrennte Quellspur zeigt eine Vertragsinkompatibilitaet:
`CohorsOperationsWrite` speichert den Schluessel aus
`SHA256("cohors-operations-write-v1:" + RequestHash)` und die
Auditkorrelation `operations-<Schluessel>`. Der Auditleser rekonstruiert
bisher ausschliesslich den generischen `CommandContext`-Schluessel.
Ein rein synthetischer SHA-/JSON-Gegenbeleg zeigt unterschiedliche
Schluessel; er ist keine ausgefuehrte SQL-Reproduktion und benennt
keine nachtraeglich erfundene historische Fehlerzeile. Eine minimale,
streng writergebundene Lesekompatibilitaet ist unten lokal vorbereitet;
SQL-Writer, Rechte, Sicherheitsguards und Watchdog bleiben unveraendert.
Dieser Originalbefund ersetzt weder die b765-Negativen noch einen
ausgefuehrten Nachweis der lokalen Folgekorrekturen.

## Audit-Leserkandidat nach 3c1dff: Tests vor dem natuerlichen Lauf NotRun

Der lokale Kandidat erkennt den eingeschraenkten Writer ausschliesslich
an `ActorId == "cohors-operations"`. Fuer diesen Akteur sind der ordinale
Name `Cohors operations`, genau die Rolle `Workflow`, die drei
003b-Paare `case/DeadlineEscalated`, `psurReview/PsurQuestionEscalated`
und `eudamedDelivery/EudamedCorrespondenceEscalated` sowie die exakte
Korrelation `operations-` mit 64 kleingeschriebenen Hexzeichen zwingend.
Abweichungen ergeben `409/audit_command`, ohne generischen Rueckfall.
Alle anderen Akteure behalten die generische Schluesselformel, auch
bei einer mit `operations-` beginnenden Korrelation.

Die bereits 1:1-gepruefte Receipt-Auswahl wird bijektiv zugeordnet.
Akteur, Originalresultathash und `EqualsExact`-Zeitbindung bleiben
Pflicht. Das Korrelationssuffix muss zum 64-stelligen Lowercase-
`RequestHash` nach der unveraenderten 003b-Schluesselformel passen.
Fuer `case` und `psurReview` wird zusaetzlich der RequestHash aus Kind,
Aggregat-ID, invariant dezimaler Vorversion, PayloadHash und exakt `[]`
neu abgeleitet. `[]` ist der kanonische Aufruf von `SqlOperationsStore`;
abweichend formatierte leere Arrays werden bewusst fail-closed
abgewiesen. Fuer `eudamedDelivery` sind die urspruenglichen
Dependencies nicht persistiert: hier wird keine vollstaendige
Request-Neuableitung oder nachtraegliche Dependencypruefung behauptet.

Die Quell-/Fixture-Historienpruefung fand in 1079 erreichbaren
Textblobs nur zwei Fassungen desselben 003b-Writers mit diesem
Akteur; die weiteren Treffer sind Telemetrie und Testassertions.
Keine lokale Datenbank oder unversionierten Runtime-Daten wurden
dafuer gelesen. Manifest, Kette, History, Outbox, Inbox, Quellen,
Produkte, Fristen und der `operations`-Ausschluss bleiben unveraendert.
SQL-Writer, Migrationen, Rechte, Watchdog, Browserauswahl und
Ressourcen-/Zeit-/Cleanupgrenzen werden nicht geaendert.

`AuditCommandReceiptTests` beschreibt positive Writerbindungen,
strikte Metadaten, fehlende/doppelte bzw. nicht bijektive Receipts,
vertauschte Receiptfelder, neu gebundene falsche Requesteingaben und
den weiterhin generischen Fremdakteur mit Operations-Praefix.
`OperationsSqlTests` ergaenzt echte `restricted.Escalate`-Regressionen
fuer Fall und PSUR-Rueckfrage, vollstaendigen Auditschnitt sowie
gezielte Receiptmanipulationen mit Wiederherstellung im eigenen
Fixture. Eintragsmanipulationen bleiben Einheitstests, weil direkte
Auditzeilenaenderungen zuerst an der Manifestkette scheitern.
**Alle neuen Einheits- und SQL-Faelle: NotRun.** Kein Compiler-,
Build-, SQL-, API- oder Browserlauf qualifiziert diesen Kandidaten.
Die vier beobachteten `409` sind damit bis zu einem neu gebundenen
natuerlichen Lauf nicht als behoben nachgewiesen; C12 bleibt eine
separate Fixtureanpassung. Dieser Stand ist kein Abnahmenachweis.

## Lokale C12-Folge: vollstaendiger synthetischer Ruecklauf

Diese reine Testfixture-Ergaenzung folgt getrennt auf den gebundenen
Diagnosestand `3c1dff925af474c8e9bd67123c813e9c7267c7a7`.
C12 verwendet vor der Bewertung jetzt den bestehenden Ablauf aus
`InformationService` und `CaseScenario.Respond`: getrennte Medical-/RA-
Anfragefreigaben, expliziter interner Simulatorauftrag, beobachtete
bestaetigte Zustellung, vollstaendiger synthetischer `Envelope`,
begruendete PMS-Zuordnung und separate medizinische Quellenpruefung.
Anfrage-ID, Fall, Produkt, Korrelation, Empfaenger und der gebundene
Empfaengerhash stammen aus genau dieser eigenen synthetischen Anfrage.
Die Quelle muss nach Zuordnung noch unbestaetigt und erst nach dem
Medical-Review freigegeben sein. Die anschliessenden bestehenden
Sammelmeldungs-, Rollen-, Frist- und Exportpruefungen bleiben erhalten.

Keine Produktionslogik, Guards, Zeitlimits, Projektauswahl oder
Ressourcen-/Cleanupgrenzen werden geaendert. Diese lokale Vorbereitung
ist kein ausgefuehrter C12-Browsernachweis, kein Auditfehlerfix und
nicht Teil der getrennt gebundenen Diagnose-Publikation von `3c1dff`.

## Browserfolge nach b765: belegte Fixturekorrektur und begrenzte Diagnostik

Der natuerliche [Cohors-CI-Lauf 44490806373](https://github.com/micnhs/AMA-05-agentic-PMS-02/actions/runs/44490806373),
Attempt 1, ist an `b765880726c40255d145b1358fea37a83f72b673` gebunden:
395/395 .NET und 39/39 synthetische Bootstrap-Faelle bestanden.
Alle elf Browserprofile fuehrten ihre 32 Faelle aus: **26 bestanden,
sechs fehlgeschlagen, keine Skips**. Appjob `135445619500` und
Infrajob `135445620024` bleiben negativ; Infra erreichte 56 positive
Offline-Vertraege und Checkov 186 bestanden / 77 fehlgeschlagen.
Die 27 allowlistgebundenen Artefaktdateien einschliesslich aller
22 Profiloriginale sind erhalten. Elf positive Terminal-Cleanupbelege
sind keine unabhaengige v8/v14-Closure.

Die nachfolgende reine Quellrevision ergaenzt bei C12 den fehlenden
benannten synthetischen `recipient` mit dem bestehenden
`InformationRecipient`-Vertrag und identischer `recipientRole`.
Damit ist die Quelle des zuerst erreichten `422/request_recipient`
korrigiert, nicht der gesamte C12-Fall als bestanden nachgewiesen.
Der weitere, im Original noch nicht erreichte C12-Ruecklauf verwendet
weiterhin den alten Ablauf ohne `Envelope`, bestaetigten simulierten
Versand, Zuordnung und medizinische Quellenpruefung. Der bestehende
Vertrag in `InformationService` und die aktuelle `CaseScenario.Respond`-
Fixture belegen diese separate Folgearbeit; sie wird hier weder
stillschweigend ersetzt noch als Laufzeitbefund ausgegeben.

L03 erreichte zweimal beim Warten auf den Download das unveraenderte
120-Sekunden-Testlimit; E04 und E05 erhielten jeweils HTTP 409. Die
Originale enthalten keinen passenden ProblemDetails-Body/Domaenencode.
SQL-Historien-/Transport-/Quellenpruefung und Objektbytepruefung haben
mehrere fail-closed Guards; eine gemeinsame Ursache ist nicht bewiesen.
Deshalb wird keine Produktionslogik geaendert. Die drei betroffenen
Teststellen erfassen kuenftig nur HTTP-Status und die vorhandenen
ProblemDetails-Felder `status`, `code`, `title`, `traceId` in der
bestehenden credentialgeprueften JUnit. L03 beobachtet dazu denselben
UI-GET parallel zum echten Download, ohne zusaetzlichen Request,
Retry oder verlaengertes Zeitlimit. E04/E05 verlangen weiterhin 200;
ZIP-, Originalbyte-, Scope- und weitere fachliche Assertions bleiben
unveraendert. Erfolgreiche ZIP-Bodies werden nicht als JSON gelesen,
unlesbare Fehlerbodies ausdruecklich kenntlich gemacht; keine Header,
privaten Logs oder beliebigen weiteren Antwortfelder werden kopiert.

Nur statische/In-Memory-Pruefung dieser Revision ist zugelassen.
Kein neuer Build-, SQL-, API-, Browser-, Hosted-, Paket- oder Cloudlauf,
kein weiterer Push und kein neuer App-/Security-/Releasepass.

## Hosted-CI-Browserintegration: Quellkorrektur, noch kein neuer Lauf

Der natuerliche [Cohors-CI-Lauf 44478644967](https://github.com/micnhs/AMA-05-agentic-PMS-02/actions/runs/44478644967),
Attempt 1, bleibt unveraendert an `1f8122df67f92f12aeb22b7abd457a0affe7edb8` gebunden:
Appjob `135405215212` erreichte **395/395 .NET**, **39/39 Bootstrap**
(synthetische SQL-/Search-Fakes) und tatsaechlich **32 Chromium-Faelle:
9 bestanden, 23 fehlgeschlagen, keine Skips**. Infrajob `135405215541`
bleibt separat negativ: 47 Offline-Vertragsfaelle bestanden,
Checkov 186 bestanden / 77 fehlgeschlagen. Das ist kein App-Gesamtpass
und kein Security-/Cloudnachweis.

Die nachfolgende Quellkorrektur ersetzt nur die bisher gemeinsame,
nicht isolierte Browser-Fixture durch den vorhandenen `scripts/Test-Ui.ps1`.
`Invoke-CohorsCi.ps1` publiziert die bereits gebaute API einmal lokal im
ephemeren Runner (`--no-build --no-restore`) und ruft alle elf bestehenden
Profile seriell auf: Preview, AuditExport, SummaryReport, CaseDocuments,
ReportPrefill, InformationLifecycle, EudamedConfiguration, EudamedDossiers,
CockpitCalendar, SignalReview und PhysicalRetention. Die Auswahl bleibt
32 Faelle; PrimaryBaseline ist keine zusaetzliche oder ersetzende Auswahl.

Der ausdrueckliche `-HostedCi`-Opt-in verlangt Linux, GitHub Actions,
den identischen `GITHUB_WORKSPACE` und das gemeinsame Credentialinventar.
Jedes Profil erhaelt durch den bestehenden Runner eine eigene Datenbank,
API, echte private Konfiguration, scoped synthetische Konten und ein
eigenes Artefaktverzeichnis. Die bestehenden Rollen-/Produktgrenzen,
Signalfixture, isolierte physische Retention, Port-5187-Sperre,
Fristen und Stop-/Stream-/Cleanupgrenzen bleiben erhalten. Neue
Testpasswoerter, Salts und Hashes werden vor dem API-Start ebenfalls
maskiert und in die bisherige Artefakt-Geheimnispruefung aufgenommen.
Nur die im Runnerprozess ausgefuehrte Signalfixture erhaelt eine
nicht gepoolte Verbindung zur eigenen Testdatenbank, damit sie nach
Disposal keine Session zurueckhaelt. Anwendungs-Pooling, fremde Pools
und die bestehende Verweigerung bei verbleibenden SQL-Sessions bleiben
unveraendert; es gibt kein pauschales `ClearAllPools`.

Ein Browserfehler bleibt ein Fehler. Nur bei vollstaendig belegtem
Terminal-Cleanup darf das naechste Profil beginnen; ohne diesen Beleg
werden auch der uebergeordnete SQL-Container und seine Konfiguration
nicht blind entfernt. Ausfuehrungs- und Cleanupfehler bleiben getrennt
erhalten. Die aggregierte JUnit behaelt alle ausgefuehrten Testfaelle
einschliesslich Fehlern/Skips, Projektidentitaet und Originaldetails;
Profil-JUnit- und Terminal-Hashes sowie der Cleanupstatus werden gebunden.
Fehlende Projekte, reduzierte Fallzahlen oder widerspruechliche Zaehler
blockieren Erfolg. Das Aggregat ist ausdruecklich als abgeleitet markiert.
Zusaetzlich bleiben die elf originalen JUnitdateien und elf originalen
`owned-resources-terminal.json` im selben synthetischen Testartefakt
unter `browser-originals/<projekt>/` nachpruefbar. Die Allowlist benennt
alle 27 Dateien einzeln: diese 22 Originalbelege, drei bisherige
Hauptberichte und zwei Screenshots ausschliesslich aus Preview.
Keine Rekursion oder Wildcards, privaten API-/Browserlogs, Zugangsdaten,
Runtimeverzeichnisse oder weiteren Screenshots werden hochgeladen.
Saemtliche vorhandenen Textbelege durchlaufen vor jeglicher Freigabe
dieselbe vollstaendige Credentialpruefung einschliesslich aller neuen
scoped Konten. Es werden genau die geprueften Originalbytes kopiert,
auch bei Testfehlern; ein Geheimnisfund sperrt die Veroeffentlichung,
statt Originale nachtraeglich zu bereinigen oder zu ueberschreiben.
Diese Terminalbelege sind keine unabhaengige v8/v14-Closure.

Zwei weitere Originalfehler sind davon unabhaengig korrigiert:
UI02 verwendete wiederholt den jeweils aktuellen Langtext als Basis
(16.590, danach 32.869 Zeichen). Die synthetische neue Fassung basiert
jetzt auf der hashverifizierten unveraenderlichen Revision 1, bleibt
mit eigenem Revisionsmarker unterscheidbar und innerhalb der
unveraenderten Grenze 50 bis 30.000 Zeichen. Die Kalenderpruefung
waehlt den Monat aus dem gespeicherten LegalDue-/Follow-up-Instant
in der tatsaechlich ausgewaehlten Anzeigezeitzone. Beispielsweise liegt
der 30. November, 23:59 Uhr in Dublin, bereits im Dezember in Berlin.
Gesetzliche Berechnung, gespeicherte Frist, Erledigungsstatus und
fachliche Assertions wurden nicht abgeschwaecht.

Bislang wurden nur Quellsyntax, reine In-Memory-Vertraege fuer
Auswahl/JUnit/Credentials/Cleanup und isolierte Text-/Zeitzonenausdruecke
geprueft. **Keine lokale Build-, SQL-, API- oder Browserausfuehrung,
kein neuer Hosted-Lauf und keine Paket-/Previewpromotion** erfolgten
fuer diese Korrektur. Ein vollstaendiger Appnachweis bleibt offen;
die vorherigen negativen Ergebnisse werden nicht umetikettiert.

## Lokaler EUDAMED-Actor-/Konfigurationsschnitt

Anwendungsstand `7335caec08ea9f32c2d6cf768b5b2202e650f3eb` und
Browserquelle `5b0c39223bd05af7581be7769d49de56d7e687cb` wurden in einer
eigenen SQL-/API-/Browserinstanz geprueft: **45/45 Offline-Vertragsfaelle,
4/4 SQL-/Outboxfaelle und 2/2 E01-/E02-Browserfaelle**, keine Skips.
Dieser Schnitt ist nicht in die weiterhin unveraenderte 5187-Vorschau
promoviert und nicht als Gesamtintegration abgenommen.

Der persistente Actor-Antrag durchlaeuft Korrektur, Ablehnung und
Testfreischaltung mit sechs korrelierten Runden in neun Versionen.
Explizite Geraete-/Zertifikatsreferenzen, Simulatoraktivierung, ein davon
getrennt gespeicherter blockierter Domibusentwurf, Reload, serverseitige
Rollen-/Produktpruefungen und echte Kontenwechsel mit verspaeteter
Altantwort sind nachgewiesen. Der Simulatorpfad hat keinen Gateway
aufgerufen; der Browser keine fremde Origin angefordert.

Der erste echte Hostlauf fand einen Fehler in der Bindung leerer
Domibus-Konfigurationskataloge. Ein reproduzierender JSON-Bindungstest
und ein sicherer parameterloser Optionskonstruktor beheben ihn, ohne
lokale Netzwerkrechte aus Benutzerkonfiguration zu uebernehmen.
Ein anschliessender Browserfehler war ein zu enger Selektor fuer
implizite Select-Labels. Beide negativen Versuche bleiben erhalten.
Der abschliessende Browserlauf und sein unabhaengiges Cleanup sind
terminal erfolgreich: eigene Prozesse, Testdatenbank, Browserprofile
und temporaere Zugangsdaten entfernt; Nutzerbrowser nicht beruehrt.

| Gebundener Beleg | SHA-256 |
| --- | --- |
| Build02 | `6eee5556e41ca9a1448af42d9a9a458747b81050723e5a81e594a18cf20216dc` |
| Native03, 4/4 | `c1f6b30e73cdc150ba6039b94c5ecdf9ca37f13256bad3fe0b123ad4d141a6b0` |
| Browser03, 2/2 | `3352248ed4a11a7bee5b4b61a2e196f175c0d1e2c83b7f8f337fe8fd73eb6452` |
| Unabhaengiges Cleanup03 | `8d8560a5e79b2771f0cd9a62fc1e1c0c16a972fba1dcddaafb4a99e22a9f95f3` |

Die Quellen und Testprofile liegen im Repository; private Laufzeitlogs,
Zugangsdaten und Testartefakte sind keine oeffentlichen Repositorydateien.
Dossier-/Versand-/Empfangsabnahme folgt getrennt. Weder ein laufender
Domibus noch eine AS4-Gegenstelle, Playground, echte Registrierung,
Behoerdenmeldung oder Azure-Bereitstellung wurden damit nachgewiesen.

### Gebundener Dossier-/Versandkern

Auf `9efda9bddbd60c32fb6dc52caa14baac219a7a1b` sind Web-/C#-Build und
privates Anwendungspaket sowie **22/22 SQL-Faelle ohne Skips** nachgewiesen.
Die fuenf getrennten, nacheinander ausgefuehrten Partitionen behalten
das jeweilige 180-Sekunden-Limit und die exakten Quellen-/Assemblybindungen:
8 bestehende Actor-/Dossier-/PDF-Faelle und 14 neue Versandfaelle.
Die vorherige Quellkompilation und 47 Offline-Vertragsfaelle waren
ebenfalls erfolgreich; das ist keine Ausfuehrung eines Domibus-Gateways.

Der Versandledger reserviert die native MIR-Pflicht auch gegen einen
gleichzeitigen Legacy-Simulatorauftrag. Outbox-Claim, technischer Beleg
und fachlicher Eingang sind getrennt. Nur eine passende fachliche
Annahme eines expliziten Submit-/FollowUp-Auftrags quittiert genau
die gebundenen nativen Pflichten; Fristtermine und Originalsignaturen
werden nicht ersetzt. Ungeklaerte Versuche blockieren Zweitversand.
Deaktivierung vor Claim sperrt; eine spaete Antwort nach Start bleibt
am urspruenglichen Profil.

DraftOnly bleibt auch bei beiden OutOfOrder-Reihenfolgen ein Entwurf.
Ein widerspruechlicher spaeter BusinessAccepted-Eingang bleibt als
Original erhalten und erzeugt einen sichtbaren, sperrenden
ActionMismatch, keine Pflichtquittierung. Der normale Uebergang von
DraftOnly zu Submit verlangt eine neue Paketfassung und neue getrennte
Medical-/RA-Freigaben. Duplikate, verzoegerte Antworten, technische und
fachliche Ablehnung sowie geaenderte Quellstaende sind separat geprueft.

| Gebundener Beleg | SHA-256 |
| --- | --- |
| Build07 | `0e89c78c6a6c74795f5f77314b3ba1df06481cab0c9a33d555aeed7824c8a28b` |
| Native09, 3/3 | `b616ff93eed5f0e7621f0700a67b429d38249e9fa9041a0383028c8a0aa9b313` |
| Native10, 4/4 | `d8c38ed24f5f3e54a739d317ce23624434203f6e12b94b44892ede3e9bda68f3` |
| Native11, 4/4 | `616d91d94a3e552b3f59721e0b136bf280ba9d7c0c83193880752588239556ca` |
| Native12, 3/3 | `604a2dad19f623d64521cd1c859e61dd474f5ccd75ae17e703b6733101af92a4` |
| Native13, 8/8 | `467061bf8e55dfc845ebf5dec9bc24737ff0617f401f64addb8dfe8f92997e31` |

Die negativen Vorlaeufe bleiben erhalten: eine kanonische
Id-Namenskollision wurde korrigiert, ein Test suchte faelschlich nur
im begrenzten ersten Outbox-Batch. Zwei zeitlich begrenzte kombinierte
SQL-Laeufe wurden beendet; ihre exakt protokollierten eigenen
Datenbanken und Objekte wurden unabhaengig entfernt, ohne die
Vorschau zu beruehren. Die erfolgreichen Einzelpartitionen belegen
zusaetzlich ihr regulaeres vollstaendiges Cleanup.
API/UI-Dossierversand, Korrespondenz, vollstaendiger Produktauditexport,
Backup/Restore und externe Empfangs-/Betriebsabnahme sind durch diesen
Kernnachweis noch nicht abgeschlossen. Keine Previewpromotion.

### EUDAMED-Originale im Produktaudit

Der Produktaudit-Erhaltungsschnitt auf `a13aa3771959a2a4a30b57e9625d173ad878b899`
mit reiner Testreferenzkorrektur `22a955e77a2b6456cc47d5da9664e19ec269d86d`
ist kompiliert und in **13 unterschiedlichen SQL-Faellen** nachgewiesen:
zwei neue EUDAMED-Faelle sowie alle elf bestehenden Produktaudit-Gates,
jeweils in begrenzten, sequenziellen Partitionen ohne Skips und mit
abgeschlossener Testressourcenbereinigung.

Actor, Referenzen, Konfiguration, Kanal, Dokument, Dossier, Versandledger
und Inbox werden nicht mehr als unbekannte Aggregate verworfen.
Ein real erstelltes, doppelt freigegebenes FSCA-Dossier bindet sein
byteidentisches PDF, eingefrorenes Paket, freigegebenen Versandsnapshot,
technischen Beleg, fachlichen Originaleingang und Zertifikatsoriginal im
Export. Fehlende PDF-/Paket-/Snapshotbytes verhindern einen vollstaendigen
Export. Eingebettete XML- oder kanonische SQL-Hashes werden nicht
faelschlich als Blob-Adressen behandelt.

Ein gruppierter PSUR mit Versand und Inbox bleibt auch nach spaeterer
Verkleinerung der Gruppe fuer das historische nichtfuehrende Produkt
auffindbar; alle historischen Mitgliedsberechtigungen bleiben erforderlich.
Ein unbeteiligter Produktscope sowie bisherige Text-/XML-/JSON-Hashes und
Archivpfade sind durch die bestehenden Gates abgesichert.
Build09: `cf40f5488b1dcb67982f58b6dc8b96db99845abe5bafb7b56e51979f07d0daba`;
Binaerfall Native14: `9f00b01f4a6d034ac5081cf933b60d6b67564191777bcb0155b49082adfdd406`;
Gruppenfall Native21: `3f5fe37b213f687320a3e164a9367378931e0cbea028da87b20a69ddffd16acb`.
Die bestehenden Faelle liegen in Native16/17/18/19/22. Der vorangegangene
falsche Gruppenreferenz-Prefix und ein zu breiter Testfilter wurden
offengelegt und korrigiert, nicht als bestandene Runner-Abnahme gewertet.
Dies ist weiterhin **kein Backup-/Restore-Nachweis**, keine externe
Ledgerverifikation und keine vollstaendige EUDAMED-Gesamtabnahme.

## Qualifizierter lokaler Preview-Schnitt

Die zuletzt veroeffentlichte **5187-Vorschau** verwendete
`.local\app-replay-rfi-742db5d` aus
**`742db5d7fc11b394436f9524b565fbb6f2b1077f` / Nachforderungen Build02**.
Benannte Empfaenger, getrennte Freigabe und simulierte Zustellung,
originalgebundene Ruecklaeufe, menschliche Zuordnung und separate
Medical-Quellenpruefung sind mit **20/20 gezielten nativen Faellen und
2/2 vollstaendigen R01-/V01-Faellen im privaten Chromium Headless Shell**
geprueft. Browserquelle ist `8397fe47e67a861b2efd91fbddafe31cb9a7d06a`.
Beim rein lesenden Erreichbarkeitscheck am 17.11.2026 um 12:40 UTC wurde
die Verbindung auf 5187 abgelehnt; ein laufender Host war nicht nachgewiesen.
Der Grund fuer den Stillstand ist unbekannt. Das erhaltene Paket ist damit
kein Nachweis eines aktuell erreichbaren Demolinks; ein Neustart oder eine
Promotion des spaeteren Integrationsstands wurde daraus nicht abgeleitet.
Die vorherigen Pflichtfeld-, Ablehnungs-/Wiederaufnahme- und
Kontowechselnachweise bleiben erhalten; ihre 44/44- beziehungsweise
25/25-Serien sind historische, nicht neu ausgefuehrte Vollumfaenge.
Die eigenen Abschnitte unten trennen diese Abnahmen. Aktive Grenzen bleiben `Retention:PhysicalMode=Disabled`,
`Seed=false`, `AutoMigrate=false`, `LocalOperations=true` und
`AgentTransport=Local`. Der unmittelbare unveraenderte Rueckfall ist
`.local\app-replay-required-a6df31f`; die fuenf aelteren Pakete bleiben ebenfalls erhalten.

### Historischer Vorbefuellungsschnitt d166

Die fruehere **5187-Vorschau** verwendete das unveraenderte Paket
`.local\app-replay-prefill-d166658` aus dem sauberen Quellcheckpoint
**`d16665832fd1066be60c7f15db41b2c3d1233cc7`**. Sie wurde mit
**`Retention:PhysicalMode=Disabled`**, `Seed=false`, `AutoMigrate=false`,
`LocalOperations=true` und `AgentTransport=Local` betrieben. Weitere Quellarbeit aendert
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

## Abgelehnte Signale und direkte Wiederaufnahme: geprueft und bereitgestellt

Am 16.11.2026 wurde der priorisierte Schnitt einschliesslich der gemeldeten
Kontowechselgrenze unter `http://localhost:5187/` bereitgestellt:
**App-/Browserquelle `603ea28a093c4e455ade5d4813ff7470425c8175`, Build03,
Paket `.local\app-replay-signals-603ea28`**. Die erfolgreiche zweite
Promotion bestaetigt den eigenen API-PID **119156** zum damaligen
Beobachtungszeitpunkt. Ein PID ist keine dauerhafte Eigentumszusage;
spaetere Eingriffe benoetigen erneut die begrenzte native Identitaetspruefung.

Die Navigation **Abgelehnte Signale** zeigt aktuelle Ablehnungen mit
Originalquellbezug, Grund, Zeitpunkt und tatsaechlichem Agentenlauf bzw.
Reviewer. Undokumentierte historische Herkunft bleibt ausdruecklich
unbekannt. Medical Safety Reviewer **oder** Regulatory Affairs kann mit
Begruendung **Wiederaufnehmen / Jetzt ins Review** ausfuehren.
Der bestehende Fall wird versionsgebunden wiederverwendet; ein bislang
fallloser oeffentlicher Metadatensatz erhaelt einen stabilen Quellenreview
in derselben Review Queue, keinen erfundenen Herstellerfall oder
Awareness-Zeitpunkt. Aenderung, Historie, Audit, Command-Receipt und Outbox
liegen in der vorhandenen einzelnen SQL-Aggregattransaktion.

Die Aktion erzeugt keinen neuen KI-Auftrag, keine Freigabe und keine
reaktivierte Signatur. Originale, Ablehnungsverlauf und bestehende Fristen
bleiben erhalten; fehlende oder durch Wiederaufnahme ueberholte Bewertung
bleibt sichtbar. Signalablehnung ist weder Meldungsrueckgabe noch
Nichtmeldepflichtentscheidung oder Versandablehnung.

Der erste Build auf `4a59b16` bestand Web-/TypeScript-Pruefung, scheiterte
aber an fuenf xUnit-Analyzerbefunden der neuen Tests. Diese wurden ohne
Abschwaechung der Erwartungen korrigiert; der Outbox-Nachweis verwendet
jetzt den exakten SQL-Fallbezug statt der global auf 100 Zeilen begrenzten
Pending-Seite. Build02 stoppte vor erneuter Kompilierung an einer zu engen
Zeitstempelpruefung der unveraenderten Original-Markenassets.
Build03 bindet alle unveraenderten App-/Web-/Build-Eingaenge und die
tatsaechlichen Web01-Ausgaben; Logo und Animation sind zusaetzlich gegen
ihre urspruenglichen getrackten Bytes gebunden. **Web01 wurde nicht erneut
ausgefuehrt**. C#-Build und privates Publish liefen erfolgreich; alle
**116 Paketdateien**, sechs Testassemblies und die komprimierten Webassets
sind nachgewiesen. Beide negativen Buildbelege bleiben erhalten.

**25/25 native Domain-/SQL-Faelle, keine Skips:** 21 neue Signalfaelle
einschliesslich beider Rollen, fremder Produktscopes, nicht berechtigter
Rollen/nichtmenschlicher Identitaeten, Agentenherkunft, unbekannter Historie,
Idempotenz/Konkurrenz, direkter Review-Persistenz, alter Signaturen und
ausstehender KI plus die vier bestehenden Sicherheitsregressionen
T06, T07/T08, T11 und T13. Die genaue eigene Testdatenbank und ihre
Objekte sind terminal entfernt; vor und nach dem Block waren keine
Testdatenbanken uebrig.

**4/4 native Chromium-Faelle in einem einzigen Lauf, keine Skips/Retries:**

| Fall | Tatsaechlich gepruefter UI-/Persistenzvertrag |
| --- | --- |
| S01 | Medical nimmt eine explizite Replay-Agentenablehnung unmittelbar in die Review Queue auf; gespeicherte Historie/Fristen bleiben erhalten, alte Bewertung ist sichtbar ueberholt. |
| S02 | RA lehnt als Reviewer ab und nimmt den unbewerteten bestehenden Fall eigenstaendig unmittelbar wieder auf; keine neue Fallakte, keine versteckte Analyse. |
| S03 | Medical und RA nehmen jeweils falllose Quellenmetadaten in denselben Review-Arbeitsbereich auf; stabile Referenz, keine erfundene Awareness, keine duplizierten Faelle. |
| S04 | Echter UI-Logout/-Login zum Cardio-Konto entfernt Ortho-Hinweis und Quellenreviews; verspaetete alte GET-/Reopen-Antworten tragen keine fremden Daten oder Hinweise erneut ein. |

In S01 und S02 war das 10-Sekunden-Polling im Browser gezielt ausgeschaltet:
**853 ms fuer Medical, 584 ms fuer RA** bis zur nachgewiesenen Review-
Sichtbarkeit, **0 manuelle Reloads und kein neuer Agentenlauf**.
Das sind einzelne technische Demobeobachtungen, kein Last- oder
Performanceversprechen. S04 verwendet fuer die Antwort nach dem Logout
zusaetzlich einen ausdruecklichen Timing-Fault am echten Logout-Handler;
es wird kein normaler Pointer-Klick durch einen beschaeftigten Dialog behauptet.
Der bereits berechtigte alte Schreibvorgang bleibt gespeichert, nur seine
verspaetete UI-Antwort wird nach Identitaetswechsel verworfen.

Der Browser war das zur installierten Playwright-Version **1.56.1**
gehoerende private **Chromium1194 / 141.0.7390.37** mit explizitem
`COHORS_BROWSER=chromium`; Binary, DLL, Version, Lockfile und Helfer
sind getrennt gebunden. Ausfuehrung und unabhaengiges `finally`-Cleanup
blieben bei **600 s / 60 s / 10 s Stopgrenze**, beide endeten mit Exit0
und geschlossenen Streams. Eigene API, aufgezeichnete Browser-/Worker-PIDs,
Datenbank, private Settings/Zugaenge/Objekte und Profil sind entfernt.
Keine zusaetzliche Edge-Aussage, globale Browseraenderung oder Benutzung
von `cohors-tryout`.

Der neue rein lesende Bestands-Capture umfasste **249 aktuelle/historische
Zeilen**, darunter **17 aktuelle Faelle und zwei PSURs**, Schema2.
Alt4b und NeuBuild03 wurden mit demselben Vergleichshelfer-Hash
`f04cc44490f5ad5fdc18d4584108706774fbdee5b9277b31ca53aa0d0a4451ff`
in frischen getrennten Prozessen **119508 / 119344** verglichen.
Die tatsaechlich geladenen Domain-Locations zeigten auf die jeweils
ausdruecklich ausgewaehlten Pakete; deren SHA256 waren
`1b2aad26a77288fa45c65b8d2040f271bd589166f5c9dae61d2e3134d0c41f05`
und `e936d1e37494058ffab6dc1b0303062b0d3befc45c74342330a2e9b18da1dd6f`.
Kein neuer typisierter/Freigabescopedrift, zwei bekannte Altanomalien
unveraendert. Vor der Umschaltung wurde erneut auf Datenaenderungen
geprueft. Das anschliessende Preserved-Gate bestaetigt alle urspruenglichen
Versionen, jede aktuelle Zeile gegen ihre eigene Historienkopie und jede
unveraenderte Version gegen den urspruenglichen rawHash; neuere korrekt
historisierte Nutzerversionen bleiben zulaessig.

Die **erste Promotion blieb ein negativer Beleg**: Die API war gestartet,
aber das nachgelagerte Helfer-Gate umhuellte leere `Invoke-RestMethod`-
Arrays nochmals und bewertete sie dadurch als ein falsches Element.
Der unveraenderte4b-Paketfallback wurde gestartet; keine Daten oder
Nutzeraktionen wurden zurueckgesetzt. Nur der getrennte Promotionshelfer
wurde korrigiert, mit vier positiven und sechs negativen reinen
Payload-Pruefungen. Die zweite Promotion verwendete exakt dasselbe
gepruefte Paket und dieselben SQL-/Browserbelege, keine neue Fachsuite.
Ihre tatsaechlichen neuen HTTP-Antworten sind gueltige leere Arrays
(`[]`, jeweils SHA256 `4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945`):
Bestehende Nutzerstaende wurden nicht kuenstlich zu Ablehnungen umgedeutet.

| Beleg unter `.local\signal-review-01` | SHA256 |
| --- | --- |
| `build-03-result.json` | `523e59e4a57698c86e020a020a07d4d9251414df27f8f8f9fd1325e8f14b54f1` |
| `tests-03.trx` | `fa2aa5d85d75b89147d4afd3cef3eb561221b93ab6e10e6a9e330ba5de32dd6c` |
| `browser-01-binding.json` | `055af64b9b175edef5bfa3315200c7dc413522b855f2955baf59d486ad2a4af2` |
| `browser-01\e2e-results.xml` | `e85c09330362f9cedc3e42a904f429800c66ec2e259092fa420882cce5da285d` |
| `browser-01\test-events.jsonl` | `b85f2e561018c857dc7894371c638a722f54bd3bd8d6c3af862491964786ca9c` |
| `browser-01-bounded-result.json` | `4d6c2a2363016a9f2c3af6eb3d0e4e3be94be4e7156e473aad683d9f19509fce` |
| `browser-01-bounded-cleanup.json` | `5185638daec247a84fd04c3e716d9152fe9589f9d07946a1f7d320604bd95884` |
| `compatibility-01-verified.json` | `b0a2479135042b745d5643d4d4bfd309bbc8d925c0dca19897e8e3d14bf17ef7` |
| `compatibility-01-preserved.json` | `11ba33a8ca81d39800939263d0b957caaf1061162af3be89dfc4a41d75bb3101` |
| `promotion-result.json` (negativ) | `47aed52c7bd356394edf44203c7885ecd57be986a556c611b4f50f285a3eb261` |
| `promotion-02-preflight.json` | `c519fe11320e6017a1130c18ca13bbc8056573463986474ff4d0893f18b4598b` |
| `promotion-02-result.json` | `23af885145c823bec59c77dd430885cfa65780ac9be3a292f70ccafbc1143f61` |

Die tatsaechlich ausgelieferten HTML-/JS-/CSS-Bytes, Health, autorisierten
Signalprojektionen sowie effektiven Replay-/Local-/Retention-Grenzen sind
bestaetigt. Alle Nutzerstaende, Accounts, Schluessel, Originalobjekte,
Build03 und die vier Rueckfallpakete sind erhalten. Kein Seed, keine
Migration, Datenkorrektur, echte Uebermittlung oder Live-Foundry-Aufruf.

Der explizite Intake-Schalter **Explizites Replay-Profil** demonstriert
Agentenablehnung mit einer klar synthetischen Begruendung; das ist keine
verifizierte inhaltliche Irrelevanz. Die falllosen Browserquellen waren
ausdruecklich synthetische Metadaten einer privaten Fixture, kein echter
Dateiimport oder Publikationsabruf. Am Ende dieses Schnitts blieben
On-demand-Dokumentimport, Pflichtfeld-Vervollstaendigung,
Nachforderungszuordnung und umfassendere Reviewhistorie getrennte
Folgeauftraege. Die anschliessende Pflichtfeld-Auslieferung ist unten belegt. Load/Recovery/Cloud und
eine neue Vollsuite wurden durch diesen Schnitt nicht freigegeben.

## Pflichtfelder erneut vervollstaendigen: geprueft und bereitgestellt

Am 16.11.2026 um **19:42:26Z** wurde die verifizierte Fortsetzung auf
`http://localhost:5187/` bereitgestellt: **App-/Browserquelle
`a6df31ffa7fc24379b0d27e681b744e32554588a`, Build02,
Paket `.local\app-replay-required-a6df31f`**. Der eigene API-PID **113120**
ist nur die damalige Beobachtung; spaetere Eingriffe erfordern erneut
eine frische, begrenzte Eigentumspruefung.

Der Nutzerweg liegt im Fall unter **Human Review & Meldung**:
**Pflichtfelder erneut durch Agenten vervollstaendigen** startet auch fuer
Medical einen neuen Entwurf ohne erneute Eingabe bekannter Werte.
Gebundene Fall-/Produkt-/Herstellerdaten und eindeutig geeignete
Kontaktbelege werden mit Versions-/Hashherkunft uebernommen.
Vorhandene menschliche Angaben, auch aktuell nicht angezeigte UDI-Felder,
werden erhalten; abweichende Quellen werden nicht still bevorzugt.
Die gesonderte Sektion **Nicht automatisch befuellbar - Angaben oder
Belege erforderlich** nennt lesbare Feldbezeichnung, Grund und benoetigte
Information mit direktem Bearbeitungs-/Nachforderungs-/Quellenweg.
Technische Feldschluessel bleiben getrennt einsehbar.

Ein Kontakt muss bereits als integritaetsgepruefte, genehmigte synthetische
Evidenz fuer genau Produkt, Hersteller-SRN, Zieljurisdiktion und
Hersteller-Melderrolle an den Fall gebunden sein. Das ist **keine neue
Kontaktstammdaten- oder Kontaktimport-Oberflaeche** und kein Befuellen aus
Reviewer-Login oder PRRC. Ohne geeigneten Beleg bleibt die Angabe sichtbar
offen. Mehrere verschiedene Kontakte oder mehrere Codes fuer ein benoetigtes
skalares MIR-Feld werden nicht beliebig zusammengefuehrt oder ausgesucht.
Quellkonflikte erfordern eine ausdruecklich begruendete menschliche
Aufloesung; Pflicht-/XSD-Pruefungen bleiben wirksam.
Eine neue Folgeterminzusage bleibt menschlich. Derselbe bereits zugesagte
Termin bleibt bei der Vervollstaendigung desselben Meldeanlasses erhalten.
Jeder neue Entwurf benoetigt neue Medical-/RA-Freigaben; alte Berichte,
Signaturen und Fristen bleiben Historie, nicht aktuelle Freigabe.

**Build01 bleibt negativ:** Web/TypeScript lief erfolgreich, der
C#-Testbuild scheiterte am fehlenden expliziten `HistoricalAsOf`-Argument
im neuen Mehrfachcode-Test. Die Korrektur von `8cd4f14` nach `a6df31f`
betrifft nur diesen Test. Build02 weist alle unveraenderten App-/Web-/
Build-Eingaenge nach und **verwendet Web01 erneut**, ohne einen zweiten
Webbuild zu behaupten. C#-Build und privates Publish bestanden;
**116 Paketdateien, 16 zentrale Anwendungs-/Assetdateien und sechs
Testassemblies** sind gebunden.

**44/44 neue gezielte native Faelle, keine Skips:** 34
`ReportPrefillRuleTests`-Instanzen, sechs SQL-`ReportPrefillTests`,
T07/T08 fuer Freigabescopes/getrennte Menschen, T13 fuer unklaren Empfang
und beide bestehenden Wiederaufnahmerollen. Geprueft wurden unter anderem
Kontakteligibilitaet und widerspruechliche Quellen, menschliche Werte und
Terminzusagen, bedingte Initial-/Final-Codes, Medical-Neuversuch,
Historien-/Fristerhaltung und alte optionale JSON-Vertraege.
Eigene SQL-/Objektressourcen wurden entfernt. Diese 44 sind nicht die
historischen Cockpit-44; nach den Browserproblemen wurden sie **nicht**
nochmals ausgefuehrt.

**Browser01 und Browser02 bleiben insgesamt fehlgeschlagen.** V01 bestand
jeweils seine Fachassertionen (79,035 s / 130,023 s), aber beide Laeufe
blieben beim Browserabbau ohne finales Reporter-Ende/JUnit stehen.
Die unveraenderten 600-s-Aussengrenzen griffen; die ersten Cleanup-Versuche
blockierten an verbliebenen aufgezeichneten Node-PIDs. Spaetere getrennte
Recovery-Belege bestaetigen die Entfernung der genauen Ressourcen.
Bei Browser02 wurden nur die frisch nativ nach Owner, Image,
Erstellungszeit und vollstaendiger Befehlszeile geprueften eigenen
Runner-/Worker-PIDs einzeln beendet. Kein Benutzerbrowser, kein CIM,
keine Elevation, kein Zusammenrechnen von Teilpasses.

Ein kurzer isolierter **Shutdown-Probe01 ohne Cohors/API/SQL** reproduzierte
den Stillstand mit drei `about:blank`-Kontexten: CDP-Pipes3/4 schlossen,
nach 35 s fehlten Prozess-exit/close und Streamabschluss1/2.
Die 60-s-Grenze hielt. Eine zeitweise gesperrte Datei des eigenen Profils
wurde nicht durch Beenden eines unbekannten Prozesses freigemacht;
der spaetere begrenzte Cleanup war erfolgreich. Die genaue native
Dateihalterabfrage ergab keinen Halter und damit keinen Ursachennachweis.

Als gezielte private Alternative wurde der zur **unveraenderten
Playwright1.56.1** gehoerende klassische **Chromium Headless Shell1194 /
141.0.7390.37** gebunden. Erst nach echter SDK-Missing-Binary-Meldung wurde
die Abhaengigkeit bereitgestellt. Auch der begrenzte CLI-Installationsversuch
blieb negativ; die alternative native Extraktion des exakt vom SDK
gewaehlten offiziellen ZIP wurde gegen **alle 13 Archivdateien
bytegeprueft**. Die Shell besitzt keine PE-Versionsressource: ein erster
PE-basierter Pruefversuch bleibt negativ, anschliessend wurden die
tatsaechliche `--version`-Ausgabe und die Browserprotokoll-Version
jeweils exakt geprueft. Es wird kein erfolgreicher CLI-Installer behauptet.
Binary-SHA256:
`ed19d88b4486c39c0265b0c07cbf1d6835d06069e600bede1adb09db0df46c8e`.
Node26, Playwright, Lockfile, bestehendes Chromium und globale
Browser-/Unternehmensrichtlinien wurden nicht geaendert.
**Shutdown-Probe02** schloss alle Streams und den eigenen Prozess normal
mit Exit0 und separatem erfolgreichem Cleanup; erst danach lief V01 neu.

**Browser03: 1/1 vollstaendiger nativer V01-Fall, 0 Fehler/Skips/Retries,
32,017 s.** App-/Browserinputs, Assertions und 210-s-Falltimeout sind
unveraendert; `COHORS_BROWSER=chromium-headless-shell`, Binary, Version,
privater Cache und neue Helfer sind getrennt gebunden. Nativer JUnit,
finales Reporter-`end=passed`, normale Ausfuehrung und unabhaengiges
`finally`-Cleanup mit geschlossenen Streams bestanden gemeinsam.
Die Grenzen bleiben **600 s Ausfuehrung / 60 s Cleanup / 10 s Stop**.
Die vier aufgezeichneten API-/Node-/Browser-PIDs, genaue Testdatenbank,
private Settings/Zugaenge/Objekte und das Profil sind entfernt.
`prefill-workflow.json` belegt Medical-Neuversuch ohne Wiedereingabe,
erhaltene verborgene UDI, Agententexte, historische Signaturen und
Fristen, **0 aktuelle Freigaben, 0 automatische Uebermittlungen und
0 Seitenfehler**. Die beiden Screenshots fuer Medical und schmale
RA-Ansicht wurden gelesen. Dies ist ein eigener Headless-Shell-Nachweis,
keine rueckwirkende Full-Chromium-/Edge-Freigabe oder geklaerte Hostursache.

Der **frische** lesende Capture von 19:37:16Z umfasst 249 aktuelle/
historische Zeilen einschliesslich aller **17 Faelle und zwei PSURs**,
Schema2. Alt603 und NeuBuild02 wurden in getrennten frischen Prozessen
**122988 / 95560** mit demselben Vergleichshelfer-Hash
`f04cc44490f5ad5fdc18d4584108706774fbdee5b9277b31ca53aa0d0a4451ff`
verglichen. Die tatsaechlich geladenen Domain-Locations zeigten auf die
jeweils ausgewaehlten unveraenderlichen Pakete, SHA256
`e936d1e37494058ffab6dc1b0303062b0d3befc45c74342330a2e9b18da1dd6f`
und `82bea15f3398a44336c187887e540ca6470049f48a14a160afc1de5ec0492657`.
Kein neuer typisierter/Freigabescopedrift; zwei bekannte Altanomalien
blieben unveraendert. Unmittelbar vor Stop wurden Datenfrische und
native API-Identitaet erneut geprueft. Danach bestaetigte das starke
Preserved-Gate jede aktuelle Live-Zeile gegen ihre eigene Historie,
unveraenderte Versionen gegen den Capture und alle Originalversionen.
Neuere korrekt historisierte Nutzerversionen werden nicht zurueckgesetzt.

| Beleg unter `.local\required-fields-completion-01` | SHA256 |
| --- | --- |
| `build-01-result.json` (negativ) | `9e72eab87c28de1c477c9083ec0cd0338c8b3cb4cfe88fbbd3e66094ff2f149d` |
| `build-02-result.json` | `b6b687bb8abe04b880cc92e070c3b1f8f0a67d2567012247a78e9b6b61476afe` |
| `tests-02.trx` | `40f44075c922a85771f907f60ff9bd7594eed7396c2a397935b06057d84cadf9` |
| `browser-01-bounded-result.json` (negativ) | `eb9a2da3fba6437a1b51cb71e92652a0b55b164a0ac7bdeef7421e9a9cd32559` |
| `browser-02-bounded-result.json` (negativ) | `246eed9a9ed306844b3dc7808f6ae0aa5f4f91c88c99c4e02c3e288476b03bc4` |
| `browser-02-recovery-cleanup.json` | `d55863dc86b8749301842f295ff39bff541987a977634bb69d8411f4e71d1fc8` |
| `shutdown-probe-01\bounded-result.json` (negativ) | `adfc80abb0b1575b9a67e483385ddc8e44d3596cc75ccf12aabca56c74392828` |
| `shutdown-probe-02\bounded-result.json` | `bfbf751e44be545295e171bc2deab997e11401c2c4aac059e5e92c0b33e60c68` |
| `chromium-headless-runtime-01.json` | `7d060365183e75d385d7800c29ae0b28bd75bddbd060ad158bb1caad659761d1` |
| `browser-03-binding.json` | `49216ac9c153eef77dc72b56dd6d270ddc82ee58b770312e5d5fa2995ab944c6` |
| `browser-03\e2e-results.xml` | `00c902fe3c2b74f05ecc392146a52e8cf164dfed3fb4d90c96d065949bf4fca0` |
| `browser-03\test-events.jsonl` | `bb2d6674fb06db476606e72c70e44dad9988f8d0a3dfd514bebe2168c72c6245` |
| `browser-03-bounded-result.json` | `8832801792ca00306f69f09c83070ea6b1c2603bf1387d8a87c8aaaeae79577a` |
| `browser-03-bounded-cleanup.json` | `2edddbcaf74e72e75f1d84c57de3deaefa70e1b71621d7fb3768bf2e8ecd6a9d` |
| `compatibility-01-verified.json` | `aa17ff314572348517de7c6b4bd0094d428200f40374188ff722cf604c3d6a3c` |
| `compatibility-01-preserved.json` | `ac83a80b564acb025b4731c562f5131e4b809038665e21d277213f9df62139d2` |
| `promotion-01-result.json` | `8bc2bf55c696431d34b512b764fef15576fc95ba7fdb8a26794cc37a7e7cf351` |

Die tatsaechlich ausgelieferten HTML-/JS-/CSS-Bytes, Health, autorisierten
lesenden Projektionen und effektiven Replay-/Local-/Retention-Grenzen sind
bestaetigt. Vorhandene Nutzerstaende, Accounts, Schluessel, Originalobjekte,
Build02 und alle fuenf Rueckfallpakete sind erhalten. Kein Seed, keine
Migration, Datenkorrektur, echter Versand oder Live-Foundry-Aufruf;
`cohors-tryout` blieb unangetastet. Nachforderungs-Lifecycle/-Zuordnung,
umfassendere Reviewhistorie und echter On-demand-Dokumentimport bleiben
die getrennte Folgequeue; dieser Teilabschluss erweitert weder
Load/Recovery/Cloud-Freigaben noch die fachliche Gesamt-Abnahme.

## Nachforderungen: Kontakt, Zustellung und Originalruecklauf geprueft und bereitgestellt

Am **16.11.2026 um 21:27:27Z** wurde Build02 aus
`742db5d7fc11b394436f9524b565fbb6f2b1077f` kontrolliert auf5187
bereitgestellt; beobachtete eigene API-PID **119536**, Start
21:26:52.8452731Z. Der getrennte Browsercheckpoint ist
`8397fe47e67a861b2efd91fbddafe31cb9a7d06a`.
Die alte a6df-API wurde erst nach frischem Identitaets-/Datenabgleich gestoppt.
Der neue Supervisor bleibt bewusst aktiv; Paket und Belege sind unveraenderlich.

**Bedienweg:** Fallakten -> Informationsbedarf -> Anfrage entwerfen.
PMS/Medical benennt den synthetischen Kontakt, Organisation, Rolle,
vorgesehenen Kanal und Zielreferenz ausdruecklich. Medical und die getrennte
RA-Identitaet genehmigen genau diese Anfrage. Erst der eigene Sendeauftrag
erzeugt `SendPending`; der vorhandene Outbox-Worker liefert einen separaten
simulierten Zustellungs-/Fehlerbeleg. E-Mail/Telefon/Portal sind beabsichtigte
Kontaktkanaele, keine real angeschlossenen Transporte.

Originalruecklauf und Text bleiben hashgebunden erhalten. Fehlende oder
widerspruechliche Referenzen bleiben sichtbar zurueckgehalten, nicht mit
der letzten Anfrage verbunden. Nur fehlende Angaben koennen begruendet
zugeordnet werden; widerspruechliche Angaben werden damit nicht ueberstimmt.
Die Zuordnung erzeugt unbestaetigte Evidenz und menschliche Reviewarbeit,
keinen KI-Auftrag. Medical prueft danach getrennt die Quelle.
Dies ist weder eine klinische Wahrheitsbestaetigung noch eine Fall-,
Assessment- oder Meldefreigabe. Alte Freigaben, eingefrorene Pakete,
Awareness und Fristen werden nicht reaktiviert, ueberschrieben oder pausiert.
Altanfragen ohne Lifecycle bleiben lesbar, aber ihr frueheres
`SimulatedSent` wird nicht nachtraeglich als eigenstaendiger Versandnachweis ausgegeben.

Build01 aus0c7e2af enthielt den neuen Schnitt. Native01 endete **19/20**:
Nur die neue historische Test-JSON hatte nicht die bereits zuvor verwendete
kanonische Feldreihenfolge/Offset-Escapierung. Die separat geladene alte
a6df-Domain lieferte die exakten historischen Bytes; allein dieses Literal
wurde in742db5d korrigiert, keine Assertion oder Anwendungslogik.
Build02 verwendete nach Dateibindungspruefung unveraendertes Web01.
Native02 pruefte **20/20, keine Skips**: zwoelf neue Lifecycle-/Kompatibilitaetsfaelle
und acht unmittelbar gekoppelte Assurance-, Awareness-, Follow-up-,
Abschluss- und Prozessprojektionsregressionen. Eigene SQL-/Objektressourcen
wurden terminal entfernt; keine neue Vollsuite.

**Negative Browserbelege bleiben erhalten:** Browser01 scheiterte vor
Readiness und vor jedem Browserfall am nativen SqlClient-SNI-Lader
(`0x800700CE`, Dateiname/Pfad zu lang). Alle **116** Paketdateien wurden
byteidentisch an den separat gebundenen kurzen Pfad
`.local\rfi-b02-742db5d` kopiert; der dokumentierte SNI-Pfad verkuerzte sich
von253 auf215 Zeichen. Keine Anwendungs-, Betriebssystemrichtlinien- oder
globale Runtimeaenderung; das urspruengliche Build02 blieb erhalten.

Browser02 endete **1 bestanden/1 fehlgeschlagen/0 uebersprungen**.
R01 erreichte den zugestellten Anfragezustand, wartete dann bis210s auf
einen falschen exakten Label-Selektor am vorbelegten Antwort-Textarea.
Snapshot und installierter Playwright-Selektorcode zeigen: Der umschliessende
Labeltext enthaelt auch den vorbelegten Text; der zugreifbare Textboxname
enthaelt ihn nicht. Nur dieser Selektor wurde in8397fe4 korrigiert.
Der zusaetzliche Context-Close-Fehler am Testtimeout ist kein Nachweis
eines fachlichen Fristfehlers. Beide negativen Versuche haben terminale,
erfolgreiche eigene Cleanup-Belege; sie werden nicht zu Erfolgen umgedeutet.

**Browser03** pruefte R01 (**79,728s**) und den unveraenderten V01
(**87,165s**) gemeinsam: **2/2, keine Skips oder Retries**, nativer JUnit,
Reporter-Ende, geschlossene Streams und erfolgreiche unabhaengige Bereinigung.
Die Grenzen bleiben **600s Ausfuehrung /60s Cleanup /10s Stop**;
beide Falltimeouts bleiben210s. Der private, versionspassende Headless-Shell-
Bestand und sein vorhandener Shutdown-Nachweis wurden unveraendert wiederverwendet.
Es ist keine neue Edge-/Full-Chromium-Freigabe.

R01 belegt an einem eigenen synthetischen Fall die Folge
`Draft -> Approved -> SendPending -> SendFailed -> SendPending ->
TransmissionUnknown -> SimulatedSent -> Held -> MappedUnconfirmed ->
SourceReviewedNotCaseApproved`. Der Workflowbeleg zeigt beide Originalhashes,
bewahrt fehlende Originalreferenzen, bestaetigt unveraenderte Fristen/
Awareness sowie **0 KI-Analysen, 0 reale Uebermittlungen und 0 Fallfreigaben**.
Fremder Produktscope und unberechtigte RA-Quellenpruefung wurden abgewiesen.
Die Screenshots des Empfaengers/Versandbelegs und unbestaetigten Befunds
wurden gelesen; sie stammen aus der privaten Abnahme, nicht aus Nutzerfaellen.

Der neue frische Alt-a6df-/Neu-Build02-Abgleich umfasst **249 aktuelle/
historische Zeilen, 30.415.374 Byte,17 Faelle, zwei PSURs, Schema2**.
Getrennte Prozesse verifizierten die tatsaechlich geladenen Domain-Pfade/
Hashes. Keine neue typisierte oder Freigabedrift; zwei bekannte Altanomalien
blieben unveraendert. Das byteidentisch uebernommene starke Preserved-Gate
prueft jede Live-Zeile gegen ihre eigene Historie, gleiche Versionen gegen
den Capture und saemtliche alten Versionen; legitime Fortschritte sind erlaubt.
Die native Eigentumspruefung bleibt vor Stop und nach Start unabhaengig
begrenzt; keine CIM-/GetOwner-Abfrage, Elevation oder unsichere Annahme.

| Beleg unter `.local\information-request-lifecycle-01` | SHA256 |
| --- | --- |
| `build-02-result.json` | `0f5569d9115f1de50c13890bad12d8add0b989c6520cd6f35cc074d1ee678d08` |
| `tests-02-result.json` | `43b4183d657566070cd909beb7a3d1cc3961e81760abe84822574611edd924a4` |
| `tests-02.trx` | `c5a8ee2a09689d7dbeab1f8d0f9701d555970e94dfc56fa9bccbc89fdd31cd95` |
| `browser-03-binding.json` | `96ed76e119ade3e19beac6dd6b60c7c2e37d88e124c0186a77a4d2bc51dc1da2` |
| `browser-03\e2e-results.xml` | `ba13a60c971d37eca207764c20d8df9e74f6e51f9e8864453ca297dd31aba784` |
| `browser-03-bounded-result.json` | `6bde9183aac17ef9ed3121e756ba935a80f12caa47f95ea44f36ccb8ca20a7ad` |
| `browser-03-bounded-cleanup.json` | `41a5aed15777248ef264883d97bed081e5be9cc16f557759987e56df01989ac4` |
| `browser-03\information-lifecycle-workflow.json` | `ca2e418156595d346a6467b2f4d56d404c127183fa0115035b4a3c49650dd194` |
| `application-location-02.json` | `c16512272884106cb4c1af9f26108ef23b950fd5f85e18d2bbb75f5a0ea6ab04` |
| `compatibility-01-verified.json` | `db3e8c931cb50216bd22e2498e778a3234d414022c84225ace4e5a9ca022c682` |
| `compatibility-01-preserved.json` | `ff410e4d07ab8b530ca5824cd928d182ab251ceedce95cc35d9085dff73762bc` |
| `promotion-01-preflight.json` | `7018a395051542d872b6800371f18aee436a06d7a0779f85e71869af76dc3433` |
| `promotion-01-result.json` | `1ce71c675b58c96fc0c72c16c82bbb762f00e7476471adda10b7eb9e3ba0148b` |
| `promotion-helper-preparation.json` | `868c0ee3185693106dd656e0aacf8663674b43818a3d3512ca67c762da497e14` |
| `identity-self-check-01.json` | `5bff02fd378bf162211e160c4733e2f8f079a8649f9c20cc6ecf534d7924dee4` |
| `tests-01.trx` (negativ) | `436822bfbbf976952b0d0813188db4a41e7dee48502c7247ab9d2c1d37c51358` |
| `browser-01\private-api.log` (negativ) | `3dc742954f2ab8af5e369d5483ec5a1285134d745b7d445017aaa544aaa51507` |
| `browser-02\e2e-results.xml` (negativ) | `be69b06bb8a0aaf9ba274597f43c32164444fa94876f576b11880e709b9e36d6` |

Tatsaechliche HTML-/JS-/CSS-Bytes, Health, autorisierte lesende Projektionen
und effektive sichere Overrides wurden nach Umschaltung bestaetigt.
Nutzerstaende, Accounts, Schluessel, Originalobjekte und alle sechs
Rueckfallpakete sind erhalten. Kein Seed, keine Migration/Datenkorrektur,
kein echter Versand, keine Live-Foundry-/Cloud-/Load-/Recovery-Ausfuehrung
und kein Zugriff auf den Nutzerbrowser `cohors-tryout`.
EUDAMED/Domibus mit lokalem Simulator ist der ausdruecklich priorisierte
naechste Auftrag, nicht durch diese Nachforderungsabnahme implementiert.
Umfassendere Reviewhistorie und echter Dokumentimport bleiben danach offen.

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

## EUDAMED-Korrespondenzfristen: gebundener Zwischenstand

Der Stand `ee4712a96f1c0f54160496659cbfa6ba9a063267` wurde im vorhandenen,
sauberen Verifikationscheckout reproduziert. Nach dem echten Compilerfehler
von Build22 und dem unveraenderten 180-Sekunden-Gesamtbuildtimeout von Build23
wurden alle elf Projekte explizit in Abhaengigkeitsreihenfolge mit `Rebuild`
neu kompiliert: zusammen 303,642 Sekunden, jeweils innerhalb der separaten
180-Sekunden-Projektgrenze. Build24 pruefte anschliessend den unveraenderten
vollstaendigen Buildpfad: frischer Web-Build ohne inkrementellen Cache,
Locked Restore, Loesungsbuild, API-Publish, 39/39 Bootstrap-Tests ohne Skips
und Bootstrap-Publish. Der anschliessende Loesungsbuild ist kein unabhaengiger
kalter Neuaufbau; die expliziten frischen Projektpartitionen gehoeren zu
seiner Nachweisbasis. Build22/23 bleiben negativ.

Die folgenden getrennten, begrenzten Ausfuehrungen sind an Build24 gebunden.
Ein zusaetzlich gepruefter eigener Windows-Job schliesst SDK-/Testnachfahren
ein; Quell-, Paket-, XLSX-, Ergebniszahl-, Timeout- und Fixturebereinigungsgates
bleiben unveraendert. Native45 bis Native48 entfernten jeweils ihre ganze
eigene Fixture; die SQL-Baseline war vorher/nachher leer.

| Lauf | Tatsaechlich bestanden, ohne Skips |
| --- | --- |
| Offline43 | 12 Kalender-/Korrespondenzregeln, einschliesslich historischer Gruppenrechte und fehlender/widerspruechlicher Termine |
| Offline44 | 8 Recovery-Diagnose-/SQL-freie Vertragsfaelle; nicht die 17 nativen Recovery-Faelle |
| Native45 | 4 SQL-Workspacefaelle einschliesslich historischer Originale, PSUR-/BS-Termine und Prozesshistorie |
| Native46 | 1 Zwei-Rueckfragen-Durchstich: Ueberfaelligkeit, Watchdog-Dedupe und ausschliesslich fachlich quittierter, exakt adressierter Reply-Abschluss |
| Native47 | 2 MIR-Korrespondenzantworten mit neuen getrennten Freigaben |
| Native48 | 1 akzeptierte PSR-Vereinbarung, PSRP, Rueckfrage und neu freigegebener Reply; urspruengliche Vereinbarung und exakte Periode bleiben gebunden |
| Offline49 | 5 Mehrfixture-Journalvertraege, darunter neuer Journal-Elternpfad sowie Bereinigung trotz Journalfehler mit vollstaendigem Fehlererhalt |
| Offline50 | 15 PSRP-/Remote-Code-Faelle; alte gleiche/hoehere Sequenzen und unkorrelierte Antworten ersetzen keine aktuelle Annahme |

Browser15 verwendete dieselben Anwendungsbinaerdateien und nur die
Browserquellkorrektur `b5698387f84ce2f260a1da64f32e8601fd69b0af`
(UTC-Anzeige und UTC-Monat im Kalender). **Der Gesamtlauf ist fehlgeschlagen:**
E03 ueberschritt die unveraenderte 210-Sekunden-Fallgrenze; E04 bestand in
82,585 Sekunden mit bytegleichen Originaldownloads und Kontenwechselisolation.
Die erstmalige Navigation vom Kalender zum Original gelang und der Reply
erreichte die fachliche Annahme, aber die abschliessende Kalenderpruefung
wurde nicht vollstaendig ausgefuehrt. Es gibt keinen E03-Erfolgsbeleg.
327 abgeschlossene E03-Seitenantworten enthalten keine HTTP-Fehler, aber
19 Antworten ueber fuenf Sekunden; abgebrochene/unvollstaendige Anfragen
und separate Setup-API-Aufrufe werden damit nicht beurteilt. Daraus folgt
weder ein fehlerfreier Requestpfad noch eine belegte einzelne Latenzursache.
Die 600-Sekunden-Aussengrenze wurde nicht erreicht; Executionexit 1 und
unabhaengiger Cleanupexit 0. Alle exakt erfassten eigenen Ressourcen wurden
entfernt. Keine Kalender-Browserabnahme und keine Preview-Promotion.

Die unveraenderte versionierte FDA-XLSX ist die Eingangsdatei dieses sauberen
Checkouts. Die separat erhaltene fremde, rechtegeschuetzte Arbeitskopie bleibt
ausserhalb der Nachweisbasis unveraendert; der Lauf akzeptiert nicht den
schmutzigen Ursprungsarbeitsbaum. Es wurden weder Schutzattribute geaendert
noch Dateien entschluesselt oder Git-Ausnahmen zum Gruenmachen verwendet.
Die privaten Originalbelege unter `.local/eudamed-integration-01` werden
nicht als Rohlog-/Konfigurationssammlung veroeffentlicht.

| Beleg | SHA256 |
| --- | --- |
| `build-24-project-partitions.json` | `eb4d6072098fd0a4c7c3157e43eedaf5bccc4c53a14be9103878bdf430025aa6` |
| `build-24-result.json` | `2474a7f9add538d22c50f5665b217e101bcf9adbe4e0e2767743f63a1c6eb330` |
| `build-24-owned-job.json` | `d270b4992f8889cfc28ed1ce512715c3bdadf4402994f5118ddd2f17e3e1f49a` |
| `offline-43-result.json` | `dff838226154fb367cb45ca919d21e286c1f130b1dd45bfdfce722e39b0480bf` |
| `offline-44-result.json` | `2aaf472218cfbe8e8d263ab71982139bc74eb68f43bef4bb560f19e0f2915aa5` |
| `tests-45-result.json` | `65c1a61dac62c2027573fad011b0158e8d87b28db2b03edac6782b34e69c34a0` |
| `tests-46-result.json` | `dd9aa3a44cedfa493efcca63abb2626fdb8c7eb33433ad8f6c1b483e8e4379c7` |
| `tests-47-result.json` | `8c87cec071a23a2507965f6a9f9f4e87e957c702d06f78de48c43fd42f6bd09f` |
| `tests-48-result.json` | `8a2747df8e0db6d2c116e57b015de22cbc909e096e1ad6bdc6e317e03c022b5d` |
| `offline-49-result.json` | `0cefe885c0f6bf7e7af2e683575b3d249783ae5495459655d77a905ab0f0f13d` |
| `offline-50-result.json` | `9b41a92c43eab09bf8b7340976a68bab414caaf2d3b7dcfb529113439fc73dc3` |
| `browser-15-bounded-result.json` (negativ) | `f801cea3301c0d3bf8d0f02e7705a4e94d9aa53ecf17810e2e14bc6fb3e25661` |
| `browser-15-bounded-cleanup.json` | `1fd94046a8f7613daa30c2afb7d87e3b3e4caf589d38841309467f3da8710b52` |

### Vollstaendige Workspace-Originale und weiterer negativer Command-Durchstich

`445efc75c0b79dcfa9f23489593df0ddb8be02dc` liest die vollstaendigen
Workspace-Originale sequentiell in Spaltenreihenfolge. Serializable-Snapshot,
Versions-/Hashpruefungen und historische Produktberechtigungen bleiben
erhalten. Build25 ueberschritt trotz erfolgreichem frischem Web und Locked
Restore die unveraenderte 180-Sekunden-Gesamtbuildgrenze und bleibt negativ.
Elf explizite frische Projekt-Rebuilds bestanden danach in 518,315 Sekunden,
jeweils innerhalb 180 Sekunden und insgesamt innerhalb der eigenen
600-Sekunden-Partitionsgrenze. Build26 bestand anschliessend alle unveraenderten
Komplettgates einschliesslich erneut frischem Web, Locked Restore, Loesungsbuild,
beiden Publishes und 39/39 Bootstrap-Tests. Die frischen Partitionen gehoeren
zu diesem Nachweis; der nachfolgende Loesungsbuild ist kein zweiter kalter Build.

Native51 bestand **4/4 SQL-Workspacefaelle ohne Skips** auf genau Build26.
Der anschliessende einzelne Zwei-Rueckfragen-/Watchdog-Fall Native52 erreichte
hingegen die unveraenderte 180-Sekunden-Grenze: **kein abgeschlossener TRX,
kein Testpass**. Der eigene Prozessbaum und seine Ausgabestroeme waren terminal.
Die eine dauerhaft journalisierte Fixture blieb zunaechst bestehen.

Ein eng begrenzter read-only Restbefund wurde gespeichert; dessen abschliessende
Konsolendarstellung schlug wegen eines fehlenden optionalen Diagnosefelds unter
StrictMode fehl. Die gespeicherten Daten zeigen Fortschritt bis zur fachlichen
Antwort und Projektion des Originals, nicht den abgeschlossenen Reply-Zyklus
oder eine bewiesene einzelne Stallursache. Die nachfolgende unabhaengige,
auf 60 Sekunden begrenzte Bereinigung pruefte Journal, DB-Erstellidentitaet,
fehlende Benutzerverbindungen, Handlepfade und alle 61 Objektdateihashes.
Nur diese eigene Datenbank, die einzeln benannten Dateien und die danach leeren
ganzen Fixtureverzeichnisse wurden entfernt; die fremde SQL-Baseline blieb
erhalten. Bereinigung macht Native52 nicht nachtraeglich erfolgreich.

Ein anschliessender synthetischer Command-Zeilenvergleich brach am
Ausfuehrungsgate ab. Weder Messresultat noch aeusserer Ausfuehrungsdatensatz
wurden gespeichert, weil der Aufrufer vor dessen Serialisierung warf; nur
leere Ausgabelogs liegen vor. Der konkrete Ausfuehrungsfehler ist nicht
rekonstruierbar, der zweite Messmodus wurde nicht gestartet. Hieraus folgt
weder ein Performanceergebnis noch ein erfolgreicher Containmentnachweis.
Die anschliessende sequentielle Command-Lesekorrektur ist deshalb eine
quellengestuetzte Vereinheitlichung, kein aus diesem Vergleich belegter
Latenz-Fix. Neue Quellen benoetigen erneut eigene Build-/Fach-/Browsernachweise.

| Beleg | SHA256 |
| --- | --- |
| `build-25-result.json` (negativ) | `e0a78fb28a4c8574e1db67c17e3567eed8170db9b42dc2990a50b68e3274c256` |
| `build-26-project-partitions.json` | `e043d7243e460f96f396b996d5f1655e8360e7f426b67161a2dfef03083ea75c` |
| `build-26-result.json` | `80cf27b3171e5848ef0ace747999611f1c9f8fa10269b882033874f11973184f` |
| `build-26-owned-job.json` | `0a55c4cddd151dc412049f97bd37a97735fe0090cb27e5a584919caec0687d96` |
| `tests-51-result.json` | `cbe7017763d44c6720602cc21c852117ae16c2d2ca110dc13bd40ee59247bf58` |
| `tests-52-result.json` (negativ) | `186143c02e12ebf00496cfe0c73dc68f00d1f12db2dc766cf66a087378c9c200` |
| `tests-52-residue-inspection.json` (Daten; Darstellungsfehler separat) | `6082bf827faabb2e011b5875d6b811fe7226ee25fb63a459ac984e3ec1a882f9` |
| `tests-52-bounded-cleanup.json` | `fc127f22102b1ba1be7524dc73d488bc79f73ac230a03b92a30d81b0be3139d5` |
| `tests-52-cleanup-contained.json` | `cab9e3d0b44445e4115a66ef258f1c5ac3c82bb325f5c94208d8dc6f686e193f` |

### Command-Lesungen, Zwei-Rueckfragen-Watchdog und getrennte Diagnosepartitionen

`0ebe7600a8f9ca87272e45641cd36d501aa84a50` erweitert das vollstaendige
sequentielle Lesen auf aktuelle Command-Aggregate, Abhaengigkeiten und
gespeicherte Idempotenzantworten. Build27 scheiterte nach fuenf erfolgreichen
frischen Projekt-Rebuilds an der unveraenderten 180-Sekunden-Grenze des
API-Projekts. Zwei einzelne API-Diagnosen bestanden danach; erst die zweite
setzte explizit dieselbe Isolationsumgebung. Diese Messungen beweisen keine
einzelne Ursache der schwankenden Laufzeit. Build28 blieb wegen seines
180-Sekunden-Loesungsbuild-Timeouts ebenfalls negativ.

Elf erneut frische Rebuilds bestanden fuer Build29 in **391,069 Sekunden**,
jeweils innerhalb 180 Sekunden und insgesamt innerhalb 600 Sekunden.
Der nachfolgende komplette Build29 bestand frisches Web ohne inkrementellen
Cache, Locked Restore, Loesungsbuild, API-Publish, **39/39 Bootstrap-Tests**
und Bootstrap-Publish mit terminalem eigenen Prozessbaum. Die vorherigen
frischen Partitionen gehoeren zu diesem Nachweis, nicht zu einem behaupteten
zweiten kalten Loesungsbuild.

Native53 bestand auf genau diesem Stand **3/3** Command-/Versions-/
Idempotenz- und Abhaengigkeitsrennen. Native54 bestand den vollstaendigen
**einen Zwei-Rueckfragen-/Watchdog-Fall**: RA-Eskalation und Dedupe,
Produktscope, Queue-/ACK-Nichtabschluss sowie Abschluss nur des fachlich
angenommenen exakten Reply-Ziels. TRX-Falldauer **49,794524 Sekunden**,
TRX-Suitenintervall 63,214335 Sekunden, Runnerintervall 75,963223 Sekunden,
aeusserer eigener Job 89,275354 Sekunden. Der damalige Runner speicherte
keine separate native Prozessendzeit. Keiner dieser Werte ist deshalb
eine erfundene exakte Prozessdauer oder reine Rueckfrage-bis-Antwort-Zeit.
Der 180-Sekunden-Waechter umfasst den gesamten nativen Testprozess,
einschliesslich Hoststart, Fixture/Migration/Seed, Assertions, Teardown und
TRX-Erzeugung. Vor-/Nachpruefungen sind davon getrennt; der aeussere
900-Sekunden-Waechter erweitert nicht die native Grenze. Die Testuhr wird
fuer die Ueberfaelligkeit vorgestellt, keine reale Behoerdenwartezeit gemessen.

Der andere nachfolgende **MIR-Zweierlauf Native55 blieb ein 180-Sekunden-
Timeout ohne abgeschlossenen TRX oder Teilpass**. Seine eine exakt
journalisierte Restfixture wurde read-only erfasst. Der erste unabhaengige
60-Sekunden-Cleanupversuch lief ebenfalls ab. Ein danach phasenprotokollierter
Versuch behielt dieselben 60 Sekunden und saemtliche Schutzgates und bestand
in 33,120 Sekunden: SQL-Erstellung innerhalb des originalen UTC-Journalfensters,
keine Benutzerverbindung, 72 einzeln hashgepruefte Objekte und ganzer
Fixture-Elternordner entfernt, alle fuenf anderen Datenbanken und beide
Workbookbindungen erhalten. Beide negativen Laeufe bleiben negativ.

Die anschliessenden Diagnosepartitionen Native56 (Comment, 40,420774 Sekunden
TRX-Falldauer) und Native57 (InformationRequest, 31,749269 Sekunden) bestanden
je **1/1** mit vollstaendiger eigener Bereinigung. Sie ersetzen **keinen**
bestandenen gemeinsamen Zweierprozess innerhalb 180 Sekunden. Die lediglich
vorbereitete private Browser-v9-Partitionenzusammenfassung wird dafuer nicht
verwendet; Browser16 wurde nicht als Anwendungsdurchlauf gestartet.
Der neue Quellstand `8e70d7f` ergaenzt eine sofort dauerhaft geschriebene,
monotone Fachzeitspur je Variante, getrennt vom unveraenderten Ressourcenjournal.
Der erste vollstaendige Build30 dieses Quellstands blieb ein historischer
180-Sekunden-Buildtimeout. Elf erneut frische Rebuilds bestanden danach in
462,114 Sekunden innerhalb derselben 600-Sekunden-Partitionsgrenze.
Build31 bestand anschliessend alle unveraenderten Komplettgates inklusive
39/39 Bootstrap; Offline58 bestand 7/7 SQL-freie Journal-/Timingvertraege.

Der Nutzer gab danach ausdruecklich **hoechstens 300 Sekunden ausschliesslich
fuer den gemeinsamen vollstaendigen Native55-Folge-Zweierprozess** frei.
Andere native Laeufe behalten 180 Sekunden, ebenso bleiben Build-, Browser-,
Cleanup-, Stop- und Streamgrenzen unveraendert. Native59 bestand auf
Build31 beide Originalvarianten **2/2 in einem Prozess**:
OS-Prozessdauer **174,661792 Sekunden**, monotone Wait-Dauer 174,635994 Sekunden,
Runner mit Vor-/Nachpruefungen 188,895516 Sekunden, aeusserer eigener Job
199,577415 Sekunden innerhalb unveraenderter 900 Sekunden. Die tatsaechliche
OS-Endzeit stammt aus `Process.ExitTime`, nicht aus dem Runnerabschluss.
Die TRX-Falldauern waren 61,541472 Sekunden (InformationRequest) und
80,338187 Sekunden (Comment). Alle 46 monotonen Fachphasenmarken sind
vollstaendig; der getrennte sechsphasige Ressourcenjournalvertrag und
die Abwesenheit des ganzen eigenen Fixture-Elternordners sind nachgewiesen.
Die Zeit verteilt sich hier auf Dossieraufbau, einzelne Fachschritte und
Fixture/Host. Weder erklaert dies Native55 rueckwirkend noch macht es aus
dessen Abbruch einen Pass. Keine Browser- oder reale Behoerdenabnahme.

| Beleg | SHA256 |
| --- | --- |
| `build-27-project-partitions.json` (negativ) | `2b6ed93681b168267acb3b5110f3e49368036088ec76d98c1f60f2351dbb8de3` |
| `build-28-result.json` (negativ) | `ec018e1599b3d9ec40be1bcf7e59647d42b96efcb99dc7d77609b95bdd03983b` |
| `build-29-project-partitions.json` | `8475026e4576613e0f0a8aaa189b43c335cb31c35fc2647faa62a5ed25142d22` |
| `build-29-result.json` | `75e0dc5f68a11600ebe5eda3f65f8cdb5b67f82fcdbcd2a1634e76212e663cf4` |
| `tests-53-result.json` | `81967d1eaa0c8fbb80966e7c00d7cf4a79743f98a84131c6fd3819665c100ab5` |
| `tests-54-result.json` | `368fcdbd1526367538088364410771fb79767b5e27a6196eeef9647b9805ba76` |
| `tests-54.trx` | `77361325675395d7a82104fa75cfbcf4857c132316ea2e6d27a0e72b56808301` |
| `tests-55-result.json` (negativ) | `1aa6d0c1b7e7422cfbf7c8b6cd5988b0b46f511812b0110ac3dc98166b65012d` |
| `tests-55-bounded-cleanup-02.json` | `b3432a22150e427f047a09c461cad4f8a0dd65765fe10ea5e7042f4623cc88d5` |
| `tests-55-cleanup-contained-02.json` | `f671e7b641406b53e3ba0fbccd4515c134297189bc61bb851d579bf34558a816` |
| `tests-56-result.json` (nur einzelne Diagnosepartition) | `ba519d9d5ac9d2309a97b63788be5a6b9dc60ee7f26f9a23f824bd502e0cbbbe` |
| `tests-57-result.json` (nur einzelne Diagnosepartition) | `1d958bd30418ad2b19e236382d1552e670d9982567fb8e0e07541ca30948d437` |
| `build-31-project-partitions.json` | `a2e0359fe82305b630cf108b9c9bcec3c611e2dd20d2e2c3cd95e7f3b380c1a0` |
| `build-31-result.json` | `d7047602050950910181ba97ba946b56de9313765c16fd2d4810669dcf0cbd95` |
| `offline-58-result.json` | `f8b2d7a0d39d825069c63167344d9d54127145a98a591fac953af0669808210c` |
| `tests-59-result.json` (vollstaendiger Zweierlauf) | `e9c0f1d2de9e01c9390f84fdbf6393ed13eb89bc64d91211a29abf13eb5b7e5c` |
| `tests-59.trx` | `db387e82fa279f893e021672e2cd7d983217a15bb68b02f62a0a66cb774e303b` |
| `tests-59-business-phases.jsonl` (nur private Metazeiten) | `4db8d21757fc939ee1493bfb7cf018e4a2fad01e9805f61e2602b86b7245d359` |
| `tests-59-phase-summary.json` | `b9902503f606af724b1e2d1425f6a931eb889e8047b9596bf4acb7748e9ff2b0` |

### Browser17: Vorlauf-Timeout und separat abgeschlossene eigene Bereinigung

Der nach Native59 gestartete unveraenderte E03/E04-Ausfuehrungspfad auf
8e70/Build31 blieb ein **600-Sekunden-Ausfuehrungstimeout**; auch seine
unabhaengige 60-Sekunden-Bereinigung lief in den Timeout. Es entstanden
weder Playwright-Log noch Testereignisse oder JUnit: keine
Browser-Assertion, kein Teilpass und keine UI-Abnahme. Die Paketbindung
wurde erst spaet gespeichert; API und sieben kleine Seedobjekte wurden
gestartet/erstellt, die Playwright-Aufrufstelle wurde nicht erreicht.

Nach getrennten negativen Cleanup-Diagnosen bestaetigte der letzte
unveraendert auf 60 Sekunden begrenzte eigene Job den Abschluss in
19,3946 Sekunden: die exakt zugeordnete Datenbank war beim Read bereits
abwesend (dieser Abschluss fuehrte kein DROP aus), alle neun eigenen
Dateien einschliesslich Zugang/Settings und sieben Objekten sowie der
gesamte Runtime-Elternordner wurden entfernt. Alle fuenf vorhandenen
Datenbanken blieben erhalten; keine eigenen Nachkommen/offenen Streams.
406 Quellfiles, 116 Paketfiles, die Handle-/Ownershipbindung und beide
unveraenderten XLSX-Bindungen wurden vollstaendig geprueft.

Die Diagnose grenzte Zeit in vollen Quell-/Paketbytepruefungen ein, nicht
in Git. Ein rein lesender 1-MiB-SequentialScan-Puffer ersetzte den
4-KiB-Standard bei weiterhin vollstaendigen SHA256-Pruefungen. Kein
Hash-/Dateisatz-Gate, Budget oder Betriebssystemschutz wurde gelockert;
keine eindeutige rueckwirkende OS-Ursachenbehauptung. Dispatchfehler,
Vorprueftimeouts und die spaetere explizite Bereits-abwesend-DB-Ablehnung
bleiben negative Belege, nicht nachtraegliche Erfolge.

| Privater Beleg | SHA256 |
| --- | --- |
| `browser-17-bounded-result.json` (negativ) | `2cdc64a91d38d2ad4d4a4f973e84d3e636dba8469afee639a38e86bd1f177ec8` |
| `browser-17-bounded-cleanup-06.json` | `1aa9104279afa745ffc7f078775d9b166db24498444167521a6c4e35384dd64d` |
| `browser-17-cleanup-contained-06.json` | `b4d6469f8d8f9c43b1913aaa26a69ea161b9d6d497121a63522d6c2c63ac34b3` |
| `browser-17-cleanup-06-phases.jsonl` | `0a71567cd5d5aa741306c82b155d2c316a1c2edf6320f1b354866ea553284068` |

X-12/X-15 waren zu diesem Zeitpunkt anschliessende Quellarbeit. Weder Native59 noch Browser17
belegen deren neuen Compiler-, SQL-, Export- oder UI-Stand.
Recovery17, die neue Browserabnahme sowie Security-/Releasegates bleiben offen.

### Build32 und X-12/X-15: Compiler-, Fach- und Exportnachweis

Auf `01345f9ff7bd28b34dd2e884f1d71ee6e809b9c4` bestand Build32 alle
Komplettgates: frischer Web-/TypeScript-Build, alle elf .NET-Projekte,
39/39 Bootstrap-Faelle, API-/Bootstrap-Publish, Assemblygleichheit und
vollstaendige Bindung aller 116 Paketdateien. Die elf vorherigen
Einzel-Rebuilds benoetigten zusammen 210,0667954 Sekunden bei einem
600-Sekunden-Gesamtbudget; ihre einzelnen 180-Sekunden-Grenzen blieben
erhalten. Der anschliessende Web-Build benoetigte 32,6996271 Sekunden,
der Gesamt-Solution-Build 45,5290609 Sekunden.

Die 28 Abhaengigkeitsmanifeste/Lockfiles waren gegen den tatsaechlich
erfolgreichen Locked-Restore von Build31 byteidentisch. Deshalb wurde
dieser vorhandene Hauptprojekt-Restore explizit gebunden wiederverwendet.
Das belegt **keine erneute explizite Hauptprojekt-Restore-Stufe**, nicht
die Abwesenheit aller SDK-internen Restores. Der gemessene
Prozessrunner blieb byteidentisch; nur volle SHA256-Lesungen verwenden
nun den bereits nachgewiesenen 1-MiB-SequentialScan-Puffer. Keine
Dateisatz-, Quell-, Paket-, XLSX-, Handle- oder Cleanupausnahme.

Die spaetere Nachpruefung der Build34-/Build35-MSBuildlogs und des
installierten `Microsoft.Azure.Functions.Worker.Sdk`2.1.0 zeigt:
`_FunctionsInnerBuild` ruft `_WorkerExtensionsRestore` auf; dieses Target
restoret das generierte `WorkerExtensions.csproj` auch bei einem
Top-level-`--no-restore`. Seine `RestoreTask`-Zeiten betrugen in Build34
4,300s/3,487s und in Build35 22,061s/11,669s fuer Operations/Workflows.
Die Wiederverwendungsfelder der privaten Pruefprotokolle beziehen sich
auf die elf Hauptprojekt-Assetgraphen, nicht auf diese generierten Projekte.
Ein tatsaechlicher Paketdownload wurde damit weder nachgewiesen noch
ausgeschlossen. Alte Ergebnisdateien bleiben unveraendert; SDK-Targets,
Analyser und Abhaengigkeitspruefungen werden nicht deaktiviert.

Offline60 und Native61 bis Native67 bestanden **zehn unterschiedliche
Faelle ohne Skips**. Native63 bis Native67 besassen jeweils sowohl eine
Collection- als auch eine eigene Fixture; alle insgesamt zwoelf
SQL-Fixtures wurden einschliesslich ganzer Elternordner entfernt.
Jeder Sechsphasenjournalabschluss, die Baselineerhaltung sowie terminale
eigene Prozesse und geschlossene Streams wurden geprueft.

| Lauf | Tatsaechlicher Umfang | Prozessdauer / Limit |
| --- | --- | --- |
| Offline60 | 3 SQL-freie Monats-/Altmanifestfaelle; keine SQL-Fixture | 38,1444535 / 180 s |
| Native61 | Gebundene Versandvorschau ohne Schreibwirkung; Unknown bleibt abgleichpflichtig | 30,2075022 / 180 s |
| Native62 | Neue Revision, Empfaenger- oder Profilbindung entwertet die alte Versandbestaetigung | 89,6974509 / 180 s |
| Native63 | Lesbares PDF, Monatsdaten und Exportpruefsummen; Bericht unveraendert | 40,8448833 / 180 s |
| Native64 | Falsches Produkt, veraltete Fassung und manipulierte Quellbindung abgewiesen | 54,7181804 / 180 s |
| Native65 | Historische Quellen unveraendert; unbelegbare Monate explizit nicht verfuegbar | 40,6532315 / 180 s |
| Native66 | Basic-UDI-Mitglieder durch Review und Export getrennt rekonstruierbar | 35,7829412 / 180 s |
| Native67 | Leitproduktberechtigung reicht nicht fuer Gruppen- oder Monatszugriff | 27,3409082 / 180 s |

Die Zeiten sind OS-Prozessstart bis OS-Prozessende einschliesslich
Testhost/Discovery, Fixtureaufbau, Fachschritten, Assertions, TRX und
Test-Teardown, **keine isolierten fachlichen Antwortzeiten**.
Wait-Budget, Stop-/Streamnachlauf und aeussere Vor-/Nachpruefungen sind
in den privaten Ergebnissen separat ausgewiesen.

X-12 liest die exakt freigegebene Vorbereitungsfassung per
`(Kind, Id, Version)` mit Roh-JSON-Hashpruefung statt Produkthistorien-
Vollscan. Die UI zeigt Paket, Bericht/XML, Originalbezuege, Empfaenger,
Jurisdiktion, Kanal/Umgebung und getrennte Freigaben vor der explizit
simulierten Sendebestaetigung. X-15 projiziert allein eingefrorene
Quellen: UTC-Ereignismonate, unbekannte Daten, historische Offenstaende,
Carry-over/Nachtraege und einzelne Basic-UDI-Mitglieder bleiben
unterschieden. Weder laufende Faelle noch Ersatzereignisdaten werden
nachtraeglich in genehmigte Monatszahlen aufgenommen; keine
Inzidenzbehauptung. Unverstaendige historische Manifeste werden nicht
repariert oder als Nullfallmonate ausgegeben.

Native68 pruefte auf demselben Build32 zudem **beide unveraenderten
MIR-Korrespondenzvarianten gemeinsam, 2/2 mit 46 sofort persistierten
Fachphasen**. Tatsaechlicher Prozess:
`2026-11-17T20:38:02.8876859Z` bis
`2026-11-17T20:39:14.4449400Z`, also **71,5572541 Sekunden / 300 Sekunden**.
Die eng begrenzte Zweierlauf-Ausnahme wurde nicht auf andere Laeufe
uebertragen; Native55 bleibt der historische 180-Sekunden-Timeout.

| Privater Beleg | SHA256 |
| --- | --- |
| `build-32-result.json` | `0883c394bda0b9b22627b472269093d42b840e56d7d7d841485cc05e47fd827f` |
| `build-32-owned-job.json` | `b9e8d83d73b90f780334e16c0a53ba1f6ccd50d99e2a2a77c0f05174761df342` |
| `x12-x15-native-02.json` (alle acht Einzelbindungen) | `ab1c570eaf436d91b4dc80c20f78281f8d412c6446cac694ba6c5d359d783449` |
| `tests-68-result.json` | `86005c7758af8d3f8261d218f20c370854f3a2bf8f0885713edc650db7f0ba20` |
| `tests-68-owned-job.json` | `5831ff47c27a42db1667cca5512c7753c2078a17e8fb6b15acdc9387076c817d` |

Die erste abgeleitete X-12/X-15-Zusammenfassung bleibt erhalten:
ihr Summenzaehler war durch eine PowerShell-Dictionaryprojektion null.
Fassung02 summiert ausdruecklich die unveraenderten zehn echten
Einzelresultate; kein Test wurde dadurch erneut ausgefuehrt oder
nachtraeglich umgewertet.

Dies ist **keine Browser-/Gesamtabnahme und keine Previewpromotion**.
Insbesondere sind X-12-Dialog/Abbruch und X-15-Monatssicht trotz
vorhandener Browserassertions hier noch nicht ausgefuehrt. E03/E04,
Recovery17, strikte Securitypruefung und weitere Releasegates bleiben offen.

### Browser18: falsche Testauswahl, Timeout und nachgewiesene Nachbereinigung

Browser18 band Anwendung01345/Build32 und Browserquelle0637089, erreichte
aber die unveraenderte Ausfuehrungsgrenze von **600 Sekunden**. Der
PowerShell-Auswahlausdruck lieferte fuer das einzelne Projektargument
einen skalaren String statt eines Arrays; das Splatting uebergab
`- - p r o j e c t ...` als einzelne Zeichen. Playwright selektierte
dadurch **31 statt zwei Tests**. Die inkrementellen Events enthalten
Abschluesse fuer V01, UI01 und UI02, aber keinen E03-/E04-Abschluss.
Dies ist weder ein bestandener Zweierlauf noch der Nachweis zweier
fehlgeschlagener EUDAMED-Faelle; keine UI-Teilabnahme wird abgeleitet.

Der eigene Prozess startete um `2026-11-17T21:15:49.0493097Z`.
Der Zehn-Sekunden-Stop konnte drei eigene Nachfahren nicht rechtzeitig
als abwesend nachweisen. Der Runner bewahrte deshalb keinen belegten
OS-Prozessendzeitpunkt und keine abgeschlossene Streambestaetigung;
sein spaeterer Abschluss ist **keine Prozessdauer**. Die unabhaengige
60-Sekunden-Bereinigung verweigerte korrekt vor Ressourcenmutation.
Ein spaeterer Pid-Abwesenheitscheck wertet diese Grenzen nicht um.

Eine separat gebundene Nachbereinigung pruefte alle Quell-/Paketbytes,
Original-Workbookbindungen und die vor CREATE persistierte Ownership.
Sie entfernte in 5,035 Sekunden nur die aufgezeichnete UUID-Datenbank,
den vollstaendigen privaten Runtimeparent und das eine aufgezeichnete
Chromiumprofil; die fuenf Baseline-Datenbanken blieben erhalten.
Ihr abschliessender Textformatter endete erst **nach** persistiertem
Erfolg mit Exit1, weil die optionale `error`-Eigenschaft unter dem
importierten StrictMode fehlte. Das negative Prozessergebnis bleibt
erhalten. Eine weitere, ausschliesslich lesende Verifikation bestaetigte
mit Exit0 nach **2,155 Sekunden / 60 Sekunden** die entfernten Ressourcen,
alle abwesenden aufgezeichneten Pids und den freien Testport5197.
Die freigegebene Vorschau5187 wurde nicht veraendert.

| Privater Beleg | SHA256 |
| --- | --- |
| `browser-18-bounded-result.json` | `cc7c4bb1a7a8828c94b158c4cc98fad26278a2da51ee213c4531b8118c89d0f1` |
| `browser-18-recovery-01.json` | `d928027c722b72fff684bf6487dbf461491f75bdc9eccb9b355bf33a51998236` |
| `browser-18-recovery-owned-01.json` | `87a4452a566810bae26849b9d8771e839985ba873c6c0a0845c60d21bdf5e40e` |
| `browser-18-recovery-verification-01.json` | `05d54f1386c129f711322822f5b761a08e1ce3c7efa46c8a1ee9e3a2073120b8` |
| `browser-18-recovery-verification-owned-01.json` | `1b3af5ca4d79fdbb8719de646a9acd376005fdd55ae319427ddad10cbdc71f7e` |

### Browser19: korrektes Zweierprofil, E04 bestanden, E03 noch offen

Auf Anwendung01345/Build32 und Browserquellea951697 wurden nach der
Auswahlkorrektur tatsaechlich nur E03 und E04 ausgefuehrt.
**E04 bestand in 105,373 Sekunden**: heruntergeladene PDF- und
Audit-ZIP-Originale waren bytegleich, historische Originale blieben
erhalten und der tatsaechlich verzoegerte Originalrequest wurde beim
echten Logout-/Login-/Produktscopewechsel abgebrochen. Es entstand kein
spaeter Download; der Server verweigerte dem neuen Konto den Quellzugriff.

**E03 scheiterte vor der eigentlichen Dossierstrecke** in der
Simulatoraktivierung: Der Actor blieb waehrend der unveraenderten
25-Sekunden-Pollassertion `Submitted` statt `SimulatedApproved`.
Ergebnis: 1 Passed, 1 Failed, 0 Skipped; keine EUDAMED-Gesamtabnahme.
Der eigene Gesamtprozess lief von `2026-11-17T21:46:07.9413234Z`
bis `2026-11-17T21:55:16.8838607Z`, also **548,943 / 600 Sekunden**,
ohne Prozess-Timeout. Die unabhaengige Bereinigung bestand mit Exit0
nach **20,850 / 60 Sekunden**. Beide eigenen Prozessjobs und ihre
Streams waren geschlossen; DB, Runtimeparent und aufgezeichnete
private Browserprofile waren entfernt, die fuenf Baseline-Datenbanken
und saemtliche Quell-/Paket-/Workbookbindungen blieben erhalten.

Die Quellenpruefung fand eine konkrete Abweichung im gemeinsamen
SQL-Pfad: Der Browserrunner verwendete denselben ConnectionStringBuilder
fuer Administration und Anwendung. `Pooling=false` fuer `master` wurde
dadurch unbeabsichtigt in die App-Konfiguration kopiert; die unveraenderte
Quellkonfiguration verwendet dagegen den normalen Pooling=true-Default.
Getrennte Builder erhalten jetzt die Anwendungseinstellung, waehrend
die Administration ungepoolt bleibt. Der private Fuenf-Sekunden-
Verbindungsaufbaugrenzwert wurde nicht veraendert. Drei SQL-freie
Regressionen am echten Konstruktorblock bestaetigen Default, explizit
ein- und ausgeschaltetes Pooling, weiterhin ohne geoeffnete Verbindung.
Die alleinige Kausalitaet fuer E03 ist damit **noch nicht gemessen**;
keine Assertion, fachliche Strecke oder Laufzeitgrenze wurde gelockert.

| Privater Beleg | SHA256 |
| --- | --- |
| `browser-19-result.json` | `055cf774fdb95cd4656aea319ac06a20e0fe277d9f2fa164d9e3b7c6977a9257` |
| `browser-19-bounded-result.json` | `d76376221276a17a3c324e82cff1c04604938268787adaf26601783dee7b9fd5` |
| `browser-19-bounded-cleanup.json` | `8b66278bc441553face14abfab8e3efd93d255722d6ca5796a911489d96a3a28` |
| `.local\v10\.local\eu-u19\e2e-results.xml` | `8827639818d35c94024f4e749a99ec796890e1905c1fb7d14937169510566352` |
| `.local\v10\.local\eu-u19\eudamed-original-download-proof.json` | `b847a48847c0dc255748b5294ba3e13e8408b23115355760fa65c3e2b76ad662` |
| `browser-sql-policy-01.json` | `ff2d5f97704dabfe41bfeab76a774f3c201956d097b0c4db1896b85fbe7ce1a1` |
| `browser-resource-journal-04.json` | `ce20d2b230add509bf48746726034bbb921b40d011b86e05b449334b76b6187c` |
| `browser-boundary-check-headless-20.json` | `1133ae61064b29d289acfc3cae72fda92040be4c02e6cdb3e63e828b49b2ec07` |

### Browser20: erster fachlicher Eingang zu langsam, E04 erneut bestanden

Unveraendert Anwendung01345/Build32 und Browserquellea951697; die private
SQL-Konfiguration erhielt diesmal nachweislich Pooling=true fuer die
Anwendung und Pooling=false fuer die Administration. E03 durchlief
Actoraktivierung, Dossiererstellung, getrennte Freigaben und Queue,
scheiterte jedoch beim ersten `receiveDossierUi` an der unveraenderten
25-Sekunden-Dialogassertion. Das gelesene Fehlerbild zeigt eine
ausgefuellte Begruendung mit aktivem Spinner, kein Pflichtfeldproblem.

Der zugehoerige `/simulator`-POST lieferte HTTP200-Header erst nach
**26,488361 Sekunden**. Der bestehende Browserhook misst ausdruecklich
`request.timing().responseStart` bei `page.response`: Das ist die Zeit
bis zu den Antwortheadern, **nicht** das Ende des Responsebodys oder die
rein fachliche Verarbeitungszeit. Die 206 E03-Beobachtungen enthalten
keinen HTTP-Status ab400 und 16 Headerzeiten ueber fuenf Sekunden;
Requests ohne beobachtete Antwort und Abbrueche sind damit nicht
bewertet. Die Pooling-Korrektur allein schliesst den Engpass nicht.
Es wurde keine Assertion oder Prozessgrenze vergroessert.

E04 bestand erneut in **128,659 Sekunden**, einschliesslich bytegleicher
PDF-/ZIP-Originale, historischer Originalerhaltung, tatsaechlichem
Logout-/Login-/Scopewechsel, abgebrochenem alten Download und
serverseitiger Verweigerung fuer das neue Konto. Insgesamt weiterhin
**1 Passed, 1 Failed, 0 Skipped**, keine EUDAMED-Gesamtabnahme.
Der eigene Prozess lief von `2026-11-17T22:06:25.2208211Z` bis
`2026-11-17T22:15:11.5863736Z`, also **526,365553 / 600 Sekunden**,
ohne aeusseren Timeout. Die unabhaengige Bereinigung bestand mit Exit0
in **16,994985 / 60 Sekunden**. Alle eigenen Prozesse und Streams waren
geschlossen; Datenbank, Runtimeparent und beide privaten Browserprofile
waren entfernt, die fuenf Baseline-Datenbanken sowie Quellen, Paket und
Workbook blieben unveraendert. Keine Vorschaupromotion.

| Privater Beleg | SHA256 |
| --- | --- |
| `browser-20-result.json` | `1745f998c4a48bc4124d84da864cbf735521c787416d5aeeb93bab456289b28f` |
| `browser-20-bounded-result.json` | `0f6ec7e3dd3f8d712a2b036bc4e566bd663e2f7e30866f9dfbf42955678f5c25` |
| `browser-20-bounded-cleanup.json` | `bb17eb433998b4310a6f1b64565f4c34b8116a37f29d53d62480a7fcfabdebfb` |
| `.local\v10\.local\eu-u20\e2e-results.xml` | `bf5db75a86f3b52ff7d408f990c5a6c4a305e288b5a63932518953425fca51d3` |
| `.local\v10\.local\eu-u20\eudamed-request-timings.jsonl` | `2bee358272f6a01197c1c75546824e7dd8c29eb4300a6a698c893716df884091` |
| `.local\v10\.local\eu-u20\eudamed-original-download-proof.json` | `0d59b9b132b4f77ccb3b5dd06993ee92984aea2f3e2dc5fb53535714623533ac` |

### X-15: numerische Browserassertions und geschlossene Auswahlluecke

Die browserexklusive Quelle `a951697f097cdd2053e305d84f7b09ac375796b2`
erweitert den bestehenden befuellten Gruppenfall T16: Fuer dieselbe
Berichtsfassung und das jeweils gewaehlte Mitglied stammen die Sollwerte
aus `/psurs/{id}/months?version=N&productId=P`. Geprueft werden die
geordneten Monats-/Periodenzeilen, Basisfallzahlen, offene und unbekannte
Stichtagsstatus sowie Beschriftungen und Zahlenpunkte im Diagramm.
Der vorhandene Mitgliederwechsel muss auch die zugehoerigen Zahlen
wechseln; die vorherige Basic-UDI darf nicht stehenbleiben. Tabellen-
und Diagrammsichtbarkeit werden ausdruecklich geprueft. Keine zweite
fachliche Monatsformel und keine neue Fixture ersetzen den Gruppenfall.

Der zusaetzliche strikte E2E-TypeScript-Aufruf scheiterte zuerst an vier
bereits in01345 vorhandenen DOM-/Node-Timer-Typfehlern in
`blockReviewPolling`. Eine typisierte `Window`-Referenz erhaelt dieselbe
Browser-Timerfunktion ohne Cast oder Unterdrueckung. Der anschliessende
vollstaendige E2E-Typecheck bestand nach **25,289 Sekunden / 120 Sekunden**.
Die Anwendung selbst bleibt unveraendert01345/Build32.

Der private Runner verwendet jetzt typisierte Argumentarrays und prueft
vor SQL-/Runtimeerzeugung die tatsaechliche Playwright-`--list`-Antwort:
genau die beiden erwarteten Titel im ausgewaehlten Projekt, keine
fremden oder ausgelassenen Faelle. Beide Profilauswahlen wurden
SQL-/API-/browserfrei mit je zwei echten Testdefinitionen geprueft;
eine falsche Auswahl wurde abgewiesen. Die vier vorhandenen
Ownership-/Fehlererhaltungschecks bestanden erneut mit ausschliesslich
simulierter SQL-Grenze. Das ist **keine Ausfuehrung der UI-Faelle**.
600/60/10/3-Sekunden-Grenzen, vollstaendige fachliche Faelle und die sechs
Ressourcenjournalphasen bleiben unveraendert. Neue Runner zeichnen das
tatsaechliche Prozessende auch unabhaengig von einem Nachfahren-Stopfehler
auf, soweit der OS-Prozess wirklich beendet ist; historische Belege
werden nicht nachtraeglich ergaenzt.

| Privater Beleg | SHA256 |
| --- | --- |
| `psur-month-browser-typecheck-02.json` | `117ce88c3fccf5942778f5b40eed59b73c44a95ec1d1ca1e9d271059b08e8b3b` |
| `browser-selection-01.json` | `b018dba9afec684c9481937cfb11592e8eb2facc1ff0704ad9ae31aef99b46db` |
| `browser-resource-journal-03.json` | `9bbbdfe023dfe4abf0d60d11426229361a7b2e67e7ef1b2781fffcf7c1017dbc` |
| `browser-boundary-check-headless-19.json` | `7757ab5b827a3f8a4d3ca954b23424bad9d0ac81313bd7e311d0010675af6f58` |

### Browser21: X-12 und X-15 im vollstaendigen Zweierprofil bestanden

Auf derselben Anwendung01345/Build32 und Browserquellea951697 bestand
das exakt selektierte Profil `PrimaryBaseline`: **2 Passed, 0 Failed,
0 Skipped**. Der vollstaendige Beschwerde-/Review-/Unknown-/Reconcile-/
Auditfall bestand in **41,794 Sekunden**, der vollstaendige befuellte
Gruppen-PSUR-Fall T16 in **68,052 Sekunden**. Die Fallgrenzen blieben
150 bzw.240 Sekunden; keine Retries oder Ersatzfaelle wurden ergaenzt.

Damit sind die vorstehend beschriebenen numerischen Monatsassertions
tatsaechlich ausgefuehrt: beide gewaehlten Basic-UDI-Mitglieder, die
genau gebundene Berichtsversion, geordnete Periodenausschnitte und
Fall-/Offen-/Unbekanntzahlen sowie alle dazugehoerigen Diagrammwerte.
Die Werte stammen aus der jeweiligen versionierten API-Antwort;
mindestens ein Monat ist befuellt. Der Mitgliedswechsel, der
vollstaendige Gruppenscope, Downloads, kontrollierte Nachbearbeitung,
unveraenderte Snapshotbytes und erneut getrennte Freigaben bleiben
Teil desselben Falls. X-12 wurde im unverkuerzten Beschwerdefall
einschliesslich gebundener Versandvorschau, Abbruch ohne Sendung und
Unknown-/Reconcile-Schutz geprueft.

Der eigene Gesamtprozess lief von `2026-11-17T22:23:56.7021262Z` bis
`2026-11-17T22:27:16.2571288Z`, also **199,555003 / 600 Sekunden**.
Die unabhaengige Bereinigung bestand mit Exit0 in
**19,933655 / 60 Sekunden**. Beide Prozessjobs und Streams sind
geschlossen; Datenbank, kompletter Runtimeparent und privates
Browserprofil wurden entfernt, die fuenf Baseline-Datenbanken sowie
Quell-/Paket-/Workbookbindungen blieben erhalten.

Dies ist eine isolierte lokale Funktionsabnahme dieses X-12-/X-15-
Schnitts, **keine Promotion**: Die separate freigegebene Vorschau
bleibt742db5d. E03/EUDAMED, Recovery17, Security-/CI-/Last- und die
vollstaendige Baseline-/Betriebsabnahme bleiben getrennte offene Gates.

| Privater Beleg | SHA256 |
| --- | --- |
| `browser-21-binding.json` | `6cc5ebf457f016407fcbd7335851c6857eff72ed1f846ce2154f2ac6b467bea8` |
| `browser-21-result.json` | `378cf4d767dcb6fb24becbbb81de86f8b3166d769b0a82e630bb5317af757660` |
| `browser-21-bounded-result.json` | `e35c671c0260c42d6ddb3654d45429acd46c6dcb4954865d7a32bf553a39c4da` |
| `browser-21-bounded-cleanup.json` | `069090d918d64ef12b9e55f56cf45c0c414d7a328abf68f362c2c99e550b6781` |
| `.local\v10\.local\eu-u21\e2e-results.xml` | `215dba2328230c4ee2ce314fcf3da2e881427766fb088f07db04b4eeec4864f9` |
| `.local\v10\.local\eu-u21\owned-resources-terminal.json` | `1396a21a47da9144d891015e4324daff67be52854b7f85a13f60240be526ab90` |

### Verbleibender Outbox-Lesepfad und negatives Build33-Gate

Der weitere E03-Ursachenpfad umfasst den gemeinsam genutzten SQL-Pfad,
nicht nur Rendering oder Polling. Die aktuellen und gezielt historischen
Aggregatleser verwenden bereits sequenzielles Textlesen; `ApprovedDossier`
liest das exakt gebundene Original und keine gesamte Produkthistorie.
`PendingEvents` und `ClaimEvent` lasen dagegen noch gepufferte
`nvarchar(max)`-Payloads. Der lokale Worker verwendet diese Leser auch
waehrend der HTTP-Kommandos.

Commit `9845b3f0b696a2e5f83147bdd1d7ac4ce6ccba28` stellt nur diese
beiden Leser auf den bestehenden `SequentialAccess`-/`ReadText`-Pfad
um. SQL-Abfragen, Batchgrenze, Spalten, Lease, Backoff, Attempts und
Idempotenz bleiben unveraendert. Ein gezielter neuer Outboxfall prueft
eine grosse Mehrpuffer-Payload einschliesslich Unicode/Escapes, die
nachfolgenden Metadaten, genau eine Lease und das abschliessende Receipt.
Dies ist eine eingegrenzte Korrekturhypothese, **noch kein gemessener
E03-Latenz- oder Browsererfolg**.

Build33 auf diesem Stand scheiterte mit Compiler-Exit1 an `xUnit2031`
im neuen Test (`Assert.Single` mit vorgeschaltetem `Where`), nicht an
einem Timeout. Zehn Projekt-Rebuilds bestanden, das elfte nicht.
Alle elf Projektjobs und der aeussere Partitionsjob schlossen ihre
Streams ohne verbleibende Nachfahren. Publish, native Faelle und
Browser wurden deshalb nicht gestartet; es entstand keine SQL-Fixture.
Der eng gekoppelte Teststil ist in
`19961ee9a1d662b6ec98b12d9fb92e85135ab773` durch den bereits im Test
verwendeten Praedikat-Overload korrigiert, ohne Erwartung oder Budget
zu aendern. Build33 bleibt als negativer Beleg erhalten; sein Teilergebnis
ersetzt keinen erfolgreichen neuen vollstaendigen Build.

| Privater Beleg | SHA256 |
| --- | --- |
| `build-33-project-partitions.json` | `2abc4e9fb4e036e0f739b84a2c7ce03210b17e54d45a0e0696ef5e1a175968d7` |
| `build-33-partitions-owned-job.json` | `4f9108c300b605640f83a6306ee972526fcf96a902f2daff48e05f94e4be97c3` |

Der nachfolgende **Build34 auf19961ee** bestand alle elf neuen
Projekt-Rebuilds, frischen Web-/TypeScript-Build, Solution-Build,
API-Publish, **39/39 Bootstrapfaelle** und Bootstrap-Publish.
Die Anwendung liegt als separat gebundenes Paket mit116 Dateien vor;
es wurden keine alten Teil-Rebuilds als neuer Gesamtbuild gewertet
und keine Abhaengigkeiten neu installiert.

**Native69 bestand alle drei Outboxfaelle** auf diesem Paket:
der neue Mehrpuffer-/Metadatenfall sowie die unveraenderten
Lease-/Backoff-/Poison-/Recovery-/Receipt- und Stale-Lease-Faelle.
Der echte Testprozess lief von `2026-11-17T23:00:31.5090024Z` bis
`2026-11-17T23:00:51.7831265Z`, **20,274124 / 180 Sekunden**;
der neue Fall selbst dauerte1,843578 Sekunden. Die eine Fixture
durchlief alle sechs Ressourcenphasen und wurde samt komplettem
Objektparent entfernt; Baseline, Streams und Prozessjob sind geschlossen.
Dies belegt den Leser-/Leasevertrag, nicht die Ursache oder Behebung
der bislang zu langsamen E03-HTTP-Antwort.

| Neuer privater Beleg | SHA256 |
| --- | --- |
| `build-34-result.json` | `dd020a413cb93c16a3390dcf4c3959ee42c9f3d462d50e93e31c684f55872d68` |
| `build-34-owned-job.json` | `707cffeb678e7eb31a5062fc16f36fa11b09ae486b09eeea6c682bfbdcdf07b7` |
| `build-34-project-partitions.json` | `4171c9776fc7c6047a73ab3e754cc3a6490bca27d37028d6338a47fb6f041587` |
| `tests-69-result.json` | `8f1ae2cf8b06339b88d2a3cc75b6f5088ee5221dc12f67c21e5d3efad3892911` |
| `tests-69-owned-job.json` | `1ca62192fccd58df9bfbd9302f64920f501b05f109c6c5856b2637cf4f9ff3c7` |
| `tests-69.trx` | `a9905528d1b3ad3ab33320a0b861fb17ce77877eddf2213f6c908b24fcdfab93` |

Native70 selektierte anschliessend die unveraenderten zehn
X-12-/X-15-Faelle gemeinsam, erreichte aber **keinen Abnahmenachweis**:
Timeout nach180,048163 Sekunden Wait-Budget, kein TRX und kein
behaupteter Passed-Zaehler. Der echte Prozess begann um
`2026-11-17T23:02:46.266143Z` und endete nach dem begrenzten Stop um
`2026-11-17T23:05:51.9511766Z`, also **185,685034 Sekunden**.
Die Differenz umfasst insbesondere5,422010 Sekunden vor Beginn des
Wait-Budgets; Stop-/Stream-Nachlauf ist weiterhin separat erfasst.
Das Ressourcenjournal zeigt zwei Fixtures, deren erste allein
zwischen `SchemaReady` und `Ready`101,094171 Sekunden benoetigte.
Das ist kein Messwert einer fachlichen Rueckfrage-Antwort-Zeit.

Nach geschlossenem Testprozess wurden beide exakt journalgebundenen
Reste zunaechst rein lesend inspiziert und danach in einem getrennten
unveraenderten60s-Bereinigungsjob entfernt: beide Datenbanken sowie
52 bzw.35 bytegebundene Objekte einschliesslich beider Objektparents.
SQL-Erstellungszeit, fehlende aktive Benutzer, vollstaendige
Journalbindung, Quell-/Paket-/Workbookhashes und alle fuenf anderen
Datenbanken wurden geprueft. Die acht originalen Journalzeilen bleiben
unveraendert; externe Bereinigung wird nicht als normaler sechsphasiger
Testabschluss oder nachtraeglicher Erfolg ausgegeben. Die vorhandene
742-Vorschau blieb unangetastet. Ein naechster Featurebeleg verwendet
wieder die bereits etablierten gezielten Selektionen und keine
unveraenderte blinde Wiederholung dieses180s-Gesamtlaufs.

| Negativer Lauf und separate Bereinigung | SHA256 |
| --- | --- |
| `tests-70-result.json` | `2b46d9d5e8bc4f670a4ad7328bde278bb1423ea6260fc2d5c640bc5c9dc8321a` |
| `tests-70-owned-job.json` | `a57a2beadb9b221688cadb380ea24ff98da0f4c15ed978ba354235430e8fb141` |
| `tests-70-residue-inspection.json` | `84dc19816718c92626791463b3b592e739d5368aabb5856fe6847c76b05992f4` |
| `tests-70-inspect-contained.json` | `6f0bf8f6ae59bf7af7d5757b6d85cc5b055e1b90f73e226ec3f8dca55fa96471` |
| `tests-70-bounded-cleanup.json` | `116de75332fc7563c20e68cd68040555224e24bd28a72532987072dec389623d` |
| `tests-70-cleanup-contained.json` | `130bf82d507708ac49aaab83fa1eade138030e5aee56d3055f0ad9af4fe2ee55` |

**Native71** pruefte danach auf19961ee/Build34 beide unveraenderten
vollstaendigen Comment-/InformationRequest-Varianten gemeinsam:
**2 Passed, 0 Skipped**, mit46 dauerhaft geschriebenen Fachphasen.
Der echte Prozess lief von `2026-11-17T23:13:57.2191561Z` bis
`2026-11-17T23:15:28.8786435Z`, **91,659487 / 300 Sekunden**.
Wait-Budget-Messung91,643881 Sekunden und Streamnachlauf0,035316
Sekunden bleiben getrennt. Die einzelnen Testkoerper dauerten
36,369220 bzw.32,034166 Sekunden; sie sind keine isolierte
Rueckfrage-Antwort-Messung. Dossieraufbau, erste Annahme,
Rueckfrageeingang, Antwortentwurf, getrennte Freigaben, Queue/Transport,
fachliche Reply-Annahme und Abschlusspruefungen sind unverkuerzt
enthalten. Fixture/Seed und TRX/Teardown gehoeren zum Gesamtprozess,
nicht zu den einzelnen Fachphasen. Die einzige Fixture durchlief
alle sechs Ressourcenphasen; Datenbank und Objektparent wurden
entfernt, Job/Streams geschlossen. Dies ist ein neuer gebundener
Erfolg, keine Umdeutung des historischen Native55-Timeouts und keine
Addition der frueheren Einzelpartitionen56/57.

| Vollstaendiger Zweiernachweis | SHA256 |
| --- | --- |
| `tests-71-result.json` | `2ac6731f99d8142dcc15f38bd318c6dc07bc118ef0e7b30249195c71da1202cb` |
| `tests-71-owned-job.json` | `6c7a288d0a8b412e63e8d4f7469ebc5f0b1aa1d91e1173c47ce799a74aea80ef` |
| `tests-71.trx` | `9b7b66f81d865671d3607b0937d6bf2470c70a2d1bf0ad0a23da8eacdc3c0f1e` |
| `tests-71-business-phases.jsonl` | `9cee2c17a6de12fded1c7e6ad1d36c75c98997b78a45d953fb8c151423e25baa` |
| `tests-71-owned-sql.json.journal.jsonl` | `5145528507642aa9656e8bf6028dd26bf2fb5b1f400c1b17df9769709a772a5a` |

Die anschliessenden **Offline72 und Native73--79** prueften alle zehn
unveraenderten X-12-/X-15-Faelle auf19961ee/Build34 erfolgreich. Es
wurden die bereits zuvor verwendeten acht gezielten Selektionen
seriell ausgefuehrt, keine Faelle gekuerzt oder Erwartungen entfernt.
Jeder Prozess behielt sein180s-Limit; der negative gemeinsame Native70
bleibt separat erhalten und gilt dadurch nicht nachtraeglich als bestanden.

| Lauf | Tatsaechlich bestanden | Echte Prozessdauer / Limit in Sekunden |
| --- | ---: | ---: |
| Offline72, Monatsprojektion | 3 | 20,791438 / 180 |
| Native73, gebundene Versandvorschau/Unknown | 1 | 108,631225 / 180 |
| Native74, veraltete Revision/Empfaenger/Profil | 1 | 85,878822 / 180 |
| Native75, PSUR-Export und unveraenderter Bericht | 1 | 74,011916 / 180 |
| Native76, falsches Produkt/Version/manipulierte Bindung | 1 | 65,004908 / 180 |
| Native77, unveraenderter historischer Snapshot | 1 | 64,018394 / 180 |
| Native78, einzelne Basic-UDI-Gruppenmitglieder | 1 | 63,151567 / 180 |
| Native79, vollstaendige Gruppenberechtigung | 1 | 114,623080 / 180 |

Alle zehn konkreten TRX-Ergebnisse sind `Passed`, keines uebersprungen.
Jede SQL-Fixture durchlief ihre sechs normalen Ressourcenphasen und
wurde einschliesslich Objektparent entfernt; alle Prozessjobs und
Streams schlossen. Der Index bindet exakt dieselben zehn Fallnamen
wie der fruehere Featurebeleg, jeden einzelnen Resultat-/TRX-/Ownerhash
und den neuen Build. Dies ist fachliche/native Evidenz, keine neue
Browser- oder Vorschaufreigabe.

Vor dem naechsten Browserdurchlauf bestanden ausserdem der strikte
TypeScript-Check der E2E-Quelle und das echte Playwright-Listing beider
Zweierprofile einschliesslich Ablehnung einer falschen Auswahl.
Die neue Supervisorbindung bestaetigte den realen Timeout und die
unabhaengige Bereinigung/Fehlererhaltung ohne SQL, API oder Browser.
Das tatsaechlich publizierte Bootstrapprogramm aus Build34 gab
`--help` im eigenen30s-Prozessjob erfolgreich aus. Seine Hilfe weist
ausdruecklich darauf hin, dass ein SQL-/Search-Aufruf ohne `--apply`
remote lesend und nicht offline waere. **Hier wurde ausschliesslich
die Hilfe ausgefuehrt**, kein SQL-/Search-Bootstrap und keine
Cloudbereitstellung.

| Neuer privater Beleg | SHA256 |
| --- | --- |
| `x12-x15-native-03.json` | `58453bd9df15d5e70665c27505fed8de6c6945e3bad902494d2c80a6486d0855` |
| `psur-month-browser-typecheck-03.json` | `65680f7916a92cc4d4e364764c1c0945ea0f243aee3aeb79413092e913d641fe` |
| `browser-selection-02.json` | `dac36c422e581c0ffb7bd2e66e50cac8500a99c430ecdd7a31b15812f7af21c6` |
| `browser-selection-owned-02.json` | `73787629c703443a1845e912dc75c9e60afec9638210eaa543fa8b07fe0c8a7d` |
| `bootstrap-help-build34-01.json` | `f7d45cc08981ba93f4207c325ebbd9ac115afa2b76e214475643364e4b3b3bd3` |
| `bootstrap-help-build34-owned-01.json` | `52fd5d3de26a9b73171b6a008a5a83805cc8cc624fcc1db69194059286df513e` |

**Browser22 erreichte keine Browserfaelle.** Das unveraenderte
Runtime-Preflightgate lehnte vor Ressourcen-Intent/API/SQL/Browser
eine zusaetzliche Datei im privaten Headless-Paket ab. Der Prozess
endete mit Exit1 nach35,429947/600 Sekunden, ohne Timeout. Die
unabhaengige Bereinigung bestaetigte nach21,915526/60 Sekunden,
dass keine zugeordnete Runtime/Fixture angelegt worden war und
alle eigenen Nachfahren/Streams geschlossen waren.

Der nachfolgende reine Dateiabgleich fand genau
`chrome-win\debug.log` mit153 Bytes. Inhalt und Zeitpunkt stimmen
vollstaendig mit der aufgezeichneten GPU-Fehlerzeile des eigenen
Browser21-Prozesses ueberein. Alle13 urspruenglichen Archivdateien
blieben bytegleich. Das Log wurde unveraendert als
`browser-21-runtime-debug.log` erhalten; eine separate Hash-/Zeit-/
Prozess-/Stderr-Bindung belegt die Zuordnung und den wieder exakt
urspruenglichen Dateisatz. Kein Binary, Timeout, Dateisatzgate,
Browserfall, Zugriffsrecht oder frueheres Ergebnis wurde geaendert.
Browser22 bleibt ein negativer Preflight, nicht ein neuer E03-Fehler
oder -Erfolg; die separate Browser08-Abweichung wird nicht umgedeutet.

| Preflight und reine Artefakterhaltung | SHA256 |
| --- | --- |
| `browser-22-bounded-result.json` | `4ed2ff9652df3363cc5a9c1678434c6a6f81069e194d725e892fdb55caf89fca` |
| `browser-22-bounded-cleanup.json` | `09b7abff97402bf042cd1fcee726e94e1489891e1b02c4965f5fd74876a7f886` |
| `browser-21-runtime-debug.log` | `2f18527dad44e1eafc7c905de0089c71ac290ded9bb8225b50ca72b167a1dabf` |
| `browser-21-debug-log-preservation.json` | `8067a33e516570fb577d8d42c3e54c8173011f39a9afe1e6fc9e981a627fc78c` |

### Browser23: vollstaendige E03-/E04-Abnahme auf Build34

Auf `19961ee9a1d662b6ec98b12d9fb92e85135ab773`/Build34 bestanden
beide unveraenderten Faelle: **2 Passed, 0 Fehler/Skips**. E03
dauerte100,425 Sekunden, E04173,344 Sekunden bei jeweils unveraenderten
210 Sekunden. Der echte Gesamtprozess lief von
`2026-11-17T23:50:12.3827254Z` bis
`2026-11-17T23:56:10.1409056Z`, **357,758180 / 600 Sekunden**.
Die separate Bereinigung benoetigte15,430304/60 Sekunden;
Stop10s und Stream3s blieben unveraendert. Alle eigenen Nachfahren,
Streams, Datenbank, Runtimeparent und aufgezeichneten privaten Profile
sind entfernt/geschlossen; fuenf Baseline-Datenbanken, Quell-/Paket-
und beide Workbookbindungen blieben erhalten.

E03 bestaetigt das quellgebundene MIR-Dossier, getrennte neue
Medical-/RA-Reply-Freigaben, unveraenderte native Meldung/Fristen/alte
Freigaben sowie einen technischen Ack ohne fachlichen Pflichtabschluss.
Nur die adressierte Rueckfrage schliesst nach fachlicher Reply-Annahme.
Der Kalender fuehrt zum exakten Original und zeigt erst danach den
abgeschlossenen Rueckfragestand. E04 bestaetigt bytegleiches PDF/Archiv,
historischen Originalerhalt, echten Logout/Login und abgebrochenen
verspaeteten Originaldownload: keine spaeten Downloads fuer das neue
Konto, mit serverseitiger Scopeverweigerung.

Die238 erfassten E03-Antworten enthalten keine HTTP-Statuscodes ab400;
sechs Headerankuenfte lagen ueber5 Sekunden. Die erste Simulatorantwort
erreichte ihre Header nach3,435594 Sekunden, die drei Simulatorantworten
nach3,435594/2,130813/6,205570 Sekunden; `/api/status` maximal0,276974
Sekunden. Dies sind weiterhin **Headerzeiten**, keine Messung des
vollstaendigen Responsebodys oder abgebrochener Requests. Der erfolgreiche
unveraenderte Ablauf ist belegt; eine alleinige Ursache der historischen
Latenz oder ein allgemeiner Last-/SLO-Nachweis ist daraus nicht ableitbar.

Ein waehrend Browser23 erzeugtes153-Byte-GPU-Diagnoselog wurde nach
geschlossenem Job ebenfalls anhand jeder identischen Stderrzeile und
seiner Zeitbindung unveraendert erhalten. Der urspruengliche13-Dateien-
Archivbestand blieb bytegleich, ohne Dateisatz-Ausnahme.
Alle Kommunikation war simuliert, die Browserbelege enthalten keine
externen Requests und keine reale Behoerdenannahme. **Keine Promotion
der bestehenden742-Vorschau und keine Gesamt-/Cloudfreigabe.**

| Browser23-Beleg | SHA256 |
| --- | --- |
| `browser-23-binding.json` | `5a88cdd5b1cbaef7f39c49be3208fb27d63aa4b12d49dbc031624b3861a20c78` |
| `browser-23-result.json` | `510eb93e859e43afc68e0d2630c8724a261f86141f4a0db5f7a1d15c4dc0a506` |
| `browser-23-bounded-result.json` | `8186ab7511d319c1fea2ad7533c65063ff8e196178f036ac2c870e7fa8848369` |
| `browser-23-bounded-cleanup.json` | `b8f441aa9cb8c8e98621874cb77d275faf29948a7893be1fd18271248d19ce54` |
| `.local\v10\.local\eu-u23\e2e-results.xml` | `d62d1a38ddd38588fdbb3d4e134471b4f21128cbcaef2ae7fc563e8aa7fd0c37` |
| `.local\v10\.local\eu-u23\eudamed-dossier-reply-proof.json` | `94a3c29fa48beae04e9aca5e289d95cf15e4edae5f697f31f9db15f59064ba42` |
| `.local\v10\.local\eu-u23\eudamed-original-download-proof.json` | `9816485e5e100de1c571f87fb54a55be703de18b4cdc2f845af275290cf74ffa` |
| `.local\v10\.local\eu-u23\eudamed-request-timings.jsonl` | `bf02253154ffa4a1cd50ccf6612855b09d5b2dbf07dd8f5690028023a549f2de` |
| `browser-23-debug-log-preservation.json` | `8b91c17b3621845f7e57bcdb3a2080f63cda50cf246376f639dfc93d12526fb3` |

### Browser24: Fachfaelle bestanden, unabhaengiges Cleanupgate fehlgeschlagen

Das unveraenderte vollstaendige `PrimaryBaseline`-Profil auf demselben
19961ee/Build34 lieferte **2 Passed, 0 Fehler/Skips**:
Beschwerde/X-12 in47,078 Sekunden und der befuellte vollstaendige
PSUR-Gruppenfall/X-15 in83,619 Sekunden. Die versions- und
mitgliedsgebundenen Monatswerte werden unveraendert mit der exakten
API-Antwort verglichen, einschliesslich Tabellenreihenfolge,
Periodenausschnitt, offenem/unbekanntem Status und Diagrammwerten.
Der echte Ausfuehrungsprozess endete mit Exit0 nach252,988664/600
Sekunden. Sein normaler Abschluss protokolliert alle sechs
Ressourcenphasen, keine Cleanupfehler sowie entfernte Datenbank,
Zugangsdaten/Objekte und kompletten Runtimeparent.

**Der gesamte Browser24-Versuch ist dennoch negativ.** Der anschliessende
unabhaengige Bereinigungs-/Verifikationsprozess erreichte sein unveraendertes
60s-Limit und wurde nach tatsaechlich60,242105 Sekunden beendet. Er
hinterliess keine lebenden Nachfahren oder offenen Streams, aber auch
keinen abschliessenden Cleanupbeleg. Seine Ursache/letzte Arbeitsphase
ist durch die leeren Ausgabestreams nicht feststellbar. Weder der
positive JUnit noch die normale innere Bereinigung ersetzen dieses Gate.

Danach wurde ausschliesslich eine neue, getrennt gebundene **lesende
Verifikation** ausgefuehrt:16,656235/60 Sekunden, mit dauerhaft
geschriebenen monotonen Metazeiten, ohne neue API/Browser/Fixture oder
Ressourcenmutation. Sie bestaetigte die exakte negative Vorgeschichte,
normale sechsphasige Journalbindung, abwesende aufgezeichnete Prozesse,
Profile, Runtime und Port5197 sowie die entfernte Datenbank und exakt
fuenf unveraenderte Baseline-Datenbanken. Alle411 gebundenen Quelldateien,
116 Paketdateien und beide IMDRF-Dateibindungen (kanonische Gitdatei
und unveraenderter fremder Worktree-Stand) wurden erneut geprueft.
Diese separate Schliessung erlaubt sichere Folgearbeit; sie macht den
historischen60s-Timeout **nicht nachtraeglich erfolgreich**. Ein allgemeiner
Cleanup-SLO oder eine vollstaendige neue Releaseabnahme ist nicht belegt.
Die bestehende742-Vorschau blieb unveraendert.

| Browser24 und separate Verifikation | SHA256 |
| --- | --- |
| `.local\v10\.local\eu-u24\e2e-results.xml` | `e1021dab2d0403922ee122077ed690b836831194b081918457d79dd448dcdc2c` |
| `browser-24-bounded-result.json` | `123044972c6641068487e6f90d1e9abc9af918f83ae4ed533b804cc084c1958b` |
| `.local\v10\.local\eu-u24\owned-resources-journal.jsonl` | `f686640db197e7309e5233ad87b592a5269c5cfd9dce201cc93c2d794760f217` |
| `browser-24-cleanup-verification-01.json` | `cd266731759829bfec27ff974a82266ddbbe9605283927171f3771e793741b2f` |
| `browser-24-cleanup-verification-owned-01.json` | `2ac9602f183370eb7109a9f8004a413ec491e34e302dd977c2d683de2463ea54` |
| `browser-24-cleanup-verification-01-phases.jsonl` | `2a0d09d9a00611f1d3d42c2bbf173b1c16c9b3d53c295b56c75e412affcb5404` |

### Oeffentlicher Browserrunner: Quell- und Vertragsnachweis, keine neue Runtimeabnahme

`scripts\Test-Ui.ps1` wurde um das exakte `PrimaryBaseline`-Profil,
wirkliche Playwright-Listenselektion vor Ressourcenanlage, durable
Ownershipregistrierung vor CREATE und den unveraenderten sechsphasigen
Ressourcenjournalvertrag ergaenzt. Die vollstaendige eigene Runtime wird
nur nach nachgewiesenem API-Stop und Datenbankabschluss entfernt;
vorhandene Runtime, unbekannte Objekte oder unbewiesener Stop/Drop bleiben
fehlerschliessend erhalten. Ausfuehrungs- und Cleanupfehler werden getrennt
bewahrt. Der API-Stop ist auf10s, der **gemeinsame** stdout/stderr-Nachlauf
auf3s und die scoped Cleanup-SQL-Befehle auf15s begrenzt.
Das sind keine neuen Gesamtprozess- oder Browserfallbudgets.

Die erste wirkliche Listenselektion01 scheiterte nach erfolgreicher
Eudamed-Listenausgabe an der ungenauen Umgebungswiederherstellung:
PowerShell band `$null` als leere Zeichenfolge statt als abwesende
Umgebungsvariable. Der Defekt wurde in einem allein prozesslokalen
Durchstich reproduziert und mit `[NullString]::Value` korrigiert.
Urspruenglich fehlend, leer und gesetzt werden nun unterschieden.
Der negative Versuch bleibt unveraendert erhalten;23,209738/60s,
Exit1, kein Timeout und keine SQL/API/Browseranlage.

`tests\scripts\Test-UiContracts.ps1` extrahiert die tatsaechlichen
Runnerfunktionen, den Registrierungsblock, das Finally und die
Fehlererhaltung. Lauf04 pruefte **11/11** Varianten in19,588173/60s:
Registrierung vor CREATE, verweigertes Journal, vorhandene Runtime,
unbekanntes Objekt vor Loeschung, unabhaengige Fehler, ungestoppte API,
unfertige Streams, unerledigte Datenbank mit erhaltener Runtime,
exakte Umgebungswiederherstellung sowie beide Zwei-Fall-Selektionen
einschliesslich falschem Titel/Projekt/Skip. SQL und Prozesse waren
synthetische Callbacks; keine echte API oder Browserinstanz wurde gestartet.

Anschliessend bestaetigte die echte `node ... --list --reporter=json`
Selektion03 fuer dieselben finalen Runnerbytes beide Originalpaare:
E03/E04 und vollstaendiger Beschwerde-/befuellter Gruppen-T16-Fall.
21,663250/60s, Exit0, keine Skips in der Listendeklaration, Zugangsstub
entfernt und Prozessumgebung exakt zurueckgesetzt. Der vorhandene
unveraenderte19961ee-Toolingcheckout stellte seine bereits installierte
Playwright-Lockversion bereit; keine Installation oder neue Infrastruktur.
Beide begrenzten Jobs samt Nachkommen/Streams waren geschlossen.

Der oeffentliche Runnerhash lautet
`0eb286dc69643921ce361fbf5cf1b11239b3523b81a6f3370ebb792938e17c4b`,
der Vertragstesthash
`6162a82ab193b4c21168f1494be482abb21ffe257dff3e8521c1753e6c9b7ddf`.
Die Checks sind mit `.\tests\scripts\Test-UiContracts.ps1` wiederholbar.
Nach eigenem Build/Publish und den erforderlichen lokalen Voraussetzungen
waehlt `.\scripts\Test-Ui.ps1 -ApplicationDirectory <eigenes-Paket>
-ArtifactDirectory <neuer-eigener-Pfad> -Profile PrimaryBaseline`
das deklarierte Paar. Eine **vollstaendige neue SQL/API/Browserausfuehrung
des portierten oeffentlichen Runners ist noch offen**. Seine internen
Einzelgrenzen ersetzen keinen externen Gesamtprozess-/Cleanup-Waechter.
Die privaten Windows-Handle-/Tag-Proofs und Browser23/24 gelten nicht
automatisch fuer den oeffentlichen `Assert-RecoveryPath`-Pfad.
Keine Previewpromotion, neue fachliche Abnahme oder Cloudaktivierung.

| Quell-/Vertragsbeleg | SHA256 |
| --- | --- |
| `.local\ui-contracts-public-04\result.json` | `56eb9b2f6ca910a909f821521adf293c520fa89aeed6e1a0d73edbdafb22eb2c` |
| `public-ui-contracts-owned-04.json` | `cf1c2563babcb9b4e8eab22fa93cc1a8c19871c2d8aefc3981f162d462d3bb21` |
| `public-ui-selection-03.json` | `abbe6a860b77c824d5a2ab288e5ad246ae1c964ce1259ef6d0b272da45ecc8de` |
| `public-ui-selection-owned-03.json` | `ec009dd4ddc9a93aae5b30ce4aef69a3caefda88c5ec2cf0a382c019b5db449f` |
| `public-ui-selection-owned-01.json` (negativ) | `305205400915f679e0c1a4ae87e7eb3d491fc3bb3ed5999a4e616bfc3ce90afe` |

### Recovery-Quellpraezisierung und negativer Build35

`6ec9c37f1487a0e315eef09e85ad2ff375b86dfb` ergaenzt die dauerhaften
monotonen RecoverySetup-Metazeiten, nicht den fachlichen Testumfang.
Build35 baute neun Projekte frisch, erschoepfte aber das unveraenderte
600s-Gesamtbudget waehrend `Cohors.Bootstrap.Tests`: diesem Prozess
verblieben28s; `Cohors.Tests` wurde nicht mehr erreicht. Der umschliessende
900s-Job endete negativ ohne eigenen Timeout und ohne verbliebene
Nachkommen/Streams. **Kein vollstaendiger Build und kein Recovery17-Pass**;
gemischte Ausgaben dieses Versuchs werden nicht fuer Tests freigegeben.

Ein anschliessender begrenzter reiner Lesedurchstich mass34,122s fuer
alle412 Quelldateihashes und1,254s fuer elf Assetgraphen. Eine
aequivalente serielle CLR-Schleife las alle423 Dateien in1,6564213s
und bestaetigte jeden Originaldigest sowie erneutes Lesen geaenderter
Bytes und fehlerschliessendes Verhalten bei fehlendem Input.
SHA256, vollstaendige Dateiinhalte und FileStream-Zugriffsoptionen bleiben
gleich; kein Cache, Parallelismus oder reduzierter Dateisatz.
Der zweite Prozess endete nach10,0084918s/60s vollstaendig.
Das sind einzelne Diagnosemessungen, kein allgemeines Leistungsversprechen.
Sie begruenden die eng begrenzte Builderkorrektur, nicht einen Build-Pass
oder eine Erhoehung von180/600/900s.

| Diagnosebeleg | SHA256 |
| --- | --- |
| `build-35-project-partitions.json` (negativ) | `5262bd0e52c4cd4426b6efd44e793667c49abdfbb3050c12bbc17a160d09c60a` |
| `build-35-partitions-owned-job.json` | `3fe384712145320519a1b6e058d9979c190d88a283b9ac39ffe5a4b890b81af6` |
| `partition-bindings-probe-01.json` | `a76ce57e7d27295be8f46188d83858d3a918446efd1627a15f432062c21ed586` |
| `bound-file-hashes-proof-01.json` | `617404ce5dc2ba72a68bd776f29fdbb7768fb62200535daaffdbd1c2bc929601` |
| `bound-file-hashes-proof-owned-01.json` | `885307affe494b034f8b9522821cdefe7b598e1a912de3440acc40c268d2ab01` |

### Build36 und getrennte Recovery-Voraussetzungen

Der nachfolgende **Build36 auf demselben6ec9c37** bestand alle
urspruenglichen Komplettgates. Alle elf Projekte wurden erneut frisch
gebaut, zusammen480,7241584s/600; keine frueheren Teilergebnisse zaehlen
mit. Alle13 vollen Quellhashgrenzen blieben erhalten und dauerten
einzeln1,237 bis6,029s. Die neuen monotonen Metazeiten unterscheiden
Vorbereitung, Guard, Git, Quellen, Assetgraphen und Projektprozesse;
sie ersetzen keine OS-Prozessendzeit.

| Unveraendertes Buildgate | Tatsaechliche Prozessdauer / Limit |
| --- | --- |
| Frisches Web ohne TypeScript-Inkrementalcache | 88,5839257 / 120 s |
| Vollstaendiger Solution-Build | 70,3930900 / 180 s |
| API-Publish | 14,2803802 / 90 s |
| Bootstrap-Tests, 39/39 ohne Skips | 14,9482837 / 180 s |
| Bootstrap-Publish | 3,7212407 / 90 s |

Alle Prozesse und Streams waren geschlossen;116 API-Dateien und55
Bootstrap-Dateien sind vollstaendig gebunden. Der Build ist keine
Previewpromotion oder UI-/Recovery-/Cloudabnahme.

**Offline80** bestand anschliessend die originalen sieben
`RecoveryDiagnostics`-Faelle und den einen SQL-freien
`RecoveryEudamedContract`:8/8,0 Skips,19,6859332s/180 fuer den echten
Testprozess. Kein SQL-Fixturejournal, geschlossener Gesamtjob.
Diese acht Faelle ersetzen ausdruecklich nicht Recovery17.

Der zusaetzliche private Vierfach-Grenzdiagnoseversuch01 endete
nach60,2920789s/60 ohne abgeschlossenes Resultat; eigene Prozesse und
Streams waren geschlossen, ohne SQL/Docker/API/Browseranlage.
Seine Teilfortschritte sind unbelegt. Versuch02 mass stattdessen die
**eine vollstaendige** Source-/Assembly-/Package-/Guard-/Workbookgrenze,
die der separate Abschlusspruefer genau einmal ausfuehrt:
39,7733079s/60 fuer den Prozess, mit dauerhaftem Phasenprotokoll,
412 Quelldateien und116 Paketdateien. Er ist weder eine Umwertung
des vorherigen Timeouts noch eine Verkuerzung der17 fachlichen Faelle.
Der17er-Gesamtlauf und seine unabhaengige Ressourcenpruefung bleiben
separate offene Gates.

| Aktueller Build-/Voraussetzungsbeleg | SHA256 |
| --- | --- |
| `build-36-project-partitions.json` | `3a479bbf98d8aa825002e2dcf698251e193934d8287bae65e89601a7a72fdaf8` |
| `build-36-project-phases.jsonl` | `1bb2e7c1d0cb53e8bc319b91742f9cdffce17bf8f6d5fd01f8c9cfc189fb8330` |
| `build-36-result.json` | `5e69c01550d3a660cdaf45f8081f6d5973519c89210fcb73c44e10689d3035fe` |
| `build-36-owned-job.json` | `52872bf794a974c9ad6f6f87995ad75b8d7554bc52afb39d91d4a70a0580dce5` |
| `offline-80-result.json` | `fea4189f0efffab14fc821fc51a1f3ca23d4c10b33acf93ddceb4cd6d3eef1ea` |
| `offline-80-owned-job.json` | `e4b605b4f24c7a1af6cff5ee0f1536116176599bebcccb32bc2963beb73f5f5e` |
| `recovery-boundary-proof-owned-01.json` (negativ) | `3520402be745f5175b54ccaaffbe0c0475c7561a46fa00c9f83fc35f457ce501` |
| `recovery-boundary-proof-02.json` | `aeb551b48754a006d5b6a82b623f05799dc77ee48fc1d21219c378ef62b025c8` |
| `recovery-boundary-proof-owned-02.json` | `c5501ad024843abcd47696d2e6b2738ffd174fa90895bbb495cc47899ee3361e` |

### Recovery03: Timeout und separate Bereinigung

Der originale gemeinsame17er-Runner auf
`6ec9c37f1487a0e315eef09e85ad2ff375b86dfb`/Build36 endete am18.11.2026
erneut **negativ**. OS-Prozessstart01:59:52,2589982Z,
OS-Ende02:14:52,6831350Z: **900,4241368s bei900s**. Eigene erfasste
Prozesse, Jobhandle und Streams wurden geschlossen. Kein17er-TRX-Pass;
auch keine Addition frueherer Teilversuche.

Das unveraenderte dauerhafte Setupjournal weist fuer die volle
PSUR-Vorbereitung460,9817339s aus. Die letzte Marke ist
`pre-deletion-backup-start` bei584,6439284s **ab Fixturekonstruktion**,
nicht ab OS-Start. Nachfolgend sind ein natives Backup und Objektkopien,
aber kein abgeschlossener Backupmanifest-/Restorebeleg vorhanden.
Die reine BS-Rueckfrage-Antwort-Latenz ist damit nicht isoliert gemessen;
eine einzelne SQL-Abfrage ist noch nicht als Zeitursache nachgewiesen.

| Getrenntes Abschlussverfahren | Prozessdauer / Limit | Ergebnis |
| --- | --- | --- |
| Originale unabhaengige Abschlusspruefung |25,6300026 /60s| Fehler: normale Fixturebereinigung nicht abgeschlossen |
| Exakte nachtraegliche eigene Ressourcenbereinigung |41,8397878 /60s| Eigener Sourcecontainer/Volume, voller journalgebundener Fixture-Elternordner und generierte Source-Konfiguration entfernt |
| Nachfolgende Read-only-Abwesenheits-/Quell-/Assembly-/Paketgrenze |48,5615119 /60s| Vollstaendig geprueft:412 Quellen,116 Paketdateien, Testassemblies und urspruengliche Ressourcenbasis |

Die beiden spaeteren Prozesse sind separate Nachweise, keine Verlaengerung
oder Umwertung des ersten Cleanupgates. Vor jeder Entfernung wurden die
konkreten aktuellen Container-/Volume-Identitaeten und Labels gegen
Inventory und vierphasiges Originaljournal geprueft. Alle65 Run-Artefakte,
einschliesslich negativer Ergebnisse und unvollstaendigem Backup,
blieben bytegleich erhalten. Das Journal wurde nicht um erfundene normale
Cleanup-/Disposed-Phasen erweitert. Beide fremden Workbookdeltas und
die alte742-Vorschau blieben unangetastet.

| Privater Beleg unter `.local\eudamed-integration-01` | SHA256 |
| --- | --- |
| `recovery-03-bounded-result.json` | `ef7fb501fa9627d10108a5f37c8f7befdfcb7aeacdb72e6cb273a7379876702b` |
| `recovery-03-cleanup.json` (negativ) | `aaaf1d78e9d9092b35a6858c0fac6b8344af1c382ce5eeb548c3c3946d8d4a82` |
| `recovery-03-cleanup-reconciled.json` | `89433317db12fed567b150ca8812faa354c4dde7e92459db9dc615d421f9239a` |
| `recovery-03-cleanup-reconciled-owned.json` | `d58eb5237f1f6f9c567b39af34e069e62e7b6fe39b8882da10f13d09b83b99a7` |
| `recovery-03-post-cleanup-boundary.json` | `dfafd354d5be67ea28ee1410f188841eb124b6742f1d91df6277bf1bbbed65db` |

### Build37, Recovery04 und Recovery05

Quelle `655ef4a45007976bd972fa3a60385136d5520816`, Build37:
alle elf frischen Projekte360,3553563s/600, Web44,2414413s/120,
Solution37,8605364s/180, API-Publish9,8174974s/90,
Bootstrap39/39 in14,8785048s/180 und Bootstrap-Publish3,6099271s/90.
Offline81 bestand8/8 in37,7435515s/180; die vier gezielten nativen
Regressionen Tests82 bestanden in39,8895783s/180 einschliesslich beider
vollstaendig bereinigten Fixtures. Diese Voraussetzungen sind kein
Recovery17-Erfolg.

Recovery04 scheiterte vor einem Testprozess am fehlenden Docker-Engine.
Nach der ausdruecklich erlaubten Wiederinbetriebnahme verwendete
Recovery05 dieselbe gebundene Quelle und den originalen17er-Runner.
OS-Start07:02:05,6816085Z, OS-Ende07:17:05,9864877Z am18.11.2026:
**900,3048792s/900, Timeout**, kein vollstaendiges17er-TRX.
Eigenes Prozessende, Streams und Jobabschluss sind belegt.
Die normale unabhaengige Abschlusspruefung endete nach9,6524257s/60
mit Fehler wegen nicht abgeschlossener Fixturebereinigung.

Die feineren dauerhaften Metazeiten belegen PSUR-Aufbau80,7039093s und
vollstaendige Pre-/Post-Deletion-Backupmanifeste. Die beiden Backupaufrufe
dauerten innerhalb der Setupuhr131,9823448s beziehungsweise138,1008534s.
Vier Originalreferenzpruefungen beanspruchten jeweils21,31 bis24,21s.
Das ist kein isolierter Rueckfrage-Antwort-Wert, kein kausal kontrollierter
Vergleich zu Recovery03 und kein Nachweis der spaeteren positiven Restorefaelle.
Erhaltene abgeschlossene Restorelogs enthalten erwartete Negativcodes;
der letzte Versuch hinterliess53 gestagte Dateien ohne Aktivierungsbeleg.
Seine genaue Operation am900s-Abbruch ist ohne weitere Phasenmarken
nicht bestimmt.

Der erste Sondercleanup stoppte nach9,0546427s/60 **vor jeder Entfernung**:
der bisherige Win32-Metadatenleser konnte ein bestehendes268-Zeichen-
Verzeichnis nicht adressieren. Der neue private Reader aendert nur den
`CreateFileW`-Pfad auf die dokumentierte Extended-Length-Schreibweise.
Zugriffs-/Sharing-/Open-Reparse-Flags, Normalisierung und Klassifikation
bleiben gebunden. Die rein lesende Probe bestand in6,0156587s/60:
alle urspruenglichen12 Verweigerungen,16 bekannten Cloud-Tags, echtes
langes Verzeichnis und Verweigerung eines fehlenden langen Pfads.
Keine ACL-, Tag-, Schutz-, System- oder Policyaenderung.

Sondercleanup02 bestand in29,2611495s/60; danach bestand eine unabhaengige
vollstaendige Read-only-Quell-/Assembly-/Paket-/SDK-/Abwesenheitspruefung
in36,3842629s/60. Ausschliesslich zwei exakt gebundene Source-/Target-
Container, deren zwei Volumes und zwei private Konfigurationen wurden
entfernt. Der urspruengliche fremde Before-Satz blieb unveraendert.
Alle541 anderen Run-Dateien einschliesslich unvollstaendiger Restoreobjekte
blieben vollstaendig hashgleich. Das echte sechsstufige Sourcejournal,
der Timeout und `cleanupVerified=false` im historischen Inventory blieben
unveraendert. Keine nachtraegliche Umwertung des normalen Cleanupgates,
keine neue Vorschau oder Cloudpromotion.

| Privater Beleg unter `.local\eudamed-integration-01` | SHA256 |
| --- | --- |
| `build-37-result.json` | `57c40c324ab42c87f4ce5c9f5a194606742ae6cdfed62dd5714bffe66b634574` |
| `offline-81-result.json` | `8301c883129b312920e6e246d470eece8cd6503ee8fa659c23421883920b5c9a` |
| `tests-82-result.json` | `2cc325c590b5d3276613f4b18dacc483f2b19d899edf20434e429a380d760aab` |
| `recovery-04-preflight-failure.json` | `cf376f25183ef2571d013673778d036c7ec224ac30e80586edf8b0f722b2c715` |
| `recovery-05-bounded-result.json` | `076febfba4d53ff5146b33ced2dce39ff3896a1cf4b6b79d071cad0a0dba5808` |
| `recovery-05-cleanup.json` (negativ) | `fe0479eb3439be013a1e49eabbfeca88ca21db3580efebeeb69457cede1043af` |
| `recovery-05-cleanup-reconciled-02.json` | `b28fd3eab0893752a152bfcbd1f55b0169532cfafa9bbf535887b5174520fc57` |
| `recovery-05-cleanup-reconciled-owned-02.json` | `8dccbe2a0a032a85a1281db5b86111cc0c531130d76dbc5b309b2957bafee56f` |
| `recovery-05-post-cleanup-boundary.json` | `bf0c5678ebbb7c084eae0c7b79c048de041de1d15447ab9a4a86a9e8ae7e8f47` |

Der prospektive native Gesamtjournal-Abbruchpfad ist separat quellgebunden:
`Invoke-SliceCheck-v12.ps1` behaelt v11 ausserhalb einer einzigen
Before-Set-Erfassung bytegleich; Prozessmessung,180s-/300s-Auswahl und
normale sechsphasige Abschlusspruefung bleiben unveraendert.
`Complete-NativeCleanup-v3.ps1` verarbeitet nach unabhaengigem Inspect
alle eindeutig journalisierten CreateRequested-Intents, auch vor Ready;
es laesst fremde Ressourcen und unvollstaendige Eigentumsbelege unberuehrt.
Die13 SQL-freien Vertraege bestanden in5,907661s/60, die Runnerbindung
in2,3149378s/60. **Kein realer nativer SQL-Abbruchcleanup wurde dadurch
ausgefuehrt oder als bewiesen ausgegeben.** Historisches v2 bleibt unveraendert.
`native-journal-contract-03.json` ist gebunden an
`645c2b63d4b04111e6b7ed0f4f48e4fa7dd75b0d28daf97ef0933b8f57e8bccb`,
`slice-runner-12-binding.json` an
`689bfc8ccc3230f52f8e3d450dd52c6a5e307212f3fd10675b90fa12f49860ab`.

Die anschliessende Nutzerentscheidung vom18.11.2026 erlaubt dem
naechsten gemeinsamen oeffentlichen Recovery-Pruefprozess **1800s
statt900s**. Sie aendert weder die17 Originalfaelle/11 Fixturegates noch
480s-Einzeloperationen,60s-Abschlusspruefung oder10s-/15s-Nachlauf.
Fruehere900s-Timeouts bleiben negative historische Ergebnisse.
Eine neue Quell-/Build-/Runner-/Zeitlimitbindung ist erforderlich;
die Erlaubnis ist kein Laufzeitergebnis, Produktions-RTO/SLO oder
Vorschau-/Cloudfreigabe.

### Recovery06 und getrennte Testumfeldfreigabe

Auf `df61254598737f5b8524ab2e0a94b638cf745853`/Build38 endete
Recovery06 **negativ**. Der eigentliche Prozess lief von
2026-11-18T08:37:07.0809396Z bis09:07:07.2979073Z:
**1800,2169677s bei1800s Limit**. Stop498ms sowie Stream-/Runnernachlauf
sind davon getrennt. Es liegt kein vollstaendiger17er-TRX-Erfolg vor.
Die normale unabhaengige Abschlusspruefung scheiterte in11,1484533s/60
an der unvollstaendigen Fixturebereinigung.

Der nachfolgende exakte Sondercleanup entfernte nur die gebundenen
Source-/Target-Container und Volumes sowie zwei erzeugte private
Konfigurationen. Er endete selbst nach **60,4594741s/60** mit Timeout.
Letzte persistierte Phase: `preserved-artifacts-verification-start`.
Der erste vollstaendige Hashsatz wurde nicht dauerhaft gespeichert;
ein finaler Vergleichsbeleg fehlt. Die anschliessend beobachteten
585 Dateien/875487725 Bytes sind **nur aktuelle Metadaten**, kein
Nachweis ihres vollstaendigen historischen Byteerhalts.
Inventar, echtes sechsstufiges Sourcejournal und negative Ergebnisse
wurden nicht nachtraeglich umgeschrieben.

Read-only Beobachtung01 scheiterte ebenfalls nach60,3554203s/60.
Beobachtung02 nutzte denselben bewiesenen uncached seriellen
Voll-SHA256-Leser auch fuer einzelne Assembly-/Belegdateien, ohne
Dateimenge oder Pruefungen zu reduzieren. Sie bestand von
09:29:33.2666627Z bis09:29:58.5292409Z in **25,2625782s/60**:
exakte Prozess-/Ressourcenabwesenheit, unveraenderter fremder
Before-Satz,413 Quellen/6 Testassemblies/116 Paketdateien, SDK,
geschuetzte Workbooks und34 originale Controlbindungen plus14 explizite
Belegbindungen. Der Koordinator pruefte diese Ergebnisse unabhaengig
und erteilte **separat** die technische Freigabe fuer neue serielle
lokale Validierung. Die Beobachtung selbst behaelt
`testsPassed=false`, `completeCleanupAcceptance=false`,
`completeHistoricalRunBytePreservationVerified=false` und
`allowsNewRuntime=false`. Alte negative Ergebnisse bleiben negativ.

Die rein lesende Auswertung der vorhandenen Invocation-/Phasenjournale
erfasst28 Scriptaufrufe, davon27 mit echtem Prozessende und zusammen
1419,5734709s. Beim letzten Backup fehlt der Enddatensatz; er wird nicht
ergaenzt. Sieben spaete abgeschlossene Backups beanspruchten357,082974s.
Ihr originaler Testname ist `null` und bleibt unzugeordnet.
Disjunkte benachbarte Phasenintervalle enthalten384,8154364s
Originalreferenzpruefung,265,4564043s JSON-Aufbereitung/Hashing und
152,1903629s Auditmanifestpruefung. Diese Teilzeiten werden nicht
nochmals zu den Prozesszeiten addiert und sind keine Test-Passbelege.
Die gekoppelte Quellkorrektur weist fehlende typisierte
EUDAMED-Originale frueher ab; erfolgreiche Backup-/Restorepfade behalten
saemtliche vollstaendigen Integritaetspruefungen.

Ein neuer Datei-only-Abbruchvertrag bestand in17,3198898s/60:
unvollstaendiges Hashen erreicht keine Mutation; nach vollstaendig
dauerhafter Vorher-Aufnahme bleibt diese trotz hartem Abbruch nach
erster beabsichtigter Entfernung aus einem neuen Prozess lesbar.
Falscher Digest/Runbezug, veraenderte Bytes und zusaetzliche Dateien
werden abgewiesen. Beide eigenen Probeprozesse und temporaeren
Dateifixtures sind beendet/entfernt. Das ist **kein realer
SQL-/Docker-Abbruchcleanup**. Vor dem naechsten Recoveryvollversuch
muss diese dauerhafte Vorher-Aufnahme in dessen passenden
mutierenden Cleanupweg gebunden sein.

Private Belege unter `.local\eudamed-integration-01`:

| Datei | SHA256 |
|---|---|
| `build-38-result.json` | `7fc577524152e555c3f865ea95f6ce864e71f5d495e9dcc446d2c07cb445338d` |
| `recovery-06-bounded-result.json` | `b0d008caeaaca5707814f72331b241c1b2afc6ffd1110c0d93857a9266d3dacb` |
| `recovery-06-cleanup.json` | `314d139cda446301fa8df35924e730a2f623468aa4228683161d81c5df40423a` |
| `recovery-06-cleanup-reconciled-owned-01.json` | `e4048f6a1d74493854730ad8ee4b9b1b1b6f246ca8a45fbd57e1bf8af1db1e0d` |
| `recovery-06-cleanup-reconciled-01.phases.jsonl` | `08b8f9e439b403486c01df1a92d06627450543e0967bcdf345194b5af820510f` |
| `recovery-06-closure-observation-02.json` | `b7ca611dcfef4d2da9f2a06d19c3dfc39047761de01120b3a7902d87ff003486` |
| `recovery-06-closure-observation-owned-02.json` | `4fcaff19db9c617474551f4f92f4c2f4a59fb25f5230e54acf64c8e9393b60a1` |
| `recovery-06-phase-analysis-02.json` | `60c29f99c0ddfa6f6351beef1efb8b26a0fceeaefb6258fdd0da656bf1ad766c` |
| `preservation-contract-01.json` | `abb44dfdceffa88b72d88eb24ee4e9672efb7f469f80e7a78423b1210ec34f87` |
| `preservation-contract-owned-01.json` | `f8c5b960924ddcc5764fbbdc789a0c5a2ce387d5f150a251818544990d635366` |

Der Originalpraesenzcode, sein zweiter SQL-freier Vertrag und der
getrennte native `RecoveryPresence`-Fall sind separat unter
`4a82a8c62a5c1ae156259da7b8c56dc211744a1e`/Build39 gebaut.
Alle elf Projekt-Rebuilds, fuenf finalen Buildstufen und39 Bootstrapfaelle
bestanden; sechs Testassemblies und116 Paketdateien sind neu gebunden.
Offline86 verband die bestehenden Recovery-JSON-/Retention-/Diagnosevertraege
mit dem neuen Praesenzvertrag: **13/14 bestanden**,56,5894257s/180,
kein Skip oder Timeout, aber fehlgeschlagener neuer Praesenzvertrag.
Kein SQL-Fixturejournal wurde erzeugt; eigener Job und Streams sind beendet.
Die gezielte Diagnose reproduzierte dessen neue `$home`-Zuweisung:
PowerShell behandelt sie als schreibgeschuetztes `$HOME` und wirft
`VariableNotWritable`/`SessionStateUnauthorizedAccessException`
vor der Dateifixtureanlage. Die Quellkorrektur verwendet `$fixtureHome`;
sie verlangt einen eigenen Build-/Vertragsnachweis.
Build39 ist kein positiver nativer Praesenz- oder Recovery17-Nachweis.
`build-39-result.json` hat SHA256
`cabed4e4455a5c029be478c928d2458dacdf617810d3ae6f4935949ef80bef74`;
`offline-86-result.json` hat
`7b959a2d85d9a2ff9e08bac53822857a5db7c23a92d1ef830aeea6b85b913bd8`.
Recovery17, FSN/EU-27, gemeinsame X-12/X-15-/Releaseabnahme,
allgemeiner nativer Abbruchcleanup und Preview-/Cloudpromotion bleiben offen.

Der Variable-Fix wurde als
`2456d1d0916d6cd1cfbedbcb9c8b4f833fa1990a`/Build40 separat vollstaendig
gebaut. Offline87 bestand anschliessend **14/14 in54,3142624s/180**.
Native88 startete mit erneut identischem Docker-Before-Satz und pruefte
den neuen echten SQL-Fall separat. Er scheiterte nach43,3125705s/180
bereits in `RecoveryFixture.Tables` an einem SQL-NULL, noch vor dem
Praesenz-Unterprozess. Seine einzige Fixture erreichte alle sechs
Originalphasen; Datenbank, Objektverzeichnis, Parent und Prozess waren
entfernt, der fremde Satz unveraendert. Kein Timeout und kein nachtraeglich
umgedeutetes positives Testergebnis.

Die gezielte Diagnose fuehrte ausschliesslich konstante read-only
SQL-Ausdruecke auf der bestehenden lokalen Instanz aus. Ein leerer
`FOR JSON`-Unterausdruck lieferte tatsaechlich SQL-NULL; explizites
`COALESCE(...,N'[]')` lieferte `[]`. Entsprechend dem vorhandenen
`SqlStateStore` normalisiert der neue Folgefix nur diese legitime leere
Ergebnismenge: im Originalpraesenzleser, allen acht vollstaendigen
Recoverytabellen und dem unabhaengigen Tabellenorakel.
Der erweiterte, weiterhin einzelne Nativefall prueft zuerst den leeren
EUDAMED-Stand einschliesslich echtem `Get-RecoveryState`, dann befuellte
aktuelle/historische Originale mit unveraendertem SQL-Schnitt.
Nur kontrollierter Fehlercode, Typ und Zeilennummer werden bei einem
Unterprozessfehler ausgegeben, keine Originaltexte oder Verbindungsdaten.
Dieser Folgefix ist hier noch nicht gebaut oder nativ bestaetigt.

**Nachfolgender gebundener Stand:** Der Folgefix ist als
`b7ced18271ae64599d5fa0aad3d0614524921187` festgeschrieben.
Build41 erreichte unter dem unveraenderten600s-Projektbudget nur7/11
Projekte; nach600,6682971s war der achte Prozess mit seinem verbliebenen
14s-Limit negativ beendet. Alle eigenen Nachkommen/Streams waren
geschlossen. Beim unveraenderten Domainprojekt stieg die gemessene
Compilerzeit gegenueber Build40 von4901ms auf42757ms.
Eine getrennte neue Ausfuehrung nur dieses unveraenderten Projekts
schloss danach in15,0957024s/180. Keine fremden Prozesse, Hostregeln
oder Zeitbudgets wurden geaendert. Erst anschliessend wurde **Build42
vollstaendig neu** ausgefuehrt und bestanden, ohne Uebernahme der sieben
Teilresultate. Elf Projekte, frisches Web, Solution, beide Publishes
und39 Bootstrapfaelle waren erfolgreich.

Auf diesem Build bestanden **Offline89:14/14 in18,9512022s/180**,
**Native90:1/1 in54,8449124s/180** und **Native91:4/4
in30,0348234s/180**. Native90 pruefte den tatsaechlichen leeren
EUDAMED-Stand samt vollstaendigem `Get-RecoveryState`, anschliessend
aktuelle/historische Originale mit gezielten Fehlstellen, Wiederherstellung
der Originalbytes und unveraendertem SQL-Schnitt. Native91 behielt alle
vier urspruenglichen Outbox-/Audit-/Manifest-/PSUR-Stichtagsfaelle.
Eine beziehungsweise zwei Fixtures erreichten ihren regulaeren
sechsstufigen Journalabschluss; SQL, Objekte, Elternverzeichnis und
Prozesse waren entfernt, der fremde Before-Satz blieb erhalten.

Boundary05 pruefte erneut Source/Assemblies/Package, enthielt aber
keinen separaten erneuten SDK-Hash. Der neue Boundary06/Helperv4
bestaetigte deshalb **414 Source-Dateien, sechs Assemblies,
116 Paketdateien und den aktuellen SDK-Executable-Hash zusammen in
6,5094146s/60**. Das ist keine nachtraegliche Erweiterung von05.

Die neue Supervisorv7-Vorbereitung bindet die zusammenhaengenden
14 Vertraege statt der alten getrennten8+5-Auswahl plus den separaten
echten SQL-Praesenzfall. Die Originalfaelle sind nicht entfallen.
Der2700s-Ausfuehrungs-/60s-Abschluss-/17-Faelle-/11-Gates-Teil ist
gegenueberv6 unveraendert; der neue Read-only-Closurev4 ergaenzt nur
aktuellen SDK-Hash und den exakten Fremdressourcensatz. Der wirkliche
Prerequisite-Praefix wurde separat **in13,3772636s/60** ausgefuehrt:
alle aktuellen Querverbindungen und die34+14 vorgebundenen Originale
von Recovery06 wurden geprueft, ohne neue Runbindung oder Ressourcen
anzulegen. Beobachtung02 bleibt selbst ohne Runtimefreigabe; nur die
separate Entscheidung erlaubt neue serielle lokale Validierung.

Die konkrete zukuenftige ausserordentliche Route verwendet ausschliesslich
`Complete-Recovery07Cleanup-v1-preserved.ps1` mit vorab dauerhaft
geschriebenem vollstaendigem Originalmanifest. Der tatsächliche Aufruf
des enthaltenen60s-Prozesses wurde ohne Start abgefangen; seine
Vorbereitung pruefte13 Eingabe-/Ablehnungsfaelle. Gesamter frischer
Source-/SDK-/Assembly-/Package- und Fremdsatz-Preflight, Manifest,
Mutation und Nachvergleich bleiben **ein**60s-Prozess, keine Addition
von Teilbudgets. Die Route ist eng begrenzt auf einen beendeten
Recovery07/Build42-Timeout mit bereits vollstaendig bereinigter
Quellfixture, zwei exakt belegten lebenden Source-/Targetressourcen
und einer bereits entfernten Failed-start-Ressource. Unvollstaendige
oder abweichende Zustaende werden vor Mutation abgewiesen. Dies ist
Source-/Dateikontraktevidenz, **kein ausgefuehrter generischer
Pre-Ready-SQL-/Docker-Sondercleanup**. Ein fuer07 konkreter
Post-exit-Eingabebeleg wurde noch nicht erzeugt.

| Private Datei | SHA256 |
|---|---|
| `build-42-result.json` | `8eb1c77e6e9ba0f66d3ca6a6eecd3c9659270fa646b6d1921c591e30988a763e` |
| `offline-89-result.json` | `b718b6a4cad315b7a32522bcf72353ab447e7a0b6bdfc400b3220e68a29e1f6a` |
| `tests-90-result.json` | `50b1a09cb69ffcba34687ee74389848e63ede7367cd0e31fd723081e02232b47` |
| `tests-91-result.json` | `a59083c6e242b4255515b1678bf7b53d61d8cd7cd986f2ecdb9a045a79a79d3c` |
| `recovery-boundary-proof-06.json` | `9c9df9c47c0a77d85140342ad6d6e835845238fc175a2879651b66d4aa01ab80` |
| `recovery-07-cleanup-route.json` | `9a6b6d7914d11556e1c100512f16b7bb90340095ccfddb59be77fee2f389cba0` |
| `recovery-07-preparation-binding.json` | `fff85cb2c4500a6a8e7b97bbc840545d621dbd2f938bd7c3518630813dfd4535` |
| `recovery-07-preparation-proof-01.json` | `81789a957b689967ea7d67fb879260375db63fc61d28b555febb6f7c421fef73` |

Bis hier wurde noch kein Recovery07-Vollprozess gestartet und nichts
auf die alte Vorschau oder nach Azure promoviert.

### Recovery07: vollstaendiger lokaler Nachweis

Der anschliessende **einmalige Recovery07 aufb7ced182/Build42** bestand
saemtliche17 urspruenglichen Faelle mit17 Passed/0 Failed/0 Skipped,
einschliesslich aller elf urspruenglichen Fixturegates. Die Testnamen und
Gateinhalte wurden einzeln gelesen; keine Addition vorheriger Partitionen.
Quelle und oeffentliches Ergebnis sind `sourceTreeDirty=false`.

Der oeffentliche Prozess lief laut OS von
`2026-11-18T11:59:34.5904161Z` bis
`2026-11-18T12:26:59.3048926Z`: **1644,7144765s/2700**.
Der unabhaengige read-only Abschluss lief von
`2026-11-18T12:26:59.5128411Z` bis
`2026-11-18T12:27:58.5166952Z`: **59,0038541s/60**.
Beide Exitcodes sind0; Streams und Jobhandles sind geschlossen,
keine eigenen Nachkommen verblieben. Die getrennten Stopzeiten
betrugen23ms beziehungsweise0ms; Stop10s/Stream15s blieben unveraendert.
Die Abschlussgrenze wurde knapp eingehalten, nicht erweitert.

Die persistierten monotonen Controllerphasen ergeben gesondert
**1736,9298176s** von `preflight-start` bis
`independent-cleanup-finished`. Das ist keine Messung des vollstaendigen
aeusseren Shell-Prozesslebens einschliesslich der davor geprueften
Voraussetzungen. Der Abschnitt ab gespeicherter Laufbindung bis zum
Ergebniszeitpunkt ist separat1709,4291058s.

Die Abschlusspruefung bestaetigt den unveraenderten exakten fremden
Containersatz und beide fremden Volumes, die Entfernung aller eigenen
SQL-/Docker-/privaten Konfigurationsressourcen, das vollstaendige
Sourcejournal `CreateRequested` bis `Disposed` und die Abwesenheit seines
Elternverzeichnisses.414 Quellen, sechs Testassemblies,116 Paketdateien
und die SDK-Executable wurden erneut gebunden. Die Source war waehrend
der Wiederherstellung nicht verfuegbar. PSUR-/Gruppen-/BS-Historie,
Stichtags-/Addendum-Snapshots, offene Rueckfragen, historische
EUDAMED-Originale, native SQL-/Objektintegritaet und idempotente
Wiederaufnahme bleiben in diesem gemeinsamen Schnitt erhalten.

Run: `.local\v10\.local\recovery-run-3fb22dfb077e4dddb04d537ff13a48a9`.
Die erste Zeile der folgenden Tabelle liegt im oeffentlichen
`.local\v10\.local\recovery-07-public`; die weiteren privaten Belege
liegen in `.local\eudamed-integration-01`.

| Datei | SHA256 |
|---|---|
| `recovery-results.json` | `078b940d575657e7c39e73d1fe72048d8e4ef03ad1e717427708010ce7eb590f` |
| `recovery-07-bounded-result.json` | `6350aa75c45a3b25811bb7289c16ef1fca2f8aae9ec421c0bcea79c4824150e5` |
| `recovery-07-execution-owned.json` | `b60d930b2b443f800e78e57eb44cc7e73d6d4e7501d7b06c67d959d0df22529d` |
| `recovery-07-cleanup.json` | `e56ad98504ea84ced259583f75af15a871a70b93ef9157439176da630cffc650` |
| `recovery-07-binding.json` | `2ba05722ad43b8cfb0a5649ad5bf189be04935c68502f6d299a17b52385e69b6` |
| `recovery-07-owned-sql.json.journal.jsonl` | `5c8ce7193c404222748b62016e1425cd87257c247fe4c1a4dfa0b1c28ff27575` |

Es gab keinen Sondercleanup und keinen konkreten Sondercleanup-
Eingabebeleg. Recovery06 einschliesslich seines verlorenen585-Dateien-
Vorhervergleichs bleibt unveraendert negativ. `azureRecovery=false`
und `externalLedgerWitness=false` bleiben ausdruecklich. Es erfolgte
keine Vorschau-/Cloudpromotion. Die gleichzeitig nur im Primaercheckout
bearbeitete FSN-Publikation ist **nicht** Teil dieses Build42-Erfolgs;
ihre neue Native-/Browser-/Recoveryevidenz ist gesondert zu erbringen.

Die anschliessende FSN-Quellfassung827d984 wurde als eigener **Build43**
geprueft. Der Domain-Rebuild endete mit dem CompilerfehlerCS8602 in
der neuen Korrespondenzbewertung, nicht mit einem Timeout. Die eigene
Prozessgruppe und Streams wurden vollstaendig geschlossen; kein
Anwendungspaket oder Testpass wird aus diesem abgebrochenen Build
abgeleitet. Der Folgefix verwendet explizite nichtnullbare lokale
Sammlungen mit fachlichem Fehler fuer fehlende Eingaben, keine
Nullunterdrueckung oder stillschweigende leere Bewertung.

**Build44 auf918e6a5** bestand danach alle elf frischen Projektrebuilds,
den Web-/Solutionbuild, API-Publish, alle39 Bootstrapfaelle und den
Bootstrap-Publish. Der gebundene Stand umfasst417 Quelldateien,
sechs Testassemblies und116 Paketdateien; eigene Prozesse und Streams
sind geschlossen. **Offline92** bestand alle15 ausgewaehlten SQL-freien
Recoveryvertraege ohne Skip in31,6060184s/180, darunter den neuen
FSN-Verweisgraphen fuer historische SQL-Fassungen.

Der anschliessende **Native93 bleibt negativ:0/9 Passed**. Alle neun
FSN-Faelle scheiterten in32,7543747s/180 bereits an der unveraenderten
Sperre `eudamed_remote_reference`: Die neue Vorbereitung hatte die
FSCA nur menschlich eingefroren, aber noch keine korrelierte
Simulator-Geschaeftsannahme eingeholt. Ihre einzelne eigene Fixture
durchlief den vollstaendigen Journal-Lifecycle bis `Disposed`;
Fremdsatz, Datenbank-/Objektabwesenheit und Prozessabschluss wurden
geprueft. Kein Sondercleanup und kein FSN-Runtimepass daraus.

Die gezielte Quellkorrektur fuehrt vor der FSN-Erstellung die echte
lokale Queue-/Process-/Simulatorantwortfolge fuer die FSCA aus, statt
ein Remote-Kennzeichen einzusetzen oder den Produktionsguard zu
lockern. Auch E05 nutzt diese Voraussetzung. Im Recoveryaufbau wird
die FSN noch gegen die aktuelle angenommene FSCA freigegeben und
simuliert verteilt, bevor beide spaetere Dokumentfassungen erhalten;
die bisherigen historischen/current Assertions und17/11-Gates bleiben.
Diese Korrektur benoetigt ihren eigenen Build und neue Ausfuehrung.

Die anschliessenden reinen Quellpruefungen sind positiv:
`public-ui-contracts-05` pruefte alle elf echten Runnervertraege
in22,6306181s/60, einschliesslich fehlender/zusaetzlicher Auswahlfaelle
und unveraenderter Ownership-vor-CREATE-/Cleanup-/Umgebungsgrenzen.
`fsn-browser-source-01` bestand den strikten TypeScriptcheck und
die tatsaechliche Playwright-Enumeration in35,8422901s/120:
genau E03/E04/E05 im Dossierprofil, genau zwei PrimaryBaselinefaelle
und weiterhin elf Previewfaelle ohne E05. Kein Browser, API, SQL
oder Build wurde fuer diese beiden Quellpruefungen gestartet.

| Privater FSN-Zwischenbeleg | SHA256 |
| --- | --- |
| `build-44-result.json` | `f2721cc31ccb8285c71db564d32993700a0c181741b2f507008890d96b62c2f4` |
| `build-44-owned-job.json` | `fd82febcd2b37936d835064a8fc2ada3ff753387af029873376a2bce747cbea8` |
| `offline-92-result.json` | `9c875a2de5b51fabb2c1badd7b5ceaf7d02c920e37d07580cf52bb04481237a4` |
| `tests-93-result.json` | `5f15af86260230cc2520f3ceb61a1ed5641469f35bd4bc7994400896d3b9c73c` |
| `tests-93-owned-job.json` | `5e6ef4c20302a9247d03f96d10fc9f1b71e78b4dc1d6c481fca0c7cba90f47ce` |
| `tests-93-owned-sql.json.journal.jsonl` | `c9ffb4718bcd5053e1a8129ce65351e623a9ea0f97b22d1d0310d5e55cfe2805` |

**Build45 wurde vor jedem Buildaufruf abgewiesen.** Zwei bereits
vorhandene LF-/CRLF-Unterschiede zwischen Arbeitskopie und sauberem
Pruefclone passten nicht zum Bytebeleg des ersten Browserquellchecks.
Die negative Vorstartdatei bleibt erhalten; weder wurden Originalbytes
normalisiert noch ein Paket oder Buildpass daraus abgeleitet.
Der neue `fsn-browser-source-02` pruefte anschliessend die tatsaechlichen
Clonebytes: striktes TypeScript sowie die unveraenderten echten
Playwright-Auswahlen3/2/11 bestanden in41,089273s/120, weiterhin ohne
Browser, API oder SQL.

**Build46 auf `0fac3ae5e383da6a677a3c9aea6f7de77771f8f9`** bestand
alle elf frischen Projektrebuilds, Web-/Solutionbuild, API-Publish,
alle39 Bootstrapfaelle und Bootstrap-Publish. Die sechs Testassemblies
und116 Paketdateien sind genau an diesen Quellstand gebunden; alle
eigenen Buildprozesse und Streams sind geschlossen.

Danach bestanden ausschliesslich seriell mit dieser Buildbindung:

| Auswahl | Tatsaechlicher Nachweis |
| --- | --- |
| Native94, FSN-Publikation | Alle neun Faelle ohne Skip in92,1390881s/180; getrennte Freigaben, finale Simulation, Original-/Scopes-/Feedbackbindung, Replay und dringlicher Alternativweg. |
| Native95, urspruengliches MIR-Paar | Beide Comment-/InformationRequestfaelle gemeinsam in55,6845652s/300, alle46 fachlichen Phasen. |
| Offline94, Recoveryvertraege | Alle15 SQL-freien Original-/JSON-/Verweisgraph-/Retentionvertraege ohne Skip. |
| Native97, erweiterte Recoveryfixture | Ein Fall in56,3544385s/180; tatsaechlicher aktueller/historischer SQL-Originalsatz einschliesslich publizierter FSN und spaeterer Dokumentfassung. |

Die drei nativen Laeufe bestaetigen jeweils ihren vollstaendigen eigenen
Journal-Lifecycle, entfernte Testdatenbank/Objektdateien, unveraenderten
fremden Before-Satz und geschlossene Prozesse/Streams. Dies ist normaler
Abschluss, kein erstmaliger Realnachweis des ausserordentlichen
Gesamtjournal-Cleanups. Native93 und Build45 bleiben negativ.

| Privater Folgebeleg | SHA256 |
| --- | --- |
| `build-46-result.json` | `d38f7993042dfdc25551b3843a6a8028261baae36e81210ea361628e4d466522` |
| `build-46-owned-job.json` | `cdd56b34ff5d4d496ef0a6de0843b83d24ff9288ef0747608ac1cc24cee4ef04` |
| `tests-94-result.json` | `3cfdd076623c446bf569e1f1fd877e140b4fc7ec4a858b31b4083f55b421fab8` |
| `tests-94-owned-job.json` | `71c122728ddfc3246c19197068aa98341ce274a65da82f28903ed905dc7ddbaf` |
| `tests-95-result.json` | `b8755aae0416356b239246fa99aafbe635f16b21c767270ed9e6bd8382751e7c` |
| `offline-94-result.json` | `fdd63678cd138ce895d8d41615686ace76789e2abcd5ca7aac71b2196005ac8c` |
| `tests-97-result.json` | `d4543c20d034ee35fc619cdee0992d672c374b3195f62ca0123073eff86d3138` |

Build46 ist damit **kein neuer Browser-, vollstaendiger FSN-Restore-,
Operations-SQL-Schreibgrenzen- oder Auslieferungsbeleg**. E05, die
vollstaendige gemeinsame Recovery17/11 und EU-27 bleiben offen.
Recovery07 belegt weiterhin ausschliesslich b7/Build42. Die alte
742-Vorschau wurde fuer diese Schritte weder veraendert noch promoviert.

Der eigene Datei-only-Integrationsvertrag
`preservation-binding-01.json` bestand in38,4495698s/60. Die Einfuegung
erhaelt reversibel alle bestehenden Ownership-/Pfad-/Prozess-/Fremdsatz-
Pruefungen, liegt vor allen drei Mutationsstellen und vergleicht danach
den dauerhaft gespeicherten Originalsatz. Der tatsaechliche eingefuegte
Vorher-/Nachher-Code wurde auf eigenen synthetischen Dateien ausgefuehrt;
unvollstaendiges Hashen erreicht keine Fortsetzung zur Mutation.
Historischer Recovery06 kann nicht neu gebunden werden. Die eigene
Dateifixture ist entfernt, ihr vollstaendiger Vorherbeleg bleibt erhalten.
Dies ist weiterhin **kein ausgefuehrter SQL-/Docker-Sondercleanup**;
eine neue konkrete Ressourcenbereinigung benoetigt ihre eigene genaue
Run-/Quell-/Journalbindung.

| Private Datei | SHA256 |
|---|---|
| `build-40-result.json` | `c172fb5a44b6d029977bcaab9be4cdc6cd24afe71a36c6ff6a382a4e0df62c1d` |
| `offline-87-result.json` | `3a4b5bbd8bd9219c0b38cc97623e951556324f192005120cbe5638bbe102d610` |
| `tests-88-result.json` | `6bf6aeba584614c62d082543bc829e8811a0623249678385069ed32c5a4267b6` |
| `recovery-empty-json-diagnosis-01.json` | `a2668e7bc5288d4269dd254d78bf381c3f238ada6d9d1790da37c080b04e4cee` |
| `preservation-binding-01.json` | `ac92c6136a9a91e57a4b24013b8d61fe205271ad4210fe97a79be8aae3a3f2fb` |
| `preservation-binding-owned-01.json` | `e1a19f869b2975832eb03eaca199a99a895b53b0d1bb3826ad8b8f5d3c1e7449` |
| `Test-RecoveryPreservationBinding-v1-before.json` | `4b3f89cfe8d9017dc062217cf5f9f4df16063be8f221062b01c17006cd82c492` |

### Prospektive 45-Minuten-Grenze

Die nachfolgende ausdrueckliche Nutzerentscheidung erhoeht nur die
Gesamtprozessgrenze der naechsten vollstaendigen Recovery-Suite
von1800 auf **2700 Sekunden**. Der bereits beendete Recovery06 bleibt
mit seinen urspruenglichen Timeout-, Cleanup- und Evidenzdateien negativ.
Keine alte Datei wurde dafuer ersetzt oder umetikettiert.

Die neue private Generation `Invoke-RecoveryBounded-v6.ps1` verwendet
2700 beim wirklichen `Invoke-OwnedJobProcess`-Aufruf mit
`-FullRecoveryProcess`, bei der gebundenen Konfiguration und bei der
Pruefung der tatsaechlichen OS-Prozessdauer. `OwnedRecoveryJob-v3.ps1`
laesst die Ausnahme ausschliesslich fuer den exakten oeffentlichen
`scripts\Test-Recovery.ps1`-Einstieg zu. Sein Prozess-/Ownership-/Stop-/
Streamcode ist abseits der begrenzten Budgetvorpruefung unveraendert.
Die separate Abschlusspruefung bleibt60s, Einzeloperationen480s,
Stop10s und Streams15s;17 Originalfaelle und11 Fixturegates bleiben gleich.

Der gezielte Quellnachweis wertete die **tatsaechlichen beiden
Aufruf-ASTs mit einem nicht startenden Empfaenger** aus und pruefte
anschliessend den unveraenderten Guardcode im expliziten Dateikontext:
2700 fuer den vollstaendigen Prozess,60 fuer den Abschluss.
Unscoped2700,2701, anderer Einstieg und901 fuer einen anderen Prozess
wurden abgewiesen. Das ist Konfigurations-/Aufrufbindung, **kein
45-Minuten-Laufbeleg**. Der erste rein lesende Ableitungsversuch
scheiterte vor Ausgabe an fehlendem `$PSScriptRoot`-Kontext im isolierten
AST; die anschliessende Pruefung setzte diesen Dateikontext explizit.
Beide starteten weder Recovery noch SQL/Browser.

| Private Datei | SHA256 |
|---|---|
| `Invoke-RecoveryBounded-v6.ps1` | `c6070cce931a4402224dca868874761dffb0ef0790791a6ae79e53ce9f833891` |
| `OwnedRecoveryJob-v3.ps1` | `6fe09d9b4c76cad38906ed90a7cc6cbc9741175be394d603c76b9c3180ca5497` |
| `recovery-2700-binding.json` | `f7df0ee293e11e024c9e332aae600df26ec694e3e9b42ab11ac76473f8063fe3` |

Die Budgetaenderung allein ist keine neue Ausfuehrungsfreigabe. Die
gezielte Ursachenbehebung, frische Source-/Build-/Package-/Ressourcenbindung
und dauerhafte vollstaendige Vorher-Evidenz vor mutierendem Cleanup
bleiben Voraussetzungen. Es gab dafuer keinen neuen Vollversuch,
keine Bereinigung, keine Preview-/Cloudpromotion.

### Wiederherstellung ausschliesslich der freigegebenen 742-Vorschau

Der zuvor nicht erreichbare lokale Host wurde nach der bedingten
Wiederherstellungsfreigabe ausschliesslich aus dem bereits gebundenen
Paket `.local\app-replay-rfi-742db5d` gestartet:
Anwendung `742db5d7fc11b394436f9524b565fbb6f2b1077f`,
historische Browserquelle `8397fe47e67a861b2efd91fbddafe31cb9a7d06a`.
Alle 116 Paketdateien, bestehenden Konteneinstellungen, 53 Originale
und sieben vorhandenen DataProtection-Schluessel blieben unveraendert.
Der alte Prozess war abwesend; ein beabsichtigter Stop war in den
zugehoerigen eigenen Belegen nicht dokumentiert. Die Ursache des
frueheren Hostendes bleibt unbekannt; eine vorherige Heartbeatwarnung
beweist sie nicht.

Restore01 scheiterte **vor Start-Intent und API-Start** an einer
lokalisierten Zeitstempelkonvertierung. Die unveraenderte negative
Fassung bleibt erhalten. Restore02 korrigierte ausschliesslich die
JSON-Lesung mit `-DateKind String`; dieselben Freshness-/Paket- und
Startgrenzen blieben bestehen. Der neue Host wurde anhand seines
tatsaechlichen Starts, Images, Besitzers, Kommandozeilenpakets und
Listeners zugeordnet.

`http://localhost:5187/` war am 17.11.2026 um 20:07:19Z bereit,
um 20:10:17Z nochmals rein lesend als `ready` bestaetigt.
HTML sowie referenzierte JS-/CSS-Dateien entsprachen dem alten Paket.
Ein eigener neuer Browserkontext wurde dafuer nicht geoeffnet;
der Nutzerbrowser blieb unveraendert. Die unabhaengigen SQL-Aufnahmen
vor/nach dem Start bestaetigten dieselben 249 aktuellen/historischen
Fachzeilen mit 30.415.374 Payloadbytes, Schema2, erhaltenen Original-
versionen und keinem Ruecksetzen aktueller Staende. Operationszeilen
sind wie im bisherigen Erhaltungsvertrag ausgenommen.

Der beibehaltene Supervisor setzt Seed/AutoMigrate ausdruecklich aus,
lokale Operations/Agenttransport an und physische Loeschung aus.
Keine Migration, kein Seed, Backup/Restore oder Kontenreset.
Zugang erfolgt ueber die vorhandenen getrennten lokalen Demokonten;
Passwoerter bleiben ausschliesslich in der privaten
`.local\demo-access.txt`. Generische Neustarts mit den aelteren
Settings-Defaults sind kein gleichwertiger Wiederanlaufpfad.

| Privater Beleg | SHA256 |
| --- | --- |
| `restore-01-preflight-rejection.json` | `0c798f88f4226a6dbd09bfa39e0d4e8350b1f6cdfe6594546ab3e5759f424964` |
| `Restore-ApprovedPreview-v2.ps1` | `f3e7705d1de6c9b50fbd92df7e583034198416e6607d706a9f28ed66a891f6f5` |
| `restore-02-ready.json` | `9b0c052ad295a143446699f4e4ab159032757b206bc526f8ca0b22d0ad4d0c30` |
| `restore-01-before-state.json` | `978993fb549bd170ddc52951abdd344b9e8f63b6ebb17abb59c1db3caed47f10` |
| `restore-01-after-state.json` | `8d729afad493ee5bc3f41de8bb97b156f3327cb03c1ec909313f91a021bcc380` |

Diese Vorschau bleibt **deterministischer Replay mit simuliertem
Versand/Quittungen**, nicht Live-Foundry. Die sechs logischen Rollen,
getrennte Medical-/RA-Reviews, Fristen, Trends/CAPA, eingefrorener PSUR
und die bereits freigegebene Informationsrueckfrage stehen im alten
Paket; neuer EUDAMED-Schnitt sowie X-12/X-15 wurden nicht aufgeschaltet.
Lokale fachliche Demoabnahme und spaetere Azure-/Entra-/Live-KI-/
Betriebsabnahme bleiben getrennt. Keine Azure-Aktivierung oder
Cloudkosten-/Zielfreigabe folgt aus dieser Wiederherstellung.

## Eingeschraenkter SQL-Operationsvertrag: lokaler Nachweis

Der [Objekt-/Prozedurvertrag](sql-runtime-contract.md)
`cohors-operations-write-v1`, Schemaversion3, wurde in
`c13f041b1be009acb696b566eaaf60a9bbef62c6` implementiert und in
`dd112705af2baf6c4e7787794b782f4f8937b1e8` gegen SQL-Zielaliase
gehaertet. **Nur dd11270/Build49 ist der nachfolgend nativ gepruefte
Quell-/Paketstand.** Dieser Dokumentationsnachtrag ist kein Neubuild.

Die anschliessende Quellkontrolle von `Cohors.Bootstrap.Program` und
`BootstrapRequest.Parse` berichtigt ausschliesslich die Vertragsdoku:
der vorhandene Befehl ist `sql --expect-server ... --expect-database ... --apply`,
nicht `sql-migrate`. Die CLI kennt `sql` und `search`; ohne `--apply`
sind es lesende Remotepruefungen. Weder Runtime noch Build49-Belege
wurden fuer diese Dokumentationskorrektur geaendert oder neu ausgefuehrt.

Build47 bestand auf c13f041. Build48 auf dd11270 bleibt dagegen
**negativ**: neun von elf Projekten,900,4323136s im unveraenderten
600s-Gesamtbudget. Der Workflows-Rebuild meldete573,621s fuer Delete
und zwei MSB3231-Warnungen an eigenen generierten Verzeichnissen.
Sein beobachtetes Ownerintervall betrug647,8722676s; das dortige
alte `success=true` ist ausdruecklich kein180s-Nachweis.
Dieselben Warnungen gab es bereits im erfolgreichen Build47 bei
0,668s Delete. Die Warnung allein beweist daher nicht die Ursache
der Verzoegerung.

Der separate, unveraenderte Workflows-Rebuildprobe01 bestand danach
in102,7104856s/180 einschliesslich beobachtetem Ownerintervall;
alle425 Quelldigests blieben erhalten. Er ist **kein Gesamtbuild**.
Weder ACLs, OneDrive-Attribute, Quellpruefungen noch Zeitgrenzen
wurden veraendert. Alte Belege wurden nicht ueberschrieben und
Teilprojekte nicht zu einem spaeteren Gesamtpass zusammengerechnet.

Der eigenstaendige **Build49** bestand alle elf frischen Projekte
in418,0795611s/600. Jedes beobachtete Projekt-Ownerintervall lag
innerhalb seiner180s-Grenze. Auch die fuenf abschliessenden Schritte
bestanden: Web32,2717826s/120, Solution32,5553975s/180,
API-Publish8,5007981s/90,39 Bootstrapfaelle ohne Skip
in9,8108167s/180 und Bootstrap-Publish3,1965294s/90.
Gebunden sind425 Quelldateien, sechs Testassemblies,116 API- und
55 Bootstrap-Paketdateien; alle eigenen Buildprozesse und Streams
sind geschlossen.

**Native98** pruefte die fuenf tatsaechlichen Faelle der Kategorie
`OperationsSqlBoundary` in58,6053396s/180, ohne Fehler oder Skip:

| Faelle | Tatsaechlicher Nachweis |
| --- | --- |
| Case-/PSUR-/Korrespondenz-Watchdog | Echter `Tick` mit eingeschraenktem SQL-Benutzer; Freigaben, Berichte, urspruengliche Fristen und Originalkorrespondenz bleiben erhalten, Hinweise dedupliziert. |
| `OperationsPrincipalCannotForgeApprovalsIdentitiesOrDirectAuditOutboxAndSchemaWrites` | Medical-/RA-Identitaetsaenderung, Fristmutation, zusaetzliches Freigabefeld, gefaelschter Hinweis, Gross-/Kleinschreibungs-/Leerzeichenalias, direkte DML/DDL, freier Action-Parameter und generischer Schreibversuch mit gefaelschtem menschlichem Actor werden abgewiesen. Auch ein absichtlicher Benutzer-Spalten-GRANT umgeht das Spalten-DENY der Rolle nicht. |
| `OperationsFailureIsAtomicAndConcurrentExactRetryHasOnlyOneVersionAuditAndOutboxEffect` | Eigener Outbox-Triggerfehler nach Beginn der Fachschreibfolge rollt alles zurueck. Drei gleichzeitige identische Aufrufe und ein spaeterer Retry ergeben nur eine Version/Audit-/Outboxwirkung; abweichender veralteter Vorschlag scheitert. |

Die eingeschraenkten Benutzer sind echte `WITHOUT LOGIN`-Principals
in den eigenen Testdatenbanken; frische ungepoolte Verbindungen
verwenden `EXECUTE AS USER`. Es ist keine gemockte Berechtigung und
kein Test unter einer behaupteten Browserrolle. Das ist zugleich
**kein Entra-Anmelde- oder Azure-Functions-Hostnachweis**.

**Native99** bestand anschliessend die drei unmittelbar gekoppelten
Regressionen in62,0646546s/180: beide Varianten von
`T05_ModelOrSearchFailureLeavesHumanPreliminaryPathAndClockOperational`
und `T14_PromisedDatesSurviveUnknownReplayEscalateAndFinalOnlyCompletesBoundJurisdiction`.
Die ausdruecklich gescheiterten Modell-/Searchpfade lassen menschliche
vorlaeufige Bearbeitung und Watchdog zu; der fest zugesagte Folgetermin
wird weder neu begonnen noch auf eine andere Jurisdiktion uebertragen.
Die Tests fuehren keinen echten Foundry-Modellaufruf aus.

Beide nativen Laeufe haben je zwei genau journalgebundene Fixtures
vollstaendig ueber alle sechs normalen Lifecycle-Stufen beendet.
Eigene Datenbanken und ganze Objektverzeichnisse sind entfernt,
der fremde Before-Satz ist erhalten, Testdatenbanksaetze davor/danach
sind leer und alle eigenen Prozesse/Streams geschlossen.
Der prospektive Before-Beleg lag vor der ersten Fixturemutation vor.
Das ist kein nachtraeglicher Pass eines historischen Abbruchs oder
erstmaliger Realnachweis des ausserordentlichen Cleanups.

Tatsaechliche Build49-Ausgaben:

| Ausgabe | Pfad bzw. SHA256 |
| --- | --- |
| API-Paket | `.local\v10\.local\eu-b49` |
| Bootstrap-Paket | `.local\v10\.local\eu-k49` |
| Gemeinsame `Cohors.Infrastructure.dll` in API, Bootstrap und Operations | `30a395d987b0541b7b0e96700c1eddd9067091a4f5538b319110450462b5461c` |
| Operations-Buildausgabe | `.local\v10\src\Cohors.Operations\bin\Debug\net10.0` |
| `Cohors.Operations.dll` | `d1dc65ef8b6df441a1414a2e3562239e111ba29692430f2bb7367f896acdc0a2` |
| Operations-`functions.metadata` | `91cbe204f8becb6ad15fb6eb63d69002b91fa6eb1b4067e082818a693db8c305` |

Die Metadaten enthalten `DeadlineWatchdog`, `OperationsLive` und
`OperationsReady`. Fuer diesen Stand wurde **kein Operations-Release-ZIP
oder Containerimage** erzeugt; die Debug-Buildausgabe darf nicht als
bereits ausgerolltes Flexpaket bezeichnet werden. Der vorhandene
`Publish-Functions.ps1` bleibt der getrennte Release-Publishpfad.

| Privater Beleg unter `.local\eudamed-integration-01` | SHA256 |
| --- | --- |
| `build-48-project-partitions.json` | `4c299929100ca4286c8a594733a0b2093bb1817c71f859df0cf89b94c333adb9` |
| `build-48-partitions-owned-job.json` | `66cdcb176801fbff1c75f182712189c432eedd0b13d69989c709b3ba25b57486` |
| `build-49-result.json` | `ad99667a11540d97e0041f6501a27bd99139dcd7a57c84093835d94cd98f3f77` |
| `build-49-owned-job.json` | `94fd8965d76f1c0bbd967e55b6881972de1028b4bc68ec5f237c789c105b7073` |
| `build-49-source.json` | `552d8a8b61e7d36fa70c0f5d97f75fc01a58b9f6f0da0f4829e5c367a40faccf` |
| `tests-98-result.json` | `0ea99c2a6f138b7a00156984724483f508c3ef8eebf342bff17db7031f0a2461` |
| `tests-98-owned-job.json` | `234b51c2566c16d79766b2cd9c78453514a1e1a2ce17975bb1fbb0fdd5ff3977` |
| `tests-98.trx` | `dcc4bc8ef96cac2ce4cea94c7cc92b2ad281992925114bfc9711953a9022abc4` |
| `tests-98-owned-sql.json.journal.jsonl` | `b42e746bbf9073c034257b1d34bd75a2ad3228c128094134a8c801d0ad18ffe5` |
| `tests-99-result.json` | `12f1ff9207adc2231a3c876b74f1781d13cc9d757416efed3b8190c11323a053` |
| `tests-99-owned-job.json` | `05d752f55498d4d3414d8b1c971ccf5ea858bc5a217c355bc2b0df2a6b0786d4` |

Der Runtimevertrag verlangt getrennte API-, Operations- und
Bootstrapidentitaeten mit dem versionierten Objektvertrag statt
generischen Operations-DML-Rechten. Die Azure-/Entra-Runtimefreigabe
bleibt getrennt und offen.
Dieser Nachweis akzeptiert weder E05/EU-27 noch eine neue vollstaendige
FSN-Recovery oder ein rollenabhaengiges Foundry-Modellprofil.
Recovery07 bleibt auf b7/Build42, Recovery06 und Build48 bleiben
negativ; die bestehende Vorschau wurde nicht promoviert.

## Nicht ausgefuehrt / keine Behauptung

Keine Azure-Bereitstellung, keine Entra-Live-Freigabe, kein Foundry-Live-Modellergebnis, keine private Azure-Netzwerkabnahme, kein extern geschuetztes Ledger-Digestpublishing, keine produktive Retention, keine echte Behoerden-/BS-Akzeptanz.

Die unveraenderte 118er-Matrix und der abgeschlossene Einzelabgleich sind integriert. Alle Originalstatus bleiben Offen; zugeordnete Pilot- und Zielbetriebsnachweise sind nicht pauschal erfuellt. Klinische/medizinische Plausibilitaetsabnahme, vollstaendige Laenderkalender, SOP-/Sprachfreigaben, DSGVO/DPIA und ein unabhaengiger fachlicher Holdout bleiben separat. Weder behauptete Zeitersparnisprozente noch klinische Sensitivitaet lassen sich aus diesen kleinen technischen Demotests ableiten.
