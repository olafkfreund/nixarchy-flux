namespace FluxKit;

// Splits a byte stream into lines without the newline. The limit can change
// while the reader runs: the core raises it after a pairing.
public sealed class LineReader(Stream stream, int limit)
{
    byte[] buffer = new byte[16 * 1024];
    int start;
    int end;
    // The buffered bytes after start that hold no newline. The next search
    // starts after them, so a long line costs no repeated scans.
    int scanned;
    int max = limit;

    public int Limit
    {
        get => Volatile.Read(ref max);
        set => Volatile.Write(ref max, value);
    }

    // Returns the next line, or null at the end of the stream. A line longer
    // than the limit throws InvalidDataException.
    public async ValueTask<byte[]?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var newline = buffer.AsSpan(start + scanned, end - start - scanned).IndexOf((byte)'\n');
            if (newline >= 0)
            {
                var length = scanned + newline;
                if (length > Limit)
                {
                    throw new InvalidDataException("packet too large");
                }
                var line = buffer.AsSpan(start, length).ToArray();
                start += length + 1;
                scanned = 0;
                return line;
            }
            scanned = end - start;
            if (scanned > Limit)
            {
                throw new InvalidDataException("packet too large");
            }
            if (end == buffer.Length)
            {
                if (start > 0)
                {
                    buffer.AsSpan(start, end - start).CopyTo(buffer);
                    end -= start;
                    start = 0;
                }
                if (end == buffer.Length)
                {
                    Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, (long)Limit + 2));
                }
            }
            var n = await stream.ReadAsync(buffer.AsMemory(end), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }
            end += n;
        }
    }

    // Reads one line one byte at a time, so that no byte after the newline
    // leaves the stream. The plain-text identity uses it, because the TLS
    // handshake follows on the same stream.
    public static async Task<byte[]> ReadExactLineAsync(Stream stream, int limit, CancellationToken ct)
    {
        var line = new MemoryStream();
        var one = new byte[1];
        while (await stream.ReadAsync(one, ct).ConfigureAwait(false) != 0)
        {
            if (one[0] == (byte)'\n')
            {
                return line.ToArray();
            }
            if (line.Length >= limit)
            {
                throw new InvalidDataException("identity too large");
            }
            line.WriteByte(one[0]);
        }
        throw new EndOfStreamException();
    }
}
