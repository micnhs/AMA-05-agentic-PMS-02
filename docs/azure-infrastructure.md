# Cohors: Infrastruktur- und CI-Vorbereitung

**Status: Implementierungsvorbereitung, kein Azure-Deployment.** Dieses Paket erzeugt
hier weder Azure-Ressourcen noch Tenant-Konfiguration. Es ist keine klinische,
regulatorische, Produktions-, Kosten-, Quota- oder Cloud-Betriebsfreigabe.
Der synthetische Pilot behaelt getrennte menschliche medizinische und regulatorische
Freigaben. Behoerden, Kliniken und FSCA-Versand bleiben Simulatoren; echte oeffentliche
Quellen sind ausschliesslich lesend und im Standard-CI deaktiviert.

## Automatischer Primaerbootstrap: noch geschlossenes Implementierungspaket

Der neue Pfad `infra/scripts/primary_bootstrap.py` ergaenzt die unten beschriebene
historische, auf vorhandene Plattformverweise angewiesene `main.bicep`-Schnittstelle.
Er ist **noch kein fertig abgenommener Gesamtinstaller**. `primary-network.bicep`
und `primary.bicep` verdrahten eigene Gruppen, Netz, NVA, privaten Operator,
optionalen Bastion-/Windows-Client, Action Group und Datenplattform aus
journalisierten Ausgaben statt abzutippender Azure-Ressourcen-IDs.
Keine bestehende fremde Ressource wird uebernommen; Azure Firewall/VPN bleiben aus.
Der gewaehlte Initialactor bleibt der separat gebundene delegierte Benutzer.
`controlTokenBindings` bindet beobachtete ARM-/Graph-Audience und Issuer, nicht
aus `/.default` abgeleitete Werte. Private Personen-/Tenant-/Subscriptionbindungen
und echte Alarmempfaenger gehoeren nicht ins Repository.

Der aktuelle **Verpackungskandidat** ist
`dd112705af2baf6c4e7787794b782f4f8937b1e8` / lokaler Build49, mit dem
quellgebundenen Vertrag `cohors-operations-write-v1`/Schema3. Die Original-CLI
lautet `Cohors.Bootstrap sql --expect-server ... --expect-database ... --apply`
bzw. `search ... --apply`; ohne `--apply` erfolgen echte Remote-Reads.
Der historische dd112-Vertragsabsatz mit `sql-migrate` bezeichnet **keinen**
gueltigen CLI-Befehl. Die spaetere Dokumentationskorrektur1429877 ist kein
Runtime-Neubuild. Migrationen und SQL-Schreibprozedur bleiben ausschliesslich
in der originalen Anwendung, nicht als zweite Fach-DDL im Infra-Code.

Der lokale Build49-/Native98/99-Nachweis ist kein Entra-/Azure-/FSN-/UI-Pass.
Er liefert insbesondere noch keine Release-ZIPs oder Images. Der ausschliesslich
gehostete Workflow `cohors-primary-package.yml` baut die beiden originalen
Container-Dockerfiles und ruft das originale `scripts/Publish-Functions.ps1`
auf. Er bindet OCI-Digests, beide ZIPs, echte Trigger-Metadaten, eingebettete
Originalmigrationen und alle Paketdateien. Er meldet `cloudReleased=false`.
Die separaten privaten CLI-Ausgaben enthalten die tatsaechlichen SDK-Runtimes,
kein zweites Fachprogramm und keine erfundenen ACR-/ZIP-URLs.
Ein solcher Rebuild ist **nicht** das unveraenderte lokale Build49-Binaerpaket.

