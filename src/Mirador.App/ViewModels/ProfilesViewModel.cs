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

/// <summary>A headset choice in the profile editor's Windows tools; a null id is "None".</summary>
/// <param name="WindowsTools">What its module does on Windows around Monado.</param>
public sealed record HeadsetItem(string? Id, string Name, string WindowsTools = "")
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
            Headsets.Insert(Headsets.Count - 1, new HeadsetItem(module.Id, module.DisplayName, module.WindowsTools));
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

    /// <summary>The picker's items: the profiles, then <see cref="AddItem"/>.</summary>
    public ObservableCollection<ProfileItem> Profiles { get; } = [];

    /// <summary>The picker's last item, which adds a profile instead of picking one.</summary>
    public static readonly ProfileItem AddItem = new("", "Add a profile…");

    /// <summary>The picker card's description.</summary>
    public string PickerDescription => IsDirty ? "Save or revert your changes to pick another." : "The profile to change. Home picks the one Monado starts with.";

    /// <summary>
    /// Whether the picked profile can be deleted: not with unsaved changes, not the last one, not the one Monado runs
    /// with now, and not while its branch is being built (the build writes into the folder the delete removes).
    /// </summary>
    public bool CanDelete =>
        !IsDirty
        && Selected is { } selected && selected != AddItem
        && Profiles.Count(p => p != AddItem) > 1
        && host.Session.CurrentProfile?.Profile.Id != selected.Id
        && !IsBuilding(selected.Id);

    private bool IsBuilding(string profileId) => host.Builds.RunOf(profileId) is { Outcome: null, Cancelled: false };

    /// <summary>The build card of a branch profile.</summary>
    public BuildViewModel Build { get; }

    /// <summary>"Verify build for Windows".</summary>
    public VerifyViewModel Verify { get; }

    /// <summary>The card of Mirador's Monado: installed, newest, Update, Roll back.</summary>
    public MiradorMonadoViewModel MiradorMonado { get; }

    public ObservableCollection<HeadsetItem> Headsets { get; } = [];

    public ObservableCollection<EnvRow> Extra { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    public partial ProfileItem? Selected { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadsetDescription))]
    public partial HeadsetItem? Headset { get; set; }

    /// <summary>
    /// The "Monado" drop-down (Mirador.md §4.11 item 55): 0 Mirador's Monado, 1 build upstream main, 2 build from a
    /// link, 3 a build folder.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonadoFromFolder), nameof(MonadoFromLink), nameof(RunAsAdministratorDescription))]
    public partial int MonadoSource { get; set; }

    /// <summary>The pasted link of a branch or merge request (or the repository's URL).</summary>
    [ObservableProperty]
    public partial string MonadoLink { get; set; } = "";

    /// <summary>The branch or ref to build; filled in from <see cref="MonadoLink"/>, and changeable.</summary>
    [ObservableProperty]
    public partial string MonadoBranch { get; set; } = "";

    /// <summary>
    /// The profile's "Run as administrator" (Mirador.md §4.10 item 44, §4.11 item 51): for Mirador's Monado the user's
    /// consent (one UAC prompt, at Save) is enough; a branch or build folder also needs its build approved.
    /// </summary>
    [ObservableProperty]
    public partial bool RunAsAdministrator { get; set; }

    /// <summary>False for a standard account, which has no administrator token to run Monado with.</summary>
    public bool CanRunAsAdministrator { get; } = !SafeIsStandardAccount();

    /// <summary>The toggle's description, for the Monado the profile runs.</summary>
    public string RunAsAdministratorDescription
    {
        get
        {
            var text = MonadoSource == MiradorSource
                ? "Gives Monado the GPU's realtime priority (with hardware-accelerated GPU scheduling on). Games still run as you. Windows asks once, when you save. Programs running as you could use it to get administrator rights."
                : "Only turn this on if you trust this Monado branch: it gets full control of your PC while it runs. Approving copies the build where only administrators can change it; each new build runs as you until you approve it again. Monado as administrator also needs the elevation fix, which upstream doesn't have yet.";
            return CanRunAsAdministrator ? text : $"{text} Needs an administrator account.";
        }
    }

    private static bool SafeIsStandardAccount()
    {
        try
        {
            return ElevatedMonado.IsStandardAccount();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

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
    [NotifyPropertyChangedFor(nameof(CanSwitch), nameof(CanDelete), nameof(PickerDescription))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand), nameof(ApproveCommand))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool HasProblems { get; set; }

    /// <summary>The problems, one per line, for the page's message.</summary>
    public string ProblemsText => string.Join(Environment.NewLine, Problems);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    /// <summary>The Windows tools card's description: what the picked headset's module does around Monado.</summary>
    public string HeadsetDescription => Headset?.WindowsTools is { Length: > 0 } tools
        ? tools
        : "Mirador's steps for a headset around Monado. With None, Monado runs on its own.";

    public bool MonadoFromFolder => MonadoSource == FolderSource;

    public bool MonadoFromLink => MonadoSource == LinkSource;

    private const int MiradorSource = 0;
    private const int UpstreamSource = 1;
    private const int LinkSource = 2;
    private const int FolderSource = 3;

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
        host.Builds.Changed += OnBuildsChanged;
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
        host.Builds.Changed -= OnBuildsChanged;
        Build.Detach();
        Verify.Detach();
    }

    // A build started or ended: Delete follows it.
    private void OnBuildsChanged(string profileId) => App.Current.Ui.TryEnqueue(() => OnPropertyChanged(nameof(CanDelete)));

    /// <summary>A name no profile has yet: <paramref name="wanted"/>, else with 2, 3, … after it.</summary>
    public string FreeName(string wanted)
    {
        var names = Profiles.Where(p => p != AddItem).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Deletes the selected profile; the page asked first. The consent goes with the last profile that needed it.</summary>
    public async Task DeleteAsync()
    {
        var id = Selected!.Id;
        if (IsBuilding(id))
        {
            Message = "Its Monado is being built: cancel the build, then delete the profile.";
            return;
        }

        if (Change(set => set.Delete(id), keep: null))
        {
            Message = await TakeBackUnneededAsync();

            // What was the profile's alone goes with it: its branch's source, build and install (often gigabytes),
            // and its Verify result.
            _ = Task.Run(() => RemoveLeftovers(id));
        }
    }

    private void RemoveLeftovers(string profileId)
    {
        try
        {
            if (host.Locator.BuildsRoot is not null && host.Locator.BranchDir(profileId) is var dir && Directory.Exists(dir))
            {
                // Git keeps its objects read-only, which a recursive delete refuses: clear that first. Links in the
                // tree aren't followed (the enumeration doesn't recurse into them), and a delete removes the link only.
                foreach (var file in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }

            File.Delete(host.VerifyResultFile(profileId));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Write($"Couldn't remove what was left of a deleted profile's build ({profileId})", e);
        }
    }

    /// <summary>"Add a profile…" was picked: adds one and picks it, or picks the one before again when adding failed.</summary>
    private async Task AddFromPickerAsync()
    {
        var before = editing?.Id;
        if (!await AddAsync())
        {
            LoadList(before);
        }
    }

    private async Task<bool> AddAsync()
    {
        // New profiles start with no headset module, unless a headset a module knows is plugged in (Mirador.md §4.11 item 53).
        // Listing the USB devices is a WMI query (up to seconds): off the UI thread.
        string? module = null;
        try
        {
            var present = await Task.Run(() => host.Usb.Present());
            module = host.Modules.Values.FirstOrDefault(m => m.IsPresent(present))?.Id;
        }
        catch (Exception e)
        {
            // The devices can't be listed: no module.
            AppLog.Write("Couldn't list the USB devices for a new profile", e);
        }

        var profile = new Profile(Profile.NewId(), FreeName("New profile"), module, BuildSource.Mirador, [], new Dictionary<string, string>());
        return Change(set => set.Add(profile), profile.Id);
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is not { } selected || selected == AddItem)
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
    private async Task SaveAsync()
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

        // "Run as administrator" needs the user's consent: Windows asks once, here. Without it the profile runs as you.
        string? problem = null;
        // Only the installed copy asks: the helper starts Monado as administrator for it alone, so a consent recorded
        // from another copy would buy nothing (Home says why this copy runs as you).
        if (profile.RunAsAdministrator && !host.ElevatedMonadoAllowed() && host.IsInstalledCopy(askHelper: false))
        {
            var code = await ElevatedMonado.AllowAsync();
            if (code == 0)
            {
                // The consent step recorded SteamVR's Lighthouse files as they are now.
                host.SteamVrAllowedAgain();
            }
            else
            {
                profile = profile with { RunAsAdministrator = false };
                problem = code == ElevatedRuntime.PromptCancelled
                    ? "Saved to run as you: Windows' administrator prompt was cancelled."
                    : "Saved to run as you: Mirador couldn't record the consent for Monado to run as administrator. The reason is in mirador.log.";
            }
        }

        // Saved or not, a consent no profile uses goes (the prompt may have recorded one for a save that then failed), and
        // so does an approved build the profile no longer runs as administrator.
        var saved = Change(set => set.Replace(profile), profile.Id);
        var takeBack = await TakeBackUnneededAsync();
        if (saved)
        {
            Message = problem ?? takeBack;
        }
    }

    /// <summary>
    /// Takes back the consent no profile uses and the approvals no profile runs as administrator any more, each whether
    /// or not the other worked; what went wrong, or null.
    /// </summary>
    private async Task<string?> TakeBackUnneededAsync()
    {
        var consent = await host.TakeBackUnneededConsentAsync();
        var approvals = await host.ForgetUnneededApprovalsAsync();
        return consent is null ? approvals : approvals is null ? consent : $"{consent} {approvals}";
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

        if (value == AddItem)
        {
            // After the drop-down has closed: adding reloads the list it shows.
            App.Current.Ui.TryEnqueue(() => _ = AddFromPickerAsync());
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

    /// <summary>
    /// "Approve": one UAC prompt copies the saved profile's build where only administrators can change it. Greyed out
    /// until the profile is saved: the build approved is the saved profile's.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApproveNow))]
    private async Task ApproveAsync()
    {
        if (editing is null)
        {
            return;
        }

        ApprovalStatus = "Approving…";
        var problem = await host.ApproveBuildAsync(editing);
        RefreshApproval(problem);
    }

    private bool CanApproveNow() => editing is not null && !IsDirty;

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

            Profiles.Add(AddItem);
            Selected = Profiles.FirstOrDefault(p => p != AddItem && p.Id == keep) ?? Profiles.FirstOrDefault(p => p != AddItem && p.Id == set.SelectedId);
        }
        finally
        {
            loading = false;
        }

        OnPropertyChanged(nameof(CanDelete));

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
        ApproveCommand.NotifyCanExecuteChanged();
        Verify.Show(p);
        Verify.Unsaved = false;
        RefreshApproval();
        MiradorMonado.Show(MonadoSource == MiradorSource);
        Build.Show(p);
        Build.Unsaved = false;
        Validate(Edited());
        OnPropertyChanged(nameof(UsesBaseStations));
    }

    /// <summary>The profile as the editor shows it; the module's options, which the editor doesn't show, are kept.</summary>
    private Profile Edited()
    {
        var options = new Dictionary<string, string>(editing?.ModuleOptions ?? new Dictionary<string, string>());
        return new Profile(
            editing?.Id ?? "",
            Name.Trim(),
            Headset?.Id,
            EditedSource(),
            [.. Extra.Select(r => new EnvVar(r.Name.Trim(), r.Value))],
            options,
            lighthouse,
            RunAsAdministrator && CanRunAsAdministrator);
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
        var problems = ProfileValidation.Problems(p, host.Locator, _ => null).ToList();
        if (LinkProblem() is { } linkProblem)
        {
            // Instead of the URL's own problem: the link is what the owner typed.
            problems.RemoveAll(x => x.Contains(MonadoLink.Trim(), StringComparison.Ordinal) && MonadoLink.Trim().Length > 0);
            problems.Insert(0, linkProblem);
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

    private void OnSessionChanged() =>
        App.Current.Ui.TryEnqueue(() => MiradorMonado.MonadoRuns = Build.MonadoRuns = Verify.MonadoRuns = IsRunning = host.Session.State != SessionState.Stopped);
}
