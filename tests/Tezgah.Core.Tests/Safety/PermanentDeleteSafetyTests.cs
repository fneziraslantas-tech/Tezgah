using Tezgah.Core.Safety;
using Tezgah.Core.Tests.Execution;

namespace Tezgah.Core.Tests.Safety;

public class PermanentDeleteSafetyTests
{
    private readonly FileRemover _remover = new(PathGuard.Default);

    [Fact]
    public async Task Folder_in_use_is_left_completely_intact()
    {
        using var dir = new TempDir();
        var repo = dir.Sub("busy-repo");
        var head = Path.Combine(dir.Sub("busy-repo", ".git"), "HEAD");
        File.WriteAllText(head, "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        var log = Path.Combine(repo, "server.log");
        File.WriteAllText(log, "x");

        using (new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<IOException>(() => _remover.RemoveDirectoryAsync(repo, toRecycleBin: false, CancellationToken.None));
            Assert.Contains(repo, ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(File.Exists(head), "repository history was deleted");
        Assert.True(File.Exists(Path.Combine(repo, "a.txt")));
        Assert.True(File.Exists(log));
        Assert.Empty(Directory.GetDirectories(dir.Path, ".*tezgah-delete*"));
    }

    [Fact]
    public void Root_that_became_a_link_is_refused()
    {
        using var dir = new TempDir();
        var target = dir.Sub("documents");
        var sentinel = Path.Combine(target, "keep.txt");
        File.WriteAllText(sentinel, "x");
        var link = Path.Combine(dir.Path, "project");
        if (!Junction.TryCreate(link, target))
        {
            return;
        }

        Assert.Throws<IOException>(() => FileRemover.DeleteTreePermanently(link, isRoot: true, CancellationToken.None));
        Assert.True(File.Exists(sentinel));
    }

    [Theory]
    [InlineData(0x9000001Au, false)] // IO_REPARSE_TAG_CLOUD
    [InlineData(0x9000101Au, false)] // CLOUD_1
    [InlineData(0x9000F01Au, false)] // CLOUD_F
    [InlineData(0x80000013u, false)] // DEDUP
    [InlineData(0x80000017u, false)] // WOF
    [InlineData(0xA000000Cu, true)] // SYMLINK
    [InlineData(0xA0000003u, true)] // MOUNT_POINT (junction)
    [InlineData(0xA000001Du, true)] // LX_SYMLINK
    [InlineData(0x8000001Bu, true)] // unknown non-surrogate: fail closed
    public void Only_namespace_redirecting_reparse_tags_are_treated_as_links(uint tag, bool link) =>
        Assert.Equal(link, FileRemover.IsLinkLikeTag(tag));

    [Fact]
    public void Unreadable_tag_is_treated_as_a_link() => Assert.True(FileRemover.IsLinkLikeTag(null));
}
