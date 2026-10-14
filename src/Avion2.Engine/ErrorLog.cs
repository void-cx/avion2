using Avion2.Protocol;

namespace Avion2.Engine;

/// <summary>
/// Errors while decoding, with the offending bytes, in %LocalAppData%\AVION2\errors.log (first 50 per run, so a
/// repeating error can't fill the disk). Lets us find and fix a bad packet instead of guessing.
/// </summary>
public static class ErrorLog
{
    private const int MaxPerRun = 50;
    private static readonly object Sync = new();
    private static int _written;

    public static string Path { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "errors.log");

    /// <summary>Errors seen this run (also those not written).</summary>
    public static int Count { get; private set; }

    /// <summary>Routes packet errors from every decoder here.</summary>
    public static void Install() =>
        GameStreamDecoder.PacketError = (ex, payload, ts) => Write($"packet {Opcodes.Format(Opcodes.Read(payload))}", ex, payload.ToArray(), ts);

    public static void Write(string what, Exception error, byte[]? data, long? timestampMs = null)
    {
        lock (Sync)
        {
            Count++;
            if (_written >= MaxPerRun)
            {
                return;
            }

            _written++;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                string when = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                string bytes = data is null ? string.Empty : $"{Environment.NewLine}  {Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 4096)))}";
                File.AppendAllText(Path, $"{when} {what}: {error.GetType().Name}: {error.Message}{Environment.NewLine}  " +
                                         $"{error.StackTrace?.Split(Environment.NewLine).FirstOrDefault()?.Trim()}{bytes}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // nowhere to report; the meter keeps running
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
