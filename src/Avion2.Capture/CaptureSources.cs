using System.Runtime.InteropServices;
using System.Threading.Channels;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Avion2.Capture;

public static class NpcapLoader
{
    private static bool _done;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? path);

    /// <summary>
    /// Npcap installs wpcap.dll into System32\Npcap unless "WinPcap API-compatible mode" was chosen.
    /// Must run before the first SharpPcap call.
    /// </summary>
    public static void EnsureSearchPath()
    {
        if (_done || !OperatingSystem.IsWindows())
        {
            return;
        }

        _done = true;
        string npcap = Path.Combine(Environment.SystemDirectory, "Npcap");
        if (Directory.Exists(npcap))
        {
            SetDllDirectory(npcap);
        }
    }
}

public sealed record CaptureDeviceInfo(int Index, string Name, string Description, bool IsLoopback, IReadOnlyList<string> Addresses);

public static class CaptureDevices
{
    private const uint PcapIfLoopback = 0x1;

    public static IReadOnlyList<CaptureDeviceInfo> List()
    {
        NpcapLoader.EnsureSearchPath();
        var list = new List<CaptureDeviceInfo>();
        var devices = LibPcapLiveDeviceList.Instance;
        for (int i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            var iface = device.Interface;
            bool loopback = (iface.Flags & PcapIfLoopback) != 0 ||
                            (iface.Description ?? string.Empty).Contains("loopback", StringComparison.OrdinalIgnoreCase);
            var addresses = iface.Addresses
                .Select(a => a.Addr?.ipAddress?.ToString())
                .Where(a => !string.IsNullOrEmpty(a))
                .Cast<string>()
                .ToList();
            list.Add(new CaptureDeviceInfo(i, iface.Name, iface.FriendlyName ?? iface.Description ?? iface.Name, loopback, addresses));
        }

        return list;
    }
}

