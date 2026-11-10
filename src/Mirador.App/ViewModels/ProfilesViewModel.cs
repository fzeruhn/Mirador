using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.DirectMode;
using Mirador.Core.Profiles;
using Mirador.Core.Session;
using Mirador.Pimax;

namespace Mirador.App.ViewModels;

/// <summary>A headset choice in the profile editor; a null id is "None".</summary>
public sealed record HeadsetItem(string? Id, string Name, bool UsesNvidiaDirectMode = false)
{
    public override string ToString() => Name;
}

/// <summary>One extra environment variable in the editor.</summary>
public sealed partial class EnvRow : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Value { get; set; } = "";
}

/// <summary>
/// The Profiles page: the list (add, duplicate, rename, delete) and the editor of the selected profile, which saves
/// only a profile without problems. While the editor holds unsaved changes the list is locked, so nothing is lost.
/// </summary>
public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly AppHost host;
    private Profile? editing;
    private bool loading;

    /// <summary>The profile's "Lighthouse base stations" as the user set it; null while untouched (it follows the profile).</summary>
    private bool? lighthouse;

    public ProfilesViewModel(AppHost host)
    {
        this.host = host;
        Build = new BuildViewModel(host, App.Current.Ui);
        Verify = new VerifyViewModel(host, App.Current.Ui);
        Verify.Changed += () => RefreshApproval();
        MiradorMonado = new MiradorMonadoViewModel(host);
        Headsets.Add(new HeadsetItem(null, "None"));
        foreach (var module in host.Modules.Values)
        {
            Headsets.Insert(Headsets.Count - 1, new HeadsetItem(module.Id, module.DisplayName, module.UsesNvidiaDirectMode));
        }

        Extra.CollectionChanged += (_, e) =>
        {
            foreach (EnvRow row in e.NewItems ?? Array.Empty<EnvRow>())
            {
                row.PropertyChanged += OnRowChanged;
            }

            foreach (EnvRow row in e.OldItems ?? Array.Empty<EnvRow>())
            {
                row.PropertyChanged -= OnRowChanged;
            }

            Changed();
        };
    }

    public ObservableCollection<ProfileItem> Profiles { get; } = [];

    /// <summary>The build card of a branch profile.</summary>
    public BuildViewModel Build { get; }

    /// <summary>"Verify build for Windows" (Mirador_Plan.md Task 63).</summary>
    public VerifyViewModel Verify { get; }

    /// <summary>The card of Mirador's Monado: installed, newest, Update, Roll back.</summary>
    public MiradorMonadoViewModel MiradorMonado { get; }

    public ObservableCollection<HeadsetItem> Headsets { get; } = [];

    public ObservableCollection<EnvRow> Extra { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty]
    public partial ProfileItem? Selected { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesDirectMode))]
    public partial HeadsetItem? Headset { get; set; }

    /// <summary>
    /// The "Monado" drop-down (Mirador.md §4.11 item 55): 0 Mirador's Monado, 1 build upstream main, 2 build from a
    /// link, 3 a build folder.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonadoFromFolder), nameof(MonadoFromLink))]
    public partial int MonadoSource { get; set; }

    /// <summary>The pasted link of a branch or merge request (or the repository's URL).</summary>
    [ObservableProperty]
    public partial string MonadoLink { get; set; } = "";

    /// <summary>The branch or ref to build; filled in from <see cref="MonadoLink"/>, and changeable.</summary>
    [ObservableProperty]
    public partial string MonadoBranch { get; set; } = "";

    /// <summary>"Run as administrator" for a branch or build folder (Mirador.md §4.11 item 51).</summary>
    [ObservableProperty]
    public partial bool RunAsAdministrator { get; set; }

    /// <summary>Whether the approval card shows: the saved profile builds a branch or uses a build folder.</summary>
    [ObservableProperty]
    public partial bool CanApprove { get; set; }

    /// <summary>The approval's state: approved, a newer build waiting, or none.</summary>
    [ObservableProperty]
    public partial string ApprovalStatus { get; set; } = "";

    /// <summary>Extra CMake options for a branch, one per line or separated by spaces.</summary>
    [ObservableProperty]
    public partial string MonadoOptions { get; set; } = "";

    [ObservableProperty]
    public partial string MonadoFolder { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LayerBundled), nameof(LayerSource))]
    public partial bool LayerFromFolder { get; set; }

    [ObservableProperty]
    public partial string LayerFolder { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool HasProblems { get; set; }

    /// <summary>The problems, one per line, for the page's message.</summary>
    public string ProblemsText => string.Join(Environment.NewLine, Problems);

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    /// <summary>Whether the profile's headset uses NVIDIA direct mode: the layer's cards show.</summary>
    public bool UsesDirectMode => Headset?.UsesNvidiaDirectMode == true;

    public bool MonadoFromFolder => MonadoSource == FolderSource;

    public bool MonadoFromLink => MonadoSource == LinkSource;

    private const int MiradorSource = 0;
    private const int UpstreamSource = 1;
    private const int LinkSource = 2;
    private const int FolderSource = 3;

    public bool LayerBundled
    {
        get => !LayerFromFolder;
        set => LayerFromFolder = !value;
    }

    /// <summary>The "Vulkan layer" drop-down: 0 the one that comes with Mirador, 1 a layer folder.</summary>
    public int LayerSource
    {
        get => LayerFromFolder ? 1 : 0;
        set => LayerFromFolder = value == 1;
    }

    /// <summary>
    /// The "Lighthouse base stations" toggle: the user's choice, else whether the headset and the variables being edited
    /// use the base stations (so it follows them until it is flipped). Flipping it makes the choice the user's.
    /// </summary>
    public bool UsesBaseStations
    {
        get => lighthouse ?? Edited().UsesBaseStations(HeadsetUsesLighthouse());
        set
        {
            if (value == UsesBaseStations)
            {
                return;
            }

            lighthouse = value;
            Changed();
        }
    }

    /// <summary>Whether another profile may be picked: not while the editor holds unsaved changes.</summary>
    public bool CanSwitch => !IsDirty;

    public bool HasMessage => Message is not null;

    /// <summary>The message bar was closed.</summary>
    public void DismissMessage() => Message = null;

    public void Attach()
    {
        host.Session.Changed += OnSessionChanged;
        Build.Attach();
        Verify.Attach();
        IsRunning = host.Session.State != SessionState.Stopped;
        Build.MonadoRuns = IsRunning;
        Verify.MonadoRuns = IsRunning;
        MiradorMonado.MonadoRuns = IsRunning;
        if (!IsDirty)
        {
            LoadList(keep: Selected?.Id); // unsaved edits stay as they were left
        }
    }

    public void Detach()
    {
        host.Session.Changed -= OnSessionChanged;
        Build.Detach();
        Verify.Detach();
    }

    /// <summary>A name no profile has yet: <paramref name="wanted"/>, else with 2, 3, … after it.</summary>
    public string FreeName(string wanted)
    {
        var names = Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(wanted))
        {
            return wanted;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{wanted} {i}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Renames the selected profile; the page asked for the name.</summary>
    public void Rename(string name) => Change(set => set.Rename(Selected!.Id, name), Selected?.Id);

    /// <summary>Deletes the selected profile; the page asked first.</summary>
    public void Delete() => Change(set => set.Delete(Selected!.Id), keep: null);

    [RelayCommand]
    private void Add()
    {
        // New profiles start with no headset module, unless a headset a module knows is plugged in (Mirador.md §4.11 item 53).
        string? module = null;
        try
        {
            var present = host.Usb.Present();
            module = host.Modules.Values.FirstOrDefault(m => m.IsPresent(present))?.Id;
        }
        catch (MiradorException)
        {
            // The devices can't be listed: no module.
        }

        var profile = new Profile(Profile.NewId(), FreeName("New profile"), module, BuildSource.Mirador, [], new Dictionary<string, string>());
        Change(set => set.Add(profile), profile.Id);
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        string? added = null;
        Change(
            set =>
            {
                var next = set.Duplicate(selected.Id, FreeName($"{selected.Name} copy"));
                added = next.Profiles[^1].Id;
                return next;
            },
            keep: null);
        LoadList(added);
    }

    [RelayCommand]
    private void AddVariable() => Extra.Add(new EnvRow());

    [RelayCommand]
    private void RemoveVariable(EnvRow row) => Extra.Remove(row);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (editing is null)
        {
            return;
        }

        var profile = Edited();
        if (!Validate(profile))
        {
            return;
        }

        if (Change(set => set.Replace(profile), profile.Id))
        {
            Message = null;
        }
    }

    private bool CanSave() => IsDirty && !HasProblems;

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Revert() => Show(editing);

    partial void OnSelectedChanged(ProfileItem? value)
    {
        if (loading)
        {
            return;
        }

        try
        {
            Show(host.Profiles.Load().Profiles.FirstOrDefault(p => p.Id == value?.Id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Mirador couldn't read its profiles ({e.Message.Trim().TrimEnd('.')}).";
        }
    }

    partial void OnNameChanged(string value) => Changed();

    partial void OnHeadsetChanged(HeadsetItem? value) => Changed();

    partial void OnMonadoSourceChanged(int value)
    {
        MiradorMonado.Show(value == MiradorSource);
        Changed();
    }

    /// <summary>A pasted link fills in its branch (a merge request's ref), which the owner can still change.</summary>
    partial void OnMonadoLinkChanged(string value)
    {
        if (!loading)
        {
            try
            {
                MonadoBranch = BranchLink.Parse(value).Ref;
            }
            catch (MiradorException)
            {
                // Validate says why.
            }
        }

        Changed();
    }

    partial void OnMonadoBranchChanged(string value) => Changed();

    partial void OnRunAsAdministratorChanged(bool value) => Changed();

    /// <summary>"Approve": one UAC prompt copies the saved profile's build where only administrators can change it.</summary>
    [RelayCommand]
    private async Task ApproveAsync()
    {
        if (editing is null || IsDirty)
        {
            ApprovalStatus = "Save the profile first.";
            return;
        }

        ApprovalStatus = "Approving…";
        var problem = await host.ApproveBuildAsync(editing);
        RefreshApproval(problem);
    }

    private void RefreshApproval(string? problem = null)
    {
        CanApprove = editing?.Monado.Kind is SourceKind.Branch or SourceKind.Folder;
        if (!CanApprove || editing is null)
        {
            return;
        }

        var state = !editing.RunAsAdministrator
            ? "Off: this build runs as you."
            : host.ApprovedBuildCurrent(editing)
                ? "Approved: this build runs as administrator and can be the OpenXR runtime."
                : "The new build runs as you until you approve it.";
        if (Verify.ElevationNote is { } verified)
        {
            state += " " + verified;
        }

        ApprovalStatus = problem is null ? state : $"{problem} {state}";
    }

    partial void OnMonadoOptionsChanged(string value) => Changed();

    partial void OnMonadoFolderChanged(string value) => Changed();

    partial void OnLayerFromFolderChanged(bool value) => Changed();

    partial void OnLayerFolderChanged(string value) => Changed();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Changed();

    /// <summary>Applies <paramref name="change"/> to the saved profiles and saves them; a refusal is shown, not thrown.</summary>
    private bool Change(Func<ProfileSet, ProfileSet> change, string? keep)
    {
        Message = null;
        try
        {
            var set = change(host.Profiles.Load());
            host.Profiles.Save(set);
            LoadList(keep ?? Selected?.Id);
            host.NotifyProfilesChanged();
            return true;
        }
        catch (MiradorException e)
        {
            Message = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Mirador couldn't save its profiles ({e.Message.Trim().TrimEnd('.')}).";
        }

        return false;
    }

    private void LoadList(string? keep)
    {
        ProfileSet set;
        try
        {
            set = host.Profiles.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Mirador couldn't read its profiles ({e.Message.Trim().TrimEnd('.')}).";
            return;
        }

        loading = true;
        try
        {
            Profiles.Clear();
            foreach (var p in set.Profiles)
            {
                Profiles.Add(new ProfileItem(p.Id, p.Name));
            }

            Selected = Profiles.FirstOrDefault(p => p.Id == keep) ?? Profiles.FirstOrDefault(p => p.Id == set.SelectedId);
        }
        finally
        {
            loading = false;
        }

        Show(set.Profiles.FirstOrDefault(p => p.Id == Selected?.Id));
    }

    /// <summary>Puts <paramref name="p"/> into the editor, with no unsaved changes.</summary>
    private void Show(Profile? p)
    {
        editing = p;
        lighthouse = p?.Lighthouse;
        loading = true;
        try
        {
            Name = p?.Name ?? "";
            Headset = Headsets.FirstOrDefault(h => h.Id == p?.ModuleId) ?? Headsets[^1];
            MonadoSource = p?.Monado switch
            {
                { Kind: SourceKind.Folder } => FolderSource,
                { IsUpstreamMain: true } => UpstreamSource,
                { Kind: SourceKind.Branch } => LinkSource,
                _ => MiradorSource,
            };
            MonadoFolder = p?.Monado.Folder ?? "";
            RunAsAdministrator = p?.RunAsAdministrator ?? false;
            MonadoLink = p?.Monado.Kind == SourceKind.Branch && !p.Monado.IsUpstreamMain ? p.Monado.Url ?? "" : "";
            MonadoBranch = MonadoLink.Length > 0 ? p!.Monado.Ref ?? "" : "";
            MonadoOptions = MonadoLink.Length > 0 ? string.Join(Environment.NewLine, p!.Monado.Options) : "";
            var layer = p is null ? null : DirectModeLayer.FolderOf(p);
            LayerFromFolder = layer is not null;
            LayerFolder = layer ?? "";
            Extra.Clear();
            foreach (var v in p?.ExtraEnvironment ?? [])
            {
                Extra.Add(new EnvRow { Name = v.Name, Value = v.Value });
            }
        }
        finally
        {
            loading = false;
        }

        IsDirty = false;
        Verify.Show(p);
        Verify.Unsaved = false;
        RefreshApproval();
        MiradorMonado.Show(MonadoSource == MiradorSource);
        Build.Show(p);
        Build.Unsaved = false;
        Validate(Edited());
        OnPropertyChanged(nameof(UsesBaseStations));
    }

    /// <summary>The profile as the editor shows it; options of the module it doesn't touch are kept.</summary>
    private Profile Edited()
    {
        var options = new Dictionary<string, string>(editing?.ModuleOptions ?? new Dictionary<string, string>());
        if (UsesDirectMode && LayerFromFolder)
        {
            options[DirectModeLayer.OptionKey] = LayerFolder.Trim();
        }
        else
        {
            options.Remove(DirectModeLayer.OptionKey);
        }

        return new Profile(
            editing?.Id ?? "",
            Name.Trim(),
            Headset?.Id,
            EditedSource(),
            [.. Extra.Select(r => new EnvVar(r.Name.Trim(), r.Value))],
            options,
            lighthouse,
            RunAsAdministrator && MonadoSource is LinkSource or UpstreamSource or FolderSource);
    }

    /// <summary>
    /// The Monado the editor shows. A link that can't be read is kept as typed, so that validation names the problem
    /// (<see cref="LinkProblem"/>).
    /// </summary>
    private BuildSource EditedSource() => MonadoSource switch
    {
        FolderSource => new BuildSource(SourceKind.Folder, MonadoFolder.Trim()),
        UpstreamSource => BuildSource.UpstreamMain,
        LinkSource => BuildSource.FromBranch(
            LinkUrl() ?? MonadoLink.Trim(), MonadoBranch.Trim(), BranchLink.SplitOptions(MonadoOptions)),
        _ => BuildSource.Mirador,
    };

    private string? LinkUrl()
    {
        try
        {
            return BranchLink.Parse(MonadoLink).Url;
        }
        catch (MiradorException)
        {
            return null;
        }
    }

    /// <summary>Why the pasted link can't be built from, or null.</summary>
    private string? LinkProblem()
    {
        if (MonadoSource != LinkSource)
        {
            return null;
        }

        try
        {
            BranchLink.Parse(MonadoLink);
            return null;
        }
        catch (MiradorException e)
        {
            return e.Message;
        }
    }

    /// <summary>Whether the headset being edited tracks with Lighthouse (its module says so).</summary>
    private bool HeadsetUsesLighthouse() =>
        Headset?.Id is { } id && host.Modules.TryGetValue(id, out var module) && module.UsesSteamVrLighthouse;

    private void Changed()
    {
        if (loading || editing is null)
        {
            return;
        }

        IsDirty = true;
        Build.Unsaved = true;
        Verify.Unsaved = true;
        Validate(Edited());

        // While untouched, the toggle follows the headset and the variables.
        OnPropertyChanged(nameof(UsesBaseStations));
    }

    private bool Validate(Profile p)
    {
        var problems = ProfileValidation.Problems(p, host.Locator, LayerProblem).ToList();
        if (LinkProblem() is { } linkProblem)
        {
            // Instead of the URL's own problem: the link is what the owner typed.
            problems.RemoveAll(x => x.Contains(MonadoLink.Trim(), StringComparison.Ordinal) && MonadoLink.Trim().Length > 0);
            problems.Insert(0, linkProblem);
        }

        if (UsesDirectMode && LayerFromFolder && string.IsNullOrWhiteSpace(LayerFolder))
        {
            problems.Add("Pick the layer folder, or choose the layer that comes with Mirador.");
        }

        Problems.Clear();
        foreach (var problem in problems)
        {
            Problems.Add(problem);
        }

        HasProblems = problems.Count > 0;
        OnPropertyChanged(nameof(ProblemsText));
        return !HasProblems;
    }

    /// <summary>The Dream Air layer folder's problem; the layer that comes with Mirador is checked when Monado starts.</summary>
    private string? LayerProblem(Profile p)
    {
        if (p.ModuleId != PimaxModule.ModuleId || DirectModeLayer.FolderOf(p) is not { } folder)
        {
            return null;
        }

        try
        {
            DirectModeLayer.Locate(folder, Path.Combine(host.AppDir, "layer"));
            return null;
        }
        catch (MiradorException e)
        {
            return e.Message;
        }
    }

    private void OnSessionChanged() =>
        App.Current.Ui.TryEnqueue(() => MiradorMonado.MonadoRuns = Build.MonadoRuns = Verify.MonadoRuns = IsRunning = host.Session.State != SessionState.Stopped);
}
