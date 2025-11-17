using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using blackHole.Core.Math;
using blackHole.Renderer.Vulkan;
using blackHole.Tools;
using Silk.NET.Maths;
using Silk.NET.Vulkan;

namespace blackHole.Simulation.Entities;

/// <summary>
/// Class representing a dust particle in the simulation
/// Hive entity
/// </summary>
public class Dust
{
    public Guid Id;
    public Vector3D<float> Position;
    public Vector3D<float> Velocity;

    public static readonly float DustSize = 0.1f; // Example size

    public Vertex VertexData;

    // Black hole gravity parameters
    private Vector3D<float> _blackHolePosition = Vector3D<float>.Zero;
    private float _blackHoleMass = 0f;

    public Dust()
    {
        Id = Guid.NewGuid();
        Position = Vector3D<float>.Zero;
        Velocity = Vector3D<float>.Zero;
    }

    public Dust(
        Vector3D<float> position,
        Vector3D<float> velocity,
        Vector3D<float> blackHolePosition,
        float blackHoleMass
    )
    {
        Id = Guid.NewGuid();
        Position = position;
        Velocity = velocity;
        _blackHolePosition = blackHolePosition;
        _blackHoleMass = blackHoleMass;
        VertexData = new Vertex
        {
            Position = new Vector3(position.X, position.Y, position.Z),
            Normal = Vector3.Normalize(new Vector3(position.X, position.Y, position.Z)),
            Color = new Vector3(0.8f, 0.8f, 1.0f), // Light blue-white dust
            TexCoord = Vector2.Zero, // not used for dust
        };
    }

    public static implicit operator List<object>(Dust v)
    {
        throw new NotImplementedException();
    }

    public override string ToString()
    {
        return $"Dust(Id: {Id}, Position: {Position}, Velocity: {Velocity})";
    }

    public void UpdatePosition(double deltaTime)
    {
        // Calculate gravitational acceleration towards the black hole
        Vector3D<float> toBlackHole = _blackHolePosition - Position;
        float distance = toBlackHole.Length;

        if (distance > 0.1f) // Avoid division by zero and singularity
        {
            Vector3D<float> direction = Vector3D.Normalize(toBlackHole);
            // F = GMm/r^2, but we use a = GM/r^2 (simplified with G=1)
            float accelerationMagnitude = _blackHoleMass / (distance * distance);
            Vector3D<float> acceleration = direction * accelerationMagnitude;

            // Update velocity with acceleration
            Velocity += acceleration * (float)deltaTime;
        }

        // Update position
        Position += Velocity * (float)deltaTime;

        // Update VertexData position
        VertexData.Position = new Vector3(Position.X, Position.Y, Position.Z);
        VertexData.Normal = Vector3.Normalize(new Vector3(Position.X, Position.Y, Position.Z));
    }

    public static void updateAllDustPositions(List<Dust> dustList, double deltaTime)
    {
        Parallel.ForEach(
            dustList,
            dust =>
            {
                dust.UpdatePosition(deltaTime);
            }
        );
    }

    public static void generateDustFieldDisk(
        List<Dust> dustList,
        int count,
        float radius,
        float holeRadius,
        float height,
        float blackHoleMass,
        LambdaExpression? gradientFunction = null
    )
    {
        var randThreadLocal = new ThreadLocal<Random>(() => new Random());
        var concurrentBag = new ConcurrentBag<Dust>();
        Parallel.For(
            0,
            count,
            i =>
            {
                var rand = randThreadLocal.Value!;
                // Generate random angle and distance from center
                double angle = rand.NextDouble() * 2.0 * Math.PI;
                double distance = Math.Sqrt(rand.NextDouble()) * radius; // sqrt for uniform distribution

                if (distance < holeRadius)
                {
                    distance = holeRadius;
                }

                // Calculate position in disk
                float x = (float)(distance * Math.Cos(angle));
                float y = (float)(rand.NextDouble() * height - height / 2); // Random height within the disk thickness
                float z = (float)(distance * Math.Sin(angle));

                Vector3D<float> position = new Vector3D<float>(x, y, z);

                // Initial velocity can be set to zero or some small random value
                Vector3D<float> velocity = MathHelpers.CreateOrbit(
                    Vector3D<float>.Zero,
                    position,
                    blackHoleMass,
                    1f
                );

                concurrentBag.Add(
                    new Dust(position, velocity, Vector3D<float>.Zero, blackHoleMass)
                );
            }
        );
        dustList.AddRange(concurrentBag);
    }
}

