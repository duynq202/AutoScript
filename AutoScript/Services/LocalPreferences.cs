using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
namespace AutoScript.Services;

public sealed record CollectorPreferences(string OutputRoot = "", string BatchRoot = "", string Provider = "vidiq_mcp", string McpEndpoint = "https://mcp.vidiq.com/mcp");
public static class LocalPreferences
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoScript");
    public static string ReferencePath => Path.Combine(Root, "default_style.txt");
    public static CollectorPreferences Load()
    {
        try { return JsonSerializer.Deserialize<CollectorPreferences>(File.ReadAllText(Path.Combine(Root, "preferences.json"))) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public static void Save(CollectorPreferences preferences)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "preferences.json"), JsonSerializer.Serialize(preferences));
    }
    public static void SaveReference(string text)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(ReferencePath, text, new UTF8Encoding(false));
    }
}

/// <summary>Windows Credential Manager, per-user credentials, never plaintext settings files.</summary>
public static class CredentialStore
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Write(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Read(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);
    public static void Save(string name, string value)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Credential Manager chỉ hỗ trợ Windows.");
        var bytes = Encoding.Unicode.GetBytes(value);
        var ptr = Marshal.AllocCoTaskMem(bytes.Length);
        try {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = "AutoScript/" + name, CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = ptr, Persist = 2, UserName = "AutoScript" };
            if (!Write(ref credential, 0)) throw new InvalidOperationException("Không lưu được key vào Windows Credential Manager.");
        } finally {
            for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(ptr, i, 0);
            Marshal.FreeCoTaskMem(ptr); Array.Clear(bytes);
        }
    }
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Delete(string target, uint type, uint flags);
    public static void Remove(string name)
    {
        if (OperatingSystem.IsWindows()) Delete("AutoScript/" + name, 1, 0);
    }
    public static string Load(string name)
    {
        if (!OperatingSystem.IsWindows() || !Read("AutoScript/" + name, 1, 0, out var ptr)) return "";
        try {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2) ?? "";
        } finally { CredFree(ptr); }
    }
}
