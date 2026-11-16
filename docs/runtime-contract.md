# Verbindlicher Host- und CI-Vertrag

Integrationsvertrag, kein Nachweis einer Azure-Abnahme. Die anfaengliche gemeinsame Produktions-Agentenruntime/Functions-Container-Annahme wurde nach Abgleich mit dem genehmigten Konzept ausdruecklich zurueckgenommen. Die bestaetigte Option 2 gilt; kein neues Hostingmodell oder Deployment ist damit genehmigt.

## Laufende qualifizierte Vorschau

Der aktuell auf 5187 veroeffentlichte lokale Replay-Schnitt verwendet
`.local\app-replay-2a29196` aus
`2a291966e551f5f69ac7ceed2cf1d5aed1c0c13f`, mit `Seed=false`,
`AutoMigrate=false`, `Retention:PhysicalMode=Disabled`, `LocalOperations=true`
und `AgentTransport=Local`. Er bleibt waehrend weiterer Quellarbeit
unveraendert. Die 122/122 Anwendungsfaelle und 6/6 isolierten Browserfaelle
sind in [Validierung](validation.md) an dieses Paket gebunden; Recovery war
darin ausdruecklich separat. Die alten ee13e91-Nachweise bleiben historisch.

Der getrennte lokale Linux-Hostlauf T12 Lauf09 gilt weiterhin fuer
`245b3b0`, nicht fuer 2a29196 oder spaetere Fachquellen. Weder der folgende
Hostvertrag noch ein erneutes Ausfuehren eines unveraendert auf diese Baseline
gebundenen Harness ersetzt den ausstehenden aktuellen Quellen-/Hostnachweis.
Die vorhandene Demo und ihre SQL-Instanz nicht fuer isolierte Abnahmen
neu initialisieren oder stoppen.

Der spaetere L03-/I/O-Pruefblock verwendet ausschliesslich ein eigenes Paket
unter `.local\audit-diagnostics-01\application`: 17/17 gezielte SQL-Faelle
und getrennt 1/1 `AuditExport`/1/1 `PhysicalRetention`. Dies ist kein
aktueller separater Hostlauf und kein Upgrade der 5187-Vorschau. Die
quellengebundenen Ergebnisse und der terminale eigene Ressourcenabschluss
stehen in [Validierung](validation.md).

## Auslieferung

| Komponente | Artefakt / Entry-Point | Port / Health |
| --- | --- | --- |
| React + API | `src/Cohors.Api/Dockerfile`, `cohors-api`, `dotnet Cohors.Api.dll` | 8080; `/health/live`, `/health/ready` |
| Interne Agent Runtime | `src/Cohors.AgentHost/Dockerfile`, `cohors-agents`, `dotnet Cohors.AgentHost.dll` | intern 8080; `/health/live`, `/health/ready` |
| Durable Workflows | .NET-isolated-Flex-Codepaket `cohors-workflows.zip` | `/api/health/live`, `/api/health/ready`; kein Flex-targetPort |
| Unabhaengige Operations | .NET-isolated-Flex-Codepaket `cohors-operations.zip` | `/api/health/live`, `/api/health/ready`; kein Flex-targetPort |

Alle Docker-Builds verwenden das Repository als Buildkontext. React wird zuerst in API-`wwwroot` gebaut. Die sechs logischen Agentenrollen teilen **einen** internen Runtime-Host mit eigener Identitaet; keine sechs Agentenservices. `Cohors.Agents` bleibt Bibliothek. In-Process-Aufrufe sind nur ein ausdruecklicher lokaler Development-/Testmodus.

Functions werden als Codepakete per OneDeploy in zwei getrennte Flex-Apps geliefert. `scripts/Publish-Functions.ps1` prueft Trigger-Metadaten und erzeugt Pakete ohne Deployment. Die Dockerfiles in `src/Cohors.Workflows` und `src/Cohors.Operations` mit dem veroeffentlichten MCR-Tag `dotnet-isolated:4-dotnet-isolated10.0` dienen ausschliesslich lokaler Linux-Hostabnahme auf Port 80. Sie sind **kein** Flex-Auslieferungsformat.

## API-Einstellungen

Azure verwendet Double-Underscore-Umgebungsnamen. Beispielwerte sind keine bereitgestellten Ressourcen.

