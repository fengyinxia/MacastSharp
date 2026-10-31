namespace Macast.Core.Utils;

public static class ExecutableLocator
{
    /// <summary>Resolves an absolute path, a path relative to the install dir, or a bare name on PATH.</summary>
    public static string? Resolve(string? configured, string fallbackName)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var value = configured.Trim();
            if (Path.IsPathRooted(value))
            {
                return File.Exists(value) ? value : null;
            }

            var local = Path.Combine(AppContext.BaseDirectory, value);
            if (File.Exists(local))
            {
                return local;
            }

            if (!value.Contains(Path.DirectorySeparatorChar) && !value.Contains('/'))
            {
                var onPath = OnPath(value);
                if (onPath is not null)
                {
                    return onPath;
                }
            }
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "bin", fallbackName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        return OnPath(fallbackName);
    }

    public static string? OnPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (Path.IsPathRooted(name))
        {
            return File.Exists(name) ? name : null;
        }

        var env = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // malformed PATH entry
            }
        }

        return null;
    }
}
