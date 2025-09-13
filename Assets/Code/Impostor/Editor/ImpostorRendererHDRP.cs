using System;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering.HighDefinition.Attributes;
using RenderPass = TimeGhost.IImpostorRenderer.RenderPass;
namespace TimeGhost
{
    public class ImpostorContextHdrp : IImpostorContext
    {

        private PreviewRenderUtility m_PreviewRenderer;
        private Camera m_Camera;
        private HDAdditionalCameraData m_HDCameraData;

        private RTHandle m_ColorBufferHandle;
        private RTHandle m_DepthBufferHandle;
        private int2 m_LastAllocatedSize;

        public void Init()
        {
            ImpostorRendererResources.StaticInitialize();

            m_PreviewRenderer = new PreviewRenderUtility();
            m_PreviewRenderer.camera.clearFlags = CameraClearFlags.SolidColor;
            m_PreviewRenderer.camera.backgroundColor = Color.black;
            m_PreviewRenderer.camera.nearClipPlane = 0.001f;
            m_PreviewRenderer.camera.farClipPlane = 50.0f;
            m_PreviewRenderer.camera.fieldOfView = 50.0f;
            m_PreviewRenderer.camera.transform.position = Vector3.zero;
            m_PreviewRenderer.camera.transform.LookAt(Vector3.forward, Vector3.up);

            for (int i = 0; i != m_PreviewRenderer.lights.Length; i++)
            {
                m_PreviewRenderer.lights[i].enabled = false;
            }

            //setup AOV
            m_Camera = m_PreviewRenderer.camera;
            if (!m_Camera.TryGetComponent(out m_HDCameraData))
            {
                m_HDCameraData = m_Camera.gameObject.AddComponent<HDAdditionalCameraData>();
            }

            m_LastAllocatedSize = new int2(0, 0);
        }

        public void Deinit()
        {
            m_PreviewRenderer.Cleanup();
        }

        float4 GetZBufferParams()
        {
            var projMatrix = m_Camera.projectionMatrix;
            var n = m_Camera.nearClipPlane;
            var f = m_Camera.farClipPlane;
            float scale = projMatrix[2, 3] / (f * n) * (f - n);
            bool reverseZ = scale > 0;

            if (reverseZ)
            {
                return new float4(-1 + f / n, 1, -1 / f + 1 / n, 1 / f);
            }
            else
            {
                return new float4(1 - f / n, f / n, 1 / f - 1 / n, 1 / n);
            }
        }

