using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Avion2.Protocol;

namespace Avion2.Engine;

/// <summary>Writes decoded events as JSON lines, for offline analysis.</summary>
public sealed class EventLog : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { AddEveryEvent } },
    };

    private readonly StreamWriter _writer;

    public EventLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = false };
    }

    public void Write(GameEvent e) => _writer.WriteLine(Serialize(e));

    /// <summary>One event as a JSON line, with its type in <c>type</c>.</summary>
    public static string Serialize(GameEvent e) => JsonSerializer.Serialize(e, Options);

    public void Dispose() => _writer.Dispose();

    /// <summary>
    /// Adds every event the attribute list on <see cref="GameEvent"/> leaves out, named after the type without
    /// "Event" (DodgeEvent → dodge), so a new event never breaks the log.
    /// </summary>
    private static void AddEveryEvent(JsonTypeInfo info)
    {
        if (info.Type != typeof(GameEvent) || info.PolymorphismOptions is not { } poly) return;
        var types = poly.DerivedTypes.Select(d => d.DerivedType).ToHashSet();
        var names = poly.DerivedTypes.Select(d => d.TypeDiscriminator as string).ToHashSet();
        foreach (Type type in typeof(GameEvent).Assembly.GetTypes()
                     .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(GameEvent)))
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (!types.Add(type)) continue;
            string name = type.Name.EndsWith("Event", StringComparison.Ordinal) ? type.Name[..^5] : type.Name;
            name = char.ToLowerInvariant(name[0]) + name[1..];
            if (!names.Add(name)) name = type.Name;
            poly.DerivedTypes.Add(new JsonDerivedType(type, name));
        }
    }
}
