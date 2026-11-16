# Lokale Wiederherstellung (T20)

Dieser Pfad sichert ausschliesslich den synthetischen Cohors-Piloten: echte lokale
SQL-Server-Datenbanken und lokale inhaltsadressierte Objektdateien. Ausgangspunkt
ist `a5c75454553e1325246546578a8ab3ef624ab119`, einschliesslich des unveraendert
uebernommenen [Byte-Integritaetsfixes](object-integrity.md)
`245b3b049228c3d4e01883bec56babca2012b91b`. Der uebergebene Recovery-Abschnitt
`09e3807` ist als `e46f5b4` integriert. Die Arbeitsfassung erweitert ihn um den
unten beschriebenen T18-Datenklassen-/Loeschkatalogvertrag. HTTP/UI und der
getrennte Functions-Host-/Neustartnachweis bleiben separate Pruefungen.

## Zurueckgegebene PSUR-/BS-Fixture: noch kein Laufnachweis

`d0e864dc0f7ccdbfd3758499bc4a9b4d88f59684` wurde nach Abschluss des begrenzten
L03-/Diagnostikblocks als **`c90664121339d81ca56a4d225203e768c2ebe146`**
integriert. Die zwei Git-Blobs in `scripts\Test-Recovery.ps1` und
`tests\Cohors.Tests\RecoveryTests.cs` entsprechen in diesem Integrationscommit
exakt der Uebergabe.
**Backup-/Restore-Produktionscode blieb unveraendert.**

Die neue Fixture ergaenzt drei PSURs in zwei getrennten Mehrproduktserien,
Gruppenfassungen v1/v2, getrennte Freigaben, zwei BS-Antwortrunden/CAPA,
Stichtags- und Nachtragszustaende sowie einen weiterhin nur medizinisch
freigegebenen Antwortentwurf. Nach Restore muessen Quellen, Historien, Hashes
und Mitgliedsscope unveraendert sein; der historische Receipt-Replay darf
keine neuen Schreibvorgaenge erzeugen. Die anfangs ausgewaehlte Originalevidenz
liegt zeitlich vor `CurrentEvidence.ValidFrom`.

Die anschliessende erste echte Typpruefung des Testprojekts ist nun
abgeschlossen: Basis `75bee999fff3c1bb723274a16486283aa8883892` plus explizite
Arbeitsfassung einschliesslich Sammelbericht-C#-Quellen. Der erste Build
scheiterte an genau zwei `xUnit2031` in `RequireSeparateApprovals`.
`Assert.Single(collection, predicate)` ersetzt dort die vorgeschaltete
`Where`-Filterung; exakt-eins-Pruefung und Rollenpraedikate bleiben gleich.
Der zweite Build endete mit **Exit 0, 0 Warnungen, 0 Fehlern**.
`.local\recovery-typecheck-01\source-02.json` bindet die Quellen und
aufgeloesten Dependencygraphen, SHA256
`ea29c9fdc7ed4b50d0590b22d0d207a403a023ce35adc7bf1a884e3325364f51`.
Beide Buildausgaenge, sechs Ausgabeassemblyhashes und der unabhaengig
gepruefte eigene Ressourcenabschluss stehen im kleinen `handoff.json`
derselben Ablage, SHA256
`281bddf277de1a76c4a01ed92733a8b65696d9120fb04de1e475c050f46cbaa4`.

Privates SDK 10.0.404/CLI-Home, beide First-use-Flags `false`,
`--no-restore`, keine globale Profilaenderung. **Kein SQL-, Docker- oder
Restorelauf wurde ausgefuehrt.** Die **14 Faelle / 11
Fixture-Abschlusspruefungen** bleiben ungetestete Laufzeiterwartungen;
historische 13/13 werden nicht als 14/14 umgedeutet.

## Isolierter Gesamtablauf

Voraussetzungen: Docker Desktop mit Linux-Engine, das gepinnte .NET-SDK und
**PowerShell 7.6 auf .NET 10**. `RecoveryTests` gehoert zu `Category=Sql` und
startet selbst Docker; dies gilt auch bei einem allgemeinen SQL-Testfilter.
Der lokale Runner verlangt `Category=Recovery` vollstaendig: elf echte
SQL-/Docker-Faelle und drei getrennte Pfad-/Retention-Vertragstests ohne SQL.
Diese **14er-Gesamtselektion wurde noch nicht ausgefuehrt**. Die vorherige
13er-Selektion und der urspruengliche 9er-Lauf bleiben unten getrennt als
historische Nachweise benannt. Der fruehere
Linux-Runner mit PowerShell 7.5 belegt keine Kompatibilitaet dieses neuen Pfads.