/// <summary>
/// Captures from several Npcap devices at once (the game may run over a VPN, a loopback relay
/// or the physical NIC) and funnels frames into one channel.
/// </summary>
public sealed class LiveCapture : IDisposable
{
    private readonly List<(LibPcapLiveDevice Device, CaptureDeviceInfo Info)> _open = new();
    private readonly Channel<RawFrame> _channel = Channel.CreateBounded<RawFrame>(
        new BoundedChannelOptions(200_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public ChannelReader<RawFrame> Frames => _channel.Reader;

    public long Dropped { get; private set; }

    public IReadOnlyList<CaptureDeviceInfo> OpenDevices => _open.Select(o => o.Info).ToList();

    /// <summary>Opens the given devices (all devices with an address, plus loopback, when null).</summary>
    public IReadOnlyList<string> Start(IReadOnlyCollection<int>? deviceIndices = null)
    {
        NpcapLoader.EnsureSearchPath();
        var errors = new List<string>();
        var devices = LibPcapLiveDeviceList.Instance;
        var infos = CaptureDevices.List();

        foreach (var info in infos)
        {
            bool wanted = deviceIndices is null
                ? info.IsLoopback || info.Addresses.Count > 0
                : deviceIndices.Contains(info.Index);
            if (!wanted)
            {
                continue;
            }

            var device = devices[info.Index];
            try
            {
                device.Open(new DeviceConfiguration
                {
                    Mode = DeviceModes.None,
                    ReadTimeout = 100,
                    Snaplen = 65535,
                    KernelBufferSize = 16 * 1024 * 1024,
                });

                // Cheap kernel-side pre-filter; game detection itself happens in user space.
                device.Filter = "tcp";
                int dlt = (int)device.LinkType;
                int index = info.Index;
                bool loopback = info.IsLoopback || PacketDissector.IsLoopbackLink(dlt);
                device.OnPacketArrival += (_, e) =>
                {
                    var header = e.Header.Timeval;
                    long ts = (long)header.Seconds * 1000 + (long)header.MicroSeconds / 1000;
                    if (!_channel.Writer.TryWrite(new RawFrame(index, dlt, loopback, ts, e.Data.ToArray())))
                    {
                        Dropped++;
                    }
                };
                device.StartCapture();
                _open.Add((device, info));
            }
            catch (Exception ex)
            {
                errors.Add($"#{info.Index} {info.Description}: {ex.Message}");
                try
                {
                    device.Close();
                }
                catch
                {
                    // ignored
                }
            }
        }

        return errors;
    }

    public void Dispose()
    {
        foreach (var (device, _) in _open)
        {
            try
            {
                device.StopCapture();
                device.Close();
            }
            catch
            {
                // ignored on shutdown
            }
        }

        _open.Clear();
        _channel.Writer.TryComplete();
    }
}

public static class PcapFile
{
    public static IEnumerable<RawFrame> Read(string path)
    {
        NpcapLoader.EnsureSearchPath();
        using var device = new CaptureFileReaderDevice(path);
        device.Open();
        int dlt = (int)device.LinkType;
        bool loopback = PacketDissector.IsLoopbackLink(dlt);
        while (device.GetNextPacket(out PacketCapture capture) == GetPacketStatus.PacketRead)
        {
            var header = capture.Header.Timeval;
            long ts = (long)header.Seconds * 1000 + (long)header.MicroSeconds / 1000;
            yield return new RawFrame(0, dlt, loopback, ts, capture.Data.ToArray());
        }
    }
}

/// <summary>
/// Records the game device to a .pcap for offline replay. Frames are buffered per device until the
/// game connection locks (so the lead-up, e.g. the identity burst, is kept), then streamed.
/// </summary>
public sealed class PcapRecorder : IDisposable
{
    private const long PreLockBufferMs = 15_000;

    private readonly string _path;
    private readonly Dictionary<int, Queue<RawFrame>> _preLock = new();
    private CaptureFileWriterDevice? _writer;
    private int _device = -1;

    public PcapRecorder(string path)
    {
        _path = path;
    }

    public long Written { get; private set; }

    public void Write(in RawFrame frame, LockedFlow? locked)
    {
        if (_writer is null)
        {
            if (locked is null)
            {
                Buffer(frame);
                return;
            }

            Open(locked.DeviceIndex, frame);
        }

        if (frame.DeviceIndex == _device)
        {
            WriteFrame(frame);
        }
    }

    private void Buffer(in RawFrame frame)
    {
        if (!_preLock.TryGetValue(frame.DeviceIndex, out var queue))
        {
            queue = new Queue<RawFrame>();
            _preLock[frame.DeviceIndex] = queue;
        }

        queue.Enqueue(frame);
        while (queue.Count > 0 && frame.TimestampMs - queue.Peek().TimestampMs > PreLockBufferMs)
        {
            queue.Dequeue();
        }
    }

    private void Open(int device, in RawFrame current)
    {
        NpcapLoader.EnsureSearchPath();
        _device = device;
        int dlt = current.LinkType;
        if (_preLock.TryGetValue(device, out var queue) && queue.Count > 0)
        {
            dlt = queue.Peek().LinkType;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        _writer = new CaptureFileWriterDevice(_path, FileMode.Create);
        _writer.Open(new DeviceConfiguration { LinkLayerType = (LinkLayers)dlt });

        if (queue is not null)
        {
            foreach (var buffered in queue)
            {
                WriteFrame(buffered);
            }
        }

        _preLock.Clear();
    }

    private void WriteFrame(in RawFrame frame)
    {
        ulong seconds = (ulong)(frame.TimestampMs / 1000);
        ulong micros = (ulong)(frame.TimestampMs % 1000) * 1000;
        _writer!.Write(new RawCapture((LinkLayers)frame.LinkType, new PosixTimeval(seconds, micros), frame.Data));
        Written++;
    }

    public void Dispose()
    {
        _writer?.Close();
        _writer = null;
    }
}
