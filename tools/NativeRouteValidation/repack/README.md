# 同源存储重写

这是一项离线分析工具，不是新的原生测试。它逐页读取完整 Schema 25/26 录像，以加载的 `ReplayContract.dll` 当前 schema 重写场景和共享块，保持原始独立页边界、decoded 预算、全局覆盖元数据及玩家 Actor 补丁／关节 wire payload。保留 wire 可以避免再次量化已经解码的玩家 quaternion。

```powershell
dotnet build tests/ReplayContract/ReplayContract.csproj -c Release
dotnet build tools/NativeRouteValidation/repack/ReplayRepack.csproj -c Release
dotnet tools/NativeRouteValidation/repack/bin/Release/net8.0/ReplayRepack.dll <原会话/summary.json> <新的私人输出目录>
```

输入也可以直接是 `.peakrun`。工具拒绝覆盖非空输出目录；源文件只读。传入 summary 时，成功核对后复制原 summary/heartbeat，更新录像路径，并写入 `OfflineRepack` 来源说明。原生 InstalledVersion、MVID、动作、采集时间和性能心跳仍保留原会话的值。

全部页须独立逆序读回，所有 DTO 的公开属性／字段（包括 float/double 位和不进入普通 JSON 的 joint SampleTime）相等；两个文件各自的独立 effect history 也必须与当前帧及对方历史相等。只有通过这些检查才产生 `repack-verification.json` 和更新后的 summary。中途失败留下的私人录像不能当作验证通过的结果。

对照 protocol 变更时，应使用同一重写管线比较旧 schema control 和新 schema，不能直接把 .NET 重写大小与 Unity/Mono 原录像之差视为协议或原生性能收益。旧 DLL 可从明确的源码检查点在 ignored artifacts 中构建。工具支持 `ReplayContractAssemblyPath` 构建参数；更严格的同 runner 对照可将完全相同的 `ReplayRepack.dll/.deps.json/.runtimeconfig.json` 放入两个输出运行目录，仅分别提供旧／新 `ReplayContract.dll` 和相同 Newtonsoft.Json。目标 schema 在运行时从实际加载的 DLL 读取，避免编译时常量把新旧目标混淆。

之后对两份重写会话运行 `steady-composition.py`，只比较完全相同的 FarIdle 页窗口。保存 runner/DLL 校验值，并把 control 与 candidate 的输出目录及验证报告一起保留。压缩成员总字节可以精确比较；混合页面内部的生物磁盘占比仍不可相加推算。
