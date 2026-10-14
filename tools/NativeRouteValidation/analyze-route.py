"""Reduce protected native route heartbeats; standard library, no game access."""
import collections, json, math, statistics, struct, sys
from pathlib import Path

MIB = 1024 ** 2
def recording_storage(summary):
    """Index metadata only; never expand snapshots while measuring heartbeats."""
    path=Path(summary.get('RecordingFile',''))
    if not path.is_file():return None
    with path.open('rb') as stream:
        if stream.read(8)!=b'PEAKRUN1':return None
        length=struct.unpack('<i',stream.read(4))[0]
        assert 0<length<=8*MIB
        header=json.loads(stream.read(length))
        stream.seek(-16,2);footer=stream.read(16)
        if footer[8:]!=b'PEAKEND1':return {'Schema':header['Schema'],'IndexComplete':False}
        length=struct.unpack('<q',footer[:8])[0]
        assert 0<length<=64*MIB
        stream.seek(-16-length,2);index=json.loads(stream.read(length))
    kinds=collections.defaultdict(collections.Counter)
    for block in index.get('SharedBlocks',[]):
        kinds[block['Kind']].update(Blocks=1,RawBytes=block['RawBytes'],CompressedBytes=block['Length'])
    return {'Schema':header['Schema'],'ContainerVersion':index['Version'],'IndexComplete':True,
            'SharedBlockKinds':[{'Kind':kind,**dict(counts)} for kind,counts in sorted(kinds.items())]}

def get(row, path):
    for key in path.split('.'):
        if not isinstance(row, dict): return None
        row = row.get(key)
    return row if isinstance(row, (int, float)) and not isinstance(row, bool) and math.isfinite(row) else None

def series(rows, path):
    return [(float(r['RunSeconds']), get(r, path)) for r in rows if get(r, path) is not None]

def slope(values):
    if len(values) < 3: return None
    x = statistics.mean(p[0] for p in values); y = statistics.mean(p[1] for p in values)
    den = sum((t-x)**2 for t, _ in values)
    return None if den == 0 else sum((t-x)*(v-y) for t, v in values)/den*60

def metric(rows, path, scale=1):
    values = series(rows, path)
    if not values: return None
    return {'Start': values[0][1]/scale, 'End': values[-1][1]/scale,
            'Minimum': min(v for _,v in values)/scale, 'Maximum': max(v for _,v in values)/scale,
            'NetChange': (values[-1][1]-values[0][1])/scale,
            'SlopePerMinute': None if slope(values) is None else slope(values)/scale}

def numeric(obj, prefix=''):
    out={}
    if not isinstance(obj, dict): return out
    for key, value in obj.items():
        path = prefix+'.'+key if prefix else key
        if isinstance(value, dict): out.update(numeric(value,path))
        elif isinstance(value,(int,float)) and not isinstance(value,bool) and math.isfinite(value): out[path]=value
    return out

def meters(rows):
    out={}
    for name in ('Capture','SampledCapture','Prepare','Probe'):
        calls=series(rows,'MainThread.'+name+'.Calls'); total=series(rows,'MainThread.'+name+'.TotalMs')
        if not calls or not total: continue
        count=calls[-1][1]-calls[0][1]; ms=total[-1][1]-total[0][1]
        out[name]={'CallsBetweenSnapshots': count,'TotalMsBetweenSnapshots': ms,
                   'AverageMs': ms/count if count>0 else None,
                   'StageMaximumMs': max((get(r,'MainThread.'+name+'.StageMaximumMs') or 0) for r in rows),
                   'LastRecentP95Ms': get(rows[-1],'MainThread.'+name+'.RecentP95Ms')}
    return out

def reduce_group(rows):
    last=rows[-1]; segment=last.get('NativeSegment') or {}
    tail=[r for r in rows if r['RunSeconds']>=last['RunSeconds']-30]
    duration=rows[-1]['RunSeconds']-rows[0]['RunSeconds']
    before=numeric(rows[0].get('Work')); after=numeric(last.get('Work'))
    delta={k:after[k]-v for k,v in before.items() if k in after}
    reads={k:v for k,v in delta.items() if any(token in k for token in ('Read','Probe','Skip','Check','Wake','Sleep','Geometry','ChangedFrame','Retirement'))}
    inactive_paths={r['RootPath'] for r in last.get('NativeRoots') or [] if not r.get('Active') and r.get('RootPath')}
    return {'Phase':last['Phase'],'Index':segment.get('Index'),'Biome':segment.get('Biome'),
            'Snapshots':len(rows),'ObservedSeconds':duration,'StartRunSeconds':rows[0]['RunSeconds'],'EndRunSeconds':last['RunSeconds'],
            'MemoryMiB':{key:metric(rows,'Memory.'+key,MIB) for key in ('ManagedBytes','WorkingSetBytes','PrivateBytes','UnityAllocatedBytes','UnityReservedBytes')},
            'Tail30Seconds':{'ObservedSeconds':tail[-1]['RunSeconds']-tail[0]['RunSeconds'],
                'MemoryMiB':{key:metric(tail,'Memory.'+key,MIB) for key in ('ManagedBytes','WorkingSetBytes','PrivateBytes','UnityAllocatedBytes')},
                'WriterMiB':metric(tail,'WriterBytes',MIB),'RollingMiB':metric(tail,'RollingBytes',MIB)},
            'WriterMiB':metric(rows,'WriterBytes',MIB),'RollingMiB':metric(rows,'RollingBytes',MIB),
            'QueueMiB':metric(rows,'QueueBytes',MIB),'QueueFrames':metric(rows,'QueueFrames'),
            'MainThread':meters(rows),'NativeWorkDeltas':reads,'ScopeStart':rows[0].get('Work',{}).get('Scope'),'ScopeEnd':last.get('Work',{}).get('Scope'),
            'FrameCountsEnd':last.get('Counts'),'CreatureKindsEnd':last.get('CreatureKinds'),
            'InactiveNativeRootCount':len(inactive_paths),
            'Interpretation':'Snapshots cover a finite window. Memory includes native game and other mods; positive slope is not proof of an unbounded recorder leak. Work counters and timing are separate.'}

