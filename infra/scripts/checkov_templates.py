"""Export every compiled ARM template for Checkov's legacy resource-list reader.

These scan-only files are not deployment artifacts. Resource expressions,
conditions, copy loops and security properties are not evaluated or replaced.
"""

import argparse
import copy
import hashlib
import json
from pathlib import Path


def templates(document, path=()):
    resources = document.get("resources")
    if not isinstance(resources, (dict, list)):
        raise ValueError(f"Template has no supported resource collection: {path}")
    yield path, document
    entries = resources.items() if isinstance(resources, dict) else enumerate(resources)
    for symbol, resource in entries:
        if not isinstance(resource, dict) or not isinstance(resource.get("type"), str):
            raise ValueError(f"Malformed resource at {path + (str(symbol),)}")
        if resource["type"].lower() == "microsoft.resources/deployments":
            nested = resource.get("properties", {}).get("template")
            if nested is None:
                raise ValueError("Linked templates are not included in the offline scan.")
            yield from templates(nested, path + (str(symbol),))


def resource_list(document):
    result = copy.deepcopy(document)
    if isinstance(result["resources"], dict):
        result["resources"] = list(result["resources"].values())
    for resource in result["resources"]:
        if resource["type"].lower() == "microsoft.resources/deployments":
            resource["properties"]["template"] = resource_list(resource["properties"]["template"])
    return result


def export(source, destination):
    manifest_path = destination.with_suffix(".manifest.json")
    if destination.exists() or manifest_path.exists():
        raise ValueError("Use a new, empty scan-input directory; stale templates must not be scanned.")
    source_text = source.read_text(encoding="utf-8")
    # Checkov 3.4.5 discards failed ARM checks containing these unresolved functions.
    for expression in ("toLower(", "trim(", "join(", "split(", "substring("):
        if expression in source_text:
            raise ValueError(f"Checkov may silently filter unresolved {expression} expressions; review scanner support first.")
    document = json.loads(source_text)
    inventory = list(templates(document))
    if not inventory:
        raise ValueError("No templates found.")
    destination.mkdir(parents=True)
    manifest = []
    for index, (path, template) in enumerate(inventory):
        filename = f"SCAN-ONLY-{index:03}.json"
        resources = template["resources"]
        entries = resources.items() if isinstance(resources, dict) else enumerate(resources)
        manifest.append({
            "file": filename,
            "templatePath": list(path),
            "resources": [
                {"symbol": str(symbol), "type": resource["type"], "existing": resource.get("existing", False)}
                for symbol, resource in entries
            ],
        })
        (destination / filename).write_text(
            json.dumps(resource_list(template), indent=2, ensure_ascii=True) + "\n",
            encoding="utf-8",
        )
    manifest_path.write_text(json.dumps({
        "sourceSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "templates": manifest,
    }, indent=2) + "\n", encoding="utf-8")
    print(f"Exported all {len(manifest)} compiled templates; manifest: {manifest_path}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    options = parser.parse_args()
    export(options.source, options.destination)
