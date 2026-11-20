using System.Security.Cryptography;
using System.Text.Json;

namespace SteamFrame.PatchMaintenance;

public sealed class MaintenanceStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    private string FilePath(string name)
    {
        if (Path.GetFileName(name) != name || name.Contains(':')) throw new ArgumentException("Expected a maintenance file name.");
        return Path.Combine(DirectoryPath, name);
    }
    public T? Read<T>(string name)
    {
        var path = FilePath(name);
        MaintenancePaths.RejectReparseAncestors(path);
        if (!File.Exists(path)) return default;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1_048_576) throw new InvalidDataException("Maintenance state exceeds the size limit.");
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Maintenance state is empty.");
    }

    public void Write<T>(string name, T value)
    {
        MaintenancePaths.RejectReparseAncestors(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        var path = FilePath(name);
        MaintenancePaths.RejectReparseAncestors(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public bool HasCredentials => File.Exists(Path.Combine(DirectoryPath, "credentials.json"));
    public PushoverCredentials? ReadCredentials()
    {
        var encrypted = Read<byte[]>("credentials.json");
        if (encrypted is null) return null;
        var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
        try { var result = JsonSerializer.Deserialize<PushoverCredentials>(bytes) ?? throw new InvalidDataException("Invalid credentials."); result.Validate(); return result; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void SaveCredentials(PushoverCredentials credentials)
    {
        credentials.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try { Write("credentials.json", ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void RemoveCredentials()
        => Remove("credentials.json");
    public void Remove(string name)
    {
        var path = FilePath(name);
        MaintenancePaths.RejectReparseAncestors(path);
        if (File.Exists(path)) File.Delete(path);
    }
}
