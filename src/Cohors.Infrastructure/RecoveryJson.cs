using System.Text.Json;

namespace Cohors.Infrastructure;

public static class RecoveryJson
{
    private static readonly HashSet<string> EmbeddedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Json", "Payload", "Result", "frozenJson", "inputJson", "outputJson"
    };

    public static string[] ReferenceNodes(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = 100, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
        });
        var pending = new Stack<JsonElement>();
        var result = new List<string>();
        pending.Push(document.RootElement);
        while (pending.TryPop(out var node))
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray()) pending.Push(item);
                continue;
            }
            if (node.ValueKind != JsonValueKind.Object) continue;
            var retention = node.TryGetProperty("contract", out var contract) &&
                contract.ValueKind == JsonValueKind.String && contract.GetString() == "cohors-retention-record-1";
            var relevant = retention || node.TryGetProperty("objectKey", out _) ||
                node.TryGetProperty("xmlHash", out _) || node.TryGetProperty("attachmentHashes", out _) ||
                ((node.TryGetProperty("uri", out _) || node.TryGetProperty("evidenceId", out _)) &&
                    node.TryGetProperty("hash", out _));
            foreach (var property in node.EnumerateObject())
            {
                if (retention && (property.Name.Equals("object", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("deletion", StringComparison.OrdinalIgnoreCase))) continue;
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    pending.Push(property.Value);
                else if (EmbeddedFields.Contains(property.Name))
                    relevant = true;
            }
            // Return original nodes, not inferred references; the existing recovery validators still decide.
            if (relevant) result.Add(node.GetRawText());
        }
        return result.ToArray();
    }
}
