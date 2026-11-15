using System.Data;
using System.Reflection;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Cohors.Application;
using Cohors.Domain;
using Microsoft.Data.SqlClient;

namespace Cohors.Infrastructure;

public sealed class SqlStateStore(string connectionString, TokenCredential? credential = null) : IStateStore, IRetentionStateStore
{
    public const int SchemaVersion = 2;
    private readonly Func<SqlAuthenticationParameters, CancellationToken, Task<SqlAuthenticationToken>>? tokenCallback =
        credential is null ? null : async (_, ct) =>
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(["https://database.windows.net/.default"]), ct);
            return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
        };

    public static SqlStateStore Configure(string connection, string? authentication, bool localAllowed)
    {
        var mode = authentication ?? (localAllowed ? "Local" : "ManagedIdentity");
        var settings = new SqlConnectionStringBuilder(connection);
        if (mode == "Local")
        {
            if (!localAllowed || !System.Text.RegularExpressions.Regex.IsMatch(settings.DataSource,
                    @"^(tcp:)?(localhost|127\.0\.0\.1|\[::1\]|::1)(,\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Local SQL authentication requires Development/Testing and exact loopback.");
            return new(connection);
        }
        if (mode != "ManagedIdentity" || settings.Authentication != SqlAuthenticationMethod.NotSpecified ||
            settings.IntegratedSecurity || settings.UserID.Length != 0 || settings.Password.Length != 0 ||
            settings.TrustServerCertificate || settings.Encrypt == SqlConnectionEncryptOption.Optional)
            throw new InvalidOperationException("ManagedIdentity SQL requires an encrypted, credential-free connection string and certificate validation.");
        return new(connection, new DefaultAzureCredential());
    }

    public async Task Initialize(CancellationToken ct = default)
    {
        await using var connection = await Open(ct);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Migrations.") && n.EndsWith(".sql")).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            using var command = new SqlCommand(await reader.ReadToEndAsync(ct), connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task RequireReady(CancellationToken ct = default)
    {
        await using var connection = await Open(ct);
        using var command = new SqlCommand("SELECT COALESCE(MAX(Version),0) FROM dbo.SchemaVersions", connection);
        Guard.Against((int)(await command.ExecuteScalarAsync(ct))! != SchemaVersion,
            "schema_not_ready", "SQL-Schema benoetigt eine explizite, zur Anwendung passende Migration.", 503);
    }

    public async Task<SqlConnection> Open(CancellationToken ct = default)
    {
        var connection = new SqlConnection(connectionString);
        if (tokenCallback is not null) connection.AccessTokenCallback = tokenCallback;
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<T?> Get<T>(string kind, string id, CancellationToken ct = default)
    {
        await using var connection = await Open(ct);
        using var query = Command(connection, null, "SELECT Json FROM dbo.Aggregates WHERE Kind=@kind AND Id=@id",
            ("kind", kind), ("id", id));
        var json = await query.ExecuteScalarAsync(ct) as string;
        return json is null ? default : Canonical.Read<T>(json);
    }

    public async Task<T[]> List<T>(string kind, string[] products, CancellationToken ct = default)
    {
        if (products.Length == 0) return [];
        await using var connection = await Open(ct);
        using var query = Command(connection, null,
            "SELECT Json FROM dbo.Aggregates WHERE Kind=@kind AND ProductId IN (SELECT value FROM OPENJSON(@products)) ORDER BY UpdatedAt DESC, Id",
            ("kind", kind), ("products", Canonical.Serialize(products)));
        var items = new List<T>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(Canonical.Read<T>(reader.GetString(0)));
        return items.ToArray();
    }

    public async Task<T[]> History<T>(string kind, string[] products, CancellationToken ct = default)
    {
        if (products.Length == 0) return [];
        await using var connection = await Open(ct);
        using var query = Command(connection, null, """
            SELECT h.Json FROM dbo.AggregateVersions h
            INNER JOIN dbo.Aggregates a ON a.Kind=h.Kind AND a.Id=h.Id
            WHERE h.Kind=@kind AND a.ProductId IN (SELECT value FROM OPENJSON(@products))
            ORDER BY h.Id,h.Version
            """, ("kind", kind), ("products", Canonical.Serialize(products)));
        var items = new List<T>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(Canonical.Read<T>(reader.GetString(0)));
        return items.ToArray();
    }

    public async Task<PortfolioState> Capture(string[] products, CancellationToken ct = default)
    {
        await using var connection = await Open(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        using var query = Command(connection, transaction, """
            SELECT a.Kind,a.Id,a.Json,a.UpdatedAt,h.Hash,CAST(0 AS bit) AS Historical FROM dbo.Aggregates a
            LEFT JOIN dbo.AggregateVersions h ON h.Kind=a.Kind AND h.Id=a.Id AND h.Version=a.Version
            WHERE a.ProductId IN (SELECT value FROM OPENJSON(@products)) AND a.Kind IN ('case','exposure','trend','capa','change','productGovernance','psur','source','psurGroup')
            UNION ALL
            SELECT h.Kind,h.Id,h.Json,h.At,h.Hash,CAST(1 AS bit) AS Historical FROM dbo.AggregateVersions h
            INNER JOIN dbo.Aggregates a ON a.Kind=h.Kind AND a.Id=h.Id
            WHERE h.Kind IN ('evidence','case','capa','change') AND a.ProductId IN (SELECT value FROM OPENJSON(@products))
            """, ("products", Canonical.Serialize(products)));
        var rows = new List<(string Kind, string Json)>();
        var history = new List<AggregateRevision>();
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var json = reader.GetString(2);
                Guard.Against(reader.IsDBNull(4) || Canonical.HashText(json) != reader.GetString(4),
                    "portfolio_integrity", "Aktueller oder historischer SQL-Payload stimmt nicht mit der gespeicherten Version ueberein.", 409);
                if (!reader.GetBoolean(5) || reader.GetString(0) == "evidence") rows.Add((reader.GetString(0), json));
                if (reader.GetBoolean(5))
                {
                    using var document = System.Text.Json.JsonDocument.Parse(json);
                    history.Add(new(reader.GetString(0), reader.GetString(1), document.RootElement.GetProperty("version").GetInt32(),
                        reader.GetFieldValue<DateTimeOffset>(3), json, reader.GetString(4)));
                }
            }
        await transaction.CommitAsync(ct);
        T[] Read<T>(string kind) => rows.Where(r => r.Kind == kind).Select(r => Canonical.Read<T>(r.Json)).ToArray();
        return new(Read<SafetyCase>("case"), Read<Evidence>("evidence"), Read<Exposure>("exposure"),
            Read<TrendRun>("trend"), Read<Capa>("capa"), Read<ChangeRequest>("change"), history.ToArray(),
            Read<ProductGovernance>("productGovernance"), Read<Psur>("psur"), Read<PublicSourceRun>("source"), Read<PsurGroupDefinition>("psurGroup"));
    }

    public Task<T> Execute<T>(CommandContext c, Func<T?, T> change, CancellationToken ct = default) =>
        ExecuteCore<T>(c, current => Task.FromResult(change(current)), [], ct);

    public Task<Psur> FreezePsur(CommandContext c, PsurCaptureScope scope, Func<PortfolioState, Psur> create, CancellationToken ct = default)
    {
        Guard.Against(c.Kind != "psur" || c.ExpectedVersion != 0, "psur_create", "Seriengebundene Erzeugung braucht ein neues PSUR-Aggregat.");
        // The series lock spans a consistent read and the atomic aggregate/audit/outbox write.
        // The separate read does not turn Commands into serializable empty-key-range locks.
        Guard.Against(!scope.Products.Contains(c.ProductId), "psur_capture_scope", "Leitprodukt muss zum vollstaendigen Capture-Scope gehoeren.");
        Guard.Text(scope.LeadingBasicUdi, "Fuehrende Basic-UDI", 1, 100);
        foreach (var product in scope.Products) c.Actor.RequireProduct(product);
        string[] locks = ["psur-series:" + c.ProductId, "psur-basic-udi:" + scope.LeadingBasicUdi,
            .. scope.GroupId is null ? Array.Empty<string>() : ["aggregate:psurGroup:" + scope.GroupId]];
        return ExecuteCore<Psur>(c, async _ =>
        {
            var portfolio = await Capture(scope.Products, ct);
            return create(portfolio with { LeadingPeriods = await LeadingPeriods(scope.LeadingBasicUdi, ct) });
        }, locks, ct);
    }

    private async Task<PsurLeadingPeriod[]> LeadingPeriods(string basicUdi, CancellationToken ct)
    {
        await using var connection = await Open(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        using var query = Command(connection, transaction, """
            SELECT a.ProductId,a.Json,h.Hash FROM dbo.Aggregates a
            LEFT JOIN dbo.AggregateVersions h ON h.Kind=a.Kind AND h.Id=a.Id AND h.Version=a.Version
            WHERE a.Kind='psur'
            """);
        var result = new List<PsurLeadingPeriod>();
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var json = reader.GetString(1);
                Guard.Against(reader.IsDBNull(2) || Canonical.HashText(json) != reader.GetString(2),
                    "psur_period_integrity", "Bestehende fuehrende Periodenbindung hat keine konsistente SQL-Version.", 409);
                using var document = System.Text.Json.JsonDocument.Parse(json);
                var snapshot = document.RootElement.GetProperty("snapshot");
                if (!snapshot.TryGetProperty("series", out var series) || series.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
                if (series.GetProperty("governance").GetProperty("device").GetProperty("basicUdi").GetString() != basicUdi) continue;
                result.Add(new(reader.GetString(0), basicUdi, series.GetProperty("reference").GetString()!,
                    DateOnly.ParseExact(snapshot.GetProperty("start").GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    DateOnly.ParseExact(snapshot.GetProperty("end").GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
            }
        await transaction.CommitAsync(ct);
        return result.ToArray();
    }

    public async Task<ObjectDependency[]> ObjectDependencies(string hash, string retentionId, CancellationToken ct = default)
    {
        Guard.Against(hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)), "object_id", "Ungueltiger Objekt-Hash.");
        await using var connection = await Open(ct);
        using var query = Command(connection, null, """
            SELECT a.Kind,a.Id,a.Version,a.ProductId,a.Json,h.Hash
            FROM dbo.Aggregates a LEFT JOIN dbo.AggregateVersions h ON h.Kind=a.Kind AND h.Id=a.Id AND h.Version=a.Version
            WHERE NOT(a.Kind='retention' AND a.Id=@id) AND CHARINDEX(@hash,a.Json)>0
            UNION ALL
            SELECT h.Kind,h.Id,h.Version,COALESCE(a.ProductId,JSON_VALUE(h.Json,'$.productId'),''),h.Json,h.Hash
            FROM dbo.AggregateVersions h LEFT JOIN dbo.Aggregates a ON a.Kind=h.Kind AND a.Id=h.Id
            WHERE NOT(h.Kind='retention' AND h.Id=@id) AND CHARINDEX(@hash,h.Json)>0
            UNION ALL
            SELECT 'outbox:'+o.AggregateKind,o.Id,o.Version,o.ProductId,o.Payload,h.Hash
            FROM dbo.Outbox o LEFT JOIN dbo.AggregateVersions h
                ON h.Kind=o.AggregateKind AND h.Id=o.AggregateId AND h.Version=o.Version
            WHERE NOT(o.AggregateKind='retention' AND o.AggregateId=@id) AND CHARINDEX(@hash,o.Payload)>0
            """, ("hash", hash), ("id", retentionId));
        var result = new List<ObjectDependency>();
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                Guard.Against(reader.IsDBNull(5) || Canonical.HashText(reader.GetString(4)) != reader.GetString(5),
                    "retention_reference_integrity", "Objektreferenz hat keinen konsistenten SQL-/Historien-/Outbox-Stand.", 409);
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(5)));
            }
        using var commands = Command(connection, null, """
            SELECT IdempotencyKey,Result FROM dbo.Commands WHERE CHARINDEX(@hash,Result)>0
            """, ("hash", hash));
        await using (var reader = await commands.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var json = reader.GetString(1);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id) && id.GetString() == retentionId &&
                    root.TryGetProperty("object", out var binding) &&
                    binding.TryGetProperty("contract", out var contract) && contract.GetString() == RetentionRules.ObjectContract)
                    continue;
                result.Add(new("command", reader.GetString(0), root.GetProperty("version").GetInt32(),
                    root.GetProperty("productId").GetString()!, Canonical.HashText(json)));
            }
        return result.DistinctBy(r => (r.Kind, r.Id, r.Version)).ToArray();
    }

    public Task<RetentionRecord> ExecuteDeletion(CommandContext c, string hash,
        Func<RetentionRecord, ObjectDependency[], Task<RetentionRecord>> change, CancellationToken ct = default)
    {
        Guard.Against(c.Kind != "retention" || c.ExpectedVersion < 1, "retention_command", "Versionierter Loeschauftrag erforderlich.");
        return ExecuteCore<RetentionRecord>(c, async current =>
        {
            Guard.Against(current is null || current.Object.Hash != hash, "retention_object_changed",
                "Loeschauftrag bindet nicht das aktuelle Objekt.", 409);
            return await change(current!, await ObjectDependencies(hash, c.Id, ct));
        }, [], ct, deletion: true);
    }

    private static async Task RequireNoRetiredReferences(SqlConnection connection, SqlTransaction transaction,
        CommandContext c, string json, CancellationToken ct)
    {
        using var query = Command(connection, transaction, """
            SELECT a.Json,h.Hash FROM dbo.Aggregates a
            LEFT JOIN dbo.AggregateVersions h ON h.Kind=a.Kind AND h.Id=a.Id AND h.Version=a.Version
            WHERE a.Kind='retention' AND NOT(@kind='retention' AND a.Id=@id)
                AND JSON_VALUE(a.Json,'$.status') IN ('Authorized','Deleting','DeletionFailed','Deleted')
            """, ("kind", c.Kind), ("id", c.Id));
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            Guard.Against(reader.IsDBNull(1) || Canonical.HashText(reader.GetString(0)) != reader.GetString(1),
                "retention_record_integrity", "Loeschsperre hat keine konsistente SQL-Version.", 409);
            var retained = Canonical.Read<RetentionRecord>(reader.GetString(0));
            RetentionRules.RequireObject(retained.Object);
            Guard.Against(json.Contains(retained.Object.Hash, StringComparison.Ordinal), "retention_rebinding",
                "Neue Bindung an ein zur Loeschung autorisiertes oder geloeschtes Objekt ist gesperrt.", 409);
        }
    }

    private async Task<T> ExecuteCore<T>(CommandContext c, Func<T?, Task<T>> change, string[] locks, CancellationToken ct,
        bool deletion = false)
    {
        c.Actor.RequireProduct(c.ProductId);
        Guard.Text(c.IdempotencyKey, "Idempotency-Key", 8, 120);
        Guard.Text(c.Reason, "Begruendung", 1, 4000);
        var commandKey = Canonical.Hash(new { c.Actor.Id, c.Kind, c.Action, c.IdempotencyKey });
        var requestHash = Canonical.Hash(new { c.Id, c.ProductId, c.ExpectedVersion, c.Request });
        await using var connection = await Open(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // A destructive scan must exclude new references until its SQL outcome commits.
        using (var objectGate = Command(connection, transaction,
            "DECLARE @r int; EXEC @r=sp_getapplock @Resource='cohors-object-reference-gate',@LockMode=@mode,@LockOwner='Transaction',@LockTimeout=15000; SELECT @r;",
            ("mode", deletion ? "Exclusive" : "Shared")))
            Guard.Against((int)(await objectGate.ExecuteScalarAsync(ct))! < 0, "retention_busy",
                "Objektbindungen werden kontrolliert abgeglichen; unveraenderten Auftrag spaeter erneut pruefen.", 409);
        using (var lockCommand = Command(connection, transaction,
            "DECLARE @r int; EXEC @r=sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000; SELECT @r;",
            ("key", commandKey)))
        {
            var result = (int)(await lockCommand.ExecuteScalarAsync(ct))!;
            Guard.Against(result < 0, "command_busy", "Command wird bereits verarbeitet; spaeter mit demselben Schluessel abgleichen.", 409);
        }
        foreach (var resource in locks.Order(StringComparer.Ordinal))
        {
            using var seriesLock = Command(connection, transaction,
                "DECLARE @r int; EXEC @r=sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000; SELECT @r;",
                ("key", resource));
            Guard.Against((int)(await seriesLock.ExecuteScalarAsync(ct))! < 0, "psur_series_busy",
                "Die Produktserie wird gerade eingefroren; unveraenderten Auftrag spaeter abgleichen.", 409);
        }
        using (var aggregateLock = Command(connection, transaction,
            "DECLARE @r int; EXEC @r=sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000; SELECT @r;",
            ("key", "aggregate:" + c.Kind + ":" + c.Id)))
        {
            var result = (int)(await aggregateLock.ExecuteScalarAsync(ct))!;
            Guard.Against(result < 0, "aggregate_busy", "Aggregat wird bearbeitet; mit unveraenderter erwarteter Version erneut pruefen.", 409);
        }
        using (var existing = Command(connection, transaction,
            "SELECT RequestHash,Result FROM dbo.Commands WHERE IdempotencyKey=@key", ("key", commandKey)))
        {
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                Guard.Against(reader.GetString(0) != requestHash, "idempotency_conflict",
                    "Idempotency-Key wurde fuer einen anderen Auftrag verwendet.", 409);
                return Canonical.Read<T>(reader.GetString(1));
            }
        }
        T? current = default;
        var version = 0;
        using (var read = Command(connection, transaction,
            """
            SELECT a.Version,a.Json,a.ProductId,h.Hash FROM dbo.Aggregates a WITH (UPDLOCK)
            LEFT JOIN dbo.AggregateVersions h ON h.Kind=a.Kind AND h.Id=a.Id AND h.Version=a.Version
            WHERE a.Kind=@kind AND a.Id=@id
            """,
            ("kind", c.Kind), ("id", c.Id)))
        {
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                version = reader.GetInt32(0);
                if (c.Kind is "retention" or "productLifecycle")
                    Guard.Against(reader.IsDBNull(3) || Canonical.HashText(reader.GetString(1)) != reader.GetString(3),
                        "retention_record_integrity", "Aufbewahrungs-/Lifecycle-Version ist nicht konsistent gebunden.", 409);
                current = Canonical.Read<T>(reader.GetString(1));
                Guard.Against(reader.GetString(2) != c.ProductId, "scope_conflict", "Produktscope stimmt nicht ueberein.", 403);
            }
        }
        Guard.Against(version != c.ExpectedVersion, "version_conflict",
            $"Veraltete Version {c.ExpectedVersion}; aktuell ist {version}. Bitte neu laden.", 409);
        var next = await change(current);
        var json = Canonical.Serialize(next);
        await RequireNoRetiredReferences(connection, transaction, c, json, ct);
        var nextVersion = version + 1;
        using var document = JsonDocument.Parse(json);
        Guard.Against(!document.RootElement.TryGetProperty("version", out var nextVersionElement)
            || nextVersionElement.GetInt32() != nextVersion, "aggregate_version",
            "Domaenenversion muss atomar um genau eins steigen.");
        Guard.Against(!document.RootElement.TryGetProperty("id", out var nextId) || nextId.GetString() != c.Id ||
            document.RootElement.TryGetProperty("productId", out var nextProduct) && nextProduct.GetString() != c.ProductId,
            "aggregate_identity", "Aggregatidentitaet und Produktscope duerfen nicht implizit wechseln.");
        var payloadHash = Canonical.HashText(json);
        using (var write = Command(connection, transaction, version == 0
            ? "INSERT dbo.Aggregates(Id,Kind,ProductId,Version,Json,UpdatedAt) VALUES(@id,@kind,@product,@version,@json,@at)"
            : "UPDATE dbo.Aggregates SET Version=@version,Json=@json,UpdatedAt=@at WHERE Kind=@kind AND Id=@id",
            ("id", c.Id), ("kind", c.Kind), ("product", c.ProductId), ("version", nextVersion), ("json", json), ("at", c.At)))
            await write.ExecuteNonQueryAsync(ct);
        using (var history = Command(connection, transaction,
            "INSERT dbo.AggregateVersions(Kind,Id,Version,Json,Hash,At) VALUES(@kind,@id,@version,@json,@hash,@at)",
            ("kind", c.Kind), ("id", c.Id), ("version", nextVersion), ("json", json), ("hash", payloadHash), ("at", c.At)))
            await history.ExecuteNonQueryAsync(ct);
        var previousHash = new string('0', 64);
        using (var head = Command(connection, transaction,
            "SELECT TOP(1) Hash FROM dbo.AuditEvents WHERE AggregateId=@id ORDER BY Sequence DESC", ("id", c.Id)))
            previousHash = await head.ExecuteScalarAsync(ct) as string ?? previousHash;
        var audit = new AuditEvent(0, c.Id, nextVersion, c.Actor.Id, c.Actor.Name, c.Actor.Roles,
            c.Action, c.Reason, c.At, c.IdempotencyKey, payloadHash, previousHash, "");
        var auditHash = Canonical.Hash(audit with { Sequence = 0, Hash = "" });
        using (var insert = Command(connection, transaction, """
            INSERT dbo.AuditEvents(AggregateId,Version,ActorId,ActorName,Roles,Action,Reason,At,CorrelationId,PayloadHash,PreviousHash,Hash)
            VALUES(@id,@version,@actor,@name,@roles,@action,@reason,@at,@correlation,@payload,@previous,@hash);
            INSERT dbo.Outbox(Id,AggregateId,AggregateKind,ProductId,Version,Type,Payload,CreatedAt)
            VALUES(@event,@id,@kind,@product,@version,@action,@json,@at);
            INSERT dbo.Commands(IdempotencyKey,RequestHash,ActorId,Result,At) VALUES(@key,@request,@actor,@json,@at);
            """, ("id", c.Id), ("version", nextVersion), ("actor", c.Actor.Id), ("name", c.Actor.Name),
            ("roles", Canonical.Serialize(c.Actor.Roles)), ("action", c.Action), ("reason", c.Reason), ("at", c.At),
            ("correlation", c.IdempotencyKey), ("payload", payloadHash), ("previous", previousHash), ("hash", auditHash),
            ("event", Guid.NewGuid().ToString("N")), ("kind", c.Kind), ("product", c.ProductId),
            ("json", json), ("key", commandKey), ("request", requestHash)))
            await insert.ExecuteNonQueryAsync(ct);
        if (next is SafetyCase safetyCase)
        {
            foreach (var deadline in safetyCase.Deadlines)
            {
                using var projection = Command(connection, transaction, """
                    UPDATE dbo.Deadlines SET LegalDue=@legal,SafetyDue=@safety,Status=@status,Version=@version WHERE Id=@id;
                    IF @@ROWCOUNT=0 INSERT dbo.Deadlines(Id,CaseId,ProductId,LegalDue,SafetyDue,Status,Version)
                    VALUES(@id,@case,@product,@legal,@safety,@status,@version);
                    """, ("id", deadline.Id), ("case", c.Id), ("product", c.ProductId), ("legal", deadline.LegalDue),
                    ("safety", deadline.SafetyDue), ("status", deadline.Status), ("version", nextVersion));
                await projection.ExecuteNonQueryAsync(ct);
            }
        }
        if (next is OutboxRecovery recovery)
        {
            using var restart = Command(connection, transaction, """
                UPDATE dbo.Outbox SET Attempts=0,Error=NULL,NextAttemptAt=NULL,LeaseOwner=NULL,LeaseUntil=NULL,
                    RecoveryVersion=@version
                WHERE Id=@id AND ProductId=@product AND CompletedAt IS NULL
                    AND (LeaseUntil IS NULL OR LeaseUntil<SYSUTCDATETIME());
                """, ("id", recovery.EventId), ("product", recovery.ProductId), ("version", recovery.Version));
            Guard.Against(await restart.ExecuteNonQueryAsync(ct) != 1, "delivery_not_recoverable",
                "Aktive oder erledigte Zustellung darf nicht blind erneut gestartet werden.", 409);
        }
        if (next is RetentionRecord { Status: "Deleted", Deletion: not null } retired)
        {
            RetentionRules.RequireDeletionReceipt(retired, retired.Deletion);
            using var purge = Command(connection, transaction, "DELETE FROM dbo.SearchProjection WHERE Hash=@hash",
                ("hash", retired.Object.Hash));
            await purge.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return next;
    }

    public async Task<T?> Receipt<T>(CommandContext c, CancellationToken ct = default)
    {
        c.Actor.RequireProduct(c.ProductId);
        Guard.Text(c.IdempotencyKey, "Idempotency-Key", 8, 120);
        var key = Canonical.Hash(new { c.Actor.Id, c.Kind, c.Action, c.IdempotencyKey });
        var hash = Canonical.Hash(new { c.Id, c.ProductId, c.ExpectedVersion, c.Request });
        await using var connection = await Open(ct);
        using var command = Command(connection, null,
            "SELECT RequestHash,Result FROM dbo.Commands WHERE IdempotencyKey=@key", ("key", key));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return default;
        Guard.Against(reader.GetString(0) != hash, "idempotency_conflict",
            "Idempotency-Key wurde fuer einen anderen Auftrag verwendet.", 409);
        return Canonical.Read<T>(reader.GetString(1));
    }

    public async Task<object> Manifest(string id, CancellationToken ct = default, string kind = "case")
    {
        await using var connection = await Open(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        string currentJson;
        int currentVersion;
        string currentProduct;
        using (var query = Command(connection, transaction,
            "SELECT Version,Json,ProductId FROM dbo.Aggregates WITH(HOLDLOCK) WHERE Kind=@kind AND Id=@id", ("id", id), ("kind", kind)))
        {
            await using var reader = await query.ExecuteReaderAsync(ct);
            Guard.Against(!await reader.ReadAsync(ct), "aggregate_missing", "Fuehrender Aggregattyp/Datensatz fehlt.", 404);
            currentVersion = reader.GetInt32(0);
            currentJson = reader.GetString(1);
            currentProduct = reader.GetString(2);
        }
        var audit = await ReadAudit(connection, transaction, id, ct);
        Guard.Against(audit.Length == 0, "audit_missing", "Keine Auditkette vorhanden.", 404);
        var previous = new string('0', 64);
        var expectedVersion = 1;
        foreach (var entry in audit)
        {
            Guard.Against(entry.Version != expectedVersion++ || entry.PreviousHash != previous ||
                entry.Hash != Canonical.Hash(entry with { Sequence = 0, Hash = "" }),
                "audit_tampered", "Auditkette ist nicht konsistent.", 409);
            using var query = Command(connection, transaction,
                "SELECT Json FROM dbo.AggregateVersions WHERE Kind=@kind AND Id=@id AND Version=@version",
                ("kind", kind), ("id", id), ("version", entry.Version));
            var json = await query.ExecuteScalarAsync(ct) as string;
            Guard.Against(json is null || Canonical.HashText(json) != entry.PayloadHash,
                "history_tampered", "Historische Version passt nicht zum Audit.", 409);
            previous = entry.Hash;
        }
        using var current = JsonDocument.Parse(currentJson);
        Guard.Against(currentVersion != audit[^1].Version || Canonical.HashText(currentJson) != audit[^1].PayloadHash ||
            !current.RootElement.TryGetProperty("version", out var version) || version.GetInt32() != currentVersion ||
            !current.RootElement.TryGetProperty("id", out var currentId) || currentId.GetString() != id ||
            current.RootElement.TryGetProperty("productId", out var product) && product.GetString() != currentProduct,
            "current_state_tampered", "Fuehrender SQL-Datensatz stimmt nicht mit der letzten geprueften Historienversion ueberein.", 409);
        await transaction.CommitAsync(ct);
        return new { aggregateId = id, kind, auditVersion = audit[^1].Version, currentVersion, currentStateVerified = true, hashChainVerified = true,
            lastHash = previous, events = audit, externalLedgerDigestVerification = "Not performed; deployment gate" };
    }

    public async Task<AuditEvent[]> Audit(string id, CancellationToken ct = default)
    {
        await using var connection = await Open(ct);
        return await ReadAudit(connection, null, id, ct);
    }

    private static async Task<AuditEvent[]> ReadAudit(SqlConnection connection, SqlTransaction? transaction, string id, CancellationToken ct)
    {
        using var command = Command(connection, transaction,
            "SELECT Sequence,AggregateId,Version,ActorId,ActorName,Roles,Action,Reason,At,CorrelationId,PayloadHash,PreviousHash,Hash FROM dbo.AuditEvents WHERE AggregateId=@id ORDER BY Sequence",
            ("id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AuditEvent>();
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                reader.GetString(4), Canonical.Read<string[]>(reader.GetString(5)), reader.GetString(6),
                reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8), reader.GetString(9),
                reader.GetString(10), reader.GetString(11), reader.GetString(12)));
        return result.ToArray();
    }

    public async Task<OutboxEvent[]> PendingEvents(int limit, CancellationToken ct = default)
    {
        await using var connection = await Open(ct);
        using var query = Command(connection, null,
            """
            SELECT TOP(@limit) Id,AggregateId,Version,Type,Payload,CreatedAt,Attempts,Error,RecoveryVersion FROM dbo.Outbox
            WHERE CompletedAt IS NULL AND Attempts<5 AND (LeaseUntil IS NULL OR LeaseUntil<SYSUTCDATETIME())
                AND (NextAttemptAt IS NULL OR NextAttemptAt<=SYSUTCDATETIME()) ORDER BY CreatedAt,Id
            """,
            ("limit", Math.Clamp(limit, 1, 100)));
        await using var reader = await query.ExecuteReaderAsync(ct);
        var events = new List<OutboxEvent>();
        while (await reader.ReadAsync(ct))
            events.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8)));
        return events.ToArray();
    }

    public async Task<OutboxEvent?> ClaimEvent(string eventId, string owner, CancellationToken ct = default)
    {
        await using var connection = await Open(ct);
        using var command = Command(connection, null, """
            UPDATE dbo.Outbox WITH(UPDLOCK,ROWLOCK) SET LeaseOwner=@owner,LeaseUntil=DATEADD(minute,10,SYSUTCDATETIME()),Attempts=Attempts+1
            OUTPUT inserted.Id,inserted.AggregateId,inserted.Version,inserted.Type,inserted.Payload,
                inserted.CreatedAt,inserted.Attempts,inserted.Error,inserted.RecoveryVersion
            WHERE Id=@id AND CompletedAt IS NULL AND Attempts<5
                AND (LeaseUntil IS NULL OR LeaseUntil<SYSUTCDATETIME())
                AND (NextAttemptAt IS NULL OR NextAttemptAt<=SYSUTCDATETIME());
            """, ("id", eventId), ("owner", owner));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8)) : null;
    }

    public async Task<OutboxIssue[]> OutboxIssues(string[] products, CancellationToken ct = default)
    {
        await using var connection = await Open(ct);
        using var query = Command(connection, null, """
            SELECT Id,ProductId,Type,Attempts,Error,LeaseUntil,NextAttemptAt,RecoveryVersion FROM dbo.Outbox
            WHERE CompletedAt IS NULL AND (Attempts>0 OR Error IS NOT NULL)
                AND ProductId IN (SELECT value FROM OPENJSON(@products)) ORDER BY CreatedAt,Id;
            """, ("products", Canonical.Serialize(products)));
        var result = new List<OutboxIssue>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt32(7)));
        return result.ToArray();
    }

    public async Task<bool> HasReceipt(string eventId, string consumer, CancellationToken ct = default)
    {
        await using var connection = await Open(ct);
        using var query = Command(connection, null,
            "SELECT COUNT(*) FROM dbo.Inbox WHERE EventId=@id AND Consumer=@consumer", ("id", eventId), ("consumer", consumer));
        return (int)(await query.ExecuteScalarAsync(ct))! > 0;
    }

    public async Task CompleteEvent(string eventId, string consumer, string owner, string? error, CancellationToken ct = default)
    {
        await using var connection = await Open(ct);
        using var command = Command(connection, null, """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            IF EXISTS(SELECT 1 FROM dbo.Inbox WHERE EventId=@id AND Consumer=@consumer)
            BEGIN COMMIT; RETURN; END;
            UPDATE dbo.Outbox SET Error=@error,LeaseOwner=NULL,LeaseUntil=NULL,
                CompletedAt=CASE WHEN @error IS NULL THEN SYSUTCDATETIME() ELSE NULL END,
                NextAttemptAt=CASE WHEN @error IS NOT NULL THEN DATEADD(second,15*POWER(2,Attempts-1),SYSUTCDATETIME()) ELSE NULL END
                WHERE Id=@id AND LeaseOwner=@owner AND CompletedAt IS NULL;
            IF @@ROWCOUNT<>1 BEGIN ROLLBACK; THROW 51001,'Delivery lease no longer belongs to this execution.',1; END;
            IF @error IS NULL AND NOT EXISTS(SELECT 1 FROM dbo.Inbox WITH(UPDLOCK,HOLDLOCK) WHERE EventId=@id AND Consumer=@consumer)
                INSERT dbo.Inbox VALUES(@id,@consumer,SYSUTCDATETIME());
            COMMIT;
            """, ("id", eventId), ("consumer", consumer), ("owner", owner), ("error", error));
        await command.ExecuteNonQueryAsync(ct);
    }

    public static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql,
        params (string Key, object? Value)[] values)
    {
        var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 30 };
        foreach (var (key, value) in values) command.Parameters.AddWithValue("@" + key, value ?? DBNull.Value);
        return command;
    }
}
