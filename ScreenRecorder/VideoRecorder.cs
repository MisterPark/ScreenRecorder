using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SharpAvi;
using SharpAvi.Output;

namespace ScreenRecorder
{
    public class VideoRecorder
    {
        private AviWriter writer;
        private IAviVideoStream videoStream;
        private Thread screenThread;
        private bool isRecording;
        private int frameRate;
        private string filePath;

        public VideoRecorder(string filePath, int fps)
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

            videoStream = writer.AddVideoStream();
            videoStream.Width = bounds.Width;
            videoStream.Height = bounds.Height;
            videoStream.Codec = 0; // 무압축
            videoStream.BitsPerPixel = BitsPerPixel.Bpp32;

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
                // 최대 1초만 기다리고 강제 종료 방지
                if (!screenThread.Join(1000))
                {
                    screenThread.Abort();
                }
                screenThread = null;
            }

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

                    // 내부 루프에서도 isRecording 체크
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

                    Thread.Sleep(1); // CPU 부하 줄이기
                }
            }
            catch (ThreadAbortException)
            {
                // 강제 종료 시 안전하게 빠져나오기
            }
        }
    }
}
