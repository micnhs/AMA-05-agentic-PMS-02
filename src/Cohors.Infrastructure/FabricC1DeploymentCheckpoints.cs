using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1CheckpointAllocation(string Contract, FabricC1BlobCheckpointBinding Binding,
    FabricC1BlobCheckpointDocument Seed, DateTimeOffset IoDeadlineUtc);

public sealed partial class FabricC1Provisioner
{
    private const string CheckpointAllocationContract = "cohors-fabric-c1-checkpoint-allocation-1";
    private const string AllocationPrefix = "checkpoint-allocation-";
    private const string ReservationPrefix = "checkpoint-reservation-";
    private bool checkpointJournalUncertain;

    public FabricC1CheckpointAllocation PrepareBlobCheckpoint(FabricC1ExecutionSettings execution,
        FabricC1BlobCheckpointBinding binding)
    {
        var endpoint = ValidateCheckpointDeployment(execution, binding);
        var originals = state.Objects.Where(pair => pair.Key.StartsWith(AllocationPrefix, StringComparison.Ordinal))
            .Select(pair => CheckpointAllocation(pair.Value, pair.Key)).ToArray();
        Guard.Against(originals.Any(original => original.Binding.ExecutionBindingHash != binding.ExecutionBindingHash),
            "c1_deploy_checkpoint_drift", "All runtime allocations belong to one original C1 execution.", 409);
        var key = AllocationPrefix + binding.Runtime;
        if (state.Objects.TryGetValue(key, out var existing))
        {
            PlanNormalRequests(0);
            var original = CheckpointAllocation(existing, key);
            Guard.Against(existing.Id != endpoint.AbsoluteUri || Canonical.Hash(original.Binding) != Canonical.Hash(binding) ||
                time.GetUtcNow() >= original.IoDeadlineUtc, "c1_deploy_checkpoint_drift",
                "The original storage allocation, seed and deadline cannot be replaced.", 409);
            return original;
        }

        var now = time.GetUtcNow();
        var (next, controlDeadline) = PlanNormalRequests(0, binding.AllocatedRequests);
        var cleanup = TimeSpan.FromSeconds(execution.Preparation.Limits.CleanupTimeoutSeconds!.Value);
        var started = originals.FirstOrDefault()?.Seed.Checkpoint.StartedAtUtc ?? now;
        var deadline = now.AddSeconds(execution.Preparation.Limits.MaxDurationSeconds!.Value);
        if (execution.ExpiresAtUtc < deadline) deadline = execution.ExpiresAtUtc;
        if (controlDeadline - cleanup < deadline) deadline = controlDeadline - cleanup;
        if (originals.FirstOrDefault() is { } first) deadline = first.Seed.Checkpoint.DeadlineUtc;
        Guard.Against(deadline <= now, "c1_deploy_checkpoint_deadline",
            "The original control window has no room for this run and its checkpoint release.", 409);
        var seed = new FabricC1BlobCheckpointDocument(FabricC1BlobCheckpointStore.DocumentContract,
            Canonical.Hash(binding), new(binding.ExecutionBindingHash, binding.Runtime, started, deadline), []);
        var allocation = new FabricC1CheckpointAllocation(CheckpointAllocationContract, binding, seed, deadline + cleanup);
        var response = JsonSerializer.SerializeToNode(allocation, Canonical.Json)!.AsObject();
        var receipt = new FabricC1ProvisionedObject("CheckpointIoAllocation", endpoint.AbsoluteUri,
            Canonical.Hash(response), response);
        next = next with { Objects = new(next.Objects, StringComparer.Ordinal) { [key] = receipt } };
        SaveCheckpointJournal(next);
        Guard.Against(time.GetUtcNow() >= allocation.IoDeadlineUtc, "c1_deploy_checkpoint_deadline",
            "Allocation expired while persisting; it remains charged and cannot be restarted.", 409);
        return allocation;
    }

