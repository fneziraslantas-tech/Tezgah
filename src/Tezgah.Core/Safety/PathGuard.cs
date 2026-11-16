using System.Text.RegularExpressions;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Interop;
using Tezgah.Core.Model;

namespace Tezgah.Core.Safety;

/// <summary>
/// The single gate every filesystem action passes through. Design rules:
/// <list type="bullet">
/// <item>Deny by default: anything ambiguous (relative, UNC, device namespace, ADS, trailing dot/space, "..") is refused
/// before normalisation, so Win32 path canonicalisation tricks cannot turn an innocent-looking string into a protected one.</item>
/// <item>Compare canonical forms: 8.3 short names are expanded and the path is opened to ask the OS where it really
/// points (GetFinalPathNameByHandle). Protected-location rules are applied to both forms.</item>
/// <item>Links are never trusted for deletion: any symlink/junction/mount point (any "name surrogate" or unknown reparse tag)
/// in the chain from the drive root to the target refuses the delete.</item>
/// </list>
/// </summary>
internal sealed partial class PathGuard : IPathGuard
{
    private static readonly Lazy<PathGuard> LazyDefault = new(() => new PathGuard(PathGuardOptions.FromEnvironment()));

    private readonly string[] _trees;
    private readonly string[] _folders;
    private readonly string[] _installRoots;
    private readonly string? _profilesRoot;

    public PathGuard(PathGuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _trees = Canonicalize(options.ProtectedTrees);
        _folders = Canonicalize(options.ProtectedFolders);
        _installRoots = Canonicalize(options.InstallRoots);
        _profilesRoot = options.ProfilesRoot is { } root ? Canonicalize([root]).FirstOrDefault() : null;
    }

