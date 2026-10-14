# 自动传送、篝火推进与停留路线：独立采集验收规则

日期：2026-10-13。只读审查当前生产采集代码和既有探针；本报告没有启动游戏、改动生产源码或安装插件。地图、事件、动作是否真正出现必须由本轮原生运行证明，不能由旧报告中的数量或名称推定。

## 1. 每个步骤必须留下两套证据

1. **原生期望**：步骤开始时对当前关的真实 native component 做一次盘点；保留 instance ID、scene handle、native owner root、类型、activeSelf/activeInHierarchy、初始状态。动作之后由 native 完成回调和实际状态改变证明刺激生效。
2. **采集结果**：下一次接受的 ReplayFrame、实际 frame Key、事件序列/时间、末态，以及封盘后的逐页读回结果。producer 的目录成员数和 AcceptedFrames > 0 均不能作为独立完整性期望。

实际出现在 native 场景中的受支持实例没有采集记录应报错；没有出现或没有动作成功的类别标为 **未覆盖/原生刺激未成功**，不能以 0 个缺失算通过。正常的无变化快照、场景 tick 复用、跨页初始状态引用不能算重复采集。源 ID 到录像 Key 可通过生产 binding 建一次关联，但状态和出生期望必须来自 native 场景/回调。

每个对象保留关联直到本轮结束：旧关退役后 producer entry 被删除，若只看当前字典，会把真实泄漏误标为 unresolved/tail。Unity instance ID 的复用还需 scene handle 和对象引用代次。

## 2. 地图边界与路线

- 每轮先保存当前 `MapHandler.segments`、`variantSegments` 的原始 root 与实际 getter 选择的 root、Biome enum、campfire root、activeInHierarchy。先跑当前地图实际选定的路线，不强开不被游戏选中的变体。
- `currentSegment` 会在旧图退役之前提前递增。每关应独立记录 `oldRoot inactive`、`nextRoot active` 时刻，以实际原生边界划分 bootstrap、transition、active、settled 窗口。
- 普通步骤建议：出生和初态完成 → 原地静置 30–60 秒 → 传送近互动点并停留 → 原生互动 → 真实动作结束后再停留 30–60 秒 → 持有同一个物品推进篝火 → 新图初态完成后重复。稳定窗口至少独立统计最后 20 秒；不要把切关和首次建档混入静置斜率。
- 篝火须调用原生 `Campfire.Interact_CastFinished` 或其已验证入口，并独立验证火已点亮、原生 `GoToSegment` 生效、旧/新根实际开关和雾的过渡。只把角色坐标传到下一地图不能验证切关隔离。
- Native 生物群系：Shore 0、Tropics 1、Alpine 2、Volcano 3、Mesa 6、Roots 7、Swamp 8、Temple 9、Void 17。其余 enum 存在并不等于本次种子/路线实例存在。普通 5 个关卡槽和 Nadir 须按原生选定 root 分别记录，不能把槽序号直接当 biome。
- 测试路线外的未来地图/未选变体只允许轻量句柄和 `Header.MapObjects`；任何完整 Item/Creature/Crate/Rope/Effect/Audio/Interaction 帧或姿态读取都不应提前发生。Header 的地图切换信息是快速跳转必要元数据。
- 旧图 `segmentParent` 实际关闭后，旧图普通对象应退出实时目录，下一次 scene capture 后不再周期读骨骼/外观/灯光。已观察而待提交的短特效/音效可保留结束证据，不应无限保留为活跃。
- 例外必须列明：原生仍活动的上一篝火、全局天气/雾、已拿取/背包/粘附角色物品、真正全局对象。根级网络对象以出生调用者/原生返回实例的 provenance 判归属，不凭坐标/名字猜；没有可证归属的对象列为未知，不能宣称全部旧关对象均已清除。

## 3. 按原生存在实例安排的内容矩阵

