namespace Avion2.Cli;

/// <summary>Tiny <c>command [positional...] [--key value] [--flag]</c> parser.</summary>
internal sealed class CliArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public CliArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                string key = arg[2..];
                string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
                _options[key] = value;
            }
            else if (Command is null)
            {
                Command = arg.ToLowerInvariant();
            }
            else
            {
                Positional.Add(arg);
            }
        }
    }

    public string? Command { get; }

    public List<string> Positional { get; } = new();

    public bool Has(string key) => _options.ContainsKey(key);

    public string? Get(string key) => _options.GetValueOrDefault(key);

    public int GetInt(string key, int fallback) => int.TryParse(Get(key), out int value) ? value : fallback;
}
