using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace IPTVViewer
{
    public partial class SettingsForm : Form
    {
        private readonly AppSettings _settings;
        private ComboBox _cbDefaultSource;
        private TextBox _txtUrl1;
        private TextBox _txtUrl2;
        private CheckBox _chkAutoLoad;
        private CheckBox _chkExternal;
        private CheckBox _chkUpdates;
        private TextBox _txtRepo;
        private Button _btnOk;
        private Button _btnCancel;

        public SettingsForm(AppSettings settings)
        {
            _settings = settings;
            InitializeComponent();
            FillControls();
        }

        private void InitializeComponent()
        {
            this.Text = "Настройки";
            this.Size = new Size(680, 570);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Theme.Bg;
            this.ForeColor = Theme.Text;
            this.Font = Theme.FontRegular;
            this.DoubleBuffered = true;

            var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Theme.Panel };
            var title = Theme.CreateLabel("Настройки", false, 14f, true);
            title.Location = new Point(18, 16);
            header.Controls.Add(title);
            this.Controls.Add(header);

            var lblSource = Theme.CreateLabel("Источник по умолчанию", true, 9f, false);
            lblSource.Location = new Point(20, 76);
            _cbDefaultSource = new ComboBox { Location = new Point(20, 98), Size = new Size(620, 26) };
            Theme.StyleCombo(_cbDefaultSource);

            var lbl1 = Theme.CreateLabel("URL плейлиста 1 (IPTVPortal)", true, 9f, false);
            lbl1.Location = new Point(20, 136);
            _txtUrl1 = new TextBox { Location = new Point(20, 158), Size = new Size(620, 26) };
            Theme.StyleTextBox(_txtUrl1);

            var lbl2 = Theme.CreateLabel("URL плейлиста 2 (ISP-NL)", true, 9f, false);
            lbl2.Location = new Point(20, 196);
            _txtUrl2 = new TextBox { Location = new Point(20, 218), Size = new Size(620, 26) };
            Theme.StyleTextBox(_txtUrl2);

            _chkAutoLoad = new CheckBox
            {
                Text = "Автоматически загружать плейлист при запуске",
                Location = new Point(20, 262),
                Size = new Size(520, 24),
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            _chkExternal = new CheckBox
            {
                Text = "Открывать потоки во внешнем проигрывателе",
                Location = new Point(20, 292),
                Size = new Size(520, 24),
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };

            var lblRepo = Theme.CreateLabel("GitHub-репозиторий для обновлений (owner/repo)", true, 9f, false);
            lblRepo.Location = new Point(20, 330);
            _txtRepo = new TextBox { Location = new Point(20, 352), Size = new Size(620, 26) };
            Theme.StyleTextBox(_txtRepo);

            _chkUpdates = new CheckBox
            {
                Text = "Проверять обновления автоматически при запуске",
                Location = new Point(20, 392),
                Size = new Size(520, 24),
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };

            _btnOk = Theme.CreateButton("Сохранить", true);
            _btnOk.DialogResult = DialogResult.OK;
            _btnOk.Location = new Point(440, 478);
            _btnOk.Size = new Size(110, 36);

            _btnCancel = Theme.CreateButton("Отмена", false);
            _btnCancel.DialogResult = DialogResult.Cancel;
            _btnCancel.Location = new Point(558, 478);
            _btnCancel.Size = new Size(90, 36);

            this.Controls.Add(lblSource);
            this.Controls.Add(_cbDefaultSource);
            this.Controls.Add(lbl1);
            this.Controls.Add(_txtUrl1);
            this.Controls.Add(lbl2);
            this.Controls.Add(_txtUrl2);
            this.Controls.Add(_chkAutoLoad);
            this.Controls.Add(_chkExternal);
            this.Controls.Add(lblRepo);
            this.Controls.Add(_txtRepo);
            this.Controls.Add(_chkUpdates);
            this.Controls.Add(_btnOk);
            this.Controls.Add(_btnCancel);

            this.AcceptButton = _btnOk;
            this.CancelButton = _btnCancel;
        }

        private void FillControls()
        {
            _cbDefaultSource.Items.Add("IPTVPortal — newline.iptvportal.ru");
            _cbDefaultSource.Items.Add("ISP-NL — tv.isp-nl.ru/nltv.m3u");
            _cbDefaultSource.SelectedIndex = ClampIndex(_settings.SelectedSourceIndex, _cbDefaultSource.Items.Count);
            if (_settings.SourceUrls != null && _settings.SourceUrls.Count >= 2)
            {
                _txtUrl1.Text = _settings.SourceUrls[0];
                _txtUrl2.Text = _settings.SourceUrls[1];
            }
            _chkAutoLoad.Checked = _settings.AutoLoad;
            _chkExternal.Checked = _settings.UseExternalPlayer;
            _txtRepo.Text = UpdateChecker.NormalizeRepo(_settings.UpdateRepo);
            _chkUpdates.Checked = _settings.CheckUpdatesOnStartup;
        }

        private static int ClampIndex(int value, int count)
        {
            if (count <= 0) return 0;
            if (value < 0) return 0;
            if (value >= count) return count - 1;
            return value;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (this.DialogResult == DialogResult.OK)
            {
                _settings.SelectedSourceIndex = _cbDefaultSource.SelectedIndex;
                var urls = _settings.SourceUrls ?? new List<string>();
                while (urls.Count < 2) urls.Add("");
                urls[0] = _txtUrl1.Text.Trim();
                urls[1] = _txtUrl2.Text.Trim();
                _settings.SourceUrls = urls;
                _settings.AutoLoad = _chkAutoLoad.Checked;
                _settings.UseExternalPlayer = _chkExternal.Checked;
                _settings.UpdateRepo = UpdateChecker.NormalizeRepo(_txtRepo.Text);
                _settings.CheckUpdatesOnStartup = _chkUpdates.Checked;
                SettingsManager.Save(_settings);
            }
        }
    }
}
