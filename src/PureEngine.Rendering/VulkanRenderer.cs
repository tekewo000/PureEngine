using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Buffer = Silk.NET.Vulkan.Buffer;


namespace PureEngine.Rendering;

/// <summary>Windows Vulkan 1.1, one ordered atlas batch and one in-flight frame. Not thread safe.</summary>
public sealed unsafe class VulkanRenderer : IDisposable
{
    private readonly Vk _vk;
    private readonly VulkanDevice _owner;
    private Instance _instance;
    private PhysicalDevice _physical;
    private Device _device;
    private Queue _queue;
    private uint _family;
    private CommandPool _pool;
    private CommandBuffer _command;
    private Fence _fence;
    private RenderPass _pass;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _descriptors;
    private DescriptorSet _set;
    private Sampler _sampler;
    private Buffer _vertices, _staging;
    private DeviceMemory _vertexMemory, _stagingMemory;
    private Image _atlas;
    private DeviceMemory _atlasMemory;
    private ImageView _atlasView;
    private Image _image;
    private ImageView _view;
    private DeviceMemory _memory;
    private Framebuffer _framebuffer;
    private SharedTexture? _shared;
    private int _atlasRevision;
    private DrawList? _atlasOwner;
    private bool _presented;
    private bool _disposed;
    private bool _faulted;
    private readonly bool _counted;
    private static int _activeInstances;
    public static int ActiveInstances => Volatile.Read(ref _activeInstances);
    public string DeviceName => _owner.DeviceName;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ulong MemorySize { get; private set; }
    public const uint SharedLayout = (uint)ImageLayout.General;
    private const ulong Timeout = 5_000_000_000;
    private const ImageUsageFlags TargetUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit;

    public VulkanRenderer(VulkanDevice owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ObjectDisposedException.ThrowIf(owner.IsDisposed, owner);
        _owner = owner; _vk = owner.Api;
        try { Initialize(); Interlocked.Increment(ref _activeInstances); _counted = true; }
        catch { Dispose(); throw; }
    }

