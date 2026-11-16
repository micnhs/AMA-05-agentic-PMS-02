# Cohors

**Product safety, together.** Ein ausfuehrbarer, ausschliesslich synthetischer Pilot fuer einen agentenunterstuetzten Post-Market-Surveillance-Regelkreis.

KI entwirft und begruendet. Medical Safety und Regulatory Affairs entscheiden mit **getrennten Konten**. Es gibt keine echten Patientenfaelle, Kliniknachfragen, Behoerdenmeldungen oder produktiven Zugangsdaten.

## Lokal starten (Windows / PowerShell)

Voraussetzungen: PowerShell 7.6 oder neuer (Recovery-/Browserhelfer laden .NET-10-Assemblies), Node.js 22.12 oder neuer, .NET SDK gemaess `global.json`, Docker Desktop mit Linux-Engine. SQL Server Developer benoetigt ausreichend Docker-Arbeitsspeicher. Fuer Browserabnahmen wird lokal Microsoft Edge verwendet.

```powershell
.\scripts\Initialize-Local.ps1
.\scripts\Build-Local.ps1
.\scripts\Start-Local.ps1
```

Die Anwendung ist unter **http://localhost:5187** erreichbar. `Start-Local.ps1` bleibt im Vordergrund; `Ctrl+C` beendet die API, nicht die SQL-Datenbank. SQL ist ausschliesslich an `127.0.0.1:14338` gebunden und verwendet ein eigenes Docker-Volume.

Getrennte zufaellig erzeugte Kennwoerter liegen nur in `.local\demo-access.txt`. Diese Datei und `.local\settings.json` sind gitignoriert und duerfen nicht geteilt werden. Es gibt keinen Browser-Rollenumschalter.

| Konto | Verantwortung |
| --- | --- |
| `pms.demo` | Intake, Recherche, begruendete Entwurfsauftraege |
| `medical.demo` | Medizinische Bewertung und erste Freigabe |
| `ra.demo` | Regulatorische Pruefung, zweite Freigabe, Simulator/Abgleich |
| `quality.demo` | Massnahmen-/Dokumentkontrolle und Wirksamkeitspruefung |
| `auditor.demo` | Lesender Nachweiszugriff |

Ein lokales SDK kann unter `.tools\dotnet` liegen; die Skripte bevorzugen es. Auf diesem Entwicklungsrechner scheiterte der NuGet-Standardendpunkt am TLS-Handshake. `NuGet.Config` verwendet deshalb Microsofts oeffentlichen `dotnet-public`-Feed **mit normaler TLS-Pruefung**, keine unsichere Zertifikatsumgehung.

### Bereits veroeffentlichte Vorschau erhalten

Auf dem Pruefrechner laeuft die qualifizierte 5187-Vorschau aus dem separat
veroeffentlichten, unveraenderten Paket `.local\app-replay-2a29196`, Quellstand
`2a291966e551f5f69ac7ceed2cf1d5aed1c0c13f`. Ihre expliziten Startwerte sind
`Cohors__Seed=false`, `Cohors__AutoMigrate=false`,
`Cohors__LocalOperations=true`, `Cohors__AgentTransport=Local` und
`Retention__PhysicalMode=Disabled`. Die lokale Outbox-/Watchdog-Emulation ist
kein getrennter Functions-Hostnachweis.

Die obige Initialisierungsfolge gilt fuer eine neue lokale Installation,
**nicht** fuer das Ueberschreiben oder Neuinitialisieren dieser laufenden Demo.
Die bestehende `.local\settings.json` wurde nicht geaendert und enthaelt noch
den urspruenglichen Seed-Standard; ein Neustart ohne die expliziten Overrides
ist deshalb kein gleichwertiger Erhaltungsweg. Neue Pakete und isolierte
Testdatenbanken getrennt erstellen, das laufende Paket und die Demo-SQL
waehrend der weiteren Implementierung unangetastet lassen. Die aktuellen
122/122 Anwendungs- und 6/6 Preview-Ergebnisse sowie die **historisch
getrennten** ee13e91-Recovery-/Physical-Nachweise stehen in
[Validierung](docs/validation.md).

