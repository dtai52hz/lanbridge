using System.Runtime.InteropServices;

namespace LanBridge.CrossPlatform;

// Xlib's long/Window/Time are native unsigned longs: supported desktop RIDs are 64-bit.
internal static class LinuxX11Native
{
    private const string X11 = "libX11.so.6";
    private const string Xi = "libXi.so.6";

    [StructLayout(LayoutKind.Sequential)]
    internal struct GenericCookie
    {
        public int Type; public nuint Serial; public int SendEvent; public nint Display;
        public int Extension, EventType; public uint Cookie; public nint Data;
    }
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    internal struct Event
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(0)] public GenericCookie Cookie;
        [FieldOffset(0)] public MappingEvent Mapping;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MappingEvent
    {
        public int Type; public nuint Serial; public int SendEvent; public nint Display;
        public nuint Window; public int Request, FirstKeyCode, Count;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Valuators { public int MaskLength; public nint Mask, Values; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawEvent
    {
        public int Type; public nuint Serial; public int SendEvent; public nint Display;
        public int Extension, EventType; public nuint Time;
        public int DeviceId, SourceId, Detail, Flags; public Valuators Valuators; public nint RawValues;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct EventMask { public int DeviceId, MaskLength; public nint Mask; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfo
    {
        public int DeviceId; public nint Name; public int Use, Attachment, Enabled, NumberOfClasses;
        public nint Classes;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardState
    {
        public byte Group, LockedGroup; public ushort BaseGroup, LatchedGroup;
        public byte Mods, BaseMods, LatchedMods, LockedMods, CompatState, GrabMods,
            CompatGrabMods, LookupMods, CompatLookupMods;
        public ushort PointerButtons;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowAttributes
    {
        public nuint BackgroundPixmap, BackgroundPixel, BorderPixmap, BorderPixel;
        public int BitGravity, WindowGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel; public int SaveUnder;
        public nint EventMask, DoNotPropagateMask; public int OverrideRedirect;
        public nuint Colormap, Cursor;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Color { public nuint Pixel; public ushort Red, Green, Blue; public byte Flags, Pad; }

    [DllImport(X11)] internal static extern nint XOpenDisplay(nint name);
    [DllImport(X11)] internal static extern int XCloseDisplay(nint display);
    [DllImport(X11)] internal static extern int XDefaultScreen(nint display);
    [DllImport(X11)] internal static extern nuint XRootWindow(nint display, int screen);
    [DllImport(X11)] internal static extern int XDisplayWidth(nint display, int screen);
    [DllImport(X11)] internal static extern int XDisplayHeight(nint display, int screen);
    [DllImport(X11)] internal static extern int XQueryExtension(nint display,
        [MarshalAs(UnmanagedType.LPStr)] string name, out int opcode, out int firstEvent, out int firstError);
    [DllImport(X11)] internal static extern int XQueryPointer(nint display, nuint window, out nuint root,
        out nuint child, out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);
    [DllImport(X11)] internal static extern int XPending(nint display);
    [DllImport(X11)] internal static extern int XNextEvent(nint display, out Event input);
    [DllImport(X11)] internal static extern int XRefreshKeyboardMapping(ref MappingEvent input);
    [DllImport(X11)] internal static extern int XGetEventData(nint display, ref GenericCookie cookie);
    [DllImport(X11)] internal static extern void XFreeEventData(nint display, ref GenericCookie cookie);
    [DllImport(X11)] internal static extern int XFlush(nint display);
    [DllImport(X11)] internal static extern int XSync(nint display, int discard);
    [DllImport(X11)] internal static extern int XWarpPointer(nint display, nuint source, nuint destination,
        int sourceX, int sourceY, uint sourceWidth, uint sourceHeight, int destinationX, int destinationY);
    [DllImport(X11)] internal static extern int XGrabKeyboard(nint display, nuint window, int ownerEvents,
        int pointerMode, int keyboardMode, nuint time);
    [DllImport(X11)] internal static extern int XUngrabKeyboard(nint display, nuint time);
    [DllImport(X11)] internal static extern int XGrabPointer(nint display, nuint window, int ownerEvents,
        uint eventMask, int pointerMode, int keyboardMode, nuint confineTo, nuint cursor, nuint time);
    [DllImport(X11)] internal static extern int XUngrabPointer(nint display, nuint time);
    [DllImport(X11)] internal static extern nuint XCreateWindow(nint display, nuint parent, int x, int y,
        uint width, uint height, uint borderWidth, int depth, uint windowClass, nint visual,
        nuint valueMask, ref WindowAttributes attributes);
    [DllImport(X11)] internal static extern int XMoveWindow(nint display, nuint window, int x, int y);
    [DllImport(X11)] internal static extern int XMapWindow(nint display, nuint window);
    [DllImport(X11)] internal static extern int XUnmapWindow(nint display, nuint window);
    [DllImport(X11)] internal static extern int XDestroyWindow(nint display, nuint window);
    [DllImport(X11)] internal static extern nuint XCreateBitmapFromData(nint display, nuint drawable,
        byte[] data, uint width, uint height);
    [DllImport(X11)] internal static extern nuint XCreatePixmapCursor(nint display, nuint source,
        nuint mask, ref Color foreground, ref Color background, uint x, uint y);
    [DllImport(X11)] internal static extern int XFreePixmap(nint display, nuint pixmap);
    [DllImport(X11)] internal static extern int XFreeCursor(nint display, nuint cursor);
    [DllImport(X11)] internal static extern nuint XkbKeycodeToKeysym(nint display, byte key, int group, int level);
    [DllImport(X11)] internal static extern int XkbGetState(nint display, uint device, out KeyboardState state);
    [DllImport(X11)] internal static extern int XkbLookupKeySym(nint display, byte key, uint state,
        out uint modifiers, out nuint symbol);
    [DllImport(Xi)] internal static extern int XIQueryVersion(nint display, ref int major, ref int minor);
    [DllImport(Xi)] internal static extern int XISelectEvents(nint display, nuint window,
        ref EventMask masks, int count);
    [DllImport(Xi)] internal static extern nint XIQueryDevice(nint display, int device, out int count);
    [DllImport(Xi)] internal static extern void XIFreeDeviceInfo(nint info);
    [DllImport("libxkbcommon.so.0")] internal static extern uint xkb_keysym_to_utf32(uint symbol);
}
