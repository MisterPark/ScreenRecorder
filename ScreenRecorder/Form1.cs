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
        private ScreenRecorder _recorder;
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
                sfd.Filter = "MP4 files (*.mp4)|*.mp4";
                sfd.FileName = Path.GetFileName(txtPath.Text);
                if (sfd.ShowDialog(this) == DialogResult.OK)
                    txtPath.Text = sfd.FileName;
            }
        }

        private async void btnStart_Click(object sender, EventArgs e)
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
                _recorder = new ScreenRecorder(path, bounds.Size, fps, quality, includeCursor, bounds.Location);
                await _recorder.StartAsync();

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

        private async void btnStop_Click(object sender, EventArgs e)
        {
            btnStop.Enabled = false;
            try
            {
                if (_recorder != null)
                    await _recorder.StopAsync();

                MessageBox.Show("저장 완료: " + txtPath.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show("녹화 종료 중 오류: " + ex.Message);
            }
            finally
            {
                _recorder = null;
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
