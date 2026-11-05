using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>What the applicant card shows for one join request.</summary>
/// <param name="Index">Which request (0 = the newest) of <paramref name="Count"/>.</param>
/// <param name="Lookup">The official site's answer; null while it is asked.</param>
/// <param name="Seen">"3 runs with you, last Krao Cave 2 days ago", or null for none.</param>
/// <param name="FirstSeenMs">When the request came in (epoch ms), for the expiry bar.</param>
public sealed record ApplicantShown(
    FinderJoinRequestView Request,
    string Dungeon,
    RoomStanding Room,
    int Index,
    int Count,
    ArmoryLookup? Lookup,
    string? Seen,
    string? OwnLegion,
    long FirstSeenMs);

/// <summary>
/// A small always-on-top card for someone asking to join the group-finder room we lead: who they are, their item level
/// and combat power against the room, and their gear from the official site. Never takes focus; drag it anywhere.
/// </summary>
public partial class ApplicantWindow : Window
{
    /// <summary>The card's inner width (300 less the padding and border).</summary>
    private const double Inner = 270;

    private readonly AppState _state;
    private ApplicantShown? _shown;

    public ApplicantWindow(AppState state)
    {
        _state = state;
        InitializeComponent();
        TopmostKeeper.Keep(this);
        ThemeManager.Attach(this);
        Left = state.Stored.ApplicantLeft ?? SystemParameters.WorkArea.Left + 24;
        Top = state.Stored.ApplicantTop ?? SystemParameters.WorkArea.Top + 180;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                SavePlace();
            }
        };
        Closed += (_, _) => SavePlace();
    }

    /// <summary>The pager was clicked: −1 for a newer request, +1 for an older one.</summary>
    public event Action<int>? PageClicked;

    /// <summary>✕: hide until the next request.</summary>
    public event Action? Dismissed;

    private void SavePlace()
    {
        if (_state.Stored.ApplicantLeft != Left || _state.Stored.ApplicantTop != Top)
        {
            _state.Stored.ApplicantLeft = Left;
            _state.Stored.ApplicantTop = Top;
            _state.SaveSettings();
        }
    }

    public void Fill(ApplicantShown shown)
    {
        _shown = shown;
        var request = shown.Request;
        var profile = shown.Lookup?.Profile;
        Chip.Text = $"Join request · {shown.Dungeon}";

        // Who: the portrait from the site with the class emblem on it, else the class emblem alone
        var classIcon = ClassIcons.For(request.Class);
        var portrait = StreamerMode.On ? null : CharacterSheet.WebImage(profile?.ImageUrl, 120);
        PortraitBrush.ImageSource = portrait;
        PortraitFrame.Visibility = portrait is null ? Visibility.Collapsed : Visibility.Visible;
        ClassLarge.Source = portrait is null ? classIcon : null;
        EmblemIcon.Source = classIcon;
        Emblem.Visibility = portrait is not null && classIcon is not null ? Visibility.Visible : Visibility.Collapsed;

        NameText.Text = StreamerMode.Name(request.Name);
        string server = BundledGameData.Server(request.Server)?.Name ?? profile?.ServerName ?? string.Empty;
        SubText.Text = string.Join(" · ", new[] { LegionExport.ClassName(request.Class), $"Lv {request.Level}", server }.Where(s => s.Length > 0));
        FillLegion(shown, profile);

        // Item level and combat power against the room
        var room = shown.Room;
        string against = room.Members <= 1 ? "vs. you" : "vs. your room";
        ItemLevelText.Text = Number(request.ItemLevel);
        Versus(ItemLevelVs, ApplicantCard.PercentVs(request.ItemLevel, room.AverageItemLevel), against);
        PowerText.Text = Number(request.CombatPower);
        Versus(PowerVs, ApplicantCard.PercentVs(request.CombatPower, room.AverageCombatPower), against);
        FillScale(request.ItemLevel, room);

        FillGear(shown.Lookup);

        SeenText.Text = shown.Seen is { } seen ? $"Seen before: {seen}" : "Not in your runs on this PC yet.";
        SeenText.Foreground = Resource(shown.Seen is null ? "MutedBrush" : "SecondaryTextBrush");

        ProfileButton.IsEnabled = request.Server != 0;
        Pager.Visibility = shown.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = $"{shown.Index + 1} / {shown.Count}";
        PrevButton.IsEnabled = shown.Index > 0;
        NextButton.IsEnabled = shown.Index < shown.Count - 1;
    }

    /// <summary>The countdown and the bar running out, every half second.</summary>
    public void Tick(long nowMs)
    {
        if (_shown is not { Request.ExpiresAtMs: > 0 } shown)
        {
            Timer.Text = string.Empty;
            Expiry.Visibility = Visibility.Hidden;
            return;
        }

        long expires = shown.Request.ExpiresAtMs;
        long left = Math.Max(0, expires - nowMs);
        var span = TimeSpan.FromMilliseconds(left);
        Timer.Text = $"{(int)span.TotalMinutes}:{span.Seconds:00}";
        Expiry.Visibility = Visibility.Visible;
        double whole = Math.Max(1, expires - Math.Min(shown.FirstSeenMs, nowMs));
        ExpiryFill.Width = Inner * Math.Clamp(left / whole, 0, 1);
    }

    private void FillLegion(ApplicantShown shown, ArmoryProfile? profile)
    {
        LegionText.Inlines.Clear();
        LegionText.Visibility = Visibility.Visible;
        if (shown.Lookup is null)
        {
            LegionText.Text = "Looking up on the official site…";
        }
        else if (StreamerMode.On || profile is null)
        {
            LegionText.Visibility = Visibility.Collapsed;
        }
        else if (profile.Legion is not { Length: > 0 } legion)
        {
            LegionText.Text = "No legion";
        }
        else if (string.Equals(legion, shown.OwnLegion, StringComparison.Ordinal))
        {
            LegionText.Inlines.Add(new Run("Legion "));
            LegionText.Inlines.Add(new Run(legion) { Foreground = Resource("SelfBrush") });
            LegionText.Inlines.Add(new Run(" (yours)"));
        }
        else
        {
            LegionText.Text = $"Legion {legion}";
        }
    }

    private static void Versus(TextBlock block, int? percent, string against)
    {
        (block.Text, block.Foreground) = percent switch
        {
            null => (string.Empty, Resource("MutedBrush")),
            0 => ($"same as {against[4..]}", Resource("MutedBrush")),
            > 0 => ($"+{percent}% {against}", Resource("LiveBrush")),
            _ => ($"−{-percent}% {against}", Resource("WarnBrush")),
        };
    }

    /// <summary>The room's item levels as dots on a line, the applicant's in gold.</summary>
    private void FillScale(uint applicant, RoomStanding room)
    {
        Scale.Children.Clear();
        if (room.MinItemLevel is not { } min || room.MaxItemLevel is not { } max)
        {
            RoomPanel.Visibility = Visibility.Collapsed;
            return;
        }

        RoomPanel.Visibility = Visibility.Visible;
        RoomRange.Text = min == max ? Number(min) : $"{Number(min)} – {Number(max)}";
        uint low = applicant > 0 ? Math.Min(min, applicant) : min;
        uint high = Math.Max(max, applicant);
        const double Edge = 6; // dots stay inside the card
        double At(uint value) => Edge + (Inner - 2 * Edge) * ApplicantCard.Position(value, low, high);

        var track = new Rectangle { Width = Inner, Height = 2, Fill = Resource("TrackBrush"), RadiusX = 1, RadiusY = 1 };
        Canvas.SetTop(track, 8);
        Scale.Children.Add(track);
        foreach (uint level in room.ItemLevels)
        {
            Dot(At(level), 10, Resource("MutedBrush"), $"Item level {Number(level)}");
        }

        if (applicant > 0)
        {
            Dot(At(applicant), 12, Resource("AccentBrush"), $"Applicant: {Number(applicant)}");
        }
    }

    private void Dot(double x, double size, Brush fill, string tip)
    {
        var dot = new Ellipse { Width = size, Height = size, Fill = fill, Stroke = Resource("EmblemFillBrush"), StrokeThickness = 1, ToolTip = tip };
        Canvas.SetLeft(dot, x - size / 2);
        Canvas.SetTop(dot, 9 - size / 2);
        Scale.Children.Add(dot);
    }

    private void FillGear(ArmoryLookup? lookup)
    {
        GearPanel.Children.Clear();
        Facts.Children.Clear();
        Facts.RowDefinitions.Clear();
        GearLoading.Visibility = lookup is null ? Visibility.Visible : Visibility.Collapsed;
        if (lookup?.Profile is not { } profile)
        {
            // the site's answer names the character: not in streamer mode
            GearNote.Text = StreamerMode.On && lookup?.Problem is not null ? "Not found on the official site." : lookup?.Problem ?? string.Empty;
            GearNote.Visibility = lookup is null ? Visibility.Collapsed : Visibility.Visible;
            GearPanel.Visibility = Facts.Visibility = Visibility.Collapsed;
            return;
        }

        var gear = ApplicantCard.Gear(profile);
        GearNote.Visibility = Visibility.Collapsed;
        GearPanel.Visibility = gear.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in gear.Items)
        {
            GearPanel.Children.Add(Item(item));
        }

        Facts.Visibility = Visibility.Visible;
        if (gear.Grades.Count > 0)
        {
            Fact("Grades", ApplicantCard.GradeLine(gear));
        }

        if (gear.AverageEnchant is { } enchant)
        {
            Fact("Enchant", $"+{enchant.ToString("0.0", CultureInfo.InvariantCulture)} on average");
        }

        if (gear.DaevanionTotal > 0)
        {
            Fact("Daevanion", $"{Number(gear.DaevanionOpen)} of {Number(gear.DaevanionTotal)} nodes");
        }

        if (gear.Titles > 0)
        {
            Fact("Titles", Number(gear.Titles));
        }

        if (gear.Items.Count == 0 && Facts.Children.Count == 0)
        {
            GearNote.Text = "The official site shows no gear.";
            GearNote.Visibility = Visibility.Visible;
        }
    }

    /// <summary>An item: its icon in a frame of its grade's color, the enchant level in the corner.</summary>
    private static Border Item(ArmoryItem item)
    {
        var cell = new Grid();
        if ((ItemIcons.For(item.Id) ?? CharacterSheet.WebImage(item.Icon, 40)) is { } icon)
        {
            cell.Children.Add(new Image { Source = icon, Stretch = Stretch.UniformToFill });
        }

        if (item.Enchant > 0)
        {
            cell.Children.Add(new TextBlock
            {
                Text = $"+{item.Enchant}",
                FontSize = 8,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 1, -1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 2, ShadowDepth = 0, Opacity = 1 },
            });
        }

        string tip = $"{item.Name}\n{ApplicantCard.GradeName(item.Grade)}{(item.Enchant > 0 ? $" · +{item.Enchant}" : string.Empty)}" +
                     (item.Exceed > 0 ? $" · breakthrough {item.Exceed}" : string.Empty);
        return new Border
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 2.4, 2.4), // 12 to a row
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = ItemGrades.Brush(item.Grade),
            Background = Resource("TileBackgroundBrush"),
            ClipToBounds = true,
            Child = cell,
            ToolTip = tip,
        };
    }

    private void Fact(string name, string value)
    {
        int row = Facts.RowDefinitions.Count;
        Facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var key = new TextBlock { Text = name, Foreground = Resource("MutedBrush"), FontSize = 11.5, Margin = new Thickness(0, 1, 10, 1) };
        var text = new TextBlock
        {
            Text = value, Foreground = Resource("SecondaryTextBrush"), FontSize = 11.5, Margin = new Thickness(0, 1, 0, 1),
            TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(key, row);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        Facts.Children.Add(key);
        Facts.Children.Add(text);
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static Brush Resource(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Dismissed?.Invoke();

    private void Prev_Click(object sender, RoutedEventArgs e) => PageClicked?.Invoke(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => PageClicked?.Invoke(1);

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (_shown?.Request is { Server: not 0 } request)
        {
            _state.ShowCharacter(request.Name, request.Server);
        }
    }
}
