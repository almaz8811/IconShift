using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IconShift;

public partial class MainForm : Form
{
    private ComboBox comboBox1;
    private ComboBox comboBox2;
    private ComboBox comboBox3;
    private Button btnOpenFolder1;
    private Button btnOpenFolder2;
    private Button btnOpenFolder3;
    private Label lblCount1;
    private Label lblCount2;
    private Label lblCount3;
    private ListView listView1;
    private ImageList imageList1;
    private ListView listView2;
    private ImageList imageList2;
    private ListView listView3;
    private ImageList imageList3;
    private FileSystemWatcher desktopWatcher;
    private FileSystemWatcher commonDesktopWatcher;
    private FileSystemWatcher thirdDesktopWatcher;
    private Form? dragOverlayForm;
    private System.Windows.Forms.Timer dragOverlayTimer;
    private Bitmap? dragOverlayBitmap;
    private Point dragOverlayHotspot;

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
        InitializeFileSystemWatchers();
        LoadDesktopFiles();
        LoadCommonDesktopFiles();
        LoadThirdDesktopFiles();
    }

    private void InitializeDragDropOverlay()
    {
        dragOverlayTimer = new System.Windows.Forms.Timer();
        dragOverlayTimer.Interval = 20;
        dragOverlayTimer.Tick += DragOverlayTimer_Tick;
    }

    private void DragOverlayTimer_Tick(object? sender, EventArgs e)
    {
        if (dragOverlayForm != null && dragOverlayForm.Visible)
        {
            UpdateDragOverlayPosition();
        }
    }

    private void StartDragOverlay(ListView sourceListView, ListViewItem item, DataObject dataObject)
    {
        if (item == null)
        {
            return;
        }

        dragOverlayBitmap?.Dispose();

        int iconSize = 48;
        int padding = 8;
        using Font font = SystemFonts.MenuFont;
        Size textSize = TextRenderer.MeasureText(item.Text, font);
        int width = iconSize + padding * 3 + Math.Min(textSize.Width, 220);
        int height = Math.Max(iconSize + padding * 2, textSize.Height + padding * 2);

        dragOverlayBitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(dragOverlayBitmap))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var backgroundBrush = new SolidBrush(Color.FromArgb(220, Color.White));
            g.FillRectangle(backgroundBrush, 0, 0, width, height);
            g.DrawRectangle(Pens.LightGray, 0, 0, width - 1, height - 1);

            Image? iconImage = null;
            if (sourceListView.LargeImageList != null && item.ImageIndex >= 0 && item.ImageIndex < sourceListView.LargeImageList.Images.Count)
            {
                iconImage = sourceListView.LargeImageList.Images[item.ImageIndex];
            }

            if (iconImage != null)
            {
                g.DrawImage(iconImage, new Rectangle(padding, padding, iconSize, iconSize));
            }

            var textRect = new Rectangle(iconSize + padding * 2, padding, width - iconSize - padding * 3, height - padding * 2);
            TextRenderer.DrawText(g, item.Text, font, textRect, Color.Black, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        dragOverlayHotspot = new Point(padding, padding);

        if (!TryInitializeShellDragImage(dataObject, dragOverlayBitmap, dragOverlayHotspot))
        {
            dragOverlayForm = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                BackColor = Color.White,
                TransparencyKey = Color.Empty,
                AllowTransparency = true,
                TopMost = true,
                Size = dragOverlayBitmap.Size,
                Opacity = 0.85
            };

            dragOverlayForm.BackgroundImage = dragOverlayBitmap;
            dragOverlayForm.BackgroundImageLayout = ImageLayout.None;
            dragOverlayForm.Show();
            UpdateDragOverlayPosition();
            dragOverlayTimer.Start();
        }
    }

    private void UpdateDragOverlayPosition()
    {
        if (dragOverlayForm == null)
        {
            return;
        }

        Point cursorPos = Cursor.Position;
        dragOverlayForm.Location = new Point(cursorPos.X - dragOverlayHotspot.X, cursorPos.Y - dragOverlayHotspot.Y);
    }

    private void EndDragOverlay()
    {
        dragOverlayTimer.Stop();

        if (dragOverlayForm != null)
        {
            dragOverlayForm.Close();
            dragOverlayForm.Dispose();
            dragOverlayForm = null;
        }

        dragOverlayBitmap?.Dispose();
        dragOverlayBitmap = null;
    }

    private bool TryInitializeShellDragImage(DataObject dataObject, Bitmap dragBitmap, Point hotspot)
    {
        try
        {
            var helper = (IDragSourceHelper)new DragDropHelper();
            IntPtr hBitmap = dragBitmap.GetHbitmap(Color.FromArgb(0));
            var shdi = new SHDRAGIMAGE
            {
                sizeDragImage = new SIZE { cx = dragBitmap.Width, cy = dragBitmap.Height },
                ptOffset = new POINT { x = hotspot.X, y = hotspot.Y },
                hbmpDragImage = hBitmap,
                crColorKey = unchecked((int)0xFFFFFFFF)
            };

            helper.InitializeFromBitmap(ref shdi, dataObject);
            DeleteObject(hBitmap);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ListView_GiveFeedback(object? sender, GiveFeedbackEventArgs e)
    {
        e.UseDefaultCursors = true;
    }

    private void InitializeComponent()
    {
        // Настройка ComboBox для первой панели
        comboBox1 = new ComboBox();
        comboBox1.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox1.SelectedIndexChanged += ComboBox1_SelectedIndexChanged;

        // Настройка кнопки открытия папки для первой панели
        btnOpenFolder1 = new Button();
        btnOpenFolder1.Text = "📁";
        btnOpenFolder1.Width = 35;
        btnOpenFolder1.Click += BtnOpenFolder1_Click;

        // Настройка ComboBox для второй панели
        comboBox2 = new ComboBox();
        comboBox2.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox2.SelectedIndexChanged += ComboBox2_SelectedIndexChanged;

        // Настройка кнопки открытия папки для второй панели
        btnOpenFolder2 = new Button();
        btnOpenFolder2.Text = "📁";
        btnOpenFolder2.Width = 35;
        btnOpenFolder2.Click += BtnOpenFolder2_Click;

        // Настройка ComboBox для третьей панели
        comboBox3 = new ComboBox();
        comboBox3.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox3.SelectedIndexChanged += ComboBox3_SelectedIndexChanged;

        // Настройка кнопки открытия папки для третьей панели
        btnOpenFolder3 = new Button();
        btnOpenFolder3.Text = "📁";
        btnOpenFolder3.Width = 35;
        btnOpenFolder3.Click += BtnOpenFolder3_Click;

        // Настройка Label для счётчика первой панели
        lblCount1 = new Label();
        lblCount1.AutoSize = false;
        lblCount1.TextAlign = ContentAlignment.MiddleLeft;
        lblCount1.Height = 20;

        // Настройка Label для счётчика второй панели
        lblCount2 = new Label();
        lblCount2.AutoSize = false;
        lblCount2.TextAlign = ContentAlignment.MiddleLeft;
        lblCount2.Height = 20;

        // Настройка Label для счётчика третьей панели
        lblCount3 = new Label();
        lblCount3.AutoSize = false;
        lblCount3.TextAlign = ContentAlignment.MiddleLeft;
        lblCount3.Height = 20;

        // Настройка ListView для рабочего стола
        listView1 = new ListView();
        imageList1 = new ImageList();

        // Настройка ListView для общих ярлыков
        listView2 = new ListView();
        imageList2 = new ImageList();

        // Настройка ListView для третьей панели
        listView3 = new ListView();
        imageList3 = new ImageList();

        // Настройка ImageList1
        imageList1.ColorDepth = ColorDepth.Depth32Bit;
        imageList1.ImageSize = new Size(32, 32);

        // Настройка ImageList2
        imageList2.ColorDepth = ColorDepth.Depth32Bit;
        imageList2.ImageSize = new Size(32, 32);

        // Настройка ImageList3
        imageList3.ColorDepth = ColorDepth.Depth32Bit;
        imageList3.ImageSize = new Size(32, 32);

        // Настройка ListView1 (левая панель - рабочий стол)
        listView1.View = View.LargeIcon;
        listView1.FullRowSelect = true;
        listView1.LargeImageList = imageList1;
        listView1.AllowDrop = true;
        listView1.ItemDrag += ListView1_ItemDrag;
        listView1.DragEnter += ListView1_DragEnter;
        listView1.DragDrop += ListView1_DragDrop;

        // Настройка ListView2 (правая панель - общие ярлыки)
        listView2.View = View.LargeIcon;
        listView2.FullRowSelect = true;
        listView2.LargeImageList = imageList2;
        listView2.AllowDrop = true;
        listView2.ItemDrag += ListView2_ItemDrag;
        listView2.DragEnter += ListView2_DragEnter;
        listView2.DragDrop += ListView2_DragDrop;

        // Настройка ListView3 (третья панель)
        listView3.View = View.LargeIcon;
        listView3.FullRowSelect = true;
        listView3.LargeImageList = imageList3;
        listView3.AllowDrop = true;
        listView3.ItemDrag += ListView3_ItemDrag;
        listView3.DragEnter += ListView3_DragEnter;
        listView3.DragDrop += ListView3_DragDrop;

        // Настройка формы
        Text = "IconShift - Рабочий стол";

        // Устанавливаем размер формы - 85% от экрана
        int screenWidth = Screen.PrimaryScreen!.Bounds.Width;
        int screenHeight = Screen.PrimaryScreen!.Bounds.Height;
        ClientSize = new Size((int)(screenWidth * 0.85), (int)(screenHeight * 0.85));

        // Регистрируем обработчик изменения размера окна
        Resize += MainForm_Resize;

        // Заполняем выпадающие списки пользователями
        PopulateUserComboBoxes();

        // Устанавливаем размеры панелей для растягивания на всё окно
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

    private void MainForm_Resize(object? sender, EventArgs e)
    {
        // Адаптируем размеры панелей при изменении размера окна
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

    private void ListView1_ItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (sender is ListView listView && e.Item is ListViewItem item && (item.Tag is FileInfo || item.Tag is DirectoryInfo))
        {
            var dataObject = new DataObject();
            dataObject.SetData(typeof(ListViewItem).FullName!, item);
            StartDragOverlay(listView, item, dataObject);
            listView.GiveFeedback += ListView_GiveFeedback;

            try
            {
                DoDragDrop(dataObject, DragDropEffects.Move | DragDropEffects.Copy);
            }
            finally
            {
                listView.GiveFeedback -= ListView_GiveFeedback;
                EndDragOverlay();
            }
        }
    }

    private void ListView2_ItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (sender is ListView listView && e.Item is ListViewItem item && (item.Tag is FileInfo || item.Tag is DirectoryInfo))
        {
            var dataObject = new DataObject();
            dataObject.SetData(typeof(ListViewItem).FullName!, item);
            StartDragOverlay(listView, item, dataObject);
            listView.GiveFeedback += ListView_GiveFeedback;

            try
            {
                DoDragDrop(dataObject, DragDropEffects.Move | DragDropEffects.Copy);
            }
            finally
            {
                listView.GiveFeedback -= ListView_GiveFeedback;
                EndDragOverlay();
            }
        }
    }

    private void ListView1_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(ListViewItem)) is ListViewItem)
        {
            e.Effect = DragDropEffects.Move | DragDropEffects.Copy;
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private void ListView2_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(ListViewItem)) is ListViewItem)
        {
            e.Effect = DragDropEffects.Move | DragDropEffects.Copy;
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private void ListView1_DragDrop(object? sender, DragEventArgs e)
    {
        HandleDragDrop(e, listView1, GetCurrentDesktopPath(comboBox1));
    }

    private void ListView2_DragDrop(object? sender, DragEventArgs e)
    {
        HandleDragDrop(e, listView2, GetCurrentDesktopPath(comboBox2));
    }

    private void ListView3_ItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (sender is ListView listView && e.Item is ListViewItem item && (item.Tag is FileInfo || item.Tag is DirectoryInfo))
        {
            var dataObject = new DataObject();
            dataObject.SetData(typeof(ListViewItem).FullName!, item);
            StartDragOverlay(listView, item, dataObject);
            listView.GiveFeedback += ListView_GiveFeedback;

            try
            {
                DoDragDrop(dataObject, DragDropEffects.Move | DragDropEffects.Copy);
            }
            finally
            {
                listView.GiveFeedback -= ListView_GiveFeedback;
                EndDragOverlay();
            }
        }
    }

    private void ListView3_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(ListViewItem)) is ListViewItem)
        {
            e.Effect = DragDropEffects.Move | DragDropEffects.Copy;
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private void ListView3_DragDrop(object? sender, DragEventArgs e)
    {
        HandleDragDrop(e, listView3, GetCurrentDesktopPath(comboBox3));
    }

    private void HandleDragDrop(DragEventArgs e, ListView targetListView, string targetPath)
    {
        ListViewItem? draggedItem = null;
        ListView? sourceListView = null;
        object? tag = null;
        string? sourceFilePath = null;
        string? targetFilePath = null;
        
        try
        {
            if (e.Data?.GetData(typeof(ListViewItem)) is ListViewItem item)
            {
                draggedItem = item;
                sourceListView = item.ListView;

                // Если элемент был отпущен в той же панели, ничего не делаем
                if (sourceListView == targetListView)
                {
                    return;
                }
                
                // Получаем информацию о перетаскиваемом элементе
                string itemName = item.Text;
                tag = item.Tag;
                
                sourceFilePath = tag switch
                {
                    FileInfo fi => fi.FullName,
                    DirectoryInfo di => di.FullName,
                    _ => null
                };

                if (string.IsNullOrEmpty(sourceFilePath))
                {
                    return;
                }

                targetFilePath = Path.Combine(targetPath, itemName);

                // Если файл уже существует в целевой папке, спрашиваем пользователя
                if (File.Exists(targetFilePath) || Directory.Exists(targetFilePath))
                {
                    DialogResult result = MessageBox.Show(
                        $"Файл '{itemName}' уже существует в целевой папке. Перезаписать?",
                        "Подтверждение",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Warning);
                    
                    if (result == DialogResult.Cancel)
                    {
                        return;
                    }
                    
                    if (result == DialogResult.No)
                    {
                        // Пропускаем этот файл, но продолжаем перенос остальных
                        return;
                    }
                    
                    // Удаляем существующий файл перед перемещением
                    if (File.Exists(targetFilePath))
                    {
                        File.Delete(targetFilePath);
                    }
                    else if (Directory.Exists(targetFilePath))
                    {
                        Directory.Delete(targetFilePath, true);
                    }
                }

                // Перемещаем файл
                if (tag is FileInfo)
                {
                    File.Move(sourceFilePath, targetFilePath);
                }
                else if (tag is DirectoryInfo)
                {
                    Directory.Move(sourceFilePath, targetFilePath);
                }

                // Обновляем интерфейс
                item.Remove();
                RefreshListView(targetListView, targetPath);
                if (sourceListView != null)
                {
                    string sourcePath = GetDesktopPathForListView(sourceListView);
                    RefreshListView(sourceListView, sourcePath);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            // Обработка ошибки доступа при перемещении файлов из системных папок
            if (draggedItem != null && tag != null && sourceFilePath != null && targetFilePath != null)
            {
                DialogResult result = MessageBox.Show(
                    $"Ошибка доступа при перемещении файла: {ex.Message}\n\n" +
                    "Это может быть связано с тем, что файл находится в системной папке, требующей прав администратора.\n\n" +
                    "Попробовать скопировать файл и удалить исходный?",
                    "IconShift - Ошибка доступа",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning);
                
                if (result == DialogResult.Yes)
                {
                    try
                    {
                        // Копируем файл
                        if (tag is FileInfo fi)
                        {
                            File.Copy(fi.FullName, targetFilePath, true);
                        }
                        else if (tag is DirectoryInfo di)
                        {
                            // Для папок используем рекурсивное копирование
                            CopyDirectory(di.FullName, targetFilePath);
                        }
                        
                        // Удаляем исходный файл
                        if (tag is FileInfo)
                        {
                            File.Delete(sourceFilePath);
                        }
                        else if (tag is DirectoryInfo)
                        {
                            Directory.Delete(sourceFilePath, true);
                        }
                        
                        // Обновляем интерфейс
                        draggedItem.Remove();
                        RefreshListView(targetListView, targetPath);
                        if (sourceListView != null)
                        {
                            string sourcePath = GetDesktopPathForListView(sourceListView);
                            RefreshListView(sourceListView, sourcePath);
                        }
                    }
                    catch (Exception copyEx)
                    {
                        MessageBox.Show($"Ошибка при копировании файла: {copyEx.Message}", 
                            "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при перемещении файла: {ex.Message}", 
                "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Вспомогательный метод для рекурсивного копирования папок
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

    private void RefreshListView(ListView listView, string folderPath)
    {
        listView.Items.Clear();

        try
        {
            DirectoryInfo dir = new DirectoryInfo(folderPath);
            var items = new List<ListViewItem>();

            // Получаем нужный ImageList для ListView
            ImageList imageList;
            if (listView == listView1)
                imageList = imageList1;
            else if (listView == listView2)
                imageList = imageList2;
            else
                imageList = imageList3;

            // Сначала добавляем папки
            foreach (var directory in dir.EnumerateDirectories())
            {
                var listItem = CreateListViewItem(directory, imageList);
                if (listItem != null)
                {
                    items.Add(listItem);
                }
            }

            // Потом добавляем файлы
            foreach (var file in dir.EnumerateFiles())
            {
                var listItem = CreateListViewItem(file, imageList);
                if (listItem != null)
                {
                    items.Add(listItem);
                }
            }

            // Сортируем: сначала папки, потом файлы, по алфавиту внутри каждой группы
            items.Sort((a, b) =>
            {
                bool aIsDir = a.Tag is DirectoryInfo;
                bool bIsDir = b.Tag is DirectoryInfo;

                if (aIsDir && !bIsDir) return -1;
                if (!aIsDir && bIsDir) return 1;
                return string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);
            });

            listView.Items.AddRange(items.ToArray());

            // Обновляем счётчик
            Label label;
            if (listView == listView1)
                label = lblCount1;
            else if (listView == listView2)
                label = lblCount2;
            else
                label = lblCount3;

            UpdateItemCount(label, listView, folderPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при обновлении списка: {ex.Message}",
                "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadDesktopFiles()
    {
        string desktopPath = GetCurrentDesktopPath(comboBox1);
        RefreshListView(listView1, desktopPath);
    }

    private void LoadCommonDesktopFiles()
    {
        string commonDesktopPath = GetCurrentDesktopPath(comboBox2);
        RefreshListView(listView2, commonDesktopPath);
    }

    private void LoadThirdDesktopFiles()
    {
        string thirdDesktopPath = GetCurrentDesktopPath(comboBox3);
        RefreshListView(listView3, thirdDesktopPath);
    }

    private ListViewItem? CreateListViewItem(FileInfo file, ImageList imageList)
    {
        try
        {
            string displayName = file.Name;
            Icon? icon = null;

            // Если это .lnk файл, получаем иконку целевого файла
            if (file.Extension.ToLower() == ".lnk")
            {
                string targetPath = GetLnkTargetPath(file.FullName);
                if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
                {
                    icon = Icon.ExtractAssociatedIcon(targetPath);
                }
            }

            // Если не удалось получить иконку ярлыка, используем иконку самого файла
            if (icon == null && File.Exists(file.FullName))
            {
                icon = Icon.ExtractAssociatedIcon(file.FullName);
            }

            // Если иконка не найдена, используем иконку по умолчанию
            if (icon == null)
            {
                icon = SystemIcons.Application;
            }

            // Добавляем иконку в ImageList
            int iconIndex = imageList.Images.Count;
            imageList.Images.Add(icon!);

            // Создаем элемент ListView
            var listItem = new ListViewItem
            {
                Text = displayName,
                ImageIndex = iconIndex,
                Tag = file
            };

            return listItem;
        }
        catch
        {
            return null;
        }
    }

    private ListViewItem? CreateListViewItem(DirectoryInfo directory, ImageList imageList)
    {
        try
        {
            string displayName = directory.Name;
            Icon? icon = GetSystemIcon(directory.FullName, true) ?? SystemIcons.Application;

            int iconIndex = imageList.Images.Count;
            imageList.Images.Add(icon);

            var listItem = new ListViewItem
            {
                Text = displayName,
                ImageIndex = iconIndex,
                Tag = directory
            };

            return listItem;
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
            // Используем WScript.Shell для получения целевого пути ярлыка
            Type shellType = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8"));
            if (shellType == null)
            {
                return string.Empty;
            }
            
            object? shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                return string.Empty;
            }
            
            dynamic shellObj = shell;
            var shortcut = shellObj.CreateShortcut(lnkPath);
            string targetPath = shortcut.TargetPath;
            
            // Освобождаем COM-объекты
            Marshal.ReleaseComObject(shell);
            
            return targetPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void InitializeFileSystemWatchers()
    {
        // Настройка наблюдателя для личного рабочего стола
        string desktopPath = GetCurrentDesktopPath(comboBox1);
        desktopWatcher = new FileSystemWatcher(desktopPath);
        desktopWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
        desktopWatcher.Created += (s, e) => UpdateListView(listView1, GetDesktopPathForListView(listView1));
        desktopWatcher.Deleted += (s, e) => UpdateListView(listView1, GetDesktopPathForListView(listView1));
        desktopWatcher.Renamed += (s, e) => UpdateListView(listView1, GetDesktopPathForListView(listView1));
        desktopWatcher.Changed += (s, e) => UpdateListView(listView1, GetDesktopPathForListView(listView1));
        desktopWatcher.EnableRaisingEvents = true;

        // Настройка наблюдателя для общего рабочего стола
        string commonDesktopPath = GetCurrentDesktopPath(comboBox2);
        commonDesktopWatcher = new FileSystemWatcher(commonDesktopPath);
        commonDesktopWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
        commonDesktopWatcher.Created += (s, e) => UpdateListView(listView2, GetDesktopPathForListView(listView2));
        commonDesktopWatcher.Deleted += (s, e) => UpdateListView(listView2, GetDesktopPathForListView(listView2));
        commonDesktopWatcher.Renamed += (s, e) => UpdateListView(listView2, GetDesktopPathForListView(listView2));
        commonDesktopWatcher.Changed += (s, e) => UpdateListView(listView2, GetDesktopPathForListView(listView2));
        commonDesktopWatcher.EnableRaisingEvents = true;

        string thirdDesktopPath = GetCurrentDesktopPath(comboBox3);
        thirdDesktopWatcher = new FileSystemWatcher(thirdDesktopPath);
        thirdDesktopWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
        thirdDesktopWatcher.Created += (s, e) => UpdateListView(listView3, GetDesktopPathForListView(listView3));
        thirdDesktopWatcher.Deleted += (s, e) => UpdateListView(listView3, GetDesktopPathForListView(listView3));
        thirdDesktopWatcher.Renamed += (s, e) => UpdateListView(listView3, GetDesktopPathForListView(listView3));
        thirdDesktopWatcher.Changed += (s, e) => UpdateListView(listView3, GetDesktopPathForListView(listView3));
        thirdDesktopWatcher.EnableRaisingEvents = true;
    }

    private void PopulateUserComboBoxes()
    {
        // Добавляем опцию "Общая папка" в три списка
        comboBox1.Items.Add("Общая папка");
        comboBox2.Items.Add("Общая папка");
        comboBox3.Items.Add("Общая папка");

        // Получаем список профилей пользователей из реестра
        try
        {
            using (RegistryKey? profileList = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
            {
                if (profileList != null)
                {
                    foreach (string sidName in profileList.GetSubKeyNames())
                    {
                        using (RegistryKey? profile = profileList.OpenSubKey(sidName))
                        {
                            if (profile != null)
                            {
                                string? profilePath = profile.GetValue("ProfileImagePath") as string;
                                if (!string.IsNullOrEmpty(profilePath))
                                {
                                    string userName = Path.GetFileName(profilePath);

                                    // Пропускаем системные профили
                                    if (!userName.StartsWith("systemprofile") &&
                                        !userName.StartsWith("LocalService") &&
                                        !userName.StartsWith("NetworkService"))
                                    {
                                        // Проверяем перенаправление Desktop через User Shell Folders
                                        string desktopPath = GetUserDesktopPath(sidName, profilePath);

                                        // Проверяем существование папки Desktop
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
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при чтении реестра: {ex.Message}",
                "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Устанавливаем текущего пользователя для первой панели
        string currentUser = Environment.UserName;
        int currentUserIndex = -1;
        for (int i = 0; i < comboBox1.Items.Count; i++)
        {
            if (comboBox1.Items[i] is UserProfileItem item && item.UserName == currentUser)
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
            // Пытаемся прочитать перенаправленный путь из User Shell Folders для конкретного SID
            using (RegistryKey? userKey = Registry.Users.OpenSubKey($"{sid}\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders"))
            {
                if (userKey != null)
                {
                    string? desktopPath = userKey.GetValue("Desktop") as string;
                    if (!string.IsNullOrEmpty(desktopPath))
                    {
                        // Разворачиваем переменные окружения
                        desktopPath = Environment.ExpandEnvironmentVariables(desktopPath);
                        if (Directory.Exists(desktopPath))
                        {
                            return desktopPath;
                        }
                    }
                }
            }
        }
        catch
        {
            // Игнорируем ошибки доступа к реестру других пользователей
        }

        // Проверяем стандартный путь
        string standardPath = Path.Combine(profilePath, "Desktop");
        if (Directory.Exists(standardPath))
        {
            return standardPath;
        }

        // Проверяем альтернативные пути на других дисках
        string userName = Path.GetFileName(profilePath);
        string[] driveLetters = { "D", "E", "F" };

        foreach (string drive in driveLetters)
        {
            string altPath = Path.Combine($"{drive}:\\Users", userName, "Desktop");
            if (Directory.Exists(altPath))
            {
                return altPath;
            }
        }

        return standardPath;
    }

    private void ComboBox1_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (comboBox1.SelectedItem == null) return;

        string selectedPath = GetCurrentDesktopPath(comboBox1);

        // Обновляем FileSystemWatcher только если он уже инициализирован
        if (desktopWatcher != null)
        {
            desktopWatcher.EnableRaisingEvents = false;
            desktopWatcher.Path = selectedPath;
            desktopWatcher.EnableRaisingEvents = true;
        }

        // Обновляем ListView
        RefreshListView(listView1, selectedPath);
        UpdateItemCount(lblCount1, listView1, selectedPath);
    }

    private void ComboBox2_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (comboBox2.SelectedItem == null) return;

        string selectedPath = GetCurrentDesktopPath(comboBox2);

        // Обновляем FileSystemWatcher только если он уже инициализирован
        if (commonDesktopWatcher != null)
        {
            commonDesktopWatcher.EnableRaisingEvents = false;
            commonDesktopWatcher.Path = selectedPath;
            commonDesktopWatcher.EnableRaisingEvents = true;
        }

        // Обновляем ListView
        RefreshListView(listView2, selectedPath);
        UpdateItemCount(lblCount2, listView2, selectedPath);
    }

    private void ComboBox3_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (comboBox3.SelectedItem == null) return;

        string selectedPath = GetCurrentDesktopPath(comboBox3);

        if (thirdDesktopWatcher != null)
        {
            thirdDesktopWatcher.EnableRaisingEvents = false;
            thirdDesktopWatcher.Path = selectedPath;
            thirdDesktopWatcher.EnableRaisingEvents = true;
        }

        RefreshListView(listView3, selectedPath);
        UpdateItemCount(lblCount3, listView3, selectedPath);
    }

    private void BtnOpenFolder1_Click(object? sender, EventArgs e)
    {
        string folderPath = GetCurrentDesktopPath(comboBox1);
        OpenFolderInExplorer(folderPath);
    }

    private void BtnOpenFolder2_Click(object? sender, EventArgs e)
    {
        string folderPath = GetCurrentDesktopPath(comboBox2);
        OpenFolderInExplorer(folderPath);
    }

    private void BtnOpenFolder3_Click(object? sender, EventArgs e)
    {
        string folderPath = GetCurrentDesktopPath(comboBox3);
        OpenFolderInExplorer(folderPath);
    }

    private void OpenFolderInExplorer(string folderPath)
    {
        try
        {
            if (Directory.Exists(folderPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", folderPath);
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
        {
            label.Text = $"Элементов: {count}  |  {folderPath}";
        }
        else
        {
            label.Text = $"Элементов: {count}";
        }
    }

    private string GetCurrentDesktopPath(ComboBox comboBox)
    {
        if (comboBox.SelectedItem == null)
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        if (comboBox.SelectedItem is string && comboBox.SelectedItem.ToString() == "Общая папка")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        }
        else if (comboBox.SelectedItem is UserProfileItem item)
        {
            return item.DesktopPath;
        }

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

    private class UserProfileItem
    {
        public string UserName { get; }
        public string DesktopPath { get; }

        public UserProfileItem(string userName, string desktopPath)
        {
            UserName = userName;
            DesktopPath = desktopPath;
        }

        public override string ToString()
        {
            return UserName;
        }
    }

    private void UpdateListView(ListView listView, string folderPath)
    {
        // Проверяем, нужно ли вызвать метод в UI-потоке
        if (listView.InvokeRequired)
        {
            listView.Invoke(new Action(() => RefreshListView(listView, folderPath)));
        }
        else
        {
            RefreshListView(listView, folderPath);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            desktopWatcher?.Dispose();
            commonDesktopWatcher?.Dispose();
            thirdDesktopWatcher?.Dispose();
        }
        base.Dispose(disposing);
    }
}