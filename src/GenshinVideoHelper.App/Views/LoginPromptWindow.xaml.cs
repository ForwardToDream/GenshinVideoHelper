using System.Windows;

namespace GenshinVideoHelper.App;

public partial class LoginPromptWindow : Window
{
    public event Action? LoginRequested;
    public LoginPromptWindow() => InitializeComponent();
    private void Continue_Click(object sender, RoutedEventArgs e) => Close();
    private void Login_Click(object sender, RoutedEventArgs e) { Close(); LoginRequested?.Invoke(); }
}
