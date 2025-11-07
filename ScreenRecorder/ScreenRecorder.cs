// ScreenRecorder.cs (Final, MF MP4: H.264 + AAC, exact timestamps, .NET Fx 4.8)
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

namespace ScreenRecorder
{
    public class ScreenRecorder
    {
        // ctor params
        private readonly string _filePath;
        private readonly Size _size;
        private readonly int _fps;              // 표기/힌트용(저장은 TS 기반 가변 가능)
        private readonly int _quality;          // 비트레이트 스케일링
        private readonly bool _includeCursor;
        private readonly Point _screenOrigin;

        // cancel & tasks
        private CancellationTokenSource _cts;
        private Task _recordingTask;

        // Audio
        private WasapiLoopbackCapture _audioCap;
        private int _audioSampleRate = 48000;
        private const int OUT_BITS = 16;
        private const int OUT_CHANNELS = 2;
        private const int AUDIO_BLOCK_ALIGN = OUT_CHANNELS * (OUT_BITS / 8); // 4 bytes

        // Media Foundation SinkWriter
        private IMFSinkWriter _sink = null;
        private int _videoStreamIndex = -1;
        private int _audioStreamIndex = -1;

        // timeline
        private long _qpcStart;
        private long _qpcFreq;
        private long _audioPts100ns; // AAC 타임라인 누적(100ns)

        // GDI capture (DIBSection top-down)
        private IntPtr _hScreenDC = IntPtr.Zero;
        private IntPtr _hMemDC = IntPtr.Zero;
        private IntPtr _hDIB = IntPtr.Zero;
        private IntPtr _dibPtr = IntPtr.Zero;
        private IntPtr _hOld = IntPtr.Zero;

        // ====== 리샘플링(44.1k → 48k) 상태 ======
        private const int AUDIO_TARGET_RATE = 48000; // AAC는 48k로 고정
        private double _rsPos = 0.0;                 // 리샘플 누적 위치(입력 프레임 기준, 소수 포함)
        private double _rsRatio = 1.0;               // 48k / 입력샘플레이트(예: 48000/44100)
        private float _prevL = 0f, _prevR = 0f;      // 경계 보간용 이전 L/R 샘플
        private bool _havePrev = false;              // 이전 샘플 존재 여부

        public ScreenRecorder(string filePath, Size size, int fps, int quality, bool includeCursor, Point screenOrigin)
        {
            _filePath = System.IO.Path.ChangeExtension(filePath, ".mp4");
            _size = size;
            _fps = Math.Max(1, Math.Min(fps, 240));
            _quality = Math.Max(1, Math.Min(quality, 100));
            _includeCursor = includeCursor;
            _screenOrigin = screenOrigin;
        }

