"""Read-only attribution of complete seek pages inside native FarIdle windows.

Count original UTF-8 fields, companion effect histories and shared blocks first
written for each selected page. Never re-encode or recompress a source recording.
"""
import collections
import gzip
import importlib.util
import json
from pathlib import Path
import struct
import sys

spec = importlib.util.spec_from_file_location("composition", Path(__file__).with_name("recording-composition.py"))
composition = importlib.util.module_from_spec(spec)
spec.loader.exec_module(composition)

GROUPS = {
    "Players": "Players", "Creatures": "Creatures", "Items": "Items",
    "World": "Scene", "Crates": "Scene", "Ropes": "Scene", "Interactions": "Scene",
    "Spawned": "Scene", "Balloons": "Scene", "Effects": "Effects", "EffectHistory": "Effects",
    "Audio": "Audio", "TimelineAndControl": "Other", "PageFraming": "Other",
    "SharedVisuals": "Other", "SharedLights": "Other", "SharedPoseNodes": "Other",
}
SHARED = {0: "Items", 1: "Crates", 2: "Ropes", 3: "Creatures", 4: "Interactions",
          5: "Spawned", 6: "SharedVisuals", 7: "SharedLights", 8: "SharedPoseNodes"}
LABELS = {"Shore": "海岸", "Roots": "森蕈", "Alpine": "高山", "Swamp": "雾沼"}
ORDER = ("Players", "Creatures", "Items", "Scene", "Effects", "Audio", "Other")
CHINESE = dict(zip(ORDER, ("玩家", "生物", "物品", "场景与机关", "特效及历史", "声音", "共享叶块与时序")))
ITEM_CREATURES = {"Frog", "EarlyWorm", "Mandrake_Hidden"}


