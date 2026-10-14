import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('comparison', ROOT / 'scripts/tooling_comparison.py')
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)

PROJECT = '''<Project><ItemGroup>
<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="8.0.0" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
<PackageReference Include="xunit.runner.visualstudio" Version="3.1.0" PrivateAssets="all" />
<PackageReference Include="xunit" Version="2.9.3" />
</ItemGroup></Project>'''


class ComparisonTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.project = self.root / 'sample.csproj'
        self.project.write_text(PROJECT, encoding='utf-8')

    def test_baseline_is_byte_identical(self):
        original = self.project.read_bytes()
        result = comparison.prepare(self.project, 'old')
        self.assertEqual(original, self.project.read_bytes())
        self.assertEqual(0, result.get('changed_packages'))

    def test_each_variant_changes_exactly_one_package(self):
        for variant, old, new in [('timeprovider', '8.0.0', '8.10.0'),
                                   ('testsdk', '17.13.0', '17.14.1'),
                                   ('runner', '3.1.0', '3.1.5')]:
            with self.subTest(variant=variant):
                self.project.write_text(PROJECT, encoding='utf-8')
                result = comparison.prepare(self.project, variant)
                self.assertEqual(PROJECT.replace('Version="'+old+'"', 'Version="'+new+'"'),
                                 self.project.read_text(encoding='utf-8'))
                self.assertEqual(1, result.get('changed_packages'))

    def test_unknown_variant_refused_without_edit(self):
        with self.assertRaises(ValueError):
            comparison.prepare(self.project, 'all-new')
        self.assertEqual(PROJECT, self.project.read_text(encoding='utf-8'))

    def test_wrong_baseline_refused_without_edit(self):
        changed = PROJECT.replace('8.0.0', '8.10.0')
        self.project.write_text(changed, encoding='utf-8')
        with self.assertRaises(ValueError):
            comparison.prepare(self.project, 'testsdk')
        self.assertEqual(changed, self.project.read_text(encoding='utf-8'))

    def trx(self, outcome='Passed', count=1, error='', duplicate=False):
        result = f'<UnitTestResult testName="Synthetic.Case" outcome="{outcome}" startTime="2026-01-01T00:00:00Z" endTime="2026-01-01T00:00:01Z"><Output><ErrorInfo><Message>{error}</Message></ErrorInfo></Output></UnitTestResult>'
        path = self.root / 'main.trx'
        path.write_text(f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{result * (2 if duplicate else 1)}</Results><ResultSummary outcome="Completed"><Counters total="{count}" executed="{count}" passed="{count if outcome == "Passed" else 0}" failed="{count if outcome == "Failed" else 0}" notExecuted="0" /></ResultSummary></TestRun>', encoding='utf-8')
        return path

    def test_complete_results_pass(self):
        result = comparison.summarize(self.trx(), 0, 1)
        self.assertEqual('passed', result.get('classification'))
        self.assertEqual(1, result.get('total'))

    def test_actual_assertion_retained_and_never_passes(self):
        result = comparison.summarize(self.trx('Failed', error='Assert.False: owner still running'), 1, 1)
        self.assertEqual('test-failure', result.get('classification'))
        self.assertIn('Assert.False: owner still running', str(result))

    def test_missing_trx_never_passes(self):
        result = comparison.summarize(self.root / 'missing.trx', 1, 1)
        self.assertEqual('incomplete', result.get('classification'))

    def test_wrong_count_and_duplicate_never_pass(self):
        for args in [{'count': 2}, {'duplicate': True}]:
            path = self.trx(**args)
            self.assertEqual('incomplete', comparison.summarize(path, 0, 1).get('classification'))

    def test_nonzero_exit_with_green_trx_never_passes(self):
        result = comparison.summarize(self.trx(), 7, 1)
        self.assertEqual('runner-failure', result.get('classification'))

    def test_capture_retains_separate_streams_exit_and_timestamps(self):
        result = comparison.capture([sys.executable, '-c', 'import sys; print("out"); print("err", file=sys.stderr); sys.exit(9)'], self.root)
        self.assertEqual(9, result.get('exit_code'))
        self.assertEqual('out', (self.root / 'stdout.txt').read_text().strip())
        self.assertEqual('err', (self.root / 'stderr.txt').read_text().strip())
        self.assertLessEqual(result['started_utc'], result['ended_utc'])


if __name__ == '__main__':
    unittest.main()
