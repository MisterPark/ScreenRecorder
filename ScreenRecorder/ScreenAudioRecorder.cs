using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using SharpAvi;
using SharpAvi.Output;

namespace ScreenRecorder
{
    /// <summary>
    /// WinForms / .NET 4.8
    /// SharpAvi + NAudio(WasapiLoopbackCapture)
    /// - 비디오: GDI CopyFromScreen → 32bpp RGB → Uncompressed AVI
    /// - 오디오: WASAPI Loopback(float32) → 16-bit PCM(장치 샘플레이트) → AVI
    /// - 동기화: 공용 Stopwatch 기반. 오디오는 무음 패딩으로 타임라인 유지, 비디오는 과도한 리드 제한
    /// - unsafe 미사용, 불필요한 의존 제거, 컴파일 오류 제거
    /// </summary>
    public sealed class ScreenAudioRecorder : IDisposable
    {
        // ===== 설정 =====
        private readonly int _fps;
        private readonly Rectangle _captureArea;
        private readonly string _outputPath;

        // ===== 공통 상태 =====
        private CancellationTokenSource _cts;
        private volatile bool _isRunning;
        private readonly object _stateLock = new object();
        private Stopwatch _startSw; // Start() 기준 절대 시계

        // ===== 비디오 =====
        private AviWriter _writer;
        private IAviVideoStream _videoStream;
        private Task _videoTask;
        private Bitmap _screenBmp;
        private Graphics _screenG;
        private byte[] _frameBuffer; // width*4*height (bottom-up)

        // ===== 오디오 =====
        private IAviAudioStream _audioStream;
        private WasapiLoopbackCapture _loopback;
        private Task _audioWriterTask;
        private AutoResetEvent _audioDataEvent;
        private readonly ConcurrentQueue<byte[]> _audioQueue = new ConcurrentQueue<byte[]>();
        private int _audioChannels;
        private int _audioSampleRate;
        private int _audioBlockAlign;               // channels * 2 (16-bit)
        private long _audioBytesWritten;            // AVI에 쓴 총 오디오 바이트 수

        public ScreenAudioRecorder(string outputPath, int fps = 30, Rectangle? area = null)
        {
            _outputPath = outputPath ?? throw new ArgumentNullException(nameof(outputPath));
            _fps = Math.Max(1, fps);

            var screenBounds = area ?? System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            _captureArea = new Rectangle(0, 0, screenBounds.Width, screenBounds.Height);
        }

        // ===== 수명주기 =====
        public void Start()
        {
            lock (_stateLock)
            {
                if (_isRunning)
                    throw new InvalidOperationException("이미 시작됨");
                _isRunning = true;
            }

            _cts = new CancellationTokenSource();
            _audioDataEvent = new AutoResetEvent(false);
            _startSw = Stopwatch.StartNew();
            Interlocked.Exchange(ref _audioBytesWritten, 0);

            // AviWriter
            _writer = new AviWriter(_outputPath)
            {
                FramesPerSecond = _fps,
                EmitIndex1 = true
            };

            // 비디오 스트림 (Uncompressed 32bpp)
            _videoStream = _writer.AddVideoStream();
            _videoStream.Width = _captureArea.Width;
            _videoStream.Height = _captureArea.Height;
            _videoStream.Codec = 0;
            _videoStream.BitsPerPixel = BitsPerPixel.Bpp32;

            _screenBmp = new Bitmap(_captureArea.Width, _captureArea.Height, PixelFormat.Format32bppRgb);
            _screenG = Graphics.FromImage(_screenBmp);
            _frameBuffer = new byte[_captureArea.Width * 4 * _captureArea.Height];

            // 오디오(장치 샘플레이트 유지, 16-bit로 변환만)
            _loopback = new WasapiLoopbackCapture();
            _audioChannels = Math.Max(1, _loopback.WaveFormat.Channels);
            _audioSampleRate = _loopback.WaveFormat.SampleRate; // 보통 48000
            _audioBlockAlign = _audioChannels * 2;

            _audioStream = _writer.AddAudioStream(
                channelCount: _audioChannels,
                samplesPerSecond: _audioSampleRate,
                bitsPerSample: 16);

            _loopback.DataAvailable += OnAudioData;
            _loopback.RecordingStopped += OnLoopbackStopped;
            _loopback.StartRecording();

            // 쓰레드 시작
            _videoTask = Task.Run(() => CaptureLoop(_cts.Token), _cts.Token);
            _audioWriterTask = Task.Run(() => AudioWriteLoop(_cts.Token), _cts.Token);
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                if (!_isRunning)
                    return;
                _isRunning = false;
            }

