namespace OneScope.Sdk;

/// <summary>
/// Thrown when bytes a module was asked to read (a replay, a snapshot, a step input) are malformed.
/// The message names the input and says what was expected and where.
/// </summary>
public sealed class ScopeFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="input">What was being read, for example a file name or "snapshot".</param>
    /// <param name="message">What was expected and where it went wrong.</param>
    public ScopeFormatException(string input, string message)
        : base($"{input}: {message}")
    {
        Input = input;
    }

    /// <summary>What was being read when the problem was found.</summary>
    public string Input { get; }
}
