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

    /// <summary>
    /// When the DPS overlay shows by itself while capturing: "start" (with the capture), "connection" (when the game
    /// connection is found, hidden again when it is lost), "fight" (from a fight's start until 30 s after it ends) or
    /// "manual" (only when shown). It can always be hidden.
    /// </summary>
    public string OverlayAutoShow { get; set; } = "connection";

    /// <summary>Start the meter as soon as AVION2 opens.</summary>
    public bool AutoStart { get; set; }

    /// <summary>Single-line overlay rows instead of player cards.</summary>
    public bool CompactRows { get; set; }

    /// <summary>The overlay shows the other party members' HP while in a party.</summary>
    public bool PartyFrames { get; set; } = true;

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

    /// <summary>The screen edges flash red when you lose a big share of your HP at once.</summary>
    public bool HudDamageFlash { get; set; } = true;

    /// <summary>Your DPS, crit and fight time above your HP bar while you fight.</summary>
    public bool HudDpsLine { get; set; } = true;

    /// <summary>The party's DPS right under the game's group list, in its style.</summary>
    public bool HudPartyMeter { get; set; } = true;

    /// <summary>Under the boss bar: exact HP, time to kill and the fight clock.</summary>
    public bool HudBossInfo { get; set; } = true;

    /// <summary>Boss skills appear under the target frame (instead of a notice).</summary>
    public bool HudBossCasts { get; set; } = true;

    /// <summary>HUD: whether a gathering will succeed, above the gathering bars, as soon as it starts.</summary>
    public bool HudGatherOutcome { get; set; } = true;

    /// <summary>HUD: while flying, the seconds until we land, beside the game's flight gauge.</summary>
    public bool HudFlightTime { get; set; } = true;

    /// <summary>HUD: the chain skills we can use right now, with the time left to use them.</summary>
    public bool HudChainSteps { get; set; } = true;

    /// <summary>HUD: a small mark beside the boss bar while the boss we have selected targets us.</summary>
    public bool HudBossOnYou { get; set; } = true;

    /// <summary>Calibrated HUD positions per game resolution ("2560x1440").</summary>
    public Dictionary<string, Avion2.Analysis.HudLayout> HudLayouts { get; set; } = [];

    /// <summary>
    /// When the cube compass shows by itself (Find My Cube on, capturing): "manual" (only from Find a cube) or
    /// "nearby" (when a cube packet arrives for a cube near you).
    /// </summary>
    public string CompassAutoShow { get; set; } = "manual";

    public double? CompassLeft { get; set; }
    public double? CompassTop { get; set; }

    /// <summary>Server region for the rift/event timetable.</summary>
    public Avion2.Analysis.ServerRegion Region { get; set; } = Avion2.Analysis.ServerRegion.Europe;

    /// <summary>Look for a newer release on GitHub at start (one small request; dev builds never do).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Streamer mode: player, character and legion names show as stand-ins everywhere.</summary>
    public bool HideNames { get; set; }

    /// <summary>OBS overlay: the page on http://localhost:<see cref="ObsPort"/>/overlay.</summary>
    public bool ObsOverlay { get; set; }

    public int ObsPort { get; set; } = Avion2.Engine.ObsServer.DefaultPort;

    /// <summary>OBS overlay: other players show as "Player 2", … (streamer mode hides everything anyway).</summary>
    public bool ObsHideOthers { get; set; } = true;

    /// <summary>OBS overlay: our name and legion show as "You" and "Legion".</summary>
    public bool ObsHideSelf { get; set; }

    /// <summary>OBS overlay: what the page shows (the address Settings builds).</summary>
    public Avion2.Engine.ObsPage ObsPage { get; set; } = new();

    /// <summary>Discord Rich Presence: what we do in game shows in our Discord profile (off until switched on).</summary>
    public bool DiscordPresence { get; set; }

    /// <summary>Discord: our character's name shows too (never in streamer mode).</summary>
    public bool DiscordShowName { get; set; }

    /// <summary>Discord: our character's portrait from the official site is the large picture (never in streamer mode).</summary>
    public bool DiscordShowPicture { get; set; }

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

    /// <summary>A line when pet souls come in ("Zelophi: 3/5 souls to the pet").</summary>
    public bool NotifyPetSouls { get; set; } = true;

    /// <summary>Group finder notices: a join request, the room full and ready, the dungeon starting.</summary>
    public bool NotifyJoinRequests { get; set; } = true;

    /// <summary>A card with the applicant's stats and gear when someone asks to join the room we lead.</summary>
    public bool ApplicantCard { get; set; } = true;

    public double? ApplicantLeft { get; set; }

    /// <summary>A profile opened in the game opens on the Characters page too (without taking the focus).</summary>
    public bool OpenProfiles { get; set; } = true;
    public double? ApplicantTop { get; set; }
    public bool NotifyRoomReady { get; set; } = true;
    public bool NotifyDungeonStart { get; set; } = true;
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

    /// <summary>Gear Forge: the tool opened last ("enchant", "crafting" or "stats").</summary>
    public string? ForgeTool { get; set; }

    /// <summary>Gear Forge crafting: materials bought or gathered instead of crafted (item ids).</summary>
    public List<uint> CraftBuyInstead { get; set; } = [];

    /// <summary>Home-screen background video: null = %AppData%\AVION2\background.mp4 if present, "" = none.</summary>
    public string? BackgroundVideo { get; set; }

    /// <summary>Record each meter session to a .pcap (for troubleshooting).</summary>
    public bool RecordSessions { get; set; }

    /// <summary>Developer: recordings hold every connection of AION2.exe (login, lobby, world), not only the world.</summary>
    public bool RecordAllGameConnections { get; set; }

    /// <summary>Developer: session recordings also save pictures of the game window, two a second.</summary>
    public bool RecordScreen { get; set; }

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
