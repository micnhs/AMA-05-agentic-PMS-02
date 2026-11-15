# Lokale Wiederherstellung (T20)

Dieser Pfad sichert ausschliesslich den synthetischen Cohors-Piloten: echte lokale
SQL-Server-Datenbanken und lokale inhaltsadressierte Objektdateien. Ausgangspunkt
ist `a5c75454553e1325246546578a8ab3ef624ab119`, einschliesslich des unveraendert
uebernommenen [Byte-Integritaetsfixes](object-integrity.md)
`245b3b049228c3d4e01883bec56babca2012b91b`. Retention, Fachfunktionen, HTTP/UI und
der getrennte Functions-Host-/Neustartnachweis sind nicht Bestandteil dieses Abschnitts.

## Isolierter Gesamtablauf

Voraussetzungen: Docker Desktop mit Linux-Engine, das gepinnte .NET-SDK und
**PowerShell 7.6 auf .NET 10**. `RecoveryTests` gehoert zu `Category=Sql` und
startet selbst Docker; dies gilt auch bei einem allgemeinen SQL-Testfilter.
Der lokale Runner fuehrt `Category=Recovery` vollstaendig aus: acht echte
SQL-/Docker-Faelle und einen getrennten Pfadtest ohne SQL. Der fruehere
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

## Ausgefuehrter Stand am 15.11.2026

Der aktuelle Stand wurde auf `245b3b0` mit den vier im jeweiligen Bericht
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

Der aktuelle Positivfall stellte 39 Objektdateien mit historischer und aktueller
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

Aktuelle bereinigte Nachweise:
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
