#!/usr/bin/env python3
"""只读已有收据，不执行测试；输出观测成本而非缓存或验收证明。"""
import argparse
import datetime as dt
import json
import math
import os
import re
import stat

MAX_FILES = 256
MAX_FILE_BYTES = 4 * 1024 * 1024
MAX_TOTAL_BYTES = 16 * 1024 * 1024
GROUP = re.compile(r"[a-z0-9][a-z0-9-]{0,95}\Z")
HEX = re.compile(r"[a-fA-F0-9]{64}\Z")
ROLES = {"repository", "build", "selected", "strict-development-resources", "default-tcp", "additional-selected-tcp"}
BUILD_STEPS = {"Restore", "EvaluationProbe", "BuildNoRestore"}


class Invalid(ValueError):
    pass


def require(condition, code="invalid-schema"):
    if not condition:
        raise Invalid(code)


def number(value):
    require(type(value) in (int, float) and math.isfinite(value) and 0 <= value <= 2**53, "invalid-metric")
    return value


def integer(value):
    require(type(value) is int and 0 <= value <= 2**53, "invalid-integer")
    return value


def timestamp(value):
    require(isinstance(value, str) and len(value) <= 64, "invalid-time")
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
        require(parsed.tzinfo is not None, "invalid-time")
        return parsed
    except (ValueError, OverflowError):
        raise Invalid("invalid-time") from None


def interval(start, end):
    elapsed = (timestamp(end) - timestamp(start)).total_seconds() * 1000
    return round(number(elapsed), 3)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate-json-key")
        result[key] = value
    return result


def shape(value, depth=0):
    require(depth <= 16, "json-depth-limit")
    if isinstance(value, dict):
        for child in value.values():
            shape(child, depth + 1)
    elif isinstance(value, list):
        for child in value:
            shape(child, depth + 1)


def read_json(path, budget):
    # Linux逐组件nofollow打开；不递归枚举，也不跟随路径中间目录链接。
    require(hasattr(os, "O_NOFOLLOW") and hasattr(os, "O_DIRECTORY"), "unsupported-platform")
    parts = os.path.abspath(path).split(os.sep)
    directory = os.open(os.sep, os.O_RDONLY | os.O_DIRECTORY)
    descriptor = None
    try:
        for part in parts[1:-1]:
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
            os.close(directory)
            directory = child
        descriptor = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
        before = os.fstat(descriptor)
        require(stat.S_ISREG(before.st_mode), "not-regular-file")
        require(before.st_size <= MAX_FILE_BYTES and budget[0] + before.st_size <= MAX_TOTAL_BYTES, "input-byte-limit")
        budget[0] += before.st_size
        with os.fdopen(descriptor, "rb") as stream:
            descriptor = None
            raw = stream.read(MAX_FILE_BYTES + 1)
            after = os.fstat(stream.fileno())
        require(len(raw) == before.st_size and (before.st_size, before.st_mtime_ns, before.st_ctime_ns) ==
                (after.st_size, after.st_mtime_ns, after.st_ctime_ns), "input-changed")
        current = os.stat(parts[-1], dir_fd=directory, follow_symlinks=False)
        require((before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns, before.st_ctime_ns) ==
                (current.st_dev, current.st_ino, current.st_size, current.st_mtime_ns, current.st_ctime_ns), "input-changed")
        value = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique_object,
                           parse_constant=lambda _: (_ for _ in ()).throw(Invalid("nonfinite-json")))
        shape(value)
        require(isinstance(value, dict))
        return value
    except (OSError, UnicodeError):
        raise Invalid("unreadable-or-linked-input") from None
    except Invalid:
        raise
    except (ValueError, RecursionError):
        raise Invalid("invalid-json") from None
    finally:
        if descriptor is not None:
            os.close(descriptor)
        os.close(directory)


def group_name(value):
    require(isinstance(value, str) and GROUP.fullmatch(value), "invalid-group")
    return value


