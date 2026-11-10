using System.Windows;

namespace Carp.Gui;

/// <summary>只有「确定」的信息框，用于备份完成、版本不一致、数据目录不可写等提示。</summary>
public partial class InfoDialog : Window
{
    public InfoDialog()
    {
        InitializeComponent();
    }

    public static void ShowMessage(Window? owner, string heading, string message, string details = "")
    {
        var dialog = new InfoDialog { Owner = owner, Title = heading };
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
        dialog.ShowDialog();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
