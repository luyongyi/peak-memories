"""Compare completed protected routes; field bytes are raw, file bytes compressed."""
import json
import sys
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def load(root):
    summary = read(root / 'summary.json')
    if summary.get('PerformanceMeasurementValid') is False or summary.get('FaunaVisual'):
        raise ValueError('Visual-audit routes are invalid for performance comparisons')
    result = summary.get('RecordingResult', {})
    if not summary.get('Success') or result.get('Status') != 'completed' or result.get('UnwrittenFrames') != 0:
        raise ValueError('Only normally completed native routes may be compared')
    composition = read(root / 'analysis/recording-composition.json')
    analysis = read(root / 'analysis/route-analysis.json')
    far = [p for p in analysis['Phases'] if p['Phase'] == 'FarIdle' and p['Biome'] == 'Swamp']
    if len(far) != 1 or far[0]['ObservedSeconds'] < 100:
        raise ValueError('Require one >=100 second Swamp FarIdle window')
    families = {}
    for track, family in [('Items', 'EarlyWorm'), ('Items', 'Frog'), ('Creatures', 'venus-flytrap'), ('Creatures', 'frog-tongue'), ('Creatures', 'voice-thief')]:
        selected = [f for f in composition['MainPageEntityPayloadActualUtf8BytesByFamily'][track]['Families'] if f['Family'] == family]
        if len(selected) != 1:
            raise ValueError('Missing or ambiguous family: ' + family)
        f = selected[0]
        families[family] = {k: f.get(k) for k in ('RawBytes', 'EntityUpdates', 'DistinctKnownKeys', 'PoseNodesValueRawBytes', 'NonNullLineValueRawBytes')}
        families[family]['RawBytesPerMinuteWholeRoute'] = f['RawBytes'] * 60 / composition['Duration']
    return {'Version': summary['InstalledVersion'], 'Duration': composition['Duration'],
            'FileBytes': composition['FileBytes'], 'FarIdle': far[0], 'Families': families,
            'Seeks': summary['Seeks'], 'RawModuleShares': composition['RawModuleSharesIncludingSharedSnapshotsAndHistory']}


def reduction(before, after):
    return (1 - after / before) * 100 if before else None


def main():
    before, after = [load(Path(p)) for p in sys.argv[1:3]]
    b, a = before['FarIdle'], after['FarIdle']
    if b['CreatureKindsEnd'] != a['CreatureKindsEnd']:
        raise ValueError('End-of-window native recorded creature counts differ; compare separately')
    report = {'Before': before, 'After': after,
              'FarIdleCompressedGrowthReductionPercent': reduction(b['WriterMiB']['SlopePerMinute'], a['WriterMiB']['SlopePerMinute']),
              'FarIdleSampledCaptureAverageReductionPercent': reduction(b['MainThread']['SampledCapture']['AverageMs'], a['MainThread']['SampledCapture']['AverageMs']),
              'FamilyRawRateReductionPercent': {key: reduction(before['Families'][key]['RawBytesPerMinuteWholeRoute'], after['Families'][key]['RawBytesPerMinuteWholeRoute']) for key in before['Families']},
              'Limits': 'Same protected route and seed, separate game runs. Native physics/audio timing can differ. CPU is capture-call time, not FPS. Raw family entry bytes are uncompressed; compressed module shares are not additive. Counts do not prove all actions. Full-route rate includes transitions and is separate from the >=100s FarIdle steady window.'}
    output = Path(sys.argv[3])
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({key: report[key] for key in ('FarIdleCompressedGrowthReductionPercent', 'FarIdleSampledCaptureAverageReductionPercent', 'FamilyRawRateReductionPercent')}, ensure_ascii=False))


if __name__ == '__main__':
    main()
