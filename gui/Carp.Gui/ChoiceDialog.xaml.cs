using System.Windows;
using System.Windows.Media;

namespace Carp.Gui;

/// <summary>通用二选一确认框（结束进程 / 接管核心 / 重置密码 / 退出等场景）。</summary>
public partial class ChoiceDialog : Window
{
    public ChoiceDialog()
    {
        InitializeComponent();
    }

    /// <summary>显示对话框；确定返回 true，取消 / 关闭返回 false。</summary>
    public static bool Ask(
        Window? owner,
        string heading,
        string message,
        string details = "",
        string okText = "确定",
        string cancelText = "取消",
        bool danger = false)
    {
        var dialog = new ChoiceDialog { Owner = owner, Title = heading };
        dialog.HeadingText.Text = heading;
        dialog.MessageText.Text = message;
        if (string.IsNullOrWhiteSpace(details))
        {
            dialog.DetailsBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            dialog.DetailsBorder.Visibility = Visibility.Visible;
            dialog.DetailsBox.Text = details;
        }
        dialog.OkButton.Content = okText;
        dialog.CancelButton.Content = cancelText;
        if (danger)
        {
            dialog.OkButton.Style = (Style)dialog.FindResource("PrimaryButton");
            dialog.OkButton.Background = (Brush)dialog.FindResource("Red");
        }
        return dialog.ShowDialog() == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
