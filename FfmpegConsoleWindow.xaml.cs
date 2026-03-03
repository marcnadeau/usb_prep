using System;
using System.Windows;

namespace MediaFileAnalyzer
{
    public partial class FfmpegConsoleWindow : Window
    {
        public FfmpegConsoleWindow()
        {
            InitializeComponent();
        }

        public void ClearLogs()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ClearLogs);
                return;
            }

            ConsoleTextBox.Clear();
        }

        public void AppendLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendLog(text));
                return;
            }

            ConsoleTextBox.AppendText(text + Environment.NewLine);
            ConsoleTextBox.ScrollToEnd();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearLogs();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }
    }
}
