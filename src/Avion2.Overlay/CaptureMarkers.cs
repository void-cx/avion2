using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// Capture markers: the marker hotkey (F9 by default) while a session is recorded saves the moment, a picture of the
/// game and a note on what was just done, so packet bursts can be matched with actions in the game. One JSON line per
/// marker in <c>session-…-markers.jsonl</c> beside the .pcap:
/// <c>{"utc_ms":…,"n":1,"note":"…","screenshot":"session-…-marker-001.png"}</c>. <c>utc_ms</c> is the system clock in
/// Unix milliseconds at the key press: the clock Npcap stamps the packets with, so markers line up with the recording.
/// </summary>
internal sealed class CaptureMarkers
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _recording;
    private int _next;

    public CaptureMarkers(string recording)
    {
        _recording = recording;
        // A recording path given on the command line can be used again: number on from the markers already there
        _next = Count = ReadCount(Recordings.MarkersPath(recording));
    }

    /// <summary>Markers saved so far.</summary>
    public int Count { get; private set; }

    /// <summary>The note of the last saved marker ("" when it had none), or null before the first.</summary>
    public string? LastNote { get; private set; }

    public string FilePath => Recordings.MarkersPath(_recording);

    /// <summary>
    /// At the key press: takes the time and the picture (the game's area, or the whole desktop when the game window
    /// isn't found), and starts saving the picture in the background. The marker is written by <see cref="Finish"/>.
    /// </summary>
    public PendingMarker Begin()
    {
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int number = ++_next;
        BitmapSource? picture = null;
        try
        {
            picture = GameWindow.Capture() ?? GameWindow.CaptureDesktop();
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or OutOfMemoryException)
        {
            // window closed or resized mid-copy: a marker without a picture
        }

        string? path = picture is null ? null : Recordings.MarkerPicturePath(_recording, number);
        var saved = picture is null ? Task.FromResult(false) : Task.Run(() => SavePicture(picture, path!));
        return new PendingMarker(ms, number, path is null ? null : Path.GetFileName(path), saved);
    }

    /// <summary>Writes the marker with its note (after the note box closed). Returns false when the file can't be written.</summary>
    public bool Finish(PendingMarker marker, string note)
    {
        // The picture is usually done long before the note; never write a name of a picture that failed
        bool pictureSaved = marker.PictureSaved.Wait(TimeSpan.FromSeconds(10)) && marker.PictureSaved.Result;
        lock (_write)
        {
            return Append(marker, note, pictureSaved);
        }
    }

    private readonly object _write = new();

    private bool Append(PendingMarker marker, string note, bool pictureSaved)
    {
        string line = JsonSerializer.Serialize(new
        {
            utc_ms = marker.UtcMs,
            n = marker.Number,
            note,
            screenshot = pictureSaved ? marker.Picture : null,
        }, Json);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, line + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        Count++;
        LastNote = note;
        return true;
    }

    private static bool SavePicture(BitmapSource picture, string path)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(picture));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            encoder.Save(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or
                                       System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private static int ReadCount(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).Count(l => l.Trim().Length > 0) : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}

/// <summary>A marker between the key press and the note: its time, number and picture file (null without one).</summary>
internal sealed record PendingMarker(long UtcMs, int Number, string? Picture, Task<bool> PictureSaved);