| 原生内容 | 必需刺激/独立期望 | 录像/回放断言 |
|---|---|---|
| 每个关的静态物品/脆梅、行李箱/补给箱 | 初态；打开箱子产生实际内容；拿取、装备、放下、远离后静置，再拿起 | 初态数匹配可记录 native 实例；开箱边界和出生内容匹配；身份无重复；稳定晚期不更新固定姿态/外观；正放/回跳不瞬隐 |
| 持有、背包、粘附物 | 使用同一 native Item 穿过篝火切关，然后丢弃/重新拾取 | HolderId、Inventory instance 与事件 ActorId 同属原角色；old-root 关闭不丢失物品；释放后仍可录；不生成重复身份 |
| 绳索/藤索/锚 | 原生发射/部署、附着、攀爬/施力、停止动作、静置、新动作 | 点/骨骼变化和最终拓扑对应 native；停止后高频读取终止；新动作再唤醒；部署中新生对象可直接跳转 |
| Spider 与 Web Variant（实际出现关） | native Drop/Grab/LetGo/Bonk 完成；抓捕前挂网、抓捕后返回 | 本体状态、蛛丝、Actor WebWrap、眩晕特效一致；验证 idle clip 后挂网不读骨骼；未验证 idle asset 的保守高频另报，不伪算休眠 |
| Beetle（实际出现关） | native sleeping 边界、接近唤醒、移动/攻击、重新睡眠 | sleeping checkpoint 保留，睡眠晚期 GeometryReads 0；唤醒下一 scene tick 有动作；sleeping/awake 边界不少录 |
| Roots 森蕈 | MushroomZombie 的 native Arise/追击/扑咬/死亡；Forest_SporeFungus 被真正触发 | NPC 与 Player zombie resource 分开；295 renderer 的实际外观/骨骼可呈现；死亡 Skeleton 出生；陷阱 armed→consumed，VFX_SporeExplo 出生且可回跳 |
| Tropics 雨林 | 两种真实 TriggerEvent 完成：Jungle_SporeMushroom、Jungle_SporeMushroomExplo；本关实际 spiders/flytrap | 两陷阱各只发生一次 consumed 边界，对应 VFX_PoisonExplo/VFX_SporeExploExplo 各有独立 run；无未来关提前初态 |
| Swamp 雾沼 | Frog/MobItem 跳跃、吐舌/收舌、抓取/抛出/烹饪；本关真实 raven/ghost | Frog 身体 Item 与 FrogTongue 的世界空间 Line 同一时间；200 点线不能裁到蜘蛛128点；舌收回正确；烹饪产物 FrogLegs 新生；VoiceThief 出生/飞离/嘴型须单独证据 |
| Mesa 荒漠（实际出现时） | Antlion 出头/攻击/收回；TumbleWeed 真实滚动；Scorpion 抓取/翻身/烹饪 | 蚁狮 `_Str` 流沙与骨骼动作；风滚草位置；蝎子 Item 动作与粒子；每类不因别的 Item 记录存在而算覆盖 |
| Volcano/Caldera 火山 | Condor native 盘旋→俯冲→携带→松手→返航；原生 lava 环境变化 | 四状态、鸟姿态及被抓玩家对应；音效和火山环境资源存在；缺少实际行为则未验收 |
| Alpine 或其他实际出现普通图 | 本图原生天气/风/霜/物品；按盘点存在的动态内容行动 | 天气归属和活动时钟正确，未来 storm 不准入；不能用进入地图替代所有生物动作测试 |
| Scoutmaster 全局/激活 | 原生待命→激活→追击/抓投→退场 | 待命无完整 creature 帧/骨骼读取；激活动作有帧；退场停止，不能用 activeInHierarchy 代表激活 |
| 蜂巢/BeeSwarm | 蜂巢 native 摔破/生成独立蜂群，追逐和散群 | 蜂巢 Item、独立蜂群粒子本体、emission 与 force field/音效分别核对；蜂巢存在不能推导蜂群已录 |
| Capybara/Mirage、PlayerGhost、GhostBall、ScoutmasterGhostOrbiter、Skeleton | 原生存在/出生/销毁，真实缩放/外观/漂移/遗骸状态 | 不以模型资源能解析代替出生采集；player ghost init 后外观正确；记录附着角色和真实显隐 |
| Temple/Citadel/Nadir（本轮可进时） | GhostFire 点亮和熄灭、SoulPillar 交互/破坏、原生 portal/Nadir 进入 | 1秒 ghost fade 和最终值完整；重复同步 setter 不续高频；portal 后处理和 native Void 状态可回跳；每个可进区域独立验收 |
| 全地图通用道具动作 | 火把/灯笼点灭/耗尽、烹饪、烟、flare、mandrake/worm 动作、一次性使用、投掷/破碎 | Primary/Secondary/Uses/Fuel/Cooked/Lit、真实外观/灯光和结束帧；静止使用过程中的真实持续视觉允许活动采样 |

