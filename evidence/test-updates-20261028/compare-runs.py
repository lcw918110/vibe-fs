import collections
import hashlib
import json
import pathlib
import re
import sys

if len(sys.argv) != 5:
    raise SystemExit('Usage: compare-runs.py DATA_DIRECTORY BEFORE_ID AFTER_ID OUTPUT_JSON')
data_root = pathlib.Path(sys.argv[1])


def parse(run_id):
    record = json.loads((data_root / (run_id + '.json')).read_text())
    log_data = (data_root / (run_id + '.log')).read_bytes()
    if hashlib.sha256(log_data).hexdigest() != record['logSha256']:
        raise ValueError('Log checksum mismatch: ' + run_id)
    lines = log_data.decode().splitlines()
    lifecycle = []
    bodies = []
    exclusions = []
    summaries = []
    for line in lines:
        if line.startswith('runner: file lifecycle '):
            lifecycle.append(json.loads(line.split('runner: file lifecycle ', 1)[1]))
        elif line.startswith('[verification-test-start] '):
            bodies.append(json.loads(line.split(' ', 1)[1]))
        elif line.startswith(('  skip:', '  todo:', '  cancelled:')):
            status, details = line.strip().split(': ', 1)
            if ':WHAT[' in details:
                exclusions.append((status, 'WHAT[' + details.split(':WHAT[', 1)[1]))
            else:
                exclusions.append((status, details))
        elif line.startswith('[test-summary] '):
            summaries.append(line)
    pids = collections.Counter(x['pid'] for x in lifecycle if x['type'] == 'runner:file-start' and 'pid' in x)
    runner_pid = pids.most_common(1)[0][0]
    starts = {x['entryFile']: x['elapsedMs'] for x in lifecycle
              if x['type'] == 'runner:file-start' and x.get('pid') == runner_pid}
    ends = {x['entryFile']: x['elapsedMs'] for x in lifecycle
            if x['type'] == 'runner:file-drained' and x['entryFile'] in starts}
    names = collections.Counter(x['fullName'] for x in bodies if x['parentPid'] == runner_pid)
    exclusions = collections.Counter(exclusions)
    costs = sorted(({'file': 'requirements/' + file.split('/requirements/', 1)[1], 'startMs': starts[file], 'endMs': end,
                     'durationMs': end - starts[file]} for file, end in ends.items()),
                   key=lambda x: x['durationMs'], reverse=True)
    identity = json.dumps(sorted(names.items()), ensure_ascii=False, separators=(',', ':')).encode()
    return {
        'id': run_id,
        'commit': record['commit'],
        'wallMs': record['wallMs'],
        'exitCode': record['exitCode'],
        'sourceStable': record['inputDigestBefore'] == record['inputDigestAfter'],
        'inputDigest': record['inputDigestBefore'],
        'manifestSha256': record['manifestSha256'],
        'logSha256': record['logSha256'],
        'startedFiles': len(starts),
        'drainedFiles': len(ends),
        'summary': summaries[-1],
        'startedBodyCount': sum(names.values()),
        'startedBodyIdentitySha256': hashlib.sha256(identity).hexdigest(),
        'exclusionCount': sum(exclusions.values()),
        'slowestFiles': costs[:15],
        'environment': record['environment'],
        'versions': {key: record[key] for key in ['node', 'npmVersion', 'rgVersion', 'dotnetVersion', 'bunVersion']},
        'loadSamples': record['hostLoadSamples'],
    }, names, exclusions


before, before_names, before_excluded = parse(sys.argv[2])
after, after_names, after_excluded = parse(sys.argv[3])
report = {
    'before': before, 'after': after,
    'sameStartedBodies': before_names == after_names,
    'sameSkippedAndTodoIdentities': before_excluded == after_excluded,
    'removedBodies': list((before_names - after_names).items()),
    'addedBodies': list((after_names - before_names).items()),
    'removedExclusions': list((before_excluded - after_excluded).items()),
    'addedExclusions': list((after_excluded - before_excluded).items()),
    'sameEnvironment': before['environment'] == after['environment'],
    'sameVersions': before['versions'] == after['versions'],
    'savedMs': before['wallMs'] - after['wallMs'],
    'savedPercent': 100 * (1 - after['wallMs'] / before['wallMs']),
}
output = pathlib.Path(sys.argv[4])
output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
print(json.dumps({key: value for key, value in report.items() if key not in ['before', 'after']}, ensure_ascii=False))
print(before['summary'])
print(after['summary'])