        // ---------- Public API ----------
        public Task StartAsync()
        {
            _cts = new CancellationTokenSource();

            // MF init & clock base
            CheckHR(MFStartup(0x20070, MFSTARTUP_NOSOCKET));
            _qpcFreq = Stopwatch.Frequency;
            _qpcStart = Stopwatch.GetTimestamp();
            _audioPts100ns = 0;

            // (A) 오디오 캡처 먼저 만들어 포맷 획득
            _audioCap = new WasapiLoopbackCapture();
            int inRate = _audioCap.WaveFormat.SampleRate;   // 44100일 것
            _rsRatio = AUDIO_TARGET_RATE / (double)inRate;  // 48000 / 44100
            _rsPos = 0.0;
            _havePrev = false;
            _prevL = _prevR = 0f;
            _audioSampleRate = _audioCap.WaveFormat.SampleRate; // 44100/48000 등
            _audioCap.DataAvailable += OnAudioData;

            // (B) SinkWriter 생성
            CheckHR(MFCreateSinkWriterFromURL(_filePath, IntPtr.Zero, IntPtr.Zero, out _sink));

            // (C) 스트림 추가: Video → Audio → 입력 타입 설정 → BeginWriting
            AddVideoStreams();
            AddAudioStreams();
            CheckHR(_sink.BeginWriting());

            // (D) 캡처 시작
            _audioCap.StartRecording();
            PrepareDIBSection();
            _recordingTask = Task.Run(() => CaptureLoopMF(_cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            try { _cts?.Cancel(); } catch { }
            try { if (_recordingTask != null) await _recordingTask.ConfigureAwait(false); } catch { }

            try { _audioCap?.StopRecording(); } catch { }
            _audioCap?.Dispose(); _audioCap = null;

            ReleaseDIBSection();

            try { if (_sink != null) _sink.Finalize_(); } catch { }
            if (_sink != null) Marshal.ReleaseComObject(_sink); _sink = null;

            MFShutdown();
            _cts?.Dispose(); _cts = null;
        }

        // ---------- Video: add streams ----------
        private void AddVideoStreams()
        {
            // 대략적 비트레이트 추정 + 품질 반영 (2~20 Mbps 범위)
            long est = (long)_size.Width * _size.Height * _fps * 2;
            uint bitrate = ClampU(est * (50 + _quality) / 120, 2_000_000u, 20_000_000u);

            // 출력(H.264)
            IMFMediaType outVideo; CheckHR(MFCreateMediaType(out outVideo));
            CheckHR(MTSetGUID(outVideo, MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Video));
            CheckHR(MTSetGUID(outVideo, MFAttributesClsid.MF_MT_SUBTYPE, MFVideoFormat.H264));
            CheckHR(MTSetUINT32(outVideo, MFAttributesClsid.MF_MT_AVG_BITRATE, (int)bitrate));
            CheckHR(MTSetUINT32(outVideo, MFAttributesClsid.MF_MT_INTERLACE_MODE, (int)MFVideoInterlaceMode.Progressive));
            CheckHR(SetAttrSize(outVideo, MFAttributesClsid.MF_MT_FRAME_SIZE, (uint)_size.Width, (uint)_size.Height));
            CheckHR(SetAttrRatio(outVideo, MFAttributesClsid.MF_MT_FRAME_RATE, (uint)_fps, 1));
            CheckHR(SetAttrRatio(outVideo, MFAttributesClsid.MF_MT_PIXEL_ASPECT_RATIO, 1, 1));

            // H.264 프로파일(Main=77)
            var MF_MT_MPEG2_PROFILE = new Guid("ad76a80b-2d5c-4e0b-b375-4796d7b72f9f");
            MTSetUINT32(outVideo, MF_MT_MPEG2_PROFILE, 77);
            // (옵션) 모든 샘플 독립 플래그
            var MF_MT_ALL_SAMPLES_INDEPENDENT = new Guid("b8ebefaf-b718-4e04-b0a9-116775e3321b");
            MTSetUINT32(outVideo, MF_MT_ALL_SAMPLES_INDEPENDENT, 1);

            CheckHR(_sink.AddStream(outVideo, out _videoStreamIndex));

            // 입력(RGB32)
            IMFMediaType inVideo; CheckHR(MFCreateMediaType(out inVideo));
            CheckHR(MTSetGUID(inVideo, MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Video));
            CheckHR(MTSetGUID(inVideo, MFAttributesClsid.MF_MT_SUBTYPE, MFVideoFormat.RGB32));
            CheckHR(MTSetUINT32(inVideo, MFAttributesClsid.MF_MT_INTERLACE_MODE, (int)MFVideoInterlaceMode.Progressive));
            CheckHR(SetAttrSize(inVideo, MFAttributesClsid.MF_MT_FRAME_SIZE, (uint)_size.Width, (uint)_size.Height));
            CheckHR(SetAttrRatio(inVideo, MFAttributesClsid.MF_MT_FRAME_RATE, (uint)_fps, 1));
            CheckHR(SetAttrRatio(inVideo, MFAttributesClsid.MF_MT_PIXEL_ASPECT_RATIO, 1, 1));

            CheckHR(_sink.SetInputMediaType(_videoStreamIndex, inVideo, IntPtr.Zero));
        }

        // ---------- Audio: add streams ----------
        private void AddAudioStreams()
        {
            // ===== 출력(AAC) =====
            IMFMediaType outAudio; CheckHR(MFCreateMediaType(out outAudio));
            CheckHR(MTSetGUID(outAudio, MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Audio));
            CheckHR(MTSetGUID(outAudio, MFAttributesClsid.MF_MT_SUBTYPE, MFAudioFormat.AAC));

            // 48 kHz 고정 (리샘플로 맞춤)
            CheckHR(MTSetUINT32(outAudio, MFAttributesClsid.MF_MT_AUDIO_NUM_CHANNELS, OUT_CHANNELS));                 // 2
            CheckHR(MTSetUINT32(outAudio, MFAttributesClsid.MF_MT_AUDIO_SAMPLES_PER_SECOND, AUDIO_TARGET_RATE));      // 48000

            // 평균 비트레이트(bps). 128kbps 기본, 안되면 96000 또는 192000 시도
            CheckHR(MTSetUINT32(outAudio, MFAttributesClsid.MF_MT_AVG_BITRATE, 128000));

            // AAC LC 레벨
            var MF_MT_MPEG2_AAC_PROFILE_LEVEL_INDICATION = new Guid("7632f0e6-9538-4d61-acda-ea29c8c14456");
            MTSetUINT32(outAudio, MF_MT_MPEG2_AAC_PROFILE_LEVEL_INDICATION, 0x29);

            // ADTS payload type 0
            CheckHR(MTSetUINT32(outAudio, MFAttributesClsid.MF_MT_AAC_PAYLOAD_TYPE, 0));

            CheckHR(_sink.AddStream(outAudio, out _audioStreamIndex));

            // ===== 입력(PCM 16-bit stereo) =====
            int avgBytes = AUDIO_TARGET_RATE * AUDIO_BLOCK_ALIGN;   // 48000 * 4 = 192000
            const int KSAUDIO_SPEAKER_STEREO = 0x00000003;
            var MF_MT_AUDIO_CHANNEL_MASK = new Guid("55fb5765-644a-4caf-86b7-6f4f1c36b0a3");
            var MF_MT_AUDIO_PREFER_WAVEFORMATEX = new Guid("a901aaba-e037-458a-bdf6-545be2074042");
            var MF_MT_FIXED_SIZE_SAMPLES = new Guid("b8ebefaf-b718-4e04-b0a9-116775e3321a");
            var MF_MT_ALL_SAMPLES_INDEPENDENT = new Guid("b8ebefaf-b718-4e04-b0a9-116775e3321c");
            var MF_MT_SAMPLE_SIZE = new Guid("dad3ab78-1990-408b-bce2-eba673dacc10");

            IMFMediaType inAudio; CheckHR(MFCreateMediaType(out inAudio));
            CheckHR(MTSetGUID(inAudio, MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Audio));
            CheckHR(MTSetGUID(inAudio, MFAttributesClsid.MF_MT_SUBTYPE, MFAudioFormat.PCM));
            CheckHR(MTSetUINT32(inAudio, MFAttributesClsid.MF_MT_AUDIO_NUM_CHANNELS, OUT_CHANNELS));                  // 2
            CheckHR(MTSetUINT32(inAudio, MFAttributesClsid.MF_MT_AUDIO_SAMPLES_PER_SECOND, AUDIO_TARGET_RATE));       // 48000
            CheckHR(MTSetUINT32(inAudio, MFAttributesClsid.MF_MT_AUDIO_BITS_PER_SAMPLE, OUT_BITS));                   // 16
            CheckHR(MTSetUINT32(inAudio, MFAttributesClsid.MF_MT_AUDIO_BLOCK_ALIGNMENT, AUDIO_BLOCK_ALIGN));          // 4
            CheckHR(MTSetUINT32(inAudio, MFAttributesClsid.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, avgBytes));              // 192000

            // ★ 추가 플래그들 (이게 빠지면 0xC00D36B4 나는 환경 있음)
            MTSetUINT32(inAudio, MF_MT_AUDIO_CHANNEL_MASK, KSAUDIO_SPEAKER_STEREO);                                   // L|R
            MTSetUINT32(inAudio, MF_MT_AUDIO_PREFER_WAVEFORMATEX, 1);                                                 // WAVEFORMATEX 선호
            MTSetUINT32(inAudio, MF_MT_FIXED_SIZE_SAMPLES, 1);                                                        // PCM 고정 샘플 크기
            MTSetUINT32(inAudio, MF_MT_ALL_SAMPLES_INDEPENDENT, 1);                                                   // 샘플 독립
                                                                                                                      // 샘플(블록) 크기 명시(바이트). 일부 인코더가 요구.
            MTSetUINT32(inAudio, MF_MT_SAMPLE_SIZE, AUDIO_BLOCK_ALIGN);

            CheckHR(_sink.SetInputMediaType(_audioStreamIndex, inAudio, IntPtr.Zero));
        }


        // ---------- Audio callback: float32 → PCM16 stereo with timestamps ----------
        private void OnAudioData(object sender, WaveInEventArgs e)
        {
            // 입력: float32 interleaved, inRate(예: 44100), inCh(보통 2)
            int inCh = _audioCap.WaveFormat.Channels;
            int inFloats = e.BytesRecorded / 4;
            if (inFloats <= 0) return;

            int inFrames = inFloats / inCh;

            // 1) 입력을 프레임 단위 L/R float로 읽기
            //    (다채널이면 평균으로 다운믹스)
            //    src[f] = (L,R)
            var srcL = new float[inFrames + 1]; // 마지막 경계 보간 위해 +1
            var srcR = new float[inFrames + 1];

            int idx = 0;
            for (int f = 0; f < inFrames; f++)
            {
                int baseIdx = f * inCh;
                float L, R;
                if (inCh == 1)
                {
                    float m = BitConverter.ToSingle(e.Buffer, (baseIdx + 0) * 4);
                    L = R = m;
                }
                else if (inCh == 2)
                {
                    L = BitConverter.ToSingle(e.Buffer, (baseIdx + 0) * 4);
                    R = BitConverter.ToSingle(e.Buffer, (baseIdx + 1) * 4);
                }
                else
                {
                    float sum = 0f;
                    for (int c = 0; c < inCh; c++)
                        sum += BitConverter.ToSingle(e.Buffer, (baseIdx + c) * 4);
                    float avg = sum / inCh;
                    L = R = avg;
                }
                srcL[idx] = L; srcR[idx] = R; idx++;
            }

            // 경계 보간을 위해 마지막 샘플을 한 개 더 복제
            srcL[inFrames] = inFrames > 0 ? srcL[inFrames - 1] : 0f;
            srcR[inFrames] = inFrames > 0 ? srcR[inFrames - 1] : 0f;

            // 2) 44.1k → 48k 리샘플 (선형 보간, 지속 상태 사용)
            //    출력 프레임 수 대략 = 입력프레임 * ratio
            int outFramesEst = (int)Math.Ceiling(inFrames * _rsRatio) + 8;
            var outBytes = new byte[outFramesEst * AUDIO_BLOCK_ALIGN];

            int o = 0;
            double pos = _rsPos;      // 현재 입력 프레임 위치(소수)
            double ratio = _rsRatio;  // 48000 / 44100

            // 이전 마지막 샘플을 src[-1]처럼 써야 하는 첫 보간 대비
            float prevL = _prevL, prevR = _prevR;
            bool havePrev = _havePrev;

            // 입력 프레임 인덱스 0..inFrames-1 사이에서 보간
            while (pos < inFrames)
            {
                int i0 = (int)pos;
                double t = pos - i0; // 0..1

                float l0, r0;
                if (i0 >= 0) { l0 = srcL[i0]; r0 = srcR[i0]; }
                else { l0 = prevL; r0 = prevR; }

                float l1 = srcL[i0 + 1];
                float r1 = srcR[i0 + 1];

                float L = (float)(l0 + (l1 - l0) * t);
                float R = (float)(r0 + (r1 - r0) * t);

                // float -> 16bit PCM
                if (L > 1f) L = 1f; else if (L < -1f) L = -1f;
                if (R > 1f) R = 1f; else if (R < -1f) R = -1f;

                short l16 = (short)(L * 32767f);
                short r16 = (short)(R * 32767f);

                outBytes[o++] = (byte)(l16 & 0xFF);
                outBytes[o++] = (byte)((l16 >> 8) & 0xFF);
                outBytes[o++] = (byte)(r16 & 0xFF);
                outBytes[o++] = (byte)((r16 >> 8) & 0xFF);

                pos += ratio;
            }

            // 3) 다음 콜백 대비 경계 상태 저장
            //    입력 마지막 실제 샘플을 prev로 보관
            if (inFrames > 0)
            {
                _prevL = srcL[inFrames - 1];
                _prevR = srcR[inFrames - 1];
                _havePrev = true;
            }
            _rsPos = pos - inFrames; // 다음 콜백 시작 시, 새 입력의 0 기준 재설정

            // 4) 48k PCM16을 MF 샘플로 기록
            int outLen = o; // 실제 채워진 바이트 수
            if (outLen <= 0) return;

            int outFrames = outLen / AUDIO_BLOCK_ALIGN;
            long dur100 = (long)(10_000_000L * outFrames / (double)AUDIO_TARGET_RATE);

            CheckHR(MFCreateMemoryBuffer((uint)outLen, out var buf));
            IntPtr p; int max, cur;
            CheckHR(buf.Lock(out p, out max, out cur));
            Marshal.Copy(outBytes, 0, p, outLen);
            CheckHR(buf.Unlock());
            CheckHR(buf.SetCurrentLength(outLen));

            CheckHR(MFCreateSample(out var sample));
            CheckHR(sample.AddBuffer(buf));
            CheckHR(sample.SetSampleTime(_audioPts100ns));
            CheckHR(sample.SetSampleDuration(dur100));
            CheckHR(_sink.WriteSample(_audioStreamIndex, sample));

            _audioPts100ns += dur100;

            Marshal.ReleaseComObject(buf);
            Marshal.ReleaseComObject(sample);
        }

        private static byte[] FloatToPcm16Stereo(byte[] src, int bytesRecorded, int inChannels)
        {
            int totalFloats = bytesRecorded / 4;
            if (totalFloats == 0) return Array.Empty<byte>();
            int frames = totalFloats / inChannels;

            byte[] dst = new byte[frames * AUDIO_BLOCK_ALIGN];
            int o = 0;
            for (int f = 0; f < frames; f++)
            {
                int baseIdx = f * inChannels;
                float L, R;
                if (inChannels == 1)
                {
                    float m = BitConverter.ToSingle(src, (baseIdx + 0) * 4);
                    L = R = m;
                }
                else if (inChannels == 2)
                {
                    L = BitConverter.ToSingle(src, (baseIdx + 0) * 4);
                    R = BitConverter.ToSingle(src, (baseIdx + 1) * 4);
                }
                else
                {
                    float sum = 0f;
                    for (int c = 0; c < inChannels; c++)
                        sum += BitConverter.ToSingle(src, (baseIdx + c) * 4);
                    float avg = sum / inChannels;
                    L = R = avg;
                }
                if (L > 1f) L = 1f; else if (L < -1f) L = -1f;
                if (R > 1f) R = 1f; else if (R < -1f) R = -1f;

                short l16 = (short)(L * 32767f);
                short r16 = (short)(R * 32767f);
                dst[o++] = (byte)(l16 & 0xFF);
                dst[o++] = (byte)((l16 >> 8) & 0xFF);
                dst[o++] = (byte)(r16 & 0xFF);
                dst[o++] = (byte)((r16 >> 8) & 0xFF);
            }
            return dst;
        }

        // ---------- Video capture loop (VFR: 각 프레임을 실제 시각으로 기록) ----------
        private void CaptureLoopMF(CancellationToken token)
        {
            timeBeginPeriod(1);
            try
            {
                int bufBytes = _size.Width * _size.Height * 4;
                var frame = new byte[bufBytes];

                while (!token.IsCancellationRequested)
                {
                    BitBlt(_hMemDC, 0, 0, _size.Width, _size.Height, _hScreenDC, _screenOrigin.X, _screenOrigin.Y, SRCCOPY);
                    Marshal.Copy(_dibPtr, frame, 0, bufBytes);

                    if (_includeCursor)
                    {
                        using (var bmp = new Bitmap(_size.Width, _size.Height, _size.Width * 4,
                               System.Drawing.Imaging.PixelFormat.Format32bppArgb,
                               Marshal.UnsafeAddrOfPinnedArrayElement(frame, 0)))
                        using (var g = Graphics.FromImage(bmp))
                        {
                            var global = Control.MousePosition;
                            var local = new Point(global.X - _screenOrigin.X, global.Y - _screenOrigin.Y);
                            if (local.X >= 0 && local.Y >= 0 && local.X < _size.Width && local.Y < _size.Height)
                                Cursors.Default.Draw(g, new Rectangle(local, Cursors.Default.Size));
                        }
                    }

                    WriteVideoFrameToMF(frame);
                    Thread.Sleep(1); // 고정 FPS 강제 안 함(진짜 그 순간만 기록)
                }
            }
            finally
            {
                timeEndPeriod(1);
            }
        }

        private void WriteVideoFrameToMF(byte[] bgra32)
        {
            long ticks = Stopwatch.GetTimestamp() - _qpcStart;
            long pts100ns = ticks * 10_000_000 / _qpcFreq;

            CheckHR(MFCreateMemoryBuffer((uint)bgra32.Length, out var buf));
            IntPtr p; int max, cur;
            CheckHR(buf.Lock(out p, out max, out cur));
            Marshal.Copy(bgra32, 0, p, bgra32.Length);
            CheckHR(buf.Unlock());
            CheckHR(buf.SetCurrentLength(bgra32.Length));

            CheckHR(MFCreateSample(out var sample));
            CheckHR(sample.AddBuffer(buf));
            CheckHR(sample.SetSampleTime(pts100ns));       // 진짜 시각으로 기록
            // Duration은 생략(VFR). 필요하면 이전-현재 차이로 넣어도 됨.

            CheckHR(_sink.WriteSample(_videoStreamIndex, sample));
            Marshal.ReleaseComObject(buf);
            Marshal.ReleaseComObject(sample);
        }

        // ---------- DIBSection top-down ----------
        private void PrepareDIBSection()
        {
            _hScreenDC = GetDC(IntPtr.Zero);
            _hMemDC = CreateCompatibleDC(_hScreenDC);

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = _size.Width;
            bmi.bmiHeader.biHeight = -_size.Height; // top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB;
            bmi.bmiHeader.biSizeImage = (uint)(_size.Width * _size.Height * 4);
            bmi.bmiColors = new uint[256];

            _hDIB = CreateDIBSection(_hScreenDC, ref bmi, 0, out _dibPtr, IntPtr.Zero, 0);
            _hOld = SelectObject(_hMemDC, _hDIB);
        }

        private void ReleaseDIBSection()
        {
            if (_hOld != IntPtr.Zero && _hMemDC != IntPtr.Zero) SelectObject(_hMemDC, _hOld);
            if (_hDIB != IntPtr.Zero) DeleteObject(_hDIB);
            if (_hMemDC != IntPtr.Zero) DeleteDC(_hMemDC);
            if (_hScreenDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _hScreenDC);
            _hOld = _hDIB = _hMemDC = _hScreenDC = IntPtr.Zero;
            _dibPtr = IntPtr.Zero;
        }

        // ---------- Helpers ----------
        private static uint ClampU(long value, uint min, uint max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return (uint)value;
        }

        // IMFMediaType attribute helpers (ref Guid 시그니처 대응)
        private static int MTSetGUID(IMFMediaType mt, Guid key, Guid val)
        {
            return ((IMFAttributes)mt).SetGUID(ref key, ref val);
        }
        private static int MTSetUINT32(IMFMediaType mt, Guid key, int value)
        {
            return ((IMFAttributes)mt).SetUINT32(ref key, value);
        }
        private static int MTSetUINT64(IMFMediaType mt, Guid key, long value)
        {
            return ((IMFAttributes)mt).SetUINT64(ref key, value);
        }
        private static int SetAttrSize(IMFMediaType mt, Guid key, uint w, uint h)
        {
            ulong packed = ((ulong)w << 32) | h;
            return MTSetUINT64(mt, key, (long)packed);
        }
        private static int SetAttrRatio(IMFMediaType mt, Guid key, uint n, uint d)
        {
            ulong packed = ((ulong)n << 32) | d;
            return MTSetUINT64(mt, key, (long)packed);
        }

        // ---------- WinMM timer ----------
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uMilliseconds);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uMilliseconds);

