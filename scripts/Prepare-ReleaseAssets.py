#!/usr/bin/env python3
"""Verify the approved Windows build and stage its three public release assets."""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import struct
import tempfile


BUILD_ID = "T06-actions-37608567667-1"
SOURCE_COMMIT = "f83e005388701d2438c0e3e99a7be37114e337da"
RELEASE_LABEL = "meta0.0.2-20261007"
BUILD_VERSION = "0.5.1-local.5"
EXE_SHA256 = "6f57b4c1ab526d29e181f230a3db74eafff4fdec32f967f4405e6554f942a2e4"
RUN_URL = "https://github.com/paimeng5201314/autumnlab/actions/runs/37608567667"
NATIVE_NOT_RUN = "not_run_ci_no_interactive_desktop"
TEST_NAMES = (
    "tests", "sdk-tests", "sample-tests", "native-client-tests",
    "server-http-tests", "t04-tools-tests", "machine-contracts",
)
BUILD_STAGES = (
    "install-pinned-sdk", "restore-locked-contract-tools", "cache-verified-webview",
    "build", "test-current-build", "prepare-complete-directory", "package-single-file",
)


class ValidationError(Exception):
    pass


def require(condition, message):
    if not condition:
        raise ValidationError(message)


def exact_file(root, *parts):
    """Only fixed artifact-relative paths are read; no report-supplied paths are used."""
    path = root
    for index, part in enumerate(parts):
        path = path / part
        mode = path.lstat().st_mode
        require(not stat.S_ISLNK(mode), "Artifact paths must not contain symbolic links.")
        expected = stat.S_ISREG if index == len(parts) - 1 else stat.S_ISDIR
        require(expected(mode), "An expected artifact file or directory is missing.")
    return path


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "JSON contains a duplicate property.")
        result[key] = value
    return result


def reject_constant(_):
    raise ValidationError("Non-finite JSON numbers are not permitted.")


def read_report(path):
    require(path.stat().st_size <= 16 * 1024 * 1024, "JSON report exceeds the size limit.")
    with path.open(encoding="utf-8-sig") as source:
        value = json.load(source, object_pairs_hook=unique_object, parse_constant=reject_constant)
    require(isinstance(value, dict), "A JSON report must be an object.")
    return value


def same_identity(report, snapshot_id, label):
    require(report.get("build_id") == BUILD_ID, f"{label}: wrong build identity.")
    require(report.get("source_snapshot_id") == snapshot_id, f"{label}: source snapshot mismatch.")


def validate_snapshot(report):
    snapshot_id = report.get("source_snapshot_id")
    require(isinstance(snapshot_id, str) and re.fullmatch(r"sha256:[0-9a-f]{64}", snapshot_id),
            "Invalid source snapshot identifier.")
    require(report.get("algorithm") == "SHA-256", "Unexpected snapshot hash algorithm.")
    require(report.get("commit") in ("not_applicable", SOURCE_COMMIT), "Unexpected snapshot commit.")
    files = report.get("files")
    require(isinstance(files, list) and files, "Source snapshot has no file inventory.")
    lines, seen = [], set()
    for item in files:
        require(isinstance(item, dict), "Invalid source inventory entry.")
        name, digest, size = item.get("path"), item.get("sha256"), item.get("bytes")
        require(isinstance(name, str) and name and "\\" not in name and ":" not in name
                and not any(ord(char) < 32 for char in name), "Invalid source inventory path.")
        relative = PurePosixPath(name)
        require(not relative.is_absolute() and all(part not in ("", ".", "..") for part in name.split("/"))
                and name not in seen, "Duplicate or unsafe source inventory path.")
        require(isinstance(digest, str) and re.fullmatch(r"[0-9a-f]{64}", digest), "Invalid source file hash.")
        require(type(size) is int and size >= 0, "Invalid source file size.")
        seen.add(name)
        lines.append(f"{digest}  {name}")
    # Preserve the producer's PowerShell-sorted order; Python's ordinal order can differ.
    actual = hashlib.sha256("\n".join(lines).encode("utf-8")).hexdigest()
    require(snapshot_id == "sha256:" + actual, "Source snapshot inventory hash mismatch.")
    return snapshot_id


def validate_pe(path, expected_size):
    require(type(expected_size) is int and expected_size >= 128, "Invalid packaged EXE size.")
    require(path.stat().st_size == expected_size, "EXE size differs from the packaging report.")
    with path.open("rb") as source:
        dos = source.read(64)
        require(len(dos) == 64 and dos[:2] == b"MZ", "EXE is missing its DOS header.")
        offset = struct.unpack_from("<I", dos, 0x3C)[0]
        require(64 <= offset <= expected_size - 26, "Invalid PE header offset.")
        source.seek(offset)
        header = source.read(26)
        require(header[:4] == b"PE\0\0", "EXE is missing its PE signature.")
        machine, sections = struct.unpack_from("<HH", header, 4)
        optional_size, flags, magic = struct.unpack_from("<HHH", header, 20)
        require(machine == 0x8664 and magic == 0x20B and sections > 0
                and optional_size >= 112 and offset + 24 + optional_size <= expected_size
                and flags & 0x0002 and not flags & 0x2000, "EXE must be a Windows x64 executable PE image.")
        source.seek(0)
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    require(digest == EXE_SHA256, "EXE SHA-256 does not match the approved build.")


