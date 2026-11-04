using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ContextCompanion.Core.Capturing;
using ContextCompanion.Core.Privacy;
using ContextCompanion.Platform.Windows.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ContextCompanion.Platform.Windows.Capture;

/// <summary>
/// One frame per request through Windows.Graphics.Capture: a capture item for the window (<c>CreateForWindow</c>) or
/// the monitor (<c>CreateForMonitor</c>), a free-threaded frame pool, and a staging texture to read the BGRA pixels.
/// The session's <c>IsBorderRequired</c> is never changed: the system's capture border stays visible while it runs,
/// and borderless capture is never requested.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WgcCapture : IDisposable
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid CaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDirect3DDevice winrtDevice;

    /// <summary>How long to wait for the first frame before giving up (and falling back to GDI).</summary>
    public static TimeSpan FrameTimeout { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Whether this session can use Windows.Graphics.Capture at all.</summary>
    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidCastException) { return false; }
        }
    }

    public WgcCapture()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out ID3D11Device created, out ID3D11DeviceContext createdContext)
            .CheckError();
        device = created ?? throw new CaptureFailedException("No Direct3D 11 device is available.");
        context = createdContext ?? throw new CaptureFailedException("No Direct3D 11 device context is available.");
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable));
        try { winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }
    }

    public BgraImage CaptureWindow(IntPtr window, bool includeCursor) => Capture(CreateItem(window, forMonitor: false), includeCursor);

    public BgraImage CaptureMonitor(IntPtr monitor, bool includeCursor) => Capture(CreateItem(monitor, forMonitor: true), includeCursor);

    private static GraphicsCaptureItem CreateItem(IntPtr handle, bool forMonitor)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Guid interopIid = CaptureItemInteropIid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in interopIid, out IntPtr interopPtr));
        try
        {
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(interopPtr);
            Guid itemIid = GraphicsCaptureItemIid;
            int hr = forMonitor ? interop.CreateForMonitor(handle, ref itemIid, out IntPtr itemPtr) : interop.CreateForWindow(handle, ref itemIid, out itemPtr);
            if (hr < 0 || itemPtr == IntPtr.Zero)
                throw new CaptureFailedException($"Windows.Graphics.Capture cannot capture this {(forMonitor ? "monitor" : "window")} (0x{hr:X8}).");
            try { return GraphicsCaptureItem.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }
        }
        finally { Marshal.Release(interopPtr); }
    }

    private BgraImage Capture(GraphicsCaptureItem item, bool includeCursor)
    {
        var size = item.Size;
        if (size.Width < 1 || size.Height < 1) throw new CaptureFailedException("The capture item has no area.");
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = includeCursor;
        // IsBorderRequired is deliberately left alone: the capture border stays visible.
        using var arrived = new ManualResetEventSlim();
        pool.FrameArrived += (_, _) => arrived.Set();
        session.StartCapture();
        if (!arrived.Wait(FrameTimeout)) throw new CaptureFailedException("Windows.Graphics.Capture delivered no frame in time.");
        using var frame = pool.TryGetNextFrame() ?? throw new CaptureFailedException("Windows.Graphics.Capture delivered no frame.");
        return Read(frame.Surface, frame.ContentSize.Width, frame.ContentSize.Height);
    }

    /// <summary>Copies the frame into a staging texture and reads its rows, converting premultiplied to straight alpha.</summary>
    private BgraImage Read(IDirect3DSurface surface, int contentWidth, int contentHeight)
    {
        using var texture = new ID3D11Texture2D(TextureOf(surface));
        var description = texture.Description;
        int width = Math.Min(contentWidth, (int)description.Width), height = Math.Min(contentHeight, (int)description.Height);
        if (width < 1 || height < 1 || width > BgraImage.MaxDimension || height > BgraImage.MaxDimension)
            throw new CaptureFailedException($"The captured frame must be 1 to {BgraImage.MaxDimension} pixels on each side.");

        var stagingDescription = description with
        {
            MipLevels = 1,
            ArraySize = 1,
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        using var staging = device.CreateTexture2D(stagingDescription);
        context.CopyResource(staging, texture);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var pixels = new byte[width * 4 * height];
        try
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(mapped.DataPointer + y * (nint)mapped.RowPitch, pixels, y * width * 4, width * 4);
        }
        finally { context.Unmap(staging, 0); }
        Unpremultiply(pixels);
        return new BgraImage(width, height, width * 4, pixels);
    }

    private static IntPtr TextureOf(IDirect3DSurface surface)
    {
        IntPtr surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Guid accessIid = DxgiInterfaceAccessIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, in accessIid, out IntPtr accessPtr));
            try
            {
                var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
                Guid textureIid = Texture2DIid;
                Marshal.ThrowExceptionForHR(access.GetInterface(ref textureIid, out IntPtr texture));
                return texture;
            }
            finally { Marshal.Release(accessPtr); }
        }
        finally { Marshal.Release(surfacePtr); }
    }

    /// <summary>Windows.Graphics.Capture delivers premultiplied alpha; packets and <see cref="BgraImage"/> use straight alpha.</summary>
    internal static void Unpremultiply(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a is 0 or 255) continue;
            for (int c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Min(255, (pixels[i + c] * 255 + a / 2) / a);
        }
    }

    public void Dispose()
    {
        (winrtDevice as IDisposable)?.Dispose();
        context.Dispose();
        device.Dispose();
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig] int GetInterface(ref Guid iid, out IntPtr result);
    }
}