## Vorfuehrung

1. Als PMS Specialist eine synthetische Beschwerde mit eigenstaendiger Vorfallreferenz aufnehmen. UDI darf unbekannt sein. Die eingegebene erste Organisationskenntnis ist nicht der Systemeingang.
2. Eine **Replay**-Analyse beauftragen. Zwoelf Abschnitte, belegte Aussagen, Gegenhypothesen, Empfehlungen und offene Fragen in der Workbench lesen; Originalfundstellen und Hash pruefen.
3. Einen vorlaeufigen Bericht vorbereiten. Mit `medical.demo` die expliziten synthetischen MIR-Pflichtdaten samt Belegen und fest zugesagtem Folgetermin erfassen, die Original-XSD-Pruefung ansehen und den konkreten Inhalt begruendet freigeben.
4. Abmelden, mit `ra.demo` anmelden und das gebundene Paket regulatorisch freigeben. Das Paket wird eingefroren.
5. Im Simulator `Unknown` waehlen. Es liegt **keine bestaetigte Annahme** vor. Zuerst die Korrelations-ID abgleichen, nicht erneut senden.
6. Audit Explorer und Manifest pruefen. RFI, Folgeberichte, Trends/CAPA und PSUR werden im selben fachlichen Regelkreis bearbeitet.

Die Logo-PNG und MP4 sind unveraenderte Originaldateien. Die Animation startet nur ausdruecklich; bei `prefers-reduced-motion` wird ausschliesslich das statische Logo gezeigt.

## Architektur und Grenzen

- React/TypeScript, ASP.NET Core/.NET, SQL Server als fuehrender Zustand. Kein statischer Mock.
- SQL-Aenderung, Historie, Audit und Outbox liegen in einer Transaktion. Belege sind inhaltsadressiert; Search ist eine wiederaufbaubare Projektion.
- Lokale SQL-Outbox-/Watchdog-Worker sind als **lokale Orchestrierungsemulation** gekennzeichnet. Sie sind kein Nachweis eines ausgefuehrten Azure-Durable-Hosts.
- Azure-Adapter fuer Blob, AI Search, Entra und Foundry sind getrennt konfigurierbar. Lokal werden SQL Server, ein lokaler Objektspeicher, eine SQL-abgeleitete Suchprojektion und Replay verwendet.
- **Live** bedeutet einen echten konfigurierten Foundry-Modellaufruf. Fehlende Konfiguration oder Fehler bleiben Fehler; es gibt keinen stillen Replay-Fallback.
- Behoerden- und Klinikkommunikation ist ausschliesslich simuliert. XSD-Gueltigkeit ist von Versand, fachlicher Vollstaendigkeit und Behoerdenakzeptanz zu unterscheiden.
- LegalDue wird in der gespeicherten Jurisdiktionszeitzone angezeigt, auch wenn der Browser eine andere Zeitzone hat. SafetyDue ist eine fruehere interne SOP-Frist. GB und NI bleiben getrennt.

Der erste End-to-End-Schnitt wurde mit echter lokaler SQL-Persistenz und Browserabnahmen ausgefuehrt. Der vollstaendige Paket-/Abnahmestand und verbleibende Einschraenkungen stehen in [Validierung](docs/validation.md); nicht jeder sichtbare Arbeitsbereich bedeutet eine bereits abgeschlossene Fachabnahme.

## Tests und Betrieb

```powershell
.\scripts\Test-Local.ps1
```

SQL-Tests verwenden eine **eigene zufaellig benannte Testdatenbank**, niemals die Demo-Datenbank. Sie verweigern nicht lokale SQL-Server. Die Recovery-Kategorie startet zusaetzlich eigene Docker-SQL-Instanzen; `Test-Local.ps1` ist deshalb kein leichter Preview-Check.

