using System.IO;
using System.Windows.Media;

namespace Avion2.Overlay;

/// <summary>Which built-in sound to use when no custom file is set.</summary>
public enum SoundKind
{
    /// <summary>Urgent: fast alternating 880/660 Hz beeps.</summary>
    LowHp,

    /// <summary>Calm: a soft rising two-note bell, so a timer never sounds like danger.</summary>
    Notice,
}

/// <summary>
/// An alert sound with adjustable volume: a custom .wav/.mp3 when configured, otherwise a generated built-in tone
/// (distinct from Windows system sounds, and low-HP and notices distinct from each other).
/// </summary>
public sealed class AlertSound : IDisposable
{
    private const int SampleRate = 22050;
    private readonly MediaPlayer _player = new();

    public AlertSound(string? customPath, double volume, SoundKind kind = SoundKind.LowHp)
    {
        CustomPath = !string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath) ? Path.GetFullPath(customPath) : null;
        _player.Open(new Uri(CustomPath ?? WriteBuiltIn(kind), UriKind.Absolute));
        Volume = volume;
    }

    /// <summary>The custom file in use; null = built-in tone.</summary>
    public string? CustomPath { get; }

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

    /// <summary>Writes the built-in tone to %LocalAppData%\AVION2 (MediaPlayer needs a file).</summary>
    private static string WriteBuiltIn(SoundKind kind)
    {
        string name = kind == SoundKind.Notice ? "notice.wav" : "low-hp.wav";
        byte[] wav = Wav(kind == SoundKind.Notice ? Bell() : Beeps());
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", name);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, wav);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            path = Path.Combine(Path.GetTempPath(), "avion2-" + name);
            File.WriteAllBytes(path, wav);
        }

        return path;
    }

    /// <summary>Low HP: 880/660 Hz alternating, 140 ms each, with short fades to avoid clicks.</summary>
    private static double[] Beeps()
    {
        double[] frequencies = [880, 660, 880, 660];
        int tone = (int)(SampleRate * 0.14);
        int gap = SampleRate / 50;
        int fade = SampleRate / 200;
        var samples = new double[frequencies.Length * (tone + gap)];
        for (int n = 0; n < frequencies.Length; n++)
        {
            int start = n * (tone + gap);
            for (int i = 0; i < tone; i++)
            {
                double envelope = Math.Min(1.0, Math.Min(i, tone - i) / (double)fade);
                samples[start + i] = Math.Sin(2 * Math.PI * frequencies[n] * i / SampleRate) * envelope * 0.8;
            }
        }

        return samples;
    }

    /// <summary>Notice: G5 then C6, bell-like (soft attack, exponential decay, a quiet octave overtone).</summary>
    private static double[] Bell()
    {
        (double Frequency, double Start)[] notes = [(784, 0.0), (1046.5, 0.16)];
        const double length = 0.9;
        var samples = new double[(int)(SampleRate * (notes[^1].Start + length))];
        int attack = SampleRate / 100;
        foreach (var (frequency, start) in notes)
        {
            int offset = (int)(start * SampleRate);
            for (int i = 0; i < SampleRate * length && offset + i < samples.Length; i++)
            {
                double t = i / (double)SampleRate;
                double envelope = Math.Min(1.0, i / (double)attack) * Math.Exp(-t * 5.5);
                double wave = Math.Sin(2 * Math.PI * frequency * t) + 0.22 * Math.Sin(2 * Math.PI * frequency * 2 * t);
                samples[offset + i] += wave * envelope * 0.42;
            }
        }

        return samples;
    }

    /// <summary>16-bit mono PCM WAV.</summary>
    private static byte[] Wav(double[] samples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        int dataBytes = samples.Length * 2;
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
        foreach (double sample in samples)
        {
            writer.Write((short)(Math.Clamp(sample, -1, 1) * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