            // 취소 먼저
            _cts.Cancel();

            // 오디오 콜백 정리 후 정지(경합 방지)
            try
            {
                if (_loopback != null)
                {
                    _loopback.DataAvailable -= OnAudioData;
                    _loopback.RecordingStopped -= OnLoopbackStopped;
                    _loopback.StopRecording();
                }
            }
            catch { }

            try { _videoTask?.Wait(); } catch { }
            try { _audioWriterTask?.Wait(); } catch { }

            _screenG?.Dispose(); _screenG = null;
            _screenBmp?.Dispose(); _screenBmp = null;
            _loopback?.Dispose(); _loopback = null;

            try { _writer?.Close(); } finally { _writer = null; }

            _videoTask = null;
            _audioWriterTask = null;
            _videoStream = null;
            _audioStream = null;
            _frameBuffer = null;

            _audioDataEvent?.Set();
            _audioDataEvent?.Dispose();
            _audioDataEvent = null;

            _cts.Dispose();
            _cts = null;
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }

        ~ScreenAudioRecorder()
        {
            try { Stop(); } catch { }
        }

        // ===== 비디오 루프 =====
        private void CaptureLoop(CancellationToken ct)
        {
            // 고정 FPS 유지 + 오디오 진행도보다 과도하게 앞서지 않도록 제한
            long frameIndex = 0;
            const double MaxLeadSec = 0.08; // 비디오가 오디오보다 앞설 수 있는 최대 허용 리드(80ms)

            while (!ct.IsCancellationRequested)
            {
                // 최신 화면을 캡처해 버퍼 채움
                _screenG.CopyFromScreen(_captureArea.Location, Point.Empty, _captureArea.Size);
                var rect = new Rectangle(0, 0, _screenBmp.Width, _screenBmp.Height);
                var bmpData = _screenBmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                try
                {
                    int width = bmpData.Width;
                    int height = bmpData.Height;
                    int srcStride = bmpData.Stride;
                    int dstStride = width * 4;
                    IntPtr srcScan0 = bmpData.Scan0;
                    for (int y = 0; y < height; y++)
                    {
                        IntPtr srcPtr = srcScan0 + y * srcStride; // top-down
                        int dstY = height - 1 - y;                 // bottom-up
                        int dstOffset = dstY * dstStride;
                        Marshal.Copy(srcPtr, _frameBuffer, dstOffset, dstStride);
                    }
                }
                finally
                {
                    _screenBmp.UnlockBits(bmpData);
                }

                // 지금까지 경과한 시간에 비례해 **써야 하는** 프레임 수 계산
                long shouldBe = (long)Math.Floor(_startSw.Elapsed.TotalSeconds * _fps);

                // 비디오가 오디오보다 과도하게 앞서 있으면 잠깐 대기(soft sync)
                double videoSec = (frameIndex + 1) / (double)_fps;
                long audioBytes = Interlocked.Read(ref _audioBytesWritten);
                double audioSec = audioBytes / (double)(_audioSampleRate * _audioBlockAlign);
                if (videoSec - audioSec > MaxLeadSec)
                {
                    while (!ct.IsCancellationRequested)
                    {
                        audioBytes = Interlocked.Read(ref _audioBytesWritten);
                        audioSec = audioBytes / (double)(_audioSampleRate * _audioBlockAlign);
                        if (videoSec - audioSec <= MaxLeadSec)
                            break;
                        Thread.Sleep(5);
                    }
                }

                // 늦었으면 따라잡을 때까지 중복 기록(재생속도 가속 방지)
                while (!ct.IsCancellationRequested && frameIndex <= shouldBe)
                {
                    _videoStream.WriteFrame(isKeyFrame: true, _frameBuffer, 0, _frameBuffer.Length);
                    frameIndex++;
                }

                // 너무 앞서면 살짝 휴식
                Thread.Sleep(1);
            }
        }

