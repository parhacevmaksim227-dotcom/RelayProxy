using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace RelayProxy.Desktop;

public sealed class InstanceChannel : IDisposable
{
    private static readonly string Name = "RelayProxy-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
    private readonly CancellationTokenSource stop = new();
    private readonly string channelName;
    public InstanceChannel(Action<string> received, string? name = null) { channelName = name ?? Name; _ = ListenAsync(received); }
    private async Task ListenAsync(Action<string> received)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // The tray instance may run elevated while a desktop shortcut does not.
                // Grant only this user's SID; CurrentUserOnly also enforces matching elevation.
                var security = new PipeSecurity();
                var sid = WindowsIdentity.GetCurrent().User!.Value;
                security.SetSecurityDescriptorSddlForm($"D:P(A;;GA;;;{sid})");
                await using var pipe = NamedPipeServerStreamAcl.Create(channelName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, security);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var reader = new StreamReader(pipe);
                var command = await reader.ReadLineAsync(stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2), stop.Token);
                if (command is "show" or "exit" or "connect" or "chrome") received(command);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException ex) { RelayProxy.Core.DiagnosticLog.Write("Instance channel: " + ex.Message); await Task.Delay(250); }
            catch (TimeoutException) { }
            catch (UnauthorizedAccessException) { return; }
        }
    }
    public static bool Send(string command, string? name = null)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", name ?? Name, PipeDirection.Out);
            pipe.Connect(1500);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(command); return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }
    public void Dispose() => stop.Cancel();
}