/// <summary>
/// Rendering pipeline for dust particles - renders as points
/// </summary>
public unsafe class DustPipeline : PipelineBuilder
{
    private const int MAX_FRAMES_IN_FLIGHT = 2;
    private Vertex[]? _vertexData;
    private Silk.NET.Vulkan.Buffer[] vertexBuffers;
    private DeviceMemory[] vertexBufferMemories;
    private Silk.NET.Vulkan.Buffer[] uniformBuffers;
    private DeviceMemory[] uniformBuffersMemory;
    private DescriptorPool descriptorPool;
    private DescriptorSet[] descriptorSets;
    private DescriptorSetLayout descriptorSetLayout;

    protected override PrimitiveTopology Topology => PrimitiveTopology.PointList;
    protected override CullModeFlags CullMode => CullModeFlags.None;

    public DustPipeline(Vulkan vulkan)
        : base(vulkan)
    {
        vertexBuffers = new Silk.NET.Vulkan.Buffer[MAX_FRAMES_IN_FLIGHT];
        vertexBufferMemories = new DeviceMemory[MAX_FRAMES_IN_FLIGHT];
        uniformBuffers = new Silk.NET.Vulkan.Buffer[MAX_FRAMES_IN_FLIGHT];
        uniformBuffersMemory = new DeviceMemory[MAX_FRAMES_IN_FLIGHT];
        descriptorSets = new DescriptorSet[MAX_FRAMES_IN_FLIGHT];
    }

    public void SetVertexArray(Vertex[] vertices)
    {
        _vertexData = vertices;
    }

