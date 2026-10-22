namespace VznBooster.Desktop.ViewModels;

/// <summary>Pedido de confirmação detalhada. Se AcknowledgeText existir, o botão só habilita depois que o usuário marcar a caixa.</summary>
public sealed record ConfirmRequest(
    string Title, string Heading, IReadOnlyList<string> Lines, string ConfirmLabel, string? AcknowledgeText = null)
{
    public bool HasAcknowledge => AcknowledgeText is not null;
}
