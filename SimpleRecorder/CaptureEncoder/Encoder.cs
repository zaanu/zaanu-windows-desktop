// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace CaptureEncoder
{
    public sealed class Encoder : IDisposable
    {
        public Encoder(Microsoft.Graphics.Canvas.CanvasDevice canvasDevice, GraphicsCaptureItem item, Windows.UI.Composition.CompositionDrawingSurface surface)
        {
            //_device = device;
            _canvasDevice = canvasDevice;
            _captureItem = item;
            _surface = surface;
            
            _isRecording = false;


            CreateMediaObjects();
        }

        public IAsyncAction EncodeAsync(IRandomAccessStream stream, uint width, uint height, uint bitrateInBps, uint frameRate)
        {
            return EncodeInternalAsync(stream, width, height, bitrateInBps, frameRate).AsAsyncAction();
        }

        private async Task EncodeInternalAsync(IRandomAccessStream stream, uint width, uint height, uint bitrateInBps, uint frameRate)
        {
            if (!_isRecording)
            {
                _isRecording = true;

                _frameGenerator = new CaptureFrameWait(
                    _canvasDevice,
                    _captureItem,
                    _surface,
                    _captureItem.Size);

                using (_frameGenerator)
                {
                    
                    var encodingProfile = new MediaEncodingProfile();
                    //encodingProfile.Container.Subtype = "MPEG4";
                    encodingProfile.Container.Subtype = "WEBM";
                    encodingProfile.Video.Subtype = "H264";
                    encodingProfile.Video.Width = width;
                    encodingProfile.Video.Height = height;
                    encodingProfile.Video.Bitrate = bitrateInBps;
                    encodingProfile.Video.FrameRate.Numerator = frameRate;
                    encodingProfile.Video.FrameRate.Denominator = 1;
                    encodingProfile.Video.PixelAspectRatio.Numerator = 1;
                    encodingProfile.Video.PixelAspectRatio.Denominator = 1;
                    var transcode = await _transcoder.PrepareMediaStreamSourceTranscodeAsync(_mediaStreamSource, stream, encodingProfile);

                    //await Task.Delay(-1);
                    await transcode.TranscodeAsync();
                }
            }
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;

            if (!_isRecording)
            {
                DisposeInternal();
            }

            _isRecording = false;            
        }

        private void DisposeInternal()
        {
            _frameGenerator.Dispose();
        }

        private void CreateMediaObjects()
        {
            // Create our encoding profile based on the size of the item
            int width = _captureItem.Size.Width;
            int height = _captureItem.Size.Height;

            // Describe our input: uncompressed BGRA8 buffers
            var videoProperties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
            _videoDescriptor = new VideoStreamDescriptor(videoProperties);

            // Create our MediaStreamSource
            _mediaStreamSource = new MediaStreamSource(_videoDescriptor);
            _mediaStreamSource.BufferTime = TimeSpan.FromSeconds(0);
            _mediaStreamSource.Starting += OnMediaStreamSourceStarting;
            _mediaStreamSource.SampleRequested += OnMediaStreamSourceSampleRequested;

            // Create our transcoder
            _transcoder = new MediaTranscoder();
            _transcoder.HardwareAccelerationEnabled = true;
        }

        private void OnMediaStreamSourceSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            if (_isRecording && !_closed)
            {
                try
                {
                    using (var frame = _frameGenerator.WaitForNewFrame())
                    {
                        if (frame == null)
                        {
                            args.Request.Sample = null;
                            DisposeInternal();
                            return;
                        }

                        var timeStamp = frame.SystemRelativeTime;

                        var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, timeStamp);
                        args.Request.Sample = sample;                       
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.Message);
                    Debug.WriteLine(e.StackTrace);
                    Debug.WriteLine(e);
                    args.Request.Sample = null;
                    DisposeInternal();
                }
            }
            else
            {
                args.Request.Sample = null;
                DisposeInternal();
            }
        }

        private void OnMediaStreamSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            using (var frame = _frameGenerator.WaitForNewFrame())
            {
                args.Request.SetActualStartPosition(frame.SystemRelativeTime);
            }
        }

        //private IDirect3DDevice _device;
        private Microsoft.Graphics.Canvas.CanvasDevice _canvasDevice;

        private GraphicsCaptureItem _captureItem;

        private Windows.UI.Composition.CompositionDrawingSurface _surface;
        private CaptureFrameWait _frameGenerator;

        private VideoStreamDescriptor _videoDescriptor;
        private MediaStreamSource _mediaStreamSource;
        private MediaTranscoder _transcoder;
        private bool _isRecording;
        private bool _closed = false;
    }
}