Die Remotequelle dd112 ist inzwischen abrufbar. Ihr natuerlicher
[CI-Lauf44355710367](https://github.com/micnhs/AMA-05-agentic-PMS-02/actions/runs/44355710367)
ist jedoch **negativ**: 395 .NET-Faelle, 385 bestanden, zehn fehlgeschlagen,
keiner uebersprungen. Recovery/Referenzabdeckung, FSN-Auditschnitt und die
integrierte T12-/Operations-Historybindung bleiben beim Primaerowner offen.
Das nimmt den gezielten lokalen Restricted-Principal-Nachweis nicht pauschal
zurueck, qualifiziert aber auch keinen integrierten Applicationrelease.
Der Infrabranch liest deshalb genau diesen vorhandenen Lauf, statt die gleiche
dd112-Suite erneut zu starten. Der Readback bleibt negativ; Paketbau und
Manifest-Hashes koennen ihn nicht ersetzen. `releaseBundle` bindet getrennt
`validationRunId` fuer Infra und `applicationValidationRunId` fuer die Anwendung.

Die eigene NVA ist eine **zusaetzliche kostenpflichtige Filterkomponente** mit
eigenem Betrieb: Linux-VM, Disk, Public IP, Updates, Ausfall- und Messverfahren.
Der gepinnte mitmproxy-Parser/TCP-Mechanismus leitet TLS verschluesselt weiter;
keine Zertifizierungsstelle, Inhaltsentschluesselung oder TLS-Abschwaechung.
Ungeprueftes/fehlendes SNI, ECH/eSNI, ALPN, DNS-/IPv6-/UDP-/QUIC-/Direkt-IP-Pfade
bleiben gesperrt. FQDN/SNI ist keine HTTP-Pfad-/Inhaltskontrolle und kein
umfassender Exfiltrationsschutz. Der Einzelnachweis in isolierten Linux-Namespaces
ist ausdruecklich **kein ACA-/Flex-/Operator-/Browser-Cloudnachweis**.

**Offene Profilentscheidung:** Der historische ACA-Kandidat ist Consumption,
nicht Dedicated. Laut
[profilbezogener Custom-DNS-Dokumentation](https://learn.microsoft.com/azure/container-apps/private-endpoints-with-dns#custom-dns)
braucht Consumption `AzurePlatformDNS` auch mit Custom DNS. Eine UDR erzwingt
diesen Pfad nicht; die Plattform-IP168.63.129.16 umgeht UDRs.
Der neue Strict-DNS-Bootstrap verweigert daher Network/Foundation mit Consumption,
statt still einen DNS-Bypass zu oeffnen. `containerAppsWorkloadProfileType=D4`
ist lediglich ein **expliziter Alternativkandidat** mit einem Dedicated-Knoten
(Minimum=Maximum=1); beide echten Container Apps bekommen dieselbe explizite
Profilzuordnung. Kein stiller Defaultwechsel, keine HA-/SLO-Zusage. D4-Region,
Kapazitaet, Mehrkosten und die obligatorische Aufloesung unter anderem von
`*.hcp.<region>.azmk8s.io`/`mcr.microsoft.com` sowie deren tatsaechliche Nutzpfade
muessen gebunden werden. DNS-Aufloesbarkeit allein ist keine HTTPS-Abnahme.

**Modellqualitaet vor Preis:** Der aktuelle Appvertrag hat genau ein gemeinsames
Foundry-Profil fuer alle sechs Rollen; aus Infra-Metadaten entsteht kein
Mehrmodellrouting. Modellbereitstellung zur Evaluation und fachliche
Live-Akzeptanz sind getrennt. `primary_model_gate.py` bindet echte Liveberichte,
Fachreview, aufgabenspezifische Kriterien und gemessene FP/FN-/unbelegte-Aussagen-/
Formatfehler an Appcommit, Prompt und tatsaechlichen Modellreadback.
Dieser Infrastruktur-Envelope ist kein bereits implementierter fachlicher
Evaluator; Replay, fehlende Werte und Nullfehler-/klinische-Validierungsversprechen
werden nicht als Qualitaetsnachweis akzeptiert. Billigere Nebenprofile brauchen
einen kuenftigen versionierten Appvertrag und eigenen Eignungsnachweis.

`primary_sql.py` und `Cohors.InfraSql` enthalten einen noch nicht gehostet
qualifizierten Principal-/Grantadapter: Originalmodulhashes, Schema3 und exakte
Rollenrechte werden gegengelesen; die Anwendungsmigrationen werden nicht kopiert.
Ein Metadatenreadback meldet ausdruecklich noch keinen Runtime-Entra-Login.
Noch offen sind dessen echter SQL-/Entra-Nachweis, native Plattform-/Ausfallpruefungen, vollstaendiger
Deadline-/Aktivierungs-/Cleanupabschluss, reale Personen-/Alarm-/Lizenzbindungen,
vollstaendige aktuelle Kosten und gehostete Sicherheits-/Paketnachweise.
Runtimephasen bleiben geschlossen; ein bloss passender Manifest-SHA oeffnet sie
nicht. Die nachfolgenden historischen Voraussetzungen sind kein Beleg, dass
diese neuen Aufgaben schon abgeschlossen waeren.

## Artefakte und Architektur

| Pfad | Zweck |
| --- | --- |
| `infra/main.bicep`, `infra/modules/*` | Private Grundlage, Identitaeten, optionale Modellbereitstellung und optionale Runtime |
| `infra/parameters/pilot.example.bicepparam` | Nur kompilierbares Beispiel mit bewusst ungueltigen Deployment-IDs |
| `infra/parameters/pilot.approval.example.json` | Bewusst abgelaufene, nicht freigegebene Reviewvorlage |
| `infra/monitoring/watchdog-missing.kql` | Fehlender erfolgreicher SQL-Watchdog-Tick, einschliesslich nie gestarteter Hosts |
| `infra/scripts/Validate-Infrastructure.ps1`, `infra/tests/*` | Ausschliesslich lokale Validierung und negative Gate-Tests |
| `.github/workflows/cohors-ci.yml` | Bicep/Sicherheitspruefung und echte SQL/.NET/Chromium-Akzeptanz |
| `.github/workflows/cohors-deploy.yml` | Nur manueller, standardmaessig gesperrter OIDC-Deploymentpfad |
| `.azure/infrastructure-plan.json`, `.azure/insights.json` | Nachvollziehbare Planung, Quellen und offene Gates; niemals Status `deployed` |

Der genehmigte Zuschnitt wird nicht geaendert: React und API laufen gemeinsam in
einer Container App, die sechs logischen Agenten gemeinsam in **einem separaten
internen AgentHost** mit eigener Identitaet. Workflows und Operations sind zwei
getrennte .NET-10-isolated **Flex Consumption** Function Apps mit getrennten Plaenen,
Subnets, Hostkonten und Codepaketen. Functions-Dockerfiles dienen allein der lokalen
Linux-Hostabnahme. Kein AKS, Cosmos DB, neuer Broker oder Durable Task Scheduler:
SQL-Outbox/Inbox bleibt fachliche Integration; Durable nutzt Azure Storage
einschliesslich Blob, Queue und Table.

SQL ist fuehrender Zustand, Blob Evidenzspeicher, Search eine rekonstruierbare
Push-Projektion. Es gibt weder Search-Indexer noch ausgehende AI-Enrichment-Pipelines.
Die Modellidentitaet erhaelt keine SQL-, Blob-Schreib-, Freigabe- oder Senderechte.
Operations spricht SQL direkt an und ist fachlich nicht von API, AgentHost, Foundry
oder Search abhaengig.

Quellenfortschreibung: Der Coordinator hat die Original-Anforderungsmatrix
gelesen und 118 eindeutige IDs (104 Muss/14 Soll) zugeordnet; Konzeptstand v0.3.
Das ist kein Erfuellungsnachweis. Fuer dieses Paket sind L-01
(objektbezogene Retention, kein pauschales Rohdaten-WORM), L-03
(Audit/Recovery/unabhaengige Digests) und L-05
(Need-to-know/Verschluesselung/Privacy/EU) massgeblich. Fachliche Einzelabnahmen
und Originaldateiuebernahme bleiben bei Cohors-Pilot.

## Bestaetigte Produktionsannahmen (nur Zielbild)

**Entscheidungsstand 2026-11-16:** Der Nutzer hat die vom koordinierenden Project
empfohlenen Produktionsziele ausdruecklich uebernommen. Quelle ist diese
Nutzerbestaetigung mit den vom Project dazu weitergegebenen Empfehlungswerten;
die Zahlen sind keine woertliche Nutzerauesserung. Dieser Abschnitt dokumentiert
Zielannahmen, **keine implementierte Produktionsarchitektur, Istnachweise,
gesetzlich vorgeschriebenen Minutenwerte oder zugesagte Produktions-SLA**.
Er ersetzt weder BIA (Business Impact Analysis) und End-to-end-DR-Uebungen noch
formale PRRC-/RA-, QM-, Datenschutz- oder Betriebsfreigaben.

| Bereich | Uebernommenes Produktionsziel |
| --- | --- |
| PMS-Kern | Intake, Fristen, Fall-/Evidenzzugriff, Review und Meldungspakete: SLO **99,95 %**, 24/7, rollierende 30 Tage. |
| Wiederherstellung des Kerns | RTO **<= 5 Minuten** bei Komponenten-/AZ-Ausfall; **<= 60 Minuten** bei vollstaendigem Regionalausfall fuer den funktionsfaehigen Kern. |
| Unabhaengiger Notbetrieb | Fristenregister, Aufnahme und manueller Meldeweg **<= 15 Minuten** verfuegbar; kein Ersatznachweis fuer vollstaendige Wiederherstellung. |
| Kritischer fachlicher Zustand | **Fachliches RPO 0** fuer bereits bestaetigte Eingaenge, Awareness-Zeitpunkte, Freigaben, Auditdaten, Evidenz und freigegebene Meldungen/Quittungen einschliesslich freigegebener PSURs. |
| Wiederholbare Zwischenstaende | **RPO <= 5 Minuten** ausschliesslich fuer wiederholbare, noch nicht freigegebene Arbeitszwischenstaende. |
| KI-Analyse und PSUR-Erzeugung | RTO **<= 4 Stunden**, SLO **99,9 %**; menschliche Bearbeitung und Erstmeldung duerfen nicht darauf warten. Freigegebene PSURs bleiben im kritischen RPO-0-Scope. |
| Fristenalarme | **99,9 % innerhalb <= 2 Minuten** ab gespeicherter Warn-/Faelligkeitsgrenze; jedes Ueberschreiten von **5 Minuten** als Stoerung behandeln. |
| Missing-Watchdog | Fehlenden Herzschlag **unabhaengig innerhalb <= 3 Minuten** erkennen/alarmieren; getrennt von fachlichen Fristenalarmen. |
| Nicht-KI-Bedienoperationen | **p95 < 2 Sekunden**, **p99 < 5 Sekunden** unter einem definierten Produktionsprofil einschliesslich Importspitzen. |
| Menschliche Alarmreaktion | Kritische Alarme **<= 15 Minuten** menschlich bestaetigen, mit Vertretung und 24x7-Eskalation; technische Zustellung ist keine menschliche Bestaetigung. |

99,95 % entsprechen rund **21,6 Minuten Ausfallbudget je 30 Tage**. Ein
60-minuetiger Ausfall verletzt dieses SLO auch dann, wenn das regionale RTO
eingehalten wird. RTO umfasst Erkennung, Entscheidung, Umschaltung, Identitaet
und den Daten-/Objekt-/Workflowabgleich bis zum funktionsfaehigen Dienst,
nicht nur SQL-Failover.

Asynchrone SQL-Geo-Replikation belegt **kein RPO 0**. Kritische Ereignisse und
Evidenz muessen vor dem endgueltigen Erfolgs-ACK regionenuebergreifend dauerhaft
gesichert und vollstaendig abgleichbar sein. Ein konsistenter, ausfallgepruefter
Bestaetigungs-/Abgleichvertrag ist noch zu entwerfen und nachzuweisen; parallele
ungesicherte Dualwrites sind keine Loesung oder Erfuellungsbehauptung. Die
bestehenden EU-Verarbeitungs-/Datenschutzgates gelten auch fuer diese Kopien.
Separate geschuetzte Backup-/Restore-Szenarien fuer logische Korruption und
Ransomware bleiben erforderlich. Regulatorische Uhren laufen unveraendert;
KI-Ausfaelle oder DR rechtfertigen keine Fristverlaengerung.

**Kapazitaet und unveraenderte Pilotgrenzen:** Das genehmigte Konzept nennt
250.000 Eingangssignale/Jahr und 90 PMS-Teammitglieder. Rohsignale sind nicht
automatisch bestaetigte Faelle; Bestandsvolumen, Spitzen und Gleichzeitigkeit
sind separat zu dimensionieren. Der bestehende Pilotlastvertrag mit
1.000 Rohinformationen, 10 Testnutzern und 1.800 Messwerten wird nicht ersetzt.
Die 118 Originalmatrix-IDs und deren Abnahmestatus bleiben unveraendert.
Insbesondere werden der lokale T12-Nachweis, das Pilot-Watchdog-Profil und
die Fuenf-Minuten-In-App-Eskalation nicht zu Produktions-SLO-Nachweisen
umetikettiert. End-to-end-Messungen unter Produktionslast und im Fehlerfall
bleiben offen; die Zielwerte aendern hier keine IaC, Runtime, Settings,
Aktivierungsdefaults oder bestehenden Sicherheits-/Cloudfreigaben.

## Voraussetzungen vor irgendeinem Azure-Lauf

Es wurden keine Subscription, vorhandenen Ressourcen, EU-Region/SKU-Kombinationen,
Modelle, Quotas, Preise oder vorhandenen Freigaben entdeckt/verifiziert.
`westeurope` und alle Groessen im Beispiel sind **Kandidaten, keine Empfehlung mit
Kapazitaetsnachweis**. Bicep-Typpruefung beweist keine regionale Verfuegbarkeit.

Vor einer spaeteren Freigabe sind mindestens folgende Nachweise erforderlich:

1. Dedizierte bereits vorhandene Pilot-Resource-Group, Subscription/Tenant, registrierte
   Provider, Azure Policy, Namensverfuegbarkeit und erlaubte Resource APIs.
2. Regionale Kombination fuer ACA-Workload-Profiles, FC1/.NET 10, SQL GP Gen5,
   Search, Storage, ACR Premium, Monitor/AMPLS und Foundry; tatsaechliche Flex-
   Runtime-Katalogversion (`10.0` ist der Templatekandidat), Quotas, Kapazitaet und Kosten.
3. Nicht ueberlappende IPAM-Zuteilung und ein **existierender gepruefter Egress-
   Routentabellenverweis**, erreichbare Firewall/NVA, Default-Route, Peering/VPN,
   DNS-Forwarding und privater Browser-/Operator-/Runnerzugriff. Bicep erstellt
   keinen erfundenen Hub, Resolver, VPN oder Firewall.
4. Entra-App-Registrierungen, Benutzer-/Produktzuordnung, getrennte Freigabepersonen,
   Workload-App-Rollen, SQL-Bootstrap und eine getrennte Release-/Migrationsidentitaet.
5. Kostenbudget/Alerts, Aufbewahrung, Restore, Ledger-Digest-Verifikation und
   unabhaengige Alarmzustellung mit benannten operativen Verantwortlichkeiten.

Die NSGs, UDR-Verknuepfungen und privaten Endpunkte sind kein Nachweis fuer eine
funktionierende Landing-Zone-Verbindung. Plattform-Egress muss unter anderem Entra,
Container-Images, Functions-Plattform und Monitor kontrolliert erlauben.
Die Routentabelle wird nur referenziert, nicht erzeugt oder veraendert.

### DNS und Netze

Eigene Subnets: ACA `/23`, Workflows `/26`, Operations `/26`, Endpunkte `/24`
innerhalb des Beispielnetzes `10.42.0.0/16`. Die ersten drei sind jeweils separat
an `Microsoft.App/environments` delegiert; die gleiche Delegation erlaubt keine
gemeinsame Nutzung zwischen ACA und Flex. Private Endpunkte stehen im
nicht delegierten Subnet; TLS 443 und SQL-Proxy 1433 sind privat erlaubt.
SQL-Redirect-Portbereiche werden nicht unnoetig geoeffnet.

Das Paket erstellt private Zonen fuer Blob/Queue/Table, SQL, Vault, ACR, Search,
OpenAI/Cognitive Services/Foundry, Functions und AMPLS; AMPLS nutzt dieselbe Blob-Zone.
Die interne ACA-Domain erhaelt private Apex-/Wildcard-Eintraege auf die interne
Environment-IP. API-Ingress `external=true` bedeutet hier **VNet-erreichbar im
internen, oeffentlich gesperrten Environment**, nicht Internetfreigabe. AgentHost-
Ingress bleibt `external=false` und nur innerhalb dieses Environments erreichbar.

**Bestehende autoritative DNS-Zonen oder AMPLS nicht duplizieren.** Dieses
Greenfield-Spoke-Beispiel darf dann erst nach Anpassung auf die vorhandenen,
ausdruecklich autorisierten Ressourcen verwendet werden. AMPLS ist mit Workspace
und App Insights verknuepft und erlaubt nur private Ingestion/Queries. Private DNS
allein transportiert keinen Verkehr aus dem Arbeitsplatz/VPN.

## Parametrisierung und Aktivierung

`deployRuntime=false`, `deployModel=false`, `enableWatchdogAlert=false` und
`enableDigestPublisherGrant=false` sind absichtliche Startwerte. Ein deaktivierter
Alarm schuetzt keinen Betrieb. Das spaetere Runtime-Gate verlangt Modellkonfiguration,
immutable Images, produktive Auth-Konfiguration und aktivierten fehlenden
Watchdog-Erfolgsalarm. **Foundation allein ist noch keine laufende Demo.**

Erst nach kontrollierter Pruefung das Beispiel als eigene `.bicepparam`-Datei
anpassen und lokal in `infra/parameters/pilot.reviewed.parameters.json` kompilieren.
Daneben `infra/parameters/pilot.approval.json` aus der Reviewvorlage anlegen.
Diese beiden **nicht mitgelieferten** Dateien sind verbindliche Workfloweingaben.
Keine Zugangsdaten, Tokens oder echten Falldaten eintragen.

Der Review bindet die exakten Parameterbytes per SHA-256, Tenant, Subscription,
Resource Group, Location, Referenz und Ablaufzeit (maximal 14 Tage). Die Gates
verlangen echte dokumentierte Evidenz statt eines allgemeinen `approved=true`.
`deployment_gate.py` prueft dies offline, unter anderem gegen Platzhalter,
ueberlappende Netze, falsche Scopes, offene Produktlisten und fehlende Images.
Diese Pruefung kann nicht feststellen, ob jemand einen falschen Nachweis eintraegt:
geschuetzte Environments und unabhaengige menschliche Review bleiben zwingend.

### Regionale Modellgrenze

Nur die `Standard`-Modell-SKU ist vorgesehen. Keine Global-, DataZone- oder
Developer-SKU, kein Spillover/Fallback und kein automatisches Modellupgrade.
`Standard` bedeutet **Geography**, nicht garantiert eine einzige Region.
Die erlaubten ARM-Locations in EU-Mitgliedstaaten ersetzen deshalb keinen
Nachweis der tatsaechlichen Modellverarbeitung.

Die aktuellen Microsoft-Quellen widersprechen sich: die
[Deployment-Typen](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/deployment-types)
beschreiben die EU Data Boundary einschliesslich EFTA; die
[Datenschutzseite](https://learn.microsoft.com/azure/foundry/responsible-ai/openai/data-privacy)
enthaelt weiterhin eine engere EU-Member-Nations-Formulierung. Fuer die bestehende
Anforderung **EU-Mitgliedstaaten-only** reicht `EUDataZone` daher nicht als Freigabe.
Dies ist keine pauschale Aussage, EFTA-Verarbeitung sei DSGVO-widrig.

AgentHost verlangt `Foundry__ProcessingBoundary=EU-Member-States` und den
`foundryBoundaryReview` mit `reference`, `configurationHash`, `deploymentSku`,
`deploymentRegion`, `deploymentGeography`, `processingCountries`.
Laender sind exakte EU-ISO-Codes. `configurationHash` muss nach Providerpruefung
mit dem **von der Anwendung bereitgestellten read-only `--boundary-hash`-
Einstieg** erzeugt werden; niemals mit ARM-Objektstringifizierung nachbilden.
Hashberechnung ist selbst keine Provider-/Rechtsfreigabe.

Gebunden sind Endpoint, Deployment, ModelVersion, ProcessingBoundary, Reference,
DeploymentSku, DeploymentRegion, DeploymentGeography und ordinal sortierte
ProcessingCountries. Die Anwendung sortiert Objektschluessel rekursiv ordinal,
verwendet kompaktes System.Text.Json-Web-JSON/default escaping, UTF-8 ohne BOM,
SHA-256 in lowercase. Keine Trim-/Case-/URI-Normalisierung oder Laender-Deduplikation.
Insbesondere den Endpoint einschliesslich abschliessendem `/` exakt aus den
Foundation-Outputs verwenden. Die Hashform wird offline geprueft; die exakte
Anwendungs-Kanonisierung wird im Runtime-Bootstrap gegengeprueft.

## Host-, Image-, Port- und Auth-Vertraege

| Host | Artefakt / Buildkontext | Health |
| --- | --- | --- |
| API + React | `src/Cohors.Api/Dockerfile`, Repo-Kontext; `cohors-api@sha256:...`; 8080 | `/health/live`, `/health/ready` |
| Sechs logische Agenten | `src/Cohors.AgentHost/Dockerfile`, Repo-Kontext; `cohors-agents@sha256:...`; 8080 | `/health/live`, `/health/ready` |
| Workflows | Release-`dotnet publish`-Codepaket, Flex **OneDeploy** | `/api/health/live`, `/api/health/ready` |
| Operations | Separates Release-`dotnet publish`-Codepaket, Flex **OneDeploy** | `/api/health/live`, `/api/health/ready` |

`apiImageDigest` und `agentsImageDigest` sind 64-stellige Hex-Digests **ohne**
`sha256:`. Bicep bildet die Referenzen ausschliesslich auf das eigene ACR; keine
fremde Registry oder mutable Tags. Functions-Containerport 80 ist **nur lokal**,
kein Flex-targetPort. Das bestaetigte lokale Basisimage
`mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated10.0`
ist keine Freigabe fuer containerbasiertes Cloud-Hosting.

API: `ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_HTTP_PORTS=8080`,
`COHORS_HOME=/app`, `Authentication__Mode=Entra`, Tenant/Audience/SpaClientId/Scope
und serverseitige `Entra__ProductGrants__<object-id>__N`.
Menschliche Rollen: PmsSpecialist, MedicalSafetyReviewer, RegulatoryAffairs,
QualityManager, Auditor. Leere Produktgrants erlauben keinen Produktzugriff.

API und Operations: `Cohors__SqlAuthentication=ManagedIdentity`;
`ConnectionStrings__Cohors` enthaelt nur Server, Datenbank, Encrypt=true und
TrustServerCertificate=false, **keine User-ID, kein Passwort, kein Authentication-
Feld**. TokenCallback/DefaultAzureCredential und `AZURE_CLIENT_ID` liefern den
UAMI-Token. Dies ist eine credentialfreie Adresskonfiguration, kein SQL-Geheimnis.

API nutzt `Cohors__ObjectStore=Azure`, `Blob__ContainerUri`,
`Cohors__Search=Azure`, `Search__Endpoint/Index`,
`Cohors__AgentTransport=Remote`, `Agents__Endpoint` und `Agents__Scope`.
Foundry-Konfiguration liegt **nur am AgentHost**. Dort sind Tenant/Audience,
`Agents__AllowedCallerClientId=<API-UAMI-Client-ID>` und die explizite
`Agents__Products__N`-Liste erforderlich.

Es gibt keinen `Cohors__AgentMode`-Umgebungswert und keinen Production-Default
Live. Jeder unveraenderliche Auftrag uebermittelt `AgentInput.Mode`
(`Replay`/`Live`/`Failure`) an `/internal/run`; Auftrag und Ergebnis dokumentieren
den Modus. `/health/ready` meldet `modelMode=explicit-per-request`. Ein Endpoint
allein aktiviert kein Live-Modell. Live erfordert die gebundene BoundaryReview
und darf bei Fehlern nicht still auf Replay zurueckfallen.

Workflows: interne HTTPS-`Cohors__ApiBaseUrl` **ohne `/api`**,
`Cohors__ApiScope=api://<API-App-ID>/.default`, Task Hub `CohorsPilotV1`.
Die Workflows-MI benoetigt an der API die Application-Rolle **Workflow**.
API-MI benoetigt am AgentHost die Application-Rolle **Agent**.
Tokens muessen `idtyp=app` besitzen und duerfen keinen delegierten `scp` haben.
Azure-RBAC-Rollenzuweisungen erzeugen diese Entra-App-Rollen **nicht**.

Flex: `functionAppConfig.runtime.name=dotnet-isolated`, Runtime-Katalogversion
`10.0` als zu pruefender Kandidat. **FUNCTIONS_WORKER_RUNTIME und
FUNCTIONS_EXTENSION_VERSION nicht setzen**: Flex ersetzt/verwaltet diese Werte.
Sie bleiben nur in den lokalen Containerhosts. Beide Apps bekommen
`AZURE_FUNCTIONS_ENVIRONMENT=Production`,
`AzureWebJobsStorage__accountName/__credential=managedidentity/__clientId`
und `AZURE_CLIENT_ID`. Keine Azure-Files-, SAS-, Key- oder
`WEBSITE_RUN_FROM_PACKAGE`-Ersatzkonfiguration.

Auch `ftpsState` wird fuer Flex nicht gesetzt: FTP/FTPS ist dort nicht unterstuetzt.
SCM-/FTP-Basic-Publishing-Credentials bleiben explizit deaktiviert.

Fuer alle Cloudhosts bleiben LocalOperations, AutoMigrate, Seed und PublicSources
aus. Keine automatische Runtime-Migration und keine LocalDemo-Authentisierung in
Production. Die eigentliche SQL-/Search-Schemaanlage ist ein gesonderter Bootstrap.

### Identitaeten und Rechte

| Identitaet | ARM-Datenrechte dieses Pakets | Separater Bootstrap |
| --- | --- | --- |
| API | AcrPull, Blob Data Contributor nur Container `evidence`, Search Index Data Contributor, Telemetrie-Publisher | SQL-Anwendungsrechte; Agent-App-Rolle |
| Agents | AcrPull, Cognitive Services OpenAI User, Telemetrie-Publisher | Keine SQL-/Blob-Rechte; API-Callerbindung |
| Workflows | Blob Data Owner sowie Queue/Table Data Contributor nur am eigenen Hostkonto; Contributor eigener Paketcontainer; Telemetrie | API-App-Rolle Workflow |
| Operations | Entsprechende Rechte nur am separaten Hostkonto/Paketcontainer; Telemetrie | Eigener Entra-Principal ausschliesslich in `cohors_operations_runtime`; SQL-direktes `IOperationsStore`/`dbo.CohorsOperationsWrite`, keine breite Tabellen-DML |
| Digest-Publisher | Standardmaessig keine Datenrechte; optional Contributor nur am Digestcontainer | Separater gepruefter Publisher, SQL GENERATE LEDGER DIGEST, kein medizinischer Runtimehost |
| Digest-Verifier | Blob Data Reader nur am Digestcontainer | Unabhaengige SQL-/Digest-Verifikation nach eigenem Betriebskonzept |
| Alerts | Log Analytics Reader nur am Pilotworkspace | Externe Action-Group-Zustellung und On-call-Prozess |

Kein Runtimehost bekommt Search Service Contributor, SQL-Administrator oder
standardmaessig Key-Vault-Secretrechte. App-Registrierungen, App-Role-Assignments,
SQL-contained users/grants und Migrationen fehlen nicht versehentlich: sie sind
bewusste Bootstrap-Gates. Fuer dd112/Schema3 liefert ausschliesslich die originale
Migration `003c_operations_permissions.sql` die dbo-eigene Operationsrolle:
SELECT auf Aggregates/AggregateVersions/SchemaVersions, EXECUTE nur auf
`dbo.CohorsOperationsWrite`, I/U/D-DENYs auf allen neun Anwendungstabellen,
UPDATE-DENYs je Spalte sowie DDL-/User-/Role-/Schema-/Ownership-DENYs.
Der Infraadapter ordnet den exakt gebundenen Client-ID-SID-Principal dieser Rolle
zu und erteilt CONNECT; keine zweite Prozedur-/Rollen-DDL, keine pauschalen
EXECUTE-, Owner- oder Securityrechte. Dbo-Ownershipchaining ist kein API-HTTP-Umweg.
API behaelt die quellgebundene Objektmatrix ohne History-/Audit-UPDATE/DELETE.
Bootstrap-Migrationsrechte sind temporaer und nicht Operations-Adminrechte.
Echte Entra-Anmeldung, effektive Rechte und negative Berechtigungsproben am
Ziel bleiben erforderlich; lokale SQL-Principals beweisen diese nicht.

Die vorhandene Blob-Schnittstelle bindet aktuell den Container `evidence`.
`quarantine` und `approved-packages` sind vorbereitete getrennte Container,
aber noch **keine nachgewiesene Runtime-Aufteilung**; sie erhalten keine pauschalen
zusatzlichen API-Rechte. Paket- und Digestkonten sind getrennt. Inkrementelle ARM-
Deployments entfernen alte Rollen bei spaeteren Rechteaenderungen nicht automatisch:
Widerruf und Access Review sind gesondert freizugeben.

## Monitoring, Watchdog, Aufbewahrung und Wiederherstellung

Log Analytics: 30 Tage Pilot-Telemetrie, keine taegliche harte Ingestion-Sperre,
die den Watchdog still verschwinden laesst. Kosten stattdessen ueber Budget,
gezielte Messwerte und Review kontrollieren. App Insights verweigert lokale
Key-Authentisierung. Runtime/Host verwenden Connection String als Zieladresse,
AAD-Token, `APPLICATIONINSIGHTS_AUTHENTICATION_STRING=ClientId=<UAMI>;Authorization=AAD`
und Monitoring Metrics Publisher.

Konfigurierte Auswahl: ACA-Systemlogs, Vault-/Registry-Audits, SQL-Auth-/Rechte-
/Ledger-Ereignisse und Dienstmetriken. Das ist **kein Nachweis inhaltsfreier
Metadaten**: Auch ausgewaehlte SQL-Auditgruppen koennen SQL-Anweisungen,
Identifikatoren und sensible Werte enthalten. Der vorgeschlagene SQL-Server-
Auditpfad nutzt die Diagnostikeinstellung auf **master**. Vor Freigabe muessen
Datenklassifikation, Zugang, Aufbewahrung und eine erforderliche bereinigende
Ingestion/Transformation nach L-05 konkret belegt sein
(`sqlAuditDataClassification`); dieses Paket behauptet keine implementierte
SQL-Feldredaktion. Entra-fehlgeschlagene Logins koennen schon vor SQL enden und
benoetigen daher die bestehende geschuetzte Entra-Protokollierung, nicht die
Behauptung, SQL-Auditing erfasse sie vollstaendig.

Ziel bleibt: keine pauschale Container-Konsole, Prompt-/Antwort-/Search-Query-
Texte, SQL-Batches, Fall-/Personennamen oder medizinischen Bodies in Betriebslogs.
SDK-/Host-Redaktion und die tatsaechlichen Diagnosepayloads sind vor Cloudfreigabe
zu testen; die Wahl einer Diagnostikkategorie allein ist keine Garantie.

### Kontrollierte SQL-Threat-Protection-Option

`deploySqlThreatProtection=false` laesst die Server-Policy **weg**. Es entsteht
keine `state=Disabled`-Ressource, die vorhandenen/ererbten Schutz abschalten
wuerde; aus diesem Default folgt keine Aussage zum Azure-Istzustand.
`sqlProtectionBaseline` verlangt vor jedem Deployment den Nachweis des
bestehenden Defender-/Policy-Zustands und eine formale Controlbewertung.

Nur ein spaeter gesondert freigegebenes `true` bereitet die Server-Policy
`Microsoft.Sql/servers/securityAlertPolicies@2023-08-01` mit `state=Enabled`,
allen Alerttypen und expliziten `sqlSecurityNotificationEmails` vor.
Kein Subscription-weites Pricing/Defender-Onboarding, kein automatischer
E-Mail-Versand an alle Admins und kein neuer keyed Blob-Export.
Die fuenf zusaetzlichen Gates `sqlThreatProtectionCost`,
`sqlThreatProtectionPrivacy`, `sqlThreatProtectionEu`,
`sqlThreatProtectionAccessRetention` und `sqlThreatProtectionSecOps`
verlangen jeweils Nachweise. Zugriff, tatsaechliche Retention und
SecOps-Verantwortung/Reaktion muessen definiert sein; diese Blueprint-Option
setzt keine dienstuebergreifende Aufbewahrungspolitik.

[Microsoft dokumentiert](https://learn.microsoft.com/azure/azure-sql/database/threat-detection-overview)
SQL-Anweisungen in Injection-Alertdetails. Deshalb ist die nicht aktivierte
Option ein **offener/formal zu bewertender Control**, kein bestandener
Schutznachweis und keine genehmigte Produktionsausnahme.

### Unabhaengiger Watchdog und Recovery

Operations-Trigger: **DeadlineWatchdog**, Timer alle 30 Sekunden; eigenes
Always-ready-Scaling-Group `function:DeadlineWatchdog`. Workflows besitzt die
getrennten Always-ready-Gruppen `durable` und `http`. Diese Floor-Kosten sowie das
FC1-Limit von 40 Instanzen **pro Scaling Group** sind gesondert zu pruefen; das ist
kein Gesamtinstanz- oder Rechnungslimit.

Erst nach erfolgreichem SQL-`OperationsService.Tick` emittiert der bestaetigte
Anwendungscode `cohors.watchdog.completed`, Cloud Role `cohors-operations`,
ohne fachliche Payload/IDs und ohne Sampling. Workspace-Tabelle: `AppEvents`,
Spalten `Name`, `AppRoleName`, `TimeGenerated`. Das freigegebene Pilotprofil
(Konzept v0.3, Abschnitt 9.4) verlangt drei ausgebliebene einminuetige Intervalle.
KQL zaehlt deshalb nur passende Erfolge im **inklusiven** Bereich
`ago(3m) <= TimeGenerated <= now()` und liefert `MissingSuccess` als Zahl (0/1).
Der letzte Erfolg darf genau drei Minuten alt sein; **mehr als drei Minuten**
ohne Erfolg ergeben 1, konsistent mit `OperationsService.HeartbeatMissing`.
Alte, fremde, undatierte und zukuenftig datierte Events loeschen den Alarmgrund
nicht. `summarize count()` ohne Gruppierung liefert auch ohne passenden Erfolg
eine Ergebniszeile: ein nie gestarteter Host darf nicht als gesund erscheinen.
Alert: Maximum > 0, Auswertung jede Minute (`PT1M`), eine Auswertung mit
`MissingSuccess=1` genuegt. Das **15-Minuten-ARM-Abfragefenster** (`PT15M`) ist nur der
Scanbereich, nicht die fachliche Missing-Success-Grenze. Weder ein
30-Sekunden-Produzent noch ein Timer-Invocation-Zaehler aendern dieses Kriterium.

**Initiale Ingestion ist ein Aktivierungsgate:** Microsoft unterstuetzt `union`
nicht fuer diese Log-Alertquery; Ein-Minuten-Regeln verlangen bereits ingesierte
Daten ([Querygrenzen](https://learn.microsoft.com/azure/azure-monitor/alerts/alerts-log-query#log-query-constraints),
[Ein-Minuten-Regeln](https://learn.microsoft.com/azure/azure-monitor/alerts/alerts-create-log-alert-rule#configure-alert-rule-conditions)).
Deshalb beginnt die Query direkt mit `AppEvents`, ohne vermeintlich sicheren
`union`-/Leerquellen-Fallback. Bei `enableWatchdogAlert=false` wird keine Regel
bereitgestellt. Der Watchdog-Output ist nullable und wird im Root mit Safe Access
gelesen, weil ARM den leeren Moduloutput auslassen kann; das ist keine Aussage
ueber bereits vorhandene Regeln. Vor erstmaliger Regelerstellung muss ein kontrollierter,
separat berechtigter privater Bootstrap die AAD-Ingestion eines synthetischen
**Nicht-Erfolgsereignisses** sowie Tabelle/Spalten und Queryzugriff nachweisen,
z.B. `cohors.monitor.bootstrap`, Rolle `cohors-monitor-bootstrap`.
Niemals einen `cohors.watchdog.completed`-Erfolg vortaeuschen. Anschliessend
Query gegen `AppEvents` kompilieren/ausfuehren, Ein-Minuten-Voraussetzungen
pruefen und im zusaetzlichen
`watchdogQueryReadiness`-Gate dokumentieren, erst dann die Regel bereitstellen.
Das Gate gilt auch ohne gleichzeitiges Runtime-Deployment. Foundation kann so
ohne ungueltige Startregel vorbereitet werden; eine laufende Runtime verlangt
weiterhin den aktivierten unabhaengigen Alert.
Fehlende Tabelle/Schema-/Queryfehler sind **keine** gesunde Nullserie und muessen
ueber Regelgesundheit und Betriebseskalation behandelt werden.

Cloud-Abnahme: erstmalige Ingestion, AAD/AMPLS, Querykompilierung, gesunder Tick,
nie gestarteter Host, SQL-Ausfall, API-/Foundry-/Search-Ausfall, Hoststopp,
Telemetryverlust, unsampled Signal und tatsaechliche Action-Group-Zustellung testen.
Grenzen mit letztem Erfolg unmittelbar vor, auf und nach `now()-3m` sowie alte
und fremde Events pruefen. Bis zur naechsten Ein-Minuten-Auswertung kann nach
Grenzueberschreitung weitere Taktwartezeit entstehen; Ingestion, Auswertung und
Zustellung separat messen. **Keine garantierte Cloudlatenz:** Ist das Pilotprofil
im Zielbetrieb nicht erreichbar, bleibt die Abnahme offen, statt die Grenze
still auf fuenf oder zehn Minuten zu erweitern. Die Vorgabe einer **In-App-
Eskalation spaetestens fuenf Minuten nach einer gespeicherten fachlichen Warn-/
Faelligkeitsgrenze** ist ein getrenntes Kriterium, keine Heartbeat-Ausfalltoleranz
und keine Verlaengerung rechtlicher Fristen. Ein gemeinsamer EU-Region-Ausfall
benoetigt ausserhalb dieser Fehlerdomaene getestete menschliche Eskalation.

`infra/tests/test_watchdog_query.py` prueft gesunden/nie gestarteten Host,
die Drei-Minuten-Grenze inklusive exakter Gleichheit, alte/fremde/undatierte/
zukuenftige Events und Wiederaufnahme. Die Offline-Vertragstests lesen die
produktiven KQL-Praedikate und werten ausschliesslich deren eingeschraenkte
Filter-/Count-/Projektionsform mit SQLite aus; andere Queryformen scheitern.
Das ist **kein Kusto-Engine-, Scheduler-, Ingestion- oder Zustellnachweis**.
Diese Zielbetriebspruefungen bleiben trotz bestandener Offline-Faelle offen.
Eine gesunde Queryausgabe 0 bedeutet zudem nicht sofort `Resolved`: Fuer
zustandsbehaftete Ein-Minuten-Regeln (`autoMitigate=true`) dokumentiert Microsoft
zehn gesunde Minuten vor automatischer Aufloesung. Diese separate
**Wiederherstellungsregel** ist keine zehnminuetige Ausfalltoleranz; auch diesen
Zustandswechsel im Cloud-Drill messen.

SQL: TDE, Entra-only, Proxy, PITR standardmaessig 14 Tage, Backupredundanz nur
Local/Zone. Keine Behauptung eines erfolgreichen Restores. Evidence-/Paket-/Digest-
Blobs: Versionierung und 14 Tage Blob-/Container-Soft-Delete als Pilot-Kandidaten,
**keine** Loesch-Lifecycle, produktive WORM-Dauer, Legal-Hold- oder Retention-Locks.
Durable-/Hostkonten erhalten keine WORM-/Lifecycle-/Versionierungsregeln.
Key Vault hat 90 Tage Soft-Delete und Purge Protection; deren irreversible Wirkung
ist vor einer realen Erstellung ausdruecklich in den Risikoreview aufzunehmen.

Ledger: ausgewaehlte Audit-Tabelle muss die Anwendungsmigration als append-only
Ledger anlegen; nicht die gesamte Datenbank ungefragt in Ledger verwandeln.
Eine Hashkette alleine ersetzt keine unabhaengige Ledger-Verifikation.
Digest-Publisher und -Verifier bleiben getrennt; kein laufender Publisher und
kein automatischer SQL-Digest-Upload wird erfunden. Automatischer SQL-Upload wuerde
die SQL-Server-MI, Storage-Rechte, geschuetztes Append und einen nachgewiesenen
privaten Pfad erfordern; dafuer niemals das Storage-Firewall-Gate umgehen.
Soft-Delete/Versionierung und Contributor-Rechte allein beweisen keine
Manipulationssicherheit gegen privilegierte Administratoren.

Restore-Runbook vor Aktivierung erproben: Wiederherstellung in eine **neue**
isolierte Datenbank, keine produktive Ueberschreibung; alte/neue DB-Inkarnation,
Ledger-Digests, Blobversionen und rechtliche Fristen erhalten; Verifier unabhaengig
ausfuehren; Search aus SQL/Blob rekonstruieren; Outbox-/Inbox-/Idempotenz- und
TransmissionUnknown-Zustaende ohne blinde Doppelsendung wiederaufnehmen.
Durable-History/Task-Hub nicht manuell loeschen oder als fuehrenden fachlichen Zustand
restaurieren. RPO/RTO und Kapazitaet sind erst nach gemessener Uebung belastbar.

## Lokale Validierung ohne Azure-Anmeldung

Benutzte Toolversionen sind im Paket fixiert: Bicep 0.49.2, Checkov 3.4.5,
Actionlint 1.7.12, Python 3.13, PowerShell 7. Azure CLI mit Bicep, Docker Linux
sowie Actionlint muessen verfuegbar sein. Checkov wird als offizielles, auf Digest
fixiertes Linux-Image ausgefuehrt, ohne Netzwerk, ohne Capabilities, mit
schreibgeschuetztem Dateisystem/Quellmount und eigenem temporaeren Verzeichnis.
Python-Abhaengigkeiten aus
`infra/requirements-validation.txt` in einer isolierten Umgebung installieren,
nicht in der globalen Python-Installation. Der Validator meldet fehlende Tools
als Fehler; er meldet sie nicht als bestandene Sicherheitspruefung.

```powershell
python -m pip install -r infra\requirements-validation.txt
docker pull bridgecrew/checkov@sha256:122602cf32cc72286155d39f1f234b7b594818f7b8d7fdf490701620e521368b
pwsh -NoProfile -File infra\scripts\Validate-Infrastructure.ps1 `
  -OutputDirectory (Join-Path $env:TEMP ("cohors-iac-validation-{0}" -f [Guid]::NewGuid().ToString('N')))
```

Der Validator baut/lintet Bicep, kompiliert Parameter, prueft lokale Sicherheits-
und CI-Vertraege und erzeugt Checkov-Ergebnisse lokal. Keine Azure-Anmeldung,
kein `az deployment`, kein `what-if`, kein Apply, kein SaaS-Ergebnisupload.
Kompilate und Scannerberichte werden nicht als Quellcode committed. Das Image
wird vorab heruntergeladen; der eigentliche Validator verwendet `--pull never`
und `--network none` und uebernimmt keine SaaS-Credentials aus der Umgebung.

Checkov 3.4.5 verwendet pycep-parser 0.5.1: Der direkte Bicep-Scan scheitert an
`string[]` und `toObject`; ein unveraenderter ARM-v2-Scan sah sogar null Ressourcen.
Beides wurde verworfen. `checkov_templates.py` exportiert deshalb **alle**
eingebetteten kompilierten Templates, auch bedingte/standardmaessig inaktive
Zweige, als Ressourcenlisten. Die zusaetzlichen quellengebundenen Projektionen
sind die SQL-Server-Auditzuordnung und beide Zweige des eng unterstuetzten
DHCP-Objektausdrucks. Storage-Namen, uebrige Eigenschaften,
Ressourcenbedingungen und Copy-Loops bleiben unveraendert.

**CKV_AZURE_182/183:** Checkov3.4.5 behandelte das kompilierte
`if(..., createObject('dnsServers', ...), createObject())` als String und
warf vier TypeErrors, obwohl `parsing_errors=0` war. Die Scanansichten enthalten
nun fuer jede betroffene Template-Scope beide Booleanzweige vollstaendig,
auch wenn ein Parameterdefault nur einen davon waehlt. Der DNS-Parameter bleibt
symbolisch; keine erfundene IP, kein Ersatz-DNS und kein impliziter Default.
Eine inverse Vollbaumpruefung bindet jede Auswahl an den Originalausdruck.
Ein einzelner DNS-Server bleibt ein echter CKV_AZURE_182-Befund; ein nicht
aufgeloester Adressvergleich bleibt unbewertet, nicht bestanden.
`checkov_observe.py` beobachtet die unveraenderten Python-Checkrueckgaben und
erneut geworfenen Exceptions. UNKNOWN, SKIPPED, Ausfuehrungsfehler und sonstige
Scannerdiagnostik schliessen den Gate auch bei null Parsingfehlern.
Dieser Beobachter ist keine eigene Implementierung der Checkov-Kontrollen.

**CKV_AZURE_23:** Eine eigenstaendige Server-`auditingSettings`-Ressource wird
zusaetzlich exakt unter dem nachweislich zugehoerigen SQL-Server sichtbar gemacht.
Originalressource und alle Policywerte bleiben erhalten, auch `Disabled`,
fehlender `state` und kurze Retention. Die Beziehung muss im selben Template
anhand identischer Namensausdruecke plus `/default` eindeutig sein. Mehrdeutige
Zuordnung, bestehende Ressourcen, abweichender Scope, Copy-Loops und nicht den
Server abdeckende Policybedingungen werden verweigert. **Keine** erfundene
Server-Policy-Vererbung in Datenbanken und keine weitere Policy-Korrektur.

**CKV_AZURE_43 bleibt unbehoben/offen:** Saemtliche Storage-Namenswerte und ihre
Typen bleiben unveraendert, auch unaufgeloeste `parameters`-, `format`-,
`uniqueString`- und Scope-Ausdruecke. Der Konverter wertet weder Parameter,
Variablen noch Defaults aus. Er benoetigt fuer den vollstaendigen Export keine
realen Subscription-/Resource-Group-Eingaben. Es gibt keine Ersatznamen,
symbolischen Musternamen, ARM-Runtime oder generellen Expression-Fallbacks.

Eine inverse Pruefung entfernt nur diese aufgezeichneten Projektionen und muss
danach den **gesamten** unveraenderten Ressourcenbaum als Listen rekonstruieren.
Sie verweigert verlorene Ressourcen oder sonstige Aenderungen, nicht nur einen
veraenderten Ressourcenzaehler. Das Manifest bindet die einmal gelesenen
Quellbytes, jede Originalressource, alle Scan-Dateien und die Projektionsherkunft
per SHA256. Eine bytegleiche `.original.json` liegt neben, nicht im Scanordner.
Fehlgeschlagene Aequivalenzpruefungen publizieren keine Teil-Scanartefakte.
Die erzeugten `SCAN-ONLY-*.json` sind keine Deploy-Artefakte; Deploy nutzt
ausschliesslich das unveraenderte Bicep-Kompilat. Aufgrund verschachtelter
Modulpruefung sind Checkov-Zaehler **Befundvorkommen**, nicht Azure-Instanzzahlen.
Funktionen, bei denen Checkov fehlgeschlagene ARM-Pruefungen still ausfiltern
koennte, und rohe Storage-Namenswerte mit den bekannten Checkov-`UNKNOWN`-Markern
werden weiterhin vor dem Scan abgewiesen. Unaufgeloeste Ausdruecke werden nicht
pauschal als still gefilterte Befunde behandelt; insbesondere bleibt der vorhandene
`compactName`-/`take(uniqueString(...), 8)`-Pfad vollstaendig im Scan.
Es gibt keine `UNKNOWN`-Freigabe. Fuer spaetere Vollscans immer ein neues
Ausgabeverzeichnis verwenden; bisherige Rohbefunde nicht ueberschreiben.

**Realer Offline-Konverternachweis (kein Neu-Build/Checkov-Lauf):** Das unveraenderte
historische `main.json` mit SHA256
`e05e7592f193f8ab29b559257dfec9c4de6f4ffc13b25bf9350731edb02d340d`
wurde vollstaendig exportiert: 36 Templates und alle 144 Originaldeklarationen
stimmen mit dem historischen Manifest ueberein, einschliesslich vier bedingter,
28 Copy- und zwei Existing-Deklarationen. Alle fuenf Storage-Deklarationen und
ihre zehn Vorkommen in den exportierten Dateien behalten Namen und Typen.
Eine belegte SQL-Server-Auditzuordnung erzeugt die erwartete zusaetzliche Kopie
in zwei Dateiansichten. Alle 36 Dateien wurden gegen den erwarteten Gesamtbaum,
ihre Hashes und die inverse Rekonstruktion geprueft; die Originalbytes sind
gebunden. Die 144 Manifestdeklarationen bzw. 325 urspruenglichen Vorkommen in den
verschachtelten Dateiansichten sind **nicht** der historische Checkov-Zaehler 246.
Zusaetzlich bestanden 12 fokussierte Python-Unittests; Konverter, Readback und
Unittests jeweils Exitcode 0.

Dies ist kein neuer Security-Pass. CKV_AZURE_43, die SQL-Datenbank-Befunde, die
bisherigen Rohbefunde und die uebrigen 15 Controls bleiben offen/unveraendert.
Mit `$compiledTemplate` als bereits vorhandenem Kompilat und
`$freshScanDirectory` als neuem eigenen Artefaktverzeichnis laesst sich der
reine Offline-Weg ohne Build oder Ressourcenstart wiederholen:
```powershell
python -m unittest discover -s infra\tests -p test_checkov_templates.py -v
python infra\scripts\checkov_templates.py $compiledTemplate $freshScanDirectory
```

Es gibt keine `skip-check`-Liste, Suppressions oder automatische Freigabe anhand
der folgenden Einordnung. Nullscans, Parserfehler, Skips und verbleibende Befunde
lassen den Sicherheitsgate fehlschlagen. Ohne heruntergeladene SaaS-Metadaten
liegen keine belastbaren Severity-Einstufungen vor; deshalb wird insbesondere
nicht behauptet, es gaebe keine offenen High/Critical-Befunde.

**Lokaler Nachweis dieses Infra-Auftrags:** Ergebnisse stehen im Abschnitt
Validierungsnachweis.
SQL-/Browser-/Container-/Functions-Abnahme kann in diesem Quellstand ohne
Anwendungsquellen nicht behauptet werden.

### Einzelklassifikation der 17 beobachteten Controls

Diese Klassifikation bezieht sich auf die bisherigen Rohbefunde. Der oben
begrenzte Scan-only-Pfad ist keine Freigabe und keine Produktionsausnahme.
Echte offene Controls benoetigen einen
Nachweis oder einen gesonderten formalen Risikoentscheid; Werkzeugfehler
benoetigen einen kontrolliert korrigierten Pruefpfad statt unsicherer Azure-Werte.

| Checkov-ID | Einordnung und konkreter Restpunkt |
| --- | --- |
| CKV_AZURE_166 | **Offene Lieferkette.** ACR-Quarantaene ist Preview und hier nicht aktiviert. Image-Digests allein belegen keinen Scan, SBOM, verifizierte Promotion oder laufenden Quarantaene-Controller. Diese Release-Nachweise bleiben erforderlich. |
| CKV_AZURE_17 | **Nicht verwendetes Auth-Verfahren.** Die generische Web-App-Regel fordert Clientzertifikate. Flex hat private TLS-Health-Endpunkte; Workload-Rollen werden an API/AgentHost geprueft. Kein mTLS-Verfahren wird erfunden; der tatsaechliche Zugriff muss abgenommen werden. |
| CKV_AZURE_206 | **Bewusste geografische/DR-Grenze.** Die konkrete Regel akzeptiert nur GRS/RAGRS/GZRS/RAGZRS, nicht LRS/ZRS. Ungepruefte Geo-Kopien werden nicht fuer ein gruenes Ergebnis eingeschaltet. EU-Ziele, RPO/RTO und Redundanz bleiben Deployment-/Produktionsgates. |
| CKV_AZURE_208 | **Genehmigte Pilotgrenze.** Ein Search-Replikat erfuellt kein SLA fuer Indexupdates; SQL bleibt fuehrend, Search rekonstruierbar. Drei Replikate und ihre Kosten/Verfuegbarkeit sind nicht als vorhanden bestaetigt. |
| CKV_AZURE_209 | **Genehmigte Pilotgrenze.** Ein Search-Replikat belegt kein Query-SLA. Zusaetzliche Replikate erfordern einen eigenen Kapazitaets-/Kosten-/SLO-Nachweis. |
| CKV_AZURE_212 | **Werkzeug/Flex-Zuordnung.** Legacy-App-Service-Instanzfelder ersetzen nicht Flex-Scaling-Gruppen und `functionAppConfig.scaleAndConcurrency.alwaysReady`. Die vorhandenen Floors sind dennoch kein Ausfall-/Failover-Nachweis. |
| CKV_AZURE_213 | **Nachgewiesene Flex-Fehlzuordnung.** App-Service-Health-Check ist laut Learn fuer Flex nicht verfuegbar. Eigene HTTP-Health-Routen und der unabhaengige Missing-Success-Alert werden separat abgenommen. |
| CKV_AZURE_225 | **Genehmigte Pilotgrenze.** FC1-Zonenredundanz ist nicht bestaetigt/aktiviert. Region, Mindestzahl Always-ready-Instanzen, Kosten und Failover sind offene Zielbetriebsgates. |
| CKV_AZURE_229 | **Genehmigte Pilotgrenze.** SQL-Zonenredundanz bleibt ein explizit zu pruefender Parameter, standardmaessig false. Kein HA-SLA wird aus privatem SQL oder PITR abgeleitet. |
| CKV_AZURE_23 | **Quellengebundene Server-Zuordnung im Scan-only-Konverter; Vollscan/Betriebsnachweis offen.** Die Regel sucht eingebettete `resources`; die exakt zugeordnete eigenstaendige `servers/auditingSettings` wird dort zusaetzlich sichtbar. Datenbank-Befunde werden nicht durch unterstellte Vererbung entfernt. Vererbung, master-Sink, Payloadklassifikation und tatsaechliche Ingestion bleiben unbewiesen. |
| CKV_AZURE_24 | **Retention-Ziel/Pruefmodell und formale Bewertung offen.** Die Regel sucht eine verschachtelte SQL-Audit-Blob-Retention >=90 Tage; hier ist Azure Monitor mit 30 Tagen Pilot-Telemetrie vorgesehen. Das ist weder eine 90-Tage-CIS-Freigabe noch die fachliche L-01-Aufbewahrung. Die geeignete Security-/Audit-Retention ist gesondert festzulegen. |
| CKV_AZURE_25 | **Offener Schutzcontrol.** Die optionale Threat-Detection-Policy wird standardmaessig nicht bereitgestellt. Ein vorhandener/ererbter Schutz ist unbekannt, nicht ausgeschaltet. Istzustand und formale Bewertung/Aktivierung bleiben Pflicht. |
| CKV_AZURE_26 | **Offener SecOps-Nachweis.** Gezielte Empfaenger und die spezialisierten Aktivierungsgates sind vorbereitet; keine reale Zustellung, Reaktion oder Empfaengerfreigabe ist erfolgt. |
| CKV_AZURE_27 | **Explizite Need-to-know-Vorgabe.** Kein automatischer Versand an alle Account-Admins; kontrollierte SecOps-Empfaenger statt Aufweitung nur fuer den Scanner. Dies belegt noch keinen funktionierenden Alarmprozess. |
| CKV_AZURE_36 | **Absichtlich engere Netzgrenze.** Storage verwendet `bypass=None`, private Endpunkte und MI statt pauschalem Trusted-Services-Bypass. Benotigte Pfade muessen positiv getestet werden; die Firewall wird nicht fuer die Regel geoeffnet. |
| CKV_AZURE_43 | **Unbehoben; Namensausdruecke bleiben unveraendert im vollstaendigen Scan.** Keine Storage-Namensaufloesung, Ersatzwerte oder Regel-Ausnahmen. Die bekannten unaufgeloesten Root-/Scope-Ausdruecke blockieren nicht mehr den Konverter; sie sind weiterhin kein Nachweis gueltiger oder global verfuegbarer Azure-Namen. |
| CKV_AZURE_78 | **Nachgewiesene Flex-Fehlzuordnung.** `ftpsState` ist in Flex nicht unterstuetzt. SCM-/FTP-Basic-Publishing ist explizit false; ein nicht unterstuetztes Legacy-Feld wird nicht hinzugefuegt. |

Quellen: [Flex-Monitoring](https://learn.microsoft.com/azure/azure-functions/configure-monitoring#monitor-function-apps-using-health-check),
[Flex-Einstellungen](https://learn.microsoft.com/azure/azure-functions/functions-app-settings#flex-consumption-plan-deprecations),
[Flex-Zonenredundanz](https://learn.microsoft.com/azure/reliability/reliability-functions?pivots=flex-consumption-plan),
[SQL-Auditing und sensible Statementtexte](https://learn.microsoft.com/azure/azure-sql/database/auditing-overview),
[SQL Threat Protection](https://learn.microsoft.com/azure/azure-sql/database/threat-detection-overview),
[ACR-Quarantaene-Preview](https://learn.microsoft.com/azure/container-registry/container-registry-faq#how-do-i-enable-automatic-image-quarantine-for-a-registry).
Die beschriebenen Regelbedingungen wurden in den lokalen Checkov-3.4.5-Klassen
`StorageAccountsUseReplication`, `SQLServerAuditingEnabled`,
`SQLServerAuditingRetention90Days` und `ACREnableImageQuarantine` gelesen.
Der begrenzte Folgepatch ist zudem an die unveraenderten Checkov-3.4.5-Quellen
[SQLServerAuditingEnabled](https://github.com/bridgecrewio/checkov/blob/3.4.5/checkov/arm/checks/resource/SQLServerAuditingEnabled.py)
und [StorageAccountName](https://github.com/bridgecrewio/checkov/blob/3.4.5/checkov/arm/checks/resource/StorageAccountName.py)
gebunden; er ersetzt oder unterdrueckt keine dieser Regeln.
SQL-Elternnamen werden nur strukturell anhand ihrer Syntax verglichen, nicht
ausgewertet; es werden keine Parameter-, Default- oder Runtimewerte eingesetzt.

## CI und die noch ausstehenden Integrationspruefungen

CI: Ubuntu 24.04, Node 24 LTS, SDK aus
`global.json`, PowerShell 7, Docker Linux. Actions sind auf volle Commit-SHAs
fixiert. Keine Azure-Credentials und kein OIDC-Recht im normalen CI.
Auf dem isolierten Infrabranch ersetzt ausschliesslich ein lesender,
versuchsgebundener Verweis auf den vorhandenen Primaer-App-CI den erneuten
App-Testjob. Fehlgeschlagene, fehlende oder uebersprungene Originaltests bleiben
negativ. Andere Appbranches behalten den echten SQL-/Browser-Harness.
Infra- und Readbackartefakte haben eigene explizite Upload-Allowlists; sie
duerfen nicht als die nachfolgend beschriebenen Fachtestartefakte gelten.

Der Harness verlangt einen frischen Checkout ohne `.local/settings.json`, fuehrt
`npm ci`, `Initialize-Local.ps1`, `Build-Local.ps1`, `Test-Local.ps1 -SkipBrowser`
aus und setzt **COHORS_TEST_CONNECTION explizit auf den frisch erzeugten Loopback-
SQL-Server**. Keine Mock-/InMemory-Substitution und kein stilles Ueberspringen.
PublicSources wird in der frisch erzeugten CI-Konfiguration und in der Umgebung
auf false gesetzt; bestehende Entwicklerkonfiguration wird nie dafuer gelesen.
Der vorhandene SQL-Fixture legt eine isolierte Testdatenbank an und entfernt sie.
Danach eigener `Start-Local.ps1`-Prozess, SQL-Readiness abwarten, Chromium samt
Abhaengigkeiten installieren, `COHORS_URL=http://localhost:5187`,
`COHORS_BROWSER=chromium`, Browsertests ausfuehren. Vite baut zuerst nach
`Cohors.Api/wwwroot`; keine angenommene Playwright-webServer-Automatik.

TRX und JUnit muessen ausgefuehrte, bestandene Tests ohne Skips nachweisen.
Frische SQL-/Demo-Zugaenge werden maskiert; Berichte mit erzeugten Credentials
blockieren die Veroeffentlichung. Upload nur `acceptance.trx`,
`e2e-results.xml`, `cohors-cockpit.png`, `cohors-workbench.png`, sieben Tage.
Keine `.local`-Ordner, Settings, Login-Traces, Browser-StorageStates oder Hostlogs.
Vorhandene Berichte/Screenshots und ein unvollstaendiges Passwortinventar werden
abgewiesen. Die SQL-Passwortentnahme verwendet ausdruecklich den CLR-Setter
`DbConnectionStringBuilder.set_ConnectionString(...)`: PowerShell behandelt
`.ConnectionString = ...` hier als IDictionary-Eintrag statt als Setter.
`TryGetValue('Password', ...)`, Stringtyp, Nicht-Leere und eine einzelne
steuerzeichenfreie Zeile sind zwingend. Ungueltige Eingabe oder Parserfehler
brechen mit einer inhaltsfreien Fehlermeldung ab, bevor das Passwort ins Inventar
gelangt; `credentialsCollected` bleibt false und keine Artefakte werden freigegeben.
Gueltige quotierte Passwoerter werden unveraendert maskiert, nicht getrimmt.

`infra/tests/test_ci_credentials.py` startet echte isolierte PowerShell-Prozesse
mit ausschliesslich synthetischen Werten. Per AST werden nur die produktive
Extraktionsfunktion und ihr tatsaechlicher Inventar-Add-Aufruf geladen und
ausgefuehrt, niemals der komplette Harness. Der GitHub-Actions-/Linux-Guard
bleibt unveraendert; keine Actions-Umgebung wird vorgetaeuscht, keine Settings
gelesen, keine SQL-/Docker-/App-Prozesse gestartet. Die alte Propertyzuweisung
wird ausschliesslich im Speicher mutiert und muss am selben Extraktionspfad
scheitern. Diese Regression ist kein erfolgreicher Linux-Gesamt-CI-Lauf.

Der SQL-/Browser-Job laeuft unabhaengig vom Infra-Job; ein roter
Sicherheitsgate darf diese Integrationspruefungen nicht still ueberspringen.
Cleanup betrifft nur den eigenen Prozessbaum, den vorher nicht existierenden
benannten SQL-Container/Volume und konkrete frisch erzeugte Credential-/Logdateien.
Reale oeffentliche Quellen bleiben ein separater freiwilliger Read-only-Smoke-Test.

Nach Uebernahme in die Anwendungssession zwingend:

1. Vollstaendiges `cohors-ci.yml` auf dem zusammengefuehrten Commit: Linuxpfade,
   SDK/Lockfiles, echte SQL-Transaktionen und sämtliche Browserfaelle; keine
   fehlenden oder uebersprungenen Suites. Der Infra-Quellstand kann dies nicht ausfuehren.
2. API- und AgentHost-Images aus Repo-Kontext bauen; Non-root/Dateirechte, React-
   Ausgabe, Port 8080, Health und getrennte Identitaets-/Auth-Konfiguration pruefen.
   Image-Scan, SBOM/Provenienz und SHA256-Zuordnung zum freigegebenen Commit vor
   privater ACR-Veroeffentlichung. Kein Runtime-Image-Push in diesem Infra-Workflow.
3. Beide Functions mit der verfuegbaren .NET-10-Worker/Worker.Sdk/Durable-Kombination
   separat `dotnet publish -c Release` ausfuehren; `host.json`,
   `worker.config.json`, `functions.metadata`, `extensions.json` im Paketroot
   sowie Extension-DLLs unter `.azurefunctions` pruefen. `DeadlineWatchdog` sowie alle HTTP-/Durable-
   Trigger muessen entdeckt werden. Der Anwendungsowner hat Linux-Publish mit
   SDK 10.0.404, Worker 2.52.0/Worker.Sdk 2.1.0 und Durable-Extension 1.20.1
   sowie die Metadaten/DLLs fuer DispatchOutbox, CohorsOutboxV1, ProcessOutbox,
   WorkflowLive/Ready, DeadlineWatchdog und OperationsLive/Ready gemeldet.
   Nach Uebernahme erneut pruefen; das ist kein in diesem Infra-Checkout
   ausgefuehrter Functions-Host-Test. `target=package` der Functions-Dockerfiles
   exportiert nur Codepakete; deren abschliessender Image-Stage bleibt lokal.
4. Lokale Functions-Container nur als Host-/Trigger-Smoke-Test verwenden. Cloud-
   Codepakete mit **Flex OneDeploy/package deployment**, nicht dem klassischen
   ZIP-Deploy-Protokoll ausrollen. Kein Flex-Slot/Rollback per erfundenem Staging-Slot.
5. Den exakten AgentHost-Boundary-Hash-Einstieg mit den IaC-Outputs und den Review-
   Feldern abgleichen; falschen Hash, andere Region/Version/Referenz, EFTA,
   DataZone/Global/Developer und fehlende Produktliste negativ testen.
6. Erst spaeter auf geprueftem privatem Runner: DNS/TLS/Private Endpoint/AAD fuer
   alle Datenpfade, SQL-contained users, negative Blob-/SQL-/API-/Agent-Rechte,
   Entra workload-only und getrennte menschliche Freigaben, Search-Schema/Rebuild,
   MI-TokenCallback, Durable B/Q/T, unabhaengigen SQL-Watchdog und Alarmzustellung,
   Backup-/Ledger-/Unknown-Reconcile-Drills abnehmen.

### Isolierter lokaler T12-Hostlauf

`infra/scripts/local_runtime.py` ist ein **separater lokaler Harness**, kein
Aufruf oder Ersatz fuer `Invoke-CohorsCi.ps1`. Er verweigert Actions-Kontext und
baut ausschliesslich einen frischen Git-Export von
`245b3b049228c3d4e01883bec56babca2012b91b` mit Parent
`a5c75454553e1325246546578a8ab3ef624ab119`. Der laufende Demo-Arbeitsbaum, dessen
`.local`-Dateien und die eigenstaendige T20-Recovery-Pruefung werden nicht
verwendet. Der Export wird nicht um Anwendungscode, Debug-Endpunkte oder
veraenderte Zeitgeber ergaenzt.

Voraussetzungen: Python 3.13, Git, Docker Linux, mindestens 8 GiB beobachteter
freier Docker-Speicher und 15 GiB freier lokaler Plattenplatz. Die Beobachtung
ist keine exklusive Reservierung; bei Ressourcenmangel abbrechen, nicht fremde
Container stoppen. Benoetigte lokale Images sind
`mcr.microsoft.com/mssql/server:2022-latest`,
`mcr.microsoft.com/azure-storage/azurite:3.37.0`,
`node:22-bookworm-slim` und die im Quellstand gepinnten Host-Buildimages.
Fehlende Images nur ueber den normalen Docker-Pull-/Buildweg bereitstellen;
Virenschutz, Docker-, Firewall- und sonstige Schutzkonfiguration bleiben
unveraendert. Die vier App-Images werden aus dem angegebenen Commit gebaut;
ihre tatsaechlichen Image-IDs und die Quellidentitaet werden im Ergebnis gebunden.
Ein lokaler Build ist kein freigegebener Supply-Chain-Nachweis.

Beispielaufruf mit **noch nicht existierenden**, getrennten Verzeichnissen
ausserhalb des Repositorys:

```powershell
py -3.13 infra\scripts\local_runtime.py `
  --workspace C:\Temp\cohors-t12-new-run `
  --artifacts C:\Temp\cohors-t12-new-evidence
```

Keine Hostports werden veroeffentlicht. SQL besitzt einen eigenen
`network=none`-Namespace; API, AgentHost, beide Functions-Hosts, Azurite und der
Testtreiber teilen nur dessen Loopback. Dadurch bleiben die vorhandenen
Development-/Loopback-Authguards unveraendert und es existiert keine externe
Route fuer Live-Foundry oder oeffentliche Quellen. Die Prozesse und Container
sind getrennt; dies ist **kein** Azure-VNet-, MI-, TLS- oder Identitaetsabnahmenachweis.
Die lokalen Functions-Hosts bekommen `WEBSITE_HOSTNAME=127.0.0.1:8082` bzw.
`:8083` passend zu ihren tatsaechlichen Loopback-Listenern. Ohne diese lokale
Webhook-Basis konnte der originale Workflows-Container den Durable-Client
nicht initialisieren; weder Azure-Hostname noch Actions-Umgebung werden
vorgegeben.
SQL und beide Azurite-Konten erhalten neue Zufallszugaenge und ausschliesslich
eigene Volumes. Lokale SQL-Admin-/Migrationsrechte dienen nur diesem Bootstrap,
nicht als vorgeschlagene Azure-Runtimeberechtigung. Die Functions-Dockerfiles
bleiben lokale Testhelfer; Azure verwendet weiterhin zwei Flex-Codepakete.
Alle Testcontainer behalten `no-new-privileges` und `cap-drop=ALL`; lediglich
der eigene SQL-Container benoetigt `NET_BIND_SERVICE`, weil das unveraenderte
Microsoft-Binary genau diese File-Capability traegt (per `getcap` geprueft).
Die kurzlebige Initialisierung des eigenen API-Volumes erhaelt nur `CHOWN`.
Keine dieser eng begrenzten Containeroptionen veraendert Hostschutz,
Docker-Standardkonfiguration oder Systemrichtlinien; insbesondere kein
`privileged`, `SYS_ADMIN`, Seccomp-Abschalten oder Virenschutz-Workaround.

Der in Lauf 07 konkret gemessene Readiness-Fehler war ein lokaler
Versionskonflikt: API-Ready/Outbox lieferten HTTP 200, aber Azurite 3.35.0
wies Storage-REST `2026-02-06` mit HTTP 400 / `InvalidHeaderValue` ab.
Azurite 3.37.0 unterstuetzt diese Version fuer Blob, Queue und Table;
der Harness prueft dies im unveraenderten Image. **Kein**
`--skipApiVersionCheck`, Header-Umschreiben oder App-/Functions-Patch.
Die [Azurite-Dokumentation](https://learn.microsoft.com/azure/storage/common/storage-use-azurite)
weist weiterhin auf funktionale Unterschiede und Table-Preview hin.

Timer-Start/-Ende-Metadaten werden nur fuer
`Function.DispatchOutbox` und `Function.DeadlineWatchdog` mit `Information`
aktiviert; die jeweiligen `.User`-Kategorien bleiben `Warning`.
`Host.Executor` war dafuer nicht die richtige Kategorie
([Functions-Loggingvertrag](https://learn.microsoft.com/azure/azure-functions/configure-monitoring#configure-categories)).
Der Harness liest beide lokalen Console-Streams, paart echte
`Reason='Timer fired ...'`-Starts und Erfolge anhand derselben Invocation-ID
und verwirft reine Erfolgszeilen, fremde IDs, manuelle Aufrufe und Duplikate
als Ersatznachweis. Das Gate laeuft schon vor der langen Unterbrechung und
erneut danach. Nur Zaehler werden ausgegeben, keine Rohlogs oder Payloads.

Der Node-Treiber `infra/scripts/local-runtime-driver.mjs` fuehrt eine echte
lokale PMS-Anmeldung und eine synthetische Replay-Anforderung aus.
`DispatchOutbox`, der Durable-Orchestrator und `ProcessOutbox` muessen danach
selbst laufen. Eine begrenzte Loopback-Transportbarriere leitet Requests und
echte Antworten unveraendert weiter, haelt aber die ausgewahlte erfolgreiche
ProcessOutbox-Antwort **nach** fachlichem SQL-Commit und **vor** Durable-Ack
zurueck. Erst mit persistierter `Running`-Instanz und echter Activity-History
darf der ausschliesslich eigene Workflows-Container hart beendet werden.
Dies ist ein verlorener Zustellungsabschluss, kein simulierter
Agenten-/Timererfolg. Default-Storage-Leases/Visibility werden nicht durch
SQL-/Azurite-Manipulation oder einen virtuellen Zeitsprung abgekuerzt.

Die Abnahme verlangt Wiederanlauf derselben gespeicherten Durable-Ausfuehrung,
nachgewiesene Activity-Wiederzustellung, genau eine Inbox-Quittung/einen
Assessmententwurf sowie unveraenderte abgeschlossene Replay-Modellauftraege.
Operations muss waehrend des Workflows-Ausfalls und separat bei gestoppter
API/AgentHost weiter ticken. Der Vergleich beginnt erst nach dem vollstaendigen
Prozessstopp; der nachfolgende Tick muss zeitlich nach den gemessenen
`FinishedAt`-Werten liegen. Ein Operations-Stopp wird mit echter UTC-Zeit bis
zur Drei-Minuten-Grenze beobachtet: kein Heartbeat vor dem ersten Timerstart,
fehlender Herzschlag erst bei `>3m`, frischer Herzschlag nach eigenem Neustart.
Direkte `/internal/watchdog`-Aufrufe, direkte Activity-Aufrufe aus dem Treiber
und gefaelschte erfolgreiche Antworten sind kein Bestandteil dieses Tests.

Das allowlist-orientierte Ergebnis `t12-runtime-results.json` enthaelt nur
Quell-/Image-/Hostidentitaeten, Timerzaehler, selektierte Durable-Metadaten,
fachliche Zaehler/Hashes, Zeitmessungen und Cleanup-Ergebnis. Es enthaelt keine
Zugangsdaten, Quellpayloads, klinischen Texte, Cookies oder rohen Hostlogs.
Fehler/Timeouts ergeben `blocked` und einen Fehlerexit, niemals einen Ersatzpass.
Cleanup verifiziert Runlabel und exakte eigene Container-IDs bzw. Volumen- und
Image-Tagnamen. Fremde Ressourcen, globale Caches und Basisimages werden nicht
entfernt.

**Lokaler T12-Lauf 09 vom 2026-11-15 bestanden.**
Run-ID: `465d12070d94451f9e748ab5c51f7133`. Die unveraenderte
Quellidentitaet ist oben angegeben; `t12-runtime-results.json` bindet zusaetzlich
Tree, Archivhash, ausgefuehrte Harness-Hashes und die vier Image-/Host-IDs.

| Kontrollziel | Gefilterter realer Befund |
|---|---|
| Quellen/Hosts | Vier originale Images, vier getrennte laufende Hostprozesse, alle Ready-Routen erfolgreich. Keine App-Datei geaendert. |
| Reale Kette | `DispatchOutbox` -> `CohorsOutboxV1` -> `ProcessOutbox` -> AgentHost; genau drei Replay-Aufrufe (`Ingestion`, `CausalityRisk`, `EvidenceAssurance`). |
| Persistierte Unterbrechung | Dieselbe Durable-Execution vor Workflows-KILL `Running`, nach Wiederanlauf `Completed`; 300.468 Sekunden von Erstellung bis Abschluss, keine abgekuerzten Storage-Leases. |
| Idempotenz | Activity-Zustellungen 1 -> 2; unveraendert je eine Inbox-Quittung, ein Assessment, ein Entwurf, ein AgentStarted und ein SQL-Attempt. Keine erneuten AgentHost-Aufrufe; Input-/Output-Hashes erhalten. |
| Unabhaengige Operations | Heartbeat-Version 3 -> 5 bei gestoppten Workflows und 13 -> 14 nach vollstaendigem API-/AgentHost-Stopp. |
| Eigener Operations-Neustart | Vor erstem Start `heartbeat=null`/`missing=true`; nach eigenem Stopp echte Alterung, danach neuer Hostprozess und Heartbeat-Version 14 -> 15. |
| Drei-Minuten-Grenze | Letzter eindeutig gesunder Messwert bis 176.633s; erster Missing-Request umfasst 179.634-180.211s und damit die Grenze; naechster eindeutig fehlender Messwert ab 183.211s. Keine Zeitmanipulation oder behauptete Null-Latenz. |
| Timer-Metadaten | Vor Unterbrechung echte Start-/Erfolgspaare beider Timer; insgesamt DispatchOutbox 35 Starts/34 Erfolge/1 Fehler, DeadlineWatchdog 15/15/0. |
| Cleanup | Acht eigene Container, drei Volumes, vier Image-Tags, private Konfiguration und Testexport bereinigt; separate Nachkontrolle fuer die exakte Run-ID: jeweils null Ressourcen. |

Der einzelne Dispatch-Fehler bleibt im Fault-Injection-Lauf mit absichtlich
gestoppter API ausgewiesen; dies ist kein Nachweis fehlerfreien Dauerbetriebs.
Lauf 06/07 bleiben historische Readiness-Fehllauefe, Lauf 08 bleibt wegen des
damaligen Timer-Log-Gates `blocked`, obwohl seine Wiederanlaufteilnachweise
vorliegen. Sie werden nicht rueckwirkend als bestanden umetikettiert.
Die Folgeaenderung wurde ausserdem mit 38 Offline-Tests (ohne Fehler/Skips)
und Node-Syntaxpruefung validiert.

Gefilterte Ergebnisdateien: `t12-runtime-results.json` und
`t12-terminal-verification.json`; der separate Offline-/Commitnachweis bindet
die geprueften Dateihashes an den integrierbaren Folgecommit.
Azure/Flex, Entra/Managed Identity, private Cloud-Netzpfade, Kusto-Ingestion,
Benachrichtigungszustellung, Actions und T20 werden nicht abgenommen. Die
Cloud-Dreiminutenmessung und der separate Checkov-Gate bleiben unveraendert offen.

## Bewusst manueller Deploymentworkflow

Nur `workflow_dispatch`; Standardoperation `what-if`. Repositoryvariable
`COHORS_DEPLOY_ENABLED` fehlt standardmaessig: der Workflow **scheitert sichtbar**,
anstatt einen uebersprungenen Job als Freigabe auszugeben.
Manuelle Bestaetigung `REVIEWED-SYNTHETIC-PILOT`, exakte Source-SHA, erfolgreiche
Cohors-CI-Push-Run-ID fuer diesen Commit und Review-ID sind zwingend.
Die Source-SHA muss in der geschuetzten Default-Branch-Historie liegen.

Zwei **extern einzurichtende** geschuetzte Environments:
`cohors-pilot-preview` und `cohors-pilot-deploy`, unabhaengige erforderliche Reviewer,
keine Selbstfreigabe/Admin-Umgehung, nur geschuetzte Default Branch.
Die Apply-Freigabe erfolgt erst nach dem sichtbaren What-if-Ergebnis.
Beide erhalten passende nicht geheime Tenant-/Subscription-/Client-/RG-Variablen
und `COHORS_PRIVATE_RUNNER_READY=true` erst nach echter privater Runnerabnahme.
Die Ephemeral-Runnergruppe `cohors-private-deployment` darf keine untrusted
PR-Jobs ausfuehren und braucht die lokal fixierten Validierungstools.

GitHub stellt pro Environment ein OIDC-Token mit Audience
`api://AzureADTokenExchange` bereit; direkte Azure-CLI-Federation, keine langlebigen
Secrets oder Publish Profiles. Exakte Issuer/Subject-Claims und Repository-/Environment-
Bindung beim Erstellen der Federated Identity pruefen, keine Wildcards.
Preview-Identitaet nur fuer no-write ProviderNoRbac-What-if und notwendige
Lese-/What-if-Aktionen, Apply-Identitaet nur fuer die dedizierte RG und genau
erforderliche Ressourcentypen. RBAC-Schreibrechte zusaetzlich auf die vorgesehenen
Rollen/Workload-Principals beschraenken; kein Subscription-Owner als Abkuerzung.

Jede Phase prueft Parameter, Approval, Bicep und Account-Kontext. Apply erzeugt
erneut What-if und muss denselben Change-Digest liefern. Delete, unbekannte/
nicht aufgeloeste Changes und Ressourcen ausserhalb der RG blockieren.
Es wird ausschliesslich **Incremental** angewendet; keine Resource-Group-Anlage,
Destroy-, Purge-, WORM-Lock-, Migration-, Container-Push- oder Package-Release-Aktion.
Die uebrigen Cloud-Daten-/Runtime-Releases bleiben gesonderte genehmigte Schritte.
Ein erfolgreicher ARM-Lauf wird niemals als funktionierende Pilotabnahme bezeichnet.

## Primaerquellen und Versionsentscheidungen

Pruefstand: 2026-11-15. Gepinnte stabile APIs werden verwendet, nicht ungeprueft
die jeweils neueste Preview. Azure-Typen ersetzen keine Live-Provider-/SKU-Pruefung.

| Bereich | API / Quelle |
| --- | --- |
| Netz/PE, DNS | `2024-07-01`, `2020-06-01`; [Private Endpoint DNS](https://learn.microsoft.com/azure/private-link/private-endpoint-dns) |
| Storage | `2025-01-01`; [ARM](https://learn.microsoft.com/azure/templates/microsoft.storage/2025-01-01/storageaccounts) |
| SQL | `2023-08-01`; [Ledger-Digests](https://learn.microsoft.com/sql/relational-databases/security/ledger/ledger-digest-management) |
| ACR Premium / Vault | `2025-04-01`, `2024-11-01`; [ACR Private Link](https://learn.microsoft.com/azure/container-registry/container-registry-private-endpoints) |
| Search / Foundry | `2025-05-01`, `2025-06-01`; [Search-RBAC](https://learn.microsoft.com/azure/search/search-security-rbac), [Deploymenttypen](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/deployment-types) |
| ACA | `2025-07-01` (PNA-Feld); [Environment-ARM](https://learn.microsoft.com/azure/templates/microsoft.app/2025-07-01/managedenvironments) |
| Flex | `2024-11-01`; [Bicep-MI-Beispiel](https://learn.microsoft.com/azure/azure-functions/functions-create-first-function-bicep), [Flex](https://learn.microsoft.com/azure/azure-functions/flex-consumption-plan), [ersetzte Settings](https://learn.microsoft.com/azure/azure-functions/functions-app-settings#flex-consumption-plan-deprecations) |
| Functions / Durable | [.NET isolated](https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-process-guide), [MI B/Q/T](https://learn.microsoft.com/azure/durable-task/durable-functions/durable-functions-configure-managed-identity), [Deploymenttechnologien](https://learn.microsoft.com/azure/azure-functions/functions-deployment-technologies) |
| Monitor | Workspace `2025-02-01`, App Insights `2020-02-02`, AMPLS `2021-09-01`, Query Rules `2023-12-01`; [AMPLS](https://learn.microsoft.com/azure/azure-monitor/fundamentals/private-link-design), [Log-Alerts](https://learn.microsoft.com/azure/azure-monitor/alerts/alerts-log-query) |
| Identitaeten / Rollen | UAMI `2024-11-30`, RBAC `2022-04-01`; [Rollen](https://learn.microsoft.com/azure/role-based-access-control/built-in-roles) |
| OIDC | [GitHub-Claims](https://docs.github.com/en/actions/reference/security/oidc), [Azure CLI login](https://learn.microsoft.com/cli/azure/reference-index#az-login) |

## Validierungsnachweis

IaC-Vollvalidator zum Watchdog-Folgecommit, tatsaechlich lokal ausgefuehrt am
2026-11-15, ohne Azure-Anmeldung:

```text
Validate-Infrastructure.ps1 -OutputDirectory <session-files>\validation-watchdog-three-minutes-final
Bicep CLI version 0.49.2 (b34271ac51); Checkov 3.4.5; Actionlint 1.7.12
Bicep build: exit 0.
Bicep lint: exit 0.
Bicep parameter example: exit 0 (not deployable approval).
PowerShell syntax and Actionlint: exit 0.
Ran 25 tests ... OK
Exported all 36 compiled templates.
Checkov compiled ARM:
{"passed":184,"failed":79,"skipped":0,"parsing_errors":0,
 "resource_count":246,"checkov_version":"3.4.5"}
Checkov exit 1.
Security gate is NOT passed.
Validator exit 1.
```

Die 25 Tests pruefen Offline-Freigabegates, Scanner-Export/-Abdeckung,
TRX/JUnit-Null-/Skip-/Fehlerfaelle, Workflow-/Artefaktgrenzen sowie den
Watchdog-Queryvertrag und sein separates Aktivierungsgate; es sind
**keine hier ausgefuehrten .NET-/SQL-/Browser-Anwendungstests**.
Vier gezielte Mutationen reproduzierten die Regressionen: alte Zehn-Minuten-
Toleranz, exklusive Drei-Minuten-Untergrenze, fehlende Zukunftsgrenze und fehlender
Operations-Rollenfilter. Die korrigierte Query besteht die jeweiligen
Offline-Faelle; dies ersetzt keine echte Kusto-/Ein-Minuten-Alertabnahme.
Kompilat, Abdeckungsmanifest und vollstaendiger Checkov-JSON-Bericht bleiben
lokale Session-Artefakte; es wurde nichts an einen Scanner-SaaS uebermittelt.

Ergaenzender Credential-Regressionsnachweis am 2026-11-15, **PowerShell 7.6.10**:
Mit ausschliesslich synthetischem Connection-String erzeugte die alte
Propertyzuweisung einen Key und `TryGetValue('Password')=false`; der CLR-Setter
erzeugte vier Keys, fand das Passwort und lieferte exakt den synthetischen Wert.
Danach wurden die produktive Extraktionsfunktion samt Inventaraufruf, drei
gueltige Varianten, elf ungueltige Werte und die alte In-Memory-Mutation geprueft:

```text
PowerShell 7.6.10: harness syntax passed.
py -3.13 -m unittest discover -s infra\tests -p test_ci_credentials.py -v
Ran 3 tests ... OK
py -3.13 -m unittest discover -s infra\tests -p "test_*.py" -v
Ran 28 tests ... OK
```

Die alte Mutation scheitert vor dem Inventar-Add; fehlende, leere, fehlerhaft
quotierte und steuerzeichenhaltige Werte hinterlassen kein Passwort im Inventar
oder in der ausgegebenen Fehlermeldung. Dies ist kein ausgefuehrter
GitHub-Actions-/Linux-/SQL-/Browser-Gesamtlauf. Der Actions-Guard wurde weder
geaendert noch umgangen. IaC, Scannerkonfiguration und Workflows sind bei diesem
Credential-Folgefix unveraendert; Bicep/Checkov wurden dafuer nicht erneut
ausgefuehrt und der obige offene Sicherheitsbefund bleibt bestehen.

**Infra-Planner-Phase-6-Selbstpruefung:** Dateien liegen in den exklusiv
freigegebenen Pfaden; Anwendungsdateien, Originaldokumente und Referenzplaene
wurden nicht geaendert. Private Zugriffe, MI, credentialfreie Cloudvertraege,
abgeschaltete Aktivierungsdefaults und explizite Freigabegates sind vorbereitet.
Build/Lint/Offline-Vertragstests sind bestanden; der Sicherheitsgate ist
ausdruecklich **nicht bestanden**, nicht severity-freigegeben und nicht durch
pauschale Ausnahmen bereinigt. Insbesondere SQL-Datenklassifikation, wirksamer
Threat-Protection-/SecOps-Schutz und die Image-Lieferkette bleiben offen.

**Phase 7 bleibt gesperrt.** Es gab keinen Azure-Login, What-if, Deploy/Apply,
Image-Push, Flex-Release, Restore oder WORM-Lock. Der Commit ist eine
reviewbare, sicherheitsseitig noch gesperrte Vorbereitung, keine Cloud- oder
Produktionsfreigabe. Der Infra-CI-Job bleibt daher rot; der getrennte SQL-/
Browser-Job kann nach Uebernahme trotzdem seine echten Nachweise ausfuehren.
