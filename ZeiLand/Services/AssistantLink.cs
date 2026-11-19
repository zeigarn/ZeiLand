using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Windows.Threading;
using ZeiLand.Native;

namespace ZeiLand.Services;

/// <summary>One line on the pipe to ZEI (ZEI's docs/protocol.md); <see cref="Type"/> says which fields matter.</summary>
internal sealed record AssistantMessage
{
    public required string Type { get; init; }
    public string? App { get; init; }
    public string? Version { get; init; }
    public int? Protocol { get; init; }

    /// <summary>assistant: "listening", "thinking" or "idle".</summary>
    public string? State { get; init; }

    /// <summary>reply / confirm: what to show.</summary>
    public string? Text { get; init; }

    /// <summary>reply: "done", "info" or "error".</summary>
    public string? Kind { get; init; }

    /// <summary>timer: "start", "stopwatch" or "stop".</summary>
    public string? Action { get; init; }

    /// <summary>timer: the countdown; reply / confirm / ask: how long to show it.</summary>
    public int? Seconds { get; init; }

    /// <summary>step: why ZEI takes it, in its own words.</summary>
    public string? Detail { get; init; }

    /// <summary>step: which step of the task, from 1.</summary>
    public int? Number { get; init; }

    /// <summary>ask: what may be picked, each a button.</summary>
    public List<string>? Choices { get; init; }
}

/// <summary>
/// The link to ZEI, the voice assistant, when it runs: it shows what it hears and answers on the island
/// and uses the island's timer. ZEI is the server; this side looks for it every few seconds, which is a
/// single failed file open while it isn't there. Messages arrive on the UI thread.
/// </summary>
internal sealed class AssistantLink : IDisposable
{
    private const string PipeName = "ZEI.Island";
    // 2: "step" (a task's steps, live), "ask" (a question with choices to click) and "choose" back.
    private const int ProtocolVersion = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    // Lines for ZEI, written by a loop of their own: a send (a click on the island) never waits on the pipe. An older
    // ZEI's pipe had no buffer, and both sides' first writes waited for each other forever.
    private Channel<string>? _outbox;

    public AssistantLink(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public event Action<AssistantMessage>? Received;

    /// <summary>ZEI came or went.</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected
    {
        get
        {
            lock (_gate) return _outbox is not null;
        }
    }

    public void Start() => _ = Task.Run(RunAsync);

    /// <summary>"cancel" (the user clicked the assistant away), "confirm" (clicked yes) or "choose" (a choice clicked, as <paramref name="text"/>).</summary>
    public void Send(string type, string? text = null)
    {
        string line = JsonSerializer.Serialize(new AssistantMessage { Type = type, Text = text }, Json);
        lock (_gate) _outbox?.Writer.TryWrite(line);
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                // Not PipeOptions.CurrentUserOnly: run as administrator, our token's owner is the Administrators
                // group rather than the user, so .NET's check would refuse ZEI's pipe. IsZei checks
                // the owner against the user instead.
                await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    pipe.Connect(0); // returns at once when ZEI isn't running
                }
                catch (TimeoutException)
                {
                    await Task.Delay(RetryDelay, _stop.Token);
                    continue;
                }
                if (!IsZei(pipe))
                {
                    await Task.Delay(RetryDelay, _stop.Token);
                    continue;
                }
                await ServeAsync(pipe);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(RetryDelay);
            }
        }
    }

    private async Task ServeAsync(NamedPipeClientStream pipe)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        lock (_gate) _outbox = outbox;
        var writing = WriteAsync(writer, outbox.Reader, ended.Token);
        outbox.Writer.TryWrite(JsonSerializer.Serialize(new AssistantMessage
        {
            Type = "hello", App = "ZeiLand", Version = UpdateService.CurrentVersion, Protocol = ProtocolVersion,
        }, Json));
        Post(() => ConnectionChanged?.Invoke(true));
        try
        {
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                AssistantMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<AssistantMessage>(line, Json);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (message is not null) Post(() => Received?.Invoke(message));
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            lock (_gate) _outbox = null;
            outbox.Writer.TryComplete();
            ended.Cancel();
            try
            {
                await writing;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
            try
            {
                await writer.DisposeAsync();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
            }
            Post(() => ConnectionChanged?.Invoke(false));
        }
    }

    /// <summary>The outbox's lines onto the pipe, in order, until it ends or the pipe breaks.</summary>
    private static async Task WriteAsync(StreamWriter writer, ChannelReader<string> lines, CancellationToken cancel)
    {
        try
        {
            await foreach (string line in lines.ReadAllAsync(cancel))
                await writer.WriteLineAsync(line.AsMemory(), cancel);
        }
        catch (IOException)
        {
            // ZEI went: the reading side notices too and starts looking again.
        }
    }

    private void Post(Action action) => _dispatcher.InvokeAsync(action);

    /// <summary>The pipe belongs to this user and is served by ZEI.exe.</summary>
    private static bool IsZei(NamedPipeClientStream pipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != identity.User)
            return false;
        if (!WindowNative.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint id)) return false;
        try
        {
            using var process = Process.GetProcessById((int)id);
            return process.ProcessName.Equals("ZEI", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_gate) _outbox = null;
    }
}
