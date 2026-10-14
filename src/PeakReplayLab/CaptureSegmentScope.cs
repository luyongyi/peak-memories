using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace PeakReplayLab;

// This directory owns only native handles and their map-root membership. It must
// run before a track constructs a component catalogue, pose, or visual binding.
internal sealed class CaptureSegmentScope : IDisposable
{
    internal sealed class Root
    {
        internal readonly GameObject Source;
        internal readonly HashSet<SegmentCaptureClient.Entry> Members = new();
        internal readonly List<Root>? SegmentOwners;
        internal bool EnvelopeExamined;
        internal bool IsEnvelope => SegmentOwners != null;
        internal int SelectedGeneration;
        internal bool Selected, Active, WasActive;
        internal Root(GameObject source, bool envelope = false)
        { Source = source; if (envelope) SegmentOwners = new(); }
    }

    private static readonly AccessTools.FieldRef<MapHandler.MapSegment, GameObject> rawParent =
        AccessTools.FieldRefAccess<MapHandler.MapSegment, GameObject>("_segmentParent");
    private static readonly AccessTools.FieldRef<MapHandler.MapSegment, GameObject> rawCampfire =
        AccessTools.FieldRefAccess<MapHandler.MapSegment, GameObject>("_segmentCampfire");
    private readonly Dictionary<int, Root> roots = new();
    private readonly List<Root> orderedRoots = new();
    private readonly Dictionary<int, Root> envelopes = new();
    private readonly List<Root> orderedEnvelopes = new();
    private readonly List<SegmentCaptureClient> clients = new();
    private readonly List<Root> selectedRoots = new();
    private readonly Action<string>? warning;
    private readonly Func<GameObject, GameObject?>? originResolver;
    private readonly Dictionary<int, HashSet<SegmentCaptureClient.Entry>> originDependents = new();
    private readonly Dictionary<int, GameObject> deferredOrigins = new();
    private readonly HashSet<int> changedBoundaryIds = new();
    private readonly Queue<SegmentCaptureClient.Entry> deferredRebind = new();
    private readonly Func<double> retryClock;
    private int selectedGeneration, nextClient, warnings, pendingRebind, lastRebindBatch, maximumRebindBatch;
    private bool disposed, observing, draining, directoryReliable;
    private long baselineAdmissions, urgentAdmissions, globalAdmissions, retirements, preserved, parentWalks, rootChecks, callbackFailures, drainCalls;
    private long rebinds;
    private int lastDrainAdmissions, maximumDrainAdmissions;
    internal static CaptureSegmentScope? Active { get; private set; }

    internal CaptureSegmentScope(Action<string>? warning = null, Func<GameObject, GameObject?>? originResolver = null,
        Func<double>? retryClock = null)
    {
        if (Active != null) throw new InvalidOperationException("A capture segment scope is already active.");
        this.warning = warning;
        this.originResolver = originResolver;
        this.retryClock = retryClock ?? MonotonicSeconds;
        Active = this;
        Observe();
    }

    internal SegmentCaptureClient CreateClient(Action<GameObject> admitCallback, Action<int> retireCallback,
        Func<GameObject, bool>? preserve = null)
    {
        if (disposed) throw new ObjectDisposedException(nameof(CaptureSegmentScope));
        if (admitCallback == null) throw new ArgumentNullException(nameof(admitCallback));
        if (retireCallback == null) throw new ArgumentNullException(nameof(retireCallback));
        var client = new SegmentCaptureClient(this, admitCallback, retireCallback, preserve);
        clients.Add(client);
        return client;
    }

