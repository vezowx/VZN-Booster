namespace VznBooster.Core.Tweaker;

public enum TweakRisk { Low, Moderate, High }
public enum TweakBenefit { Measured, Expected, Visual, NotMeasurable }
public enum RestartRequirement { None, SignOut, Restart }
public enum RegKind { DWord, String }
public enum TweakStatus { NotApplied, Applied, Partial, Unknown }

/// <summary>Alteração de um valor em HKEY_CURRENT_USER (sem administrador). KeyPath é relativo ao HKCU.</summary>
public sealed record RegistryChange(string KeyPath, string ValueName, RegKind Kind, string Desired);

public sealed record ValueSnapshot(bool Exists, RegKind? Kind, string? Value)
{
    public static ValueSnapshot Absent { get; } = new(false, null, null);

    public string Encode() => Exists ? $"{Kind}|{Value}" : "absent";

    public static ValueSnapshot Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text == "absent") return Absent;
        var i = text.IndexOf('|');
        if (i <= 0 || !Enum.TryParse<RegKind>(text[..i], out var kind) || !Enum.IsDefined(kind))
            throw new FormatException("Valor de backup inválido.");
        return new ValueSnapshot(true, kind, text[(i + 1)..]);
    }

    public string Describe() => !Exists ? "não definido" : Kind == RegKind.String ? $"\"{Value}\"" : Value ?? "";
}

public sealed record TweakDefinition(
    string Id, string Name, string Category, string Summary, string Technical, string Goal,
    string ExpectedBenefit, string UsefulWhen, string SideEffects,
    TweakRisk Risk, TweakBenefit BenefitKind, RestartRequirement Restart,
    IReadOnlyList<RegistryChange> Changes,
    int? MinBuild = null, int? MaxBuild = null, string? CompatibilityNote = null)
{
    /// <summary>Todos os ajustes atuais são HKCU e não precisam de administrador.</summary>
    public bool NeedsAdmin => false;
}

public sealed record TweakState(
    TweakDefinition Definition, bool Applicable, string? NotApplicableReason, TweakStatus Status,
    IReadOnlyList<string> CurrentValues);

public enum TweakResultKind { Success, AlreadyApplied, Refused, Conflict, Failed }

public sealed record TweakOutcome(
    TweakResultKind Kind, string Message, string? BackupId, bool Verified, IReadOnlyList<string> Conflicts)
{
    public bool IsSuccess => Kind is TweakResultKind.Success or TweakResultKind.AlreadyApplied;
}

public sealed record TweakProfile(string Id, string Name, string Description, IReadOnlyList<string> TweakIds);
