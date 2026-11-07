using Mirador.Core.Modules;
using Mirador.Core.Profiles;

namespace Mirador.Core.Session;

/// <summary>
/// Told when a Monado session starts and when it ends, for work that goes along with a session without being part of it
/// (switching the base stations). Called from the session's background thread; both calls return at once, do their work
/// elsewhere and never throw, so that they can't fail, delay or block a Start or a Stop.
/// </summary>
public interface ISessionHook
{
    /// <summary>A session of <paramref name="profile"/> is starting, with <paramref name="module"/> as its headset module (null: none).</summary>
    void Started(Profile profile, IHeadsetModule? module);

    /// <summary>The session that <see cref="Started"/> was told of has ended, however it ended.</summary>
    void Ended(Profile profile, IHeadsetModule? module);
}
