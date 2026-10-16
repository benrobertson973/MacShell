using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacShell.Native;

/// <summary>
/// Just enough of the Windows Runtime to call a few system APIs (radios, notifications, Focus) without the 25 MB
/// projection assembly: activation factories, HSTRINGs, calls through the interface's vtable, and waiting for an
/// async operation (polled - so call these off the UI thread). Slots: 0-2 IUnknown, 3-5 IInspectable, methods from 6.
/// </summary>
public static unsafe class WinRt
{
    [DllImport("combase.dll")] static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] static extern char* WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll")] static extern int RoInitialize(int initType);

    static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-C000-000000000046");

    /// <summary>The calling thread joins the multithreaded apartment if it has none yet (worker threads).</summary>
    public static void Init() => RoInitialize(1);

    public static IntPtr Factory(string className, string iid)
    {
        Init();
        var g = new Guid(iid);
        IntPtr h = String(className);
        try { Check(RoGetActivationFactory(h, ref g, out var f)); return f; }
        finally { WindowsDeleteString(h); }
    }

    public static IntPtr String(string s) { WindowsCreateString(s, s.Length, out var h); return h; }
    public static void FreeString(IntPtr h) { if (h != IntPtr.Zero) WindowsDeleteString(h); }

    /// <summary>The text of an HSTRING the callee handed over (and its release).</summary>
    public static string TakeString(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        char* p = WindowsGetStringRawBuffer(h, out uint len);
        var s = new string(p, 0, (int)len);
        WindowsDeleteString(h);
        return s;
    }

    public static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    public static void Release(IntPtr p) { if (p != IntPtr.Zero) Marshal.Release(p); }

    public static IntPtr As(IntPtr obj, string iid)
    {
        var g = new Guid(iid);
        Check(Marshal.QueryInterface(obj, ref g, out var p));
        return p;
    }

    static void* Slot(IntPtr obj, int slot) => (*(void***)obj)[slot];

    public static IntPtr GetPtr(IntPtr obj, int slot)
    {
        IntPtr r = IntPtr.Zero;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(obj, slot))(obj, &r));
        return r;
    }

    public static int GetInt(IntPtr obj, int slot)
    {
        int r = 0;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(obj, slot))(obj, &r));
        return r;
    }

    public static bool GetBool(IntPtr obj, int slot)
    {
        byte r = 0;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Slot(obj, slot))(obj, &r));
        return r != 0;
    }

    public static string GetString(IntPtr obj, int slot) => TakeString(GetPtr(obj, slot));

    /// <summary>A method taking one 32-bit value (an index, an enum) and returning an object.</summary>
    public static IntPtr CallIntPtr(IntPtr obj, int slot, int arg)
    {
        IntPtr r = IntPtr.Zero;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Slot(obj, slot))(obj, arg, &r));
        return r;
    }

    /// <summary>A method taking one pointer (an HSTRING, an object) and returning an object.</summary>
    public static IntPtr CallPtrPtr(IntPtr obj, int slot, IntPtr arg)
    {
        IntPtr r = IntPtr.Zero;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(obj, slot))(obj, arg, &r));
        return r;
    }

    public static void CallVoid(IntPtr obj, int slot) => Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(obj, slot))(obj));

    /// <summary>Waits until an IAsyncOperation/IAsyncAction is done: true if it completed (then call GetResults, slot 8).</summary>
    public static bool Wait(IntPtr op, int timeoutMs = 10000)
    {
        IntPtr info = As(op, "00000036-0000-0000-C000-000000000046");
        try
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                int status = GetInt(info, 7);   // Started 0, Completed 1, Canceled 2, Error 3
                if (status == 1) return true;
                if (status >= 2) return false;
                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    try { CallVoid(info, 9); } catch { }   // Cancel
                    return false;
                }
                Thread.Sleep(15);
            }
        }
        finally { Release(info); }
    }

    /// <summary>The objects in an IVectorView (each to be released by the caller).</summary>
    public static List<IntPtr> Items(IntPtr vectorView)
    {
        var list = new List<IntPtr>();
        int n = GetInt(vectorView, 7);   // get_Size
        for (int i = 0; i < n; i++) list.Add(CallIntPtr(vectorView, 6, i));   // GetAt
        return list;
    }
}
