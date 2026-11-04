using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Avion2.Engine;

/// <summary>The opcodes of Discord's local IPC frames.</summary>
public enum DiscordOp
{
    Handshake = 0,
    Frame = 1,
    Close = 2,
    Ping = 3,
    Pong = 4,
}

/// <summary>
/// Discord's local IPC framing: a little-endian 32-bit opcode, a little-endian 32-bit length, then that many bytes
/// of UTF-8 JSON. The handshake (op 0) names the application; commands such as <c>SET_ACTIVITY</c> go in op 1.
/// </summary>
public static class DiscordFrames
{
    public const int HeaderSize = 8;

    /// <summary>Answers are small (the READY event is the largest); anything longer is not Discord.</summary>
    public const int MaxPayload = 1 << 20;

    public static byte[] Encode(DiscordOp op, string json)
    {
        int length = Encoding.UTF8.GetByteCount(json);
        var frame = new byte[HeaderSize + length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), length);
        Encoding.UTF8.GetBytes(json, frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>The opcode and payload length of a frame header; throws <see cref="InvalidDataException"/> for a bad one.</summary>
    public static (DiscordOp Op, int Length) ReadHeader(ReadOnlySpan<byte> header)
    {
        int op = BinaryPrimitives.ReadInt32LittleEndian(header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (op is < 0 or > (int)DiscordOp.Pong || length is < 0 or > MaxPayload)
        {
            throw new InvalidDataException("Not a Discord IPC frame.");
        }

        return ((DiscordOp)op, length);
    }

    /// <summary>The handshake: <c>{"v":1,"client_id":"…"}</c>.</summary>
    public static string Handshake(string applicationId) => Json(w =>
    {
        w.WriteNumber("v", 1);
        w.WriteString("client_id", applicationId);
    });

    /// <summary>
    /// <c>SET_ACTIVITY</c> for our process; a null activity clears the presence. Times are Unix milliseconds.
    /// </summary>
    public static string SetActivity(int processId, DiscordActivity? activity, string nonce) => Json(w =>
    {
        w.WriteString("cmd", "SET_ACTIVITY");
        w.WriteStartObject("args");
        w.WriteNumber("pid", processId);
        if (activity is null)
        {
            w.WriteNull("activity");
        }
        else
        {
            w.WriteStartObject("activity");
            WriteIf(w, "details", activity.Details);
            WriteIf(w, "state", activity.State);
            if (activity.StartMs is { } start)
            {
                w.WriteStartObject("timestamps");
                w.WriteNumber("start", start);
                w.WriteEndObject();
            }

            w.WriteStartObject("assets");
            w.WriteString("large_image", activity.LargeImage);
            WriteIf(w, "large_text", activity.LargeText);
            WriteIf(w, "small_image", activity.SmallImage);
            WriteIf(w, "small_text", activity.SmallText);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        w.WriteEndObject();
        w.WriteString("nonce", nonce);
    });

    private static void WriteIf(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null)
        {
            w.WriteString(name, value);
        }
    }

    private static string Json(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads one whole frame; throws <see cref="EndOfStreamException"/> when the other side closed.</summary>
    public static async Task<(DiscordOp Op, string Json)> ReadAsync(Stream stream, CancellationToken cancel)
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, cancel);
        var (op, length) = ReadHeader(header);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancel);
        return (op, Encoding.UTF8.GetString(payload));
    }
}

/// <summary>Where the connection to Discord stands.</summary>
public enum DiscordState
{
    /// <summary>Not started (the feature is off).</summary>
    Off,

    /// <summary>Looking for Discord for the first time.</summary>
    Connecting,

    /// <summary>No Discord app on this PC answered; tried again now and then.</summary>
    NotRunning,

    Connected,

    /// <summary>Discord refused the application id.</summary>
    Rejected,
}

/// <summary>
/// A small client for the Discord desktop app's local IPC (the named pipe <c>\\.\pipe\discord-ipc-0</c> … <c>-9</c>)
/// that keeps our Rich Presence up to date. Everything runs on a background task: <see cref="SetActivity"/> only
/// stores the latest activity. When Discord isn't running it tries again every <c>retry</c>, quietly; when it
/// starts later, or restarts, the latest activity is sent again. Stopping clears the presence first.
/// </summary>
public sealed class DiscordClient : IDisposable
{
    private readonly Func<CancellationToken, Task<Stream?>> _connect;
    private readonly int _processId;
    private readonly TimeSpan _retry;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _applicationId;
    private DiscordActivity? _activity;
    private int _version;

    /// <param name="connect">Opens the connection (tests pass their own); by default the local Discord pipes.</param>
    /// <param name="retry">How long to wait before looking for Discord again (default 20 s).</param>
    public DiscordClient(Func<CancellationToken, Task<Stream?>>? connect = null, int? processId = null, TimeSpan? retry = null)
    {
        _connect = connect ?? ConnectLocalAsync;
        _processId = processId ?? Environment.ProcessId;
        _retry = retry ?? TimeSpan.FromSeconds(20);
    }

    public DiscordState State { get; private set; }

    /// <summary>Raised on a background thread when <see cref="State"/> changes.</summary>
    public event Action<DiscordState>? StateChanged;

    /// <summary>Starts talking to Discord for this application (nothing changes when it already does).</summary>
    public void Start(string applicationId)
    {
        lock (_sync)
        {
            if (_loop is not null && _applicationId == applicationId)
            {
                return;
            }

            StopLocked();
            _applicationId = applicationId;
            _cts = new CancellationTokenSource();
            SetState(DiscordState.Connecting);
            var cancel = _cts.Token;
            _loop = Task.Run(() => RunAsync(applicationId, cancel));
        }
    }

