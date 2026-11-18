"""Read an existing, source-bound application acceptance run; never launch one."""

import argparse
import json
from pathlib import Path
import re
import subprocess

from primary_control import GateError


APPLICATION_JOB = "SQL, .NET and Chromium acceptance"
APPLICATION_STEP = "Real SQL and browser tests with fresh ephemeral credentials"


def github_json(path):
    result = subprocess.run(["gh", "api", path], stdin=subprocess.DEVNULL, capture_output=True,
                            timeout=90, check=False)
    if result.returncode:
        raise GateError("Existing GitHub context cannot read the bound evidence; no login or token fallback.")
    return json.loads(result.stdout)


def application_acceptance(repository, run_id, application_commit, fetch=github_json):
    if (not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository)
            or type(run_id) is not int or run_id < 1
            or not re.fullmatch(r"[0-9a-f]{40}", application_commit)):
        raise GateError("An exact repository, application SHA and existing run ID are required.")
    run = fetch(f"repos/{repository}/actions/runs/{run_id}")
    if (run.get("id") != run_id or run.get("head_sha") != application_commit
            or run.get("path") != ".github/workflows/cohors-ci.yml"
            or run.get("head_branch") != "micnhs-cohors-pilot" or run.get("event") != "push"
            or run.get("repository", {}).get("full_name") != repository
            or type(run.get("run_attempt")) is not int or run["run_attempt"] < 1):
        raise GateError("The existing application run does not match the reviewed source and natural push lane.")
    jobs = fetch(f"repos/{repository}/actions/runs/{run_id}/attempts/{run['run_attempt']}/jobs?per_page=100")
    if (not isinstance(jobs.get("jobs"), list) or jobs.get("total_count") != len(jobs["jobs"])):
        raise GateError("Incomplete application job inventory; no partial pagination result is accepted.")
    matching = [job for job in jobs["jobs"] if job.get("name") == APPLICATION_JOB]
    if len(matching) != 1:
        raise GateError("Exactly one original application acceptance job must be present.")
    job = matching[0]
    steps = [step for step in job.get("steps", []) if step.get("name") == APPLICATION_STEP]
    if (job.get("head_sha") != application_commit or job.get("run_id") != run_id
            or job.get("run_attempt") != run["run_attempt"] or len(steps) != 1):
        raise GateError("The original test step and job must belong to this exact source/run attempt.")
    step = steps[0]
    passed = (run.get("status") == "completed" and run.get("conclusion") == "success"
              and job.get("status") == "completed" and job.get("conclusion") == "success"
              and step.get("status") == "completed" and step.get("conclusion") == "success")
    return {"contract": "cohors-existing-application-acceptance-1", "repository": repository,
            "applicationCommit": application_commit, "runId": run_id, "runAttempt": run["run_attempt"],
            "runConclusion": run.get("conclusion"), "jobId": job["id"],
            "jobConclusion": job.get("conclusion"), "testStepConclusion": step.get("conclusion"),
            "passed": passed, "testsRerun": False, "cloudReleased": False}


def require_application_acceptance(repository, run_id, application_commit, fetch=github_json):
    receipt = application_acceptance(repository, run_id, application_commit, fetch)
    if not receipt["passed"]:
        raise GateError("The existing source-bound application acceptance is not successful; packaging cannot waive it.")
    return receipt


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--run-id", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    if options.output.exists():
        raise GateError("Use a fresh application evidence path; do not overwrite earlier results.")
    receipt = application_acceptance(options.repository, options.run_id, options.commit)
    options.output.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt))
    if not receipt["passed"]:
        raise SystemExit("Application qualification remains closed. The existing tests were not repeated.")
