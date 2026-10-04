using System.Globalization;
using System.Runtime.InteropServices;

namespace AltyaziDB.Player.Playback.Interop;

internal static class MpvNative
{
    static MpvNative()
    {
        MpvLibraryResolver.EnsureRegistered();
    }

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint mpv_create();

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_initialize(nint context);

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_destroy(nint context);

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_terminate_destroy(nint context);

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint mpv_wait_event(nint context, double timeout);

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_option_string(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_option(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        nint data);

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_property_string(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_property(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        nint data);

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_get_property(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        nint data);

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint mpv_get_property_string(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_command(nint context, nint arguments);

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint mpv_error_string(int error);

    [DllImport("altyazidb-libmpv", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free(nint data);

    internal static int SetOptionInt64(nint context, string name, long value)
    {
        var data = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            Marshal.WriteInt64(data, value);
            return mpv_set_option(context, name, MpvFormat.Int64, data);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    internal static int SetPropertyDouble(nint context, string name, double value)
    {
        var data = Marshal.AllocHGlobal(sizeof(double));
        try
        {
            Marshal.Copy(new[] { value }, 0, data, 1);
            return mpv_set_property(context, name, MpvFormat.Double, data);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    internal static int SetPropertyFlag(nint context, string name, bool value)
    {
        var data = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(data, value ? 1 : 0);
            return mpv_set_property(context, name, MpvFormat.Flag, data);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    internal static double GetDouble(nint context, string name, double fallback = 0)
    {
        var data = Marshal.AllocHGlobal(sizeof(double));
        try
        {
            return mpv_get_property(context, name, MpvFormat.Double, data) >= 0
                ? Marshal.PtrToStructure<double>(data)
                : fallback;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    internal static long GetInt64(nint context, string name, long fallback = 0)
    {
        var data = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            return mpv_get_property(context, name, MpvFormat.Int64, data) >= 0
                ? Marshal.ReadInt64(data)
                : fallback;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    internal static bool GetFlag(nint context, string name, bool fallback = false)
    {
        var data = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            return mpv_get_property(context, name, MpvFormat.Flag, data) >= 0
                ? Marshal.ReadInt32(data) != 0
                : fallback;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    internal static string? GetString(nint context, string name)
    {
        var value = mpv_get_property_string(context, name);
        if (value == nint.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            mpv_free(value);
        }
    }

    internal static int Command(nint context, params string[] arguments)
    {
        if (arguments.Length == 0)
        {
            throw new ArgumentException("En az bir mpv komutu gereklidir.", nameof(arguments));
        }

        var strings = new nint[arguments.Length];
        var array = nint.Zero;

        try
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                strings[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
            }

            array = Marshal.AllocHGlobal((arguments.Length + 1) * nint.Size);
            for (var index = 0; index < arguments.Length; index++)
            {
                Marshal.WriteIntPtr(array, index * nint.Size, strings[index]);
            }

            Marshal.WriteIntPtr(array, arguments.Length * nint.Size, nint.Zero);
            return mpv_command(context, array);
        }
        finally
        {
            if (array != nint.Zero)
            {
                Marshal.FreeHGlobal(array);
            }

            foreach (var value in strings)
            {
                if (value != nint.Zero)
                {
                    Marshal.FreeCoTaskMem(value);
                }
            }
        }
    }

    internal static string ErrorText(int error)
    {
        var pointer = mpv_error_string(error);
        return pointer == nint.Zero
            ? $"Bilinmeyen mpv hatası ({error.ToString(CultureInfo.InvariantCulture)})"
            : Marshal.PtrToStringUTF8(pointer) ?? $"mpv hatası ({error})";
    }
}
