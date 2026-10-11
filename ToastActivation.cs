using System.Runtime.InteropServices;

namespace CodexMeter;

[ComVisible(true), Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IToastActivation
{
    void Activate([MarshalAs(UnmanagedType.LPWStr)] string appId,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments, IntPtr userInput, uint inputCount);
}

[ComVisible(true), Guid("31D28E52-1CBB-4B37-A288-9A0AEEA5DE40"), ClassInterface(ClassInterfaceType.None)]
public sealed class ToastActivation : IToastActivation
{
    public void Activate(string appId, string arguments, IntPtr userInput, uint inputCount)
    {
        if (appId == DesktopNotifications.AppId && EventWaitHandle.TryOpenExisting(TrayApp.ShowEventName, out var show))
            using (show) show.Set();
    }
}

[ComVisible(true), Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IToastClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid interfaceId, out IntPtr instance);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ToastClassFactory : IToastClassFactory
{
    private readonly ToastActivation callback = new();

    public int CreateInstance(IntPtr outer, ref Guid interfaceId, out IntPtr instance)
    {
        instance = IntPtr.Zero;
        if (outer != IntPtr.Zero) return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
        var unknown = Marshal.GetIUnknownForObject(callback);
        try { return Marshal.QueryInterface(unknown, in interfaceId, out instance); }
        finally { Marshal.Release(unknown); }
    }

    public int LockServer(bool locked) => 0;
}
