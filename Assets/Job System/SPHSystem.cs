﻿using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Transforms;
using Unity.Mathematics;
using Unity.Entities;
using Unity.Burst;
using Unity.Jobs;

[UpdateBefore(typeof(TransformSystemGroup))]
public class SPHSystem : JobComponentSystem
{

    private EntityQuery SPHCharacterGroup;
    private EntityQuery SPHColliderGroup;

    private JobHandle collidersToNativeArrayJobHandle;
    private NativeArray<SPHCollider> colliders;

    private Transform cameraTransform;

    private List<SPHParticle> uniqueTypes = new List<SPHParticle>(10);
    private List<PreviousParticle> previousParticles = new List<PreviousParticle>();

    private static readonly int[] cellOffsetTable =
    {
        1, 1, 1, 1, 1, 0, 1, 1, -1, 1, 0, 1, 1, 0, 0, 1, 0, -1, 1, -1, 1, 1, -1, 0, 1, -1, -1,
        0, 1, 1, 0, 1, 0, 0, 1, -1, 0, 0, 1, 0, 0, 0, 0, 0, -1, 0, -1, 1, 0, -1, 0, 0, -1, -1,
        -1, 1, 1, -1, 1, 0, -1, 1, -1, -1, 0, 1, -1, 0, 0, -1, 0, -1, -1, -1, 1, -1, -1, 0, -1, -1, -1
    };



    private struct PreviousParticle
    {
        #pragma warning disable 0649
        public NativeMultiHashMap<int, int> hashMap;
        public NativeArray<Translation> particlesPosition;
        public NativeArray<SPHVelocity> particlesVelocity;
        public NativeArray<float3> particlesForces;
        public NativeArray<float> particlesPressure;
        public NativeArray<float> particlesDensity;
        public NativeArray<int> particleIndices;

        public NativeArray<int> cellOffsetTable;
        #pragma warning restore 0649
    }



    [BurstCompile]
    private struct HashPositions : IJobParallelFor
    {
        #pragma warning disable 0649
        [ReadOnly] public float cellRadius;

        public NativeArray<Translation> positions;
        public NativeMultiHashMap<int, int>.ParallelWriter hashMap;
        #pragma warning restore 0649

        public void Execute(int index)
        {
            float3 position = positions[index].Value;

            int hash = GridHash.Hash(position, cellRadius);
            hashMap.Add(hash, index);

            positions[index] = new Translation { Value = position };
        }
    }



    [BurstCompile]
    private struct MergeParticles : IJobNativeMultiHashMapMergedSharedKeyIndices
    {
        public NativeArray<int> particleIndices;
        #pragma warning restore 0649



        public void ExecuteFirst(int index)
        {
            particleIndices[index] = index;
        }


        public void ExecuteNext(int cellIndex, int index)
        {
            particleIndices[index] = cellIndex;
        }
    }



    [BurstCompile]
    private struct ComputeDensityPressure : IJobParallelFor
    {
        #pragma warning disable 0649
        [ReadOnly] public NativeMultiHashMap<int, int> hashMap;
        [ReadOnly] public NativeArray<int> cellOffsetTable;
        [ReadOnly] public NativeArray<Translation> particlesPosition;
        [ReadOnly] public SPHParticle settings;

        public NativeArray<float> densities;
        public NativeArray<float> pressures;
        #pragma warning restore 0649

        private const float PI = 3.14159274F;
        private const float GAS_CONST = 2000.0f;

        

