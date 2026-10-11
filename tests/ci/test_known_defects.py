"""The expected-failure validator itself must fail closed."""
import importlib.util
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "verify_known_defects.py"


class KnownDefectValidatorTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(SCRIPT.is_file(), "Strict known-defect result validator is missing")
        spec = importlib.util.spec_from_file_location("known_validator", SCRIPT)
        self.validator = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.validator)
        self.registry = [{"test": "BroadcastRouter.Tests.KnownDefectTests.CorrectBehavior",
                          "finding": "R99", "message": "R99: specific assertion"}]
        self.root = ET.fromstring(r'''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results><UnitTestResult testName="BroadcastRouter.Tests.KnownDefectTests.CorrectBehavior"
             outcome="Failed" testId="id1"><Output><ErrorInfo><Message>R99: specific assertion</Message>
             <StackTrace>   at BroadcastRouter.Tests.KnownDefectTests.CorrectBehavior() in C:\fixture\KnownDefectTests.cs:line 10</StackTrace>
             </ErrorInfo></Output></UnitTestResult></Results>
          <ResultSummary outcome="Failed"><Counters total="1" executed="1" passed="0" failed="1" error="0" timeout="0" aborted="0" notExecuted="0" /></ResultSummary>
        </TestRun>''')
        self.ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

    def node(self, path):
        return self.root.find(path, self.ns)

    def check(self, code=1):
        return self.validator.validate(self.root, self.registry, code)

    def test_exact_assertion_failure_is_accepted(self):
        self.check()

    def test_unexpected_pass_is_rejected(self):
        self.node("t:Results/t:UnitTestResult").set("outcome", "Passed")
        with self.assertRaises(ValueError): self.check()

    def test_different_assertion_is_rejected(self):
        self.node(".//t:Message").text = "Expected true"
        with self.assertRaises(ValueError): self.check()

    def test_crash_with_expected_message_is_rejected(self):
        self.node(".//t:StackTrace").text = "at ChildCrashHandler()"
        with self.assertRaises(ValueError): self.check()

    def test_aborted_run_is_rejected(self):
        self.node("t:ResultSummary").set("outcome", "Aborted")
        with self.assertRaises(ValueError): self.check()

    def test_compile_or_runner_error_is_rejected(self):
        with self.assertRaises(ValueError): self.check(code=2)

    def test_missing_result_is_rejected(self):
        self.node("t:Results").clear()
        with self.assertRaises(ValueError): self.check()

    def test_duplicate_result_is_rejected(self):
        results = self.node("t:Results")
        results.append(ET.fromstring(ET.tostring(results[0])))
        with self.assertRaises(ValueError): self.check()

    def test_runner_level_error_is_rejected(self):
        namespace = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        infos = ET.SubElement(self.node("t:ResultSummary"), namespace + "RunInfos")
        ET.SubElement(infos, namespace + "RunInfo", outcome="Error")
        with self.assertRaises(ValueError): self.check()

    def test_runner_level_warning_is_rejected(self):
        namespace = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        infos = ET.SubElement(self.node("t:ResultSummary"), namespace + "RunInfos")
        ET.SubElement(infos, namespace + "RunInfo", outcome="Warning")
        with self.assertRaises(ValueError): self.check()

    def test_counter_mismatch_is_rejected(self):
        self.node("t:ResultSummary/t:Counters").set("error", "1")
        with self.assertRaises(ValueError): self.check()


if __name__ == "__main__":
    unittest.main(verbosity=2)