def receipt(data):
    require(type(data.get("SchemaVersion")) is int and data["SchemaVersion"] == 2, "unsupported-receipt-schema")
    require(data.get("Execution") == "executed" and data.get("Scope") == "group")
    name = group_name(data["Group"])
    require(isinstance(data.get("RunId"), str) and re.fullmatch(r"[a-f0-9]{32}", data["RunId"]))
    selected = data["SelectedGroups"]
    require(isinstance(selected, list) and 0 < len(selected) <= MAX_FILES)
    for entry in selected:
        group_name(entry)
    require(len(set(selected)) == len(selected) and name in selected)
    status, coverage = data["Status"], data["Coverage"]
    require(status in ("running", "passed", "failed", "interrupted") and coverage in ("complete", "partial", "unknown"))
    require(type(data["Passed"]) is bool and data["Passed"] == (status == "passed"))
    skips = data["Skips"]
    require(isinstance(skips, list) and len(skips) <= 1024)
    for skip in skips:
        require(isinstance(skip, dict) and isinstance(skip.get("Code"), str) and
                re.fullmatch(r"[A-Za-z0-9.-]{1,96}", skip["Code"]) and
                isinstance(skip.get("Reason"), str) and 0 < len(skip["Reason"]) <= 1024)
    require(not skips or coverage == "partial")
    identity = data["Identity"]
    require(isinstance(identity, dict))
    for field in ("Source", "Input", "Runner"):
        require(field in identity and (identity[field] is None or isinstance(identity[field], str) and HEX.fullmatch(identity[field])))
    require(type(data["CacheEligible"]) is bool)
    require(not data["CacheEligible"] or status == "passed" and coverage == "complete" and not skips and all(identity[field] for field in ("Source", "Input", "Runner")))
    timestamp(data["StartedUtc"])
    if status == "running":
        require(data["CompletedUtc"] is None)
    else:
        interval(data["StartedUtc"], data["CompletedUtc"])
    integer(data["Assertions"])
    elapsed = integer(data["ElapsedMs"])
    for field in ("FixtureStarts", "FixtureStartMs", "ResourceLoadMs"):
        if field in data:
            integer(data[field])
    # 失败/中断分支未填准备指标，默认0不能解释为没有准备成本。
    prep = status == "passed" and all(field in data for field in ("FixtureStarts", "FixtureStartMs", "ResourceLoadMs"))
    return {"Group": name, "Outcome": status + ("-" + coverage if status == "passed" else ""),
            "Coverage": coverage, "SkipCount": len(skips), "ElapsedMs": elapsed if status != "running" else None,
            "FixtureStarts": data.get("FixtureStarts") if prep else None,
            "FixtureStartMs": data.get("FixtureStartMs") if prep else None,
            "ResourceLoadMs": data.get("ResourceLoadMs") if prep else None,
            "PreparationEvidence": "observed-inclusive" if prep else "unknown"}


def supervisor(data, role):
    require(type(data.get("schema")) is int and data["schema"] == 1, "unsupported-supervisor-schema")
    require(role in ROLES, "invalid-stage-role")
    state = data["state"]
    require(state in ("completed", "failed", "timeout", "cancelled", "output-limit", "termination-unknown"))
    require(type(data["descendantsTerminated"]) is bool)
    require(data["exitCode"] is None or type(data["exitCode"]) is int)
    elapsed = interval(data["startedUtc"], data["completedUtc"])
    terminal = state == "completed" and data["exitCode"] == 0 and data["descendantsTerminated"]
    return {"Role": role, "Outcome": "completed-process-only" if terminal else "failed-or-unconfirmed",
            "ElapsedMs": elapsed, "Basis": "supervisor-utc-span-includes-cleanup", "Coverage": "not-established"}


def build_measurement(data):
    require(type(data.get("Schema")) is int and data["Schema"] == 1, "unsupported-build-schema")
    require(type(data["Valid"]) is bool)
    require(data["Scenario"] in ("Cold", "WarmUnchanged", "WarmChangedTest") and
            data["Profile"] in ("Default", "CompilerCpu1", "CompilerCpu2"))
    interval(data["StartedUtc"], data["FinishedUtc"])
    steps = data["Steps"]
    require(isinstance(steps, list) and len(steps) <= 3)
    seen, rows = set(), []
    for step in steps:
        require(isinstance(step, dict) and step.get("Name") in BUILD_STEPS and step["Name"] not in seen)
        seen.add(step["Name"])
        elapsed = number(step["ElapsedMs"])
        require(step.get("ExitCode") is None or type(step["ExitCode"]) is int)
        completed = step.get("ExitCode") == 0 and not step.get("Failure")
        rows.append({"Step": step["Name"], "ElapsedMs": elapsed,
                     "Outcome": "observed-completed" if completed else "failed-or-partial"})
    valid = data["Valid"]
    require(not valid or seen == BUILD_STEPS and all(row["Outcome"] == "observed-completed" for row in rows) and
            data.get("Failure") is None and data.get("ProcessCleanup") == "observed processes exited")
    return {"Outcome": "declared-valid-measurement" if valid else "invalid-or-partial-measurement",
            "Scenario": data["Scenario"], "Profile": data["Profile"], "Steps": rows,
            "MissingSteps": sorted(BUILD_STEPS - seen), "CompilerAnalyzerCopyBreakdown": "unknown-no-binlog-analysis"}


