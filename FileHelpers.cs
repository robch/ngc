using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Lifted (trimmed) from cycod-main2/src/common/Helpers/FileHelpers.cs
// Only the glob-resolution parts are kept (FilesFromGlob(s), SplitGlobPattern,
// MakeRelativePath). All ConsoleHelpers/Logger calls have been stripped since
// they're not portable/needed here.
public static class FileHelpers
{
    public static IEnumerable<string> FilesFromGlobs(IEnumerable<string> globs)
    {
        foreach (var glob in globs)
        {
            foreach (var file in FilesFromGlob(glob))
            {
                yield return file;
            }
        }
    }

    public static IEnumerable<string> FilesFromGlob(string glob)
    {
        try
        {
            if (glob == "-") return new[] { glob }; // special case for stdin

            var isAbsolutePath = Path.IsPathRooted(glob);
            var globIsFile = isAbsolutePath && File.Exists(glob);
            if (globIsFile) return new[] { glob };

            if (File.Exists(glob)) return new[] { glob };

            // Split glob into a literal directory prefix and the wildcard pattern.
            // The Matcher doesn't support ".." traversal, so we resolve the prefix
            // as the actual base directory and pass only the remaining pattern.
            var (baseDirectory, globPattern) = SplitGlobPattern(glob);

            var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher();
            matcher.AddInclude(globPattern);

            var directoryInfo = new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(new DirectoryInfo(baseDirectory));
            var matchResult = matcher.Execute(directoryInfo);

            return matchResult.Files.Select(file => MakeRelativePath(Path.Combine(baseDirectory, file.Path)));
        }
        catch (Exception)
        {
            return Enumerable.Empty<string>();
        }
    }

    private static (string baseDirectory, string globPattern) SplitGlobPattern(string glob)
    {
        // Normalize separators to forward slash for consistent splitting
        var normalized = glob.Replace('\\', '/');

        // Find the index of the first wildcard character
        var wildcardIndex = normalized.IndexOfAny(new[] { '*', '?', '[' });
        if (wildcardIndex < 0)
        {
            // No wildcard — treat the whole thing as a path/file; use cwd
            return (Directory.GetCurrentDirectory(), normalized);
        }

        // Find the last separator before the wildcard
        var lastSepBeforeWild = wildcardIndex > 0
            ? normalized.LastIndexOf('/', wildcardIndex - 1)
            : -1;
        string dirPrefix, pattern;
        if (lastSepBeforeWild < 0)
        {
            // No directory separator before wildcard (e.g. "*.md", "**/*.md")
            dirPrefix = ".";
            pattern = normalized;
        }
        else
        {
            dirPrefix = normalized.Substring(0, lastSepBeforeWild + 1); // includes trailing slash
            pattern = normalized.Substring(lastSepBeforeWild + 1);
        }

        var baseDirectory = Path.GetFullPath(dirPrefix);
        return (baseDirectory, pattern);
    }

    public static string MakeRelativePath(string fullPath)
    {
        var currentDirectory = Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        fullPath = Path.GetFullPath(fullPath);

        if (fullPath.StartsWith(currentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath.Substring(currentDirectory.Length);
        }

        Uri fullPathUri = new Uri(fullPath);
        Uri currentDirectoryUri = new Uri(currentDirectory);

        string relativePath = Uri.UnescapeDataString(currentDirectoryUri.MakeRelativeUri(fullPathUri).ToString().Replace('/', Path.DirectorySeparatorChar));

        if (Path.DirectorySeparatorChar == '\\')
        {
            relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        }

        return relativePath;
    }
}
