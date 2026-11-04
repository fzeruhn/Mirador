using Windows.Storage.Pickers;

namespace Mirador.App.Services;

/// <summary>Windows' file and folder pickers, tied to Mirador's window (an unpackaged app must name it).</summary>
public static class Pickers
{
    /// <summary>A folder the user picks; null when they cancel.</summary>
    public static async Task<string?> FolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        Bind(picker);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>A file to save, with <paramref name="suggestedName"/> and one file type; null when they cancel.</summary>
    public static async Task<string?> SaveFileAsync(string suggestedName, string typeName, string extension)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName, SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeChoices.Add(typeName, [extension]);
        Bind(picker);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    /// <summary>A file to open of one type; null when they cancel.</summary>
    public static async Task<string?> OpenFileAsync(string extension)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeFilter.Add(extension);
        Bind(picker);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private static void Bind(object picker) =>
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow));
}
