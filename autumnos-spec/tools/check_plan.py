#!/usr/bin/env python3
"""Validate plan structure only; this does not build or test AutumnOS."""
from __future__ import annotations
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def load(name: str) -> dict:
    return json.loads((ROOT / name).read_text(encoding="utf-8"))

def ensure(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)

def main() -> None:
    requirements = load("requirements.json")["requirements"]
    tests = load("acceptance-tests.json")["tests"]
    tasks = load("tasks/index.json")["tasks"]
    req_map = {item["id"]: item for item in requirements}
    test_map = {item["id"]: item for item in tests}
    task_map = {item["id"]: item for item in tasks}
    for values, mapped, label in [(requirements, req_map, "requirement"),
                                   (tests, test_map, "test"), (tasks, task_map, "task")]:
        ensure(len(values) == len(mapped), f"Duplicate {label} IDs")
    for req in requirements:
        ensure(req["task_id"] in task_map, f"Unknown task: {req['id']}")
        ensure(bool(req["acceptance_test_ids"]), f"No tests: {req['id']}")
        for tid in req["acceptance_test_ids"]:
            ensure(tid in test_map, f"Unknown test: {tid}")
            ensure(req["id"] in test_map[tid]["requirement_ids"], f"Nonreciprocal mapping: {tid}")
    for test in tests:
        ensure(bool(test["assertions"]), f"No assertions: {test['id']}")
        for rid in test["requirement_ids"]:
            ensure(rid in req_map, f"Unknown requirement: {rid}")
            ensure(test["id"] in req_map[rid]["acceptance_test_ids"], f"Nonreciprocal test: {rid}")
    assigned = []
    for task in tasks:
        ensure((ROOT / task["path"]).is_file(), f"Missing task document: {task['path']}")
        assigned.extend(task["requirement_ids"])
        for rid in task["requirement_ids"]:
            ensure(rid in req_map and req_map[rid]["task_id"] == task["id"], f"Bad owner: {rid}")
        for dep in task["depends_on"]:
            ensure(dep in task_map, f"Unknown dependency: {dep}")
    ensure(len(assigned) == len(set(assigned)) == len(requirements), "Task assignment mismatch")
    visited: set[str] = set()
    active: set[str] = set()
    def visit(tid: str) -> None:
        ensure(tid not in active, f"Dependency cycle: {tid}")
        if tid in visited:
            return
        active.add(tid)
        for dep in task_map[tid]["depends_on"]:
            visit(dep)
        active.remove(tid)
        visited.add(tid)
    for tid in task_map:
        visit(tid)
    required = ["AGENTS.md", "README.md", "prompts/START.md", "docs/05-release-gates.md"]
    for name in required:
        ensure((ROOT/name).is_file(), f"Missing file: {name}")
    result = {
        "plan_structure": "passed",
        "requirements": len(requirements),
        "test_definitions": len(tests),
        "task_definitions": len(tasks),
        "windows_build_executed": False,
        "product_tests_executed_by_this_script": False,
        "public_release_performed_by_this_script": False,
        "meaning": "Only plan consistency was checked; product verification remains separate."
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))

if __name__ == "__main__":
    main()
