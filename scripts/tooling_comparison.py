"""Private-to-CI diagnostic helpers; never imported by the application."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

PACKAGES = {
    'timeprovider': ('Microsoft.Extensions.TimeProvider.Testing', '8.0.0', '8.10.0'),
    'testsdk': ('Microsoft.NET.Test.Sdk', '17.13.0', '17.14.1'),
    'runner': ('xunit.runner.visualstudio', '3.1.0', '3.1.5'),
}
BASE = '443c811e3f0af1b2e28cf6a86556f5c79aa6fb90'
FILTER = ('Category!=KnownDefect&FullyQualifiedName!~BroadcastRouter.Tests.PhaseBTelemetryMeasurementTests.'
          '&FullyQualifiedName!~BroadcastRouter.Tests.StandbyLifecycleTests.')
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2), encoding='utf-8')


def prepare(project, variant):
    if variant not in ('old', *PACKAGES):
        raise ValueError('Unknown comparison variant')
    original = project.read_bytes()
    text = original.decode('utf-8')
    refs = ET.fromstring(text).findall('.//PackageReference')
    for name, old, _ in PACKAGES.values():
        entries = [ref for ref in refs if ref.get('Include') == name]
        if len(entries) != 1 or entries[0].get('Version') != old:
            raise ValueError('Unexpected baseline package: ' + name)
    if variant != 'old':
        name, old, new = PACKAGES[variant]
        before = f'Include="{name}" Version="{old}"'
        if text.count(before) != 1:
            raise ValueError('Expected exactly one unambiguous package declaration')
        project.write_bytes(text.replace(before, f'Include="{name}" Version="{new}"').encode('utf-8'))
    return {'variant': variant, 'changed_packages': int(variant != 'old'),
            'versions': {name: (new if key == variant else old)
                         for key, (name, old, new) in PACKAGES.items()}}


def summarize(trx_path, exit_code, expected):
    result = {'classification': 'incomplete', 'exit_code': exit_code, 'expected': expected}
    if not trx_path.is_file():
        return {**result, 'reason': 'No completed TRX; inspect retained job/process logs.'}
    try:
        root = ET.parse(trx_path).getroot()
        counters = root.find('t:ResultSummary/t:Counters', NS)
        counts = {key: int(counters.get(key, '-1')) for key in
                  ('total', 'executed', 'passed', 'failed', 'notExecuted')}
        cases = [dict(case.attrib, error='\n'.join(case.itertext()).strip())
                 for case in root.findall('t:Results/t:UnitTestResult', NS)]
        result.update(counts, cases=cases)
        if (counts['total'] != expected or len(cases) != expected or
                len({case['testName'] for case in cases}) != expected or
                counts['executed'] != expected or counts['notExecuted'] != 0 or
                counts['passed'] + counts['failed'] != expected or
                sum(case.get('outcome') == 'Passed' for case in cases) != counts['passed'] or
                sum(case.get('outcome') == 'Failed' for case in cases) != counts['failed']):
            return {**result, 'reason': 'Missing, skipped, duplicate or inconsistent cases.'}
        infos = root.findall('t:ResultSummary/t:RunInfos/t:RunInfo', NS)
        summary = root.find('t:ResultSummary', NS)
        if summary.get('outcome') not in ('Completed', 'Passed', 'Failed') or infos:
            return {**result, 'classification': 'runner-failure', 'reason': 'Run-level diagnostics or aborted execution.'}
        result['classification'] = ('test-failure' if counts['failed'] else
                                    'runner-failure' if exit_code != 0 else 'passed')
        return result
    except (ET.ParseError, AttributeError, KeyError, ValueError) as error:
        return {**result, 'reason': 'Malformed TRX: ' + str(error)}


def capture(command, destination):
    started = utc_now()
    write_json(destination / 'start.json', {'started_utc': started, 'command': command})
    with (destination / 'stdout.txt').open('wb') as stdout, (destination / 'stderr.txt').open('wb') as stderr:
        process = subprocess.run(command, stdout=stdout, stderr=stderr, check=False)
    receipt = {'started_utc': started, 'ended_utc': utc_now(), 'exit_code': process.returncode}
    write_json(destination / 'end.json', receipt)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('variant', choices=('old', *PACKAGES))
    parser.add_argument('repetition', type=int, choices=range(1, 11))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    os.chdir(root)
    destination = root / 'comparison-evidence' / f'{args.variant}-{args.repetition}'
    destination.mkdir(parents=True, exist_ok=False)
    # Reject any application, helper or existing test changes before touching packages.
    subprocess.run(['git', 'diff', '--exit-code', BASE, '--', 'src',
                    'tests/BroadcastRouter.Tests', 'tests/BroadcastRouter.FakeFfmpeg',
                    'tests/BroadcastRouter.OwnerHost'], check=True)
    project = root / 'tests/BroadcastRouter.Tests/BroadcastRouter.Tests.csproj'
    package_receipt = prepare(project, args.variant)
    write_json(destination / 'environment.json', {
        'base': BASE, 'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
        'repetition': args.repetition, **package_receipt,
        'runner': {key: os.environ.get(key) for key in
                   ('ImageOS', 'ImageVersion', 'RUNNER_OS', 'RUNNER_ARCH', 'NUMBER_OF_PROCESSORS', 'PROCESSOR_IDENTIFIER')},
    })
    (destination / 'dotnet-info.txt').write_bytes(subprocess.check_output(['dotnet', '--info']))
    (destination / 'project-diff.txt').write_bytes(subprocess.check_output(['git', 'diff', '--', str(project)]))
    receipt = capture(['dotnet', 'test', str(project), '--configuration', 'Release',
                       '--filter', FILTER, '--logger', 'trx;LogFileName=main.trx',
                       '--results-directory', str(destination)], destination)
    assets = project.parent / 'obj/project.assets.json'
    if assets.exists():
        shutil.copyfile(assets, destination / 'project.assets.json')
    summary = summarize(destination / 'main.trx', receipt['exit_code'], 854)
    write_json(destination / 'summary.json', summary)
    print(json.dumps({key: value for key, value in summary.items() if key != 'cases'}))
    return 0 if summary['classification'] == 'passed' else 1


if __name__ == '__main__':
    raise SystemExit(main())