        public void Execute(int index)
        {
            // Cache
            int particleCount = particlesPosition.Length;
            float3 position = particlesPosition[index].Value;
            float density = 0.0f;
            int i, hash, j;
            int3 gridOffset;
            int3 gridPosition = GridHash.Quantize(position, settings.radius);
            bool found;

            // Find neighbors
            for (int oi = 0; oi < 27; oi++)
            {
                i = oi * 3;
                gridOffset = new int3(cellOffsetTable[i], cellOffsetTable[i + 1], cellOffsetTable[i + 2]);
                hash = GridHash.Hash(gridPosition + gridOffset);
                NativeMultiHashMapIterator<int> iterator;
                found = hashMap.TryGetFirstValue(hash, out j, out iterator);
                while (found)
                {
                    // Neighbor found, get density
                    float3 rij = particlesPosition[j].Value - position;
                    float r2 = math.lengthsq(rij);

                    if (r2 < settings.smoothingRadiusSq)
                    {
                        density += settings.mass * (315.0f / (64.0f * PI * math.pow(settings.smoothingRadius, 9.0f))) * math.pow(settings.smoothingRadiusSq - r2, 3.0f);
                    }

                    // Next neighbor
                    found = hashMap.TryGetNextValue(out j, ref iterator);
                }
            }

            // Apply density and compute/apply pressure
            densities[index] = density;
            pressures[index] = GAS_CONST * (density - settings.restDensity);
        }
    }



    [BurstCompile]
    private struct ComputeForces : IJobParallelFor
    {
        #pragma warning disable 0649
        [ReadOnly] public NativeMultiHashMap<int, int> hashMap;
        [ReadOnly] public NativeArray<int> cellOffsetTable;
        [ReadOnly] public NativeArray<Translation> particlesPosition;
        [ReadOnly] public NativeArray<SPHVelocity> particlesVelocity;
        [ReadOnly] public NativeArray<float> particlesPressure;
        [ReadOnly] public NativeArray<float> particlesDensity;
        [ReadOnly] public SPHParticle settings;

        public NativeArray<float3> particlesForces;
        #pragma warning restore 0649

        private const float PI = 3.14159274F;



        public void Execute(int index)
        {
            // Cache
            int particleCount = particlesPosition.Length;
            float3 position = particlesPosition[index].Value;
            float3 velocity = particlesVelocity[index].Value;
            float pressure = particlesPressure[index];
            float density = particlesDensity[index];
            float3 forcePressure = new float3(0, 0, 0);
            float3 forceViscosity = new float3(0, 0, 0);
            int i, hash, j;
            int3 gridOffset;
            int3 gridPosition = GridHash.Quantize(position, settings.radius);
            bool found;

            // Physics
            // Find neighbors
            for (int oi = 0; oi < 27; oi++)
            {
                i = oi * 3;
                gridOffset = new int3(cellOffsetTable[i], cellOffsetTable[i + 1], cellOffsetTable[i + 2]);
                hash = GridHash.Hash(gridPosition + gridOffset);
                NativeMultiHashMapIterator<int> iterator;
                found = hashMap.TryGetFirstValue(hash, out j, out iterator);
                while (found)
                {
                    // Neighbor found, get density
                    if (index == j)
                    {
                        found = hashMap.TryGetNextValue(out j, ref iterator);
                        continue;
                    }

                    float3 rij = particlesPosition[j].Value - position;
                    float r2 = math.lengthsq(rij);
                    float r = math.sqrt(r2);

                    if (r < settings.smoothingRadius)
                    {
                        forcePressure += -math.normalize(rij) * settings.mass * (2.0f * pressure) / (2.0f * density) * (-45.0f / (PI * math.pow(settings.smoothingRadius, 6.0f))) * math.pow(settings.smoothingRadius - r, 2.0f);

                        forceViscosity += settings.viscosity * settings.mass * (particlesVelocity[j].Value - velocity) / density * (45.0f / (PI * math.pow(settings.smoothingRadius, 6.0f))) * (settings.smoothingRadius - r);
                    }

                    // Next neighbor
                    found = hashMap.TryGetNextValue(out j, ref iterator);
                }
            }

            // Gravity
            float3 forceGravity = new float3(0.0f, -9.81f, 0.0f) * density * settings.gravityMult;

            // Apply
            particlesForces[index] = forcePressure + forceViscosity + forceGravity;
        }
    }



    [BurstCompile]
    private struct Integrate : IJobParallelFor
    {
        #pragma warning disable 0649
        [ReadOnly] public NativeArray<float3> particlesForces;
        [ReadOnly] public NativeArray<float> particlesDensity;

        public NativeArray<Translation> particlesPosition;
        public NativeArray<SPHVelocity> particlesVelocity;
        #pragma warning restore 0649

        private const float DT = 0.0008f;



