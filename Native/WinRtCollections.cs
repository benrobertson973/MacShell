using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MacShell.Native;

/// <summary>
/// A list of strings handed to the Windows Runtime (IIterable&lt;HSTRING&gt;), e.g. the extra properties a device query
/// asks for: a tiny agile COM object made here, so no projection assembly is needed.
/// </summary>
public static unsafe class WinRtStrings
{
    static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    static readonly Guid IID_IIterable = new("E2FCC7C1-3BFC-5A0B-B2B0-72E769D1CB7E");   // IIterable<String>
    static readonly Guid IID_IIterator = new("8C304EBB-6615-50A4-8829-879ECD443236");   // IIterator<String>

    [StructLayout(LayoutKind.Sequential)]
    struct Obj { public void** Vtbl; public int Ref; public int Kind; public IntPtr State; }   // Kind 0 iterable, 1 iterator

    sealed class IterState { public string[] Items; public int Pos; }

    static readonly void** IterableVtbl = MakeIterableVtbl();
    static readonly void** IteratorVtbl = MakeIteratorVtbl();

    static void** MakeIterableVtbl()
    {
        var v = (void**)NativeMemory.Alloc((nuint)(7 * sizeof(void*)));
        v[0] = (delegate* unmanaged[Stdcall]<Obj*, Guid*, void**, int>)&QueryInterface;
        v[1] = (delegate* unmanaged[Stdcall]<Obj*, uint>)&AddRef;
        v[2] = (delegate* unmanaged[Stdcall]<Obj*, uint>)&Release;
        v[3] = (delegate* unmanaged[Stdcall]<Obj*, uint*, Guid**, int>)&GetIids;
        v[4] = (delegate* unmanaged[Stdcall]<Obj*, IntPtr*, int>)&GetRuntimeClassName;
        v[5] = (delegate* unmanaged[Stdcall]<Obj*, int*, int>)&GetTrustLevel;
        v[6] = (delegate* unmanaged[Stdcall]<Obj*, Obj**, int>)&First;
        return v;
    }

    static void** MakeIteratorVtbl()
    {
        var v = (void**)NativeMemory.Alloc((nuint)(10 * sizeof(void*)));
        v[0] = (delegate* unmanaged[Stdcall]<Obj*, Guid*, void**, int>)&QueryInterface;
        v[1] = (delegate* unmanaged[Stdcall]<Obj*, uint>)&AddRef;
        v[2] = (delegate* unmanaged[Stdcall]<Obj*, uint>)&Release;
        v[3] = (delegate* unmanaged[Stdcall]<Obj*, uint*, Guid**, int>)&GetIids;
        v[4] = (delegate* unmanaged[Stdcall]<Obj*, IntPtr*, int>)&GetRuntimeClassName;
        v[5] = (delegate* unmanaged[Stdcall]<Obj*, int*, int>)&GetTrustLevel;
        v[6] = (delegate* unmanaged[Stdcall]<Obj*, IntPtr*, int>)&GetCurrent;
        v[7] = (delegate* unmanaged[Stdcall]<Obj*, byte*, int>)&GetHasCurrent;
        v[8] = (delegate* unmanaged[Stdcall]<Obj*, byte*, int>)&MoveNext;
        v[9] = (delegate* unmanaged[Stdcall]<Obj*, uint, IntPtr*, uint*, int>)&GetMany;
        return v;
    }

    /// <summary>A new IIterable&lt;HSTRING&gt; of these strings (one reference: Release it when done).</summary>
    public static IntPtr Create(params string[] items) => (IntPtr)New(0, new IterState { Items = items ?? Array.Empty<string>() });

    static Obj* New(int kind, IterState state)
    {
        var o = (Obj*)NativeMemory.Alloc((nuint)sizeof(Obj));
        o->Vtbl = kind == 0 ? IterableVtbl : IteratorVtbl;
        o->Ref = 1;
        o->Kind = kind;
        o->State = GCHandle.ToIntPtr(GCHandle.Alloc(state));
        return o;
    }

