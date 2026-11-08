using System;
using System.Windows;

namespace Carp.Gui;

/// <summary>首次启动时展示一次性初始密码，并提供复制按钮。</summary>
public partial class FirstRunDialog : Window
{
    private readonly string _password;

    public FirstRunDialog(string password)
    {
        InitializeComponent();
        _password = password;
        PasswordBox.Text = password;
        HintText.Text = "关闭此窗口后仍可在「高级 → 管理员账号」点「显示密码」再次查看。";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_password);
            HintText.Text = "已复制到剪贴板。";
        }
        catch
        {
            HintText.Text = "复制失败，请手动选中密码后复制。";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
