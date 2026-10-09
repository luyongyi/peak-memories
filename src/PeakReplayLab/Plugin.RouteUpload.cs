using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

public sealed partial class Plugin
{
    private ConfigEntry<string> routeUploadEndpoint = null!;
    private CancellationTokenSource? routeCancellation;
    private Task<TrajectoryExportResult>? routeExport;
    private Task<TrajectoryUploadReceipt>? routeSending;
    private TrajectoryExportResult? routePackage;
    private Uri? routeDestination;
    private string routeSummary = "", routeError = "";
    private bool routeFinished;
    private sealed class RouteProgress { public float Value; public int UploadedParts; public int TotalParts; }
    private RouteProgress routeProgress = new();
    private int routeDisplayedParts = -1;
    private bool RouteUploadBusy => routeExport != null || routeSending != null || routePackage != null;
    private float VolatileRouteProgress() => Volatile.Read(ref routeProgress.Value);
    private string RouteExportDirectory => Path.Combine(Paths.BepInExRootPath, "PeakReplayLab", "TrajectoryExports");

    private void InitializeRouteUploads() => routeUploadEndpoint = Config.Bind("Routes", "UploadEndpoint",
        "https://peak.mylus.cn/api/route-uploads", "Optional manual trajectory upload from sealed continuous .peakrun recordings only. Export at most 10Hz cm coordinates and names; never uploads replay files. HTTPS required except loopback development.");

