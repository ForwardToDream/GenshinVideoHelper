using System.Windows;

namespace GenshinVideoHelper.App;

public partial class LoginPromptWindow : Window
{
    public Native.WindowDisplayLayout DisplayLayout { get; }
    public event Action? LoginRequested;
    public LoginPromptWindow()
    {
        InitializeComponent();
        DisplayLayout = new(this);
    }
    private void Continue_Click(object sender, RoutedEventArgs e) => Close();
    private void Login_Click(object sender, RoutedEventArgs e) { Close(); LoginRequested?.Invoke(); }
}