        // ===== 오디오 콜백 & 쓰기 =====
        private void OnAudioData(object sender, WaveInEventArgs e)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;

            int bytes = e.BytesRecorded;
            if (bytes <= 0)
            {
                // 실제 무음 콜백이면 10ms 대기 유도
                _audioDataEvent?.Set();
                return;
            }

            var wf = _loopback.WaveFormat;

            if (wf.Encoding == WaveFormatEncoding.IeeeFloat && wf.BitsPerSample == 32)
            {
                // float32 → pcm16 변환(unsafe 미사용)
                int floatCount = bytes / 4;
                var pcm16 = new byte[floatCount * 2];

                for (int i = 0, o = 0; i < bytes; i += 4, o += 2)
                {
                    float f = BitConverter.ToSingle(e.Buffer, i);
                    if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
                    int v = (int)(f * 32767f);
                    if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                    short s = (short)v;
                    pcm16[o] = (byte)(s & 0xFF);
                    pcm16[o + 1] = (byte)((s >> 8) & 0xFF);
                }

                int aligned = pcm16.Length - (pcm16.Length % _audioBlockAlign);
                if (aligned > 0)
                {
                    if (aligned != pcm16.Length)
                    {
                        var trimmed = new byte[aligned];
                        Buffer.BlockCopy(pcm16, 0, trimmed, 0, aligned);
                        _audioQueue.Enqueue(trimmed);
                    }
                    else
                    {
                        _audioQueue.Enqueue(pcm16);
                    }
                    _audioDataEvent?.Set();
                }
            }
            else if (wf.Encoding == WaveFormatEncoding.Pcm && wf.BitsPerSample == 16)
            {
                int aligned = bytes - (bytes % _audioBlockAlign);
                if (aligned > 0)
                {
                    var pcm = new byte[aligned];
                    Buffer.BlockCopy(e.Buffer, 0, pcm, 0, aligned);
                    _audioQueue.Enqueue(pcm);
                    _audioDataEvent?.Set();
                }
            }
            else
            {
                // 드문 케이스: 다른 포맷은 여기서 변환 생략(필요 시 Resampler 추가)
            }
        }

        private void AudioWriteLoop(CancellationToken ct)
        {
            // 20ms 단위 무음 블록(필요 시 반복해서 패딩)
            int silenceLen = Math.Max(1, (_audioSampleRate / 50) * _audioBlockAlign);
            var silence20ms = new byte[silenceLen];
            long bytesPerSec = (long)_audioSampleRate * _audioBlockAlign;

            while (!ct.IsCancellationRequested)
            {
                // 경과시간에 해당하는 기대 오디오 바이트 수
                long expectedBytes = (long)(_startSw.Elapsed.TotalSeconds * bytesPerSec);

                // 부족분은 무음으로 채워 타임라인 맞춤(leading/mid silence 보전)
                while (!ct.IsCancellationRequested && Interlocked.Read(ref _audioBytesWritten) + silence20ms.Length <= expectedBytes)
                {
                    _audioStream.WriteBlock(silence20ms, 0, silence20ms.Length);
                    Interlocked.Add(ref _audioBytesWritten, silence20ms.Length);
                }

                if (_audioQueue.TryDequeue(out var chunk))
                {
                    int aligned = chunk.Length - (chunk.Length % _audioBlockAlign);
                    if (aligned > 0)
                    {
                        _audioStream.WriteBlock(chunk, 0, aligned);
                        Interlocked.Add(ref _audioBytesWritten, aligned);
                    }
                }
                else
                {
                    _audioDataEvent?.WaitOne(10);
                }
            }
        }

        private void OnLoopbackStopped(object sender, StoppedEventArgs e)
        {
            _audioDataEvent?.Set();
        }
    }
}
