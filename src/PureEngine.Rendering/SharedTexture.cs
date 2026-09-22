using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace PureEngine.Rendering;

/// <summary>Only the shared allocation uses D3D11. Every draw is submitted through Vulkan.</summary>
internal sealed unsafe class SharedTexture : IDisposable
{
    private readonly ComPtr<ID3D11Device> _device;
    private ComPtr<ID3D11Texture2D> _texture;
    private bool _disposed;
    public SharedTexture(ComPtr<ID3D11Device> device, int width, int height)
    {
        _device = device;
        try
        {
            var texture = new Texture2DDesc { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = Format.FormatR8G8B8A8Unorm, SampleDesc = new(1, 0), Usage = Usage.Default,
                BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource),
                MiscFlags = (uint)(ResourceMiscFlag.SharedKeyedmutex | ResourceMiscFlag.SharedNthandle) };
            ComPtr<ID3D11Texture2D> created = default;
            SilkMarshal.ThrowHResult(device.CreateTexture2D(&texture, (SubresourceData*)null, created.GetAddressOf()));
            _texture = created;
        }
        catch { Dispose(); throw; }
    }

    internal static ComPtr<ID3D11Device> CreateDevice(DXGI dxgi, D3D11 d3d, byte[] luidBytes)
    {
        using var factory = dxgi.CreateDXGIFactory1<IDXGIFactory1>();
        var luid = MemoryMarshal.Read<Luid>(luidBytes);
        for (var i = 0u; ; i++)
        {
            ComPtr<IDXGIAdapter> adapter = default;
            var result = factory.EnumAdapters(i, adapter.GetAddressOf());
            if (result == unchecked((int)0x887A0002)) throw new NotSupportedException("No D3D11 adapter matches Vulkan.");
            SilkMarshal.ThrowHResult(result);
            using (adapter)
            {
                AdapterDesc desc;
                SilkMarshal.ThrowHResult(adapter.GetDesc(&desc));
                if (desc.AdapterLuid.Low != luid.Low || desc.AdapterLuid.High != luid.High) continue;
                var level = D3DFeatureLevel.Level110;
                ComPtr<ID3D11Device> device = default;
                SilkMarshal.ThrowHResult(d3d.CreateDevice(adapter, D3DDriverType.Unknown, 0, 0, &level, 1, D3D11.SdkVersion,
                    device.GetAddressOf(), null, (ID3D11DeviceContext**)null));
                return device;
            }
        }
    }

    public SafeFileHandle Export()
    {
        using var resource = _texture.QueryInterface<IDXGIResource1>();
        void* handle;
        SilkMarshal.ThrowHResult(resource.CreateSharedHandle((SecurityAttributes*)null, DXGI.SharedResourceRead | DXGI.SharedResourceWrite, (char*)null, &handle));
        return new SafeFileHandle((nint)handle, true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _texture.Dispose(); _texture = default;
        // D3D11 defers object destruction until Flush, even for a texture never drawn by D3D11.
        ComPtr<ID3D11DeviceContext> context = default;
        _device.GetImmediateContext(context.GetAddressOf());
        using (context) context.Flush();
    }
}

