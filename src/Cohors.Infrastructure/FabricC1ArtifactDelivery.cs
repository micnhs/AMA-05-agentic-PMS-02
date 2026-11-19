using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1ArtifactDeliveryResult(string Status, string CommandResourceId,
    string AllocationReceiptSha256, JsonObject? Receipt)
{
    public bool ArtifactVerified => Status == "VerifiedNotExecuted";
    public string EvidenceLevel => "run-command-result-not-c1-or-business-acceptance";
}

public sealed partial class FabricC1Provisioner
{
    private const string ArtifactCommandKey = "private-artifact-stage-command";
    private const string ArtifactCommandIntentKey = ArtifactCommandKey + "-intent";
    private const string ArtifactStageReceiptKey = "private-artifact-stage-receipt";
    private const string ArtifactCommandApi = "?api-version=2024-07-01";
    private const string ArtifactCommandView = "?api-version=2024-07-01&$expand=instanceView";

    public async Task<FabricC1ArtifactDeliveryResult> AdvancePrivateArtifactDelivery(
        FabricC1ArtifactPublicationBinding binding, string packageDirectory, int maxDurationSeconds, CancellationToken ct)
    {
        var bundle = await PreparePrivateArtifactStage(binding, packageDirectory, maxDurationSeconds, ct);
        var (_, target, _) = ReadArtifactAccess(binding, packageDirectory);
        var intent = ArtifactCommandIntent(bundle, target);
        var expiry = StageTimestamp(bundle.Allowance, "expiresAtUtc");
        RequireArtifactStageTime(expiry);
        var remaining = expiry - time.GetUtcNow();
        Guard.Against(remaining <= TimeSpan.Zero, "c1_artifact_stage_deadline", "The original artifact delivery window elapsed.", 409);
        using var timeout = new CancellationTokenSource(remaining, time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var vmUri = "https://management.azure.com" + Text(target, "vmResourceId");
        var vm = await SendArtifactCommand(HttpMethod.Get, new(vmUri + ArtifactCommandApi), expiry, bounded.Token);
        RequireStatus(vm, 200);
        VerifyArtifactAccessProbe(vm.Body, target);
        var instance = await SendArtifactCommand(HttpMethod.Get, new(vmUri + "/instanceView" + ArtifactCommandApi), expiry, bounded.Token);
        RequireStatus(instance, 200);
        VerifyArtifactGuestAgent(vm.Body, instance.Body);

        state.Objects.TryGetValue(ArtifactCommandKey, out var known);
        state.PendingCreates.TryGetValue(ArtifactCommandKey, out var pending);
        ValidateArtifactCommandInventory(intent, known, pending);
        var uri = new Uri("https://management.azure.com" + intent.Id + ArtifactCommandView);
        var current = await SendArtifactCommand(HttpMethod.Get, uri, expiry, bounded.Token);
        RequireStatus(current, 200, 404);
        if (current.Status == 404)
        {
            Guard.Against(known is not null, "c1_artifact_command_missing", "A previously observed command cannot be recreated.", 409);
            if (pending is not null) return PendingArtifactDelivery(intent);
            Pending(ArtifactCommandKey, intent.RequestHash);
            var created = await SendArtifactCommand(HttpMethod.Put, new("https://management.azure.com" + intent.Id + ArtifactCommandApi),
                expiry, bounded.Token, intent.Response["request"]!.AsObject());
            RecordDeniedRequest(ArtifactCommandKey, intent.RequestHash, created.Status);
            RequireStatus(created, 200, 201);
            RequireArtifactStageTime(expiry);
            if (created.Body.Count != 0)
            {
                VerifyArtifactCommand(created.Body, intent, known);
                SaveObject(ArtifactCommandKey, "ArtifactStageRunCommand", intent.Id, intent.RequestHash, created.Body);
                known = state.Objects[ArtifactCommandKey];
            }
            current = await SendArtifactCommand(HttpMethod.Get, uri, expiry, bounded.Token);
            RequireStatus(current, 200, 404);
            if (current.Status == 404) return PendingArtifactDelivery(intent);
        }
        else Guard.Against(known is null && pending is null, "c1_deploy_foreign", "An unjournaled Run Command is not adopted.", 409);

        VerifyArtifactCommand(current.Body, intent, known);
        var properties = current.Body["properties"]!.AsObject();
        var view = properties["instanceView"] as JsonObject;
        var execution = view?["executionState"]?.GetValue<string>();
        var provisioning = properties["provisioningState"]?.GetValue<string>();
        Guard.Against(state.Objects.ContainsKey(ArtifactStageReceiptKey) &&
            (execution != "Succeeded" || provisioning != "Succeeded"),
            "c1_deploy_drift", "A completed Run Command changed execution state.", 409);
        if (provisioning is "Failed" or "Canceled" || execution is "Failed" or "Canceled" or "TimedOut")
        {
            SaveObject(ArtifactCommandKey, "ArtifactStageRunCommand", intent.Id, intent.RequestHash, current.Body);
            throw new DomainException("c1_artifact_command_failed", "The bound Guest Agent command failed; no artifact result is accepted.", 409);
        }
        if (provisioning != "Succeeded" || execution != "Succeeded")
        {
            Guard.Against(provisioning is not ("Accepted" or "Creating" or "Updating" or "Running" or "Succeeded") ||
                execution is not (null or "Unknown" or "Pending" or "Running" or "Succeeded"),
                "c1_artifact_command_state", "Unsupported Run Command provisioning or execution state.", 409);
            SaveObject(ArtifactCommandKey, "ArtifactStageRunCommand", intent.Id, intent.RequestHash, current.Body);
            return PendingArtifactDelivery(intent);
        }

        var receipt = ValidateArtifactDeliveryResult(intent, bundle, view!);
        if (state.Objects.TryGetValue(ArtifactStageReceiptKey, out var original))
            Guard.Against(original.Kind != "PrivateArtifactStageReadback" || original.Id != intent.Id ||
                original.RequestHash != intent.RequestHash || Canonical.Hash(original.Response) != Canonical.Hash(receipt),
                "c1_deploy_drift", "The original successful artifact delivery result changed.", 409);
        SaveObject(ArtifactCommandKey, "ArtifactStageRunCommand", intent.Id, intent.RequestHash, current.Body);
        if (original is null) SaveObject(ArtifactStageReceiptKey, "PrivateArtifactStageReadback", intent.Id, intent.RequestHash, receipt);
        return new("VerifiedNotExecuted", intent.Id, Text(intent.Response, "allocationReceiptSha256"),
            (JsonObject)receipt.DeepClone());
    }

    private FabricC1ProvisionedObject ArtifactCommandIntent(FabricC1ArtifactStageBundle bundle, JsonObject target)
    {
        var intent = ExpectedArtifactCommandIntent(bundle, target);
        if (state.Objects.TryGetValue(ArtifactCommandIntentKey, out var original))
        {
            Guard.Against(original.Kind != intent.Kind || original.Id != intent.Id ||
                original.RequestHash != intent.RequestHash || Canonical.Hash(original.Response) != intent.RequestHash,
                "c1_artifact_command_binding", "The original script, receiver allocation or VM generation changed.", 409);
            return original;
        }
        Guard.Against(new[] { ArtifactCommandKey, ArtifactStageReceiptKey }.Any(key => state.Objects.ContainsKey(key) ||
            state.PendingCreates.ContainsKey(key) || state.PendingDeletes.ContainsKey(key) || state.CleanupReceipts.ContainsKey(key)),
            "c1_artifact_command_binding", "An original command intent cannot be reconstructed after I/O.", 409);
        SaveCheckpointJournal(state with
        { Objects = new(state.Objects, StringComparer.Ordinal) { [ArtifactCommandIntentKey] = intent } });
        return intent;
    }

    private FabricC1ProvisionedObject ExpectedArtifactCommandIntent(FabricC1ArtifactStageBundle bundle, JsonObject target)
    {
        var id = Text(target, "vmResourceId") + "/runCommands/c1-stage-" + Text(bundle.Allowance, "invocationId");
        var request = new JsonObject
        {
            ["location"] = input.Location, ["tags"] = FabricC1Deployment.Tags(input),
            ["properties"] = new JsonObject
            {
                ["source"] = new JsonObject { ["script"] = ArtifactDeliveryScript(bundle) },
                ["asyncExecution"] = true, ["timeoutInSeconds"] = bundle.Profile["maxDurationSeconds"]!.GetValue<int>(),
                ["treatFailureAsDeploymentFailure"] = true, ["parameters"] = new JsonArray(), ["protectedParameters"] = new JsonArray()
            }
        };
        var response = new JsonObject
        {
            ["allocationReceiptSha256"] = Canonical.Hash(state.Objects[ArtifactStageKey]),
            ["vmResourceId"] = Text(target, "vmResourceId"), ["vmInstanceId"] = Text(target, "vmInstanceId"),
            ["request"] = request
        };
        return new("ArtifactStageCommandIntent", id, Canonical.Hash(response), response);
    }

    private void ValidateArtifactCommandInventory(FabricC1ProvisionedObject intent, FabricC1ProvisionedObject? known, string? pending)
    {
        Guard.Against(known is not null && (pending is not null || known.Kind != "ArtifactStageRunCommand" ||
                known.Id != intent.Id || known.RequestHash != intent.RequestHash) ||
            pending is not null && pending != intent.RequestHash ||
            known is null && (state.Objects.ContainsKey(ArtifactStageReceiptKey) ||
                state.PendingDeletes.ContainsKey(ArtifactCommandKey) || state.CleanupReceipts.ContainsKey(ArtifactCommandKey)) ||
            state.DeniedRequests.TryGetValue(ArtifactCommandKey, out var denied) &&
                (denied.RequestHash != intent.RequestHash || denied.Status is not (401 or 403)),
            "c1_artifact_command_binding", "The original command creation inventory changed.", 409);
        if (known is not null) VerifyArtifactCommand(known.Response, intent, null);
    }

    private void VerifyArtifactCommand(JsonObject actual, FabricC1ProvisionedObject intent, FabricC1ProvisionedObject? previous)
    {
        RequireArmOwnership(actual);
        var properties = actual["properties"] as JsonObject ??
            throw new DomainException("c1_artifact_command_binding", "Run Command properties are missing.", 409);
        var expected = intent.Response["request"]!["properties"]!;
        var source = properties["source"] as JsonObject;
        Guard.Against(!SameArmId(actual["id"]?.GetValue<string>(), intent.Id) ||
            !string.Equals(actual["type"]?.GetValue<string>(), "Microsoft.Compute/virtualMachines/runCommands", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual["location"]?.GetValue<string>(), input.Location, StringComparison.OrdinalIgnoreCase) ||
            source is null || source["script"]?.GetValue<string>() != expected["source"]!["script"]!.GetValue<string>() ||
            source.Any(pair => pair.Key != "script" && pair.Value is not null &&
                (pair.Value is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length != 0)) ||
            properties["asyncExecution"]?.GetValue<bool>() != true ||
            properties["treatFailureAsDeploymentFailure"]?.GetValue<bool>() != true ||
            properties["timeoutInSeconds"]?.GetValue<int>() != expected["timeoutInSeconds"]!.GetValue<int>() ||
            new[] { "parameters", "protectedParameters" }.Any(name => properties[name] is not (null or JsonArray { Count: 0 })) ||
            new[] { "runAsUser", "runAsPassword", "outputBlobUri", "errorBlobUri" }.Any(name =>
                properties[name] is not null && properties[name]!.GetValue<string>().Length != 0) ||
            new[] { "outputBlobManagedIdentity", "errorBlobManagedIdentity" }.Any(name =>
                properties[name] is not (null or JsonObject { Count: 0 })),
            "c1_artifact_command_binding", "Run Command must retain its exact inline source, default execution account and no external script/output channel.", 409);
        if (previous is null) return;
        foreach (var name in new[] { "createdAt", "createdBy" })
            Guard.Against(previous.Response["systemData"]?[name] is { } original &&
                Canonical.Hash(original) != Canonical.Hash(actual["systemData"]?[name]),
                "c1_deploy_drift", "Run Command resource generation changed.", 409);
        var previousView = previous.Response["properties"]?["instanceView"] as JsonObject;
        if (previousView?["startTime"] is not null && StageTimestamp(previousView, "startTime") > DateTimeOffset.MinValue)
            Guard.Against(properties["instanceView"] is not JsonObject currentView ||
                StageTimestamp(currentView, "startTime") != StageTimestamp(previousView, "startTime"),
                "c1_deploy_drift", "A previously observed Run Command execution cannot restart.", 409);
    }

    private static void VerifyArtifactGuestAgent(JsonObject vm, JsonObject instance)
    {
        static bool Status(JsonNode? list, string code) => list is JsonArray values &&
            values.Any(value => value?["code"]?.GetValue<string>() == code);
        var agent = instance["vmAgent"];
        Guard.Against(vm["properties"]?["storageProfile"]?["osDisk"]?["osType"]?.GetValue<string>() != "Linux" ||
            vm["properties"]?["osProfile"]?["linuxConfiguration"]?["provisionVMAgent"]?.GetValue<bool>() != true ||
            vm["properties"]?["osProfile"]?["allowExtensionOperations"]?.GetValue<bool>() != true ||
            !Version.TryParse(agent?["vmAgentVersion"]?.GetValue<string>(), out var version) || version < new Version(2, 4, 0, 2) ||
            !Status(agent?["statuses"], "ProvisioningState/succeeded") || !Status(instance["statuses"], "PowerState/running"),
            "c1_artifact_guest_agent", "The original Linux VM must be running with a ready supported Guest Agent and extension operations enabled.", 409);
    }

    private JsonObject ValidateArtifactDeliveryResult(FabricC1ProvisionedObject intent,
        FabricC1ArtifactStageBundle bundle, JsonObject view)
    {
        var started = StageTimestamp(view, "startTime");
        var ended = StageTimestamp(view, "endTime");
        var output = Text(view, "output");
        Guard.Against(view["exitCode"]?.GetValue<int>() != 0 || !string.IsNullOrWhiteSpace(view["error"]?.GetValue<string>()) ||
            started < StageTimestamp(bundle.Allowance, "startedAtUtc") || ended < started ||
            ended > StageTimestamp(bundle.Allowance, "expiresAtUtc") || ended > time.GetUtcNow() ||
            Encoding.UTF8.GetByteCount(output) is <= 0 or > 4096,
            "c1_artifact_stage_result", "Successful execution, zero exit code, bounded output and original execution window are required.", 409);
        JsonObject result;
        try
        {
            using var document = JsonDocument.Parse(output);
            Guard.Against(!UniqueC1JsonProperties(document.RootElement), "c1_artifact_stage_result", "Duplicate result fields are not accepted.", 409);
            result = JsonNode.Parse(output) as JsonObject ??
                throw new DomainException("c1_artifact_stage_result", "The receiver result is not a JSON object.", 409);
        }
        catch (JsonException)
        { throw new DomainException("c1_artifact_stage_result", "The receiver result is not complete JSON.", 409); }
        var profile = bundle.Profile;
        var transport = result["privateTransport"] as JsonObject;
        var addresses = transport?["resolvedAddresses"] as JsonArray;
        var permitted = profile["privateAddresses"]!.AsArray().Select(value => IPAddress.Parse(value!.GetValue<string>())).ToHashSet();
        Guard.Against(result.Count != 16 || Text(result, "status") != "VerifiedNotExecuted" ||
            Text(result, "contract") != "cohors-fabric-c1-private-stage-1" ||
            new[] { "sourceCommit", "runId", "runAttempt", "outerArchiveSha256" }.Any(field => Text(result, field) != Text(profile, field)) ||
            Text(result, "profileSha256") != Text(bundle.Allowance, "profileSha256") ||
            Text(result, "invocationId") != Text(bundle.Allowance, "invocationId") ||
            Text(result, "journalReceiptSha256") != Text(bundle.Allowance, "journalReceiptSha256") ||
            Text(result, "allowanceSha256") != Canonical.HashText(Canonical.Serialize(bundle.Allowance) + "\n") ||
            Text(result, "archiveSha256") != Text(state.Objects[ArtifactPlanKey].Response["provenance"]!.AsObject(), "innerArchiveSha256") ||
            result["files"]?.GetValue<int>() is not (> 0 and <= 2048) ||
            result["httpRequests"]?.GetValue<int>() != 2 || result["retries"]?.GetValue<int>() != 0 ||
            result["payloadExecuted"]?.GetValue<bool>() != false ||
            transport?.Count != 2 || addresses is not { Count: > 0 and <= 16 } ||
            addresses.Any(value => !IPAddress.TryParse(value?.GetValue<string>(), out var address) || !permitted.Contains(address)) ||
            addresses.Select(value => IPAddress.Parse(value!.GetValue<string>())).Distinct().Count() != addresses.Count ||
            !IPAddress.TryParse(transport!["connectedAddress"]?.GetValue<string>(), out var connected) ||
            !addresses.Any(value => IPAddress.Parse(value!.GetValue<string>()).Equals(connected)),
            "c1_artifact_stage_result", "Receiver result must match the original package, allowance, private path and verify-only execution.", 409);
        return new()
        {
            ["contract"] = "cohors-fabric-c1-stage-readback-1", ["commandResourceId"] = intent.Id,
            ["allocationReceiptSha256"] = Text(intent.Response, "allocationReceiptSha256"),
            ["commandIntentSha256"] = intent.RequestHash, ["providerStartedAtUtc"] = StageUtc(started),
            ["providerEndedAtUtc"] = StageUtc(ended), ["result"] = result
        };
    }

    private static bool UniqueC1JsonProperties(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() ==
            value.EnumerateObject().Count() && value.EnumerateObject().All(property => UniqueC1JsonProperties(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(UniqueC1JsonProperties),
        _ => true
    };

    private static DateTimeOffset StageTimestamp(JsonObject value, string name)
    {
        Guard.Against(!DateTimeOffset.TryParse(value[name]?.GetValue<string>(), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var timestamp) || timestamp.Offset != TimeSpan.Zero,
            "c1_artifact_stage_result", "A complete UTC timestamp is required.", 409);
        return timestamp;
    }

    private void RequireArtifactStageTime(DateTimeOffset expiry)
    {
        PlanNormalRequests(0);
        Guard.Against(time.GetUtcNow() >= expiry, "c1_artifact_stage_deadline", "The original artifact delivery window elapsed.", 409);
    }

    private async Task<FabricC1DeploymentReply> SendArtifactCommand(HttpMethod method, Uri uri,
        DateTimeOffset expiry, CancellationToken ct, JsonObject? body = null)
    {
        RequireArtifactStageTime(expiry);
        var reply = await Send(method, uri, body, ct: ct, operationDeadlineUtc: expiry);
        RequireArtifactStageTime(expiry);
        return reply;
    }

    private static FabricC1ArtifactDeliveryResult PendingArtifactDelivery(FabricC1ProvisionedObject intent) =>
        new("Pending", intent.Id, Text(intent.Response, "allocationReceiptSha256"), null);

    private static string ArtifactDeliveryScript(FabricC1ArtifactStageBundle bundle)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            bundle.Profile, bundle.Allowance,
            Receiver = Convert.ToBase64String(Encoding.UTF8.GetBytes(bundle.ReceiverSource)),
            Verifier = Convert.ToBase64String(Encoding.UTF8.GetBytes(bundle.VerifierSource))
        }, Canonical.Json);
        var script = "set -eu\numask 077\nexec python3 -I - <<'COHORS_C1_STAGE_DELIVERY'\n" +
            "PAYLOAD_BASE64 = '" + Convert.ToBase64String(payload) + "'\n" + ArtifactDeliveryPython + "\nCOHORS_C1_STAGE_DELIVERY\n";
        Guard.Against(Encoding.UTF8.GetByteCount(script) > 1024 * 1024,
            "c1_artifact_command_size", "The bounded inline delivery script is oversized.", 409);
        return script;
    }

