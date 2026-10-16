using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// "Update now": downloads, verifies and unpacks the release with a progress bar, then hands the file swap to
/// AVION2-Updater.exe (it waits for AVION2 to close, replaces the files and starts AVION2 again).
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly AppState _state;
    private readonly ReleaseInfo _release;
    private readonly CancellationTokenSource _cancel = new();
    private bool _running;

    public UpdateWindow(AppState state, ReleaseInfo release, bool preview = false)
    {
        _state = state;
        _release = release;
        InitializeComponent();
        ThemeManager.Attach(this);
        Versions.Text = $"v{UpdateChecker.CurrentVersion} → {release.Tag}";
        if (preview)
        {
            Show(new UpdateDownload.Progress(UpdateDownload.Stage.Downloading, 31_400_000, 79_700_000)); // render shots
        }
        else
        {
            Loaded += async (_, _) => await RunAsync();
        }
        Closing += (_, e) =>
        {
            if (_running)
            {
                _cancel.Cancel(); // the window closes once the download stopped
                e.Cancel = true;
            }
        };
    }

    private async Task RunAsync()
    {
        _running = true;
        var progress = new Progress<UpdateDownload.Progress>(Show);
        try
        {
            string staged = await UpdateDownload.RunAsync(_release, progress, _cancel.Token);
            Status.Text = "Installing… AVION2 starts again in a moment.";
            Amount.Text = string.Empty;
            Bar.IsIndeterminate = true;
            CancelButton.IsEnabled = false;
            _running = false;
            if (_state.HandOverUpdate(staged, _release) is { } error)
            {
                Fail(error);
            }
        }
        catch (OperationCanceledException)
        {
            _running = false;
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or InvalidDataException
                                       or UnauthorizedAccessException or TaskCanceledException)
        {
            _running = false;
            Fail(ex.Message);
        }
    }

    private void Show(UpdateDownload.Progress p)
    {
        switch (p.Stage)
        {
            case UpdateDownload.Stage.Downloading:
                Status.Text = "Downloading…";
                Bar.IsIndeterminate = p.Total is not > 0;
                Bar.Value = p.Total is > 0 ? p.Done / (double)p.Total.Value : 0;
                Amount.Text = p.Total is > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{p.Done / 1_048_576.0:0.0} / {p.Total.Value / 1_048_576.0:0.0} MB")
                    : string.Create(CultureInfo.InvariantCulture, $"{p.Done / 1_048_576.0:0.0} MB");
                break;
            case UpdateDownload.Stage.Verifying:
                Status.Text = "Checking the download…";
                Bar.Value = 1;
                break;
            case UpdateDownload.Stage.Unpacking:
                Status.Text = "Unpacking…";
                Amount.Text = string.Empty;
                Bar.IsIndeterminate = true;
                break;
        }
    }

    private void Fail(string message)
    {
        Heading.Text = "The update did not work";
        Status.Text = message;
        Status.SetResourceReference(ForegroundProperty, "HpBarBrush");
        Amount.Text = string.Empty;
        Bar.IsIndeterminate = false;
        Bar.Value = 0;
        Note.Text = "AVION2 was left as it was. You can try again later or download the release from GitHub.";
        CancelButton.Content = "Close";
        CancelButton.IsEnabled = true;
        PageButton.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Page_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_release.PageUrl) { UseShellExecute = true });
}
