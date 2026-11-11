using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FluxKit;

namespace Flux;

// Control: the agents of the computers in scope, the tools that act on the
// computer, and the streams of this computer. The dock plays the media of
// the computer. The features add each part. ControlView.swift of the Mac app
// has the same order.
public sealed class ControlPage : Page
{
    public ControlPage(AppModel model) : base(model)
    {
        foreach (var m in ControlTools.Models(model))
        {
            Watch(m);
        }
    }

    public override string Title => "Control";

    public override string Subtitle => Texts.ContextLine($"on {Shared.ScopeName(Model)}", Shared.OnlineText(Model));

    protected override UIElement Build()
    {
        if (Model.Paired.Count == 0)
        {
            return Parts.Unavailable(Icons.Control, "No computer is paired", "Pair your Omarchy computer in Computers. Its tools then show here.");
        }
        var items = new List<UIElement?>();
        foreach (var make in ControlTools.Sections)
        {
            if (make(Model) is { } section)
            {
                items.Add(section);
            }
        }
        var tools = ControlTools.Tools.Select(t => t(Model)).OfType<UIElement>().ToArray();
        if (tools.Length > 0)
        {
            items.Add(Parts.ListHeading("Tools"));
            items.Add(Parts.GridRows(tools, 2));
        }
        var streams = ControlTools.Streams.Select(t => t(Model)).OfType<UIElement>().ToArray();
        if (streams.Length > 0)
        {
            items.Add(Parts.ListHeading("Stream"));
            items.Add(Parts.GridRows(streams, 3));
        }
        if (items.Count == 0)
        {
            return Parts.Unavailable(Icons.Control, "No tools", "The tools of your computers show here.");
        }
        var layout = new DockPanel();
        if (MediaDock.Build(Model) is { } dock)
        {
            DockPanel.SetDock(dock, Dock.Bottom);
            layout.Children.Add(dock);
        }
        layout.Children.Add(Ui.Scroll(Ui.Column(Ui.V(Metrics.Gap, items), 8, 24)));
        return layout;
    }
}

// The parts of Control that the features add. Each list keeps 1 entry per
// feature, in reading order.
public static class ControlTools
{
    // Sections above the tools, such as the agents.
    public static List<Func<AppModel, UIElement?>> Sections { get; } = [];

    // The tools of the computer, 2 in a row.
    public static List<Func<AppModel, UIElement?>> Tools { get; } = [];

    // The streams of this computer, 3 in a row.
    public static List<Func<AppModel, UIElement?>> Streams { get; } = [];

    // The models that Control watches.
    public static List<Func<AppModel, INotifyPropertyChanged?>> ModelMakers { get; } = [];

    public static IEnumerable<INotifyPropertyChanged> Models(AppModel model) =>
        ModelMakers.Select(m => m(model)).OfType<INotifyPropertyChanged>();

    // A tool for the computers in scope that accept the packet type. A click
    // runs the action for the 1 computer that takes it, or asks which one.
    public static UIElement Tool(AppModel model, string glyph, string title, string line, string accepts, Action<DeviceSnapshot> run,
                                 bool small = false)
    {
        var ready = model.ConnectedPaired.Where(d => (model.Scope is null || d.Id == model.Scope) && d.Accepts(accepts)).ToArray();
        return Parts.GridTool(glyph, title, line, () =>
        {
            if (Targets.Pick(model, accepts) is { } id && model.PairedDevice(id) is { } d)
            {
                run(d);
            }
        }, ready.Length > 0, small);
    }

    // True when a paired computer in scope has the feature, also while it is
    // not reachable.
    public static bool HasFeature(AppModel model, string accepts) =>
        model.Paired.Any(d => (model.Scope is null || d.Id == model.Scope) && d.Accepts(accepts));
}
