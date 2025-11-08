using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SharpAvi;
using SharpAvi.Output;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ScreenRecorder
{
    public class ScreenRecorder
    {
        private AviWriter writer;
        private IAviVideoStream videoStream;
        private IAviAudioStream audioStream;
        private Thread screenThread;
        private bool isRecording;
        private int frameRate;
        private string filePath;

        private WasapiLoopbackCapture loopbackCapture;
        private Stopwatch stopwatch;
        private long lastWriteMs;

        public ScreenRecorder(string filePath, int fps)
        {
            this.filePath = filePath;
            this.frameRate = fps;
        }

        public void StartRecording()
        {
            var bounds = Screen.PrimaryScreen.Bounds;

            writer = new AviWriter(filePath)
            {
                FramesPerSecond = frameRate,
                EmitIndex1 = true
            };

            // 비디오 스트림 추가
            videoStream = writer.AddVideoStream();
            videoStream.Width = bounds.Width;
            videoStream.Height = bounds.Height;
            videoStream.Codec = 0;
            videoStream.BitsPerPixel = BitsPerPixel.Bpp32;

            // 오디오 스트림 추가 (시스템 사운드)
            audioStream = writer.AddAudioStream(
                channelCount: 2,
                samplesPerSecond: 44100,
                bitsPerSample: 16);
            audioStream.Name = "SystemAudio";

            // NAudio Loopback 캡처 (스피커 출력)
            stopwatch = Stopwatch.StartNew();
            lastWriteMs = 0;

            loopbackCapture = new WasapiLoopbackCapture();
            loopbackCapture.DataAvailable += (s, e) =>
            {
                if (!isRecording) return;

                // 경과 시간 계산
                long currentMs = stopwatch.ElapsedMilliseconds;
                long elapsedMs = currentMs - lastWriteMs;
                lastWriteMs = currentMs;

                int bytesPerSecond = loopbackCapture.WaveFormat.AverageBytesPerSecond;
                int bytesToFill = (int)(bytesPerSecond * elapsedMs / 1000.0);

                if (e.BytesRecorded > 0)
                {
                    // 실제 오디오 데이터 기록
                    audioStream.WriteBlock(e.Buffer, 0, e.BytesRecorded);
                    
                    // 남은 시간만큼 무음 채워넣기 (실제 데이터가 부족한 경우)
                    if (e.BytesRecorded < bytesToFill)
                    {
                        int silenceBytes = bytesToFill - e.BytesRecorded;
                        byte[] silenceBuffer = new byte[silenceBytes];
                        audioStream.WriteBlock(silenceBuffer, 0, silenceBuffer.Length);
                    }
                }
                else
                {
                    // 소리가 없을 때는 경과 시간만큼 무음 기록
                    byte[] silenceBuffer = new byte[bytesToFill];
                    audioStream.WriteBlock(silenceBuffer, 0, silenceBuffer.Length);
                }
            };
            loopbackCapture.StartRecording();

            isRecording = true;

            screenThread = new Thread(RecordScreen)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            screenThread.Start();
        }

        public void StopRecording()
        {
            isRecording = false;

            if (screenThread != null && screenThread.IsAlive)
            {
                if (!screenThread.Join(1000))
                {
                    screenThread.Abort();
                }
                screenThread = null;
            }

            loopbackCapture?.StopRecording();
            loopbackCapture?.Dispose();

            writer?.Close();
            writer = null;
        }

        private void RecordScreen()
        {
            var bounds = Screen.PrimaryScreen.Bounds;
            long frameDurationMs = 1000 / frameRate;

            Stopwatch sw = Stopwatch.StartNew();
            long writtenFrames = 0;

            try
            {
                while (isRecording)
                {
                    long elapsed = sw.ElapsedMilliseconds;
                    long expectedFrames = elapsed / frameDurationMs;

                    while (isRecording && writtenFrames < expectedFrames)
                    {
                        using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb))
                        {
                            using (var g = Graphics.FromImage(bmp))
                            {
                                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                            }

                            bmp.RotateFlip(RotateFlipType.RotateNoneFlipY);

                            var bits = bmp.LockBits(
                                new Rectangle(0, 0, bounds.Width, bounds.Height),
                                ImageLockMode.ReadOnly,
                                PixelFormat.Format32bppRgb);

                            int stride = bits.Stride;
                            int length = stride * bits.Height;
                            byte[] buffer = new byte[length];
                            Marshal.Copy(bits.Scan0, buffer, 0, length);

                            videoStream.WriteFrame(true, buffer, 0, buffer.Length);

                            bmp.UnlockBits(bits);
                        }

                        writtenFrames++;
                    }

                    Thread.Sleep(1);
                }
            }
            catch (ThreadAbortException)
            {
                // 강제 종료 시 안전하게 빠져나오기
            }
        }
    }
}
