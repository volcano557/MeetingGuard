using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace MeetingAgent.Capture;

internal static class Direct3D11Interop
{
    private static readonly Guid D3D11DeviceGuid = typeof(ID3D11Device).GUID;
    private static readonly Guid D3D11Texture2DGuid = typeof(ID3D11Texture2D).GUID;

    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var direct3DDevice);
        return MarshalInterface<IDirect3DDevice>.FromAbi(direct3DDevice);
    }

    public static ID3D11Texture2D CreateSharpDXTexture2D(IDirect3DSurface surface)
    {
        var access = GetDxgiInterfaceAccess(surface);
        access.GetInterface(D3D11Texture2DGuid, out var texturePointer);
        return new ID3D11Texture2D(texturePointer);
    }

    private static IDirect3DDxgiInterfaceAccess GetDxgiInterfaceAccess(IDirect3DSurface surface)
    {
        try
        {
            return surface.As<IDirect3DDxgiInterfaceAccess>();
        }
        catch (InvalidCastException)
        {
            var unknownPtr = Marshal.GetIUnknownForObject(surface);
            try
            {
                var iid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
                Marshal.QueryInterface(unknownPtr, ref iid, out var accessPtr);
                if (accessPtr == IntPtr.Zero)
                {
                    throw;
                }

                try
                {
                    return (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
                }
                finally
                {
                    Marshal.Release(accessPtr);
                }
            }
            finally
            {
                Marshal.Release(unknownPtr);
            }
        }
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        void GetInterface(in Guid iid, out IntPtr p);
    }

    [DllImport("d3d11.dll")]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);
}
