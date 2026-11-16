using Tezgah.Core.Model;

namespace Tezgah.App.Tests;

/// <summary>
/// A terminal opened in a folder runs the user's shell profile and prompt there (git-aware prompts honour the
/// repository's config, direnv-style hooks load its scripts), so untrusted project folders go through the trust prompt.
/// </summary>
public class TerminalTrustGateTests
{
    [Fact]
    public async Task Terminal_in_untrusted_repo_asks_and_opens_nothing_when_declined()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        var item = h.Item("whisper-transcriber");
        h.Vm.SelectedItem = item;
        h.Dialogs.TrustAnswer = false;

        await h.Vm.OpenTerminalCommand.ExecuteAsync(null);

        var prompt = Assert.Single(h.Dialogs.TrustPrompts);
        Assert.Equal("Trust_OpenTerminal", prompt.CommandLine);
        Assert.Equal(item.Path, prompt.Path);
        Assert.Empty(h.Shell.Terminals);
        Assert.False(h.Catalog.GetMeta(item.Id).Trusted);
    }

    [Fact]
    public async Task Terminal_in_untrusted_repo_opens_and_marks_trusted_when_confirmed()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        var item = h.Item("whisper-transcriber");
        h.Vm.SelectedItem = item;
        h.Dialogs.TrustAnswer = true;

        await h.Vm.OpenTerminalCommand.ExecuteAsync(null);

        Assert.Single(h.Dialogs.TrustPrompts);
        Assert.Equal([item.Path!], h.Shell.Terminals);
        Assert.True(h.Catalog.GetMeta(item.Id).Trusted);
    }

    [Fact]
    public async Task Terminal_in_trusted_project_opens_without_prompt()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        var item = h.Vm.AllItems.First(i => i.IsTrusted && i.Kind is ItemKind.GitRepository or ItemKind.ProjectFolder && i.Path is not null);
        h.Vm.SelectedItem = item;

        await h.Vm.OpenTerminalCommand.ExecuteAsync(null);

        Assert.Empty(h.Dialogs.TrustPrompts);
        Assert.Equal([item.Path!], h.Shell.Terminals);
    }

    [Fact]
    public async Task Terminal_in_installed_app_folder_has_no_project_trust_prompt()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        var app = h.Vm.AllItems.First(i => i.Kind == ItemKind.InstalledApp && i.Path is not null);
        h.Vm.SelectedItem = app;

        await h.Vm.OpenTerminalCommand.ExecuteAsync(null);

        Assert.Empty(h.Dialogs.TrustPrompts);
        Assert.Equal([app.Path!], h.Shell.Terminals);
    }

    [Fact]
    public async Task VsCode_relies_on_its_own_workspace_trust_and_does_not_prompt()
    {
        var h = new Harness();
        await h.Vm.InitializeAsync();
        var item = h.Item("whisper-transcriber");
        h.Vm.SelectedItem = item;

        h.Vm.OpenInVsCodeCommand.Execute(null);

        Assert.Empty(h.Dialogs.TrustPrompts);
        Assert.Equal([item.Path!], h.Shell.VsCodeFolders);
        Assert.False(h.Catalog.GetMeta(item.Id).Trusted);
    }
}