        public void Execute(int index)
        {
            // Cache
            float3 velocity = particlesVelocity[index].Value;
            float3 position = particlesPosition[index].Value;

            // Process
            velocity += DT * particlesForces[index] / particlesDensity[index];
            position += DT * velocity;

            // Apply
            particlesVelocity[index] = new SPHVelocity { Value = velocity };
            particlesPosition[index] = new Translation { Value = position };
        }
    }



    [BurstCompile]
    private struct ComputeColliders : IJobParallelFor
    {
        #pragma warning disable 0649
        [ReadOnly] public SPHParticle settings;
        [ReadOnly] public NativeArray<SPHCollider> copyColliders;

        public NativeArray<Translation> particlesPosition;
        public NativeArray<SPHVelocity> particlesVelocity;
        #pragma warning restore 0649

        private const float BOUND_DAMPING = -0.5f;



        private static bool Intersect(SPHCollider collider, float3 position, float radius, out float3 penetrationNormal, out float3 penetrationPosition, out float penetrationLength)
        {
            float3 colliderProjection = collider.position - position;

            penetrationNormal = math.cross(collider.right, collider.up);
            penetrationLength = math.abs(math.dot(colliderProjection, penetrationNormal)) - (radius / 2.0f);
            penetrationPosition = collider.position - colliderProjection;

            return penetrationLength < 0.0f
                && math.abs(math.dot(colliderProjection, collider.right)) < collider.scale.x
                && math.abs(math.dot(colliderProjection, collider.up)) < collider.scale.y;
        }



        private static Vector3 DampVelocity(SPHCollider collider, float3 velocity, float3 penetrationNormal, float drag)
        {
            float3 newVelocity = math.dot(velocity, penetrationNormal) * penetrationNormal * BOUND_DAMPING
                                + math.dot(velocity, collider.right) * collider.right * drag
                                + math.dot(velocity, collider.up) * collider.up * drag;
            newVelocity = math.dot(newVelocity, new float3(0, 0, 1)) * new float3(0, 0, 1)
                        + math.dot(newVelocity, new float3(1, 0, 0)) * new float3(1, 0, 0)
                        + math.dot(newVelocity, new float3(0, 1, 0)) * new float3(0, 1, 0);
            return newVelocity;
        }



        public void Execute(int index)
        {
            // Cache
            int colliderCount = copyColliders.Length;
            float3 position = particlesPosition[index].Value;
            float3 velocity = particlesVelocity[index].Value;

            // Process
            for (int i = 0; i < colliderCount; i++)
            {
                float3 penetrationNormal;
                float3 penetrationPosition;
                float penetrationLength;
                if (Intersect(copyColliders[i], position, settings.radius, out penetrationNormal, out penetrationPosition, out penetrationLength))
                {
                    velocity = DampVelocity(copyColliders[i], velocity, penetrationNormal, 1.0f - settings.drag);
                    position = penetrationPosition - penetrationNormal * math.abs(penetrationLength);
                }
            }

            // Apply
            particlesVelocity[index] = new SPHVelocity { Value = velocity };
            particlesPosition[index] = new Translation { Value = position };
        }
    }
    [BurstCompile]
    private struct ApplyTranslations : IJobForEachWithEntity<Translation, SPHVelocity>
    {
        [ReadOnly] public NativeArray<Translation> particlesPosition;
        [ReadOnly] public NativeArray<SPHVelocity> particlesVelocity;

        public void Execute(Entity entity, int index, ref Translation translation, ref SPHVelocity sphCollider)
        {
            translation = new Translation { Value = particlesPosition[index].Value };
            sphCollider = new SPHVelocity { Value = particlesVelocity[index].Value };
        }
    }
    


    protected override void OnCreate()
    {
        // Import
        SPHCharacterGroup = GetEntityQuery(ComponentType.ReadOnly(typeof(SPHParticle)), typeof(Translation), typeof(SPHVelocity));
        SPHColliderGroup = GetEntityQuery(ComponentType.ReadOnly(typeof(SPHCollider)));
    }