上表是“存在时必须测”的矩阵。当前代码支持的 creature kind：spider、mushroom-zombie、condor、frog-tongue、spore-trap、beetle、scoutmaster、antlion、venus-flytrap、tumbleweed、voice-thief、skeleton、scoutmaster-ghost、capybara、ghost-ball、player-ghost。Frog 身体、Scorpion、Mandrake 等走 Item，BeeSwarm 走 Effect；逐 kind 缺席时必须保留未覆盖项。

## 4. 短事件必须独立核对

- 在测试 companion 上观察 native 方法完成/实际返回对象，不能 hook `ItemEventCapture` 或 `EffectReplayCapture` 产出作为期望。陷阱 Trigger 只启动协程，实际 UnityEvent 约晚一秒，期望应在完成之后建立。
- Effect 期望键：native prefab/resource、原生 spawn 实例、发生时刻、owner root、播放代次；拿同一资源连放两次也应有不同 run。短到一个 30 Hz tick 内已结束的效果仍要有出生和结束证据。
- Audio 观察原生 SFX pooled play/stop、直接 native owner 的 AudioSource play、PlayOneShot clip 参数；`AudioSource.clip` 对 PlayOneShot 不一定改变，靠它盘点会漏。短音独立 native 开始时间与记录 StartedAt 对应，pending transient 不能在切关退役时丢失。聊天语音按现有产品范围单列，不能当原生游戏 SFX 丢失。
- 一个场景 tick 之内同一 source 同一 clip 重放要两个 sound Key；反向/跨页回跳后暂停位置正确、播放没有旧声音串入下一关。
- 验证离散事件 Sequence 全局单调、时间有限且在对应 capture interval、Kind 合法、已知 item/actor 关联成立、无 overflow/hook/validation 错误。分帧 ItemEvent 可能发生在 native 状态改变前的 prefix，语义结束仍用实际 native 后态核验。
- 给每次刺激保存 native→recorded 的耗时，正常场景采样期望下一次实际 admitted scene tick；首次普通建档受公平预算影响，等待 BootstrapReady/Pending=0 后再开始覆盖统计。不能硬要求每个 ordinary birth 都在 33 ms 内，但真实动作应绕过普通初态队列。

## 5. 开销、占用与稳定性

使用 `ReplayCapture` 自带 diagnostics 的窗口差值，并分别报告每秒值：