    /// <summary>
    /// Folders directly under an install root (Program Files, Program Files (x86), ProgramData) that hold many vendors'
    /// products. Nothing in or under them is ever deleted as an installed-app leftover, whatever a registry entry claims.
    /// </summary>
    internal static readonly HashSet<string> SharedInstallFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common Files", "dotnet", "WindowsApps", "ModifiableWindowsApps", "WindowsPowerShell", "Microsoft", "Microsoft SDKs",
        "Microsoft Visual Studio", "Microsoft Office", "Microsoft SQL Server", "Microsoft Analysis Services", "Microsoft.NET",
        "Reference Assemblies", "Windows Kits", "MSBuild", "IIS", "IIS Express", "Internet Explorer", "Package Cache",
        "Packages", "chocolatey", "Uninstall Information", "Application Verifier", "dotnet-core-uninstall",
    };

    /// <summary>Install roots this guard protects (canonical forms).</summary>
    internal IReadOnlyList<string> InstallRoots => _installRoots;

    /// <summary>Guard built from the current environment (used by <see cref="Execution.ShellOpener"/>).</summary>
    public static PathGuard Default => LazyDefault.Value;

    public PathCheck Check(string path, PathOperation operation) => Check(path, operation, DeletePolicy.Default);

    public PathCheck Check(string path, PathOperation operation, DeletePolicy policy)
    {
        if (CheckSyntax(path) is { } syntaxError)
        {
            return PathCheck.Deny(syntaxError);
        }

        string full;
        try
        {
            full = TrimEnd(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PathCheck.Deny(L.T("Geçersiz yol.", "Invalid path."));
        }

        if (!Path.Exists(full))
        {
            return PathCheck.Deny(L.T($"Yol bulunamadı: {full}", $"Path does not exist: {full}"));
        }

        var logical = TrimEnd(Win32.GetLongPath(full) ?? full);
        var final = Win32.GetFinalPath(logical);
        if (final is null)
        {
            return PathCheck.Deny(L.T("Yolun gerçek konumu çözümlenemedi.", "Could not resolve where the path really points."));
        }

        final = TrimEnd(final);
        if (final.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return PathCheck.Deny(L.T("Yol bir ağ konumuna yönleniyor.", "The path resolves to a network location."));
        }

        var linkInChain = FindLinkInChain(logical);
        var escapes = linkInChain is not null || !string.Equals(final, logical, StringComparison.OrdinalIgnoreCase);

        switch (operation)
        {
            case PathOperation.Read:
                return PathCheck.Ok;

            case PathOperation.Execute:
                if (!Directory.Exists(final))
                {
                    return PathCheck.Deny(L.T("Çalışma klasörü bir klasör değil.", "The working directory is not a folder."));
                }

                // A link inside a project is fine to run from, as long as it does not escape into the OS.
                foreach (var candidate in escapes ? new[] { logical, final } : [logical])
                {
                    if (_trees.FirstOrDefault(t => IsSameOrUnder(candidate, t)) is { } tree)
                    {
                        return PathCheck.Deny(L.T($"Sistem klasöründe komut çalıştırılamaz: {tree}", $"Commands cannot run inside a system folder: {tree}"));
                    }
                }

                return PathCheck.Ok;

            case PathOperation.Delete:
                if (linkInChain is not null)
                {
                    return PathCheck.Deny(L.T(
                        $"Yol bir bağlantı (junction/symlink) içeriyor, silinmeyecek: {linkInChain}",
                        $"The path goes through a link (junction/symlink) and will not be deleted: {linkInChain}"));
                }

                if (escapes)
                {
                    return PathCheck.Deny(L.T(
                        $"Yol başka bir konuma yönleniyor ({final}), silinmeyecek.",
                        $"The path resolves to a different location ({final}) and will not be deleted."));
                }

                return CheckProtected(logical, policy) is { } reason ? PathCheck.Deny(reason) : PathCheck.Ok;

            default:
                return PathCheck.Deny(L.T("Bilinmeyen işlem.", "Unknown operation."));
        }
    }

    /// <summary>True when <paramref name="child"/> equals <paramref name="parent"/> or lies below it (case-insensitive, whole segments).</summary>
    public static bool IsSameOrUnder(string child, string parent)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(parent);
        child = TrimEnd(child);
        parent = TrimEnd(parent);
        if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = parent.EndsWith('\\') ? parent : parent + "\\";
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="child"/> lies strictly below <paramref name="parent"/>.</summary>
    public static bool IsStrictlyUnder(string child, string parent) =>
        IsSameOrUnder(child, parent) && !string.Equals(TrimEnd(child), TrimEnd(parent), StringComparison.OrdinalIgnoreCase);

    private static string? CheckSyntax(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return L.T("Yol boş.", "The path is empty.");
        }

        if (path.Any(char.IsControl))
        {
            return L.T("Yol kontrol karakteri içeriyor.", "The path contains control characters.");
        }

        if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            return L.T("Ağ (UNC) ve aygıt yolları (\\\\?\\, \\\\.\\) desteklenmez.", "Network (UNC) and device paths (\\\\?\\, \\\\.\\) are not allowed.");
        }

        if (!DriveAbsolute().IsMatch(path))
        {
            return L.T("Yol tam (sürücü harfiyle başlayan) bir yol olmalı.", "The path must be fully qualified (start with a drive letter).");
        }

        if (path.IndexOf(':', 2) >= 0)
        {
            return L.T("Yol alternatif veri akışı (:) içeriyor.", "The path contains an alternate data stream (:).");
        }

        if (path.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
        {
            return L.T("Yol geçersiz karakter içeriyor.", "The path contains invalid characters.");
        }

        var segments = path[3..].Split('\\', '/');
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                return L.T("Yol '.' veya '..' içeremez.", "The path must not contain '.' or '..' segments.");
            }

            // Win32 silently strips trailing dots/spaces ("C:\Windows." == "C:\Windows"); refuse the ambiguity.
            if (segment.Length > 0 && (segment[^1] == '.' || segment[^1] == ' '))
            {
                return L.T("Yol parçaları nokta veya boşlukla bitemez.", "Path segments must not end with a dot or a space.");
            }
        }

        return null;
    }

    private string? CheckProtected(string path, DeletePolicy policy)
    {
        var segmentCount = path.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
        if (segmentCount <= 1)
        {
            return L.T("Sürücü kökü silinemez.", "A drive root cannot be deleted.");
        }

        foreach (var tree in _trees)
        {
            if (IsSameOrUnder(path, tree) || IsSameOrUnder(tree, path))
            {
                return L.T($"Korunan klasör: {tree}", $"Protected folder: {tree}");
            }
        }

        foreach (var folder in _folders)
        {
            if (IsSameOrUnder(folder, path))
            {
                return L.T($"Korunan klasör (veya onu içeren klasör): {folder}", $"Protected folder (or a folder containing it): {folder}");
            }
        }

        foreach (var root in _installRoots)
        {
            if (IsSameOrUnder(root, path))
            {
                return L.T($"Korunan klasör: {root}", $"Protected folder: {root}");
            }

            if (IsStrictlyUnder(path, root) && !policy.AllowProgramFilesChild)
            {
                return L.T(
                    $"{root} altındaki klasörler yalnızca kurulu uygulama kalıntısı olarak silinebilir.",
                    $"Folders under {root} can only be deleted as installed-app leftovers.");
            }

            if (IsStrictlyUnder(path, root))
            {
                var first = path[(root.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries)[0];
                if (SharedInstallFolders.Contains(first) || first.StartsWith("Windows ", StringComparison.OrdinalIgnoreCase))
                {
                    return L.T(
                        $"Birçok ürünün paylaştığı klasör: {Path.Combine(root, first)}",
                        $"Folder shared by many products: {Path.Combine(root, first)}");
                }
            }
        }

        if (_profilesRoot is not null && string.Equals(Path.GetDirectoryName(path), _profilesRoot, StringComparison.OrdinalIgnoreCase))
        {
            return L.T("Kullanıcı profil klasörleri silinemez.", "User profile folders cannot be deleted.");
        }

        var firstSegment = path.Split('\\', StringSplitOptions.RemoveEmptyEntries)[1];
        if (firstSegment.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("Recovery", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("Boot", StringComparison.OrdinalIgnoreCase))
        {
            return L.T($"Korunan sistem klasörü: {firstSegment}", $"Protected system folder: {firstSegment}");
        }

        if (segmentCount < 3)
        {
            return L.T(
                "Güvenlik için en az iki klasör derinliğindeki yollar silinebilir (ör. C:\\dev\\proje).",
                "For safety only paths at least two folders deep can be deleted (e.g. C:\\dev\\project).");
        }

        return null;
    }

    /// <summary>Returns the first element of the chain (excluding the drive root) that is a link-like reparse point.</summary>
    private static string? FindLinkInChain(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var current = root;
        foreach (var segment in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Win32.GetReparseTag(current) is { } tag && !IsBenignReparseTag(tag))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// Reparse points that do not redirect the namespace: OneDrive/cloud placeholders, dedup and WOF-compressed files.
    /// Everything else (symlinks, junctions, mount points, WSL links, unknown tags) is treated as a link.
    /// </summary>
    private static bool IsBenignReparseTag(uint tag)
    {
        const uint nameSurrogate = 0x20000000;
        if ((tag & nameSurrogate) != 0)
        {
            return false;
        }

        return (tag & 0xFFFF0FFF) == 0x9000001A // IO_REPARSE_TAG_CLOUD_*
            || tag == 0x80000013 // IO_REPARSE_TAG_DEDUP
            || tag == 0x80000017; // IO_REPARSE_TAG_WOF
    }

    private static string[] Canonicalize(IEnumerable<string> paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p))
            {
                continue;
            }

            var full = TrimEnd(Path.GetFullPath(p));
            result.Add(full);
            if (Win32.GetLongPath(full) is { } longPath)
            {
                result.Add(TrimEnd(longPath));
            }

            if (Win32.GetFinalPath(full) is { } final && !final.StartsWith(@"\\", StringComparison.Ordinal))
            {
                result.Add(TrimEnd(final));
            }
        }

        return [.. result];
    }

    private static string TrimEnd(string path) => Path.TrimEndingDirectorySeparator(path);

    private static bool IsSeparator(char c) => c is '\\' or '/';

    [GeneratedRegex(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex DriveAbsolute();
}
