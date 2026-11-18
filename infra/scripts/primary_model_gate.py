"""Infrastructure binding of a domain-owned live model acceptance; never a clinical evaluator."""

from pathlib import Path
import re

from primary_control import GateError, digest, file_hash, parse_time, read_json, utcnow


LIVE_PHASES = {"runtime", "seed", "publish-functions", "activate"}
TASKS = {"Ingestion", "CausalityRisk", "EvidenceAssurance", "VigilanceAuthoring", "TrendSignal", "PsurAuthoring"}
SHA256 = re.compile(r"[0-9a-f]{64}\Z")


def bound_evidence(directory, reference):
    if (not isinstance(reference, dict) or not isinstance(reference.get("sha256"), str)
            or not SHA256.fullmatch(reference["sha256"]) or not isinstance(reference.get("file"), str)):
        raise GateError("Model acceptance requires an actual SHA-256-bound evidence file.")
    relative = Path(reference.get("file", ""))
    if not relative.parts or relative.is_absolute() or ".." in relative.parts:
        raise GateError("Model evidence must stay inside the private approval directory.")
    path = Path(directory) / relative
    if (path.is_symlink() or not path.is_file() or not path.resolve().is_relative_to(Path(directory).resolve())
            or file_hash(path) != reference["sha256"]):
        raise GateError("Model evidence is absent, a symlink, or changed.")
    return path


def candidate_binding(manifest):
    model = manifest["model"]
    return {key: model[key] for key in ("name", "version", "deploymentName", "sku", "region")}


def require_model_acceptance(manifest, phase, approval, directory, outputs, *, now=None):
    if phase not in LIVE_PHASES:
        return None
    path = bound_evidence(directory, approval.get("modelAcceptance"))
    acceptance = read_json(path)
    if (not isinstance(acceptance, dict) or acceptance.get("contract") != "cohors-primary-model-acceptance-1"
            or acceptance.get("bindingHash") != manifest["bindingHash"]
            or acceptance.get("applicationCommit") != manifest["releaseBundle"]["applicationCommit"]
            or acceptance.get("candidate") != candidate_binding(manifest)
            or acceptance.get("runtimeProfileMode") != "SingleSharedQualifiedProfile"
            or acceptance.get("accepted") is not True):
        raise GateError("Live model acceptance is not bound to this exact application, deployment and shared profile.")
    current = now or utcnow()
    reviewed = parse_time(acceptance.get("reviewedAtUtc"))
    expires = parse_time(acceptance.get("expiresAtUtc"))
    if not reviewed <= current < expires or expires > parse_time(manifest["expiresAtUtc"]):
        raise GateError("Live model acceptance is expired, future-dated or outlives the pilot.")
    if acceptance.get("guaranteesZeroErrors") is not False or acceptance.get("clinicalValidationClaim") is not False:
        raise GateError("A synthetic pilot cannot assert universal zero errors or clinical validation.")
    tasks = acceptance.get("acceptedTasks")
    if (not isinstance(tasks, list) or len(tasks) != len(TASKS)
            or any(not isinstance(task, str) for task in tasks) or set(tasks) != TASKS
            or acceptance.get("automaticCheaperFallback") is not False
            or acceptance.get("unqualifiedTaskRouting") is not False):
        raise GateError("Every actual shared-profile task requires domain acceptance; no implicit cheap-task routing.")
    observation = outputs.get("phase-initialize", {}).get("modelObservation")
    if not observation or acceptance.get("modelReadbackSha256") != digest(observation):
        raise GateError("Model acceptance is not bound to the actual regional deployment/version readback.")
    evaluate = read_json(bound_evidence(directory, acceptance.get("evaluationReport")))
    domain_review = read_json(bound_evidence(directory, acceptance.get("domainReview")))
    if (not isinstance(evaluate, dict) or evaluate.get("executionMode") != "FoundryLive"
            or evaluate.get("applicationCommit") != manifest["releaseBundle"]["applicationCommit"]
            or evaluate.get("candidate") != candidate_binding(manifest)
            or evaluate.get("modelReadbackSha256") != digest(observation)
            or evaluate.get("promptVersion") != acceptance.get("promptVersion")
            or not evaluate.get("promptVersion")):
        raise GateError("Offline/replay, another prompt, or another model's results cannot qualify live analysis.")
    if not isinstance(domain_review, dict) or not isinstance(domain_review.get("criteria"), dict):
        raise GateError("The domain review requires actual task-specific criteria references.")
    task_results = evaluate.get("taskResults")
    if (not isinstance(task_results, list) or len(task_results) != len(TASKS)
            or any(not isinstance(item, dict) or not isinstance(item.get("task"), str)
                   for item in task_results) or {
            item.get("task") for item in task_results} != TASKS):
        raise GateError("Live evaluation must cover each actual role once, including triage and assurance.")
    for result in task_results:
        if (not isinstance(result.get("sampleCount"), int) or isinstance(result["sampleCount"], bool)
                or result["sampleCount"] <= 0):
            raise GateError("A live evaluation with no real samples is not an acceptance result.")
        for metric in ("falsePositives", "falseNegatives", "unsupportedClaims", "invalidOutputs"):
            count = result.get(metric)
            if not isinstance(count, int) or isinstance(count, bool) or count < 0:
                raise GateError("Every role requires measured error counts; missing is not zero.")
        if not isinstance(result.get("criteriaSha256"), str) or not SHA256.fullmatch(result["criteriaSha256"]):
            raise GateError("Domain-owned acceptance criteria must be version-bound, not invented by infrastructure.")
        criterion = domain_review.get("criteria", {}).get(result["task"])
        bound_evidence(directory, criterion)
        if criterion["sha256"] != result["criteriaSha256"]:
            raise GateError("Live results and the domain review use different task acceptance criteria.")
    reviewers = {actor["objectId"] for actor in manifest["actors"]
                 if "MedicalSafetyReviewer" in actor["roles"] or "QualityManager" in actor["roles"]}
    if (domain_review.get("reviewerObjectId") not in reviewers
            or domain_review.get("evaluationReportSha256") != acceptance["evaluationReport"]["sha256"]
            or domain_review.get("accepted") is not True
            or domain_review.get("scope") != "SyntheticPilotOnly"):
        raise GateError("An explicitly bound domain reviewer must accept the live report; infrastructure ownership is not domain approval.")
    return {"acceptanceSha256": file_hash(path), "evaluationReportSha256": acceptance["evaluationReport"]["sha256"],
            "domainReviewSha256": acceptance["domainReview"]["sha256"], "clinicalValidationClaim": False}
