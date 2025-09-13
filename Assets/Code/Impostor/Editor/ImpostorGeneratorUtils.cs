using System;
using System.IO;
using Unity.Assertions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TimeGhost
{
    public static class ImpostorGeneratorUtils
    {
        public static float2 HemiOctEncode(float3 d)
        {
            float2 t = d.xz * (1.0f / (math.abs(d.x) + math.abs(d.y) + math.abs(d.z)));
            return new float2(t.x + t.y, t.y - t.x) * 0.5f + 0.5f;
        }

        public static float3 HemiOctDecode(float2 uv)
        {
            float3 position = new float3(uv.x - uv.y, 0.0f, -1.0f + uv.x + uv.y);
            float2 absolute = math.abs(position.xz);
            position.y = 1.0f - absolute.x - absolute.y;
            return position;
        }

        public static float2 OctEncode(float3 d)
        {
            float sum = math.abs(d.x) + math.abs(d.y) + math.abs(d.z);        
            float3 p = d / sum;
            float t = math.saturate(-p.y);
            p.xz = math.sign(p.xz) * new float2(math.abs(p.xz) + t);
            return p.xz * 0.5f + 0.5f;
        }

        public static float3 OctDecode(float2 uv)
        {
            uv = uv * 2.0f - 1.0f;
            float3 p = new float3(uv.x, 1.0f - math.abs(uv.x) - math.abs(uv.y), uv.y);          
            float t = math.saturate(-p.y);
            p.xz = math.sign(p.xz) * new float2(math.abs(p.xz) - t);
            return p;
        }

        public static Bounds TransformAABB(Bounds aabb, float4x4 matrix)
        {
            Bounds bounds = new Bounds();
            bounds.min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            bounds.max = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);

            for (int i = 0; i < 8; ++i)
            {
                float x = i % 2  == 0 ? aabb.min.x : aabb.max.x;
                float y = (i / 2) % 2 == 0 ? aabb.min.y : aabb.max.y;
                float z = i < 4 ? aabb.min.z : aabb.max.z;

                var p = math.mul(matrix,  new float4(x, y, z, 1));
                
                bounds.Encapsulate(p.xyz);
            }

            return bounds;

        }

        public static Texture2D WriteTextureToDisk(string path, Texture2D tex, bool writeAsPng)
        {
            if (writeAsPng)
            {
                path += ".png";
                var bytes = tex.EncodeToPNG();
                File.WriteAllBytes(Path.GetFullPath(path), bytes);
            }
            else
            {
                path += ".asset";
                tex.Compress(true);
                ForceCreateAsset(path, tex);
            }
            
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        public static void ForceCreateAsset(string path, Object asset)
        {
            if (AssetDatabase.AssetPathExists(path))
            {
                AssetDatabase.DeleteAsset(path);
            }

            var directory = Path.GetDirectoryName(path);
            
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            AssetDatabase.CreateAsset(asset, path);
        }

        public static void CalculateCameraParametersAroundBounds(in AABB bounds, in float cameraDistanceFromTarget, out float nearPlane, out float farPlane, out float fovXRadians, out float fovYRadians,  out float cameraDistanceFromBoundsCenter)
        {
            float r = math.length(bounds.Extents);

            cameraDistanceFromBoundsCenter = cameraDistanceFromTarget + r;
            nearPlane = cameraDistanceFromTarget;
            farPlane = cameraDistanceFromTarget + 2 * r;

            float angle = math.atan(r / cameraDistanceFromBoundsCenter);
            fovXRadians = angle * 2;
            fovYRadians = angle * 2;
        }
        
        public static void ClearTexture(CommandBuffer cmd, RenderTexture rt, float4 clearValue)
        {
            int3 dispatchArgs = (int3)ImpostorRendererResources.GetDispatchArgs(ImpostorRendererResources.Kernels.kClear, new uint3((uint)rt.width, (uint)rt.height, 1));

            cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._TextureResolution, rt.width, rt.height);
            cmd.SetComputeVectorParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._ClearValue, clearValue);
            cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kClear, ImpostorRendererResources.UniformIDs._TargetTexture, rt);
            cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kClear, dispatchArgs.x, dispatchArgs.y, dispatchArgs.z);
        }

        public static void DownSample(IImpostorContext.ImpostorFrameResources source, int numberOfDownSamplePasses, bool produceVarianceTargets, out IImpostorContext.ImpostorFrameResources downsampledFrameResources, out IImpostorContext.ImpostorFrameResources standardDevOut)
        {
            int2 res = new int2(source.albedoAlpha.width, source.albedoAlpha.height);
            Assert.IsTrue(math.ispow2(res.x) && math.ispow2(res.y));
            Assert.IsTrue(res.x > 1 && res.y > 1);
            Assert.IsTrue(numberOfDownSamplePasses > 0);

            float maxNumberOfPasses = math.floor(math.log2(math.min(res.x, res.y)));
            int numberOfPasses = (int)math.min(maxNumberOfPasses, numberOfDownSamplePasses);

            Action<CommandBuffer, RenderTexture, RenderTexture,RenderTexture, bool, bool> downSampleTexFunc = (CommandBuffer cmd, RenderTexture src, RenderTexture dst, RenderTexture varianceOptional, bool singleChannel, bool isNormalDepth) =>
            {
                var kernel = singleChannel ? ImpostorRendererResources.Kernels.kDownSampleCutout : ImpostorRendererResources.Kernels.kDownSample;

                int2 srcSize = new int2(src.width, src.height);
                int2 dstSize = new int2(dst.width, dst.height);

                int2 kernelSize = srcSize / dstSize;

                int flags = 0;
                flags |= isNormalDepth ? ImpostorRendererResources.DOWNSAMPLE_TARGET_IS_NORMALDEPTH: 0;
                flags |= IsAllocated(varianceOptional) ? ImpostorRendererResources.DOWNSAMPLE_CALCULATE_VARIANCE : 0;
                

                int3 dispatchCutout = (int3)ImpostorRendererResources.GetDispatchArgs(kernel, new uint3((uint)dstSize.x, (uint)dstSize.y, 1));
                cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._KernelSize, kernelSize.x, kernelSize.y);
                cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._TextureResolution, dstSize.x, dstSize.x);
                cmd.SetComputeIntParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._Flags, flags);
                
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, kernel, ImpostorRendererResources.UniformIDs._SourceTexture, src);
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, kernel, ImpostorRendererResources.UniformIDs._TargetTexture, dst);
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, kernel, ImpostorRendererResources.UniformIDs._TargetTexture2, varianceOptional);
                cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, kernel, dispatchCutout.x, dispatchCutout.y, dispatchCutout.z);
            };

            var cmd = CommandBufferPool.Get("DownSampleImpostorFrame");

            
            int2 endResolution = res / (int)math.pow(2, numberOfPasses);
            IImpostorContext.ImpostorFrameResources output = IImpostorContext.ImpostorFrameResources.CreateImpostorFrameData((uint2)endResolution, IsAllocated(source.specSmoothness), source.albedoAlpha.format == RenderTextureFormat.ARGBFloat);
            IImpostorContext.ImpostorFrameResources sd;
            if (produceVarianceTargets)
            {
                sd = IImpostorContext.ImpostorFrameResources.CreateImpostorFrameData((uint2)endResolution, IsAllocated(source.specSmoothness), IsAllocated(source.customOutput), source.albedoAlpha.format == RenderTextureFormat.ARGBFloat);
            }
            else
            {
                sd.albedoAlpha = null;
                sd.normalDepth = null;
                sd.specSmoothness = null;
                sd.scratch = null;
                sd.cutout = null;
                sd.customOutput = null;
            }
                
            
            IImpostorContext.ImpostorFrameResources src = source;
            IImpostorContext.ImpostorFrameResources dst = output;

            downSampleTexFunc(cmd, src.albedoAlpha, dst.albedoAlpha, sd.albedoAlpha, false, false);
            downSampleTexFunc(cmd, src.normalDepth, dst.normalDepth, sd.normalDepth, false, true);
            downSampleTexFunc(cmd, src.cutout, dst.cutout, null, true, false);
            if (IsAllocated(src.specSmoothness))
            {
                downSampleTexFunc(cmd, src.specSmoothness, dst.specSmoothness,sd.specSmoothness, false, false);
            }

            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            downsampledFrameResources = output;
            standardDevOut = sd;
        }


        public static void PostProcessImpostorFrame(in IImpostorContext.ImpostorFrameResources frameResources, int dilateFrameSteps, float alphaSearchRadius, bool alphaToDistanceField)
        {
            var width = frameResources.albedoAlpha.width;
            var height = frameResources.albedoAlpha.height;

            var widthInv = 1.0f / width;
            var heightInv = 1.0f / height;

            int dilateDirectionCount = 8;

            CommandBuffer cmd = CommandBufferPool.Get("ImpostorGenPostProcess");

            int3 dispatchArgsDilate = (int3)ImpostorRendererResources.GetDispatchArgs(ImpostorRendererResources.Kernels.kDilateFrameColorChannels, new uint3((uint)width, (uint)height, 1));
            int3 dispatchArgsCopy = (int3)ImpostorRendererResources.GetDispatchArgs(ImpostorRendererResources.Kernels.kCopyToAtlas, new uint3((uint)width, (uint)height, 1));

            cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._TextureResolution, width, height);
            cmd.SetComputeIntParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._DilateStepCount, (int)dilateFrameSteps);

            //produce cutout
            {
                int3 dispatchCutout = (int3)ImpostorRendererResources.GetDispatchArgs(ImpostorRendererResources.Kernels.kProduceCutout, new uint3((uint)width, (uint)height, 1));
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kProduceCutout, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.albedoAlpha);
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kProduceCutout, ImpostorRendererResources.UniformIDs._TargetTexture, frameResources.cutout);
                cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kProduceCutout, dispatchCutout.x, dispatchCutout.y, dispatchCutout.z);
            }
            
            
            //dilate albedo & alpha
            {
                ClearTexture(cmd, frameResources.scratch, float4.zero);
                //albedo
                for (int i = 0; i < dilateDirectionCount; ++i)
                {
                    float angle = math.PI2 * i / dilateDirectionCount;
                    float2 direction = new float2(math.cos(angle) * widthInv, math.sin(angle) * heightInv);
                    cmd.SetComputeFloatParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._DilateDirection, direction.x, direction.y);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorChannels, ImpostorRendererResources.UniformIDs._SourceCutout, frameResources.cutout);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorChannels, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.albedoAlpha);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorChannels, ImpostorRendererResources.UniformIDs._TargetTexture, frameResources.scratch);
                    cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorChannels, dispatchArgsDilate.x, dispatchArgsDilate.y, dispatchArgsDilate.z);
                }
                
                //alpha
                if(alphaSearchRadius > 0)
                {
                    int2 blurSize = new int2((int)(width * alphaSearchRadius), (int)(height * alphaSearchRadius));
                    cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._KernelSize, blurSize.x, blurSize.y);
                    cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._Flags, alphaToDistanceField ? ImpostorRendererResources.PROCESS_ALPHA_PRODUCE_DISTANCE_FIELD : 0);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kProcessFrameAlpha, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.albedoAlpha);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kProcessFrameAlpha, ImpostorRendererResources.UniformIDs._TargetTexture, frameResources.scratch);
                    cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kProcessFrameAlpha, dispatchArgsDilate.x, dispatchArgsDilate.y, dispatchArgsDilate.z);


                    cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._TextureOffset, 0, 0);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.scratch);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._TargetTexture, frameResources.albedoAlpha);
                    cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, dispatchArgsCopy.x, dispatchArgsCopy.y, dispatchArgsCopy.z);
                }

            }
            
            //dilate normal & depth
            {
                ClearTexture(cmd, frameResources.scratch, float4.zero);
                for (int i = 0; i < dilateDirectionCount; ++i)
                {
                    float angle = math.PI2 * i / dilateDirectionCount;
                    float2 direction = new float2(math.cos(angle) * widthInv, math.sin(angle) * heightInv);
                    cmd.SetComputeFloatParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._DilateDirection, direction.x, direction.y);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorAndAlphaChannels, ImpostorRendererResources.UniformIDs._SourceCutout, frameResources.cutout);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorAndAlphaChannels, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.normalDepth);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorAndAlphaChannels, ImpostorRendererResources.UniformIDs._TargetTexture, frameResources.scratch);
                    cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorAndAlphaChannels, dispatchArgsDilate.x, dispatchArgsDilate.y, dispatchArgsDilate.z);
                }

                cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._TextureOffset, 0, 0);
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.scratch);
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._TargetTexture, frameResources.normalDepth);
                cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, dispatchArgsCopy.x, dispatchArgsCopy.y, dispatchArgsCopy.z);
            }
            
            //dilate spec & smoothness
            if (IsAllocated(frameResources.specSmoothness))
            {
                ClearTexture(cmd, frameResources.scratch, float4.zero);
                for (int i = 0; i < dilateDirectionCount; ++i)
                {
                    float angle = math.PI2 * i / dilateDirectionCount;
                    float2 direction = new float2(math.cos(angle) * widthInv, math.sin(angle) * heightInv);
                    cmd.SetComputeFloatParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._DilateDirection, direction.x, direction.y);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorAndAlphaChannels, ImpostorRendererResources.UniformIDs._SourceCutout, frameResources.cutout);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorAndAlphaChannels, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.specSmoothness);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorAndAlphaChannels, ImpostorRendererResources.UniformIDs._TargetTexture, frameResources.scratch);
                    cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorAndAlphaChannels, dispatchArgsDilate.x, dispatchArgsDilate.y, dispatchArgsDilate.z);
                }
    
                cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._TextureOffset, 0, 0);
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.scratch);
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._TargetTexture, frameResources.specSmoothness);
                cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, dispatchArgsCopy.x, dispatchArgsCopy.y, dispatchArgsCopy.z);
                
            }
            
            //dilate custom target (should have this as optional)
            if (IsAllocated(frameResources.customOutput))
            {
                ClearTexture(cmd, frameResources.scratch, float4.zero);
                for (int i = 0; i < dilateDirectionCount; ++i)
                {
                    float angle = math.PI2 * i / dilateDirectionCount;
                    float2 direction = new float2(math.cos(angle) * widthInv, math.sin(angle) * heightInv);
                    cmd.SetComputeFloatParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._DilateDirection, direction.x, direction.y);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorAndAlphaChannels, ImpostorRendererResources.UniformIDs._SourceCutout, frameResources.cutout);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorAndAlphaChannels, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.customOutput);
                    cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorAndAlphaChannels, ImpostorRendererResources.UniformIDs._TargetTexture, frameResources.scratch);
                    cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kDilateFrameColorAndAlphaChannels, dispatchArgsDilate.x, dispatchArgsDilate.y, dispatchArgsDilate.z);
                }
    
                cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._TextureOffset, 0, 0);
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._SourceTexture, frameResources.scratch);
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, ImpostorRendererResources.UniformIDs._TargetTexture, frameResources.customOutput);
                cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCopyToAtlas, dispatchArgsCopy.x, dispatchArgsCopy.y, dispatchArgsCopy.z);
                
            }

            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
        
        public static void ConvertAlbedoToMonochromatic(in IImpostorContext.ImpostorFrameResources frameResources)
        {
            var width = frameResources.albedoAlpha.width;
            var height = frameResources.albedoAlpha.height;
            CommandBuffer cmd = CommandBufferPool.Get("ConvertAlbedoToMonochromatic");
            GraphicsBuffer minLightnessBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
            
            {
                NativeArray<uint> clearData = new NativeArray<uint>(1, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                clearData[0] = 0;
                minLightnessBuffer.SetData(clearData);
                clearData.Dispose();
            }
            
            
            cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._TextureResolution, width, height);
            {
                int3 dispatchArgs = (int3)ImpostorRendererResources.GetDispatchArgs(ImpostorRendererResources.Kernels.kCalculateMinMaxLightness, new uint3((uint)width, (uint)height, 1));
                cmd.SetComputeBufferParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCalculateMinMaxLightness, ImpostorRendererResources.UniformIDs._LightnessMaxBuffer, minLightnessBuffer);
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCalculateMinMaxLightness, ImpostorRendererResources.UniformIDs._TargetTexture, frameResources.albedoAlpha);
                cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kCalculateMinMaxLightness, dispatchArgs.x, dispatchArgs.y, dispatchArgs.z);
            }
            
            {
                int3 dispatchArgs = (int3)ImpostorRendererResources.GetDispatchArgs(ImpostorRendererResources.Kernels.kConvertAlbedoToLightness, new uint3((uint)width, (uint)height, 1));
                cmd.SetComputeBufferParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kConvertAlbedoToLightness, ImpostorRendererResources.UniformIDs._LightnessMaxBuffer, minLightnessBuffer);
                cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kConvertAlbedoToLightness, ImpostorRendererResources.UniformIDs._TargetTexture, frameResources.albedoAlpha);
                cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kConvertAlbedoToLightness, dispatchArgs.x, dispatchArgs.y, dispatchArgs.z);
            }

            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            minLightnessBuffer.Dispose();
        }
        
        public static void ForceAlphaToCutout(in IImpostorContext.ImpostorFrameResources frameResources)
        {
            var width = frameResources.albedoAlpha.width;
            var height = frameResources.albedoAlpha.height;
            CommandBuffer cmd = CommandBufferPool.Get("ImpostorGenForceAlphaToCutout");
            int3 dispatchArgs = (int3)ImpostorRendererResources.GetDispatchArgs(ImpostorRendererResources.Kernels.kProduceCutoutToAlphaChannel, new uint3((uint)width, (uint)height, 1));
            cmd.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._TextureResolution, width, height);
            cmd.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kProduceCutoutToAlphaChannel, ImpostorRendererResources.UniformIDs._TargetTexture, frameResources.albedoAlpha);
            cmd.DispatchCompute(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.Kernels.kProduceCutoutToAlphaChannel, dispatchArgs.x, dispatchArgs.y, dispatchArgs.z);
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
        

        public static bool IsAllocated(RenderTexture rt)
        {
            return rt != null && rt.IsCreated();
        }
        
        public static RenderTextureDescriptor GetRenderTextureDesc(uint width, uint height, RenderTextureFormat format)
        {
            RenderTextureDescriptor desc = new RenderTextureDescriptor((int)width, (int)height, format);
            desc.enableRandomWrite = true;
            return desc;
        }
        
        public static bool GetCompactImpostorMeshData(out Vector3[] positions, out Vector3[] normals, out Vector2[] uvs, out int[] indices, int numberOfPlanes,  uint2 resolution, NativeArray<int> mask)
        {
            numberOfPlanes = math.max(4, numberOfPlanes);
            
            NativeArray<float2> planeDirection = new NativeArray<float2>(numberOfPlanes, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            NativeArray<float> planeOffsets = new NativeArray<float>(numberOfPlanes, Allocator.TempJob);
            for (int i = 0; i < numberOfPlanes; ++i)
            {
                float angle = math.PI2 * i / numberOfPlanes;
                float2 direction = new float2(math.cos(angle), math.sin(angle));
                planeDirection[i] = direction;
            }
            JobHandle jobHandle = default;
            unsafe
            {
                

                /*Texture2D tex = new Texture2D((int)frameResolution.x, (int)frameResolution.y, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None);
                for (int i = 0; i < mask.Length; ++i)
                {
                    int x = i % (int)frameResolution.x;
                    int y = i / (int)frameResolution.x;
                    tex.SetPixel(x, y, mask[i] > 0 ? Color.white :  Color.black);
                }
                byte[] bytes = tex.EncodeToPNG();
                System.IO.File.WriteAllBytes($"asdf.png", bytes);*/
                
                jobHandle = new CalculatePlanesOffsetsForFrame()
                {
                    AccumulatedMask = mask,
                    FrameWidth = (int)resolution.x,
                    FrameHeight = (int)resolution.y,
                    PlaneDirections = planeDirection,
                    PlaneOffsets = planeOffsets
                }.Schedule(numberOfPlanes, 1, jobHandle);
            }
            
            jobHandle.Complete();

            bool validPlaneOffsets = true;
            for (int i = 0; i < planeOffsets.Length; ++i)
            {
                if (planeOffsets[i] == float.MaxValue)
                {
                    validPlaneOffsets = false;
                }
            }

            if (validPlaneOffsets)
            {
                positions = new Vector3[numberOfPlanes + 1];
                positions[0] = Vector3.zero;

                for (int i = 0; i < numberOfPlanes; ++i)
                {
                    int ind0 = i;
                    int ind1 = (i + 1) % numberOfPlanes;
                
                    float2 n0 = planeDirection[ind0];
                    float2 n1 = planeDirection[ind1];

                    float a0 = planeOffsets[ind0];
                    float a1 = planeOffsets[ind1];

                    float2 intersection = GetIntersection(n0, a0, n1, a1);

                    positions[i + 1] = new Vector3(intersection.x, intersection.y, 0);
                }

                uvs = new Vector2[numberOfPlanes + 1];
                for (int i = 0; i < positions.Length; ++i)
                {
                    float2 p = (Vector2)positions[i];
                    float2 uv = p + 0.5f;
                    uvs[i] = uv;
                }
            
                Vector3 normal = new float3(0.0f, 0.0f, 1.0f);
                normals = new Vector3[numberOfPlanes + 1];
                Array.Fill(normals, normal);

                indices = new int[numberOfPlanes * 3];
                for (int i = 0; i < numberOfPlanes; ++i)
                {
                    int ind = i * 3;
                    indices[ind] = 1 + i;
                    indices[ind + 1] = 0;
                    indices[ind + 2] = 1 + (i + 1) % numberOfPlanes;
                }
            }
            else
            {
                positions = null;
                uvs = null;
                normals = null;
                indices = null;
            }

            planeDirection.Dispose();
            planeOffsets.Dispose();

            return validPlaneOffsets;
        }

        private static float2 GetIntersection(float2 n0, float a0, float2 n1, float a1)
        {
            float3 l0 = new float3(n0.x, n0.y, a0);
            float3 l1 = new float3(n1.x, n1.y, a1);

            float3 p = math.cross(l0, l1);
            if (p.z == 0)
            {
                return 0; //no intersection
            }

            return new float2(p.x / p.z, p.y / p.z);
        }

        //For now a naively traverse the whole frame for every plane to find the offset. TODO: optimize and start from the direction relative to the plane and traverse from there to terminate after first valid sample found
        [BurstCompile]
        private struct CalculatePlanesOffsetsForFrame : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<int> AccumulatedMask;
            public int FrameWidth;
            public int FrameHeight;

            [ReadOnly]
            public NativeArray<float2> PlaneDirections;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<float> PlaneOffsets;

            public void Execute(int index)
            {
                float2 planeDir = PlaneDirections[index];
                float offset = float.MaxValue;
                float frameWidthInv = 1.0f / FrameWidth;
                float frameHeightInv = 1.0f / FrameHeight;
                for (int k = 0; k < FrameHeight; ++k)
                {
                    for (int i = 0; i < FrameWidth; ++i)
                    {
                        int ind = k * FrameWidth + i;
                        int mask = AccumulatedMask[ind];
                        bool validSample = mask > 0;
                        if (validSample)
                        {
                            float2 p = new float2(i * frameWidthInv, k * frameHeightInv);
                            p -= 0.5f; //[-0.5,0.5]
                            float d = p.x * planeDir.x + p.y * planeDir.y;
                            d -= math.max(frameWidthInv, frameHeightInv);//pixel margin
                            if (d < offset)
                            {
                                offset = d;
                            }
                        }
                    }
                }

                PlaneOffsets[index] = offset;
            }
        }
        [BurstCompile]
        private unsafe struct GenerateAccumulatedFrameMask : IJobParallelFor
        {
            [NativeDisableUnsafePtrRestriction]
            public Color* AtlasColors;
            public int2 FrameResolution;
            public int2 FrameCounts;

            [NativeDisableUnsafePtrRestriction]
            public NativeArray<int> AccumulatedMask;
            
            public void Execute(int index)
            {
                int atlasResX = FrameResolution.x * FrameCounts.x;
                int atlasResY = FrameResolution.y * FrameCounts.y;
                
                int xInFrame = index % FrameResolution.x;
                int yInFrame = index / FrameResolution.y;

                int maskValue = 0;
                
                for (int i = 0; i < FrameCounts.y; ++i)
                {
                    for (int k = 0; k < FrameCounts.x; ++k)
                    {
                        var x = k * FrameResolution.x + xInFrame;
                        var y = i * FrameResolution.y + yInFrame;

                        var atlasIndex = y * atlasResX + x;

                        Color col = AtlasColors[atlasIndex];
                        if (col.r > 0)
                        {
                            maskValue = 1;
                        }
                    }
                }

                AccumulatedMask[index] = maskValue;
            }
        }
    }
}
