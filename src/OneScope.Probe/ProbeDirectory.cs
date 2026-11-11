namespace OneScope.Probe;

/// <summary>Where probes put their sockets, so a viewer can list the programs running a probe.</summary>
public static class ProbeDirectory
{
    /// <summary>The environment variable that overrides the directory (used by tests and unusual setups).</summary>
    public const string OverrideVariable = "ONESCOPE_SOCKET_DIR";

    /// <summary>File extension of a probe's socket.</summary>
    public const string SocketExtension = ".sock";

    /// <summary>
    /// The per-user directory: the override variable if set, else <c>$XDG_RUNTIME_DIR/onescope</c> on Unix when that is set
    /// (short paths, and the system cleans it at logout), else <c>onescope/probes</c> under the user's local application data.
    /// </summary>
    public static string Default()
    {
        string? over = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrEmpty(over))
        {
            return over;
        }

        if (!OperatingSystem.IsWindows())
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg))
            {
                return Path.Combine(xdg, "onescope");
            }
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "onescope", "probes");
    }

    /// <summary>
    /// Creates the directory readable only by its user (mode 0700 on Unix; on Windows it inherits the user's profile permissions)
    /// and tightens it if it already exists with wider permissions.
    /// </summary>
    /// <exception cref="IOException">The directory cannot be created or made private.</exception>
    public static void Prepare(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(directory, Private);
        if (File.GetUnixFileMode(directory) != Private)
        {
            File.SetUnixFileMode(directory, Private);
        }
    }
}
