using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1BlobCheckpointProfile(string Contract, FabricC1CheckpointAllocation Allocation,
    FabricC1CheckpointSeedReceipt SeedReceipt);
public sealed record FabricC1InvocationAllowance(string Contract, string ProfileSha256, FabricC1Invocation Command,
    FabricC1CheckpointReservation Reservation);

public static class FabricC1CheckpointProfiles
{
    public const string Contract = "cohors-fabric-c1-checkpoint-profile-1";
    public const string AllowanceContract = "cohors-fabric-c1-invocation-allowance-1";

    public static FabricC1BlobCheckpointBinding Validate(FabricC1ExecutionSettings execution,
        FabricC1BlobCheckpointProfile profile, string runtime, DateTimeOffset now)
    {
        var allocation = profile.Allocation ??
            throw new DomainException("c1_checkpoint_profile", "An original allocation is required.", 409);
        var binding = allocation.Binding ??
            throw new DomainException("c1_checkpoint_profile", "An original storage binding is required.", 409);
        var endpoint = FabricC1BlobCheckpointStore.Endpoint(execution, binding, now);
        var seed = allocation.Seed ??
            throw new DomainException("c1_checkpoint_profile", "An original seed is required.", 409);
        var checkpoint = seed.Checkpoint ??
            throw new DomainException("c1_checkpoint_profile", "An original checkpoint clock is required.", 409);
        var receipt = profile.SeedReceipt ??
            throw new DomainException("c1_checkpoint_profile", "A confirmed original seed receipt is required.", 409);
        var node = JsonSerializer.SerializeToNode(allocation, Canonical.Json)!.AsObject();
        var originalAllocation = new FabricC1ProvisionedObject("CheckpointIoAllocation", endpoint.AbsoluteUri, Canonical.Hash(node), node);
        Guard.Against(profile.Contract != Contract || allocation.Contract != FabricC1Provisioner.CheckpointAllocationContract ||
            binding.Runtime != runtime || seed.Contract != FabricC1BlobCheckpointStore.DocumentContract ||
            seed.StorageBindingHash != Canonical.Hash(binding) || seed.Reservations is not { Length: 0 } ||
            checkpoint.BindingHash != binding.ExecutionBindingHash || checkpoint.Runtime != runtime ||
            checkpoint.StartedAtUtc > now || checkpoint.DeadlineUtc <= checkpoint.StartedAtUtc ||
            checkpoint.DeadlineUtc > execution.ExpiresAtUtc ||
            checkpoint.DeadlineUtc - checkpoint.StartedAtUtc > TimeSpan.FromSeconds(execution.Preparation.Limits.MaxDurationSeconds!.Value) ||
            Canonical.Hash(checkpoint) != Canonical.Hash(new FabricC1Checkpoint(binding.ExecutionBindingHash, runtime,
                checkpoint.StartedAtUtc, checkpoint.DeadlineUtc)) ||
            allocation.IoDeadlineUtc != checkpoint.DeadlineUtc.AddSeconds(execution.Preparation.Limits.CleanupTimeoutSeconds!.Value) ||
            now >= allocation.IoDeadlineUtc ||
            receipt.Endpoint != endpoint.AbsoluteUri || receipt.AllocationReceiptSha256 != Canonical.Hash(originalAllocation) ||
            receipt.SeedSha256 != Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(seed, Canonical.Json))) ||
            !Guid.TryParseExact(receipt.CreationId, "D", out var creation) || creation == Guid.Empty || receipt.CreationId != creation.ToString("D") ||
            receipt.ObservedAtUtc < checkpoint.StartedAtUtc || receipt.ObservedAtUtc >= checkpoint.DeadlineUtc || receipt.ObservedAtUtc > now ||
            !EntityTagHeaderValue.TryParse(receipt.ETag, out var etag) || etag.IsWeak || etag.Tag == "*",
            "c1_checkpoint_profile", "Storage, original clock/allocation and seed receipt must agree; a profile cannot reset them.", 409);
        return binding;
    }

    public static FabricC1Invocation ValidateAllowance(FabricC1ExecutionSettings execution, FabricC1BlobCheckpointProfile profile,
        FabricC1InvocationAllowance allowance, string runtime, string operation, string? probe, DateTimeOffset now)
    {
        var binding = Validate(execution, profile, runtime, now);
        var command = allowance.Command ??
            throw new DomainException("c1_checkpoint_allowance", "The original bound command is required.", 409);
        Guard.Against(runtime is not ("direct" or "hosted-control") || allowance.Contract != AllowanceContract ||
            allowance.ProfileSha256 != Canonical.Hash(profile) || command.Operation != operation || command.ProbeId != probe ||
            runtime == "direct" && command.HostedCheckpointReservation is not null ||
            runtime == "hosted-control" && command.HostedCheckpointReservation is null,
            "c1_checkpoint_allowance", "Invocation allowance cannot move to another profile, command or runtime.", 409);
        ValidateCommand(execution, command);
        ValidateClaim(execution, profile, binding, command, allowance.Reservation, now);
        return command;
    }

    public static FabricC1CheckpointReservation HostedReservation(FabricC1ExecutionSettings execution,
        FabricC1BlobCheckpointProfile profile, FabricC1Invocation command, DateTimeOffset now)
    {
        var binding = Validate(execution, profile, "hosted", now);
        ValidateCommand(execution, command);
        var reservation = command.HostedCheckpointReservation ??
            throw new DomainException("c1_checkpoint_allowance", "Every Hosted invocation needs its own previously debited reservation.", 409);
        ValidateClaim(execution, profile, binding, command, reservation, now);
        return reservation;
    }

    private static void ValidateCommand(FabricC1ExecutionSettings execution, FabricC1Invocation command) =>
        Guard.Against(command.BindingHash != Canonical.Hash(execution) ||
            !Guid.TryParseExact(command.InvocationId, "D", out var id) || id == Guid.Empty || command.InvocationId != id.ToString("D") ||
            command.Operation is not ("discover" or "query" or "task-start" or "resume" or "cancel" or "close" or "status") ||
            (command.Operation is "query" or "task-start"
                ? string.IsNullOrWhiteSpace(command.ProbeId) || command.ProbeId.Length > 128 : command.ProbeId is not null),
            "c1_checkpoint_allowance", "A canonical invocation identity and supported original operation are required.", 409);

    private static void ValidateClaim(FabricC1ExecutionSettings execution, FabricC1BlobCheckpointProfile profile,
        FabricC1BlobCheckpointBinding binding, FabricC1Invocation command, FabricC1CheckpointReservation? reservation, DateTimeOffset now)
    {
        Guard.Against(reservation is null || reservation.Id != command.InvocationId ||
            reservation.ExpiresAtUtc > profile.Allocation.IoDeadlineUtc, "c1_checkpoint_allowance",
            "A claim is single-use for its original invocation and cannot extend the allocation deadline.", 409);
        FabricC1BlobCheckpointStore.ValidateReservation(execution, binding, reservation!, now);
    }
}

