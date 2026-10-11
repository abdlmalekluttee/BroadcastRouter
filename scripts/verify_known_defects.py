"""Strict expected-assertion verifier. A successful check does NOT close defects."""
import argparse
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def validate(root, registry, exit_code):
    if exit_code != 1:
        raise ValueError("Expected the assertion-failure test exit code 1, not success or runner failure")
    expected = {item["test"]: item for item in registry}
    if not expected or len(expected) != len(registry):
        raise ValueError("Registry must contain unique open test identities")
    summary = root.find("t:ResultSummary", NS)
    if summary is None or summary.get("outcome") != "Failed":
        raise ValueError("Run did not complete with assertion failures")
    # The pinned xUnit adapter also emits each assertion's display line as a
    # RunInfo Error. Accept ONLY that exact echo for a registered test, once.
    # An adapter crash/diagnostic, extra suffix or unknown test still fails CI.
    echoes = set()
    for info in summary.findall(".//t:RunInfo", NS):
        line = info.findtext("t:Text", default="", namespaces=NS).strip()
        match = re.fullmatch(r"\[xUnit\.net \d{2}:\d{2}:\d{2}\.\d{2}\]\s+(.+) \[FAIL\]", line)
        if info.get("outcome") != "Error" or match is None or match[1] not in expected or match[1] in echoes:
            raise ValueError("Runner-level diagnostic is not an expected assertion echo")
        echoes.add(match[1])
    if summary.find(".//t:ErrorInfo", NS) is not None:
        raise ValueError("Runner-level error metadata is present")
    counters = summary.find("t:Counters", NS)
    if counters is None:
        raise ValueError("Missing test counters")
    for field in ("total", "executed", "failed"):
        if counters.get(field) != str(len(expected)):
            raise ValueError("Unexpected test count: " + field)
    for field in ("passed", "error", "timeout", "aborted", "notExecuted"):
        if counters.get(field) != "0":
            raise ValueError("Unexpected test outcome: " + field)
    results = root.findall("t:Results/t:UnitTestResult", NS)
    seen = set()
    for result in results:
        name = result.get("testName")
        if name not in expected or name in seen:
            raise ValueError("Unexpected or duplicate test identity")
        seen.add(name)
        if result.get("outcome") != "Failed":
            raise ValueError("Unexpected pass or non-assertion outcome: " + name)
        message = result.findtext("t:Output/t:ErrorInfo/t:Message", default="", namespaces=NS)
        stack = result.findtext("t:Output/t:ErrorInfo/t:StackTrace", default="", namespaces=NS)
        if message.strip() != expected[name]["message"]:
            raise ValueError("Different assertion/error: " + name)
        first_frame = stack.strip().splitlines()[0] if stack.strip() else ""
        if not re.fullmatch(r"at " + re.escape(name) + r"\(\) in .*[\\/]KnownDefectTests\.cs:line [1-9][0-9]*", first_frame):
            raise ValueError("Failure did not originate at the registered assertion: " + name)
    if seen != set(expected):
        raise ValueError("Missing known-defect results")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--trx", required=True, type=Path)
    parser.add_argument("--registry", required=True, type=Path)
    parser.add_argument("--exit-code", required=True, type=int)
    args = parser.parse_args()
    try:
        registry = json.loads(args.registry.read_text(encoding="utf-8-sig"))
        validate(ET.parse(args.trx).getroot(), registry, args.exit_code)
    except (ValueError, OSError, ET.ParseError, KeyError, TypeError) as error:
        print("KnownDefect verification FAILED: " + str(error), file=sys.stderr)
        return 1
    for entry in registry:
        print("OPEN " + entry["finding"] + " | " + entry["test"] + " | exact expected assertion observed")
    print(f"Strict KnownDefect contract passed; {len(registry)} defects remain OPEN, not fixed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
