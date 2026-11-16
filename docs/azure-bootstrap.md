# Expliziter SQL-/Search-Bootstrap

`src\Cohors.Bootstrap` ist ein .NET-10-Konsolenprogramm, kein API-Host und kein
Provisionierungswerkzeug. Es arbeitet nur gegen **bereits bereitgestellte** Azure
SQL-/Search-Ziele. SQL und Search sind getrennte Befehle; es gibt keinen impliziten
Gesamtlauf. Ohne `--apply` erfolgen ausschliesslich echte lesende Pruefungen.
Ein fehlendes Schema ist dabei ein Fehler, kein erfolgreicher Dry-run.

## Voraussetzungen und Rechte

Vor einem Aufruf muessen Ziel, EU-Verarbeitung, Netzwerk/Private DNS, TLS,
Identitaet und Aenderungsfenster freigegeben sein. Die Parameterpruefung ist kein
Azure-/Tenant-/Abonnement-/EU-Nachweis. Erlaubt sind die regulaeren
`*.database.windows.net`-/`*.search.windows.net`-Namen mit privater DNS-Aufloesung,
nicht IP-Adressen, lokale Emulatoren oder abweichende Clouds.

Eine **gesonderte Bootstrapidentitaet** muss bereits existieren. Das Programm
verwendet den bestehenden `SqlStateStore.Configure(..., "ManagedIdentity", false)`
und fuer Search `DefaultAzureCredential`; es erteilt keine Rollen oder SQL-Grants.
Die Credential-Kette ist kein Identitaetsnachweis: den ausgewaehlten Principal im
freigegebenen Ausfuehrungskontext separat binden und pruefen. Keine Runtimeidentitaet
fuer diesen privilegierten Schritt aufwerten, keine SQL-/Search-Schluessel verwenden.

| Ziel | Vorab separat zu autorisierende Berechtigungen |
| --- | --- |
| SQL, lesend | Datenbankzugang, `VIEW DEFINITION` auf der Zieldatenbank fuer vollstaendige Metadatensicht, `SELECT` auf `dbo.SchemaVersions`, soweit vorhanden. |
| SQL, Migration | Zusaetzlich die Rechte der eingebetteten Migrationen: `CREATE TABLE`, `ALTER` im Schema `dbo` (einschliesslich Tabellen/Indizes), `ENABLE LEDGER`, `SELECT` auf Aggregates/Outbox/SchemaVersions, `UPDATE` auf Outbox, `INSERT` auf SchemaVersions. Rechte auf neu entstehenden Objekten muessen ebenfalls abgedeckt sein. DBA muss die konkrete zeitlich begrenzte Rechtekombination pruefen; kein pauschales `db_owner`-Grant. |
| Search | Lesender Schemazugriff bzw. fuer Anlage `Search Service Contributor` am genehmigten Suchdienst oder eine enger zugeschnittene, gepruefte Rolle fuer Indexdefinitionen. Diese eingebaute Rolle ist breiter als nur Indexanlage und kann auch Admin-Keys abrufen; deshalb separat und zeitlich begrenzt. Keine Dokumentlese-/Schreibrolle fuer diesen Bootstrap erforderlich. |

API/Operations erhalten dadurch **keine DDL-/Search-Service-Administratorrechte**.
Die separate Runtime-Rechtematrix ist weiterhin zu pruefen; insbesondere schreibt
`OperationsService.Tick` Cases, PsurReviews und Heartbeat ueber generisches Execute.
Dieser Bootstrap ersetzt weder diese Analyse noch die spaetere least-privilege
Abnahme mit den wirklichen Runtimeidentitaeten.

## Integration, Build und Quelltests

Der Code ist an den Vertrag des App-Commits
`0dcdb7f18bf9e7e95b7efa137a42b0ce21bde6d9` gebunden. Der historische Infra-Branch
enthaelt die referenzierten App-Projekte nicht; diese werden nicht nochmals kopiert.
Im vollstaendigen App-Checkout beide neuen Projekte in `Cohors.slnx` aufnehmen.
Nur im koordinierten Buildfenster ausfuehren:

