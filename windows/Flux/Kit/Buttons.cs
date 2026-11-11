using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Flux;

// A button that draws only its content. The content draws its own tile. A
// press shows the button at 80% alpha. Tab and Space reach it, and a screen
// reader reads it as a button.
public class PlainButton : Button
{
    static readonly ControlTemplate Bare = MakeTemplate();

    static ControlTemplate MakeTemplate()
    {
        var t = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        t.Seal();
        return t;
    }

    public PlainButton()
    {
        Template = Bare;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        FocusVisualStyle = FocusRing.Style;
    }

    public PlainButton(UIElement content, Action onClick) : this()
    {
        Ui.Detach(content);
        Content = content;
        Click += (_, _) => onClick();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsPressedProperty)
        {
            Opacity = IsPressed ? 0.8 : 1;
        }
        else if (e.Property == IsEnabledProperty || e.Property == IsMouseOverProperty)
        {
            Refresh();
        }
    }

    // Draws the hover and enabled state. The default only dims a disabled button.
    protected virtual void Refresh() => Opacity = IsEnabled ? 1 : Metrics.DisabledAlpha;
}

// The focus ring of the app: 2 px in the accent around the control.
public static class FocusRing
{
    public static readonly Style Style = Make();

    static Style Make()
    {
        var rect = new FrameworkElementFactory(typeof(Rectangle));
        rect.SetValue(Shape.StrokeThicknessProperty, 2.0);
        rect.SetValue(Rectangle.RadiusXProperty, Metrics.SmallCorner);
        rect.SetValue(Rectangle.RadiusYProperty, Metrics.SmallCorner);
        rect.SetValue(FrameworkElement.MarginProperty, new Thickness(-2));
        rect.SetResourceReference(Shape.StrokeProperty, "Flux.Accent");
        var style = new Style(typeof(Control));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Control)) { VisualTree = rect }));
        style.Seal();
        return style;
    }
}

// The kinds of the 1 button of the app.
public enum ButtonKind
{
    // The main action of a page or a tile.
    Filled,
    // A second action with weight.
    Tonal,
    // A second action.
    Outlined,
    // An action that ends or deletes for good.
    Destructive,
    // A small action in a line of text, such as Retry.
    Text,
}

// The button of the app. It is 28 px high or more, with the small corner. A
// hover adds 6% of the text color. A disabled button takes the dim label.
public sealed class FluxButton : PlainButton
{
    readonly ButtonKind kind;
    readonly Border chrome;
    readonly Border hover;
    readonly TextBlock label;
    readonly TextBlock? icon;

    public FluxButton(string text, ButtonKind kind, Action onClick, string? glyph = null, bool enabled = true)
    {
        this.kind = kind;
        label = Ui.Text(text, 13, FontWeights.SemiBold, align: TextAlignment.Center);
        if (glyph is not null)
        {
            icon = Ui.Icon(glyph, 13);
        }
        var row = Ui.H(6, icon, label);
        row.HorizontalAlignment = HorizontalAlignment.Center;
        hover = new Border { CornerRadius = new CornerRadius(Metrics.SmallCorner), Background = Brushes.Transparent };
        var grid = new Grid();
        grid.Children.Add(hover);
        grid.Children.Add(new Border { Child = row, Padding = new Thickness(kind == ButtonKind.Text ? 8 : 14, 5, kind == ButtonKind.Text ? 8 : 14, 5) });
        chrome = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(Metrics.SmallCorner),
            BorderThickness = new Thickness(1),
            MinHeight = Metrics.ButtonHeight,
        };
        Content = chrome;
        HorizontalAlignment = HorizontalAlignment.Left;
        IsEnabled = enabled;
        Click += (_, _) => onClick();
        Refresh();
    }

    protected override void Refresh()
    {
        if (chrome is null)
        {
            return;
        }
        var tn = Tn.Current;
        var enabled = IsEnabled;
        Brush ink = !enabled ? tn.Dim : kind switch
        {
            ButtonKind.Filled => tn.OnAccent,
            ButtonKind.Tonal => tn.Text,
            ButtonKind.Destructive => tn.Red,
            _ => tn.Accent,
        };
        label.Foreground = ink;
        if (icon is not null)
        {
            icon.Foreground = ink;
        }
        chrome.Background = kind switch
        {
            ButtonKind.Filled => enabled ? tn.Accent : tn.Tile,
            ButtonKind.Tonal => enabled ? tn.Line : tn.Tile,
            _ => Brushes.Transparent,
        };
        chrome.BorderBrush = kind switch
        {
            ButtonKind.Outlined => enabled ? tn.Dim : tn.Line,
            ButtonKind.Destructive => enabled ? tn.Red : tn.Line,
            _ => Brushes.Transparent,
        };
        var veil = tn.Text.Color;
        hover.Background = IsMouseOver && enabled ? new SolidColorBrush(Color.FromArgb(15, veil.R, veil.G, veil.B)) : Brushes.Transparent;
    }

    public FluxButton Stretched()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        return this;
    }
}
