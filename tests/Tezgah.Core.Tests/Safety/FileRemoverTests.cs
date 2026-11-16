using Tezgah.Core.Safety;
using Tezgah.Core.Tests.Execution;

namespace Tezgah.Core.Tests.Safety;

public class FileRemoverTests
{
    private readonly FileRemover _remover = new(PathGuard.Default);

    [Fact]
    public async Task Moves_folder_to_recycle_bin()
    {
        using var dir = new TempDir();
        var target = dir.Sub("recycle-me");
        File.WriteAllText(Path.Combine(target, "a.txt"), "tezgah test file");

        await _remover.RemoveDirectoryAsync(target, toRecycleBin: true, CancellationToken.None);

        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Recycles_git_like_folder_with_read_only_objects()
    {
        using var dir = new TempDir();
        var repo = dir.Sub("recycle-repo");
        var objectFile = Path.Combine(dir.Sub("recycle-repo", ".git", "objects", "ab"), "cdef");
        File.WriteAllText(objectFile, "blob");
        File.SetAttributes(objectFile, FileAttributes.ReadOnly);

        await _remover.RemoveDirectoryAsync(repo, toRecycleBin: true, CancellationToken.None);

        Assert.False(Directory.Exists(repo));
    }

    [Fact]
    public void ClearReadOnly_clears_flags_but_not_through_junctions()
    {
        using var dir = new TempDir();
        var outside = dir.Sub("outside-ro");
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "keep read-only");
        File.SetAttributes(sentinel, FileAttributes.ReadOnly);

        var root = dir.Sub("ro-root");
        var inner = Path.Combine(dir.Sub("ro-root", "sub"), "f.txt");
        File.WriteAllText(inner, "x");
        File.SetAttributes(inner, FileAttributes.ReadOnly);
        var junctionCreated = Junction.TryCreate(Path.Combine(root, "link"), outside);

        Assert.True(FileRemover.ClearReadOnly(root) >= 1);
        Assert.False(File.GetAttributes(inner).HasFlag(FileAttributes.ReadOnly));
        if (junctionCreated)
        {
            Assert.True(File.GetAttributes(sentinel).HasFlag(FileAttributes.ReadOnly), "followed a junction");
        }

        File.SetAttributes(sentinel, FileAttributes.Normal);
    }

    [Fact]
    public async Task Permanent_delete_handles_read_only_files_and_does_not_follow_junctions()
    {
        using var dir = new TempDir();
        var outside = dir.Sub("outside");
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "must survive");

        var repo = dir.Sub("repo");
        var objectFile = Path.Combine(dir.Sub("repo", ".git", "objects", "ab"), "cdef");
        File.WriteAllText(objectFile, "blob");
        File.SetAttributes(objectFile, FileAttributes.ReadOnly);
        File.SetAttributes(Path.Combine(repo, ".git"), FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.ReadOnly);
        var junctionCreated = Junction.TryCreate(Path.Combine(repo, "node_modules_link"), outside);

        await _remover.RemoveDirectoryAsync(repo, toRecycleBin: false, CancellationToken.None);

        Assert.False(Directory.Exists(repo));
        Assert.True(File.Exists(sentinel), "junction target content was deleted");
        if (junctionCreated)
        {
            Assert.True(Directory.Exists(outside));
        }
    }

    [Fact]
    public async Task Refuses_path_through_junction()
    {
        using var dir = new TempDir();
        var real = dir.Sub("real", "inner");
        var link = Path.Combine(dir.Path, "link");
        if (!Junction.TryCreate(link, Path.GetDirectoryName(real)!))
        {
            return;
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _remover.RemoveDirectoryAsync(Path.Combine(link, "inner"), false, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _remover.RemoveDirectoryAsync(link, false, CancellationToken.None));
        Assert.True(Directory.Exists(real));
    }

    [Fact]
    public async Task Refuses_protected_folder_via_injected_guard()
    {
        using var dir = new TempDir();
        var precious = dir.Sub("precious");
        var remover = new FileRemover(new PathGuard(new PathGuardOptions { ProtectedFolders = [precious] }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => remover.RemoveDirectoryAsync(precious, false, CancellationToken.None));
        Assert.True(Directory.Exists(precious));
    }

    [Fact]
    public async Task Removes_single_file_permanently_and_to_recycle_bin()
    {
        using var dir = new TempDir();
        var a = dir.File("a.bin");
        var b = dir.File("b.bin");
        File.SetAttributes(a, FileAttributes.ReadOnly);
        await _remover.RemoveFileAsync(a, toRecycleBin: false, CancellationToken.None);
        await _remover.RemoveFileAsync(b, toRecycleBin: true, CancellationToken.None);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public async Task Missing_folder_throws()
    {
        using var dir = new TempDir();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _remover.RemoveDirectoryAsync(Path.Combine(dir.Path, "nope"), false, CancellationToken.None));
    }
}
