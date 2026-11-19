using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed class AuditCommandReceiptTests
{
    private const string Result = """{"id":"synthetic-audit","productId":"ortho","version":2}""";
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-16T15:00:00.1234567+00:00");

    [Theory]
    [InlineData("case", "DeadlineEscalated")]
    [InlineData("psurReview", "PsurQuestionEscalated")]
    [InlineData("eudamedDelivery", "EudamedCorrespondenceEscalated")]
    public void RestrictedReceiptsBindTheirExistingWriterContract(string kind, string action)
    {
        var (entry, receipt) = Restricted(kind, action);
        Check(kind, entry, receipt);
    }

    [Theory]
    [InlineData("wrong-role")]
    [InlineData("additional-role")]
    [InlineData("missing-role")]
    [InlineData("actor-name-case")]
    [InlineData("actor-name-space")]
    [InlineData("uppercase-hex")]
    [InlineData("short-hex")]
    [InlineData("wrong-prefix")]
    [InlineData("prefix-case")]
    [InlineData("trailing-newline")]
    public void ReservedActorCannotFallBackToAMatchingGenericReceipt(string fault)
    {
        var (entry, _) = Restricted("case", "DeadlineEscalated");
        entry = fault switch
        {
            "wrong-role" => entry with { Roles = [Roles.Pms] },
            "additional-role" => entry with { Roles = [Roles.Workflow, Roles.Pms] },
            "missing-role" => entry with { Roles = [] },
            "actor-name-case" => entry with { ActorName = "cohors operations" },
            "actor-name-space" => entry with { ActorName = "Cohors operations " },
            "uppercase-hex" => entry with { CorrelationId = "operations-" + entry.CorrelationId[11..].ToUpperInvariant() },
            "short-hex" => entry with { CorrelationId = entry.CorrelationId[..^1] },
            "wrong-prefix" => entry with { CorrelationId = "operationx-" + entry.CorrelationId[11..] },
            "prefix-case" => entry with { CorrelationId = "Operations-" + entry.CorrelationId[11..] },
            "trailing-newline" => entry with { CorrelationId = entry.CorrelationId + "\n" },
            _ => throw new InvalidOperationException("Unknown synthetic metadata fault.")
        };
        Rejected(() => Check("case", entry, Generic("case", entry)));
    }

    [Theory]
    [InlineData("case", "PsurQuestionEscalated")]
    [InlineData("psurReview", "DeadlineEscalated")]
    [InlineData("eudamedDelivery", "DeadlineEscalated")]
    [InlineData("operations", "WatchdogHeartbeat")]
    [InlineData("Case", "DeadlineEscalated")]
    [InlineData("case", "deadlineEscalated")]
    public void ReservedActorRejectsOtherKindActionPairs(string kind, string action)
    {
        var (entry, _) = Restricted(kind, action);
        Rejected(() => Check(kind, entry, Generic(kind, entry)));
    }

    [Fact]
    public void ReceiptSelectionIsUniqueAndBijective()
    {
        var (entry, receipt) = Restricted("case", "DeadlineEscalated");
        Rejected(() => Check("case", entry));
        Rejected(() => Check("case", entry, receipt, receipt));
        var unused = receipt.DeepClone().AsObject();
        unused["IdempotencyKey"] = Canonical.HashText("synthetic unused receipt");
        Rejected(() => Check("case", entry, receipt, unused));
        Rejected(() => SqlStateStore.RequireAuditCommandReceipts(
            [Aggregate("case", entry), Aggregate("case", entry)], [Row(receipt), Row(unused)]));
    }

    [Theory]
    [InlineData("request-hash")]
    [InlineData("key")]
    [InlineData("actor")]
    [InlineData("result")]
    [InlineData("instant")]
    [InlineData("offset")]
    public void RestrictedReceiptFieldsCannotBeSubstituted(string fault)
    {
        var (entry, receipt) = Restricted("case", "DeadlineEscalated");
        switch (fault)
        {
            case "request-hash": receipt["RequestHash"] = Canonical.HashText("synthetic altered request"); break;
            case "key": receipt["IdempotencyKey"] = Canonical.HashText("synthetic altered key"); break;
            case "actor": receipt["ActorId"] = "synthetic-other-actor"; break;
            case "result": receipt["Result"] = "{}"; break;
            case "instant": receipt["At"] = JsonValue.Create(entry.At.AddTicks(1)); break;
            case "offset": receipt["At"] = JsonValue.Create(entry.At.ToOffset(TimeSpan.FromHours(1))); break;
            default: throw new InvalidOperationException("Unknown synthetic receipt fault.");
        }
        Rejected(() => Check("case", entry, receipt));
    }

    [Theory]
    [InlineData("uppercase")]
    [InlineData("short")]
    [InlineData("non-hex")]
    public void CorrespondenceRequestHashStillRequiresCanonicalLowercaseHex(string fault)
    {
        var (entry, receipt) = Restricted("eudamedDelivery", "EudamedCorrespondenceEscalated");
        var request = fault switch
        {
            "uppercase" => new string('A', 64),
            "short" => new string('a', 63),
            "non-hex" => new string('g', 64),
            _ => throw new InvalidOperationException("Unknown synthetic request-hash fault.")
        };
        entry = RebindRequest(entry, receipt, request);
        Rejected(() => Check("eudamedDelivery", entry, receipt));
    }

    [Theory]
    [InlineData("case", "DeadlineEscalated")]
    [InlineData("psurReview", "PsurQuestionEscalated")]
    public void ReboundKeysCannotHideDifferentRequestInputsOrNoncanonicalEmptyDependencies(string kind, string action)
    {
        foreach (var fault in new[] { "kind", "id", "version", "payload", "dependencies" })
        {
            var (entry, receipt) = Restricted(kind, action);
            var input = fault switch
            {
                "kind" => $"other:{entry.AggregateId}:1:{entry.PayloadHash}:[]",
                "id" => $"{kind}:other:1:{entry.PayloadHash}:[]",
                "version" => $"{kind}:{entry.AggregateId}:2:{entry.PayloadHash}:[]",
                "payload" => $"{kind}:{entry.AggregateId}:1:{new string('0', 64)}:[]",
                "dependencies" => $"{kind}:{entry.AggregateId}:1:{entry.PayloadHash}:[ ]",
                _ => throw new InvalidOperationException("Unknown synthetic request-input fault.")
            };
            entry = RebindRequest(entry, receipt, Canonical.HashText(input));
            Rejected(() => Check(kind, entry, receipt));
        }
    }

    [Theory]
    [InlineData("cohors-workflow", "operations-not-a-hash")]
    [InlineData("COHORS-OPERATIONS", "operations-not-a-hash")]
    [InlineData("synthetic-generic-actor", "operations-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void OtherActorsRemainGenericRegardlessOfOperationsCorrelationPrefix(string actor, string correlation)
    {
        var (entry, _) = Restricted("case", "DeadlineEscalated");
        entry = entry with { ActorId = actor, CorrelationId = correlation };
        Check("case", entry, Generic("case", entry));
    }

    private static (AuditEvent Entry, JsonObject Receipt) Restricted(string kind, string action)
    {
        var entry = new AuditEvent(1, "synthetic-audit", 2, "cohors-operations", "Cohors operations", [Roles.Workflow],
            action, "Synthetic receipt-binding unit fixture.", At, "", Canonical.HashText(Result),
            new string('0', 64), new string('1', 64));
        var request = Canonical.HashText(kind == "eudamedDelivery"
            ? "synthetic dependency-bound request with unavailable original dependency bytes"
            : $"{kind}:{entry.AggregateId}:1:{entry.PayloadHash}:[]");
        var receipt = Receipt("", request, entry);
        return (RebindRequest(entry, receipt, request), receipt);
    }

    private static AuditEvent RebindRequest(AuditEvent entry, JsonObject receipt, string request)
    {
        var key = Canonical.HashText("cohors-operations-write-v1:" + request);
        receipt["RequestHash"] = request;
        receipt["IdempotencyKey"] = key;
        return entry with { CorrelationId = "operations-" + key };
    }

    private static JsonObject Generic(string kind, AuditEvent entry) => Receipt(
        Canonical.Hash(new { Id = entry.ActorId, Kind = kind, entry.Action, IdempotencyKey = entry.CorrelationId }),
        Canonical.HashText("synthetic generic request"), entry);

    private static JsonObject Receipt(string key, string request, AuditEvent entry) => new()
    {
        ["IdempotencyKey"] = key, ["RequestHash"] = request, ["ActorId"] = entry.ActorId,
        ["Result"] = Result, ["At"] = JsonValue.Create(entry.At)
    };

    private static AuditAggregate Aggregate(string kind, AuditEvent entry) => new(kind, entry.AggregateId, "ortho", ["ortho"],
        new(entry.AggregateId, kind, entry.Version, entry.Version, true, true, entry.Hash, [entry],
            "Synthetic unit fixture; no database or manifest-chain verification."), []);

    private static JsonElement Row(JsonObject receipt) => JsonSerializer.SerializeToElement(receipt);
    private static void Check(string kind, AuditEvent entry, params JsonObject[] receipts) =>
        SqlStateStore.RequireAuditCommandReceipts([Aggregate(kind, entry)], receipts.Select(Row).ToArray());

    private static void Rejected(Action action)
    {
        var error = Assert.Throws<DomainException>(action);
        Assert.Equal("audit_command", error.Code);
        Assert.Equal(409, error.Status);
    }
}
