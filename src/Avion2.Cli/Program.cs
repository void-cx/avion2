using System.Globalization;
using System.Text;
using Avion2.Analysis;
using Avion2.Capture;
using Avion2.Cli;
using Avion2.Engine;
using Avion2.Protocol;

Console.OutputEncoding = Encoding.UTF8;
var cli = new CliArgs(args);

try
{
    return cli.Command switch
    {
        "devices" => Commands.Devices(),
        "live" => await Commands.Live(cli),
        "replay" when cli.Positional.Count > 0 => Commands.Replay(cli),
        "dump" when cli.Positional.Count > 0 => Commands.Dump(cli),
        "import-data" => await Commands.ImportData(cli),
        _ => Commands.Usage(),
    };
}
catch (DllNotFoundException)
{
    Console.Error.WriteLine("Npcap (wpcap.dll) was not found. Install it from https://npcap.com/.");
    return 2;
}

internal static class Commands
{
    public static int Usage()
    {
        Console.WriteLine("""
            AVION2 — AION2 gameplay analyzer

            Usage:
              avion2 devices                         List capture devices
              avion2 live   [options]                Live DPS meter
              avion2 replay <file.pcap> [options]    Decode a recorded capture and print encounters
              avion2 dump   <file.pcap> [options]    Hex-dump decoded packets (reverse engineering)
              avion2 import-data [--lang de] [--source <dir>]
                                                     Fetch skill/NPC names (A2Tools tables) into ./data

            Options:
              --device 3,5        Only capture on these devices (default: all with an address + loopback)
              --record <file>     live: also record the game device to a .pcap
              --log <file>        Write decoded events as JSON lines
              --data <dir>        Directory with skills.json / npcs.json (default: ./data)
              --idle <seconds>    Idle gap that ends an encounter (default 15)
              --duration <sec>    live: stop automatically after this many seconds
              --all-targets       live: include damage on targets you did not hit yourself
              --opcode 0438       dump: only this opcode (wire order, hex)
              --limit <n>         dump: stop after n packets
              --summary           dump: only print the opcode histogram
              --unparsed          dump: only packets the parsers rejected
              --events            dump: show the events decoded from each packet
            """);
        return 1;
    }

