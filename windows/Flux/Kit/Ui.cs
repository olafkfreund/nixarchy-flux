using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Flux;

// Builders for the views. The pages are code, not XAML, so that the
// compiler checks each property. They follow the SwiftUI views of the Mac
// app part by part.
public static class Ui
{
    // A text in the body face, or in mono for data from the computer.
    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, Brush? color = null,
                                 bool mono = false, bool wrap = false, int maxLines = 0, TextAlignment align = TextAlignment.Left,
                                 bool select = false)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            FontFamily = mono ? Faces.Mono : Faces.Body,
            Foreground = color ?? Tn.Current.Text,
            TextAlignment = align,
            TextWrapping = wrap || maxLines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (maxLines > 1)
        {
            t.MaxHeight = maxLines * size * 1.4;
        }
        return t;
    }

    // A text that the user can select and copy, such as a device ID.
    public static TextBox SelectableText(string text, double size = 12, bool mono = true, Brush? color = null) => new()
    {
        Text = text,
        IsReadOnly = true,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Padding = new Thickness(0),
        FontSize = size,
        FontFamily = mono ? Faces.Mono : Faces.Body,
        Foreground = color ?? Tn.Current.Text,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Icon(string glyph, double size = 15, Brush? color = null, double? width = null)
    {
        var t = new TextBlock
        {
            Text = glyph,
            FontFamily = Faces.Icons,
            FontSize = size,
            Foreground = color ?? Tn.Current.Text,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        if (width is { } w)
        {
            t.Width = w;
        }
        return t;
    }

    public static Stack V(double spacing, params UIElement?[] children) => Make(Orientation.Vertical, spacing, children);
    public static Stack H(double spacing, params UIElement?[] children) => Make(Orientation.Horizontal, spacing, children);

    public static Stack V(double spacing, IEnumerable<UIElement?> children) => Make(Orientation.Vertical, spacing, children);
    public static Stack H(double spacing, IEnumerable<UIElement?> children) => Make(Orientation.Horizontal, spacing, children);

    static Stack Make(Orientation o, double spacing, IEnumerable<UIElement?> children)
    {
        var s = new Stack { Orientation = o, Spacing = spacing };
        foreach (var c in children)
        {
            if (c is not null)
            {
                Detach(c);
                s.Children.Add(c);
            }
        }
        return s;
    }

    // A flexible gap that takes the rest of the space in a stack.
    public static FrameworkElement Spacer(double min = 0)
    {
        var e = new FrameworkElement { MinWidth = min };
        Stack.SetFill(e, 1);
        return e;
    }

    // Makes the element take a share of the rest of the space in a stack.
    public static T Fill<T>(this T e, double weight = 1) where T : UIElement
    {
        Stack.SetFill(e, weight);
        return e;
    }

    public static T Margin<T>(this T e, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        e.Margin = new Thickness(left, top, right, bottom);
        return e;
    }

    public static T Margin<T>(this T e, double all) where T : FrameworkElement
    {
        e.Margin = new Thickness(all);
        return e;
    }

    public static T Align<T>(this T e, HorizontalAlignment h = HorizontalAlignment.Stretch, VerticalAlignment v = VerticalAlignment.Stretch) where T : FrameworkElement
    {
        e.HorizontalAlignment = h;
        e.VerticalAlignment = v;
        return e;
    }

    public static T Left<T>(this T e) where T : FrameworkElement
    {
        e.HorizontalAlignment = HorizontalAlignment.Left;
        return e;
    }

    public static T Top<T>(this T e) where T : FrameworkElement
    {
        e.VerticalAlignment = VerticalAlignment.Top;
        return e;
    }

    public static T Width<T>(this T e, double width) where T : FrameworkElement
    {
        e.Width = width;
        return e;
    }

    public static T Height<T>(this T e, double height) where T : FrameworkElement
    {
        e.Height = height;
        return e;
    }

    public static T MinHeight<T>(this T e, double height) where T : FrameworkElement
    {
        e.MinHeight = height;
        return e;
    }

    public static T Help<T>(this T e, string help) where T : FrameworkElement
    {
        if (help.Length > 0)
        {
            e.ToolTip = help;
        }
        return e;
    }

    // The name that a screen reader reads for the element.
    public static T Named<T>(this T e, string name) where T : FrameworkElement
    {
        AutomationProperties.SetName(e, name);
        return e;
    }

    public static T Dimmed<T>(this T e, bool dim) where T : UIElement
    {
        e.Opacity = dim ? Metrics.DisabledAlpha : 1;
        return e;
    }

    public static T Shown<T>(this T e, bool shown) where T : UIElement
    {
        e.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        return e;
    }

    // A tile: a fill, a 1 px border, and the tile corner. No shadow.
    public static Border Tile(UIElement? child, Brush? fill = null, Brush? border = null, double padding = 14,
                              double corner = Metrics.TileCorner, double borderWidth = 1)
    {
        if (child is not null)
        {
            Detach(child);
        }
        return new Border
        {
            Child = child,
            Background = fill ?? Tn.Current.Tile,
            BorderBrush = border ?? Tn.Current.Line,
            BorderThickness = new Thickness(borderWidth),
            CornerRadius = new CornerRadius(corner),
            Padding = new Thickness(padding),
        };
    }

    public static Border Box(UIElement? child, double left, double top, double right, double bottom)
    {
        if (child is not null)
        {
            Detach(child);
        }
        return new Border { Child = child, Padding = new Thickness(left, top, right, bottom) };
    }

    // A circle of the size in the color, such as a link dot.
    public static Ellipse Dot(Brush fill, double size = 8) => new()
    {
        Width = size,
        Height = size,
        Fill = fill,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // A ring of the size, the mark of something that is off.
    public static Ellipse Ring(Brush stroke, double size = 8, double width = 1.5) => new()
    {
        Width = size,
        Height = size,
        Stroke = stroke,
        StrokeThickness = width,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // A line of 1 px in line, as a divider.
    public static Border Divider() => new() { Height = 1, Background = Tn.Current.Line, Margin = new Thickness(0, 4, 0, 4) };

    // A scroll view with the vertical bar only when it needs one.
    public static ScrollViewer Scroll(UIElement content)
    {
        Detach(content);
        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
    }

    // The column of the content of a page: at most the content width, in the
    // center, with the gutter.
    public static Border Column(UIElement content, double top = 6, double bottom = 20)
    {
        Detach(content);
        return new Border
        {
            Child = content,
            MaxWidth = Metrics.MaxContentWidth,
            Padding = new Thickness(Metrics.Gutter, top, Metrics.Gutter, bottom),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    // Removes the element from its parent, so that a new view can hold it.
    // The pages keep fields, scroll views, and lists across their redraws.
    public static void Detach(UIElement e)
    {
        switch (VisualTreeHelper.GetParent(e) ?? (e as FrameworkElement)?.Parent)
        {
            case Panel p:
                p.Children.Remove(e);
                break;
            case Decorator d when d.Child == e:
                d.Child = null;
                break;
            case ContentPresenter cp when cp.Content == e:
                cp.Content = null;
                break;
            case ContentControl cc when cc.Content == e:
                cc.Content = null;
                break;
        }
        if (e is FrameworkElement { Parent: ContentControl owner } && owner.Content == e)
        {
            owner.Content = null;
        }
        if (e is FrameworkElement { Parent: Decorator holder } && holder.Child == e)
        {
            holder.Child = null;
        }
    }
}
