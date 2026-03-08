using System;
using System.Windows;

namespace ExpressPackingMonitoring
{
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog(string message, string caption)
        {
            InitializeComponent();
            MessageText.Text = message;
            Title = caption;
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