    private bool RequestRouteExport(string path)
    {
        if (!FileManagementAllowed || LibraryManagementBusy || !library || RouteUploadBusy)
        { Note("请在主菜单等录像读取、保存或回放结束，再上传轨迹。"); return false; }
        var item = fullRunCards.FirstOrDefault(card => card.IsFullRun && card.Playable &&
            string.Equals(card.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item == null || !path.EndsWith(".peakrun", StringComparison.OrdinalIgnoreCase))
        { Note("只支持已封存的完整录像，请在「完整录像」里选择。"); return false; }
        try
        {
            routeDestination = TrajectoryUploader.Endpoint(routeUploadEndpoint.Value);
            routeCancellation = new CancellationTokenSource();
            var cancellation = routeCancellation.Token;
            string output = RouteExportDirectory;
            routePackage = null; routeFinished = false; routeError = "";
            var progressState = new RouteProgress();
            routeProgress = progressState;
            routeSummary = "正在从完整录像筛选轨迹…\n最多 10 Hz · 厘米坐标 · 不上传录像本体";
            routeExport = Task.Run(() => ReplayTrajectoryExporter.Export(path, output, cancellation,
                progress => Volatile.Write(ref progressState.Value, (float)progress)), cancellation);
            ObserveFault(routeExport);
            return true;
        }
        catch (Exception e)
        { CancelRouteUpload(); Note("准备轨迹失败：" + e.Message); return false; }
    }

    private void ConfirmRouteUpload()
    {
        if (routePackage == null || routeDestination == null || routeSending != null || routeFinished ||
            routeExport != null || !library || SceneManager.GetActiveScene().name != "Title" || ReplaySafety.Active || theatre?.Busy == true)
            return;
        routeError = "";
        var package = routePackage;
        var destination = routeDestination;
        var cancellation = routeCancellation!.Token;
        var progressState = routeProgress;
        Volatile.Write(ref progressState.TotalParts, package.Paths.Length);
        Volatile.Write(ref progressState.UploadedParts, 0);
        routeDisplayedParts = -1;
        routeSending = Task.Run(async () =>
        {
            var receipt = await TrajectoryUploader.UploadManyAsync(package.Paths, destination, cancellation,
                (done, total) =>
                {
                    Volatile.Write(ref progressState.UploadedParts, done);
                    Volatile.Write(ref progressState.Value, (float)done / total);
                }).ConfigureAwait(false);
            // A small local acknowledgement is separate from the immutable package.
            // Server success must remain success even if writing the local note fails.
            try
            {
                string json = JsonConvert.SerializeObject(new
                {
                    receipt.UploadId, receipt.UploadIds, receipt.ModerationStatus, receipt.MapCompatibility, receipt.Duplicate,
                    Destination = destination.GetLeftPart(UriPartial.Authority), ReceivedUtc = DateTime.UtcNow.ToString("O")
                }, Formatting.Indented);
                File.WriteAllText(package.Path + ".receipt.json", json);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return receipt;
        }, cancellation);
        ObserveFault(routeSending);
    }

    private void UpdateRouteUploads()
    {
        if (routeExport?.IsCompleted == true)
        {
            try
            {
                routePackage = routeExport.GetAwaiter().GetResult();
                string names = string.Join(" · ", routePackage.PlayerNames.Select(name => MemoriesLibraryModel.Plain(name, 48)));
                routeSummary = $"将上传到：{routeDestination!.Host}\n" +
                    $"{routePackage.PlayerCount} 位玩家 · {routePackage.PointCount:N0} 个坐标 · 最多 10 Hz\n" +
                    $"压缩后 {routePackage.CompressedBytes / 1048576d:F2} MiB · {ClockLabel(routePackage.DurationMs / 1000d)}\n" +
                    $"全部成员 · {routePackage.Paths.Length} 个轨迹分包（自动全部上传）\n" +
                    $"地图：{routePackage.Scene} · {routePackage.DifficultyLabel}\n" +
                    "姓名：" + MemoriesLibraryModel.Plain(names, 240) +
                    "\n只含存活轨迹、姓名、地图与难度及少量过关事件。\n幽灵移动不上传，复活后另起一段。\n有效投稿自动审核；个人完整线路才计入公开热力。";
                if (!routePackage.NativeEvidence)
                    routeSummary += "\n旧录像缺少个人过关证据，保留为未知，不计入默认热力。";
                else if (!routePackage.StageGatesKnown)
                    routeSummary += "\n部分关卡缺少原生边界，仅有完整证据的关卡参与路线统计。";
                if (routePackage.MapLandmarkCount < 3)
                    routeSummary += "\n这份录像未记录足够的地图地标；投稿会保留，地标核验完成前不会叠到地图。";
            }
            catch (OperationCanceledException) { routeError = "已取消轨迹筛选。"; }
            catch (Exception e) { Logger.LogWarning("Trajectory export failed: " + e); routeError = "轨迹筛选失败：" + MemoriesLibraryModel.Plain(e.Message, 180); }
            finally { routeExport = null; }
        }
        if (routeSending != null && !routeSending.IsCompleted)
        {
            int completed = Volatile.Read(ref routeProgress.UploadedParts);
            if (completed != routeDisplayedParts)
            {
                routeDisplayedParts = completed;
                routeSummary = $"正在上传全队轨迹…\n已确认 {completed}/{Volatile.Read(ref routeProgress.TotalParts)} 个分包。\n全部分包确认后才显示上传完成。";
            }
        }
        if (routeSending?.IsCompleted == true)
        {
            try
            {
                var receipt = routeSending.GetAwaiter().GetResult();
                routeFinished = true;
                string map = receipt.MapCompatibility == "matched" ? "地图版本匹配。" : "等待对应版本地图；不会叠到旧地图。";
                string moderation = receipt.ModerationStatus switch
                {
                    "approved" => "轨迹已审核通过；完整关卡可进入公开路线。",
                    "hidden" => "投稿已接收，目前已隐藏，不展示公开路线。",
                    "rejected" => "投稿已接收，但未列入公开路线。",
                    _ => "轨迹已上传，等待审核。"
                };
                routeSummary = (receipt.Duplicate ? "这份轨迹已经提交。\n" : "") + moderation + "\n" + map +
                    $"\n全队 {receipt.UploadIds.Length} 个轨迹分包均已确认。" +
                    "\n投稿编号：" + receipt.UploadId.Substring(0, 16) +
                    "\n查看路线：首页 → 进入地图 → 对应关卡 → 线路图层。" +
                    "\n选择「大家的路线」或「热力图」（须审核通过且地图匹配）。";
                Logger.LogInfo("Trajectory receipt: " + receipt.UploadId + "; " + receipt.ModerationStatus + "; " + receipt.MapCompatibility);
            }
            catch (OperationCanceledException) { routeError = "上传等待已取消；可再次提交查询同一轨迹的收录结果。"; }
            catch (Exception e) { Logger.LogWarning("Trajectory upload failed: " + e); routeError = "上传未确认：" + MemoriesLibraryModel.Plain(e.Message, 180) + "\n筛选包已保留，可以重试。"; }
            finally { routeSending = null; }
        }
    }

    private void CancelRouteUpload()
    {
        // Keep cancellation alive until the export/HTTP worker has stopped using it.
        var cancellation = routeCancellation;
        var exporting = routeExport;
        var sending = routeSending;
        routeCancellation = null;
        cancellation?.Cancel();
        routeExport = null; routeSending = null; routePackage = null; routeDestination = null;
        routeSummary = routeError = ""; routeFinished = false;
        if (cancellation == null) return;
        Task work = sending ?? (Task?)exporting ?? Task.CompletedTask;
        _ = work.ContinueWith(_ => cancellation.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void OpenRouteWebsite()
    {
        if (routeDestination == null) return;
        Application.OpenURL(new Uri(routeDestination, "/").AbsoluteUri);
    }
}