def validate_artifacts(artifact_root, evidence_root):
    package = read_report(exact_file(artifact_root, "builds", BUILD_ID, "package-single-file.json"))
    snapshot = read_report(exact_file(artifact_root, "builds", BUILD_ID, "source-snapshot.json"))
    pipeline = read_report(exact_file(artifact_root, "build-pipelines", BUILD_ID, "pipeline.json"))
    snapshot_id = validate_snapshot(snapshot)
    for label, report in (("Package", package), ("Pipeline", pipeline)):
        same_identity(report, snapshot_id, label)
        require(report.get("version") == BUILD_VERSION and report.get("release_label") == RELEASE_LABEL,
                f"{label}: version mismatch.")
        require(report.get("executable_sha256") == EXE_SHA256, f"{label}: executable hash mismatch.")
    require(package.get("status") == "prepared" and package.get("delivery_files") == 1,
            "Package is not the approved single-file delivery.")
    require(package.get("authenticode") == "not_signed", "Unexpected package signature status.")
    require(pipeline.get("status") == "packaged_native_not_run" and pipeline.get("skip_native_smoke") is True
            and pipeline.get("native_smoke") == NATIVE_NOT_RUN and pipeline.get("native_tests") == NATIVE_NOT_RUN,
            "Pipeline status does not match the approved validation boundary.")
    stages = pipeline.get("stages")
    require(isinstance(stages, list), "Pipeline stage evidence is missing.")
    by_name = {}
    for stage in stages:
        require(isinstance(stage, dict) and isinstance(stage.get("name"), str), "Invalid pipeline stage.")
        name = stage["name"]
        require(name not in by_name, "Duplicate pipeline stage.")
        expected = NATIVE_NOT_RUN if name == "native-single-file-smoke" else "passed"
        require(stage.get("status") == expected, "A pipeline stage did not succeed.")
        by_name[name] = stage
    require(all(name in by_name for name in BUILD_STAGES), "Required pipeline stage evidence is missing.")
    for name in TEST_NAMES:
        report = read_report(exact_file(evidence_root, "builds", BUILD_ID, name + ".json"))
        same_identity(report, snapshot_id, name)
        require(report.get("status") == "passed", f"{name}: tests did not pass.")
        if name in ("sdk-tests", "sample-tests", "machine-contracts"):
            require(type(report.get("exit_code")) is int and report["exit_code"] == 0,
                    f"{name}: unsuccessful test command.")
        if name in ("tests", "t04-tools-tests"):
            results = report.get("results")
            total = report.get("total")
            require(type(total) is int and total > 0 and report.get("failed") == 0
                    and isinstance(results, list) and len(results) == total
                    and all(isinstance(item, dict) and item.get("status") == "passed" for item in results),
                    f"{name}: inconsistent individual test results.")
            if name == "tests":
                require(total == 649 and report.get("filters") == [], "The complete 649-case C# suite is required.")
                names = [item.get("name") for item in results]
                require(all(isinstance(value, str) and value for value in names) and len(set(names)) == total,
                        "The C# test report contains duplicate or unnamed cases.")
        if name == "server-http-tests":
            checks = report.get("checks")
            require(isinstance(checks, list) and len(checks) == 7
                    and all(isinstance(item, dict) and item.get("passed") is True for item in checks),
                    "Server HTTP checks did not all pass.")
    executable = exact_file(artifact_root, "single-file", BUILD_ID, "AutumnOS.exe")
    validate_pe(executable, package.get("bytes"))
    return executable, package["bytes"], snapshot_id


def stage_assets(executable, size, snapshot_id, output):
    require(not os.path.lexists(output), "Output directory already exists; refusing to overwrite assets.")
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix=".autumn-release-", dir=output.parent))
    try:
        staged_exe = temporary / "AutumnOS.exe"
        shutil.copyfile(executable, staged_exe)
        validate_pe(staged_exe, size)
        # Deliberate public projection: never copy report paths, arbitrary fields, or logs.
        info = {
            "schema_version": 1,
            "repository": "paimeng5201314/autumnlab",
            "release_label": RELEASE_LABEL,
            "internal_version": BUILD_VERSION,
            "build_id": BUILD_ID,
            "source_commit": SOURCE_COMMIT,
            "source_snapshot_id": snapshot_id,
            "build_run_url": RUN_URL,
            "artifact_ids": {"executable": 11476915941, "evidence": 11477035033},
            "executable": {"file": "AutumnOS.exe", "sha256": EXE_SHA256, "bytes": size, "platform": "win-x64"},
            "validation": {
                "csharp_tests": {"status": "passed", "passed": 649, "total": 649},
                "test_summaries": {name: "passed" for name in TEST_NAMES},
                "native_interactive_ui_tests": NATIVE_NOT_RUN,
                "complete_release_acceptance": "not_run",
            },
            "authenticode": "not_signed",
            "production_update_trust": "unconfigured_signed_install_disabled",
        }
        info_bytes = (json.dumps(info, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
        (temporary / "build-info.json").write_bytes(info_bytes)
        info_hash = hashlib.sha256(info_bytes).hexdigest()
        (temporary / "SHA256SUMS.txt").write_text(
            f"{EXE_SHA256}  AutumnOS.exe\n{info_hash}  build-info.json\n", encoding="utf-8")
        require(not os.path.lexists(output), "Output directory was created during validation.")
        temporary.rename(output)
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifact-dir", required=True, type=Path)
    parser.add_argument("--evidence-dir", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()
    try:
        for root in (args.artifact_dir, args.evidence_dir):
            require(root.is_dir() and not root.is_symlink(), "Artifact input must be a real directory.")
        executable, size, snapshot_id = validate_artifacts(args.artifact_dir, args.evidence_dir)
        stage_assets(executable, size, snapshot_id, args.output_dir.absolute())
    except (ValidationError, OSError, ValueError, RecursionError) as error:
        parser.exit(1, f"Release asset validation failed: {error}\n")
    print("Verified and staged AutumnOS.exe, SHA256SUMS.txt, and build-info.json.")


if __name__ == "__main__":
    main()
