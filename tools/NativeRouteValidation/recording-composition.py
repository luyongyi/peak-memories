"""Exact physical byte accounting + actual uncompressed JSON field attribution.
Compressed module shares are not additive, so no invented compressed percentages.
Reads a completed protected test recording only; never rewrites a source.
"""
import collections, gzip, json, struct, sys
from pathlib import Path

DECODER=json.JSONDecoder()
UNKNOWN_FAMILY='<unknown>'
FAMILY_FIELDS={'Items':'Prefab','Creatures':'Kind'}
SHARED_NAMES={0:'Items',1:'Crates',2:'Ropes',3:'Creatures',4:'Interactions',5:'Spawned',
              6:'SharedVisuals',7:'SharedLights',8:'SharedPoseNodes',9:'SharedOrder',10:'SharedCreatureDirectory'}

def direct_reference(value):
    assert isinstance(value,dict) and set(value)=={'$Shared'}, 'Expected a direct shared leaf reference'
    identifier=value['$Shared']
    assert isinstance(identifier,int) and not isinstance(identifier,bool) and identifier>=0, 'Invalid shared leaf ID'
    return identifier

def skip_space(text,i):
    while i<len(text) and text[i].isspace():i+=1
    return i

def object_field_spans(text,start=0):
    """Original character spans, including field separators but not braces."""
    i=skip_space(text,start)
    assert text[i]=='{';i+=1
    while True:
        i=skip_space(text,i)
        if text[i]=='}':return
        start=i;key,i=DECODER.raw_decode(text,i)
        i=skip_space(text,i)
        assert text[i]==':';i+=1
        value_start=skip_space(text,i)
        value,value_end=DECODER.raw_decode(text,value_start)
        i=skip_space(text,value_end)
        if text[i]==',':i+=1
        yield key,start,i,value_start,value_end,value

def field_slices(text):
    for key,start,end,_,_,_ in object_field_spans(text):
        yield key,len(text[start:end].encode('utf-8'))

def array_entry_spans(text,start):
    """Original array value spans; never include brackets or separators."""
    i=skip_space(text,start)
    assert text[i]=='[';i+=1
    while True:
        i=skip_space(text,i)
        if text[i]==']':return
        start=i;value,end=DECODER.raw_decode(text,i)
        yield value,start,end
        i=skip_space(text,end)
        if text[i]==',':i+=1
        else:assert text[i]==']'