def inspect(receipt_paths=(), stages=(), measurements=(), expected=()):
    require(0 < len(receipt_paths) + len(stages) + len(measurements) <= MAX_FILES, "input-file-limit")
    expected = list(expected)
    for name in expected:
        group_name(name)
    require(len(expected) <= MAX_FILES and len(set(expected)) == len(expected), "invalid-expected-groups")
    report = {"SchemaVersion": 1, "State": "observed-only", "Groups": [], "Stages": [], "BuildMeasurements": [],
              "Issues": [], "DuplicateReceiptCopies": 0, "ExpectedGroups": expected,
              "Association": "explicit-input-selection-not-proof-of-one-run", "ReuseSafety": "not-established",
              "EstimatedSavingsMs": None, "ValidationToolOverheadMs": None,
              "StartupBuckets": {"State": "unknown-no-run-bound-stage-evidence"},
              "Notes": ["no-tests-executed", "parallel-group-times-are-not-wall-time", "preparation-times-are-inclusive-do-not-add",
                        "history-timings-are-not-current-evidence", "build-span-is-not-compiler-time",
                        "no-paths-environment-or-log-body-exported"]}
    budget, source_records, conflicts = [0], {}, set()
    inputs = [("receipt", None, path) for path in receipt_paths] + [("stage", role, path) for role, path in stages] + [("build", None, path) for path in measurements]
    for index, (kind, role, path) in enumerate(inputs, 1):
        ref = "input-" + str(index)
        try:
            data = read_json(path, budget)
            if kind == "receipt":
                row = receipt(data)
                name = row["Group"]
                if name in source_records:
                    if data == source_records[name]:
                        report["DuplicateReceiptCopies"] += 1
                    else:
                        conflicts.add(name)
                        report["Issues"].append({"Input": ref, "Code": "conflicting-group-evidence"})
                else:
                    source_records[name] = data
                    row["Input"] = ref
                    report["Groups"].append(row)
            elif kind == "stage":
                row = supervisor(data, role)
                row["Input"] = ref
                report["Stages"].append(row)
            else:
                row = build_measurement(data)
                row["Input"] = ref
                report["BuildMeasurements"].append(row)
        except (Invalid, KeyError, TypeError, OverflowError) as error:
            report["Issues"].append({"Input": ref, "Code": str(error) if isinstance(error, Invalid) else "invalid-schema"})
    for row in report["Groups"]:
        if row["Group"] in conflicts:
            row.update(Outcome="conflicting-evidence", Coverage="unknown", ElapsedMs=None,
                       FixtureStarts=None, FixtureStartMs=None, ResourceLoadMs=None, PreparationEvidence="unknown")
    report["MissingGroups"] = sorted(set(expected) - set(source_records))
    report["SelectionCoverage"] = "unknown" if not expected else "missing" if report["MissingGroups"] else "observed-not-verified"
    report["Groups"].sort(key=lambda row: (row["ElapsedMs"] is None, -(row["ElapsedMs"] or 0), row["Group"]))
    report["SlowestGroups"] = [row["Group"] for row in report["Groups"] if row["ElapsedMs"] is not None][:10]
    report["RepeatedPreparationCandidates"] = [row["Group"] for row in report["Groups"] if (row["FixtureStarts"] or 0) > 1]
    if report["Issues"] or report["MissingGroups"] or any(row["Outcome"] != "passed-complete" for row in report["Groups"]) or any(
            row["Outcome"] != "completed-process-only" for row in report["Stages"]) or any(
            row["Outcome"] != "declared-valid-measurement" for row in report["BuildMeasurements"]):
        report["State"] = "incomplete-or-conflicting-evidence"
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receipt", action="append", default=[])
    parser.add_argument("--stage", action="append", nargs=2, metavar=("ROLE", "PATH"), default=[])
    parser.add_argument("--build-measurement", action="append", default=[])
    parser.add_argument("--expected-group", action="append", default=[])
    args = parser.parse_args()
    try:
        result = inspect(args.receipt, args.stage, args.build_measurement, args.expected_group)
    except Invalid as error:
        result = {"SchemaVersion": 1, "State": "rejected-input-selection", "Code": str(error)}
    print(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False))
    return 0 if result["State"] == "observed-only" else 2


if __name__ == "__main__":
    raise SystemExit(main())
