using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using Tezgah.Core.Model;

namespace Tezgah.Core.Discovery;

/// <summary>Small, defensive filesystem helpers shared by detectors and providers.</summary>
internal static class FsUtil
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: OneDrive "online-only" placeholders.</summary>
    private const FileAttributes CloudPlaceholder = (FileAttributes)0x00040000 | (FileAttributes)0x00400000 | FileAttributes.Offline;

    public const long DefaultMaxReadBytes = 256 * 1024;

    /// <summary>
    /// Reads a small text file. Returns null when missing, too big, inaccessible, or an online-only cloud placeholder
    /// (reading those would trigger a download).
    /// </summary>
    public static string? ReadText(string path, long maxBytes = DefaultMaxReadBytes)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > maxBytes || (fi.Attributes & CloudPlaceholder) != 0)
            {
                return null;
            }

            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads at most <paramref name="maxChars"/> characters (for sniffing imports in big source files).</summary>
    public static string? ReadHead(string path, int maxChars = 64 * 1024)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || (fi.Attributes & CloudPlaceholder) != 0)
            {
                return null;
            }

            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[maxChars];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when the directory exists and is not a reparse point (junction/symlink).</summary>
    public static bool IsRealDirectory(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return (attr & FileAttributes.Directory) != 0 && (attr & FileAttributes.ReparsePoint) == 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sums file sizes below <paramref name="path"/> without following reparse points. Bounded by an entry count
    /// and a time budget so huge trees never stall a scan; the result is then a lower bound.
    /// </summary>
    public static long DirectorySize(string path, int maxEntries = 400_000, TimeSpan? budget = null, CancellationToken ct = default)
    {
        var limit = budget ?? TimeSpan.FromSeconds(8);
        var sw = Stopwatch.StartNew();
        long total = 0;
        var count = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };
            var enumerable = new FileSystemEnumerable<long>(
                path,
                (ref FileSystemEntry e) => e.IsDirectory ? 0 : e.Length,
                options);
            foreach (var len in enumerable)
            {
                total += len;
                if (++count >= maxEntries)
                {
                    break;
                }

                if ((count & 0xFFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (sw.Elapsed > limit)
                    {
                        break;
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return total;
    }

    /// <summary>Artifact entries for the given sub-directory names that exist (and are not links).</summary>
    public static List<ArtifactDir> Artifacts(string directory, string kind, params string[] names)
    {
        var list = new List<ArtifactDir>();
        foreach (var name in names)
        {
            var p = Path.Combine(directory, name);
            if (IsRealDirectory(p))
            {
                list.Add(new ArtifactDir(p, kind, DirectorySize(p)));
            }
        }

        return list;
    }

    /// <summary>Top-level file names of a directory (cheap, no recursion).</summary>
    public static List<string> TopLevelFiles(string directory, string pattern = "*")
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, new EnumerationOptions { IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive })
                .Take(5000)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Top-level sub-directories that are not reparse points.</summary>
    public static List<string> TopLevelDirectories(string directory, string pattern = "*")
    {
        try
        {
            return Directory.EnumerateDirectories(directory, pattern, new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
                MatchCasing = MatchCasing.CaseInsensitive,
            }).Take(2000).ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Newest write time of top-level files (ignores directories such as .git and node_modules).</summary>
    public static DateTimeOffset? NewestTopLevelFileWrite(string directory)
    {
        try
        {
            DateTimeOffset? newest = null;
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var enumerable = new FileSystemEnumerable<DateTimeOffset>(
                directory,
                (ref FileSystemEntry e) => e.LastWriteTimeUtc,
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            };
            foreach (var t in enumerable.Take(5000))
            {
                if (newest is null || t > newest)
                {
                    newest = t;
                }
            }

            return newest;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static CommandSpec Spec(string fileName, string workingDirectory, params string[] args) =>
        new() { FileName = fileName, Arguments = args, WorkingDirectory = workingDirectory };

    public static RunProfile Profile(string id, string label, RunProfileKind kind, string origin, CommandSpec command, bool isDefault = false) =>
        new() { Id = id, Label = label, Kind = kind, Origin = origin, Command = command, IsDefault = isDefault };

    public static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);
}