    private void Initialize()
    {
        _instance = _owner.Instance; _physical = _owner.Physical; _device = _owner.Device;
        _queue = _owner.Queue; _family = _owner.Family;
        var pool = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _family, Flags = CommandPoolCreateFlags.ResetCommandBufferBit };
        Check(_vk.CreateCommandPool(_device, in pool, null, out _pool));
        var command = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _pool, CommandBufferCount = 1, Level = CommandBufferLevel.Primary };
        Check(_vk.AllocateCommandBuffers(_device, in command, out _command));
        var fence = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        Check(_vk.CreateFence(_device, in fence, null, out _fence));
        CreateBuffer((ulong)(DrawList.MaxVertices * sizeof(DrawVertex)), BufferUsageFlags.VertexBufferBit, out _vertices, out _vertexMemory);
        CreateBuffer(DrawList.AtlasSize * DrawList.AtlasSize * 4, BufferUsageFlags.TransferSrcBit, out _staging, out _stagingMemory);
        CreateImage(DrawList.AtlasSize, DrawList.AtlasSize, false, out _atlas, out _atlasMemory, out _atlasView, out _);
        CreatePipeline();
    }

    private uint MemoryType(uint bits, MemoryPropertyFlags flags)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physical, out var memory);
        for (var i = 0u; i < memory.MemoryTypeCount; i++)
            if ((bits & (1u << (int)i)) != 0 && (memory.MemoryTypes[(int)i].PropertyFlags & flags) == flags) return i;
        throw new NotSupportedException($"No Vulkan memory type for {flags}.");
    }

    private void CreateBuffer(ulong size, BufferUsageFlags usage, out Buffer buffer, out DeviceMemory memory)
    {
        memory = default;
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        Check(_vk.CreateBuffer(_device, in info, null, out buffer));
        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var allocate = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size,
            MemoryTypeIndex = MemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit) };
        Check(_vk.AllocateMemory(_device, in allocate, null, out memory));
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0));
    }

    private void CreateImage(int width, int height, bool shared, out Image image, out DeviceMemory memory, out ImageView view, out ulong memorySize)
    {
        memory = default; view = default;
        var external = new ExternalMemoryImageCreateInfo { SType = StructureType.ExternalMemoryImageCreateInfo, HandleTypes = ExternalMemoryHandleTypeFlags.D3D11TextureBit };
        var info = new ImageCreateInfo { SType = StructureType.ImageCreateInfo, PNext = shared ? &external : null,
            ImageType = ImageType.Type2D, Format = Format.R8G8B8A8Unorm, Extent = new((uint)width, (uint)height, 1),
            MipLevels = 1, ArrayLayers = 1, Samples = SampleCountFlags.Count1Bit, Tiling = ImageTiling.Optimal,
            Usage = TargetUsage, SharingMode = SharingMode.Exclusive, Flags = shared ? ImageCreateFlags.CreateMutableFormatBit : 0 };
        Check(_vk.CreateImage(_device, in info, null, out image));
        _vk.GetImageMemoryRequirements(_device, image, out var requirements);
        memorySize = requirements.Size;
        var dedicated = new MemoryDedicatedAllocateInfo { SType = StructureType.MemoryDedicatedAllocateInfo, Image = image };
        using var handle = shared ? _shared!.Export() : null;
        var import = new ImportMemoryWin32HandleInfoKHR { SType = StructureType.ImportMemoryWin32HandleInfoKhr,
            HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureBit, Handle = handle?.DangerousGetHandle() ?? 0, PNext = &dedicated };
        var memoryBits = requirements.MemoryTypeBits;
        if (shared)
        {
            if (!_vk.TryGetDeviceExtension<KhrExternalMemoryWin32>(_instance, _device, out var ext)) throw new NotSupportedException("Win32 external memory unavailable.");
            using (ext)
            {
                var properties = new MemoryWin32HandlePropertiesKHR { SType = StructureType.MemoryWin32HandlePropertiesKhr };
                Check(ext.GetMemoryWin32HandleProperties(_device, ExternalMemoryHandleTypeFlags.D3D11TextureBit, import.Handle, &properties));
                memoryBits &= properties.MemoryTypeBits;
            }
        }
        var allocate = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, PNext = shared ? &import : null,
            AllocationSize = requirements.Size, MemoryTypeIndex = MemoryType(memoryBits, MemoryPropertyFlags.DeviceLocalBit) };
        Check(_vk.AllocateMemory(_device, in allocate, null, out memory));
        Check(_vk.BindImageMemory(_device, image, memory, 0));
        var viewInfo = new ImageViewCreateInfo { SType = StructureType.ImageViewCreateInfo, Image = image, ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm, SubresourceRange = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1) };
        Check(_vk.CreateImageView(_device, in viewInfo, null, out view));
    }

    // Caller must dispose imported objects and await their completion before changing the target.
    public void Resize(int width, int height)
    {
        EnsureUsable();
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 8192);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 8192);
        if (Width == width && Height == height) return;
        try
        {
            Wait();
            DestroyTarget();
            _shared = new SharedTexture(_owner.D3DDevice, width, height);
            CreateImage(width, height, true, out _image, out _memory, out _view, out var bytes);
            MemorySize = bytes;
            var view = _view;
            var info = new FramebufferCreateInfo { SType = StructureType.FramebufferCreateInfo, RenderPass = _pass,
                AttachmentCount = 1, PAttachments = &view, Width = (uint)width, Height = (uint)height, Layers = 1 };
            Check(_vk.CreateFramebuffer(_device, in info, null, out _framebuffer));
            Width = width; Height = height;
        }
        catch { _faulted = true; throw; }
    }

    public SafeFileHandle ExportImage()
    {
        EnsureUsable();
        return _shared!.Export();
    }

    public void Render(DrawList list, Vector2 logicalSize)
    {
        EnsureUsable();
        if (Width == 0 || !float.IsFinite(logicalSize.X + logicalSize.Y) || logicalSize.X <= 0 || logicalSize.Y <= 0) throw new ArgumentException("Invalid target dimensions.", nameof(logicalSize));
        try
        {
            Wait();
            Upload(_vertexMemory, MemoryMarshal.AsBytes(list.Vertices));
            Begin();
            if (!ReferenceEquals(_atlasOwner, list) || _atlasRevision != list.Revision)
            {
                // ponytail: upload the 16 MiB atlas on changes; use dirty rectangles if changing text is frequent.
                Upload(_stagingMemory, new ReadOnlySpan<byte>((void*)list.Pixels, DrawList.AtlasSize * DrawList.AtlasSize * 4));
                Barrier(_atlas, _atlasRevision == 0 ? ImageLayout.Undefined : ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal,
                    _atlasRevision == 0 ? 0 : AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
                var copy = new BufferImageCopy { ImageSubresource = new(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new(DrawList.AtlasSize, DrawList.AtlasSize, 1) };
                _vk.CmdCopyBufferToImage(_command, _staging, _atlas, ImageLayout.TransferDstOptimal, 1, in copy);
                Barrier(_atlas, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
                _atlasRevision = list.Revision;
                _atlasOwner = list;
            }
            Barrier(_image, _presented ? ImageLayout.General : ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                0, AccessFlags.ColorAttachmentWriteBit, Vk.QueueFamilyExternal, _family);
            var clear = new ClearValue { Color = new ClearColorValue(.035f, .045f, .065f, 1) };
            var begin = new RenderPassBeginInfo { SType = StructureType.RenderPassBeginInfo, RenderPass = _pass, Framebuffer = _framebuffer,
                RenderArea = new(new(0, 0), new((uint)Width, (uint)Height)), ClearValueCount = 1, PClearValues = &clear };
            _vk.CmdBeginRenderPass(_command, in begin, SubpassContents.Inline);
            _vk.CmdBindPipeline(_command, PipelineBindPoint.Graphics, _pipeline);
            var viewport = new Viewport(0, 0, Width, Height, 0, 1);
            var scissor = new Rect2D(new(0, 0), new((uint)Width, (uint)Height));
            _vk.CmdSetViewport(_command, 0, 1, in viewport);
            _vk.CmdSetScissor(_command, 0, 1, in scissor);
            var vertices = _vertices;
            ulong offset = 0;
            _vk.CmdBindVertexBuffers(_command, 0, 1, &vertices, &offset);
            var set = _set;
            _vk.CmdBindDescriptorSets(_command, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
            _vk.CmdPushConstants(_command, _layout, ShaderStageFlags.VertexBit, 0, 8, &logicalSize);
            _vk.CmdDraw(_command, (uint)list.Vertices.Length, 1, 0, 0);
            _vk.CmdEndRenderPass(_command);
            Barrier(_image, ImageLayout.ColorAttachmentOptimal, ImageLayout.General, AccessFlags.ColorAttachmentWriteBit, 0, _family, Vk.QueueFamilyExternal);
            Check(_vk.EndCommandBuffer(_command));
            var cmd = _command;
            var memory = _memory;
            ulong acquireKey = 0, releaseKey = 1;
            uint timeout = 5000;
            var mutex = new Win32KeyedMutexAcquireReleaseInfoKHR { SType = StructureType.Win32KeyedMutexAcquireReleaseInfoKhr,
                AcquireCount = 1, PAcquireSyncs = &memory, PAcquireKeys = &acquireKey, PAcquireTimeouts = &timeout,
                ReleaseCount = 1, PReleaseSyncs = &memory, PReleaseKeys = &releaseKey };
            var submit = new SubmitInfo { SType = StructureType.SubmitInfo, PNext = &mutex, CommandBufferCount = 1, PCommandBuffers = &cmd };
            Check(_vk.ResetFences(_device, 1, in _fence));
            Check(_vk.QueueSubmit(_queue, 1, in submit, _fence));
            Wait();
            _presented = true;
        }
        catch { _faulted = true; throw; }
    }

    private void Upload(DeviceMemory memory, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        void* destination;
        Check(_vk.MapMemory(_device, memory, 0, (ulong)bytes.Length, 0, &destination));
        try { bytes.CopyTo(new Span<byte>(destination, bytes.Length)); }
        finally { _vk.UnmapMemory(_device, memory); }
    }

    private void Begin()
    {
        Check(_vk.ResetCommandBuffer(_command, 0));
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(_vk.BeginCommandBuffer(_command, in begin));
    }

    private void Barrier(Image image, ImageLayout from, ImageLayout to, AccessFlags source, AccessFlags destination, uint sourceFamily = Vk.QueueFamilyIgnored, uint destinationFamily = Vk.QueueFamilyIgnored)
    {
        var barrier = new ImageMemoryBarrier { SType = StructureType.ImageMemoryBarrier, Image = image, OldLayout = from, NewLayout = to,
            SrcAccessMask = source, DstAccessMask = destination, SrcQueueFamilyIndex = sourceFamily, DstQueueFamilyIndex = destinationFamily,
            SubresourceRange = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1) };
        _vk.CmdPipelineBarrier(_command, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, in barrier);
    }

    private void Wait() => Check(_vk.WaitForFences(_device, 1, in _fence, true, Timeout));
    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);
        if (_faulted) throw new InvalidOperationException("Rendering stopped after a Vulkan failure. Automatic device recreation is disabled.");
    }
    private static void Check(Result result)
    {
        if (result != Result.Success) throw new InvalidOperationException($"Vulkan: {result}");
    }

    private void CreatePipeline()
    {
        var attachment = new AttachmentDescription { Format = Format.R8G8B8A8Unorm, Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store, StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare, InitialLayout = ImageLayout.ColorAttachmentOptimal, FinalLayout = ImageLayout.ColorAttachmentOptimal };
        var reference = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &reference };
        var pass = new RenderPassCreateInfo { SType = StructureType.RenderPassCreateInfo, AttachmentCount = 1, PAttachments = &attachment, SubpassCount = 1, PSubpasses = &subpass };
        Check(_vk.CreateRenderPass(_device, in pass, null, out _pass));
        var binding = new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit };
        var setLayout = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &binding };
        Check(_vk.CreateDescriptorSetLayout(_device, in setLayout, null, out _setLayout));
        var range = new PushConstantRange(ShaderStageFlags.VertexBit, 0, 8);
        var sl = _setLayout;
        var layout = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &sl, PushConstantRangeCount = 1, PPushConstantRanges = &range };
        Check(_vk.CreatePipelineLayout(_device, in layout, null, out _layout));
        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 1);
        var pool = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &poolSize };
        Check(_vk.CreateDescriptorPool(_device, in pool, null, out _descriptors));
        var allocate = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descriptors, DescriptorSetCount = 1, PSetLayouts = &sl };
        Check(_vk.AllocateDescriptorSets(_device, in allocate, out _set));
        var sampler = new SamplerCreateInfo { SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Linear, MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge, AddressModeW = SamplerAddressMode.ClampToEdge };
        Check(_vk.CreateSampler(_device, in sampler, null, out _sampler));
        var image = new DescriptorImageInfo(_sampler, _atlasView, ImageLayout.ShaderReadOnlyOptimal);
        var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = _set, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &image };
        _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
        ShaderModule vert = default, frag = default;
        var entry = SilkMarshal.StringToPtr("main");
        try
        {
            vert = Shader("vert"); frag = Shader("frag");
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vert, PName = (byte*)entry };
            stages[1] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = frag, PName = (byte*)entry };
            var vertexBinding = new VertexInputBindingDescription(0, (uint)sizeof(DrawVertex), VertexInputRate.Vertex);
            var attrs = stackalloc VertexInputAttributeDescription[4];
            attrs[0] = new(0, 0, Format.R32G32Sfloat, 0);
            attrs[1] = new(1, 0, Format.R32G32Sfloat, 8);
            attrs[2] = new(2, 0, Format.R32G32B32A32Sfloat, 16);
            attrs[3] = new(3, 0, Format.R32G32B32A32Sfloat, 32);
            var input = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo, VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &vertexBinding, VertexAttributeDescriptionCount = 4, PVertexAttributeDescriptions = attrs };
            var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var viewport = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var raster = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, LineWidth = 1 };
            var samples = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            var blend = new PipelineColorBlendAttachmentState { BlendEnable = true, SrcColorBlendFactor = BlendFactor.One, DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add, SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha, AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
            var blending = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blend };
            var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
            var pipeline = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages,
                PVertexInputState = &input, PInputAssemblyState = &assembly, PViewportState = &viewport, PRasterizationState = &raster,
                PMultisampleState = &samples, PColorBlendState = &blending, PDynamicState = &dynamic, Layout = _layout, RenderPass = _pass };
            Check(_vk.CreateGraphicsPipelines(_device, default, 1, in pipeline, null, out _pipeline));
        }
        finally
        {
            if (vert.Handle != 0) _vk.DestroyShaderModule(_device, vert, null);
            if (frag.Handle != 0) _vk.DestroyShaderModule(_device, frag, null);
            SilkMarshal.Free(entry);
        }
    }

    private ShaderModule Shader(string stage)
    {
        using var stream = typeof(VulkanRenderer).Assembly.GetManifestResourceStream($"PureEngine.Rendering.Shaders.quad.{stage}.spv")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var data = bytes.ToArray();
        fixed (byte* p = data)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)data.Length, PCode = (uint*)p };
            Check(_vk.CreateShaderModule(_device, in info, null, out var module));
            return module;
        }
    }

    private void DestroyTarget()
    {
        if (_framebuffer.Handle != 0) _vk.DestroyFramebuffer(_device, _framebuffer, null);
        if (_view.Handle != 0) _vk.DestroyImageView(_device, _view, null);
        if (_image.Handle != 0) _vk.DestroyImage(_device, _image, null);
        if (_memory.Handle != 0) _vk.FreeMemory(_device, _memory, null);
        _shared?.Dispose(); _shared = null;
        _framebuffer = default; _view = default; _image = default; _memory = default;
        _presented = false; Width = 0; Height = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device.Handle != 0)
        {
            // All imports must already have been retired by the host. No GPU resources are freed in flight.
            _ = _vk.DeviceWaitIdle(_device);
            DestroyTarget();
            if (_pipeline.Handle != 0) _vk.DestroyPipeline(_device, _pipeline, null);
            if (_layout.Handle != 0) _vk.DestroyPipelineLayout(_device, _layout, null);
            if (_descriptors.Handle != 0) _vk.DestroyDescriptorPool(_device, _descriptors, null);
            if (_setLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
            if (_sampler.Handle != 0) _vk.DestroySampler(_device, _sampler, null);
            if (_pass.Handle != 0) _vk.DestroyRenderPass(_device, _pass, null);
            if (_atlasView.Handle != 0) _vk.DestroyImageView(_device, _atlasView, null);
            if (_atlas.Handle != 0) _vk.DestroyImage(_device, _atlas, null);
            if (_atlasMemory.Handle != 0) _vk.FreeMemory(_device, _atlasMemory, null);
            if (_vertices.Handle != 0) _vk.DestroyBuffer(_device, _vertices, null);
            if (_vertexMemory.Handle != 0) _vk.FreeMemory(_device, _vertexMemory, null);
            if (_staging.Handle != 0) _vk.DestroyBuffer(_device, _staging, null);
            if (_stagingMemory.Handle != 0) _vk.FreeMemory(_device, _stagingMemory, null);
            if (_fence.Handle != 0) _vk.DestroyFence(_device, _fence, null);
            if (_pool.Handle != 0) _vk.DestroyCommandPool(_device, _pool, null);
        }
        _atlasOwner = null;
        if (_counted) Interlocked.Decrement(ref _activeInstances);
    }
}


