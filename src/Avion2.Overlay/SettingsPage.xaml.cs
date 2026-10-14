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
        SetUpStorage();
        CubeFinderBox.IsChecked = state.Stored.CubeFinder;
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

    private sealed record KeepChoice(int Days, string Label);

    private void SetUpStorage()
    {
        var fights = new List<KeepChoice> { new(0, "Keep all"), new(90, "Delete after 90 days"), new(30, "Delete after 30 days"), new(7, "Delete after 7 days") };
        var recordings = new List<KeepChoice> { new(0, "Keep all"), new(30, "Delete after 30 days"), new(7, "Delete after 7 days"), new(1, "Delete after 1 day") };
        KeepFightsBox.ItemsSource = fights;
        KeepFightsBox.SelectedItem = fights.FirstOrDefault(f => f.Days == _state.Stored.KeepFightsDays) ?? fights[0];
        KeepRecordingsBox.ItemsSource = recordings;
        KeepRecordingsBox.SelectedItem = recordings.FirstOrDefault(r => r.Days == _state.Stored.KeepRecordingsDays) ?? recordings[0];

        KeepFightsBox.SelectionChanged += (_, _) =>
        {
            if (KeepFightsBox.SelectedItem is not KeepChoice choice || choice.Days == _state.Stored.KeepFightsDays)
            {
                return;
            }

            _state.Stored.KeepFightsDays = choice.Days;
            _state.SaveSettings();
            if (choice.Days > 0 && Ask($"Delete saved fights older than {choice.Days} days now?"))
            {
                _state.DeleteFightsOlderThan(choice.Days);
            }

            UpdateStorage();
        };
        KeepRecordingsBox.SelectionChanged += (_, _) =>
        {
            if (KeepRecordingsBox.SelectedItem is not KeepChoice choice || choice.Days == _state.Stored.KeepRecordingsDays)
            {
                return;
            }

            _state.Stored.KeepRecordingsDays = choice.Days;
            _state.SaveSettings();
            if (choice.Days > 0 && Ask($"Delete recordings older than {choice.Days} day{(choice.Days == 1 ? "" : "s")} now?"))
            {
                Recordings.Delete(DateTime.Now.AddDays(-choice.Days), _state.Engine is not null ? _state.CurrentRecording : null);
            }

            UpdateStorage();
        };
        Loaded += (_, _) => UpdateStorage();
    }

    private bool Ask(string question) =>
        MessageBox.Show(Window.GetWindow(this)!, question, "AVION2", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void UpdateStorage()
    {
        var (fights, fightBytes) = _state.History.Usage();
        var (files, recordingBytes) = Recordings.Usage();
        FightsUsage.Text = $"{fights:N0} fights · {Size(fightBytes)}";
        RecordingsUsage.Text = files == 0 ? "none" : $"{files:N0} files · {Size(recordingBytes)}";
        DeleteRecordingsButton.IsEnabled = files > 0;
    }

    private static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
        : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB"
        : $"{Math.Max(1, bytes / 1024):N0} KB";

    private void DeleteRecordings_Click(object sender, RoutedEventArgs e)
    {
        var (files, bytes) = Recordings.Usage();
        if (files > 0 && Ask($"Delete all {files} recordings ({Size(bytes)})? The one being recorded right now is kept."))
        {
            _state.DeleteRecordings();
            UpdateStorage();
        }
    }

    private void RecordingsFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(Recordings.Directory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", Recordings.Directory) { UseShellExecute = true });
    }

    /// <summary>Switching "Find My Cube" on needs an explicit yes after reading what it means.</summary>
    private void CubeFinder_Click(object sender, RoutedEventArgs e)
    {
        if (CubeFinderBox.IsChecked != true)
        {
            _state.SetCubeFinder(false);
            return;
        }

        const string warning =
            "Find My Cube leads you to hidden cubes with a compass. To do that it uses cube positions that the game " +
            "sends to your PC before the cube is visible on your screen, and remembers where cubes were.\n\n" +
            "This gives you an advantage over other players. NCSOFT may consider it unfair or against the game's rules, " +
            "and using it could put your account at risk (for example a warning or a suspension). AVION2 only reads " +
            "network traffic and never touches the game, but it cannot rule that out.\n\n" +
            "You use this feature at your own risk. Switch it on?";
        bool yes = MessageBox.Show(Window.GetWindow(this)!, warning, "Find My Cube: are you sure?",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        CubeFinderBox.IsChecked = yes;
        _state.SetCubeFinder(yes);
    }
}
