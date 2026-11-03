using Avion2.Analysis;

namespace Avion2.Overlay;

/// <summary>Group-finder notices: the text, or null when that kind is switched off in Settings.</summary>
public static class FinderNotices
{
    public static string? Text(FinderAlert alert, OverlayViewModel settings)
    {
        bool on = alert.Kind switch
        {
            FinderAlertKind.JoinRequest => settings.NotifyJoinRequests,
            FinderAlertKind.FullAndReady => settings.NotifyRoomReady,
            FinderAlertKind.Starting => settings.NotifyDungeonStart,
            _ => false,
        };
        return on ? FinderNoticeText.Text(alert, name => StreamerMode.Name(name)) : null;
    }
}
