using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Avion2.Capture;

/// <summary>Writes captured frames to a .pcap; which frames depends on the recorder.</summary>
public interface IFrameRecorder : IDisposable
{
    long Written { get; }

    void Write(in RawFrame frame, LockedFlow? locked);
}

/// <summary>
/// The TCP connections owned by given processes, from Windows' connection table (<c>GetExtendedTcpTable</c>), as flows
/// in both directions. Windows only; elsewhere the set stays empty.
/// </summary>
public static class ProcessConnections
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    /// <summary>Process ids of the running processes with one of these names (without ".exe").</summary>
    public static HashSet<int> ProcessIds(IEnumerable<string> names)
    {
        var ids = new HashSet<int>();
        foreach (string name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                ids.Add(process.Id);
                process.Dispose();
            }
        }

        return ids;
    }

    /// <summary>Every connection of these processes, as (local → remote) and (remote → local) flows.</summary>
    public static HashSet<FlowKey> Flows(IReadOnlySet<int> pids)
    {
        var flows = new HashSet<FlowKey>();
        if (pids.Count == 0 || !OperatingSystem.IsWindows())
        {
            return flows;
        }

        Read(AfInet, pids, flows);
        Read(AfInet6, pids, flows);
        return flows;
    }

    private static void Read(int family, IReadOnlySet<int> pids, HashSet<FlowKey> flows)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0);
        for (int attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0);
                if (result == 122) // ERROR_INSUFFICIENT_BUFFER: the table grew in between
                {
                    continue;
                }

                if (result != 0)
                {
                    return;
                }

                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, size);
                Parse(bytes, family == AfInet6, pids, flows);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// MIB_TCPTABLE_OWNER_PID: count u32, then rows of state, local addr, local port, remote addr, remote port, pid
    /// (u32 each; addresses and ports in network order). The IPv6 rows: local addr[16], scope u32, local port u32,
    /// remote addr[16], scope u32, remote port u32, state u32, pid u32.
    /// </summary>
    public static void Parse(ReadOnlySpan<byte> table, bool v6, IReadOnlySet<int> pids, HashSet<FlowKey> flows)
    {
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(table);
        int rowSize = v6 ? 56 : 24;
        for (int i = 0; i < count && 4 + (i + 1) * rowSize <= table.Length; i++)
        {
            var row = table.Slice(4 + i * rowSize, rowSize);
            NetEndpoint local, remote;
            int pid;
            if (v6)
            {
                pid = (int)BinaryPrimitives.ReadUInt32LittleEndian(row[52..]);
                local = new NetEndpoint(BinaryPrimitives.ReadUInt128BigEndian(row), true, Port(row[20..]));
                remote = new NetEndpoint(BinaryPrimitives.ReadUInt128BigEndian(row[24..]), true, Port(row[44..]));
            }
            else
            {
                pid = (int)BinaryPrimitives.ReadUInt32LittleEndian(row[20..]);
                local = new NetEndpoint(BinaryPrimitives.ReadUInt32BigEndian(row[4..]), false, Port(row[8..]));
                remote = new NetEndpoint(BinaryPrimitives.ReadUInt32BigEndian(row[12..]), false, Port(row[16..]));
            }

            if (pids.Contains(pid) && remote.Port != 0)
            {
                flows.Add(new FlowKey(local, remote));
                flows.Add(new FlowKey(remote, local));
            }
        }
    }

    // The port is the first two bytes of the u32, in network order
    private static ushort Port(ReadOnlySpan<byte> field) => BinaryPrimitives.ReadUInt16BigEndian(field);
}

/// <summary>
/// Developer recording: every TCP connection of the game process (login, lobby, world, instances), not only the
/// locked world connection. Frames that match no connection yet are held for a few seconds, because Windows lists a
/// new connection only after its first packets went by.
/// </summary>
public sealed class ProcessRecorder : IFrameRecorder
{
    private const long HoldMs = 10_000;
    private const int MaxHeld = 50_000;
    private const long RefreshMs = 500;