```powershell
dotnet sln Cohors.slnx add src\Cohors.Bootstrap\Cohors.Bootstrap.csproj tests\Cohors.Bootstrap.Tests\Cohors.Bootstrap.Tests.csproj
dotnet restore Cohors.slnx
dotnet test tests\Cohors.Bootstrap.Tests\Cohors.Bootstrap.Tests.csproj --no-restore -c Release
dotnet publish src\Cohors.Bootstrap\Cohors.Bootstrap.csproj --no-restore -c Release -o artifacts\bootstrap
```

Die Produktionsabhaengigkeiten kommen ueber `Cohors.Infrastructure` unveraendert
aus dem App-Stand: SqlClient 7.1.2, Azure.Identity 1.21.0, Azure.Search.Documents
12.0.0. Test-SDK/xUnit/Runner verwenden vorhandene Versionspins. Neue Lockdateien
erst beim autorisierten Restore erzeugen und pruefen, nicht manuell erfinden.
Die isolierten Quelltests verwenden synthetische Konfiguration und Fakes,
keine SQL-/Azure-Ressourcen, keine API und keine Seed-/Reset-Routinen.

## Aufruf

Die Anwendungskonfiguration kommt nur aus Prozess-Umgebungsvariablen: keine
`.local\settings.json`, kein `demo-access.txt`, keine JSON-Appkonfiguration und
keine Secret-Argumente. Die separate Credential-Kette bleibt wie oben beschrieben.
Die Platzhalter unten durch **separat freigegebene** Werte ersetzen.
`--expect-*` ist die ausdrueckliche Zielbestaetigung, nicht automatisch aus einer
ungeprueften Verbindungszeichenfolge abzuleiten.

```powershell
$env:ConnectionStrings__Cohors = 'Server=tcp:<sql-server>.database.windows.net,1433;Database=<datenbank>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30'
$env:Cohors__SqlAuthentication = 'ManagedIdentity'
$env:Search__Endpoint = 'https://<search-service>.search.windows.net'
$env:Search__Index = '<indexname>'

dotnet artifacts\bootstrap\Cohors.Bootstrap.dll --help

# Echte lesende Pruefungen; fehlende Schemas liefern einen Fehlercode.
dotnet artifacts\bootstrap\Cohors.Bootstrap.dll sql --expect-server '<sql-server>.database.windows.net' --expect-database '<datenbank>'
dotnet artifacts\bootstrap\Cohors.Bootstrap.dll search --expect-endpoint 'https://<search-service>.search.windows.net' --expect-index '<indexname>'

# Ausschliesslich nach eigener Freigabe fuer das jeweilige Ziel:
dotnet artifacts\bootstrap\Cohors.Bootstrap.dll sql --expect-server '<sql-server>.database.windows.net' --expect-database '<datenbank>' --apply
dotnet artifacts\bootstrap\Cohors.Bootstrap.dll search --expect-endpoint 'https://<search-service>.search.windows.net' --expect-index '<indexname>' --apply
```

Jeden Exitcode einzeln auswerten: `0` bedeutet abgeschlossene Versions-/Schemapruefung
(oder nur Hilfetext bei `--help`), `2` verweigerter Auftrag/fehlendes oder
inkompatibles Schema, `1` Konfigurations-/Authentifizierungs-/Dienst-/Migrationsfehler,
`130` Abbruch. Fehlermeldungen enthalten eigene Fehlercodes bzw. SQL-Fehlernummern
oder HTTP-Status, **keine** Verbindungszeichenfolgen, Tokens, SQL-/Response-Payloads.
Keine unredigierten SDK-Listener/Hostlogs aktivieren oder als Artefakte hochladen.

## Nichtdestruktiver Vertrag und Grenzen

