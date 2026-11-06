using System;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace XivCompanionBridge;

// Only immutable managed snapshots cross this boundary. No game objects reach the worker.
public sealed class StatePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Channel<Snapshot> pending = Channel.CreateBounded<Snapshot>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly string path;
    private readonly Action<Exception> reportError;

    public StatePublisher(string path, Action<Exception> reportError)
    {
        this.path = path;
        this.reportError = reportError;
    }

    public bool Publish(Snapshot snapshot) => pending.Writer.TryWrite(snapshot);
    public void Complete() => pending.Writer.TryComplete();

    public async Task RunAsync()
    {
        var temporary = path + ".tmp";
        var failed = false;
        try
        {
            await foreach (var snapshot in pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, JsonOptions)).ConfigureAwait(false);
                    File.Move(temporary, path, overwrite: true);
                    failed = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    if (!failed) reportError(ex);
                    failed = true;
                }
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
