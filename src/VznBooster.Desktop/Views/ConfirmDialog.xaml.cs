using System.Windows;
using VznBooster.Desktop.ViewModels;

namespace VznBooster.Desktop.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(ConfirmRequest request)
    {
        InitializeComponent();
        DataContext = request;
        ConfirmButton.Content = request.ConfirmLabel;
        ConfirmButton.IsEnabled = !request.HasAcknowledge;   // com reconhecimento exigido, só habilita ao marcar a caixa
    }

    private void OnAckChanged(object sender, RoutedEventArgs e) => ConfirmButton.IsEnabled = AckBox.IsChecked == true;
    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
