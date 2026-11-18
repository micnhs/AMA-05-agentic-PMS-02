using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed partial class FabricC1Provisioner
{
    private sealed record ArmParent(string Stage, string RequestHash, JsonObject Snapshot);

    private void VerifyArmDeploymentReadback(JsonObject body, string id, JsonObject parameters,
        FabricC1ProvisionedObject? previous)
    {
        Guard.Against(!SameArmId(Text(body, "id"), id) ||
            body["properties"]?["mode"]?.GetValue<string>() != "Incremental" ||
            body["properties"]?["parameters"] is not JsonObject, "c1_deploy_arm_binding",
            "ARM deployment identity, mode or parameter readback differs.", 409);
        var observed = (JsonObject)body["properties"]!["parameters"]!;
        Guard.Against(observed.Count != parameters.Count || parameters.Any(parameter =>
            !observed.ContainsKey(parameter.Key) ||
            !JsonNode.DeepEquals(parameter.Value?["value"], observed[parameter.Key]?["value"])),
            "c1_deploy_arm_binding", "ARM parameters differ from the original submitted request.", 409);
        var correlation = GuidValue(Text((JsonObject)body["properties"]!, "correlationId"));
        var templateHash = Text((JsonObject)body["properties"]!, "templateHash");
        Guard.Against(previous is not null &&
            (previous.Kind is not ("ArmDeployment" or "ArmDeploymentObservation") || !SameArmId(previous.Id, id) ||
             GuidValue(Text((JsonObject)previous.Response["properties"]!, "correlationId")) != correlation ||
             previous.Response["properties"]?["templateHash"]?.GetValue<string>() != templateHash),
            "c1_deploy_arm_replaced", "The recorded ARM deployment was replaced; do not adopt its new outputs.", 409);
    }

    private async Task<Dictionary<string, FabricC1ProvisionedObject>> VerifyArmInventory(CancellationToken ct,
        string? candidateStage = null, string? candidateHash = null, JsonObject? candidate = null)
    {
        var group = FabricC1Deployment.GroupId(input);
        var resources = await ReadAll($"https://management.azure.com{group}/resources?api-version=2021-04-01", false, ct);
        var inventory = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resources)
            Guard.Against(!inventory.TryAdd(Text(resource, "id"), resource),
                "c1_deploy_arm_inventory", "ARM inventory contains duplicate resource identities.", 409);
        var parents = new List<ArmParent>();
        foreach (var stage in new[] { "foundation", "private-endpoints" })
        {
            if (stage == candidateStage)
                AddArmParents(parents, stage, candidateHash!, candidate!);
            else if (state.Objects.TryGetValue("arm-" + stage, out var saved))
            {
                Guard.Against(saved.Kind != "ArmDeployment", "c1_deploy_receipt_kind", "ARM receipt kind differs.", 409);
                AddArmParents(parents, stage, saved.RequestHash, saved.Response);
            }
        }
        foreach (var owner in parents)
        {
            var parentId = Text(owner.Snapshot, "id");
            var network = owner.Stage == "private-endpoints";
            var parentType = network ? "Microsoft.Network/privateEndpoints" : "Microsoft.Compute/virtualMachines";
            RequireTypedArmResource(owner.Snapshot, parentType);
            RequireArmOwnership(owner.Snapshot);
            var expected = network ? owner.Snapshot["properties"]?["networkInterfaces"] as JsonArray :
                new JsonArray(owner.Snapshot["properties"]?["storageProfile"]?["osDisk"]?["managedDisk"]?.DeepClone());
            Guard.Against(expected is null || expected.Count is < 1 or > 16 ||
                expected.Any(item => item is not JsonObject || !inventory.ContainsKey(Text((JsonObject)item, "id"))) ||
                !inventory.ContainsKey(parentId),
                "c1_deploy_arm_inventory_incomplete", "Original parents or their generated children are absent from inventory; absence is not cleanup proof.", 409);
        }
        Guard.Against(state.Objects.Values.Any(item => item.Kind == "ArmGeneratedChild" && !inventory.ContainsKey(item.Id)),
            "c1_deploy_arm_inventory_incomplete", "A previously inventoried generated child disappeared; require explicit absence reconciliation.", 409);
        var children = new Dictionary<string, FabricC1ProvisionedObject>(StringComparer.Ordinal);
        var currentParents = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resources)
        {
            var id = Text(resource, "id");
            var type = Text(resource, "type");
            var network = type.Equals("Microsoft.Network/networkInterfaces", StringComparison.OrdinalIgnoreCase);
            var matches = parents.Where(parent => ReferencesArmChild(parent.Snapshot, id, network)).ToArray();
            if (resource["tags"] is not (null or JsonObject { Count: 0 }))
            {
                RequireArmOwnership(resource);
                if (matches.Length == 0 && !state.Objects.ContainsKey("arm-child-" + Canonical.HashText(id.ToLowerInvariant()))) continue;
            }
            Guard.Against(!network && !type.Equals("Microsoft.Compute/disks", StringComparison.OrdinalIgnoreCase),
                "c1_deploy_arm_untagged", "An untagged resource is not a supported provider-generated NIC or OS disk.", 409);
            RequireTypedArmResource(resource, type);
            Guard.Against(matches.Length != 1, "c1_deploy_arm_parent_receipt",
                "No unique completed deployment output binds this generated child; do not infer ownership from its name.", 409);
            var owner = matches[0];
            var parentType = network ? "Microsoft.Network/privateEndpoints" : "Microsoft.Compute/virtualMachines";
            var parentId = RequireTypedArmResource(owner.Snapshot, parentType);
            RequireArmOwnership(owner.Snapshot);
            Guard.Against(!inventory.TryGetValue(parentId, out var listedParent),
                "c1_deploy_arm_parent_inventory", "The original parent is absent from the complete group inventory.", 409);
            RequireArmOwnership(listedParent!);
            if (!currentParents.TryGetValue(parentId, out var parent))
            {
                parent = await Read("https://management.azure.com" + parentId +
                    "?api-version=" + (network ? "2024-05-01" : "2024-07-01"), ct);
                RequireArmOwnership(parent);
                Guard.Against(!SameArmId(RequireTypedArmResource(parent, parentType), parentId),
                    "c1_deploy_arm_child_binding", "Parent readback returned another resource.", 409);
                currentParents.Add(parentId, parent);
            }
            Guard.Against(!ReferencesArmChild(parent, id, network), "c1_deploy_arm_child_binding",
                "The parent no longer references its original generated child.", 409);
            if (!network)
                Guard.Against(GuidValue(Text((JsonObject)parent["properties"]!, "vmId")) !=
                    GuidValue(Text((JsonObject)owner.Snapshot["properties"]!, "vmId")),
                    "c1_deploy_arm_parent_replaced", "The probe VM was recreated under the same resource ID.", 409);
            var child = await Read("https://management.azure.com" + id +
                "?api-version=" + (network ? "2024-05-01" : "2024-03-02"), ct);
            Guard.Against(!SameArmId(RequireTypedArmResource(child, type), id),
                "c1_deploy_arm_child_binding", "Child readback returned another resource.", 409);
            if (child["tags"] is not (null or JsonObject { Count: 0 })) RequireArmOwnership(child);
            var reverse = network ? child["properties"]?["privateEndpoint"]?["id"]?.GetValue<string>()
                : child["managedBy"]?.GetValue<string>();
            Guard.Against(!SameArmId(reverse, parentId) ||
                !network && (child["managedByExtended"] is not (null or JsonArray { Count: 0 }) ||
                    parent["properties"]?["storageProfile"]?["osDisk"]?["deleteOption"]?.GetValue<string>() != "Delete"),
                "c1_deploy_arm_child_binding", "The generated child does not exclusively belong to its recorded parent.", 409);
            var instance = GuidValue(Text((JsonObject)child["properties"]!, network ? "resourceGuid" : "uniqueId")).ToString("D");
            var key = "arm-child-" + Canonical.HashText(id.ToLowerInvariant());
            var binding = Canonical.Hash(new { ResourceId = id.ToLowerInvariant(), ParentId = parentId.ToLowerInvariant(),
                owner.Stage, owner.RequestHash });
            if (state.Objects.TryGetValue(key, out var previous))
                Guard.Against(previous.Kind != "ArmGeneratedChild" || !SameArmId(previous.Id, id) ||
                    previous.RequestHash != binding || previous.Response["instanceId"]?.GetValue<string>() != instance,
                    "c1_deploy_arm_child_replaced", "A generated child was replaced or moved; do not adopt the new instance.", 409);
            children.Add(key, new("ArmGeneratedChild", id, binding, new()
            {
                ["parentId"] = parentId, ["parentStage"] = owner.Stage, ["instanceId"] = instance,
                ["parentRequestHash"] = owner.RequestHash, ["observedAtUtc"] = time.GetUtcNow().ToString("O"),
                ["cleanupAssociation"] = "ThroughRecordedParentOnly", ["directDeleteAuthorized"] = false,
                ["parent"] = parent.DeepClone(), ["resource"] = child.DeepClone()
            }));
        }
        return children;
    }

    private void AddArmParents(List<ArmParent> parents, string stage, string hash, JsonObject deployment)
    {
        Guard.Against(!SameArmId(Text(deployment, "id"), FabricC1Deployment.GroupId(input) +
            "/providers/Microsoft.Resources/deployments/c1-" + stage) ||
            deployment["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded",
            "c1_deploy_arm_parent_receipt", "Only the original completed stage can supply generated-child bindings.", 409);
        if (stage == "foundation")
        {
            var probe = deployment["properties"]?["outputs"]?["probe"]?["value"] as JsonObject ??
                throw new DomainException("c1_deploy_arm_parent_receipt", "Completed foundation did not return its probe VM.", 409);
            Guard.Against(!SameArmId(Text(probe, "id"), FabricC1Deployment.GroupId(input) +
                "/providers/Microsoft.Compute/virtualMachines/" + FabricC1Deployment.Name(input) + "-probe"),
                "c1_deploy_arm_parent_receipt", "Foundation probe output identifies another VM.", 409);
            parents.Add(new(stage, hash, probe));
        }
        if (stage == "private-endpoints")
        {
            var endpoints = deployment["properties"]?["outputs"]?["privateEndpoints"]?["value"] as JsonArray ??
                throw new DomainException("c1_deploy_arm_parent_receipt", "Completed network stage did not return its private endpoints.", 409);
            Guard.Against(endpoints.Count is < 1 or > 32 || endpoints.Any(endpoint => endpoint is not JsonObject),
                "c1_deploy_arm_parent_receipt", "Unexpected private endpoint output inventory.", 409);
            foreach (var endpoint in endpoints) parents.Add(new(stage, hash, (JsonObject)endpoint!));
        }
    }

    private string RequireTypedArmResource(JsonObject resource, string type)
    {
        var id = Text(resource, "id");
        Guard.Against(!string.Equals(Text(resource, "type"), type, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(id, "^" + Regex.Escape(FabricC1Deployment.GroupId(input) + "/providers/" + type + "/") +
                "[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) ||
            !string.Equals(Text(resource, "location"), input.Location, StringComparison.OrdinalIgnoreCase),
            "c1_deploy_arm_child_scope", "Generated-child inventory left the bound type, group or region.", 409);
        return id;
    }

    private static bool ReferencesArmChild(JsonObject parent, string child, bool network) => network
        ? parent["properties"]?["networkInterfaces"] is JsonArray interfaces &&
            interfaces.Count(item => item is JsonObject && SameArmId(item["id"]?.GetValue<string>(), child)) == 1
        : SameArmId(parent["properties"]?["storageProfile"]?["osDisk"]?["managedDisk"]?["id"]?.GetValue<string>(), child);

    private static bool SameArmId(string? left, string? right) => left is not null && right is not null &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private void SaveArmInventory(Dictionary<string, FabricC1ProvisionedObject> children,
        string? stage = null, string? id = null, string? hash = null, JsonObject? response = null)
    {
        var objects = new Dictionary<string, FabricC1ProvisionedObject>(state.Objects, StringComparer.Ordinal);
        foreach (var child in children) objects[child.Key] = child.Value;
        var pending = new Dictionary<string, string>(state.PendingCreates, StringComparer.Ordinal);
        if (stage is not null)
        {
            objects["arm-" + stage] = new("ArmDeployment", id!, hash!, (JsonObject)response!.DeepClone());
            pending.Remove("arm-" + stage);
        }
        var next = state with { Objects = objects, PendingCreates = pending };
        journal.Save(next);
        state = next;
    }
}