    // Only the small native root directory is examined on an ordinary tick.
    // currentSegment is deliberately absent: PEAK increments it before retiring
    // the old hierarchy, and retains the previous campfire independently.
    internal void Observe()
    {
        if (disposed || observing) return;
        observing = true;
        try
        {
            var current = MapHandler.Exists ? MapHandler.Instance : null;
            selectedRoots.Clear();
            if (current)
            {
                ReadRawRoots(current.segments);
                ReadRawRoots(current.variantSegments);
                // Resolve selection atomically. A broken native property must not
                // turn a partially read directory into false retirement events.
                if (current.segments != null)
                    foreach (var segment in current.segments)
                    {
                        if (segment == null) continue;
                        Select(segment.segmentParent, segmentParent: true);
                        Select(segment.segmentCampfire);
                    }
            }
            selectedGeneration++;
            foreach (var root in selectedRoots) root.SelectedGeneration = selectedGeneration;
            foreach (var root in orderedRoots)
            {
                root.WasActive = root.Active;
                root.Selected = root.SelectedGeneration == selectedGeneration;
                rootChecks++;
                root.Active = root.Selected && root.Source && root.Source.activeInHierarchy;
            }
            foreach (var envelope in orderedEnvelopes)
            {
                envelope.WasActive = envelope.Active;
                envelope.Selected = envelope.Active = false;
                foreach (var owner in envelope.SegmentOwners!)
                {
                    envelope.Selected |= owner.Selected;
                    envelope.Active |= owner.Active;
                }
                rootChecks++;
                envelope.Active = envelope.Active && envelope.Source && envelope.Source.activeInHierarchy;
            }
            directoryReliable = true;
            FlushDeferredOrigins();
            // A map can acquire its final roots after a source's first OnEnable.
            // Index ancestor handles at first binding, then reclassify only
            // candidates covered by a newly known native boundary. Admitted
            // candidates must be corrected before this frame's scene capture;
            // unadmitted candidates can use the small per-frame rebind budget.
            if (changedBoundaryIds.Count != 0) ScheduleBoundaryRebind();
            DrainDeferredRebind();
            foreach (var root in orderedRoots)
            {
                if (disposed) break;
                if (root.WasActive && !root.Active) RetireRoot(root);
                else if (!root.WasActive && root.Active) ActivateRoot(root);
            }
            foreach (var envelope in orderedEnvelopes)
            {
                if (disposed) break;
                if (envelope.WasActive && !envelope.Active) RetireRoot(envelope);
                else if (!envelope.WasActive && envelope.Active) ActivateRoot(envelope);
            }
        }
        catch (Exception error) { directoryReliable = false; Warn("root observation", error); }
        finally { observing = false; }
    }

    private void ReadRawRoots(MapHandler.MapSegment[]? segments)
    {
        if (segments == null) return;
        foreach (var segment in segments)
        {
            if (segment == null) continue;
            // These are raw serialized roots, including unselected variants.
            // Treating the latter as unknown would accidentally admit them.
            // Re-read this small root list: native initialization can fill a
            // serialized field on the same MapSegment after our first tick.
            AddSegmentRoot(rawParent(segment));
            AddRoot(rawCampfire(segment));
        }
    }

    private bool AddRoot(GameObject? source)
    {
        if (!source || roots.ContainsKey(source.GetInstanceID())) return false;
        var root = new Root(source);
        roots.Add(source.GetInstanceID(), root); orderedRoots.Add(root);
        // Preserve this boundary if a later resolved native getter fails in
        // the same Observe. Recovery must still reclassify existing unknowns.
        changedBoundaryIds.Add(source.GetInstanceID());
        return true;
    }

    private bool AddSegmentRoot(GameObject? source)
    {
        bool added = AddRoot(source);
        if (!source) return added;
        var owner = roots[source.GetInstanceID()];
        if (owner.EnvelopeExamined) return added;
        // The native map also owns authored siblings outside segmentParent:
        // e.g. Gloom's temple details. Its Biome component is the exact native
        // ownership boundary; no names, coordinates, or scene-wide scan are used.
        // Resolve it once when this serialized root enters the directory.
        var biome = source.GetComponentInParent<Biome>(includeInactive: true);
        owner.EnvelopeExamined = true;
        if (!biome || ReferenceEquals(biome.gameObject, source)) return added;
        int id = biome.gameObject.GetInstanceID();
        if (!envelopes.TryGetValue(id, out var envelope))
        {
            envelope = new Root(biome.gameObject, envelope: true);
            envelopes.Add(id, envelope); orderedEnvelopes.Add(envelope);
            changedBoundaryIds.Add(id);
        }
        envelope.SegmentOwners!.Add(owner);
        // Campfires remain independent exact roots and never activate the
        // surrounding biome after its last native segmentParent shuts down.
        return true;
    }

    private void Select(GameObject? source, bool segmentParent = false)
    {
        if (!source) return;
        if (segmentParent) AddSegmentRoot(source); else AddRoot(source);
        selectedRoots.Add(roots[source.GetInstanceID()]);
    }

