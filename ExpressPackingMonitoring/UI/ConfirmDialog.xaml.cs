using System;
using System.Windows;

namespace ExpressPackingMonitoring.UI
{
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog(
            string message,
            string caption,
            string confirmText = "确定",
            string cancelText = "取消",
            bool isDangerous = true)
        {
            InitializeComponent();
            MessageText.Text = message;
            Title = caption;
            ConfirmButton.Content = confirmText;
            CancelButton.Content = cancelText;
            if (!isDangerous)
                ConfirmButton.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
