using System.Text.Json;
using Cohors.Domain;

namespace Cohors.Application;

public sealed record AuditManifest(string AggregateId, string Kind, int AuditVersion, int CurrentVersion,
    bool CurrentStateVerified, bool HashChainVerified, string LastHash, AuditEvent[] Events,
    string ExternalLedgerDigestVerification);
public sealed record AuditVersion(int Version, DateTimeOffset At, string Json, string Hash);
public sealed record AuditAggregate(string Kind, string Id, string ProductId, string[] Products,
    AuditManifest Manifest, AuditVersion[] Versions);
public sealed record AuditObjectReference(string Hash, string[] Uses);
public sealed record AuditExclusion(string Source, string DataClass, string Reason);
public sealed record AuditTransport(JsonElement[] Outbox, JsonElement[] Inbox, JsonElement[] Commands,
    JsonElement[] Deadlines);
public sealed record ProductAuditCapture(string ProductId, string[] Products, DateTimeOffset CapturedAt,
    AuditAggregate[] Aggregates, AuditTransport Transport);

public static class ProductAuditRules
{
    public const string Contract = "cohors-product-audit-1";
    public const int MaximumBytes = 128 * 1024 * 1024;
    public const int MaximumVersions = 20000;
    private static readonly string[] Kinds =
    [
        "case", "evidence", "exposure", "trend", "capa", "change", "productGovernance", "psur",
        "source", "psurGroup", "psurReview", "agentInvocation", "retention", "productLifecycle", "delivery-recovery",
        "summaryAgreement", "summaryReport"
    ];
    private static readonly string[] JsonFields = ["Json", "Payload", "Result", "frozenJson", "inputJson", "outputJson"];

