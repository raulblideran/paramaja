using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Paramaja.App.Services;

/// <summary>
/// Drag and drop on Linux. Avalonia 11's X11 backend has no XDND (12 has), so nothing dropped on
/// the player ever arrived: not from Dolphin, a browser or any other app. This is XDND's drop side
/// (version 5) for the player's windows: they're marked XdndAware, and the protocol's messages,
/// which X delivers to Avalonia's connection (the windows are its own), are taken out of Avalonia's
/// event dispatch for the window before it sees them. KDE's bridge for Wayland apps (KWin's
/// Xwayland drag) checks XdndAware on the app's own window and sends its messages straight there,
/// as an X11 app's drag does.
/// </summary>
public static partial class X11DragDrop
{
    private const string X11 = "libX11.so.6";
    private const int ClientMessage = 33;
    private const int SelectionNotify = 31;
    private const int PropModeReplace = 0;
    private const int XdndVersion = 5;
    private static readonly IntPtr XaAtom = 4;
    private static readonly IntPtr AnyPropertyType = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct XClientMessageEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Window;
        public IntPtr MessageType;
        public int Format;
        public nint L0, L1, L2, L3, L4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XSelectionEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Requestor;
        public IntPtr Selection;
        public IntPtr Target;
        public IntPtr Property;
        public nuint Time;
    }

    [LibraryImport(X11)] private static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport(X11, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [LibraryImport(X11)]
    private static partial int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format,
        int mode, ref IntPtr data, int elements);
    [LibraryImport(X11)]
    private static partial int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, nint offset, nint length,
        [MarshalAs(UnmanagedType.Bool)] bool delete, IntPtr type, out IntPtr actualType, out int actualFormat, out nuint items,
        out nuint bytesAfter, out IntPtr data);
    [LibraryImport(X11)] private static partial int XFree(IntPtr data);
    [LibraryImport(X11)]
    private static unsafe partial int XSendEvent(IntPtr display, IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool propagate,
        nint mask, byte* xevent);
    [LibraryImport(X11)]
    private static partial int XConvertSelection(IntPtr display, IntPtr selection, IntPtr target, IntPtr property, IntPtr requestor,
        nuint time);
    [LibraryImport(X11)] private static partial int XFlush(IntPtr display);

    private static IntPtr _display;
    private static Func<IReadOnlyList<string>, string?, Task>? _dropped;
    private static readonly Dictionary<string, IntPtr> Atoms = [];

    // The drag in progress: who it's from, which window it's over, what we'll ask it for.
    private static IntPtr _source;
    private static IntPtr _over;
    private static IntPtr _type;
    private static DispatcherTimer? _waiting;

    /// <summary>Why drops can't be taken (shown once on the console); null while they can.</summary>
    public static string? Problem { get; private set; }

    /// <summary>What to do with a drop: the files (local paths), else a link or other text.</summary>
    public static void OnDrop(Func<IReadOnlyList<string>, string?, Task> dropped) => _dropped = dropped;

    /// <summary>Takes drops on this window. False if it can't (not X11, or Avalonia changed).</summary>
    public static bool Register(TopLevel topLevel)
    {
        if (!OperatingSystem.IsLinux() || Problem is not null
            || topLevel.TryGetPlatformHandle() is not { HandleDescriptor: "XID", Handle: var window and not 0 }) return false;
        try
        {
            if (_display == IntPtr.Zero) _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero) return Fail("no X display");
            if (!Hook(topLevel.PlatformImpl, window)) return false;
            var version = (IntPtr)XdndVersion;
            XChangeProperty(_display, window, Atom("XdndAware"), XaAtom, 32, PropModeReplace, ref version, 1);
            XFlush(_display);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return Fail(e.Message);
        }
    }

    private static bool Fail(string why)
    {
        if (Problem is null) Console.Error.WriteLine($"Paramaja: drag and drop is off ({why})");
        Problem = why;
        return false;
    }

    private static IntPtr Atom(string name)
    {
        if (!Atoms.TryGetValue(name, out var atom)) Atoms[name] = atom = XInternAtom(_display, name, false);
        return atom;
    }

    // ---- Avalonia's event dispatch ------------------------------------------------------------

    /// <summary>
    /// Where Avalonia 11.3 keeps each X11 window's event handler: its X11 platform's
    /// <c>Windows</c>, a dictionary of window → <c>X11EventDispatcher.EventHandler(ref XEvent)</c>,
    /// the platform being each X11Window's <c>_platform</c>. Null if that isn't so any more (an
    /// Avalonia update): a test checks it is.
    /// </summary>
    internal static (FieldInfo Platform, PropertyInfo Windows, Type Handler)? HookPoints()
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var platform = Type.GetType("Avalonia.X11.AvaloniaX11Platform, Avalonia.X11");
        var field = Type.GetType("Avalonia.X11.X11Window, Avalonia.X11")?.GetField("_platform", any);
        var windows = platform?.GetProperty("Windows", any);
        if (field is null || field.FieldType != platform || windows?.PropertyType is not { IsGenericType: true } dictionary
            || dictionary.GetGenericTypeDefinition() != typeof(Dictionary<,>)
            || dictionary.GetGenericArguments() is not [var key, var handler] || key != typeof(IntPtr)
            || !typeof(Delegate).IsAssignableFrom(handler)
            || handler.GetMethod("Invoke") is not { ReturnType: var returns } invoke || returns != typeof(void)
            || invoke.GetParameters() is not [{ ParameterType.IsByRef: true }])
            return null;
        return (field, windows, handler);
    }

    /// <summary>The window's handler, replaced by one that offers us each event first.</summary>
    private static bool Hook(object? impl, IntPtr window)
    {
        if (HookPoints() is not var (platformField, windowsProperty, handlerType)) return Fail("Avalonia's X11 internals have changed");
        if (impl is null || !platformField.DeclaringType!.IsInstanceOfType(impl)) return Fail("not Avalonia's X11 window");
        if (windowsProperty.GetValue(platformField.GetValue(impl)) is not IDictionary windows || windows[window] is not Delegate original)
            return Fail("the window isn't Avalonia's");
        windows[window] = Wrap(handlerType, original);
        return true;
    }

    /// <summary>Holds Avalonia's own handler; <see cref="Wrap"/>'s method is bound to it.</summary>
    public sealed class Chain(Delegate next)
    {
        public readonly Delegate Next = next;

        /// <summary>The event (an XEvent, on Avalonia's stack) is ours: Avalonia doesn't see it.</summary>
        public static bool Take(IntPtr xevent) => Handle(xevent);
    }

    /// <summary>
    /// A handler of Avalonia's delegate type that passes the event's address to <see cref="Chain.Take"/>
    /// and, unless it's taken, calls Avalonia's handler with it. Built in IL: the event's type is internal.
    /// </summary>
    private static Delegate Wrap(Type handlerType, Delegate original)
    {
        var invoke = handlerType.GetMethod("Invoke")!;
        var xevent = invoke.GetParameters()[0].ParameterType;
        var method = new DynamicMethod("ParamajaXdnd", typeof(void), [typeof(Chain), xevent], typeof(X11DragDrop).Module, skipVisibility: true);
        var il = method.GetILGenerator();
        var done = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Call, typeof(Chain).GetMethod(nameof(Chain.Take))!);
        il.Emit(OpCodes.Brtrue_S, done);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, typeof(Chain).GetField(nameof(Chain.Next))!);
        il.Emit(OpCodes.Castclass, handlerType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, invoke);
        il.MarkLabel(done);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate(handlerType, new Chain(original));
    }

    // ---- The protocol --------------------------------------------------------------------------

    /// <summary>On the UI thread (Avalonia's dispatch). True for the events that are XDND's.</summary>
    private static unsafe bool Handle(IntPtr xevent)
    {
        var type = *(int*)xevent;
        if (type == ClientMessage)
        {
            var m = *(XClientMessageEvent*)xevent;
            if (m.Format != 32) return false;
            if (m.MessageType == Atom("XdndEnter")) Enter(m);
            else if (m.MessageType == Atom("XdndPosition")) Position(m);
            else if (m.MessageType == Atom("XdndLeave")) Reset();
            else if (m.MessageType == Atom("XdndDrop")) Drop(m);
            else return false;
            return true;
        }
        if (type == SelectionNotify)
        {
            var s = *(XSelectionEvent*)xevent;
            if (s.Selection != Atom("XdndSelection") || s.Requestor != _over || _source == IntPtr.Zero) return false;
            Received(s);
            return true;
        }
        return false;
    }

    /// <summary>What we take, best first: files (and links) as URIs, else text.</summary>
    internal static readonly string[] Wanted = ["text/uri-list", "UTF8_STRING", "text/plain;charset=utf-8", "text/plain"];

    private static void Enter(XClientMessageEvent m)
    {
        _source = m.L0;
        _over = m.Window;
        // More than three types: they're in the source's XdndTypeList.
        var offered = (m.L1 & 1) != 0 ? TypeList(m.L0) : [m.L2, m.L3, m.L4];
        _type = Wanted.Select(Atom).FirstOrDefault(offered.Contains);
    }

    private static IntPtr[] TypeList(IntPtr source)
    {
        if (XGetWindowProperty(_display, source, Atom("XdndTypeList"), 0, 64, false, XaAtom, out _, out var format, out var count,
                out _, out var data) != 0 || data == IntPtr.Zero) return [];
        try
        {
            if (format != 32) return [];
            var atoms = new IntPtr[(int)count];
            Marshal.Copy(data, atoms, 0, atoms.Length);
            return atoms;
        }
        finally { XFree(data); }
    }

    private static void Position(XClientMessageEvent m)
    {
        if (m.L0 != _source) return;
        _over = m.Window;
        var accept = _type != IntPtr.Zero;
        // Accepted, with positions wanted everywhere (no rectangle), as a copy.
        Send(_source, Atom("XdndStatus"), m.Window, accept ? 3 : 0, 0, 0, accept ? Atom("XdndActionCopy") : 0);
    }

    private static void Drop(XClientMessageEvent m)
    {
        if (m.L0 != _source) return;
        _over = m.Window;
        if (_type == IntPtr.Zero)
        {
            Finish(accepted: false);
            return;
        }
        XConvertSelection(_display, Atom("XdndSelection"), _type, Atom("PARAMAJA_XDND"), _over, (nuint)m.L2);
        XFlush(_display);
        // A source that never answers mustn't hold the drag (and the next one) forever.
        _waiting?.Stop();
        _waiting = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (_, _) => Finish(accepted: false));
        _waiting.Start();
    }

    private static void Received(XSelectionEvent s)
    {
        string? text = null;
        if (s.Property != IntPtr.Zero
            && XGetWindowProperty(_display, _over, s.Property, 0, 1 << 20, true, AnyPropertyType, out _, out var format, out var count,
                out _, out var data) == 0 && data != IntPtr.Zero)
        {
            try { if (format == 8) text = Marshal.PtrToStringUTF8(data, (int)count); }
            finally { XFree(data); }
        }
        var uris = _type == Atom("text/uri-list");
        Finish(accepted: text is not null);
        if (text is null || _dropped is not { } dropped) return;
        var (paths, link) = uris ? ParseUriList(text) : ([], text.Trim());
        // After this event's dispatch: opening a video from inside it would nest a lot in it.
        Dispatcher.UIThread.Post(() => _ = dropped(paths, link));
    }

    private static void Finish(bool accepted)
    {
        _waiting?.Stop();
        _waiting = null;
        if (_source != IntPtr.Zero)
            Send(_source, Atom("XdndFinished"), _over, accepted ? 1 : 0, accepted ? Atom("XdndActionCopy") : 0, 0, 0);
        Reset();
    }

    private static void Reset()
    {
        _source = IntPtr.Zero;
        _type = IntPtr.Zero;
    }

    private static unsafe void Send(IntPtr to, IntPtr type, nint l0, nint l1, nint l2, nint l3, nint l4)
    {
        var buffer = stackalloc byte[192]; // sizeof(XEvent)
        new Span<byte>(buffer, 192).Clear();
        *(XClientMessageEvent*)buffer = new XClientMessageEvent
        {
            Type = ClientMessage, Display = _display, Window = to, MessageType = type, Format = 32,
            L0 = l0, L1 = l1, L2 = l2, L3 = l3, L4 = l4,
        };
        XSendEvent(_display, to, false, 0, buffer);
        XFlush(_display);
    }

    /// <summary>
    /// A text/uri-list: local files (file:// URIs) as paths, else the first other URI (a link from
    /// a browser) as text. Comments (#) and blank lines are skipped.
    /// </summary>
    internal static (List<string> Paths, string? Text) ParseUriList(string list)
    {
        var paths = new List<string>();
        string? link = null;
        foreach (var raw in list.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\0');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsFile) paths.Add(uri.LocalPath);
            else link ??= line;
        }
        return (paths, paths.Count > 0 ? null : link);
    }
}