public sealed partial class FabricC1Provisioner
{
    public FabricC1BlobCheckpointProfile ExportBlobCheckpointProfile(FabricC1ExecutionSettings execution,
        FabricC1BlobCheckpointBinding binding)
    {
        var endpoint = ValidateCheckpointDeployment(execution, binding);
        PlanNormalRequests(0);
        var allocationKey = AllocationPrefix + binding.Runtime;
        var seedKey = SeedPrefix + binding.Runtime;
        Guard.Against(!state.Objects.TryGetValue(allocationKey, out var allocated) ||
            !state.Objects.TryGetValue(seedKey, out _) || state.PendingCreates.ContainsKey(seedKey),
            "c1_deploy_checkpoint_profile", "The original allocation and confirmed seed readback must precede profile export.", 409);
        var allocation = CheckpointAllocation(allocated!, allocationKey);
        var known = state.Objects[seedKey];
        state.Objects.TryGetValue(seedKey + "-intent", out var intent);
        Guard.Against(Canonical.Hash(allocation.Binding) != Canonical.Hash(binding) ||
            known.Kind != "CheckpointSeedReadback" || known.Id != endpoint.AbsoluteUri ||
            intent is null ||
            intent.Kind != "CheckpointSeedIntent" || intent.Id != endpoint.AbsoluteUri ||
            intent.RequestHash != Canonical.Hash(intent.Response) || known.RequestHash != intent.RequestHash,
            "c1_deploy_checkpoint_profile", "The original seed/allocation cannot be substituted during export.", 409);
        var receipt = known.Response.Deserialize<FabricC1CheckpointSeedReceipt>(Canonical.Json) ??
            throw new DomainException("c1_deploy_checkpoint_profile", "Seed receipt is missing.", 409);
        Guard.Against(receipt.CreationId != intent!.Response["creationId"]?.GetValue<string>() ||
            receipt.AllocationReceiptSha256 != intent.Response["allocationReceiptSha256"]?.GetValue<string>() ||
            receipt.SeedSha256 != intent.Response["seedSha256"]?.GetValue<string>(),
            "c1_deploy_checkpoint_profile", "Seed receipt and original create intent differ.", 409);
        var profile = new FabricC1BlobCheckpointProfile(FabricC1CheckpointProfiles.Contract, allocation, receipt);
        FabricC1CheckpointProfiles.Validate(execution, profile, binding.Runtime, time.GetUtcNow());
        return profile;
    }

