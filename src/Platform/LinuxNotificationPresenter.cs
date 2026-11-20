#if LINUX
using System.Diagnostics;

namespace Clippy;

/// <summary>
/// The Linux side of the shared notification pipeline: decides where a payload lands, the
/// NotificationCenter decides what it says.
///
/// Routing follows the same rules as the Windows presenter: "overlay" draws the game card (and
/// falls back to the desktop toast when X11 is unavailable), "windows_toast" is the desktop toast,
/// "none" stays silent unless the payload insists (a manual update check must be seen) and then
/// prefers the card. The card is created lazily and kept for the presenter's lifetime -- one
/// window, reused by every payload, which is what makes Checking → Downloading → Installing render
/// as one card updating in place instead of a stack of cards.
///
/// The toast is one long-lived D-Bus connection carrying one replaceable notification: every
/// ShowOrUpdate to the toast path re-issues org.freedesktop.Notifications.Notify with
/// replaces_id, so a download ticking at 11 Hz updates THE SAME desktop bubble instead of
/// stacking dozens. A new connection would get a new bus name, and the daemon scopes replace ids
/// per sender -- which is exactly why the connection is kept, not re-opened per call.
/// </summary>
internal sealed class LinuxNotificationPresenter : INotificationPresenter, IDisposable
{
    private readonly Func<ClippyConfig> config;
    private LinuxOverlayNotification? overlay;
    private LinuxDbus? toastBus;
    private uint toastNotifyId;

    public LinuxNotificationPresenter(Func<ClippyConfig> config) => this.config = config;

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
            if (AcquireOverlay() is { } card && card.IsReady && card.ShowOrUpdate(payload))
                return;
            // The card could not come up (no X11, no Xft): the desktop toast takes over, so an
            // update progress is never announced by silence.
        }

        SendToast(payload);
    }

    private LinuxOverlayNotification? AcquireOverlay()
    {
        if (overlay is not null)
            return overlay;

        var candidate = new LinuxOverlayNotification();
        candidate.Start();
        if (!candidate.IsReady)
        {
            candidate.Dispose();
            return null;
        }

        overlay = candidate;
        return overlay;
    }

    /// <summary>
    /// The desktop toast over this program's own D-Bus wire layer, on a connection of its own (the
    /// tray's connection serves its menu on a dedicated dispatch thread and must not share it).
    /// A failed call re-opens the connection and starts a fresh notification rather than retrying
    /// a replace against a daemon that has forgotten the old id.
    /// </summary>
    private void SendToast(NotificationPayload payload)
    {
        try
        {
            if (toastBus is null)
            {
                toastBus = LinuxDbus.Connect();
                toastBus.Hello();
                toastNotifyId = 0;
            }

            // Sticky updates stay parked for a minute (the daemon's own timer must not close the
            // bubble mid-download); everything else holds for about as long as the card does.
            var expire = payload.Sticky ? 60_000 : Math.Max(3500, payload.HoldMs);
            var reply = toastBus.Call("org.freedesktop.Notifications", "/org/freedesktop/Notifications",
                "org.freedesktop.Notifications", "Notify",
                "susssasa{sv}i",
                w =>
                {
                    w.String("Clippy");                        // app_name
                    w.UInt32(toastNotifyId);                   // replaces_id: THE same bubble
                    w.String("clippy");                        // app_icon: the hicolor PNG
                    w.String(payload.Title);                   // summary
                    w.String(payload.Subtitle);
                    w.WriteStringArray([]);                    // actions: none
                    w.WriteOptionsDict(new Dictionary<string, (string, Action<DbusWriter>)>
                    {
                        ["desktop-entry"] = ("s", x => x.String("clippy")),
                    });
                    w.Int32(expire);
                }, 5000);
            toastNotifyId = new DbusReader(reply.Body).ReadUInt32();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"Export: the notification daemon refused the D-Bus call ({ex.GetType().Name}: {ex.Message}).");
            // A dead connection would keep failing every replace: drop it, so the next payload
            // re-opens from scratch (and loses the stale replaces_id with it).
            try { toastBus?.Dispose(); } catch { /* best effort */ }
            toastBus = null;
            toastNotifyId = 0;
        }
    }

    public void Dispose()
    {
        overlay?.Dispose();
        overlay = null;
        try { toastBus?.Dispose(); } catch { /* best effort */ }
        toastBus = null;
    }
}
#endif
