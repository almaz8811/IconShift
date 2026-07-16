using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IconShift;

public class MainForm : Form
{
    private ComboBox comboBox1 = null!;
    private ComboBox comboBox2 = null!;
    private ComboBox comboBox3 = null!;
    private Button btnOpenFolder1 = null!;
    private Button btnOpenFolder2 = null!;
    private Button btnOpenFolder3 = null!;
    private Label lblCount1 = null!;
    private Label lblCount2 = null!;
    private Label lblCount3 = null!;
    private ListView listView1 = null!;
    private ImageList imageList1 = null!;
    private ListView listView2 = null!;
    private ImageList imageList2 = null!;
    private ListView listView3 = null!;
    private ImageList imageList3 = null!;
    private FileSystemWatcher? desktopWatcher;
    private FileSystemWatcher? commonDesktopWatcher;
    private FileSystemWatcher? thirdDesktopWatcher;
    private LayeredDragWindow? dragOverlayForm;
    private System.Windows.Forms.Timer dragOverlayTimer = null!;
    private System.Windows.Forms.Timer refreshDebounceTimer1 = null!;
    private System.Windows.Forms.Timer refreshDebounceTimer2 = null!;
    private System.Windows.Forms.Timer refreshDebounceTimer3 = null!;
    private Bitmap? dragOverlayBitmap;
    private Point dragOverlayHotspot;
    private ListView? dragSourceListView;
    private ListView? dropHighlightListView;
    private Color dropHighlightOriginalBackColor;
    private bool suppressComboLoad;
    private string? lastPath1;
    private string? lastPath2;
    private string? lastPath3;

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref POINT pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref POINT pptSrc,
        int crKey,
        ref BLENDFUNCTION pblend,
        int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHDRAGIMAGE
    {
        public SIZE sizeDragImage;
        public POINT ptOffset;
        public IntPtr hbmpDragImage;
        public int crColorKey;
    }

