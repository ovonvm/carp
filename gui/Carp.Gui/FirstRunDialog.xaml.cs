using System;
using System.Windows;

namespace Carp.Gui;

/// <summary>一次性密码展示框：首次启动的初始管理员密码，或重置后的 root 密码。</summary>
public partial class FirstRunDialog : Window
{
    private readonly string _password;

    public FirstRunDialog(
        string password,
        string? hint = null,
        string? heading = null,
        string? message = null,
        string? account = null)
    {
        InitializeComponent();
        _password = password;
        PasswordBox.Text = password;
        HintText.Text = hint ?? "关闭此窗口后仍可在「高级 → 管理员账号」点「显示密码」再次查看。";
        if (!string.IsNullOrWhiteSpace(heading))
        {
            HeadingText.Text = heading;
        }
        if (!string.IsNullOrWhiteSpace(message))
        {
            MessageText.Text = message;
        }
        if (!string.IsNullOrWhiteSpace(account))
        {
            AccountText.Text = account;
        }
        Loaded += (_, _) =>
        {
            try
            {
                PasswordBox.Focus();
                PasswordBox.SelectAll();
            }
            catch
            {
                // 忽略焦点异常
            }
        };
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