    protected override void OnStartRunning()
    {
        // Get the colliders
        colliders = SPHColliderGroup.ToComponentDataArray<SPHCollider>(Allocator.Persistent, out collidersToNativeArrayJobHandle);
    }
    


    protected override JobHandle OnUpdate(JobHandle inputDeps)
    {
        if (cameraTransform == null)
            cameraTransform = GameObject.Find("Main Camera").transform;

        EntityManager.GetAllUniqueSharedComponentData(uniqueTypes);

        for (int typeIndex = 1; typeIndex < uniqueTypes.Count; typeIndex++)
        {
            // Get the current chunk setting
            SPHParticle settings = uniqueTypes[typeIndex];
            SPHCharacterGroup.SetSharedComponentFilter(settings);

            // Cache the data
            JobHandle particlesPositionJobHandle;
            NativeArray<Translation> particlesPosition = SPHCharacterGroup.ToComponentDataArray<Translation>(Allocator.TempJob, out particlesPositionJobHandle);
            JobHandle particlesVelocityJobHandle;
            NativeArray<SPHVelocity> particlesVelocity = SPHCharacterGroup.ToComponentDataArray<SPHVelocity>(Allocator.TempJob, out particlesVelocityJobHandle);

            int cacheIndex = typeIndex - 1;
            int particleCount = particlesPosition.Length;

            NativeMultiHashMap<int, int> hashMap = new NativeMultiHashMap<int, int>(particleCount, Allocator.TempJob);

            NativeArray<float3> particlesForces = new NativeArray<float3>(particleCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            NativeArray<float> particlesPressure = new NativeArray<float>(particleCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            NativeArray<float> particlesDensity = new NativeArray<float>(particleCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            NativeArray<int> particleIndices = new NativeArray<int>(particleCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            NativeArray<int> cellOffsetTableNative = new NativeArray<int>(cellOffsetTable, Allocator.TempJob);



            // Add new or dispose previous particle chunks
            PreviousParticle nextParticles = new PreviousParticle
            {
                hashMap = hashMap,
                particlesPosition = particlesPosition,
                particlesVelocity = particlesVelocity,
                particlesForces = particlesForces,
                particlesPressure = particlesPressure,
                particlesDensity = particlesDensity,
                particleIndices = particleIndices,
                cellOffsetTable = cellOffsetTableNative
            };

            if (cacheIndex > previousParticles.Count - 1)
            {
                previousParticles.Add(nextParticles);
            }
            else
            {
                previousParticles[cacheIndex].hashMap.Dispose();
                previousParticles[cacheIndex].particlesPosition.Dispose();
                previousParticles[cacheIndex].particlesVelocity.Dispose();
                previousParticles[cacheIndex].particlesForces.Dispose();
                previousParticles[cacheIndex].particlesPressure.Dispose();
                previousParticles[cacheIndex].particlesDensity.Dispose();
                previousParticles[cacheIndex].particleIndices.Dispose();
                previousParticles[cacheIndex].cellOffsetTable.Dispose();
            }
            previousParticles[cacheIndex] = nextParticles;



            // Initialize the empty arrays with a default value
            MemsetNativeArray<float> particlesPressureJob = new MemsetNativeArray<float> { Source = particlesPressure, Value = 0.0f };
            JobHandle particlesPressureJobHandle = particlesPressureJob.Schedule(particleCount, 64, inputDeps);

            MemsetNativeArray<float> particlesDensityJob = new MemsetNativeArray<float> { Source = particlesDensity, Value = 0.0f };
            JobHandle particlesDensityJobHandle = particlesDensityJob.Schedule(particleCount, 64, inputDeps);

            MemsetNativeArray<int> particleIndicesJob = new MemsetNativeArray<int> { Source = particleIndices, Value = 0 };
            JobHandle particleIndicesJobHandle = particleIndicesJob.Schedule(particleCount, 64, inputDeps);

            MemsetNativeArray<float3> particlesForcesJob = new MemsetNativeArray<float3> { Source = particlesForces, Value = new float3(0, 0, 0) };
            JobHandle particlesForcesJobHandle = particlesForcesJob.Schedule(particleCount, 64, inputDeps);



            // Put positions into a hashMap
            HashPositions hashPositionsJob = new HashPositions
            {
                positions = particlesPosition,
                hashMap = hashMap.AsParallelWriter(),
                cellRadius = settings.radius
            };
            JobHandle hashPositionsJobHandle = hashPositionsJob.Schedule(particleCount, 64, particlesPositionJobHandle);

            JobHandle mergedPositionIndicesJobHandle = JobHandle.CombineDependencies(hashPositionsJobHandle, particleIndicesJobHandle);

            MergeParticles mergeParticlesJob = new MergeParticles
            {
                particleIndices = particleIndices
            };
            JobHandle mergeParticlesJobHandle = mergeParticlesJob.Schedule(hashMap, 64, mergedPositionIndicesJobHandle);

            JobHandle mergedMergedParticlesDensityPressure = JobHandle.CombineDependencies(mergeParticlesJobHandle, particlesPressureJobHandle, particlesDensityJobHandle);

            // Compute density pressure
            ComputeDensityPressure computeDensityPressureJob = new ComputeDensityPressure
            {
                particlesPosition = particlesPosition,
                densities = particlesDensity,
                pressures = particlesPressure,
                hashMap = hashMap,
                cellOffsetTable = cellOffsetTableNative,
                settings = settings
            };
            JobHandle computeDensityPressureJobHandle = computeDensityPressureJob.Schedule(particleCount, 64, mergedMergedParticlesDensityPressure);

            JobHandle mergeComputeDensityPressureVelocityForces = JobHandle.CombineDependencies(computeDensityPressureJobHandle, particlesForcesJobHandle, particlesVelocityJobHandle);

            // Compute forces
            ComputeForces computeForcesJob = new ComputeForces
            {
                particlesPosition = particlesPosition,
                particlesVelocity = particlesVelocity,
                particlesForces = particlesForces,
                particlesPressure = particlesPressure,
                particlesDensity = particlesDensity,
                cellOffsetTable = cellOffsetTableNative,
                hashMap = hashMap,
                settings = settings
            };
            JobHandle computeForcesJobHandle = computeForcesJob.Schedule(particleCount, 64, mergeComputeDensityPressureVelocityForces);

            // Integrate
            Integrate integrateJob = new Integrate
            {
                particlesPosition = particlesPosition,
                particlesVelocity = particlesVelocity,
                particlesDensity = particlesDensity,
                particlesForces = particlesForces
            };
            JobHandle integrateJobHandle = integrateJob.Schedule(particleCount, 64, computeForcesJobHandle);

            JobHandle mergedIntegrateCollider = JobHandle.CombineDependencies(integrateJobHandle, collidersToNativeArrayJobHandle);

            // Compute Colliders
            ComputeColliders computeCollidersJob = new ComputeColliders
            {
                particlesPosition = particlesPosition,
                particlesVelocity = particlesVelocity,
                copyColliders = colliders,
                settings = settings
            };
            JobHandle computeCollidersJobHandle = computeCollidersJob.Schedule(particleCount, 64, mergedIntegrateCollider);

            // Apply translations and velocities
            ApplyTranslations applyTranslationsJob = new ApplyTranslations
            {
                particlesPosition = particlesPosition,
                particlesVelocity = particlesVelocity
            };
            JobHandle applyTranslationsJobHandle = applyTranslationsJob.Schedule(this, computeCollidersJobHandle);

            inputDeps = applyTranslationsJobHandle;
        }

        // Done
        uniqueTypes.Clear();
        return inputDeps;
    }



    protected override void OnStopRunning()
    {
        EntityManager.CompleteAllJobs();

        for (int i = 0; i < previousParticles.Count; i++)
        {
            previousParticles[i].hashMap.Dispose();
            previousParticles[i].particlesPosition.Dispose();
            previousParticles[i].particlesVelocity.Dispose();
            previousParticles[i].particlesForces.Dispose();
            previousParticles[i].particlesPressure.Dispose();
            previousParticles[i].particlesDensity.Dispose();
            previousParticles[i].particleIndices.Dispose();
            previousParticles[i].cellOffsetTable.Dispose();
        }

        colliders.Dispose();

        previousParticles.Clear();
    }
}
