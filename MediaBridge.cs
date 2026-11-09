using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace Janela;

sealed class MediaBridge
{
    sealed record Signal(string App, nint Window, long At);
    Signal? current;

    public void Clear() => Volatile.Write(ref current, null);

    public bool Playing(string app, nint hwnd)
    {
        var signal = Volatile.Read(ref current);
        return signal != null && signal.App == app && signal.Window == hwnd &&
            Environment.TickCount64 - signal.At < 2500;
    }

    public async Task Listen(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(700);
                byte[] bytes = new byte[2];
                await pipe.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
                var window = Native.Foreground();
                if (window == null || !(bytes[0] switch
                {
                    1 => window.App is "chrome" or "brave",
                    2 => window.App == "msedge",
                    _ => false
                })) continue;
                if (bytes[1] == 1) Volatile.Write(ref current, new Signal(window.App, window.Handle, Environment.TickCount64));
                else Clear();
            }
            catch (OperationCanceledException) { }
            catch (IOException) { await Task.Delay(100).ConfigureAwait(false); }
        }
    }

    public static async Task Forward(byte app)
    {
        using var input = Console.OpenStandardInput();
        var header = new byte[4];
        while (true)
        {
            try
            {
                await input.ReadExactlyAsync(header);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is < 1 or > 128) return;
                var body = new byte[length];
                await input.ReadExactlyAsync(body);
                using var json = JsonDocument.Parse(body);
                if (!json.RootElement.TryGetProperty("playing", out var value) ||
                    value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;

                using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(400);
                    await pipe.WriteAsync(new byte[] { app, value.GetBoolean() ? (byte)1 : (byte)0 });
                }
                catch (TimeoutException) { }
            }
            catch (IOException) { return; }
            catch (JsonException) { return; }
        }
    }
}
