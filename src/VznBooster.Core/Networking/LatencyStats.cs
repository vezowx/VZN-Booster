namespace VznBooster.Core.Networking;

/// <summary>
/// Estatísticas de latência calculadas a partir de amostras reais.
/// Jitter = média das diferenças absolutas entre RTTs bem-sucedidos consecutivos
/// (as perdas são ignoradas na sequência). Desvio padrão é populacional.
/// </summary>
public sealed record LatencyStats(
    int Sent, int Received,
    double? MinMs, double? AvgMs, double? MaxMs,
    double? StdDevMs, double? JitterMs, double LossPercent)
{
    /// <param name="samples">RTT em ms por tentativa; null representa pacote perdido/timeout.</param>
    public static LatencyStats FromSamples(IReadOnlyList<double?> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("É necessário ao menos uma amostra.", nameof(samples));

        var rtts = new List<double>();
        foreach (var s in samples)
        {
            if (s is null) continue;
            if (double.IsNaN(s.Value) || double.IsInfinity(s.Value) || s.Value < 0)
                throw new ArgumentException("Amostra de latência inválida.", nameof(samples));
            rtts.Add(s.Value);
        }

        var sent = samples.Count;
        var loss = (sent - rtts.Count) * 100.0 / sent;
        if (rtts.Count == 0)
            return new LatencyStats(sent, 0, null, null, null, null, null, loss);

        var avg = rtts.Average();
        var std = Math.Sqrt(rtts.Sum(r => (r - avg) * (r - avg)) / rtts.Count);
        double? jitter = null;
        if (rtts.Count >= 2)
        {
            double sum = 0;
            for (var i = 1; i < rtts.Count; i++) sum += Math.Abs(rtts[i] - rtts[i - 1]);
            jitter = sum / (rtts.Count - 1);
        }
        return new LatencyStats(sent, rtts.Count, rtts.Min(), avg, rtts.Max(), std, jitter, loss);
    }
}
