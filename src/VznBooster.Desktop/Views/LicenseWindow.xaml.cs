using System.Windows;
using VznBooster.Desktop.Services;

namespace VznBooster.Desktop.Views;

public partial class LicenseWindow : Window
{
    private readonly LicenseService _license;

    public LicenseWindow(LicenseService license, string? chave, string? aviso)
    {
        InitializeComponent();
        _license = license;
        KeyBox.Text = chave ?? "";
        StatusText.Text = aviso ?? "";
        Loaded += (_, _) => KeyBox.Focus();
    }

    private async void Ativar_Click(object sender, RoutedEventArgs e)
    {
        var chave = KeyBox.Text.Trim();
        if (chave.Length == 0) { StatusText.Text = "Digite a chave."; return; }

        EnterButton.IsEnabled = false;
        StatusText.Text = "Verificando...";
        try
        {
            var (ok, msg) = await _license.LoginAsync(chave, motivo =>
                MessageBox.Show(this, motivo + "\n\nMover a licença para este PC?", "VZN Booster",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

            if (ok) { DialogResult = true; return; }
            StatusText.Text = msg; // mensagem já vem escrita para o cliente
        }
        catch (Exception)
        {
            StatusText.Text = "Sem conexão com o servidor de licenças. Verifique sua internet.";
        }
        finally { EnterButton.IsEnabled = true; }
    }
}