        private void PrepareAOVResources(int resWidth, int resHeight)
        {
            if (m_LastAllocatedSize.x != resWidth || m_LastAllocatedSize.y != resHeight)
            {
                m_ColorBufferHandle?.Release();
                m_DepthBufferHandle?.Release();


                m_ColorBufferHandle = RTHandles.Alloc(resWidth, resHeight);
                m_DepthBufferHandle = RTHandles.Alloc(resWidth, resHeight, 1, DepthBits.None, GraphicsFormat.R32_SFloat);

                m_LastAllocatedSize.x = resWidth;
                m_LastAllocatedSize.y = resHeight;
            }
        }
        private void SetupAOV(RenderPass pass, IImpostorContext.ImpostorFrameResources frameResources)
        {
            var aovRequestBuilder = new AOVRequestBuilder();

            switch (pass)
            {
                case RenderPass.Albedo:
                {
                    var aovRequest = AOVRequest.NewDefault();
                    aovRequest.SetLightFilter(DebugLightFilterMode.None);
                    aovRequest.SetFullscreenOutput(MaterialSharedProperty.Albedo);
                    AOVBuffers[] aovBuffers = new[] { AOVBuffers.Color };
                    aovRequestBuilder.Add(aovRequest,
                        bufferId =>
                        {
                            switch (bufferId)
                            {
                                case AOVBuffers.Color:
                                    return m_ColorBufferHandle;
                                default:
                                    return null;
                            }
                        },
                        null,
                        aovBuffers,
                        null,
                        null,
                        (cmd, textures, customPassTextures, properties) =>
                        {
                            //extract frame data from AOV textures
                            if (textures.Count > 0)
                            {
                                ExtractAlbedo(cmd, textures[0], frameResources.albedoAlpha);
                            }

                        });
                }
                    break;
                case RenderPass.Normal:
                {
                    var aovRequest = AOVRequest.NewDefault();
                    aovRequest.SetLightFilter(DebugLightFilterMode.None);
                    aovRequest.SetFullscreenOutput(MaterialSharedProperty.Normal);
                    AOVBuffers[] aovBuffers = new[] { AOVBuffers.Color };
                    aovRequestBuilder.Add(aovRequest,
                        bufferId =>
                        {
                            switch (bufferId)
                            {
                                case AOVBuffers.Color:
                                    return m_ColorBufferHandle;
                                default:
                                    return null;
                            }
                        },
                        null,
                        aovBuffers,
                        null,
                        null,
                        (cmd, textures, customPassTextures, properties) =>
                        {
                            //extract frame data from AOV textures
                            if (textures.Count > 0)
                            {
                                ExtractNormals(cmd, textures[0], frameResources.normalDepth);
                            }

                        });
                }
                    break;
                case RenderPass.Alpha:
                {
                    var aovRequest = AOVRequest.NewDefault();
                    aovRequest.SetLightFilter(DebugLightFilterMode.None);
                    aovRequest.SetFullscreenOutput(MaterialSharedProperty.Alpha);
                    AOVBuffers[] aovBuffers = new[] { AOVBuffers.Color };
                    aovRequestBuilder.Add(aovRequest,
                        bufferId =>
                        {
                            switch (bufferId)
                            {
                                case AOVBuffers.Color:
                                    return m_ColorBufferHandle;
                                default:
                                    return null;
                            }
                        },
                        null,
                        aovBuffers,
                        null,
                        null,
                        (cmd, textures, customPassTextures, properties) =>
                        {
                            //extract frame data from AOV textures
                            if (textures.Count > 0)
                            {
                                ExtractAlpha(cmd, textures[0], frameResources.albedoAlpha);
                            }

                        });
                }
                    break;
                case RenderPass.Depth:
                {
                    var aovRequest = AOVRequest.NewDefault();
                    aovRequest.SetLightFilter(DebugLightFilterMode.None);
                    aovRequest.SetFullscreenOutput(MaterialSharedProperty.None);
                    AOVBuffers[] aovBuffers = new[] { AOVBuffers.DepthStencil };
                    aovRequestBuilder.Add(aovRequest,
                        bufferId =>
                        {
                            switch (bufferId)
                            {
                                case AOVBuffers.DepthStencil:
                                    return m_DepthBufferHandle;
                                default:
                                    return null;
                            }
                        },
                        null,
                        aovBuffers,
                        null,
                        null,
                        (cmd, textures, customPassTextures, properties) =>
                        {
                            //extract frame data from AOV textures
                            if (textures.Count > 0)
                            {
                                ExtractDepth(cmd, textures[0], frameResources.normalDepth);
                            }

                        });
                }
                    break;
                case RenderPass.Smoothness:
                {
                    var aovRequest = AOVRequest.NewDefault();
                    aovRequest.SetLightFilter(DebugLightFilterMode.None);
                    aovRequest.SetFullscreenOutput(MaterialSharedProperty.Smoothness);
                    AOVBuffers[] aovBuffers = new[] { AOVBuffers.Color };
                    aovRequestBuilder.Add(aovRequest,
                        bufferId =>
                        {
                            switch (bufferId)
                            {
                                case AOVBuffers.Color:
                                    return m_ColorBufferHandle;
                                default:
                                    return null;
                            }
                        },
                        null,
                        aovBuffers,
                        null,
                        null,
                        (cmd, textures, customPassTextures, properties) =>
                        {
                            //extract frame data from AOV textures
                            if (textures.Count > 0)
                            {
                                ExtractSmoothness(cmd, textures[0], frameResources.specSmoothness);
                            }

                        });
                }
                    break;
                case RenderPass.Specular:
                {
                    var aovRequest = AOVRequest.NewDefault();
                    aovRequest.SetLightFilter(DebugLightFilterMode.None);
                    aovRequest.SetFullscreenOutput(MaterialSharedProperty.Specular);
                    AOVBuffers[] aovBuffers = new[] { AOVBuffers.Color };
                    aovRequestBuilder.Add(aovRequest,
                        bufferId =>
                        {
                            switch (bufferId)
                            {
                                case AOVBuffers.Color:
                                    return m_ColorBufferHandle;
                                default:
                                    return null;
                            }
                        },
                        null,
                        aovBuffers,
                        null,
                        null,
                        (cmd, textures, customPassTextures, properties) =>
                        {
                            //extract frame data from AOV textures
                            if (textures.Count > 0)
                            {
                                ExtractSpecular(cmd, textures[0], frameResources.specSmoothness);
                            }

                        });
                }
                    break;
                case RenderPass.Custom:
                {
                    var aovRequest = AOVRequest.NewDefault();
                    aovRequest.SetLightFilter(DebugLightFilterMode.None);
                    aovRequest.SetFullscreenOutput(MaterialSharedProperty.Albedo);
                    AOVBuffers[] aovBuffers = new[] { AOVBuffers.Color };
                    aovRequestBuilder.Add(aovRequest,
                        bufferId =>
                        {
                            switch (bufferId)
                            {
                                case AOVBuffers.Color:
                                    return m_ColorBufferHandle;
                                default:
                                    return null;
                            }
                        },
                        null,
                        aovBuffers,
                        null,
                        null,
                        (cmd, textures, customPassTextures, properties) =>
                        {
                            //extract frame data from AOV textures
                            if (textures.Count > 0)
                            {
                                ExtractCustomOutput(cmd, textures[0], frameResources.customOutput);
                            }

                        });
                }
                    
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(pass), pass, null);
            }

            var aovRequestDataCollection = aovRequestBuilder.Build();
            m_HDCameraData.SetAOVRequests(aovRequestDataCollection);
        }

