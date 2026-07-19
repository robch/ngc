using System;

// Lifted (trimmed) from cycod-main2/src/common/Helpers/PathHelpers.cs
// Only the bits FileHelpers.cs (glob support) needs.
public class PathHelpers
{
    public static string? Combine(string path1, string path2)
    {
        try
        {
            return System.IO.Path.Combine(path1, path2);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Expands tilde (~) paths to full paths using the user's home directory.
    /// Handles both "~" (home directory) and "~/path" (home directory + path).
    /// </summary>
    public static string ExpandPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/"))
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(homeDir, path.Substring(2));
        }

        return path;
    }
}
