# Bytegenaue Objektintegritaet

`LocalObjectStore` und `AzureObjectStore` pruefen SHA-256 der tatsaechlich gelesenen
Rohbytes gegen die Objekt-ID **vor** jeder Textdekodierung. Danach gilt strikt
UTF-8 ohne automatische BOM-/UTF-16-Erkennung und ohne Ersatzzeichen-Fallback.
Ein explizites U+FEFF im gueltigen Text bleibt Inhalt. Bereits vorhandene Objekte
werden auch beim idempotenten `Put` mit derselben Rohbyte-/Dekodierpruefung gelesen.
Ungueltige UTF-16-Eingaben werden vor einem Schreibzugriff abgewiesen.

Gueltige UTF-8-Inhalte behalten ihre bisherigen `Canonical.HashText`-IDs und
gespeicherten Bytes; es gibt keine Neuschreibung, Migration, neue Speicherschicht
oder automatische Reparatur bestehender Evidenz beziehungsweise eingefrorener
Pakete. Abweichende Rohbytes ergeben `object_integrity`; eine nach erfolgreicher
Rohhashpruefung ungueltige UTF-8-Dekodierung ergibt `object_encoding`.

## Ausgefuehrter lokaler Nachweis

Neun regulaere Tests in `ObjectStoreIntegrityTests.cs` verwenden den wirklichen
lokalen Dateistore sowie den wirklichen Azure-Store und Blob-SDK-Lesepfad mit
einem abgegrenzten synthetischen HTTP-Transport. Der Transport kontaktiert keine
Azure-Ressource. Geprueft werden eingefuegte UTF-8-Praeambel, UTF-16-Umkodierung,
ersetzend dekodierbare ungueltige Bytes, hashgleiche aber ungueltige UTF-8-Daten,
unvollstaendige Surrogate und unveraenderte gueltige mehrsprachige Inhalte.

Auf einer isolierten Quellkopie von
`a5c75454553e1325246546578a8ab3ef624ab119` mit ausschliesslich dieser neuen
Testdatei schlugen sieben der neun Tests erwartungsgemaess fehl. Nach Uebernahme
nur der korrigierten `ObjectStores.cs` und einem erzwungenen Neubau:
**9/9 bestanden, kein Skip, Build ohne Warnungen/Fehler** (Windows, .NET 10).
Ein erster inkrementeller Nachlauf verwendete wegen erhaltener Kopierzeitstempel
noch alte Assemblies; deshalb wurde explizit mit `--no-incremental` neu gebaut.

Lokale Belege liegen unter
`.local\object-integrity-checkpoint-proof\evidence\baseline.trx` und
`corrected-rebuilt.trx`; sie gehoeren nicht in Git. Reproduktion im jeweiligen
Quellstand mit `dotnet build tests\Cohors.Tests\Cohors.Tests.csproj --no-incremental`,
danach `dotnet test tests\Cohors.Tests\Cohors.Tests.csproj --no-build
--filter FullyQualifiedName~ObjectStoreIntegrityTests`.

Dies ist weder ein Cloud-/Azure-Storage-Dienstnachweis noch eine vollstaendige
T20-Wiederherstellungsabnahme. Die separat gemeldete blockierte PowerShell-Probe
wurde nicht verwendet oder wiederholt; Schutzkontrollen blieben unveraendert.
