using System.Text.Json;
using Avion2.Protocol;

namespace Avion2.Engine;

/// <summary>Writes decoded events as JSON lines, for offline analysis.</summary>
public sealed class EventLog : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly StreamWriter _writer;

    public EventLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = false };
    }

    public void Write(GameEvent e) => _writer.WriteLine(JsonSerializer.Serialize(e, Options));

    public void Dispose() => _writer.Dispose();
}