    private readonly string _path;
    private readonly IReadOnlyList<string> _processNames;
    private readonly HashSet<FlowKey> _flows = new();
    private readonly LinkedList<RawFrame> _held = new();
    private readonly Stopwatch _sinceRefresh = new();
    private CaptureFileWriterDevice? _writer;
    private int _device = -1;

    public ProcessRecorder(string path, IReadOnlyList<string> processNames)
    {
        _path = path;
        _processNames = processNames;
    }

    public long Written { get; private set; }

    public void Write(in RawFrame frame, LockedFlow? locked)
    {
        if (!_sinceRefresh.IsRunning || _sinceRefresh.ElapsedMilliseconds >= RefreshMs)
        {
            Refresh();
        }

        if (!PacketDissector.TryParse(frame.LinkType, frame.Data, out var segment, out var payload))
        {
            return;
        }

        if (_flows.Contains(segment.Flow))
        {
            Record(frame);
            return;
        }

        // The lobby (character and server list) can come from another process than the game: known by its packets
        if (IsLobby(payload))
        {
            Adopt([segment.Flow, new FlowKey(segment.Flow.Destination, segment.Flow.Source)]);
            Record(frame);
            return;
        }

        _held.AddLast(frame);
        while (_held.Count > 0 && (_held.Count > MaxHeld || frame.TimestampMs - _held.First!.Value.TimestampMs > HoldMs))
        {
            _held.RemoveFirst();
        }
    }

    /// <summary>New connections of the game: their held frames are written first, in the order they arrived.</summary>
    private void Refresh()
    {
        _sinceRefresh.Restart();
        var current = ProcessConnections.Flows(ProcessConnections.ProcessIds(_processNames));
        current.ExceptWith(_flows);
        if (current.Count == 0)
        {
            return;
        }

        Adopt(current);
    }

    /// <summary>
    /// A lobby packet: a varint length that matches the segment, then an opcode <c>xx 39</c> (the lobby family:
    /// handshake, account, server list, characters, redirect).
    /// </summary>
    public static bool IsLobby(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            return false;
        }

        uint length = 0;
        int i = 0, shift = 0;
        while (i < payload.Length && i < 3)
        {
            byte b = payload[i++];
            length |= (uint)(b & 0x7F) << shift;
            shift += 7;
            if (b < 0x80)
            {
                break;
            }
        }

        long frame = (long)length - 4 + i;
        return i + 2 <= payload.Length && payload[i + 1] == 0x39 && payload[i] is >= 0x01 and <= 0x20 &&
               frame >= 2 && frame <= payload.Length;
    }

    /// <summary>Records these flows from now on, and writes what was held of them, in the order it arrived.</summary>
    private void Adopt(IReadOnlyCollection<FlowKey> current)
    {
        _flows.UnionWith(current); // closed connections stay: their last packets may still be on the way
        for (var node = _held.First; node is not null;)
        {
            var next = node.Next;
            if (PacketDissector.TryParse(node.Value.LinkType, node.Value.Data, out var segment, out _) && current.Contains(segment.Flow))
            {
                Record(node.Value);
                _held.Remove(node);
            }

            node = next;
        }
    }

    /// <summary>One file holds one link type: the device of the first game frame is recorded.</summary>
    private void Record(in RawFrame frame)
    {
        if (_writer is null)
        {
            NpcapLoader.EnsureSearchPath();
            _device = frame.DeviceIndex;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            _writer = new CaptureFileWriterDevice(_path, FileMode.Create);
            _writer.Open(new DeviceConfiguration { LinkLayerType = (LinkLayers)frame.LinkType });
        }

        if (frame.DeviceIndex != _device)
        {
            return;
        }

        ulong seconds = (ulong)(frame.TimestampMs / 1000);
        ulong micros = (ulong)(frame.TimestampMs % 1000) * 1000;
        _writer.Write(new RawCapture((LinkLayers)frame.LinkType, new PosixTimeval(seconds, micros), frame.Data));
        Written++;
    }

    public void Dispose()
    {
        _writer?.Close();
        _writer = null;
    }
}
