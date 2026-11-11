using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FluxKit;

// The UI state of a feature. WPF binds to it. Change the properties only on
// the UI thread: a plugin on a network thread uses core.Host.Post.
public abstract class Model : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // Sets the field and reports the change. It returns false when the value
    // did not change.
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        Changed(name);
        return true;
    }

    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
