using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Veil.Desktop;

/// <summary>Password input for an already registered encrypted volume; never saves the secret.</summary>
internal sealed class ProfileUnlockDialog : Window
{
    private readonly PasswordBox password = new() { Height = 34, MaxLength = 256 };

    private ProfileUnlockDialog(string title, string prompt, Window? owner)
    {
        Title = title;
        Owner = owner;
        Width = 420;
        Height = 235;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 23, 35));
        Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(password);
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 17, 0, 0) };
        var cancel = new Button { Content = "취소", Width = 84, Height = 32,
            IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var unlock = new Button { Content = "잠금 해제", Width = 98, Height = 32, IsDefault = true };
        unlock.Click += (_, _) =>
        {
            using var entered = password.SecurePassword;
            if (entered.Length > 0) DialogResult = true;
        };
        cancel.Click += (_, _) => DialogResult = false;
        actions.Children.Add(cancel);
        actions.Children.Add(unlock);
        panel.Children.Add(actions);
        Content = panel;
        Loaded += (_, _) => password.Focus();
    }

    internal static char[]? Prompt(string title = "보호 프로필 잠금 해제",
        string prompt = "보호 프로필과 다운로드 금고를 여는 암호를 입력하세요.", Window? owner = null)
    {
        var dialog = new ProfileUnlockDialog(title, prompt, owner);
        if (dialog.ShowDialog() != true)
        {
            dialog.password.Clear();
            return null;
        }
        using SecureString entered = dialog.password.SecurePassword;
        char[] secret = new char[entered.Length];
        IntPtr memory = IntPtr.Zero;
        try
        {
            memory = Marshal.SecureStringToGlobalAllocUnicode(entered);
            for (int i = 0; i < secret.Length; i++)
                secret[i] = (char)Marshal.ReadInt16(memory, i * sizeof(char));
            return secret;
        }
        finally
        {
            if (memory != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(memory);
            dialog.password.Clear();
        }
    }
}
