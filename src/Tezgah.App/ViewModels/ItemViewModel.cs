using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tezgah.App.Services;
using Tezgah.Core.Model;

namespace Tezgah.App.ViewModels;

/// <summary>One row of the list and the content of the detail pane.</summary>
public sealed partial class ItemViewModel : ObservableObject
{
    private readonly IStrings _s;
    private DateTimeOffset _now;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private RunSessionViewModel? _selectedRun;

    public ItemViewModel(CatalogItem item, UserMeta meta, IReadOnlyList<RunProfile> profiles, bool forgotten, DateTimeOffset now, IStrings strings)
    {
        ArgumentNullException.ThrowIfNull(item);
        _s = strings;
        Item = item;
        Meta = meta;
        IsForgotten = forgotten;
        _now = now;
        Profiles = BuildProfiles(profiles);
        Runs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRuns));
    }

    public CatalogItem Item { get; private set; }

    public UserMeta Meta { get; private set; }

    public bool IsForgotten { get; private set; }

    public string Id => Item.Id;

    public string Name => Item.Name;

    public string? Path => Item.Path;

    public string? Description => string.IsNullOrWhiteSpace(Item.Description) ? null : Item.Description;

    public ItemKind Kind => Item.Kind;

    /// <summary>Key for the icon converter: ecosystem for projects, kind for the rest.</summary>
    public string IconKey => Item.Kind switch
    {
        ItemKind.InstalledApp => "app",
        ItemKind.PackageTool => "tool",
        _ => Item.ProjectTypes.Count > 0 ? Item.ProjectTypes[0] : Item.Kind == ItemKind.GitRepository ? "git" : "folder",
    };

    public IReadOnlyList<string> Badges => Item.Kind == ItemKind.PackageTool ? [Item.Source] : Item.ProjectTypes;

    public bool IsFavorite => Meta.Favorite;

    public bool IsTrusted => Meta.Trusted;

    public IReadOnlyList<string> Tags => Meta.Tags;

    public bool HasTags => Meta.Tags.Count > 0;

    public string? Note => string.IsNullOrWhiteSpace(Meta.Note) ? null : Meta.Note;

    public string SizeText => Formatting.Bytes(Item.SizeBytes);

    public DateTimeOffset? EffectiveActivity => Meta.LastRunAt is { } r && (Item.LastActivity is null || r > Item.LastActivity) ? r : Item.LastActivity;

    public string ActivityText => Formatting.Relative(EffectiveActivity, _now, _s);

    public string ForgottenText => _s.Format("Item_ForgottenDays", Formatting.DaysSince(EffectiveActivity, _now));

    /// <summary>Short secondary line under the name in the list.</summary>
    public string SubtitleText => Item.Kind switch
    {
        ItemKind.InstalledApp => string.Join(" · ", new[] { Item.Publisher, Item.Version }.Where(x => !string.IsNullOrEmpty(x))),
        ItemKind.PackageTool => string.Join(" · ", new[] { Item.Source, Item.Version }.Where(x => !string.IsNullOrEmpty(x))),
        _ => Item.Path ?? string.Empty,
    };

    public string KindText => _s.Text("Kind_" + Item.Kind);

    // ── git ──
    public bool HasGit => Item.Git is not null;

    public GitInfo? Git => Item.Git;

    /// <summary>Observed unsaved work only: untrusted repositories are read without git, so their working tree is unknown.</summary>
    public bool HasUnsavedWork => Item.Git?.HasKnownUnsavedWork == true;

    public bool IsDirty => Item.Git is { } g && (g.ModifiedFiles > 0 || g.UntrackedFiles > 0 || g.Stashes > 0);

    public bool IsUnpushed => Item.Git is { } g && (g.Ahead > 0 || g.UnpushedCommits > 0 || (g.UnpushedCommits is null && !g.StatusUnknown) || ((!g.HasUpstream || g.UpstreamGone) && g.HasCommits));

    public string AttentionText
    {
        get
        {
            if (Item.Git is not { } g)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (g.Ahead > 0)
            {
                parts.Add(_s.Format("Git_Unpushed", g.Ahead));
            }
            else if (!g.HasUpstream && g.HasCommits)
            {
                parts.Add(_s.Text("Git_NoUpstream"));
            }

            if (IsDirty)
            {
                parts.Add(_s.Text("Git_Dirty"));
            }

            return string.Join(" · ", parts);
        }
    }

    public string BranchText => Item.Git?.Branch ?? string.Empty;

    public string AheadBehindText => Item.Git is { } g ? $"↑{g.Ahead}  ↓{g.Behind}" : string.Empty;

    public string WorkingTreeText => Item.Git is { } g
        ? _s.Format("Git_WorkingTree", g.ModifiedFiles, g.UntrackedFiles, g.Stashes)
        : string.Empty;

    public string? LastCommitText => Item.Git is { LastCommitSubject: { } subj } g
        ? $"“{subj}” · {Formatting.Relative(g.LastCommitAt, _now, _s)}"
        : null;

    public Uri? RemoteUri => Formatting.RemoteToWeb(Item.Git?.RemoteUrl);

    public string? RemoteText => RemoteUri is { } u ? u.Host + u.AbsolutePath : Item.Git?.RemoteUrl;

    public Uri? WebUri => Uri.TryCreate(Item.WebUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u : null;

    /// <summary>Remote for repos, homepage for apps.</summary>
    public Uri? LinkUri => RemoteUri ?? WebUri;

    public string? LinkText => RemoteUri is not null ? RemoteText : WebUri?.Host;

    // ── meta row ──
    public string MetaText
    {
        get
        {
            var parts = new List<string> { _s.Format("Detail_LastActivity", ActivityText) };
            if (Item.SizeBytes is not null)
            {
                parts.Add(SizeText);
            }

            if (!string.IsNullOrEmpty(Item.Version))
            {
                parts.Add("v" + Item.Version.TrimStart('v'));
            }

            if (!string.IsNullOrEmpty(Item.Publisher))
            {
                parts.Add(Item.Publisher);
            }

            if (Item.InstalledAt is { } inst)
            {
                parts.Add(_s.Format("Detail_Installed", inst.ToLocalTime().ToString("d", Formatting.Culture)));
            }

            return string.Join("  ·  ", parts);
        }
    }

    // ── run profiles ──
    public IReadOnlyList<ProfileViewModel> Profiles { get; private set; }

    public bool HasProfiles => Profiles.Count > 0;

    public ProfileViewModel? DefaultProfile => Profiles.FirstOrDefault(p => p.IsDefault) ?? Profiles.FirstOrDefault(p => p.Kind == RunProfileKind.Run) ?? (Profiles.Count > 0 ? Profiles[0] : null);

    public string DefaultCommandText => DefaultProfile?.CommandLine ?? string.Empty;

    public IReadOnlyList<ProfileGroup> ProfileGroups =>
    [
        .. Profiles.GroupBy(p => p.Kind).OrderBy(g => g.Key).Select(g => new ProfileGroup(g.Key, _s.Text("ProfileKind_" + g.Key), [.. g])),
    ];

    public ObservableCollection<RunSessionViewModel> Runs { get; } = [];

    public bool HasRuns => Runs.Count > 0;

    // ── cleanup / uninstall ──
    public bool HasArtifacts => Item.Artifacts.Count > 0;

    public string ArtifactsText => _s.Format("Detail_Artifacts", Formatting.Bytes(Item.Artifacts.Sum(a => a.SizeBytes)));

    public bool CanOpenFolder => !string.IsNullOrEmpty(Item.Path);

    public bool IsProject => Item.Kind is ItemKind.GitRepository or ItemKind.ProjectFolder;

    public void Update(CatalogItem item, UserMeta meta, IReadOnlyList<RunProfile> profiles, bool forgotten, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        _searchHay = null;
        Item = item;
        Meta = meta;
        IsForgotten = forgotten;
        _now = now;
        Profiles = BuildProfiles(profiles);
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Re-reads every computed text (language switch).</summary>
    public void RefreshTexts()
    {
        Profiles = [.. Profiles.Select(p => new ProfileViewModel(p.Profile, _s))];
        OnPropertyChanged(string.Empty);
    }

    /// <summary>All whitespace-separated terms must appear in name/path/description/tags/remote/note/publisher.</summary>
    public bool Matches(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        // Folded once per item (reset in Update), not rebuilt for every item on every keystroke.
        var hay = _searchHay ??= Formatting.SearchFold(string.Join('\n', new[]
        {
            Item.Name, Item.Path, Item.Description, Item.Git?.RemoteUrl, Item.Publisher, Meta.Note, Item.Source,
            string.Join(' ', Meta.Tags), string.Join(' ', Item.ProjectTypes),
        }.Where(x => x is not null)));
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => hay.Contains(Formatting.SearchFold(term), StringComparison.Ordinal));
    }

    private string? _searchHay;

    private List<ProfileViewModel> BuildProfiles(IReadOnlyList<RunProfile> profiles) =>
        [.. (profiles ?? []).Select(p => new ProfileViewModel(p, _s))];
}

public sealed class ProfileViewModel(RunProfile profile, IStrings strings)
{
    public RunProfile Profile { get; } = profile ?? throw new ArgumentNullException(nameof(profile));

    public string Label => Profile.Label;

    public string CommandLine => Profile.Command.Display;

    public RunProfileKind Kind => Profile.Kind;

    public bool IsDefault => Profile.IsDefault;

    public string KindText => strings.Text("ProfileKind_" + Profile.Kind);
}

public sealed record ProfileGroup(RunProfileKind Kind, string Title, IReadOnlyList<ProfileViewModel> Profiles);
