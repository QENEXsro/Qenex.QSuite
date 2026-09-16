namespace Qenex.QSuite.Controls.Control;

/// <summary>
/// Marks a control that accumulates samples along a time axis (time series, X/Y pairing
/// history, persistence window) and would therefore show a stale state after a replay seek:
/// the replay driver re-sends the whole history up to the new position, and without a reset
/// the re-sent samples merge into, or are plotted next to, the samples of the old position.
/// The host (QInsight) calls <see cref="ResetTimeline"/> on the UI thread before the seek,
/// after discarding the samples still queued for the control; the control forgets its history
/// and time-axis origin and starts a fresh timeline with the next sample. Controls that show
/// only the current value (Signal, Gauge, WatchTable, Matrix) do not need it: the next sample
/// replaces their display and their refresh throttle already tolerates a backward timestamp.
/// </summary>
public interface ITimelineResetControl
{
    /// <summary>
    /// Forgets the accumulated samples and the time-axis origin. Called on the UI thread.
    /// </summary>
    void ResetTimeline();
}
