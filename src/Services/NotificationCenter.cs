namespace Clippy;

/// <summary>
/// One notification pipeline for both platforms.
///
/// Every announcement the program makes -- a saved clip, an update check, the progress of a
/// download -- is built HERE as a <see cref="NotificationPayload"/> and handed to whichever
/// <see cref="INotificationPresenter"/> the running platform installed. Windows and Linux stopped
/// drifting the moment the texts, the timings and the routing rules stopped living in two files.
///
/// The presenter decides WHERE a payload lands (the game overlay card, the system toast, or
/// nowhere); this class decides WHAT is said and never touches a window itself, so a headless run
/// (--self-update from a terminal) degrades to silence without a null check at every call site.
/// </summary>
internal static class NotificationCenter
{
    /// <summary>Installed by the platform's run path; null (a no-op) until then.</summary>
    private static INotificationPresenter? presenter;

    /// <summary>
    /// Installs the platform's presenter. Called once per recorder run; a second call replaces the
    /// first (a restart inside one process life, which only the tests do).
    /// </summary>
    public static void SetPresenter(INotificationPresenter? value) => presenter = value;

    private static void Dispatch(NotificationPayload payload) => presenter?.ShowOrUpdate(payload);

    // --- The shared accent for the progress bar, the one colour both cards draw themselves ---
    // The green the Linux card's check badge used to carry; with the badge gone the accent lives
    // in the progress fill, where it reads as "this is moving" on both platforms alike.
    internal const byte AccentR = 34, AccentG = 197, AccentB = 94;          // #22C55E
    internal const byte TrackR = 61, TrackG = 61, TrackB = 61;              // #3D3D3D

    /// <summary>The progress strip's design metrics: 6 px tall pill, its own 16 px zone.</summary>
    internal const int BarZoneDesignPx = 16;
    internal const int BarHeightDesignPx = 6;
    internal const int BarGapDesignPx = 5;      // below the subtitle line
    internal const int BarBottomInsetDesignPx = 5;

    // --- The announcements, in the words both platforms share ---

    /// <summary>
    /// The clip card. The SOUND is not part of this: it is platform-native (winmm on Windows,
    /// pw-play on Linux) and stays at the export site, which already knows the config. The text is
    /// what the player wants -- length and disk cost -- not a path.
    /// </summary>
    public static void ClipSaved(double clipSeconds, double megabytes)
    {
        // Rounded, not truncated: a 30 s clip that the encoder closed at 29.96 s is 30 s to the
        // player, and "0:29" reads as a clip that came up a second short -- i.e. as a recorder
        // bug, not as a GOP boundary. AwayFromZero so .5 always goes up.
        var total = (int)Math.Round(clipSeconds, MidpointRounding.AwayFromZero);
        Dispatch(new NotificationPayload
        {
            Kind = NotificationKind.ClipSaved,
            Title = "Clip Saved",
            Subtitle = $"{ClipStamp(total)} · {megabytes:F1} MB",
        });
    }

    /// <summary>m:ss -- 6:05, not 365 s; zero-padded seconds so widths stay stable.</summary>
    internal static string ClipStamp(int totalSeconds) => $"{totalSeconds / 60}:{totalSeconds % 60:D2}";

    public static void CheckingForUpdates() => Dispatch(new NotificationPayload
    {
        Kind = NotificationKind.Checking,
        Title = "Checking for Updates",
        Subtitle = "Contacting GitHub...",
        Sticky = true,
        ForceVisual = true,
    });

    public static void UpToDate(string currentVersion) => Dispatch(new NotificationPayload
    {
        Kind = NotificationKind.UpToDate,
        Title = "Up to Date",
        Subtitle = $"Clippy {currentVersion} is the latest version",
        ForceVisual = true,
    });

    /// <summary>
    /// One download step. Called once per buffer chunk; a 90 ms throttle keeps the card (or the
    /// toast) from repainting per 64 KB, and the final 100% never needs to squeeze through -- the
    /// Installing step that always follows draws the full bar itself.
    /// </summary>
    public static void UpdateDownloading(string version, long bytesRead, long? totalBytes)
    {
        var now = Environment.TickCount64;
        if (now - lastProgressTicks < 90)
            return;
        lastProgressTicks = now;

        var mbRead = bytesRead / 1048576.0;
        if (totalBytes is var total && total > 0)
        {
            // Determinate: the card grows its bar, the fill tracks the real fraction.
            var progress = Math.Clamp(bytesRead / (double)total, 0, 1);
            Dispatch(new NotificationPayload
            {
                Kind = NotificationKind.UpdateProgress,
                Title = $"Downloading {version}...",
                Subtitle = $"{mbRead:F1} / {total / 1048576.0:F1} MB · {(int)Math.Round(progress * 100)}%",
                Progress = progress,
                Sticky = true,
                ForceVisual = true,
            });
        }
        else
        {
            // The server sent no length (chunked): the bar is shown but sweeps, because an
            // honest unknown beats a lying percentage.
            Dispatch(new NotificationPayload
            {
                Kind = NotificationKind.UpdateProgress,
                Title = $"Downloading {version}...",
                Subtitle = $"{mbRead:F1} MB downloaded...",
                Indeterminate = true,
                Sticky = true,
                ForceVisual = true,
            });
        }
    }