Der Browserteil veroeffentlicht den zuvor gebauten Debug-Stand und nutzt `Test-Ui.ps1`: eigene Testdatenbank, eigener API-Prozess auf Port 5197, getrennte Konten einschliesslich zweier unabhaengiger Einzelfamilienkonten. Die laufende Demo auf 5187 wird nicht neu initialisiert oder veraendert. Das explizite `Preview`-Profil erwartet im weiterentwickelten Quellstand acht Szenarien mit **`Retention:PhysicalMode=Disabled`**, einschliesslich des getrennten BS-Rueckfrage-/Antwort-/Massnahmenzyklus, seiner historischen PSUR-Snapshotbindung, des Produkt-Auditexports und der neu vorbereiteten periodischen Sammelmeldung. Der Auditfall wurde gesondert im **`AuditExport`-Profil 1/1** geprueft; der Sammelberichtfall wurde noch nicht ausgefuehrt. Das ist kein 8/8-Lauf und kein Upgrade der unveraenderten 2a29196-Vorschau mit ihren sechs bestandenen Faellen. **`PhysicalRetention`** wurde getrennt 1/1 mit echter Fehlerdiagnostik und kontrolliertem Abgleich geprueft; dadurch wird weder der physische Pfad auf 5187 aktiviert noch produktiv freigegeben. Ein eigener Preview-Lauf nach `Build-Local.ps1` ist nach gesonderter Lauf-Freigabe moeglich:

```powershell
.\.tools\dotnet\dotnet.exe publish src\Cohors.Api\Cohors.Api.csproj --configuration Debug --no-build --no-restore --output .local\ui-package
.\scripts\Test-Ui.ps1 -ApplicationDirectory .local\ui-package -ArtifactDirectory .local\ui-results-01 -Profile Preview
```

Fuer ausschliesslich den benannten Produkt-Auditfall denselben Runner mit
`-Profile AuditExport` und einem **neuen** Artefaktverzeichnis verwenden.
Das isolierte `PhysicalRetention`-Profil verlangt ebenfalls seinen eigenen
freigegebenen Lauf; der physische Fehlertest verweigert die 5187-Vorschau.

Fuer jeden Lauf einen neuen Ergebnisordner verwenden. JUnit, Screenshots und Quellen-/Assemblybindung liegen darin. Der Runner beendet nur seinen eigenen API-Prozess, entfernt seine Datenbank und privaten Testkonfigurationen und prueft den Datenbankabschluss. Private Logs und Zugangsdaten duerfen nicht geteilt werden. Die normale Vorschau emuliert Outbox/Watchdog lokal; sie ist kein aktueller Nachweis getrennter Functions-Hosts, Live-KI, Azure, Lastprofil oder vollstaendiger T18/T20-Abnahme.

Weitere Informationen:

- [Unveraenderter freigegebener Architekturentwurf](docs/architecture/approved-design.md)
- [Implementierungspraezisierungen](docs/implementation.md)
- [Azure-Uebersicht](docs/architecture/azure-overview.svg)
- [PMS-Prozess](docs/process/pms-workflow.svg)
- [Validierung und offene Gates](docs/validation.md)

**Keine Deployment-Freigabe:** Subscription, Region, Modell, SKU, Kosten, private Netzwerkpfade und irreversible produktive Retention muessen getrennt genehmigt werden. Dieser Pilot ist keine klinische, rechtliche oder regulatorische Betriebsfreigabe. Die unveraenderte Anforderungsmatrix und der abgeschlossene Einzelabgleich aller 118 IDs liegen im Repository; Originalstatus, verbleibende Pilotabnahme und Zielbetriebsgrenzen bleiben sichtbar. Die Zuordnung allein ist kein Erfuellungsnachweis.
