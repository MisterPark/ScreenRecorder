using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SharpAvi;
using SharpAvi.Output;
using SharpAvi.Codecs;

namespace ScreenRecorder
{
    public sealed class AudioRecorder : IDisposable
    {
        private WasapiLoopbackCapture capture;
        private AviWriter writer;
        private IAviAudioStream audioStream;
        private IAviVideoStream videoStream; // 호환성용 더미 비디오
        private System.Threading.Timer dummyVideoTimer;
        private volatile bool isRecording;
        private readonly object sync = new object();
        private string outputPath;

        // 변환 버퍼 재사용
        private byte[] convBuffer = new byte[0];
        private byte[] blackFrame;

        public bool IsRecording => isRecording;

        public AudioRecorder(string path)
        {
            outputPath = path;
        }

        /// <summary>
        /// 시스템 출력(스피커) 캡처를 시작하고 오디오 스트림을 AVI에 기록.
        /// 필요 시 호환성을 위해 1x1 더미 비디오(1fps)를 함께 추가.
        /// </summary>
        public void StartRecording(int targetBitsPerSample = 16)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("outputPath is null/empty.", nameof(outputPath));

            lock (sync)
            {
                if (isRecording) return;

                // NAudio WASAPI Loopback (시스템 출력)
                capture = new WasapiLoopbackCapture(); // 기본 출력 장치
                // 캡처 포맷 (보통 IEEE float 32-bit)
                var srcFmt = capture.WaveFormat;
                int channels = srcFmt.Channels;
                int sampleRate = srcFmt.SampleRate;

                // SharpAvi 준비
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
                writer = new AviWriter(outputPath)
                {
                    // 오디오 기반 파일이라 FPS는 낮게(더미 비디오 1fps)
                    FramesPerSecond = 30,
                    EmitIndex1 = true,
                    // 엔디안/인터리브는 SharpAvi가 처리
                };

                // 호환성용 1x1 Uncompressed 비디오 스트림 (검정 프레임)
                videoStream = writer.AddUncompressedVideoStream(1, 1);
                videoStream.Name = "blank";
                blackFrame = new byte[1 * 1 * 4]; // RGBA 32bpp로 들어감(SharpAvi 내부 규칙에 맞춰 4바이트 픽셀)
                // 1초마다 더미 프레임 기록
                dummyVideoTimer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        videoStream.WriteFrame(true, blackFrame, 0, blackFrame.Length);
                    }
                    catch { /* 녹화 종료 타이밍 경쟁 무시 */ }
                }, null, 0, 1000);

                // 오디오 스트림(PCM만 지원하는 플레이어 호환 위해 16-bit 권장)
                // SharpAvi는 PCM 인터리브 바이트를 그대로 받음
                audioStream = writer.AddAudioStream(
                    channelCount: channels,
                    samplesPerSecond: sampleRate,
                    bitsPerSample: targetBitsPerSample
                );
                audioStream.Name = "system-audio";
                //audioStream.WriteBlock(new byte[0], 0, 0); // 헤더 고정 위해 더미 기록(선택)

                // 캡처 이벤트 연결
                capture.DataAvailable += CaptureOnDataAvailable;
                capture.RecordingStopped += CaptureOnRecordingStopped;

                capture.StartRecording();
                isRecording = true;
            }
        }

        public void StopRecording()
        {
            lock (sync)
            {
                if (!isRecording) return;
                try { capture?.StopRecording(); } catch { }
                // RecordingStopped에서 정리됨
            }
        }

        private void CaptureOnDataAvailable(object sender, WaveInEventArgs e)
        {
            // 대부분 WasapiLoopbackCapture는 32-bit float, interleaved
            // SharpAvi 오디오 스트림엔 16-bit PCM을 넣자(호환성 ↑)
            if (!isRecording || audioStream == null || e.BytesRecorded <= 0) return;

            EnsureConvBufferSize(e.BytesRecorded); // 최악의 경우 32f -> 16pcm에서 동일 길이 이상 필요 없지만 넉넉히

            int outBytes = ConvertFloat32ToPcm16(e.Buffer, e.BytesRecorded, convBuffer);
            if (outBytes > 0)
            {
                // AVI 오디오 스트림에 블록 쓰기
                audioStream.WriteBlock(convBuffer, 0, outBytes);
            }
        }

        private void CaptureOnRecordingStopped(object sender, StoppedEventArgs e)
        {
            lock (sync)
            {
                isRecording = false;

                try
                {
                    capture.DataAvailable -= CaptureOnDataAvailable;
                    capture.RecordingStopped -= CaptureOnRecordingStopped;
                    capture?.Dispose();
                }
                catch { }
                finally
                {
                    capture = null;
                }

                try
                {
                    dummyVideoTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    dummyVideoTimer?.Dispose();
                }
                catch { }
                finally
                {
                    dummyVideoTimer = null;
                }

                try
                {
                    writer?.Close(); // SharpAvi는 Dispose 대신 Close/Finish 개념. Close가 안전.
                    writer = null;
                    audioStream = null;
                    videoStream = null;
                }
                catch { }

                if (e.Exception != null)
                {
                    // UI Thread가 아니라면 예외만 로그로 넘기고 끝
                    // 필요하면 콜백/이벤트로 폼에 전달
                    System.Diagnostics.Debug.WriteLine("RecordingStopped exception: " + e.Exception);
                }
            }
        }

        private void EnsureConvBufferSize(int srcBytes)
        {
            // float32 -> int16 변환 시 바이트 수는 절반(4 -> 2) 정도지만, 여유 있게 확보
            if (convBuffer.Length < srcBytes)
                convBuffer = new byte[srcBytes];
        }

        /// <summary>
        /// 32-bit float(-1..1) interleaved -> 16-bit PCM little-endian 변환
        /// </summary>
        private static int ConvertFloat32ToPcm16(byte[] src, int srcBytes, byte[] dst)
        {
            int samples = srcBytes / 4;
            int outBytes = samples * 2;

            // 경계 체크
            if (dst.Length < outBytes) outBytes = dst.Length;

            int outIndex = 0;
            for (int i = 0; i < samples && outIndex + 1 < outBytes; i++)
            {
                // little-endian float 읽기
                float f = BitConverter.ToSingle(src, i * 4);
                // 클램프
                if (f > 1f) f = 1f;
                else if (f < -1f) f = -1f;

                short s = (short)Math.Round(f * short.MaxValue);
                dst[outIndex++] = (byte)(s & 0xFF);
                dst[outIndex++] = (byte)((s >> 8) & 0xFF);
            }
            return outIndex;
            // 참고: 채널 수는 이미 인터리브된 상태로 들어오므로 그대로 유지
        }

        public void Dispose()
        {
            try { StopRecording(); } catch { }
            try { writer?.Close(); } catch { }
            try { capture?.Dispose(); } catch { }
        }
    }
}