    private static long lastProgressTicks;

    public static void UpdateInstalling(string version, string stageDetail) => Dispatch(new NotificationPayload
    {
        Kind = NotificationKind.UpdateProgress,
        Title = $"Installing {version}...",
        Subtitle = stageDetail,
        Progress = 1.0,
        Sticky = true,
        ForceVisual = true,
    });

    /// <summary>
    /// The one card a human is meant to READ, not just glimpse: the hold is stretched past the
    /// usual 2.6 s because the summary under the title is the only report of what changed.
    /// </summary>
    public static void UpdateCompleted(string version, string changeSummary) => Dispatch(new NotificationPayload
    {
        Kind = NotificationKind.UpdateSuccess,
        Title = $"Updated to {version}",
        Subtitle = changeSummary,
        ForceVisual = true,
        HoldMs = 4500,
    });

    public static void UpdateFailed(string error) => Dispatch(new NotificationPayload
    {
        Kind = NotificationKind.UpdateError,
        Title = "Update Failed",
        Subtitle = error,
        ForceVisual = true,
    });

    /// <summary>
    /// The --updated announcement, from the one-shot marker the apply wrote before restarting: WHAT
    /// changed in the system plus the release's own summary line. The marker is deleted on read, so
    /// a later plain restart does not re-announce an old update; with no marker (an update from a
    /// build older than the marker's introduction, or a wiped logs dir) the card still shows the
    /// version, just with the generic text.
    /// </summary>
    public static void UpdateCompletedFromMarker(string fallbackVersion)
    {
        if (UpdateService.ReadUpdateSummaryMarker() is { } marker)
        {
            var subtitle = marker.WhatChanged;
            if (marker.ReleaseNotes.Length > 0)
                subtitle = subtitle.Length > 0 ? $"{subtitle} — {marker.ReleaseNotes}" : marker.ReleaseNotes;
            UpdateCompleted(marker.Version, subtitle);
        }
        else
        {
            UpdateCompleted(fallbackVersion, "Recording continues as before.");
        }
    }

    /// <summary>Ordinary information (an update is available; a setting changed). Not forced.</summary>
    public static void Info(string title, string subtitle) => Dispatch(new NotificationPayload
    {
        Kind = NotificationKind.Info,
        Title = title,
        Subtitle = subtitle,
    });

    /// <summary>The live preview the tray menu fires when a notification mode is picked.</summary>
    public static void NotificationModePreview(string label) => Dispatch(new NotificationPayload
    {
        Kind = NotificationKind.Info,
        Title = "Notifications",
        Subtitle = label,
        ForceVisual = true,
    });

    public static void AlreadyRunning() => Dispatch(new NotificationPayload
    {
        Kind = NotificationKind.Info,
        Title = "Clippy",
        Subtitle = "Clippy is already running in the system tray.",
        ForceVisual = true,
    });
}

/// <summary>What kind of thing is being announced; the presenter may style by it.</summary>
internal enum NotificationKind
{
    ClipSaved,
    Checking,
    UpToDate,
    UpdateProgress,
    UpdateSuccess,
    UpdateError,
    Info,
}

/// <summary>
/// One notification, fully described. Everything the card needs to draw itself, and everything
/// the presenter needs to route it, travels together -- so a card already on screen can be
/// re-drawn in place from a newer payload with no flicker and no fade restart.
/// </summary>
internal sealed record NotificationPayload
{
    public NotificationKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";

    /// <summary>
    /// 0.0..1.0 for a determinate fill; null means "no bar". <see cref="Indeterminate"/> is the
    /// third state: a bar is drawn, but it sweeps instead of filling (the server sent no length).
    /// </summary>
    public double? Progress { get; init; }

    public bool Indeterminate { get; init; }

    /// <summary>
    /// True while an operation is in flight (checking, downloading, installing): the card does not
    /// start its close timer and lives until a non-sticky payload replaces it.
    /// </summary>
    public bool Sticky { get; init; }

    /// <summary>Show visually even in Sound Only mode (a manual update check must be seen).</summary>
    public bool ForceVisual { get; init; }

    /// <summary>How long the card holds before fading out; the completed card stretches this.</summary>
    public int HoldMs { get; init; } = 2600;
}

/// <summary>
/// The platform's notification surface: the game overlay card where it can come up, the system
/// toast where it cannot. <see cref="ShowOrUpdate"/> is the contract that makes live progress
/// possible: an update to a card already on screen replaces its content in place, and only the
/// payload decides whether the close timer runs. Dispose tears the surfaces down with the run
/// that installed the presenter.
/// </summary>
internal interface INotificationPresenter : IDisposable
{
    void ShowOrUpdate(NotificationPayload payload);
}