    public FabricC1CheckpointReservation ReserveBlobCheckpoint(FabricC1ExecutionSettings execution,
        FabricC1BlobCheckpointBinding binding, string reservationId, int requests, DateTimeOffset expiresAtUtc)
    {
        var endpoint = ValidateCheckpointDeployment(execution, binding);
        PlanNormalRequests(0);
        Guard.Against(!state.Objects.TryGetValue(AllocationPrefix + binding.Runtime, out var allocated),
            "c1_deploy_checkpoint_allocation", "The original I/O allocation and seed must already be durably recorded.", 409);
        var allocation = CheckpointAllocation(allocated!, AllocationPrefix + binding.Runtime);
        Guard.Against(allocated!.Id != endpoint.AbsoluteUri || Canonical.Hash(allocation.Binding) != Canonical.Hash(binding) ||
            !Guid.TryParseExact(reservationId, "D", out var id) || id == Guid.Empty || reservationId != id.ToString("D") ||
            requests < 4 || requests > binding.AllocatedRequests || expiresAtUtc <= time.GetUtcNow() ||
            expiresAtUtc > allocation.IoDeadlineUtc, "c1_deploy_checkpoint_reservation",
            "A canonical one-use reservation must fit the original allocation and deadline.", 409);
        var prefix = ReservationPrefix + binding.Runtime + "-";
        var key = prefix + reservationId;
        var response = new JsonObject
        {
            ["allocationReceiptSha256"] = Canonical.Hash(allocated!), ["id"] = reservationId,
            ["requests"] = requests, ["expiresAtUtc"] = expiresAtUtc
        };
        var hash = Canonical.Hash(response);
        if (state.Objects.TryGetValue(key, out var known))
        {
            VerifyCheckpointReservation(known, Canonical.Hash(allocated!));
            Guard.Against(known.RequestHash != hash, "c1_deploy_checkpoint_drift",
                "An issued reservation cannot change; retrieving it is not permission to replay its consumer.", 409);
            return new(reservationId, Canonical.Hash(known), requests, expiresAtUtc);
        }
        var previous = state.Objects.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        var consumed = previous.Sum(pair =>
        {
            VerifyCheckpointReservation(pair.Value, Canonical.Hash(allocated!));
            Guard.Against(pair.Key != prefix + pair.Value.Id, "c1_deploy_checkpoint_reservation",
                "Reservation key and original identity differ.", 409);
            return pair.Value.Response["requests"]!.GetValue<int>();
        });
        Guard.Against(consumed + requests > binding.AllocatedRequests, "c1_deploy_checkpoint_reservation",
            "Unused or uncertain reservations are not refunded; the original allocation is exhausted.", 409);
        var receipt = new FabricC1ProvisionedObject("CheckpointIoReservation", reservationId, hash, response);
        var next = state with { Objects = new(state.Objects, StringComparer.Ordinal) { [key] = receipt } };
        SaveCheckpointJournal(next);
        Guard.Against(time.GetUtcNow() >= expiresAtUtc, "c1_deploy_checkpoint_deadline",
            "The reservation expired while persisting and remains charged.", 409);
        return new(reservationId, Canonical.Hash(receipt), requests, expiresAtUtc);
    }

