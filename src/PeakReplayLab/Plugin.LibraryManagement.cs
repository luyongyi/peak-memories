using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

public sealed partial class Plugin
{
    private sealed class PreparedDeletion
    {
        public ReplayTrashTicket Ticket = null!;
        public MemoriesLibraryItem Item = null!;
        public long Generation;
    }

    private ReplayTrash replayTrash = null!;
    private Task<PreparedDeletion>? preparingDeletion;
    private Task<ReplayTrashResult>? movingDeletion;
    private PreparedDeletion? preparedDeletion;
    private long deletionGeneration;
    private string requestedDeletion = "", deletionError = "";
    private bool LibraryManagementBusy => preparingDeletion != null || movingDeletion != null || preparedDeletion != null;
    private bool FileManagementAllowed => SceneManager.GetActiveScene().name == "Title" &&
        !ReplaySafety.Active && theatre?.Busy != true && saving == null && fullRun == null && closingFullRun == null &&
        listing == null && fullRunListing == null && !RouteUploadBusy;
    private bool CanManageRecordings => FileManagementAllowed && !LibraryManagementBusy;
    private bool CanConfirmDeletion => FileManagementAllowed && preparedDeletion != null &&
        preparingDeletion == null && movingDeletion == null && preparedDeletion.Generation == deletionGeneration;

    private void InitializeLibraryManagement() => replayTrash = new ReplayTrash(DirectoryPath, FullRunDirectoryPath,
        Path.Combine(Paths.BepInExRootPath, "PeakReplayLab", "Trash"));

    private bool RequestReplayDeletion(string path)
    {
        if (!CanManageRecordings)
        { Note("请等录像读取、保存或回放结束，再删除录像。"); return false; }
        // Only a real catalog entry can initiate a confirmation; backend validation
        // independently confines the operation to one file in our recording roots.
        if (!highlightCards.Concat(fullRunCards).Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
        { Note("这份录像不在当前列表，请刷新后重试。"); return false; }
        long generation = ++deletionGeneration;
        requestedDeletion = path; deletionError = ""; preparedDeletion = null;
        preparingDeletion = Task.Run(() =>
        {
            ReplayTrashTicket ticket = replayTrash.Prepare(path);
            // Re-read metadata for the confirmation, not an old list index/header.
            MemoriesLibraryItem item;
            if (path.EndsWith(".peakreplay", StringComparison.OrdinalIgnoreCase))
            {
                ReplayHeader? header = null; string error = "";
                try { header = ReplayFiles.ReadHeader(path); } catch (Exception e) { error = e.Message; }
                item = MemoriesLibraryItem.FromHighlight(path, header, error);
            }
            else
            {
                FullReplayInfo? info = null; string error = "";
                if (path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) error = "未完成录像";
                else try { info = FullReplayArchive.ReadInfo(path); } catch (Exception e) { error = e.Message; }
                item = MemoriesLibraryItem.FromFullRun(path, info, ticket.Length, error);
            }
            var current = replayTrash.Prepare(path);
            if (current.Length != ticket.Length || current.LastWriteTimeUtc != ticket.LastWriteTimeUtc)
                throw new IOException("文件在读取期间发生变化，请返回列表重新确认。");
            return new PreparedDeletion { Ticket = ticket, Item = item, Generation = generation };
        });
        ObserveFault(preparingDeletion);
        return true;
    }

    private void ConfirmReplayDeletion()
    {
        if (!CanConfirmDeletion)
        { deletionError = "当前不能删除：请等待保存完成，或返回列表重新检查。"; return; }
        var ticket = preparedDeletion!.Ticket;
        preparedDeletion = null; deletionError = "";
        // This is the sole mutation entry point, reached only after explicit
        // confirmation. Closing the UI after this point does not undo a move.
        movingDeletion = Task.Run(() => replayTrash.MoveToTrash(ticket));
        ObserveFault(movingDeletion);
        Note("正在将录像移入本地回收目录…", notify: false);
    }

    private void CancelReplayDeletion()
    {
        deletionGeneration++;
        requestedDeletion = ""; preparedDeletion = null; deletionError = "";
        // A metadata read is harmless and will be harvested/discarded. A confirmed
        // atomic move must finish; never abandon the task or start a duplicate.
    }

    private void UpdateLibraryManagement()
    {
        if (preparingDeletion?.IsCompleted == true)
        {
            try
            {
                var result = preparingDeletion.GetAwaiter().GetResult();
                if (result.Generation == deletionGeneration && requestedDeletion.Length != 0)
                    preparedDeletion = result;
            }
            catch (Exception e)
            {
                if (requestedDeletion.Length != 0)
                { deletionError = "无法准备删除：" + e.Message; Logger.LogWarning(deletionError); }
            }
            finally { preparingDeletion = null; }
        }
        if (movingDeletion?.IsCompleted != true) return;
        try
        {
            var result = movingDeletion.GetAwaiter().GetResult();
            // No scans run during a mutation. Replace (do not mutate) all cached
            // arrays, then rescan, so a removed last-page entry cannot reappear.
            bool Keep(string path) => !string.Equals(path, result.SourcePath, StringComparison.OrdinalIgnoreCase);
            entries = entries.Where(e => Keep(e.Path)).ToArray();
            fullRunEntries = fullRunEntries.Where(e => Keep(e.Path)).ToArray();
            highlightCards = highlightCards.Where(e => Keep(e.Path)).ToArray();
            fullRunCards = fullRunCards.Where(e => Keep(e.Path)).ToArray();
            requestedDeletion = ""; deletionError = "";
            nativeLibrary?.DeletionFinished(result.SourcePath);
            Logger.LogInfo("Recording moved to recoverable local Trash: " + result.SourcePath + " -> " + result.DestinationPath);
            Note("录像已移到本地回收目录，可恢复。");
        }
        catch (Exception e)
        {
            deletionError = "删除未完成：" + e.Message;
            Logger.LogWarning(deletionError); Note(deletionError);
        }
        finally { movingDeletion = null; }
        if (library) { if (fullRunLibrary) RefreshFullRuns(); else RefreshLibrary(); }
    }

    private static void ObserveFault(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; },
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
}