def main():
    root=Path(sys.argv[1]); out=Path(sys.argv[2]) if len(sys.argv)>2 else root/'analysis'
    out.mkdir(parents=True,exist_ok=True)
    rows=[]
    for line in (root/'heartbeat.jsonl').read_text(encoding='utf-8-sig').splitlines():
        try: rows.append(json.loads(line))
        except json.JSONDecodeError: pass # active writer's partial last line
    groups=[]
    for row in rows:
        seg=row.get('NativeSegment') or {}; key=(row.get('Phase'),seg.get('Index'),seg.get('Biome'))
        if not groups or groups[-1][0]!=key: groups.append((key,[]))
        groups[-1][1].append(row)
    phases=[reduce_group(part) for _,part in groups if part]
    summary_path=root/'summary.json'; summary=json.loads(summary_path.read_text(encoding='utf-8-sig')) if summary_path.exists() else {}
    counts={}
    for action in summary.get('Actions',[]):
        key=action.get('Kind','unknown'); counts.setdefault(key,{'Succeeded':0,'UnavailableOrFailed':0})
        counts[key]['Succeeded' if action.get('Success') else 'UnavailableOrFailed']+=1
    report={'Session':root.name,'Snapshots':len(rows),'NativeSuccess':summary.get('Success'),
            'InstalledVersion':summary.get('InstalledVersion'),'InstalledMvid':summary.get('InstalledMvid'),
            'RecordingStorage':recording_storage(summary),'OfflineRepack':summary.get('OfflineRepack'),
            'CompletedBiomes':summary.get('CompletedBiomes'),'RecordingResult':summary.get('RecordingResult'),
            'ActionCounts':counts,'Phases':phases,'ParticleArrayReadsObserved':max((get(r,'Work.Effects.NativeParticleStateReads') or 0 for r in rows),default=0),
            'Notes':['No forced GC or runtime settings change.','Heartbeat reflection and native memory metrics run at1Hz; serialization is on a worker. Probe timing measures this diagnostic cost.',
                     'SampledCapture times only Tick calls which accepted an actual recording frame; Capture includes fast skipped-clock calls.',
                     'Byte slopes are compressed writer progress with page flush granularity; loading/index overhead and unfinished pages can skew short windows.',
                     'Only independently observed native stimuli plus offline matching establish action coverage; counts alone do not prove completeness.']}
    (out/'route-analysis.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    lines=['# 自动路线测量','',f"版本：{report['InstalledVersion']}；路线成功：{report['NativeSuccess']}；快照：{len(rows)}。",'',
           '| 关卡／阶段 | 窗口秒 | 录像增量 MiB | 增长 MiB/min | 滚动缓存末 MiB | 采样调用平均 ms | 管理堆末 MiB |',
           '|---|---:|---:|---:|---:|---:|---:|']
    if report['OfflineRepack']:lines.insert(3,'录像经过离线同源存储重写；心跳、采集计时与工作计数仍来自原始原生会话，不能用作新版本采集性能。')
    for p in phases:
        if p['Phase'] not in ('BaselineIdle','EventObservation','FarIdle'): continue
        w=p['WriterMiB'] or {}; b=p['RollingMiB'] or {}; c=p['MainThread'].get('SampledCapture') or {}; m=p['MemoryMiB']['ManagedBytes'] or {}
        def fmt(v):return '—' if v is None else f'{v:.3f}'
        lines.append('| '+str(p['Index'])+': '+str(p['Biome'])+' / '+p['Phase']+' | '+f"{p['ObservedSeconds']:.1f}"+' | '+fmt(w.get('NetChange'))+' | '+fmt(w.get('SlopePerMinute'))+' | '+fmt(b.get('End'))+' | '+fmt(c.get('AverageMs'))+' | '+fmt(m.get('End'))+' |')
    lines+=['','内存含游戏和其他模组；有限窗口增长不等于持续泄漏。短阶段写入率受分页刷盘影响。具体读数、CPU 最大值、队列和原生读取次数见 JSON。']
    (out/'route-analysis.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    print(json.dumps({'Session':root.name,'Snapshots':len(rows),'NativeSuccess':report['NativeSuccess'],'Phases':len(phases),'ParticleArrayReads':report['ParticleArrayReadsObserved']},ensure_ascii=False))

if __name__=='__main__':main()