class EntityFamilyAttribution:
    """Attribute only stored main-page array entries; no reconstructed JSON."""
    def __init__(self):
        self.shared={};self.directories={};self.identities={track:{} for track in FAMILY_FIELDS}
        self.directory_memberships=collections.Counter()
        self.directory_bytes=collections.Counter();self.directory_entries=collections.Counter();self.directory_keys=collections.defaultdict(set)
        self.directory_raw=0;self.directory_framing=0
        self.totals=collections.defaultdict(collections.Counter)
        self.bytes=collections.defaultdict(collections.Counter)
        self.counts=collections.defaultdict(collections.Counter)
        self.entities=collections.defaultdict(set)
        self.fields=collections.defaultdict(collections.Counter)
        self.nested=collections.defaultdict(collections.Counter)
        self.region_bytes=collections.defaultdict(collections.Counter)
        self.unknown_reasons=collections.defaultdict(collections.Counter)

    def shared_block(self,identifier,kind,payload):
        if kind==10:
            value=json.loads(payload)
            assert isinstance(value,dict) and set(value)=={'Value'} and isinstance(value['Value'],list)
            entries=[];keys=set()
            for entry in value['Value']:
                assert isinstance(entry,dict) and '$Shared' not in entry and isinstance(entry.get('Key'),str) and isinstance(entry.get('Kind'),str)
                assert entry['Key'] not in keys, 'Duplicate static directory key'
                keys.add(entry['Key'])
                entries.append((entry['Key'],entry['Kind'],(entry.get('SampleTimeKnown',False),entry.get('SampleTime',0),entry.get('ClockMode',0))))
            self.directories[identifier]=entries
            text=payload.decode('utf-8');entry_bytes=0
            for name,_,_,start,_,_ in object_field_spans(text):
                if name!='Value':continue
                for entry,begin,end in array_entry_spans(text,start):
                    size=len(text[begin:end].encode('utf-8'));family=entry['Kind'];entry_bytes+=size
                    self.directory_bytes[family]+=size;self.directory_entries[family]+=1;self.directory_keys[family].add(entry['Key'])
            self.directory_raw+=len(payload);self.directory_framing+=len(payload)-entry_bytes
            return
        track={0:'Items',3:'Creatures'}.get(kind)
        if track is None:return
        value=json.loads(payload)
        self.shared[identifier]=(track,value.get('Key'),value.get(FAMILY_FIELDS[track]))

    def static_entries(self,record):
        if 'StaticCreatures' not in record:return []
        assert record.get('ObjectsFull') is True, 'Static directory outside a page baseline'
        identifier=direct_reference(record['StaticCreatures'])
        assert identifier in self.directories, 'Missing or wrong-kind static directory'
        return self.directories[identifier]

    def seed_directory(self,record):
        for key,family,_ in self.static_entries(record):
            self.identities['Creatures'][key]=family
            self.entities[('Creatures',family)].add(key)
            self.directory_memberships[family]+=1

    def family(self,track,value):
        if not isinstance(value,dict):return None,UNKNOWN_FAMILY,'non-object-entry'
        if '$Shared' in value:
            shared=self.shared.get(value['$Shared'])
            if shared is None or shared[0]!=track:return None,UNKNOWN_FAMILY,'unresolved-shared-reference'
            _,key,family=shared
        else:
            key=value.get('Key')
            # Full patches replace the identity baseline, never inherit a
            # possibly reused Key's previous resource when the field is absent.
            family=value.get(FAMILY_FIELDS[track])
            if FAMILY_FIELDS[track] not in value and not value.get('Full'):
                family=self.identities[track].get(key)
        if not isinstance(family,str) or not family or family==UNKNOWN_FAMILY:
            family=UNKNOWN_FAMILY;reason='missing-family-baseline'
        else:reason=None
        if isinstance(key,str):self.identities[track][key]=family
        return key,family,reason

    def record(self,text,record,biome,spans):
        if record.get('ObjectsFull'):
            for identities in self.identities.values():identities.clear()
        self.seed_directory(record)
        for track in FAMILY_FIELDS:
            for key in record.get('Removed'+track,[]):self.identities[track].pop(key,None)
        for track,_,_,start,end,_ in spans:
            if track not in FAMILY_FIELDS:continue
            array_bytes=len(text[start:end].encode('utf-8'));entry_bytes=0;entry_count=0
            for value,entry_start,entry_end in array_entry_spans(text,start):
                size=len(text[entry_start:entry_end].encode('utf-8'));entry_bytes+=size;entry_count+=1
                key,family,reason=self.family(track,value);bucket=(track,family)
                self.bytes[track][family]+=size;self.counts[track][family]+=1
                self.region_bytes[(biome,track)][family]+=size
                if key is not None:self.entities[bucket].add(key)
                if reason:self.unknown_reasons[track][reason]+=1
                field_bytes=0
                if isinstance(value,dict):
                    for field,field_start,field_end,value_start,value_end,field_value in object_field_spans(text,entry_start):
                        amount=len(text[field_start:field_end].encode('utf-8'))
                        self.fields[bucket][field]+=amount;field_bytes+=amount
                        if field=='Pose' and isinstance(field_value,dict):
                            for node_field,_,_,node_start,node_end,_ in object_field_spans(text,value_start):
                                if node_field=='Nodes':self.nested[bucket]['PoseNodesValueRawBytes']+=len(text[node_start:node_end].encode('utf-8'))
                        elif field=='Line' and field_value is not None:
                            self.nested[bucket]['NonNullLineValueRawBytes']+=len(text[value_start:value_end].encode('utf-8'))
                self.nested[bucket]['ObjectFramingRawBytes']+=size-field_bytes
            assert entry_bytes<=array_bytes
            self.totals[track]['TrackArrayValueRawBytes']+=array_bytes
            self.totals[track]['EntityPayloadRawBytes']+=entry_bytes
            self.totals[track]['ArrayFramingRawBytes']+=array_bytes-entry_bytes
            self.totals[track]['EntityUpdates']+=entry_count

    def report(self):
        report={}
        for track in FAMILY_FIELDS:
            totals=dict(self.totals[track]);total=totals.get('EntityPayloadRawBytes',0)
            families=[]
            for family,size in self.bytes[track].most_common():
                bucket=(track,family);nested=self.nested[bucket]
                assert sum(self.fields[bucket].values())+nested['ObjectFramingRawBytes']==size
                families.append({'Family':family,'RawBytes':size,'PercentOfTrackMainEntityPayload':100*size/total if total else 0,
                    'EntityUpdates':self.counts[track][family],'DistinctKnownKeys':len(self.entities[bucket]),
                    'EntryFieldRawBytes':dict(self.fields[bucket]),**dict(nested)})
            assert sum(x['RawBytes'] for x in families)==total
            assert total+totals.get('ArrayFramingRawBytes',0)==totals.get('TrackArrayValueRawBytes',0)
            # Always expose an unknown count, even when no unknown row exists.
            report[track]={'FamilyField':FAMILY_FIELDS[track],**totals,'Families':families,
                'StaticDirectoryMembershipReferencesByFamily':dict(self.directory_memberships) if track=='Creatures' else {},
                'UnknownRawBytes':self.bytes[track][UNKNOWN_FAMILY],
                'UnknownEntityUpdates':self.counts[track][UNKNOWN_FAMILY],
                'UnknownRawPercent':100*self.bytes[track][UNKNOWN_FAMILY]/total if total else 0,
                'UnknownEntityUpdatePercent':100*self.counts[track][UNKNOWN_FAMILY]/totals['EntityUpdates'] if totals.get('EntityUpdates') else 0,
                'UnknownReasons':dict(self.unknown_reasons[track]),'ExactEntryAccounting':True}
        return report

    def region_report(self):
        report=collections.defaultdict(dict)
        for (biome,track),values in self.region_bytes.items():
            total=sum(values.values())
            report[biome][track]={'EntityPayloadRawBytes':total,'Families':[
                {'Family':family,'RawBytes':size,'PercentOfTrackMainEntityPayload':100*size/total if total else 0}
                for family,size in values.most_common()]}
        return dict(report)

    def directory_report(self):
        assert sum(self.directory_bytes.values())+self.directory_framing==self.directory_raw
        return {'RawBytes':self.directory_raw,'EntityPayloadRawBytes':sum(self.directory_bytes.values()),
                'WrapperAndSeparatorsRawBytes':self.directory_framing,'ExactEntryAccounting':True,
                'Families':[{'Family':family,'RawBytes':size,'DirectoryEntries':self.directory_entries[family],
                             'DistinctKnownKeys':len(self.directory_keys[family])} for family,size in self.directory_bytes.most_common()]}