    /// <summary>The activity to show from now on (null clears it); sent in the background.</summary>
    public void SetActivity(DiscordActivity? activity)
    {
        lock (_sync)
        {
            _activity = activity;
            _version++;
        }

        Wake();
    }

    /// <summary>Clears the presence and disconnects, in the background.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            StopLocked();
        }
    }

    /// <summary>Clears the presence and disconnects, waiting up to <paramref name="wait"/> for it (on exit).</summary>
    public void Stop(TimeSpan wait)
    {
        Task? loop;
        lock (_sync)
        {
            loop = _loop;
            StopLocked();
        }

        try
        {
            loop?.Wait(wait);
        }
        catch (AggregateException)
        {
            // stopping anyway
        }
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(1));

    private void StopLocked()
    {
        _cts?.Cancel();
        _cts = null;
        _loop = null;
        _applicationId = null;
        SetState(DiscordState.Off);
    }

    private void SetState(DiscordState state)
    {
        if (State != state)
        {
            State = state;
            StateChanged?.Invoke(state);
        }
    }

    /// <summary>Sets the state unless the loop was stopped meanwhile (Off belongs to <see cref="Stop()"/>).</summary>
    private void SetLoopState(DiscordState state, CancellationToken cancel)
    {
        lock (_sync)
        {
            if (!cancel.IsCancellationRequested)
            {
                SetState(state);
            }
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // already woken
        }
    }

    private async Task RunAsync(string applicationId, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var wait = _retry;
            try
            {
                var stream = await _connect(cancel).ConfigureAwait(false);
                if (stream is null)
                {
                    SetLoopState(DiscordState.NotRunning, cancel);
                }
                else
                {
                    await using (stream.ConfigureAwait(false))
                    {
                        if (!await SessionAsync(stream, applicationId, cancel).ConfigureAwait(false))
                        {
                            SetLoopState(DiscordState.Rejected, cancel);
                            wait = TimeSpan.FromTicks(_retry.Ticks * 15); // an id Discord refused won't work again soon
                        }
                        else
                        {
                            SetLoopState(DiscordState.NotRunning, cancel); // Discord closed or went away
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or TimeoutException
                                           or ObjectDisposedException or UnauthorizedAccessException or OperationCanceledException)
            {
                SetLoopState(DiscordState.NotRunning, cancel);
            }

            try
            {
                await Task.Delay(wait, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One connection: the handshake, then the activity whenever it changes. False when Discord refused us.</summary>
    private async Task<bool> SessionAsync(Stream stream, string applicationId, CancellationToken cancel)
    {
        using var writeLock = new SemaphoreSlim(1, 1);
        async Task Send(DiscordOp op, string json, CancellationToken token)
        {
            await writeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(DiscordFrames.Encode(op, json), token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        await Send(DiscordOp.Handshake, DiscordFrames.Handshake(applicationId), cancel).ConfigureAwait(false);
        using (var ready = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            ready.CancelAfter(TimeSpan.FromSeconds(10));
            var (op, json) = await DiscordFrames.ReadAsync(stream, ready.Token).ConfigureAwait(false);
            if (op == DiscordOp.Close)
            {
                return !IsRefusal(json);
            }

            if (op != DiscordOp.Frame || !IsReady(json))
            {
                throw new InvalidDataException("Discord did not answer the handshake.");
            }
        }

        SetLoopState(DiscordState.Connected, cancel);

        // Discord's answers are read (and its pings answered) beside the writes; the session ends with the reader
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var reader = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var (op, json) = await DiscordFrames.ReadAsync(stream, session.Token).ConfigureAwait(false);
                    if (op == DiscordOp.Ping)
                    {
                        await Send(DiscordOp.Pong, json, session.Token).ConfigureAwait(false);
                    }
                    else if (op == DiscordOp.Close)
                    {
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
            {
                // the connection is gone
            }
            finally
            {
                session.Cancel();
            }
        });

        int sent = -1;
        try
        {
            while (true)
            {
                DiscordActivity? activity;
                int version;
                lock (_sync)
                {
                    activity = _activity;
                    version = _version;
                }

                if (version != sent)
                {
                    await Send(DiscordOp.Frame, DiscordFrames.SetActivity(_processId, activity, Guid.NewGuid().ToString("N")), session.Token)
                        .ConfigureAwait(false);
                    sent = version;
                }

                await _wake.WaitAsync(session.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // stopped: clear the presence before closing (Discord would on its own once the pipe closes)
            using var clear = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            try
            {
                await Send(DiscordOp.Frame, DiscordFrames.SetActivity(_processId, null, Guid.NewGuid().ToString("N")), clear.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // gone already
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            return true; // the reader saw the connection end
        }
        finally
        {
            session.Cancel();
            await reader.ConfigureAwait(false);
        }
    }

    /// <summary>The handshake's answer: <c>{"cmd":"DISPATCH","evt":"READY",…}</c>.</summary>
    private static bool IsReady(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("evt", out var evt) && evt.ValueKind == JsonValueKind.String && evt.GetString() == "READY";
    }

    /// <summary>A close for a bad application id (code 4000, "Invalid Client ID").</summary>
    private static bool IsRefusal(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("code", out var code) && code.TryGetInt32(out int value) && value == 4000;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The first Discord pipe that answers, or null when Discord isn't running on this PC.</summary>
    public static async Task<Stream?> ConnectLocalAsync(CancellationToken cancel)
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(100, cancel).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        return null;
    }
}