    private const string ArtifactDeliveryPython = """
import base64, hashlib, json, os, re, signal, stat, sys
from datetime import datetime, timezone
from pathlib import Path

BASE_DIRECTORY = Path("/var/lib/cohors-c1")

def encoded(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n").encode()

def private_directory(path):
    for candidate in (path, *path.parents):
        if candidate.is_symlink():
            raise ValueError("linked directory")
    path.mkdir(mode=0o700, exist_ok=True)
    info = path.stat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
        raise ValueError("shared directory")
    descriptor = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)

def materialize(payload):
    profile, allowance = payload["profile"], payload["allowance"]
    binding = profile["deploymentBindingHash"]
    if not isinstance(binding, str) or re.fullmatch("[0-9a-f]{64}", binding) is None:
        raise ValueError("invalid binding")
    if hashlib.sha256(encoded(profile)).hexdigest() != allowance["profileSha256"]:
        raise ValueError("profile drift")
    files = {"profile.json": encoded(profile), "allowance.json": encoded(allowance)}
    for name, field, content in (
        ("c1_private_stage.py", "receiverSha256", payload["receiver"]),
        ("c1_package.py", "verifierSha256", payload["verifier"]),
    ):
        data = base64.b64decode(content, validate=True)
        if not 0 < len(data) <= 256 * 1024 or hashlib.sha256(data).hexdigest() != profile[field]:
            raise ValueError("tool drift")
        files[name] = data
    root = BASE_DIRECTORY / binding / "artifact-stage"
    for directory in (BASE_DIRECTORY, BASE_DIRECTORY / binding, root):
        private_directory(directory)
    for name, data in files.items():
        path = root / name
        if path.exists() or path.is_symlink():
            info = path.lstat()
            if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_uid != os.geteuid() or info.st_mode & 0o077:
                raise ValueError("unsafe existing file")
            with path.open("rb") as file:
                if file.read(len(data) + 1) != data:
                    raise ValueError("existing file drift")
                os.fsync(file.fileno())
        else:
            descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            with os.fdopen(descriptor, "wb") as file:
                file.write(data)
                file.flush()
                os.fsync(file.fileno())
    descriptor = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)
    return root

def main():
    try:
        if os.environ.get("GITHUB_ACTIONS") == "true":
            raise ValueError("CI cannot execute native delivery")
        payload = json.loads(base64.b64decode(PAYLOAD_BASE64, validate=True))
        expiry = datetime.fromisoformat(payload["profile"]["expiresAtUtc"].replace("Z", "+00:00"))
        remaining = min(payload["profile"]["maxDurationSeconds"], (expiry - datetime.now(timezone.utc)).total_seconds())
        if remaining <= 0:
            raise TimeoutError("expired delivery")
        def expired(*_):
            raise TimeoutError("delivery deadline")
        previous = signal.signal(signal.SIGALRM, expired)
        try:
            signal.setitimer(signal.ITIMER_REAL, remaining)
            root = materialize(payload)
            sys.dont_write_bytecode = True
            sys.path.insert(0, str(root))
            import c1_private_stage
            return c1_private_stage.main(["--profile", str(root / "profile.json"), "--allowance", str(root / "allowance.json"),
                                         "--destination", str(root / "package")])
        finally:
            signal.setitimer(signal.ITIMER_REAL, 0)
            signal.signal(signal.SIGALRM, previous)
    except (OSError, ValueError, TypeError, KeyError, ImportError):
        print(json.dumps({"status": "Blocked", "code": "c1_stage_delivery_incomplete", "payloadExecuted": False}), file=sys.stderr)
        return 2

if __name__ == "__main__":
    sys.exit(main())
""";
}
