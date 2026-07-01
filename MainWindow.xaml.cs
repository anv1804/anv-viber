using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using ViberManager.Services;
using ViberManager.Models;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using System.Drawing;
using System.Drawing.Imaging;

namespace ViberManager
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public ObservableCollection<ViberProfile> Profiles { get; set; }
        public ObservableCollection<VerifyResult> VerifyResults { get; set; } = new();
        public ObservableCollection<VerifyResult> HistoryResults { get; set; } = new();

        private readonly string _historyDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verifier_history.db");

        private int _historyCurrentPage = 1;
        private const int HistoryPageSize = 100;
        private int _historyTotalItems = 0;
        private int _historyTotalPages = 1;

        private List<VerifyResult> _allVerifyResultsMaster = new List<VerifyResult>();
        private int _currentPageTab1 = 1;
        private const int PageSizeTab1 = 100;
        private int _totalItemsTab1 = 0;
        private int _totalPagesTab1 = 1;

        public string ViberPath { get; set; } = string.Empty;

        private bool _isAllSelected = false;
        public bool IsAllSelected
        {
            get => _isAllSelected;
            set
            {
                if (_isAllSelected != value)
                {
                    _isAllSelected = value;
                    OnPropertyChanged(nameof(IsAllSelected));
                    
                    // Khi người dùng bấm tick chọn tất cả ở Header, thay đổi toàn bộ trạng thái dòng con
                    if (_isUpdatingAllSelected == false)
                    {
                        _isUpdatingAllSelected = true;
                        foreach (var p in Profiles)
                        {
                            p.IsSelected = value;
                        }
                        _isUpdatingAllSelected = false;
                        UpdateSelectedCount();
                    }
                }
            }
        }
        private bool _isUpdatingAllSelected = false;
        
        private readonly ProfileRepository _profileRepo;
        private readonly ViberManagerConfig _config;
        private ViberService? _viberService;
        private ViberProfile? _currentActiveProfile;
        private ViberHost? _currentHost;
        private readonly DispatcherTimer _windowScannerTimer;
        // Timer kiểm tra focus state của Viber để cập nhật màu nút Focus bàn phím
        private readonly DispatcherTimer _focusCheckTimer;
        // Timer quét đồng bộ SĐT & Tên Viber thật từ Database
        private readonly DispatcherTimer _profileDataSyncTimer;
        // CollectionViewSource cho phép filter/search danh sách profile
        private ICollectionView? _profilesView;
        // Cờ lưu trạng thái reset ngầm ở click chuột đầu tiên để sửa lỗi lệch Qt
        private bool _hasAutoResetOnFirstClick = false;

        // Quản lý luồng kiểm tra SĐT hàng loạt
        private bool _isVerifyingPhones = false;
        private System.Threading.CancellationTokenSource? _verifyPhonesCts;
        // ID phiên quét hiện tại – tạo mới mỗi khi người dùng bấm "Bắt đầu kiểm tra"
        private string _currentSessionId = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            _profileRepo = new ProfileRepository();
            _config = _profileRepo.LoadConfig();
            
            Profiles = _config.Profiles;
            ViberPath = _config.ViberPath;

            _windowScannerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _windowScannerTimer.Tick += WindowScannerTimer_Tick;

            // Timer 300ms: poll focus state -> cập nhật màu nút Focus bàn phím
            _focusCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _focusCheckTimer.Tick += FocusCheckTimer_Tick;
            _focusCheckTimer.Start();

            DetectViberPath();

            // Khởi tạo CollectionViewSource để support filter/search
            _profilesView = CollectionViewSource.GetDefaultView(Profiles);
            _profilesView.Filter = ProfileFilter;
            GridProfiles.ItemsSource = _profilesView;
            UpdateProfileCount();

            GridVerifyResults.ItemsSource = VerifyResults;
            GridHistoryResults.ItemsSource = HistoryResults;

            // Khởi tạo Database SQLite cục bộ lưu lịch sử verify
            InitHistoryDatabase();

            // Đăng ký callback ẩn/hiện loading overlay phục vụ chụp ảnh màn hình Viber không bị đè màu tối
            ViberAutomationService.ToggleLoadingOverlay = (visible) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (PopupViberLoading != null && _isVerifyingPhones)
                    {
                        PopupViberLoading.IsOpen = visible;
                        UpdatePopupPosition();
                    }
                });
            };

            // Cấu hình chọn Viber profile làm mẫu test (Sử dụng ListCollectionView mới để độc lập bộ lọc với danh sách chính)
            var verifyView = new System.Windows.Data.ListCollectionView(Profiles);
            verifyView.Filter = item =>
            {
                var profile = item as ViberProfile;
                return profile != null && profile.Status == "Đang nhúng";
            };
            CmbVerifyProfile.ItemsSource = verifyView;
            if (CmbVerifyProfile.Items.Count > 0)
            {
                CmbVerifyProfile.SelectedIndex = 0;
            }

            // Timer 3 giây: định kỳ quét đồng bộ SĐT & Tên Viber thật từ Database nếu có thay đổi hoặc mới login
            _profileDataSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _profileDataSyncTimer.Tick += ProfileDataSyncTimer_Tick;
            _profileDataSyncTimer.Start();

             // Bật cờ khởi tạo xong để cho phép lọc và search
             _isInitialized = true;
             
             // Theo dõi PropertyChanged của các profile để cập nhật số lượng được chọn
             foreach (var p in Profiles)
             {
                 p.PropertyChanged += Profile_PropertyChanged;
             }
             Profiles.CollectionChanged += (s, e) =>
             {
                 if (e.NewItems != null)
                 {
                     foreach (ViberProfile p in e.NewItems) p.PropertyChanged += Profile_PropertyChanged;
                 }
                 UpdateSelectedCount();
             };

             RefreshProfiles();

             // Đồng bộ vị trí PopupViberLoading theo cửa sổ chính và tránh đè app khác
             this.LocationChanged += MainWindow_LocationChanged;
             this.SizeChanged += MainWindow_SizeChanged;
             this.Activated += MainWindow_Activated;
             this.Deactivated += MainWindow_Deactivated;
             this.StateChanged += MainWindow_StateChanged;
         }

         private void MainWindow_LocationChanged(object? sender, EventArgs e)
         {
             UpdatePopupPosition();
         }

         private void MainWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
         {
             UpdatePopupPosition();
         }

         private void MainWindow_Activated(object? sender, EventArgs e)
         {
             if (_isVerifyingPhones && PopupViberLoading != null)
             {
                 PopupViberLoading.IsOpen = true;
                 UpdatePopupPosition();
             }
         }

         private void MainWindow_Deactivated(object? sender, EventArgs e)
         {
             if (PopupViberLoading != null)
             {
                 PopupViberLoading.IsOpen = false;
             }
         }

         private void MainWindow_StateChanged(object? sender, EventArgs e)
         {
             if (PopupViberLoading != null)
             {
                 if (this.WindowState == WindowState.Minimized)
                 {
                     PopupViberLoading.IsOpen = false;
                 }
                 else if (this.WindowState != WindowState.Minimized && _isVerifyingPhones)
                 {
                     PopupViberLoading.IsOpen = true;
                     UpdatePopupPosition();
                 }
             }
         }

         private void UpdatePopupPosition()
         {
             if (PopupViberLoading != null && PopupViberLoading.IsOpen)
             {
                 // Đẩy nhẹ offset để ép Popup tính lại tọa độ chính xác theo PlacementTarget (ViberContainer)
                 double offset = PopupViberLoading.HorizontalOffset;
                 PopupViberLoading.HorizontalOffset = offset + 0.01;
                 PopupViberLoading.HorizontalOffset = offset;
             }
         }

         private void Profile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
         {
             if (e.PropertyName == nameof(ViberProfile.IsSelected))
             {
                 UpdateSelectedCount();
             }
         }

         private void UpdateSelectedCount()
         {
             if (TxtSelectedCount == null) return;
             int count = 0;
             foreach (var p in Profiles)
             {
                 if (p.IsSelected) count++;
             }
             TxtSelectedCount.Text = count.ToString();

             // Đồng bộ ngược trạng thái Checkbox Chọn tất cả ở Header
             if (_isUpdatingAllSelected == false)
             {
                 _isUpdatingAllSelected = true;
                 if (count == 0)
                 {
                     IsAllSelected = false;
                 }
                 else if (count == Profiles.Count)
                 {
                     IsAllSelected = true;
                 }
                 else
                 {
                     IsAllSelected = false; // Tắt trạng thái chọn tất cả nếu chỉ chọn một phần
                 }
                 _isUpdatingAllSelected = false;
             }

             // Chuyển đổi ẩn hiện động các cụm nút để chống đè lấn diện tích
             if (PanelSingleActions != null && PanelBulkActions != null)
             {
                 if (count >= 2)
                 {
                     PanelSingleActions.Visibility = Visibility.Collapsed;
                     PanelBulkActions.Visibility = Visibility.Visible;
                 }
                 else
                 {
                     PanelSingleActions.Visibility = Visibility.Visible;
                     PanelBulkActions.Visibility = Visibility.Collapsed;
                 }
             }
         }

        // ================================================================
        // SEARCH & FILTER: lọc danh sách tài khoản Viber
        // ================================================================

        private bool ProfileFilter(object obj)
        {
            if (obj is not ViberProfile profile) return false;

            // Lọc theo text tìm kiếm (tên hoặc SĐT)
            string searchText = TxtSearchProfile?.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(searchText))
            {
                bool nameMatch  = profile.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase);
                bool phoneMatch = profile.Phone.Contains(searchText, StringComparison.OrdinalIgnoreCase);
                if (!nameMatch && !phoneMatch) return false;
            }

            // Lọc theo trạng thái
            string statusFilter = (CmbStatusFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Tất cả";
            if (statusFilter != "Tất cả" && profile.Status != statusFilter) return false;

            return true;
        }

        private bool _isInitialized = false;

        private void UpdateProfileCount()
        {
            if (!_isInitialized || TxtProfileCount == null) return;
            int count = _profilesView?.Cast<object>().Count() ?? Profiles?.Count ?? 0;
            TxtProfileCount.Text = $"{count} tài khoản";
        }

        private void RefreshProfiles()
        {
            if (!_isInitialized || _profilesView == null) return;
            // Cập nhật STT (DisplayIndex) cho từng profile trong danh sách đã lọc
            int idx = 1;
            foreach (ViberProfile p in _profilesView)
                p.DisplayIndex = idx++;
            _profilesView.Refresh();

            // Refresh danh sách tài khoản test để phản ánh trạng thái 'Đang nhúng'
            var verifyView = CmbVerifyProfile.ItemsSource as System.Windows.Data.ListCollectionView;
            if (verifyView != null)
            {
                verifyView.Refresh();
                
                // Tự động chọn tài khoản đầu tiên nếu chưa chọn gì hoặc tài khoản cũ không còn 'Đang nhúng'
                if (CmbVerifyProfile.SelectedIndex == -1 && CmbVerifyProfile.Items.Count > 0)
                {
                    CmbVerifyProfile.SelectedIndex = 0;
                }
            }

            UpdateProfileCount();
        }

        private void TxtSearchProfile_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isInitialized) return;
            RefreshProfiles();
        }

        private void CmbStatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            RefreshProfiles();
        }



        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        private bool _lastViberFocused = false;

        private void FocusCheckTimer_Tick(object? sender, EventArgs e)
        {
            if (_currentHost == null || _currentHost.IsDetached)
            {
                // Không có Viber nào đang nhúng → nút trắng
                if (_lastViberFocused)
                {
                    SetFocusButtonActive(false);
                    _lastViberFocused = false;
                }
                return;
            }

            // Kiểm tra focus window hiện tại có thuộc Viber không
            IntPtr focused = GetFocus();
            IntPtr viberHwnd = _currentActiveProfile?.WindowHandle ?? IntPtr.Zero;
            bool isFocused = (viberHwnd != IntPtr.Zero) &&
                             (focused == viberHwnd || IsChild(viberHwnd, focused));

            if (isFocused != _lastViberFocused)
            {
                SetFocusButtonActive(isFocused);
                _lastViberFocused = isFocused;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        private void SetFocusButtonActive(bool active)
        {
            if (active)
            {
                BtnFocusKeyboard.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#7360F2"));
                BtnFocusKeyboard.Foreground = System.Windows.Media.Brushes.White;
                BtnFocusKeyboard.BorderBrush = System.Windows.Media.Brushes.Transparent;
            }
            else
            {
                // Reset về outline style
                BtnFocusKeyboard.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                BtnFocusKeyboard.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
                BtnFocusKeyboard.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            }
        }

        private void DetectViberPath()
        {
            // Nếu đã lưu cấu hình đường dẫn cũ và file có tồn tại thì dùng luôn
            if (!string.IsNullOrEmpty(ViberPath) && File.Exists(ViberPath))
            {
                _viberService = new ViberService(ViberPath);
                TxtViberPathDisplay.Text = $"Đường dẫn: {ViberPath}";
                TxtViberPathDisplay.ToolTip = ViberPath;
                TxtStatus.Text = $"Đã nạp đường dẫn Viber từ cấu hình: {ViberPath}";
                return;
            }

            // Nếu chưa cấu hình, thử quét thư mục chuẩn
            string userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string standardPath = Path.Combine(userFolder, @"AppData\Local\Viber\Viber.exe");

            if (File.Exists(standardPath))
            {
                ViberPath = standardPath;
                _config.ViberPath = ViberPath;
                _viberService = new ViberService(ViberPath);
                _profileRepo.SaveConfig(_config);
                TxtViberPathDisplay.Text = $"Đường dẫn: {ViberPath}";
                TxtViberPathDisplay.ToolTip = ViberPath;
                TxtStatus.Text = $"Tự động nhận dạng Viber: {ViberPath}";
            }
            else
            {
                TxtViberPathDisplay.Text = "Đường dẫn: Chưa cấu hình";
                TxtStatus.Text = "Không tìm thấy Viber.exe mặc định. Vui lòng cấu hình thủ công đường dẫn.";
            }
        }

        private void BtnConfigViberPath_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Các file thực thi (*.exe)|*.exe|Tất cả file (*.*)|*.*",
                Title = "Chọn file chạy Viber (Viber.exe hoặc file cài đặt)"
            };

            if (dlg.ShowDialog() == true)
            {
                string selectedPath = dlg.FileName;
                
                if (selectedPath.Contains("setup", StringComparison.OrdinalIgnoreCase) || 
                    selectedPath.Contains("install", StringComparison.OrdinalIgnoreCase))
                {
                    var msgResult = MessageBox.Show(
                        "Cảnh báo: Bạn dường như đang chọn file cài đặt Viber (Setup/Installer) chứ không phải file chạy Viber.exe đã cài.\n\n" +
                        "Nếu chọn file Setup, mỗi khi bạn tạo Profile mới, Viber sẽ yêu cầu cài đặt lại từ đầu.\n\n" +
                        "Thư mục cài mặc định của Viber thường nằm ở:\nC:\\Users\\<Tên_User>\\AppData\\Local\\Viber\\Viber.exe\n(bạn có thể dán %localappdata%\\Viber vào thanh địa chỉ của cửa sổ chọn file để tìm).\n\n" +
                        "Bạn có chắc chắn muốn tiếp tục sử dụng file này không?", 
                        "Cấu hình cảnh báo", 
                        MessageBoxButton.YesNo, 
                        MessageBoxImage.Warning);
                        
                    if (msgResult == MessageBoxResult.No)
                    {
                        return;
                    }
                }

                ViberPath = selectedPath;
                _config.ViberPath = ViberPath;
                _viberService = new ViberService(ViberPath);
                
                // Lưu lại cấu hình ngay lập tức để lần sau tự nhận diện
                _profileRepo.SaveConfig(_config);
                
                TxtViberPathDisplay.Text = $"Đường dẫn: {ViberPath}";
                TxtViberPathDisplay.ToolTip = ViberPath;
                TxtStatus.Text = $"Đã lưu đường dẫn Viber: {ViberPath}";
                MessageBox.Show($"Đã lưu đường dẫn Viber cố định: {ViberPath}", "Cấu hình thành công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnInstallViberToE_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Viber Setup (*.exe)|*.exe",
                Title = "Chọn file bộ cài ViberSetup.exe đã tải về"
            };

            if (dlg.ShowDialog() == true)
            {
                string setupPath = dlg.FileName;
                string targetLocalAppData = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ViberApp");
                Directory.CreateDirectory(targetLocalAppData);

                TxtStatus.Text = "Đang khởi chạy bộ cài Viber...";

                try
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = setupPath,
                        UseShellExecute = false
                    };
                    
                    // Thử chuyển hướng LOCALAPPDATA
                    startInfo.EnvironmentVariables["LOCALAPPDATA"] = targetLocalAppData;
                    
                    Process? process = Process.Start(startInfo);
                    if (process != null)
                    {
                        MessageBox.Show(
                            "Trình cài đặt Viber đã chạy.\n\n" +
                            "1. Vui lòng tiến hành cài đặt Viber bình thường trên màn hình cài đặt.\n" +
                            "2. Chờ cho quá trình cài đặt chạy xong hoàn toàn (và mở Viber lên).\n" +
                            "3. Quay lại đây và nhấn nút OK để phần mềm tự động định vị và di chuyển Viber sang ổ E.",
                            "Cài đặt Viber",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);

                        // Tắt tiến trình Viber vừa chạy tự động sau khi cài đặt để giải phóng file lock
                        KillAllViberProcesses();

                        string installedOnE = Path.Combine(targetLocalAppData, @"Viber\Viber.exe");
                        string defaultOnC = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Viber");
                        string defaultViberOnC = Path.Combine(defaultOnC, "Viber.exe");

                        // 1. Nếu Viber cài thẳng vào ổ E thành công
                        if (File.Exists(installedOnE))
                        {
                            SetViberPathAndSave(installedOnE);
                            MessageBox.Show($"Cài đặt thành công! Viber gốc hiện chạy trên ổ E tại:\n{installedOnE}", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        // 2. Nếu Viber cài mặc định vào ổ C (do bộ cài bỏ qua biến môi trường)
                        else if (File.Exists(defaultViberOnC))
                        {
                            TxtStatus.Text = "Phát hiện Viber trên ổ C. Đang di chuyển dữ liệu sang ổ E...";
                            string targetViberDir = Path.Combine(targetLocalAppData, "Viber");

                            if (MoveDirectory(defaultOnC, targetViberDir))
                            {
                                string newInstalledPath = Path.Combine(targetViberDir, "Viber.exe");
                                SetViberPathAndSave(newInstalledPath);
                                MessageBox.Show($"Đã di chuyển Viber gốc sang ổ E để tiết kiệm dung lượng C! Đường dẫn hiện tại:\n{newInstalledPath}", "Di chuyển thành công", MessageBoxButton.OK, MessageBoxImage.Information);
                            }
                            else
                            {
                                MessageBox.Show($"Không thể di chuyển thư mục từ ổ C sang ổ E. Vui lòng kiểm tra quyền hoặc tắt Viber.", "Lỗi di chuyển", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        }
                        else
                        {
                            MessageBox.Show("Không tìm thấy Viber.exe. Vui lòng kiểm tra lại xem cài đặt Viber đã hoàn tất chưa.", "Không tìm thấy file", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi chạy trình cài đặt: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void KillAllViberProcesses()
        {
            foreach (var proc in Process.GetProcessesByName("Viber"))
            {
                try { proc.Kill(); proc.WaitForExit(1000); } catch { }
            }
        }

        private void SetViberPathAndSave(string path)
        {
            ViberPath = path;
            _config.ViberPath = ViberPath;
            _viberService = new ViberService(ViberPath);
            _profileRepo.SaveConfig(_config);

            TxtViberPathDisplay.Text = $"Đường dẫn: {ViberPath}";
            TxtViberPathDisplay.ToolTip = ViberPath;
            TxtStatus.Text = $"Đã cấu hình Viber gốc: {ViberPath}";
        }

        private bool MoveDirectory(string source, string target)
        {
            try
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, true);
                }
                Directory.CreateDirectory(target);

                // Copy đệ quy
                foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                {
                    Directory.CreateDirectory(dir.Replace(source, target));
                }

                foreach (string file in Directory.GetFiles(source, "*.*", SearchOption.AllDirectories))
                {
                    File.Copy(file, file.Replace(source, target), true);
                }

                // Xóa thư mục gốc ở ổ C để giải phóng dung lượng
                Directory.Delete(source, true);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi di chuyển: {ex.Message}");
                return false;
            }
        }

        private void BtnAddViber_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddProfileDialog { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                // Tạo ID ngẫu nhiên không trùng lặp cho tên thư mục dữ liệu để tránh lấy chéo tên và SĐT khi đặt trùng tên gợi nhớ
                string randomId = Guid.NewGuid().ToString("N");
                string baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles", randomId);
                
                var newProfile = new ViberProfile
                {
                    Name = dialog.ProfileName,
                    Phone = dialog.ProfilePhone,
                    Status = "Đang đóng",
                    DataDirPath = baseDir
                };

                Profiles.Add(newProfile);
                _profileRepo.SaveConfig(_config);
                TxtStatus.Text = $"Đã thêm profile mới: {newProfile.Name}";
            }
        }

        private void BtnRename_Click(object sender, RoutedEventArgs e)
        {
            if (GridProfiles.SelectedItem is ViberProfile selected)
            {
                var dialog = new AddProfileDialog { Owner = this };
                dialog.TxtName.Text = selected.Name;
                dialog.TxtPhone.Text = selected.Phone;

                if (dialog.ShowDialog() == true)
                {
                    selected.Name = dialog.ProfileName;
                    selected.Phone = dialog.ProfilePhone;
                    GridProfiles.Items.Refresh();
                    _profileRepo.SaveConfig(_config);
                    TxtStatus.Text = $"Đã đổi tên profile thành: {selected.Name}";
                }
            }
            else
            {
                MessageBox.Show("Vui lòng chọn một profile từ danh sách để đổi tên!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (GridProfiles.SelectedItem is ViberProfile selected)
            {
                var result = MessageBox.Show($"Bạn có chắc chắn muốn xóa profile '{selected.Name}'?", "Xác nhận xóa", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    CloseViberProcess(selected);

                    try
                    {
                        if (Directory.Exists(selected.DataDirPath))
                        {
                            Directory.Delete(selected.DataDirPath, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        TxtStatus.Text = $"Không thể xóa thư mục dữ liệu: {ex.Message}";
                    }

                    Profiles.Remove(selected);
                    _profileRepo.SaveConfig(_config);
                    TxtStatus.Text = $"Đã xóa profile: {selected.Name}";
                }
            }
        }

        private void BtnKillAllViber_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show("Bạn có chắc chắn muốn CƯỠNG CHẾ TẮT toàn bộ tiến trình Viber đang chạy ngầm trên máy tính không?\n\n(Hành động này sẽ tắt sạch Viber và giải phóng bộ nhớ hệ thống)", "Cảnh báo tắt hàng loạt", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                TxtStatus.Text = "Đang cưỡng chế tắt toàn bộ các tiến trình Viber...";
                
                // Quét và kill sạch các tiến trình Viber.exe trên Windows
                Process[] processes = Process.GetProcessesByName("Viber");
                int killedCount = 0;
                foreach (var p in processes)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(1000);
                        killedCount++;
                    }
                    catch { }
                }

                // Reset trạng thái của tất cả profile hiển thị trên bảng về "Đang đóng"
                foreach (var profile in Profiles)
                {
                    profile.ProcessId = 0;
                    profile.WindowHandle = IntPtr.Zero;
                    profile.Status = "Đang đóng";
                }

                // Dọn dẹp giao diện hiển thị nhúng hiện tại nếu có
                ViberContainer.Children.Clear();
                PanelPlaceholder.Visibility = Visibility.Visible;
                TxtActiveProfile.Text = "Không có Viber nào đang hoạt động";
                _currentActiveProfile = null;
                _currentHost = null;

                // Làm mới bảng danh sách và lưu cấu hình
                GridProfiles.Items.Refresh();
                _profileRepo.SaveConfig(_config);

                TxtStatus.Text = $"Đã cưỡng chế tắt thành công {killedCount} tiến trình Viber!";
                MessageBox.Show($"Đã quét tắt sạch các tiến trình Viber chạy ngầm!\nTổng số tiến trình đã đóng: {killedCount}", "Kết quả", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Có lỗi xảy ra khi tắt các tiến trình Viber: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenViber_Click(object sender, RoutedEventArgs e)
        {
            if (GridProfiles.SelectedItem is ViberProfile selected)
            {
                if (selected.Status != "Đang đóng") return;

                var confirm = MessageBox.Show($"Bạn có muốn khởi chạy ứng dụng Viber cho tài khoản '{selected.Name}' không?", "Xác nhận mở", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;

                if (_viberService == null)
                {
                    MessageBox.Show("Cấu hình đường dẫn Viber.exe chưa chính xác!", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                try
                {
                    TxtStatus.Text = $"Đã khởi tạo tiến trình cho {selected.Name}...";
                    selected.Status = "Đang mở";
                    selected.LastUpdated = DateTime.Now;  // cập nhật timestamp
                    RefreshProfiles();
                    _profileRepo.SaveConfig(_config);   // lưu LastUpdated vào file

                    Process? process = _viberService.StartIsolatedViber(selected.Name, selected.DataDirPath);
                    if (process != null)
                    {
                        selected.ProcessId = process.Id;
                        _currentActiveProfile = selected;
                        _windowScannerTimer.Start();
                    }
                }
                catch (Exception ex)
                {
                    selected.Status = "Đang đóng";
                    RefreshProfiles();
                    MessageBox.Show($"Lỗi khởi động Viber: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void WindowScannerTimer_Tick(object? sender, EventArgs e)
        {
            if (_currentActiveProfile == null || _currentActiveProfile.ProcessId == 0)
            {
                _windowScannerTimer.Stop();
                return;
            }

            IntPtr hwnd = WindowManager.FindWindowByProcessId(_currentActiveProfile.ProcessId);
            if (hwnd != IntPtr.Zero)
            {
                _windowScannerTimer.Stop();
                _currentActiveProfile.WindowHandle = hwnd;
                _currentActiveProfile.Status = "Đang nhúng";
                _currentActiveProfile.LastUpdated = DateTime.Now;

                // Cập nhật thông tin thực tế khi nhúng thành công
                TryUpdateProfileRealData(_currentActiveProfile);

                RefreshProfiles();
                _profileRepo.SaveConfig(_config);

                EmbedViberWindow(hwnd);
            }
        }

        /// <summary>
        /// Quét định kỳ 3 giây một lần để đồng bộ dữ liệu SĐT và Tên thật từ Database của Viber
        /// </summary>
        private void ProfileDataSyncTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                bool needRefresh = false;
                
                // Quét toàn bộ danh sách profile để cập nhật tên thật (kể cả đang đóng)
                foreach (var profile in Profiles)
                {
                    if (TryUpdateProfileRealData(profile))
                    {
                        needRefresh = true;
                    }
                }

                if (needRefresh)
                {
                    // Cập nhật lại Grid UI và lưu cấu hình
                    Dispatcher.Invoke(() =>
                    {
                        GridProfiles.Items.Refresh();
                        _profileRepo.SaveConfig(_config);
                    });
                }
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(@"e:\viber-manager\sync_timer_error.txt", $"Timer Error at {DateTime.Now}: {ex.Message}\n{ex.StackTrace}");
                }
                catch { }
            }
        }

        private bool TryUpdateProfileRealData(ViberProfile profile)
        {
            try
            {
                string viberPcDir = Path.Combine(profile.DataDirPath, "AppData", "Roaming", "ViberPC");
                if (Directory.Exists(viberPcDir))
                {
                    // 1. Quét tìm thư mục SĐT để lấy Số điện thoại trước
                    var subDirs = Directory.GetDirectories(viberPcDir);
                    string phoneDigits = string.Empty;
                    foreach (var dir in subDirs)
                    {
                        string dirName = Path.GetFileName(dir);
                        if (dirName.Length >= 9 && dirName.All(char.IsDigit))
                        {
                            phoneDigits = dirName;
                            string newPhone = "+" + dirName;
                            if (profile.Phone != newPhone)
                            {
                                profile.Phone = newPhone;
                            }
                            break;
                        }
                    }

                    // 2. Đọc file config.db không mã hóa để lấy Tên thật (NickName) của tài khoản
                    string configDbPath = Path.Combine(viberPcDir, "config.db");
                    if (File.Exists(configDbPath))
                    {
                        string realName = TryGetRealViberNameFromConfigDb(configDbPath, phoneDigits);
                        if (!string.IsNullOrEmpty(realName))
                        {
                            // Thay thế nếu tên thật khác biệt hoặc tên hiện tại đang là số tạm: "2", "3", v.v.
                            bool nameIsTemporaryNumber = profile.Name.All(char.IsDigit);
                            if (profile.Name != realName || nameIsTemporaryNumber)
                            {
                                profile.Name = realName;
                                return true; // Trả về true báo có sự thay đổi
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Truy vấn tên thật (NickName) từ file config.db không bị mã hóa của Viber PC
        /// </summary>
        private string TryGetRealViberNameFromConfigDb(string dbPath, string phoneDigits)
        {
            string tempDbPath = Path.Combine(Path.GetTempPath(), "viber_config_temp_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                // Sử dụng FileShare.ReadWrite để mở và copy an toàn
                using (var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var tempFs = new FileStream(tempDbPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.CopyTo(tempFs);
                }

                string realName = string.Empty;
                string connectionString = $"Data Source={tempDbPath};";

                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
                {
                    connection.Open();

                    using (var command = connection.CreateCommand())
                    {
                        // Truy vấn lấy cột NickName từ bảng Accounts
                        command.CommandText = "SELECT NickName FROM Accounts WHERE ID = @phone LIMIT 1;";
                        command.Parameters.AddWithValue("@phone", phoneDigits);
                        
                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read() && !reader.IsDBNull(0))
                            {
                                realName = reader.GetString(0);
                            }
                        }
                    }

                    // Nếu không khớp SĐT không có dấu cộng, thử query toàn bộ NickName đầu tiên
                    if (string.IsNullOrEmpty(realName))
                    {
                        try
                        {
                            using (var command = connection.CreateCommand())
                            {
                                command.CommandText = "SELECT NickName FROM Accounts LIMIT 1;";
                                using (var reader = command.ExecuteReader())
                                {
                                    if (reader.Read() && !reader.IsDBNull(0))
                                    {
                                        realName = reader.GetString(0);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }

                if (!string.IsNullOrEmpty(realName) && realName.Trim().Length >= 2)
                {
                    // Loại bỏ các ký tự điều hướng Unicode nếu có (Viber đôi khi ghi kèm ký tự ẩn)
                    return realName.Replace("\u2068", "").Replace("\u2069", "").Trim();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi truy vấn config.db: {ex.Message}");
            }
            finally
            {
                // Dọn dẹp file temp an toàn trong khối finally
                try { if (File.Exists(tempDbPath)) File.Delete(tempDbPath); } catch { }
            }
            return string.Empty;
        }

        // Hàm helper tìm kiếm mảng byte
        private int FindBytes(byte[] src, byte[] pattern)
        {
            int maxFirstCharIdx = src.Length - pattern.Length;
            for (int i = 0; i <= maxFirstCharIdx; i++)
            {
                if (src[i] != pattern[0]) continue;
                
                bool found = true;
                for (int j = 1; j < pattern.Length; j++)
                {
                    if (src[i + j] != pattern[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found) return i;
            }
            return -1;
        }

        private void PhoneTextBlock_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.DataContext is ViberProfile profile)
            {
                string phone = profile.Phone ?? "";
                if (!string.IsNullOrEmpty(phone))
                {
                    // Chuyển đổi +84 hoặc 84 ở đầu thành 0
                    string cleanPhone = phone.Trim();
                    if (cleanPhone.StartsWith("+84"))
                    {
                        cleanPhone = "0" + cleanPhone.Substring(3);
                    }
                    else if (cleanPhone.StartsWith("84"))
                    {
                        cleanPhone = "0" + cleanPhone.Substring(2);
                    }

                    try
                    {
                        Clipboard.SetText(cleanPhone);
                        TxtStatus.Text = $"Đã copy số điện thoại {cleanPhone} vào Clipboard!";
                    }
                    catch (Exception ex)
                    {
                        TxtStatus.Text = $"Lỗi sao chép Clipboard: {ex.Message}";
                    }
                }
            }
        }

        private void EmbedViberWindow(IntPtr hwnd)
        {
            try
            {
                ViberContainer.Children.Clear();
                PanelPlaceholder.Visibility = Visibility.Collapsed;

                if (_currentActiveProfile != null)
                {
                    TxtActiveProfile.Text = $"Đang hiển thị: {_currentActiveProfile.Name} ({_currentActiveProfile.Phone})";
                }

                _currentHost = new ViberHost(hwnd, _currentActiveProfile?.DataDirPath ?? "")
                {
                    Width = ViberContainer.ActualWidth,
                    Height = ViberContainer.ActualHeight
                };

                ViberContainer.Children.Add(_currentHost);
                
                // Gọi Resize ngay lập tức để đồng bộ kích thước ban đầu với WPF Grid
                if (ViberContainer.ActualWidth > 0 && ViberContainer.ActualHeight > 0)
                {
                    _currentHost.Resize(ViberContainer.ActualWidth, ViberContainer.ActualHeight);
                }

                TxtStatus.Text = $"Đã nhúng Viber thành công.";
                
                // Reset cờ click lần đầu cho Viber mới nhúng
                _hasAutoResetOnFirstClick = false;

                // Tự động focus bàn phím mặc định ngay khi nhúng xong
                Task.Run(async () =>
                {
                    await Task.Delay(200);
                    Dispatcher.Invoke(() =>
                    {
                        if (_currentHost != null && !_currentHost.IsDetached)
                        {
                            _currentHost.FocusViber();
                            TxtStatus.Text = $"Đã nhúng Viber và tự động focus bàn phím.";
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Lỗi nhúng cửa sổ: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Lỗi nhúng cửa sổ: {ex.Message}");
            }
        }

        private void BtnToggleDetach_Click(object sender, RoutedEventArgs e)
        {
            if (_currentHost == null) return;

            if (!_currentHost.IsDetached)
            {
                // --- Tách Viber ra chạy độc lập ---
                _currentHost.Detach();

                // Cập nhật UI
                BtnToggleDetach.Content = "🔗 Gắn lại cửa sổ";
                PanelDetachedNotice.Visibility = Visibility.Visible;
                // Ẩn ViberHost nhưng giữ nguyên (không clear) để có thể reattach
                if (_currentHost != null)
                    _currentHost.Visibility = System.Windows.Visibility.Collapsed;
            }
            else
            {
                // --- Gắn lại Viber vào màn chiếu ---
                if (_currentHost != null)
                    _currentHost.Visibility = System.Windows.Visibility.Visible;

                _currentHost.Reattach();

                // Cập nhật UI
                BtnToggleDetach.Content = "📤 Mở ra ngoài";
                PanelDetachedNotice.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnRebindWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_currentActiveProfile != null && _currentActiveProfile.ProcessId != 0)
            {
                _windowScannerTimer.Start();
            }
        }

        private void BtnFocusViber_Click(object sender, RoutedEventArgs e)
        {
            if (_currentHost != null && !_currentHost.IsDetached)
                _currentHost.FocusViber();
            else if (_currentActiveProfile != null && _currentActiveProfile.WindowHandle != IntPtr.Zero)
                WindowManager.FocusWindow(_currentActiveProfile.WindowHandle);
        }

        /// <summary>Forward keyboard focus vào Viber ngay khi click vào vùng chiếu.</summary>
        private void ViberContainer_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Kiểm tra và thực hiện reset ngầm (Detach -> Reattach) duy nhất một lần ở click đầu tiên
            if (!_hasAutoResetOnFirstClick && _currentHost != null && !_currentHost.IsDetached)
            {
                _hasAutoResetOnFirstClick = true;

                // Tách ngầm và gắn lại ngay lập tức
                _currentHost.Detach();
                
                // Trì hoãn cực ngắn để Win32 cập nhật lại layout
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    await Task.Delay(20);
                    if (_currentHost != null)
                    {
                        _currentHost.Reattach();
                        _currentHost.FocusViber();
                        TxtStatus.Text = "Đã sửa lỗi hiển thị Viber ở click đầu tiên thành công.";
                    }
                }), System.Windows.Threading.DispatcherPriority.Input);

                // Đánh dấu đã xử lý xong event
                e.Handled = true;
                return;
            }

            // Dùng BeginInvoke để WPF xử lý xong mouse event trước khi chuyển Win32 focus.
            // Nếu SetFocus ngay lập tức → WPF layout pass có thể chạy giữa chừng → Viber bị xô.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _currentHost?.FocusViber();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void BtnSaveSession_Click(object sender, RoutedEventArgs e)
        {
            if (GridProfiles.SelectedItem is ViberProfile selected)
            {
                MessageBox.Show($"Phiên làm việc tự động được lưu trữ tại:\n{Path.GetFullPath(selected.DataDirPath)}", "Lưu phiên", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnCloseViber_Click(object sender, RoutedEventArgs e)
        {
            if (GridProfiles.SelectedItem is ViberProfile selected)
            {
                var confirm = MessageBox.Show($"Bạn có chắc chắn muốn đóng tiến trình Viber của tài khoản '{selected.Name}'?", "Xác nhận đóng", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm == MessageBoxResult.Yes)
                {
                    CloseViberProcess(selected);
                }
            }
        }

        private void CloseViberProcess(ViberProfile profile)
        {
            if (profile.ProcessId != 0 && _viberService != null)
            {
                _viberService.StopViberProcess(profile.ProcessId);
                
                profile.ProcessId = 0;
                profile.WindowHandle = IntPtr.Zero;
                profile.Status = "Đang đóng";
                GridProfiles.Items.Refresh();

                if (_currentActiveProfile == profile)
                {
                    ViberContainer.Children.Clear();
                    PanelPlaceholder.Visibility = Visibility.Visible;
                    TxtActiveProfile.Text = "Không có Viber nào đang hoạt động";
                    _currentActiveProfile = null;
                    _currentHost = null;
                }
                TxtStatus.Text = $"Đã đóng profile: {profile.Name}";
            }
        }

        // ================================================================
        // MULTI-SELECT & BULK ACTIONS: Thao tác hàng loạt
        // ================================================================

        private async void BtnBulkRun_Click(object sender, RoutedEventArgs e)
        {
            var selectedProfiles = Profiles.Where(p => p.IsSelected && p.Status == "Đang đóng").ToList();
            if (selectedProfiles.Count == 0)
            {
                MessageBox.Show("Vui lòng chọn ít nhất một tài khoản đang đóng để chạy loạt!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show($"Bạn có chắc chắn muốn chạy hàng loạt {selectedProfiles.Count} tài khoản đã chọn?", "Xác nhận chạy loạt", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            if (_viberService == null) return;

            TxtStatus.Text = $"Đang chạy loạt {selectedProfiles.Count} tài khoản...";
            foreach (var profile in selectedProfiles)
            {
                try
                {
                    profile.Status = "Đang mở";
                    profile.LastUpdated = DateTime.Now;
                    RefreshProfiles();
                    _profileRepo.SaveConfig(_config);

                    Process? process = _viberService.StartIsolatedViber(profile.Name, profile.DataDirPath);
                    if (process != null)
                    {
                        profile.ProcessId = process.Id;
                        // Hỗ trợ tự động nhúng tài khoản cuối cùng hoặc quét tuần tự
                        _currentActiveProfile = profile;
                        _windowScannerTimer.Start();
                    }
                    // Chờ 1.5 giây giữa mỗi lần mở để tránh xung đột hệ thống
                    await Task.Delay(1500);
                }
                catch (Exception ex)
                {
                    profile.Status = "Đang đóng";
                    RefreshProfiles();
                    System.Diagnostics.Debug.WriteLine($"Lỗi chạy loạt {profile.Name}: {ex.Message}");
                }
            }
            TxtStatus.Text = "Đã thực hiện yêu cầu chạy loạt xong.";
        }

        private void BtnBulkStop_Click(object sender, RoutedEventArgs e)
        {
            var selectedProfiles = Profiles.Where(p => p.IsSelected && p.Status != "Đang đóng").ToList();
            if (selectedProfiles.Count == 0)
            {
                MessageBox.Show("Vui lòng chọn ít nhất một tài khoản đang mở để dừng loạt!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show($"Bạn có chắc chắn muốn đóng hàng loạt {selectedProfiles.Count} tài khoản đã chọn?", "Xác nhận dừng loạt", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            TxtStatus.Text = $"Đang đóng loạt {selectedProfiles.Count} tài khoản...";
            foreach (var profile in selectedProfiles)
            {
                CloseViberProcess(profile);
            }
            TxtStatus.Text = "Đã dừng loạt các tài khoản được chọn.";
        }

        private void BtnBulkDelete_Click(object sender, RoutedEventArgs e)
        {
            var selectedProfiles = Profiles.Where(p => p.IsSelected).ToList();
            if (selectedProfiles.Count == 0)
            {
                MessageBox.Show("Vui lòng chọn ít nhất một tài khoản để xóa loạt!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show($"CẢNH BÁO: Bạn có chắc chắn muốn XÓA HOÀN TOÀN {selectedProfiles.Count} tài khoản đã chọn cùng dữ liệu của chúng? Hành động này không thể hoàn tác!", "Xác nhận xóa loạt", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            TxtStatus.Text = $"Đang xóa loạt {selectedProfiles.Count} tài khoản...";
            foreach (var profile in selectedProfiles)
            {
                try
                {
                    CloseViberProcess(profile);
                    if (Directory.Exists(profile.DataDirPath))
                    {
                        Directory.Delete(profile.DataDirPath, true);
                    }
                    Profiles.Remove(profile);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Lỗi khi xóa profile {profile.Name}: {ex.Message}");
                }
            }

            _profileRepo.SaveConfig(_config);
            UpdateSelectedCount();
            RefreshProfiles();
            TxtStatus.Text = "Đã hoàn tất xóa hàng loạt.";
        }

        private async void BtnCheckLive_Click(object sender, RoutedEventArgs e)
        {
            if (GridProfiles.SelectedItem is ViberProfile selected)
            {
                TxtStatus.Text = $"Đang tiến hành check Live/Die cho tài khoản '{selected.Name}'...";
                bool success = await RunCheckLiveWorkflowAsync(selected);
                
                if (success)
                {
                    TxtStatus.Text = $"Tài khoản '{selected.Name}' có trạng thái: {selected.Status}!";
                    MessageBox.Show($"Check Live hoàn tất!\nTài khoản: {selected.Name}\nKết quả: {selected.Status}", "Kết quả Check Live", MessageBoxButton.OK, selected.Status == "LIVE" ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
                else
                {
                    TxtStatus.Text = $"Không thể check Live tự động cho '{selected.Name}' (Vui lòng đảm bảo Viber đã đăng nhập).";
                }
            }
            else
            {
                MessageBox.Show("Vui lòng chọn một tài khoản trên bảng để check Live!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnBulkCheckLive_Click(object sender, RoutedEventArgs e)
        {
            var selectedProfiles = Profiles.Where(p => p.IsSelected).ToList();
            if (selectedProfiles.Count == 0)
            {
                MessageBox.Show("Vui lòng tích chọn ít nhất một tài khoản để check loạt!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show($"Bạn có chắc chắn muốn CHECK LIVE hàng loạt cho {selectedProfiles.Count} tài khoản đã chọn?", "Xác nhận check loạt", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            TxtStatus.Text = $"Đang check Live hàng loạt cho {selectedProfiles.Count} tài khoản...";
            int liveCount = 0;
            int dieCount = 0;

            foreach (var profile in selectedProfiles)
            {
                TxtStatus.Text = $"[Check loạt] Đang kiểm tra '{profile.Name}'...";
                bool success = await RunCheckLiveWorkflowAsync(profile);
                if (success)
                {
                    if (profile.Status == "LIVE") liveCount++;
                    else if (profile.Status == "DIE") dieCount++;
                }
                
                // Đóng tiến trình Viber sau khi check xong để giải phóng tài nguyên cho tài khoản tiếp theo
                CloseViberProcess(profile);
                await Task.Delay(1000);
            }

            TxtStatus.Text = $"Hoàn tất check loạt! Live: {liveCount} | Die: {dieCount}";
            MessageBox.Show($"Hoàn tất kiểm tra hàng loạt!\nTổng số tài khoản: {selectedProfiles.Count}\n- LIVE: {liveCount}\n- DIE: {dieCount}", "Kết quả check loạt", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Thực thi quy trình check Live/Die tự động bằng GUI Automation và OCR
        /// </summary>
        private async Task<bool> RunCheckLiveWorkflowAsync(ViberProfile profile)
        {
            try
            {
                // Kiểm tra nhanh xem tài khoản đã từng đăng nhập quét mã QR thành công chưa (nếu chưa có viber.db thì chưa đăng nhập)
                bool isLoggedIn = false;
                try
                {
                    string viberPcDir = Path.Combine(profile.DataDirPath, "AppData", "Roaming", "ViberPC");
                    if (Directory.Exists(viberPcDir))
                    {
                        var dbFiles = Directory.GetFiles(viberPcDir, "viber.db", SearchOption.AllDirectories);
                        isLoggedIn = dbFiles.Length > 0;
                    }
                }
                catch { }

                if (!isLoggedIn)
                {
                    MessageBox.Show($"Tài khoản '{profile.Name}' chưa được đăng nhập Viber trên máy tính này!\n\nVui lòng nhấn nút 'Mở / Login' và quét mã QR để kích hoạt tài khoản trước khi thực hiện Check Live.", "Chưa đăng nhập", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                // Bước 1: Nếu Viber đang đóng, tự động khởi động lên trước
                if (profile.Status == "Đang đóng" || profile.ProcessId == 0 || profile.WindowHandle == IntPtr.Zero)
                {
                    if (_viberService == null) return false;
                    
                    profile.Status = "Đang mở";
                    RefreshProfiles();
                    Process? process = _viberService.StartIsolatedViber(profile.Name, profile.DataDirPath);
                    if (process != null)
                    {
                        profile.ProcessId = process.Id;
                        _currentActiveProfile = profile;
                        _windowScannerTimer.Start();
                    }

                    // Chờ tối đa 8 giây để Viber mở và nhúng cửa sổ thành công
                    int timeoutCount = 0;
                    while (profile.WindowHandle == IntPtr.Zero && timeoutCount < 16)
                    {
                        await Task.Delay(500);
                        timeoutCount++;
                    }

                    if (profile.WindowHandle == IntPtr.Zero)
                    {
                        profile.Status = "Đang đóng";
                        RefreshProfiles();
                        return false; // Hết thời gian chờ, có thể Viber chưa đăng nhập
                    }
                }

                // Nhúng cửa sổ lên màn hình xem trước
                _currentActiveProfile = profile;
                EmbedViberWindow(profile.WindowHandle);
                await Task.Delay(1000); // Chờ hiển thị mượt mà

                // Bước 2: Tìm và mở Chat Bot hỗ trợ Unblock trực tiếp trên giao diện Viber đang nhúng
                IntPtr hwnd = profile.WindowHandle;

                // Sử dụng phím tắt Ctrl + Shift + F để focus 100% chính xác vào thanh Tìm kiếm của Viber
                ViberAutomationService.SendCtrlShiftF(hwnd);
                await Task.Delay(300);

                // Xóa trắng ô tìm kiếm cũ (gửi Backspace 25 lần để sạch nội dung cũ)
                for (int i = 0; i < 25; i++)
                {
                    ViberAutomationService.SendVirtualKey(hwnd, 0x08); // 0x08 là VK_BACK (Backspace)
                }
                await Task.Delay(100);

                // Gõ từ khóa tối ưu "support" và "number" để lọc sạch kênh rác, đưa Unblock Bot lên đầu
                ViberAutomationService.SendText(hwnd, "support number");

                // Đợi 1.8 giây để Viber nạp nhanh kết quả hiển thị ngắn
                await Task.Delay(1800);

                // Click nhẹ vào vùng danh sách cột trái để focus ổn định
                ViberAutomationService.ClickRelative(hwnd, 120, 250);
                await Task.Delay(200);

                // 3. Chụp ảnh màn hình danh sách cột trái và dùng OCR tìm tọa độ Y chính xác của dòng chữ chứa bot
                int botY = 420; // Tọa độ Y mặc định (fallback) an toàn: Y=420 nằm dưới mục "Bot", tránh lệch lên Kênh eSewa ở trên
                using (Bitmap? bmpSearch = ViberAutomationService.CaptureWindow(hwnd))
                {
                    if (bmpSearch != null)
                    {
                        // Thử tìm theo từ khóa "unblock"
                        int foundY = await ViberOcrService.FindTextLocationAsync(bmpSearch, "unblock");
                        
                        // Dự phòng 1: Nếu không thấy, thử tìm theo từ khóa "number"
                        if (foundY <= 0)
                        {
                            foundY = await ViberOcrService.FindTextLocationAsync(bmpSearch, "number");
                        }
                        
                        // Dự phòng 2: Nếu vẫn không thấy, thử tìm theo từ khóa "support"
                        if (foundY <= 0)
                        {
                            foundY = await ViberOcrService.FindTextLocationAsync(bmpSearch, "support");
                        }

                        if (foundY > 0)
                        {
                            botY = foundY;
                            System.Diagnostics.Debug.WriteLine($"Tìm thấy Unblock Bot bằng OCR ở tọa độ Y = {botY}");
                        }
                    }
                }

                // 4. Click chuột trực tiếp vào dòng chứa bot "Unblock Number - Support Bot" theo tọa độ Y tìm được
                ViberAutomationService.ClickRelative(hwnd, 120, botY);
                
                // Lấy kích thước hiện tại của cửa sổ Viber con để click chính xác vào trục tọa độ tương đối
                System.Windows.Point size = _currentHost != null ? new System.Windows.Point(_currentHost.ActualWidth, _currentHost.ActualHeight) : new System.Windows.Point(400, 500);
                int w = (int)size.X;
                int h = (int)size.Y;
                if (w <= 0 || h <= 0)
                {
                    w = 500;
                    h = 600;
                }

                // Bước 3: Đợi nút "Tiếp tục" hiển thị trên giao diện và click chính xác
                int clickX = w / 2;
                int clickY = h - 95; // Tọa độ Y mặc định của nút Tiếp tục
                bool foundContinueButton = false;

                TxtStatus.Text = "Đang đợi nút 'Tiếp tục' của Bot xuất hiện...";
                
                // Vòng lặp tối đa 6 lần (chờ tối đa 7.2 giây) để tìm chữ "Tiếp tục"
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    using (Bitmap? bmp = ViberAutomationService.CaptureWindow(hwnd))
                    {
                        if (bmp != null)
                        {
                            // Dùng OCR tìm tọa độ chữ "tiếp tục" hoặc "tiếp"
                            int foundY = await ViberOcrService.FindTextLocationAsync(bmp, "tiếp");
                            if (foundY <= 0)
                            {
                                foundY = await ViberOcrService.FindTextLocationAsync(bmp, "tục");
                            }

                            if (foundY > 0)
                            {
                                clickY = foundY;
                                foundContinueButton = true;
                                System.Diagnostics.Debug.WriteLine($"Tìm thấy nút Tiếp tục ở tọa độ Y = {clickY}");
                                break;
                            }
                        }
                    }
                }

                profile.Status = "Đang nhúng";
                GridProfiles.Items.Refresh();
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi quy trình check Live: {ex.Message}");
                profile.Status = "Đang nhúng";
                GridProfiles.Items.Refresh();
                return false;
            }
        }

        private void ViberContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                if (_currentHost != null)
                {
                    _currentHost.Width = ViberContainer.ActualWidth;
                    _currentHost.Height = ViberContainer.ActualHeight;
                    _currentHost.Resize(ViberContainer.ActualWidth, ViberContainer.ActualHeight);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi resize: {ex.Message}");
            }
        }

        private void GridProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridProfiles.SelectedItem is ViberProfile selected)
            {
                if (selected.Status == "Đang nhúng" && selected.WindowHandle != IntPtr.Zero)
                {
                    _currentActiveProfile = selected;
                    EmbedViberWindow(selected.WindowHandle);
                }
            }
        }

        private void BtnImportVerifyPhones_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                Title = "Chọn file chứa danh sách số điện thoại"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    var lines = File.ReadAllLines(openFileDialog.FileName)
                                    .Select(l => l.Trim())
                                    .Where(l => !string.IsNullOrEmpty(l))
                                    .ToList();

                    TxtVerifyPhones.Text = string.Join(Environment.NewLine, lines);
                    TxtStatus.Text = $"Đã nạp {lines.Count} số điện thoại từ file.";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi khi đọc file: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnStartVerify_Click(object sender, RoutedEventArgs e)
        {
            if (_isVerifyingPhones)
            {
                _verifyPhonesCts?.Cancel();
                TxtStatus.Text = "Đang dừng tiến trình kiểm tra số...";
                return;
            }

            var selectedProfile = CmbVerifyProfile.SelectedItem as ViberProfile;
            if (selectedProfile == null)
            {
                MessageBox.Show("Vui lòng chọn một tài khoản Viber làm mẫu test!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var phones = TxtVerifyPhones.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(p => p.Trim())
                                           .Where(p => !string.IsNullOrEmpty(p))
                                           .ToList();

            if (phones.Count == 0)
            {
                MessageBox.Show("Vui lòng nhập hoặc nạp file ít nhất một số điện thoại để kiểm tra!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _allVerifyResultsMaster.Clear();
            VerifyResults.Clear();
            _currentPageTab1 = 1;

            _isVerifyingPhones = true;
            BtnStartVerify.Content = "⏹ Dừng kiểm tra";
            BtnStartVerify.Style = (Style)FindResource("DangerBtnStyle");
            VerifyProgress.Value = 0;
            VerifyProgress.Maximum = phones.Count;
            _verifyPhonesCts = new System.Threading.CancellationTokenSource();

            TxtStatus.Text = $"Bắt đầu kiểm tra {phones.Count} số điện thoại...";

            // Tạo ID phiên quét mới – dạng "yyyyMMdd_HHmmss" để dễ nhìn
            _currentSessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            
            try
            {
                PopupViberLoading.IsOpen = true; // Hiển thị lớp phủ loading từ đầu tiến trình
                int completed = 0;
                foreach (string phone in phones)
                {
                    if (_verifyPhonesCts.Token.IsCancellationRequested) break;

                    TxtVerifyProgressStatus.Text = $"{completed}/{phones.Count}";
                    TxtStatus.Text = $"Đang kiểm tra số: {phone}...";

                    string resultText = await RunSinglePhoneVerifyAsync(selectedProfile, phone);
                    string accountName = "";
                    if (resultText == "LIVE")
                    {
                        accountName = ViberAutomationService.GetChatContactName(selectedProfile.WindowHandle);
                    }

                    var verifyResult = new VerifyResult
                    {
                        Phone = phone,
                        Result = resultText,
                        Time = DateTime.Now.ToString("HH:mm:ss dd/MM/yy"),
                        AccountName = accountName
                    };

                    // Tự động lưu kết quả vào Database SQLite cục bộ (luôn INSERT mới theo phiên)
                    SaveToHistoryDb(phone, resultText, verifyResult.Time, accountName, _currentSessionId);

                    Dispatcher.Invoke(() =>
                    {
                        var existing = _allVerifyResultsMaster.FirstOrDefault(r => r.Phone == phone);
                        if (existing != null)
                        {
                            existing.Result = resultText;
                            existing.Time = verifyResult.Time;
                            existing.AccountName = accountName;
                            
                            // Đẩy lên đầu danh sách master
                            _allVerifyResultsMaster.Remove(existing);
                            _allVerifyResultsMaster.Insert(0, existing);
                        }
                        else
                        {
                            _allVerifyResultsMaster.Insert(0, verifyResult);
                        }
                        ApplyFilterCurrentResults();
                    });

                    completed++;
                    VerifyProgress.Value = completed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi tiến trình check SĐT: {ex.Message}");
            }
            finally
            {
                // 2. Thu hồi nhúng Viber trở lại ViberManager sau khi kết thúc (nếu người dùng có lỡ tay gỡ ra ngoài)
                try
                {
                    if (_currentHost != null && _currentHost.IsDetached)
                    {
                        _currentHost.Visibility = System.Windows.Visibility.Visible;
                        _currentHost.Reattach();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Lỗi nhúng lại Viber: {ex.Message}");
                }
                await Task.Delay(500);

                PopupViberLoading.IsOpen = false; // Ẩn lớp phủ loading khi kết thúc toàn bộ danh sách
                _isVerifyingPhones = false;
                BtnStartVerify.Content = "▶ Bắt đầu kiểm tra";
                BtnStartVerify.Style = (Style)FindResource("AddBtnStyle");
                int finalCompleted = (int)VerifyProgress.Value;
                TxtVerifyProgressStatus.Text = $"Hoàn tất ({finalCompleted}/{phones.Count})";
                TxtStatus.Text = $"Hoàn tất kiểm tra danh sách {finalCompleted}/{phones.Count} số điện thoại.";
            }
        }

        private async Task<string> RunSinglePhoneVerifyAsync(ViberProfile profile, string phone)
        {
            IntPtr hwnd = profile.WindowHandle;
            if (hwnd == IntPtr.Zero) return "Không thể mở Viber";

            // Gọi logic đã được đóng gói trong module Service riêng biệt
            return await ViberPhoneVerifierService.VerifySinglePhoneAsync(hwnd, phone);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, System.Text.StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static string GetProcessExecutablePath(int processId)
        {
            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    int size = 1024;
                    var sb = new System.Text.StringBuilder(size);
                    if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                    {
                        return sb.ToString();
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
            return string.Empty;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 1. Thử đóng từng profile theo ProcessId trước
            if (Profiles != null)
            {
                foreach (var profile in Profiles)
                {
                    if (profile.ProcessId != 0)
                    {
                        try { CloseViberProcess(profile); } catch { }
                    }
                }
            }

            // 2. Quét dọn triệt để: Kill toàn bộ tiến trình tên "Viber" đang chạy từ thư mục của ta
            try
            {
                string standardCPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Viber\Viber.exe");
                
                foreach (var proc in Process.GetProcessesByName("Viber"))
                {
                    try
                    {
                        // Lấy đường dẫn thực thi bằng Win32 API an toàn (không bị chặn bởi UAC/MainModule)
                        string procPath = GetProcessExecutablePath(proc.Id);
                        if (string.IsNullOrEmpty(procPath))
                        {
                            procPath = proc.MainModule?.FileName ?? string.Empty;
                        }
                        
                        // Nếu tiến trình chạy từ thư mục của ta hoặc không phải bản cài mặc định ở ổ C
                        if (!string.IsNullOrEmpty(procPath) && 
                            (!procPath.Equals(standardCPath, StringComparison.OrdinalIgnoreCase) || 
                             procPath.Contains(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase)))
                        {
                            proc.Kill();
                            proc.WaitForExit(500);
                        }
                    }
                    catch
                    {
                        // Fallback ép buộc tắt
                        try { proc.Kill(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi dọn dẹp tiến trình ngầm: {ex.Message}");
            }

            base.OnClosing(e);
        }

        #region PHẦN QUẢN LÝ LỊCH SỬ QUÉT SĐT (TAB CONTROL & SQLITE DATABASE)

        /// <summary>
        /// Khởi tạo SQLite lưu lịch sử verify
        /// </summary>
        private void InitHistoryDatabase()
        {
            try
            {
                string connectionString = $"Data Source={_historyDbPath};";
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            CREATE TABLE IF NOT EXISTS verifier_history (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                phone TEXT NOT NULL,
                                result TEXT NOT NULL,
                                time TEXT NOT NULL,
                                account_name TEXT,
                                session_id TEXT
                            );";
                        command.ExecuteNonQuery();

                        // Migration: thêm cột account_name nếu chưa có
                        command.CommandText = "PRAGMA table_info(verifier_history);";
                        bool hasAccountName = false;
                        bool hasSessionId = false;
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string colName = reader.GetString(1);
                                if (colName == "account_name") hasAccountName = true;
                                if (colName == "session_id") hasSessionId = true;
                            }
                        }

                        if (!hasAccountName)
                        {
                            command.CommandText = "ALTER TABLE verifier_history ADD COLUMN account_name TEXT;";
                            command.ExecuteNonQuery();
                        }
                        if (!hasSessionId)
                        {
                            command.CommandText = "ALTER TABLE verifier_history ADD COLUMN session_id TEXT;";
                            command.ExecuteNonQuery();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi Init SQLite History DB: {ex.Message}");
            }
        }

        /// <summary>
        /// Lưu bản ghi verify xuống SQLite – luôn INSERT mới, mỗi phiên quét giữ nguyên lịch sử riêng
        /// </summary>
        private void SaveToHistoryDb(string phone, string result, string time, string accountName, string sessionId)
        {
            try
            {
                string connectionString = $"Data Source={_historyDbPath};";
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        // Luôn INSERT bản ghi mới – mỗi phiên quét là một tập dữ liệu riêng biệt
                        command.CommandText = "INSERT INTO verifier_history (phone, result, time, account_name, session_id) VALUES (@phone, @result, @time, @account_name, @session_id);";
                        command.Parameters.AddWithValue("@phone", phone);
                        command.Parameters.AddWithValue("@result", result);
                        command.Parameters.AddWithValue("@time", time);
                        command.Parameters.AddWithValue("@account_name", accountName);
                        command.Parameters.AddWithValue("@session_id", sessionId);
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi Save SQLite History DB: {ex.Message}");
            }
        }

        /// <summary>
        /// Nạp và lọc danh sách lịch sử từ SQLite
        /// </summary>
        private void LoadHistoryResults()
        {
            try
            {
                HistoryResults.Clear();

                string searchText = TxtSearchHistoryResult?.Text?.Trim() ?? "";
                int filterStatusIdx = CmbFilterHistoryStatus?.SelectedIndex ?? 0; // 0: Tất cả, 1: LIVE, 2: Not LIVE

                // Lọc theo phiên: lấy session_id được chọn trong CmbFilterSession (null = tất cả)
                string selectedSession = "";
                if (CmbFilterSession?.SelectedIndex > 0 && CmbFilterSession.SelectedItem is string sessionTag)
                {
                    selectedSession = sessionTag;
                }

                string connectionString = $"Data Source={_historyDbPath};";
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        // 1. Đếm tổng số bản ghi khớp bộ lọc để tính số trang
                        StringBuilder countQuery = new StringBuilder("SELECT COUNT(*) FROM verifier_history WHERE 1=1");
                        if (!string.IsNullOrEmpty(searchText))
                        {
                            countQuery.Append(" AND phone LIKE @search");
                        }
                        if (filterStatusIdx == 1)
                        {
                            countQuery.Append(" AND result = 'LIVE'");
                        }
                        else if (filterStatusIdx == 2)
                        {
                            countQuery.Append(" AND result = 'Not LIVE'");
                        }
                        if (!string.IsNullOrEmpty(selectedSession))
                        {
                            countQuery.Append(" AND session_id = @session");
                        }

                        command.CommandText = countQuery.ToString();
                        if (!string.IsNullOrEmpty(searchText))
                        {
                            command.Parameters.AddWithValue("@search", $"%{searchText}%");
                        }
                        if (!string.IsNullOrEmpty(selectedSession))
                        {
                            command.Parameters.AddWithValue("@session", selectedSession);
                        }
                        _historyTotalItems = Convert.ToInt32(command.ExecuteScalar());

                        // Tính tổng số trang (mỗi trang tối đa 100 bản ghi)
                        _historyTotalPages = (int)Math.Ceiling((double)_historyTotalItems / HistoryPageSize);
                        if (_historyTotalPages < 1) _historyTotalPages = 1;
                        if (_historyCurrentPage > _historyTotalPages) _historyCurrentPage = _historyTotalPages;
                        if (_historyCurrentPage < 1) _historyCurrentPage = 1;

                        // 2. Lấy dữ liệu phân trang (LIMIT 100 OFFSET (trang - 1)*100)
                        command.Parameters.Clear();
                        StringBuilder query = new StringBuilder("SELECT phone, result, time, account_name FROM verifier_history WHERE 1=1");
                        if (!string.IsNullOrEmpty(searchText))
                        {
                            query.Append(" AND phone LIKE @search");
                            command.Parameters.AddWithValue("@search", $"%{searchText}%");
                        }
                        if (filterStatusIdx == 1)
                        {
                            query.Append(" AND result = 'LIVE'");
                        }
                        else if (filterStatusIdx == 2)
                        {
                            query.Append(" AND result = 'Not LIVE'");
                        }
                        if (!string.IsNullOrEmpty(selectedSession))
                        {
                            query.Append(" AND session_id = @session");
                            command.Parameters.AddWithValue("@session", selectedSession);
                        }

                        query.Append(" ORDER BY id DESC LIMIT @limit OFFSET @offset;");
                        command.CommandText = query.ToString();
                        command.Parameters.AddWithValue("@limit", HistoryPageSize);
                        command.Parameters.AddWithValue("@offset", (_historyCurrentPage - 1) * HistoryPageSize);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                HistoryResults.Add(new VerifyResult
                                {
                                    Phone = reader.GetString(0),
                                    Result = reader.GetString(1),
                                    Time = reader.GetString(2),
                                    AccountName = reader.IsDBNull(3) ? "" : reader.GetString(3)
                                });
                            }
                        }
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    GridHistoryResults?.Items.Refresh();
                    
                    // Cập nhật thông tin phân trang trên giao diện
                    if (TxtHistoryTotal != null) TxtHistoryTotal.Text = $"Tổng số: {_historyTotalItems:N0} số";
                    if (TxtHistoryPageInfo != null) TxtHistoryPageInfo.Text = $"Trang {_historyCurrentPage} / {_historyTotalPages}";
                    if (BtnHistoryPrevPage != null) BtnHistoryPrevPage.IsEnabled = _historyCurrentPage > 1;
                    if (BtnHistoryNextPage != null) BtnHistoryNextPage.IsEnabled = _historyCurrentPage < _historyTotalPages;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi Load SQLite History: {ex.Message}");
            }
        }

        /// <summary>
        /// Load danh sách phiên quét từ DB để populate vào CmbFilterSession
        /// </summary>
        private void RefreshSessionList()
        {
            try
            {
                string connectionString = $"Data Source={_historyDbPath};";
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT DISTINCT session_id FROM verifier_history WHERE session_id IS NOT NULL AND session_id != '' ORDER BY session_id DESC LIMIT 50;";
                        var sessions = new List<string>();
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                sessions.Add(reader.GetString(0));
                            }
                        }

                        Dispatcher.Invoke(() =>
                        {
                            if (CmbFilterSession == null) return;
                            int prevIdx = CmbFilterSession.SelectedIndex;
                            string? prevVal = prevIdx > 0 ? CmbFilterSession.SelectedItem as string : null;

                            CmbFilterSession.Items.Clear();
                            CmbFilterSession.Items.Add("Tat ca phien");
                            foreach (var s in sessions)
                            {
                                // Hiển thị dạng đẹp: "20260701_103500" → "01/07/26 10:35:00"
                                string label = s;
                                if (s.Length == 15 && s[8] == '_')
                                {
                                    try
                                    {
                                        var dt = DateTime.ParseExact(s, "yyyyMMdd_HHmmss", null);
                                        label = dt.ToString("dd/MM/yy HH:mm:ss");
                                    }
                                    catch { }
                                }
                                CmbFilterSession.Items.Add(s); // tag = raw session_id
                                // Thay item string bằng object ẩn tag để GetString dễ hơn
                            }

                            // Giữ lại lựa chọn cũ nếu vẫn còn tồn tại
                            if (prevVal != null && CmbFilterSession.Items.Contains(prevVal))
                                CmbFilterSession.SelectedItem = prevVal;
                            else
                                CmbFilterSession.SelectedIndex = 0;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi RefreshSessionList: {ex.Message}");
            }
        }


        // ==========================================
        // SỰ KIỆN TAB 1: KẾT QUẢ HIỆN TẠI
        // ==========================================
        
        private void TxtSearchCurrentResult_TextChanged(object sender, TextChangedEventArgs e)
        {
            _currentPageTab1 = 1;
            ApplyFilterCurrentResults();
        }

        private void CmbFilterCurrentStatus_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            _currentPageTab1 = 1;
            ApplyFilterCurrentResults();
        }

        private void ApplyFilterCurrentResults()
        {
            if (!_isInitialized || GridVerifyResults == null) return;

            string searchText = TxtSearchCurrentResult?.Text?.Trim() ?? "";
            int filterStatusIdx = CmbFilterCurrentStatus?.SelectedIndex ?? 0;

            // Lọc danh sách từ master list
            var filtered = _allVerifyResultsMaster.Where(item =>
            {
                // Lọc theo SĐT
                if (!string.IsNullOrEmpty(searchText) && !item.Phone.Contains(searchText))
                {
                    return false;
                }

                // Lọc theo kết quả
                if (filterStatusIdx == 1 && item.Result != "LIVE")
                {
                    return false;
                }
                if (filterStatusIdx == 2 && item.Result != "Not LIVE")
                {
                    return false;
                }

                return true;
            }).ToList();

            _totalItemsTab1 = filtered.Count;
            _totalPagesTab1 = (int)Math.Ceiling((double)_totalItemsTab1 / PageSizeTab1);
            if (_totalPagesTab1 < 1) _totalPagesTab1 = 1;
            if (_currentPageTab1 > _totalPagesTab1) _currentPageTab1 = _totalPagesTab1;
            if (_currentPageTab1 < 1) _currentPageTab1 = 1;

            // Lấy trang hiện tại
            var pageItems = filtered.Skip((_currentPageTab1 - 1) * PageSizeTab1).Take(PageSizeTab1).ToList();

            // Cập nhật ObservableCollection VerifyResults
            VerifyResults.Clear();
            foreach (var item in pageItems)
            {
                VerifyResults.Add(item);
            }

            GridVerifyResults.Items.Refresh();

            // Cập nhật thông tin phân trang trên giao diện Tab 1
            if (TxtTab1Total != null) TxtTab1Total.Text = $"Tổng số: {_totalItemsTab1:N0} số";
            if (TxtTab1PageInfo != null) TxtTab1PageInfo.Text = $"Trang {_currentPageTab1} / {_totalPagesTab1}";
            if (BtnTab1PrevPage != null) BtnTab1PrevPage.IsEnabled = _currentPageTab1 > 1;
            if (BtnTab1NextPage != null) BtnTab1NextPage.IsEnabled = _currentPageTab1 < _totalPagesTab1;
        }

        private void BtnTab1PrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPageTab1 > 1)
            {
                _currentPageTab1--;
                ApplyFilterCurrentResults();
            }
        }

        private void BtnTab1NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPageTab1 < _totalPagesTab1)
            {
                _currentPageTab1++;
                ApplyFilterCurrentResults();
            }
        }

        private void BtnClearCurrentResults_Click(object sender, RoutedEventArgs e)
        {
            _allVerifyResultsMaster.Clear();
            VerifyResults.Clear();
            _currentPageTab1 = 1;
            ApplyFilterCurrentResults();
        }

        // ==========================================
        // SỰ KIỆN TAB 2: LỊCH SỬ QUÉT SQLite
        // ==========================================

        private void TabHistory_Selected(object sender, RoutedEventArgs e)
        {
            RefreshSessionList();
            LoadHistoryResults();
        }

        private void TxtSearchHistoryResult_TextChanged(object sender, TextChangedEventArgs e)
        {
            _historyCurrentPage = 1;
            LoadHistoryResults();
        }

        private void CmbFilterHistoryStatus_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            _historyCurrentPage = 1;
            LoadHistoryResults();
        }

        private void CmbFilterSession_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            _historyCurrentPage = 1;
            LoadHistoryResults();
        }

        private void BtnHistoryPrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_historyCurrentPage > 1)
            {
                _historyCurrentPage--;
                LoadHistoryResults();
            }
        }

        private void BtnHistoryNextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_historyCurrentPage < _historyTotalPages)
            {
                _historyCurrentPage++;
                LoadHistoryResults();
            }
        }

        private void BtnClearHistoryDb_Click(object sender, RoutedEventArgs e)
        {
            var boxResult = System.Windows.MessageBox.Show(
                "Bạn có chắc chắn muốn xóa vĩnh viễn toàn bộ lịch sử quét trong cơ sở dữ liệu?",
                "Xác nhận xóa lịch sử",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (boxResult == MessageBoxResult.Yes)
            {
                try
                {
                    string connectionString = $"Data Source={_historyDbPath};";
                    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
                    {
                        connection.Open();
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = "DELETE FROM verifier_history;";
                            command.ExecuteNonQuery();
                        }
                    }
                    LoadHistoryResults();
                    System.Windows.MessageBox.Show("Đã xóa toàn bộ lịch sử quét thành công!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Lỗi khi xóa lịch sử: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnOpenChat_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is VerifyResult result)
            {
                if (result.Result != "LIVE") return;

                var activeProfile = _currentActiveProfile ?? GridProfiles.SelectedItem as ViberProfile;
                if (activeProfile == null || activeProfile.WindowHandle == IntPtr.Zero)
                {
                    System.Windows.MessageBox.Show("Vui lòng chọn hoặc mở hoạt động một tài khoản Viber từ danh sách profile phía trên trước!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                TxtStatus.Text = $"Đang mở cuộc trò chuyện với số {result.Phone} trên profile {activeProfile.Name}...";
                
                await ViberPhoneVerifierService.OpenChatWithPhoneAsync(activeProfile.WindowHandle, result.Phone);
                
                TxtStatus.Text = $"Đã yêu cầu mở cuộc trò chuyện với số {result.Phone}.";
            }
        }

        private void BtnExportCurrentResults_Click(object sender, RoutedEventArgs e)
        {
            if (_allVerifyResultsMaster.Count == 0)
            {
                System.Windows.MessageBox.Show("Không có dữ liệu kết quả hiện tại để xuất file!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveFileDialog = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|CSV files (*.csv)|*.csv",
                FileName = "KetQuaQuet_HienTai.txt",
                Title = "Lưu danh sách kết quả hiện tại"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    var lines = new List<string>();
                    bool isCsv = saveFileDialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
                    
                    if (isCsv)
                    {
                        lines.Add("So Dien Thoai,Ket Qua,Ten Viber,Thoi Gian");
                    }

                    foreach (var res in _allVerifyResultsMaster)
                    {
                        if (isCsv)
                        {
                            lines.Add($"\"{res.Phone}\",\"{res.Result}\",\"{res.AccountName}\",\"{res.Time}\"");
                        }
                        else
                        {
                            lines.Add($"{res.Phone}|{res.Result}|{res.Time}|{res.AccountName}");
                        }
                    }

                    File.WriteAllLines(saveFileDialog.FileName, lines, Encoding.UTF8);
                    System.Windows.MessageBox.Show($"Đã xuất thành công {_allVerifyResultsMaster.Count} kết quả ra file!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Lỗi khi xuất file: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnUploadHistoryDb_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                Title = "Chọn file chứa kết quả quét để tải lên DB"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    var lines = File.ReadAllLines(openFileDialog.FileName)
                                    .Select(l => l.Trim())
                                    .Where(l => !string.IsNullOrEmpty(l))
                                    .ToList();

                    int imported = 0;
                    string connectionString = $"Data Source={_historyDbPath};";
                    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction())
                        {
                            using (var command = connection.CreateCommand())
                            {
                                command.Transaction = transaction;

                                foreach (string line in lines)
                                {
                                    string[] parts = line.Split(new[] { '|', ',' }, StringSplitOptions.None)
                                                         .Select(p => p.Trim())
                                                         .ToArray();

                                    if (parts.Length == 0 || string.IsNullOrEmpty(parts[0])) continue;

                                    string phone = parts[0];
                                    string result = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : "LIVE";
                                    string time = parts.Length > 2 && !string.IsNullOrEmpty(parts[2]) ? parts[2] : DateTime.Now.ToString("HH:mm:ss dd/MM/yy");
                                    string accountName = parts.Length > 3 ? parts[3] : "";

                                    command.CommandText = "SELECT COUNT(*) FROM verifier_history WHERE phone = @phone;";
                                    command.Parameters.Clear();
                                    command.Parameters.AddWithValue("@phone", phone);
                                    long count = Convert.ToInt64(command.ExecuteScalar());

                                    command.Parameters.Clear();
                                    if (count > 0)
                                    {
                                        command.CommandText = "UPDATE verifier_history SET result = @result, time = @time, account_name = @account_name WHERE phone = @phone;";
                                    }
                                    else
                                    {
                                        command.CommandText = "INSERT INTO verifier_history (phone, result, time, account_name) VALUES (@phone, @result, @time, @account_name);";
                                    }
                                    command.Parameters.AddWithValue("@phone", phone);
                                    command.Parameters.AddWithValue("@result", result);
                                    command.Parameters.AddWithValue("@time", time);
                                    command.Parameters.AddWithValue("@account_name", accountName);
                                    command.ExecuteNonQuery();
                                    
                                    imported++;
                                }
                            }
                            transaction.Commit();
                        }
                    }

                    _historyCurrentPage = 1;
                    LoadHistoryResults();
                    System.Windows.MessageBox.Show($"Đã tải lên và cập nhật {imported} số điện thoại thành công vào lịch sử!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Lỗi khi tải file lên DB: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnExportHistoryResults_Click(object sender, RoutedEventArgs e)
        {
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|CSV files (*.csv)|*.csv",
                FileName = "LichSuQuet_Viber.txt",
                Title = "Lưu danh sách lịch sử quét"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    var lines = new List<string>();
                    bool isCsv = saveFileDialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
                    
                    if (isCsv)
                    {
                        lines.Add("So Dien Thoai,Ket Qua,Ten Viber,Thoi Gian");
                    }

                    string searchText = TxtSearchHistoryResult?.Text?.Trim() ?? "";
                    int filterStatusIdx = CmbFilterHistoryStatus?.SelectedIndex ?? 0;

                    int exportedCount = 0;
                    string connectionString = $"Data Source={_historyDbPath};";
                    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
                    {
                        connection.Open();
                        using (var command = connection.CreateCommand())
                        {
                            StringBuilder query = new StringBuilder("SELECT phone, result, time, account_name FROM verifier_history WHERE 1=1");
                            if (!string.IsNullOrEmpty(searchText))
                            {
                                query.Append(" AND phone LIKE @search");
                                command.Parameters.AddWithValue("@search", $"%{searchText}%");
                            }
                            if (filterStatusIdx == 1)
                            {
                                query.Append(" AND result = 'LIVE'");
                            }
                            else if (filterStatusIdx == 2)
                            {
                                query.Append(" AND result = 'Not LIVE'");
                            }

                            query.Append(" ORDER BY id DESC;");
                            command.CommandText = query.ToString();

                            using (var reader = command.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string phone = reader.GetString(0);
                                    string result = reader.GetString(1);
                                    string time = reader.GetString(2);
                                    string accountName = reader.IsDBNull(3) ? "" : reader.GetString(3);

                                    if (isCsv)
                                    {
                                        lines.Add($"\"{phone}\",\"{result}\",\"{accountName}\",\"{time}\"");
                                    }
                                    else
                                    {
                                        lines.Add($"{phone}|{result}|{time}|{accountName}");
                                    }
                                    exportedCount++;
                                }
                            }
                        }
                    }

                    File.WriteAllLines(saveFileDialog.FileName, lines, Encoding.UTF8);
                    System.Windows.MessageBox.Show($"Đã xuất thành công {exportedCount} lịch sử quét ra file!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Lỗi khi xuất file: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        #endregion
    }
}