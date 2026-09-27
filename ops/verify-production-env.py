#!/usr/bin/env python3
"""Verify the protected Compose source matches the live production containers.

This reads credentials into memory but never displays or writes their values.
"""

import json
import os
import re
import stat
import subprocess
import sys
from pathlib import Path


PROJECT = "wukna-prod"
COMPOSE_FILE = Path("/home/ubuntu/wukna-deploy/compose.production.yaml")
ENV_FILE = Path("/etc/wukna/production.env")
DOCKER = "/usr/bin/docker"


def docker_output(*args, env=None):
    result = subprocess.run(
        [DOCKER, *args], env=env, capture_output=True, text=True
    )
    if result.returncode != 0:
        raise RuntimeError("A Docker inspection or Compose validation command failed")
    return result.stdout.strip()


def container_id(service):
    output = docker_output(
        "ps",
        "--quiet",
        "--filter",
        f"label=com.docker.compose.project={PROJECT}",
        "--filter",
        f"label=com.docker.compose.service={service}",
    )
    matches = output.splitlines()
    if len(matches) != 1:
        raise RuntimeError(f"Expected one running {service} container; found {len(matches)}")
    return matches[0]


def container_env(container):
    raw = docker_output("inspect", "--format", "{{json .Config.Env}}", container)
    return dict(entry.split("=", 1) for entry in json.loads(raw) if "=" in entry)


def check_protected_file():
    directory = ENV_FILE.parent.lstat()
    source = ENV_FILE.lstat()
    if not stat.S_ISDIR(directory.st_mode) or directory.st_uid != 0 or stat.S_IMODE(directory.st_mode) != 0o700:
        raise RuntimeError("/etc/wukna must be a root-owned directory with mode 0700")
    if not stat.S_ISREG(source.st_mode) or source.st_uid != 0 or stat.S_IMODE(source.st_mode) != 0o600:
        raise RuntimeError("production.env must be a root-owned regular file with mode 0600")


def main():
    if os.geteuid() != 0:
        raise RuntimeError("Run this verification as root")
    check_protected_file()
    if not COMPOSE_FILE.is_file():
        raise RuntimeError("The production Compose file is missing")

    api_id = container_id("api")
    frontend_id = container_id("frontend")
    db_id = container_id("db")
    live_api = container_env(api_id)
    live_db = container_env(db_id)

    image = docker_output("inspect", "--format", "{{.Config.Image}}", api_id)
    match = re.fullmatch(r"ghcr\.io/ahmadkawkab-dev/wukna-api:sha-([0-9a-f]{40})", image)
    if not match:
        raise RuntimeError("The running API image has no full SHA tag")
    sha = match.group(1)

    rendered = json.loads(
        docker_output(
            "compose",
            "--project-directory",
            str(COMPOSE_FILE.parent),
            "--env-file",
            str(ENV_FILE),
            "-f",
            str(COMPOSE_FILE),
            "config",
            "--format",
            "json",
            env={"PATH": "/usr/bin:/bin", "HOME": "/root", "WUKNA_IMAGE_SHA": sha},
        )
    )
    services = rendered["services"]
    desired_api = services["api"]["environment"]
    desired_db = services["db"]["environment"]

    comparisons = {
        "POSTGRES_PASSWORD": (desired_db.get("POSTGRES_PASSWORD"), live_db.get("POSTGRES_PASSWORD")),
        "ConnectionStrings__Postgres": (
            desired_api.get("ConnectionStrings__Postgres"),
            live_api.get("ConnectionStrings__Postgres"),
        ),
        "Jwt__SigningKey": (desired_api.get("Jwt__SigningKey"), live_api.get("Jwt__SigningKey")),
        "Authentication__Google__ClientId": (
            desired_api.get("Authentication__Google__ClientId"),
            live_api.get("Authentication__Google__ClientId"),
        ),
        "Authentication__Google__ClientSecret": (
            desired_api.get("Authentication__Google__ClientSecret"),
            live_api.get("Authentication__Google__ClientSecret"),
        ),
        "Authentication__Google__FrontendBaseUrl": (
            desired_api.get("Authentication__Google__FrontendBaseUrl"),
            live_api.get("Authentication__Google__FrontendBaseUrl"),
        ),
    }

    mismatches = [name for name, (expected, current) in comparisons.items() if not expected or expected != current]
    if mismatches:
        raise RuntimeError("The protected file differs from running containers for: " + ", ".join(mismatches))

    expected_images = {
        "api": image,
        "frontend": docker_output("inspect", "--format", "{{.Config.Image}}", frontend_id),
    }
    for service, running_image in expected_images.items():
        if services[service]["image"] != running_image:
            raise RuntimeError(f"The rendered {service} image differs from the running image")

    print("Protected environment matches the running API and database; no secret values displayed")


if __name__ == "__main__":
    try:
        main()
    except (KeyError, OSError, ValueError, RuntimeError) as error:
        print(f"Verification failed: {error}", file=sys.stderr)
        sys.exit(1)