    internal Root? FindRoot(GameObject source, SegmentCaptureClient.Entry? entry = null)
    {
        parentWalks++;
        if (entry != null) UnindexOrigin(entry);
        Root? envelope = null;
        for (var transform = source.transform; transform; transform = transform.parent)
        {
            int id = transform.gameObject.GetInstanceID();
            if (entry != null) IndexAncestor(entry, id);
            if (roots.TryGetValue(id, out var root)) return root;
            if (envelope == null) envelopes.TryGetValue(id, out envelope);
        }
        // A physical native biome owner takes the same precedence as an exact
        // segment root when a source has genuinely moved away from its spawner.
        envelope = NearestEnvelope(source, envelope);
        if (envelope != null) return envelope;
        if (originResolver != null)
        {
            GameObject? origin;
            try { origin = originResolver(source); }
            catch (Exception error) { Warn("native origin", error); return null; }
            if (origin && !ReferenceEquals(origin, source))
            {
                Root? originEnvelope = null;
                for (var transform = origin!.transform; transform; transform = transform.parent)
                {
                    int id = transform.gameObject.GetInstanceID();
                    if (entry != null) IndexAncestor(entry, id);
                    if (roots.TryGetValue(id, out var root)) return root;
                    if (originEnvelope == null) envelopes.TryGetValue(id, out originEnvelope);
                }
                if (originEnvelope != null) return NearestEnvelope(origin, originEnvelope);
            }
        }
        return null;
    }

    private void IndexAncestor(SegmentCaptureClient.Entry entry, int id)
    {
        if (!originDependents.TryGetValue(id, out var dependents)) originDependents.Add(id, dependents = new());
        if (dependents.Add(entry)) entry.OriginAncestors.Add(id);
    }

    private static Root? NearestEnvelope(GameObject source, Root? envelope)
    {
        if (envelope == null) return null;
        // A mod may insert an unrelated nested Biome. Only the nearest native
        // marker may own these siblings; an unresolved marker stays unknown.
        var biome = source.GetComponentInParent<Biome>(includeInactive: true);
        return biome && ReferenceEquals(biome.gameObject, envelope.Source) ? envelope : null;
    }

    internal void UnindexOrigin(SegmentCaptureClient.Entry entry)
    {
        foreach (int id in entry.OriginAncestors)
            if (originDependents.TryGetValue(id, out var dependents))
            { dependents.Remove(entry); if (dependents.Count == 0) { originDependents.Remove(id); deferredOrigins.Remove(id); } }
        entry.OriginAncestors.Clear();
    }

    // Native Instantiate/Awake may precede the spawner's returned PhotonView
    // list. Reclassify only this source and its indexed descendants, once at
    // that exact event, rather than walking every unknown object each frame.
    internal void RefreshOrigin(GameObject source)
    {
        if (disposed || !source || originResolver == null || !originDependents.TryGetValue(source.GetInstanceID(), out var dependents)) return;
        if (!directoryReliable) { deferredOrigins[source.GetInstanceID()] = source; return; }
        var snapshot = new SegmentCaptureClient.Entry[dependents.Count]; dependents.CopyTo(snapshot);
        foreach (var entry in snapshot)
        {
            if (!entry.Client.Current(entry)) continue;
            if (!entry.Source) { entry.Client.Destroyed(entry); continue; }
            var owner = FindRoot(entry.Source, entry);
            entry.Client.Bind(entry, owner);
            CancelRebind(entry);
            if (entry.Admitted && !Allowed(owner))
            {
                entry.Admitted = false; retirements++;
                entry.Client.Retire(entry.Id, originRejection: true);
            }
            else if (!entry.Admitted && Allowed(owner)) entry.Client.Queue(entry);
            if (!Allowed(owner)) entry.Client.ClearFailure(entry);
        }
    }

    private void FlushDeferredOrigins()
    {
        if (deferredOrigins.Count == 0) return;
        var sources = new GameObject[deferredOrigins.Count]; deferredOrigins.Values.CopyTo(sources, 0); deferredOrigins.Clear();
        foreach (var source in sources) RefreshOrigin(source);
    }

    private static double MonotonicSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    internal double RetryTime => retryClock();

    internal static bool Allowed(Root? root) => root == null ||
        (root.Selected && root.Active && root.Source && root.Source.activeInHierarchy);

