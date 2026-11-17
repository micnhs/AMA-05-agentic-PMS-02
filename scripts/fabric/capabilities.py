"""Read-only evidence-level validation; never authenticates, provisions or deploys."""
import argparse
import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
GATE_IDS = {f"G{number:02}" for number in range(1, 12)}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate(document):
    require(document["deliveryId"] == "cohors-fabric", "Wrong delivery identity.")
    gates = document["gates"]
    require(len(gates) == 11 and {gate["id"] for gate in gates} == GATE_IDS,
            "The complete, unique G01-G11 gate set is required.")
    evidence = {item["id"]: item for item in document["evidence"]}
    sources = {item["id"]: item for item in document["sources"]}
    require(len(evidence) == len(document["evidence"]), "Duplicate evidence ID.")
    require(len(sources) == len(document["sources"]), "Duplicate source ID.")
    for item in evidence.values():
        require(re.fullmatch(r"[a-f0-9]{40}", item["sourceCommit"]), "Evidence needs an exact source commit.")
        require(item["result"] in {"passed", "failed-before-job-start", "failed"}, "Unknown evidence result.")
        require(item["level"] in {"workflow-validation", "hosted-in-process", "real-service"},
                "Unknown evidence level.")
    for gate in gates:
        require(gate["hardProductionGate"] is True, f'{gate["id"]}: production gate was weakened.')
        require(gate["realService"] in {"not_run", "failed", "passed"}, "Unknown service status.")
        for field in ("functionalAcceptance", "operationalAcceptance"):
            require(gate[field] in {"not_accepted", "rejected", "accepted"}, "Unknown acceptance status.")
            if gate[field] == "accepted":
                require(gate["realService"] == "passed", "Acceptance cannot precede real-service proof.")
        for ref in gate.get("sources", []):
            require(ref in sources, f"Unknown documentation source: {ref}")
        for ref in gate["modelEvidence"]:
            require(ref in evidence and evidence[ref]["level"] == "hosted-in-process" and
                    evidence[ref]["result"] == "passed", "Model evidence must identify actual in-process execution.")
        service_refs = gate.get("serviceEvidence", [])
        for ref in service_refs:
            require(ref in evidence and evidence[ref]["level"] == "real-service" and
                    evidence[ref]["result"] == "passed", "A build, model or document cannot become real-service proof.")
        if gate["realService"] == "passed":
            require(service_refs, f'{gate["id"]}: real-service pass has no service evidence.')
        for relative in gate.get("code", []):
            path = (ROOT / relative).resolve()
            require(path.is_relative_to(ROOT) and path.is_file(), f"Missing or non-workspace code reference: {relative}")
    accepted = all(gate["realService"] == "passed" and gate["functionalAcceptance"] == "accepted" and
                   gate["operationalAcceptance"] == "accepted" for gate in gates)
    if document["productionCutoverAllowed"] or document["adapterActivationAllowed"]:
        require(accepted and document["evidencePolicy"]["resourceApprovalGranted"],
                "Open gates or missing resource approval prohibit adapter activation and production cutover.")
    for field in ("specificationIsExecution", "hostedModelExecutionIsServiceEvidence",
                  "documentationIsServiceEvidence", "serviceExecutionIsAcceptance"):
        require(document["evidencePolicy"][field] is False, "Evidence levels must remain separate.")


def runtime_blockers(document, delivery):
    validate(document)
    result = []
    for gate in document["gates"]:
        if gate["realService"] != "passed" or gate["functionalAcceptance"] != "accepted" or gate["operationalAcceptance"] != "accepted":
            result.append(f'{gate["id"]}: service proof and functional/operational acceptance are incomplete.')
    if not document["adapterActivationAllowed"] or not document["productionCutoverAllowed"]:
        result.append("Adapter activation and production cutover are disabled.")
    if not document["evidencePolicy"]["resourceApprovalGranted"] or not delivery["deployment"]["resourceApproval"]:
        result.append("Separate target, cost, data and provisioning approval is missing.")
    for field in ("subscriptionIds", "regions", "fabricCapacityIds", "fabricWorkspaceIds", "identityIds", "resourceGroupIds"):
        if not delivery["deployment"][field]:
            result.append(f"Approved delivery allocation is missing: {field}.")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--require-runtime", action="store_true",
                        help="Fail closed when runtime prerequisites are incomplete; never grants permission to deploy.")
    args = parser.parse_args()
    document = json.loads((ROOT / "docs" / "fabric" / "capability-gates.json").read_text(encoding="utf-8"))
    delivery = json.loads((ROOT / "fabric" / "delivery.json").read_text(encoding="utf-8"))
    blockers = runtime_blockers(document, delivery)
    print(f"Capability manifest valid: 11 hard gates; {len(blockers)} runtime blockers.")
    if args.require_runtime:
        for blocker in blockers:
            print(blocker)
        return 2 if blockers else 0
    print("Schema consistency is not service proof, acceptance or deployment authorization.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