    [ComImport]
    [Guid("DE5BF786-477A-11d2-839D-00C04FD918D0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDragSourceHelper
    {
        void InitializeFromBitmap(ref SHDRAGIMAGE pshdi, [MarshalAs(UnmanagedType.Interface)] object pDataObject);
        void InitializeFromWindow(IntPtr hwnd, ref POINT ppt, [MarshalAs(UnmanagedType.Interface)] object pDataObject);
    }

    [ComImport]
    [Guid("4657278A-411B-11d2-839A-00C04FD918D0")]
    [ClassInterface(ClassInterfaceType.None)]
    private class DragDropHelper
    {
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    /// <summary>
    /// Topmost layered window with per-pixel alpha — Explorer-style drag ghost.
    /// Caches the HBITMAP so the ghost can track the cursor at ~60 FPS cheaply.
    /// </summary>
    private sealed class LayeredDragWindow : Form
    {
        private IntPtr cachedHBitmap = IntPtr.Zero;
        private int bitmapWidth;
        private int bitmapHeight;
        private byte currentOpacity = 255;

        public LayeredDragWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowIcon = false;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void SetBitmap(Bitmap bitmap, byte opacity = 255)
        {
            FreeCachedBitmap();
            cachedHBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            bitmapWidth = bitmap.Width;
            bitmapHeight = bitmap.Height;
            currentOpacity = opacity;
            Size = new Size(bitmapWidth, bitmapHeight);
            Present(Left, Top);
        }

        public void MoveTo(int x, int y)
        {
            if (cachedHBitmap == IntPtr.Zero)
            {
                Location = new Point(x, y);
                return;
            }

            Present(x, y);
        }

        private void Present(int x, int y)
        {
            if (cachedHBitmap == IntPtr.Zero)
                return;

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr oldBitmap = SelectObject(memDc, cachedHBitmap);

            try
            {
                SIZE size = new SIZE { cx = bitmapWidth, cy = bitmapHeight };
                POINT pointSource = new POINT { x = 0, y = 0 };
                POINT topPos = new POINT { x = x, y = y };
                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = currentOpacity,
                    AlphaFormat = AC_SRC_ALPHA
                };

                UpdateLayeredWindow(
                    Handle,
                    screenDc,
                    ref topPos,
                    ref size,
                    memDc,
                    ref pointSource,
                    0,
                    ref blend,
                    ULW_ALPHA);

                Location = new Point(x, y);
            }
            finally
            {
                SelectObject(memDc, oldBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void FreeCachedBitmap()
        {
            if (cachedHBitmap != IntPtr.Zero)
            {
                DeleteObject(cachedHBitmap);
                cachedHBitmap = IntPtr.Zero;
            }
        }

        protected override void Dispose(bool disposing)
        {
            FreeCachedBitmap();
            base.Dispose(disposing);
        }
    }

    private static Icon? GetSystemIcon(string path, bool isDirectory)
    {
        var shinfo = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES;
        uint attributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        IntPtr result = SHGetFileInfo(path, attributes, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);

        if (result != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
        {
            Icon icon = (Icon)Icon.FromHandle(shinfo.hIcon).Clone();
            DestroyIcon(shinfo.hIcon);
            return icon;
        }

        return null;
    }

    public MainForm()
    {
        InitializeComponent();
        InitializeDragDropOverlay();
        InitializeRefreshDebounceTimers();
        InitializeFileSystemWatchers();
        LoadDesktopFiles();
        LoadCommonDesktopFiles();
        LoadThirdDesktopFiles();
    }

    private void InitializeDragDropOverlay()
    {
        dragOverlayTimer = new System.Windows.Forms.Timer();
        dragOverlayTimer.Interval = 16; // ~60 FPS cursor tracking
        dragOverlayTimer.Tick += DragOverlayTimer_Tick;
    }

    private void InitializeRefreshDebounceTimers()
    {
        refreshDebounceTimer1 = CreateDebounceTimer(() => RefreshListView(listView1, GetDesktopPathForListView(listView1)));
        refreshDebounceTimer2 = CreateDebounceTimer(() => RefreshListView(listView2, GetDesktopPathForListView(listView2)));
        refreshDebounceTimer3 = CreateDebounceTimer(() => RefreshListView(listView3, GetDesktopPathForListView(listView3)));
    }

    private static System.Windows.Forms.Timer CreateDebounceTimer(Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    private void DragOverlayTimer_Tick(object? sender, EventArgs e)
    {
        if (dragOverlayForm != null && dragOverlayForm.Visible)
            UpdateDragOverlayPosition();
    }

    /// <summary>
    /// Builds an Explorer-like drag image: stacked icons, count badge, caption for a single item.
    /// Caption uses the same Font/ForeColor/GDI rendering as the ListView item label.
    /// </summary>
    private static Bitmap CreateExplorerStyleDragImage(
        ListView sourceListView,
        ListViewItem[] items,
        out Point hotspot)
    {
        // Match ListView LargeIcon image size (no upscale → cleaner look).
        int iconSize = sourceListView.LargeImageList?.ImageSize.Width ?? 32;
        if (iconSize < 16) iconSize = 32;

        const int stackOffset = 8;
        const int margin = 10;
        const int maxStack = 3;

        // Same flags ListView uses for LargeIcon captions (GDI TextRenderer path).
        const TextFormatFlags labelFlags =
            TextFormatFlags.HorizontalCenter
            | TextFormatFlags.Top
            | TextFormatFlags.WordBreak
            | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPrefix
            | TextFormatFlags.TextBoxControl
            | TextFormatFlags.NoPadding;

        int count = items.Length;
        int stackCount = Math.Min(count, maxStack);
        int stackExtra = (stackCount - 1) * stackOffset;

        // Exact same typeface as the shortcut caption in the ListView.
        using Font font = (Font)sourceListView.Font.Clone();
        Color textColor = sourceListView.ForeColor;
        // Fully opaque background required for ClearType (same as ListView solid back color).
        Color labelBackColor = sourceListView.BackColor.A == 255
            ? sourceListView.BackColor
            : Color.White;

        bool showLabel = count == 1;
        string label = showLabel ? items[0].Text : string.Empty;

        // Width of the caption area under a LargeIcon item (Bounds include icon+text column).
        int maxLabelWidth = 100;
        if (showLabel)
        {
            int itemW = items[0].Bounds.Width;
            maxLabelWidth = itemW > iconSize ? itemW : Math.Max(iconSize + 24, 80);
        }

        Size labelSize = showLabel
            ? TextRenderer.MeasureText(label, font, new Size(maxLabelWidth, int.MaxValue), labelFlags)
            : Size.Empty;

        int contentWidth = iconSize + stackExtra;
        int contentHeight = iconSize + stackExtra;
        int labelPadX = 4;
        int labelPadY = 2;
        int labelBlockWidth = showLabel ? Math.Min(Math.Max(labelSize.Width + labelPadX * 2, maxLabelWidth), maxLabelWidth + labelPadX * 2) : 0;
        int labelBlockHeight = showLabel ? labelSize.Height + labelPadY * 2 : 0;
        int gap = showLabel ? 4 : 0;

        int width = Math.Max(contentWidth, labelBlockWidth) + margin * 2 + 12;
        int height = contentHeight + gap + labelBlockHeight + margin * 2 + 12;

        if (count > 1)
        {
            width = Math.Max(width, contentWidth + margin * 2 + 28);
            height = Math.Max(height, contentHeight + margin * 2 + 20);
        }

        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            // No shadows / semi-transparent plates — they spoil ClearType and make text look dirty.
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.None;
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int stackLeft = margin + (width - margin * 2 - contentWidth) / 2;
            int stackTop = margin;

            // Icons only (no plates, no dimming, no shadows).
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            for (int i = stackCount - 1; i >= 0; i--)
            {
                ListViewItem stackItem = i < items.Length ? items[i] : items[^1];

                Image? iconImage = null;
                if (sourceListView.LargeImageList != null &&
                    stackItem.ImageIndex >= 0 &&
                    stackItem.ImageIndex < sourceListView.LargeImageList.Images.Count)
                {
                    iconImage = sourceListView.LargeImageList.Images[stackItem.ImageIndex];
                }

                if (iconImage == null)
                    continue;

                int x = stackLeft + i * stackOffset;
                int y = stackTop + i * stackOffset;
                g.DrawImage(iconImage, new Rectangle(x, y, iconSize, iconSize));
            }

            if (count > 1)
            {
                string badgeText = count > 99 ? "99+" : count.ToString();
                Size badgeTextSize = TextRenderer.MeasureText(badgeText, font);
                int badgeW = Math.Max(20, badgeTextSize.Width);
                int badgeH = Math.Max(18, badgeTextSize.Height);
                int badgeX = Math.Min(stackLeft + contentWidth - badgeW / 2, width - badgeW - 4);
                int badgeY = Math.Max(stackTop - 4, 2);

                var badgeRect = new Rectangle(badgeX, badgeY, badgeW, badgeH);
                using (var badgeBrush = new SolidBrush(Color.FromArgb(255, 0, 120, 215)))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.FillEllipse(badgeBrush, badgeRect);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                }

                TextRenderer.DrawText(
                    g,
                    badgeText,
                    font,
                    badgeRect,
                    Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }

            // Caption: opaque ListView-colored plate + GDI text (identical to item label).
            if (showLabel)
            {
                int labelW = labelBlockWidth;
                int labelH = labelBlockHeight;
                int labelX = (width - labelW) / 2;
                int labelY = stackTop + contentHeight + gap;

                var labelRect = new Rectangle(labelX, labelY, labelW, labelH);

                // Fully opaque solid fill — required for clean ClearType.
                using (var labelBg = new SolidBrush(Color.FromArgb(255, labelBackColor)))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.FillRectangle(labelBg, labelRect);
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                }

                var textRect = new Rectangle(
                    labelRect.X + labelPadX,
                    labelRect.Y + labelPadY,
                    labelRect.Width - labelPadX * 2,
                    labelRect.Height - labelPadY * 2);

                TextRenderer.DrawText(g, label, font, textRect, textColor, labelBackColor, labelFlags);
            }
        }

        hotspot = new Point(margin + 8, margin + 8);
        return bitmap;
    }

    private void StartDragOverlay(ListView sourceListView, ListViewItem[] items, DataObject dataObject)
    {
        if (items.Length == 0)
            return;

        EndDragOverlay();

        dragOverlayBitmap = CreateExplorerStyleDragImage(sourceListView, items, out dragOverlayHotspot);

        // Also register with the shell so drops into Explorer get a native drag image when possible.
        TryInitializeShellDragImage(dataObject, dragOverlayBitmap, dragOverlayHotspot);

        dragOverlayForm = new LayeredDragWindow();
        // Create handle before SetBitmap so UpdateLayeredWindow works.
        _ = dragOverlayForm.Handle;
        // Full opacity so ClearType glyphs stay sharp (no extra alpha blend on the ghost).
        dragOverlayForm.SetBitmap(dragOverlayBitmap, 255);
        dragOverlayForm.Show();
        UpdateDragOverlayPosition();
        dragOverlayTimer.Start();
    }

    private void UpdateDragOverlayPosition()
    {
        if (dragOverlayForm == null)
            return;

        Point cursorPos = Cursor.Position;
        int x = cursorPos.X - dragOverlayHotspot.X;
        int y = cursorPos.Y - dragOverlayHotspot.Y;
        dragOverlayForm.MoveTo(x, y);
    }

    private void EndDragOverlay()
    {
        dragOverlayTimer.Stop();

        if (dragOverlayForm != null)
        {
            dragOverlayForm.Hide();
            dragOverlayForm.Dispose();
            dragOverlayForm = null;
        }

        dragOverlayBitmap?.Dispose();
        dragOverlayBitmap = null;
    }

    private bool TryInitializeShellDragImage(DataObject dataObject, Bitmap dragBitmap, Point hotspot)
    {
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            var helper = (IDragSourceHelper)new DragDropHelper();
            // Shell takes ownership of a copy; we still free our HBITMAP after call when it fails,
            // but InitializeFromBitmap typically copies — free in finally either way.
            hBitmap = dragBitmap.GetHbitmap(Color.FromArgb(0));
            var shdi = new SHDRAGIMAGE
            {
                sizeDragImage = new SIZE { cx = dragBitmap.Width, cy = dragBitmap.Height },
                ptOffset = new POINT { x = hotspot.X, y = hotspot.Y },
                hbmpDragImage = hBitmap,
                crColorKey = unchecked((int)0xFFFFFFFF)
            };

            helper.InitializeFromBitmap(ref shdi, dataObject);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
        }
    }

    private void ListView_GiveFeedback(object? sender, GiveFeedbackEventArgs e)
    {
        // Keep system Move/Copy/None cursors (Explorer-like) while our ghost image follows the pointer.
        e.UseDefaultCursors = true;
        UpdateDragOverlayPosition();
    }

    private void SetDropHighlight(ListView? listView)
    {
        if (dropHighlightListView == listView)
            return;

        if (dropHighlightListView != null)
        {
            dropHighlightListView.BackColor = dropHighlightOriginalBackColor;
            dropHighlightListView = null;
        }

        if (listView != null)
        {
            dropHighlightOriginalBackColor = listView.BackColor;
            listView.BackColor = Color.FromArgb(232, 242, 254); // light Explorer selection blue
            dropHighlightListView = listView;
        }
    }

    private void ClearDropHighlight() => SetDropHighlight(null);

    private void InitializeComponent()
    {
        comboBox1 = new ComboBox();
        comboBox1.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox1.SelectedIndexChanged += ComboBox1_SelectedIndexChanged;

        btnOpenFolder1 = new Button();
        btnOpenFolder1.Text = "📁";
        btnOpenFolder1.Width = 35;
        btnOpenFolder1.Click += BtnOpenFolder1_Click;

        comboBox2 = new ComboBox();
        comboBox2.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox2.SelectedIndexChanged += ComboBox2_SelectedIndexChanged;

        btnOpenFolder2 = new Button();
        btnOpenFolder2.Text = "📁";
        btnOpenFolder2.Width = 35;
        btnOpenFolder2.Click += BtnOpenFolder2_Click;

        comboBox3 = new ComboBox();
        comboBox3.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox3.SelectedIndexChanged += ComboBox3_SelectedIndexChanged;

        btnOpenFolder3 = new Button();
        btnOpenFolder3.Text = "📁";
        btnOpenFolder3.Width = 35;
        btnOpenFolder3.Click += BtnOpenFolder3_Click;

        lblCount1 = new Label();
        lblCount1.AutoSize = false;
        lblCount1.TextAlign = ContentAlignment.MiddleLeft;
        lblCount1.Height = 20;

        lblCount2 = new Label();
        lblCount2.AutoSize = false;
        lblCount2.TextAlign = ContentAlignment.MiddleLeft;
        lblCount2.Height = 20;

        lblCount3 = new Label();
        lblCount3.AutoSize = false;
        lblCount3.TextAlign = ContentAlignment.MiddleLeft;
        lblCount3.Height = 20;

        listView1 = new ListView();
        imageList1 = new ImageList();

        listView2 = new ListView();
        imageList2 = new ImageList();

        listView3 = new ListView();
        imageList3 = new ImageList();

        imageList1.ColorDepth = ColorDepth.Depth32Bit;
        imageList1.ImageSize = new Size(32, 32);

        imageList2.ColorDepth = ColorDepth.Depth32Bit;
        imageList2.ImageSize = new Size(32, 32);

        imageList3.ColorDepth = ColorDepth.Depth32Bit;
        imageList3.ImageSize = new Size(32, 32);

        listView1.View = View.LargeIcon;
        listView1.FullRowSelect = true;
        listView1.MultiSelect = true;
        listView1.LargeImageList = imageList1;
        listView1.AllowDrop = true;
        listView1.ItemDrag += ListView1_ItemDrag;
        listView1.DragEnter += ListView1_DragEnter;
        listView1.DragOver += ListView_DragOver;
        listView1.DragLeave += ListView_DragLeave;
        listView1.DragDrop += ListView1_DragDrop;
        listView1.DoubleClick += ListView_DoubleClick;

        listView2.View = View.LargeIcon;
        listView2.FullRowSelect = true;
        listView2.MultiSelect = true;
        listView2.LargeImageList = imageList2;
        listView2.AllowDrop = true;
        listView2.ItemDrag += ListView2_ItemDrag;
        listView2.DragEnter += ListView2_DragEnter;
        listView2.DragOver += ListView_DragOver;
        listView2.DragLeave += ListView_DragLeave;
        listView2.DragDrop += ListView2_DragDrop;
        listView2.DoubleClick += ListView_DoubleClick;

        listView3.View = View.LargeIcon;
        listView3.FullRowSelect = true;
        listView3.MultiSelect = true;
        listView3.LargeImageList = imageList3;
        listView3.AllowDrop = true;
        listView3.ItemDrag += ListView3_ItemDrag;
        listView3.DragEnter += ListView3_DragEnter;
        listView3.DragOver += ListView_DragOver;
        listView3.DragLeave += ListView_DragLeave;
        listView3.DragDrop += ListView3_DragDrop;
        listView3.DoubleClick += ListView_DoubleClick;

        EnableDoubleBuffering(listView1);
        EnableDoubleBuffering(listView2);
        EnableDoubleBuffering(listView3);

        Text = "IconShift - Рабочий стол";

        int screenWidth = Screen.PrimaryScreen!.Bounds.Width;
        int screenHeight = Screen.PrimaryScreen!.Bounds.Height;
        ClientSize = new Size((int)(screenWidth * 0.85), (int)(screenHeight * 0.85));

        Resize += MainForm_Resize;

        // Avoid triple refresh from SelectedIndexChanged during initial populate.
        suppressComboLoad = true;
        PopulateUserComboBoxes();
        suppressComboLoad = false;

        int panelSpacing = 20;
        int topMargin = 10;
        int controlAreaHeight = 25;
        int afterControlsSpacing = 2;
        int bottomMargin = 35;
        int labelHeight = 20;
        int labelSpacing = 5;
        int panelWidth = (ClientSize.Width - panelSpacing * 2 - 30) / 3;
        int panelTop = topMargin + controlAreaHeight + afterControlsSpacing;
        int labelTop = ClientSize.Height - bottomMargin - labelHeight;

        int panel2X = 15 + panelWidth + panelSpacing;
        int panel3X = 15 + (panelWidth + panelSpacing) * 2;

        comboBox1.Width = panelWidth - 45;
        comboBox1.Location = new Point(15, topMargin);
        btnOpenFolder1.Location = new Point(15 + panelWidth - 35, topMargin);

        comboBox2.Width = panelWidth - 45;
        comboBox2.Location = new Point(panel2X, topMargin);
        btnOpenFolder2.Location = new Point(panel2X + panelWidth - 35, topMargin);

        comboBox3.Width = panelWidth - 45;
        comboBox3.Location = new Point(panel3X, topMargin);
        btnOpenFolder3.Location = new Point(panel3X + panelWidth - 35, topMargin);

        lblCount1.Width = panelWidth;
        lblCount1.Location = new Point(15, labelTop);

        lblCount2.Width = panelWidth;
        lblCount2.Location = new Point(panel2X, labelTop);

        lblCount3.Width = panelWidth;
        lblCount3.Location = new Point(panel3X, labelTop);

        listView1.Width = panelWidth;
        listView1.Location = new Point(15, panelTop);
        listView1.Height = labelTop - panelTop - labelSpacing;

        listView2.Width = panelWidth;
        listView2.Location = new Point(panel2X, panelTop);
        listView2.Height = labelTop - panelTop - labelSpacing;

        listView3.Width = panelWidth;
        listView3.Location = new Point(panel3X, panelTop);
        listView3.Height = labelTop - panelTop - labelSpacing;

        Controls.Add(lblCount1);
        Controls.Add(lblCount2);
        Controls.Add(lblCount3);
        Controls.Add(btnOpenFolder1);
        Controls.Add(btnOpenFolder2);
        Controls.Add(btnOpenFolder3);
        Controls.Add(comboBox1);
        Controls.Add(comboBox2);
        Controls.Add(comboBox3);
        Controls.Add(listView3);
        Controls.Add(listView2);
        Controls.Add(listView1);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
    }

    private void EnableDoubleBuffering(ListView listView)
    {
        PropertyInfo? property = typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
        if (property != null)
        {
            property.SetValue(listView, true, null);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            string full = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return full;
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static bool PathsEqual(string? pathA, string? pathB)
    {
        if (string.IsNullOrEmpty(pathA) || string.IsNullOrEmpty(pathB))
            return false;

        return string.Equals(NormalizePath(pathA), NormalizePath(pathB), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True if candidate is the same as root or is nested under root.
    /// </summary>
    private static bool IsSameOrUnder(string root, string candidate)
    {
        string nRoot = NormalizePath(root);
        string nCandidate = NormalizePath(candidate);
        if (string.IsNullOrEmpty(nRoot) || string.IsNullOrEmpty(nCandidate))
            return false;

        if (string.Equals(nRoot, nCandidate, StringComparison.OrdinalIgnoreCase))
            return true;

        string prefix = nRoot.EndsWith(Path.DirectorySeparatorChar)
            ? nRoot
            : nRoot + Path.DirectorySeparatorChar;
        return nCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private void StartListViewItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (sender is ListView listView && e.Item is ListViewItem item)
        {
            var itemsToDrag = listView.SelectedItems.Cast<ListViewItem>()
                .Where(i => i.Tag is FileInfo || i.Tag is DirectoryInfo)
                .ToArray();

            if (itemsToDrag.Length == 0)
            {
                if (item.Tag is FileInfo or DirectoryInfo)
                    itemsToDrag = new[] { item };
                else
                    return;
            }
            else if (!itemsToDrag.Contains(item) && item.Tag is FileInfo or DirectoryInfo)
            {
                itemsToDrag = new[] { item };
            }
            else if (!itemsToDrag.Contains(item))
            {
                return;
            }

            string[] pathsToDrag = itemsToDrag
                .Select(i =>
                {
                    if (i.Tag is FileInfo fi)
                        return fi.FullName;
                    if (i.Tag is DirectoryInfo di)
                        return di.FullName;
                    return null;
                })
                .Where(p => !string.IsNullOrEmpty(p))
                .Cast<string>()
                .ToArray();

            if (pathsToDrag.Length == 0)
                return;

            var dataObject = new DataObject();
            dataObject.SetData(DataFormats.FileDrop, pathsToDrag);
            dataObject.SetData(DataFormats.StringFormat, string.Join("\r\n", pathsToDrag));

            dragSourceListView = listView;
            StartDragOverlay(listView, itemsToDrag, dataObject);
            listView.GiveFeedback += ListView_GiveFeedback;
            // Also track on form for smoother feedback during cross-control drag.
            GiveFeedback += ListView_GiveFeedback;
            QueryContinueDrag += MainForm_QueryContinueDrag;

            try
            {
                DoDragDrop(dataObject, DragDropEffects.Move | DragDropEffects.Copy);
            }
            finally
            {
                listView.GiveFeedback -= ListView_GiveFeedback;
                GiveFeedback -= ListView_GiveFeedback;
                QueryContinueDrag -= MainForm_QueryContinueDrag;
                EndDragOverlay();
                ClearDropHighlight();
                dragSourceListView = null;
            }
        }
    }

    private void MainForm_QueryContinueDrag(object? sender, QueryContinueDragEventArgs e)
    {
        // Keep ghost glued to the cursor even when feedback events are sparse.
        if (e.Action != DragAction.Cancel && e.Action != DragAction.Drop)
            UpdateDragOverlayPosition();
    }

    private void MainForm_Resize(object? sender, EventArgs e)
    {
        int panelSpacing = 20;
        int topMargin = 10;
        int controlAreaHeight = 25;
        int afterControlsSpacing = 2;
        int bottomMargin = 35;
        int labelHeight = 20;
        int labelSpacing = 5;
        int panelWidth = (ClientSize.Width - panelSpacing * 2 - 30) / 3;
        int panelTop = topMargin + controlAreaHeight + afterControlsSpacing;
        int labelTop = ClientSize.Height - bottomMargin - labelHeight;

        int panel2X = 15 + panelWidth + panelSpacing;
        int panel3X = 15 + (panelWidth + panelSpacing) * 2;

        comboBox1.Width = panelWidth - 45;
        comboBox1.Location = new Point(15, topMargin);
        btnOpenFolder1.Location = new Point(15 + panelWidth - 35, topMargin);

        comboBox2.Width = panelWidth - 45;
        comboBox2.Location = new Point(panel2X, topMargin);
        btnOpenFolder2.Location = new Point(panel2X + panelWidth - 35, topMargin);

        comboBox3.Width = panelWidth - 45;
        comboBox3.Location = new Point(panel3X, topMargin);
        btnOpenFolder3.Location = new Point(panel3X + panelWidth - 35, topMargin);

        lblCount1.Width = panelWidth;
        lblCount1.Location = new Point(15, labelTop);

        lblCount2.Width = panelWidth;
        lblCount2.Location = new Point(panel2X, labelTop);

        lblCount3.Width = panelWidth;
        lblCount3.Location = new Point(panel3X, labelTop);

        listView1.Width = panelWidth;
        listView1.Location = new Point(15, panelTop);
        listView1.Height = labelTop - panelTop - labelSpacing;

        listView2.Width = panelWidth;
        listView2.Location = new Point(panel2X, panelTop);
        listView2.Height = labelTop - panelTop - labelSpacing;

        listView3.Width = panelWidth;
        listView3.Location = new Point(panel3X, panelTop);
        listView3.Height = labelTop - panelTop - labelSpacing;
    }

    private void ListView1_ItemDrag(object? sender, ItemDragEventArgs e) => StartListViewItemDrag(sender, e);
    private void ListView2_ItemDrag(object? sender, ItemDragEventArgs e) => StartListViewItemDrag(sender, e);
    private void ListView3_ItemDrag(object? sender, ItemDragEventArgs e) => StartListViewItemDrag(sender, e);

    private void SetDragEffect(DragEventArgs e, ListView? target)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true ||
            e.Data?.GetDataPresent(typeof(string[])) == true)
        {
            bool copy = (e.KeyState & 8) == 8; // Ctrl = copy (Explorer)
            e.Effect = copy ? DragDropEffects.Copy : DragDropEffects.Move;
            if (target != null)
                SetDropHighlight(target);
        }
        else
        {
            e.Effect = DragDropEffects.None;
            if (target != null && dropHighlightListView == target)
                ClearDropHighlight();
        }

        UpdateDragOverlayPosition();
    }

    private void ListView1_DragEnter(object? sender, DragEventArgs e) => SetDragEffect(e, listView1);
    private void ListView2_DragEnter(object? sender, DragEventArgs e) => SetDragEffect(e, listView2);
    private void ListView3_DragEnter(object? sender, DragEventArgs e) => SetDragEffect(e, listView3);
    private void ListView_DragOver(object? sender, DragEventArgs e) =>
        SetDragEffect(e, sender as ListView);

    private void ListView_DragLeave(object? sender, EventArgs e)
    {
        if (sender is ListView lv && dropHighlightListView == lv)
            ClearDropHighlight();
    }

    private void ListView1_DragDrop(object? sender, DragEventArgs e)
    {
        ClearDropHighlight();
        HandleDragDrop(e, listView1, GetCurrentDesktopPath(comboBox1));
    }

    private void ListView2_DragDrop(object? sender, DragEventArgs e)
    {
        ClearDropHighlight();
        HandleDragDrop(e, listView2, GetCurrentDesktopPath(comboBox2));
    }

    private void ListView3_DragDrop(object? sender, DragEventArgs e)
    {
        ClearDropHighlight();
        HandleDragDrop(e, listView3, GetCurrentDesktopPath(comboBox3));
    }

    private void ListView_DoubleClick(object? sender, EventArgs e)
    {
        if (sender is not ListView listView || listView.SelectedItems.Count == 0)
            return;

        var item = listView.SelectedItems[0];
        try
        {
            if (item.Tag is FileInfo file)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = file.FullName,
                    UseShellExecute = true
                });
            }
            else if (item.Tag is DirectoryInfo dir)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir.FullName,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть: {ex.Message}",
                "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void HandleDragDrop(DragEventArgs e, ListView targetListView, string targetPath)
    {
        string[] draggedPaths = Array.Empty<string>();

        try
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] fileDropPaths)
            {
                draggedPaths = fileDropPaths;
            }
            else if (e.Data?.GetData(typeof(string[])) is string[] paths)
            {
                draggedPaths = paths;
            }

            if (draggedPaths.Length == 0)
                return;

            if (!Directory.Exists(targetPath))
            {
                MessageBox.Show($"Целевая папка не найдена: {targetPath}",
                    "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ListView? sourceListView = dragSourceListView;
            string? sourcePanelPath = sourceListView != null
                ? GetDesktopPathForListView(sourceListView)
                : null;

            // Same panel — nothing to do.
            if (sourceListView != null && sourceListView == targetListView)
                return;

            // Same folder shown in two panels, or item already in target folder.
            if (sourcePanelPath != null && PathsEqual(sourcePanelPath, targetPath))
                return;

            bool preferCopy = (e.KeyState & 8) == 8 || (e.Effect & DragDropEffects.Copy) == DragDropEffects.Copy;
            // External drop from Explorer: copy by default if Ctrl held, else move.
            bool isExternal = sourceListView == null;
            if (isExternal && (e.Effect & DragDropEffects.Move) != DragDropEffects.Move)
                preferCopy = true;
            if (isExternal && (e.KeyState & 8) != 8 && (e.Effect & DragDropEffects.Move) == DragDropEffects.Move)
                preferCopy = false;

            var affectedSourceDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceFilePath in draggedPaths)
            {
                if (string.IsNullOrEmpty(sourceFilePath))
                    continue;

                bool isFile = File.Exists(sourceFilePath);
                bool isDir = !isFile && Directory.Exists(sourceFilePath);
                if (!isFile && !isDir)
                    continue;

                string itemName = Path.GetFileName(sourceFilePath);
                if (string.IsNullOrEmpty(itemName))
                    continue;

                string targetFilePath = Path.Combine(targetPath, itemName);

                // Already at destination.
                if (PathsEqual(sourceFilePath, targetFilePath))
                    continue;

                // Prevent moving a directory into itself or a descendant.
                if (isDir && IsSameOrUnder(sourceFilePath, targetFilePath))
                {
                    MessageBox.Show(
                        $"Нельзя переместить папку '{itemName}' внутрь самой себя.",
                        "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    continue;
                }

                if (File.Exists(targetFilePath) || Directory.Exists(targetFilePath))
                {
                    DialogResult result = MessageBox.Show(
                        $"'{itemName}' уже существует в целевой папке. Перезаписать?",
                        "Подтверждение",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Warning);

                    if (result == DialogResult.Cancel)
                        return;

                    if (result == DialogResult.No)
                        continue;

                    try
                    {
                        if (File.Exists(targetFilePath))
                            File.Delete(targetFilePath);
                        else if (Directory.Exists(targetFilePath))
                            Directory.Delete(targetFilePath, true);
                    }
                    catch (Exception delEx)
                    {
                        MessageBox.Show($"Не удалось удалить существующий элемент '{itemName}': {delEx.Message}",
                            "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        continue;
                    }
                }

                string? parentDir = Path.GetDirectoryName(sourceFilePath);
                if (!string.IsNullOrEmpty(parentDir))
                    affectedSourceDirs.Add(NormalizePath(parentDir));

                if (preferCopy)
                    CopyFileOrDirectory(sourceFilePath, targetFilePath);
                else
                    MoveFileOrDirectory(sourceFilePath, targetFilePath);
            }

            RefreshListViewsWithPath(targetPath);

            if (sourcePanelPath != null)
                RefreshListViewsWithPath(sourcePanelPath);

            // External drops: refresh any panel that shows a parent of moved items.
            foreach (string sourceDir in affectedSourceDirs)
                RefreshListViewsWithPath(sourceDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при переносе файла: {ex.Message}",
                "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Error);

            try
            {
                RefreshListViewsWithPath(targetPath);
                if (dragSourceListView != null)
                    RefreshListViewsWithPath(GetDesktopPathForListView(dragSourceListView));
            }
            catch
            {
                // Ignore refresh errors after a failed transfer.
            }
        }
    }

    private void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string destSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }

    private void CopyFileOrDirectory(string sourcePath, string targetPath)
    {
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, targetPath, true);
        }
        else if (Directory.Exists(sourcePath))
        {
            CopyDirectory(sourcePath, targetPath);
        }
        else
        {
            throw new FileNotFoundException("Источник не найден", sourcePath);
        }
    }

    private void MoveFileOrDirectory(string sourcePath, string targetPath)
    {
        if (PathsEqual(sourcePath, targetPath))
            return;

        if (Directory.Exists(sourcePath) && IsSameOrUnder(sourcePath, targetPath))
            throw new InvalidOperationException("Нельзя переместить папку внутрь самой себя.");

        if (File.Exists(sourcePath))
        {
            try
            {
                File.Move(sourcePath, targetPath, overwrite: true);
            }
            catch (IOException)
            {
                File.Copy(sourcePath, targetPath, true);
                try
                {
                    File.Delete(sourcePath);
                }
                catch (Exception deleteEx)
                {
                    // Leave the copy in place; surface that source was not removed.
                    throw new IOException(
                        $"Файл скопирован в '{targetPath}', но исходник не удалён: {deleteEx.Message}",
                        deleteEx);
                }
            }
            catch (UnauthorizedAccessException)
            {
                File.Copy(sourcePath, targetPath, true);
                try
                {
                    File.Delete(sourcePath);
                }
                catch (Exception deleteEx)
                {
                    throw new IOException(
                        $"Файл скопирован в '{targetPath}', но исходник не удалён: {deleteEx.Message}",
                        deleteEx);
                }
            }
        }
        else if (Directory.Exists(sourcePath))
        {
            try
            {
                Directory.Move(sourcePath, targetPath);
            }
            catch (Exception)
            {
                CopyDirectory(sourcePath, targetPath);
                try
                {
                    Directory.Delete(sourcePath, true);
                }
                catch (Exception deleteEx)
                {
                    throw new IOException(
                        $"Папка скопирована в '{targetPath}', но исходник не удалён: {deleteEx.Message}",
                        deleteEx);
                }
            }
        }
        else
        {
            throw new FileNotFoundException("Источник не найден", sourcePath);
        }
    }

    private ImageList GetImageListFor(ListView listView)
    {
        if (listView == listView1) return imageList1;
        if (listView == listView2) return imageList2;
        return imageList3;
    }

    private string? GetLastPathFor(ListView listView)
    {
        if (listView == listView1) return lastPath1;
        if (listView == listView2) return lastPath2;
        return lastPath3;
    }

    private void SetLastPathFor(ListView listView, string path)
    {
        if (listView == listView1) lastPath1 = path;
        else if (listView == listView2) lastPath2 = path;
        else lastPath3 = path;
    }

    private void RefreshListView(ListView listView, string folderPath)
    {
        ImageList imageList = GetImageListFor(listView);
        var pathToIconIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var newItems = new List<ListViewItem>();

        // When switching to a different folder, drop icon cache to limit ImageList growth.
        string? previousPath = GetLastPathFor(listView);
        bool pathChanged = previousPath != null && !PathsEqual(previousPath, folderPath);
        if (pathChanged)
        {
            imageList.Images.Clear();
            listView.Tag = null;
        }
        else if (listView.Tag is Dictionary<string, int> existingTag)
        {
            foreach (var kvp in existingTag)
                pathToIconIndex[kvp.Key] = kvp.Value;
        }

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            listView.BeginUpdate();
            listView.Items.Clear();
            listView.EndUpdate();
            listView.Tag = pathToIconIndex;
            SetLastPathFor(listView, folderPath ?? string.Empty);

            Label emptyLabel = listView == listView1 ? lblCount1 : listView == listView2 ? lblCount2 : lblCount3;
            UpdateItemCount(emptyLabel, listView, folderPath ?? string.Empty);
            return;
        }

        try
        {
            DirectoryInfo dir = new DirectoryInfo(folderPath);

            foreach (var directory in dir.EnumerateDirectories())
            {
                var listItem = CreateListViewItem(directory, imageList, pathToIconIndex);
                if (listItem != null)
                    newItems.Add(listItem);
            }

            foreach (var file in dir.EnumerateFiles())
            {
                var listItem = CreateListViewItem(file, imageList, pathToIconIndex);
                if (listItem != null)
                    newItems.Add(listItem);
            }

            newItems.Sort((a, b) =>
            {
                bool aIsDir = a.Tag is DirectoryInfo;
                bool bIsDir = b.Tag is DirectoryInfo;
                if (aIsDir && !bIsDir) return -1;
                if (!aIsDir && bIsDir) return 1;
                return string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);
            });

            listView.BeginUpdate();
            listView.Items.Clear();
            listView.Items.AddRange(newItems.ToArray());
            listView.EndUpdate();

            listView.Tag = pathToIconIndex;
            SetLastPathFor(listView, folderPath);

            Label label = listView == listView1 ? lblCount1 : listView == listView2 ? lblCount2 : lblCount3;
            UpdateItemCount(label, listView, folderPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при обновлении списка: {ex.Message}",
                "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadDesktopFiles() => RefreshListView(listView1, GetCurrentDesktopPath(comboBox1));
    private void LoadCommonDesktopFiles() => RefreshListView(listView2, GetCurrentDesktopPath(comboBox2));
    private void LoadThirdDesktopFiles() => RefreshListView(listView3, GetCurrentDesktopPath(comboBox3));

    private ListViewItem? CreateListViewItem(FileInfo file, ImageList imageList, Dictionary<string, int> pathToIconIndex)
    {
        try
        {
            string displayName = file.Name;
            string iconKey = file.FullName;

            if (pathToIconIndex.TryGetValue(iconKey, out int existingIndex))
            {
                return new ListViewItem
                {
                    Text = displayName,
                    ImageIndex = existingIndex,
                    Tag = file
                };
            }

            Icon? icon = null;
            bool disposeIcon = false;

            if (string.Equals(file.Extension, ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string targetPath = GetLnkTargetPath(file.FullName);
                if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
                {
                    icon = Icon.ExtractAssociatedIcon(targetPath);
                    disposeIcon = icon != null;
                }
            }

            if (icon == null && File.Exists(file.FullName))
            {
                icon = Icon.ExtractAssociatedIcon(file.FullName);
                disposeIcon = icon != null;
            }

            if (icon == null)
            {
                icon = SystemIcons.Application;
                disposeIcon = false;
            }

            int iconIndex = imageList.Images.Count;
            imageList.Images.Add(icon);
            if (disposeIcon)
                icon.Dispose();

            pathToIconIndex[iconKey] = iconIndex;

            return new ListViewItem
            {
                Text = displayName,
                ImageIndex = iconIndex,
                Tag = file
            };
        }
        catch
        {
            return null;
        }
    }

    private ListViewItem? CreateListViewItem(DirectoryInfo directory, ImageList imageList, Dictionary<string, int> pathToIconIndex)
    {
        try
        {
            string displayName = directory.Name;
            string iconKey = directory.FullName;

            if (pathToIconIndex.TryGetValue(iconKey, out int existingIndex))
            {
                return new ListViewItem
                {
                    Text = displayName,
                    ImageIndex = existingIndex,
                    Tag = directory
                };
            }

            Icon? icon = GetSystemIcon(directory.FullName, true);
            bool disposeIcon = icon != null;
            icon ??= SystemIcons.Application;

            int iconIndex = imageList.Images.Count;
            imageList.Images.Add(icon);
            if (disposeIcon)
                icon.Dispose();

            pathToIconIndex[iconKey] = iconIndex;

            return new ListViewItem
            {
                Text = displayName,
                ImageIndex = iconIndex,
                Tag = directory
            };
        }
        catch
        {
            return null;
        }
    }

    private string GetLnkTargetPath(string lnkPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8"));
            if (shellType == null)
                return string.Empty;

            object? shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return string.Empty;

            dynamic shellObj = shell;
            var shortcut = shellObj.CreateShortcut(lnkPath);
            string targetPath = shortcut.TargetPath ?? string.Empty;

            Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell);

            return targetPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private FileSystemWatcher? CreateWatcher(string path, System.Windows.Forms.Timer debounceTimer)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return null;

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            void OnFsEvent(object sender, FileSystemEventArgs e)
            {
                // Debounce on UI thread via timer.Restart pattern.
                if (IsDisposed)
                    return;

                void Restart()
                {
                    debounceTimer.Stop();
                    debounceTimer.Start();
                }

                if (InvokeRequired)
                    BeginInvoke(Restart);
                else
                    Restart();
            }

            watcher.Created += OnFsEvent;
            watcher.Deleted += OnFsEvent;
            watcher.Renamed += OnFsEvent;
            watcher.Changed += OnFsEvent;
            return watcher;
        }
        catch
        {
            return null;
        }
    }

    private void SafeSetWatcherPath(ref FileSystemWatcher? watcher, string path, System.Windows.Forms.Timer debounceTimer)
    {
        if (watcher != null)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // Ignore dispose races.
            }
            watcher = null;
        }

        watcher = CreateWatcher(path, debounceTimer);
    }

    private void InitializeFileSystemWatchers()
    {
        desktopWatcher = CreateWatcher(GetCurrentDesktopPath(comboBox1), refreshDebounceTimer1);
        commonDesktopWatcher = CreateWatcher(GetCurrentDesktopPath(comboBox2), refreshDebounceTimer2);
        thirdDesktopWatcher = CreateWatcher(GetCurrentDesktopPath(comboBox3), refreshDebounceTimer3);
    }

    private void PopulateUserComboBoxes()
    {
        comboBox1.Items.Add("Общая папка");
        comboBox2.Items.Add("Общая папка");
        comboBox3.Items.Add("Общая папка");

        try
        {
            using RegistryKey? profileList = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profileList != null)
            {
                foreach (string sidName in profileList.GetSubKeyNames())
                {
                    using RegistryKey? profile = profileList.OpenSubKey(sidName);
                    if (profile == null)
                        continue;

                    string? profilePath = profile.GetValue("ProfileImagePath") as string;
                    if (string.IsNullOrEmpty(profilePath))
                        continue;

                    string userName = Path.GetFileName(profilePath);

                    if (userName.StartsWith("systemprofile", StringComparison.OrdinalIgnoreCase) ||
                        userName.StartsWith("LocalService", StringComparison.OrdinalIgnoreCase) ||
                        userName.StartsWith("NetworkService", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string desktopPath = GetUserDesktopPath(sidName, profilePath);

                    if (Directory.Exists(desktopPath))
                    {
                        var userItem = new UserProfileItem(userName, desktopPath);
                        comboBox1.Items.Add(userItem);
                        comboBox2.Items.Add(userItem);
                        comboBox3.Items.Add(userItem);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при чтении реестра: {ex.Message}",
                "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        string currentUser = Environment.UserName;
        int currentUserIndex = -1;
        for (int i = 0; i < comboBox1.Items.Count; i++)
        {
            if (comboBox1.Items[i] is UserProfileItem item &&
                string.Equals(item.UserName, currentUser, StringComparison.OrdinalIgnoreCase))
            {
                currentUserIndex = i;
                break;
            }
        }

        if (currentUserIndex != -1)
        {
            comboBox1.SelectedIndex = currentUserIndex;
            comboBox2.SelectedIndex = currentUserIndex;
            comboBox3.SelectedIndex = currentUserIndex;
        }
        else if (comboBox1.Items.Count > 0)
        {
            comboBox1.SelectedIndex = 0;
            comboBox2.SelectedIndex = 0;
            comboBox3.SelectedIndex = 0;
        }
    }

    private string GetUserDesktopPath(string sid, string profilePath)
    {
        try
        {
            using RegistryKey? userKey = Registry.Users.OpenSubKey($"{sid}\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders");
            if (userKey != null)
            {
                string? desktopPath = userKey.GetValue("Desktop") as string;
                if (!string.IsNullOrEmpty(desktopPath))
                {
                    desktopPath = Environment.ExpandEnvironmentVariables(desktopPath);
                    if (Directory.Exists(desktopPath))
                        return desktopPath;
                }
            }
        }
        catch
        {
            // Ignore access errors for other users' registry hives.
        }

        string standardPath = Path.Combine(profilePath, "Desktop");
        if (Directory.Exists(standardPath))
            return standardPath;

        string userName = Path.GetFileName(profilePath);
        string[] driveLetters = { "D", "E", "F" };

        foreach (string drive in driveLetters)
        {
            string altPath = Path.Combine($"{drive}:\\Users", userName, "Desktop");
            if (Directory.Exists(altPath))
                return altPath;
        }

        return standardPath;
    }

    private void ComboBox1_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (suppressComboLoad || comboBox1.SelectedItem == null)
            return;

        string selectedPath = GetCurrentDesktopPath(comboBox1);
        SafeSetWatcherPath(ref desktopWatcher, selectedPath, refreshDebounceTimer1);
        RefreshListView(listView1, selectedPath);
    }

    private void ComboBox2_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (suppressComboLoad || comboBox2.SelectedItem == null)
            return;

        string selectedPath = GetCurrentDesktopPath(comboBox2);
        SafeSetWatcherPath(ref commonDesktopWatcher, selectedPath, refreshDebounceTimer2);
        RefreshListView(listView2, selectedPath);
    }

    private void ComboBox3_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (suppressComboLoad || comboBox3.SelectedItem == null)
            return;

        string selectedPath = GetCurrentDesktopPath(comboBox3);
        SafeSetWatcherPath(ref thirdDesktopWatcher, selectedPath, refreshDebounceTimer3);
        RefreshListView(listView3, selectedPath);
    }

    private void BtnOpenFolder1_Click(object? sender, EventArgs e) =>
        OpenFolderInExplorer(GetCurrentDesktopPath(comboBox1));

    private void BtnOpenFolder2_Click(object? sender, EventArgs e) =>
        OpenFolderInExplorer(GetCurrentDesktopPath(comboBox2));

    private void BtnOpenFolder3_Click(object? sender, EventArgs e) =>
        OpenFolderInExplorer(GetCurrentDesktopPath(comboBox3));

    private void OpenFolderInExplorer(string folderPath)
    {
        try
        {
            if (Directory.Exists(folderPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = folderPath,
                    UseShellExecute = true
                });
            }
            else
            {
                MessageBox.Show($"Папка не найдена: {folderPath}",
                    "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при открытии папки: {ex.Message}",
                "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateItemCount(Label label, ListView listView, string folderPath = "")
    {
        int count = listView.Items.Count;
        if (!string.IsNullOrEmpty(folderPath))
            label.Text = $"Элементов: {count}  |  {folderPath}";
        else
            label.Text = $"Элементов: {count}";
    }

    private string GetCurrentDesktopPath(ComboBox comboBox)
    {
        if (comboBox.SelectedItem == null)
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        if (comboBox.SelectedItem is string s && s == "Общая папка")
            return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        if (comboBox.SelectedItem is UserProfileItem item)
            return item.DesktopPath;

        return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    private string GetDesktopPathForListView(ListView listView)
    {
        if (listView == listView1)
            return GetCurrentDesktopPath(comboBox1);
        if (listView == listView2)
            return GetCurrentDesktopPath(comboBox2);
        if (listView == listView3)
            return GetCurrentDesktopPath(comboBox3);
        return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    private sealed class UserProfileItem
    {
        public string UserName { get; }
        public string DesktopPath { get; }

        public UserProfileItem(string userName, string desktopPath)
        {
            UserName = userName;
            DesktopPath = desktopPath;
        }

        public override string ToString() => UserName;
    }

    private void RefreshListViewsWithPath(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath))
            return;

        if (PathsEqual(GetDesktopPathForListView(listView1), folderPath))
            RefreshListView(listView1, GetDesktopPathForListView(listView1));

        if (PathsEqual(GetDesktopPathForListView(listView2), folderPath))
            RefreshListView(listView2, GetDesktopPathForListView(listView2));

        if (PathsEqual(GetDesktopPathForListView(listView3), folderPath))
            RefreshListView(listView3, GetDesktopPathForListView(listView3));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { desktopWatcher?.Dispose(); } catch { /* ignore */ }
            try { commonDesktopWatcher?.Dispose(); } catch { /* ignore */ }
            try { thirdDesktopWatcher?.Dispose(); } catch { /* ignore */ }

            EndDragOverlay();
            ClearDropHighlight();

            dragOverlayTimer?.Stop();
            dragOverlayTimer?.Dispose();
            refreshDebounceTimer1?.Stop();
            refreshDebounceTimer1?.Dispose();
            refreshDebounceTimer2?.Stop();
            refreshDebounceTimer2?.Dispose();
            refreshDebounceTimer3?.Stop();
            refreshDebounceTimer3?.Dispose();

            imageList1?.Dispose();
            imageList2?.Dispose();
            imageList3?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void FillRoundedRectangle(Graphics g, Brush brush, Rectangle bounds, int radius)
    {
        using var path = CreateRoundedRect(bounds, radius);
        g.FillPath(brush, path);
    }

    private static void DrawRoundedRectangle(Graphics g, Pen pen, Rectangle bounds, int radius)
    {
        using var path = CreateRoundedRect(bounds, radius);
        g.DrawPath(pen, path);
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        int d = Math.Max(0, radius) * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        if (radius <= 0 || d > bounds.Width || d > bounds.Height)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
