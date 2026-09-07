using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Scalemon.Common.Updates;

/// <summary>Ограниченный локальный протокол служб; размер сообщения не более 256 КБ.</summary>
public static class LocalUpdatePipe
{
    public const string Updater = "Scalemon.Updater.v1";
    public const string Application = "Scalemon.Application.v1";
    /// <summary>Отправляет одну ограниченную команду проверенной локальной службе.</summary>
    public static async Task<UpdateReply> CallAsync(string name, UpdateRequest request, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5000, ct);
        PipeServerTrust.Verify(pipe.SafePipeHandle, name);
        await WriteAsync(pipe, request, ct);
        return await ReadAsync<UpdateReply>(pipe, ct);
    }
    /// <summary>Обслуживает локальные команды с ACL, таймаутом и ограничением параллелизма.</summary>
    public static async Task ServeAsync(string name, Func<UpdateRequest, CancellationToken, Task<UpdateReply>> handler,
        Action<Exception> log, CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(4);
        var sessions = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await slots.WaitAsync(ct);
                NamedPipeServerStream? pipe = null;
                try
                {
                    var security = new PipeSecurity();
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                    var serviceName = AtomicJson.Read<InstallationRecord>(InstallationPaths.Installation)?.ServiceName ?? "Scalemon";
                    foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
                    var applicationSid = (SecurityIdentifier)new NTAccount("NT SERVICE", serviceName).Translate(typeof(SecurityIdentifier));
                    security.AddAccessRule(new PipeAccessRule(applicationSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 4,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
                    await pipe.WaitForConnectionAsync(ct);
                    var connected = pipe; pipe = null;
                    sessions.RemoveAll(t => t.IsCompleted);
                    sessions.Add(HandleConnectionAsync(connected));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                { pipe?.Dispose(); slots.Release(); break; }
                catch (Exception ex)
                { pipe?.Dispose(); slots.Release(); log(ex); await Task.Delay(1000, ct); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(sessions); }

        async Task HandleConnectionAsync(NamedPipeServerStream connected)
        {
            using (connected)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(130));
                try
                {
                    var request = await ReadAsync<UpdateRequest>(connected, timeout.Token);
                    request = request with { Actor = connected.GetImpersonationUserName() + (string.IsNullOrEmpty(request.Actor) ? "" : " / " + request.Actor) };
                    UpdateReply reply;
                    try { reply = await handler(request, timeout.Token); }
                    catch (Exception ex) { log(ex); reply = new(false, "Операция не выполнена: " + ex.Message); }
                    await WriteAsync(connected, reply, timeout.Token);
                }
                catch (Exception ex)
                { if (!ct.IsCancellationRequested) log(ex); }
                finally { slots.Release(); }
            }
        }
    }
    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > 262144) throw new InvalidDataException("Недопустимый размер сообщения.");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes, AtomicJson.Options) ?? throw new InvalidDataException("Пустое сообщение.");
    }
    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AtomicJson.Options);
        if (bytes.Length > 262144) throw new InvalidDataException("Сообщение слишком велико.");
        var header = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
}
/// <summary>Клиент Updater для веб-интерфейса и локального обслуживания.</summary>
public sealed class UpdaterPipeClient : IUpdaterClient
{
    /// <inheritdoc />
    public Task<UpdateReply> SendAsync(UpdateRequest request, CancellationToken cancellationToken = default)
        => LocalUpdatePipe.CallAsync(LocalUpdatePipe.Updater, request, cancellationToken);
}
