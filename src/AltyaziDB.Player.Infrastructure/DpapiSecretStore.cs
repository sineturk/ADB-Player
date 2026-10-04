using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;

namespace AltyaziDB.Player.Infrastructure;

public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AltyaziDB.Player.Windows.W3");
    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DpapiSecretStore(AppPaths paths, IAppLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var values = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (!values.TryGetValue(key, out var protectedValue) || string.IsNullOrWhiteSpace(protectedValue))
                return null;

            try
            {
                var encrypted = Convert.FromBase64String(protectedValue);
                var clear = Unprotect(encrypted, Entropy);
                return Encoding.UTF8.GetString(clear);
            }
            catch (Exception exception)
            {
                _logger.Error($"Gizli değer çözülemedi: {key}", exception);
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var values = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(value))
            {
                values.Remove(key);
            }
            else
            {
                var clear = Encoding.UTF8.GetBytes(value);
                values[key] = Convert.ToBase64String(Protect(clear, Entropy));
            }

            await WriteAllAsync(values, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        SetAsync(key, null, cancellationToken);

    private async Task<Dictionary<string, string>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.SecretsFile))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            await using var stream = File.OpenRead(_paths.SecretsFile);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception exception)
        {
            _logger.Error("Gizli ayarlar dosyası okunamadı.", exception);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private async Task WriteAllAsync(Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.SecretsFile)!);
        var temp = _paths.SecretsFile + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, values, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, _paths.SecretsFile, overwrite: true);
    }

    private static byte[] Protect(byte[] clearText, byte[] entropy)
    {
        var clearBlob = ToBlob(clearText);
        var entropyBlob = ToBlob(entropy);
        try
        {
            if (!CryptProtectData(ref clearBlob, "AltyaziDB Player", ref entropyBlob, nint.Zero, nint.Zero, 0x1, out var protectedBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                var result = new byte[protectedBlob.cbData];
                Marshal.Copy(protectedBlob.pbData, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (protectedBlob.pbData != nint.Zero) LocalFree(protectedBlob.pbData);
            }
        }
        finally
        {
            FreeBlob(ref clearBlob);
            FreeBlob(ref entropyBlob);
        }
    }

    private static byte[] Unprotect(byte[] cipherText, byte[] entropy)
    {
        var cipherBlob = ToBlob(cipherText);
        var entropyBlob = ToBlob(entropy);
        try
        {
            if (!CryptUnprotectData(ref cipherBlob, nint.Zero, ref entropyBlob, nint.Zero, nint.Zero, 0x1, out var clearBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                var result = new byte[clearBlob.cbData];
                Marshal.Copy(clearBlob.pbData, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (clearBlob.pbData != nint.Zero) LocalFree(clearBlob.pbData);
            }
        }
        finally
        {
            FreeBlob(ref cipherBlob);
            FreeBlob(ref entropyBlob);
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { cbData = data.Length, pbData = pointer };
    }

    private static void FreeBlob(ref DataBlob blob)
    {
        if (blob.pbData != nint.Zero)
        {
            Marshal.FreeHGlobal(blob.pbData);
            blob.pbData = nint.Zero;
            blob.cbData = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public nint pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        nint pvReserved,
        nint pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        nint ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        nint pvReserved,
        nint pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint hMem);
}
