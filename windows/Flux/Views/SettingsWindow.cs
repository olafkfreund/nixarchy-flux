using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FluxKit;

namespace Flux;

// The settings: General with Flux on or off, this computer, the login start,
// and the theme, and Features with the settings of each feature.
// SettingsView.swift of the Mac app has the same sections.
public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppModel model)
    {
        Title = "Flux settings";
        Width = 560;
        Height = 600;
        MinWidth = 480;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = Faces.Body;
        Content = new SettingsView(model);
        Background = Tn.Current.Bg;
        Tn.Changed += () => Background = Tn.Current.Bg;
        InputBindings.Add(new KeyBinding(new Relay(Close), Key.Escape, ModifierKeys.None));
    }
}

sealed class SettingsView : View
{
    readonly AppModel model;
    bool features;

    public SettingsView(AppModel model)
    {
        this.model = model;
        Watch(model);
        Watch(model.Core.Plugin<ThemePlugin>()?.Model);
        foreach (var m in FeatureSettings.Models(model))
        {
            Watch(m);
        }
    }

    protected override UIElement Build()
    {
        var tn = Tn.Current;
        var tabs = Ui.H(6, Tab("General", !features, () => features = false), Tab("Features", features, () => features = true)).Margin(0, 0, 0, 8);
        var body = features ? FeaturesTab() : General();
        return Ui.Scroll(Ui.Column(Ui.V(Metrics.Gap, tabs, body), 16, 24));
    }

    UIElement Tab(string title, bool selected, Action pick)
    {
        var tn = Tn.Current;
        var chip = new Border
        {
            Child = Ui.Text(title, 13, FontWeights.SemiBold, selected ? tn.Text : tn.Sub),
            Background = selected ? tn.AccentTile : tn.Tile,
            BorderBrush = selected ? tn.Accent : tn.Line,
            BorderThickness = new Thickness(selected ? 2 : 1),
            CornerRadius = new CornerRadius(Metrics.SmallCorner),
            Padding = new Thickness(12, 6, 12, 6),
        };
        return new PlainButton(chip, () =>
        {
            pick();
            Invalidate();
        }).Named(title);
    }

    UIElement General()
    {
        var tn = Tn.Current;
        var s = model.State;
        var items = new List<UIElement?>
        {
            Parts.SectionLabel("This computer"),
            Group(
                Toggle("Flux is on", s.Enabled, on => model.Core.Enabled = on),
                Field("Name", Ui.Text(s.DeviceName, 13)),
                Field("Device ID", Ui.SelectableText(s.DeviceId, 11, color: tn.Sub)),
                Field("Link port", Ui.Text(s.TcpPort == 0 ? "–" : s.TcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture), 13, mono: true)),
                s.ListeningUdp ? null : Note("Another app uses UDP port 12100. Flux still announces itself and computers can connect."),
                Note("Change the name in Windows Settings > System > About > Rename this PC.")),
            Parts.SectionLabel("Start"),
            Group(
                Toggle("Open at login", LoginItem.Enabled, on =>
                {
                    if (!LoginItem.Set(on))
                    {
                        model.Show("Windows did not change the login start");
                    }
                    Invalidate();
                }),
                Note("Flux opens in the tray when you log in, so that the clipboard syncs without a click. After you close the window, Flux keeps running in the tray.")),
            Parts.SectionLabel("Theme"),
            Group(
                Choices(),
                Note(ComputerLine()),
                Note("Computer draws the Omarchy theme of the computer in scope. System follows the app mode of Windows with Tokyo Night.")),
        };
        return Ui.V(Metrics.Gap, items);
    }

    UIElement Choices()
    {
        var tn = Tn.Current;
        var chips = ThemeModes.All.Select(mode =>
        {
            var selected = model.Theme == mode;
            var chip = new Border
            {
                Child = Ui.Text(mode.Label(), 13, FontWeights.SemiBold, selected ? tn.Text : tn.Sub),
                Background = selected ? tn.AccentTile : tn.Tile,
                BorderBrush = selected ? tn.Accent : tn.Line,
                BorderThickness = new Thickness(selected ? 2 : 1),
                CornerRadius = new CornerRadius(Metrics.SmallCorner),
                Padding = new Thickness(12, 6, 12, 6),
            };
            var b = new PlainButton(chip, () => model.Theme = mode).Named("Theme " + mode.Label());
            System.Windows.Automation.AutomationProperties.SetItemStatus(b, selected ? "Selected" : "");
            return (UIElement)b;
        });
        return Ui.H(6, chips);
    }

    // The theme that the Computer choice draws now, and the computer that sent it.
    string ComputerLine()
    {
        var theme = model.Core.Plugin<ThemePlugin>()?.Model.Current(model.Scope);
        var themeComputer = theme is null ? null : model.DeviceOf(theme.DeviceId)?.Name;
        return "Computer: " + ThemeModes.ComputerLine(theme, themeComputer, model.PairedDevice(model.Scope)?.Name, ThisComputer.SystemDark);
    }

    UIElement FeaturesTab()
    {
        var sections = FeatureSettings.Sections(model).ToArray();
        if (sections.Length == 0)
        {
            return Ui.Text("The settings of the features show here.", 13, color: Tn.Current.Sub, wrap: true).Margin(4);
        }
        var items = new List<UIElement?>();
        foreach (var (label, content) in sections)
        {
            items.Add(Parts.SectionLabel(label));
            items.Add(content);
        }
        return Ui.V(Metrics.Gap, items);
    }

    // A group of rows in a tile.
    public static UIElement Group(params UIElement?[] rows) => Ui.Tile(Ui.V(10, rows), padding: 14);

    // A row with a label and a switch.
    public static UIElement Toggle(string label, bool on, Action<bool> set)
    {
        var box = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        box.Click += (_, _) => set(box.IsChecked == true);
        return Ui.H(10, Ui.Text(label, 13).Fill(), box);
    }

    public static UIElement Field(string label, UIElement value) =>
        Ui.H(10, Ui.Text(label, 13, color: Tn.Current.Sub).Width(110), value.Fill());

    public static UIElement Note(string text) => Ui.Text(text, 12, color: Tn.Current.Sub, wrap: true);
}

// The sections of the Features tab that the features add.
public static class FeatureSettings
{
    public static List<Func<AppModel, (string Label, UIElement Content)?>> Makers { get; } = [];
    public static List<Func<AppModel, System.ComponentModel.INotifyPropertyChanged?>> ModelMakers { get; } = [];

    public static IEnumerable<System.ComponentModel.INotifyPropertyChanged> Models(AppModel model) =>
        ModelMakers.Select(m => m(model)).OfType<System.ComponentModel.INotifyPropertyChanged>();

    public static IEnumerable<(string Label, UIElement Content)> Sections(AppModel model)
    {
        foreach (var make in Makers)
        {
            if (make(model) is { } s)
            {
                yield return s;
            }
        }
    }
}
