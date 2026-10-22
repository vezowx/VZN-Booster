using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using VznBooster.Core.Hardware;
using VznBooster.Core.Privacy;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed record HardwareRowData(string Label, string Value, string SourceText, string SourceKey);

public sealed class HardwareSectionViewModel
{
    public HardwareSectionViewModel(string title, IEnumerable<HardwareRowData> rows)
    {
        Title = title;
        Rows = new ObservableCollection<HardwareRowData>(rows);
    }

    public string Title { get; }
    public ObservableCollection<HardwareRowData> Rows { get; }
}

/// <summary>Inventário real. Cada valor mostra sua fonte (informado pelo sistema, medido ou indisponível). Nome do computador e da conta ficam ocultos por padrão.</summary>
public sealed class HardwareViewModel : PageViewModel
{
    private HardwareInventory? _inventory;
    private string _info = "Carregando inventário…";
    private bool _includePersonal;
    private bool _busy;

    public HardwareViewModel(AppServices services) : base("Hardware", services)
    {
        ComputerName = new SensitiveField(services.Privacy, SensitiveCategory.ComputerName, () => Environment.MachineName);
        UserName = new SensitiveField(services.Privacy, SensitiveCategory.UserName, () => Environment.UserName);
        RefreshCommand = new AsyncRelayCommand(LoadAsync, services.ReportError, () => !_busy);
        ExportCommand = new AsyncRelayCommand(ExportAsync, services.ReportError, () => !_busy && _inventory is not null);
    }

    public SensitiveField ComputerName { get; }
    public SensitiveField UserName { get; }
    public ObservableCollection<HardwareSectionViewModel> Sections { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }

    public string Info { get => _info; private set => SetProperty(ref _info, value); }
    public bool IncludePersonal { get => _includePersonal; set => SetProperty(ref _includePersonal, value); }

    public override void OnActivated() => RefreshCommand.Execute(null);

    private async Task LoadAsync()
    {
        _busy = true;
        Info = "Lendo o inventário…";
        try
        {
            var inventory = await Task.Run(() =>
                HardwareInventoryReader.Read(new WindowsLocalMachineRegistry(), new WindowsPlatformInfoSource().Read()));
            _inventory = inventory;
            Sections.Clear();
            foreach (var s in inventory.Sections)
                Sections.Add(new HardwareSectionViewModel(s.Title,
                    s.Items.Select(i => new HardwareRowData(i.Label, i.Value, i.SourceText, i.Source.ToString()))));
            Info = "Valores marcados como \"indisponível\" não foram obtidos e não são estimados.";
        }
        finally { _busy = false; }
    }

    private async Task ExportAsync()
    {
        if (_inventory is null) return;

        if (IncludePersonal)
        {
            var confirm = new ConfirmRequest("Exportar relatório", "Incluir dados pessoais no relatório?",
                new[]
                {
                    "O arquivo conterá o nome do computador e o nome da conta do Windows.",
                    "Ele será salvo apenas no local que você escolher; nada é enviado a servidores externos.",
                    "Para um relatório sem dados pessoais, cancele e desmarque a opção."
                }, "Incluir e exportar");
            if (!Services.Confirm(confirm)) return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Salvar relatório de hardware",
            Filter = "Texto (*.txt)|*.txt",
            FileName = "VZN-Booster-hardware.txt"
        };
        if (dialog.ShowDialog() != true) return;

        var text = HardwareReport.Build(_inventory, DateTimeOffset.Now, IncludePersonal, Environment.MachineName, Environment.UserName);
        await File.WriteAllTextAsync(dialog.FileName, text, new UTF8Encoding(false));
        Services.SetStatus(IncludePersonal
            ? "Relatório salvo, incluindo dados pessoais por sua escolha."
            : "Relatório salvo sem dados pessoais.");
    }
}
