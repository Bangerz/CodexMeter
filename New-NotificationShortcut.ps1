param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$Destination = [IO.Path]::GetFullPath($Destination)
if ([IO.Path]::GetExtension($Destination) -ne '.lnk') { throw 'Destination must be a .lnk file.' }
if (Test-Path -LiteralPath $Destination) { throw 'Refusing to overwrite an existing shortcut.' }
if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($Destination)))) { throw 'Destination directory must already exist.' }

# Create in an explicitly chosen location. Installing in the Start menu is a
# separate deployment step; merely staging a link does not register the app.
$shell = New-Object -ComObject WScript.Shell
try {
    $shortcut = $shell.CreateShortcut($Destination)
    $shortcut.TargetPath = $Executable
    $shortcut.Arguments = '--show'
    $shortcut.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    $shortcut.Description = 'Codex Meter usage and reset notifications'
    $shortcut.Save()
} finally {
    if ($shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MeterShortcutIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Key { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct Variant {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Value;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface Store {
        void GetCount(out uint count);
        void GetAt(uint index, out Key key);
        void GetValue(ref Key key, out Variant value);
        void SetValue(ref Key key, ref Variant value);
        void Commit();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr context, uint flags, ref Guid iid, out Store store);
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref Variant value);

    public static string StampAndVerify(string path) {
        var iid = typeof(Store).GUID;
        Store store;
        SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2, ref iid, out store);
        var format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        var appKey = new Key { Format = format, Id = 5 };
        var clsidKey = new Key { Format = format, Id = 26 };
        var app = new Variant { Type = 31, Value = Marshal.StringToCoTaskMemUni("Bangerz.CodexMeter") };
        var clsid = new Variant { Type = 72, Value = Marshal.AllocCoTaskMem(16) };
        Marshal.StructureToPtr(new Guid("31D28E52-1CBB-4B37-A288-9A0AEEA5DE40"), clsid.Value, false);
        try {
            store.SetValue(ref appKey, ref app);
            store.SetValue(ref clsidKey, ref clsid);
            store.Commit();
        } finally {
            PropVariantClear(ref app);
            PropVariantClear(ref clsid);
            Marshal.FinalReleaseComObject(store);
        }
        // Reopen to validate the persisted properties, not just the in-memory values.
        SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store);
        Variant savedApp = new Variant(), savedClsid = new Variant();
        try {
            store.GetValue(ref appKey, out savedApp);
            store.GetValue(ref clsidKey, out savedClsid);
            string appId = savedApp.Type == 31 ? Marshal.PtrToStringUni(savedApp.Value) : null;
            var activation = savedClsid.Type == 72 ? (Guid)Marshal.PtrToStructure(savedClsid.Value, typeof(Guid)) : Guid.Empty;
            if (appId != "Bangerz.CodexMeter" || activation != new Guid("31D28E52-1CBB-4B37-A288-9A0AEEA5DE40"))
                throw new InvalidOperationException("Shortcut identity verification failed.");
            return appId + " / " + activation;
        } finally {
            PropVariantClear(ref savedApp);
            PropVariantClear(ref savedClsid);
            Marshal.FinalReleaseComObject(store);
        }
    }
}
'@
[MeterShortcutIdentity]::StampAndVerify($Destination)
Write-Output "Prepared: $Destination"
