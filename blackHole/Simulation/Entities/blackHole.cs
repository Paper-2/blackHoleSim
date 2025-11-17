using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using blackHole.Renderer.Vulkan;
using blackHole.Tools;
using static blackHole.Core.Math.MathConstants;

namespace blackHole.Simulation.Entities;

public class BlackHole
{
    internal readonly float radius;

    public double Spin = 0; // RPM. Non rotating, it would make the computation of the schwarzschild radius more complicated (I think)
    public int Sols = 4; // Solar mass of the black hole
    public Vector3D<float> Position = Vector3D<float>.Zero; //  0 0 0 will be our center.

    public Sphere SphereModel = new Sphere(2, 10.0f);

    public double SchwarzschildRadius;
    public double PhotonSphereRadius;

    public BlackHole()
    {
        // Compute the 2 radius.

        SchwarzschildRadius = SchwarzschildConstant * Sols * SolarMass;
        PhotonSphereRadius = 1.5 * SchwarzschildRadius;
    }
}

/// <summary>
/// Rendering pipeline for black hole sphere
/// </summary>
public unsafe class BlackHolePipeline : PipelineBuilder
{
    private const int MAX_FRAMES_IN_FLIGHT = 2;
    private Sphere? sphere;
    
    protected override CullModeFlags CullMode => CullModeFlags.None;
    private Silk.NET.Vulkan.Buffer vertexBuffer;
    private DeviceMemory vertexBufferMemory;
    private Silk.NET.Vulkan.Buffer indexBuffer;
    private DeviceMemory indexBufferMemory;
    private Silk.NET.Vulkan.Buffer[] uniformBuffers;
    private DeviceMemory[] uniformBuffersMemory;
    private DescriptorPool descriptorPool;
    private DescriptorSet[] descriptorSets;
    private DescriptorSetLayout descriptorSetLayout;

    public BlackHolePipeline(Vulkan vulkan) : base(vulkan)
    {
        uniformBuffers = new Silk.NET.Vulkan.Buffer[MAX_FRAMES_IN_FLIGHT];
        uniformBuffersMemory = new DeviceMemory[MAX_FRAMES_IN_FLIGHT];
        descriptorSets = new DescriptorSet[MAX_FRAMES_IN_FLIGHT];
    }

    public void SetSphere(Sphere sphere)
    {
        this.sphere = sphere;
    }

    protected override void CreateVertexBuffer()
    {
        if (sphere == null)
            throw new Exception("Sphere not set for BlackHolePipeline.");

        Vertex[] vertices = new Vertex[sphere.Vertices.Count];
        for (int i = 0; i < sphere.Vertices.Count; i++)
        {
            Vector3D<float> pos = sphere.Vertices[i];
            Vector3D<float> normal = Vector3D.Normalize(pos);
            Vector2D<float> uv = sphere.UVs[i];
            vertices[i] = new Vertex
            {
                Position = new System.Numerics.Vector3(pos.X, pos.Y, pos.Z),
                Normal = new System.Numerics.Vector3(normal.X, normal.Y, normal.Z),
                Color = new System.Numerics.Vector3(0.0f, 0.0f, 0.0f),
                TexCoord = new System.Numerics.Vector2(uv.X, uv.Y)
            };
        }

        ulong bufferSize = (ulong)(vertices.Length * Marshal.SizeOf<Vertex>());

        Silk.NET.Vulkan.Buffer stagingBuffer = default;
        DeviceMemory stagingBufferMemory = default;

        _vulkan.CreateBuffer(
            bufferSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            ref stagingBuffer,
            ref stagingBufferMemory
        );

        void* data;
        _vk.MapMemory(_device, stagingBufferMemory, 0, bufferSize, 0, &data);
        fixed (Vertex* arrayPtr = vertices)
        {
            System.Buffer.MemoryCopy(arrayPtr, data, (long)bufferSize, (long)bufferSize);
        }
        _vk.UnmapMemory(_device, stagingBufferMemory);

        _vulkan.CreateBuffer(
            bufferSize,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit,
            ref vertexBuffer,
            ref vertexBufferMemory
        );

        _vulkan.CopyBuffer(stagingBuffer, vertexBuffer, bufferSize);

        _vk.DestroyBuffer(_device, stagingBuffer, null);
        _vk.FreeMemory(_device, stagingBufferMemory, null);
    }

