"""Verify the producer of retained release artifacts, without publishing credentials."""
import argparse
import json
import os
import re
import subprocess


def verify_producer(run, jobs, repository, workflow_id):
    if (run.get("repository", {}).get("full_name") != repository or
            run.get("head_repository", {}).get("full_name") != repository or
            run.get("workflow_id") != workflow_id or
            run.get("path") != ".github/workflows/publish-nuget.yml" or
            run.get("head_branch") != "main" or run.get("event") != "workflow_dispatch" or
            run.get("status") != "completed" or
            not re.fullmatch(r"[0-9a-f]{40}", run.get("head_sha", ""))):
        raise RuntimeError("Artifact producer must be this repository's completed main publishing workflow.")
    validation = [job for job in jobs if job.get("name") != "publish"]
    if not any(job.get("name") == "validate" for job in validation) or any(
            job.get("status") != "completed" or job.get("conclusion") != "success"
            for job in validation):
        raise RuntimeError("All producer validation/test jobs must have completed successfully.")
    # A successful empty/skipped validation must not qualify as tested artifacts.
    steps = [step for job in validation for step in job.get("steps", [])]
    for command in ("dotnet test ", "python -m pytest ", "python tools/verify_packages.py",
                    "python tools/check_release.py"):
        if not any(command in step.get("name", "") and step.get("status") == "completed"
                   and step.get("conclusion") == "success" for step in steps):
            raise RuntimeError(f"Missing successful producer check: {command}")
    return run["head_sha"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-id", required=True, type=int)
    args = parser.parse_args()
    repository = os.environ["GITHUB_REPOSITORY"]
    def api(path):
        return json.loads(subprocess.check_output(["gh", "api", f"repos/{repository}/{path}"], text=True))
    run = api(f"actions/runs/{args.run_id}")
    workflow = api("actions/workflows/publish-nuget.yml")
    jobs = []
    page = 1
    while True:
        batch = api(f"actions/runs/{args.run_id}/attempts/{run['run_attempt']}/jobs?per_page=100&page={page}")["jobs"]
        jobs.extend(batch)
        if len(batch) < 100:
            break
        page += 1
    sha = verify_producer(run, jobs, repository, workflow["id"])
    if api(f"compare/{sha}...main")["status"] not in ("ahead", "identical"):
        raise RuntimeError("Producer commit must be an ancestor of this repository's main branch.")
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"source_sha={sha}\n")
    print(f"Verified successful validation of {sha} in run {args.run_id}.")


if __name__ == "__main__":
    main()
