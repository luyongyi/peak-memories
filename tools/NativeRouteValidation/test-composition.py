"""Small synthetic containers verify byte conservation and schema26 attribution.

These are analysis fixtures, not playable game recordings or native evidence.
Run with Python 3; output and payloads live only in a temporary directory.
"""
import contextlib
import gzip
import importlib.util
import io
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest


def module(name,filename):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(filename))
    result=importlib.util.module_from_spec(spec);spec.loader.exec_module(result);return result


composition=module('composition_test','recording-composition.py')
steady=module('steady_test','steady-composition.py')
route=module('route_test','analyze-route.py')


def raw(value):return json.dumps(value,separators=(',',':'),ensure_ascii=False).encode('utf-8')


def fixture(root,schema):
    path=root/'fixture.peakrun';header={'Schema':schema,'Type':'header'}
    trap={'Key':'trap:α','Kind':'spore-trap','Pose':{'Nodes':[]},'State':0}
    spider={'Key':'spider:β','Kind':'spider','SampleTimeKnown':True,'SampleTime':0,'ClockMode':1,'Pose':{'Nodes':[]}}
    cold=[trap,spider];consumed=[{**trap,'State':1},spider]
    order=[trap['Key'],'beetle:γ',spider['Key']]
    values=[(10,{'Value':cold}),(9,{'Value':order}),(10,{'Value':consumed})] if schema==26 else [(3,{**trap,'Full':True}),(3,{**spider,'Full':True}),(3,{**consumed[0],'Full':True})]
    blocks=[];pages=[]
    with path.open('wb') as stream:
        encoded=raw(header);stream.write(b'PEAKRUN1'+struct.pack('<i',len(encoded))+encoded)
        def member(payload):
            offset=stream.tell();compressed=gzip.compress(payload,mtime=0);stream.write(compressed)
            return {'Offset':offset,'Length':len(compressed),'RawBytes':len(payload)}
        for second in range(60):
            moving={'Key':'beetle:γ','Full':True,'Kind':'beetle','SampleTimeKnown':True,'SampleTime':second,'ClockMode':0,'Pose':{'Nodes':[]}}
            frame={'Type':'frame','T':second,'ObjectsFull':True,'World':{'Segment':1},'Events':[],'Creatures':[moving]}
            if schema==26:
                frame.update(StaticCreatures={'$Shared':0 if second<30 else 2},CreatureOrder={'$Shared':1})
            else:
                frame['Creatures']=[{'$Shared':0 if second<30 else 2},moving,{'$Shared':1}]
                frame['CreatureOrder']=order
            later={'Type':'frame','T':second+.75,'ObjectsFull':False,'World':{},'Events':[]}
            if schema==25:
                later.update(Creatures=[],RemovedCreatures=[])
            main=member(b'\n'.join(map(raw,[header,frame,later,{'Type':'end'}]))+b'\n')
            history=member(b'\n'.join(map(raw,[header,{'Type':'frame','Effects':[]},{'Type':'end'}]))+b'\n')
            new=values[:2] if second==0 else values[2:] if second==30 else []
            for kind,value in new:
                block=member(raw(value));block['Kind']=kind;blocks.append(block)
            pages.append({**main,'Index':second,'Start':second,'End':second+.75,'FrameCount':2,'Overlap':False,
                          'SharedThrough':len(blocks),'EffectHistory':history})
        index={'Version':4,'Duration':59.75,'FrameCount':120,'Pages':pages,'SharedBlocks':blocks}
        encoded=raw(index);stream.write(encoded+struct.pack('<q',len(encoded))+b'PEAKEND1')
    summary={'Success':True,'PerformanceMeasurementValid':True,'InstalledVersion':'fixture','InstalledMvid':'fixture',
             'FirstCapture':100,'RecordingFile':str(path),'RecordingResult':{'Status':'completed','UnwrittenFrames':0},
             'NativeCensus':[{'Index':1,'Biome':'Roots'}]}
    (root/'summary.json').write_bytes(raw(summary))
    rows=[{'Phase':'FarIdle','NativeSegment':{'Index':1,'Biome':'Roots','RootName':'Forest'},
           'NativeTime':time,'Work':{'Scope':{'Pending':0}}} for time in (100,160)]
    (root/'heartbeat.jsonl').write_bytes(b'\n'.join(map(raw,rows)))
    return summary,index


