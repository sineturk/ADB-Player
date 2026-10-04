using System.Runtime.InteropServices;
using System.Text;

namespace AltyaziDB.Player.Telegram.Interop;

internal sealed class TdJsonNativeLibrary : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TdCreateClientIdDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TdSendDelegate(int clientId, nint request);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint TdReceiveDelegate(double timeout);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint TdExecuteDelegate(nint request);

    private readonly nint _handle;
    private readonly TdCreateClientIdDelegate _createClientId;
    private readonly TdSendDelegate _send;
    private readonly TdReceiveDelegate _receive;
    private readonly TdExecuteDelegate _execute;
    private bool _disposed;

    private TdJsonNativeLibrary(
        nint handle,
        string loadedPath,
        TdCreateClientIdDelegate createClientId,
        TdSendDelegate send,
        TdReceiveDelegate receive,
        TdExecuteDelegate execute)
    {
        _handle = handle;
        LoadedPath = loadedPath;
        _createClientId = createClientId;
        _send = send;
        _receive = receive;
        _execute = execute;
    }

    public string LoadedPath { get; }

    public static bool TryLoad(out TdJsonNativeLibrary? library, out string diagnostic)
    {
        library = null;
        var attempts = new List<string>();
        foreach (var candidate in CandidatePaths())
        {
            try
            {
                attempts.Add(candidate);
                if (!File.Exists(candidate)) continue;
                var handle = NativeLibrary.Load(candidate);
                try
                {
                    var create = Marshal.GetDelegateForFunctionPointer<TdCreateClientIdDelegate>(
                        NativeLibrary.GetExport(handle, "td_create_client_id"));
                    var send = Marshal.GetDelegateForFunctionPointer<TdSendDelegate>(
                        NativeLibrary.GetExport(handle, "td_send"));
                    var receive = Marshal.GetDelegateForFunctionPointer<TdReceiveDelegate>(
                        NativeLibrary.GetExport(handle, "td_receive"));
                    var execute = Marshal.GetDelegateForFunctionPointer<TdExecuteDelegate>(
                        NativeLibrary.GetExport(handle, "td_execute"));
                    library = new TdJsonNativeLibrary(handle, candidate, create, send, receive, execute);
                    diagnostic = $"TDLib yüklendi: {candidate}";
                    return true;
                }
                catch
                {
                    NativeLibrary.Free(handle);
                    throw;
                }
            }
            catch (Exception exception)
            {
                attempts.Add($"{candidate} => {exception.Message}");
            }
        }

        diagnostic = "tdjson.dll bulunamadı veya bağımlılıkları yüklenemedi. Denenen yollar:\n" +
                     string.Join("\n", attempts.Distinct(StringComparer.OrdinalIgnoreCase));
        return false;
    }

    public int CreateClientId() => _createClientId();

    public void Send(int clientId, string json)
    {
        var pointer = Utf8(json);
        try { _send(clientId, pointer); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    public string? Receive(double timeout)
    {
        var pointer = _receive(timeout);
        return pointer == nint.Zero ? null : Marshal.PtrToStringUTF8(pointer);
    }

    public string? Execute(string json)
    {
        var pointer = Utf8(json);
        try
        {
            var result = _execute(pointer);
            return result == nint.Zero ? null : Marshal.PtrToStringUTF8(result);
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    private static nint Utf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        var pointer = Marshal.AllocCoTaskMem(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var root = AppContext.BaseDirectory;
        yield return Path.Combine(root, "tdjson.dll");
        yield return Path.Combine(root, "native", "telegram", "tdjson.dll");
        yield return Path.Combine(root, "runtimes", "win-x64", "native", "tdjson.dll");
        yield return Path.Combine(Environment.CurrentDirectory, "native", "telegram", "tdjson.dll");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeLibrary.Free(_handle);
    }
}
