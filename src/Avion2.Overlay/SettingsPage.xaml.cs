using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Avion2.Engine;
using Microsoft.Win32;

namespace Avion2.Overlay;

/// <summary>Settings bound to the shared settings model, so changes apply to a running overlay immediately.</summary>
public partial class SettingsPage : UserControl
{
    private readonly AppState _state;
    private AlertSound? _preview;

    public SettingsPage(AppState state)
    {
        _state = state;
        InitializeComponent();
        DataContext = state.Settings;

        RegionBox.ItemsSource = Enum.GetValues<Avion2.Analysis.ServerRegion>();
        LanguageBox.ItemsSource = GameDataImporter.Languages;
        LanguageBox.SelectedItem = (state.DataDirectory is { } dir ? GameDataImporter.InstalledLanguage(dir) : null) ?? "en";
        state.Settings.PropertyChanged += OnSettingChanged;
        state.Fights.CollectionChanged += (_, _) => UpdateStatus();
        Unloaded += (_, _) => _state.SaveSettings();
        UpdateStatus();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverlayViewModel.LowHpSound))
        {
            _preview?.Dispose();
            _preview = null;
        }
        else if (e.PropertyName is nameof(OverlayViewModel.AlertVolume) && _preview is not null)
        {
            _preview.Volume = _state.Settings.AlertVolume;
        }
    }

    private void UpdateStatus()
    {
        DataStatus.Text = _state.SkillNames == 0
            ? "No name tables installed: skills and mobs are shown by id."
            : $"{_state.SkillNames:N0} skills · {_state.NpcNames:N0} NPCs · {_state.DataDirectory}";
        HistoryStatus.Text = $"{_state.Fights.Count} fights saved in {_state.History.Directory}";
        int icons = Enum.GetValues<Avion2.Protocol.GameClass>().Count(c => ClassIcons.For(c) is not null);
        IconStatus.Text = icons == 0
            ? "Drawn emblems (official icons not downloaded)"
            : $"{icons} official class icons in use (stored locally, not bundled)";
        VideoName.Text = _state.BackgroundVideo is { } video ? Path.GetFileName(video) : "None (drawn backdrop)";
        SoundName.Text = string.IsNullOrWhiteSpace(_state.Settings.LowHpSound) ? "Built-in chime" : Path.GetFileName(_state.Settings.LowHpSound);
    }

    private void PlayPreview()
    {
        _preview ??= new AlertSound(_state.Settings.LowHpSound, _state.Settings.AlertVolume);
        _preview.Volume = _state.Settings.AlertVolume;
        _preview.Play();
    }

    private void TestSound_Click(object sender, RoutedEventArgs e) => PlayPreview();

    private void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        var next = _state.Timers.NextRifts(DateTimeOffset.Now, 1)[0];
        _state.Notices.Notify($"Spacetime Rift at {next.ToLocalTime():HH:mm} (test)", "Spacetime Rift");
    }

    private void MoveNotifications_Click(object sender, RoutedEventArgs e) => _state.Notices.SetPositioning(true);

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Sound files (*.wav;*.mp3)|*.wav;*.mp3", Title = "Low-HP alert sound" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _state.Settings.LowHpSound = dialog.FileName;
            UpdateStatus();
            PlayPreview();
        }
    }

    private void DefaultSound_Click(object sender, RoutedEventArgs e)
    {
        _state.Settings.LowHpSound = null;
        UpdateStatus();
        PlayPreview();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        string language = LanguageBox.SelectedItem as string ?? "en";

        // Update the tables in use; fresh installs go to %AppData%\AVION2\data.
        string target = _state.DataDirectory ?? GameDataImporter.UserDataDirectory;
        ImportButton.IsEnabled = false;
        DataStatus.Text = $"Downloading {language} tables…";
        try
        {
            var result = await GameDataImporter.ImportAsync(language, target);
            _state.ReloadGameData(result.Directory);
            UpdateStatus();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            DataStatus.Text = $"Import failed: {ex.Message}";
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    private void BrowseVideo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Videos (*.mp4;*.wmv)|*.mp4;*.wmv", Title = "Home background video" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _state.SetBackgroundVideo(dialog.FileName);
            UpdateStatus();
        }
    }

    private void DefaultVideo_Click(object sender, RoutedEventArgs e)
    {
        _state.SetBackgroundVideo(null);
        UpdateStatus();
    }

    private void NoVideo_Click(object sender, RoutedEventArgs e)
    {
        _state.SetBackgroundVideo(string.Empty);
        UpdateStatus();
    }

    private async void DownloadIcons_Click(object sender, RoutedEventArgs e)
    {
        IconButton.IsEnabled = false;
        IconStatus.Text = "Downloading…";
        int saved = await ClassIconDownloader.DownloadAsync(overwrite: true);
        IconStatus.Text = saved == 0 ? "Download failed — keeping the drawn emblems" : $"Downloaded {saved} official class icons";
        IconButton.IsEnabled = true;
    }

    private void ClassIcons_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ClassIcons.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe", ClassIcons.Directory) { UseShellExecute = true });
    }

    private void HistoryFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_state.History.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe", _state.History.Directory) { UseShellExecute = true });
    }
}
