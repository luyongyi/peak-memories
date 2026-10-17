using System;

namespace PeakReplayLab;

// Optional presentation-only ownership. The recorder/timeline never references
// implementations. A session owns the extension and releases it before its view.
internal interface IReplayPresentationExtension : IDisposable
{
    bool ApplyCamera(float realDelta);
    void CameraApplied();
}
