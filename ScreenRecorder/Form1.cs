using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ScreenRecorder
{
    public partial class Form1 : Form
    {
        private ScreenRecorder recorder;
        private AudioRecorder audio;
        private VideoRecorder video;

        private ScreenAudioRecorder _rec;

        public Form1()
        {
            InitializeComponent();
        }

        private void trkQuality_Scroll(object sender, EventArgs e)
        {
            lblQuality.Text = $"품질: {trkQuality.Value}";
            LoadScreens();
            txtPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "capture.avi");
        }

        private void LoadScreens()
        {
            try
            {
                cboScreen.BeginUpdate();
                cboScreen.Items.Clear();

                var screens = Screen.AllScreens; // 최소 1개(주 모니터)는 있어야 정상
                foreach (var (scr, idx) in screens.Select((s, i) => (s, i)))
                {
                    cboScreen.Items.Add($"{idx}: {scr.Bounds.Width}x{scr.Bounds.Height} {(scr.Primary ? "(주 모니터)" : "")}");
                }

                if (cboScreen.Items.Count > 0)
                    cboScreen.SelectedIndex = 0;
                else
                    MessageBox.Show("감지된 모니터가 없습니다.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("모니터 목록을 불러오지 못했습니다: " + ex.Message);
            }
            finally
            {
                cboScreen.EndUpdate();
            }
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using (var sfd = new SaveFileDialog())
            {
                sfd.Filter = "AVI files (*.avi)|*.avi";
                sfd.FileName = Path.GetFileName(txtPath.Text);
                if (sfd.ShowDialog(this) == DialogResult.OK)
                    txtPath.Text = sfd.FileName;
            }
        }

        private void btnStart_Click(object sender, EventArgs e)
        {
            if (cboScreen.SelectedIndex < 0)
            {
                MessageBox.Show("모니터를 선택하세요.");
                return;
            }
            var screen = Screen.AllScreens[cboScreen.SelectedIndex];
            var bounds = screen.Bounds;

            int fps = (int)numFps.Value;
            int quality = trkQuality.Value;
            bool includeCursor = chkCursor.Checked;
            string path = txtPath.Text;

            try
            {
                //recorder = new ScreenRecorder(path, fps);
                //recorder.StartRecording();

                //video = new VideoRecorder(path, fps);
                //video.StartRecording();

                //audio = new AudioRecorder(path);
                //audio.StartRecording();

                // 저장 경로
                if(string.IsNullOrEmpty(path))
                {
                    path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.avi");
                }

                // 기본: 기본 모니터 전체
                var area = Screen.PrimaryScreen.Bounds;

                // fps 30 권장
                _rec = new ScreenAudioRecorder(path, 30, area);
                _rec.Start();

                btnStart.Enabled = false;
                btnStop.Enabled = true;
                btnBrowse.Enabled = false;
                cboScreen.Enabled = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show("녹화 시작 실패: " + ex.Message);
                Console.Write(ex.Message);
            }
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            btnStop.Enabled = false;
            try
            {
                //if (recorder != null)
                //    recorder.StopRecording();

                //video.StopRecording();

                //audio.StopRecording();

                _rec?.Stop();
                _rec = null;

                MessageBox.Show("저장 완료: " + txtPath.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show("녹화 종료 중 오류: " + ex.Message);
            }
            finally
            {
                recorder = null;
                btnStart.Enabled = true;
                btnBrowse.Enabled = true;
                cboScreen.Enabled = true;
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            LoadScreens();
        }
    }
}
