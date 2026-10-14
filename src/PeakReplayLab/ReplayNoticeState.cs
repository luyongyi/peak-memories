using System;

namespace PeakReplayLab;

// Time-limited notes and a failed recorder have different lifetimes. The latter
// comes from the recorder state, so loading screens and later notes cannot
// acknowledge it merely by hiding or replacing the ordinary toast.
internal sealed class ReplayNoticeState
{
    internal readonly struct Presentation
    {
        public readonly string Text;
        public readonly bool Error, HasStats, Persistent;
        public readonly float Alpha;
        public bool Visible => Text.Length != 0;
        public Presentation(string text, bool error, bool hasStats, bool persistent, float alpha)
        { Text = text; Error = error; HasStats = hasStats; Persistent = persistent; Alpha = alpha; }
    }
    private string message = "", cachedToast = "", cachedPersistent = "", cachedStats = "", cachedText = "";
    private double until;
    private bool error;

    public void Show(string text, double now, double seconds = 7, bool error = false)
    { message = text ?? ""; until = now + Math.Max(1, seconds); this.error = error; }
    public void Dismiss(string text)
    { if (message == text) { message = ""; until = 0; } }

    public Presentation Present(double now, string? persistent = null, string? stats = null)
    {
        string fixedText = persistent ?? "";
        string note = now < until ? message : "";
        if (note == fixedText) note = "";
        string diagnostics = string.IsNullOrWhiteSpace(stats) ? "" : stats!;
        if (note != cachedToast || fixedText != cachedPersistent || diagnostics != cachedStats)
        {
            cachedToast = note; cachedPersistent = fixedText; cachedStats = diagnostics;
            cachedText = fixedText;
            if (note.Length != 0) cachedText += (cachedText.Length == 0 ? "" : "\n") + note;
            if (diagnostics.Length != 0) cachedText += (cachedText.Length == 0 ? "" : "\n") + diagnostics;
        }
        bool permanent = fixedText.Length != 0, hasStats = diagnostics.Length != 0;
        float alpha = permanent || hasStats ? 1 : (float)Math.Max(0, Math.Min(1, (until - now) / .5));
        return new Presentation(cachedText, permanent || error && note.Length != 0, hasStats, permanent, alpha);
    }
}