```powershell
.\scripts\Build-Local.ps1 -SkipWeb
.\scripts\Test-Recovery.ps1 -ArtifactDirectory .local\t20-neuer-nachweis `
    -SourceSqlPort 14358 -TargetSqlPort 14359
```

Die Ports werden vor Belegung geprueft; das Artefaktverzeichnis darf noch nicht
existieren. Der Test erzeugt eigene GUID-benannte, eigentuemerbeschriftete Container,
Volumes, Datenbanken und zufaellige Zugangsdaten. Er verwendet weder den Pilotport
5187 noch vorhandene `.local\settings.json` oder `.local\demo-access.txt`.
Quelle und Ziel laufen nacheinander, nicht als zwei gleichzeitig benoetigte SQL-Server.
Weitere SQL-Tests brauchen ebenfalls eine explizite eigene `COHORS_TEST_CONNECTION`.
Es gibt keine uebersprungenen Ersatztests und keine Nachahmung von `GITHUB_ACTIONS`.

## Separater Backup-/Restore-Aufruf

Beide Skripte verlangen eine explizite private JSON-Konfiguration unter einer
eigenen Unterstruktur von `.local`. Folgendes ist nur das Konfigurationsschema,
keine verwendbaren Zugangsdaten:

```json
{
  "syntheticOnly": true,
  "ownerId": "<eigene-32-hex-id>",
  "containerId": "<eigene-vollstaendige-64-hex-container-id>",
  "connectionString": "Server=127.0.0.1,14358;Database=Cohors_Recovery_<32-hex>;User Id=sa;Password=<frisch-generiert>;Encrypt=True;TrustServerCertificate=True;Pooling=False",
  "objectRoot": "C:\\<Arbeitskopie>\\.local\\<eigene-Quelle>\\blobs"
}
```

Der neue Container muss das Label `cohors.recovery.owner=<ownerId>` tragen und
exakt an den konfigurierten Loopback-Port gebunden sein. Unterstuetzt sind die
explizit benannten `Cohors_Test_<32-hex>`-/`Cohors_Recovery_<32-hex>`-Datenbanken,
Schema 2 und das bestehende Ortho-/Cardio-Pilotportfolio. Keine bestehende
Pilotinstanz umbenennen oder fremde Ressourcen umetikettieren, um diese Grenzen
zu umgehen. Symbolische Links und Junctions werden abgewiesen.
Pfadvergleiche sind nur unter Windows ordinal/case-insensitive, sonst ordinal:
Eine Linux-Geschwisterwurzel `.LOCAL` ist nicht die ignorierte `.local`-Wurzel.
Abschliessende Trennzeichen duerfen die Ueberlappungspruefung nicht umgehen.

Vor dem Backup alle eigenen Anwendungs-/Outbox-/Objektschreiber stoppen und
**nur die ausdruecklich ausgewaehlte eigene Quelldatenbank** mit
`ALTER DATABASE [<eigene-Quelle>] SET READ_ONLY WITH NO_WAIT` ruhigstellen.
`-Quiesced` bestaetigt diese Betriebsvoraussetzung; das Skript selbst beendet
keine Prozesse und erzwingt keinen Rollback in der Quelle.

```powershell
.\scripts\Backup-Local.ps1 -ConfigurationPath .local\mein-lauf\private\source.json `
    -BackupDirectory .local\mein-lauf\backup -Quiesced

# Den beim Backup ausgegebenen Digest separat aufbewahren, nicht aus einem
# spaeter veraenderten Backup neu ableiten.
$digest = '<RECOVERY_MANIFEST_SHA256-aus-dem-erfolgreichen-Backup>'
.\scripts\Restore-Local.ps1 -ConfigurationPath .local\mein-lauf\private\target.json `
    -BackupDirectory .local\mein-lauf\backup -ManifestSha256 $digest
