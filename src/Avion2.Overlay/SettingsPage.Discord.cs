using System.Windows;

namespace Avion2.Overlay;

/// <summary>Settings, General: the Discord card (Rich Presence).</summary>
public partial class SettingsPage
{
    private void SetUpDiscord()
    {
        DiscordBox.IsChecked = _state.Stored.DiscordPresence;
        DiscordNameBox.IsChecked = _state.Stored.DiscordShowName;
        DiscordPictureBox.IsChecked = _state.Stored.DiscordShowPicture;
        DiscordBox.Click += (_, _) => ApplyDiscord();
        DiscordNameBox.Click += (_, _) => ApplyDiscord();
        DiscordPictureBox.Click += (_, _) => ApplyDiscord();
        _state.Discord.StateChanged += UpdateDiscord;
        StreamerMode.Changed += UpdateDiscord;
        UpdateDiscord();
    }

    private void ApplyDiscord()
    {
        _state.Stored.DiscordPresence = DiscordBox.IsChecked == true;
        _state.Stored.DiscordShowName = DiscordNameBox.IsChecked == true;
        _state.Stored.DiscordShowPicture = DiscordPictureBox.IsChecked == true;
        _state.SaveSettings();
        _state.Discord.Apply();
        UpdateDiscord();
    }

    private void UpdateDiscord()
    {
        DiscordOptions.Visibility = DiscordBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DiscordNameBox.IsEnabled = !StreamerMode.On;
        DiscordPictureBox.IsEnabled = !StreamerMode.On;
        DiscordStatus.Text = _state.Discord.Status;
        DiscordStatus.Visibility = DiscordStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
