// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Threading;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace CaptureEncoder
{
    public sealed class SurfaceWithInfo : IDisposable
    {
        public IDirect3DSurface Surface { get; internal set; }
        public TimeSpan SystemRelativeTime { get; internal set; }

        public void Dispose()
        {
            Surface?.Dispose();
            Surface = null;
        }
    }

    class MultithreadLock : IDisposable
    {
        public MultithreadLock(SharpDX.Direct3D11.Multithread multithread)
        {
            _multithread = multithread;
            _multithread?.Enter();
        }

        public void Dispose()
        {
            _multithread?.Leave();
            _multithread = null;
        }

        private SharpDX.Direct3D11.Multithread _multithread;
    }

    public sealed class CaptureFrameWait : IDisposable
    {
        public CaptureFrameWait(
            Microsoft.Graphics.Canvas.CanvasDevice canvasDevice,
            GraphicsCaptureItem item,
            Windows.UI.Composition.CompositionDrawingSurface surface,
            SizeInt32 size)
        {
            // _device = device;
            _canvasDevice = canvasDevice;
            _d3dDevice = Direct3D11Helpers.CreateSharpDXDevice(_canvasDevice);
            _multithread = _d3dDevice.QueryInterface<SharpDX.Direct3D11.Multithread>();
            _multithread.SetMultithreadProtected(true);
            _item = item;
            _surface = surface;
            _frameEvent = new ManualResetEvent(false);
            _closedEvent = new ManualResetEvent(false);
            _events = new[] { _closedEvent, _frameEvent };

            InitializeBlankTexture(size);
            InitializeCapture(size);

            _frameCounter = 0;
        }

        private void InitializeCapture(SizeInt32 size)
        {
            _item.Closed += OnClosed;
            //_framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _framePool = Direct3D11CaptureFramePool.Create(
                _canvasDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                size);
            _framePool.FrameArrived += OnFrameArrived;
            _session = _framePool.CreateCaptureSession(_item);
            _session.StartCapture();
        }

        private void InitializeBlankTexture(SizeInt32 size)
        {
            var description = new SharpDX.Direct3D11.Texture2DDescription
            {
                Width = size.Width,
                Height = size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SharpDX.DXGI.SampleDescription()
                {
                    Count = 1,
                    Quality = 0
                },
                Usage = SharpDX.Direct3D11.ResourceUsage.Default,
                BindFlags = SharpDX.Direct3D11.BindFlags.ShaderResource | SharpDX.Direct3D11.BindFlags.RenderTarget,
                CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.None,
                OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None
            };
            _blankTexture = new SharpDX.Direct3D11.Texture2D(_d3dDevice, description);

            using (var renderTargetView = new SharpDX.Direct3D11.RenderTargetView(_d3dDevice, _blankTexture))
            {
                _d3dDevice.ImmediateContext.ClearRenderTargetView(renderTargetView, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 1));
            }
        }

        private void SetResult(Direct3D11CaptureFrame frame)
        {
            _currentFrame = frame;
            //System.Diagnostics.Debug.WriteLine("_currentFrame is set");
            _frameEvent.Set();
        }

        private void Stop()
        {
            _closedEvent.Set();
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            _frameCounter++;
            System.Diagnostics.Debug.WriteLine("Frame Arrived Here 2: " + _frameCounter);
            Direct3D11CaptureFrame frame = sender.TryGetNextFrame();
            DisplayFrame(frame);
            SetResult(frame);
        }

        private void DisplayFrame(Direct3D11CaptureFrame frame)
        {
            // Resize and device-lost leverage the same function on the
            // Direct3D11CaptureFramePool. Refactoring it this way avoids
            // throwing in the catch block below (device creation could always
            // fail) along with ensuring that resize completes successfully and
            // isn’t vulnerable to device-lost.
            bool needsReset = false;
            bool recreateDevice = false;

            if ((frame.ContentSize.Width != _lastSize.Width) ||
                (frame.ContentSize.Height != _lastSize.Height))
            {
                needsReset = true;
                _lastSize = frame.ContentSize;
            }

            try
            {
                // Take the D3D11 surface and draw it into a  
                // Composition surface.

                // Convert our D3D11 surface into a Win2D object.
                Microsoft.Graphics.Canvas.CanvasBitmap canvasBitmap = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromDirect3D11Surface(_canvasDevice, frame.Surface);

                //_currentFrame = canvasBitmap;

                // Helper that handles the drawing for us.
                FillSurfaceWithBitmap(canvasBitmap);
            }

            // This is the device-lost convention for Win2D.
            catch (Exception e) when (_canvasDevice.IsDeviceLost(e.HResult))
            {
                System.Diagnostics.Debug.WriteLine("Canvas Device is Lost!!");
                // We lost our graphics device. Recreate it and reset
                // our Direct3D11CaptureFramePool.  
                needsReset = true;
                recreateDevice = true;
            }

            if (needsReset)
            {
                ResetFramePool(frame.ContentSize, recreateDevice);
            }
        }

        private void FillSurfaceWithBitmap(Microsoft.Graphics.Canvas.CanvasBitmap canvasBitmap)
        {
            Microsoft.Graphics.Canvas.UI.Composition.CanvasComposition.Resize(_surface, canvasBitmap.Size);

            using (var session = Microsoft.Graphics.Canvas.UI.Composition.CanvasComposition.CreateDrawingSession(_surface))
            {
                session.Clear(Windows.UI.Colors.Transparent);
                session.DrawImage(canvasBitmap);
            }
        }

        private void ResetFramePool(SizeInt32 size, bool recreateDevice)
        {
            do
            {
                try
                {
                    if (recreateDevice)
                    {
                        _canvasDevice = new Microsoft.Graphics.Canvas.CanvasDevice();
                    }

                    _framePool.Recreate(
                        _canvasDevice,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        2,
                        size);
                }
                // This is the device-lost convention for Win2D.
                catch (Exception e) when (_canvasDevice.IsDeviceLost(e.HResult))
                {
                    _canvasDevice = null;
                    recreateDevice = true;
                }
            } while (_canvasDevice == null);
        }

        private void OnClosed(GraphicsCaptureItem sender, object args)
        {
            System.Diagnostics.Debug.WriteLine("Closing capture item");
            Stop();
        }

        private void Cleanup()
        {
            _framePool?.Dispose();
            _session?.Dispose();
            if (_item != null)
            {
                _item.Closed -= OnClosed;
            }
            _item = null;
            //_device = null;
            _canvasDevice = null;
            _d3dDevice = null;
            _blankTexture?.Dispose();
            _blankTexture = null;
            _currentFrame?.Dispose();
        }

        public SurfaceWithInfo WaitForNewFrame()
        {
            // Let's get a fresh one.
            _currentFrame?.Dispose();
            _frameEvent.Reset();

            var signaledEvent = _events[WaitHandle.WaitAny(_events)];
            if (signaledEvent == _closedEvent)
            {
                Cleanup();
                return null;
            }

            var result = new SurfaceWithInfo();
            //System.Diagnostics.Debug.WriteLine("Using _currentFrame");
            result.SystemRelativeTime = _currentFrame.SystemRelativeTime;
            //System.Diagnostics.Debug.WriteLine("Done using _currentFrame");
            //result.Surface = _currentFrame.Surface; return result;
            using (var multithreadLock = new MultithreadLock(_multithread))
            using (var sourceTexture = Direct3D11Helpers.CreateSharpDXTexture2D(_currentFrame.Surface))
            {
                var description = sourceTexture.Description;
                description.Usage = SharpDX.Direct3D11.ResourceUsage.Default;
                description.BindFlags = SharpDX.Direct3D11.BindFlags.ShaderResource | SharpDX.Direct3D11.BindFlags.RenderTarget;
                description.CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.None;
                description.OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None;

                using (var copyTexture = new SharpDX.Direct3D11.Texture2D(_d3dDevice, description))
                {
                    var width = Math.Clamp(_currentFrame.ContentSize.Width, 0, _currentFrame.Surface.Description.Width);
                    var height = Math.Clamp(_currentFrame.ContentSize.Height, 0, _currentFrame.Surface.Description.Height);

                    var region = new SharpDX.Direct3D11.ResourceRegion(0, 0, 0, width, height, 1);

                    _d3dDevice.ImmediateContext.CopyResource(_blankTexture, copyTexture);
                    _d3dDevice.ImmediateContext.CopySubresourceRegion(sourceTexture, 0, region, copyTexture, 0);
                    result.Surface = Direct3D11Helpers.CreateDirect3DSurfaceFromSharpDXTexture(copyTexture);
                }
            }

            return result;
        }

        public void Dispose()
        {
            Stop();
            Cleanup();
        }

        // private IDirect3DDevice _device;
        private Microsoft.Graphics.Canvas.CanvasDevice _canvasDevice;
        private SharpDX.Direct3D11.Device _d3dDevice;
        private SharpDX.Direct3D11.Multithread _multithread;
        private SharpDX.Direct3D11.Texture2D _blankTexture;

        private ManualResetEvent[] _events;
        private ManualResetEvent _frameEvent;
        private ManualResetEvent _closedEvent;
        private Direct3D11CaptureFrame _currentFrame;

        private GraphicsCaptureItem _item;
        private GraphicsCaptureSession _session;
        private Direct3D11CaptureFramePool _framePool;
        private Windows.UI.Composition.CompositionDrawingSurface _surface;
        private SizeInt32 _lastSize;

        private Int32 _frameCounter;
    }
}
