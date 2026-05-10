using System.Drawing;
using System.Windows.Forms;

namespace WebBrowserApp
{
    partial class Browser
    {
        private System.ComponentModel.IContainer components = null;

        private Panel pnlTopBar;
        private Button btnGo;
        private TextBox txtUrl;
        private Button btnHome;
        private Button btnRefresh;
        private Panel pnlRightTools;
        private Button btnProxyTool;
        private Button btnCookieTool;
        private Button btnFlashFullscreen;
        private WebBrowser webBrowser;
        private Panel pnlStatusBar;
        private Label lblStatus;

        private void InitializeComponent()
        {
            this.pnlTopBar = new System.Windows.Forms.Panel();
            this.btnGo = new System.Windows.Forms.Button();
            this.txtUrl = new System.Windows.Forms.TextBox();
            this.btnHome = new System.Windows.Forms.Button();
            this.btnRefresh = new System.Windows.Forms.Button();
            this.pnlRightTools = new System.Windows.Forms.Panel();
            this.btnFlashFullscreen = new System.Windows.Forms.Button();
            this.btnCookieTool = new System.Windows.Forms.Button();
            this.btnProxyTool = new System.Windows.Forms.Button();
            this.webBrowser = new System.Windows.Forms.WebBrowser();
            this.pnlStatusBar = new System.Windows.Forms.Panel();
            this.lblStatus = new System.Windows.Forms.Label();
            this.pnlTopBar.SuspendLayout();
            this.pnlRightTools.SuspendLayout();
            this.pnlStatusBar.SuspendLayout();
            this.SuspendLayout();
            // 
            // pnlTopBar
            // 
            this.pnlTopBar.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(248)))), ((int)(((byte)(250)))), ((int)(((byte)(252)))));
            this.pnlTopBar.Controls.Add(this.btnGo);
            this.pnlTopBar.Controls.Add(this.txtUrl);
            this.pnlTopBar.Controls.Add(this.btnHome);
            this.pnlTopBar.Controls.Add(this.btnRefresh);
            this.pnlTopBar.Dock = System.Windows.Forms.DockStyle.Top;
            this.pnlTopBar.Location = new System.Drawing.Point(0, 0);
            this.pnlTopBar.Name = "pnlTopBar";
            this.pnlTopBar.Padding = new System.Windows.Forms.Padding(12, 10, 12, 10);
            this.pnlTopBar.Size = new System.Drawing.Size(1160, 56);
            this.pnlTopBar.TabIndex = 0;
            // 
            // btnGo
            // 
            this.btnGo.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnGo.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(37)))), ((int)(((byte)(99)))), ((int)(((byte)(235)))));
            this.btnGo.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnGo.FlatAppearance.BorderSize = 0;
            this.btnGo.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(29)))), ((int)(((byte)(78)))), ((int)(((byte)(216)))));
            this.btnGo.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(59)))), ((int)(((byte)(130)))), ((int)(((byte)(246)))));
            this.btnGo.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnGo.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold);
            this.btnGo.ForeColor = System.Drawing.Color.White;
            this.btnGo.Location = new System.Drawing.Point(977, 10);
            this.btnGo.Name = "btnGo";
            this.btnGo.Size = new System.Drawing.Size(82, 34);
            this.btnGo.TabIndex = 3;
            this.btnGo.Text = "前往";
            this.btnGo.UseVisualStyleBackColor = false;
            this.btnGo.Click += new System.EventHandler(this.BtnGo_Click);
            // 
            // txtUrl
            // 
            this.txtUrl.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtUrl.BackColor = System.Drawing.Color.White;
            this.txtUrl.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.txtUrl.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.txtUrl.Location = new System.Drawing.Point(181, 13);
            this.txtUrl.Name = "txtUrl";
            this.txtUrl.Size = new System.Drawing.Size(781, 25);
            this.txtUrl.TabIndex = 2;
            this.txtUrl.KeyDown += new System.Windows.Forms.KeyEventHandler(this.TxtUrl_KeyDown);
            // 
            // btnHome
            // 
            this.btnHome.BackColor = System.Drawing.Color.White;
            this.btnHome.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnHome.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(209)))), ((int)(((byte)(213)))), ((int)(((byte)(219)))));
            this.btnHome.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(243)))), ((int)(((byte)(244)))), ((int)(((byte)(246)))));
            this.btnHome.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnHome.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.btnHome.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(55)))), ((int)(((byte)(65)))), ((int)(((byte)(81)))));
            this.btnHome.Location = new System.Drawing.Point(96, 10);
            this.btnHome.Name = "btnHome";
            this.btnHome.Size = new System.Drawing.Size(72, 34);
            this.btnHome.TabIndex = 1;
            this.btnHome.Text = "主页";
            this.btnHome.UseVisualStyleBackColor = false;
            this.btnHome.Click += new System.EventHandler(this.BtnHome_Click);
            // 
            // btnRefresh
            // 
            this.btnRefresh.BackColor = System.Drawing.Color.White;
            this.btnRefresh.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnRefresh.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(209)))), ((int)(((byte)(213)))), ((int)(((byte)(219)))));
            this.btnRefresh.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(243)))), ((int)(((byte)(244)))), ((int)(((byte)(246)))));
            this.btnRefresh.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnRefresh.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.btnRefresh.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(55)))), ((int)(((byte)(65)))), ((int)(((byte)(81)))));
            this.btnRefresh.Location = new System.Drawing.Point(12, 10);
            this.btnRefresh.Name = "btnRefresh";
            this.btnRefresh.Size = new System.Drawing.Size(72, 34);
            this.btnRefresh.TabIndex = 0;
            this.btnRefresh.Text = "刷新";
            this.btnRefresh.UseVisualStyleBackColor = false;
            this.btnRefresh.Click += new System.EventHandler(this.BtnRefresh_Click);
            // 
            // pnlRightTools
            // 
            this.pnlRightTools.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(243)))), ((int)(((byte)(244)))), ((int)(((byte)(246)))));
            this.pnlRightTools.Controls.Add(this.btnFlashFullscreen);
            this.pnlRightTools.Controls.Add(this.btnCookieTool);
            this.pnlRightTools.Controls.Add(this.btnProxyTool);
            this.pnlRightTools.Dock = System.Windows.Forms.DockStyle.Right;
            this.pnlRightTools.Location = new System.Drawing.Point(1068, 56);
            this.pnlRightTools.Name = "pnlRightTools";
            this.pnlRightTools.Padding = new System.Windows.Forms.Padding(10, 14, 10, 14);
            this.pnlRightTools.Size = new System.Drawing.Size(92, 575);
            this.pnlRightTools.TabIndex = 1;
            // 
            // btnFlashFullscreen
            // 
            this.btnFlashFullscreen.BackColor = System.Drawing.Color.White;
            this.btnFlashFullscreen.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnFlashFullscreen.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(229)))), ((int)(((byte)(231)))), ((int)(((byte)(235)))));
            this.btnFlashFullscreen.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(239)))), ((int)(((byte)(246)))), ((int)(((byte)(255)))));
            this.btnFlashFullscreen.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnFlashFullscreen.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.btnFlashFullscreen.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(31)))), ((int)(((byte)(41)))), ((int)(((byte)(55)))));
            this.btnFlashFullscreen.Location = new System.Drawing.Point(10, 162);
            this.btnFlashFullscreen.Name = "btnFlashFullscreen";
            this.btnFlashFullscreen.Size = new System.Drawing.Size(72, 62);
            this.btnFlashFullscreen.TabIndex = 2;
            this.btnFlashFullscreen.Text = "Flash\r\n全屏";
            this.btnFlashFullscreen.UseVisualStyleBackColor = false;
            this.btnFlashFullscreen.Click += new System.EventHandler(this.BtnFlashFullscreen_Click);
            // 
            // btnCookieTool
            // 
            this.btnCookieTool.BackColor = System.Drawing.Color.White;
            this.btnCookieTool.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnCookieTool.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(229)))), ((int)(((byte)(231)))), ((int)(((byte)(235)))));
            this.btnCookieTool.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(239)))), ((int)(((byte)(246)))), ((int)(((byte)(255)))));
            this.btnCookieTool.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnCookieTool.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.btnCookieTool.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(31)))), ((int)(((byte)(41)))), ((int)(((byte)(55)))));
            this.btnCookieTool.Location = new System.Drawing.Point(10, 88);
            this.btnCookieTool.Name = "btnCookieTool";
            this.btnCookieTool.Size = new System.Drawing.Size(72, 62);
            this.btnCookieTool.TabIndex = 1;
            this.btnCookieTool.Text = "Cookie\r\n设置";
            this.btnCookieTool.UseVisualStyleBackColor = false;
            this.btnCookieTool.Click += new System.EventHandler(this.BtnCookieTool_Click);
            // 
            // btnProxyTool
            // 
            this.btnProxyTool.BackColor = System.Drawing.Color.White;
            this.btnProxyTool.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnProxyTool.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(229)))), ((int)(((byte)(231)))), ((int)(((byte)(235)))));
            this.btnProxyTool.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(239)))), ((int)(((byte)(246)))), ((int)(((byte)(255)))));
            this.btnProxyTool.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnProxyTool.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.btnProxyTool.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(31)))), ((int)(((byte)(41)))), ((int)(((byte)(55)))));
            this.btnProxyTool.Location = new System.Drawing.Point(10, 14);
            this.btnProxyTool.Name = "btnProxyTool";
            this.btnProxyTool.Size = new System.Drawing.Size(72, 62);
            this.btnProxyTool.TabIndex = 0;
            this.btnProxyTool.Text = "代理\r\n设置";
            this.btnProxyTool.UseVisualStyleBackColor = false;
            this.btnProxyTool.Click += new System.EventHandler(this.BtnProxyTool_Click);
            // 
            // webBrowser
            // 
            this.webBrowser.Dock = System.Windows.Forms.DockStyle.Fill;
            this.webBrowser.Location = new System.Drawing.Point(0, 56);
            this.webBrowser.MinimumSize = new System.Drawing.Size(20, 18);
            this.webBrowser.Name = "webBrowser";
            this.webBrowser.Size = new System.Drawing.Size(1068, 575);
            this.webBrowser.TabIndex = 2;
            this.webBrowser.Navigated += new System.Windows.Forms.WebBrowserNavigatedEventHandler(this.WebBrowser_Navigated);
            // 
            // pnlStatusBar
            // 
            this.pnlStatusBar.BackColor = System.Drawing.Color.White;
            this.pnlStatusBar.Controls.Add(this.lblStatus);
            this.pnlStatusBar.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlStatusBar.Location = new System.Drawing.Point(0, 631);
            this.pnlStatusBar.Name = "pnlStatusBar";
            this.pnlStatusBar.Padding = new System.Windows.Forms.Padding(12, 6, 12, 6);
            this.pnlStatusBar.Size = new System.Drawing.Size(1160, 33);
            this.pnlStatusBar.TabIndex = 3;
            // 
            // lblStatus
            // 
            this.lblStatus.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblStatus.Font = new System.Drawing.Font("Microsoft YaHei UI", 8.5F);
            this.lblStatus.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(75)))), ((int)(((byte)(85)))), ((int)(((byte)(99)))));
            this.lblStatus.Location = new System.Drawing.Point(12, 6);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(1136, 21);
            this.lblStatus.TabIndex = 0;
            this.lblStatus.Text = "就绪";
            this.lblStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // Browser
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.White;
            this.ClientSize = new System.Drawing.Size(1160, 664);
            this.Controls.Add(this.webBrowser);
            this.Controls.Add(this.pnlRightTools);
            this.Controls.Add(this.pnlTopBar);
            this.Controls.Add(this.pnlStatusBar);
            this.MinimumSize = new System.Drawing.Size(1000, 640);
            this.Name = "Browser";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "PVZOL 浏览器";
            this.pnlTopBar.ResumeLayout(false);
            this.pnlTopBar.PerformLayout();
            this.pnlRightTools.ResumeLayout(false);
            this.pnlStatusBar.ResumeLayout(false);
            this.ResumeLayout(false);

        }
    }
}