    protected override void CreateIndexBuffer()
    {
        if (sphere == null)
            throw new Exception("Sphere not set for BlackHolePipeline.");

        ulong bufferSize = (ulong)(sphere.Indices.Count * sizeof(uint));

        Silk.NET.Vulkan.Buffer stagingBuffer = default;
        DeviceMemory stagingBufferMemory = default;

        _vulkan.CreateBuffer(
            bufferSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            ref stagingBuffer,
            ref stagingBufferMemory
        );

        void* data;
        _vk.MapMemory(_device, stagingBufferMemory, 0, bufferSize, 0, &data);
        fixed (uint* indicesPtr = sphere.Indices.ToArray())
        {
            System.Buffer.MemoryCopy(indicesPtr, data, (long)bufferSize, (long)bufferSize);
        }
        _vk.UnmapMemory(_device, stagingBufferMemory);

        _vulkan.CreateBuffer(
            bufferSize,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit,
            ref indexBuffer,
            ref indexBufferMemory
        );

        _vulkan.CopyBuffer(stagingBuffer, indexBuffer, bufferSize);

        _vk.DestroyBuffer(_device, stagingBuffer, null);
        _vk.FreeMemory(_device, stagingBufferMemory, null);
    }

    protected override void CreateUniformBuffers()
    {
        ulong bufferSize = (ulong)sizeof(UniformBufferObject);

        for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            _vulkan.CreateBuffer(
                bufferSize,
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                ref uniformBuffers[i],
                ref uniformBuffersMemory[i]
            );
        }
    }

    protected override void CreateTextureImage()
    {
        // Create a simple 1x1 black texture for black hole
        uint width = 1;
        uint height = 1;
        byte[] pixels = { 0, 0, 0, 255 };

        Silk.NET.Vulkan.Buffer stagingBuffer = default;
        DeviceMemory stagingBufferMemory = default;

        _vulkan.CreateBuffer(
            (ulong)(width * height * 4),
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            ref stagingBuffer,
            ref stagingBufferMemory
        );

        void* data;
        _vk.MapMemory(_device, stagingBufferMemory, 0, (ulong)(width * height * 4), 0, &data);
        pixels.CopyTo(new Span<byte>(data, pixels.Length));
        _vk.UnmapMemory(_device, stagingBufferMemory);

        CreateImage(
            width,
            height,
            Format.R8G8B8A8Srgb,
            ImageTiling.Optimal,
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            MemoryPropertyFlags.DeviceLocalBit,
            ref textureImage,
            ref textureMemory
        );

        TransitionImageLayout(textureImage, Format.R8G8B8A8Srgb, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
        CopyBufferToImage(stagingBuffer, textureImage, width, height);
        TransitionImageLayout(textureImage, Format.R8G8B8A8Srgb, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);

        _vk.DestroyBuffer(_device, stagingBuffer, null);
        _vk.FreeMemory(_device, stagingBufferMemory, null);
    }

    protected override void* GetDescriptorSetLayoutPointer()
    {
        fixed (DescriptorSetLayout* ptr = &descriptorSetLayout)
        {
            return ptr;
        }
    }

    protected override void CreateDescriptorSetLayout()
    {
        DescriptorSetLayoutBinding[] bindings =
        {
            new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.VertexBit,
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            }
        };

        fixed (DescriptorSetLayoutBinding* bindingsPtr = bindings)
        {
            DescriptorSetLayoutCreateInfo layoutInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = bindingsPtr,
            };

            if (_vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out descriptorSetLayout) != Result.Success)
            {
                throw new Exception("Failed to create descriptor set layout!");
            }
        }
    }

    protected override void CreateDescriptorPool()
    {
        DescriptorPoolSize[] poolSizes =
        {
            new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = (uint)MAX_FRAMES_IN_FLIGHT },
            new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = (uint)MAX_FRAMES_IN_FLIGHT }
        };

        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = (uint)poolSizes.Length,
            MaxSets = (uint)MAX_FRAMES_IN_FLIGHT,
        };

        fixed (DescriptorPoolSize* poolSizesPtr = poolSizes)
        {
            poolInfo.PPoolSizes = poolSizesPtr;
            if (_vk.CreateDescriptorPool(_device, &poolInfo, null, out descriptorPool) != Result.Success)
            {
                throw new Exception("Failed to create descriptor pool!");
            }
        }
    }

    protected override void CreateDescriptorSets()
    {
        fixed (DescriptorSetLayout* setLayoutsPtr = &descriptorSetLayout)
        {
            DescriptorSetAllocateInfo allocInfo = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = setLayoutsPtr,
            };

            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            {
                fixed (DescriptorSet* setPtr = &descriptorSets[i])
                {
                    if (_vk.AllocateDescriptorSets(_device, &allocInfo, setPtr) != Result.Success)
                    {
                        throw new Exception("Failed to allocate descriptor sets!");
                    }
                }

                DescriptorBufferInfo bufferInfo = new()
                {
                    Buffer = uniformBuffers[i],
                    Offset = 0,
                    Range = (ulong)sizeof(UniformBufferObject),
                };

                DescriptorImageInfo imageInfo = new()
                {
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                    ImageView = textureView,
                    Sampler = textureSampler,
                };

                WriteDescriptorSet[] descriptorWrites =
                {
                    new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = descriptorSets[i],
                        DstBinding = 0,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.UniformBuffer,
                        PBufferInfo = &bufferInfo,
                    },
                    new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = descriptorSets[i],
                        DstBinding = 1,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.CombinedImageSampler,
                        PImageInfo = &imageInfo,
                    }
                };

                _vk.UpdateDescriptorSets(_device, (uint)descriptorWrites.Length, descriptorWrites, 0, ReadOnlySpan<CopyDescriptorSet>.Empty);
            }
        }
    }

    public override void RecordDrawCommands(CommandBuffer commandBuffer, int imageIndex)
    {
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _graphicsPipeline);

        ulong[] offsets = { 0 };
        fixed (Silk.NET.Vulkan.Buffer* vertexBuffersPtr = &vertexBuffer)
        {
            _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, vertexBuffersPtr, offsets);
        }

        _vk.CmdBindIndexBuffer(commandBuffer, indexBuffer, 0, IndexType.Uint32);

        fixed (DescriptorSet* setPtr = descriptorSets)
        {
            _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &setPtr[imageIndex], 0, null);
        }

        if (sphere != null)
        {
            _vk.CmdDrawIndexed(commandBuffer, (uint)sphere.Indices.Count, 1, 0, 0, 0);
        }
    }

    public override void RecordShadowCommands(CommandBuffer commandBuffer, int imageIndex)
    {
        // Black hole doesn't cast shadows
    }

    public override void UpdateVertexBuffer(Array NewVertexData)
    {
        // Black hole sphere is static
    }

    public override void UpdateUniformBuffer(int currentImage, UniformBufferObject ubo)
    {
        void* data;
        _vk.MapMemory(_device, uniformBuffersMemory[currentImage], 0, (ulong)sizeof(UniformBufferObject), 0, &data);
        new Span<UniformBufferObject>(data, 1)[0] = ubo;
        _vk.UnmapMemory(_device, uniformBuffersMemory[currentImage]);
    }

    public override void Dispose()
    {
        if (_vk == null || _device.Handle == 0)
            return;

        try
        {
            _vk.DeviceWaitIdle(_device);
        }
        catch
        {
            return;
        }

        if (uniformBuffers != null)
        {
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            {
                if (uniformBuffers[i].Handle != 0)
                    _vk.DestroyBuffer(_device, uniformBuffers[i], null);
            }
        }
        
        if (uniformBuffersMemory != null)
        {
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            {
                if (uniformBuffersMemory[i].Handle != 0)
                    _vk.FreeMemory(_device, uniformBuffersMemory[i], null);
            }
        }

        if (descriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, descriptorPool, null);
        if (descriptorSetLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, descriptorSetLayout, null);
        if (vertexBuffer.Handle != 0)
            _vk.DestroyBuffer(_device, vertexBuffer, null);
        if (vertexBufferMemory.Handle != 0)
            _vk.FreeMemory(_device, vertexBufferMemory, null);
        if (indexBuffer.Handle != 0)
            _vk.DestroyBuffer(_device, indexBuffer, null);
        if (indexBufferMemory.Handle != 0)
            _vk.FreeMemory(_device, indexBufferMemory, null);

        CleanupResources();
    }
}


