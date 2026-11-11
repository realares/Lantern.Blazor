using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.JSInterop;

namespace Photino.Blazor;

partial class PhotinoWebViewManager
{
    // Blazor components conventionally catch JSDisconnectedException around JS interop that may run
    // after the page is gone (for example in DisposeAsync), so a closed WebView reports it the same way.
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowWebViewMessagePumpStopped()
    {
        throw new JSDisconnectedException("The Photino WebView has been closed; JavaScript interop calls are no longer possible.");
    }
}