SQL prueft zuerst Zielkonfiguration, TLS/credentialfreie Authentifizierung,
`DB_NAME()` und die Versionsfolge. Erst bei explizitem `--apply` und leerer
unversionierter Datenbank bzw. erkannter aelterer Versionsfolge ruft es
`SqlStateStore.Initialize` auf. Dieser fuehrt die geordneten eingebetteten
Migrationen aus; es gibt **keine zweite DDL-Kopie**. Danach muss `RequireReady`
erfolgreich sein. Bereits aktuelle Versionen werden nicht erneut migriert.
Unversionierte nichtleere, lueckenhafte oder neuere Staende werden verweigert.
Die Readiness ist der vorhandene Versionsvertrag, kein vollstaendiger
Tabellen-/Constraint-/Ledger-Integritaetsnachweis.

Migration 002 aktualisiert vorhandene Legacy-Outbox-Zeilen zur eindeutigen
Aggregat-/Produktbindung; uneindeutige Altfaelle bleiben ein echter Fehler.
Das ist kein Demo-Seed. Es werden weder Geschaeftsdaten geloescht noch Datenbanken
angelegt/zurueckgesetzt. Nur **einen** Bootstraplauf gleichzeitig zulassen und
API-AutoMigrate waehrenddessen ausschliessen. Die bestehenden Migrationen werden
nicht umgeschrieben: 001 ist nicht als Gesamtmigration transaktional, 002 verwendet
eine Transaktion. Ein Fehler oder Abbruch kann bereits erfolgte Aenderungen
hinterlassen; kein automatischer Reset/Rollback/Reparaturversuch. Vor Migration
bestehender Daten Backup-/Restore-Faehigkeit bestaetigen und einen Teilfehler
kontrolliert untersuchen.

Search erstellt nur einen fehlenden Index mit `CreateIndexAsync` (POST), nie
`CreateOrUpdateIndexAsync`, Update oder Delete. Die acht Felder sind
`id` (String/Key), `productId` (String), `version` (Int32), `approved` (Boolean),
`validFrom`/`validTo` (DateTimeOffset), `content` (String, standard.lucene) und
`hash` (String). Produkt/Freigabe/Gueltigkeit sind filterbar; nur Inhalt ist
volltextsuchbar. Bestehende Definitionen muessen den Feld-, Key-, Retrievable-,
Filter-, Analyzer- und Suchvertrag erfuellen; zusaetzliche/abweichende Felder oder
ein Default-Scoring-Profil werden verweigert, nicht repariert. Anlage wird
anschliessend lesend verifiziert. Konkurrierende Anlage (409), Rechte-, Netzwerk-
und Readbackfehler bleiben Fehler. Der Index bleibt leer: `KnowledgeSearch.Rebuild`
und die fachliche SQL-/Blob-Autorisierung sind **separate** Betriebsaktionen.

Compile, Testausfuehrung, reale Identitaet/Rechte, Private DNS, SQL-Migration und
Search-Anlage sind fuer diesen Folgepatch noch nicht nachgewiesen. Hier wurden
nur Quellvertraege/Diff und offizielle SDK-Signaturen statisch geprueft. Kein
Azure-/Deployment-/Runtime-/Lastnachweis; bestehende Security-, EU-, Backup-,
Ledger-Digest-, Kosten- und Freigabegates bleiben unveraendert.

## Primaerquellen

- [Indexanlage und Feldvertrag](https://learn.microsoft.com/azure/search/search-how-to-create-search-index)
- [SearchIndexClient.CreateIndexAsync, SDK 12.0.0](https://learn.microsoft.com/dotnet/api/azure.search.documents.indexes.searchindexclient.createindexasync?view=azure-dotnet)
- [SearchField-SDK-Vertrag](https://learn.microsoft.com/dotnet/api/azure.search.documents.indexes.models.searchfield?view=azure-dotnet)
- [Search-Rollen und Grenzen](https://learn.microsoft.com/azure/search/search-security-rbac)
- [Search-Namensregeln](https://learn.microsoft.com/rest/api/searchservice/naming-rules)
- [CREATE TABLE: Berechtigungen](https://learn.microsoft.com/sql/t-sql/statements/create-table-transact-sql?view=sql-server-ver17#permissions)
- [Append-only Ledger: ENABLE LEDGER](https://learn.microsoft.com/sql/relational-databases/security/ledger/ledger-how-to-append-only-ledger-tables?view=sql-server-ver17#permissions)
