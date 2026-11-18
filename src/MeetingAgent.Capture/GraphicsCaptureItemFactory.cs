using System.Runtime.InteropServices;
using System.Reflection;
using Windows.Graphics.Capture;
using WinRT;

namespace MeetingAgent.Capture;

internal static class GraphicsCaptureItemFactory
{
    private static readonly Guid GraphicsCaptureItemGuid = typeof(GraphicsCaptureItem).GUID;

    public static GraphicsCaptureItem CreateForWindow(IntPtr hwnd)
    {
        try
        {
            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            interop.CreateForWindow(hwnd, GraphicsCaptureItemGuid, out var itemPointer);
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        catch (InvalidCastException)
        {
            if (TryCreateFromActivationFactory(hwnd, out var item))
            {
                return item;
            }

            throw;
        }
    }

    private static bool TryCreateFromActivationFactory(IntPtr hwnd, out GraphicsCaptureItem item)
    {
        item = null!;
        try
        {
            var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            var asType = factory.GetType()
                .GetMethod("AsType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (asType is null || !asType.IsGenericMethodDefinition)
            {
                return false;
            }

            var typedFactory = asType.MakeGenericMethod(typeof(IGraphicsCaptureItemInterop)).Invoke(factory, null);
            if (typedFactory is not IGraphicsCaptureItemInterop interop)
            {
                return false;
            }

            interop.CreateForWindow(hwnd, GraphicsCaptureItemGuid, out var itemPointer);
            item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(IntPtr window, in Guid iid, out IntPtr result);
        void CreateForMonitor(IntPtr monitor, in Guid iid, out IntPtr result);
    }
}
