using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FluxKit;

namespace Flux;

// The dialogs of the app, in the theme. Escape cancels each of them.
public static class Dialogs
{
    // A dialog window on tile-hi with the content and the buttons at the end.
    static Window Frame(Window? owner, string title, UIElement content, double width = 400)
    {
        var tn = Tn.Current;
        var w = new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = owner is null,
            Background = tn.TileHi,
            FontFamily = Faces.Body,
            Content = Ui.Box(content, 24, 22, 24, 20),
        };
        w.InputBindings.Add(new KeyBinding(new Relay(w.Close), Key.Escape, ModifierKeys.None));
        return w;
    }

    // Asks a question. It returns true when the user takes the action.
    public static bool Confirm(Window? owner, string title, string message, string action, bool destructive = false)
    {
        var tn = Tn.Current;
        var result = false;
        Window? w = null;
        var buttons = Ui.H(8,
            Ui.Spacer(),
            new FluxButton("Cancel", ButtonKind.Outlined, () => w!.Close()),
            new FluxButton(action, destructive ? ButtonKind.Destructive : ButtonKind.Filled, () =>
            {
                result = true;
                w!.Close();
            }));
        var body = Ui.V(12,
            Ui.Text(title, 17, FontWeights.SemiBold, wrap: true),
            Ui.Text(message, 13, color: tn.Sub, wrap: true),
            buttons.Margin(0, 8, 0, 0));
        w = Frame(owner, title, body);
        w.ShowDialog();
        return result;
    }

    // Shows the key before this computer sends a pairing request. The
    // timestamp of the key goes with the request.
    public static void PairRequest(Window? owner, AppModel model, DeviceSnapshot device)
    {
        var tn = Tn.Current;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Window? w = null;
        var send = new FluxButton("Send request", ButtonKind.Filled, () =>
        {
            model.Core.Pair(device.Id, timestamp);
            w!.Close();
        }) { IsDefault = true };
        var body = Ui.V(16,
            Ui.Text($"Pair with {device.Name}", 20, align: TextAlignment.Center, wrap: true),
            Parts.KeyView(model.Core.PreviewKey(device.Id, timestamp)),
            Ui.Text("The computer shows the same key. Accept the request there when the keys match.", 13, color: tn.Sub,
                wrap: true, align: TextAlignment.Center),
            Ui.H(8, new FluxButton("Cancel", ButtonKind.Outlined, () => w!.Close()), send).Align(HorizontalAlignment.Center));
        w = Frame(owner, $"Pair with {device.Name}", body, 420);
        w.ShowDialog();
    }

    // Shows an error that stops the start of Flux.
    public static void LaunchError(string message)
    {
        var tn = Tn.Current;
        Window? w = null;
        var body = Ui.V(12,
            Ui.Text("Flux could not start", 17, FontWeights.SemiBold),
            Ui.SelectableText(message, 12, mono: false, color: tn.Sub),
            Ui.H(8, Ui.Spacer(), new FluxButton("Quit Flux", ButtonKind.Filled, () => w!.Close())).Margin(0, 8, 0, 0));
        w = Frame(null, "Flux", body, 460);
        w.ShowDialog();
    }
}
