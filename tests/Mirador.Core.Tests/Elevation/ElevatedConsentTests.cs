using System.Security.Principal;
using Mirador.Core.Elevation;
using Mirador.Ipc;

namespace Mirador.Core.Tests.Elevation;

public class ElevatedConsentTests
{
    private static readonly SecurityIdentifier Alice = new("S-1-5-21-1-2-3-1001");
    private static readonly SecurityIdentifier Bob = new("S-1-5-21-1-2-3-1002");

    private sealed class FakeValues : IConsentValues
    {
        public HashSet<string> Sids { get; } = [];
        public Exception? Fails { get; set; }

        public bool Has(string sid) => Sids.Contains(sid);

        public void Set(string sid)
        {
            if (Fails is not null)
            {
                throw Fails;
            }

            Sids.Add(sid);
        }

        public void Delete(string sid) => Sids.Remove(sid);
    }

    [Fact]
    public void Nobody_is_allowed_at_first()
    {
        var consent = new ElevatedConsent(new FakeValues());

        Assert.False(consent.IsAllowed(Alice));
        Assert.False(consent.IsAllowed(Bob));
    }

    [Fact]
    public void Allow_then_revoke_one_sid_keeps_the_other()
    {
        var values = new FakeValues();
        var consent = new ElevatedConsent(values);

        consent.Allow(Alice);
        consent.Allow(Bob);
        consent.Revoke(Alice);

        Assert.False(consent.IsAllowed(Alice));
        Assert.True(consent.IsAllowed(Bob));
        Assert.Equal([Bob.Value], values.Sids);
    }

    [Fact]
    public void Revoking_a_sid_that_was_never_allowed_is_fine()
    {
        var consent = new ElevatedConsent(new FakeValues());

        consent.Revoke(Alice);

        Assert.False(consent.IsAllowed(Alice));
    }

    [Fact]
    public void Allow_command_allows_the_running_user_and_exits_0()
    {
        var values = new FakeValues();
        var errors = new List<string>();

        var code = AllowElevatedCommand.Run(new ElevatedConsent(values), Alice, errors.Add);

        Assert.Equal(0, code);
        Assert.Equal([Alice.Value], values.Sids);
        Assert.Empty(errors);
    }

    [Fact]
    public void Allow_command_that_cannot_write_exits_1_and_says_why()
    {
        var values = new FakeValues { Fails = new UnauthorizedAccessException("Attempted to perform an unauthorized operation.") };
        var errors = new List<string>();

        var code = AllowElevatedCommand.Run(new ElevatedConsent(values), Alice, errors.Add);

        Assert.Equal(1, code);
        Assert.Empty(values.Sids);
        var error = Assert.Single(errors);
        Assert.Contains("unauthorized operation", error);
    }
}
