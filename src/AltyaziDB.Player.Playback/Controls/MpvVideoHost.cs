using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AltyaziDB.Player.Playback.Controls;

public sealed class MpvVideoHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int SsBlackRect = 0x00000004;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmMouseWheel = 0x020A;

    private nint _childHandle;
    private bool _pointerActivityPending;

    public event EventHandler<nint>? HandleCreated;
    public event EventHandler? PointerActivity;

    public nint ChildHandle => _childHandle;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _childHandle = CreateWindowEx(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible | WsClipSiblings | WsClipChildren | SsBlackRect,
            0,
            0,
            Math.Max(1, (int)ActualWidth),
            Math.Max(1, (int)ActualHeight),
            hwndParent.Handle,
            nint.Zero,
            nint.Zero,
            nint.Zero);

        if (_childHandle == nint.Zero)
        {
            throw new InvalidOperationException("Video yüzeyi oluşturulamadı.");
        }

        Dispatcher.InvokeAsync(() => HandleCreated?.Invoke(this, _childHandle));
        return new HandleRef(this, _childHandle);
    }

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg is WmMouseMove or WmLButtonDown or WmLButtonUp or WmRButtonDown or WmMouseWheel)
        {
            // Native mouse traffic can be very chatty. Coalesce messages so fullscreen
            // chrome receives at most one queued activity notification per UI turn.
            if (!_pointerActivityPending)
            {
                _pointerActivityPending = true;
                _ = Dispatcher.InvokeAsync(() =>
                {
                    _pointerActivityPending = false;
                    PointerActivity?.Invoke(this, EventArgs.Empty);
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != nint.Zero)
        {
            DestroyWindow(hwnd.Handle);
        }

        _childHandle = nint.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);

        if (_childHandle != nint.Zero)
        {
            MoveWindow(
                _childHandle,
                0,
                0,
                Math.Max(1, (int)rcBoundingBox.Width),
                Math.Max(1, (int)rcBoundingBox.Height),
                true);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(
        nint window,
        int x,
        int y,
        int width,
        int height,
        [MarshalAs(UnmanagedType.Bool)] bool repaint);
}