    public FabricC1InvocationAllowance ExportBlobCheckpointAllowance(FabricC1ExecutionSettings execution,
        FabricC1BlobCheckpointBinding binding, FabricC1Invocation command, FabricC1CheckpointReservation reservation,
        FabricC1BlobCheckpointBinding? hostedBinding = null)
    {
        var profile = ExportBlobCheckpointProfile(execution, binding);
        var allowance = new FabricC1InvocationAllowance(FabricC1CheckpointProfiles.AllowanceContract,
            Canonical.Hash(profile), command, reservation);
        FabricC1CheckpointProfiles.ValidateAllowance(execution, profile, allowance, binding.Runtime,
            command.Operation, command.ProbeId, time.GetUtcNow());
        RequireOriginalClaim(binding, reservation, profile.SeedReceipt.AllocationReceiptSha256);
        if (binding.Runtime == "hosted-control")
        {
            Guard.Against(hostedBinding is null || hostedBinding.Runtime != "hosted",
                "c1_deploy_checkpoint_profile", "Hosted control requires the separately bound Hosted allocation.", 409);
            var hosted = ExportBlobCheckpointProfile(execution, hostedBinding!);
            var remote = FabricC1CheckpointProfiles.HostedReservation(execution, hosted, command, time.GetUtcNow());
            RequireOriginalClaim(hostedBinding!, remote, hosted.SeedReceipt.AllocationReceiptSha256);
        }
        else Guard.Against(hostedBinding is not null, "c1_deploy_checkpoint_profile",
            "Direct invocations do not grant a Hosted reservation.", 409);
        var key = "checkpoint-invocation-" + binding.Runtime + "-" + command.InvocationId;
        var body = JsonSerializer.SerializeToNode(allowance, Canonical.Json)!.AsObject();
        var hash = Canonical.Hash(body);
        if (state.Objects.TryGetValue(key, out var known))
            Guard.Against(known.Kind != "CheckpointInvocation" || known.Id != command.InvocationId || known.RequestHash != hash ||
                Canonical.Hash(known.Response) != hash, "c1_deploy_checkpoint_drift",
                "An issued invocation cannot change its operation, profile or local/remote claims.", 409);
        else SaveCheckpointJournal(state with
        {
            Objects = new(state.Objects, StringComparer.Ordinal)
            { [key] = new("CheckpointInvocation", command.InvocationId!, hash, body) }
        });
        return allowance;
    }

    private void RequireOriginalClaim(FabricC1BlobCheckpointBinding binding, FabricC1CheckpointReservation reservation, string allocationHash)
    {
        Guard.Against(!state.Objects.TryGetValue(ReservationPrefix + binding.Runtime + "-" + reservation.Id, out var original),
            "c1_deploy_checkpoint_reservation", "The invocation cannot invent its upstream reservation debit.", 409);
        VerifyCheckpointReservation(original!, allocationHash);
        Guard.Against(original!.Id != reservation.Id || reservation.JournalReceiptSha256 != Canonical.Hash(original) ||
            reservation.Requests != original!.Response["requests"]?.GetValue<int>() ||
            reservation.ExpiresAtUtc != original.Response["expiresAtUtc"]?.GetValue<DateTimeOffset>(),
            "c1_deploy_checkpoint_reservation", "The claim differs from its original recorded debit.", 409);
    }
}
