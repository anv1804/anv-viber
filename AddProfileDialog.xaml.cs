using System.Windows;

namespace ViberManager
{
    public partial class AddProfileDialog : Window
    {
        public string ProfileName { get; private set; } = string.Empty;
        public string ProfilePhone { get; private set; } = string.Empty;

        public AddProfileDialog()
        {
            InitializeComponent();
            TxtName.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtName.Text))
            {
                MessageBox.Show("Vui lòng nhập tên gợi nhớ cho profile!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            ProfileName = TxtName.Text.Trim();
            ProfilePhone = TxtPhone.Text.Trim();
            
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
