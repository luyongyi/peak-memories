using System;

namespace PeakReplayLab;

public sealed partial class Plugin
{
    // Optional source modules implement these hooks. Without an implementation
    // the compiler removes each call, including callback argument evaluation.
    partial void PresentationInput(ref bool ownsShortcuts);
    partial void ConfigurePresentationEntry(ref Action? openPresentation);
    partial void DisposePresentation();
    partial void RequestPresentationExit(Action continueExit, ref bool handled);
    partial void CancelPresentationPrompt(ref bool handled);
}
