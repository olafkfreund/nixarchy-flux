using System.Windows;
using System.Windows.Controls;

namespace Flux;

// A stack with a gap between its children, as VStack and HStack in SwiftUI.
// A child with a Fill weight takes a share of the space that the other
// children leave, as a Spacer or a text that wraps. A collapsed child takes
// no space and no gap.
public sealed class Stack : Panel
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
        "Fill", typeof(double), typeof(Stack),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetFill(UIElement e) => (double)e.GetValue(FillProperty);
    public static void SetFill(UIElement e, double value) => e.SetValue(FillProperty, value);

    public Orientation Orientation { get; set; } = Orientation.Vertical;
    public double Spacing { get; set; }

    bool Horizontal => Orientation == Orientation.Horizontal;

    static double MainOf(Size s, bool horizontal) => horizontal ? s.Width : s.Height;
    static double CrossOf(Size s, bool horizontal) => horizontal ? s.Height : s.Width;
    static Size Make(double main, double cross, bool horizontal) => horizontal ? new Size(main, cross) : new Size(cross, main);

    UIElement[] Visible() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToArray();

    protected override Size MeasureOverride(Size available)
    {
        var horizontal = Horizontal;
        var children = Visible();
        var availableMain = MainOf(available, horizontal);
        var availableCross = CrossOf(available, horizontal);
        double used = Math.Max(0, children.Length - 1) * Spacing;
        double cross = 0;
        double weights = 0;
        foreach (var c in children)
        {
            var fill = GetFill(c);
            if (fill > 0)
            {
                weights += fill;
                continue;
            }
            c.Measure(Make(double.PositiveInfinity, availableCross, horizontal));
            used += MainOf(c.DesiredSize, horizontal);
            cross = Math.Max(cross, CrossOf(c.DesiredSize, horizontal));
        }
        var rest = double.IsInfinity(availableMain) ? double.PositiveInfinity : Math.Max(0, availableMain - used);
        foreach (var c in children.Where(c => GetFill(c) > 0))
        {
            var share = double.IsInfinity(rest) ? double.PositiveInfinity : rest * GetFill(c) / weights;
            c.Measure(Make(share, availableCross, horizontal));
            used += MainOf(c.DesiredSize, horizontal);
            cross = Math.Max(cross, CrossOf(c.DesiredSize, horizontal));
        }
        return Make(used, cross, horizontal);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var horizontal = Horizontal;
        var children = Visible();
        var finalMain = MainOf(final, horizontal);
        var finalCross = CrossOf(final, horizontal);
        double fixedMain = Math.Max(0, children.Length - 1) * Spacing;
        double weights = 0;
        foreach (var c in children)
        {
            var fill = GetFill(c);
            if (fill > 0)
            {
                weights += fill;
            }
            else
            {
                fixedMain += MainOf(c.DesiredSize, horizontal);
            }
        }
        var rest = Math.Max(0, finalMain - fixedMain);
        double at = 0;
        foreach (var c in children)
        {
            var fill = GetFill(c);
            var size = fill > 0 ? rest * fill / weights : MainOf(c.DesiredSize, horizontal);
            var rect = horizontal ? new Rect(at, 0, size, finalCross) : new Rect(0, at, finalCross, size);
            c.Arrange(rect);
            at += size + Spacing;
        }
        return final;
    }
}
