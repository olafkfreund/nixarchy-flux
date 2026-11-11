namespace FluxKit;

public enum LogLevel
{
    Debug,
    Info,
    Error,
}

// The log of Flux. The app sets a sink that writes a file. Messages never
// hold clipboard text or file contents.
public static class FluxLog
{
    static Action<LogLevel, string, string>? sink;

    // Receives the level, the category, and the message.
    public static Action<LogLevel, string, string>? Sink
    {
        get => Volatile.Read(ref sink);
        set => Volatile.Write(ref sink, value);
    }

    public static void Debug(string category, string message) => Sink?.Invoke(LogLevel.Debug, category, message);
    public static void Info(string category, string message) => Sink?.Invoke(LogLevel.Info, category, message);
    public static void Error(string category, string message) => Sink?.Invoke(LogLevel.Error, category, message);
}
