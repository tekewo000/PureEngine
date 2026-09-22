using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Direct3D11;

namespace PureEngine.Rendering;

/// <summary>Application-owned GPU context. Dispose all renderers before disposing this owner.</summary>
public sealed unsafe class VulkanDevice : IDisposable
{
    private readonly Vk _vk;
    private Instance _instance;
    private PhysicalDevice _physical;
    private Device _device;
    private Queue _queue;
    private uint _family;
    private byte[] _luid = [];
    private bool _disposed;
    internal Vk Api => _vk;
    internal Instance Instance => _instance;
    internal PhysicalDevice Physical => _physical;
    internal Device Device => _device;
    internal Queue Queue => _queue;
    internal uint Family => _family;
    internal D3D11 D3D { get; private set; } = null!;
    internal Silk.NET.DXGI.DXGI Dxgi { get; private set; } = null!;
    internal ComPtr<ID3D11Device> D3DDevice { get; private set; }
    public string DeviceName { get; private set; } = "";
    public bool IsDisposed => _disposed;

    public VulkanDevice(byte[]? uuid, byte[]? luid, bool validation = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Rendering requires Windows x64.");
        _vk = Vk.GetApi();
        try
        {
            Initialize(uuid, luid, validation);
            Dxgi = new(Silk.NET.DXGI.DXGI.CreateDefaultContext(["dxgi.dll"]));
            D3D = new(D3D11.CreateDefaultContext(["d3d11.dll"]));
            D3DDevice = SharedTexture.CreateDevice(Dxgi, D3D, _luid);
        }
        catch { Dispose(); throw; }
    }

    private void Initialize(byte[]? uuid, byte[]? luid, bool validation)
    {
        var app = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = new Version32(1, 1, 0) };
        var layer = SilkMarshal.StringToPtr("VK_LAYER_KHRONOS_validation");
        try
        {
            var layerPointer = (byte*)layer;
            var info = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app,
                EnabledLayerCount = validation ? 1u : 0u, PpEnabledLayerNames = validation ? &layerPointer : null };
            Check(_vk.CreateInstance(in info, null, out _instance));
        }
        finally { SilkMarshal.Free(layer); }
        foreach (var physical in _vk.GetPhysicalDevices(_instance))
        {
            var id = new PhysicalDeviceIDProperties { SType = StructureType.PhysicalDeviceIDProperties };
            var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &id };
            _vk.GetPhysicalDeviceProperties2(physical, &properties);
            if (uuid is { Length: 16 } && !new ReadOnlySpan<byte>(id.DeviceUuid, 16).SequenceEqual(uuid)) continue;
            if (uuid is null && luid is { Length: 8 } && (!id.DeviceLuidvalid || !new ReadOnlySpan<byte>(id.DeviceLuid, 8).SequenceEqual(luid))) continue;
            uint familyCount = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(physical, ref familyCount, null);
            var families = new QueueFamilyProperties[familyCount];
            fixed (QueueFamilyProperties* fp = families)
                _vk.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, fp);
            for (var i = 0; i < families.Length; i++)
            {
                if ((families[i].QueueFlags & QueueFlags.GraphicsBit) == 0) continue;
                _physical = physical; _family = (uint)i;
                if (!id.DeviceLuidvalid) throw new NotSupportedException("Vulkan adapter has no Windows LUID.");
                _luid = [.. new ReadOnlySpan<byte>(id.DeviceLuid, 8)];
                DeviceName = Marshal.PtrToStringUTF8((nint)properties.Properties.DeviceName)!;
                break;
            }
            if (_physical.Handle != 0) break;
        }
        if (_physical.Handle == 0) throw new NotSupportedException("No Vulkan graphics device matches the compositor adapter.");
        var extensions = SilkMarshal.StringArrayToPtr(["VK_KHR_external_memory_win32", "VK_KHR_win32_keyed_mutex"]);
        try
        {
            var priority = 1f;
            var queueInfo = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = _family, QueueCount = 1, PQueuePriorities = &priority };
            var info = new DeviceCreateInfo { SType = StructureType.DeviceCreateInfo, QueueCreateInfoCount = 1, PQueueCreateInfos = &queueInfo,
                EnabledExtensionCount = 2, PpEnabledExtensionNames = (byte**)extensions };
            Check(_vk.CreateDevice(_physical, in info, null, out _device));
        }
        finally { SilkMarshal.Free(extensions); }
        _vk.GetDeviceQueue(_device, _family, 0, out _queue);
    }

    private static void Check(Result result)
    {
        if (result != Result.Success) throw new InvalidOperationException($"Vulkan: {result}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device.Handle != 0)
        {
            _ = _vk.DeviceWaitIdle(_device);
            _vk.DestroyDevice(_device, null);
        }
        if (_instance.Handle != 0) _vk.DestroyInstance(_instance, null);
        D3DDevice.Dispose();
        D3D?.Dispose(); Dxgi?.Dispose();
        _vk.Dispose();
    }
}
