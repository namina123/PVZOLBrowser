using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WebBrowserApp
{
    internal sealed class ServerJumpPanel : Panel
    {
        private readonly Action<int> _jumpAction;
        private readonly Action _closeAction;
        private readonly Action<int> _toggleFavoriteAction;
        private readonly FlowLayoutPanel _buttonPanel;
        private readonly Label _subtitleLabel;
        private const int PanelWidth = 220;

        internal ServerJumpPanel(Action<int> jumpAction, Action<int> toggleFavoriteAction, Action closeAction)
        {
            _jumpAction = jumpAction ?? throw new ArgumentNullException(nameof(jumpAction));
            _toggleFavoriteAction = toggleFavoriteAction ?? throw new ArgumentNullException(nameof(toggleFavoriteAction));
            _closeAction = closeAction ?? throw new ArgumentNullException(nameof(closeAction));

            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;
            Padding = new Padding(14);
            Visible = false;

            var headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 68
            };
            Controls.Add(headerPanel);

            var titleLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
                ForeColor = Color.FromArgb(17, 24, 39),
                Text = "区服跳转",
                TextAlign = ContentAlignment.MiddleLeft
            };
            headerPanel.Controls.Add(titleLabel);

            _subtitleLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 8.8F),
                ForeColor = Color.FromArgb(107, 114, 128),
                Text = "自动读取本地顺序，可拖动窗口，也可直接关闭。",
                TextAlign = ContentAlignment.TopLeft
            };
            headerPanel.Controls.Add(_subtitleLabel);

            var closeButton = new Button
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BackColor = Color.FromArgb(243, 244, 246),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(55, 65, 81),
                Location = new Point(188, 0),
                Size = new Size(34, 30),
                Text = "×",
                UseVisualStyleBackColor = false
            };
            closeButton.FlatAppearance.BorderColor = Color.FromArgb(229, 231, 235);
            closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(229, 231, 235);
            closeButton.Click += (s, e) => _closeAction();
            headerPanel.Controls.Add(closeButton);

            _buttonPanel = new FlowLayoutPanel
            {
                AutoScroll = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(0, 6, 2, 0),
                WrapContents = false
            };
            Controls.Add(_buttonPanel);
            _buttonPanel.BringToFront();
        }

        internal void SetOrderFilePath(string filePath)
        {
            _subtitleLabel.Text = "收藏区会自动前置，窗口位置本次拖动有效，关闭后不记忆。";
        }

        internal void SetZones(IEnumerable<int> zones, ISet<int> favorites)
        {
            _buttonPanel.SuspendLayout();
            _buttonPanel.Controls.Clear();

            foreach (int zone in (zones ?? Enumerable.Empty<int>()))
            {
                Control buttonCard = BuildZoneButton(zone, favorites != null && favorites.Contains(zone));
                _buttonPanel.Controls.Add(buttonCard);
            }

            _buttonPanel.ResumeLayout();
        }

        private Control BuildZoneButton(int zone, bool isFavorite)
        {
            var card = new Panel
            {
                BackColor = Color.FromArgb(239, 246, 255),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 0, 8),
                Padding = new Padding(12, 8, 8, 8),
                Size = new Size(PanelWidth, 64),
                Tag = zone
            };
            card.Paint += (s, e) =>
            {
                using (var borderPen = new Pen(Color.FromArgb(191, 219, 254)))
                {
                    Rectangle rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
                    e.Graphics.DrawRectangle(borderPen, rect);
                }
            };

            var numberLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Left,
                Width = 54,
                Font = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 64, 175),
                Text = zone.ToString(),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };

            var textLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 41, 59),
                Text = "跳转到该区",
                TextAlign = ContentAlignment.MiddleLeft,
                Cursor = Cursors.Hand
            };

            var favoriteButton = new Button
            {
                BackColor = Color.White,
                Cursor = Cursors.Hand,
                Dock = DockStyle.Right,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 8.2F, FontStyle.Bold),
                ForeColor = isFavorite ? Color.FromArgb(217, 119, 6) : Color.FromArgb(156, 163, 175),
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Size = new Size(22, 22),
                Text = isFavorite ? "★" : "☆",
                UseVisualStyleBackColor = false
            };
            favoriteButton.FlatAppearance.BorderColor = Color.FromArgb(191, 219, 254);
            favoriteButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(254, 243, 199);
            favoriteButton.Click += (s, e) =>
            {
                _toggleFavoriteAction(zone);
            };

            EventHandler clickHandler = (s, e) => _jumpAction(zone);
            card.Click += clickHandler;
            numberLabel.Click += clickHandler;
            textLabel.Click += clickHandler;

            EventHandler hoverHandler = (s, e) => card.BackColor = Color.FromArgb(219, 234, 254);
            EventHandler leaveHandler = (s, e) => card.BackColor = Color.FromArgb(239, 246, 255);
            card.MouseEnter += hoverHandler;
            card.MouseLeave += leaveHandler;
            numberLabel.MouseEnter += hoverHandler;
            numberLabel.MouseLeave += leaveHandler;
            textLabel.MouseEnter += hoverHandler;
            textLabel.MouseLeave += leaveHandler;

            card.Controls.Add(favoriteButton);
            card.Controls.Add(textLabel);
            card.Controls.Add(numberLabel);
            return card;
        }
    }
}
