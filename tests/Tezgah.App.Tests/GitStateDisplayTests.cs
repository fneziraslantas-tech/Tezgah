using Tezgah.App.ViewModels;
using Tezgah.Core.Abstractions;
using Tezgah.Core.Model;

namespace Tezgah.App.Tests;

/// <summary>
/// Untrusted repositories are read without git: their working tree and (often) ahead/behind are unknown, and the detail
/// pane must say so instead of showing "0 modified · 0 untracked" and "↑0 ↓0" as if the repository were clean and in sync.
/// </summary>
public class GitStateDisplayTests
{
    private static ItemViewModel Repo(GitInfo git) => new(
        new CatalogItem { Id = "path:c:\\r", Kind = ItemKind.GitRepository, Source = Sources.FileSystem, Name = "r", Path = @"C:\r", Git = git },
        new UserMeta(), [], false, DateTimeOffset.Now, new KeyStrings());

    [Fact]
    public void Untrusted_state_never_renders_zero_counts_as_fact()
    {
        var vm = Repo(new GitInfo { Branch = "main", HasUpstream = true, StatusUnknown = true, UnpushedCommits = null, AheadBehindUnknown = true, Stashes = 2 });

        Assert.Equal("↑?  ↓?", vm.AheadBehindText);
        Assert.Equal("Git_WorkingTreeUnknown(2)", vm.WorkingTreeText); // the stash count is real
        Assert.DoesNotContain("Git_WorkingTree(", vm.WorkingTreeText, StringComparison.Ordinal);
        Assert.True(vm.IsGitStateUnknown);
        Assert.Equal("Git_StateUnknownHint", vm.GitStateUnknownText);
    }

    [Fact]
    public void Proven_ahead_behind_of_an_untrusted_repo_is_shown()
    {
        var vm = Repo(new GitInfo { Branch = "main", HasUpstream = true, StatusUnknown = true, UnpushedCommits = null, Ahead = 0, Behind = 3 });

        Assert.Equal("↑0  ↓3", vm.AheadBehindText);
        Assert.StartsWith("Git_WorkingTreeUnknown(", vm.WorkingTreeText, StringComparison.Ordinal);
    }

    [Fact]
    public void Trusted_state_shows_the_counts_git_reported()
    {
        var vm = Repo(new GitInfo { Branch = "main", HasUpstream = true, Ahead = 1, Behind = 2, ModifiedFiles = 3, UntrackedFiles = 4, Stashes = 0 });

        Assert.Equal("↑1  ↓2", vm.AheadBehindText);
        Assert.Equal("Git_WorkingTree(3,4,0)", vm.WorkingTreeText);
        Assert.False(vm.IsGitStateUnknown);
        Assert.Equal(string.Empty, vm.GitStateUnknownText);
    }
}
