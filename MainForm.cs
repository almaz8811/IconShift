using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

namespace IconShift;

public partial class MainForm : Form
{
    private ListView listView1;
    private ImageList imageList1;
    private ListView listView2;
    private ImageList imageList2;

    public MainForm()
    {
        InitializeComponent();
        LoadDesktopFiles();
        LoadCommonDesktopFiles();
    }

    private void InitializeComponent()
    {
        // Настройка ListView для рабочего стола
        listView1 = new ListView();
        imageList1 = new ImageList();

        // Настройка ListView для общих ярлыков
        listView2 = new ListView();
        imageList2 = new ImageList();

        // Настройка ImageList1
        imageList1.ColorDepth = ColorDepth.Depth32Bit;
        imageList1.ImageSize = new Size(48, 48);

        // Настройка ImageList2
        imageList2.ColorDepth = ColorDepth.Depth32Bit;
        imageList2.ImageSize = new Size(48, 48);

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

        // Настройка формы
        Text = "IconShift - Рабочий стол";
        
        // Устанавливаем размер формы - 60% от экрана
        int screenWidth = Screen.PrimaryScreen!.Bounds.Width;
        int screenHeight = Screen.PrimaryScreen!.Bounds.Height;
        ClientSize = new Size((int)(screenWidth * 0.6), (int)(screenHeight * 0.6));
        
        // Регистрируем обработчик изменения размера окна
        Resize += MainForm_Resize;
        
        // Устанавливаем размеры панелей для растягивания на всё окно
        int panelWidth = (ClientSize.Width - 20) / 2;
        listView1.Width = panelWidth;
        listView2.Width = panelWidth;
        
        // Устанавливаем позиции
        listView1.Location = new Point(10, 10);
        listView2.Location = new Point(panelWidth + 10, 10);
        
        // Растягиваем ListView на всю доступную высоту
        listView1.Height = ClientSize.Height - 20;
        listView2.Height = ClientSize.Height - 20;
        
        Controls.Add(listView2);
        Controls.Add(listView1);
        StartPosition = FormStartPosition.CenterScreen;
    }

    private void MainForm_Resize(object? sender, EventArgs e)
    {
        // Адаптируем размеры панелей при изменении размера окна
        int panelWidth = (ClientSize.Width - 20) / 2;
        listView1.Width = panelWidth;
        listView2.Width = panelWidth;
        
        // Устанавливаем позиции
        listView1.Location = new Point(10, 10);
        listView2.Location = new Point(panelWidth + 10, 10);
        
        // Растягиваем ListView на всю доступную высоту
        listView1.Height = ClientSize.Height - 20;
        listView2.Height = ClientSize.Height - 20;
    }

    private void ListView1_ItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (e.Item is ListViewItem item && (item.Tag is FileInfo || item.Tag is DirectoryInfo))
        {
            DoDragDrop(item, DragDropEffects.Move | DragDropEffects.Copy);
        }
    }

    private void ListView2_ItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (e.Item is ListViewItem item && (item.Tag is FileInfo || item.Tag is DirectoryInfo))
        {
            DoDragDrop(item, DragDropEffects.Move | DragDropEffects.Copy);
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
        HandleDragDrop(e, listView1, listView2, 
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
    }

    private void ListView2_DragDrop(object? sender, DragEventArgs e)
    {
        HandleDragDrop(e, listView2, listView1,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
    }

    private void HandleDragDrop(DragEventArgs e, ListView targetListView, ListView sourceListView, string targetPath, string sourcePath)
    {
        ListViewItem? draggedItem = null;
        object? tag = null;
        string? sourceFilePath = null;
        string? targetFilePath = null;
        
        try
        {
            if (e.Data?.GetData(typeof(ListViewItem)) is ListViewItem item)
            {
                draggedItem = item;
                
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
                RefreshListView(sourceListView, sourcePath);
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
                        RefreshListView(sourceListView, sourcePath);
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
            ImageList imageList = listView == listView1 ? imageList1 : imageList2;

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
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при обновлении списка: {ex.Message}", 
                "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadDesktopFiles()
    {
        try
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            DirectoryInfo desktopDir = new DirectoryInfo(desktopPath);

            // Получаем все файлы и папки на рабочем столе
            var items = new List<ListViewItem>();

            // Сначала добавляем папки
            foreach (var directory in desktopDir.EnumerateDirectories())
            {
                var listItem = CreateListViewItem(directory, imageList1);
                if (listItem != null)
                {
                    items.Add(listItem);
                }
            }

            // Потом добавляем файлы
            foreach (var file in desktopDir.EnumerateFiles())
            {
                var listItem = CreateListViewItem(file, imageList1);
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

            listView1.Items.AddRange(items.ToArray());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при загрузке файлов рабочего стола: {ex.Message}", 
                "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadCommonDesktopFiles()
    {
        try
        {
            string commonDesktopPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            DirectoryInfo commonDesktopDir = new DirectoryInfo(commonDesktopPath);

            // Получаем все файлы и папки в общих ярлыках
            var items = new List<ListViewItem>();

            // Сначала добавляем папки
            foreach (var directory in commonDesktopDir.EnumerateDirectories())
            {
                var listItem = CreateListViewItem(directory, imageList2);
                if (listItem != null)
                {
                    items.Add(listItem);
                }
            }

            // Потом добавляем файлы
            foreach (var file in commonDesktopDir.EnumerateFiles())
            {
                var listItem = CreateListViewItem(file, imageList2);
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

            listView2.Items.AddRange(items.ToArray());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при загрузке общих ярлыков: {ex.Message}", 
                "IconShift", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
            Icon icon = SystemIcons.Application;

            // Добавляем иконку в ImageList
            int iconIndex = imageList.Images.Count;
            imageList.Images.Add(icon);

            // Создаем элемент ListView
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
}