def category(key):
    if key=='StaticCreatures':return 'Creatures'
    for track in ('Actors','Items','Crates','Ropes','Effects','Audio','Creatures','Interactions','Spawned','Balloons'):
        singular=track[:-1] if track.endswith('s') else track
        if key==track or key=='Removed'+track or key==singular+'Order':return 'Players' if track=='Actors' else track
    return 'World' if key=='World' else 'TimelineAndControl'

def main():
    summary=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'));path=Path(summary['RecordingFile'])
    out=Path(sys.argv[2]);out.mkdir(parents=True,exist_ok=True)
    biomes={int(x['Index']):str(x['Index'])+': '+x['Biome'] for x in summary.get('NativeCensus',[])}
    raw=collections.Counter();regions=collections.defaultdict(collections.Counter);physical=collections.Counter();updates=collections.defaultdict(collections.Counter)
    families=EntityFamilyAttribution()
    per_page=[];shared_kinds=collections.defaultdict(collections.Counter)
    with path.open('rb') as f:
        assert f.read(8)==b'PEAKRUN1';header_length=struct.unpack('<i',f.read(4))[0];header=json.loads(f.read(header_length));header_bytes=12+header_length
        f.seek(-16,2);footer=f.read(16);assert footer[8:]==b'PEAKEND1';index_length=struct.unpack('<q',footer[:8])[0]
        f.seek(-16-index_length,2);index=json.loads(f.read(index_length))
        physical['HeaderIndexFooter']=header_bytes+index_length+16;raw['HeaderIndexFooter']=physical['HeaderIndexFooter']
        def member(entry):f.seek(entry['Offset']);return gzip.decompress(f.read(entry['Length']))
        for identifier,block in enumerate(index.get('SharedBlocks',[])):
            module=SHARED_NAMES[block['Kind']];physical['Shared'+module]+=block['Length'];raw[module]+=block['RawBytes']
            shared_kinds[block['Kind']].update(Blocks=1,RawBytes=block['RawBytes'],CompressedBytes=block['Length'])
            if block['Kind'] in (0,3,10):families.shared_block(identifier,block['Kind'],member(block))
        for page in index['Pages']:
            physical['MainPages']+=page['Length'];main_bytes=member(page);segment=0;page_counts=collections.Counter();page_raw=collections.Counter()
            for data in main_bytes.splitlines(keepends=True):
                text=data.decode('utf-8').rstrip('\r\n');spans=list(object_field_spans(text));record={field[0]:field[5] for field in spans}
                if record.get('Type')=='frame':
                    segment=record.get('World',{}).get('Segment',segment);biome=biomes.get(segment,'segment-'+str(segment))
                    total=0
                    for key,start,end,_,_,_ in spans:
                        size=len(text[start:end].encode('utf-8'))
                        module=category(key);raw[module]+=size;regions[biome][module]+=size;page_raw[module]+=size;total+=size
                        if key in ('Actors','Items','Crates','Ropes','Effects','Audio','Creatures','Interactions','Spawned','Balloons'):
                            page_counts[module]+=len(record[key]);updates[biome][module]+=len(record[key])
                    families.record(text,record,biome,spans)
                    overhead=len(data)-total;raw['TimelineAndControl']+=overhead;regions[biome]['TimelineAndControl']+=overhead
                else:raw['HeaderIndexFooter']+=len(data)
            history=page.get('EffectHistory')
            if history:
                physical['EffectHistory']+=history['Length'];data=member(history)
                # The separate history stream consists of effect state and its
                # timeline/header framing. Keep it one honest raw category.
                raw['EffectHistory']+=len(data)
            per_page.append({'Index':page['Index'],'Start':page['Start'],'End':page['End'],'MainCompressedBytes':page['Length'],
                             'HistoryCompressedBytes':history['Length'] if history else 0,'RawFields':dict(page_raw),'EntityUpdates':dict(page_counts)})
    assert sum(physical.values())==path.stat().st_size,(sum(physical.values()),path.stat().st_size)
    total=sum(raw.values())
    pct=lambda values:[{'Module':k,'RawBytes':v,'RawPercent':100*v/sum(values.values()) if sum(values.values()) else 0} for k,v in values.most_common()]
    family_report=families.report()
    report={'ContainerVersion':index['Version'],'Schema':header['Schema'],'Duration':index['Duration'],'Frames':index['FrameCount'],
            'OfflineRepack':summary.get('OfflineRepack'),
            'FileBytes':path.stat().st_size,'ExactPhysicalBytes':dict(physical),'ExactPhysicalAccounting':True,'TotalUncompressedBytes':total,
            'RawModuleSharesIncludingSharedSnapshotsAndHistory':pct(raw),
            'SharedBlockKinds':[{'Kind':kind,'Module':SHARED_NAMES[kind],**dict(counts)} for kind,counts in sorted(shared_kinds.items())],
            'RawPageFieldSharesByBiome':{k:pct(v) for k,v in regions.items()},'EntityUpdateCountsByBiome':{k:dict(v) for k,v in updates.items()},'Pages':per_page,
            'MainPageEntityPayloadActualUtf8BytesByFamily':family_report,
            'StaticDirectoryPayloadActualUtf8BytesByFamily':families.directory_report(),
            'MainPageEntityPayloadActualUtf8BytesByFamilyAndBiome':families.region_report(),
            'Limits':['Raw percentages use actual UTF-8 field slices and actual shared payload lengths, before gzip. They are not compressed-file module percentages.',
                      'Cross-page renderer/light/node/order leaves stay in SharedVisuals, SharedLights, SharedPoseNodes and SharedOrder. Typed schema26 static directories stay in SharedCreatureDirectory; their main-page references are Creature metadata, not expanded entity bytes. Separate effect-history framing stays in EffectHistory.',
                      'Per-biome raw page fields exclude cross-page shared snapshots and history, because assigning those to one biome would invent ownership.',
                      'A whole page can cross a biome boundary. Its compressed byte size cannot be split additively by track or biome.',
                      'Family attribution counts original UTF-8 spans of Items/Creatures main-page array entries only. Array brackets, separators, shared-block payloads, removal/order arrays and root track property names are not assigned to families.',
                      'Family identity comes from shared entity blocks, typed static directory memberships or cumulative inline full/delta patches; unresolved identities remain <unknown>. A $Shared entry contributes only the bytes of its main-page reference. StaticDirectoryMembershipReferencesByFamily counts retained members separately and never invents main-entry bytes or observations.',
                      'EntryFieldRawBytes plus ObjectFramingRawBytes equals each family payload. PoseNodesValueRawBytes and NonNullLineValueRawBytes are nested subtotals already included in their containing entry fields, not additional bytes.']}
    (out/'recording-composition.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    lines=['# 录像字节组成','',f"完整文件 {path.stat().st_size:,} 字节；{index['Duration']:.2f} 秒；{index['FrameCount']:,} 帧。",'',
           '以下为压缩前实际编码字节占比，不能当作压缩后磁盘文件占比。','', '| 模块 | 原始字节 | 占比 |','|---|---:|---:|']
    if summary.get('OfflineRepack'):lines.insert(3,'这是相同原生观察的离线存储重写；不代表新版本重新采集或原生性能测量。')
    for row in pct(raw):lines.append(f"| {row['Module']} | {row['RawBytes']:,} | {row['RawPercent']:.2f}% |")
    lines+=['','## 主页面实体条目按家族归因','',
            '以下仅统计 Items/Creatures 数组中每个实际条目的原始 UTF-8 片段。百分比分母是各轨道的主页面条目，不含共享块、数组外壳、移除/排序数组，也不是压缩后字节。', '',
            '| 轨道 / 原生家族 | 条目原始字节 | 轨道条目占比 | 更新条目数 | 已知不同 Key | Pose.Nodes 值字节（内含子集） | 非空 Line 值字节（内含子集） |',
            '|---|---:|---:|---:|---:|---:|---:|']
    for track,details in family_report.items():
        for row in details['Families']:
            lines.append(f"| {track} / {row['Family']} | {row['RawBytes']:,} | {row['PercentOfTrackMainEntityPayload']:.2f}% | {row['EntityUpdates']:,} | {row['DistinctKnownKeys']:,} | {row.get('PoseNodesValueRawBytes',0):,} | {row.get('NonNullLineValueRawBytes',0):,} |")
    for track,details in family_report.items():
        lines.append(f"\n{track} 未知家族：{details['UnknownRawBytes']:,} 字节（{details['UnknownRawPercent']:.4f}%）/ {details['UnknownEntityUpdates']:,} 条目；数组外壳与分隔：{details.get('ArrayFramingRawBytes',0):,} 字节。")
    lines+=['','物理文件按主页面、特效历史、共享块和头尾索引精确核算；各模块共享 gzip 字典，压缩后模块占比不具有可加性。']
    (out/'recording-composition.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    print(json.dumps({'Bytes':path.stat().st_size,'Duration':index['Duration'],'Frames':index['FrameCount'],'ExactPhysicalAccounting':True,'RawModuleShares':pct(raw)[:5]},ensure_ascii=False))

if __name__=='__main__':main()