    private void ScheduleBoundaryRebind()
    {
        var affected = new HashSet<SegmentCaptureClient.Entry>();
        foreach (int id in changedBoundaryIds)
            if (originDependents.TryGetValue(id, out var dependents)) affected.UnionWith(dependents);
        // Keep the boundary IDs until all callbacks have succeeded. A failed
        // observation may retry this exact set on the next coherent tick.
        foreach (var entry in affected)
        {
            if (!entry.Client.Current(entry)) continue;
            MarkRebind(entry);
            if (!entry.Admitted) QueueRebind(entry);
        }
        // Mark every affected source before invoking any retire callback: a
        // callback may reenter Admit for another newly excluded source.
        foreach (var entry in affected)
            if (entry.Client.Current(entry) && entry.Admitted && entry.RebindPending) RebindEntry(entry);
        // A retire callback can change another candidate's admission state.
        foreach (var entry in affected)
            if (entry.Client.Current(entry) && entry.RebindPending && !entry.Admitted) QueueRebind(entry);
        changedBoundaryIds.Clear();
    }

    private void MarkRebind(SegmentCaptureClient.Entry entry)
    {
        if (entry.RebindPending || !entry.Client.Current(entry)) return;
        entry.RebindPending = true;
        pendingRebind++;
    }

    private void QueueRebind(SegmentCaptureClient.Entry entry)
    {
        MarkRebind(entry);
        if (!entry.RebindPending || entry.RebindQueued) return;
        entry.RebindQueued = true;
        deferredRebind.Enqueue(entry);
    }

    internal void CancelRebind(SegmentCaptureClient.Entry entry)
    {
        if (!entry.RebindPending) return;
        entry.RebindPending = false;
        pendingRebind--;
    }

    internal bool NeedsRebind(SegmentCaptureClient.Entry entry) => entry.RebindPending;

    internal void RebindEntry(SegmentCaptureClient.Entry entry)
    {
        var client = entry.Client;
        if (!client.Current(entry)) { CancelRebind(entry); return; }
        if (!entry.Source) { client.Destroyed(entry); return; }
        var owner = FindRoot(entry.Source, entry);
        client.Bind(entry, owner);
        CancelRebind(entry);
        rebinds++;
        // A late future/variant boundary invalidates earlier conservative
        // evidence. Never run a preserve hint for this origin rejection.
        if (entry.Admitted && !Allowed(owner))
        {
            entry.Admitted = false; retirements++;
            client.Retire(entry.Id, originRejection: true);
        }
        else if (!entry.Admitted)
        {
            if (Allowed(owner)) client.Queue(entry);
            else client.ClearFailure(entry);
        }
    }

