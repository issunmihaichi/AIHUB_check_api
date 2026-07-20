using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHubRouter.Core;

public sealed class PersistentAppSettings
{
    public bool PersistCredentials { get; init; }
    public string BaseUrl { get; init; } = "https://aihub.top";
    public string Platform { get; init; } = "openai";
    public int MinimumSuccessPercent { get; init; }
    public int PollingIntervalSeconds { get; init; } = 60;
    public bool SmoothRendering { get; init; } = true;
}

public sealed class PersistentCredentials
{
    public string BearerToken { get; init; } = string.Empty;
    public string Cookie { get; init; } = string.Empty;
    public string UserAgent { get; init; } = string.Empty;
}

public sealed record PersistenceSnapshot(
    PersistentAppSettings Settings,
    PersistentCredentials? Credentials);

public sealed class AppSettingsStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = "AIHubRouter/current-user/v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _storageDirectory;
    private readonly string _settingsPath;
    private readonly string _credentialsPath;

    public AppSettingsStore(string? storageDirectory = null)
    {
        _storageDirectory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIHubRouter");
        _settingsPath = Path.Combine(_storageDirectory, "settings.json");
        _credentialsPath = Path.Combine(_storageDirectory, "credentials.dat");
    }

    public PersistenceSnapshot Load()
    {
        var settings = File.Exists(_settingsPath)
            ? JsonSerializer.Deserialize<PersistentAppSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                ?? new PersistentAppSettings()
            : new PersistentAppSettings();

        PersistentCredentials? credentials = null;
        if (settings.PersistCredentials && File.Exists(_credentialsPath))
        {
            var encrypted = File.ReadAllBytes(_credentialsPath);
            var plaintext = Unprotect(encrypted);
            try
            {
                credentials = JsonSerializer.Deserialize<PersistentCredentials>(plaintext, JsonOptions);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        return new PersistenceSnapshot(settings, credentials);
    }

    public void Save(PersistentAppSettings settings, PersistentCredentials? credentials)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(_storageDirectory);
        WriteAtomically(_settingsPath, JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions));

        if (!settings.PersistCredentials || credentials is null)
        {
            ClearCredentials();
            return;
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials, JsonOptions);
        try
        {
            var encrypted = Protect(plaintext);
            WriteAtomically(_credentialsPath, encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void ClearCredentials()
    {
        if (File.Exists(_credentialsPath))
        {
            File.Delete(_credentialsPath);
        }
    }

    private static void WriteAtomically(string destination, byte[] content)
    {
        var temporary = destination + ".tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, destination, overwrite: true);
    }

    private static byte[] Protect(byte[] plaintext)
    {
        var input = CreateBlob(plaintext);
        var entropy = CreateBlob(Entropy);
        var output = default(DataBlob);
        try
        {
            if (!CryptProtectData(
                    ref input,
                    "AIHubRouter credentials",
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 无法加密认证配置。");
            }

            return CopyBlob(output);
        }
        finally
        {
            FreeAllocatedBlob(input);
            FreeAllocatedBlob(entropy);
            FreeLocalBlob(output);
        }
    }

    private static byte[] Unprotect(byte[] encrypted)
    {
        var input = CreateBlob(encrypted);
        var entropy = CreateBlob(Entropy);
        var output = default(DataBlob);
        var description = IntPtr.Zero;
        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    out description,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 无法解密认证配置。");
            }

            return CopyBlob(output);
        }
        finally
        {
            FreeAllocatedBlob(input);
            FreeAllocatedBlob(entropy);
            FreeLocalBlob(output);
            if (description != IntPtr.Zero)
            {
                LocalFree(description);
            }
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { Size = data.Length, Data = pointer };
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        if (blob.Size <= 0 || blob.Data == IntPtr.Zero)
        {
            return [];
        }

        var result = new byte[blob.Size];
        Marshal.Copy(blob.Data, result, 0, blob.Size);
        return result;
    }

    private static void FreeAllocatedBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    private static void FreeLocalBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            LocalFree(blob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
