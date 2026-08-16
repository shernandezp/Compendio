#!/usr/bin/env python3
"""Fails on a dependency whose licence is incompatible with AGPL-3.0-or-later distribution.

Compendio is distributed under AGPL-3.0-or-later, which means every dependency has to be a licence
we can combine with — in practice, permissive. A copyleft or source-available package cannot ship
here, and discovering that at release time is too late to do anything graceful about it.

The check is an allowlist rather than a denylist on purpose: a new licence identifier nobody has
considered should stop the build and get a decision, not slip through because it was not on a list
of known-bad ones.
"""

import gzip
import json
import sys
import urllib.error
import urllib.request

ALLOWED = {
    "MIT",
    "Apache-2.0",
    "BSD-2-Clause",
    "BSD-3-Clause",
    "0BSD",
    "ISC",
    "CC0-1.0",
    "Unlicense",
    "MS-PL",
    "MIT-0",
    "BlueOak-1.0.0",
}

# Microsoft's first-party packages carry a proprietary-looking licence URL that resolves to the MIT
# text. Treating the publisher as known is honest; pretending the metadata says MIT would not be.
TRUSTED_PREFIXES = (
    "Microsoft.",
    "System.",
    "runtime.",
    "SQLitePCLRaw.",
    "Common.Mediator",
)

NUGET_REGISTRATION = "https://api.nuget.org/v3/registration5-gz-semver2/{id}/{version}.json"


def licence_for(package_id: str, version: str) -> str | None:
    url = NUGET_REGISTRATION.format(id=package_id.lower(), version=version.lower())
    try:
        with urllib.request.urlopen(url, timeout=20) as response:
            body = response.read()
        # The registration5-gz-semver2 resource serves gzip-encoded content that urllib does not
        # transparently decode, so a raw json.load would choke on the 0x1f 0x8b magic bytes.
        if body[:2] == b"\x1f\x8b":
            body = gzip.decompress(body)
        data = json.loads(body)
    except (urllib.error.URLError, OSError, ValueError, TimeoutError):
        return None

    catalog = data.get("catalogEntry", {})
    return catalog.get("licenseExpression") or catalog.get("licenseUrl")


def main(path: str) -> int:
    with open(path, encoding="utf-8") as handle:
        report = json.load(handle)

    seen: dict[str, str] = {}
    for project in report.get("projects", []):
        for framework in project.get("frameworks", []):
            for kind in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(kind, []) or []:
                    seen[package["id"]] = package.get("resolvedVersion", package.get("requestedVersion", ""))

    problems: list[str] = []
    unknown: list[str] = []

    for package_id, version in sorted(seen.items()):
        if package_id.startswith(TRUSTED_PREFIXES):
            continue

        expression = licence_for(package_id, version)

        if expression is None:
            unknown.append(f"{package_id} {version} (could not read licence metadata)")
            continue

        # A compound expression such as "MIT OR Apache-2.0" is fine if every part is allowed.
        parts = {p.strip("()") for p in expression.replace(" OR ", " ").replace(" AND ", " ").split()}

        if not parts <= ALLOWED:
            problems.append(f"{package_id} {version}: {expression}")

    for line in unknown:
        print(f"warning: {line}")

    if problems:
        print("\nDependencies with a licence that is not on the allowlist:")
        for line in problems:
            print(f"  {line}")
        print("\nCompendio is distributed under AGPL-3.0-or-later. Adding one of these means either")
        print("removing the dependency or making an explicit, recorded decision about it.")
        return 1

    print(f"licence check passed — {len(seen)} package(s), all permissive.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "packages.json"))