    public static async Task<int> ImportData(CliArgs cli)
    {
        string lang = cli.Get("lang") ?? "en";
        string target = cli.Get("data") ?? Path.Combine(Environment.CurrentDirectory, "data");
        if (cli.Get("source") is null)
        {
            Console.WriteLine($"Downloading {lang} tables from {GameDataImporter.Repository} …");
        }

        try
        {
            var result = await GameDataImporter.ImportAsync(lang, target, cli.Get("source"));
            Console.WriteLine($"Imported {result.Skills:N0} skills and {result.Npcs:N0} NPCs ({result.Bosses:N0} bosses, {result.Dummies:N0} dummies) into {result.Directory}");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or HttpRequestException or InvalidDataException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static int Devices()
    {
        foreach (var device in CaptureDevices.List())
        {
            string loopback = device.IsLoopback ? " [loopback]" : string.Empty;
            string addresses = device.Addresses.Count > 0 ? string.Join(", ", device.Addresses) : "-";
            Console.WriteLine($"#{device.Index,-2} {device.Description}{loopback}");
            Console.WriteLine($"     {addresses}");
        }

        return 0;
    }

    public static async Task<int> Live(CliArgs cli)
    {
        var scope = cli.Has("all-targets") ? MeterScope.AllTargets : MeterScope.MyTargets;
        using var engine = new MeterEngine(new EngineOptions
        {
            Devices = ParseDevices(cli),
            RecordPath = cli.Get("record"),
            EventLogPath = cli.Get("log"),
            DataDirectory = cli.Get("data"),
            Tracker = TrackerOptions(cli),
        });

        foreach (string error in engine.Start())
        {
            Console.Error.WriteLine($"Could not open {error}");
        }

        if (!engine.Status().Message.StartsWith("Searching", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(engine.Status().Message);
            return 2;
        }

        using var cts = new CancellationTokenSource();
        if (cli.GetInt("duration", 0) is > 0 and int duration)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(duration));
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        bool interactive = !Console.IsOutputRedirected && !Console.IsInputRedirected;
        long nextPlainPrint = 0;
        if (interactive)
        {
            Console.Clear();
            Console.CursorVisible = false;
        }

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var snapshot = engine.Snapshot(scope);
                var status = engine.Status();
                string self = status.SelfName is { } name ? $"{name} ({status.SelfClass})" : "unknown — teleport once";
                string screen = ConsoleMeter.Render(snapshot, live: status.InCombat) +
                                $"\n{status.Message}\nYou: {self}   frames {status.Frames:N0}  game packets {status.GamePackets:N0}  " +
                                $"parsed {status.ParsedPackets:N0}  drops {status.Dropped:N0}" +
                                (cli.Has("record") ? $"  recorded {status.Recorded:N0}" : string.Empty) +
                                $"\n[R] reset   [A] targets: {(scope == MeterScope.MyTargets ? "mine" : "all")}   [Q] quit";

                if (!interactive)
                {
                    // Redirected output: print a plain snapshot every 5 seconds instead of redrawing.
                    if (Environment.TickCount64 >= nextPlainPrint)
                    {
                        Console.WriteLine(screen);
                        Console.WriteLine(new string('-', 60));
                        nextPlainPrint = Environment.TickCount64 + 5000;
                    }
                }
                else
                {
                    Draw(screen);
                }

                while (interactive && Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true).Key;
                    if (key == ConsoleKey.Q)
                    {
                        cts.Cancel();
                    }
                    else if (key == ConsoleKey.A)
                    {
                        scope = scope == MeterScope.MyTargets ? MeterScope.AllTargets : MeterScope.MyTargets;
                    }
                    else if (key == ConsoleKey.R)
                    {
                        engine.Reset();
                    }
                }

                try
                {
                    await Task.Delay(250, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            if (interactive)
            {
                Console.CursorVisible = true;
            }
        }

        engine.Stop();
        Console.WriteLine();
        var history = engine.History();
        foreach (var snapshot in history)
        {
            Console.WriteLine(ConsoleMeter.Render(snapshot, live: false, maxRows: 24, maxSkills: 6));
        }

        if (history.Count == 0)
        {
            Console.WriteLine("No encounters recorded.");
        }

        return 0;
    }

    public static int Replay(CliArgs cli)
    {
        var session = new GameSession(LoadData(cli), TrackerOptions(cli));
        var registry = session.Registry;
        var tracker = session.Tracker;
        using var log = cli.Get("log") is { } logPath ? new EventLog(logPath) : null;
        var eventCounts = new Dictionary<string, int>();

        var pipeline = new GameCapturePipeline(e =>
        {
            session.Apply(e);
            log?.Write(e);
            string type = e.GetType().Name;
            eventCounts[type] = eventCounts.GetValueOrDefault(type) + 1;
        });
        pipeline.StatusChanged += message => Console.WriteLine($"[stream] {message}");

        long lastTs = 0;
        foreach (var frame in PcapFile.Read(cli.Positional[0]))
        {
            pipeline.Process(frame);
            tracker.Tick(frame.TimestampMs);
            lastTs = frame.TimestampMs;
        }

        tracker.Tick(lastTs + long.MaxValue / 4);
        PrintHistory(tracker);

        var stats = pipeline.Decoder.Stats;
        Console.WriteLine($"Frames {pipeline.Stats.Frames:N0}, TCP {pipeline.Stats.TcpSegments:N0}, game segments {pipeline.Stats.GameSegments:N0}, locks {pipeline.Stats.Locks}");
        Console.WriteLine($"Stream bytes {stats.BytesIn:N0} (skipped {stats.BytesSkipped:N0}), packets {stats.Packets:N0}, parsed {stats.ParsedPackets:N0}, " +
                          $"bundles {stats.Bundles:N0} (bad {stats.BadBundles}, embedded {stats.EmbeddedBundles})");
        if (pipeline.Reassembler is { } reassembler)
        {
            Console.WriteLine($"TCP retransmits {reassembler.Retransmits:N0}, gap skips {reassembler.GapSkips} ({reassembler.SkippedBytes:N0} bytes)");
        }

        Console.WriteLine("Events: " + string.Join(", ", eventCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:N0}")));
        Console.WriteLine($"You: {registry.SelfName ?? "not detected"} (id {registry.SelfId?.ToString() ?? "-"}), map {registry.MapId?.ToString() ?? "-"}");
        return 0;
    }

    public static int Dump(CliArgs cli)
    {
        ushort? onlyOpcode = cli.Get("opcode") is { } hex ? ushort.Parse(hex.Replace(" ", string.Empty), NumberStyles.HexNumber) : null;
        int limit = cli.GetInt("limit", int.MaxValue);
        bool summaryOnly = cli.Has("summary");
        bool unparsedOnly = cli.Has("unparsed");
        bool showEvents = cli.Has("events");
        int printed = 0;

        var pipeline = new GameCapturePipeline(_ => { }, (payload, depth, ts) =>
        {
            ushort opcode = Opcodes.Read(payload);
            if (summaryOnly || printed >= limit || (onlyOpcode is { } wanted && wanted != opcode))
            {
                return;
            }

            var decoded = new List<GameEvent>();
            bool parsed = PacketParser.Parse(payload, ts, decoded.Add);
            if (unparsedOnly && parsed)
            {
                return;
            }

            printed++;
            string time = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().ToString("HH:mm:ss.fff");
            Console.WriteLine($"{time} d{depth} {Opcodes.Format(opcode)} len {payload.Length,5}  {Convert.ToHexString(payload[2..]).ToLowerInvariant()}");
            if (showEvents)
            {
                foreach (var e in decoded)
                {
                    Console.WriteLine($"    → {e}");
                }
            }
        });
        pipeline.StatusChanged += message => Console.WriteLine($"[stream] {message}");

        foreach (var frame in PcapFile.Read(cli.Positional[0]))
        {
            pipeline.Process(frame);
        }

        Console.WriteLine();
        Console.WriteLine("Opcode histogram:");
        foreach (var (opcode, count) in pipeline.Decoder.Stats.OpcodeCounts.OrderByDescending(kv => kv.Value))
        {
            Console.WriteLine($"  {Opcodes.Format(opcode)}  {count,8:N0}");
        }

        return 0;
    }

    private static void PrintHistory(CombatTracker tracker)
    {
        foreach (var encounter in tracker.History)
        {
            Console.WriteLine(ConsoleMeter.Render(encounter.Final, live: false, maxRows: 24, maxSkills: 6));
        }

        if (tracker.History.Count == 0)
        {
            Console.WriteLine("No encounters recorded.");
        }
    }

    private static void Draw(string screen)
    {
        int width = Math.Max(20, Console.WindowWidth - 1);
        int height = Console.WindowHeight;
        var lines = screen.Split('\n');
        Console.SetCursorPosition(0, 0);
        var sb = new StringBuilder();
        for (int i = 0; i < height - 1; i++)
        {
            string line = i < lines.Length ? lines[i].TrimEnd('\r') : string.Empty;
            sb.Append(line.Length > width ? line[..width] : line.PadRight(width));
            sb.Append('\n');
        }

        Console.Write(sb.ToString());
    }

    private static GameData LoadData(CliArgs cli) => MeterEngine.LoadGameData(cli.Get("data"));

    private static TrackerOptions TrackerOptions(CliArgs cli) => new()
    {
        EncounterIdleMs = cli.GetInt("idle", 15) * 1000L,
    };

    private static IReadOnlyCollection<int>? ParseDevices(CliArgs cli) =>
        cli.Get("device") is { } list
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList()
            : null;
}
