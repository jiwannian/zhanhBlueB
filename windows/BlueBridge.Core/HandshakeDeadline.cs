namespace BlueBridge.Core;
public static class HandshakeDeadline
{
    public static async Task WaitAsync(Task hello, Task ready, TimeSpan helloTimeout, TimeSpan trustTimeout, CancellationToken token = default)
    {
        await hello.WaitAsync(helloTimeout, token);
        await ready.WaitAsync(trustTimeout, token);
    }
}
