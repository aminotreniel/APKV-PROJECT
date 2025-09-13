
using System;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace TimeGhost
{
    
    
    public interface IImpostorRenderer
    {
        public enum RenderPass
        {
            Albedo,
            Normal,
            Alpha,
            Depth,
            Smoothness,
            Specular,
            Custom
        }
        
        public AABB GetBounds();

        public void SetBoundsExtraMargin(float3 margin);
        public void Render(PreviewRenderUtility previewRenderer, RenderPass currentOutput);
    }
    
    public interface IImpostorContext
    {
        
        public enum ImpostorTextureType
        {
            AlbedoAlpha,
            NormalDepth,
            SpecularSmoothness,
            Cutout,
            Custom
        }
        
        public struct CameraSettings
        {
            public float nearPlane;
            public float farPlane;
            public float fovYOrOrtoWidth;
            public float fovXOrOrthoHeight;
            public bool useOrthoGraphicProj;
        }
    
        public struct ImpostorFrameSetup
        {
            public CameraSettings settings;
            public float3 cameraPosition;
            public float3 cameraDirection;
        }
        
        public struct ImpostorTexture
        {
            public ImpostorTextureType type;
            public Texture2D texture;
        }
        
        public struct ImpostorFrameResources
        {
            public RenderTexture albedoAlpha;
            public RenderTexture normalDepth;
            public RenderTexture specSmoothness;
            public RenderTexture scratch;
            public RenderTexture cutout;
            public RenderTexture customOutput;

            public void Release()
            {
                DestroyImpostorFrameData(this);
            }
            
            public static ImpostorFrameResources CreateImpostorFrameData(uint2 resolution,  bool produceSpecularSmoothness, bool produceCustomTexture, bool useHighPrecision = false)
            {
                RenderTextureDescriptor textureDesc = ImpostorGeneratorUtils.GetRenderTextureDesc(resolution.x, resolution.y, useHighPrecision ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32);
                RenderTextureDescriptor descSpecSmoothness = ImpostorGeneratorUtils.GetRenderTextureDesc(resolution.x, resolution.y, useHighPrecision ? RenderTextureFormat.ARGBFloat :RenderTextureFormat.ARGB32);
                RenderTextureDescriptor descCutout = ImpostorGeneratorUtils.GetRenderTextureDesc(resolution.x, resolution.y, RenderTextureFormat.R8);
                RenderTextureDescriptor produceCustomOutput = ImpostorGeneratorUtils.GetRenderTextureDesc(resolution.x, resolution.y, useHighPrecision ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32);
    
    
                ImpostorFrameResources frameResources = new IImpostorContext.ImpostorFrameResources()
                {
                    albedoAlpha = new RenderTexture(textureDesc),
                    normalDepth = new RenderTexture(textureDesc),
                    specSmoothness = produceSpecularSmoothness ? new RenderTexture(descSpecSmoothness) : null,
                    customOutput = produceCustomTexture ? new RenderTexture(produceCustomOutput) : null,
                    scratch = new RenderTexture(textureDesc),
                    cutout = new RenderTexture(descCutout) 
                };
                frameResources.albedoAlpha.Create();
                frameResources.normalDepth.Create();
                frameResources.scratch.Create();
                frameResources.cutout.Create();
                
                if (produceSpecularSmoothness)
                {
                    frameResources.specSmoothness.Create();
                }
                
                if (produceCustomTexture)
                {
                    frameResources.customOutput.Create();
                }

                return frameResources;
            }

            public static void DestroyImpostorFrameData(in ImpostorFrameResources frameResources)
            {
                if (ImpostorGeneratorUtils.IsAllocated(frameResources.albedoAlpha))
                {
                    frameResources.albedoAlpha.Release();
                }
    
                if (ImpostorGeneratorUtils.IsAllocated(frameResources.normalDepth))
                {
                    frameResources.normalDepth.Release();
                }
    
                if (ImpostorGeneratorUtils.IsAllocated(frameResources.scratch))
                {
                    frameResources.scratch.Release();
                }
    
                if (ImpostorGeneratorUtils.IsAllocated(frameResources.specSmoothness))
                {
                    frameResources.specSmoothness.Release();
                }
                
                if (ImpostorGeneratorUtils.IsAllocated(frameResources.cutout))
                {
                    frameResources.cutout.Release();
                }
                
                if (ImpostorGeneratorUtils.IsAllocated(frameResources.customOutput))
                {
                    frameResources.customOutput.Release();
                }
            }
        }
        
        public void Init();
        public void Deinit();
        public void Render(in ImpostorFrameSetup frameSetup, in ImpostorFrameResources frameResources, in IImpostorRenderer renderer);
    }
    
    public class DefaultImpostorRenderer : IImpostorRenderer
    {
        public struct ImpostorSource
        {
            public Mesh mesh;
            public Material mat;
            public Material customOutputMaterial;
            public float4x4 transform;
            public MaterialPropertyBlock matPropBlock;
        }

        private ImpostorSource[] m_SourceEntries;
        private float3 m_ExtraMargin = float3.zero;

        public static AABB CalculateAABBForImpostors(ImpostorSource[] impostors)
        {
            Bounds bounds = new Bounds();
            bounds.min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            bounds.max = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);

            foreach (var impostor in impostors)
            {
                var impBounds = impostor.mesh.bounds;
                impBounds = ImpostorGeneratorUtils.TransformAABB(impBounds, impostor.transform);
                
                
                bounds.Encapsulate(impBounds);
            }

            return bounds.ToAABB();
        }

        public void SetBoundsExtraMargin(float3 margin)
        {
            m_ExtraMargin = margin;
        }
        
        public void SetImpostorSources(ImpostorSource[] sourceEntries)
        {
            m_SourceEntries = sourceEntries;
        }

        public AABB GetBounds()
        {
            AABB aabb = CalculateAABBForImpostors(m_SourceEntries);
            aabb.Extents += m_ExtraMargin;
            return aabb;
        }

        public void Render(PreviewRenderUtility previewRenderer, IImpostorRenderer.RenderPass pass)
        {
            foreach (var impostor in m_SourceEntries)
            {
                for (int subMesh = 0; subMesh < impostor.mesh.subMeshCount; ++subMesh)
                {
                    var mat = pass == IImpostorRenderer.RenderPass.Custom ? impostor.customOutputMaterial : impostor.mat;
                    mat.enableInstancing = true;
                    previewRenderer.DrawMesh(impostor.mesh, impostor.transform, mat, subMesh, impostor.matPropBlock);
                }
            }
        }
    }
    
    
    internal static class ImpostorRendererResources
    {
        internal static class UniformIDs
        {
            public static int _ClearValue;
            public static int _ZBufferParams;
            public static int _TextureResolution;
            public static int _TextureOffset;
            public static int _SourceTexture;
            public static int _SourceCutout;
            public static int _TargetTexture;
            public static int _TargetTexture2;
            public static int _DilateDirection;
            public static int _DilateStepCount;
            public static int _KernelSize;
            public static int _Flags;
            public static int _LightnessMaxBuffer;
        }
        
        internal static class Kernels
        {
            public static int kClear;
            public static int kExtractColor;
            public static int kExtractAlpha;
            public static int kExtractDepth;
            public static int kCopyToAtlas;
            public static int kCopyToAtlasCutout;
            public static int kDilateFrameColorAndAlphaChannels;
            public static int kDilateFrameColorChannels;
            public static int kProcessFrameAlpha;
            public static int kProduceCutout;
            public static int kProduceCutoutToAlphaChannel;
            public static int kDownSample;
            public static int kDownSampleCutout;
            public static int kCalculateMinMaxLightness;
            public static int kConvertAlbedoToLightness;

        }
    
        internal  static ComputeShader s_ComputeShader;

        //needs to match with shader
        internal const int DOWNSAMPLE_TARGET_IS_NORMALDEPTH = (1 << 0);
        internal const int DOWNSAMPLE_CALCULATE_VARIANCE = (1 << 1);
        internal const int PROCESS_ALPHA_PRODUCE_DISTANCE_FIELD = (1 << 2);

    
        internal static void InitializeStaticFields<T>(Type type, Func<string, T> construct)
        {
            foreach (var field in type.GetFields())
            {
                field.SetValue(null, construct(field.Name));
            }
        }
    
        internal static uint DivCeil(uint val, uint denom)
        {
            return (val + denom - 1) / denom;
        }
        internal static uint3 GetDispatchArgs(int kernelIndex, uint3 threadCount)
        {
            s_ComputeShader.GetKernelThreadGroupSizes(kernelIndex, out var groupX, out var groupY, out var groupZ );
            return new uint3(DivCeil(threadCount.x, groupX), DivCeil(threadCount.y, groupY), DivCeil(threadCount.z, groupZ));
        }
    
        internal static void StaticInitialize()
        {
            if (s_ComputeShader == null)
            {
                s_ComputeShader = Resources.Load<ComputeShader>("ImpostorGeneratorCS");
                InitializeStaticFields(typeof(UniformIDs), (string s) => Shader.PropertyToID(s));
                InitializeStaticFields(typeof(Kernels), (string s) => s_ComputeShader.FindKernel(s));
            }
        }
    
    }
            
}