```

Enthaelt der Cut Rohdaten-Aufbewahrungseintraege, verlangt Restore zusaetzlich
einen **aktuell menschlich geprueften** Katalog aus einem erfolgreichen,
quieszierten Format-v2-Backup und dessen separat aufbewahrten Digest:

```powershell
.\scripts\Restore-Local.ps1 -ConfigurationPath .local\mein-lauf\private\target.json `
    -BackupDirectory .local\mein-lauf\backup -ManifestSha256 $digest `
    -CurrentRetentionManifestPath .local\aktueller-cut\manifest.json `
    -CurrentRetentionManifestSha256 '<separat-gesicherter-aktueller-Digest>' `
    -CurrentRetentionConfirmed
```

Nicht automatisch den Digest des gerade gewaehlten alten Backups als
Aktualitaetsnachweis einsetzen. Derselbe Manifestpfad ist nur dann sachlich
geeignet, wenn der Operator ihn tatsaechlich als letzten relevanten
Aufbewahrungsstand bestaetigen kann. Nachtraegliche Retention-Aenderungen,
einschliesslich Holds, duerfen nicht uebersehen werden. Ist kein zuverlaessig
aktueller Katalog verfuegbar, bleibt der Rohdaten-Restore gesperrt.

Die Zielkonfiguration benennt einen eigenen frischen SQL-Server mit eigenen
Zugangsdaten, einen **noch nicht existierenden** Datenbanknamen und eine **noch
nicht existierende** Objektwurzel. Backup- und Objektwurzeln muessen disjunkt sein.
`-ApplicationDirectory` kann einen explizit gebauten API-Ausgabeordner statt
`src\Cohors.Api\bin\Debug\net10.0` angeben.

## Sicherungs- und Aktivierungsregeln

Das native SQL-Backup verwendet `COPY_ONLY` und `CHECKSUM`. Vorher und nachher
werden alle acht fuehrenden Tabellen, die wirklichen Anwendungsauditketten,
Historien, eingefrorenen Berichtsbindungen, Fristenprojektionen, Commands,
Outbox und Inbox kontrolliert. Alle Objektdateien werden ueber ihre **Rohbytes**
gehasht, kopiert und erneut geprueft; historische Referenzen, eingebettete
eingefrorene Pakete und aktuelle Revisionen gehen gemeinsam in die Deckungspruefung
ein. Ein Textleseerfolg allein ist kein Integritaetsnachweis. Das Manifest entsteht
erst nach den Kontrollen; Verzeichnisse ohne gueltiges Manifest sind unvollstaendig.
Gleichbleibende Vor-/Nachinventare ersetzen **keine verteilte SQL-/Blob-Transaktion**.

Restore prueft den separat uebergebenen Manifest-Digest, das SQL-Backup und die
Objektbytes, bevor es in eigene Staging-Ziele wiederherstellt. SQL-Zeilenhashes,
vollstaendige SQL-/Objektreferenzen und Audit werden dort nochmals kontrolliert.
Ein aelteres, fuer sich hashgueltiges Objektinventar wird auch dann abgewiesen,
wenn sein Manifest passend neu gehasht wurde, das SQL-Backup aber neuere Referenzen
enthaelt. Es gibt kein `WITH REPLACE`, kein Ersetzen fehlender Altbelege durch die
aktuelle Revision und keine automatische Reparatur.

Die alte `SearchProjection` wird verworfen. Der echte Adapter
`KnowledgeSearch` **ohne `SearchClient`** baut sie aus der wiederhergestellten
SQL-Historie auf und prueft Objektzugriff. Sein lokales Retrieval liest
berechtigte, zum Zeitpunkt gueltige SQL-Evidenzrevisionen mit Objektpruefung;
es ist keine lexikalische/semantische Suchengine und kein Azure-AI-Search-Nachweis.
Erst danach werden Datenbank und Objektwurzel ohne Ueberschreiben an die
Zielnamen verschoben und `<objectRoot>.recovery-ready.json` atomar veroeffentlicht.

Dieser Marker startet keine Anwendung und keinen Worker. Fuer eine gesonderte
Zielanwendung eigene Konten/Konfiguration verwenden, den Zielpfad als
`COHORS_HOME` mit Objekten unter `.local\blobs` setzen und zunaechst
`Seed=false`, `AutoMigrate=false`, `LocalOperations=false`, `PublicSources=false`
beibehalten. Auf keinen Fall Quellkonfigurationen oder Sendeauftraege kopieren.
`TransmissionUnknown` samt Korrelations-ID bleibt ungeklaert; bestehende Receipts,
Leases, Backoff-Zeitpunkte und erledigte Arbeit werden nicht zurueckgesetzt.
Fristen werden nicht neu berechnet und Annahmen nicht erfunden.

## T18: Explizite Rohdaten- und Loeschklassen

Neue Backups verwenden `cohors-local-recovery-2`. Bestehende flache
Format-v1-Backups ohne Rohdatenklassen bleiben lesbar; bestehende regulatorische
Objekt-IDs und eingefrorene Bytes werden nicht umgeformt.

Das Inventar unterscheidet unveraenderte regulatorische Hash-Dateien,
`raw-quarantine/<hash>` und `.deletion-receipts/<raw-hash>.json`.
Bei Journalen ist `hash` der SHA256 ihrer wirklichen Dateibytes,
`objectHash` der Hash des betroffenen Rohobjekts. Nur diese benannten
Unterverzeichnisse und regulaere Dateien sind erlaubt; keine Links,
beliebigen Verzeichnisbaeume oder Namensausnahmen.

Die verifizierten aktuellen SQL-Aufbewahrungszeilen bilden
`cohors-retention-cut-1`. Historische eigene Rohdaten-Aufbewahrungspayloads
muessen dieselbe unveraenderliche Identitaet und Objektbindung haben.
Nur ein aktueller `Deleted`-Stand mit passender menschlicher Scope-Freigabe,
abgelaufenem Mindestintervall, SQL-Abschlussbeleg und bytegenau identischem
kanonischem physischem Journal hebt **seinen eigenen** alten Rohbytes-Bezug auf.
Aktive Rohobjekte bleiben erforderlich. Unabhaengige Objektbindungen,
regulatorische Evidenz, Agentenkontexte oder historische Shared-Hash-Referenzen
bleiben Loesch-/Recovery-Blocker, auch bei vorhandenen Loeschbelegen.

Fehlende Journale, nicht inventarisierte Rohobjekte, wieder aufgetauchte Bytes
in beiden Objektbereichen und `Deleting`-/`DeletionFailed`-Cuts werden abgewiesen.
Ein Teilvorgang muss vor dem Backup ueber den autorisierten Anwendungsabgleich
aufgeloest werden; Backup/Restore behauptet keinen nachtraeglichen Abschluss.
Nach dem nativen SQL-Restore wird der Retention-Cut nochmals aus den echten
SQL-Zeilen aufgebaut und mit dem Manifest verglichen.

Ein altes Backup kann spaetere Loeschungen nicht selbst kennen. Deshalb muss
sein Rohdaten-Katalog exakt zum separat bestaetigten aktuellen Katalog passen.
Bei Abweichung wird ein neuer konsistenter Cut verlangt, statt alte SQL-Historie
mit neuen Metadaten zu ueberlagern. Das ist bewusst konservativ: Auch andere
Aenderungen/Ergaenzungen am Rohdaten-Katalog koennen einen alten Cut sperren.
Das Skript prueft Bindungen und Digests, **nicht automatisch die Wahrheit der
Aktualitaetsbestaetigung**. `automaticFreshnessProof=false` bleibt im Ready-Marker.
Dies ersetzt keinen externen Witness, keine Backup-Loeschung und keine
Bereinigung fremder Kopien.

Die erweiterte SQL-Fixture nimmt ein echtes Backup vor der Testroh-Loeschung
und einen zweiten Cut danach. Der alte Cut ist benanntes, isoliertes
Negativtestmaterial; seine physische Vernichtung wird nicht behauptet.
Der Positivfall muss aktive Rohbytes, geloeschte Abwesenheit, exaktes Journal
sowie alle bisherigen Evidenz-/Fristen-/Unknown-/Receipt-Invarianten erhalten.
Negative Varianten pruefen fehlenden aktuellen Katalog, den alten nativen
Cut, fehlendes Journal, wieder aufgetauchte Bytes und unabhaengige Bindungen.

Zunaechst bestanden **20/20** gezielt ausgewaehlte
Nicht-Docker-Tests, ohne Skips: neun Store-Integritaet, vier Retention-Regeln,
vier lokale Retention-Objekttests sowie drei Recovery-Pfad-/Vertragstests.
Der echte PowerShell-Helper musste dabei an die kanonische Journalserialisierung
angebunden werden; die fehlgeschlagenen Vorlaeufe waren kein Erfolg.
Historischer TRX: `.local\test-results\retention-recovery-integration-offline.trx`.
Danach bestand die vollstaendige integrierte Kategorie mit **13/13**,
ohne Skips: `.local\t18-t20-integrated-current\recovery-results.json`.
Vier Implementierungsdateien und fuenf tatsaechlich ausgefuehrte
Anwendungsassemblies sind gehasht, ein schmutziger Quellbaum ist separat
vom HEAD gekennzeichnet. Die 13 erwarteten Tests und zehn
SQL-Fixture-Abschlusspruefungen wurden nicht reduziert. Nach Abschluss
bestanden keine mit dem exakten Eigentuemerlabel verbundenen eigenen
Container oder Volumes mehr.

Dieser Nachweis liegt vor spaeteren Retention-Fehlerdiagnostikaenderungen.
Ein anderer kompletter Anwendungslauf scheiterte einmal im parallelen
Raw-Delete-Test an bisher ungeklaerter I/O; der gruene 110er-Wiederholungslauf
erklaert diese Ursache nicht. Aktuelle Recovery-Wiederholung,
Linux-Gesamtintegration und Cloud-DR bleiben offen. Die qualifizierte
Replay-UI-Teilabnahme mit deaktivierter physischer Loeschung ersetzt diese
Gates nicht; siehe [Nachweisstand](validation.md).

Die spaetere saubere ee13e91-Wiederholung mit **13/13** ist unter
`.local\recovery-ee13e91-current\recovery-results.json` gebunden
(SHA256 `118cb0525cae037def6e4094041e86d7127197e52135ce00e50ba2fec3c9ae14`).
Auch sie liegt vor dem aktuellen Audit-/Diagnostikstand und der neuen
PSUR-/BS-Fixture; sie bleibt ausschliesslich historisch.

## Historischer Owner-Nachweis am 15.11.2026, vor T18-Integration

Der uebergebene Stand wurde auf `245b3b0` mit den vier im jeweiligen Bericht
gehashten Implementierungsdateien ausgefuehrt. Der erzwungene Solution-Neubau
bestand ohne Warnungen/Fehler. Die Windows-Partitionen sind disjunkt und
zusammen vollstaendig: **86/86 in zwei Laeufen**, nicht ein einzelner 86er-Lauf.

| Ausfuehrung | Ergebnis | Ressourcenabschluss |
| --- | --- | --- |
| Windows, `Category=Recovery` | **9/9**, kein Skip | Drei eigene Container/Volumes entfernt, einschliesslich Fehlstart |
| Windows, `Category!=Recovery` | **77/77**, kein Skip; 68 bisherige plus neun Storetests | Eigener SQL-Container und eigenes Volume entfernt |
| Linux, komplette `RecoveryPathTests` | **1/1**, kein Skip | Eigener SDK-Container ohne Mounts/Volumes entfernt |

Der Linux-Fall lief auf Ubuntu 24.04.5, SDK 10.0.404, PowerShell 7.6.10 und
.NET 10.0.15, auf nativen case-sensitive Container-Dateien ohne Windows-Bindmount.
Die vier Quellhashes stimmen mit Windows ueberein; Plattformflags wurden nicht
veraendert. Er prueft den echten Linux-Zweig der C#-/PowerShell-Pfadgrenze,
**nicht** eine Linux-SQL-/Objekt-/Outbox-Wiederherstellung. Ein anfaenglicher
SDK-Aufruf ohne Assets/ohne TRX wurde nicht als Test gewertet; erst nach
nachgewiesenem `NETSDK1004` und Locked Restore entstand der bestandene TRX.

Der damalige Positivfall stellte 39 Objektdateien mit historischer und aktueller
Spezifikationsrevision, byteidentischem
MIR-Paket, offener NI-Pflicht und offener fester Folgeberichtspflicht wieder her.
Der zugesagte Termin `2026-11-22T23:59:59.9999999Z` und dessen SafetyDue
`2026-11-22T11:59:59.9999999Z` blieben unveraendert. Die Quelle war vorher aus SQL
und Dateisystem entfernt, ihr eigener Container gestoppt und ihr Port unerreichbar.
`CaseService`, `RegulatoryService.Export` mit XSD, `SqlStateStore.Audit/Manifest`,
`KnowledgeSearch.Rebuild/Retrieve` und `OutboxProcessor` liefen gegen das Ziel.

Die Negativfaelle enthalten fehlende historische Objekte, Bitflip **und einen
UTF-8-BOM mit unveraendert dekodiertem Text**, aelteres Objektinventar, bestehende
SQL-/Dateiziele sowie eine schreibbare Quelle und einen vorhandenen Backup-Pfad.
Fehler aktivierten kein Ziel; ein unabhaengiger vorhandener Sentinel blieb erhalten.

Beim kontrollierten echten SQL-Startfehler gibt es keine erfolgreiche ID-Rueckgabe
von `docker start`.
Die Fixture erfasst deshalb schon nach `docker create` die unveraenderliche ID,
den Owner, eine frische Operationsmarke und das zugehoerige Volume. Auch ein
Create-Ergebnis ohne ID kann nur ueber genau diese Labels zugeordnet werden,
nie allein ueber einen Namen. Unbekannte/mehrdeutige Identitaet bleibt ein Fehler,
kein Cleanup-Erfolg. `owned-resources.json` haelt nur gefilterte Identitaeten fest;
der Fehlstarttest prueft wirkliche Abwesenheit und den erhaltenen SQL-Sentinel.

Bereinigte Nachweise im damaligen Recovery-Owner-Arbeitsbereich:
`.local\t20-windows-current\recovery-results.json` und `resource-cleanup.json`,
`.local\t20-windows-regressions\results.json`,
`.local\t20-linux-current\results.json`.
Der finale quellengebundene Uebergabebundle liegt unter
`.local\t20-handoff\verification.json`. Er bindet den lokalen Commit an die
ausgefuehrten Dateihashes, Zaehler und Ressourcenabschluesse, ohne Rohlogs/Zugangsdaten.

Getrennt archiviert bleiben der fruehere **7/7**-Lauf
(`.local\t20-evidence-final\recovery-results.json`) und der spaetere **83/84**-Lauf
(`.local\t20-final-evidence\acceptance-results.json`). Letzterer scheiterte nach
den Positivfall-Assertions in `RecoveryTarget.DisposeAsync` und Collection-Cleanup
beim Wegfall der Docker-Engine und bleibt **fehlgeschlagen**. Nach Wiederkehr waren
die dokumentierten Container bereits abwesend; nur drei verifizierte unbenutzte
eigene Volumes wurden entfernt. Die fehlende volle historische ID des einmaligen
Gesamtlauf-Containers bleibt im separaten Inventar gekennzeichnet, nicht erraten.
Dieser spaetere Ressourcenabschluss macht den alten Lauf nicht nachtraeglich gruen.
Kein gemeinsamer Dienst oder Schutzmechanismus wurde geaendert.

Die unabhaengigen T20-Referenzen wurden gegen SHA-256
`66de6157cd25132ab691a371eb386a3fe9060a42401dca61f8a09402d787c7e9` geprueft.
Ihr urspruengliches `applicationVerified=false` wird nicht rueckwirkend umgedeutet.

## Grenzen und Aufraeumen

Nur lokale funktionale Wiederherstellung, keine Azure-PITR-/Regions-/Storage-
Abnahme, kein RPO/RTO-Versprechen, kein WORM und kein externer Ledger-Witness.
Der Blob-SDK-Anteil der neun Storetests nutzt synthetischen HTTP-Transport.
Es gab keine realen Patienten, Behoerden-/Klinik-/BS-Sendungen oder Foundry-Aufrufe.
Der Functions-Host-Nachweis bleibt beim getrennten T12-Abschnitt.

Der Test loescht ausschliesslich seine eigenen SQL-Ziele, Container, Volumes und
Zugangskonfigurationen nach gepruefter Identitaet. Backups und rohe Diagnosen
bleiben privat unter dem eigenen `.local`-Laufverzeichnis und gehoeren nicht in Git.
Bei einem Infrastrukturfehler kein pauschales Cleanup und keinen Erfolg behaupten:
nur die konkret erzeugten Ressourcen nach Identitaetspruefung entfernen.
