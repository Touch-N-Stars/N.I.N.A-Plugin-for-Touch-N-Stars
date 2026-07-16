namespace TouchNStars.Server.Infrastructure;

internal static class DataContainer {
    internal static object lockObj = new object();
    internal static bool afRun = false;
    // Set by AutofocusWatcher.AutoFocusRunStarting (mediator broadcast fired by both built-in NINA
    // AF and HocusFocus once a run has actually claimed its in-progress guard). Consumers reset it
    // before triggering an AF to detect whether the trigger was silently rejected.
    internal static bool afStartConfirmed = false;
    internal static bool afError = false;
    internal static string afErrorText = string.Empty;
    internal static bool newAfGraph = false;
}
