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

    [Fact]
    public void Subfolder_swapped_for_a_junction_before_it_is_opened_is_unlinked_not_followed()
    {
        using var dir = new TempDir();
        var canary = Path.Combine(dir.Sub("private"), "c.txt");
        File.WriteAllText(canary, "keep");
        var root = dir.Sub("victim", "node_modules");
        var sub = dir.Sub("victim", "node_modules", "sub");
        File.WriteAllText(Path.Combine(sub, "f.txt"), "x");
        var movedAway = Path.Combine(dir.Path, "moved");
        var swapped = false;

        // Between listing node_modules and opening "sub", another user renames "sub" away and puts a junction to a
        // private folder in its place.
        var hooks = new DeleteTreeHooks
        {
            BeforeOpen = path =>
            {
                if (!swapped && path.Equals(sub, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(sub, movedAway);
                    swapped = Junction.TryCreate(sub, Path.GetDirectoryName(canary)!);
                }
            },
        };

        FileRemover.DeleteTreePermanently(root, isRoot: true, hooks, CancellationToken.None);

        Assert.True(File.Exists(canary), "deleted through a swapped-in junction");
        Assert.False(Directory.Exists(root));
        if (swapped)
        {
            Assert.True(File.Exists(Path.Combine(movedAway, "f.txt")), "the junction must be removed as a link only");
        }
    }

    [Fact]
    public void Subfolder_cannot_be_swapped_for_a_junction_after_it_was_checked()
    {
        using var dir = new TempDir();
        var canary = Path.Combine(dir.Sub("private"), "c.txt");
        File.WriteAllText(canary, "keep");
        var root = dir.Sub("victim2", "node_modules");
        var sub = dir.Sub("victim2", "node_modules", "sub");
        File.WriteAllText(Path.Combine(sub, "f.txt"), "x");
        var movedAway = Path.Combine(dir.Path, "moved2");
        var attempted = false;

        // The original race: "sub" passed the link check, then is renamed away and replaced by a junction before its
        // entries are listed. A path-based delete would now list and delete the private folder.
        var hooks = new DeleteTreeHooks
        {
            BeforeList = path =>
            {
                if (attempted || !path.Equals(sub, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                attempted = true;
                try
                {
                    Directory.Move(sub, movedAway);
                    Junction.TryCreate(sub, Path.GetDirectoryName(canary)!);
                }
                catch (IOException)
                {
                    // expected: the folder is held open without delete sharing while it is emptied
                }
            },
        };

        var error = Record.Exception(() => FileRemover.DeleteTreePermanently(root, isRoot: true, hooks, CancellationToken.None));

        Assert.True(attempted);
        Assert.True(File.Exists(canary), "deleted through a swapped-in junction");
        Assert.Null(error);
        Assert.False(Directory.Exists(root));
        Assert.False(Directory.Exists(movedAway), "the folder was renamed while Tezgah was emptying it");
    }

    [Fact]
    public void Deletes_nested_read_only_tree_and_unlinks_junctions_without_following()
    {
        using var dir = new TempDir();
        var outside = dir.Sub("outside");
        var sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "x");
        var root = dir.Sub("tree");
        var obj = Path.Combine(dir.Sub("tree", ".git", "objects", "ab"), "cdef");
        File.WriteAllText(obj, "blob");
        File.SetAttributes(obj, FileAttributes.ReadOnly);
        var deep = dir.Sub("tree", "a", "b", "c");
        File.WriteAllText(Path.Combine(deep, "d.txt"), "x");
        File.SetAttributes(deep, FileAttributes.ReadOnly | FileAttributes.Directory);
        Junction.TryCreate(Path.Combine(root, "a", "link"), outside);

        FileRemover.DeleteTreePermanently(root, isRoot: true, CancellationToken.None);

        Assert.False(Directory.Exists(root));
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