    protected override void CreateVertexBuffer()
    {
        if (_vertexData == null || _vertexData.Length == 0)
            throw new Exception("Vertex data not set for DustPipeline.");

        ulong bufferSize = (ulong)(_vertexData.Length * Marshal.SizeOf<Vertex>());

        // Create vertex buffer for each frame in flight
        for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            _vulkan.CreateBuffer(
                bufferSize,
                BufferUsageFlags.VertexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                ref vertexBuffers[i],
                ref vertexBufferMemories[i]
            );

            // Initial data upload
            void* data;
            _vk.MapMemory(_device, vertexBufferMemories[i], 0, bufferSize, 0, &data);
            fixed (Vertex* arrayPtr = _vertexData)
            {
                System.Buffer.MemoryCopy(arrayPtr, data, (long)bufferSize, (long)bufferSize);
            }
            _vk.UnmapMemory(_device, vertexBufferMemories[i]);
        }
    }

    protected override void CreateIndexBuffer()
    {
        // Dust particles don't use indices - rendered as points
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
        // Create a simple 1x1 white texture for dust
        uint width = 1;
        uint height = 1;
        byte[] pixels = { 255, 255, 255, 255 };

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

        TransitionImageLayout(
            textureImage,
            Format.R8G8B8A8Srgb,
            ImageLayout.Undefined,
            ImageLayout.TransferDstOptimal
        );
        CopyBufferToImage(stagingBuffer, textureImage, width, height);
        TransitionImageLayout(
            textureImage,
            Format.R8G8B8A8Srgb,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal
        );

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
            },
        };

        fixed (DescriptorSetLayoutBinding* bindingsPtr = bindings)
        {
            DescriptorSetLayoutCreateInfo layoutInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = bindingsPtr,
            };

            if (
                _vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out descriptorSetLayout)
                != Result.Success
            )
            {
                throw new Exception("Failed to create descriptor set layout!");
            }
        }
    }

    protected override void CreateDescriptorPool()
    {
        DescriptorPoolSize[] poolSizes =
        {
            new DescriptorPoolSize
            {
                Type = DescriptorType.UniformBuffer,
                DescriptorCount = (uint)MAX_FRAMES_IN_FLIGHT,
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = (uint)MAX_FRAMES_IN_FLIGHT,
            },
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
            if (
                _vk.CreateDescriptorPool(_device, &poolInfo, null, out descriptorPool)
                != Result.Success
            )
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
                    },
                };

                _vk.UpdateDescriptorSets(
                    _device,
                    (uint)descriptorWrites.Length,
                    descriptorWrites,
                    0,
                    ReadOnlySpan<CopyDescriptorSet>.Empty
                );
            }
        }
    }

    public override void RecordDrawCommands(CommandBuffer commandBuffer, int imageIndex)
    {
        if (_vertexData == null || _vertexData.Length == 0)
            return;

        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _graphicsPipeline);

        ulong[] offsets = { 0 };
        fixed (Silk.NET.Vulkan.Buffer* vertexBuffersPtr = &vertexBuffers[imageIndex])
        {
            _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, vertexBuffersPtr, offsets);
        }

        fixed (DescriptorSet* setPtr = descriptorSets)
        {
            _vk.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Graphics,
                _pipelineLayout,
                0,
                1,
                &setPtr[imageIndex],
                0,
                null
            );
        }

        _vk.CmdDraw(commandBuffer, (uint)_vertexData.Length, 1, 0, 0);
    }

    public override void RecordShadowCommands(CommandBuffer commandBuffer, int imageIndex)
    {
        // Dust doesn't cast shadows
    }

    public void UpdateVertexBuffer(Vertex[] vertices, int frameIndex)
    {
        if (_vertexData == null || vertices.Length != _vertexData.Length)
        {
            throw new ArgumentException(
                $"Vertex count mismatch: expected {_vertexData?.Length ?? 0}, got {vertices.Length}"
            );
        }

        ulong bufferSize = (ulong)(vertices.Length * sizeof(Vertex));

        // Map memory and copy new data ONLY for this frame's buffer
        // Since we use HostCoherent memory, changes are automatically visible to GPU
        void* data;
        if (
            _vk.MapMemory(_device, vertexBufferMemories[frameIndex], 0, bufferSize, 0, &data)
            == Result.Success
        )
        {
            fixed (Vertex* arrayPtr = vertices)
            {
                System.Buffer.MemoryCopy(arrayPtr, data, (long)bufferSize, (long)bufferSize);
            }
            _vk.UnmapMemory(_device, vertexBufferMemories[frameIndex]);
        }
    }

    public override void UpdateVertexBuffer(Array NewVertexData)
    {
        if (NewVertexData is not Vertex[] vertices)
            throw new ArgumentException("NewVertexData must be Vertex[]");

        for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            UpdateVertexBuffer(vertices, i);
        }
    }

    public override void UpdateUniformBuffer(int currentImage, UniformBufferObject ubo)
    {
        void* data;
        _vk.MapMemory(
            _device,
            uniformBuffersMemory[currentImage],
            0,
            (ulong)sizeof(UniformBufferObject),
            0,
            &data
        );
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

            if (vertexBuffers != null)
            {
                for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                {
                    if (vertexBuffers[i].Handle != 0)
                        _vk.DestroyBuffer(_device, vertexBuffers[i], null);
                }
            }

            if (vertexBufferMemories != null)
            {
                for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                {
                    if (vertexBufferMemories[i].Handle != 0)
                        _vk.FreeMemory(_device, vertexBufferMemories[i], null);
                }
            }

            CleanupResources();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DustPipeline.Dispose error: {ex.Message}");
        }
    }
}
