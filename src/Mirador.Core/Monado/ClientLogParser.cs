using System.Text.RegularExpressions;

namespace Mirador.Core.Monado;

/// <summary>
/// Follows Monado's log to see which applications are connected. A client's name arrives on a later line than its
/// "connected" line, so a name goes to the most recent connected client that has none yet.
/// </summary>
public sealed partial class ClientLogParser
{
    private readonly List<Client> clients = [];
    private volatile IReadOnlyList<string> connectedApps = [];

    /// <summary>The named applications that are connected, in the order they connected. Safe to read from any thread.</summary>
    public IReadOnlyList<string> ConnectedApps => connectedApps;

    /// <summary>
    /// Takes the next line of Monado's log. Not safe to call from several threads at once.
    /// </summary>
    /// <returns>True when <see cref="ConnectedApps"/> is different afterwards.</returns>
    public bool Feed(string line)
    {
        if (ConnectedLine().Match(line) is { Success: true } connected)
        {
            var id = connected.Groups["id"].Value;
            clients.RemoveAll(c => c.Id == id); // a client that comes back without its disconnect line having been seen
            clients.Add(new Client(id));
        }
        else if (DisconnectedLine().Match(line) is { Success: true } gone)
        {
            var id = gone.Groups["id"].Value;
            clients.RemoveAll(c => c.Id == id);
        }
        else if (NameLine().Match(line) is { Success: true } named)
        {
            var waiting = clients.FindLast(c => c.Name is null);
            if (waiting is not null)
            {
                waiting.Name = named.Groups["name"].Value;
            }
        }
        else
        {
            return false;
        }

        var now = clients.Where(c => !string.IsNullOrEmpty(c.Name)).Select(c => c.Name!).ToArray();
        if (connectedApps.SequenceEqual(now))
        {
            return false;
        }

        connectedApps = now;
        return true;
    }

    [GeneratedRegex(@"\[client_connected\]\s+Client (?<id>\d+) connected")]
    private static partial Regex ConnectedLine();

    [GeneratedRegex(@"\[client_disconnected\]\s+Client (?<id>\d+) disconnected")]
    private static partial Regex DisconnectedLine();

    [GeneratedRegex(@"^\s*application_name: '(?<name>.*)'\s*$")]
    private static partial Regex NameLine();

    private sealed class Client(string id)
    {
        public string Id { get; } = id;
        public string? Name { get; set; }
    }
}