class AttributionTests(unittest.TestCase):
    def test_schema26_directory_bytes_and_memberships(self):
        with tempfile.TemporaryDirectory(prefix='peak-composition-') as directory:
            root=Path(directory);summary,index=fixture(root,26);output=root/'analysis'
            old_argv=sys.argv
            try:
                sys.argv=['composition',str(root/'summary.json'),str(output)]
                with contextlib.redirect_stdout(io.StringIO()):composition.main();steady.analyze(root,output)
            finally:sys.argv=old_argv
            full=json.loads((output/'recording-composition.json').read_text(encoding='utf-8'))
            result=json.loads((output/'steady-composition.json').read_text(encoding='utf-8'))['Windows'][0]
            self.assertTrue(full['ExactPhysicalAccounting']);self.assertEqual(sum(full['ExactPhysicalBytes'].values()),Path(summary['RecordingFile']).stat().st_size)
            self.assertEqual(full['MainPageEntityPayloadActualUtf8BytesByFamily']['Creatures']['UnknownRawBytes'],0)
            self.assertEqual(full['MainPageEntityPayloadActualUtf8BytesByFamily']['Creatures']['StaticDirectoryMembershipReferencesByFamily'],{'spore-trap':60,'spider':60})
            self.assertEqual(full['StaticDirectoryPayloadActualUtf8BytesByFamily']['RawBytes'],sum(block['RawBytes'] for block in index['SharedBlocks'] if block['Kind']==10))
            attribution=result['CreatureAttribution'];rows={row['Family']:row for row in attribution['Families']}
            self.assertEqual(rows['spore-trap']['MainEntityEntries'],0);self.assertEqual(rows['spider']['MainStaticDirectoryMemberships'],60)
            self.assertEqual(rows['spider']['SubsequentSampleTimeChangesInsideWindow'],0);self.assertEqual(rows['beetle']['SubsequentSampleTimeChangesInsideWindow'],59)
            self.assertEqual(sum(row['RawBytes'] for row in rows.values())+attribution['TrackOverheadRawBytes'],attribution['CreatureGroupRawBytes'])
            self.assertEqual({row['Kind']:row['Blocks'] for row in result['NewSharedBlockKinds']},{9:1,10:2})
            self.assertEqual(route.recording_storage(summary)['Schema'],26)

    def test_legacy_family_clocks_remain_equivalent(self):
        with tempfile.TemporaryDirectory(prefix='peak-composition-') as directory:
            root=Path(directory);fixture(root,25)
            with contextlib.redirect_stdout(io.StringIO()):steady.analyze(root,root/'analysis')
            result=json.loads((root/'analysis/steady-composition.json').read_text(encoding='utf-8'))['Windows'][0]
            rows={row['Family']:row for row in result['CreatureAttribution']['Families']}
            self.assertEqual(rows['spider']['MainSharedReferenceEntries'],60);self.assertEqual(rows['spider']['MainStaticDirectoryMemberships'],0)
            self.assertEqual(rows['spider']['SubsequentSampleTimeChangesInsideWindow'],0);self.assertEqual(rows['beetle']['SubsequentSampleTimeChangesInsideWindow'],59)

    def test_direct_reference_rejects_mixed_invalid_ids(self):
        for value in ({'$Shared':-1},{'$Shared':True},{'$Shared':0,'Value':[]},{'$Shared':{'$Shared':0}}):
            with self.assertRaises(AssertionError):composition.direct_reference(value)


if __name__=='__main__':unittest.main()
