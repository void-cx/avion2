using Avion2.Engine;
using Xunit;

namespace Avion2.Tests;

public class RecordingsTests
{
    [Fact]
    public void Capture_markers_sit_beside_the_recording()
    {
        string recording = Path.Combine(Path.GetTempPath(), "caps", "session-20261114-120000.pcap");
        string folder = Path.GetDirectoryName(Path.GetFullPath(recording))!;

        Assert.Equal(Path.Combine(folder, "session-20261114-120000-markers.jsonl"), Recordings.MarkersPath(recording));
        Assert.Equal(Path.Combine(folder, "session-20261114-120000-marker-003.png"), Recordings.MarkerPicturePath(recording, 3));
    }
}