    static IterState S(Obj* o) => (IterState)GCHandle.FromIntPtr(o->State).Target;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int QueryInterface(Obj* self, Guid* iid, void** ppv)
    {
        var g = *iid;
        if (g == IID_IUnknown || g == IID_IInspectable || g == IID_IAgileObject || g == (self->Kind == 0 ? IID_IIterable : IID_IIterator))
        {
            Interlocked.Increment(ref self->Ref);
            *ppv = self;
            return 0;
        }
        *ppv = null;
        return unchecked((int)0x80004002);   // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static uint AddRef(Obj* self) => (uint)Interlocked.Increment(ref self->Ref);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static uint Release(Obj* self)
    {
        int r = Interlocked.Decrement(ref self->Ref);
        if (r == 0)
        {
            GCHandle.FromIntPtr(self->State).Free();
            NativeMemory.Free(self);
        }
        return (uint)r;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetIids(Obj* self, uint* count, Guid** iids)
    {
        var p = (Guid*)Marshal.AllocCoTaskMem(sizeof(Guid));
        *p = self->Kind == 0 ? IID_IIterable : IID_IIterator;
        *count = 1;
        *iids = p;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetRuntimeClassName(Obj* self, IntPtr* name) { *name = IntPtr.Zero; return 0; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetTrustLevel(Obj* self, int* level) { *level = 0; return 0; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int First(Obj* self, Obj** iterator)
    {
        *iterator = New(1, new IterState { Items = S(self).Items, Pos = 0 });
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetCurrent(Obj* self, IntPtr* value)
    {
        var s = S(self);
        if (s.Pos >= s.Items.Length) { *value = IntPtr.Zero; return unchecked((int)0x8000000B); }   // E_BOUNDS
        *value = WinRt.String(s.Items[s.Pos]);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetHasCurrent(Obj* self, byte* has) { var s = S(self); *has = (byte)(s.Pos < s.Items.Length ? 1 : 0); return 0; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int MoveNext(Obj* self, byte* has)
    {
        var s = S(self);
        if (s.Pos < s.Items.Length) s.Pos++;
        *has = (byte)(s.Pos < s.Items.Length ? 1 : 0);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetMany(Obj* self, uint capacity, IntPtr* items, uint* actual)
    {
        var s = S(self);
        uint n = 0;
        while (n < capacity && s.Pos < s.Items.Length) items[n++] = WinRt.String(s.Items[s.Pos++]);
        *actual = n;
        return 0;
    }
}

/// <summary>Reading WinRT property bags (IMapView&lt;String, Object&gt;) of boxed values (IPropertyValue).</summary>
public static unsafe class WinRtProps
{
    const string IPropertyValue = "4BD682DD-7554-40E9-9A9B-82654EDE7E62";

    /// <summary>A value from an IMapView&lt;String, Object&gt; (null when it's missing): bool, string, uint, int, byte, Guid or double.</summary>
    public static object Lookup(IntPtr map, string key)
    {
        IntPtr h = WinRt.String(key);
        try
        {
            byte has = 0;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, int>)(*(void***)map)[8])(map, h, &has);   // HasKey
            if (hr < 0 || has == 0) return null;
            IntPtr v = IntPtr.Zero;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)(*(void***)map)[6])(map, h, &v);   // Lookup
            if (hr < 0 || v == IntPtr.Zero) return null;
            try { return Unbox(v); } finally { WinRt.Release(v); }
        }
        catch { return null; }
        finally { WinRt.FreeString(h); }
    }

    public static object Unbox(IntPtr inspectable)
    {
        IntPtr pv;
        try { pv = WinRt.As(inspectable, IPropertyValue); } catch { return null; }
        try
        {
            int type = WinRt.GetInt(pv, 6);
            switch (type)
            {
                case 1: { byte r = 0; WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)(*(void***)pv)[8])(pv, &r)); return r; }
                case 4: return WinRt.GetInt(pv, 11);
                case 5: return (uint)WinRt.GetInt(pv, 12);
                case 11: return WinRt.GetBool(pv, 18);
                case 12: return WinRt.GetString(pv, 19);
                case 16: { Guid g; WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)(*(void***)pv)[20])(pv, &g)); return g; }
                case 9: { double d; WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, double*, int>)(*(void***)pv)[16])(pv, &d)); return d; }
                default: return null;
            }
        }
        catch { return null; }
        finally { WinRt.Release(pv); }
    }
}
