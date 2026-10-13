"""Keep the normal suite exhaustive and the required aggregate fail-closed."""
from pathlib import Path
import re
import unittest


WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/ci.yml"


class ShardedWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.text = WORKFLOW.read_text(encoding="utf-8")

    def job(self, name):
        match = re.search(rf"^  {re.escape(name)}:\n(.*?)(?=^  [\w-]+:|\Z)",
                          self.text, re.M | re.S)
        self.assertIsNotNone(match, f"Missing CI job {name}")
        return match.group(1)

    def test_normal_suite_has_three_parallel_bounded_shards(self):
        job = self.job("normal-shards")
        self.assertIn("timeout-minutes: 10", job)
        self.assertIn("fail-fast: false", job)
        self.assertIn("name: Normal suite (${{ matrix.shard }})", job)
        self.assertEqual(3, len(re.findall(r"- shard: ", job)))
        self.assertNotIn("continue-on-error", job)

    def test_filters_are_an_exhaustive_disjoint_partition(self):
        job = self.job("normal-shards")
        filters = re.findall(r"filter: '([^']+)'", job)
        common = "Category!=KnownDefect"
        telemetry = "FullyQualifiedName~BroadcastRouter.Tests.PhaseBTelemetryMeasurementTests."
        standby = "FullyQualifiedName~BroadcastRouter.Tests.StandbyLifecycleTests."
        self.assertEqual([
            f"{common}&{telemetry}", f"{common}&{standby}",
            f"{common}&{telemetry.replace('~', '!~')}&{standby.replace('~', '!~')}",
        ], filters)
        self.assertIn('--filter "${{ matrix.filter }}"', job)
        self.assertIn("dotnet test", job)
        self.assertNotIn("--list-tests", job)

    def test_required_name_stays_stable_and_fails_if_any_shard_does_not_pass(self):
        job = self.job("main-suite")
        self.assertIn("name: Main suite", job)
        self.assertIn("needs: normal-shards", job)
        self.assertIn("if: ${{ always() }}", job)
        self.assertIn("'${{ needs.normal-shards.result }}' -ne 'success'", job)
        self.assertIn("throw", job)
        self.assertNotIn("continue-on-error", job)

    def test_every_required_job_is_bounded_and_keeps_existing_names(self):
        for job_name, display_name in (("build", "Build"), ("known-defect", "KnownDefect"),
                                       ("main-suite", "Main suite")):
            job = self.job(job_name)
            self.assertIn(f"name: {display_name}", job)
            timeout = re.search(r"timeout-minutes: (\d+)", job)
            self.assertIsNotNone(timeout)
            self.assertLessEqual(int(timeout.group(1)), 10)

    def test_all_shard_results_are_retained_even_on_failure(self):
        job = self.job("normal-shards")
        self.assertIn("name: normal-suite-${{ matrix.shard }}", job)
        self.assertIn("if: ${{ always() }}", job)
        self.assertIn("if-no-files-found: error", job)
        self.assertIn("*.trx", job)


if __name__ == "__main__":
    unittest.main()