- `SceneDiagnostics`: 30 Hz、SkippedSlots、CaptureTicks/ReusedTicks；本地玩家和队友网络采样另列。
- `ItemDiagnostics`: NativeReads、RootReads、ChildReads、LightReads、AnimationProbes、SleepTransitions、WakeTransitions、FallbackReads。ActiveVisits/RegistryVisits/SleepingDistanceChecks 是工作计数，不能叫 Unity getter 或毫秒。
- `RopeDiagnostics`: PointReadCount/BoneReadCount/ProbeCount 与 SleepingCount/ColdSkipCount，ManagedMotionChecks（睡眠绳可10 Hz）和 VisibilityProbeCount 单列。
- `CreatureDiagnostics`: GeometryReads/NodeVisits/LineReads/LinePointVisits，SpiderIdleReads/SpiderIdleSkips/SpiderActionReads，BeetleAwakeReads/BeetleSleepingSkips，BoundaryReads。不能用 aggregate GeometryReads 增长证明某个睡眠对象在读，必须按 entry.Last/SampleTime 身份与 native semantic state关联。
- `InteractionDiagnostics`: Geometry/Appearance/Light/Post/GloomReadCount、EventWakeCount、EventBurstReadCount、ContinuousReadCount、UnknownFallbackReadCount、SleepingSkipCount。真实 animation/physics/tween 和 fallback 与事件续期分别列。
- `SpawnedDiagnostics`: GeometryReads/LightReads/AppearanceReads/SleepingGeometrySkips。
- `EffectDiagnostics`: BaselineModuleReads/EventModuleReads/AnimatedModuleReads/DistanceModuleReads/UnknownAnimationModuleReads、EmitterTransformReads、CullerProbes、NativeAnimationProbes、NativeParticleStateReads。固定 VFX 不因时钟继续增长而重复读模块；4 Hz culler 或真实改变参数例外。NativeParticleStateReads 应恒0。
- `AudioDiagnostics`: PoolDiscoveryPasses、IdlePoolSkips、NativeLivenessReads。正在播的源检查允许增长；闲置 pool 源只增长 IdlePoolSkips。
- `SegmentDiagnostics`: Pending/Admitted/Retirements/Preserved、CallbackFailures/FailedBaselines/DeferredOrigins/PendingRebind/MaximumRebindBatch。轻量 candidates 保留不等于内存泄漏，完整 pose/appearance binding 才是重点。

建议每个阶段输出：真实持续时间、accepted/written/queued、文件已封 page 字节增长、MB/min、完整模块 UTF-8 原始字节、模块压缩分摊估算、CPU Capture 总体和各 stage mean/p95/max、render 帧数和>33/>100/>500 ms、managed memory/working-set/private-byte 的前后和峰值、GC 次数。CPU/内存使用既有 instrumentation 的计量窗，native 覆盖扫描/反射序列化只在边界或另窗，避免诊断开销冒充采集性能。

文件 BytesWritten/长度会落后缓冲队列；以封盘 writer 和实际 page index 对账。共享字典/首次外观计入首次引入阶段，独立 page 引用计入对应 page；不能把多页引用还原成每帧完整体积。deflate 跨字段共享，压缩模块占比只能按每个 actual gzip member 明确分摊估计，不能标精确可删除字节。内存爬升须区分120秒滚动缓存达到稳态前、完整录像后台队列、资源模板缓存和重复跳转 pool；等待稳定并重复至少3次往返才可判累计泄漏。

## 6. 封盘后的回放核验

逐页反向读回全录像，accepted==written，info.Complete=true，无坏页/事件丢失。`ReplayContract --native-audit <file> --all-pages` 已可核对 effect-history 同时间同 Key、当前资源家族、native particle checkpoints=0，但它没有 native 期望清单，需与本轮事件 ledger 合并。

至少选：各关初态、每个成功事件前/中/后、每次篝火切换前/中/后、最后静置末态。乱序往返≥3轮；等待 PlaybackReady、Buffering=false、Pending=false 后比对录像快照、实际视觉实体/资源 Missing/Lagging。静置对象跨页不应闪隐；effect 需检查 native replica Ready/Warming/Approximate 与真实可见输出，声音光有 Key 不等于听感正确。

既有路径参考：`local/checks/segment-capture-live-0.8.23-20261013/driver/SegmentLiveProbe.cs`（保护存档、原生campfire、两种孢子、accepted/frame-root/seek）；`local/checks/capture-runtime-20261011/driver/CoverageProbe.cs`（明确只证明 observed families）；`local/checks/region-cost-0.8.19-20261012/analyze_regions.py`（按gzip member存储分摊，旧硬编码边界不可照搬）；`tests/ReplayContract/NativeCoverageInspection.cs`（只读全页核对）。
