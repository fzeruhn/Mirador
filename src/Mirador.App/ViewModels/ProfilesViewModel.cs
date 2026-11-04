using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.Profiles;
using Mirador.Core.Session;
using Mirador.DreamAir;

namespace Mirador.App.ViewModels;

/// <summary>A headset choice in the profile editor; a null id is "None".</summary>
public sealed record HeadsetItem(string? Id, string Name)
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

    public ProfilesViewModel(AppHost host)
    {
        this.host = host;
        Headsets.Add(new HeadsetItem(null, "None"));
        foreach (var module in host.Modules.Values)
        {
            Headsets.Insert(Headsets.Count - 1, new HeadsetItem(module.Id, module.DisplayName));
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

    public ObservableCollection<HeadsetItem> Headsets { get; } = [];

    public ObservableCollection<EnvRow> Extra { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty]
    public partial ProfileItem? Selected { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDreamAir))]
    public partial HeadsetItem? Headset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonadoBundled))]
    public partial bool MonadoFromFolder { get; set; }

    [ObservableProperty]
    public partial string MonadoFolder { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LayerBundled))]
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

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    public bool IsDreamAir => Headset?.Id == DreamAirModule.ModuleId;

    public bool MonadoBundled
    {
        get => !MonadoFromFolder;
        set => MonadoFromFolder = !value;
    }

    public bool LayerBundled
    {
        get => !LayerFromFolder;
        set => LayerFromFolder = !value;
    }

    /// <summary>Whether another profile may be picked: not while the editor holds unsaved changes.</summary>
    public bool CanSwitch => !IsDirty;

    public bool HasMessage => Message is not null;

    /// <summary>The message bar was closed.</summary>
    public void DismissMessage() => Message = null;

    public void Attach()
    {
        host.Session.Changed += OnSessionChanged;
        IsRunning = host.Session.State != SessionState.Stopped;
        if (!IsDirty)
        {
            LoadList(keep: Selected?.Id); // unsaved edits stay as they were left
        }
    }

    public void Detach() => host.Session.Changed -= OnSessionChanged;

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
        var profile = new Profile(Profile.NewId(), FreeName("New profile"), null, BuildSource.Bundled, [], new Dictionary<string, string>());
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

    partial void OnMonadoFromFolderChanged(bool value) => Changed();

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
        loading = true;
        try
        {
            Name = p?.Name ?? "";
            Headset = Headsets.FirstOrDefault(h => h.Id == p?.ModuleId) ?? Headsets[^1];
            MonadoFromFolder = p?.Monado.Kind == SourceKind.Folder;
            MonadoFolder = p?.Monado.Folder ?? "";
            var layer = p is null ? null : DreamAirModule.LayerFolder(p);
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
        Validate(Edited());
    }

    /// <summary>The profile as the editor shows it; options of the module it doesn't touch are kept.</summary>
    private Profile Edited()
    {
        var options = new Dictionary<string, string>(editing?.ModuleOptions ?? new Dictionary<string, string>());
        if (IsDreamAir && LayerFromFolder)
        {
            options[DreamAirLayer.OptionKey] = LayerFolder.Trim();
        }
        else
        {
            options.Remove(DreamAirLayer.OptionKey);
        }

        return new Profile(
            editing?.Id ?? "",
            Name.Trim(),
            Headset?.Id,
            MonadoFromFolder ? new BuildSource(SourceKind.Folder, MonadoFolder.Trim()) : BuildSource.Bundled,
            [.. Extra.Select(r => new EnvVar(r.Name.Trim(), r.Value))],
            options);
    }

    private void Changed()
    {
        if (loading || editing is null)
        {
            return;
        }

        IsDirty = true;
        Validate(Edited());
    }

    private bool Validate(Profile p)
    {
        var problems = ProfileValidation.Problems(p, host.Locator, LayerProblem).ToList();
        if (IsDreamAir && LayerFromFolder && string.IsNullOrWhiteSpace(LayerFolder))
        {
            problems.Add("Pick the layer folder, or choose the layer that comes with Mirador.");
        }

        Problems.Clear();
        foreach (var problem in problems)
        {
            Problems.Add(problem);
        }

        HasProblems = problems.Count > 0;
        return !HasProblems;
    }

    /// <summary>The Dream Air layer folder's problem; the layer that comes with Mirador is checked when Monado starts.</summary>
    private string? LayerProblem(Profile p)
    {
        if (p.ModuleId != DreamAirModule.ModuleId || DreamAirModule.LayerFolder(p) is not { } folder)
        {
            return null;
        }

        try
        {
            DreamAirLayer.Locate(folder, Path.Combine(host.AppDir, "layer"));
            return null;
        }
        catch (MiradorException e)
        {
            return e.Message;
        }
    }

    private void OnSessionChanged() =>
        App.Current.Ui.TryEnqueue(() => IsRunning = host.Session.State != SessionState.Stopped);
}