| Schluessel | Bedeutung |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` in Azure; LocalDemo niemals oeffentlich |
| `ASPNETCORE_HTTP_PORTS`, `COHORS_HOME` | `8080`, `/app` |
| `Authentication__Mode` | `Entra` |
| `Entra__TenantId`, `Entra__Audience` | Genehmigter Tenant und API-Audience |
| `Entra__SpaClientId`, `Entra__Scope` | SPA-App und delegierter `access_as_user`-Scope |
| `Entra__ProductGrants__<object-id>__0` | Servergepflegte Produktscopes; keine Browser-Rollenwahl |
| `Cohors__SqlAuthentication` | `ManagedIdentity` |
| `ConnectionStrings__Cohors` | Server/Datenbank, Encrypt=true, TrustServerCertificate=false; keine User-ID, kein Kennwort und kein Authentication-Parameter; TokenCallback ueber DefaultAzureCredential |
| `AZURE_CLIENT_ID` | UAMI-Client-ID |
| `Cohors__ObjectStore`, `Blob__ContainerUri` | `Azure`, private Blob-Container-URI |
| `Cohors__Search`, `Search__Endpoint`, `Search__Index` | `Azure`, private Search-URI und genehmigter Index |
| `Cohors__AgentTransport` | `Remote` in Azure; `Local` ausschliesslich Development/Testing |
| `Agents__Endpoint`, `Agents__Scope` | Feste interne HTTPS-Agent-Basis ohne Pfad/Query; Application-Scope `.../.default` |
| `Cohors__LocalOperations` | `false` in Azure |
| `Cohors__AutoMigrate`, `Cohors__Seed` | `false`; gesonderte genehmigte Initialisierung statt dauerhaftem DDL-Admin |
| `Cohors__PublicSources` | Standard `false`; ausdrueckliche Freigabe fuer feste FDA-/NCBI-Leseadapter |

Menschliche App-Rollen: `PmsSpecialist`, `MedicalSafetyReviewer`, `RegulatoryAffairs`, `QualityManager`, `Auditor`. Workflows verwenden die Application-Rolle `Workflow`, `idtyp=app`, keinen delegierten `scp`. Rollenname allein reicht nicht.

## Interne Agent Runtime

AgentHost nutzt `Entra__TenantId`, `Entra__Audience`, `Agents__AllowedCallerClientId` (API-UAMI) und explizite `Agents__Products__0` usw. Die API-Identitaet muss die Application-Rolle `Agent` tragen, `idtyp=app` liefern und darf keinen delegierten `scp` haben. Der Host besitzt keine SQL-/Blob-Schreib-, Freigabe- oder Meldeberechtigung.

Eingabe-JSON und Hash werden vor Aufruf unveraenderlich in SQL persistiert. Der Runtime-Host prueft Inputhash und Produktscope, die API Ergebnisbindung und Struktur. HTTP-/Modellclients wiederholen keine unklaren Aufrufe; verlorene Ergebnisse werden nicht durch Replay als Erfolg ersetzt.

Der Modellmodus ist **explizit pro Auftrag** in `AgentInput.Mode`: `Replay`, `Live` oder `Failure`. Es gibt keinen Live-Production-Default und keinen wirksamen Host-Key `Cohors__AgentMode`; ein gesetzter Endpoint aktiviert keinen Modellaufruf. Die UI fordert den Modus an, die persistierte Eingabe bindet ihn und `/health/ready` meldet `modelMode=explicit-per-request`. Live benoetigt weiterhin die unten genannten geprueften Konfigurations-/Deploymentnachweise. Ein lokaler oder Cloud-Host kann einen ausdruecklichen Replay-Auftrag ausfuehren, ohne diesen als Live auszugeben.

Lokale Remote-Tests koennen `Agents__LocalWorkloadKey` im API-Client und `Cohors__LocalWorkloadKey` im AgentHost setzen, ausschliesslich in Development/Testing bzw. Development gegen Loopback. Werte werden lokal generiert und nie veroeffentlicht.

## Striktes EU-Verarbeitungsgate

Nur der AgentHost benoetigt in Azure `Foundry__Endpoint` (HTTPS-Modellendpunkt `*.openai.azure.com`, keine Project-/Portal-URL), `Foundry__Deployment`, `Foundry__ModelVersion`, `Foundry__ProcessingBoundary=EU-Member-States` sowie:

- `Foundry__BoundaryReview__Reference`, `__ConfigurationHash`
- `Foundry__BoundaryReview__DeploymentSku`, `__DeploymentRegion`, `__DeploymentGeography`
- `Foundry__BoundaryReview__ProcessingCountries__0` usw. als ISO-Laendercodes.

DataZone-, Global- und Developer-SKUs werden fail-closed abgewiesen. `Standard`/`ProvisionedManaged` sind nur eine notwendige Vorbedingung: regional bedeutet Geography, nicht zwingend eine einzelne Region. Ein kontrollierter Review muss die tatsaechliche Providerkonfiguration und alle moeglichen Verarbeitungslaender pruefen. `FoundryBoundary.ConfigurationHash` bindet diesen Nachweis an Endpoint, Deployment, Modellversion, SKU, Region, Geografie, Laender und Reviewreferenz. Die Laufzeit prueft die Bindung, **attestiert aber nicht selbst Azure oder die rechtliche Freigabe**.

`dotnet Cohors.AgentHost.dll --boundary-hash` berechnet den Hash ohne Hoststart oder Netzaufruf aus diesen Umgebungswerten (`ConfigurationHash=pending` waehrend der Berechnung). Root-Felder in kanonischer Reihenfolge: `countries`, `deployment`, `deploymentGeography`, `deploymentRegion`, `deploymentSku`, `endpoint`, `modelVersion`, `processingBoundary`, `reference`. System.Text.Json/Web-Defaults und dessen Standard-Escaping, rekursive ordinale Schluesselsortierung, keine Einrueckung/Newline, UTF-8 ohne BOM, SHA-256 als lowercase Hex. Laender werden ordinal sortiert; sonst keine Trim-, Case-, URI-Normalisierung oder Deduplikation. Hashberechnung ist keine Freigabe und kein Providerbeweis.

Microsofts [Deploymenttypen](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/deployment-types) nennen EU Data Boundary einschliesslich moeglicher EFTA-Laender; die [Datenschutzseite](https://learn.microsoft.com/azure/foundry/responsible-ai/openai/data-privacy) nennt noch EU Member Nations. Dieser Widerspruch ist kein EU-only-Nachweis. Die strikte Pilotgrenze erlaubt nur EU-Mitgliedstaaten; damit wird EFTA-Verarbeitung nicht pauschal als DSGVO-widrig eingestuft. Die echte Azure-/Fachabnahme bleibt offen.

## Workflows und Operations

Beide: `AZURE_FUNCTIONS_ENVIRONMENT=Production`, `AzureWebJobsStorage__accountName`, `AzureWebJobsStorage__credential=managedidentity`, bei UAMI zusaetzlich `AzureWebJobsStorage__clientId` und `AZURE_CLIENT_ID`. Flex setzt .NET-isolated/Version ueber `functionAppConfig`; `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated` und `FUNCTIONS_EXTENSION_VERSION=~4` sind lokale Container-/Toolwerte, keine deprecated Flex-Appsettings. Host-Storage braucht passende Blob-/Queue-/Table-Datenrollen und private Endpunkte. Keine Account-Keys, kein neuer DTS-Dienst.

Workflows: `Cohors__ApiBaseUrl` als interne HTTPS-Basis ohne Pfad; `Cohors__ApiScope=api://<API-App-ID>/.default`; optional `AzureFunctionsJobHost__extensions__durableTask__hubName=CohorsPilotV1`. Der Durable-Orchestrator erhaelt ausschliesslich Ereignis-IDs; HTTP-/Modellarbeit liegt in Activities. Lokaler Workload-Key: `Cohors__LocalWorkloadKey`, nur Development/Loopback.