    private Uri ValidateCheckpointDeployment(FabricC1ExecutionSettings execution, FabricC1BlobCheckpointBinding binding)
    {
        var endpoint = FabricC1BlobCheckpointStore.Endpoint(execution, binding, time.GetUtcNow());
        Guard.Against(binding.DeploymentBindingHash != state.BindingHash || execution.SourceCommit != input.SourceCommit ||
            execution.ExpiresAtUtc > input.ExpiresAtUtc || binding.BudgetAllocationSha256 != input.Reviews?.BudgetPlanSha256,
            "c1_deploy_checkpoint_binding", "Checkpoint source, budget review and deployment must match the original manifest.", 409);
        SameId(binding.Identity.TenantId!, input.TenantId!);
        var role = binding.Runtime == "hosted-control" ? "hosted-control" : "runtime";
        var principal = Owned(role + "-sp", "ServicePrincipal");
        var app = Owned(role + "-app", "Application");
        RequireMarker(principal, true);
        RequireMarker(app, true);
        SameId(binding.Identity.PrincipalObjectId!, Text(principal, "id"));
        SameId(binding.Identity.ClientId!, Text(principal, "appId"));
        SameId(binding.Identity.ClientId!, Text(app, "appId"));
        Guard.Against(GuidValue(binding.Identity.PrincipalObjectId!) == GuidValue(input.BootstrapIdentity!.PrincipalObjectId!) ||
            GuidValue(binding.Identity.ClientId!) == GuidValue(input.BootstrapIdentity.ClientId!), "c1_deploy_checkpoint_identity",
            "The initial deployer cannot become a runtime checkpoint caller.", 409);
        var foundation = Owned("arm-foundation", "ArmDeployment");
        Guard.Against(foundation["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded",
            "c1_deploy_checkpoint_storage", "A completed original foundation output is required.", 409);
        var storage = foundation["properties"]?["outputs"]?["storage"]?["value"] as JsonObject ??
            throw new DomainException("c1_deploy_checkpoint_storage", "Original storage output is missing.", 409);
        RequireArmOwnership(storage);
        Guard.Against(!SameArmId(RequireTypedArmResource(storage, "Microsoft.Storage/storageAccounts"), binding.StorageAccountResourceId),
            "c1_deploy_checkpoint_storage", "Checkpoint storage is not the own original account.", 409);
        return endpoint;
    }

    private FabricC1CheckpointAllocation CheckpointAllocation(FabricC1ProvisionedObject receipt, string key)
    {
        Guard.Against(receipt.Kind != "CheckpointIoAllocation" || receipt.RequestHash != Canonical.Hash(receipt.Response),
            "c1_deploy_checkpoint_allocation", "A persisted checkpoint allocation is inconsistent.", 409);
        var allocation = receipt.Response.Deserialize<FabricC1CheckpointAllocation>(Canonical.Json) ??
            throw new DomainException("c1_deploy_checkpoint_allocation", "Checkpoint allocation is missing.", 409);
        Guard.Against(allocation.Contract != CheckpointAllocationContract || allocation.Binding is null || allocation.Seed is null ||
            allocation.Binding.Contract != FabricC1BlobCheckpointStore.Contract ||
            allocation.Binding.DeploymentBindingHash != state.BindingHash ||
            allocation.Binding.Runtime is not ("direct" or "hosted" or "hosted-control") ||
            key != AllocationPrefix + allocation.Binding.Runtime ||
            allocation.Binding.AllocatedRequests is < 4 or > 1000 ||
            allocation.Seed.Contract != FabricC1BlobCheckpointStore.DocumentContract ||
            allocation.Seed.Reservations is not { Length: 0 } ||
            allocation.Seed.StorageBindingHash != Canonical.Hash(allocation.Binding) ||
            allocation.Seed.Checkpoint is null || allocation.Seed.Checkpoint.BindingHash != allocation.Binding.ExecutionBindingHash ||
            allocation.Seed.Checkpoint.Runtime != allocation.Binding.Runtime ||
            allocation.Seed.Checkpoint.StartedAtUtc < state.StartedAtUtc ||
            allocation.Seed.Checkpoint.DeadlineUtc <= allocation.Seed.Checkpoint.StartedAtUtc ||
            Canonical.Hash(allocation.Seed.Checkpoint) != Canonical.Hash(new FabricC1Checkpoint(
                allocation.Binding.ExecutionBindingHash, allocation.Binding.Runtime,
                allocation.Seed.Checkpoint.StartedAtUtc, allocation.Seed.Checkpoint.DeadlineUtc)) ||
            allocation.IoDeadlineUtc < allocation.Seed.Checkpoint.DeadlineUtc,
            "c1_deploy_checkpoint_allocation", "Original checkpoint allocation or seed is malformed.", 409);
        return allocation;
    }

    private static void VerifyCheckpointReservation(FabricC1ProvisionedObject receipt, string allocationHash) =>
        Guard.Against(receipt.Kind != "CheckpointIoReservation" || receipt.RequestHash != Canonical.Hash(receipt.Response) ||
            receipt.Response["allocationReceiptSha256"]?.GetValue<string>() != allocationHash ||
            receipt.Response["id"]?.GetValue<string>() != receipt.Id ||
            receipt.Response["requests"]?.GetValue<int>() is not (>= 4 and <= 1000),
            "c1_deploy_checkpoint_reservation", "A persisted checkpoint reservation is inconsistent.", 409);

    private int PrepaidCheckpointRequests() => state.Objects
        .Where(pair => pair.Key.StartsWith(AllocationPrefix, StringComparison.Ordinal))
        .Sum(pair => CheckpointAllocation(pair.Value, pair.Key).Binding.AllocatedRequests);

    private (FabricC1DeploymentJournal Next, DateTimeOffset Deadline) PlanNormalRequests(int requests, int prepay = 0)
    {
        RequireCertainCheckpointJournal();
        Guard.Against(!FabricC1Deployment.ValidCleanupLimits(input), "c1_deploy_cleanup_limits",
            "The original cleanup reserve is required before allocating control requests.", 409);
        Guard.Against(state.CleanupStartedAtUtc is not null, "c1_deploy_cleanup_started",
            "Cleanup has fenced this run; no new provisioning or reservations.", 409);
        var now = time.GetUtcNow();
        var started = state.StartedAtUtc ?? now;
        var deadline = started.AddSeconds(input.MaxControlDurationSeconds!.Value);
        if (input.ExpiresAtUtc!.Value < deadline) deadline = input.ExpiresAtUtc.Value;
        Guard.Against(requests < 0 || prepay < 0 ||
            (long)state.Requests + PrepaidCheckpointRequests() + requests + prepay >
                input.MaxControlRequests - input.Cleanup!.ReservedRequests || now < started || now >= deadline,
            "c1_deploy_budget", "Original normal budget/deadline exhausted; prepaid I/O and cleanup cannot be borrowed or reset.", 409);
        return (state with { Requests = state.Requests + requests, StartedAtUtc = started }, deadline);
    }

    private void SaveCheckpointJournal(FabricC1DeploymentJournal next)
    {
        try { SaveJournal(next); }
        catch { checkpointJournalUncertain = true; throw; }
        state = next;
    }

    private void RequireCertainCheckpointJournal() =>
        Guard.Against(checkpointJournalUncertain, "c1_deploy_checkpoint_journal_unknown",
            "The local checkpoint debit may have persisted; reopen its original exclusive journal before any more work.", 409);

    private void SaveJournal(FabricC1DeploymentJournal next)
    {
        RequireCertainCheckpointJournal();
        journal.Save(next);
    }
}
