using Mirador.Core.Monado;

namespace Mirador.Core.Tests.Monado;

public class ClientLogParserTests
{
    private static string Connected(int id) => $" INFO [client_connected] Client {id} connected";
    private static string Disconnected(int id) => $" INFO [client_disconnected] Client {id} disconnected";
    private static string Named(string name) => $"\tapplication_name: '{name}'";

    [Fact]
    public void Client_parser_tracks_app_names()
    {
        var p = new ClientLogParser();
        p.Feed(" INFO [client_connected] Client 1 connected");
        p.Feed("\tapplication_name: 'HelloXR'");
        Assert.Equal(["HelloXR"], p.ConnectedApps);
        p.Feed(" INFO [client_disconnected] Client 1 disconnected");
        Assert.Empty(p.ConnectedApps);
    }

    [Fact]
    public void Nothing_is_connected_at_first() => Assert.Empty(new ClientLogParser().ConnectedApps);

    [Fact]
    public void Feed_says_whether_the_list_changed()
    {
        var p = new ClientLogParser();

        Assert.False(p.Feed(Connected(1))); // not named yet
        Assert.True(p.Feed(Named("HelloXR")));
        Assert.False(p.Feed(" INFO [something_else] unrelated"));
        Assert.False(p.Feed(""));
        Assert.True(p.Feed(Disconnected(1)));
        Assert.False(p.Feed(Disconnected(1))); // already gone
    }

    [Fact]
    public void A_client_that_never_gave_a_name_is_not_listed_and_leaving_changes_nothing()
    {
        var p = new ClientLogParser();
        p.Feed(Connected(1));

        Assert.Empty(p.ConnectedApps);
        Assert.False(p.Feed(Disconnected(1)));
    }

    [Fact]
    public void Several_clients_are_listed_in_the_order_they_connected()
    {
        var p = new ClientLogParser();
        p.Feed(Connected(1));
        p.Feed(Named("First"));
        p.Feed(Connected(2));
        p.Feed(Named("Second"));

        Assert.Equal(["First", "Second"], p.ConnectedApps);
    }

    [Fact]
    public void A_name_belongs_to_the_most_recent_client_that_has_none()
    {
        var p = new ClientLogParser();
        p.Feed(Connected(1));
        p.Feed(Connected(2));
        p.Feed(Named("Second")); // goes to client 2
        p.Feed(Named("First"));  // client 2 has one now, so this goes to client 1

        Assert.Equal(["First", "Second"], p.ConnectedApps);

        p.Feed(Disconnected(2));
        Assert.Equal(["First"], p.ConnectedApps);
    }

    [Fact]
    public void A_disconnect_removes_only_that_clients_name()
    {
        var p = new ClientLogParser();
        p.Feed(Connected(1));
        p.Feed(Named("First"));
        p.Feed(Connected(2));
        p.Feed(Named("Second"));

        p.Feed(Disconnected(1));

        Assert.Equal(["Second"], p.ConnectedApps);
    }

    [Fact]
    public void A_name_with_no_client_waiting_is_ignored()
    {
        var p = new ClientLogParser();

        Assert.False(p.Feed(Named("Orphan")));
        Assert.Empty(p.ConnectedApps);

        p.Feed(Connected(1));
        p.Feed(Named("HelloXR"));
        Assert.False(p.Feed(Named("Extra"))); // client 1 already has its name
        Assert.Equal(["HelloXR"], p.ConnectedApps);
    }

    [Fact]
    public void Two_clients_may_run_the_same_app()
    {
        var p = new ClientLogParser();
        p.Feed(Connected(1));
        p.Feed(Named("HelloXR"));
        p.Feed(Connected(2));
        p.Feed(Named("HelloXR"));

        Assert.Equal(["HelloXR", "HelloXR"], p.ConnectedApps);
    }

    [Fact]
    public void A_name_may_hold_spaces_and_apostrophes()
    {
        var p = new ClientLogParser();
        p.Feed(Connected(1));
        p.Feed(Named("Bob's Space Game"));

        Assert.Equal(["Bob's Space Game"], p.ConnectedApps);
    }

    [Fact]
    public void An_empty_name_is_not_listed()
    {
        var p = new ClientLogParser();
        p.Feed(Connected(1));

        Assert.False(p.Feed(Named("")));
        Assert.Empty(p.ConnectedApps);
    }

    [Fact]
    public void A_client_that_connects_again_under_the_same_number_starts_without_a_name()
    {
        var p = new ClientLogParser();
        p.Feed(Connected(1));
        p.Feed(Named("Old"));

        p.Feed(Connected(1)); // the disconnect line was never seen

        Assert.Empty(p.ConnectedApps);
        p.Feed(Named("New"));
        Assert.Equal(["New"], p.ConnectedApps);
    }

    [Fact]
    public void A_list_taken_earlier_does_not_change()
    {
        var p = new ClientLogParser();
        p.Feed(Connected(1));
        p.Feed(Named("HelloXR"));
        var before = p.ConnectedApps;

        p.Feed(Disconnected(1));

        Assert.Equal(["HelloXR"], before);
    }
}