Operations: credentialfreier `ConnectionStrings__Cohors` und `Cohors__SqlAuthentication=ManagedIdentity`. SQL-Watchdog und persistierter Herzschlag sind unabhaengig von API, Foundry und Search.

Nach erfolgreichem SQL-Tick emittiert das SDK das ungesampelte CustomEvent `cohors.watchdog.completed`, `cloud_RoleName=cohors-operations`, ohne medizinische Inhalte oder Fall-/Personenbezeichner. SDK: `TelemetryConfiguration.SetAzureTokenCredential(DefaultAzureCredential)`. Functions-Host: `APPLICATIONINSIGHTS_AUTHENTICATION_STRING=ClientId=<UAMI>;Authorization=AAD`. `APPLICATIONINSIGHTS_CONNECTION_STRING` konfiguriert das Ziel, nicht ein Zugriffstoken. Ohne Ziel ist lokale SDK-Telemetrie deaktiviert. `host.json` deaktiviert Sampling; der reine CustomEvent-SDK-Pfad installiert keinen Sampling-Processor. Lokale Ausgabe beweist weder Azure-Ingestion noch Alarmzustellung.

## Linux-/Browser-CI

Voraussetzungen: SDK aus `global.json`, Node 22.12+ LTS, PowerShell 7, Docker Linux. Reihenfolge:

1. `npm --prefix src/Cohors.Web ci --no-fund`
2. `pwsh -File scripts/Initialize-Local.ps1`
3. `pwsh -File scripts/Build-Local.ps1` (Web vor .NET)
4. `pwsh -File scripts/Test-Local.ps1 -SkipBrowser`
5. `pwsh -File scripts/Start-Local.ps1` als eigener Hintergrundprozess; `/health/ready` abwarten.
6. Im Web-Verzeichnis `npx playwright install --with-deps chromium`; `COHORS_URL=http://localhost:5187`, `COHORS_BROWSER=chromium`; `npm --prefix src/Cohors.Web run test:e2e`.

Initialize erzeugt ausschliesslich lokale SQL-/Demo-Zugaenge in `.local/settings.json` und `.local/demo-access.txt`. API-Start migriert und seedet unter Development. SQL-Tests erstellen und entfernen eine eigene zufaellige `Cohors_Test_*`-Datenbank und akzeptieren nur exakte Loopback-Servernamen.

CI-Artefakte: gezielte `.local/test-results/*.trx`, `.local/e2e-results.xml` und benannte Screenshots. **Nie ganz `.local`, Login-Traces, Zugangsdaten oder Settings hochladen.** `Test-PublicSources.ps1` ist ein separater ausdruecklicher Lese-Smoke-Test, kein obligatorischer reproduzierbarer CI-Test.

Skripte uebergeben native PowerShell-Pfade; Vite nutzt `fileURLToPath(new URL(..., import.meta.url))`. Linux-Nachweise werden getrennt von Windows-Ergebnissen dokumentiert.