        void SetupCamera(in IImpostorContext.ImpostorFrameSetup frameSetup)
        {
            m_PreviewRenderer.camera.orthographic = frameSetup.settings.useOrthoGraphicProj;
            m_PreviewRenderer.camera.orthographicSize = frameSetup.settings.fovYOrOrtoWidth;
            m_PreviewRenderer.camera.nearClipPlane = frameSetup.settings.nearPlane;
            m_PreviewRenderer.camera.farClipPlane = frameSetup.settings.farPlane;
            m_PreviewRenderer.camera.fieldOfView = frameSetup.settings.fovYOrOrtoWidth * math.TODEGREES;
            m_PreviewRenderer.camera.aspect = frameSetup.settings.fovXOrOrthoHeight / frameSetup.settings.fovYOrOrtoWidth;
            m_PreviewRenderer.camera.transform.position = frameSetup.cameraPosition;
            m_PreviewRenderer.camera.transform.LookAt(frameSetup.cameraPosition + frameSetup.cameraDirection, Vector3.up);
        }

        public void Render(in IImpostorContext.ImpostorFrameSetup frameSetup, in IImpostorContext.ImpostorFrameResources frameResources, in IImpostorRenderer renderer)
        {
            int width = frameResources.albedoAlpha.width;
            int height = frameResources.albedoAlpha.height;

            SetupCamera(frameSetup);
            PrepareAOVResources(width, height);
            
            foreach (var v in Enum.GetValues(typeof(RenderPass)))
            {
                RenderPass pass = (RenderPass)v;
                bool isSpecOrSmoothnessPass = pass == RenderPass.Smoothness || pass == RenderPass.Specular;
                bool hasSpecSmoothnessResources = ImpostorGeneratorUtils.IsAllocated(frameResources.specSmoothness);
                bool hasCustomTexture = ImpostorGeneratorUtils.IsAllocated(frameResources.customOutput);
                if(isSpecOrSmoothnessPass && !hasSpecSmoothnessResources) continue;
                if((pass == RenderPass.Custom) && !hasCustomTexture) continue;
                
                SetupAOV(pass, frameResources);

                Rect rect = new Rect(0, 0, width, height);
                m_PreviewRenderer.BeginPreview(rect, GUIStyle.none);
                
                renderer.Render(m_PreviewRenderer, pass);

                m_PreviewRenderer.Render(true, false);
                m_PreviewRenderer.EndPreview();
            }
            
        }

        private void ExtractAlbedo(CommandBuffer cmdBuffer, RTHandle source, RenderTexture target)
        {
            ExtractFrameData(cmdBuffer, ImpostorRendererResources.Kernels.kExtractColor, source, target);
        }

        private void ExtractNormals(CommandBuffer cmdBuffer, RTHandle source, RenderTexture target)
        {
            ExtractFrameData(cmdBuffer, ImpostorRendererResources.Kernels.kExtractColor, source, target);
        }

        private void ExtractAlpha(CommandBuffer cmdBuffer, RTHandle source, RenderTexture target)
        {
            ExtractFrameData(cmdBuffer, ImpostorRendererResources.Kernels.kExtractAlpha, source, target);
        }

        private void ExtractDepth(CommandBuffer cmdBuffer, RTHandle source, RenderTexture target)
        {
            ExtractFrameData(cmdBuffer, ImpostorRendererResources.Kernels.kExtractDepth, source, target);
        }
        
        private void ExtractSmoothness(CommandBuffer cmdBuffer, RTHandle source, RenderTexture target)
        {
            ExtractFrameData(cmdBuffer, ImpostorRendererResources.Kernels.kExtractAlpha, source, target);
        }
        
        private void ExtractSpecular(CommandBuffer cmdBuffer, RTHandle source, RenderTexture target)
        {
            ExtractFrameData(cmdBuffer, ImpostorRendererResources.Kernels.kExtractColor, source, target);
        }
        
        private void ExtractCustomOutput(CommandBuffer cmdBuffer, RTHandle source, RenderTexture target)
        {
            ExtractFrameData(cmdBuffer, ImpostorRendererResources.Kernels.kExtractColor, source, target);
        }

        private void ExtractFrameData(CommandBuffer cmdBuffer, int kernelIndex, RTHandle source, RenderTexture target)
        {
            cmdBuffer.SetComputeIntParams(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._TextureResolution, target.width, target.height);
            cmdBuffer.SetComputeVectorParam(ImpostorRendererResources.s_ComputeShader, ImpostorRendererResources.UniformIDs._ZBufferParams, GetZBufferParams());

            //albedo & alpha
            {
                cmdBuffer.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, kernelIndex, ImpostorRendererResources.UniformIDs._SourceTexture, source);
                cmdBuffer.SetComputeTextureParam(ImpostorRendererResources.s_ComputeShader, kernelIndex, ImpostorRendererResources.UniformIDs._TargetTexture, target);

                int3 dispatchArgs = (int3)ImpostorRendererResources.GetDispatchArgs(kernelIndex, new uint3((uint)target.width, (uint)target.height, 1));
                cmdBuffer.DispatchCompute(ImpostorRendererResources.s_ComputeShader, kernelIndex, dispatchArgs.x, dispatchArgs.y, dispatchArgs.z);

            }
        }
    }
}