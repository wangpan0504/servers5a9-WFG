"""已有指标只读汇总合同；不启动服务或加载资源。"""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("costs", Path(__file__).parents[1] / "Inspect-DevelopmentCosts.py")
costs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(costs)


def receipt(group="alpha", **changes):
    data = dict(SchemaVersion=2, Execution="executed", Scope="group", Group=group, RunId="a"*32,
                SelectedGroups=[group], Status="passed", Passed=True, Coverage="complete", Skips=[],
                Identity=dict(Source="1"*64, Input="2"*64, Runner="3"*64), CacheEligible=True,
                StartedUtc="2026-10-30T00:00:00Z", CompletedUtc="2026-10-30T00:00:01Z",
                Assertions=5, ElapsedMs=1000, FixtureStarts=2, FixtureStartMs=600, ResourceLoadMs=100)
    data.update(changes)
    return data


class CostsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def write(self, name, data):
        path = self.root / name
        path.write_text(json.dumps(data), encoding="utf-8")
        return str(path)

    def test_rank_inclusive_and_unknown_scope(self):
        fast = self.write("a", receipt())
        slow = self.write("b", receipt("beta", ElapsedMs=2000, FixtureStarts=0))
        result = costs.inspect([fast, slow], expected=["alpha", "beta", "missing"])
        self.assertEqual(result["SlowestGroups"], ["beta", "alpha"])
        self.assertEqual(result["RepeatedPreparationCandidates"], ["alpha"])
        self.assertEqual(result["MissingGroups"], ["missing"])
        self.assertIsNone(result["EstimatedSavingsMs"])
        self.assertIsNone(result["ValidationToolOverheadMs"])
        self.assertNotIn("TotalElapsedMs", result)
        self.assertEqual(result["StartupBuckets"]["State"], "unknown-no-run-bound-stage-evidence")

    def test_failed_zero_is_unknown_partial_is_not_complete(self):
        failed = receipt(Status="failed", Passed=False, CacheEligible=False, FixtureStarts=0, FixtureStartMs=0)
        partial = receipt("partial", Coverage="partial", CacheEligible=False, Skips=[dict(Code="missing", Reason="secret-path")])
        result = costs.inspect([self.write("a", failed), self.write("b", partial)])
        rows = {row["Group"]: row for row in result["Groups"]}
        self.assertIsNone(rows["alpha"]["FixtureStarts"])
        self.assertEqual(rows["partial"]["Outcome"], "passed-partial")
        self.assertEqual(result["State"], "incomplete-or-conflicting-evidence")
        self.assertNotIn("secret-path", json.dumps(result))

    def test_running_and_unknown(self):
        running = receipt(Status="running", Passed=False, CompletedUtc=None, CacheEligible=False)
        unknown = receipt("unknown", Coverage="unknown", CacheEligible=False)
        result = costs.inspect([self.write("a", running), self.write("b", unknown)])
        self.assertIsNone(next(row for row in result["Groups"] if row["Group"] == "alpha")["ElapsedMs"])
        self.assertEqual(result["State"], "incomplete-or-conflicting-evidence")

    def test_duplicate_copy_and_red_green_conflict(self):
        a = self.write("a", receipt())
        b = self.write("b", receipt())
        result = costs.inspect([a, b])
        self.assertEqual(result["DuplicateReceiptCopies"], 1)
        self.assertEqual(len(result["Groups"]), 1)
        red = self.write("red", receipt(Status="failed", Passed=False, CacheEligible=False))
        for paths in ([a, red], [red, a]):
            result = costs.inspect(paths)
            self.assertEqual(result["Groups"][0]["Outcome"], "conflicting-evidence")
            self.assertIsNone(result["Groups"][0]["ElapsedMs"])
            self.assertEqual(result["SlowestGroups"], [])

    def test_schema_and_metric_negative_cases(self):
        cases = [dict(SchemaVersion=1), dict(SchemaVersion=True), dict(Passed="true"), dict(ElapsedMs=-1),
                 dict(FixtureStarts=True), dict(ElapsedMs=float("inf")), dict(Group="/secret/file"),
                 dict(CompletedUtc="2026-10-29T00:00:00Z"), dict(StartedUtc="2026-10-30T00:00:00"),
                 dict(SelectedGroups=["other"]), dict(Coverage="complete", Skips=[dict(Code="skip", Reason="x")])]
        for index, changes in enumerate(cases):
            with self.subTest(index=index):
                result = costs.inspect([self.write(str(index), receipt(**changes))])
                self.assertEqual(len(result["Issues"]), 1)
                self.assertEqual(result["Groups"], [])

    def test_duplicate_keys_links_and_limits(self):
        path = self.root / "duplicate"
        path.write_text('{"SchemaVersion":2,"SchemaVersion":1}')
        self.assertEqual(costs.inspect([str(path)])["Issues"][0]["Code"], "duplicate-json-key")
        target = self.write("regular", receipt())
        (self.root / "linked").symlink_to(target)
        self.assertEqual(costs.inspect([str(self.root / "linked")])["Groups"], [])
        (self.root / "directory-link").symlink_to(self.root, target_is_directory=True)
        self.assertEqual(costs.inspect([str(self.root / "directory-link" / "regular")])["Groups"], [])
        path.write_bytes(b" " * (costs.MAX_FILE_BYTES + 1))
        self.assertEqual(costs.inspect([str(path)])["Issues"][0]["Code"], "input-byte-limit")
        with self.assertRaises(costs.Invalid):
            costs.inspect([target] * (costs.MAX_FILES + 1))
        nested = self.write("deep", {"value": [[[[[[[[[[[[[[[[[0]]]]]]]]]]]]]]]]]})
        self.assertEqual(costs.inspect([nested])["Issues"][0]["Code"], "json-depth-limit")

    def test_supervisor_terminal_and_redaction(self):
        data = dict(schema=1, state="completed", exitCode=0, descendantsTerminated=True,
                    startedUtc="2026-10-30T00:00:00Z", completedUtc="2026-10-30T00:00:02Z", error="secret-path")
        path = self.write("supervisor", data)
        result = costs.inspect(stages=[("build", path)])
        self.assertEqual(result["Stages"][0]["ElapsedMs"], 2000)
        self.assertEqual(result["Stages"][0]["Coverage"], "not-established")
        self.assertNotIn(str(self.root), json.dumps(result))
        self.assertNotIn("secret-path", json.dumps(result))
        for state in ("failed", "timeout", "cancelled", "output-limit", "termination-unknown"):
            data.update(state=state, descendantsTerminated=False)
            result = costs.inspect(stages=[("build", self.write("supervisor", data))])
            self.assertEqual(result["Stages"][0]["Outcome"], "failed-or-unconfirmed")

    def test_measurement_not_compiler_time_and_partial(self):
        data = dict(Schema=1, Valid=True, Scenario="Cold", Profile="Default",
                    StartedUtc="2026-10-30T00:00:00Z", FinishedUtc="2026-10-30T00:00:04Z",
                    Steps=[dict(Name=name, ElapsedMs=100, ExitCode=0, Arguments=["secret-path"])
                           for name in ("Restore", "EvaluationProbe", "BuildNoRestore")],
                    Failure=None, ProcessCleanup="observed processes exited")
        result = costs.inspect(measurements=[self.write("measurement", data)])
        row = result["BuildMeasurements"][0]
        self.assertEqual(row["CompilerAnalyzerCopyBreakdown"], "unknown-no-binlog-analysis")
        self.assertNotIn("secret-path", json.dumps(result))
        bad = copy.deepcopy(data)
        bad["Steps"].pop()
        self.assertEqual(len(costs.inspect(measurements=[self.write("bad", bad)])["Issues"]), 1)
        bad["Valid"] = False
        row = costs.inspect(measurements=[self.write("bad", bad)])["BuildMeasurements"][0]
        self.assertEqual(row["MissingSteps"], ["BuildNoRestore"])
        self.assertEqual(row["Outcome"], "invalid-or-partial-measurement")

    def test_total_budget_counts_duplicate_inputs(self):
        path = self.write("receipt", receipt())
        size = Path(path).stat().st_size
        self.assertEqual(costs.MAX_TOTAL_BYTES, 16 * 1024 * 1024)
        # 缩小同一个预算参数，避免为了边界回归额外读取16MiB。
        with patch.object(costs, "MAX_TOTAL_BYTES", 2 * size):
            result = costs.inspect([path, path, path])
        self.assertEqual(result["DuplicateReceiptCopies"], 1)
        self.assertEqual(result["Issues"], [{"Input": "input-3", "Code": "input-byte-limit"}])

    def test_supervisor_invalid_state_and_types(self):
        base = dict(schema=1, state="completed", exitCode=0, descendantsTerminated=True,
                    startedUtc="2026-10-30T00:00:00Z", completedUtc="2026-10-30T00:00:02Z")
        for changes in (dict(state="unknown-state"), dict(descendantsTerminated="true"),
                        dict(exitCode=True), dict(schema=True)):
            data = dict(base, **changes)
            self.assertEqual(len(costs.inspect(stages=[("build", self.write("stage", data))])["Issues"]), 1)

    def test_history_map_not_receipt(self):
        result = costs.inspect([self.write("timings", {"alpha": 1000})])
        self.assertEqual(result["Groups"], [])
        self.assertEqual(result["Issues"][0]["Code"], "unsupported-receipt-schema")


if __name__ == "__main__":
    unittest.main()
