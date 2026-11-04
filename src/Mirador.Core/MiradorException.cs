namespace Mirador.Core;

/// <summary>A failure whose message is written for the user: plain words, which part failed, what to do.</summary>
public sealed class MiradorException(string message) : Exception(message);
