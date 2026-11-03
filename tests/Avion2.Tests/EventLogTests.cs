using System.Runtime.CompilerServices;
using System.Text.Json;
using Avion2.Engine;
using Avion2.Protocol;
using Xunit;

namespace Avion2.Tests;

public class EventLogTests
{
    public static TheoryData<string> EventTypes()
    {
        var data = new TheoryData<string>();
        foreach (Type type in typeof(GameEvent).Assembly.GetTypes()
                     .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(GameEvent))))
            data.Add(type.FullName!);
        return data;
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public void EveryEventCanBeLogged(string typeName)
    {
        var e = (GameEvent)RuntimeHelpers.GetUninitializedObject(typeof(GameEvent).Assembly.GetType(typeName)!);
        using var json = JsonDocument.Parse(EventLog.Serialize(e));
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("type").ValueKind);
    }

    [Fact]
    public void EventNamesAreUnique()
    {
        var names = typeof(GameEvent).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(GameEvent)))
            .Select(t => JsonDocument.Parse(EventLog.Serialize((GameEvent)RuntimeHelpers.GetUninitializedObject(t)))
                .RootElement.GetProperty("type").GetString())
            .ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void ListedEventsKeepTheirNames()
    {
        Assert.Contains("\"type\":\"damage\"", Logged(typeof(DamageEvent)));
        Assert.Contains("\"type\":\"valuelessHit\"", Logged(typeof(ValuelessHitEvent)));
    }

    private static string Logged(Type type) => EventLog.Serialize((GameEvent)RuntimeHelpers.GetUninitializedObject(type));
}
