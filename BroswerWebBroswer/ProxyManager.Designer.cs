using System.Drawing;
using System.Windows.Forms;

namespace WebBrowserApp
{
    partial class ProxySettingsUserControl
    {
        private System.ComponentModel.IContainer components = null;

        private Panel pnlHeader;
        private Label lblTitle;
        private Label lblSubtitle;
        private Panel pnlBody;
        private RadioButton rbDirect;
        private RadioButton rbSystemProxy;
        private RadioButton rbCustomProxy;
        private Label lblSystemProxyCaption;
        private Label lblSystemProxyValue;
        private ComboBox cmbProxyScheme;
        private TextBox txtProxyHost;
        private TextBox txtProxyPort;
        private Label lblCustomProxyHint;
        private Label lblModeHint;
        private Panel pnlFooter;
        private Button btnCancel;
        private Button btnSave;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.pnlHeader = new System.Windows.Forms.Panel();
            this.lblSubtitle = new System.Windows.Forms.Label();
            this.lblTitle = new System.Windows.Forms.Label();
            this.pnlBody = new System.Windows.Forms.Panel();
            this.lblModeHint = new System.Windows.Forms.Label();
            this.lblCustomProxyHint = new System.Windows.Forms.Label();
            this.txtProxyPort = new System.Windows.Forms.TextBox();
            this.txtProxyHost = new System.Windows.Forms.TextBox();
            this.cmbProxyScheme = new System.Windows.Forms.ComboBox();
            this.lblSystemProxyValue = new System.Windows.Forms.Label();
            this.lblSystemProxyCaption = new System.Windows.Forms.Label();
            this.rbCustomProxy = new System.Windows.Forms.RadioButton();
            this.rbSystemProxy = new System.Windows.Forms.RadioButton();
            this.rbDirect = new System.Windows.Forms.RadioButton();
            this.pnlFooter = new System.Windows.Forms.Panel();
            this.btnCancel = new System.Windows.Forms.Button();
            this.btnSave = new System.Windows.Forms.Button();
            this.pnlHeader.SuspendLayout();
            this.pnlBody.SuspendLayout();
            this.pnlFooter.SuspendLayout();
            this.SuspendLayout();
            // 
            // pnlHeader
            // 
            this.pnlHeader.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(248)))), ((int)(((byte)(250)))), ((int)(((byte)(252)))));
            this.pnlHeader.Controls.Add(this.lblSubtitle);
            this.pnlHeader.Controls.Add(this.lblTitle);
            this.pnlHeader.Dock = System.Windows.Forms.DockStyle.Top;
            this.pnlHeader.Location = new System.Drawing.Point(0, 0);
            this.pnlHeader.Name = "pnlHeader";
            this.pnlHeader.Padding = new System.Windows.Forms.Padding(18, 16, 18, 12);
            this.pnlHeader.Size = new System.Drawing.Size(420, 86);
            this.pnlHeader.TabIndex = 0;
            // 
            // lblSubtitle
            // 
            this.lblSubtitle.Dock = System.Windows.Forms.DockStyle.Top;
            this.lblSubtitle.Font = new System.Drawing.Font("Microsoft YaHei UI", 8.5F);
            this.lblSubtitle.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(107)))), ((int)(((byte)(114)))), ((int)(((byte)(128)))));
            this.lblSubtitle.Location = new System.Drawing.Point(18, 43);
            this.lblSubtitle.Name = "lblSubtitle";
            this.lblSubtitle.Size = new System.Drawing.Size(384, 26);
            this.lblSubtitle.TabIndex = 1;
            this.lblSubtitle.Text = "本地映射代理始终由浏览器自身启动，这里只配置它向外访问时的上游代理策略。";
            // 
            // lblTitle
            // 
            this.lblTitle.Dock = System.Windows.Forms.DockStyle.Top;
            this.lblTitle.Font = new System.Drawing.Font("Microsoft YaHei UI", 12F, System.Drawing.FontStyle.Bold);
            this.lblTitle.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(17)))), ((int)(((byte)(24)))), ((int)(((byte)(39)))));
            this.lblTitle.Location = new System.Drawing.Point(18, 16);
            this.lblTitle.Name = "lblTitle";
            this.lblTitle.Size = new System.Drawing.Size(384, 27);
            this.lblTitle.TabIndex = 0;
            this.lblTitle.Text = "代理设置";
            // 
            // pnlBody
            // 
            this.pnlBody.BackColor = System.Drawing.Color.White;
            this.pnlBody.Controls.Add(this.lblModeHint);
            this.pnlBody.Controls.Add(this.lblCustomProxyHint);
            this.pnlBody.Controls.Add(this.txtProxyPort);
            this.pnlBody.Controls.Add(this.txtProxyHost);
            this.pnlBody.Controls.Add(this.cmbProxyScheme);
            this.pnlBody.Controls.Add(this.lblSystemProxyValue);
            this.pnlBody.Controls.Add(this.lblSystemProxyCaption);
            this.pnlBody.Controls.Add(this.rbCustomProxy);
            this.pnlBody.Controls.Add(this.rbSystemProxy);
            this.pnlBody.Controls.Add(this.rbDirect);
            this.pnlBody.Dock = System.Windows.Forms.DockStyle.Fill;
            this.pnlBody.Location = new System.Drawing.Point(0, 86);
            this.pnlBody.Name = "pnlBody";
            this.pnlBody.Padding = new System.Windows.Forms.Padding(18);
            this.pnlBody.Size = new System.Drawing.Size(420, 250);
            this.pnlBody.TabIndex = 1;
            // 
            // lblModeHint
            // 
            this.lblModeHint.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(239)))), ((int)(((byte)(246)))), ((int)(((byte)(255)))));
            this.lblModeHint.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.lblModeHint.Font = new System.Drawing.Font("Microsoft YaHei UI", 8.5F);
            this.lblModeHint.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(30)))), ((int)(((byte)(64)))), ((int)(((byte)(175)))));
            this.lblModeHint.Location = new System.Drawing.Point(22, 184);
            this.lblModeHint.Name = "lblModeHint";
            this.lblModeHint.Padding = new System.Windows.Forms.Padding(12, 10, 12, 10);
            this.lblModeHint.Size = new System.Drawing.Size(376, 52);
            this.lblModeHint.TabIndex = 9;
            this.lblModeHint.Text = "当前模式：不使用上游代理，失败时直接访问目标地址。";
            // 
            // lblCustomProxyHint
            // 
            this.lblCustomProxyHint.Font = new System.Drawing.Font("Microsoft YaHei UI", 8.5F);
            this.lblCustomProxyHint.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(107)))), ((int)(((byte)(114)))), ((int)(((byte)(128)))));
            this.lblCustomProxyHint.Location = new System.Drawing.Point(40, 153);
            this.lblCustomProxyHint.Name = "lblCustomProxyHint";
            this.lblCustomProxyHint.Size = new System.Drawing.Size(358, 18);
            this.lblCustomProxyHint.TabIndex = 8;
            this.lblCustomProxyHint.Text = "协议目前只做结构化配置，核心仍按 host:port 使用。";
            // 
            // txtProxyPort
            // 
            this.txtProxyPort.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.txtProxyPort.Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5F);
            this.txtProxyPort.Location = new System.Drawing.Point(297, 122);
            this.txtProxyPort.Name = "txtProxyPort";
            this.txtProxyPort.Size = new System.Drawing.Size(101, 24);
            this.txtProxyPort.TabIndex = 7;
            // 
            // txtProxyHost
            // 
            this.txtProxyHost.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.txtProxyHost.Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5F);
            this.txtProxyHost.Location = new System.Drawing.Point(119, 122);
            this.txtProxyHost.Name = "txtProxyHost";
            this.txtProxyHost.Size = new System.Drawing.Size(168, 24);
            this.txtProxyHost.TabIndex = 6;
            // 
            // cmbProxyScheme
            // 
            this.cmbProxyScheme.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbProxyScheme.Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5F);
            this.cmbProxyScheme.FormattingEnabled = true;
            this.cmbProxyScheme.Items.AddRange(new object[] {
            "http",
            "https"});
            this.cmbProxyScheme.Location = new System.Drawing.Point(40, 121);
            this.cmbProxyScheme.Name = "cmbProxyScheme";
            this.cmbProxyScheme.Size = new System.Drawing.Size(69, 27);
            this.cmbProxyScheme.TabIndex = 5;
            // 
            // lblSystemProxyValue
            // 
            this.lblSystemProxyValue.Font = new System.Drawing.Font("Consolas", 8.5F);
            this.lblSystemProxyValue.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(75)))), ((int)(((byte)(85)))), ((int)(((byte)(99)))));
            this.lblSystemProxyValue.Location = new System.Drawing.Point(40, 96);
            this.lblSystemProxyValue.Name = "lblSystemProxyValue";
            this.lblSystemProxyValue.Size = new System.Drawing.Size(358, 20);
            this.lblSystemProxyValue.TabIndex = 4;
            this.lblSystemProxyValue.Text = "系统当前未启用代理";
            // 
            // lblSystemProxyCaption
            // 
            this.lblSystemProxyCaption.Font = new System.Drawing.Font("Microsoft YaHei UI", 8.5F);
            this.lblSystemProxyCaption.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(107)))), ((int)(((byte)(114)))), ((int)(((byte)(128)))));
            this.lblSystemProxyCaption.Location = new System.Drawing.Point(40, 76);
            this.lblSystemProxyCaption.Name = "lblSystemProxyCaption";
            this.lblSystemProxyCaption.Size = new System.Drawing.Size(358, 17);
            this.lblSystemProxyCaption.TabIndex = 3;
            this.lblSystemProxyCaption.Text = "系统原始代理快照";
            // 
            // rbCustomProxy
            // 
            this.rbCustomProxy.AutoSize = true;
            this.rbCustomProxy.Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5F);
            this.rbCustomProxy.Location = new System.Drawing.Point(22, 21);
            this.rbCustomProxy.Name = "rbCustomProxy";
            this.rbCustomProxy.Size = new System.Drawing.Size(106, 23);
            this.rbCustomProxy.TabIndex = 2;
            this.rbCustomProxy.TabStop = true;
            this.rbCustomProxy.Text = "自定义上游代理";
            this.rbCustomProxy.UseVisualStyleBackColor = true;
            this.rbCustomProxy.CheckedChanged += new System.EventHandler(this.ProxyMode_CheckedChanged);
            // 
            // rbSystemProxy
            // 
            this.rbSystemProxy.AutoSize = true;
            this.rbSystemProxy.Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5F);
            this.rbSystemProxy.Location = new System.Drawing.Point(154, 21);
            this.rbSystemProxy.Name = "rbSystemProxy";
            this.rbSystemProxy.Size = new System.Drawing.Size(132, 23);
            this.rbSystemProxy.TabIndex = 1;
            this.rbSystemProxy.TabStop = true;
            this.rbSystemProxy.Text = "使用系统代理作为上游";
            this.rbSystemProxy.UseVisualStyleBackColor = true;
            this.rbSystemProxy.CheckedChanged += new System.EventHandler(this.ProxyMode_CheckedChanged);
            // 
            // rbDirect
            // 
            this.rbDirect.AutoSize = true;
            this.rbDirect.Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5F);
            this.rbDirect.Location = new System.Drawing.Point(307, 21);
            this.rbDirect.Name = "rbDirect";
            this.rbDirect.Size = new System.Drawing.Size(80, 23);
            this.rbDirect.TabIndex = 0;
            this.rbDirect.TabStop = true;
            this.rbDirect.Text = "不使用代理";
            this.rbDirect.UseVisualStyleBackColor = true;
            this.rbDirect.CheckedChanged += new System.EventHandler(this.ProxyMode_CheckedChanged);
            // 
            // pnlFooter
            // 
            this.pnlFooter.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(248)))), ((int)(((byte)(250)))), ((int)(((byte)(252)))));
            this.pnlFooter.Controls.Add(this.btnCancel);
            this.pnlFooter.Controls.Add(this.btnSave);
            this.pnlFooter.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlFooter.Location = new System.Drawing.Point(0, 336);
            this.pnlFooter.Name = "pnlFooter";
            this.pnlFooter.Padding = new System.Windows.Forms.Padding(18, 12, 18, 12);
            this.pnlFooter.Size = new System.Drawing.Size(420, 64);
            this.pnlFooter.TabIndex = 2;
            // 
            // btnCancel
            // 
            this.btnCancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancel.BackColor = System.Drawing.Color.White;
            this.btnCancel.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(209)))), ((int)(((byte)(213)))), ((int)(((byte)(219)))));
            this.btnCancel.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnCancel.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.btnCancel.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(55)))), ((int)(((byte)(65)))), ((int)(((byte)(81)))));
            this.btnCancel.Location = new System.Drawing.Point(224, 12);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(84, 34);
            this.btnCancel.TabIndex = 0;
            this.btnCancel.Text = "取消";
            this.btnCancel.UseVisualStyleBackColor = false;
            this.btnCancel.Click += new System.EventHandler(this.BtnCancel_Click);
            // 
            // btnSave
            // 
            this.btnSave.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnSave.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(37)))), ((int)(((byte)(99)))), ((int)(((byte)(235)))));
            this.btnSave.FlatAppearance.BorderSize = 0;
            this.btnSave.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnSave.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold);
            this.btnSave.ForeColor = System.Drawing.Color.White;
            this.btnSave.Location = new System.Drawing.Point(314, 12);
            this.btnSave.Name = "btnSave";
            this.btnSave.Size = new System.Drawing.Size(84, 34);
            this.btnSave.TabIndex = 1;
            this.btnSave.Text = "应用";
            this.btnSave.UseVisualStyleBackColor = false;
            this.btnSave.Click += new System.EventHandler(this.BtnSave_Click);
            // 
            // ProxySettingsUserControl
            // 
            this.AcceptButton = this.btnSave;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.White;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new System.Drawing.Size(420, 400);
            this.ControlBox = true;
            this.Controls.Add(this.pnlBody);
            this.Controls.Add(this.pnlFooter);
            this.Controls.Add(this.pnlHeader);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.SizableToolWindow;
            this.MaximizeBox = false;
            this.MaximumSize = new System.Drawing.Size(680, 520);
            this.MinimizeBox = false;
            this.MinimumSize = new System.Drawing.Size(420, 400);
            this.Name = "ProxySettingsUserControl";
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            this.Text = "代理设置";
            this.pnlHeader.ResumeLayout(false);
            this.pnlBody.ResumeLayout(false);
            this.pnlBody.PerformLayout();
            this.pnlFooter.ResumeLayout(false);
            this.ResumeLayout(false);

        }
    }
}
