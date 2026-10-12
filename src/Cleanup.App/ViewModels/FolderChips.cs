using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Cleanup.App.Services;
using Cleanup.Core.Models;
using Cleanup.Core.Safety;
using Windows.Storage.Pickers;

namespace Cleanup.App.ViewModels;

/// <summary>One folder chip in a module's "Pastas:" bar (Duplicados, Desenvolvedor).</summary>
public sealed partial class FolderChip(string title, string path, bool isCustom, bool isOn) : ObservableObject
{
    public string Title { get; } = title;
    public string Path { get; } = path;
    /// <summary>Added by the user (can be removed); the others are quick picks or defaults.</summary>
    public bool IsCustom { get; } = isCustom;
    [ObservableProperty] private bool isOn = isOn;
}

/// <summary>Small JSON files in <c>%LOCALAPPDATA%\Cleanup</c> for module preferences (folders, levels).</summary>
public static class ModuleStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static T Load<T>(string name) where T : new()
    {
        try
        {
            var path = System.IO.Path.Combine(AppPaths.SupportDirectory, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T() : new T();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return new T(); }
    }

    /// <summary>Null when saved, else the error message.</summary>
    public static string? Save<T>(string name, T value)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SupportDirectory);
            AtomicFile.WriteAllText(System.IO.Path.Combine(AppPaths.SupportDirectory, name), JsonSerializer.Serialize(value, Json));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"{name}: {e.Message}");
            return e.Message;
        }
    }

    /// <summary>The folder picker, parented to the main window (unpackaged apps must pass the window handle).</summary>
    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder, CommitButtonText = "Adicionar" };
        picker.FileTypeFilter.Add("*");
        if (App.Window is { } window) WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