class CreatureFamilyAttribution:
    """Stored entity bytes only; shared references are not capture samples."""
    def __init__(self, shared_clocks, start, end):
        self.shared_clocks = shared_clocks
        self.start, self.end = start, end
        self.rows = collections.defaultdict(collections.Counter)
        self.keys = collections.defaultdict(set)
        self.fields = collections.defaultdict(lambda: collections.defaultdict(collections.Counter))
        self.clocks = {}
        self.overhead = collections.Counter()
        self.item_metadata = collections.Counter()

    def remove(self, track, keys):
        for key in keys:
            self.clocks.pop((track, key), None)

    def entry(self, track, family, key, entry, text, begin, finish):
        bucket = (track, family)
        row = self.rows[bucket]
        size = len(text[begin:finish].encode("utf-8"))
        row["MainEntryRawBytes"] += size
        row["MainEntityEntries"] += 1
        assert isinstance(key, str), "Creature entity has no resolved identity"
        self.keys[bucket].add(key)
        shared = "$Shared" in entry
        row["MainSharedReferenceEntries" if shared else "MainInlineEntityEntries"] += 1
        row["MainBaselineEntries" if shared or entry.get("Full") else "MainDeltaEntries"] += 1
        self._fields(bucket, "MainEntryFieldRawBytes", text, begin, size)

        # Retain only clocks across independent page baselines. Do not count
        # every reference/full baseline as a new observation, and do not infer
        # heavy native geometry reads from a phase-only clock correction.
        previous = self.clocks.get((track, key))
        if shared:
            clock = self.shared_clocks[entry["$Shared"]]
        else:
            defaults = (False, 0, 0) if entry.get("Full") or previous is None else previous
            clock = (entry.get("SampleTimeKnown", defaults[0]),
                     entry.get("SampleTime", defaults[1]), entry.get("ClockMode", defaults[2]))
        known, time, _ = clock
        if known:
            inside = self.start <= time <= self.end
            if previous is None or not previous[0]:
                row["FirstSeenKnownClockStates"] += 1
                if inside:
                    row["FirstSeenKnownClockStatesInsideWindow"] += 1
            elif time != previous[1]:
                row["SubsequentSampleTimeChanges"] += 1
                if inside:
                    row["SubsequentSampleTimeChangesInsideWindow"] += 1
            else:
                row["RepeatedKnownSampleTimeEntries"] += 1
        else:
            row["UnknownSampleTimeEntries"] += 1
        self.clocks[(track, key)] = clock

    def block(self, track, family, value, text, block):
        assert isinstance(family, str) and family, "New creature block has no known family"
        bucket = (track, family)
        row = self.rows[bucket]
        row["NewWholeEntityBlocks"] += 1
        row["NewWholeEntityBlockRawBytes"] += block["RawBytes"]
        row["NewWholeEntityBlockCompressedBytes"] += block["Length"]
        assert isinstance(value.get("Key"), str)
        self.keys[bucket].add(value["Key"])
        self._fields(bucket, "NewWholeBlockFieldRawBytes", text, 0, block["RawBytes"])

    def _fields(self, bucket, source, text, begin, size):
        counter = self.fields[bucket][source]
        field_bytes = 0
        for name, start, end, value_start, value_end, value in composition.object_field_spans(text, begin):
            count = len(text[start:end].encode("utf-8"))
            counter[name] += count
            field_bytes += count
            if name == "Pose" and isinstance(value, dict):
                for pose_name, _, _, pose_start, pose_end, _ in composition.object_field_spans(text, value_start):
                    if pose_name == "Nodes":
                        self.rows[bucket][source.replace("FieldRawBytes", "PoseNodesValueRawBytes")] += len(text[pose_start:pose_end].encode("utf-8"))
        counter["ObjectFraming"] += size - field_bytes

    def report(self, total, creature_group, creature_track, item_reclassified):
        rows = []
        entity_bytes = sum(row["MainEntryRawBytes"] + row["NewWholeEntityBlockRawBytes"] for row in self.rows.values())
        assert entity_bytes + sum(self.overhead.values()) == creature_group, "Creature family accounting does not conserve the group"
        track_entity_bytes = collections.Counter()
        for (track, _), row in self.rows.items():
            track_entity_bytes[track] += row["MainEntryRawBytes"] + row["NewWholeEntityBlockRawBytes"]
        assert track_entity_bytes["Creatures"] + sum(self.overhead.values()) == creature_track
        assert track_entity_bytes["Items"] == item_reclassified
        for bucket, source in self.rows.items():
            row = dict(source)
            for name in ("MainEntryRawBytes", "MainEntityEntries", "MainSharedReferenceEntries", "MainInlineEntityEntries",
                         "MainBaselineEntries", "MainDeltaEntries", "NewWholeEntityBlocks", "NewWholeEntityBlockRawBytes",
                         "NewWholeEntityBlockCompressedBytes", "FirstSeenKnownClockStates", "FirstSeenKnownClockStatesInsideWindow",
                         "SubsequentSampleTimeChanges", "SubsequentSampleTimeChangesInsideWindow", "RepeatedKnownSampleTimeEntries",
                         "UnknownSampleTimeEntries", "MainEntryPoseNodesValueRawBytes", "NewWholeBlockPoseNodesValueRawBytes"):
                row.setdefault(name, 0)
            size = row["MainEntryRawBytes"] + row["NewWholeEntityBlockRawBytes"]
            assert row["MainEntityEntries"] == row["MainSharedReferenceEntries"] + row["MainInlineEntityEntries"]
            assert row["MainEntityEntries"] == row["MainBaselineEntries"] + row["MainDeltaEntries"]
            assert row["MainEntityEntries"] == row["FirstSeenKnownClockStates"] + row["SubsequentSampleTimeChanges"] + row["RepeatedKnownSampleTimeEntries"] + row["UnknownSampleTimeEntries"]
            for source_name, expected in (("MainEntryFieldRawBytes", row["MainEntryRawBytes"]),
                                          ("NewWholeBlockFieldRawBytes", row["NewWholeEntityBlockRawBytes"])):
                row[source_name] = dict(self.fields[bucket][source_name])
                assert sum(row[source_name].values()) == expected
            rows.append({"Track": bucket[0], "Family": bucket[1], "RawBytes": size,
                         "PercentOfWindow": 100 * size / total,
                         "PercentOfCreatureGroup": 100 * size / creature_group if creature_group else 0,
                         "PercentOfCreatureEntityBytes": 100 * size / entity_bytes if entity_bytes else 0,
                         "DistinctKnownKeys": len(self.keys[bucket]), **row})
        rows.sort(key=lambda row: row["RawBytes"], reverse=True)
        overhead = sum(self.overhead.values())
        return {"Families": rows, "EntityRawBytes": entity_bytes, "TrackOverheadRawBytes": overhead,
                "TrackOverheadFields": dict(self.overhead), "ItemTrackMetadataNotReclassifiedRawBytes": dict(self.item_metadata),
                "TrackOverheadPercentOfWindow": 100 * overhead / total,
                "TrackOverheadPercentOfCreatureGroup": 100 * overhead / creature_group if creature_group else 0,
                "CreatureGroupRawBytes": creature_group, "ExactCreatureGroupAccounting": True}


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def windows(root, summary):
    rows = [json.loads(line) for line in (root / "heartbeat.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    groups = collections.defaultdict(list)
    for row in rows:
        if row.get("Phase") == "FarIdle":
            segment = row["NativeSegment"]
            groups[(segment["Index"], segment["Biome"], segment["RootName"])].append(row)
    result = []
    for (index, biome, root_name), part in groups.items():
        assert all(row["Work"]["Scope"]["Pending"] == 0 for row in part), "Initial admissions still pending"
        assert all(row["NativeTime"] <= following["NativeTime"] for row, following in zip(part, part[1:]))
        result.append({"Index": index, "Biome": biome, "RootName": root_name,
                       "Label": "雾沼神殿终段" if root_name == "Temple_Segment" else LABELS.get(biome, biome),
                       "Start": part[0]["NativeTime"] - summary["FirstCapture"],
                       "End": part[-1]["NativeTime"] - summary["FirstCapture"],
                       "NativeSamples": len(part)})
    return result


def analyze(root, output):
    summary = read_json(root / "summary.json")
    assert summary["Success"] and summary["PerformanceMeasurementValid"] and not summary.get("FaunaVisual")
    assert summary["RecordingResult"]["Status"] == "completed" and summary["RecordingResult"]["UnwrittenFrames"] == 0
    selected_windows = windows(root, summary)
    results = []
    with Path(summary["RecordingFile"]).open("rb") as stream:
        assert stream.read(8) == b"PEAKRUN1"
        header_size = struct.unpack("<i", stream.read(4))[0]
        header = json.loads(stream.read(header_size))
        stream.seek(-16, 2)
        footer = stream.read(16)
        assert footer[8:] == b"PEAKEND1"
        index_size = struct.unpack("<q", footer[:8])[0]
        stream.seek(-16 - index_size, 2)
        index = json.loads(stream.read(index_size))
        blocks = index["SharedBlocks"]
        pages = index["Pages"]
        shared_from = 0
        page_blocks = {}
        for page in pages:
            shared_to = page["SharedThrough"]
            assert shared_from <= shared_to <= len(blocks)
            new_blocks = blocks[shared_from:shared_to]
            history = page["EffectHistory"]
            position = history["Offset"] + history["Length"]
            # Writer emits pending blocks after this page and its history.
            for block in new_blocks:
                assert block["Offset"] == position, "SharedThrough does not match physical layout"
                position += block["Length"]
            next_position = pages[page["Index"] + 1]["Offset"] if page["Index"] + 1 < len(pages) else stream.seek(0, 2) - index_size - 16
            assert position == next_position, "Page/shared physical accounting gap"
            page_blocks[page["Index"]] = new_blocks
            shared_from = shared_to
        assert shared_from == len(blocks)

        def member(entry):
            stream.seek(entry["Offset"])
            payload = gzip.decompress(stream.read(entry["Length"]))
            assert len(payload) == entry["RawBytes"]
            return payload

        families = composition.EntityFamilyAttribution()
        shared_clocks = {}
        for identifier, block in enumerate(blocks):
            if block["Kind"] in (0, 3):
                payload = member(block)
                families.shared_block(identifier, block["Kind"], payload)
                value = json.loads(payload)
                shared_clocks[identifier] = (value.get("SampleTimeKnown", False), value.get("SampleTime", 0), value.get("ClockMode", 0))

        for window in selected_windows:
            selected = [page for page in pages if page["Start"] >= window["Start"] and page["End"] <= window["End"]]
            assert selected, "No complete pages in steady window"
            raw = collections.Counter()
            physical = collections.Counter()
            records = unique_records = 0
            item_creatures = collections.Counter()
            creatures = CreatureFamilyAttribution(shared_clocks, window["Start"], window["End"])
            for page in selected:
                payload = member(page)
                page_raw = collections.Counter()
                for line in payload.splitlines(keepends=True):
                    text = line.decode("utf-8").rstrip("\r\n")
                    spans = list(composition.object_field_spans(text))
                    fields = {field[0]: field[5] for field in spans}
                    if fields.get("Type") == "frame":
                        assert window["Start"] <= fields["T"] <= window["End"], "Frame outside native steady interval"
                        assert fields.get("World", {}).get("Segment", window["Index"]) == window["Index"], "Page crosses native area"
                        if fields.get("ObjectsFull"):
                            for identities in families.identities.values():
                                identities.clear()
                        for track in ("Items", "Creatures"):
                            removed = fields.get("Removed" + track, [])
                            creatures.remove(track, removed)
                            for key in removed:
                                families.identities[track].pop(key, None)
                        attributed = 0
                        for key, start, end, value_start, value_end, value in spans:
                            count = len(text[start:end].encode("utf-8"))
                            page_raw[composition.category(key)] += count
                            attributed += count
                            if key in ("Items", "Creatures"):
                                entry_bytes = 0
                                for entry, begin, finish in composition.array_entry_spans(text, value_start):
                                    identity, family, reason = families.family(key, entry)
                                    assert reason is None, "Unknown entity family in selected page"
                                    size = len(text[begin:finish].encode("utf-8"))
                                    entry_bytes += size
                                    if key == "Creatures" or family in ITEM_CREATURES:
                                        creatures.entry(key, family, identity, entry, text, begin, finish)
                                    if key == "Items" and family in ITEM_CREATURES:
                                        item_creatures[family] += size
                                framing = count - entry_bytes
                                assert framing >= 0
                                if key == "Creatures":
                                    creatures.overhead["EmptyCreaturesField" if not value else "NonEmptyCreatureArrayFraming"] += framing
                                else:
                                    creatures.item_metadata["ItemArrayFieldAndFraming"] += framing
                            elif key in ("RemovedCreatures", "CreatureOrder"):
                                creatures.overhead[key] += count
                            elif key in ("RemovedItems", "ItemOrder"):
                                creatures.item_metadata[key] += count
                        page_raw["TimelineAndControl"] += len(line) - attributed
                        records += 1
                    else:
                        page_raw["PageFraming"] += len(line)
                assert sum(page_raw.values()) == len(payload)
                raw.update(page_raw)
                unique_records += page["FrameCount"] - int(page["Overlap"])
                physical["MainPages"] += page["Length"]
                history = page["EffectHistory"]
                raw["EffectHistory"] += len(member(history))
                physical["EffectHistory"] += history["Length"]
                for block in page_blocks[page["Index"]]:
                    module = SHARED[block["Kind"]]
                    raw[module] += block["RawBytes"]
                    physical["NewSharedBlocks"] += block["Length"]
                    if block["Kind"] in (0, 3):
                        payload = member(block)
                        text = payload.decode("utf-8")
                        value = json.loads(text)
                        family = value.get("Prefab") if block["Kind"] == 0 else value.get("Kind")
                        if block["Kind"] == 3 or family in ITEM_CREATURES:
                            creatures.block("Items" if block["Kind"] == 0 else "Creatures", family, value, text, block)
                        if block["Kind"] == 0 and family in ITEM_CREATURES:
                            item_creatures[value["Prefab"]] += block["RawBytes"]
            grouped = collections.Counter()
            for module, count in raw.items():
                grouped[GROUPS[module]] += count
            track_grouped = grouped.copy()
            # These animals/plants are implemented on the Item track. Move
            # actual original entry bytes and first-written entity blocks;
            # leave Item-array framing/order/removal metadata on its track.
            reclassified = sum(item_creatures.values())
            assert reclassified <= grouped["Items"]
            grouped["Items"] -= reclassified
            grouped["Creatures"] += reclassified
            total = sum(raw.values())
            assert sum(grouped.values()) == total
            duration = selected[-1]["End"] - selected[0]["Start"]
            assert duration >= 50, "Not a near-minute steady sample"
            compressed = sum(physical.values())
            assert compressed == sum(page["Length"] + page["EffectHistory"]["Length"] + sum(block["Length"] for block in page_blocks[page["Index"]]) for page in selected)
            results.append({**window, "SelectedStart": selected[0]["Start"], "SelectedEnd": selected[-1]["End"],
                            "Seconds": duration, "FirstPage": selected[0]["Index"], "LastPage": selected[-1]["Index"],
                            "Pages": len(selected), "StoredFrameRecordsIncludingOverlap": records,
                            "UniqueFramesExcludingLeadingOverlap": unique_records,
                            "RawBytes": total, "PhysicalBytes": compressed, "PhysicalComponents": dict(physical),
                            "PhysicalMiBPerMinute": compressed / 1048576 * 60 / duration,
                            "Groups": [{"Group": group, "Label": CHINESE[group], "RawBytes": grouped[group],
                                        "Percent": 100 * grouped[group] / total} for group in ORDER],
                            "TrackGroups": [{"Group": group, "RawBytes": track_grouped[group], "Percent": 100 * track_grouped[group] / total} for group in ORDER],
                            "ItemCreatureRawBytesReclassified": dict(item_creatures),
                            "CreatureAttribution": creatures.report(total, grouped["Creatures"], raw["Creatures"], reclassified),
                            "Modules": dict(raw), "ExactRawAccounting": True, "ExactPhysicalWindowAccounting": True})
    report = {"Session": root.name, "Version": summary["InstalledVersion"], "Mvid": summary["InstalledMvid"],
              "Schema": header["Schema"], "Windows": results,
              "Method": "Whole independent seek pages strictly inside first/last native FarIdle heartbeat clocks; all pending admissions are zero. Original field UTF-8 spans, full companion effect history and newly emitted shared blocks from each selected page are counted once. Existing shared blocks established before the window are excluded. Regular page baselines/overlap and history repetition remain because they are actual new storage for independent seeking. Header/global final index/footer excluded.",
              "SceneGroup": "World, Crates, Ropes, Interactions, Spawned, Balloons, including newly emitted entity snapshots of these types.",
              "OtherGroup": "Timeline/control/event framing, page metadata, newly emitted renderer/light/pose-node leaves with shared cross-track ownership.",
              "ItemCreatureClassification": "Frog, EarlyWorm, Mandrake_Hidden original stored Item entries and newly emitted whole-item blocks are attributed to Creatures. TrackGroups retains the unmodified physical track classification. Item-array framing/order/removal fields remain on Items; cross-track shared leaves stay Other.",
              "CreatureFamilyMethod": "Each family counts actual original main entity entry spans and newly emitted whole-entity blocks (kind 3, plus item kind 0 for Frog/EarlyWorm/Mandrake_Hidden). Creature-array field/framing, removed and order fields are separate overhead; Item-track array/framing/order/removal remains on Items. PercentOfCreatureGroup includes that overhead in its denominator; PercentOfCreatureEntityBytes excludes it. Shared kind 6/7/8 stays Other. Main shared references and inline patches are counted separately. Clock metadata alone is retained across page baselines: first known states are separate from subsequent SampleTime changes inside the native FarIdle window. These are stored observation-clock transitions, including cheap native phase corrections, not native geometry reads or physics samples. New block publication is never counted as an extra observation on top of its main reference.",
              "Limits": "Percentages are additive actual uncompressed stored bytes, not gzip disk shares or CPU shares. Cross-track shared leaf data remains separate rather than inventing ownership. Complete-page cropping excludes less than roughly one second at each end. Single-player fixed-route waits do not represent every action or multiplayer. physical MiB/min uses complete finished-page bytes; prior heartbeat growth is a regression slope of asynchronous flushed bytes and can differ."}
    output.mkdir(parents=True, exist_ok=True)
    (output / "steady-composition.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = ["# 稳定等待窗口的数据类型占比", "", f"会话：{root.name}；版本：{report['Version']}；MVID：{report['Mvid']}。", "",
             "排除初态与切关，只统计 FarIdle 内完整页。百分比为压缩前实际写入字节，包含同页特效历史及首次写入的共享块，不能当作压缩后磁盘占比或 CPU 占比。", "",
             "| 区域 | 有效秒数 | 玩家 | 生物 | 物品 | 场景与机关 | 特效及历史 | 声音 | 其他 |", "|---|---:|---:|---:|---:|---:|---:|---:|---:|"]
    for result in results:
        lines.append("| " + result["Label"] + f" | {result['Seconds']:.1f} | " + " | ".join(f"{group['Percent']:.2f}%" for group in result["Groups"]) + " |")
    lines += ["", "场景与机关包含世界状态、箱子、绳索、互动机关、生成物与气球。生物合并了物品轨实现的青蛙、EarlyWorm 和曼德拉草；仅转移其实际条目及新共享实体块的编码字节，物品数组／顺序／移除字段仍在物品。其他包含时序／页元数据，以及窗口内新共享的外观、灯光和骨骼叶块。空轨道仍有字段与索引开销，占比不等于活跃对象数量或采集 CPU。JSON 同时保留原始轨道分类 TrackGroups。", "",
              "首分钟之前已有的共享初态不重复分摊；窗口内每秒独立跳转页自身的初态引用、重叠帧及特效历史仍属于真实新增数据，保留统计。文件总头尾索引不分摊进稳态窗口。", "", report["Limits"]]
    lines += ["", "## 可精确相加的压缩块组成", "", "这张表按物理 gzip 成员分组，不能进一步把混合主页面拆成精确的玩家／生物磁盘占比。", "",
              "| 区域 | 窗口压缩总 MiB | 混合主页面 | 独立特效历史 | 新共享块 |", "|---|---:|---:|---:|---:|"]
    for result in results:
        physical = result["PhysicalComponents"]
        total = result["PhysicalBytes"]
        lines.append(f"| {result['Label']} | {total / 1048576:.3f} | {100 * physical.get('MainPages', 0) / total:.2f}% | {100 * physical.get('EffectHistory', 0) / total:.2f}% | {100 * physical.get('NewSharedBlocks', 0) / total:.2f}% |")
    lines += ["", "## 稳态生物种类归因", "", "只计算真实写入的条目及首次写入的整实体共享块。引用条目不是重新采样；样本时钟变化也可能只是原生动画相位校正，不能当作骨骼读取次数。生物内占比的分母包含下面单列的轨道开销。共享外观／灯光／骨骼叶块继续留在其他组。"]
    for result in results:
        attribution = result["CreatureAttribution"]
        lines += ["", f"### {result['Label']}（{result['Seconds']:.2f} 秒）", "",
                  "| 种类 | 轨道 | 原始 MiB | 全窗口占比 | 生物内占比 | 主条目 B | 新整块 B | 实体数 | 内联条目 | 引用条目 | 窗口内后续样本时钟变化 |",
                  "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
        for row in attribution["Families"]:
            lines.append(f"| {row['Family']} | {row['Track']} | {row['RawBytes'] / 1048576:.3f} | {row['PercentOfWindow']:.2f}% | {row['PercentOfCreatureGroup']:.2f}% | {row['MainEntryRawBytes']} | {row['NewWholeEntityBlockRawBytes']} | {row['DistinctKnownKeys']} | {row['MainInlineEntityEntries']} | {row['MainSharedReferenceEntries']} | {row['SubsequentSampleTimeChangesInsideWindow']} |")
        lines.append(f"| 轨道字段／排序／移除／数组外壳 | Creatures | {attribution['TrackOverheadRawBytes'] / 1048576:.3f} | {attribution['TrackOverheadPercentOfWindow']:.2f}% | {attribution['TrackOverheadPercentOfCreatureGroup']:.2f}% | {attribution['TrackOverheadRawBytes']} | 0 | — | — | — | — |")
    (output / "steady-composition.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps({"Session": root.name, "Windows": [{"Label": result["Label"], "Seconds": result["Seconds"], "Groups": result["Groups"]} for result in results]}, ensure_ascii=False))


if __name__ == "__main__":
    analyze(Path(sys.argv[1]), Path(sys.argv[2]))