    public static void RequireReader(Actor actor, string product)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory, Roles.Quality, Roles.Auditor);
        actor.RequireProduct(product);
    }

    public static string[] Products(string kind, string product, string json)
    {
        Guard.Against(!Kinds.Contains(kind), "audit_kind",
            "Unbekannte fachliche Datenklasse; kein still unvollstaendiges Produktpaket.", 409);
        var products = new HashSet<string>(StringComparer.Ordinal) { product };
        using var document = JsonDocument.Parse(json);
        Walk(document.RootElement, node =>
        {
            if (node.TryGetProperty("productId", out var value)) Add(value);
            if (node.TryGetProperty("leadProductId", out value)) Add(value);
            if (node.TryGetProperty("products", out value) && value.ValueKind == JsonValueKind.Array)
                foreach (var member in value.EnumerateArray())
                    if (member.ValueKind == JsonValueKind.String) Add(member);
        });
        if (kind == "agentInvocation")
        {
            var invocation = Canonical.Read<AgentInvocation>(json);
            Guard.Against(invocation.InputJson is null || Canonical.HashText(invocation.InputJson) != invocation.InputHash,
                "audit_agent_input", "Historischer Modelleingang fehlt oder ist nicht gebunden; sein Produktscope bleibt unbekannt.", 409);
            var input = Canonical.Read<AgentInput>(invocation.InputJson!);
            Guard.Against(AgentBoundary.Validate(input) != product, "audit_agent_scope",
                "Modelleingang passt nicht zum gespeicherten fuehrenden Produktscope.", 409);
            if (input.Snapshot is { } snapshot) products.UnionWith(PsurGroupRules.Products(snapshot));
        }
        if (kind == "psur")
        {
            var report = Canonical.Read<Psur>(json);
            products.UnionWith(PsurGroupRules.Products(report.Snapshot));
            Guard.Against(!report.Simulated || report.SnapshotHash != Canonical.Hash(report.Snapshot) ||
                report.ScopeHash.Length > 0 && report.ScopeHash != PortfolioRules.PsurScope(report) ||
                PsurGroupRules.Check(report.Snapshot).Length != 0,
                "audit_psur_binding", "Historische PSUR-/Gruppen-/Freigabebindung stimmt nicht.", 409);
        }
        if (kind == "psurReview") products.UnionWith(PsurReviewRules.Products(Canonical.Read<PsurReview>(json)));
        if (kind == "summaryAgreement") SummaryReportingRules.RequireAgreement(Canonical.Read<SummaryAgreement>(json));
        if (kind == "summaryReport") SummaryReportingRules.RequireIntegrity(Canonical.Read<PeriodicSummaryReport>(json));
        if (kind == "case")
        {
            var c = Canonical.Read<SafetyCase>(json);
            Guard.Against(!c.Synthetic, "audit_synthetic",
                "Dieses Pilotpaket unterstuetzt nur synthetische Fallakten.", 409);
            foreach (var investigation in c.Investigations ?? [])
            {
                SpecimenInvestigationRules.RequireIntegrity(investigation);
                Guard.Against(investigation.CaseSource.Id != c.Id || investigation.Device.Id != c.ProductId ||
                    investigation.CaseSource.Version >= c.Version ||
                    investigation.PriorNotice?.CaseVersion > c.Version || investigation.Outcome?.CaseVersion > c.Version,
                    "audit_specimen_binding", "Untersuchungs-/Vorabbeleg passt nicht zur gespeicherten Fallfassung.", 409);
            }
        }
        return products.Order(StringComparer.Ordinal).ToArray();

        void Add(JsonElement value)
        {
            Guard.Against(value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()),
                "audit_product", "Leerer oder ungueltiger eingebetteter Produktscope.", 409);
            products.Add(value.GetString()!);
        }
    }

    public static (AuditObjectReference[] Objects, AuditExclusion[] Exclusions) Objects(AuditAggregate[] aggregates)
    {
        var references = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var exclusions = new List<AuditExclusion>();
        foreach (var aggregate in aggregates)
            foreach (var version in aggregate.Versions)
            {
                var source = $"{aggregate.Kind}:{aggregate.Id}@{version.Version}";
                using var document = JsonDocument.Parse(version.Json);
                Walk(document.RootElement, node =>
                {
                    if (IsRetention(node))
                    {
                        var retention = Canonical.Read<RetentionRecord>(node.GetRawText());
                        RetentionRules.RequireObject(retention.Object);
                        if (retention.Status == "Deleted")
                            RetentionRules.RequireDeletionReceipt(retention, retention.Deletion ??
                                throw new DomainException("audit_deletion_receipt", "Geloeschter Eintrag ohne gebundenen SQL-Loeschbeleg.", 409));
                        if (retention.Object.DataClass == RetentionRules.Raw)
                            exclusions.Add(new(source, RetentionRules.Raw,
                                "Nur versionierte Aufbewahrungs-/Hold-/Loeschmetadaten. Unreferenzierte Rohquarantaene-Inhalte sind kein regulatorischer Leseexport; physische Journale und Backups werden hier nicht erneut attestiert."));
                        else Add(retention.Object.Hash, source);
                    }
                    else if (node.TryGetProperty("objectKey", out _))
                    {
                        if (node.TryGetProperty("contract", out var contract) && contract.GetString() == RetentionRules.ObjectContract)
                        {
                            var binding = Canonical.Read<RetentionObjectBinding>(node.GetRawText());
                            RetentionRules.RequireObject(binding);
                            Guard.Against(binding.DataClass == RetentionRules.Raw, "audit_raw_reference",
                                "Eigenstaendige Rohdatenbindung ausserhalb ihrer Aufbewahrungsmetadaten; keine pauschale Rohdaten-Ausnahme.", 409);
                            Add(binding.Hash, source);
                        }
                        else
                        {
                            var evidence = Canonical.Read<Evidence>(node.GetRawText());
                            EvidenceRules.RequireIntegrity(evidence);
                            Add(evidence.ObjectKey, source);
                        }
                    }
                    if (node.TryGetProperty("xmlHash", out var hash))
                    {
                        Guard.Against(!node.TryGetProperty("xml", out var xml) ||
                            Canonical.HashText(xml.GetString()!) != hash.GetString(),
                            "audit_mir_binding", "MIR-Originaltext und gebundener Hash weichen ab.", 409);
                        Add(hash.GetString()!, source);
                    }
                    if ((node.TryGetProperty("uri", out _) || node.TryGetProperty("evidenceId", out _)) &&
                        node.TryGetProperty("hash", out hash)) Add(hash.GetString()!, source);
                    if (node.TryGetProperty("attachmentHashes", out var attachments))
                        foreach (var attachment in attachments.EnumerateArray()) Add(attachment.GetString()!, source);
                }, skipRetentionObjects: true);
            }
        return (references.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new AuditObjectReference(p.Key, p.Value.Order(StringComparer.Ordinal).ToArray())).ToArray(),
            exclusions.ToArray());

        void Add(string hash, string source)
        {
            Guard.Against(hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)) ||
                hash != hash.ToLowerInvariant(), "audit_object_reference", "Kein gueltiger gebundener Objekt-Hash.", 409);
            if (!references.TryGetValue(hash, out var uses)) references.Add(hash, uses = new(StringComparer.Ordinal));
            uses.Add(source);
        }
    }

    public static VersionReference[] Sources(string json)
    {
        var references = new HashSet<VersionReference>();
        using var document = JsonDocument.Parse(json);
        Walk(document.RootElement, node =>
        {
            if (node.EnumerateObject().Count() == 3 && node.TryGetProperty("id", out _) &&
                node.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number &&
                node.TryGetProperty("hash", out _))
                references.Add(Canonical.Read<VersionReference>(node.GetRawText()));
        });
        return references.ToArray();
    }

    private static bool IsRetention(JsonElement node) => node.TryGetProperty("contract", out var contract) &&
        contract.ValueKind == JsonValueKind.String && contract.GetString() == "cohors-retention-record-1";

    private static void Walk(JsonElement node, Action<JsonElement> visit, bool skipRetentionObjects = false, int depth = 0)
    {
        Guard.Against(depth > 64, "audit_depth", "Verschachtelter Originalinhalt ueberschreitet die endliche Exportgrenze.", 409);
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray()) Walk(item, visit, skipRetentionObjects, depth + 1);
        }
        else if (node.ValueKind == JsonValueKind.Object)
        {
            visit(node);
            var retention = skipRetentionObjects && IsRetention(node);
            foreach (var property in node.EnumerateObject())
            {
                if (retention && property.Name is "object" or "deletion") continue;
                if (property.Value.ValueKind == JsonValueKind.String && JsonFields.Contains(property.Name) &&
                    !string.IsNullOrEmpty(property.Value.GetString()))
                {
                    try
                    {
                        using var embedded = JsonDocument.Parse(property.Value.GetString()!);
                        Walk(embedded.RootElement, visit, skipRetentionObjects, depth + 1);
                    }
                    catch (JsonException ex)
                    {
                        throw new DomainException("audit_embedded_json", "Gespeicherter strukturierter Originalinhalt ist kein gueltiges JSON.", 409, ex);
                    }
                }
                else Walk(property.Value, visit, skipRetentionObjects, depth + 1);
            }
        }
    }
}
