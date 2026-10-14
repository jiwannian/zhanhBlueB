using System.Text;
using BlueBridge.Core;

// Test-only peer: stdout is exclusively protocol bytes. Never include this tool in the desktop package.
try
{
    string directory = ""; int expected = 0;
    List<string> files = [], texts = [], images = [];
    for (int i = 0; i < args.Length; i++)
    {
        string option = args[i];
        if (++i >= args.Length) throw new ArgumentException("Missing option value");
        switch (option)
        {
            case "--receive-dir": directory = args[i]; break;
            case "--send-file": files.Add(args[i]); break;
            case "--send-text-base64": texts.Add(Encoding.UTF8.GetString(Convert.FromBase64String(args[i]))); break;
            case "--send-image": images.Add(args[i]); break;
            case "--expect": expected = int.Parse(args[i]); break;
            default: throw new ArgumentException("Unknown option: " + option);
        }
    }
    if (string.IsNullOrEmpty(directory) || expected < 0) throw new ArgumentException("--receive-dir and a nonnegative --expect are required");
    Directory.CreateDirectory(directory);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int count = 0;
    if (expected == 0) received.SetResult();
    void CountReceived() { if (Interlocked.Increment(ref count) >= expected) received.TrySetResult(); }
    await using var session = new BridgeSession(Console.OpenStandardInput(), Console.OpenStandardOutput(), new(Guid.NewGuid(), "Windows test peer"), directory, new()
    {
        ApprovePeer = _ => Task.FromResult(true),
        ReceiveText = async (id, text) => { await File.WriteAllTextAsync(Path.Combine(directory, $"text-{id}.txt"), text, new UTF8Encoding(false), deadline.Token); CountReceived(); },
        ReceiveItem = _ => { CountReceived(); return Task.CompletedTask; },
        Status = status => Console.Error.WriteLine(status),
        TransferFailed = (id, reason) => Console.Error.WriteLine($"Transfer {id}: {reason}")
    });
    var run = session.RunAsync(deadline.Token);
    async Task Exchange()
    {
        await session.Ready.WaitAsync(deadline.Token);
        foreach (string text in texts) await session.SendTextAsync(text);
        foreach (string file in files) await session.SendFileAsync(file, token: deadline.Token);
        foreach (string image in images) await session.SendFileAsync(image, image: true, token: deadline.Token);
        await received.Task.WaitAsync(deadline.Token);
    }
    var exchange = Exchange();
    Task first = await Task.WhenAny(run, exchange);
    if (first == run && !exchange.IsCompletedSuccessfully) { await run; throw new IOException("Disconnected before expected receive count"); }
    await exchange;
    Console.Error.WriteLine($"Completed; received {count}");
    await session.DisposeAsync();
    try { await run; } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    return 0;
}
catch (Exception error) { Console.Error.WriteLine($"Failed: {error.GetType().Name}: {error.Message}"); return 1; }
