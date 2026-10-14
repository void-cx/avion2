using System.Diagnostics;

namespace Avion2.Engine;

/// <summary>
/// Whether AION2 is running on this PC (the Unreal client, <c>Aion2\Binaries\Win64\AION2.exe</c>). Checked at most
/// every two seconds, so it is cheap to ask often.
/// </summary>
public static class GameProcess
{
    private static readonly string[] Names = ["AION2", "AION2-Win64-Shipping"];
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);
    private static readonly Stopwatch SinceCheck = new();
    private static bool _running;

    public static bool IsRunning
    {
        get
        {
            lock (Names)
            {
                if (!SinceCheck.IsRunning || SinceCheck.Elapsed >= CheckEvery)
                {
                    _running = Names.Any(Exists);
                    SinceCheck.Restart();
                }

                return _running;
            }
        }
    }

    private static bool Exists(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }
}