        // ---------- GDI ----------
        private const int SRCCOPY = 0x00CC0020;
        private const uint BI_RGB = 0;

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
                                          IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BITMAPINFO pbmi,
            uint iUsage,
            out IntPtr ppvBits,
            IntPtr hSection,
            uint dwOffset
        );

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public uint[] bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;   // 음수=top-down
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        // ---------- Media Foundation minimal interop ----------
        private const int MFSTARTUP_NOSOCKET = 0x1;
        private static void CheckHR(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int dwFlags);
        [DllImport("mfplat.dll")] private static extern int MFShutdown();

        [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType ppMT);
        [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IMFSample ppSample);
        [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);

        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        private static extern int MFCreateSinkWriterFromURL(string pwszOutputURL, IntPtr pByteStream, IntPtr pAttributes, out IMFSinkWriter ppSinkWriter);

        // Keys
        private static class MFAttributesClsid
        {
            public static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
            public static readonly Guid MF_MT_SUBTYPE = new Guid("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
            public static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652C33D-D6B2-4012-B834-72030849A37D");
            public static readonly Guid MF_MT_FRAME_RATE = new Guid("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
            public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid("C21B8EE5-B956-4071-8DAF-325EDF5CAB11");
            public static readonly Guid MF_MT_AVG_BITRATE = new Guid("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
            public static readonly Guid MF_MT_INTERLACE_MODE = new Guid("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
            public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new Guid("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
            public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5F2A97E7-4A8D-467A-A726-9EF2A1206D63");
            public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new Guid("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
            public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new Guid("322DE230-9EEB-43BD-AB7A-FF412251541D");
            public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");
            public static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new Guid("BFBABE79-7434-4D1C-94F0-72A3B9E17188");
        }

        private static class MFMediaType
        {
            public static readonly Guid Video = new Guid("73646976-0000-0010-8000-00AA00389B71"); // 'vide'
            public static readonly Guid Audio = new Guid("73647561-0000-0010-8000-00AA00389B71"); // 'sdua'
        }

        private static class MFVideoFormat
        {
            public static readonly Guid H264 = new Guid("34363248-0000-0010-8000-00AA00389B71"); // 'H264'
            public static readonly Guid RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        }

        private static class MFAudioFormat
        {
            public static readonly Guid AAC = new Guid("00001610-0000-0010-8000-00AA00389B71");
            public static readonly Guid PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
        }

        private enum MFVideoInterlaceMode { Progressive = 2 }

        // IMFAttributes (base)
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
        private interface IMFAttributes
        {
            int GetItem([In] ref Guid guidKey, IntPtr pValue);
            int GetItemType([In] ref Guid guidKey, out int pType);
            int CompareItem([In] ref Guid guidKey, IntPtr Value, out bool pbResult);
            int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, int MatchType, out bool pbResult);
            int GetUINT32([In] ref Guid guidKey, out int punValue);
            int GetUINT64([In] ref Guid guidKey, out long punValue);
            int GetDouble([In] ref Guid guidKey, out double pfValue);
            int GetGUID([In] ref Guid guidKey, out Guid pguidValue);
            int GetStringLength([In] ref Guid guidKey, out int pcchLength);
            int GetString([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, int cchBufSize, out int pcchLength);
            int GetAllocatedString([In] ref Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
            int GetBlobSize([In] ref Guid guidKey, out int pcbBlobSize);
            int GetBlob([In] ref Guid guidKey, [Out] byte[] pBuf, int cbBufSize, out int pcbBlobSize);
            int GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ip, out int pcbSize);
            int GetUnknown([In] ref Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

            int SetItem([In] ref Guid guidKey, IntPtr Value);
            int DeleteItem([In] ref Guid guidKey);
            int DeleteAllItems();
            int SetUINT32([In] ref Guid guidKey, int unValue);
            int SetUINT64([In] ref Guid guidKey, long unValue);
            int SetDouble([In] ref Guid guidKey, double fValue);
            int SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
            int SetString([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            int SetBlob([In] ref Guid guidKey, [In] byte[] pBuf, int cbBufSize);
            int SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);

            int LockStore();
            int UnlockStore();
            int GetCount(out int pcItems);
            int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
            int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);
        }

        // IMFMediaType
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
        private interface IMFMediaType : IMFAttributes
        {
            // IMFAttributes 메서드 상속됨
            int GetMajorType(out Guid pguidMajorType);
            int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
            int IsEqual([MarshalAs(UnmanagedType.Interface)] IMFMediaType pIMediaType, out int pdwFlags);
            int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
            int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
        }

        // IMFSample
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
        private interface IMFSample
        {
            int GetSampleFlags(out int pdwSampleFlags);
            int SetSampleFlags(int dwSampleFlags);
            int GetSampleTime(out long phnsSampleTime);
            int SetSampleTime(long hnsSampleTime);
            int GetSampleDuration(out long phnsSampleDuration);
            int SetSampleDuration(long hnsSampleDuration);
            int GetBufferCount(out int pdwBufferCount);
            int GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
            int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
            int AddBuffer(IMFMediaBuffer pBuffer);
            int RemoveBufferByIndex(int dwIndex);
            int RemoveAllBuffers();
            int GetTotalLength(out int pcbTotalLength);
            int CopyToBuffer(IMFMediaBuffer pBuffer);
        }

        // IMFMediaBuffer
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
        private interface IMFMediaBuffer
        {
            int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
            int Unlock();
            int GetCurrentLength(out int pcbCurrentLength);
            int SetCurrentLength(int cbCurrentLength);
            int GetMaxLength(out int pcbMaxLength);
        }

        // IMFSinkWriter
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D")]
        private interface IMFSinkWriter
        {
            int AddStream([MarshalAs(UnmanagedType.Interface)] IMFMediaType pTargetMediaType, out int pdwStreamIndex);
            int SetInputMediaType(int dwStreamIndex, [MarshalAs(UnmanagedType.Interface)] IMFMediaType pInputMediaType, IntPtr pEncodingParameters);
            int BeginWriting();
            int WriteSample(int dwStreamIndex, [MarshalAs(UnmanagedType.Interface)] IMFSample pSample);
            int SendStreamTick(int dwStreamIndex, long llTimestamp);
            int PlaceMarker(int dwStreamIndex, IntPtr pvContext);
            int NotifyEndOfSegment(int dwStreamIndex);
            int Flush(int dwStreamIndex);
            int Finalize_();
            int GetServiceForStream(int dwStreamIndex, [In] ref Guid guidService, [In] ref Guid riid, out IntPtr ppvObject);
            int GetStatistics(int dwStreamIndex, out MF_SINK_WRITER_STATISTICS pStats);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MF_SINK_WRITER_STATISTICS
        {
            public int cb;
            public long qwNumSamplesProcessed;
            public long qwNumSamplesEncoded;
            public long qwNumSamplesReceived;
            public long qwLatency;
            public int dwLastSendTime;
            public int dwNumOutstandingSamples;
            public long qwByteCountQueued;
            public long qwByteCountProcessed;
            public long qwByteCountDropped;
        }
    }
}