    private void DrainDeferredRebind()
    {
        lastRebindBatch = 0;
        long started = Stopwatch.GetTimestamp();
        int visited = 0;
        while (deferredRebind.Count != 0 && visited < 64)
        {
            if (visited != 0 && (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency >= .35) break;
            var entry = deferredRebind.Dequeue();
            visited++;
            entry.RebindQueued = false;
            if (!entry.RebindPending) continue;
            RebindEntry(entry);
            lastRebindBatch++;
        }
        if (lastRebindBatch > maximumRebindBatch) maximumRebindBatch = lastRebindBatch;
    }

    private void ActivateRoot(Root root)
    {
        // This is a native activation boundary, not a per-tick candidate walk.
        // Only lightweight queue entries are created here. Heavy baselines are
        // constructed by the fair, bounded Drain below.
        var members = new SegmentCaptureClient.Entry[root.Members.Count]; root.Members.CopyTo(members);
        foreach (var entry in members)
        {
            if (!entry.Client.Current(entry)) continue;
            if (!entry.Source) { entry.Client.Destroyed(entry); continue; }
            if (!entry.Admitted) entry.Client.Queue(entry);
        }
    }

    private void RetireRoot(Root root)
    {
        var members = new SegmentCaptureClient.Entry[root.Members.Count]; root.Members.CopyTo(members);
        // A carried item's preserve callback can synchronously detach its
        // child FX/audio bindings. Resolve all moves and preserve callbacks
        // before ending any run, irrespective of hash-set/client order.
        foreach (var entry in members)
        {
            if (!entry.Client.Current(entry) || !ReferenceEquals(entry.Owner, root)) continue;
            if (!entry.Source) { entry.Client.Destroyed(entry); continue; }
            if (entry.Admitted) PreserveEntry(entry, allowPreserve: true);
        }
        foreach (var entry in members)
        {
            if (!entry.Client.Current(entry)) continue;
            // The first pass may have moved this entry while preserving
            // another source. It no longer belongs to the retiring root.
            if (Allowed(entry.Owner)) continue;
            if (!entry.Source) { entry.Client.Destroyed(entry); continue; }
            if (!entry.Admitted && !ReferenceEquals(entry.Owner, root)) continue;
            if (!entry.Admitted && entry.Client.HasFailure(entry))
            {
                var actual = FindRoot(entry.Source, entry); entry.Client.Bind(entry, actual);
                if (!Allowed(actual)) entry.Client.ClearFailure(entry);
            }
            if (entry.Admitted) FinishRetirement(entry, originRejection: false);
        }
    }

    internal void RetireEntry(SegmentCaptureClient.Entry entry, bool allowPreserve, bool originRejection = false)
    {
        if (PreserveEntry(entry, allowPreserve)) return;
        FinishRetirement(entry, originRejection);
    }

    // True means this entry was preserved, removed, or already inactive.
    // False leaves one current admitted binding ready for actual retirement.
    private bool PreserveEntry(SegmentCaptureClient.Entry entry, bool allowPreserve)
    {
        var client = entry.Client;
        if (!client.Current(entry) || !entry.Admitted) return true;
        var old = entry.Owner;
        var actual = FindRoot(entry.Source, entry);
        // A carried/detached object can now belong to a different active root or
        // no known root. Keep its existing binding without rebuilding it.
        if (!ReferenceEquals(actual, old) && Allowed(actual))
        { client.Bind(entry, actual); preserved++; return true; }
        bool keep = false;
        // A preserve hint cannot admit an unselected variant. It only applies
        // to a binding that existed before an actual selected-root shutdown.
        if (allowPreserve && old?.Selected == true && ReferenceEquals(actual, old))
        {
            try { keep = client.Preserve?.Invoke(entry.Source) == true; }
            catch (Exception error) { Warn("preserve callback", error); }
        }
        if (!client.Current(entry) || !entry.Admitted) return true;
        if (keep && old?.IsEnvelope == true)
        {
            // A carried-item callback may detach itself and its child bindings.
            // Mere FX/audio/rope liveness cannot detach authored biome siblings.
            actual = FindRoot(entry.Source, entry);
            if (!ReferenceEquals(actual, old) && Allowed(actual))
            { client.Bind(entry, actual); preserved++; return true; }
            keep = false;
        }
        if (keep && originResolver != null)
        {
            // Item preservation may synchronously detach the source and all
            // child bindings. Recheck after that callback: a still-active
            // room object with a live spawner origin remains biome-owned.
            // Generic FX/audio/rope liveness cannot make it global.
            try { keep = !originResolver(entry.Source); }
            catch (Exception error) { keep = false; Warn("preserve origin", error); }
        }
        if (keep) { client.Bind(entry, null); preserved++; return true; }
        client.Bind(entry, actual);
        return false;
    }

    private void FinishRetirement(SegmentCaptureClient.Entry entry, bool originRejection)
    {
        var client = entry.Client;
        if (!client.Current(entry) || !entry.Admitted) return;
        entry.Admitted = false;
        retirements++;
        client.Retire(entry.Id, originRejection);
        if (client.Current(entry) && Allowed(entry.Owner)) client.Queue(entry);
    }

    // Four admissions are shared across all tracks. A client with thousands of
    // trap candidates cannot starve an environment, crate, or audio baseline.
    // A single callback cannot be preempted; the time limit is checked between
    // callbacks, so a slow baseline ends the batch after that one callback.
    internal void Drain(double budgetMilliseconds = 1, int maxAdmissions = 4)
    {
        if (double.IsNaN(budgetMilliseconds) || double.IsInfinity(budgetMilliseconds) || budgetMilliseconds <= 0 || budgetMilliseconds > 10 || maxAdmissions < 1 || maxAdmissions > 128)
            throw new ArgumentOutOfRangeException(nameof(budgetMilliseconds));
        if (disposed || draining || !directoryReliable) return;
        draining = true;
        drainCalls++; lastDrainAdmissions = 0;
        long started = Stopwatch.GetTimestamp();
        int emptyVisits = 0, checkedEntries = 0;
        try
        {
            while (!disposed && directoryReliable && clients.Count != 0 && lastDrainAdmissions < maxAdmissions && checkedEntries < Math.Max(64, maxAdmissions))
            {
                if (!CaptureBootstrapWorkBudget.TryBeginRead()) break;
                if (checkedEntries != 0 && (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency >= budgetMilliseconds) break;
                if (nextClient >= clients.Count) nextClient = 0;
                var client = clients[nextClient++];
                var entry = client.Take(RetryTime);
                if (entry == null)
                {
                    if (++emptyVisits >= clients.Count) break;
                    continue;
                }
                emptyVisits = 0; checkedEntries++;
                if (!client.Current(entry) || entry.Admitted) continue;
                if (client.RetryWaiting(entry, RetryTime)) continue;
                if (!entry.Source) { client.Destroyed(entry); continue; }
                client.Bind(entry, FindRoot(entry.Source, entry));
                CancelRebind(entry);
                if (!Allowed(entry.Owner)) { client.ClearFailure(entry); continue; }
                client.BeginAttempt(entry);
                entry.Admitted = true;
                lastDrainAdmissions++; baselineAdmissions++;
                try { client.AdmitCallback(entry.Source); if (client.Current(entry)) client.ClearFailure(entry); }
                catch (Exception error)
                {
                    // A callback can reenter Forget/Dispose. Never resurrect its
                    // source after that explicit lifecycle boundary. On failure
                    // retain only the candidate and one delayed retry. An empty
                    // callback queue cannot falsely complete initial capture.
                    if (client.Current(entry))
                    {
                        entry.Admitted = false; client.Retire(entry.Id);
                        if (client.Current(entry) && Allowed(entry.Owner)) client.Failed(entry, RetryTime);
                    }
                    Warn("baseline callback", error);
                }
            }
        }
        finally
        {
            if (lastDrainAdmissions > maximumDrainAdmissions) maximumDrainAdmissions = lastDrainAdmissions;
            draining = false;
        }
    }

    internal void UrgentAdmission(bool globalOnly = false) { urgentAdmissions++; if (globalOnly) globalAdmissions++; }
    internal int PendingCount { get { int count = 0; foreach (var client in clients) count += client.PendingCount; return count; } }
    internal int FailedBaselineCount { get { int count = 0; foreach (var client in clients) count += client.FailedBaselineCount; return count; } }
    internal bool HasPending => !directoryReliable || deferredOrigins.Count != 0 || pendingRebind != 0 || PendingCount != 0;
    internal bool DirectoryReliable => directoryReliable;
    internal void Warn(string operation, Exception error)
    {
        callbackFailures++;
        if (warnings++ >= 8) return;
        try { warning?.Invoke("Segment capture " + operation + " failed: " + error.GetType().Name + ": " + error.Message); }
        catch { /* Diagnostics must never interrupt the native game. */ }
    }

    internal void Remove(SegmentCaptureClient client)
    {
        int index = clients.IndexOf(client);
        if (index < 0) return;
        clients.RemoveAt(index);
        if (index < nextClient) nextClient--;
    }

    internal object Diagnostics
    {
        get
        {
            int selected = 0, active = 0, selectedEnvelopes = 0, activeEnvelopes = 0, candidates = 0, admitted = 0, pending = 0;
            foreach (var root in orderedRoots) { if (root.Selected) selected++; if (root.Active) active++; }
            foreach (var envelope in orderedEnvelopes) { if (envelope.Selected) selectedEnvelopes++; if (envelope.Active) activeEnvelopes++; }
            foreach (var client in clients) client.Count(ref candidates, ref admitted, ref pending);
            return new { DirectoryReliable = directoryReliable, Roots = orderedRoots.Count, Envelopes = orderedEnvelopes.Count,
                SelectedRoots = selected, ActiveRoots = active, SelectedEnvelopes = selectedEnvelopes, ActiveEnvelopes = activeEnvelopes, Clients = clients.Count,
                Candidates = candidates, Admitted = admitted, Pending = pending, BaselineAdmissions = baselineAdmissions,
                UrgentAdmissions = urgentAdmissions, GlobalAdmissions = globalAdmissions, Retirements = retirements, Preserved = preserved,
                ParentWalks = parentWalks, RootChecks = rootChecks, CallbackFailures = callbackFailures,
                FailedBaselines = FailedBaselineCount, DeferredOrigins = deferredOrigins.Count,
                PendingRebind = pendingRebind, Rebinds = rebinds,
                LastRebindBatch = lastRebindBatch, MaximumRebindBatch = maximumRebindBatch,
                DrainCalls = drainCalls, LastDrainAdmissions = lastDrainAdmissions, MaximumDrainAdmissions = maximumDrainAdmissions };
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ReferenceEquals(Active, this)) Active = null;
        foreach (var client in clients.ToArray()) client.Dispose();
        clients.Clear(); roots.Clear(); orderedRoots.Clear(); envelopes.Clear(); orderedEnvelopes.Clear(); selectedRoots.Clear(); originDependents.Clear(); deferredOrigins.Clear();
        changedBoundaryIds.Clear(); deferredRebind.Clear(); pendingRebind = 0;
    }
}

internal sealed class SegmentCaptureClient : IDisposable
{
    internal sealed class Entry
    {
        internal readonly SegmentCaptureClient Client;
        internal readonly GameObject Source;
        internal readonly int Id;
        internal CaptureSegmentScope.Root? Owner;
        internal bool Admitted, Queued;
        internal bool RebindPending, RebindQueued;
        internal int RetryIndex = -1, FailureAttempts;
        internal double NextRetry;
        internal readonly List<int> OriginAncestors = new();
        internal Entry(SegmentCaptureClient client, GameObject source)
        { Client = client; Source = source; Id = source.GetInstanceID(); }
    }
    private readonly CaptureSegmentScope scope;
    private readonly Dictionary<int, Entry> entries = new();
    private readonly HashSet<Entry> unknown = new();
    private readonly Queue<Entry> pending = new();
    private readonly HashSet<Entry> failures = new();
    private readonly List<Entry> retries = new();
    private int queuedCount;
    private bool disposed;
    internal readonly Action<GameObject> AdmitCallback;
    private readonly Action<int> retireCallback;
    internal readonly Func<GameObject, bool>? Preserve;
    internal bool OriginRejection { get; private set; }
    internal int PendingCount => queuedCount + failures.Count;
    internal int FailedBaselineCount => failures.Count;
    internal bool HasFailure(Entry entry) => failures.Contains(entry);

    internal SegmentCaptureClient(CaptureSegmentScope scope, Action<GameObject> admitCallback,
        Action<int> retireCallback, Func<GameObject, bool>? preserve)
    { this.scope = scope; AdmitCallback = admitCallback; this.retireCallback = retireCallback; Preserve = preserve; }

    internal bool Admit(GameObject source, bool urgent = false, bool urgentUnknown = false)
    {
        if (disposed || !source) return false;
        int id = source.GetInstanceID();
        if (entries.TryGetValue(id, out var previous) && !ReferenceEquals(previous.Source, source))
        {
            // Unity may reuse an instance ID after a native destruction. A new
            // source must never inherit the old object's admitted root binding.
            Forget(id); Retire(id);
            if (disposed || !source) return false;
        }
        if (!entries.TryGetValue(id, out var entry))
        {
            entry = new Entry(this, source); entries.Add(id, entry);
            Bind(entry, scope.FindRoot(source, entry));
        }
        // A newly registered native root can invalidate an earlier unknown,
        // envelope, or outer-root binding. Resolve this one source at its next
        // actual event before permitting any further capture from it.
        if (scope.DirectoryReliable && scope.NeedsRebind(entry)) scope.RebindEntry(entry);
        if (!Current(entry) || !source) return false;
        if (entry.Admitted)
        {
            // A failed native variant getter cannot establish a coherent new
            // directory. Retain an existing binding until Observe recovers;
            // reject all new admissions below without erasing prior history.
            if (scope.DirectoryReliable && !CaptureSegmentScope.Allowed(entry.Owner)) scope.RetireEntry(entry, allowPreserve: true);
            return Current(entry) && entry.Admitted;
        }
        if (!scope.DirectoryReliable) { Queue(entry); return false; }
        // A true native pickup/birth/action can move a queued candidate before
        // its first baseline. Refresh that source once at the event boundary;
        // an inactive old owner must not permanently veto a now-carried object.
        // Already admitted preserve bindings above deliberately keep their
        // independent ownership and do not incur this parent walk each event.
        if (urgent || urgentUnknown) Bind(entry, scope.FindRoot(source, entry));
        if (!CaptureSegmentScope.Allowed(entry.Owner)) return false;
        // Explicit global metadata (currently the OrbFog transition controller)
        // may need its native baseline before the first scene sample. This
        // exception never bypasses the queue or exclusion of any known root.
        if (urgent || (urgentUnknown && entry.Owner == null))
        { ClearFailure(entry); CancelQueue(entry); entry.Admitted = true; scope.UrgentAdmission(globalOnly: !urgent); return true; }
        Queue(entry);
        return false;
    }

    internal bool Current(Entry entry) => !disposed && entries.TryGetValue(entry.Id, out var current) && ReferenceEquals(current, entry);
    internal void Bind(Entry entry, CaptureSegmentScope.Root? root)
    {
        entry.Owner?.Members.Remove(entry); unknown.Remove(entry);
        entry.Owner = root;
        if (root != null) root.Members.Add(entry); else unknown.Add(entry);
    }
    internal void Queue(Entry entry)
    {
        if (!Current(entry) || entry.Queued || entry.Admitted || failures.Contains(entry)) return;
        entry.Queued = true; queuedCount++; pending.Enqueue(entry);
    }
    internal Entry? Take(double now)
    {
        if (pending.Count != 0)
        { var queued = pending.Dequeue(); CancelQueue(queued); return queued; }
        if (retries.Count == 0 || retries[0].NextRetry > now) return null;
        var entry = retries[0]; RemoveRetry(entry); return entry;
    }
    private void CancelQueue(Entry entry)
    { if (entry.Queued) { entry.Queued = false; queuedCount--; } }
    internal void Failed(Entry entry, double now)
    {
        if (!Current(entry) || entry.Admitted) return;
        CancelQueue(entry);
        failures.Add(entry); RemoveRetry(entry);
        entry.FailureAttempts = Math.Min(5, entry.FailureAttempts + 1);
        entry.NextRetry = now + Math.Min(1, .1 * (1 << (entry.FailureAttempts - 1)));
        entry.RetryIndex = retries.Count; retries.Add(entry); SiftUp(entry.RetryIndex);
    }
    internal void ClearFailure(Entry entry)
    { failures.Remove(entry); RemoveRetry(entry); entry.FailureAttempts = 0; }
    internal bool RetryWaiting(Entry entry, double now) => failures.Contains(entry) && entry.NextRetry > now;
    internal void BeginAttempt(Entry entry) => RemoveRetry(entry);
    private void RemoveRetry(Entry entry)
    {
        int index = entry.RetryIndex;
        if (index < 0) return;
        int last = retries.Count - 1;
        if (index != last) { retries[index] = retries[last]; retries[index].RetryIndex = index; }
        retries.RemoveAt(last); entry.RetryIndex = -1;
        if (index < retries.Count) { if (index > 0 && retries[index].NextRetry < retries[(index - 1) / 2].NextRetry) SiftUp(index); else SiftDown(index); }
    }
    private void SiftUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (retries[parent].NextRetry <= retries[index].NextRetry) break;
            SwapRetry(parent, index); index = parent;
        }
    }
    private void SiftDown(int index)
    {
        while (index * 2 + 1 < retries.Count)
        {
            int child = index * 2 + 1;
            if (child + 1 < retries.Count && retries[child + 1].NextRetry < retries[child].NextRetry) child++;
            if (retries[index].NextRetry <= retries[child].NextRetry) break;
            SwapRetry(index, child); index = child;
        }
    }
    private void SwapRetry(int a, int b)
    { var value = retries[a]; retries[a] = retries[b]; retries[b] = value; retries[a].RetryIndex = a; retries[b].RetryIndex = b; }
    internal void Retire(int id, bool originRejection = false)
    {
        bool previousRejection = OriginRejection;
        OriginRejection = originRejection;
        try { retireCallback(id); }
        catch (Exception error) { scope.Warn("retire callback", error); }
        finally { OriginRejection = previousRejection; }
    }
    internal void Destroyed(Entry entry)
    {
        if (!Current(entry)) return;
        Forget(entry.Id); Retire(entry.Id);
    }
    internal void Forget(int id)
    {
        if (!entries.TryGetValue(id, out var entry)) return;
        ClearFailure(entry); CancelQueue(entry);
        entries.Remove(id); entry.Owner?.Members.Remove(entry); unknown.Remove(entry); scope.UnindexOrigin(entry); scope.CancelRebind(entry);
        entry.Admitted = false;
        // A stale queued entry is cheap and is rejected by Current in Drain.
    }
    internal void Count(ref int candidates, ref int admitted, ref int queued)
    {
        candidates += entries.Count; queued += failures.Count;
        foreach (var entry in entries.Values) { if (entry.Admitted) admitted++; if (entry.Queued) queued++; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var entry in entries.Values)
        { entry.Owner?.Members.Remove(entry); scope.UnindexOrigin(entry); scope.CancelRebind(entry); ClearFailure(entry); CancelQueue(entry); }
        entries.Clear(); unknown.Clear(); pending.Clear(); failures.Clear(); retries.Clear(); queuedCount = 0; scope.Remove(this);
    }
}
