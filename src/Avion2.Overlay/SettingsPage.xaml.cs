using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
        LanguageBox.ItemsSource = BundledGameData.Languages;
        LanguageBox.SelectedItem = state.Stored.DataLanguage;
        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (LanguageBox.SelectedItem is string language && language != _state.Stored.DataLanguage)
            {
                _state.SetDataLanguage(language);
                UpdateStatus();
            }
        };
        UpdateCheckBox.IsChecked = state.Stored.CheckForUpdates;
        UpdateCheckBox.Click += (_, _) =>
        {
            _state.Stored.CheckForUpdates = UpdateCheckBox.IsChecked == true;
            _state.SaveSettings();
        };
        VersionText.Text = $"Version {Avion2.Engine.UpdateChecker.CurrentVersion}";
        ThemeBox.ItemsSource = ThemeManager.Themes;
        ThemeBox.SelectedItem = ThemeManager.Current;
        ThemeManager.Changed += () =>
        {
            ThemeBox.SelectedItem = ThemeManager.Current;
            UpdateStatus();
        };
        ThemeBox.SelectionChanged += (_, _) =>
        {
            if (ThemeBox.SelectedItem is ThemeDefinition theme && theme != ThemeManager.Current)
            {
                _state.SetTheme(theme.Id);
                UpdateStatus();
            }
        };
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
        DataStatus.Text = $"{_state.SkillNames:N0} skills · {_state.NpcNames:N0} NPCs · {_state.Data.FieldBosses.Count} field bosses · {_state.DataSource}";
        HistoryStatus.Text = $"{_state.Fights.Count} fights saved in {_state.History.Directory}";
        int icons = Enum.GetValues<Avion2.Protocol.GameClass>().Count(c => ClassIcons.For(c) is not null);
        IconStatus.Text = $"{icons} official class icons (built in; PNGs in the folder override them)";
        VideoName.Text = _state.BackgroundVideo is { } video ? Path.GetFileName(video) : "None (drawn backdrop)";
        ThemeHint.Text = ThemeManager.Current.VideoBackdrop
            ? "Key-art backdrop, serif display type"
            : "Enter The Void design: no background video";
        SoundName.Text = string.IsNullOrWhiteSpace(_state.Settings.LowHpSound) ? "Built-in warning beeps" : Path.GetFileName(_state.Settings.LowHpSound);
        NoticeSoundName.Text = string.IsNullOrWhiteSpace(_state.Settings.NoticeSound) ? "Built-in bell" : Path.GetFileName(_state.Settings.NoticeSound);
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
        if (_state.Settings.NotifySound)
        {
            _state.NoticeSound().Play();
        }
    }

    private void MoveNotifications_Click(object sender, RoutedEventArgs e) => _state.Notices.SetPositioning(true);

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        VersionText.Text = "Checking…";
        string status = await _state.CheckForUpdatesAsync();
        VersionText.Text = $"Version {Avion2.Engine.UpdateChecker.CurrentVersion} · {status}" +
                           (_state.AvailableUpdate is not null ? " See the Home page to update." : string.Empty);
        CheckNowButton.IsEnabled = true;
    }

    private void TestNoticeSound_Click(object sender, RoutedEventArgs e) => _state.NoticeSound().Play();

    private void BrowseNoticeSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Sound files (*.wav;*.mp3)|*.wav;*.mp3", Title = "Notification sound" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _state.Settings.NoticeSound = dialog.FileName;
            UpdateStatus();
            _state.NoticeSound().Play();
        }
    }

    private void DefaultNoticeSound_Click(object sender, RoutedEventArgs e)
    {
        _state.Settings.NoticeSound = null;
        UpdateStatus();
        _state.NoticeSound().Play();
    }

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
