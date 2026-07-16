using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
using OxyPlot;
using TouchNStars.Server.Infrastructure;

namespace TouchNStars.Server.Services;

/// <summary>
/// Tracks autofocus run state directly from IFocuserMediator, independent of which
/// IAutoFocusVMFactory is active (built-in NINA autofocus or a plugin like HocusFocus).
/// This is the authoritative completion signal - the AF report file watched by
/// BackgroundWorker is only ever written by HocusFocus, so it cannot be relied upon alone.
/// </summary>
internal class AutofocusWatcher : IFocuserConsumer {
    private static AutofocusWatcher instance;

    public static void Start() {
        if (instance != null) return;
        instance = new AutofocusWatcher();
        TouchNStars.Mediators.Focuser.RegisterConsumer(instance);
    }

    public static void Stop() {
        if (instance == null) return;
        TouchNStars.Mediators.Focuser.RemoveConsumer(instance);
        instance = null;
    }

    public void AutoFocusRunStarting() {
        lock (DataContainer.lockObj) {
            DataContainer.afRun = true;
            DataContainer.afStartConfirmed = true;
        }
    }

    public void UpdateEndAutoFocusRun(AutoFocusInfo info) {
        DataContainer.afRun = false;
    }

    public void UpdateUserFocused(FocuserInfo info) {
    }

    public void NewAutoFocusPoint(DataPoint dataPoint) {
    }

    public void UpdateDeviceInfo(FocuserInfo deviceInfo) {
    }

    public void Dispose() {
    }
}
