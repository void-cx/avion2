using System.IO;
using System.Text.Json;

namespace Avion2.Overlay;

/// <summary>Window placement and preferences, stored in %AppData%\AVION2\overlay.json.</summary>
public sealed class OverlaySettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVION2", "overlay.json");

    public double Left { get; set; } = 80;
    public double Top { get; set; } = 160;
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 260;
    public double BackgroundOpacity { get; set; } = 0.78;
    public bool AllTargets { get; set; }
    public bool Locked { get; set; }

    public bool LowHpAlert { get; set; } = true;

    /// <summary>Start the meter as soon as AVION2 opens.</summary>
    public bool AutoStart { get; set; }

    /// <summary>Single-line overlay rows instead of player cards.</summary>
    public bool CompactRows { get; set; }

    /// <summary>Map tab: last map shown, whether it follows the map you are on, layers, collectibles found.</summary>
    public string? MapKey { get; set; }
    public bool MapFollow { get; set; } = true;
    public List<string>? MapLayers { get; set; }
    public List<string> MapHiddenSubtypes { get; set; } = [];
    public List<string>? MapMonsterRanks { get; set; }
    public int MapMinLevel { get; set; }
    public int MapMaxLevel { get; set; }
    public List<string> FoundCollectibles { get; set; } = [];
    /// <summary>Skipped hidden cubes (hub marker id → until when, UTC) and how long a skip lasts (0 = until un-skipped).</summary>
    public Dictionary<string, DateTime> SkippedCubes { get; set; } = [];
    public int CubeSkipMinutes { get; set; } = 30;
    /// <summary>
    /// "Find My Cube" (exact cube positions from game data, the compass, the cube log): off until the player opts in,
    /// because it can be seen as an unfair advantage.
    /// </summary>
    public bool CubeFinder { get; set; }

    /// <summary>
    /// HUD effects (beta): effects drawn on the game's own HUD, placed by a one-off calibration picture. Off until
    /// the player switches them on.
    /// </summary>
    public bool HudEffects { get; set; }

    /// <summary>Your HP bar pulses red while your HP is below the low-HP threshold.</summary>
    public bool HudHpBlink { get; set; } = true;

    /// <summary>Boss skills appear under the target frame (instead of a notice).</summary>
    public bool HudBossCasts { get; set; } = true;

    /// <summary>Calibrated HUD positions per game resolution ("2560x1440").</summary>
    public Dictionary<string, Avion2.Analysis.HudLayout> HudLayouts { get; set; } = [];

    public double? CompassLeft { get; set; }
    public double? CompassTop { get; set; }

    /// <summary>Server region for the rift/event timetable.</summary>
    public Avion2.Analysis.ServerRegion Region { get; set; } = Avion2.Analysis.ServerRegion.Europe;

    /// <summary>Look for a newer release on GitHub at start (one small request; dev builds never do).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>App theme id (<see cref="ThemeManager.Themes"/>): "aion2" or "void".</summary>
    public string Theme { get; set; } = "aion2";

    /// <summary>Language of the built-in skill/NPC/map names.</summary>
    public string DataLanguage { get; set; } = "en";

    /// <summary>Minutes before an event to notify.</summary>
    public int NotifyLeadMinutes { get; set; } = 5;

    public bool NotifyRift { get; set; } = true;
    public bool NotifyBossRaid { get; set; } = true;
    public bool NotifyFieldEvents { get; set; }
    public bool NotifyPvp { get; set; } = true;
    public bool NotifyReset { get; set; }
    public bool NotifyFieldBoss { get; set; } = true;
    public bool NotifyWorldBoss { get; set; } = true;

    /// <summary>Show the skill a boss starts casting (named skills only).</summary>
    public bool NotifyBossCasts { get; set; } = true;
    public bool NotifySound { get; set; } = true;

    /// <summary>Optional custom .wav/.mp3 for notifications; empty = built-in bell.</summary>
    public string? NoticeSound { get; set; }

    /// <summary>0..1, independent of the low-HP alert.</summary>
    public double NoticeVolume { get; set; } = 0.5;

    /// <summary>Position of the free-floating notification area; null = top centre.</summary>
    public double? NoticeLeft { get; set; }

    public double? NoticeTop { get; set; }

    /// <summary>Capture device index; null = automatic (all adapters, the game connection is detected).</summary>
    public int? Device { get; set; }

    /// <summary>Home-screen background video: null = %AppData%\AVION2\background.mp4 if present, "" = none.</summary>
    public string? BackgroundVideo { get; set; }

    /// <summary>Record each meter session to a .pcap (for troubleshooting).</summary>
    public bool RecordSessions { get; set; }

    /// <summary>Delete saved fights / recordings older than this many days when AVION2 starts (0 = keep).</summary>
    public int KeepFightsDays { get; set; }
    public int KeepRecordingsDays { get; set; }

    /// <summary>Fraction of max HP below which the alert fires (0.3 = 30%).</summary>
    public double LowHpThreshold { get; set; } = 0.3;

    /// <summary>0..1; scroll over the speaker icon to change it.</summary>
    public double LowHpVolume { get; set; } = 0.6;

    /// <summary>Optional custom .wav/.mp3; empty = built-in chime.</summary>
    public string? LowHpSound { get; set; }

    public static OverlaySettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(FilePath)) ?? new OverlaySettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // fall back to defaults
        }

        return new OverlaySettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not critical
        }
    }
}
