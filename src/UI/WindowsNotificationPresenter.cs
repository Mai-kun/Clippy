#if WINDOWS
namespace Clippy;

/// <summary>
/// The Windows side of the shared notification pipeline: decides where a payload lands, the
/// NotificationCenter decides what it says.
///
/// Routing follows the same rules on both platforms: "overlay" draws the game card (and falls back
/// to the tray balloon when its layered window could not be built), "windows_toast" draws the
/// balloon, "none" stays silent unless the payload insists (a manual update check must be seen)
/// and then prefers the card. The card is created lazily and lives for the presenter's lifetime --
/// one window, reused by every payload, which is what makes Checking → Downloading → Installing
/// render as one card updating in place instead of a stack of cards.
/// </summary>
internal sealed class WindowsNotificationPresenter : INotificationPresenter, IDisposable
{
    private readonly Func<ClippyConfig> config;
    private readonly Action<string, string> toast;
    private OverlayNotification? overlay;

    public WindowsNotificationPresenter(Func<ClippyConfig> config, Action<string, string> toast)
    {
        this.config = config;
        this.toast = toast;
    }

    public void ShowOrUpdate(NotificationPayload payload)
    {
        var mode = config().NotificationType;
        var isOverlay = string.Equals(mode, "overlay", StringComparison.OrdinalIgnoreCase);
        var isToast = string.Equals(mode, "windows_toast", StringComparison.OrdinalIgnoreCase);
        var isNone = string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase);

        if (isNone && !payload.ForceVisual)
            return; // Sound Only: nothing visual unless the payload insists.

        if (isOverlay || (isNone && payload.ForceVisual))
        {
            if (AcquireOverlay() is { } card && card.IsReady)
            {
                card.ShowOrUpdate(payload);
                return;
            }
            // The card could not come up (no layered window): the balloon takes over, so an
            // update progress is never announced by silence.
        }

        toast(payload.Title, payload.Subtitle);
    }

    private OverlayNotification? AcquireOverlay()
    {
        if (overlay is not null)
            return overlay;

        var candidate = new OverlayNotification();
        candidate.Start();
        if (!candidate.IsReady)
        {
            candidate.Dispose();
            return null;
        }

        overlay = candidate;
        return overlay;
    }

    public void Dispose()
    {
        overlay?.Dispose();
        overlay = null;
    }
}
#endif
