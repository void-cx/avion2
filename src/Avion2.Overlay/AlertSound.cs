using System.IO;
using System.Windows.Media;

namespace Avion2.Overlay;

/// <summary>
/// Low-HP warning sound with adjustable volume: a custom .wav/.mp3 when configured, otherwise a generated
/// two-tone chime (distinct from Windows system sounds so it is not mistaken for a notification).
/// </summary>
public sealed class AlertSound : IDisposable
{
    private readonly MediaPlayer _player = new();

    public AlertSound(string? customPath, double volume)
    {
        string path = !string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath)
            ? Path.GetFullPath(customPath)
            : WriteChime();
        _player.Open(new Uri(path, UriKind.Absolute));
        Volume = volume;
    }

    /// <summary>0..1.</summary>
    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 1);
    }

    public void Play()
    {
        _player.Stop();
        _player.Position = TimeSpan.Zero;
        _player.Play();
    }

    public void Dispose() => _player.Close();

    /// <summary>Writes the built-in chime to %LocalAppData%\AVION2\low-hp.wav (MediaPlayer needs a file).</summary>
    private static string WriteChime()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "low-hp.wav");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, GenerateChime());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            path = Path.Combine(Path.GetTempPath(), "avion2-low-hp.wav");
            File.WriteAllBytes(path, GenerateChime());
        }

        return path;
    }

    /// <summary>16-bit mono PCM: 880/660 Hz alternating, 140 ms each, with short fades to avoid clicks.</summary>
    private static byte[] GenerateChime()
    {
        const int sampleRate = 22050;
        const double toneSeconds = 0.14;
        double[] frequencies = [880, 660, 880, 660];
        int toneSamples = (int)(sampleRate * toneSeconds);
        int gapSamples = sampleRate / 50;
        int totalSamples = frequencies.Length * (toneSamples + gapSamples);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        int dataBytes = totalSamples * 2;
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);

        int fade = sampleRate / 200;
        foreach (double frequency in frequencies)
        {
            for (int i = 0; i < toneSamples; i++)
            {
                double envelope = Math.Min(1.0, Math.Min(i, toneSamples - i) / (double)fade);
                double sample = Math.Sin(2 * Math.PI * frequency * i / sampleRate) * envelope * 0.8;
                writer.Write((short)(sample * short.MaxValue));
            }

            for (int i = 0; i < gapSamples; i++)
            {
                writer.Write((short)0);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }
}
